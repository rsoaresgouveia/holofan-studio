using HoloFan.Device;

namespace HoloFan.Tests;

public class DeviceModelTests
{
    private static readonly DeviceModel M = DeviceModel.Fan42F2;

    [Fact]
    public void Fan42F2_matches_the_geometry_read_from_the_vendor_encoder()
    {
        Assert.Equal(112, M.Leds);              // one blade; the advertised 224 is both blades
        Assert.Equal(512, M.AngularSlices);
        Assert.Equal(252, M.SliceBytes);
        Assert.Equal(42, M.PlaneBytes);
        Assert.Equal(336, M.SourceSliceBytes);  // 112 LEDs × 3 (BGR)
        Assert.Equal(6, M.PlaneCount);          // ⇒ RGB666
        Assert.Equal(129_024, M.FrameBytes);    // the size every factory demo is a multiple of
    }

    [Fact]
    public void Plane_offsets_follow_294_minus_42_times_bit()
    {
        // Bits 7..2 survive, at ascending offsets; bits 1 and 0 are truncated by the writer.
        Assert.Equal(0, M.PlaneOffsetForBit(7));
        Assert.Equal(42, M.PlaneOffsetForBit(6));
        Assert.Equal(84, M.PlaneOffsetForBit(5));
        Assert.Equal(126, M.PlaneOffsetForBit(4));
        Assert.Equal(168, M.PlaneOffsetForBit(3));
        Assert.Equal(210, M.PlaneOffsetForBit(2));
        Assert.Equal(-1, M.PlaneOffsetForBit(1));
        Assert.Equal(-1, M.PlaneOffsetForBit(0));
        Assert.Equal(2, M.LowestBitKept);
    }
}

public class BinFormatTests
{
    private static readonly DeviceModel M = DeviceModel.Fan42F2;

    [Fact]
    public void A_source_bit_lands_in_the_plane_and_bit_the_encoder_puts_it_in()
    {
        var source = new byte[M.SourceSliceBytes];
        var packed = new byte[M.SliceBytes];

        source[9] = 0b1000_0000;               // byte 9, bit 7
        BinFormat.PackSlice(M, source, packed);

        // bit 7 → plane at offset 0; byte 9 → plane byte 1, bit 6 (MSB-first: bit 7 − (9 & 7)).
        Assert.Equal(0b0100_0000, packed[0 + 1]);
        Assert.Equal(1, packed.Count(b => b != 0));
    }

