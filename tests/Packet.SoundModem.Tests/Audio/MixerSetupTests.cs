using Packet.SoundModem.Audio;

namespace Packet.SoundModem.Tests.Audio;

/// <summary>
/// Finding a card's controls, setting them in dB against the card's own range, reading them back,
/// and what the journal says about each - all of it against a made-up card, because a CI runner
/// has no sound hardware and this is the layer that has to be right whatever hardware turns up.
/// </summary>
/// <remarks>
/// What is deliberately not here is the P/Invoke in <see cref="AlsaMixer"/>, which needs a card.
/// Everything that decides <em>what</em> to do to a mixer is here; the only thing left to prove
/// on hardware is that libasound does what its header says.
/// </remarks>
public class MixerSetupTests
{
    /// <summary>A capture side with the bench CM108's numbers: 36 steps of one whole dB.</summary>
    private static FakeLevel Cm108Capture(long raw = 20) =>
        new() { Min = 0, Max = 35, Raw = raw, MinDb = -12, MaxDb = 23 };

    [Fact]
    public void The_First_Control_Name_That_The_Card_Has_Is_The_One_Used()
    {
        // A card that calls it "Mic Capture", second in the fallback list.
        var mixer = new FakeMixer(
            "hw:1",
            new FakeControl { Name = "Capture", Capture = Cm108Capture(raw: 4) },
            new FakeControl { Name = "Mic Capture", Capture = Cm108Capture(raw: 4) });

        MixerReport report = MixerSetup.Apply(mixer, new MixerSettings { CaptureGainDb = 6 });

        report.Capture!.Control.Should().Be(
            "Mic Capture", "\"Mic Capture\" comes before \"Capture\" in the fallback list");
        mixer.CaptureDb("Mic Capture").Should().Be(6);
        mixer.CaptureDb("Capture").Should().Be(
            -8, "the control that was not chosen is untouched");
    }

