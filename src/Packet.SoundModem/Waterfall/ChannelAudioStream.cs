using System.Buffers.Binary;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using Packet.SoundModem.Channel;

namespace Packet.SoundModem.Waterfall;

/// <summary>
/// The local channel audio stream (issue #584): a WebSocket on the station page server that
/// hands a program on the same machine the channel's receive audio, nothing else. One receive
/// tap fans out to every connection, each with its own small bounded queue; a slow reader loses
/// blocks, marked with a gap, rather than slowing the modem or any other reader.
/// </summary>
/// <remarks>
/// <para><b>Loopback only, no key.</b> The server checks the remote address and the
/// <c>Origin</c> header before accepting the upgrade (see <see cref="IsLoopbackAddress"/> and the
/// caller in <see cref="WaterfallWebServer"/>); this class assumes that has already happened and
/// serves anything handed to it.</para>
/// <para><b>Keyed blocks.</b> <see cref="SoundModemChannel.ProcessReceive"/> skips every receive
/// tap while the channel is transmitting (half duplex), so an ordinary tap simply sees nothing
/// for the length of a keyup. <see cref="OnKeyedBlock"/> is wired to
/// <see cref="SoundModemChannel.KeyedReceiveBlock"/> instead, which fires from exactly that gate
/// with the length of the block that was skipped - a silent block of the same length, marked
/// transmitted, takes its place, so a reader's sample clock never stops and it can tell a keyup
/// from a gap.</para>
/// <para><b>The requested band.</b> A connection may name a band it needs to hear
/// (<see cref="Client.Band"/>, set from the identify message); <see cref="BandRequested"/> fires
/// with the union of every connected band whenever that union changes, null when none is asked
/// for. What that is used for - widening a headless Flex's slice filter - is the caller's, not
/// this class's: it knows nothing about Flex.</para>
/// </remarks>
internal sealed class ChannelAudioStream
{
    /// <summary>The path this stream is served under, under whatever base the station page is.</summary>
    internal const string Path = "/channel-audio";

    private const byte AudioKind = 0x01;
    private const byte FlagTransmitted = 0x01;
    private const byte FlagGap = 0x02;
    private const int HeaderBytes = 16;

    /// <summary>
    /// Blocks held per connection before the oldest is dropped. Generous for a reader on the same
    /// machine: audio typically arrives in blocks well under 100 ms, so this is several seconds
    /// of headroom, not a tight budget.
    /// </summary>
    private const int QueueCapacity = 64;

    private readonly int _sampleRate;
    private readonly Action<string>? _log;
    private readonly object _clientsLock = new();
    private volatile Client[] _clients = [];
    private ulong _sampleCounter;
    private (int LowHz, int HighHz)? _lastReportedBand;

    public ChannelAudioStream(int sampleRate, Action<string>? log)
    {
        _sampleRate = sampleRate;
        _log = log;
    }

    /// <summary>
    /// Fired with the union of every connected client's requested band (see the identify
    /// message, <see cref="ApplyIdentify"/>) whenever it changes; null when no connected client
    /// has asked for one. Never fired at all for a station nobody has asked for a band on.
    /// </summary>
    public Action<(int LowHz, int HighHz)?>? BandRequested { get; set; }

    /// <summary>How many clients are connected right now, for the journal and for tests.</summary>
    internal int ClientCount => _clients.Length;

    /// <summary>
    /// True for a loopback address (<c>127.0.0.1</c>, <c>::1</c>, and anything else
    /// <see cref="IPAddress.IsLoopback"/> calls loopback); false for null (no remote address at
    /// all, which is refused rather than trusted) or anything else.
    /// </summary>
    public static bool IsLoopbackAddress(IPAddress? address) => address is not null && IPAddress.IsLoopback(address);

    /// <summary>The channel's own receive tap: every block of audio it hears, in order.</summary>
    public void OnReceive(ReadOnlySpan<float> samples)
    {
        Client[] clients = _clients;
        ulong index = _sampleCounter;
        _sampleCounter += (ulong)samples.Length;
        if (clients.Length == 0)
        {
            // No client to tell, and nothing to copy for one - the point of checking first.
            return;
        }

        float[] copy = samples.ToArray();
        foreach (Client client in clients)
        {
            client.Queue.Enqueue(index, copy, transmitted: false);
        }
    }

