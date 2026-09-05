using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.Modems;
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

        internal Rig(Func<TxTestOptions, TxTestOptions>? settings = null)
        {
            Channel = new SoundModemChannel(Rate, randomSeed: 5);
            Channel.AddModem(0, sink => ModemCatalog.Create("afsk1200", Rate, sink));
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

        internal TxTestRunner Runner { get; }

        internal FakeAudioOutput Output { get; } = new(Rate);

        internal RecordingPtt Ptt { get; } = new();

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
        rig.Ptt.Events.Should().Equal(["key", "unkey"], "the radio is keyed for the test and let go");

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
        rig.Ptt.Events.Should().Equal(["key", "unkey"]);
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
        rig.Ptt.Events.Should().BeEmpty("a refused test never touches the radio");
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
        rig.Ptt.Events.Should().BeEmpty();
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

        rig.Ptt.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task A_Second_Test_Is_Refused_While_One_Is_Running()
    {
        await using var rig = new Rig();

        Task<TxTestOutcome> first = rig.Runner.RunAsync(new TxTestRequest(true, 0, 2));
        TxTestOutcome second;
        do
        {
            second = await rig.Runner.RunAsync(new TxTestRequest(true, 0, 1));
        }
        while (second.Refusal is null && !first.IsCompleted);

        second.Refusal.Should().Be("a test transmission is already running");
        (await first).Ran.Should().BeTrue();
    }

    [Fact]
    public async Task Stopping_A_Test_That_Is_Waiting_For_The_Channel_Sends_Nothing_And_Unkeys()
    {
        // The lost page and the changed mind. The transmission is already queued and cannot be
        // taken back, so what has to be true is that it carries no tone: the keyup happens and
        // PTT is straight back down.
        var inhibited = true;
        await using var rig = new Rig();
        rig.Channel.TransmitInhibit = () => inhibited;

        Task<TxTestOutcome> running = rig.Runner.RunAsync(new TxTestRequest(true, 0, 5));
        while (rig.Reports.Count == 0)
        {
            await Task.Delay(10);
        }

        rig.Runner.Stop();
        inhibited = false;

        TxTestOutcome outcome = await running;
        outcome.Ran.Should().BeFalse("nothing reached the air");
        rig.Output.Snapshot().Should().OnlyContain(sample => sample == 0);
        rig.Ptt.Events.Should().Equal(["key", "unkey"], "PTT is never left up by a cancellation");
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
        rig.Ptt.Events.Should().BeEmpty("the channel is held, so nothing is keyed");

        inhibited = false;
        (await running).Ran.Should().BeTrue();
        rig.Ptt.Events.Should().Equal(["key", "unkey"]);
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
        rig.Channel.TransmitInhibit = () => true;

        Task<TxTestOutcome> running = rig.Runner.RunAsync(new TxTestRequest(true, 0, 1));
        while (rig.Reports.Count == 0)
        {
            await Task.Delay(10);
        }

        clock.Advance(TimeSpan.FromSeconds(30));

        TxTestOutcome outcome = await running.WaitAsync(TimeSpan.FromSeconds(20));
        outcome.Ran.Should().BeFalse();
        outcome.Refusal.Should().Contain("did not clear");
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