    [Fact]
    public void A_Control_Name_From_The_Configuration_Beats_The_Built_In_List()
    {
        var mixer = new FakeMixer(
            "hw:1",
            new FakeControl { Name = "Mic", Capture = Cm108Capture(raw: 4) },
            new FakeControl { Name = "Line In Gain", Capture = Cm108Capture(raw: 4) });

        MixerSetup.Apply(mixer, new MixerSettings
        {
            CaptureGainDb = 3,
            CaptureControls = ["Line In Gain"],
        });

        mixer.CaptureDb("Line In Gain").Should().Be(3);
        mixer.CaptureDb("Mic").Should().Be(-8, "the operator named the other one");
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
    public void What_Was_Set_Is_Read_Back_From_The_Card_And_Journalled_With_The_Range()
    {
        var mixer = new FakeMixer(
            "hw:1",
            new FakeControl { Name = "Mic", Capture = Cm108Capture(raw: 0) },
            new FakeControl { Name = "Auto Gain Control", On = true },
            new FakeControl { Name = "Mic Boost", On = true });

        var journal = new List<string>();
        MixerReport report = MixerSetup.Apply(
            mixer,
            new MixerSettings { CaptureGainDb = 6, Agc = false, MicBoost = false },
            journal.Add);

        report.Summary.Should().Be(
            "alsa: mixer: Mic capture 6.00 dB of -12.00 to 23.00 dB (set 6.00 dB), "
            + "Auto Gain Control off, Mic Boost off");
        journal.Should().Contain(report.Summary!, "the summary is journalled, not just returned");
        mixer.Refreshes.Should().BeGreaterThan(
            0, "the card is asked for fresh values before it is read back");
    }

    /// <summary>
    /// The range is quoted beside the value on a pure read-back too, which is what
    /// <c>--mixer-show</c> and <c>GET /api/mixer</c> serve.
    /// </summary>
    [Fact]
    public void A_Read_Back_Quotes_The_Cards_Range_Beside_Every_Level()
    {
        MixerReport report = MixerSetup.Apply(FakeMixer.Cm108(), new MixerSettings());

        report.Summary.Should().Be(
            "alsa: mixer: Mic capture 8.00 dB of -12.00 to 23.00 dB, Auto Gain Control on, "
            + "Speaker playback -20.00 dB of -37.00 to 0.00 dB");
        report.Capture!.MinDb.Should().Be(-12);
        report.Capture.MaxDb.Should().Be(23);
        report.Playback!.MinDb.Should().Be(-37);
        report.Playback.MaxDb.Should().Be(0);
    }

    [Fact]
    public void A_Card_That_Quantises_A_Level_Has_Both_Figures_Journalled()
    {
        // The bench CM108's capture is whole dB and nothing between, so 6.4 dB is not a level it
        // can hold: what the operator asked for and what the card did are different numbers and
        // the line prints both.
        var mixer = new FakeMixer("hw:3", new FakeControl { Name = "Mic", Capture = Cm108Capture() });

        MixerReport report = MixerSetup.Apply(mixer, new MixerSettings { CaptureGainDb = 6.4 });

        report.Capture!.Decibels.Should().Be(6, "the read-back is the card's answer, not ours");
        report.Summary.Should().Be(
            "alsa: mixer: Mic capture 6.00 dB of -12.00 to 23.00 dB (set 6.40 dB)");
    }

    /// <summary>
    /// A card that publishes only raw steps cannot be set in dB, and says so rather than having a
    /// number invented for it.
    /// </summary>
    /// <remarks>
    /// The alternative was to convert the dB into a percentage of the raw range, which would be
    /// setting a level against a mapping the card never published - an operator asking for 6 dB
    /// would get whatever 6-of-something happened to land on. Refused, journalled, carried on.
    /// </remarks>
    [Fact]
    public void A_Control_With_No_Db_Scale_Is_Refused_In_Words_And_Left_Alone()
    {
        var mixer = new FakeMixer(
            "hw:5",
            new FakeControl { Name = "Mic", Capture = new FakeLevel { Max = 15, Raw = 6 } });

        var journal = new List<string>();
        MixerReport report = MixerSetup.Apply(
            mixer, new MixerSettings { CaptureGainDb = 6 }, journal.Add);

        journal.Should().Contain(
            "alsa: mixer: \"Mic\" on hw:5 has no dB scale, so captureGainDb cannot be set - this "
            + "card publishes only raw steps. The control is left exactly as the card has it.");
        mixer.Find("Mic")!.Capture!.Raw.Should().Be(6, "nothing was written to the card");
        report.Capture!.Decibels.Should().BeNull();
        report.Capture.MinDb.Should().BeNull();
        report.Summary.Should().Be(
            "alsa: mixer: Mic capture 40% (no dB scale) (left as found)",
            "the percentage is all there is to report, and it says which unit it is in");
    }

    [Fact]
    public void A_Level_Outside_The_Cards_Range_Is_Refused_With_The_Range_And_The_Rest_Carries_On()
    {
        FakeMixer mixer = FakeMixer.Cm108();

        var journal = new List<string>();
        MixerReport report = MixerSetup.Apply(
            mixer, new MixerSettings { CaptureGainDb = 30, Agc = false }, journal.Add);

        journal.Should().Contain(
            "alsa: mixer: captureGainDb 30.00 dB is outside the range of \"Mic\" on hw:3, which "
            + "is -12.00 to 23.00 dB. The control is left exactly as the card has it.");
        mixer.CaptureDb("Mic").Should().Be(8, "the card keeps the level it had");
        report.Agc!.On.Should().BeFalse("the rest of the request still happened");
    }

    [Theory]
    [InlineData(-12.0)]
    [InlineData(23.0)]
    public void Both_Ends_Of_The_Cards_Range_Are_Inside_It(double decibels)
    {
        FakeMixer mixer = FakeMixer.Cm108();

        var journal = new List<string>();
        MixerSetup.Apply(mixer, new MixerSettings { CaptureGainDb = decibels }, journal.Add);

        journal.Should().NotContain(line => line.Contains("outside the range", StringComparison.Ordinal));
        mixer.CaptureDb("Mic").Should().Be(decibels);
    }

    /// <summary>
    /// The API asks before it acts, because there is somebody waiting for an answer and nothing
    /// has been touched yet - unlike start-up, which journals and carries on.
    /// </summary>
    [Fact]
    public void The_Same_Two_Refusals_Can_Be_Asked_For_Before_Anything_Is_Touched()
    {
        FakeMixer card = FakeMixer.Cm108();

        MixerSetup.WhyRefused(card, new MixerSettings { CaptureGainDb = 30 })
            .Should().Contain("-12.00 to 23.00 dB");
        MixerSetup.WhyRefused(card, new MixerSettings { PlaybackDb = 6 })
            .Should().Contain("-37.00 to 0.00 dB");
        MixerSetup.WhyRefused(card, new MixerSettings { CaptureGainDb = 6, PlaybackDb = -8 })
            .Should().BeNull("both are inside the card's ranges");
        MixerSetup.WhyRefused(card, new MixerSettings { Agc = false })
            .Should().BeNull("a switch has no dB scale to be outside of");

        card.CaptureDb("Mic").Should().Be(8, "asking never writes to the card");
    }

    [Fact]
    public void A_Control_With_No_Db_Scale_Is_Refused_Before_Anything_Is_Touched_Too()
    {
        var card = new FakeMixer(
            "hw:5", new FakeControl { Name = "Mic", Capture = new FakeLevel { Max = 15, Raw = 6 } });

        MixerSetup.WhyRefused(card, new MixerSettings { CaptureGainDb = 6 })
            .Should().Contain("has no dB scale");
    }

    [Fact]
    public void A_Control_The_Card_Does_Not_Have_Is_Skipped_And_Said_So()
    {
        var mixer = new FakeMixer("hw:1", new FakeControl { Name = "Mic", Capture = Cm108Capture() });

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
        var mixer = new FakeMixer("hw:3", new FakeControl { Name = "Mic", Capture = Cm108Capture() });

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
        MixerSetup.Apply(mixer, new MixerSettings { CaptureGainDb = 6 }, journal.Add);

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
            new MixerSettings { CaptureGainDb = 6, CaptureControls = ["Auto Gain Control"] },
            journal.Add);

        journal.Should().Contain(
            "alsa: mixer: \"Auto Gain Control\" on hw:1 has no capture volume, skipped");
        report.Capture.Should().BeNull();
    }

