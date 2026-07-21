namespace HoloFan.Core;

/// <summary>
/// A known LED "hologram" fan model. These fans are circular POV (persistence
/// of vision) displays: an array of LEDs on spinning blades paints a round image
/// in the air. Content therefore has to be square, fit inside an inscribed circle,
/// and match the fan's native pixel grid.
/// </summary>
/// <param name="Id">Stable slug used by the API/UI.</param>
/// <param name="Name">Human-friendly label.</param>
/// <param name="DiameterCm">Physical blade diameter in centimetres.</param>
/// <param name="Resolution">Native square resolution in pixels.</param>
/// <param name="Fps">Frame rate the device plays comfortably.</param>
public sealed record FanPreset(string Id, string Name, int DiameterCm, int Resolution, int Fps)
{
    /// <summary>
    /// A catalogue of common consumer LED fan sizes. Exact pixel grids vary by
    /// manufacturer, so these are sensible, widely-compatible defaults; the UI
    /// always allows a fully custom resolution on top of these.
    /// </summary>
    public static readonly IReadOnlyList<FanPreset> Catalog = new[]
    {
        new FanPreset("fan-30", "30 cm — compact",   30, 224, 25),
        new FanPreset("fan-42", "42 cm — standard",  42, 384, 30),
        new FanPreset("fan-50", "50 cm — large",     50, 512, 30),
        new FanPreset("fan-56", "56 cm — XL",        56, 640, 30),
        new FanPreset("fan-65", "65 cm — XXL",       65, 832, 30),
        new FanPreset("fan-100", "1 m — pro wall",  100, 1024, 30),
    };

    public static FanPreset? FindById(string id) =>
        Catalog.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}
