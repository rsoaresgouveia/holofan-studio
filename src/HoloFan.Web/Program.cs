using System.Reflection;
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
builder.Services.AddSingleton<LibraryService>();
builder.Services.AddSingleton<JobStore>();
builder.Services.AddSingleton<SecurityService>();
builder.Services.AddSingleton<FanSyncService>();

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

// Running version + build identity, so the UI can show which build a device is on (and
// whether a Raspberry Pi has pulled the latest image). Commit/build-time come from env
// vars the Docker image bakes in at build; version is the assembly's, from the csproj.
app.MapGet("/api/version", () =>
{
    var informational = typeof(Program).Assembly
        .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    var version = (informational ?? "0.0.0").Split('+')[0];   // drop SourceLink +<sha> suffix
    var commit = Environment.GetEnvironmentVariable("HOLOFAN_COMMIT") is { Length: > 0 } c ? c : "dev";
    var builtAt = Environment.GetEnvironmentVariable("HOLOFAN_BUILT_AT") ?? "";

    var changelogPath = Path.Combine(AppContext.BaseDirectory, "CHANGELOG.md");
    var changelog = File.Exists(changelogPath) ? File.ReadAllText(changelogPath) : "";

    return Results.Ok(new
    {
        version,
        commit,
        commitShort = commit.Length >= 7 ? commit[..7] : commit,
        builtAt,
        changelog,
    });
});

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

    var clipName = string.IsNullOrWhiteSpace(request.Name) ? "clip" : request.Name.Trim();
    var job = jobs.EnqueueBin(path, options, model, expectedFrames, clipName);
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

// --- Clip library -------------------------------------------------------------
// A persistent store of every clip's bytes (renders + imports like the factory bins),
// on the data volume so it survives reboots. The fan can't hand clips back, so this is
// what lets the app manage what's on the device (and re-upload after a Format).

static object ClipDto(LibraryService.LibraryClip c) => new
{
    id = c.Id, name = c.Name, sizeBytes = c.SizeBytes, frameCount = c.FrameCount,
    source = c.Source, createdAt = c.CreatedAt,
    sha = c.Sha256.Length >= 12 ? c.Sha256[..12].ToLowerInvariant() : c.Sha256.ToLowerInvariant(),
};

app.MapGet("/api/library", (LibraryService library) =>
    Results.Ok(new { clips = library.List().Select(ClipDto) }));