    /// <summary>
    /// The channel's keyed-block callback: a block of this many samples was skipped because the
    /// station was transmitting. Fed to every client as silence, marked transmitted, so its
    /// sample clock keeps pace with the channel's own.
    /// </summary>
    public void OnKeyedBlock(int length)
    {
        Client[] clients = _clients;
        ulong index = _sampleCounter;
        _sampleCounter += (ulong)length;
        if (clients.Length == 0 || length <= 0)
        {
            return;
        }

        float[] silence = new float[length];
        foreach (Client client in clients)
        {
            client.Queue.Enqueue(index, silence, transmitted: true);
        }
    }

    /// <summary>Serves one already-accepted, already-checked WebSocket until it closes.</summary>
    /// <param name="socket">The accepted socket.</param>
    /// <param name="remoteDescription">For the journal: who connected, e.g. an address and port.</param>
    /// <param name="serverStopping">Cancelled when the whole server is going down.</param>
    public async Task ServeAsync(WebSocket socket, string remoteDescription, CancellationToken serverStopping)
    {
        var queue = new AudioQueue(QueueCapacity);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(serverStopping);
        var client = new Client(socket, queue);

        AddClient(client);
        Journal($"channel-audio: {remoteDescription} connected - {ClientCount} client{Plural(ClientCount)}");

        Task send = SendLoopAsync(client, stop.Token);
        try
        {
            byte[] hello = JsonSerializer.SerializeToUtf8Bytes(new { type = "hello", rateHz = _sampleRate });
            await socket.SendAsync(hello, WebSocketMessageType.Text, true, stop.Token).ConfigureAwait(false);

            var buffer = new byte[4096];
            while (socket.State == WebSocketState.Open && !stop.IsCancellationRequested)
            {
                WebSocketReceiveResult received =
                    await socket.ReceiveAsync(buffer, stop.Token).ConfigureAwait(false);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (received.MessageType == WebSocketMessageType.Text && received.Count > 0)
                {
                    ApplyIdentify(client, buffer.AsSpan(0, received.Count));
                }
            }
        }
        catch (Exception)
        {
            // A vanished client is normal shutdown for its connection, nothing more.
        }
        finally
        {
            queue.Complete();
            await stop.CancelAsync().ConfigureAwait(false);
            try
            {
                await send.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            RemoveClient(client);
            RecomputeBand();
            Journal($"channel-audio: {remoteDescription} disconnected - {ClientCount} client{Plural(ClientCount)} left");

            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
            }

            socket.Dispose();
        }
    }

    /// <summary>Stops every connection and drops them, for the server's own shutdown.</summary>
    public void Shutdown()
    {
        Client[] clients;
        lock (_clientsLock)
        {
            clients = _clients;
            _clients = [];
        }

        foreach (Client client in clients)
        {
            client.Queue.Complete();
        }
    }

    private void AddClient(Client client)
    {
        lock (_clientsLock)
        {
            _clients = [.. _clients, client];
        }
    }

    private void RemoveClient(Client client)
    {
        lock (_clientsLock)
        {
            _clients = Array.FindAll(_clients, c => !ReferenceEquals(c, client));
        }
    }

