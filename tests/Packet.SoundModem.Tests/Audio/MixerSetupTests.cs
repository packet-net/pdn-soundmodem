using Packet.SoundModem.Audio;

namespace Packet.SoundModem.Tests.Audio;

/// <summary>
/// Finding a card's controls, setting them, reading them back, and what the journal says about
/// each - all of it against a made-up card, because a CI runner has no sound hardware and this
/// is the layer that has to be right whatever hardware turns up.
/// </summary>
/// <remarks>
/// What is deliberately not here is the P/Invoke in <see cref="AlsaMixer"/>, which needs a card.
/// Everything that decides <em>what</em> to do to a mixer is here; the only thing left to prove
/// on hardware is that libasound does what its header says.
/// </remarks>
public class MixerSetupTests
{
    [Fact]
    public void The_First_Control_Name_That_The_Card_Has_Is_The_One_Used()
    {
        // A card that calls it "Mic Capture", second in the fallback list.
        var mixer = new FakeMixer(
            "hw:1",
            new FakeControl { Name = "Capture", Capture = 10 },
            new FakeControl { Name = "Mic Capture", Capture = 20 });

        MixerReport report = MixerSetup.Apply(
            mixer, new MixerSettings { CaptureGainPercent = 60 });

        report.Capture!.Control.Should().Be(
            "Mic Capture", "\"Mic Capture\" comes before \"Capture\" in the fallback list");
        mixer.Find("Mic Capture")!.Capture.Should().Be(60);
        mixer.Find("Capture")!.Capture.Should().Be(10, "the control that was not chosen is untouched");
    }

    [Fact]
    public void A_Control_Name_From_The_Configuration_Beats_The_Built_In_List()
    {
        var mixer = new FakeMixer(
            "hw:1",
            new FakeControl { Name = "Mic", Capture = 10 },
            new FakeControl { Name = "Line In Gain", Capture = 10 });

        MixerSetup.Apply(mixer, new MixerSettings
        {
            CaptureGainPercent = 40,
            CaptureControls = ["Line In Gain"],
        });

        mixer.Find("Line In Gain")!.Capture.Should().Be(40);
        mixer.Find("Mic")!.Capture.Should().Be(10, "the operator named the other one");
    }

    [Fact]
    public void A_Name_Is_Matched_Whatever_Case_The_Operator_Typed_It_In()
    {
        var mixer = new FakeMixer("hw:1", new FakeControl { Name = "Auto Gain Control", On = true });

        MixerSetup.Apply(mixer, new MixerSettings
        {
            Agc = false,
            AgcControls = ["auto gain CONTROL"],
        });

        mixer.Find("Auto Gain Control")!.On.Should().BeFalse();
    }

    [Fact]
    public void What_Was_Set_Is_Read_Back_From_The_Card_And_Journalled()
    {
        var mixer = new FakeMixer(
            "hw:1",
            new FakeControl { Name = "Mic", Capture = 20 },
            new FakeControl { Name = "Auto Gain Control", On = true },
            new FakeControl { Name = "Mic Boost", On = true });

        var journal = new List<string>();
        MixerReport report = MixerSetup.Apply(
            mixer,
            new MixerSettings { CaptureGainPercent = 75, Agc = false, MicBoost = false },
            journal.Add);

        report.Summary.Should().Be(
            "alsa: mixer: Mic capture 75% (set 75%), Auto Gain Control off, Mic Boost off");
        journal.Should().Contain(report.Summary!, "the summary is journalled, not just returned");
        mixer.Refreshes.Should().BeGreaterThan(
            0, "the card is asked for fresh values before it is read back");
    }

