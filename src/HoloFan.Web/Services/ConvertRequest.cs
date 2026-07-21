using HoloFan.Core;

namespace HoloFan.Web.Services;

/// <summary>The conversion settings the browser sends. Geometry is in source pixels.</summary>
public sealed record ConvertRequest
{
    public int CropX { get; init; }
    public int CropY { get; init; }
    public int CropSide { get; init; }
    public int OutputSize { get; init; } = 512;
    public int Fps { get; init; } = 30;
    public double Speed { get; init; } = 1.0;
    public bool CircularMask { get; init; } = true;
    public string Background { get; init; } = "#000000";
    public double? TrimStart { get; init; }
    public double? TrimEnd { get; init; }
    public double Brightness { get; init; }
    public double Contrast { get; init; } = 1.0;
    public double Saturation { get; init; } = 1.0;

    /// <summary>Target fan model when rendering the native .bin (e.g. "42-F2"). Ignored for MP4.</summary>
    public string DeviceModelId { get; init; } = "42-F2";

    /// <summary>Builds the core options, taking authoritative source dimensions from a fresh probe.</summary>
    public ConversionOptions ToOptions(int sourceWidth, int sourceHeight) => new()
    {
        SourceWidth = sourceWidth,
        SourceHeight = sourceHeight,
        CropX = CropX,
        CropY = CropY,
        CropSide = CropSide,
        OutputSize = OutputSize,
        Fps = Fps,
        Speed = Speed,
        CircularMask = CircularMask,
        Background = RgbColor.Parse(Background),
        TrimStart = TrimStart,
        TrimEnd = TrimEnd,
        Brightness = Brightness,
        Contrast = Contrast,
        Saturation = Saturation,
    };
}
