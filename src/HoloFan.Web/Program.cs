using HoloFan.Core;
using HoloFan.Device;
using HoloFan.Web.Services;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);

// Bind ffmpeg/storage options from configuration, then let env vars override.
builder.Services.Configure<FfmpegOptions>(o =>
{
    builder.Configuration.GetSection("Holofan").Bind(o);
    o.FfmpegPath = Environment.GetEnvironmentVariable("HOLOFAN_FFMPEG") ?? o.FfmpegPath;
    o.FfprobePath = Environment.GetEnvironmentVariable("HOLOFAN_FFPROBE") ?? o.FfprobePath;
    o.DataDirectory = Environment.GetEnvironmentVariable("HOLOFAN_DATA") ?? o.DataDirectory;
    if (long.TryParse(Environment.GetEnvironmentVariable("HOLOFAN_MAX_UPLOAD_BYTES"), out var max) && max > 0)
        o.MaxUploadBytes = max;
});

var uploadLimit = long.TryParse(Environment.GetEnvironmentVariable("HOLOFAN_MAX_UPLOAD_BYTES"), out var m) && m > 0
    ? m : 500L * 1024 * 1024;
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = uploadLimit);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = uploadLimit);

builder.Services.AddSingleton<StorageService>();
builder.Services.AddSingleton<FfmpegService>();
builder.Services.AddSingleton<JobStore>();

// One live connection to the fan, shared by the control panel.
builder.Services.AddSingleton(_ => new FanClient(new FanEndpoint
{
    Host = Environment.GetEnvironmentVariable("HOLOFAN_FAN_HOST") ?? FanEndpoint.DefaultHost,
    Port = int.TryParse(Environment.GetEnvironmentVariable("HOLOFAN_FAN_PORT"), out var fp)
        ? fp : FanEndpoint.DefaultPort,
}));

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// --- API ---------------------------------------------------------------------

// The catalogue of known fan sizes for the UI presets.
app.MapGet("/api/presets", () => Results.Ok(FanPreset.Catalog));

// Health check for Docker.
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

// Upload a source video: stores it and returns probe metadata.
app.MapPost("/api/uploads", async (HttpRequest req, StorageService storage, FfmpegService ffmpeg, CancellationToken ct) =>
{
    if (!req.HasFormContentType)
        return Results.BadRequest(new { error = "Expected a multipart/form-data upload." });

    var form = await req.ReadFormAsync(ct);
    var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
    if (file is null || file.Length == 0)
        return Results.BadRequest(new { error = "No file was uploaded." });

    var uploadId = Guid.NewGuid().ToString("N");
    var ext = Path.GetExtension(file.FileName);
    string path;
    await using (var stream = file.OpenReadStream())
        path = await storage.SaveUploadAsync(uploadId, ext, stream, ct);

    try
    {
        var info = await ffmpeg.ProbeAsync(path, ct);
        return Results.Ok(new
        {
            uploadId,
            width = info.Width,
            height = info.Height,
            duration = info.DurationSeconds,
            fps = info.Fps,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = $"Could not read the video: {ex.Message}" });
    }
});