    [Fact]
    public void A_Card_That_Quantises_A_Level_Has_Both_Figures_Journalled()
    {
        // 0-35 raw steps, as the bench CM108 has: 75% is not a step it can hold, so what the
        // operator asked for and what the card did are different numbers and both are printed.
        var mixer = new FakeMixer(
            "hw:3",
            new FakeControl { Name = "Mic", Capture = 20, IgnoresWrites = true });

        MixerReport report = MixerSetup.Apply(
            mixer, new MixerSettings { CaptureGainPercent = 75 });

        report.Capture!.Percent.Should().Be(20, "the read-back is the card's answer, not ours");
        report.Summary.Should().Be("alsa: mixer: Mic capture 20% (set 75%)");
    }

    [Fact]
    public void A_Card_That_Publishes_A_Decibel_Scale_Has_It_Quoted()
    {
        var mixer = new FakeMixer(
            "hw:3",
            new FakeControl { Name = "Mic", Capture = 0, Decibels = percent => -12 + (percent * 0.35) });

        MixerReport report = MixerSetup.Apply(
            mixer, new MixerSettings { CaptureGainPercent = 60 });

        report.Capture!.Decibels.Should().BeApproximately(9.0, 0.001);
        report.Summary.Should().Be("alsa: mixer: Mic capture 60% / 9.00 dB (set 60%)");
    }

    [Fact]
    public void A_Control_The_Card_Does_Not_Have_Is_Skipped_And_Said_So()
    {
        var mixer = new FakeMixer("hw:1", new FakeControl { Name = "Mic", Capture = 40 });

        var journal = new List<string>();
        MixerReport report = MixerSetup.Apply(
            mixer,
            new MixerSettings { Agc = false, AgcControls = ["Auto Gain Control"] },
            journal.Add);

        journal.Should().Contain(
            "alsa: mixer: no control named \"Auto Gain Control\" on hw:1, skipped");
        report.Agc.Should().BeNull("there is no such control to report a state for");
    }

    [Fact]
    public void The_Names_That_Were_Tried_Are_Named_When_There_Was_More_Than_One()
    {
        var mixer = new FakeMixer("hw:3", new FakeControl { Name = "Mic", Capture = 40 });

        var journal = new List<string>();
        MixerSetup.Apply(mixer, new MixerSettings { MicBoost = true }, journal.Add);

        journal.Should().Contain(
            "alsa: mixer: no control named \"Mic Boost\" on hw:3 (also tried "
            + "\"Mic Boost (+20dB)\", \"Internal Mic Boost\", \"Mic Capture Boost\"), skipped");
    }

    [Fact]
    public void A_Control_The_Configuration_Never_Mentioned_Is_Not_Reported_Missing()
    {
        // The bench CM108 has no "Mic Boost". A station that never asked for one must not be told
        // about it on every single start-up: absent key, nothing done, nothing said.
        FakeMixer mixer = FakeMixer.Cm108();

        var journal = new List<string>();
        MixerSetup.Apply(mixer, new MixerSettings { CaptureGainPercent = 57 }, journal.Add);

        journal.Should().NotContain(line => line.Contains("Mic Boost", StringComparison.Ordinal));
    }

    [Fact]
    public void A_Control_That_Is_There_But_Has_No_Such_Volume_Is_Skipped()
    {
        // "Auto Gain Control" is a switch and nothing else, so asking it for a capture level is a
        // different failure from asking for a control that is not there, and reads differently.
        var mixer = new FakeMixer("hw:1", new FakeControl { Name = "Auto Gain Control", On = true });

        var journal = new List<string>();
        MixerReport report = MixerSetup.Apply(
            mixer,
            new MixerSettings { CaptureGainPercent = 50, CaptureControls = ["Auto Gain Control"] },
            journal.Add);

        journal.Should().Contain(
            "alsa: mixer: \"Auto Gain Control\" on hw:1 has no capture volume, skipped");
        report.Capture.Should().BeNull();
    }

