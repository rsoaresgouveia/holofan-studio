namespace HoloFan.Device;

/// <summary>
/// Geometry of a fan model, as read out of the vendor encoder's own config tables
/// (its <c>.text</c> holds 21 such blocks, one per model). See REVERSE_ENGINEERING.md.
///
/// The device is a POV display: a spinning blade of <see cref="Leds"/> LEDs is sampled
/// at <see cref="AngularSlices"/> angular positions per revolution, giving a polar
/// image of <c>AngularSlices × Leds</c>.
/// </summary>
/// <param name="Id">Model string as selected in the vendor tool, e.g. "42-F2".</param>
/// <param name="DiameterCm">Blade diameter in centimetres.</param>
/// <param name="Leds">LEDs along one blade (the radius).</param>
/// <param name="AngularSlices">Angular samples stored per revolution.</param>
/// <param name="SliceBytes">Encoded bytes per slice (what lands in the .bin).</param>
/// <param name="PlaneBytes">Bytes per bit-plane = SourceSliceBytes / 8.</param>
public sealed record DeviceModel(
    string Id, int DiameterCm, int Leds, int AngularSlices, int SliceBytes, int PlaneBytes)
{
    /// <summary>Channels per LED. The source is BGR (OpenCV order), 8 bits per channel.</summary>
    public const int Channels = 3;

    /// <summary>Raw bytes for one slice before packing: Leds × 3 (BGR, 8-bit).</summary>
    public int SourceSliceBytes => Leds * Channels;

    /// <summary>Bit-planes actually written to the file.</summary>
    public int PlaneCount => SliceBytes / PlaneBytes;

    /// <summary>Bytes per encoded frame.</summary>
    public int FrameBytes => AngularSlices * SliceBytes;

    /// <summary>
    /// The encoder builds 8 planes (one per source bit) in a buffer of
    /// <c>PlaneBytes × 8</c> laid out as <c>offset(bit) = PlaneBytes*7 − PlaneBytes*bit</c>,
    /// then writes only the first <see cref="SliceBytes"/>. So the low
    /// <c>8 − PlaneCount</c> bits are truncated and the file keeps the top bits.
    /// </summary>
    public int LowestBitKept => 8 - PlaneCount;

    /// <summary>File offset of the plane holding <paramref name="bit"/>, or -1 if truncated.</summary>
    public int PlaneOffsetForBit(int bit)
    {
        if (bit is < 0 or > 7) throw new ArgumentOutOfRangeException(nameof(bit));
        var off = PlaneBytes * 7 - PlaneBytes * bit;
        return off < SliceBytes ? off : -1;
    }

    /// <summary>
    /// The unit in hand: 42 cm / 16.5", "224 LEDs" advertised = 2 blades × 112.
    /// 512 slices × 252 B = 129 024 B per frame; 252 = 6 planes × 42 B ⇒ RGB666.
    /// </summary>
    public static readonly DeviceModel Fan42F2 =
        new("42-F2", DiameterCm: 42, Leds: 112, AngularSlices: 512, SliceBytes: 252, PlaneBytes: 42);

    public static readonly IReadOnlyList<DeviceModel> Catalog = new[] { Fan42F2 };

    public static DeviceModel? FindById(string id) =>
        Catalog.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
}
