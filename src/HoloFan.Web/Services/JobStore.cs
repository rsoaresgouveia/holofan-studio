using System.Collections.Concurrent;
using HoloFan.Core;
using HoloFan.Device;
using Microsoft.Extensions.Options;

namespace HoloFan.Web.Services;

/// <summary>
/// In-memory registry of conversion jobs plus a bounded background worker that
/// runs them. Suitable for a single-instance app; a multi-node deployment would
/// swap this for a real queue, but the surface stays the same.
/// </summary>
public sealed class JobStore
{
    private readonly ConcurrentDictionary<string, ConversionJob> _jobs = new();
    private readonly SemaphoreSlim _gate;
    private readonly FfmpegService _ffmpeg;
    private readonly StorageService _storage;
    private readonly ILogger<JobStore> _log;

    public JobStore(FfmpegService ffmpeg, StorageService storage, IOptions<FfmpegOptions> opt, ILogger<JobStore> log)
    {
        _ffmpeg = ffmpeg;
        _storage = storage;
        _log = log;
        _gate = new SemaphoreSlim(Math.Max(1, opt.Value.MaxConcurrentJobs));
    }

    public ConversionJob? Get(string id) => _jobs.TryGetValue(id, out var j) ? j : null;

    /// <summary>Enqueues an MP4 conversion and returns immediately with a queued job.</summary>
    public ConversionJob Enqueue(string inputPath, ConversionOptions options, double expectedOutputSeconds)
    {
        var job = new ConversionJob { Id = Guid.NewGuid().ToString("N") };
        _jobs[job.Id] = job;
        var outputPath = _storage.OutputPath(job.Id, "mp4");

        _ = Task.Run(() => RunAsync(job, inputPath, outputPath, options, expectedOutputSeconds));
        return job;
    }

    /// <summary>
    /// Enqueues a render straight into the fan's native <c>.bin</c>: each frame is rendered,
    /// sampled into the device's polar grid and bit-plane packed. Copy the result onto the
    /// fan's TF card and it plays with no vendor software involved.
    /// </summary>
    public ConversionJob EnqueueBin(string inputPath, ConversionOptions options, DeviceModel model, int expectedFrames)
    {
        var job = new ConversionJob { Id = Guid.NewGuid().ToString("N"), OutputExtension = "bin" };
        _jobs[job.Id] = job;
        var outputPath = _storage.OutputPath(job.Id, "bin");

        _ = Task.Run(() => RunBinAsync(job, inputPath, outputPath, options, model, expectedFrames));
        return job;
    }

    private async Task RunBinAsync(
        ConversionJob job, string inputPath, string outputPath,
        ConversionOptions options, DeviceModel model, int expectedFrames)
    {
        await _gate.WaitAsync();
        try
        {
            job.Status = JobStatus.Running;
            await using var output = File.Create(outputPath);
            var frames = 0;

            await _ffmpeg.ExtractFramesAsync(inputPath, options, async rgb =>
            {
                var polar = BinEncoder.PolarSample(model, rgb, options.OutputSize);
                var encoded = BinEncoder.EncodeFrame(model, polar);
                await output.WriteAsync(encoded);
                frames++;
                if (expectedFrames > 0)
                    job.Progress = Math.Clamp((double)frames / expectedFrames, 0, 1);
            });

            await output.FlushAsync();
            if (frames == 0) throw new InvalidOperationException("No frames were produced.");

            job.Progress = 1.0;
            job.Status = JobStatus.Completed;
            _log.LogInformation("Encoded {Frames} frames to {Path} for {Model}", frames, outputPath, model.Id);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Bin encode {JobId} failed", job.Id);
            job.Status = JobStatus.Failed;
            job.Error = ex.Message;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RunAsync(
        ConversionJob job, string inputPath, string outputPath,
        ConversionOptions options, double expectedOutputSeconds)
    {
        await _gate.WaitAsync();
        try
        {
            job.Status = JobStatus.Running;
            await _ffmpeg.ConvertAsync(
                inputPath, outputPath, options, expectedOutputSeconds,
                frac => job.Progress = frac);
            job.Progress = 1.0;
            job.Status = JobStatus.Completed;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Conversion {JobId} failed", job.Id);
            job.Status = JobStatus.Failed;
            job.Error = ex.Message;
        }
        finally
        {
            _gate.Release();
        }
    }
}