    [Fact]
    public void A_Boost_That_Is_A_Level_Rather_Than_A_Switch_Is_Driven_To_Its_Ends()
    {
        // An HDA "Mic Boost" is four 10 dB steps, not an on/off. On means the top of it.
        var mixer = new FakeMixer("hw:0", new FakeControl { Name = "Mic Boost", Capture = 0 });

        var journal = new List<string>();
        MixerReport report = MixerSetup.Apply(
            mixer, new MixerSettings { MicBoost = true }, journal.Add);

        journal.Should().Contain(
            "alsa: mixer: \"Mic Boost\" on hw:0 is a level rather than a switch, set to 100%");
        mixer.Find("Mic Boost")!.Capture.Should().Be(100);
        report.MicBoost!.On.Should().BeTrue();
    }

    [Fact]
    public void A_Card_That_Will_Not_Take_A_Switch_Is_Reported_As_It_Actually_Is()
    {
        var mixer = new FakeMixer(
            "hw:1", new FakeControl { Name = "Auto Gain Control", On = true, IgnoresWrites = true });

        MixerReport report = MixerSetup.Apply(mixer, new MixerSettings { Agc = false });

        report.Agc!.On.Should().BeTrue("the read-back is what the card says, not what it was told");
        report.Summary.Should().Be(
            "alsa: mixer: Auto Gain Control on (set off, the card did not take it)");
    }

    [Fact]
    public void Nothing_Asked_For_Is_Still_A_Read_Back_Of_Everything_Found()
    {
        // Every start-up on a sound card does this, whether or not the file says anything: the
        // log has to record the level the station is actually listening at.
        FakeMixer mixer = FakeMixer.Cm108();

        MixerReport report = MixerSetup.Apply(mixer, new MixerSettings());

        report.Summary.Should().Be(
            "alsa: mixer: Mic capture 57% / 7.95 dB, Auto Gain Control on, "
            + "Speaker playback 46% / -19.98 dB");
        mixer.Find("Mic")!.Capture.Should().Be(57, "a read-back changes nothing on the card");
        mixer.Find("Auto Gain Control")!.On.Should().BeTrue();
    }

    [Fact]
    public void The_Cards_Controls_Are_Listed_Before_Anything_Is_Done_To_Them()
    {
        var journal = new List<string>();
        MixerSetup.Apply(FakeMixer.Cm108(), new MixerSettings(), journal.Add);

        journal[0].Should().Be("alsa: mixer: hw:3 has Mic, Auto Gain Control, Speaker");
    }

    [Fact]
    public void A_Card_With_None_Of_The_Controls_This_Station_Knows_Says_So_And_Carries_On()
    {
        var mixer = new FakeMixer("hw:2", new FakeControl { Name = "Tone Control", On = true });

        var journal = new List<string>();
        MixerReport report = MixerSetup.Apply(mixer, new MixerSettings(), journal.Add);

        report.Summary.Should().BeNull();
        journal.Should().Contain(
            "alsa: mixer: hw:2 has none of the controls this station looks for, so nothing was "
            + "set; capture gain, AGC and mic boost stay as the card has them");
    }

    [Fact]
    public void The_Bench_Cm108_Takes_A_Gain_And_An_Agc_And_Skips_The_Boost_It_Has_Not_Got()
    {
        // The whole of #17 on the card it was asked for, in one case: capture gain set and read
        // back, AGC switched off, mic boost asked for and not there, Speaker level set.
        FakeMixer mixer = FakeMixer.Cm108();

        var journal = new List<string>();
        MixerReport report = MixerSetup.Apply(
            mixer,
            new MixerSettings
            {
                CaptureGainPercent = 60,
                Agc = false,
                MicBoost = false,
                PlaybackPercent = 70,
            },
            journal.Add);

        journal.Should().BeEquivalentTo(
            [
                "alsa: mixer: hw:3 has Mic, Auto Gain Control, Speaker",
                "alsa: mixer: no control named \"Mic Boost\" on hw:3 (also tried "
                    + "\"Mic Boost (+20dB)\", \"Internal Mic Boost\", \"Mic Capture Boost\"), skipped",
                "alsa: mixer: Mic capture 60% / 9.00 dB (set 60%), Auto Gain Control off, "
                    + "Speaker playback 70% / -11.10 dB (set 70%)",
            ],
            options => options.WithStrictOrdering());
        report.MicBoost.Should().BeNull();
        report.Agc!.On.Should().BeFalse();
    }

