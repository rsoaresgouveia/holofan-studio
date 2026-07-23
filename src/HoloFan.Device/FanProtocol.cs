namespace HoloFan.Device;

/// <summary>
/// The fan's wire protocol, transcribed from the vendor app's machine code
/// (packer at VA 0x408a80). See REVERSE_ENGINEERING.md.
///
/// Every command is one framed packet:
/// <code>
/// "C0EEB7C9BAA3"   12 B header
/// len / 323        ─┐
/// 0x63 + (len/17)%19│ 3 B length, mixed radix (323 = 17 × 19)
/// 0x62 + len % 17  ─┘
/// &lt;payload&gt;        len B
/// "C0EEBDF9E5B7"   12 B trailer
/// </code>
/// The two magics are ASCII-hex of GBK and spell the developers' names — they are literal
/// bytes on the wire, not text to interpret.
/// </summary>
public static class FanProtocol
{
    public const string HeaderMagic = "C0EEB7C9BAA3";
    public const string TrailerMagic = "C0EEBDF9E5B7";

    /// <summary>Bytes a framed packet adds around its payload (12 + 3 + 12).</summary>
    public const int Overhead = 27;

    /// <summary>The greeting sent immediately after connect: header+trailer, empty payload.</summary>
    public static byte[] Handshake() =>
        System.Text.Encoding.ASCII.GetBytes(HeaderMagic + TrailerMagic);

    /// <summary>Wraps a payload in the device's framing.</summary>
    public static byte[] Frame(ReadOnlySpan<byte> payload)
    {
        var len = payload.Length;
        var packet = new byte[len + Overhead];
        var p = 0;

        p += System.Text.Encoding.ASCII.GetBytes(HeaderMagic, packet.AsSpan(p));
        packet[p++] = (byte)(len / 323);
        packet[p++] = (byte)(0x63 + len / 17 % 19);
        packet[p++] = (byte)(0x62 + len % 17);
        payload.CopyTo(packet.AsSpan(p));
        p += len;
        System.Text.Encoding.ASCII.GetBytes(TrailerMagic, packet.AsSpan(p));

        return packet;
    }

    /// <summary>Recovers a payload length from the 3 encoded bytes (inverse of <see cref="Frame"/>).</summary>
    public static int DecodeLength(byte b0, byte b1, byte b2) =>
        b0 * 323 + (b1 - 0x63) * 17 + (b2 - 0x62);

    // --- File upload (separate framing) --------------------------------------
    // Chunks use a different header/length encoding from commands. Decoded from the
    // chunk packer at VA 0x408b70 and confirmed live: the device's own ack is a chunk
    // whose length bytes decode exactly through this scheme.

    /// <summary>The 8-byte chunk header (also the first half of the .BIN trailer signature).</summary>
    public const string ChunkHeaderMagic = "B2DDDDED";

    /// <summary>The 20-byte marker that opens an upload (chunk header + trailer, no length/payload).</summary>
    public static byte[] UploadBegin() =>
        System.Text.Encoding.ASCII.GetBytes(ChunkHeaderMagic + TrailerMagic);

    private static int HighMul(int a, int b) => (int)((long)a * b >> 32);

    /// <summary>
    /// The 3-byte length field for an upload chunk, replicating the vendor's exact integer
    /// arithmetic (imul-magic divides). Verified against the device: it accepted our filename
    /// chunks and its own acks encode their length the same way.
    /// </summary>
    public static byte[] ChunkLengthBytes(int len)
    {
        var q1 = (HighMul(len, unchecked((int)0x88888889)) + len) >> 3;
        if (q1 < 0) q1++;
        var q2 = HighMul(len, 0x55555556);
        if (q2 < 0) q2++;
        return new[]
        {
            (byte)q1,
            (byte)(0x6A + q2 % 5),
            (byte)(0x63 + (len - q1 * 15) % 3),
        };
    }

    /// <summary>Wraps upload payload in the chunk framing: header + 3-byte length + data + trailer.</summary>
    public static byte[] Chunk(ReadOnlySpan<byte> data)
    {
        var hdr = System.Text.Encoding.ASCII.GetBytes(ChunkHeaderMagic);
        var trl = System.Text.Encoding.ASCII.GetBytes(TrailerMagic);
        var len = ChunkLengthBytes(data.Length);
        var packet = new byte[hdr.Length + len.Length + data.Length + trl.Length];
        var p = 0;
        hdr.CopyTo(packet, p); p += hdr.Length;
        len.CopyTo(packet, p); p += len.Length;
        data.CopyTo(packet.AsSpan(p)); p += data.Length;
        trl.CopyTo(packet, p);
        return packet;
    }