// Import a raw .bin (e.g. a factory demo clip) into the library.
app.MapPost("/api/library/import", async (HttpRequest req, LibraryService library, CancellationToken ct) =>
{
    if (!req.HasFormContentType)
        return Results.BadRequest(new { error = "Expected a multipart/form-data upload." });

    var form = await req.ReadFormAsync(ct);
    var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
    if (file is null || file.Length == 0)
        return Results.BadRequest(new { error = "No file was uploaded." });

    var name = form["name"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(name))
        name = Path.GetFileNameWithoutExtension(file.FileName);

    try
    {
        using var ms = new MemoryStream();
        await using (var stream = file.OpenReadStream()) await stream.CopyToAsync(ms, ct);
        var clip = library.Add(name!, ms.ToArray(), "imported");
        return Results.Ok(ClipDto(clip));
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/library/{id}/rename", (string id, RenameRequest request, LibraryService library) =>
{
    if (string.IsNullOrWhiteSpace(request.Name))
        return Results.BadRequest(new { error = "A name is required." });
    return library.Rename(id, request.Name)
        ? Results.Ok(new { renamed = true })
        : Results.NotFound(new { error = "Unknown clip." });
});

app.MapDelete("/api/library/{id}", (string id, LibraryService library) =>
    library.Delete(id) ? Results.Ok(new { deleted = true }) : Results.NotFound(new { error = "Unknown clip." }));

app.MapGet("/api/library/{id}/download", (string id, LibraryService library) =>
{
    var clip = library.Get(id);
    var path = library.PathOf(id);
    if (clip is null || path is null) return Results.NotFound(new { error = "Unknown clip." });
    var safe = string.Concat(clip.Name.Split(Path.GetInvalidFileNameChars()));
    return Results.File(path, "application/octet-stream", $"{(safe.Length == 0 ? "clip" : safe)}.bin");
});

// Send a library clip to the fan over WiFi.
app.MapPost("/api/library/{id}/send", async (string id, FanClient fan, LibraryService library, CancellationToken ct) =>
{
    if (!fan.IsConnected) return Results.BadRequest(new { error = "Connect to the fan first." });
    var clip = library.Get(id);
    var path = library.PathOf(id);
    if (clip is null || path is null) return Results.NotFound(new { error = "Unknown clip." });
    try
    {
        var bytes = await File.ReadAllBytesAsync(path, ct);
        await fan.UploadAsync(clip.Name, bytes, ct: ct);
        return Results.Ok(new { uploaded = clip.Name, bytes = bytes.Length, files = fan.List() });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// --- Emulated fan-storage management (Format + re-upload survivors) ------------
// The fan can't delete/rename a single clip, so "make the fan hold exactly these clips"
// is Format Disk followed by re-uploading the chosen library clips. Gated by the admin
// passphrase (it formats) and guarded against silently losing clips that aren't backed up.

app.MapGet("/api/fan/sync", (FanSyncService sync) => Results.Ok(sync.State));

app.MapPost("/api/fan/sync", (FanSyncRequest request, FanClient fan, FanSyncService sync,
    SecurityService security, LibraryService library) =>
{
    if (!fan.IsConnected) return Results.BadRequest(new { error = "Connect to the fan first." });
    if (sync.IsRunning) return Results.Conflict(new { error = "A sync is already running." });

    // This performs a Format Disk — same guard as the destructive fan buttons.
    if (!security.IsPassphraseSet())
        return Results.BadRequest(new { error = "Set an admin passphrase first.", needsPassphraseSetup = true });
    if (!security.Verify(request.Passphrase))
        return Results.BadRequest(new { error = "Wrong passphrase." });

    var ids = request.ClipIds ?? Array.Empty<string>();
    var clips = new List<LibraryService.LibraryClip>();
    foreach (var id in ids)
    {
        var clip = library.Get(id);
        if (clip is null) return Results.BadRequest(new { error = $"Unknown clip in selection." });
        clips.Add(clip);
    }

    // Refuse to silently erase clips that are on the fan but not in this selection unless the
    // caller has acknowledged the loss.
    var lost = FanSyncService.ClipsLost(sync.CurrentFanClips(), clips.Select(c => c.Name));
    if (lost.Count > 0 && !request.AcknowledgeLoss)
        return Results.Conflict(new
        {
            error = "Some clips on the fan aren't in your selection and would be lost.",
            lost,
        });

    try
    {
        sync.Start(ids);
        return Results.Ok(new { started = true, total = clips.Count });
    }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
});

// --- Fan control (WiFi) -------------------------------------------------------
// Every button the vendor software offers, spoken in the fan's own protocol.
// Join the fan's open "3DCircle_…" AP first.

app.MapGet("/api/fan/status", (FanClient fan) =>
{
    var status = fan.Status();
    return Results.Ok(new
    {
        connected = fan.IsConnected,
        host = fan.Endpoint.Host,
        port = fan.Endpoint.Port,
        poweredOn = status.PoweredOn,
        playing = status.Playing,
        currentIndex = status.CurrentIndex,
        fileCount = status.Files.Count,
        commands = Enum.GetNames<FanCommand>(),
    });
});

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

// The clips on the fan's card plus readable state (from the playlist it sends at connect).
app.MapGet("/api/fan/playlist", (FanClient fan) =>
{
    if (!fan.IsConnected) return Results.BadRequest(new { error = "Connect to the fan first." });
    var status = fan.Status();
    return Results.Ok(new { files = status.Files, poweredOn = status.PoweredOn, currentIndex = status.CurrentIndex, playing = status.Playing });
});

// Jump to a clip by list index (emulated with Next/Previous — the device has no direct command).
app.MapPost("/api/fan/play", async (PlayRequest request, FanClient fan, CancellationToken ct) =>
{
    if (!fan.IsConnected) return Results.BadRequest(new { error = "Connect to the fan first." });
    try
    {
        await fan.PlayIndexAsync(request.Index, ct);
        var status = fan.Status();
        return Results.Ok(new { currentIndex = status.CurrentIndex, files = status.Files });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// Set how long each picture is shown, 5–30 s ("How long the picture play").
app.MapPost("/api/fan/duration", async (DurationRequest request, FanClient fan, CancellationToken ct) =>
{
    if (!fan.IsConnected) return Results.BadRequest(new { error = "Connect to the fan first." });
    try
    {
        await fan.SetDisplaySecondsAsync(request.Seconds, ct);
        return Results.Ok(new { seconds = request.Seconds });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/fan/command", async (FanCommandRequest request, FanClient fan, SecurityService security, CancellationToken ct) =>
{
    if (!Enum.TryParse<FanCommand>(request.Command, ignoreCase: true, out var cmd))
        return Results.BadRequest(new { error = $"Unknown command '{request.Command}'." });

    // Destructive commands require the admin passphrase.
    var destructive = cmd is FanCommand.FormatDisk or FanCommand.ClearCache;
    if (destructive)
    {
        if (!security.IsPassphraseSet())
            return Results.BadRequest(new { error = "Set an admin passphrase first.", needsPassphraseSetup = true });
        if (!security.Verify(request.Passphrase))
            return Results.BadRequest(new { error = "Wrong passphrase." });
    }

    try
    {
        await fan.SendAsync(cmd, confirmDestructive: destructive, ct);
        return Results.Ok(new { sent = cmd.ToString() });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// Admin passphrase that gates destructive fan actions.
app.MapGet("/api/security/status", (SecurityService security) =>
    Results.Ok(new { passphraseSet = security.IsPassphraseSet() }));

app.MapPost("/api/security/passphrase", (PassphraseRequest request, SecurityService security) =>
{
    try
    {
        return security.SetPassphrase(request.Current, request.NewPassphrase)
            ? Results.Ok(new { passphraseSet = true })
            : Results.BadRequest(new { error = "Current passphrase is wrong." });
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// Push a rendered .bin to the fan over WiFi (decoded upload protocol; device writes the file).
app.MapPost("/api/fan/upload", async (FanUploadRequest request, FanClient fan, StorageService storage, CancellationToken ct) =>
{
    if (!fan.IsConnected) return Results.BadRequest(new { error = "Connect to the fan first." });

    var path = storage.FindOutput(request.JobId);
    if (path is null || !path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "No rendered .bin for that job — generate a .bin first." });

    var name = string.IsNullOrWhiteSpace(request.Name) ? "HOLOFAN" : request.Name.Trim();
    try
    {
        var bin = await File.ReadAllBytesAsync(path, ct);
        await fan.UploadAsync(name, bin, ct: ct);
        return Results.Ok(new { uploaded = name, bytes = bin.Length, files = fan.List() });
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

/// <summary>A button press on the fan's control panel. Destructive commands carry the admin passphrase.</summary>
public sealed record FanCommandRequest(string Command, bool ConfirmDestructive = false, string? Passphrase = null);

/// <summary>Set or change the admin passphrase (current required only when changing).</summary>
public sealed record PassphraseRequest(string? Current, string NewPassphrase);

/// <summary>A change to one of the fan's clock dials.</summary>
public sealed record ClockRequest(string Setting, byte Value);

/// <summary>Push a rendered .bin (by job id) to the fan under a chosen clip name.</summary>
public sealed record FanUploadRequest(string JobId, string Name);

public sealed record RenameRequest(string Name);

public sealed record FanSyncRequest(string[]? ClipIds, string? Passphrase, bool AcknowledgeLoss);

/// <summary>Seconds each picture is shown (5–30).</summary>
public sealed record DurationRequest(int Seconds);

/// <summary>Jump to the clip at this list index.</summary>
public sealed record PlayRequest(int Index);

// Exposed so integration tests (WebApplicationFactory) can reference the entry point.
public partial class Program;
