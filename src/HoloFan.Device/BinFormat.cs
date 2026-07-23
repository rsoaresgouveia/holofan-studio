namespace HoloFan.Device;

/// <summary>
/// The vendor's proprietary <c>.BIN</c> container, reverse-engineered from the encoder's
/// own machine code and validated against the factory demo clips (decoding them renders
/// the original artwork). Full write-up in REVERSE_ENGINEERING.md.
///
/// <code>
/// file   = frame[]                      (no global header; some encoders append a trailer)
/// frame  = AngularSlices × SliceBytes   (512 × 252 = 129 024 for the 42-F2)
/// slice  = PlaneCount bit-planes × PlaneBytes   (6 × 42 = 252)
/// plane  = PlaneBytes = one bit for each source byte (42 B = 336 bits)
/// source = Leds × 3 bytes, BGR interleaved, 8-bit   (112 × 3 = 336)
/// </code>
///
/// Packing rule (read off the unrolled packer at VA 0x40741b): the encoder emits eight
/// planes at <c>offset(bit) = 294 − 42·bit</c>, then writes only the first 252 bytes — so
/// bits 1 and 0 fall off the end and the file carries bits 7..2 (RGB666). Within a plane
/// the packing is <b>MSB-first</b>: source byte <c>j</c> occupies bit <c>7 − (j &amp; 7)</c>
/// of plane byte <c>j &gt;&gt; 3</c>. (Validated by decoding the factory demo clips: only
/// MSB-first bit order renders the original artwork; LSB-first scrambles every flat colour
/// into concentric rainbow rings on the physical fan.)
/// </summary>
public static class BinFormat
{
    /// <summary>
    /// Packs one raw slice (<c>Leds × 3</c> BGR bytes) into its encoded form
    /// (<see cref="DeviceModel.SliceBytes"/>). The low bits that the format truncates are
    /// simply not written — encoding is therefore lossy in exactly the way the device expects.
    /// </summary>
    public static void PackSlice(DeviceModel model, ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.Length != model.SourceSliceBytes)
            throw new ArgumentException($"source must be {model.SourceSliceBytes} bytes", nameof(source));
        if (destination.Length != model.SliceBytes)
            throw new ArgumentException($"destination must be {model.SliceBytes} bytes", nameof(destination));

        destination.Clear();
        for (var bit = model.LowestBitKept; bit < 8; bit++)
        {
            var planeOffset = model.PlaneOffsetForBit(bit);
            if (planeOffset < 0) continue;

            for (var j = 0; j < source.Length; j++)
            {
                if ((source[j] >> bit & 1) != 0)
                    destination[planeOffset + (j >> 3)] |= (byte)(1 << (7 - (j & 7)));
            }
        }
    }

    /// <summary>
    /// Unpacks an encoded slice back to <c>Leds × 3</c> BGR bytes. The truncated low bits
    /// come back as zero, so <c>Pack(Unpack(x)) == x</c> for any slice the device produced.
    /// </summary>
    public static void UnpackSlice(DeviceModel model, ReadOnlySpan<byte> slice, Span<byte> destination)
    {
        if (slice.Length != model.SliceBytes)
            throw new ArgumentException($"slice must be {model.SliceBytes} bytes", nameof(slice));
        if (destination.Length != model.SourceSliceBytes)
            throw new ArgumentException($"destination must be {model.SourceSliceBytes} bytes", nameof(destination));

        destination.Clear();
        for (var bit = model.LowestBitKept; bit < 8; bit++)
        {
            var planeOffset = model.PlaneOffsetForBit(bit);
            if (planeOffset < 0) continue;

            for (var j = 0; j < destination.Length; j++)
            {
                if ((slice[planeOffset + (j >> 3)] >> (7 - (j & 7)) & 1) != 0)
                    destination[j] |= (byte)(1 << bit);
            }
        }
    }
}
