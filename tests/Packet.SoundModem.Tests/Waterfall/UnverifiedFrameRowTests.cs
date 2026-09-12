using System.Net.WebSockets;
using System.Text.Json;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Tests.Modems;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Tests.Waterfall;

/// <summary>
/// What a row says about a frame nothing checked. The reported defect, end to end: frames tagged
/// GB7BPQ on the waterfall at a believable SNR, with no trace of a signal under them.
/// </summary>
/// <remarks>
/// <para>
/// Three things lined up to make one of those rows, and all three are here. A frame can be
/// accepted on Reed-Solomon alone when an IL2P+CRC link falls back to reading plain IL2P; chase
/// decoding flips the least-confident bits until the parity closes, so on that path nothing checks
/// the result; and the callsign is then read off a header whose two-symbol parity makes
/// mis-correction cheap, which walks a real regular a character or two off itself and sometimes
/// walks it all the way back to the correct spelling. The band SNR beside it is not a measurement
/// of the frame at all, and for two seconds after a burst it repeats the previous burst's figure
/// verbatim, which is why a fabricated row read at the same strength as the real one before it.
/// </para>
/// <para>
/// Measured on the station's own log over eight days of its 40 m slot: 75.6% of that class were
/// payloads seen exactly once on a band where everything repeats all day, against 3.5% of
/// CRC-verified frames. docs/dev/false-decodes.md.
/// </para>
/// </remarks>
public class UnverifiedFrameRowTests
{
    private const int SampleRate = 12000;

    /// <summary>A frame from GB7BPQ to GB7RDG-2, as that slot's real traffic is shaped.</summary>
    private static byte[] Frame() =>
        Convert.FromHexString("8E846EA4888E648E846E84A0A2E103F0" + "48454C4C4F");

    private static CancellationTokenSource Budget() => new(TimeSpan.FromSeconds(30));

    /// <summary>
    /// The rule itself, over the whole path: a reading nothing checked, reached by moving bits,
    /// names nobody on the page.
    /// </summary>
    /// <remarks>
    /// The frame is listed, badged and sent with its bytes - an operator can still see it, weigh
    /// it and copy it - and the row simply does not claim a station. The note says which of the
    /// two reasons it is, because "unattributed" over an address field that plainly is one sends
    /// the next person looking for a parser bug.
    /// </remarks>
    [Fact]
    public async Task A_Chased_Reed_Solomon_Only_Frame_Names_Nobody()
    {
        JsonElement row = await RowForAsync(new FrameQuality(
            "bpsk300-il2pc", Frame().Length, CorrectedBytes: 3, CrcValid: null,
            PlainIl2p: true, MonitorOnly: true, ChasedBits: 6));

        row.GetProperty("from").ValueKind.Should().Be(JsonValueKind.Null,
            "a callsign on a row is a claim, and nothing here can support one");
        row.GetProperty("to").ValueKind.Should().Be(JsonValueKind.Null);
        row.GetProperty("plain").GetBoolean().Should().BeTrue(
            "the badge stays: the operator is still shown the frame and what it stood on");
        row.GetProperty("monitorOnly").GetBoolean().Should().BeTrue();
        row.GetProperty("lenBytes").GetInt32().Should().Be(Frame().Length);
        row.GetProperty("why").GetString().Should().Contain("withheld");
        row.GetProperty("hex").GetString().Should().Be(Convert.ToHexString(Frame()),
            "the bytes come too, because this is where somebody notices one of these");
    }

    /// <summary>
    /// And the same bytes with a CRC behind them are untouched, chase and all.
    /// </summary>
    [Fact]
    public async Task A_Verified_Frame_Is_Unaffected_Even_When_The_Chase_Rescued_It()
    {
        JsonElement row = await RowForAsync(new FrameQuality(
            "bpsk300-il2pc", Frame().Length, CorrectedBytes: 3, CrcValid: true, ChasedBits: 6));

        row.GetProperty("from").GetString().Should().Be("GB7BPQ");
        row.GetProperty("to").GetString().Should().Be("GB7RDG-2");
        row.GetProperty("crc").GetBoolean().Should().BeTrue();
        row.GetProperty("why").ValueKind.Should().Be(JsonValueKind.Null,
            "there is nothing to explain about a frame something checked");
        row.GetProperty("hex").ValueKind.Should().Be(JsonValueKind.Null);
    }