    /// <summary>
    /// A mixer fault costs the mixer and never the station.
    /// </summary>
    /// <remarks>
    /// The daemon runs this from top-level statements, which have nothing above them to catch
    /// anything, so an EntryPointNotFoundException from any of the twenty entry points the apply
    /// reaches - all of them outside AlsaMixer.TryOpen's catch - would be a crash at every
    /// start-up and a systemd restart loop, over a mixer.
    /// </remarks>
    [Fact]
    public void A_Libasound_Missing_A_Mixer_Symbol_Costs_The_Mixer_And_Not_The_Daemon()
    {
        var journal = new List<string>();

        MixerReport? report = MixerSetup.TryApply(
            new ThrowingMixer(),
            new MixerSettings { CaptureGainPercent = 60, Agc = false },
            journal.Add,
            out string why);

        report.Should().BeNull();
        why.Should().Contain("EntryPointNotFoundException")
            .And.Contain("snd_mixer_selem_set_capture_volume_all",
                "a genuine fault has to stay visible rather than be swallowed");
        journal.Should().ContainSingle(line => line.Contains(
            "capture gain, AGC and mic boost are left as the card has them", StringComparison.Ordinal));
    }

    [Fact]
    public void A_Card_That_Answers_Normally_Comes_Back_From_The_Guarded_Apply_Too()
    {
        MixerReport? report = MixerSetup.TryApply(
            FakeMixer.Cm108(), new MixerSettings { CaptureGainPercent = 60 }, null, out string why);

        why.Should().BeEmpty();
        report!.Capture!.Percent.Should().Be(60);
    }

    /// <summary>
    /// A control the file never mentioned is never the subject of a journal line, whatever is
    /// wrong with it - the same rule as the not-found line, and for the same reason.
    /// </summary>
    [Fact]
    public void A_Control_The_Configuration_Never_Mentioned_Is_Never_Reported_Skipped()
    {
        // "Capture" exists and is a switch and nothing else, so reading a level off it fails.
        // On a station that never mentioned the mixer that must be silent, not a line on every
        // single start-up of every station of this card's model.
        var mixer = new FakeMixer(
            "hw:1",
            new FakeControl { Name = "Capture", On = true },
            new FakeControl { Name = "AGC", Capture = 40 });

        var journal = new List<string>();
        MixerReport report = MixerSetup.Apply(mixer, new MixerSettings(), journal.Add);

        journal.Should().NotContain(line => line.Contains("skipped", StringComparison.Ordinal));
        report.Capture.Should().BeNull();
        report.Agc.Should().NotBeNull("a level standing in for a switch still reads back");
    }

    [Fact]
    public void The_Same_Control_Is_Reported_Skipped_When_The_File_Did_Ask_For_It()
    {
        var mixer = new FakeMixer("hw:1", new FakeControl { Name = "Capture", On = true });

        var journal = new List<string>();
        MixerSetup.Apply(
            mixer,
            new MixerSettings { CaptureGainPercent = 50, CaptureControls = ["Capture"] },
            journal.Add);

        journal.Should().Contain(
            "alsa: mixer: \"Capture\" on hw:1 has no capture volume, skipped");
    }

    [Theory]
    [InlineData("plughw:CARD=Device,DEV=0", "hw:CARD=Device")]
    [InlineData("plughw:1,0", "hw:1")]
    [InlineData("hw:3", "hw:3")]
    [InlineData("sysdefault:CARD=Device", "hw:CARD=Device")]
    [InlineData("default", "default")]
    [InlineData("", "default")]
    public void A_Pcm_Device_Name_Says_Which_Card_To_Mix(string device, string card) =>
        AlsaMixer.CardFor(device).Should().Be(card);
}
