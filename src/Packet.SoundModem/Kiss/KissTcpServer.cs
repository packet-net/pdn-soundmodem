using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Packet.SoundModem.Channel;

namespace Packet.SoundModem.Kiss;

/// <summary>
/// Multi-client KISS-over-TCP server bound to one <see cref="SoundModemChannel"/>: the
/// KISS port nibble addresses the channel's logical modems. Received frames broadcast to
/// every client; data frames from any client queue for transmission; ACKMODE frames get
/// their two-byte id echoed back to the originating client once the frame's audio has
/// fully left the device (true TX-complete, not a timer guess). KISS parameter commands
/// (TXDELAY, P, SLOTTIME, TXTAIL) update the channel's CSMA settings - unlike
/// QtSoundModem, which silently ignores them.
/// </summary>
/// <remarks>
/// Constructed with a <c>subChannel</c> the server is instead <b>dedicated</b> to one modem:
/// it surfaces only that modem's frames, rewritten to nibble 0, and transmits everything it is
/// given on that modem whatever nibble the client used. That exists for the large amount of
/// host software which hardcodes KISS channel 0 and offers no way to set the nibble - on the
/// multiplexed port such a host can only ever reach sub-channel 0, however many modems are
/// configured. Several servers can share one channel, so a daemon can offer the multiplexed
/// port and per-modem ports at the same time.
/// </remarks>
public sealed class KissTcpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly SoundModemChannel _channel;
    private readonly int? _dedicatedSubChannel;
    private readonly ConcurrentDictionary<Guid, TcpClient> _clients = [];
    private readonly CancellationTokenSource _stopping = new();
    private Task? _acceptLoop;

    /// <summary>Creates a server for <paramref name="channel"/> on
    /// <paramref name="port"/> (0 = ephemeral, see <see cref="LocalPort"/>).</summary>
    /// <param name="channel">The modem channel to serve.</param>
    /// <param name="port">TCP port to listen on; 0 binds an ephemeral one.</param>
    /// <param name="bind">Address to bind; loopback when null.</param>
    /// <param name="subChannel">
    /// When set, the server is dedicated to that one modem: only its frames are surfaced (as
    /// nibble 0) and all transmits go to it regardless of the nibble received. When null the
    /// server is multiplexed and the nibble selects the modem, as QtSoundModem does.
    /// </param>
    public KissTcpServer(
        SoundModemChannel channel, int port = 8105, IPAddress? bind = null, int? subChannel = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        _channel = channel;
        _dedicatedSubChannel = subChannel;
        _listener = new TcpListener(bind ?? IPAddress.Loopback, port);
        _channel.FrameReceived += OnFrameReceived;
        _channel.FrameReceivedWithQuality += OnFrameQuality;
    }

    /// <summary>The modem this server is dedicated to, or null when it is multiplexed.</summary>
    public int? DedicatedSubChannel => _dedicatedSubChannel;

    /// <summary>A host opened a KISS session on this port.</summary>
    /// <remarks>
    /// Who is attached is operational information a station keeps no other record of: a node that
    /// stops passing traffic because its host quietly dropped the TCP session looks identical, from
    /// the modem's side, to a band that went quiet. Raised on the accept, before the client has
    /// sent anything.
    /// </remarks>
    public event Action<KissClientEvent>? ClientConnected;

    /// <summary>A host's KISS session ended, cleanly or otherwise.</summary>
    public event Action<KissClientEvent>? ClientDisconnected;

    /// <summary>
    /// When true, each received data frame is followed by a <see cref="KissCommand.RxQuality"/>
    /// frame on the same port nibble carrying its decode diagnostics as UTF-8 JSON, e.g.
    /// <c>{"mode":"qpsk2400-il2pc","len":56,"corrected":2,"crc":true}</c>. OFF by default:
    /// only hosts that know the extension should ever see it.
    /// </summary>
    public bool EmitQualityFrames { get; set; }

    /// <summary>The bound port (useful when constructed with port 0).</summary>
    public int LocalPort => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Starts accepting clients.</summary>
    public void Start()
    {
        _listener.Start();
        _acceptLoop = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(_stopping.Token).ConfigureAwait(false);
                client.NoDelay = true;
                var id = Guid.NewGuid();
                _clients[id] = client;
                // Read the endpoint now: after the socket is disposed there is nothing to ask, and
                // the disconnect line is the half an operator most wants to attribute.
                EndPoint? remote = client.Client.RemoteEndPoint;
                ClientConnected?.Invoke(new KissClientEvent(remote, _clients.Count));
                _ = ServeClientAsync(id, client, remote);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ServeClientAsync(Guid id, TcpClient client, EndPoint? remote)
    {
        string? reason = null;
        var decoder = new KissDecoder(frame => OnClientFrame(client, frame));
        var buffer = new byte[4096];
        try
        {
            NetworkStream stream = client.GetStream();
            while (!_stopping.IsCancellationRequested)
            {
                int got = await stream.ReadAsync(buffer, _stopping.Token).ConfigureAwait(false);
                if (got == 0)
                {
                    break;
                }

                decoder.Push(buffer.AsSpan(0, got));
            }
        }
        catch (OperationCanceledException)
        {
            // The server is shutting down, not the client misbehaving: a clean end.
        }
        catch (Exception e)
        {
            // Client errors only ever cost that client its connection - but say which and why,
            // because "the host vanished" and "the host closed" are different problems.
            reason = e.Message;
        }
        finally
        {
            _clients.TryRemove(id, out _);
            client.Dispose();
            ClientDisconnected?.Invoke(new KissClientEvent(remote, _clients.Count, reason));
        }
    }

    private void OnClientFrame(TcpClient origin, KissFrame frame)
    {
        switch (frame.Command)
        {
            case KissCommand.Data:
                _ = _channel.EnqueueTransmit(TransmitSubChannel(frame.Port), frame.Payload);
                break;

            case KissCommand.AckModeData when frame.Payload.Length > 2:
            {
                byte[] ackId = frame.Payload[..2];
                int port = TransmitSubChannel(frame.Port);
                Task sent = _channel.EnqueueTransmit(port, frame.Payload[2..]);
                _ = sent.ContinueWith(
                    t =>
                    {
                        if (t.IsCompletedSuccessfully)
                        {
                            Send(origin, KissCodec.Encode(new KissFrame(port, KissCommand.AckModeData, ackId)));
                        }
                    },
                    TaskScheduler.Default);
                break;
            }

            case KissCommand.TxDelay when frame.Payload.Length >= 1:
                _channel.Csma.TxDelayMilliseconds = frame.Payload[0] * 10;
                break;
            case KissCommand.Persistence when frame.Payload.Length >= 1:
                _channel.Csma.Persistence = frame.Payload[0];
                break;
            case KissCommand.SlotTime when frame.Payload.Length >= 1:
                _channel.Csma.SlotTimeMilliseconds = frame.Payload[0] * 10;
                break;
            case KissCommand.TxTail when frame.Payload.Length >= 1:
                _channel.Csma.TxTailMilliseconds = frame.Payload[0] * 10;
                break;

            case KissCommand.FullDuplex:
            case KissCommand.SetHardware:
                break; // accepted, currently no-ops (half duplex only; no hardware subcommands yet)

            default:
                break;
        }
    }

    /// <summary>
    /// Where a client's frame is transmitted. A dedicated server ignores the client's nibble
    /// entirely - the whole point is to serve a host that can only ever send 0.
    /// </summary>
    private int TransmitSubChannel(int requested) => _dedicatedSubChannel ?? requested;

    /// <summary>
    /// The nibble a frame is published under, or null to withhold it from this server's
    /// clients. A dedicated server publishes only its own modem, relabelled 0.
    /// </summary>
    private int? PublishNibble(int subChannel) => _dedicatedSubChannel switch
    {
        null => subChannel,
        int dedicated when dedicated == subChannel => 0,
        _ => null,
    };

    private void OnFrameReceived(int subChannel, byte[] frame)
    {
        if (PublishNibble(subChannel) is not int nibble)
        {
            return;
        }

        byte[] encoded = KissCodec.Encode(new KissFrame(nibble, KissCommand.Data, frame));
        foreach (TcpClient client in _clients.Values)
        {
            Send(client, encoded);
        }
    }

    private void OnFrameQuality(int subChannel, byte[] frame, Modems.FrameQuality quality)
    {
        // A frame the station read and did not pass on has no data frame here for this to
        // describe, so describing it would be a report of something the client never received.
        // Asked of the quality itself rather than inferred from whether OnFrameReceived fired:
        // the two events arrive from the same synchronous decode, so ordering looks like a usable
        // signal right up to the frame where one of them simply does not come.
        if (!EmitQualityFrames || quality.MonitorOnly || PublishNibble(subChannel) is not int nibble)
        {
            return;
        }

        // Compact JSON, absent-means-null. Sent after the data frame it describes (the
        // channel raises quality from the same synchronous decode, so ordering holds).
        var json = new System.Text.StringBuilder(96);
        json.Append("{\"mode\":\"").Append(quality.Mode).Append("\",\"len\":").Append(quality.FrameBytes);
        if (quality.CorrectedBytes is { } corrected)
        {
            json.Append(",\"corrected\":").Append(corrected);
        }

        if (quality.CrcValid is { } crc)
        {
            json.Append(",\"crc\":").Append(crc ? "true" : "false");
        }

        if (quality.FrequencyOffsetHz is { } off)
        {
            json.Append(",\"offsetHz\":").Append(off.ToString("F0", System.Globalization.CultureInfo.InvariantCulture));
        }

        if (quality.EmphasisDb is { } emph)
        {
            json.Append(",\"emphasisDb\":").Append(emph);
        }

        json.Append('}');
        byte[] encoded = KissCodec.Encode(new KissFrame(
            nibble, KissCommand.RxQuality, System.Text.Encoding.UTF8.GetBytes(json.ToString())));
        foreach (TcpClient client in _clients.Values)
        {
            Send(client, encoded);
        }
    }

    private static void Send(TcpClient client, byte[] data)
    {
        try
        {
            client.GetStream().Write(data);
        }
        catch (Exception)
        {
            // Broken pipe: the client's read loop will clean it up.
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _channel.FrameReceived -= OnFrameReceived;
        // Was never unsubscribed: harmless with one server for the process lifetime, a leak
        // once a channel carries several (the per-modem listeners) or any are disposed early.
        _channel.FrameReceivedWithQuality -= OnFrameQuality;
        await _stopping.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        foreach (TcpClient client in _clients.Values)
        {
            client.Dispose();
        }

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        _stopping.Dispose();
    }
}

/// <summary>A host attaching to, or leaving, a KISS-over-TCP port.</summary>
/// <param name="Remote">The host's address, or null if the socket was already gone.</param>
/// <param name="Clients">How many clients hold a session on that port afterwards.</param>
/// <param name="Reason">
/// Why the session ended, when it did not end cleanly; null for a clean close and on connect.
/// </param>
public readonly record struct KissClientEvent(
    System.Net.EndPoint? Remote, int Clients, string? Reason = null);
