using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Packet.SoundModem.Tests.Monitor;

/// <summary>
/// A private station, over a real socket, speaking the uplink wire format of
/// <c>docs/uplink-plan.md</c> 4.2 and nothing else.
/// </summary>
/// <remarks>
/// <para>Not a mock and not a fake of the monitor's own parser: it opens a real
/// <see cref="ClientWebSocket"/> at <c>/uplink</c>, presents a real bearer token, sends real
/// JSON and real binary audio, and reads what comes back. So what these tests exercise is the
/// framing, the caps and the refusals as a station will actually meet them.</para>
/// <para><b>It stands in for Phase 2's <c>UplinkClient</c>, which is being written in parallel</b>
/// and is not in this branch. When both phases have merged, the two-process smoke of the plan's
/// 6.3 criterion 2 replaces the one-process one this makes possible; until then this is what
/// there is, and it is honest about being it.</para>
/// <para>It is also deliberately able to send things a well-behaved station never would - an
/// oversized hello, an audio message of the wrong length, a flood - because those are the paths
/// that matter most on a public port.</para>
/// </remarks>
internal sealed class StubStation : IAsyncDisposable
{
    /// <summary>
    /// The station's own serialiser, which omits a null rather than writing one - so every
    /// optional field really is absent from the wire in these tests, as it is in production.
    /// </summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ClientWebSocket _socket;
    private readonly List<int> _demands = [];
    private readonly Lock _gate = new();
    private readonly Task _reading;
    private readonly CancellationTokenSource _stopping = new();

    private StubStation(ClientWebSocket socket, int audioRate, int blockSamples)
    {
        _socket = socket;
        AudioRate = audioRate;
        BlockSamples = blockSamples;
        _reading = Task.Run(ReadAsync);
    }

    /// <summary>The rate this station says it is relaying at.</summary>
    internal int AudioRate { get; }

    /// <summary>Samples in one audio message, which every one is checked against.</summary>
    internal int BlockSamples { get; }

    /// <summary>Every viewer count the monitor has sent, in order.</summary>
    internal IReadOnlyList<int> Demands
    {
        get
        {
            lock (_gate)
            {
                return [.. _demands];
            }
        }
    }

    /// <summary>The welcome, once it has arrived: the slug and the path this station is under.</summary>
    internal JsonElement? Welcome { get; private set; }

    /// <summary>Why the monitor hung up, once it has.</summary>
    internal string? ClosedBecause { get; private set; }

    /// <summary>Whether the socket is still up.</summary>
    internal bool Connected => _socket.State == WebSocketState.Open;

    /// <summary>
    /// Opens the socket with a bearer token. Throws whatever a real client would throw when the
    /// upgrade is refused, with the HTTP status readable on it.
    /// </summary>
    internal static async Task<ClientWebSocket> ConnectAsync(int port, string? token)
    {
        var socket = new ClientWebSocket();

        // The same option the UberSDR input sets, and for the same reason: without it a 401 on
        // the upgrade is indistinguishable from the far end being unreachable, and a station has
        // to be able to tell "my token is wrong" from "the site is down".
        socket.Options.CollectHttpResponseDetails = true;
        if (token is not null)
        {
            socket.Options.SetRequestHeader("Authorization", $"Bearer {token}");
        }

        try
        {
            // Bounded, so that an upgrade this site never answers fails here with its own name
            // on it rather than as a test that simply stopped.
            using var giveUp = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/uplink"), giveUp.Token);
        }
        catch (Exception refused)
        {
            // CollectHttpResponseDetails puts the status on the socket rather than on the
            // exception, and the socket is about to go, so it is carried across here. A station
            // has to be able to tell "my token is wrong" from "the site is down", and so does a
            // test asserting which of the two this was.
            refused.Data["HttpStatusCode"] = (int)socket.HttpStatusCode;
            socket.Dispose();
            throw;
        }

        return socket;
    }

    /// <summary>Opens the socket and sends a hello, as a real station does on connecting.</summary>
    internal static async Task<StubStation> OpenAsync(
        int port, string token, string callsign,
        string? op = "Tom M0LTE", string? location = "Reading, England",
        string? radio = "IC-7300 into a doublet at 10 m", string? site = null,
        int audioRate = 12000, int blockSamples = 480, double dialHz = 7049450,
        object[]? bands = null, object? extra = null)
    {
        ClientWebSocket socket = await ConnectAsync(port, token);
        var station = new StubStation(socket, audioRate, blockSamples);

        // The field names the station's own client writes, and the same omit-nulls serialiser it
        // uses: a monitor that only ever saw a test's tidy JSON would not have been tested at all.
        var hello = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "hello",
            ["protocol"] = 1,
            ["version"] = "0.55.2",
            ["callsign"] = callsign,
            ["operator"] = op,
            ["location"] = location,
            ["radio"] = radio,
            ["site"] = site,
            ["audioRate"] = audioRate,
            ["blockSamples"] = blockSamples,
            ["dialHz"] = dialHz,
            ["sideband"] = "usb",
            ["frames"] = "always",
            ["modems"] = bands ?? [new
            {
                sub = 0, mode = "afsk300-il2pc", lowHz = 700.0, highHz = 1000.0, centreHz = 850.0,
            }],
        };