    private void ApplyIdentify(Client client, ReadOnlySpan<byte> payload)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(payload.ToArray());
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("name", out JsonElement nameElement)
                && nameElement.ValueKind == JsonValueKind.String)
            {
                client.Name = nameElement.GetString();
            }

            if (root.TryGetProperty("pagePort", out JsonElement portElement)
                && portElement.ValueKind == JsonValueKind.Number
                && portElement.TryGetInt32(out int pagePort))
            {
                client.PagePort = pagePort;
            }

            if (root.TryGetProperty("band", out JsonElement bandElement))
            {
                if (bandElement.ValueKind == JsonValueKind.Null)
                {
                    client.Band = null;
                }
                else if (bandElement.ValueKind == JsonValueKind.Object
                    && bandElement.TryGetProperty("lowHz", out JsonElement lowElement)
                    && lowElement.TryGetInt32(out int lowHz)
                    && bandElement.TryGetProperty("highHz", out JsonElement highElement)
                    && highElement.TryGetInt32(out int highHz)
                    && lowHz < highHz)
                {
                    client.Band = (lowHz, highHz);
                }

                RecomputeBand();
            }
        }
        catch (JsonException)
        {
            // A malformed identify message changes nothing: the audio keeps flowing regardless,
            // the same as a browser that sent us an odd message on the page's own socket would.
        }
    }

    private void RecomputeBand()
    {
        (int LowHz, int HighHz)? union = null;
        foreach (Client client in _clients)
        {
            if (client.Band is (int lowHz, int highHz))
            {
                union = union is (int unionLow, int unionHigh)
                    ? (Math.Min(unionLow, lowHz), Math.Max(unionHigh, highHz))
                    : (lowHz, highHz);
            }
        }

        if (!Nullable.Equals(union, _lastReportedBand))
        {
            _lastReportedBand = union;
            BandRequested?.Invoke(union);
        }
    }

    private void Journal(string line)
    {
        try
        {
            _log?.Invoke(line);
        }
        catch (Exception)
        {
            // A journal that will not take a line is not this connection's problem.
        }
    }

    private static string Plural(int count) => count == 1 ? "" : "s";

    private static async Task SendLoopAsync(Client client, CancellationToken cancellation)
    {
        try
        {
            while (true)
            {
                QueuedBlock? block = await client.Queue.DequeueAsync(cancellation).ConfigureAwait(false);
                if (block is null)
                {
                    return;
                }

                byte[] message = Encode(block.Value);
                await client.Socket.SendAsync(message, WebSocketMessageType.Binary, true, cancellation)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// <c>[kind 1][flags 1][reserved 2][sampleIndex u64 LE][count i32 LE][samples f32 LE ...]</c>.
    /// Flags: bit 0 transmitted (a silent block generated for a keyup), bit 1 gap (audio was lost
    /// before this block - a slow reader's own queue dropped something, not the channel).
    /// </summary>
    private static byte[] Encode(QueuedBlock block)
    {
        int count = block.Samples.Length;
        byte[] message = new byte[HeaderBytes + (count * 4)];
        message[0] = AudioKind;
        byte flags = 0;
        if (block.Transmitted)
        {
            flags |= FlagTransmitted;
        }

        if (block.Gap)
        {
            flags |= FlagGap;
        }

        message[1] = flags;
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(4, 8), block.SampleIndex);
        BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(12, 4), count);
        ReadOnlySpan<float> samples = block.Samples.Span;
        for (int i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(message.AsSpan(HeaderBytes + (i * 4), 4), samples[i]);
        }

        return message;
    }

    private sealed class Client(WebSocket socket, AudioQueue queue)
    {
        public WebSocket Socket { get; } = socket;

        public AudioQueue Queue { get; } = queue;

        public string? Name { get; set; }

        public int? PagePort { get; set; }

        public (int LowHz, int HighHz)? Band { get; set; }
    }

    internal readonly record struct QueuedBlock(ulong SampleIndex, ReadOnlyMemory<float> Samples, bool Transmitted, bool Gap);

    /// <summary>
    /// A fixed-capacity queue of audio blocks for one connection: the oldest block is dropped to
    /// make room for a new one, and whichever block is actually handed to the reader next
    /// carries the gap flag - so a reader finds out audio was lost exactly once, on the first
    /// block it sees after the hole, rather than having to notice a jump in the sample counter
    /// for itself. The flag is decided when a block leaves the queue, not when it enters it: a
    /// drop can be followed by several more blocks arriving before the reader catches up, and it
    /// is whichever of those the reader actually sees first that must carry the mark, not
    /// whichever one happened to be the drop's own replacement.
    /// </summary>
    internal sealed class AudioQueue(int capacity)
    {
        private readonly object _gate = new();
        private readonly Queue<QueuedBlock> _items = new();
        private readonly SemaphoreSlim _signal = new(0);
        private bool _gapPending;
        private bool _completed;

        public void Enqueue(ulong sampleIndex, ReadOnlyMemory<float> samples, bool transmitted)
        {
            lock (_gate)
            {
                if (_completed)
                {
                    return;
                }

                if (_items.Count >= capacity)
                {
                    _items.Dequeue();
                    _gapPending = true;
                }

                _items.Enqueue(new QueuedBlock(sampleIndex, samples, transmitted, Gap: false));
            }

            _signal.Release();
        }

        public async ValueTask<QueuedBlock?> DequeueAsync(CancellationToken cancellation)
        {
            while (true)
            {
                await _signal.WaitAsync(cancellation).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_items.Count > 0)
                    {
                        QueuedBlock block = _items.Dequeue();
                        if (_gapPending)
                        {
                            block = block with { Gap = true };
                            _gapPending = false;
                        }

                        return block;
                    }

                    if (_completed)
                    {
                        return null;
                    }
                }
            }
        }

        public void Complete()
        {
            lock (_gate)
            {
                _completed = true;
            }

            _signal.Release();
        }
    }
}
