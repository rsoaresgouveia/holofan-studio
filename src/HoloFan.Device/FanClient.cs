using System.Net.Sockets;

namespace HoloFan.Device;

/// <summary>
/// Where the fan listens, read straight out of the vendor app's own machine code
/// (VA 0x401a29 initialises these members) — no packet capture was needed.
///
/// The transport is plain TCP: the app imports only <c>socket / connect / send / recv /
/// select / ioctlsocket / closesocket</c> from WS2_32 (by ordinal). No HTTP, no TLS.
/// </summary>
public sealed record FanEndpoint
{
    /// <summary>The fan's address on its own AP — the classic ESP SoftAP gateway.</summary>
    public string Host { get; init; } = DefaultHost;

    /// <summary>TCP port the fan listens on.</summary>
    public int Port { get; init; } = DefaultPort;

    /// <summary>Literal string at VA 0x40dc7c, stored into the client's IP member.</summary>
    public const string DefaultHost = "192.168.4.1";

    /// <summary>0x4f60, written to both port members at VA 0x401a33 / 0x401a3d.</summary>
    public const int DefaultPort = 20320;

    /// <summary>Factory password for changing the AP's name/password — not needed to join.</summary>
    public const string FactoryConfigPassword = "123456789";
}

/// <summary>
/// Talks to the fan over its own WiFi AP, speaking the protocol we lifted from the vendor
/// binary. Join the <c>3DCircle_…</c> network first (it is open); then every button the vendor
/// software offers is available here.
///
/// Destructive commands (<see cref="FanCommand.FormatDisk"/>, <see cref="FanCommand.ClearCache"/>)
/// require <paramref name="confirmDestructive"/> — they wipe content on the device's card.
/// </summary>
public sealed class FanClient : IAsyncDisposable
{
    private readonly FanEndpoint _endpoint;
    private TcpClient? _tcp;
    private byte[]? _handshakeReply;

    public FanClient(FanEndpoint? endpoint = null) => _endpoint = endpoint ?? new FanEndpoint();

    public FanEndpoint Endpoint => _endpoint;
    public bool IsConnected => _tcp?.Connected == true;

