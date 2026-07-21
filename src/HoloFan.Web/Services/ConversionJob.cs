namespace HoloFan.Web.Services;

public enum JobStatus { Queued, Running, Completed, Failed }

/// <summary>A single conversion, tracked in memory while it runs.</summary>
public sealed class ConversionJob
{
    public required string Id { get; init; }
    public JobStatus Status { get; set; } = JobStatus.Queued;

    /// <summary>Progress in the range [0, 1].</summary>
    public double Progress { get; set; }

    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>"mp4" for a normal clip, "bin" for the native LED-fan container.</summary>
    public string OutputExtension { get; init; } = "mp4";
}
