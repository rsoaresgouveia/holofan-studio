namespace HoloFan.Device;

/// <summary>
/// Turns rendered video frames into the device's <c>.BIN</c> container.
///
/// The pipeline mirrors the vendor's: take the square, circularly-framed frame that
/// HoloFan.Core already produces, sample it into polar space (the vendor does this with
/// an OpenCV <c>Mat(Leds, AngularSlices, CV_8UC3)</c>), then bit-plane pack each slice.
/// </summary>
public static class BinEncoder
{
    /// <summary>
    /// Samples a square RGB frame into the device's polar source buffer
    /// (<c>AngularSlices × Leds × 3</c>, BGR interleaved).
    ///
    /// LED index 0 is the <b>outer edge</b> and index <c>Leds-1</c> is the centre — confirmed
    /// by decoding the factory demos, which only render upright with the radius flipped.
    /// </summary>
    /// <param name="model">Target device geometry.</param>
    /// <param name="rgb">Square frame, <paramref name="size"/> × <paramref name="size"/>, RGB888.</param>
    /// <param name="size">Side of the square frame in pixels.</param>
    public static byte[] PolarSample(DeviceModel model, ReadOnlySpan<byte> rgb, int size)
    {
        if (rgb.Length != size * size * 3)
            throw new ArgumentException("rgb must be size*size*3 bytes", nameof(rgb));

        var source = new byte[model.AngularSlices * model.SourceSliceBytes];
        var centre = (size - 1) / 2.0;
        var maxRadius = centre;

        for (var s = 0; s < model.AngularSlices; s++)
        {
            var angle = s * 2.0 * Math.PI / model.AngularSlices;
            var (sin, cos) = Math.SinCos(angle);
            var sliceBase = s * model.SourceSliceBytes;

            for (var i = 0; i < model.Leds; i++)
            {
                // LED 0 = rim, LED Leds-1 = centre.
                var radius = maxRadius * (model.Leds - 1 - i) / (model.Leds - 1.0);
                var x = (int)Math.Round(centre + radius * cos);
                var y = (int)Math.Round(centre + radius * sin);

                byte r = 0, g = 0, b = 0;
                if ((uint)x < (uint)size && (uint)y < (uint)size)
                {
                    var p = (y * size + x) * 3;
                    r = rgb[p]; g = rgb[p + 1]; b = rgb[p + 2];
                }

                var o = sliceBase + i * DeviceModel.Channels;
                source[o] = b;      // BGR, as the vendor's OpenCV buffer
                source[o + 1] = g;
                source[o + 2] = r;
            }
        }
        return source;
    }

    /// <summary>Packs one polar source frame (<c>AngularSlices × SourceSliceBytes</c>) into a .bin frame.</summary>
    public static byte[] EncodeFrame(DeviceModel model, ReadOnlySpan<byte> source)
    {
        if (source.Length != model.AngularSlices * model.SourceSliceBytes)
            throw new ArgumentException("source has the wrong length", nameof(source));

        var frame = new byte[model.FrameBytes];
        for (var s = 0; s < model.AngularSlices; s++)
        {
            BinFormat.PackSlice(
                model,
                source.Slice(s * model.SourceSliceBytes, model.SourceSliceBytes),
                frame.AsSpan(s * model.SliceBytes, model.SliceBytes));
        }
        return frame;
    }

    /// <summary>Concatenates encoded frames into the final .bin payload (the format has no header).</summary>
    public static byte[] Encode(DeviceModel model, IEnumerable<byte[]> polarFrames)
    {
        using var ms = new MemoryStream();
        foreach (var f in polarFrames)
        {
            var encoded = EncodeFrame(model, f);
            ms.Write(encoded, 0, encoded.Length);
        }
        return ms.ToArray();
    }
}
