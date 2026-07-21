namespace HoloFan.Web.Services;

/// <summary>Locations of the ffmpeg/ffprobe binaries and where working files live.</summary>
public sealed class FfmpegOptions
{
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";

    /// <summary>Root directory for uploads and rendered outputs.</summary>
    public string DataDirectory { get; set; } =
        Path.Combine(Path.GetTempPath(), "holofan-data");

    /// <summary>Maximum upload size in bytes (default 500 MB).</summary>
    public long MaxUploadBytes { get; set; } = 500L * 1024 * 1024;

    /// <summary>How many conversions may run at once.</summary>
    public int MaxConcurrentJobs { get; set; } = 2;
}