    /// <summary>
    /// A Reed-Solomon-only reading that the chase did not touch keeps its callsign.
    /// </summary>
    /// <remarks>
    /// This is the class the plain reading exists for: the BPQ32 neighbour on this very slot
    /// transmits plain IL2P with no trailing CRC and was structurally invisible without it
    /// (Il2pReceiver). 90% of that class on the live slot were payloads heard again, so the name
    /// stands.
    /// </remarks>
    [Fact]
    public async Task A_Reed_Solomon_Only_Frame_The_Chase_Did_Not_Touch_Keeps_Its_Callsign()
    {
        JsonElement row = await RowForAsync(new FrameQuality(
            "bpsk300-il2pc", Frame().Length, CorrectedBytes: 1, CrcValid: null,
            PlainIl2p: true, MonitorOnly: true));

        row.GetProperty("from").GetString().Should().Be("GB7BPQ");
        row.GetProperty("plain").GetBoolean().Should().BeTrue();
    }

    /// <summary>
    /// The header that did not survive: a real off-air frame whose destination half came back as
    /// <c>GB&amp;)&gt;W</c> and whose source half still spelled GB7BPQ.
    /// </summary>
    /// <remarks>
    /// frames.db id=125786 on GB7RDG's slot 3, 2026-09-12, six bits chased. The row used to go up
    /// as GB7BPQ with the destination quietly dropped. Given a CRC-valid quality here on purpose:
    /// what unattributes it is the address field, not the decode, so the two rules are shown to be
    /// independent.
    /// </remarks>
    [Fact]
    public async Task A_Frame_Whose_Destination_Did_Not_Survive_Names_Nobody_Either()
    {
        byte[] corrupt = Convert.FromHexString("8E844C527CAE648E846E84A0A2E103F048454C4C4F");
        JsonElement row = await RowForAsync(
            new FrameQuality("bpsk300-il2pc", corrupt.Length, CorrectedBytes: 4, CrcValid: true),
            corrupt);

        row.GetProperty("from").ValueKind.Should().Be(JsonValueKind.Null,
            "half the address field is provably wrong, so the other half is not evidence");
        row.GetProperty("why").GetString().Should().Contain("destination");
    }

    /// <summary>
    /// The band figure, over real audio: a frame nothing checked carries no SNR on its row, and
    /// the same station's verified frame carries one.
    /// </summary>
    /// <remarks>
    /// <para>Real bursts through a real <c>bpsk300</c> bank, because the point is the figure the
    /// band tracker actually produced rather than one handed in. Two seconds of noise first, so
    /// the tracker has a floor to measure against - the same set-up <c>BurstSnrTests</c> uses.</para>
    /// <para>The measurement is not touched. It is still on the quality, still in the frame log
    /// and still on the uplink; this is only what a row is sent.</para>
    /// </remarks>
    [Fact]
    public async Task A_Band_Snr_Is_Drawn_Beside_A_Checked_Frame_And_Not_Beside_An_Unchecked_One()
    {
        (JsonElement verified, FrameQuality verifiedQuality) = await HeardAsync("bpsk300");
        verifiedQuality.SnrDb.Should().NotBeNull("the burst was loud over a banked floor");
        verified.GetProperty("snrDb").GetDouble().Should().Be(verifiedQuality.SnrDb!.Value,
            "a checked frame's row shows the figure the channel measured, unrounded and unchanged");

        (JsonElement plain, FrameQuality plainQuality) = await HeardAsync("bpsk300-nocrc");
        plainQuality.PlainIl2p.Should().BeTrue("an IL2P+CRC link read this one the plain way");
        plainQuality.TrailerNearBits.Should().BeNull("and no trailer corroborated it");
        plainQuality.SnrDb.Should().NotBeNull(
            "the station still measured the band, and still logs and relays what it measured");
        plain.GetProperty("snrDb").ValueKind.Should().Be(JsonValueKind.Null,
            "beside a frame nothing checked, a band reading with a 6 dB floor and a two-second "
                + "carry-over is what makes the row look like a real signal");
    }

