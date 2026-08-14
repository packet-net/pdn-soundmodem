using System.Net.WebSockets;
using System.Text;
using M0LTE.Radio.Audio;
using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
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
/// seconds of nothing - receive processing is gated off while transmitting, so there is no other
/// line source during a keyup. That is what "juddery, hangs at the start of TX, renders large
/// chunks at a time" looks like from the browser.</para>
/// <para>Receive audio has no such problem: it arrives from the sound card in real time. So the
/// transmit side is queued and released at the rate time passes, which is the rate the audio is
/// actually leaving the radio.</para>
/// <para><b>Time here is a <see cref="FakeTimeProvider"/>, and every assertion is exact.</b> The
/// server paces off <see cref="WaterfallOptions.TimeProvider"/> alone, so a test advances the
/// clock in whole pacer periods and knows precisely how many samples each tick releases and how
/// many lines those samples yield. Earlier versions of this file measured the same properties
/// against the wall clock with tolerance bands, and a loaded CI box walked straight through the
/// bands. Nothing here sleeps, delays or reads a stopwatch; the only real-time anything is the
/// 60-second cancellation guard, which fires on a deadlock and never on a pass.</para>
/// <para>The model, mirrored from <c>WaterfallWebServer.PaceTransmitLines</c>: the pacer ticks
/// every <see cref="PacerPeriod"/> and releases <c>(int)(elapsed * rate)</c> queued samples -
/// <see cref="SamplesPerTick"/>, which is 399 and not 400 because the period truncates to whole
/// ticks and the budget truncates to whole samples, exactly as the server computes it. The
/// spectrum source emits one line per <see cref="HopSamples"/> samples fed, from the first
/// sample. Lines reach the test through a real WebSocket; a broadcast marker
/// (<see cref="WaterfallWebServer.SetRadioStatus"/>) sent after each advance bounds the drain,
/// because the per-client queue is FIFO - everything painted before the marker arrives before
/// the marker.</para>
/// </remarks>
public class WaterfallTransmitPacingTests : IAsyncLifetime
{
    private const int SampleRate = 12000;
    private const int LinesPerSecond = 30;

    /// <summary>Samples per waterfall line: the spectrum source's hop.</summary>
    private const int HopSamples = SampleRate / LinesPerSecond;

    private const double CentreHz = 850;   // the 300-baud slot this station actually runs

    /// <summary>The pacer's period, computed exactly as the server computes it.</summary>
    private static readonly TimeSpan PacerPeriod = TimeSpan.FromMilliseconds(1000.0 / LinesPerSecond);

    /// <summary>Queued samples one pacer tick releases - the server's own truncation, mirrored.</summary>
    private static readonly int SamplesPerTick = (int)(PacerPeriod.TotalSeconds * SampleRate);