    [Fact]
    public void A_Boost_That_Is_A_Level_Rather_Than_A_Switch_Is_Driven_To_Its_Ends()
    {
        // An HDA "Mic Boost" is four 10 dB steps, not an on/off. On means the top of it - and it
        // stays a switch in the configuration, because +20 dB ahead of everything is on or off
        // and there is no dB figure for an operator to type.
        var mixer = new FakeMixer(
            "hw:0",
            new FakeControl { Name = "Mic Boost", Capture = new FakeLevel { Max = 3, Raw = 0 } });

        var journal = new List<string>();
        MixerReport report = MixerSetup.Apply(
            mixer, new MixerSettings { MicBoost = true }, journal.Add);

        journal.Should().Contain(
            "alsa: mixer: \"Mic Boost\" on hw:0 is a level rather than a switch, set to 100%");
        mixer.Find("Mic Boost")!.Capture!.Raw.Should().Be(3);
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

        report.Capture!.Decibels.Should().Be(8);
        report.Capture.Percent.Should().Be(57, "which is what alsamixer shows for the same step");
        mixer.CaptureDb("Mic").Should().Be(8, "a read-back changes nothing on the card");
        mixer.Find("Auto Gain Control")!.On.Should().BeTrue();
        report.Summary.Should().NotContain(
            "left as found", "a pure read-back describes the card and stops there");
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

    /// <summary>
    /// Where each applied value came from is journalled beside it, so a start-up log answers "why
    /// is the gain 6 dB" without anybody having to open two files to find out.
    /// </summary>
    [Fact]
    public void Every_Applied_Value_Says_Which_Source_Put_It_There()
    {
        FakeMixer mixer = FakeMixer.Cm108();

        MixerReport report = MixerSetup.Apply(
            mixer,
            new MixerSettings
            {
                CaptureGainDb = 6,
                Agc = false,
                Sources = new MixerSources(
                    CaptureGain: MixerSource.Config, Agc: MixerSource.StateFile),
            });

        report.Summary.Should().Be(
            "alsa: mixer: Mic capture 6.00 dB of -12.00 to 23.00 dB (set 6.00 dB, config), "
            + "Auto Gain Control off (state file), "
            + "Speaker playback -20.00 dB of -37.00 to 0.00 dB (left as found)");
        report.Sources.CaptureGain.Should().Be(
            MixerSource.Config, "the report carries it through to the API and the page");
    }

    [Fact]
    public void The_Bench_Cm108_Takes_A_Gain_And_An_Agc_And_Skips_The_Boost_It_Has_Not_Got()
    {
        // The whole of #17 on the card it was asked for, in one case: capture gain set in dB and
        // read back with the range, AGC switched off, mic boost asked for and not there, Speaker
        // level set.
        FakeMixer mixer = FakeMixer.Cm108();

        var journal = new List<string>();
        MixerReport report = MixerSetup.Apply(
            mixer,
            new MixerSettings
            {
                CaptureGainDb = 6,
                Agc = false,
                MicBoost = false,
                PlaybackDb = -8,
            },
            journal.Add);

        journal.Should().BeEquivalentTo(
            [
                "alsa: mixer: hw:3 has Mic, Auto Gain Control, Speaker",
                "alsa: mixer: no control named \"Mic Boost\" on hw:3 (also tried "
                    + "\"Mic Boost (+20dB)\", \"Internal Mic Boost\", \"Mic Capture Boost\"), skipped",
                "alsa: mixer: Mic capture 6.00 dB of -12.00 to 23.00 dB (set 6.00 dB), "
                    + "Auto Gain Control off, "
                    + "Speaker playback -8.00 dB of -37.00 to 0.00 dB (set -8.00 dB)",
            ],
            options => options.WithStrictOrdering());
        report.MicBoost.Should().BeNull();
        report.Agc!.On.Should().BeFalse();
        mixer.PlaybackDb("Speaker").Should().Be(-8);
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
            new MixerSettings { CaptureGainDb = 6, Agc = false },
            journal.Add,
            out string why);

        report.Should().BeNull();
        why.Should().Contain("EntryPointNotFoundException")
            .And.Contain("snd_mixer_selem_set_capture_dB_all",
                "a genuine fault has to stay visible rather than be swallowed");
        journal.Should().ContainSingle(line => line.Contains(
            "capture gain, AGC and mic boost are left as the card has them", StringComparison.Ordinal));
    }

    [Fact]
    public void A_Card_That_Answers_Normally_Comes_Back_From_The_Guarded_Apply_Too()
    {
        MixerReport? report = MixerSetup.TryApply(
            FakeMixer.Cm108(), new MixerSettings { CaptureGainDb = 6 }, null, out string why);

        why.Should().BeEmpty();
        report!.Capture!.Decibels.Should().Be(6);
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
            new FakeControl { Name = "AGC", Capture = Cm108Capture() });

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
            new MixerSettings { CaptureGainDb = 6, CaptureControls = ["Capture"] },
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
