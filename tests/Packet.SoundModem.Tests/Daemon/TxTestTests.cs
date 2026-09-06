using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.Modems;
using M0LTE.Radio.Audio;
using Packet.SoundModem.Tests.Channel;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// The operator's transmitter test, end to end against a fake radio: what goes on the air, what
/// is refused, and that PTT comes back down every way a test can end.
/// </summary>
/// <remarks>
/// <para>There is no sound card and no radio on the machine that runs these, so what is proved
/// here is the keying sequence and the audio, against the same <c>RecordingPtt</c> and
/// <c>FakeAudioOutput</c> the channel's own tests use. The real transmit path (an ALSA card and a
/// CM108 GPIO, or the Flex DAX stream) is somebody's bench, and the point of routing the test
/// through <see cref="SoundModemChannel"/> rather than writing to the device directly is exactly
/// that there is nothing left for those to do differently.</para>
/// <para>The refusals are half the feature. A test transmission is a licensed transmission, so
/// every way of asking for one that should not go out has a test here.</para>
/// </remarks>
public class TxTestTests
{
    private const int Rate = 12000;

    private sealed class Rig : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(30));
        private readonly Task _transmitter;

        internal Rig(Func<TxTestOptions, TxTestOptions>? settings = null, IPttControl? ptt = null)
        {
            Ptt = ptt ?? new RecordingPtt();
            Channel = new SoundModemChannel(Rate, randomSeed: 5);
            Channel.AddModem(0, sink => ModemCatalog.Create("afsk1200", Rate, sink));
            Channel.AddModem(9, _ => Band);
            Channel.Csma.Persistence = 255;
            Channel.Csma.TxDelayMilliseconds = 20;
            Channel.Csma.TxTailMilliseconds = 0;

            var options = new TxTestOptions
            {
                Channel = Channel,
                Journal = new StationJournal("", Lines.Add, Errors.Add),
                Report = Reports.Add,
                Recorded = Records.Add,
            };

            Runner = new TxTestRunner(settings is null ? options : settings(options));
            _transmitter = Channel.RunTransmitterAsync(Output, Ptt, _stop.Token);
        }

        internal SoundModemChannel Channel { get; }

        /// <summary>Whether the band is busy, as the transmitter's carrier sense sees it.</summary>
        internal DeafModem Band { get; } = new();

        internal TxTestRunner Runner { get; }

        internal FakeAudioOutput Output { get; } = new(Rate);

        internal IPttControl Ptt { get; }

        internal IReadOnlyList<string> Keying => Ptt is RecordingPtt recording
            ? recording.Events
            : ((ThrowingPtt)Ptt).Events;

        /// <summary>
        /// Waits for the keying to settle before it is read. A transmission's task completes when
        /// its audio has left the device, and the unkey comes after the tail and the whole
        /// transmitter loop - so reading the events straight after awaiting the test is racing
        /// them, which passes on an idle machine and fails under suite load.
        /// </summary>
        internal async Task SettledAsync(int events = 2)
        {
            using var giveUp = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (Keying.Count < events)
            {
                giveUp.Token.ThrowIfCancellationRequested();
                await Task.Delay(20, giveUp.Token);
            }
        }

        internal List<string> Lines { get; } = [];

        internal List<string> Errors { get; } = [];

        internal List<TxTestStatus> Reports { get; } = [];

        internal List<TxTestRecord> Records { get; } = [];

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            try
            {
                await _transmitter;
            }
            catch (OperationCanceledException)
            {
            }

            _stop.Dispose();
        }
    }

    /// <summary>
    /// A modem that hears whatever the test tells it to. Carrier sense is the path that matters
    /// here: <c>RunTransmitterAsync</c> waits on a busy channel with no timeout at all, so a
    /// transmission can sit queued - already accepted, already past the inhibit - for as long as
    /// the band is occupied. That is where a cancelled test would key the radio later.
    /// </summary>
    private sealed class DeafModem : IModem
    {
        public string Mode => "afsk1200";

        public event Action<byte[], FrameQuality>? FrameDecoded;

        public bool CarrierDetect => false;

        public bool ChannelBusy { get; set; }

        public void Process(ReadOnlySpan<float> samples)
        {
        }

        public float[] Modulate(ReadOnlySpan<byte> ax25Frame, int txDelayMilliseconds) => [];

        public void ResetCarrierState() => FrameDecoded?.Invoke([], default!);
    }

    /// <summary>A radio that will not key - a dead serial lead, or a contended Flex.</summary>
    private sealed class ThrowingPtt : IPttControl
    {
        internal List<string> Events { get; } = [];

        internal Exception? Failure { get; set; } = new IOException("the PTT lead has gone");

        public void Key()
        {
            Events.Add("key");
            if (Failure is not null)
            {
                throw Failure;
            }
        }

        public void Unkey() => Events.Add("unkey");
    }

    private static double Amplitude(IReadOnlyList<float> audio, double hz)
    {
        // Over the middle half, clear of the shaped edges and of the silence TXDELAY spends.
        int from = audio.Count / 4, count = audio.Count / 2;
        double w = 2 * Math.PI * hz / Rate;
        double cosine = Math.Cos(w), sine = Math.Sin(w);
        double s1 = 0, s2 = 0;
        for (int n = 0; n < count; n++)
        {
            double s = audio[from + n] + (2 * cosine * s1) - s2;
            s2 = s1;
            s1 = s;
        }

        double real = s1 - (s2 * cosine);
        double imaginary = s2 * sine;
        return 2 * Math.Sqrt((real * real) + (imaginary * imaginary)) / count;
    }

    [Fact]
    public async Task A_Two_Tone_Test_Keys_Sends_The_Pair_And_Unkeys()
    {
        await using var rig = new Rig();

        TxTestOutcome outcome = await rig.Runner.RunAsync(new TxTestRequest(true, 0, 1));

        outcome.Ran.Should().BeTrue();
        await rig.SettledAsync();
        rig.Keying.Should().Equal(["key", "unkey"], "the radio is keyed for the test and let go");

        float[] audio = rig.Output.Snapshot();
        audio.Length.Should().BeGreaterThanOrEqualTo(Rate, "a one-second test is a second of audio");
        Amplitude(audio, TestTone.TwoToneLowHz).Should().BeApproximately(0.4, 0.02);
        Amplitude(audio, TestTone.TwoToneHighHz).Should().BeApproximately(0.4, 0.02);
    }

    [Fact]
    public async Task A_Single_Tone_Test_Sends_The_Tone_That_Was_Asked_For()
    {
        await using var rig = new Rig();

        TxTestOutcome outcome = await rig.Runner.RunAsync(new TxTestRequest(false, 999, 1));

        outcome.Ran.Should().BeTrue();
        await rig.SettledAsync();
        rig.Keying.Should().Equal(["key", "unkey"]);
        Amplitude(rig.Output.Snapshot(), 999).Should().BeApproximately(0.8, 0.02);

        // And the operator is told what the tone is for, in the journal and on the page: this is
        // the whole reason 999 Hz is a preset rather than a number somebody has to remember.
        rig.Lines.Should().Contain(line => line.Contains("2.4 kHz deviation"));
    }

    [Fact]
    public async Task The_Level_Is_The_One_The_Stations_Data_Goes_Out_At()
    {
        // Set the amplitude down and the burst follows it: the point of the test is that what is
        // measured is what a frame gets, so this is not a knob with a life of its own.
        await using var rig = new Rig(o => o with { Amplitude = 0.4 });

        await rig.Runner.RunAsync(new TxTestRequest(false, 1000, 1));

        Amplitude(rig.Output.Snapshot(), 1000).Should().BeApproximately(0.4, 0.02);
        rig.Lines.Should().Contain(line => line.Contains("peak level 0.40"));
    }

    [Fact]
    public async Task A_Duration_Above_The_Cap_Is_Cut_To_It_Rather_Than_Sent()
    {
        // The stuck button, and the page that asks for something silly: the cap is the only thing
        // between a web request and a PA held up for as long as it says.
        await using var rig = new Rig(o => o with { MaxSeconds = 2 });

        await rig.Runner.RunAsync(new TxTestRequest(true, 0, 600));

        int audio = rig.Output.Snapshot().Length;
        audio.Should().BeLessThan(3 * Rate, "600 seconds was asked for and 2 is the cap");
        audio.Should().BeGreaterThanOrEqualTo(2 * Rate);
        rig.Lines.Should().Contain(line => line.Contains("capped from 600.0 s"));
    }

    [Fact]
    public async Task A_Configured_Cap_Cannot_Be_Raised_Past_The_Ceiling()
    {
        // A cap is a safety limit, so it has one of its own: a typo in the config file must not
        // be able to buy an hour of transmitter.
        await using var rig = new Rig(o => o with { MaxSeconds = 3600 });

        await rig.Runner.RunAsync(new TxTestRequest(true, 0, 3600));

        rig.Output.Snapshot().Length.Should().BeLessThanOrEqualTo(
            (int)((TxTestRunner.CeilingSeconds + 1) * Rate));
        rig.Lines.Should().Contain(line => line.Contains("60.0 s"));
    }

    [Fact]
    public async Task A_Station_With_No_Ptt_Refuses_And_Never_Keys()
    {
        await using var rig = new Rig(o => o with
        {
            Refusal = "no \"ptt\" is configured, so this daemon does not key the radio",
        });

        TxTestOutcome outcome = await rig.Runner.RunAsync(new TxTestRequest(true, 0, 1));

        outcome.Ran.Should().BeFalse();
        outcome.Refusal.Should().Contain("no \"ptt\" is configured");
        rig.Keying.Should().BeEmpty("a refused test never touches the radio");
        rig.Output.Snapshot().Should().BeEmpty();
        rig.Errors.Should().ContainSingle()
            .Which.Should().StartWith("tx test: refused, no \"ptt\" is configured");
        rig.Reports.Should().ContainSingle().Which.State.Should().Be("refused");
        rig.Records.Should().BeEmpty("nothing went out, so nothing is written down");
    }

    [Fact]
    public async Task A_Receive_Only_Station_Is_Refused_By_The_Channel_Itself()
    {
        // Not through the start-up refusal above but through the channel's own gate, so the
        // wording an operator gets is the wording every other transmission on this station gets.
        await using var rig = new Rig();
        rig.Channel.ReceiveOnlyReason = "this station receives only: its audio comes from a web receiver";

        TxTestOutcome outcome = await rig.Runner.RunAsync(new TxTestRequest(true, 0, 1));

        outcome.Ran.Should().BeFalse();
        outcome.Refusal.Should().Contain("receives only");
        rig.Keying.Should().BeEmpty();
    }

    [Fact]
    public async Task A_Tone_The_Channel_Cannot_Carry_Is_Refused_By_Frequency()
    {
        await using var rig = new Rig();

        TxTestOutcome high = await rig.Runner.RunAsync(new TxTestRequest(false, 9000, 1));
        high.Ran.Should().BeFalse();
        high.Refusal.Should().Contain("6000 Hz Nyquist");

        TxTestOutcome low = await rig.Runner.RunAsync(new TxTestRequest(false, 5, 1));
        low.Ran.Should().BeFalse();
        low.Refusal.Should().Contain("50 Hz");

        rig.Keying.Should().BeEmpty();
    }

    [Fact]
    public async Task A_Second_Test_Is_Refused_While_One_Is_Running()
    {
        // The first is held on a busy channel rather than raced against: asking again and hoping
        // the first has not finished passes on an idle machine and fails on a loaded one, which
        // is what it did under full-suite load.
        await using var rig = new Rig();
        rig.Band.ChannelBusy = true;

        Task<TxTestOutcome> first = rig.Runner.RunAsync(new TxTestRequest(true, 0, 2));
        while (rig.Reports.Count == 0)
        {
            await Task.Delay(10);
        }

        TxTestOutcome second = await rig.Runner.RunAsync(new TxTestRequest(true, 0, 1));
        second.Refusal.Should().Be("a test transmission is already running");

        rig.Band.ChannelBusy = false;
        (await first).Ran.Should().BeTrue("and the first is not disturbed by having been asked");
        await rig.SettledAsync();
        rig.Keying.Should().Equal(["key", "unkey"], "one test, one keyup");
    }

    [Fact]
    public async Task Stopping_A_Test_That_Is_Waiting_For_The_Channel_Never_Keys_The_Radio()
    {
        // The lost page and the changed mind. Emptying the burst is not enough: the transmitter
        // keys before it asks for audio, so a cancelled test has to come off the queue entirely
        // or the radio keys on the operator's behalf after they have walked away.
        await using var rig = new Rig();
        rig.Band.ChannelBusy = true;

        Task<TxTestOutcome> running = rig.Runner.RunAsync(new TxTestRequest(true, 0, 5));
        while (rig.Reports.Count == 0)
        {
            await Task.Delay(10);
        }

        rig.Runner.Stop();
        rig.Band.ChannelBusy = false;

        TxTestOutcome outcome = await running;
        outcome.Ran.Should().BeFalse("nothing reached the air");
        outcome.Refusal.Should().Contain("nothing was transmitted");

        // And it stays that way. The channel has been free since the line above; give the
        // transmitter every chance to key on a withdrawn transmission before believing it will not.
        await Task.Delay(500);
        rig.Keying.Should().BeEmpty("a withdrawn test never keys the radio, then or later");
        rig.Output.Snapshot().Should().BeEmpty();
        rig.Records.Should().BeEmpty();
    }

    /// <summary>A fake sound card that presses Stop on the very runner it belongs to, once it has
    /// taken a given number of blocks - which is what an operator's Stop looks like while a test
    /// is already on the air, without racing a fake output that takes no real time to "play"
    /// anything.</summary>
    private sealed class StoppingAfterOutput(int sampleRate, int stopAfterWrites, Action stop)
        : IAudioOutput
    {
        private readonly List<float> _written = [];
        private int _writes;

        public int SampleRate { get; } = sampleRate;

        public void Write(ReadOnlySpan<float> samples)
        {
            lock (_written)
            {
                _written.AddRange(samples.ToArray());
            }

            if (Interlocked.Increment(ref _writes) == stopAfterWrites)
            {
                stop();
            }
        }

        public void Drain()
        {
        }

        public float[] Snapshot()
        {
            lock (_written)
            {
                return [.. _written];
            }
        }
    }

    /// <summary>
    /// Proves the actual cause of #425: <see cref="TestTone.Render"/> renders a whole burst in one
    /// call, at the moment the channel keys up, so by the time an operator's Stop reached the
    /// daemon the entire tone was already on its way to the sound card with nothing left to
    /// shorten - a 5, 15 or 60 second test outlived Stop entirely. The fix leaves the render alone
    /// and changes how the rendered burst reaches the device instead
    /// (<see cref="SoundModemChannel.WriteStoppably"/>, not visible from here): in blocks, with a
    /// check between them, so a stop only has to prevent the next one.
    /// </summary>
    [Fact]
    public async Task Stopping_A_Test_Part_Way_Through_Ends_The_Audio_And_Releases_Ptt_Promptly()
    {
        var channel = new SoundModemChannel(Rate, randomSeed: 5);
        channel.Csma.Persistence = 255;
        channel.Csma.TxDelayMilliseconds = 20;
        channel.Csma.TxTailMilliseconds = 0;

        var runner = new TxTestRunner(new TxTestOptions
        {
            Channel = channel,
            Journal = new StationJournal("", _ => { }, _ => { }),
        });

        runner.Control.IsRunning!().Should().BeFalse("nothing has been asked for yet");

        // What the waterfall (and, behind it, the receive tap and the public monitor uplink) is
        // told was transmitted - which review round 1 found still counted the whole 20 s burst
        // regardless of where the stop landed, holding the receive gate shut for audio that was
        // never actually sent.
        int announcedSamples = 0;
        channel.TransmittedAudio += mem => announcedSamples += mem.Length;

        // The fake card presses Stop on the very runner it is serving, from inside the write it
        // is asked to take mid-burst - a real operator's click, mid-burst, looks the same from
        // the runner's side: some audio already out, more of it queued.
        var ptt = new RecordingPtt();
        var output = new StoppingAfterOutput(Rate, stopAfterWrites: 3, runner.Stop);

        using var stopTransmitter = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task transmitter = channel.RunTransmitterAsync(output, ptt, stopTransmitter.Token);

        TxTestOutcome outcome = await runner.RunAsync(new TxTestRequest(false, 999, 20));

        outcome.Ran.Should().BeTrue("some of the tone reached the air before the stop cut it short");
        outcome.Refusal.Should().BeNull();
        runner.Control.IsRunning!().Should().BeFalse("the run ended when this outcome came back");

        // A handful of 40 ms blocks - comfortably short of the 20 s that was asked for, which is
        // the whole point: the old, whole-burst render would have produced 20 s regardless.
        float[] audio = output.Snapshot();
        audio.Should().NotBeEmpty("some of the tone reached the air before the stop");
        audio.Length.Should().BeLessThan(Rate, "the stop cut it off within about one block, not 20 s of them");

        // What was announced must equal what was actually written - not the 240000-odd samples
        // of a 20 s render, and not the whole array minus the fade either: exactly what reached
        // the device, block by block, so a display or an uplink gated on "audio is still coming"
        // is never held shut for audio that was never sent.
        announcedSamples.Should().Be(audio.Length,
            "the waterfall must not be told about audio a stop kept off the air");

        await stopTransmitter.CancelAsync();
        try
        {
            await transmitter;
        }
        catch (OperationCanceledException)
        {
        }

        ptt.Events.Should().Equal(
            ["key", "unkey"], "PTT dropped once the last queued block drained, not 20 s later");
    }

    /// <summary>
    /// SHOULD FIX 2 (review round 1 of #430): a stop landing inside the TXDELAY lead - before any
    /// tone sample has reached the device - still refuses with "nothing was transmitted", which
    /// is true of the tone but reads as a flat contradiction of a PTT event on the radio's own
    /// log, since the radio did key for the silent lead. The journal gets a line saying so.
    /// </summary>
    [Fact]
    public async Task A_Stop_Inside_The_Txdelay_Lead_Notes_That_The_Radio_Keyed_Anyway()
    {
        var channel = new SoundModemChannel(Rate, randomSeed: 5);
        channel.Csma.Persistence = 255;
        // Long enough that one 40 ms write block (SoundModemChannel.WriteStoppably) lands
        // entirely inside the lead, so a stop after the first block never reaches any tone.
        channel.Csma.TxDelayMilliseconds = 100;
        channel.Csma.TxTailMilliseconds = 0;

        var journal = new List<string>();
        var runner = new TxTestRunner(new TxTestOptions
        {
            Channel = channel,
            Journal = new StationJournal("", journal.Add, journal.Add),
        });

        var ptt = new RecordingPtt();
        var output = new StoppingAfterOutput(Rate, stopAfterWrites: 1, runner.Stop);

        using var stopTransmitter = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task transmitter = channel.RunTransmitterAsync(output, ptt, stopTransmitter.Token);

        TxTestOutcome outcome = await runner.RunAsync(new TxTestRequest(false, 999, 20));

        outcome.Ran.Should().BeFalse("no tone sample ever reached the air");
        outcome.Refusal.Should().Contain("nothing was transmitted");
        journal.Should().Contain(
            line => line.Contains("keyed for the TXDELAY lead only"),
            "the radio did key, briefly, and the journal must not read as though it never did");

        // The radio really did key - this is the point of the note above.
        await stopTransmitter.CancelAsync();
        try
        {
            await transmitter;
        }
        catch (OperationCanceledException)
        {
        }

        ptt.Events.Should().Equal(["key", "unkey"], "PTT came down once the write settled");
    }

    [Fact]
    public async Task A_Test_Defers_To_A_Held_Channel_Exactly_As_A_Frame_Does()
    {
        // The rule an operator has to be able to rely on: this is not a back door around the
        // channel discipline. While an ARQ session holds the channel the test waits, and it goes
        // out when the hold is released - the same gate a KISS frame passes through.
        var inhibited = true;
        await using var rig = new Rig();
        rig.Channel.TransmitInhibit = () => inhibited;

        Task<TxTestOutcome> running = rig.Runner.RunAsync(new TxTestRequest(true, 0, 1));
        await Task.Delay(200);
        rig.Keying.Should().BeEmpty("the channel is held, so nothing is keyed");

        inhibited = false;
        (await running).Ran.Should().BeTrue();
        await rig.SettledAsync();
        rig.Keying.Should().Equal(["key", "unkey"]);
    }

    [Fact]
    public async Task A_Channel_That_Never_Clears_Ends_In_A_Refusal_Rather_Than_A_Held_Button()
    {
        // The other half of the cap. Airtime is bounded by the burst; this bounds the wall clock,
        // so a page does not sit saying "running" for ever and the station is free to be asked
        // again. The channel's own inhibit timeout would answer first on the shipped settings;
        // here the runner's is the shorter of the two, which is what is under test.
        var clock = new FakeTimeProvider();
        await using var rig = new Rig(o => o with
        {
            Time = clock,
            ChannelWait = TimeSpan.FromSeconds(20),
        });
        // Carrier sense, not the ARDOP inhibit: the transmission is accepted and queued, and the
        // transmitter loops on a busy channel with no timeout of its own. This is the state a
        // long carrier on the band puts a test into.
        rig.Band.ChannelBusy = true;

        Task<TxTestOutcome> running = rig.Runner.RunAsync(new TxTestRequest(true, 0, 1));
        while (rig.Reports.Count == 0)
        {
            await Task.Delay(10);
        }

        clock.Advance(TimeSpan.FromSeconds(30));

        TxTestOutcome outcome = await running.WaitAsync(TimeSpan.FromSeconds(20));
        outcome.Ran.Should().BeFalse();
        outcome.Refusal.Should().Contain("did not clear");
        outcome.Refusal.Should().Contain("nothing was transmitted");

        // The half that matters. The operator has been told the test was given up on and has
        // walked away; when the channel finally clears, the radio must not key on their behalf
        // with an unlogged 5 ms blip.
        rig.Band.ChannelBusy = false;
        await Task.Delay(500);
        rig.Keying.Should().BeEmpty("a test given up on never keys the radio afterwards");
        rig.Output.Snapshot().Should().BeEmpty();
        rig.Records.Should().BeEmpty();
        rig.Lines.Should().NotContain(line => line.Contains("on air"));
    }

    [Fact]
    public async Task A_Retry_Cannot_Queue_Behind_A_Test_That_Is_Still_On_The_Channel()
    {
        // The second consequence of a test that cannot be taken back: two transmissions under one
        // source share a keyup, so a retry landing behind a stale one goes out as a blip followed
        // by the real burst with only a token TXDELAY - a transient inside the very measurement
        // the feature exists to make. The run is held open until the channel gives the
        // transmission back, so the retry is refused instead.
        await using var rig = new Rig();
        rig.Band.ChannelBusy = true;

        Task<TxTestOutcome> first = rig.Runner.RunAsync(new TxTestRequest(true, 0, 2));
        while (rig.Reports.Count == 0)
        {
            await Task.Delay(10);
        }

        TxTestOutcome retry = await rig.Runner.RunAsync(new TxTestRequest(true, 0, 2));
        retry.Refusal.Should().Be("a test transmission is already running");

        rig.Runner.Stop();
        rig.Band.ChannelBusy = false;
        (await first).Ran.Should().BeFalse();
        rig.Keying.Should().BeEmpty();
    }

    [Fact]
    public async Task A_Keyup_That_Throws_Is_Reported_As_A_Failure_And_The_Radio_Is_Let_Go()
    {
        // A serial or hidraw lead that has been pulled. Nothing may escape the runner: the page
        // must not be left amber reading "Stop", the API caller must not lose its connection, and
        // the command line must not die before it has closed the radio down.
        var ptt = new ThrowingPtt();
        await using var rig = new Rig(ptt: ptt);

        TxTestOutcome outcome = await rig.Runner.RunAsync(new TxTestRequest(true, 0, 1));

        outcome.Ran.Should().BeFalse();
        outcome.Failed.Should().BeTrue("a dead PTT lead is a fault, not the station saying no");
        outcome.Refusal.Should().Contain("the PTT lead has gone");

        rig.Keying.Should().Equal(["key"], "the keyup threw, so there was nothing to unkey");
        rig.Errors.Should().ContainSingle()
            .Which.Should().Be("tx test: failed: the PTT lead has gone");

        // The page is told, and told something that puts the button back.
        rig.Reports.Select(r => r.State).Should().Equal(["running", "failed"]);
        rig.Reports[^1].Text.Should().Contain("the PTT lead has gone");
        rig.Records.Should().BeEmpty("nothing went out, so nothing is written down");

        // And the runner is free again rather than stuck holding a run that never ended.
        ptt.Failure = null;
        (await rig.Runner.RunAsync(new TxTestRequest(true, 0, 1))).Ran.Should().BeTrue();
    }

    [Fact]
    public async Task A_Radio_Another_Station_Is_Holding_Is_A_Refusal_Rather_Than_A_Fault()
    {
        // The arbitrated Flex. The transmitter's own keyup catch calls this an outcome and not a
        // broken radio, and the operator's answer is the same sentence the journal carries - so
        // it reads as the station saying no, not as something to retry blindly.
        var ptt = new ThrowingPtt
        {
            Failure = new InvalidOperationException("another station holds the PA"),
        };
        await using var rig = new Rig(ptt: ptt);

        TxTestOutcome outcome = await rig.Runner.RunAsync(new TxTestRequest(true, 0, 1));

        outcome.Ran.Should().BeFalse();
        outcome.Failed.Should().BeFalse();
        outcome.Refusal.Should().Contain("holds the PA");
        rig.Errors.Should().ContainSingle()
            .Which.Should().Be("tx test: refused, another station holds the PA");
        rig.Reports.Select(r => r.State).Should().Equal(["running", "refused"]);
        rig.Keying.Should().Equal(["key"]);
    }

    [Fact]
    public async Task The_Same_Refusal_Reaches_The_Journal_Once_A_Minute_And_The_Caller_Every_Time()
    {
        // With "enabled": false, or with no PTT, a page or a script can ask over and over. Every
        // caller gets its answer; the journal gets one line and a count, which is what the
        // transmit-drop suppressor already does for the same reason.
        var clock = new FakeTimeProvider();
        await using var rig = new Rig(o => o with
        {
            Time = clock,
            Refusal = "\"txTest\".\"enabled\" is false in this station's configuration",
        });

        for (int i = 0; i < 20; i++)
        {
            TxTestOutcome refused = await rig.Runner.RunAsync(new TxTestRequest(true, 0, 1));
            refused.Refusal.Should().Contain("enabled");
        }

        rig.Errors.Should().ContainSingle("twenty refusals are one line, not twenty");
        rig.Reports.Should().HaveCount(20, "but every caller is still answered");

        clock.Advance(TimeSpan.FromMinutes(2));
        await rig.Runner.RunAsync(new TxTestRequest(true, 0, 1));

        rig.Errors.Should().HaveCount(2);
        rig.Errors[1].Should().Contain("and 19 more like it in the last minute");
    }

    [Fact]
    public async Task What_Went_Out_Is_Journalled_And_Written_Down_As_A_Transmission()
    {
        await using var rig = new Rig(o => o with { SubChannel = 3 });

        await rig.Runner.RunAsync(new TxTestRequest(true, 0, 1));

        // Two lines: what was asked for, and what happened. Both plain ASCII, both readable in
        // journalctl under a C locale.
        rig.Lines.Should().HaveCount(2);
        rig.Lines[0].Should().Be("tx test: two-tone 700+1900 Hz, 1.0 s, peak level 0.80");
        rig.Lines[1].Should().StartWith("tx test: done, 1.0 s on air");
        rig.Lines.Should().OnlyContain(line => line.All(char.IsAscii));

        // And a row wherever transmissions are recorded, so an operator watching the public
        // monitor of an uplinked station sees what the burst was.
        TxTestRecord record = rig.Records.Should().ContainSingle().Subject;
        record.SubChannel.Should().Be(3);
        record.AudioHz.Should().Be(1300, "the midpoint of the pair is where the energy is");
        record.Text.Should().Contain("two-tone 700+1900 Hz");
        record.Payload.Should().Equal(System.Text.Encoding.ASCII.GetBytes(record.Text));

        rig.Reports.Select(r => r.State).Should().Equal(["running", "done"]);
    }

    [Fact]
    public async Task The_Control_The_Page_Is_Offered_Carries_The_Presets_And_The_Cap()
    {
        await using var rig = new Rig(o => o with { DefaultSeconds = 7, MaxSeconds = 12 });

        TxTestControl control = rig.Runner.Control;

        control.DefaultSeconds.Should().Be(7);
        control.MaxSeconds.Should().Be(12);
        control.LowToneHz.Should().Be(700);
        control.HighToneHz.Should().Be(1900);
        control.Refusal.Should().BeNull();
        control.Presets.Select(p => p.ToneHz).Should().Equal(500, 999, 1248, 2079);
        control.Presets.Select(p => Math.Round(p.DeviationHz / 100) * 100)
            .Should().Equal(1200, 2400, 3000, 5000);
    }

    [Fact]
    public async Task A_Station_That_Cannot_Transmit_Still_Offers_The_Reason()
    {
        // Shown and disabled, rather than absent: an operator looking for the control learns why
        // it will not work instead of wondering whether their build has it.
        await using var rig = new Rig(o => o with
        {
            Refusal = "no \"ptt\" is configured, so this daemon does not key the radio",
        });

        rig.Runner.Control.Refusal.Should().Contain("no \"ptt\" is configured");
    }
}
