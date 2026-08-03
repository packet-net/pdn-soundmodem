using System.Diagnostics;
using System.Net.WebSockets;
using M0LTE.Radio.Audio;
using AwesomeAssertions;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Tests.Waterfall;

/// <summary>
/// How our own transmissions are paced onto the waterfall.
/// </summary>
/// <remarks>
/// <para>The transmit path hands the waterfall a whole keyup in one call: the modulator produces
/// the burst as a single array long before the sound card has played a sample of it. Painted on
/// arrival, a two-second burst became sixty lines inside a few milliseconds followed by two
/// seconds of nothing — receive processing is gated off while transmitting, so there is no other
/// line source during a keyup. That is what "juddery, hangs at the start of TX, renders large
/// chunks at a time" looks like from the browser.</para>
/// <para>Receive audio has no such problem: it arrives from the sound card in real time. So the
/// transmit side is queued and released at the rate real time passes, which is the rate the audio
/// is actually leaving the radio.</para>
/// </remarks>
public class WaterfallTransmitPacingTests : IAsyncLifetime
{
    private const int SampleRate = 12000;
    private const int LinesPerSecond = 30;

    private const double CentreHz = 850;   // the 300-baud slot this station actually runs

    private readonly SoundModemChannel _channel = new(SampleRate, randomSeed: 7);
    private readonly WaterfallWebServer _server;
    private readonly int _port;
    private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromSeconds(60));

    public WaterfallTransmitPacingTests()
    {
        _channel.AddModem(0, sink => ModemCatalog.Create("afsk300-il2pc", SampleRate, sink, new ModemOptions(CentreFrequencyHz: CentreHz)));
        _port = FreePort();
        _server = new WaterfallWebServer(
            _channel, _port, new WaterfallOptions { LinesPerSecond = LinesPerSecond });
    }

    public ValueTask InitializeAsync()
    {
        _server.Start();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        _cancellation.Dispose();
    }

    [Fact]
    public async Task A_Keyup_Paints_Across_Its_Own_Duration_Rather_Than_In_One_Lurch()
    {
        using ClientWebSocket socket = await ConnectAsync();

        // A frame big enough to be over a second of AFSK1200 — long enough that "all at once"
        // and "spread over the burst" cannot be confused for one another.
        Task transmitting = TransmitAsync(Payload(60));

        List<long> arrivals = await CollectTransmitLinesAsync(socket, atLeast: 25);
        await transmitting;

        arrivals.Should().HaveCountGreaterThanOrEqualTo(25);

        // The bug, as an assertion: every line landing inside a few milliseconds.
        (arrivals[^1] - arrivals[0]).Should().BeGreaterThan(
            500, "the burst must paint across its own duration, not in a single instant chunk");

        // And no one gap may swallow most of the keyup — the "hangs at the start" half of the
        // symptom, which a spread-out first-to-last alone would not catch. Measured against the
        // burst rather than the clock: a 33 ms timer sharing a machine with the rest of the suite
        // gets starved for hundreds of milliseconds at a time, and that is the test bench
        // stalling rather than the product.
        long widestGap = 0;
        for (int i = 1; i < arrivals.Count; i++)
        {
            widestGap = Math.Max(widestGap, arrivals[i] - arrivals[i - 1]);
        }

        long span = arrivals[^1] - arrivals[0];
        widestGap.Should().BeLessThan(
            span / 2, "lines must keep coming through the keyup rather than stalling in it");
    }

    [Fact]
    public async Task Lines_Arrive_At_About_The_Display_Rate_Not_Faster()
    {
        using ClientWebSocket socket = await ConnectAsync();

        Task transmitting = TransmitAsync(Payload(60));
        List<long> arrivals = await CollectTransmitLinesAsync(socket, atLeast: 30);
        await transmitting;

        // 30 lines/s, so 30 lines is about a second. Generous either side: this is asserting that
        // the display is paced to the air at all, not measuring timer accuracy.
        double perLine = (arrivals[^1] - arrivals[0]) / (double)(arrivals.Count - 1);

        perLine.Should().BeInRange(
            1000.0 / LinesPerSecond * 0.5,
            1000.0 / LinesPerSecond * 3.0,
            "a transmitted second must take about a second of waterfall");
    }

    [Fact]
    public async Task Painting_Our_Own_Burst_Never_Holds_Up_The_Transmitter()
    {
        // The pacing must sit behind the event, not in front of it. A display that made the
        // transmit loop wait a second per second of audio would wreck channel timing outright.
        using ClientWebSocket socket = await ConnectAsync();

        var clock = Stopwatch.StartNew();
        await TransmitAsync(Payload(60));
        clock.Stop();

        // FakeAudioOutput drains instantly, so this is the handover cost and nothing else.
        clock.ElapsedMilliseconds.Should().BeLessThan(
            700, "the transmitter must hand the burst over in an instant, not play it out");

        // And the display is still painting it afterwards — the point of doing it this way.
        List<long> arrivals = await CollectTransmitLinesAsync(socket, atLeast: 10);
        arrivals.Should().HaveCountGreaterThanOrEqualTo(10);
    }

    [Fact]
    public async Task Our_Own_Burst_Renders_Like_A_Strong_Station_Rather_Than_A_Fault()
    {
        // A modulator's raw output is ~35 dB hotter than anything the display ever sees on
        // receive. Drawn literally it does not look like a strong signal, it looks like a fault:
        // the transform's leakage skirt, buried in the noise for a received signal, sits far
        // above the display floor for this one and smears across the entire span. Measured on
        // this burst: 1021 of 1024 bins lit, peak pinned past the top of the scale.
        //
        // The reference is the same audio received at a strong-signal level, so this asserts the
        // design intent directly — our own transmission is drawn on the scale the operator has
        // calibrated their eye to — rather than pinning a number that only holds for one modem.
        using ClientWebSocket socket = await ConnectAsync();

        Task transmitting = TransmitAsync(Payload(60));
        byte[] transmitted = await NextLineAsync(socket, 0x03, skip: 5);
        await transmitting;

        float[] audio = Modulated(Payload(60));
        for (int i = 0; i < audio.Length; i++)
        {
            audio[i] *= 0.02f;   // a strong station, well above the noise and not overloading
        }

        // Receive audio is deliberately not drawn while our own burst is still being painted —
        // the two must not share a transform — so it is offered repeatedly until one lands.
        using var feeding = new CancellationTokenSource();
        Task feed = Task.Run(async () =>
        {
            while (!feeding.IsCancellationRequested)
            {
                _channel.ProcessReceive(audio);
                await Task.Delay(100, CancellationToken.None);
            }
        });

        byte[] received = await NextLineAsync(socket, 0x01, skip: 5);
        await feeding.CancelAsync();
        await feed;

        (int txLit, double txPeak) = Describe(transmitted);
        (int rxLit, double rxPeak) = Describe(received);

        txLit.Should().BeLessThanOrEqualTo(
            (int)(rxLit * 1.3),
            "our own transmission must not cover more of the display than a strong station does");
        txPeak.Should().BeLessThanOrEqualTo(
            Math.Min(1.0, rxPeak * 1.3),
            "nor be pinned at the top of the scale when a real signal is not");
        txPeak.Should().BeGreaterThan(
            0.4, "it still has to be clearly visible — this is a level fix, not a mute");
    }

    [Fact]
    public void A_Quiet_Stretch_Of_A_Keyup_Is_Not_Amplified_Into_A_Full_Width_Haze()
    {
        // The regression this exists to prevent. Scaling transmitted audio by normalising each
        // buffer to a target level is an automatic gain control, and an AGC exists to make quiet
        // things loud: a ramp-down, a tail, an idle stretch of a shifted burst gets multiplied by
        // an enormous gain and its noise floor fills the whole span. Measured at the time: near
        // silence at −65 dBFS rms, normalised to −40, lit 1011 of 1024 bins — the same full-width
        // haze as the original bug, reached from the opposite direction.
        var rng = new Random(1);
        var nearSilence = new float[8000];
        for (int i = 0; i < nearSilence.Length; i++)
        {
            nearSilence[i] = (float)((rng.NextDouble() - 0.5) * 2 * 1e-3);
        }

        (int lit, double peak) = RenderTransmit(nearSilence);

        lit.Should().Be(0, "quiet transmitted audio must stay quiet on the display");
        peak.Should().BeLessThan(0.15);
    }

    [Fact]
    public void A_Loud_Burst_And_A_Quiet_One_Keep_Their_Relative_Levels()
    {
        // The property that rules out an AGC of any kind: two bursts 20 dB apart must still be
        // 20 dB apart on the display. A normaliser would draw them identically.
        float[] loud = Modulated(Payload(60));
        var quiet = new float[loud.Length];
        for (int i = 0; i < loud.Length; i++)
        {
            quiet[i] = loud[i] * 0.1f;
        }

        (_, double loudPeak) = RenderTransmit(loud);
        (_, double quietPeak) = RenderTransmit(quiet);

        loudPeak.Should().BeGreaterThan(
            quietPeak + 0.1, "a quieter transmission must render as a quieter one");
    }

    [Fact]
    public async Task Band_Noise_Is_Not_Mixed_Into_The_Burst_We_Are_Still_Painting()
    {
        // Receive processing is gated during a keyup, but the paced painting outlives the keyup
        // whenever the audio device's Drain returns before the audio has actually left the radio
        // — the normal case. Receive audio then resumes while the burst is still being drawn, and
        // both feed the same transform, which has one accumulator: a single window ends up
        // holding part of a burst and part of the band noise and comes out broadband. Measured
        // before the fix: transmitted lines lighting 450-550 bins instead of ~75, with the line
        // type alternating between the two ramps. That is the full-width haze over the back half
        // of a keyup.
        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => ModemCatalog.Create("afsk300-il2pc", SampleRate, sink, new ModemOptions(CentreFrequencyHz: CentreHz)));
        await using var server = new WaterfallWebServer(
            channel, FreePort(), new WaterfallOptions { LinesPerSecond = LinesPerSecond });
        server.Start();

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Url.Split(':')[^1].TrimEnd('/')}/ws"), _cancellation.Token);
        await ReceiveAsync(socket);   // config

        // Band noise, always flowing, exactly as a sound card delivers it.
        using var noiseStop = new CancellationTokenSource();
        var rng = new Random(3);
        Task noise = Task.Run(async () =>
        {
            var block = new float[400];
            while (!noiseStop.IsCancellationRequested)
            {
                for (int i = 0; i < block.Length; i++)
                {
                    block[i] = (float)(rng.NextDouble() - 0.5) * 0.004f;
                }

                channel.ProcessReceive(block);
                await Task.Delay(33, CancellationToken.None);
            }
        });

        channel.Csma.Persistence = 255;
        channel.Csma.TxDelayMilliseconds = 200;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task transmitter = channel.RunTransmitterAsync(
            new InstantDrainOutput(SampleRate), new NullPtt(), stop.Token);
        await channel.EnqueueTransmit(0, Payload(60)).WaitAsync(TimeSpan.FromSeconds(20));

        // The defect is not a level: it is receive lines appearing among the transmitted ones,
        // because both are feeding one transform. So that is what is asserted — no bin count,
        // which would only be calibrated for whichever modem this test happened to pick.
        var types = new List<byte>();
        int transmitLines = 0;
        var clock = Stopwatch.StartNew();
        while (transmitLines < 20 && clock.ElapsedMilliseconds < 20_000)
        {
            (WebSocketMessageType kind, byte[] payload) = await ReceiveAsync(socket);
            if (kind != WebSocketMessageType.Binary || payload.Length <= 5)
            {
                continue;
            }

            if (payload[0] != 0x01 && payload[0] != 0x03)
            {
                continue;
            }

            if (payload[0] == 0x03)
            {
                transmitLines++;
            }

            types.Add(payload[0]);
        }

        await noiseStop.CancelAsync();
        await noise;
        await stop.CancelAsync();
        try
        {
            await transmitter;
        }
        catch (OperationCanceledException)
        {
        }

        transmitLines.Should().Be(20, "the burst must paint");

        // From the first transmitted line to the last, nothing else may be drawn. Measured before
        // the fix, the two alternated line for line — TX, RX, TX, RX — all the way through.
        int first = types.IndexOf(0x03);
        List<byte> duringBurst = [.. types.Skip(first)];

        duringBurst.Should().AllSatisfy(
            type => type.Should().Be(
                (byte)0x03,
                "receive audio must not be drawn into the burst we are still painting"));
    }

    [Fact]
    public async Task The_Display_Keeps_Moving_Between_Key_Down_And_The_First_Audio()
    {
        // Receive processing stops the instant the transmitter takes the channel, but the first
        // transmitted audio does not exist until the frame has been modulated and handed to the
        // device. Nothing was drawn in between, so the waterfall visibly stalled as the PTT
        // engaged. The modem here takes half a second to modulate, which is that gap made
        // deterministic; a real one is shorter but a real display still stops for it.
        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new SlowToModulate(SampleRate, TimeSpan.FromMilliseconds(500)));
        await using var server = new WaterfallWebServer(
            channel, FreePort(), new WaterfallOptions { LinesPerSecond = LinesPerSecond });
        server.Start();

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(
            new Uri($"ws://127.0.0.1:{server.Url.Split(':')[^1].TrimEnd('/')}/ws"), _cancellation.Token);
        await ReceiveAsync(socket);   // config

        // Ordering, not stopwatch time: the property is that a line is drawn in the gap BETWEEN
        // key-down and the first transmitted audio, which is exactly what this test is named
        // after. Asserting "within 250 ms of key-down" measured the same thing on an idle machine
        // and measured the machine's load on a busy one — it failed intermittently in CI for days
        // while passing every local run.
        var keyedDown = new TaskCompletionSource();
        var firstAudio = new TaskCompletionSource();
        channel.TransmittingChanged += keyed =>
        {
            if (keyed)
            {
                keyedDown.TrySetResult();
            }
        };
        channel.TransmittedAudio += _ => firstAudio.TrySetResult();

        channel.Csma.Persistence = 255;
        channel.Csma.TxDelayMilliseconds = 20;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task transmitter = channel.RunTransmitterAsync(
            new InstantDrainOutput(SampleRate), new NullPtt(), stop.Token);
        Task sending = channel.EnqueueTransmit(0, Payload(60));

        byte[] firstLine = await NextLineAsync(socket, 0x03, skip: 0);
        bool audioHadArrivedFirst = firstAudio.Task.IsCompleted;
        await keyedDown.Task;

        await sending.WaitAsync(TimeSpan.FromSeconds(20));
        await stop.CancelAsync();
        try
        {
            await transmitter;
        }
        catch (OperationCanceledException)
        {
        }

        // Drawn while the frame was still being modulated — before any transmitted audio existed
        // to draw. That is the stall this guards against, stated as an order rather than a deadline.
        audioHadArrivedFirst.Should().BeFalse(
            "the display must draw during the modulate gap, not wait for the audio to turn up");
        firstLine.Length.Should().BeGreaterThan(5, "and it must be a real line");
    }

    /// <summary>
    /// A modem that takes a long time to produce its audio — the key-down gap, made deterministic
    /// and large enough to see. Every real modem has this gap; only the size differs.
    /// </summary>
    private sealed class SlowToModulate(int sampleRate, TimeSpan cost) : IModem
    {
        public string Mode => "slow";

        public event Action<byte[], FrameQuality>? FrameDecoded;

        public bool CarrierDetect => false;

        public bool ChannelBusy => false;

        public void Process(ReadOnlySpan<float> samples)
        {
        }

        public float[] Modulate(ReadOnlySpan<byte> ax25Frame, int txDelayMilliseconds)
        {
            Thread.Sleep(cost);
            FrameDecoded?.Invoke([], default);   // never raised; keeps the compiler honest
            var audio = new float[sampleRate];   // one second of tone
            for (int n = 0; n < audio.Length; n++)
            {
                audio[n] = 0.5f * MathF.Sin(2 * MathF.PI * (float)CentreHz * n / sampleRate);
            }

            return audio;
        }

        public void ResetCarrierState()
        {
        }
    }

    /// <summary>
    /// An output that buffers and returns straight away — its Drain does not wait for the air.
    /// What a real device does, and what leaves the pacer still painting after the keyup ends.
    /// </summary>
    private sealed class InstantDrainOutput(int sampleRate) : IAudioOutput
    {
        public int SampleRate { get; } = sampleRate;

        public void Write(ReadOnlySpan<float> samples)
        {
        }

        public void Drain()
        {
        }
    }

    /// <summary>Runs audio through the transmit display scaling and one waterfall line.</summary>
    private static (int Lit, double Peak) RenderTransmit(float[] audio)
    {
        float[] scaled = WaterfallWebServer.ForDisplay(audio);
        var lines = new List<byte[]>();
        var source = new Packet.SoundModem.Dsp.WaterfallSource(
            SampleRate, (_, line) => lines.Add(line.ToArray()), LinesPerSecond, 2048);
        source.Process(scaled);

        byte[] last = lines[^1];
        int lit = 0;
        double peak = 0;
        foreach (byte bin in last)
        {
            double brightness = Brightness(bin);
            if (brightness > 0.15)
            {
                lit++;
            }

            peak = Math.Max(peak, brightness);
        }

        return (lit, peak);
    }

    /// <summary>Lit bins and peak brightness of a line, in the page's default dB window.</summary>
    private static (int Lit, double Peak) Describe(byte[] line)
    {
        ReadOnlySpan<byte> bins = line.AsSpan(5);
        int lit = 0;
        double peak = 0;
        foreach (byte bin in bins)
        {
            double brightness = Brightness(bin);
            if (brightness > 0.15)
            {
                lit++;
            }

            peak = Math.Max(peak, brightness);
        }

        return (lit, peak);
    }

    [Fact]
    public async Task Scaling_For_The_Display_Does_Not_Move_Or_Reshape_The_Signal()
    {
        // The whole point is that it is a gain and nothing else: the operator is reading
        // bandwidth and placement off this line, and both must survive.
        using ClientWebSocket socket = await ConnectAsync();

        Task transmitting = TransmitAsync(Payload(60));
        byte[] line = await NextLineAsync(socket, 0x03, skip: 5);
        await transmitting;

        ReadOnlySpan<byte> bins = line.AsSpan(5);
        double binHz = (double)SampleRate / (bins.Length * 2);
        int peak = 0;
        for (int b = 0; b < bins.Length; b++)
        {
            if (bins[b] > bins[peak])
            {
                peak = b;
            }
        }

        (peak * binHz).Should().BeInRange(
            CentreHz - 400, CentreHz + 400, "the drawn signal must sit where the modem transmits");
    }

    /// <summary>
    /// A frame of varied data. Not zeros: an all-zero AX.25 frame modulates to something close to
    /// a pure tone, which occupies a fraction of the span a real frame does — narrow enough that
    /// it passed the level assertions below even unnormalised, and so proved nothing.
    /// </summary>
    /// <summary>The audio one frame of this mode puts on the air.</summary>
    private static float[] Modulated(byte[] frame) =>
        ModemCatalog.Create("afsk300-il2pc", SampleRate, _ => { },
            new ModemOptions(CentreFrequencyHz: CentreHz)).Modulate(frame, txDelayMilliseconds: 20);

    private static byte[] Payload(int length)
    {
        var frame = new byte[length];
        for (int i = 0; i < length; i++)
        {
            frame[i] = (byte)(i * 7);
        }

        return frame;
    }

    /// <summary>Maps a line byte to display brightness in the page's default −95..−35 dB window.</summary>
    private static double Brightness(byte bin)
    {
        const double floorDb = -95, topDb = -35;
        double db = Packet.SoundModem.Dsp.WaterfallSource.FloorDb
            + (bin * (-Packet.SoundModem.Dsp.WaterfallSource.FloorDb / 255.0));
        return Math.Clamp((db - floorDb) / (topDb - floorDb), 0, 1);
    }

    /// <summary>
    /// A line of the given type from well inside the burst. The first few are still part-filled
    /// with whatever preceded it, so they are not representative of either state.
    /// </summary>
    private async Task<byte[]> NextLineAsync(ClientWebSocket socket, byte type, int skip)
    {
        int seen = 0;
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 20_000)
        {
            (WebSocketMessageType kind, byte[] payload) = await ReceiveAsync(socket);
            if (kind == WebSocketMessageType.Binary && payload.Length > 5 && payload[0] == type
                && seen++ >= skip)
            {
                return payload;
            }
        }

        throw new InvalidOperationException($"no 0x{type:X2} line arrived");
    }

    private async Task<ClientWebSocket> ConnectAsync()
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), _cancellation.Token);
        await ReceiveAsync(socket);   // the config message
        return socket;
    }

    /// <summary>Runs a real transmit through the transmitter loop the daemon uses.</summary>
    private async Task TransmitAsync(byte[] frame)
    {
        _channel.Csma.Persistence = 255;
        _channel.Csma.TxDelayMilliseconds = 20;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var output = new Channel.FakeAudioOutput(SampleRate);
        Task transmitter = _channel.RunTransmitterAsync(output, new NullPtt(), stop.Token);
        await _channel.EnqueueTransmit(0, frame).WaitAsync(TimeSpan.FromSeconds(20));
        await stop.CancelAsync();
        try
        {
            await transmitter;
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Arrival times of transmitted (0x03) waterfall lines, in milliseconds.</summary>
    private async Task<List<long>> CollectTransmitLinesAsync(ClientWebSocket socket, int atLeast)
    {
        var arrivals = new List<long>();
        var clock = Stopwatch.StartNew();
        while (arrivals.Count < atLeast && clock.ElapsedMilliseconds < 20_000)
        {
            (WebSocketMessageType kind, byte[] payload) = await ReceiveAsync(socket);
            if (kind == WebSocketMessageType.Binary && payload.Length > 0 && payload[0] == 0x03)
            {
                arrivals.Add(clock.ElapsedMilliseconds);
            }
        }

        return arrivals;
    }

    private async Task<(WebSocketMessageType Kind, byte[] Payload)> ReceiveAsync(ClientWebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, _cancellation.Token);
        return (result.MessageType, buffer[..result.Count]);
    }

    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
