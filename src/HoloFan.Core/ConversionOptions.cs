namespace HoloFan.Core;

/// <summary>
/// The full, fully-configurable description of how a source video should be
/// turned into an LED-fan clip. All geometry is expressed in <b>source pixels</b>
/// so it is unambiguous; the web UI derives these from the probed dimensions.
/// </summary>
public sealed record ConversionOptions
{
    /// <summary>Probed width of the source video, in pixels.</summary>
    public int SourceWidth { get; init; }

    /// <summary>Probed height of the source video, in pixels.</summary>
    public int SourceHeight { get; init; }

    /// <summary>Left edge of the square crop region within the source, in source pixels.</summary>
    public int CropX { get; init; }

    /// <summary>Top edge of the square crop region within the source, in source pixels.</summary>
    public int CropY { get; init; }

    /// <summary>Side length of the (square) crop region within the source, in source pixels.</summary>
    public int CropSide { get; init; }

    /// <summary>Output resolution — the fan's square pixel grid.</summary>
    public int OutputSize { get; init; } = 512;

    /// <summary>Target frame rate.</summary>
    public int Fps { get; init; } = 30;

    /// <summary>Playback speed multiplier. 1.0 = original, 2.0 = twice as fast.</summary>
    public double Speed { get; init; } = 1.0;

    /// <summary>When true, everything outside the inscribed circle is filled with <see cref="Background"/>.</summary>
    public bool CircularMask { get; init; } = true;

    /// <summary>Colour painted outside the circle (and behind transparent content).</summary>
    public RgbColor Background { get; init; } = RgbColor.Black;

    /// <summary>Optional trim start, in seconds. Null = from the beginning.</summary>
    public double? TrimStart { get; init; }

    /// <summary>Optional trim end, in seconds. Null = to the end.</summary>
    public double? TrimEnd { get; init; }

    /// <summary>Brightness adjustment for the <c>eq</c> filter, range [-1, 1]. 0 = unchanged.</summary>
    public double Brightness { get; init; }

    /// <summary>Contrast multiplier for the <c>eq</c> filter, range [0, 3]. 1 = unchanged.</summary>
    public double Contrast { get; init; } = 1.0;

    /// <summary>Saturation multiplier for the <c>eq</c> filter, range [0, 3]. 1 = unchanged.</summary>
    public double Saturation { get; init; } = 1.0;
}