// A single JPEG frame at time t, used as the canvas backdrop.
app.MapGet("/api/uploads/{id}/frame", async (string id, double? t, StorageService storage, FfmpegService ffmpeg, CancellationToken ct) =>
{
    var path = storage.FindUpload(id);
    if (path is null) return Results.NotFound(new { error = "Unknown upload." });

    try
    {
        var bytes = await ffmpeg.ExtractFrameAsync(path, Math.Max(0, t ?? 0), ct);
        return Results.File(bytes, "image/jpeg");
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// Kick off a conversion; returns a job id to poll.
app.MapPost("/api/uploads/{id}/convert", async (string id, ConvertRequest request, StorageService storage, FfmpegService ffmpeg, JobStore jobs, CancellationToken ct) =>
{
    var path = storage.FindUpload(id);
    if (path is null) return Results.NotFound(new { error = "Unknown upload." });

    VideoInfo info;
    try { info = await ffmpeg.ProbeAsync(path, ct); }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }

    var options = request.ToOptions(info.Width, info.Height);
    var validation = ConversionValidator.Validate(options);
    if (!validation.IsValid)
        return Results.BadRequest(new { error = "Invalid settings.", details = validation.Errors });

    var start = options.TrimStart ?? 0;
    var end = options.TrimEnd ?? info.DurationSeconds;
    var expected = Math.Max(0.1, (end - start) / Math.Max(0.1, options.Speed));

    var job = jobs.Enqueue(path, options, expected);
    return Results.Ok(new { jobId = job.Id });
});

// The fan models we can emit a native .bin for.
app.MapGet("/api/devices", () => Results.Ok(DeviceModel.Catalog.Select(m => new
{
    id = m.Id,
    diameterCm = m.DiameterCm,
    leds = m.Leds,
    angularSlices = m.AngularSlices,
    frameBytes = m.FrameBytes,
})));

// Render straight into the fan's native .bin — copy it to the TF card and it plays,
// with no vendor software in the loop.
app.MapPost("/api/uploads/{id}/bin", async (string id, ConvertRequest request, StorageService storage, FfmpegService ffmpeg, JobStore jobs, CancellationToken ct) =>
{
    var path = storage.FindUpload(id);
    if (path is null) return Results.NotFound(new { error = "Unknown upload." });

    var model = DeviceModel.FindById(request.DeviceModelId);
    if (model is null) return Results.BadRequest(new { error = $"Unknown fan model '{request.DeviceModelId}'." });

    VideoInfo info;
    try { info = await ffmpeg.ProbeAsync(path, ct); }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }

    // The .bin carries a polar grid, so the square render only needs to be big enough to
    // sample it well; force at least twice the LED count across the diameter.
    var options = request.ToOptions(info.Width, info.Height) with
    {
        OutputSize = Math.Max(request.OutputSize, model.Leds * 2),
        CircularMask = true,   // the fan only lights the inscribed circle
    };

    var validation = ConversionValidator.Validate(options);
    if (!validation.IsValid)
        return Results.BadRequest(new { error = "Invalid settings.", details = validation.Errors });

    var start = options.TrimStart ?? 0;
    var end = options.TrimEnd ?? info.DurationSeconds;
    var expectedFrames = (int)Math.Max(1, (end - start) / Math.Max(0.1, options.Speed) * options.Fps);

    var job = jobs.EnqueueBin(path, options, model, expectedFrames);
    return Results.Ok(new { jobId = job.Id, model = model.Id, expectedFrames });
});

// Poll a job's status/progress.
app.MapGet("/api/jobs/{id}", (string id, JobStore jobs) =>
{
    var job = jobs.Get(id);
    if (job is null) return Results.NotFound(new { error = "Unknown job." });
    return Results.Ok(new
    {
        id = job.Id,
        status = job.Status.ToString().ToLowerInvariant(),
        progress = job.Progress,
        error = job.Error,
    });
});

// --- Fan control (WiFi) -------------------------------------------------------
// Every button the vendor software offers, spoken in the fan's own protocol.
// Join the fan's open "3DCircle_…" AP first.

app.MapGet("/api/fan/status", (FanClient fan) => Results.Ok(new
{
    connected = fan.IsConnected,
    host = fan.Endpoint.Host,
    port = fan.Endpoint.Port,
    commands = Enum.GetNames<FanCommand>(),
}));

app.MapPost("/api/fan/connect", async (FanClient fan, CancellationToken ct) =>
{
    try
    {
        await fan.ConnectAsync(ct);
        return Results.Ok(new { connected = true, host = fan.Endpoint.Host, port = fan.Endpoint.Port });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new
        {
            error = $"Could not reach the fan at {fan.Endpoint.Host}:{fan.Endpoint.Port} — " +
                    $"is this machine joined to its WiFi? ({ex.Message})",
        });
    }
});

app.MapPost("/api/fan/disconnect", async (FanClient fan) =>
{
    await fan.DisconnectAsync();
    return Results.Ok(new { connected = false });
});

app.MapPost("/api/fan/command", async (FanCommandRequest request, FanClient fan, CancellationToken ct) =>
{
    if (!Enum.TryParse<FanCommand>(request.Command, ignoreCase: true, out var cmd))
        return Results.BadRequest(new { error = $"Unknown command '{request.Command}'." });

    try
    {
        await fan.SendAsync(cmd, request.ConfirmDestructive, ct);
        return Results.Ok(new { sent = cmd.ToString() });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/fan/clock", async (ClockRequest request, FanClient fan, CancellationToken ct) =>
{
    if (!Enum.TryParse<ClockSetting>(request.Setting, ignoreCase: true, out var setting))
        return Results.BadRequest(new { error = $"Unknown clock setting '{request.Setting}'." });

    try
    {
        await fan.SetClockAsync(setting, request.Value, ct);
        return Results.Ok(new { sent = setting.ToString(), request.Value });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// Download the rendered clip (MP4) or the native fan container (.bin).
app.MapGet("/api/jobs/{id}/download", (string id, JobStore jobs, StorageService storage) =>
{
    var job = jobs.Get(id);
    if (job is null || job.Status != JobStatus.Completed) return Results.NotFound(new { error = "Not ready." });
    var path = storage.FindOutput(id);
    if (path is null) return Results.NotFound(new { error = "Output missing." });

    var isBin = job.OutputExtension == "bin";
    return Results.File(
        path,
        isBin ? "application/octet-stream" : "video/mp4",
        $"holofan-{id}.{job.OutputExtension}");
});

app.Run();

/// <summary>A button press on the fan's control panel.</summary>
public sealed record FanCommandRequest(string Command, bool ConfirmDestructive = false);

/// <summary>A change to one of the fan's clock dials.</summary>
public sealed record ClockRequest(string Setting, byte Value);

// Exposed so integration tests (WebApplicationFactory) can reference the entry point.
public partial class Program;
