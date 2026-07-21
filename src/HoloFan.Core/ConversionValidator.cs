namespace HoloFan.Core;

/// <summary>Outcome of validating a <see cref="ConversionOptions"/>.</summary>
public sealed record ValidationResult(bool IsValid, IReadOnlyList<string> Errors)
{
    public static ValidationResult Ok { get; } = new(true, Array.Empty<string>());
    public static ValidationResult Fail(IEnumerable<string> errors)
    {
        var list = errors.ToList();
        return new ValidationResult(list.Count == 0, list);
    }
}

/// <summary>
/// Validates conversion options against the probed source and hard limits.
/// Pure and side-effect free so it can be unit-tested without ffmpeg.
/// </summary>
public static class ConversionValidator
{
    public const int MinOutputSize = 16;
    public const int MaxOutputSize = 2048;
    public const int MinFps = 1;
    public const int MaxFps = 120;
    public const double MinSpeed = 0.1;
    public const double MaxSpeed = 8.0;

    public static ValidationResult Validate(ConversionOptions o)
    {
        var errors = new List<string>();

        if (o.SourceWidth <= 0 || o.SourceHeight <= 0)
            errors.Add("Source dimensions are unknown; probe the video first.");

        if (o.CropSide <= 0)
            errors.Add("Crop side must be positive.");

        if (o.SourceWidth > 0 && o.SourceHeight > 0 && o.CropSide > 0)
        {
            if (o.CropX < 0 || o.CropY < 0)
                errors.Add("Crop origin cannot be negative.");
            if (o.CropX + o.CropSide > o.SourceWidth)
                errors.Add("Crop region exceeds the source width.");
            if (o.CropY + o.CropSide > o.SourceHeight)
                errors.Add("Crop region exceeds the source height.");
        }

        if (o.OutputSize is < MinOutputSize or > MaxOutputSize)
            errors.Add($"Output size must be between {MinOutputSize} and {MaxOutputSize} px.");
        if (o.OutputSize % 2 != 0)
            errors.Add("Output size must be even (H.264 requirement).");

        if (o.Fps is < MinFps or > MaxFps)
            errors.Add($"FPS must be between {MinFps} and {MaxFps}.");

        if (o.Speed is < MinSpeed or > MaxSpeed)
            errors.Add($"Speed must be between {MinSpeed} and {MaxSpeed}.");

        if (o.TrimStart is < 0)
            errors.Add("Trim start cannot be negative.");
        if (o.TrimStart is { } ts && o.TrimEnd is { } te && te <= ts)
            errors.Add("Trim end must be after trim start.");

        if (o.Brightness is < -1 or > 1)
            errors.Add("Brightness must be between -1 and 1.");
        if (o.Contrast is < 0 or > 3)
            errors.Add("Contrast must be between 0 and 3.");
        if (o.Saturation is < 0 or > 3)
            errors.Add("Saturation must be between 0 and 3.");

        return errors.Count == 0 ? ValidationResult.Ok : ValidationResult.Fail(errors);
    }
}