    /// <summary>
    /// And the same again after a restart: the backlog a browser opens on withholds what the live
    /// row withheld.
    /// </summary>
    /// <remarks>
    /// <para>The station's log keeps the callsign, because a log is a record of what was read and
    /// the measurement that found this defect was made from exactly these columns. What the
    /// backlog does with it is a display question, and it has to answer it the same way the live
    /// row did - otherwise the fix is undone by a page refresh, which is how an operator would
    /// have met it.</para>
    /// <para>A real SQLite file, written and read back, so what is pinned is the whole path
    /// including the two columns the verdict is read off.</para>
    /// </remarks>
    [Fact]
    public async Task A_Replayed_Row_Withholds_What_The_Live_Row_Withheld()
    {
        string directory = Directory.CreateTempSubdirectory("pdnsm-unverified").FullName;
        string path = Path.Combine(directory, "frames.db");
        try
        {
            var chased = new FrameQuality(
                "bpsk300-il2pc", Frame().Length, CorrectedBytes: 3, CrcValid: null,
                PlainIl2p: true, MonitorOnly: true, ChasedBits: 6);
            var verified = new FrameQuality(
                "bpsk300-il2pc", Frame().Length, CorrectedBytes: 3, CrcValid: true, ChasedBits: 6);

            await using (FrameLog writing = FrameLog.Open(path))
            {
                writing.Record(0, Frame(), verified, audioHz: 2150, rfHz: 7_051_600);
                writing.Record(0, Frame(), chased, audioHz: 2150, rfHz: 7_051_600);

                // The log's writer is a thread of its own, so this waits for the rows rather than
                // for an interval: nothing here decides anything by the clock, and the loop ends
                // the moment the condition it is about is true.
                for (int i = 0; i < 100 && writing.Recent(10).Count < 2; i++)
                {
                    await Task.Delay(20);
                }

                IReadOnlyList<LoggedFrame> logged = writing.Recent(10);
                logged.Should().HaveCount(2);
                logged.Should().AllSatisfy(row => row.From.Should().Be("GB7BPQ",
                    "the log is a record of what was read, and keeps both callsigns"));
                logged[1].ChasedBits.Should().Be(6, "which is what the verdict is read off");
                logged[1].CallsignWorthShowing.Should().BeFalse();
                logged[0].CallsignWorthShowing.Should().BeTrue();
            }

            await using FrameLog restarted = FrameLog.Open(path);
            int port = FreePorts.Next();
            await using var server = new WaterfallWebServer(
                new SoundModemChannel(SampleRate, randomSeed: 7), port,
                new WaterfallOptions { FrameHistory = restarted.Recent });
            server.Start();

            using var socket = new ClientWebSocket();
            using (CancellationTokenSource connecting = Budget())
            {
                await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), connecting.Token);
            }

            await Receive(socket);   // config
            (_, byte[] payload) = await Receive(socket);
            using JsonDocument history = JsonDocument.Parse(payload);
            history.RootElement.GetProperty("type").GetString().Should().Be("history");
            JsonElement rows = history.RootElement.GetProperty("frames");

