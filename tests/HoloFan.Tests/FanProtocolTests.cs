using System.Text;
using HoloFan.Device;

namespace HoloFan.Tests;

public class FanProtocolTests
{
    [Fact]
    public void Framed_packet_is_payload_plus_27_bytes()
    {
        // The vendor sends `payload_len + 0x1b` (VA 0x408b3d: lea eax, [esi + 0x1b]).
        Assert.Equal(27, FanProtocol.Overhead);
        foreach (var len in new[] { 0, 1, 5, 100, 4096 })
            Assert.Equal(len + 27, FanProtocol.Frame(new byte[len]).Length);
    }

    [Fact]
    public void Packet_carries_the_header_and_trailer_magics()
    {
        var p = FanProtocol.Frame("hi"u8);
        Assert.Equal("C0EEB7C9BAA3", Encoding.ASCII.GetString(p, 0, 12));
        Assert.Equal("C0EEBDF9E5B7", Encoding.ASCII.GetString(p, p.Length - 12, 12));
        Assert.Equal("hi", Encoding.ASCII.GetString(p, 15, 2));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(322)]
    [InlineData(323)]
    [InlineData(1000)]
    [InlineData(65535)]
    public void Length_survives_the_mixed_radix_encoding(int len)
    {
        // len = 323*b0 + 17*(b1-0x63) + (b2-0x62), with 323 = 17 * 19.
        var p = FanProtocol.Frame(new byte[len]);
        Assert.Equal(len, FanProtocol.DecodeLength(p[12], p[13], p[14]));
    }

    [Fact]
    public void Handshake_is_header_plus_trailer_with_no_payload()
    {
        // The vendor sends 24 raw bytes from VA 0x40e598 right after connect.
        var h = FanProtocol.Handshake();
        Assert.Equal(24, h.Length);
        Assert.Equal("C0EEB7C9BAA3C0EEBDF9E5B7", Encoding.ASCII.GetString(h));
    }

    [Fact]
    public void Commands_match_the_bytes_the_vendor_ui_sends()
    {
        // Letters read from the payload pointers of each MFC handler; captions from its
        // dialog resources (see REVERSE_ENGINEERING.md).
        Assert.Equal((byte)'d', (byte)FanCommand.Previous);
        Assert.Equal((byte)'c', (byte)FanCommand.Next);
        Assert.Equal((byte)'e', (byte)FanCommand.PlayPause);
        Assert.Equal((byte)'h', (byte)FanCommand.ListLoop);
        Assert.Equal((byte)'g', (byte)FanCommand.SingleLoop);
        Assert.Equal((byte)'m', (byte)FanCommand.BrightnessUp);
        Assert.Equal((byte)'l', (byte)FanCommand.BrightnessDown);
        Assert.Equal((byte)'p', (byte)FanCommand.RotateCw);
        Assert.Equal((byte)'q', (byte)FanCommand.RotateCcw);
        Assert.Equal((byte)'a', (byte)FanCommand.Power);
        Assert.Equal((byte)'r', (byte)FanCommand.WifiConfig);
        Assert.Equal((byte)'k', (byte)FanCommand.ClearCache);
        Assert.Equal((byte)'j', (byte)FanCommand.FormatDisk);
    }

    [Fact]
    public void Parses_the_real_playlist_a_42F2_returned()
    {
        // Captured live from the fan at 192.168.4.1:20320 in reply to the handshake:
        // header + 00"gpi" + <len><name> entries (GBK) + status tail + trailer.
        var reply = Convert.FromHexString(
            "433045454237433942414133" +                       // header "C0EEB7C9BAA3"
            "00677069" +                                       // 00 "gpi"
            "0330d3e3" + "0531bafcc0ea" + "0532bfd6c1fa" +     // 0鱼 1狐狸 2恐龙
            "0533c9f1cade" + "06345449474552" +               // 3神兽 4TIGER
            "0536b5c6c1fd" + "0337b4ba" + "0539c6fbb3b5" +     // 6灯笼 7春 9汽车
            "04b7bfd7d3" + "02c1b3" + "06c2edc0efb0c2" +       // 房子 脸 马里奥
            "04cef7b9cf" +                                     // 西瓜
            "020c0000000001010000000000000000" +               // status tail (0x0c = 12 files)
            "433045454244463945354237");                       // trailer "C0EEBDF9E5B7"

        var files = FanProtocol.ParsePlaylist(reply);

        Assert.Equal(12, files.Count);
        Assert.Equal("0鱼", files[0]);
        Assert.Equal("4TIGER", files[4]);
        Assert.Equal("马里奥", files[10]);   // Mario
        Assert.Equal("西瓜", files[11]);     // watermelon
    }

    [Fact]
    public void Playlist_parse_is_safe_on_garbage()
    {
        Assert.Empty(FanProtocol.ParsePlaylist(new byte[] { 1, 2, 3 }));
        Assert.Empty(FanProtocol.ParsePlaylist(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Endpoint_defaults_to_what_the_binary_hardcodes()
    {
        var e = new FanEndpoint();
        Assert.Equal("192.168.4.1", e.Host);   // string at VA 0x40dc7c
        Assert.Equal(20320, e.Port);           // 0x4f60 at VA 0x401a33
    }
}

public class FanClientSafetyTests
{
    [Fact]
    public async Task Destructive_commands_refuse_without_explicit_confirmation()
    {
        await using var client = new FanClient();

        // Format Disk wipes every clip on the card — it must never fire by accident.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.SendAsync(FanCommand.FormatDisk));
        Assert.Contains("confirmDestructive", ex.Message);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.SendAsync(FanCommand.ClearCache));
    }

    [Fact]
    public async Task Harmless_commands_fail_only_because_we_are_not_connected()
    {
        await using var client = new FanClient();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.SendAsync(FanCommand.PlayPause));
        Assert.Contains("Not connected", ex.Message);
    }
}
