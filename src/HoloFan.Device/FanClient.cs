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

    public FanClient(FanEndpoint? endpoint = null) => _endpoint = endpoint ?? new FanEndpoint();

    public FanEndpoint Endpoint => _endpoint;
    public bool IsConnected => _tcp?.Connected == true;

    /// <summary>Connects and performs the greeting the device expects before any command.</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await DisconnectAsync();
        _tcp = new TcpClient { NoDelay = true };
        await _tcp.ConnectAsync(_endpoint.Host, _endpoint.Port, ct);
        var hello = FanProtocol.Handshake();
        await _tcp.GetStream().WriteAsync(hello, ct);
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
    /// Uploads a rendered <c>.bin</c>. The bulk-transfer records (VA 0x408b4a / 0x408c3f) are not
    /// fully transcribed yet, so this refuses rather than risk a malformed stream at the device —
    /// use the SD-card route in the meantime.
    /// </summary>
    public Task UploadAsync(string name, byte[] bin, IProgress<double>? progress = null, CancellationToken ct = default)
        => throw new NotImplementedException(
            "Bulk upload framing is not transcribed yet (VA 0x408b4a / 0x408c3f). " +
            "Copy the .bin to the TF card instead. See REVERSE_ENGINEERING.md.");

    public async ValueTask DisposeAsync() => await DisconnectAsync();
}
