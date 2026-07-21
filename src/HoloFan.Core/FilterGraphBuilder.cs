using System.Globalization;

namespace HoloFan.Core;

/// <summary>
/// Turns <see cref="ConversionOptions"/> into an ffmpeg command. This is the heart
/// of the converter and is deliberately pure (string in, string out) so the exact
/// filter graph can be asserted in unit tests without invoking ffmpeg.
///
/// Pipeline: square-crop the chosen region → scale to the fan grid → retime for
/// speed → resample to the target fps → optional colour tweak → optional circular
/// mask that paints everything outside the inscribed circle with the background
/// colour (that is what makes the clip read as a round hologram on the fan).
/// </summary>
public static class FilterGraphBuilder
{
    private static string F(double v) => v.ToString("0.#####", CultureInfo.InvariantCulture);

    /// <summary>Builds the value passed to ffmpeg's <c>-filter:v</c>.</summary>
    public static string BuildVideoFilter(ConversionOptions o)
    {
        var filters = new List<string>
        {
            // 1. Square crop of the region the user framed in the canvas.
            $"crop={o.CropSide}:{o.CropSide}:{o.CropX}:{o.CropY}",
            // 2. Scale to the fan's native pixel grid.
            $"scale={o.OutputSize}:{o.OutputSize}:flags=lanczos",
        };

        // 3. Speed: setpts retimes frames; only add it when it actually changes.
        if (Math.Abs(o.Speed - 1.0) > 1e-6)
            filters.Add($"setpts=PTS/{F(o.Speed)}");

        // 4. Resample to the target frame rate.
        filters.Add($"fps={o.Fps}");

        // 5. Optional colour adjustment.
        if (o.Brightness != 0 || Math.Abs(o.Contrast - 1.0) > 1e-6 || Math.Abs(o.Saturation - 1.0) > 1e-6)
            filters.Add($"eq=brightness={F(o.Brightness)}:contrast={F(o.Contrast)}:saturation={F(o.Saturation)}");

        // 6. Circular mask: paint pixels outside the inscribed circle with the
        //    background colour. Commas inside the geq expressions are escaped so
        //    the filtergraph parser does not treat them as filter separators.
        if (o.CircularMask)
        {
            var bg = o.Background;
            filters.Add("format=rgba");
            filters.Add(
                "geq=" +
                $"r={MaskExpr("r", bg.R)}:" +
                $"g={MaskExpr("g", bg.G)}:" +
                $"b={MaskExpr("b", bg.B)}:" +
                "a=255");
        }

        // 7. Land on a broadly compatible pixel format for H.264.
        filters.Add("format=yuv420p");

        return string.Join(",", filters);
    }

    private static string MaskExpr(string channel, int outsideValue) =>
        $"if(lte(hypot(X-W/2\\,Y-H/2)\\,H/2)\\,{channel}(X\\,Y)\\,{outsideValue})";

    /// <summary>
    /// Builds the full ffmpeg argument vector. Passed as a real argv (no shell),
    /// so each element is one argument and needs no shell quoting.
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(string inputPath, string outputPath, ConversionOptions o)
    {
        var args = new List<string> { "-hide_banner", "-y" };

        // Trim (seek before -i is fast; -t bounds the duration on the source timeline).
        if (o.TrimStart is { } start && start > 0)
        {
            args.Add("-ss");
            args.Add(F(start));
        }

        args.Add("-i");
        args.Add(inputPath);

        var trimStart = o.TrimStart ?? 0;
        if (o.TrimEnd is { } end && end > trimStart)
        {
            args.Add("-t");
            args.Add(F(end - trimStart));
        }

        args.Add("-filter:v");
        args.Add(BuildVideoFilter(o));

        args.Add("-an"); // LED fans have no speakers; drop audio.
        args.Add("-c:v");
        args.Add("libx264");
        args.Add("-preset");
        args.Add("veryfast");
        args.Add("-crf");
        args.Add("20");
        args.Add("-pix_fmt");
        args.Add("yuv420p");
        args.Add("-movflags");
        args.Add("+faststart");
        args.Add(outputPath);

        return args;
    }
}
