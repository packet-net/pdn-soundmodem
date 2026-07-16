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
/// (TXDELAY, P, SLOTTIME, TXTAIL) update the channel's CSMA settings — unlike
/// QtSoundModem, which silently ignores them.
/// </summary>
public sealed class KissTcpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly SoundModemChannel _channel;
    private readonly ConcurrentDictionary<Guid, TcpClient> _clients = [];
    private readonly CancellationTokenSource _stopping = new();
    private Task? _acceptLoop;

    /// <summary>Creates a server for <paramref name="channel"/> on
    /// <paramref name="port"/> (0 = ephemeral, see <see cref="LocalPort"/>).</summary>
    public KissTcpServer(SoundModemChannel channel, int port = 8105, IPAddress? bind = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        _channel = channel;
        _listener = new TcpListener(bind ?? IPAddress.Loopback, port);
        _channel.FrameReceived += OnFrameReceived;
        _channel.FrameReceivedWithQuality += OnFrameQuality;
    }

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
                _ = ServeClientAsync(id, client);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ServeClientAsync(Guid id, TcpClient client)
    {
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
        catch (Exception)
        {
            // Client errors only ever cost that client its connection.
        }
        finally
        {
            _clients.TryRemove(id, out _);
            client.Dispose();
        }
    }

    private void OnClientFrame(TcpClient origin, KissFrame frame)
    {
        switch (frame.Command)
        {
            case KissCommand.Data:
                _ = _channel.EnqueueTransmit(frame.Port, frame.Payload);
                break;

            case KissCommand.AckModeData when frame.Payload.Length > 2:
            {
                byte[] ackId = frame.Payload[..2];
                int port = frame.Port;
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

    private void OnFrameReceived(int subChannel, byte[] frame)
    {
        byte[] encoded = KissCodec.Encode(new KissFrame(subChannel, KissCommand.Data, frame));
        foreach (TcpClient client in _clients.Values)
        {
            Send(client, encoded);
        }
    }

    private void OnFrameQuality(int subChannel, byte[] frame, Modems.FrameQuality quality)
    {
        if (!EmitQualityFrames)
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
            subChannel, KissCommand.RxQuality, System.Text.Encoding.UTF8.GetBytes(json.ToString())));
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