        // Anything else a test wants on the one hello that is actually read: a field this site
        // has never heard of, or one it has to be shown ignoring.
        foreach (System.Reflection.PropertyInfo more in extra?.GetType().GetProperties() ?? [])
        {
            hello[more.Name] = more.GetValue(extra);
        }

        await station.SendAsync(hello);
        return station;
    }

    /// <summary>Sends one message as this station would, whatever shape the caller wants.</summary>
    internal Task SendAsync(object message) =>
        SendTextAsync(JsonSerializer.Serialize(message, Json));

    internal async Task SendTextAsync(string text) =>
        await _socket.SendAsync(
            Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true,
            CancellationToken.None);

    /// <summary>
    /// One audio message: <c>[0x02][kind][2 bytes pad][s16 LE mono]</c>, exactly the length the
    /// hello declared.
    /// </summary>
    internal Task SendAudioAsync(bool transmitted, short[]? pcm = null) =>
        SendBinaryAsync(AudioMessage(transmitted, pcm ?? Tone(BlockSamples)));

    internal async Task SendBinaryAsync(byte[] payload) =>
        await _socket.SendAsync(
            payload, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);

    /// <summary>Blocks of audio back to back, as a station streaming does.</summary>
    internal async Task SendSecondsAsync(double seconds, bool transmitted = false)
    {
        int blocks = (int)Math.Round(seconds * AudioRate / BlockSamples);
        for (int i = 0; i < blocks; i++)
        {
            await SendAudioAsync(transmitted);
        }
    }

    /// <summary>The wire bytes of one audio message, for a test that wants to bend them.</summary>
    internal byte[] AudioMessage(bool transmitted, short[] pcm)
    {
        byte[] message = new byte[4 + (2 * pcm.Length)];
        message[0] = 0x02;
        message[1] = transmitted ? (byte)1 : (byte)0;
        for (int i = 0; i < pcm.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(message.AsSpan(4 + (2 * i), 2), pcm[i]);
        }

        return message;
    }

    /// <summary>A frame, in the shape 4.2 carries one.</summary>
    internal Task SendFrameAsync(
        byte[]? raw, string mode = "afsk300-il2pc", int sub = 0, string? from = "M0LTE",
        string? to = "GB7RDG-2", bool transmitted = false, DateTimeOffset? at = null) =>
        SendAsync(new
        {
            type = "frame",
            sub,
            mode,
            from,
            to,
            lenBytes = raw?.Length ?? 0,
            snrDb = 12.5,
            burstLines = 8,
            offsetHz = -3.2,
            corrected = 0,
            crc = true,
            id = (bool?)null,
            tx = transmitted ? true : (bool?)null,
            plain = (bool?)null,
            monitorOnly = (bool?)null,
            at = (at ?? DateTimeOffset.UtcNow).ToString("O"),
            raw = raw is null ? null : Convert.ToBase64String(raw),
        });

    /// <summary>A tone, so that a block of audio is something a waterfall can draw.</summary>
    internal short[] Tone(int samples, double hz = 850, double amplitude = 0.3)
    {
        short[] pcm = new short[samples];
        for (int i = 0; i < samples; i++)
        {
            pcm[i] = (short)(amplitude * 32767
                * Math.Sin(2 * Math.PI * hz * (_phase + i) / AudioRate));
        }

        _phase += samples;
        return pcm;
    }

    private long _phase;

    private async Task ReadAsync()
    {
        byte[] buffer = new byte[64 * 1024];
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                WebSocketReceiveResult result =
                    await _socket.ReceiveAsync(buffer, _stopping.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    ClosedBecause = _socket.CloseStatusDescription ?? "";
                    return;
                }

                using JsonDocument message =
                    JsonDocument.Parse(buffer.AsMemory(0, result.Count));
                switch (message.RootElement.GetProperty("type").GetString())
                {
                    case "welcome":
                        Welcome = message.RootElement.Clone();
                        break;

                    case "demand":
                        lock (_gate)
                        {
                            _demands.Add(message.RootElement.GetProperty("viewers").GetInt32());
                        }

                        break;
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException
                                      or ObjectDisposedException or JsonException)
        {
            ClosedBecause ??= e.Message;
        }
    }

    /// <summary>Waits on the condition rather than sleeping through a guess.</summary>
    internal static async Task UntilAsync(Func<bool> condition, string what)
    {
        using var giveUp = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition())
        {
            if (giveUp.IsCancellationRequested)
            {
                throw new TimeoutException($"waited 20 s for {what} and it did not happen");
            }

            await Task.Delay(20, CancellationToken.None);
        }
    }

    /// <summary>Waits for the welcome, which is what says the monitor accepted this station.</summary>
    internal Task WelcomedAsync() => UntilAsync(() => Welcome is not null, "a welcome");

    /// <summary>Waits for the monitor to hang up.</summary>
    internal Task ClosedAsync() =>
        UntilAsync(() => ClosedBecause is not null || !Connected, "the monitor to hang up");

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        try
        {
            await _reading.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            // A reader that will not come down tidily is not what any of these tests is about.
        }

        _socket.Dispose();
        _stopping.Dispose();
    }
}
