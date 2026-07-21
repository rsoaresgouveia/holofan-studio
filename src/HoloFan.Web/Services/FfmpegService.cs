using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using HoloFan.Core;
using Microsoft.Extensions.Options;

namespace HoloFan.Web.Services;

/// <summary>Wraps the ffmpeg/ffprobe binaries: probing, thumbnails, and conversion with progress.</summary>
public sealed class FfmpegService
{
    private readonly FfmpegOptions _opt;
    private readonly ILogger<FfmpegService> _log;

    public FfmpegService(IOptions<FfmpegOptions> opt, ILogger<FfmpegService> log)
    {
        _opt = opt.Value;
        _log = log;
    }

    /// <summary>Probes a video for its dimensions, duration and frame rate.</summary>
    public async Task<VideoInfo> ProbeAsync(string inputPath, CancellationToken ct = default)
    {
        var args = new[]
        {
            "-v", "quiet", "-print_format", "json",
            "-show_streams", "-show_format", inputPath,
        };

        var (exit, stdout, stderr) = await RunAsync(_opt.FfprobePath, args, onStderrLine: null, ct);
        if (exit != 0)
            throw new InvalidOperationException($"ffprobe failed: {stderr}");

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;

        var video = root.GetProperty("streams").EnumerateArray()
            .FirstOrDefault(s => s.TryGetProperty("codec_type", out var t) && t.GetString() == "video");
        if (video.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException("No video stream found in the uploaded file.");

        var width = video.GetProperty("width").GetInt32();
        var height = video.GetProperty("height").GetInt32();

        double duration = 0;
        if (root.TryGetProperty("format", out var fmt) &&
            fmt.TryGetProperty("duration", out var d) &&
            double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            duration = parsed;

        double fps = 0;
        if (video.TryGetProperty("avg_frame_rate", out var afr))
            fps = ParseRate(afr.GetString());
        if (fps <= 0 && video.TryGetProperty("r_frame_rate", out var rfr))
            fps = ParseRate(rfr.GetString());

        return new VideoInfo(width, height, duration, fps);
    }

    /// <summary>Extracts a single JPEG frame at <paramref name="seconds"/> for the canvas preview.</summary>
    public async Task<byte[]> ExtractFrameAsync(string inputPath, double seconds, CancellationToken ct = default)
    {
        var args = new List<string>
        {
            "-hide_banner", "-y",
            "-ss", seconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-i", inputPath,
            "-frames:v", "1",
            "-q:v", "3",
            "-f", "mjpeg", "pipe:1",
        };

        using var proc = StartProcess(_opt.FfmpegPath, args, redirectStdoutBinary: true);
        using var ms = new MemoryStream();
        var copyTask = proc.StandardOutput.BaseStream.CopyToAsync(ms, ct);
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await copyTask;
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0 || ms.Length == 0)
            throw new InvalidOperationException($"ffmpeg frame extraction failed: {stderr}");

        return ms.ToArray();
    }

    /// <summary>
    /// Runs a conversion, invoking <paramref name="onProgress"/> with a 0..1 fraction.
    /// Progress is derived from ffmpeg's <c>-progress</c> stream against the expected
    /// output duration (source span divided by the speed multiplier).
    /// </summary>
    public async Task ConvertAsync(
        string inputPath,
        string outputPath,
        ConversionOptions options,
        double expectedOutputSeconds,
        Action<double>? onProgress,
        CancellationToken ct = default)
    {
        var args = new List<string> { "-progress", "pipe:2", "-nostats" };
        args.AddRange(FilterGraphBuilder.BuildArguments(inputPath, outputPath, options));

        void OnLine(string line)
        {
            // -progress emits key=value lines; out_time_us is microseconds rendered.
            if (onProgress is null || expectedOutputSeconds <= 0) return;
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                long.TryParse(line.AsSpan("out_time_us=".Length), out var us) && us >= 0)
            {
                var frac = Math.Clamp(us / 1_000_000.0 / expectedOutputSeconds, 0, 1);
                onProgress(frac);
            }
        }

        var (exit, _, stderr) = await RunAsync(_opt.FfmpegPath, args, OnLine, ct);
        if (exit != 0)
            throw new InvalidOperationException($"ffmpeg conversion failed: {Tail(stderr)}");

        onProgress?.Invoke(1.0);
    }