    /// <summary>True if a reply is a well-formed chunk (device acknowledged), i.e. carries both magics.</summary>
    public static bool IsFramedReply(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 20) return false;
        var hdr = System.Text.Encoding.ASCII.GetBytes(ChunkHeaderMagic);
        var trl = System.Text.Encoding.ASCII.GetBytes(TrailerMagic);
        return reply.Slice(0, hdr.Length).SequenceEqual(hdr)
            && reply.Slice(reply.Length - trl.Length).SequenceEqual(trl);
    }

    private static readonly System.Text.Encoding Gbk;

    static FanProtocol()
    {
        // The device names files in GBK (code page 936) — register the provider so we can decode them.
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        Gbk = System.Text.Encoding.GetEncoding(936);
    }

    /// <summary>
    /// Parses the playlist the device sends back right after the handshake (its "device
    /// configuration"): between the header/trailer magics it carries a status byte, a 3-char tag
    /// ("gpi"), then length-prefixed file names (GBK), then a status tail whose first byte is the
    /// file count. Confirmed against a live 42-F2 (12 demo clips). Pure and unit-tested.
    /// </summary>
    public static IReadOnlyList<string> ParsePlaylist(ReadOnlySpan<byte> reply)
        => ParseStatus(reply).Files;

    /// <summary>
    /// Parses the device's handshake reply into the clip list and readable state. The reply is
    /// <c>header + 00"gpi" + &lt;len&gt;&lt;name&gt; entries + status tail + trailer</c>; in the
    /// tail, byte 3 tracks power (1 = on, 0 = standby) — confirmed live by toggling Power.
    /// </summary>
    public static FanStatus ParseStatus(ReadOnlySpan<byte> reply)
    {
        var names = new List<string>();
        var header = System.Text.Encoding.ASCII.GetBytes(HeaderMagic);
        var trailer = System.Text.Encoding.ASCII.GetBytes(TrailerMagic);

        var start = IndexOf(reply, header);
        if (start < 0) return new FanStatus(names, null);
        start += header.Length;
        var end = IndexOf(reply.Slice(start), trailer);
        var payload = end < 0 ? reply.Slice(start) : reply.Slice(start, end);

        // Skip the leading status byte (0x00) and the ascii tag ("gpi").
        var i = 0;
        if (i < payload.Length && payload[i] == 0) i++;
        while (i < payload.Length && payload[i] is >= (byte)'a' and <= (byte)'z') i++;

        // Length-prefixed names; stop at the status tail (whose bytes aren't a plausible name).
        while (i < payload.Length)
        {
            int len = payload[i];
            if (len == 0 || i + 1 + len > payload.Length) break;
            var nameBytes = payload.Slice(i + 1, len);
            if (!IsPlausibleName(nameBytes)) break;
            names.Add(Gbk.GetString(nameBytes));
            i += 1 + len;
        }

        var tail = payload[i..];
        bool? poweredOn = tail.Length > 3 ? tail[3] != 0 : null;
        return new FanStatus(names, poweredOn);
    }

    // A name byte is printable ASCII or a GBK lead/continuation byte (>= 0x81); the status tail
    // (which starts with a small control byte like 0x0c) fails this and ends the list.
    private static bool IsPlausibleName(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
            if (b is < 0x20 or (> 0x7e and < 0x81)) return false;
        return true;
    }

    private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle)) return i;
        return -1;
    }
}

/// <summary>
/// The single-byte commands, with the button each one is wired to in the vendor UI
/// (control IDs from its MFC message map, captions from its dialog resources).
/// </summary>
public enum FanCommand : byte
{
    /// <summary>"Last one" — previous clip (ID 1008).</summary>
    Previous = (byte)'d',

    /// <summary>"Next one" — next clip (ID 1009).</summary>
    Next = (byte)'c',

    /// <summary>"Play/Pause" (ID 1051).</summary>
    PlayPause = (byte)'e',

    /// <summary>"List loop" — cycle the whole playlist (ID 1052).</summary>
    ListLoop = (byte)'h',

    /// <summary>"Single loop" — repeat one clip (ID 1079).</summary>
    SingleLoop = (byte)'g',

    /// <summary>"Brightness+" (ID 1054).</summary>
    BrightnessUp = (byte)'m',

    /// <summary>"Brightness-" (ID 1053).</summary>
    BrightnessDown = (byte)'l',

    /// <summary>"CW adjust" — rotate the image clockwise (ID 1069).</summary>
    RotateCw = (byte)'p',

    /// <summary>"CCW adjust" — rotate the image anticlockwise (ID 1070).</summary>
    RotateCcw = (byte)'q',

    /// <summary>"On/Off" — run/standby (ID 1066).</summary>
    Power = (byte)'a',

    /// <summary>"Wi-Fi config" — opens the SSID/password change flow (ID 1057).</summary>
    WifiConfig = (byte)'r',

    /// <summary>"Clear Cache" — deletes files the device considers junk (ID 1037). Destructive.</summary>
    ClearCache = (byte)'k',

    /// <summary>"Format Disk" — <b>erases every clip on the card</b> (ID 1038). Destructive.</summary>
    FormatDisk = (byte)'j',
}

/// <summary>Readable device state parsed from the handshake/playlist reply.</summary>
public sealed record FanStatus(IReadOnlyList<string> Files, bool? PoweredOn);

/// <summary>Which dial the 5-byte <c>'b'</c> command targets (clock feature).</summary>
public enum ClockSetting : byte
{
    /// <summary>Clock on (1) / off (0).</summary>
    Enabled = 2,

    /// <summary>Needle colour: white / black.</summary>
    NeedleColour = 3,

    /// <summary>Dial style: 0 Digital, 1 Symbol, 2 Constellation, 3 Zodiac.</summary>
    DialStyle = 4,
}