    /// <summary>
    /// Connects and performs the greeting the device expects. The device replies with its
    /// "configuration" — the playlist — which we keep for <see cref="ListAsync"/>.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await DisconnectAsync();
        _tcp = new TcpClient { NoDelay = true };
        await _tcp.ConnectAsync(_endpoint.Host, _endpoint.Port, ct);
        var stream = _tcp.GetStream();
        await stream.WriteAsync(FanProtocol.Handshake(), ct);
        _handshakeReply = await ReadFrameAsync(stream, ct);
    }

    /// <summary>Reads one framed reply (…up to and including the trailer magic), or null on timeout.</summary>
    private static async Task<byte[]?> ReadFrameAsync(NetworkStream stream, CancellationToken ct)
    {
        var trailer = System.Text.Encoding.ASCII.GetBytes(FanProtocol.TrailerMagic);
        var buffer = new List<byte>();
        var tmp = new byte[1024];
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            while (buffer.Count < 64 * 1024)
            {
                var n = await stream.ReadAsync(tmp, cts.Token);
                if (n == 0) break;
                buffer.AddRange(tmp[..n]);
                if (buffer.Count >= trailer.Length &&
                    buffer.GetRange(buffer.Count - trailer.Length, trailer.Length).SequenceEqual(trailer))
                    break;
            }
        }
        catch (OperationCanceledException) { /* timeout: return what we have */ }
        return buffer.Count > 0 ? buffer.ToArray() : null;
    }

    public async Task DisconnectAsync()
    {
        if (_tcp is null) return;
        try { _tcp.Close(); } catch { /* already gone */ }
        _tcp.Dispose();
        _tcp = null;
        await Task.CompletedTask;
    }

    /// <summary>Sends one of the fan's single-byte commands.</summary>
    public Task SendAsync(FanCommand command, bool confirmDestructive = false, CancellationToken ct = default)
    {
        if (command is FanCommand.FormatDisk or FanCommand.ClearCache && !confirmDestructive)
            throw new InvalidOperationException(
                $"{command} erases content on the fan's card. Pass confirmDestructive: true to proceed.");

        return SendPayloadAsync(new[] { (byte)command }, ct);
    }

    /// <summary>Sets one of the clock dials (the 5-byte <c>'b'</c> family).</summary>
    public Task SetClockAsync(ClockSetting setting, byte value, CancellationToken ct = default)
        => SendPayloadAsync(new byte[] { (byte)'b', value, 0, 0, (byte)setting }, ct);

    /// <summary>Frames and sends an arbitrary payload. Public for the commands we have not named yet.</summary>
    public async Task SendPayloadAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        if (_tcp is null || !_tcp.Connected)
            throw new InvalidOperationException("Not connected to the fan. Call ConnectAsync first.");

        var packet = FanProtocol.Frame(payload.Span);
        await _tcp.GetStream().WriteAsync(packet, ct);
    }

    /// <summary>
    /// Uploads a rendered <c>.bin</c> to the device over WiFi, then reconnects so the playlist
    /// refreshes. The sequence was decoded from the vendor's upload worker (VA 0x405a20 →
    /// 0x4050b0):
    ///
    ///   fresh connection (no handshake) → 20-byte BEGIN → filename chunk → read+validate ack
    ///   → raw file bytes (0x3c000-byte blocks) → 20-byte END marker → close.
    ///
    /// The device names clips without an extension, so <paramref name="name"/> is sent as-is.
    ///
    /// ⚠ Validated live only up to the filename ack. In testing the device accepts the name but
    /// does NOT persist the clip after data + END + close — there is a final reconnect/verify
    /// round (VA 0x4055ff) that is not yet reverse-engineered. Until that lands, this may not make
    /// the file appear in the playlist; the SD-card route is the reliable path. See
    /// REVERSE_ENGINEERING.md.
    /// </summary>
    public async Task UploadAsync(string name, byte[] bin, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        // Upload runs on its own socket; the device accepts a single client, so free the
        // shared connection first, then restore it afterwards to refresh the playlist.
        var wasConnected = IsConnected;
        await DisconnectAsync();

        try
        {
            using var tcp = new TcpClient { NoDelay = true };
            await tcp.ConnectAsync(_endpoint.Host, _endpoint.Port, ct);
            var stream = tcp.GetStream();

            await stream.WriteAsync(FanProtocol.UploadBegin(), ct);
            await stream.WriteAsync(FanProtocol.Chunk(System.Text.Encoding.ASCII.GetBytes(name)), ct);

            var ack = await ReadFrameAsync(stream, ct);
            if (ack is null || !FanProtocol.IsFramedReply(ack))
                throw new InvalidOperationException("The fan did not acknowledge the filename.");

            // Raw file bytes, in blocks, reporting progress.
            const int block = 0x3C000;
            for (var sent = 0; sent < bin.Length; sent += block)
            {
                var n = Math.Min(block, bin.Length - sent);
                await stream.WriteAsync(bin.AsMemory(sent, n), ct);
                progress?.Report((double)(sent + n) / bin.Length);
            }
            await stream.WriteAsync(FanProtocol.UploadBegin(), ct);   // same 20-byte marker delimits the end
            await stream.FlushAsync(ct);
        }
        finally
        {
            if (wasConnected)
                try { await ConnectAsync(ct); } catch { /* best effort refresh */ }
        }
    }

    /// <summary>
    /// The clips stored on the device's card, parsed from the playlist it returned at connect
    /// (the device sends this once, right after the handshake — its "device configuration").
    /// </summary>
    public IReadOnlyList<string> List()
        => _handshakeReply is null ? Array.Empty<string>() : FanProtocol.ParsePlaylist(_handshakeReply);

    public async ValueTask DisposeAsync() => await DisconnectAsync();
}
