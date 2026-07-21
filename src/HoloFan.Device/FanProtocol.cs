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
