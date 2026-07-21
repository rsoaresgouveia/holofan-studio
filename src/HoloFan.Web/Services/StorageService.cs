using Microsoft.Extensions.Options;

namespace HoloFan.Web.Services;

/// <summary>
/// Owns the on-disk layout for uploads and rendered outputs. All identifiers are
/// server-generated GUIDs, so nothing user-controlled ever reaches a filesystem path.
/// </summary>
public sealed class StorageService
{
    private readonly string _uploads;
    private readonly string _outputs;

    public StorageService(IOptions<FfmpegOptions> opt)
    {
        var root = opt.Value.DataDirectory;
        _uploads = Path.Combine(root, "uploads");
        _outputs = Path.Combine(root, "outputs");
        Directory.CreateDirectory(_uploads);
        Directory.CreateDirectory(_outputs);
    }

    public async Task<string> SaveUploadAsync(string uploadId, string extension, Stream content, CancellationToken ct)
    {
        var dir = Path.Combine(_uploads, uploadId);
        Directory.CreateDirectory(dir);
        var safeExt = SanitizeExtension(extension);
        var path = Path.Combine(dir, "source" + safeExt);
        await using var fs = File.Create(path);
        await content.CopyToAsync(fs, ct);
        return path;
    }

    /// <summary>Resolves the stored source file for an upload id, or null if missing.</summary>
    public string? FindUpload(string uploadId)
    {
        if (!IsGuid(uploadId)) return null;
        var dir = Path.Combine(_uploads, uploadId);
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateFiles(dir, "source.*").FirstOrDefault();
    }

    /// <param name="extension">"mp4" for a normal clip, "bin" for the LED-fan container.</param>
    public string OutputPath(string jobId, string extension = "mp4") =>
        Path.Combine(_outputs, $"{jobId}.{extension}");

    /// <summary>Finds a job's rendered output whatever format it was produced in.</summary>
    public string? FindOutput(string jobId)
    {
        if (!IsGuid(jobId)) return null;
        return Directory.EnumerateFiles(_outputs, $"{jobId}.*").FirstOrDefault();
    }

    private static bool IsGuid(string s) => Guid.TryParse(s, out _);

    private static string SanitizeExtension(string ext)
    {
        if (string.IsNullOrWhiteSpace(ext)) return ".mp4";
        ext = ext.Trim().ToLowerInvariant();
        if (!ext.StartsWith('.')) ext = "." + ext;
        // Keep only a short alphanumeric extension; anything odd becomes .mp4.
        return ext.Length is > 1 and <= 5 && ext[1..].All(char.IsLetterOrDigit) ? ext : ".mp4";
    }
}