    /// <summary>
    /// Runs the same visual pipeline as <see cref="ConvertAsync"/> but hands each rendered
    /// frame back as raw RGB24 instead of encoding an MP4. This is what feeds the LED-fan
    /// encoder, which needs pixels rather than a video file.
    /// </summary>
    public async Task ExtractFramesAsync(
        string inputPath,
        ConversionOptions options,
        Func<byte[], Task> onFrame,
        CancellationToken ct = default)
    {
        var args = new List<string> { "-hide_banner" };

        if (options.TrimStart is { } start && start > 0)
        {
            args.Add("-ss");
            args.Add(start.ToString("0.#####", CultureInfo.InvariantCulture));
        }
        args.Add("-i");
        args.Add(inputPath);

        var trimStart = options.TrimStart ?? 0;
        if (options.TrimEnd is { } end && end > trimStart)
        {
            args.Add("-t");
            args.Add((end - trimStart).ToString("0.#####", CultureInfo.InvariantCulture));
        }

        args.Add("-filter:v");
        args.Add(FilterGraphBuilder.BuildVideoFilter(options));
        args.Add("-an");
        args.Add("-f");
        args.Add("rawvideo");
        args.Add("-pix_fmt");
        args.Add("rgb24");
        args.Add("pipe:1");

        using var proc = StartProcess(_opt.FfmpegPath, args, redirectStdoutBinary: true);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);

        var frameSize = options.OutputSize * options.OutputSize * 3;
        var buffer = new byte[frameSize];
        var stream = proc.StandardOutput.BaseStream;

        while (true)
        {
            var read = 0;
            while (read < frameSize)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read, frameSize - read), ct);
                if (n == 0) break;
                read += n;
            }
            if (read == 0) break;                 // clean end of stream
            if (read < frameSize)
                throw new InvalidOperationException($"truncated frame: got {read} of {frameSize} bytes");

            await onFrame(buffer);
        }

        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg frame extraction failed: {Tail(await stderrTask)}");
    }

    private static double ParseRate(string? rate)
    {
        if (string.IsNullOrWhiteSpace(rate)) return 0;
        var parts = rate.Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var num) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den) &&
            den != 0)
            return num / den;
        return double.TryParse(rate, NumberStyles.Float, CultureInfo.InvariantCulture, out var flat) ? flat : 0;
    }

    private Process StartProcess(string exe, IEnumerable<string> args, bool redirectStdoutBinary)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (!redirectStdoutBinary)
            psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;
        foreach (var a in args) psi.ArgumentList.Add(a);

        _log.LogDebug("Running {Exe} {Args}", exe, string.Join(' ', psi.ArgumentList));
        var proc = new Process { StartInfo = psi };
        proc.Start();
        return proc;
    }

    private async Task<(int exit, string stdout, string stderr)> RunAsync(
        string exe, IEnumerable<string> args, Action<string>? onStderrLine, CancellationToken ct)
    {
        using var proc = StartProcess(exe, args, redirectStdoutBinary: false);

        var stderr = new StringBuilder();
        var stderrTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await proc.StandardError.ReadLineAsync(ct)) is not null)
            {
                stderr.AppendLine(line);
                onStderrLine?.Invoke(line);
            }
        }, ct);

        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        await stderrTask;
        await proc.WaitForExitAsync(ct);
        return (proc.ExitCode, stdout, stderr.ToString());
    }

    private static string Tail(string s, int lines = 8)
    {
        var arr = s.Split('\n');
        return string.Join('\n', arr[Math.Max(0, arr.Length - lines)..]);
    }
}
