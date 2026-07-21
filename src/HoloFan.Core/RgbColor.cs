using System.Globalization;

namespace HoloFan.Core;

/// <summary>A simple 8-bit RGB colour with hex parsing, used for the area outside the circle.</summary>
public readonly record struct RgbColor(int R, int G, int B)
{
    public static readonly RgbColor Black = new(0, 0, 0);

    /// <summary>Parses "#rrggbb" or "rrggbb" (also accepts "#rgb"). Falls back to black on empty input.</summary>
    public static RgbColor Parse(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return Black;

        var s = hex.Trim().TrimStart('#');
        if (s.Length == 3)
            s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);

        if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            throw new FormatException($"Invalid hex colour: '{hex}'. Expected #rrggbb.");

        return new RgbColor(
            int.Parse(s.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(s.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(s.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    public string ToHex() => $"#{R:x2}{G:x2}{B:x2}";
}