    private readonly FakeTimeProvider _time = new();
    private readonly SoundModemChannel _channel;
    private readonly WaterfallWebServer _server;
    private readonly int _port;
    private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromSeconds(60));
    private long _transmittedSamples;
    private int _markers;

    public WaterfallTransmitPacingTests()
    {
        _channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        _channel.AddModem(0, sink => ModemCatalog.Create("afsk300-il2pc", SampleRate, sink, new ModemOptions(CentreFrequencyHz: CentreHz)));
        _channel.TransmittedAudio += samples => _transmittedSamples += samples.Length;
        _port = FreePort();
        _server = new WaterfallWebServer(
            _channel, _port, new WaterfallOptions { LinesPerSecond = LinesPerSecond, TimeProvider = _time });
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

    /// <summary>Total queued samples released after this many pacer ticks.</summary>
    private static long Released(long ticks, long queued) => Math.Min(ticks * SamplesPerTick, queued);

    /// <summary>Waterfall lines the source has emitted after being fed this many samples.</summary>
    private static int LinesFor(long samples) => (int)(samples / HopSamples);

    /// <summary>Pacer ticks needed to release a queue of this many samples in full.</summary>
    private static int TicksToDrain(long samples) => (int)((samples + SamplesPerTick - 1) / SamplesPerTick);

    /// <summary>A line whose every bin is byte 0 - digital silence on the display.</summary>
    private static bool IsSilent(byte[] line) => line.AsSpan(5).IndexOfAnyExcept((byte)0) < 0;

    [Fact]
    public async Task A_Keyup_Paints_Across_Its_Own_Duration_Rather_Than_In_One_Lurch()
    {
        using ClientWebSocket socket = await ConnectAsync();

        // A frame big enough that "all at once" and "spread over the burst" cannot be confused
        // for one another: seconds of audio, dozens of lines.
        await TransmitAsync(Payload(60));
        long burst = _transmittedSamples;

        // The bug, as an assertion: every line landing in the instant of the handover. No time
        // has passed on the display clock, so nothing may have been painted.
        (await DrainAsync(socket, _server)).Should().BeEmpty(
            "the burst must paint across its own duration, not in a single instant chunk");

        // One pacer tick at a time: each releases SamplesPerTick samples, each yields the lines
        // the model says and not one more or fewer. That is both halves of the old assertion at
        // once - no lurch (never more than the tick's own lines) and no stall (never fewer).
        int ticks = TicksToDrain(burst);
        int painted = 0;
        for (int tick = 1; tick <= ticks; tick++)
        {
            _time.Advance(PacerPeriod);
            List<byte[]> lines = await DrainAsync(socket, _server);
            int expected = LinesFor(Released(tick, burst)) - LinesFor(Released(tick - 1, burst));
            lines.Count.Should().Be(
                expected,
                "tick {0} of {1}: lines must keep coming through the keyup, one per display tick, rather than stalling in it or bunching",
                tick,
                ticks);
            lines.Should().AllSatisfy(line => line[0].Should().Be((byte)0x03, "a keyup paints transmit lines"));
            painted += lines.Count;
        }

        painted.Should().Be(LinesFor(burst), "the whole keyup must have painted by the end of its own duration");

        // And the pacer has put itself away: further display time paints nothing.
        _time.Advance(PacerPeriod);
        _time.Advance(PacerPeriod);
        (await DrainAsync(socket, _server)).Should().BeEmpty("the keyup is over and fully painted");
    }

    [Fact]
    public async Task Lines_Arrive_At_About_The_Display_Rate_Not_Faster()
    {
        using ClientWebSocket socket = await ConnectAsync();

        await TransmitAsync(Payload(60));
        long burst = _transmittedSamples;
        burst.Should().BeGreaterThan(
            2L * LinesPerSecond * SamplesPerTick,
            "the burst must outlast the two display seconds this test paces through");

        // Two whole seconds of display time, a second per batch: each fake second must yield
        // exactly a second's worth of waterfall - 29 lines, not 30, because the pacer's own
        // truncation releases 399 samples per tick against the source's 400-sample hop. Exact,
        // in both directions: faster was the original bug, slower is a stall.
        int painted = 0;
        for (int second = 1; second <= 2; second++)
        {
            for (int tick = 0; tick < LinesPerSecond; tick++)
            {
                _time.Advance(PacerPeriod);
            }

            painted += (await DrainAsync(socket, _server)).Count;
            painted.Should().Be(
                LinesFor(Released((long)second * LinesPerSecond, burst)),
                "a transmitted second must take about a second of waterfall - no more, no less");
        }

        // And with the clock parked, nothing more arrives: lines come from display time passing,
        // never from the queue being helpfully flushed.
        (await DrainAsync(socket, _server)).Should().BeEmpty(
            "no display time has passed, so no line may arrive");
    }

    [Fact]
    public async Task Painting_Our_Own_Burst_Never_Holds_Up_The_Transmitter()
    {
        // The pacing must sit behind the event, not in front of it. A display that made the
        // transmit loop wait a second per second of audio would wreck channel timing outright.
        using ClientWebSocket socket = await ConnectAsync();

        // The display clock never moves during the handover. If the transmitter waited on the
        // picture in any way it would be waiting on a clock nobody is advancing, and this await
        // would deadlock into the cancellation guard - the failure this test exists to force.
        long before = _time.GetTimestamp();
        await TransmitAsync(Payload(60));
        _time.GetTimestamp().Should().Be(
            before, "the transmitter must hand the burst over in an instant, not play it out");

        // And the display is still painting it afterwards - the point of doing it this way.
        for (int tick = 0; tick < 11; tick++)
        {
            _time.Advance(PacerPeriod);
        }

        (await DrainAsync(socket, _server)).Count.Should().Be(
            LinesFor(Released(11, _transmittedSamples)),
            "the burst is still being painted after the transmitter has long since finished");
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
        // design intent directly - our own transmission is drawn on the scale the operator has
        // calibrated their eye to - rather than pinning a number that only holds for one modem.
        // The 1.3 factors below are that design margin, not a timing tolerance: both lines are
        // produced from fixed audio by a fixed transform and come out identical on every run.
        using ClientWebSocket socket = await ConnectAsync();

        await TransmitAsync(Payload(60));
        long burst = _transmittedSamples;

        // Paint the whole keyup, a display second per batch to stay inside the client queue.
        var transmitLines = new List<byte[]>();
        int ticks = TicksToDrain(burst);
        for (int done = 0; done < ticks; done += LinesPerSecond)
        {
            for (int tick = 0; tick < Math.Min(LinesPerSecond, ticks - done); tick++)
            {
                _time.Advance(PacerPeriod);
            }

            transmitLines.AddRange(await DrainAsync(socket, _server));
        }

        transmitLines.Count.Should().Be(LinesFor(burst), "the whole burst paints once its duration has passed");
        byte[] transmitted = transmitLines[5];   // well inside the burst, clear of the transform's warm-up

        // The same audio, received at a strong-signal level. The transmit queue is fully drained,
        // so the receive tap is open again and each hop of samples paints synchronously.
        float[] audio = Modulated(Payload(60));
        for (int i = 0; i < audio.Length; i++)
        {
            audio[i] *= 0.02f;   // a strong station, well above the noise and not overloading
        }

        var receiveLines = new List<byte[]>();
        for (int block = 0; receiveLines.Count < 6; block++)
        {
            _channel.ProcessReceive(audio.AsSpan(block * HopSamples, HopSamples));
            receiveLines.AddRange(await DrainAsync(socket, _server));
        }

        receiveLines.Should().AllSatisfy(line => line[0].Should().Be((byte)0x01, "these lines were heard, not sent"));
        byte[] received = receiveLines[5];

        (int txLit, double txPeak) = Describe(transmitted);
        (int rxLit, double rxPeak) = Describe(received);

        txLit.Should().BeLessThanOrEqualTo(
            (int)(rxLit * 1.3),
            "our own transmission must not cover more of the display than a strong station does");
        txPeak.Should().BeLessThanOrEqualTo(
            Math.Min(1.0, rxPeak * 1.3),
            "nor be pinned at the top of the scale when a real signal is not");
        txPeak.Should().BeGreaterThan(
            0.4, "it still has to be clearly visible - this is a level fix, not a mute");
    }

    [Fact]
    public void A_Quiet_Stretch_Of_A_Keyup_Is_Not_Amplified_Into_A_Full_Width_Haze()
    {
        // The regression this exists to prevent. Scaling transmitted audio by normalising each
        // buffer to a target level is an automatic gain control, and an AGC exists to make quiet
        // things loud: a ramp-down, a tail, an idle stretch of a shifted burst gets multiplied by
        // an enormous gain and its noise floor fills the whole span. Measured at the time: near
        // silence at -65 dBFS rms, normalised to -40, lit 1011 of 1024 bins - the same full-width
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
        // - the normal case. Receive audio then resumes while the burst is still being drawn, and
        // both feed the same transform, which has one accumulator: a single window ends up
        // holding part of a burst and part of the band noise and comes out broadband. Measured
        // before the fix: transmitted lines lighting 450-550 bins instead of ~75, with the line
        // type alternating between the two ramps. That is the full-width haze over the back half
        // of a keyup.
        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => ModemCatalog.Create("afsk300-il2pc", SampleRate, sink, new ModemOptions(CentreFrequencyHz: CentreHz)));
        long transmitted = 0;
        channel.TransmittedAudio += samples => transmitted += samples.Length;
        await using var server = new WaterfallWebServer(
            channel, FreePort(), new WaterfallOptions { LinesPerSecond = LinesPerSecond, TimeProvider = _time });
        server.Start();
        using ClientWebSocket socket = await ConnectAsync(server.Port);

        channel.Csma.Persistence = 255;
        channel.Csma.TxDelayMilliseconds = 200;
        var unkeyed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.TransmittingChanged += keyed =>
        {
            if (!keyed)
            {
                unkeyed.TrySetResult();
            }
        };

        using var stop = new CancellationTokenSource();
        Task transmitter = channel.RunTransmitterAsync(
            new InstantDrainOutput(SampleRate), new NullPtt(), stop.Token);
        await channel.EnqueueTransmit(0, Payload(60)).WaitAsync(_cancellation.Token);
        await unkeyed.Task.WaitAsync(_cancellation.Token);   // the tail is queued; the keyup is over

        // Band noise, always flowing, exactly as a sound card delivers it: one hop of samples
        // per display tick, offered while the burst is still being painted and after it.
        var rng = new Random(3);
        var block = new float[HopSamples];
        var types = new List<byte>();
        int ticks = TicksToDrain(transmitted);
        for (int tick = 1; tick <= ticks; tick++)
        {
            FillNoise(rng, block);
            channel.ProcessReceive(block);
            _time.Advance(PacerPeriod);
            foreach (byte[] line in await DrainAsync(socket, server))
            {
                types.Add(line[0]);
            }
        }

        // The burst has been painted in full; the same noise now reaches the display again.
        for (int after = 0; after < 5; after++)
        {
            FillNoise(rng, block);
            channel.ProcessReceive(block);
            foreach (byte[] line in await DrainAsync(socket, server))
            {
                types.Add(line[0]);
            }
        }

        await stop.CancelAsync();
        try
        {
            await transmitter;
        }
        catch (OperationCanceledException)
        {
        }

        types.Count(type => type == 0x03).Should().Be(LinesFor(transmitted), "the burst must paint in full");

        // From the first transmitted line to the last, nothing else may be drawn. Measured before
        // the fix, the two alternated line for line - TX, RX, TX, RX - all the way through.
        int first = types.IndexOf(0x03);
        int last = types.LastIndexOf(0x03);
        types.Skip(first).Take(last - first + 1).Should().AllSatisfy(
            type => type.Should().Be(
                (byte)0x03,
                "receive audio must not be drawn into the burst we are still painting"));

        // And the gate opens again the moment the burst is done: the band is drawn, not muted.
        types.Skip(last + 1).Should().HaveCount(5, "one line per hop of noise once the burst has finished painting")
            .And.AllSatisfy(type => type.Should().Be((byte)0x01));
    }

    [Fact]
    public async Task The_Display_Keeps_Moving_Between_Key_Down_And_The_First_Audio()
    {
        // Receive processing stops the instant the transmitter takes the channel, but the first
        // transmitted audio does not exist until the frame has been modulated and handed to the
        // device. Nothing was drawn in between, so the waterfall visibly stalled as the PTT
        // engaged. The modem here spends fifteen display ticks modulating - that gap, made
        // deterministic; a real one is shorter but a real display still stops for it. The modem
        // then HOLDS, so "a line was drawn while the frame was still being modulated" is not a
        // race this test can lose on a loaded machine: the modulation provably has not finished
        // until the test itself lets go of it.
        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        var slow = new SlowToModulate(SampleRate, _time, _cancellation.Token);
        channel.AddModem(0, _ => slow);
        await using var server = new WaterfallWebServer(
            channel, FreePort(), new WaterfallOptions { LinesPerSecond = LinesPerSecond, TimeProvider = _time });
        server.Start();
        using ClientWebSocket socket = await ConnectAsync(server.Port);

        var keyedDown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstAudio = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
        slow.Arm();
        using var stop = new CancellationTokenSource();
        Task transmitter = channel.RunTransmitterAsync(
            new InstantDrainOutput(SampleRate), new NullPtt(), stop.Token);
        Task sending = channel.EnqueueTransmit(0, Payload(60));

        // Drawn while the frame is still being modulated - before any transmitted audio exists
        // to draw. The line is already on its way: the armed modem advanced the display clock
        // through its own modulation gap before it parked itself on the hold.
        byte[] firstLine = await NextTransmitLineAsync(socket);
        firstAudio.Task.IsCompleted.Should().BeFalse(
            "the display must draw during the modulate gap, not wait for the audio to turn up");
        firstLine.Length.Should().BeGreaterThan(5, "and it must be a real line");
        IsSilent(firstLine).Should().BeTrue("what was on the air during the gap is silence, so silence is what is drawn");

        slow.Resume();
        await keyedDown.Task.WaitAsync(_cancellation.Token);
        await sending.WaitAsync(_cancellation.Token);
        await stop.CancelAsync();
        try
        {
            await transmitter;
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// A modem that takes a long time to produce its audio - the key-down gap, made deterministic
    /// and large enough to see. Every real modem has this gap; only the size differs. Armed, its
    /// <see cref="Modulate"/> advances the display clock through the gap and then holds until the
    /// test has seen what the gap painted; unarmed (the band probe at server start) it returns
    /// at once and leaves the clock alone.
    /// </summary>
    private sealed class SlowToModulate(int sampleRate, FakeTimeProvider time, CancellationToken guard) : IModem
    {
        private readonly SemaphoreSlim _resume = new(0);
        private volatile bool _armed;

        public string Mode => "slow";

        public event Action<byte[], FrameQuality>? FrameDecoded;

        public bool CarrierDetect => false;

        public bool ChannelBusy => false;

        public void Arm() => _armed = true;

        public void Resume() => _resume.Release();

        public void Process(ReadOnlySpan<float> samples)
        {
        }

        public float[] Modulate(ReadOnlySpan<byte> ax25Frame, int txDelayMilliseconds)
        {
            if (_armed)
            {
                _armed = false;
                for (int tick = 0; tick < 15; tick++)
                {
                    time.Advance(PacerPeriod);   // the modulation gap, in display time
                }

                _resume.Wait(guard);   // held until the test has read the gap's first line
            }

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
    /// An output that buffers and returns straight away - its Drain does not wait for the air.
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

        await TransmitAsync(Payload(60));

        // Six lines' worth of display time, then the sixth line - well inside the burst, clear
        // of the transform's warm-up.
        var lines = new List<byte[]>();
        for (int tick = 1; lines.Count < 6; tick++)
        {
            _time.Advance(PacerPeriod);
            lines.AddRange(await DrainAsync(socket, _server));
            tick.Should().BeLessThan(
                TicksToDrain(_transmittedSamples), "the burst must yield six lines well before it has finished painting");
        }

        byte[] line = lines[5];
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
    /// a pure tone, which occupies a fraction of the span a real frame does - narrow enough that
    /// it passed the level assertions below even unnormalised, and so proved nothing.
    /// </summary>
    /// <summary>The audio one frame of this mode puts on the air.</summary>
    private static float[] Modulated(byte[] frame) =>
        ModemCatalog.Create("afsk300-il2pc", SampleRate, _ => { },
            new ModemOptions(CentreFrequencyHz: CentreHz)).Modulate(frame, txDelayMilliseconds: 20);

    /// <summary>
    /// A keyup is painted starting when the audio goes out, not when the device has finished
    /// taking it - so the burst does not arrive on screen behind a stretch of black as long as
    /// itself.
    /// </summary>
    /// <remarks>
    /// <para>Reported from the air: <em>"the radio keys up and the waterfall starts showing black
    /// rows, then there is a pause of about 50% of the time the waterfall is not showing RX rows,
    /// then the preamble, then the frame"</em>. That halving is the tell.</para>
    /// <para>A real sound card's <see cref="IAudioOutput.Write"/> blocks until its buffer has room,
    /// so writing a burst longer than the buffer does not return until most of it has played. The
    /// display was told about the audio <em>after</em> that call returned, so the pacer spent the
    /// whole transmission painting silence - the only thing it has while the queue is empty and the
    /// channel is keyed - and then painted the actual burst over again afterwards. Twice the
    /// duration, the first half black.</para>
    /// <para>Every plain test double in this suite accepts instantly, which is exactly why nothing
    /// here ever saw it. This one models the device on the display's own clock: its Write returns
    /// only after the audio's whole duration has passed on that clock, so everything the pacer
    /// does "while the device swallows the burst" happens inside the Write call - and a marker
    /// sent as Write returns splits the line stream exactly at that boundary. Told before the
    /// write, the pacer paints the burst inside it: every during-write line carries signal. Told
    /// after - the defect - it would have painted that entire window as silence.</para>
    /// </remarks>
    [Fact]
    public async Task A_Keyup_Is_Painted_As_It_Goes_Out_Not_After_The_Device_Has_Swallowed_It()
    {
        using ClientWebSocket socket = await ConnectAsync();

        _channel.Csma.Persistence = 255;
        _channel.Csma.TxDelayMilliseconds = 20;
        _channel.Csma.TxTailMilliseconds = 0;   // one keyup, one Write, one window to model
        var unkeyed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _channel.TransmittingChanged += keyed =>
        {
            if (!keyed)
            {
                unkeyed.TrySetResult();
            }
        };

        using var stop = new CancellationTokenSource();
        var output = new FakeClockOutput(SampleRate, _time, () => _server.SetRadioStatus("wrote"));
        Task transmitter = _channel.RunTransmitterAsync(output, new NullPtt(), stop.Token);
        // Small enough that a whole keyup of lines fits the client queue while nobody reads it.
        await _channel.EnqueueTransmit(0, Payload(16)).WaitAsync(_cancellation.Token);
        await unkeyed.Task.WaitAsync(_cancellation.Token);
        long burst = _transmittedSamples;

        // The device swallowed the burst across exactly its own duration of display time, all of
        // it inside Write. The pacer ticked this many times in that window.
        long ticksInWrite = TimeSpan.FromSeconds((double)burst / SampleRate).Ticks / PacerPeriod.Ticks;
        ticksInWrite.Should().BeGreaterThan(0, "the burst must be long enough to see the device take it");

        // Whatever the write window left unreleased, paint now, then close the stream.
        long released = Released(ticksInWrite, burst);
        while (released < burst)
        {
            _time.Advance(PacerPeriod);
            released = Math.Min(released + SamplesPerTick, burst);
        }

        var duringWrite = new List<byte[]>();
        var afterWrite = new List<byte[]>();
        bool wrote = false;
        string final = NextMarker();
        _server.SetRadioStatus(final);
        while (true)
        {
            (WebSocketMessageType kind, byte[] payload) = await ReceiveAsync(socket);
            if (kind == WebSocketMessageType.Text)
            {
                string text = Encoding.UTF8.GetString(payload);
                if (text.Contains("\"wrote\"", StringComparison.Ordinal))
                {
                    wrote = true;
                }
                else if (text.Contains($"\"{final}\"", StringComparison.Ordinal))
                {
                    break;
                }

                continue;
            }

            if (payload.Length > 5 && payload[0] == 0x03)
            {
                (wrote ? afterWrite : duringWrite).Add(payload);
            }
        }

        await stop.CancelAsync();
        try
        {
            await transmitter;
        }
        catch (OperationCanceledException)
        {
        }

        // The keyup was painted as it went out: the lines drawn while the device was swallowing
        // the burst are the burst, to the exact line the pacer's budget allows.
        duringWrite.Count.Should().Be(
            LinesFor(Released(ticksInWrite, burst)),
            "the display must paint the burst while the device is taking it, not sit black until Write returns");
        (duringWrite.Count + afterWrite.Count).Should().Be(LinesFor(burst), "and the whole keyup paints exactly once");

        // The defect painted the entire write window black and the burst over again afterwards -
        // "twice the duration, the first half black". No line of this keyup may be black: no
        // display time ever passes between key-down and the audio existing, so there is nothing
        // for silence to stand in for.
        duringWrite.Should().AllSatisfy(
            line => IsSilent(line).Should().BeFalse(
                "black lead-in is the device-swallow defect: the display was told after the audio had gone"));
        afterWrite.Should().AllSatisfy(
            line => IsSilent(line).Should().BeFalse("the tail of the keyup is still the burst, not black"));
    }

    /// <summary>
    /// A sound card on the display's own clock: a <see cref="Write"/> that returns only once the
    /// samples' full duration has passed on the fake clock - the blocking write of a real device,
    /// with display time standing in for the air - and a callback as it returns, so a test can
    /// mark that boundary in the line stream. Replaces a Stopwatch-and-Sleep double that made
    /// this file's slowest test also its most load-sensitive one.
    /// </summary>
    private sealed class FakeClockOutput(int sampleRate, FakeTimeProvider time, Action wrote) : IAudioOutput
    {
        public int SampleRate { get; } = sampleRate;

        public void Write(ReadOnlySpan<float> samples)
        {
            long remaining = TimeSpan.FromSeconds((double)samples.Length / SampleRate).Ticks;
            while (remaining >= PacerPeriod.Ticks)
            {
                time.Advance(PacerPeriod);
                remaining -= PacerPeriod.Ticks;
            }

            if (remaining > 0)
            {
                time.Advance(TimeSpan.FromTicks(remaining));
            }

            wrote();
        }

        public void Drain()
        {
        }
    }

    private static byte[] Payload(int length)
    {
        var frame = new byte[length];
        for (int i = 0; i < length; i++)
        {
            frame[i] = (byte)(i * 7);
        }

        return frame;
    }

    private static void FillNoise(Random rng, float[] block)
    {
        for (int i = 0; i < block.Length; i++)
        {
            block[i] = (float)(rng.NextDouble() - 0.5) * 0.004f;
        }
    }

    /// <summary>Maps a line byte to display brightness in the page's default -95..-35 dB window.</summary>
    private static double Brightness(byte bin)
    {
        const double floorDb = -95, topDb = -35;
        double db = Packet.SoundModem.Dsp.WaterfallSource.FloorDb
            + (bin * (-Packet.SoundModem.Dsp.WaterfallSource.FloorDb / 255.0));
        return Math.Clamp((db - floorDb) / (topDb - floorDb), 0, 1);
    }

    private async Task<ClientWebSocket> ConnectAsync(int? port = null)
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port ?? _port}/ws"), _cancellation.Token);
        await ReceiveAsync(socket);   // the config message
        return socket;
    }

    /// <summary>Runs a real transmit through the transmitter loop the daemon uses. Returns once
    /// the keyup is completely over - tail queued to the display, channel unkeyed - so the whole
    /// burst is sitting in the pacer's queue and <see cref="_transmittedSamples"/> is final.</summary>
    private async Task TransmitAsync(byte[] frame)
    {
        _channel.Csma.Persistence = 255;
        _channel.Csma.TxDelayMilliseconds = 20;
        var unkeyed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _channel.TransmittingChanged += keyed =>
        {
            if (!keyed)
            {
                unkeyed.TrySetResult();
            }
        };

        using var stop = new CancellationTokenSource();
        var output = new Channel.FakeAudioOutput(SampleRate);
        Task transmitter = _channel.RunTransmitterAsync(output, new NullPtt(), stop.Token);
        await _channel.EnqueueTransmit(0, frame).WaitAsync(_cancellation.Token);
        await unkeyed.Task.WaitAsync(_cancellation.Token);
        await stop.CancelAsync();
        try
        {
            await transmitter;
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Every waterfall line already broadcast, in order. Bounded by a marker rather than by
    /// waiting: <see cref="WaterfallWebServer.SetRadioStatus"/> broadcasts through the same
    /// FIFO client queue as the lines, so once the marker text arrives, everything painted
    /// before it has been counted - no timeout, no idle-means-done heuristic.
    /// </summary>
    private async Task<List<byte[]>> DrainAsync(ClientWebSocket socket, WaterfallWebServer server)
    {
        string marker = NextMarker();
        server.SetRadioStatus(marker);
        var lines = new List<byte[]>();
        while (true)
        {
            (WebSocketMessageType kind, byte[] payload) = await ReceiveAsync(socket);
            if (kind == WebSocketMessageType.Text
                && Encoding.UTF8.GetString(payload).Contains($"\"{marker}\"", StringComparison.Ordinal))
            {
                return lines;
            }

            if (kind == WebSocketMessageType.Binary && payload.Length > 5
                && payload[0] is 0x01 or 0x03)
            {
                lines.Add(payload);
            }
        }
    }

    private string NextMarker() => $"marker-{++_markers}";

    /// <summary>The next transmit (0x03) line off the socket. Everything else is skipped.</summary>
    private async Task<byte[]> NextTransmitLineAsync(ClientWebSocket socket)
    {
        while (true)
        {
            (WebSocketMessageType kind, byte[] payload) = await ReceiveAsync(socket);
            if (kind == WebSocketMessageType.Binary && payload.Length > 5 && payload[0] == 0x03)
            {
                return payload;
            }
        }
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