    [Fact]
    public void Truncated_low_bits_are_not_written()
    {
        var source = new byte[M.SourceSliceBytes];
        var packed = new byte[M.SliceBytes];

        for (var j = 0; j < source.Length; j++) source[j] = 0b0000_0011;  // only bits 1 and 0

        BinFormat.PackSlice(M, source, packed);
        Assert.All(packed, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Unpack_then_pack_reproduces_any_device_slice_byte_for_byte()
    {
        // The strongest check available without the hardware: whatever bytes the vendor's
        // encoder emitted, our packer must reproduce them exactly from the unpacked image.
        var rng = new Random(1234);
        var slice = new byte[M.SliceBytes];
        rng.NextBytes(slice);

        var source = new byte[M.SourceSliceBytes];
        BinFormat.UnpackSlice(M, slice, source);

        var repacked = new byte[M.SliceBytes];
        BinFormat.PackSlice(M, source, repacked);

        Assert.Equal(slice, repacked);
    }

    [Fact]
    public void Pack_then_unpack_preserves_the_bits_the_format_keeps()
    {
        var rng = new Random(99);
        var source = new byte[M.SourceSliceBytes];
        rng.NextBytes(source);
        for (var j = 0; j < source.Length; j++) source[j] &= 0b1111_1100;  // drop what the format drops

        var packed = new byte[M.SliceBytes];
        BinFormat.PackSlice(M, source, packed);
        var round = new byte[M.SourceSliceBytes];
        BinFormat.UnpackSlice(M, packed, round);

        Assert.Equal(source, round);
    }
}

/// <summary>
/// Validation against real bytes produced by the vendor's own encoder. The factory SD-card
/// backup is untrusted third-party content and is gitignored, so these tests no-op when it
/// is absent (e.g. in CI) and run for anyone who has the card.
/// </summary>
public class FactoryBinTests
{
    private static readonly DeviceModel M = DeviceModel.Fan42F2;

    private static string? FindFactoryBin()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HoloFanStudio.sln")))
            dir = dir.Parent;
        if (dir is null) return null;

        var backup = Path.Combine(dir.FullName, "backup files from factory");
        return Directory.Exists(backup)
            ? Directory.EnumerateFiles(backup, "*.BIN").FirstOrDefault()
            : null;
    }

    [Fact]
    public void Every_factory_clip_is_a_whole_number_of_frames()
    {
        var path = FindFactoryBin();
        if (path is null) return;   // card not present

        var dir = Path.GetDirectoryName(path)!;
        foreach (var f in Directory.EnumerateFiles(dir, "*.BIN"))
        {
            var length = new FileInfo(f).Length;
            var remainder = length % M.FrameBytes;
            // Some encoder versions append a 20-byte trailer; anything else means our
            // understanding of the frame size is wrong.
            Assert.True(remainder is 0 or 20, $"{Path.GetFileName(f)} has remainder {remainder}");
        }
    }

    [Fact]
    public void We_reproduce_the_vendors_own_bytes_exactly()
    {
        var path = FindFactoryBin();
        if (path is null) return;   // card not present

        var data = File.ReadAllBytes(path);
        var source = new byte[M.SourceSliceBytes];
        var repacked = new byte[M.SliceBytes];

        // Walk a frame from the middle of the clip, where there is real content.
        var frameIndex = data.Length / M.FrameBytes / 2;
        var frameStart = frameIndex * M.FrameBytes;

        for (var s = 0; s < M.AngularSlices; s++)
        {
            var slice = data.AsSpan(frameStart + s * M.SliceBytes, M.SliceBytes);
            BinFormat.UnpackSlice(M, slice, source);
            BinFormat.PackSlice(M, source, repacked);
            Assert.True(slice.SequenceEqual(repacked), $"slice {s} did not round-trip");
        }
    }
}

public class BinEncoderTests
{
    private static readonly DeviceModel M = DeviceModel.Fan42F2;

    [Fact]
    public void EncodeFrame_produces_exactly_one_frame()
    {
        var source = new byte[M.AngularSlices * M.SourceSliceBytes];
        Assert.Equal(M.FrameBytes, BinEncoder.EncodeFrame(M, source).Length);
    }

    [Fact]
    public void Encode_concatenates_frames_with_no_header()
    {
        var f = new byte[M.AngularSlices * M.SourceSliceBytes];
        Assert.Equal(M.FrameBytes * 3, BinEncoder.Encode(M, new[] { f, f, f }).Length);
    }

    [Fact]
    public void PolarSample_puts_the_centre_of_the_frame_on_the_innermost_led()
    {
        const int size = 65;   // odd, so the centre is an exact pixel rather than between two
        var rgb = new byte[size * size * 3];
        // Paint the exact centre pixel pure red.
        var c = (size - 1) / 2;
        var p = (c * size + c) * 3;
        rgb[p] = 255;

        var source = BinEncoder.PolarSample(M, rgb, size);

        // Innermost LED (index Leds-1) of slice 0 should be red, stored BGR.
        var o = (M.Leds - 1) * DeviceModel.Channels;
        Assert.Equal(0, source[o]);        // B
        Assert.Equal(0, source[o + 1]);    // G
        Assert.Equal(255, source[o + 2]);  // R
    }

    [Fact]
    public void PolarSample_is_sized_for_the_device()
    {
        const int size = 32;
        var rgb = new byte[size * size * 3];
        Assert.Equal(M.AngularSlices * M.SourceSliceBytes, BinEncoder.PolarSample(M, rgb, size).Length);
    }
}
