namespace HoloFan.Web.Services;

/// <summary>Metadata probed from a source video with ffprobe.</summary>
public sealed record VideoInfo(int Width, int Height, double DurationSeconds, double Fps);