            rows[0].GetProperty("from").GetString().Should().Be("GB7BPQ",
                "the verified row is replayed exactly as it was listed");
            rows[1].GetProperty("from").ValueKind.Should().Be(JsonValueKind.Null,
                "and a page refresh does not hand back the callsign the live row withheld");
            rows[1].GetProperty("plain").GetBoolean().Should().BeTrue(
                "the badge is replayed either way");
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>The row a browser is sent for one frame of <paramref name="quality"/>.</summary>
    private static async Task<JsonElement> RowForAsync(FrameQuality quality, byte[]? bytes = null)
    {
        byte[] frame = bytes ?? Frame();
        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        var modem = new ScriptedModem(quality.Mode);
        channel.AddModem(0, _ => modem);
        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(channel, port);
        server.Start();

        using var socket = new ClientWebSocket();
        using (CancellationTokenSource connecting = Budget())
        {
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), connecting.Token);
        }

        await Receive(socket);   // config

        Task<JsonDocument> reader = Task.Run(() => ReadUntilFrameAsync(socket));
        modem.Emit = (frame, quality);
        channel.ProcessReceive(new float[SampleRate / 10]);

        using JsonDocument message = await reader;
        return message.RootElement.Clone();
    }

    /// <summary>
    /// One real decode of a <paramref name="mode"/> burst by a <c>bpsk300</c> bank: the row a
    /// browser is sent, and the quality the channel measured for the same frame.
    /// </summary>
    private static async Task<(JsonElement Row, FrameQuality Quality)> HeardAsync(string mode)
    {
        var channel = new SoundModemChannel(SampleRate, randomSeed: 5);
        channel.AddModem(0, sink => ModemCatalog.Create("bpsk300", SampleRate, sink));
        var qualities = new List<FrameQuality>();
        channel.FrameReceivedWithQuality += (_, _, quality) => qualities.Add(quality);
        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(channel, port);
        server.Start();

        using var socket = new ClientWebSocket();
        using (CancellationTokenSource connecting = Budget())
        {
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), connecting.Token);
        }

        await Receive(socket);   // config

        Task<JsonDocument> reader = Task.Run(() => ReadUntilFrameAsync(socket));

        // Two seconds of low-level noise to bank a floor, then the burst, then the flush - the
        // shape BurstSnrTests established, because a burst that opens the stream cold warms the
        // tracker's floor from its own level and honestly reports no figure at all.
        await FeedAsync(channel, Noise(SampleRate * 2));
        await FeedAsync(channel, ModemCatalog.Create(mode, SampleRate, static _ => { })
            .Modulate(Il2pReceiverTests.Gb7bpqBeacon(), txDelayMilliseconds: 300));
        await FeedAsync(channel, Noise(SampleRate / 2));

        using JsonDocument message = await reader;
        qualities.Should().NotBeEmpty("the burst decodes");
        return (message.RootElement.Clone(), qualities[0]);

        static async Task FeedAsync(SoundModemChannel channel, float[] audio)
        {
            // In tenths of a second, so the server's per-client queue drains as it fills: it is
            // bounded at about a second of messages and drops the oldest, so pushing four seconds
            // in one call evicts the frame message this test is waiting for. Yielded rather than
            // slept: a pool work item that blocks is the pattern that lost the first cut of
            // v0.50.0, and with the whole suite in parallel there are better things for that
            // thread to be doing than waiting a millisecond.
            for (int at = 0; at < audio.Length; at += SampleRate / 10)
            {
                channel.ProcessReceive(
                    audio.AsSpan(at, Math.Min(SampleRate / 10, audio.Length - at)));
                await Task.Delay(1);
            }
        }
    }

    /// <summary>Low-level noise, so the band tracker has a genuine floor to bank.</summary>
    private static float[] Noise(int samples)
    {
        var random = new Random(9);
        var noise = new float[samples];
        for (int n = 0; n < samples; n++)
        {
            noise[n] = (float)((random.NextDouble() * 2 - 1) * 0.001);
        }

        return noise;
    }

    private static async Task<JsonDocument> ReadUntilFrameAsync(ClientWebSocket socket)
    {
        while (true)
        {
            (WebSocketMessageType kind, byte[] payload) = await Receive(socket);
            if (kind != WebSocketMessageType.Text)
            {
                continue;
            }

            var message = JsonDocument.Parse(payload);
            if (message.RootElement.GetProperty("type").GetString() == "frame")
            {
                return message;
            }

            message.Dispose();
        }
    }

    private static async Task<(WebSocketMessageType Kind, byte[] Payload)> Receive(ClientWebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        var got = new List<byte>();
        WebSocketReceiveResult result;
        do
        {
            using CancellationTokenSource reading = Budget();
            result = await socket.ReceiveAsync(buffer, reading.Token);
            got.AddRange(buffer.AsSpan(0, result.Count).ToArray());
        }
        while (!result.EndOfMessage);

        return (result.MessageType, [.. got]);
    }

    /// <summary>
    /// A modem that decodes whatever it is handed, so that a reading which only a marginal
    /// channel produces can be put in front of the display.
    /// </summary>
    /// <remarks>
    /// Chase decoding fires on the receiver's least-confident bits, which means synthesising a
    /// genuinely chased frame takes a channel poor enough to damage it and lucky enough for
    /// Reed-Solomon to close over the damage. That is a modem test, and it is not what these
    /// assertions are about: what is under test here is what the display does with a reading,
    /// given one.
    /// </remarks>
    private sealed class ScriptedModem(string mode) : IModem
    {
        public string Mode => mode;

        public event Action<byte[], FrameQuality>? FrameDecoded;

        public bool CarrierDetect => false;

        public bool ChannelBusy => false;

        /// <summary>What to decode on the next block, taken as it is used.</summary>
        public (byte[] Frame, FrameQuality Quality)? Emit { get; set; }

        public void Process(ReadOnlySpan<float> samples)
        {
            if (Emit is not { } decode)
            {
                return;
            }

            Emit = null;
            FrameDecoded?.Invoke(decode.Frame, decode.Quality);
        }

        public float[] Modulate(ReadOnlySpan<byte> ax25Frame, int txDelayMilliseconds) => new float[16];

        public void ResetCarrierState()
        {
        }
    }
}
