using Packet.SoundModem.Audio;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.Tests.Audio;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// The <c>alsa.mixer</c> section: what it parses to, what it refuses, and that a file without
/// one leaves every control on the card alone.
/// </summary>
/// <remarks>
/// The last of those is the property that matters most. Every station already deployed has no
/// such section, and this feature is only safe to ship if those stations behave exactly as they
/// did - which means an absent key writes nothing, not "an absent key writes a default".
/// </remarks>
public class AlsaMixerConfigTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pdnsm-mixer-cfg").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteConfig(string json)
    {
        string path = Path.Combine(_dir, "soundmodem.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void A_Mixer_Block_Parses_To_What_It_Says()
    {
        string path = WriteConfig("""
            {
              "device": "plughw:CARD=Device,DEV=0",
              "alsa": {
                "mixer": {
                  "captureGainDb": 6.0,
                  "agc": false,
                  "micBoost": false,
                  "playbackDb": -8.5
                }
              }
            }
            """);

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        error.Should().BeEmpty();
        MixerSettings settings = config!.Alsa!.Mixer!.ToSettings();
        settings.CaptureGainDb.Should().Be(6.0);
        settings.Agc.Should().BeFalse();
        settings.MicBoost.Should().BeFalse();
        settings.PlaybackDb.Should().Be(-8.5, "a level is a decimal, not a whole number of steps");
    }

    [Fact]
    public void A_File_With_No_Alsa_Section_Asks_For_Nothing_At_All()
    {
        string path = WriteConfig("""{"device": "plughw:1,0"}""");

        DaemonConfig? config = DaemonConfig.TryLoad(path, out _);

        config!.Alsa.Should().BeNull();
        // Which is what every deployed station has, and what it turns into at the mixer.
        new MixerSettings().SetsAnything.Should().BeFalse();
    }

    [Fact]
    public void A_Setting_That_Is_Left_Out_Leaves_That_Control_As_The_Card_Has_It()
    {
        string path = WriteConfig("""
            {"device": "plughw:1,0", "alsa": {"mixer": {"agc": false}}}
            """);

        DaemonConfig? config = DaemonConfig.TryLoad(path, out _);
        MixerSettings settings = config!.Alsa!.Mixer!.ToSettings();

        settings.CaptureGainDb.Should().BeNull();
        settings.MicBoost.Should().BeNull();
        settings.PlaybackDb.Should().BeNull();

        FakeMixer card = FakeMixer.Cm108();
        double? before = card.CaptureDb("Mic");
        MixerSetup.Apply(card, settings);

        card.CaptureDb("Mic").Should().Be(before, "only the AGC was named");
        card.Find("Auto Gain Control")!.On.Should().BeFalse();
    }

    /// <summary>
    /// A dB the card cannot reach is a warning at apply time and not a refusal at load time.
    /// </summary>
    /// <remarks>
    /// Nothing at load time knows what the card's range is - the card is not open yet, and on a
    /// station that has been unplugged it never will be. So the file is accepted, the card is
    /// opened, and the one control that could not be set is journalled with the card's own range
    /// while the others are set normally. Refusing to start over it would take a station off the
    /// air for a level it could have carried on at.
    /// </remarks>
    [Theory]
    [InlineData("captureGainDb", 30)]
    [InlineData("captureGainDb", -40)]
    [InlineData("playbackDb", 12)]
    public void A_Level_The_Card_Cannot_Reach_Loads_And_Is_Refused_At_The_Card(string key, int value)
    {
        string path = WriteConfig(
            "{\"device\": \"plughw:1,0\", \"alsa\": {\"mixer\": {\""
            + key + "\": " + value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "}}}");

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        error.Should().BeEmpty("the range is the card's, and no card is open at load time");
        var journal = new List<string>();
        MixerSetup.Apply(FakeMixer.Cm108(), config!.Alsa!.Mixer!.ToSettings(), journal.Add);

        journal.Should().ContainSingle(line =>
            line.Contains($"{key} {value}.00 dB is outside the range", StringComparison.Ordinal)
            && line.Contains(" dB. The control is left exactly as the card has it.", StringComparison.Ordinal));
    }

    [Fact]
    public void A_Decimal_Level_Survives_The_Round_Trip_Rather_Than_Becoming_A_Whole_Number()
    {
        string path = WriteConfig("""
            {"device": "plughw:1,0", "alsa": {"mixer": {"captureGainDb": -3.5, "playbackDb": 0}}}
            """);

        MixerSettings settings = DaemonConfig.TryLoad(path, out string error)!.Alsa!.Mixer!.ToSettings();

        error.Should().BeEmpty();
        settings.CaptureGainDb.Should().Be(-3.5);
        settings.PlaybackDb.Should().Be(0, "0 dB is a level, not an absent key");
    }

    /// <summary>
    /// The keys 0.57.0 shipped are gone rather than aliased, and a file still carrying one is
    /// told what to write instead.
    /// </summary>
    /// <remarks>
    /// The unit changed as well as the name. <c>"captureGainPercent": 60</c> meant 60% of the
    /// card's raw range; read as dB it would be 37 dB past the top of the bench CM108. So an
    /// alias would have silently set a level nobody asked for on every station that upgraded,
    /// which is precisely the failure a mixer makes inaudible until the decodes stop.
    /// </remarks>
    [Theory]
    [InlineData("captureGainPercent", "captureGainDb")]
    [InlineData("playbackPercent", "playbackDb")]
    public void A_Percentage_Key_From_The_Version_Before_Names_Its_Replacement(string gone, string now)
    {
        string path = WriteConfig(
            "{\"device\": \"plughw:1,0\", \"alsa\": {\"mixer\": {\"" + gone + "\": 60}}}");

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        error.Should().BeEmpty("a stale key is a warning, not a station off the air");
        config!.Alsa!.Mixer!.CaptureGainDb.Should().BeNull("nothing is aliased into the new key");
        config.Alsa.Mixer.PlaybackDb.Should().BeNull();
        config.Warnings.Should().ContainSingle(w => w.Contains(
            $"{gone} is no longer read; use {now}, the card's range is shown by --mixer-show",
            StringComparison.Ordinal));

        // And the generic line as well, so a reader scanning for "IGNORED" still finds it.
        config.Warnings.Should().ContainSingle(w =>
            w.Contains($"\"{gone}\"", StringComparison.Ordinal)
            && w.Contains("IGNORED", StringComparison.Ordinal));
    }

    [Fact]
    public void A_Mixer_On_A_Flexradio_Is_Refused_Rather_Than_Silently_Ignored()
    {
        string path = WriteConfig("""
            {"device": "flex:discover", "alsa": {"mixer": {"captureGainDb": 6}}}
            """);

        DaemonConfig.TryLoad(path, out string error).Should().BeNull();
        error.Should().Contain("which is not a sound card");
        error.Should().Contain("Remove the \"alsa\" section");
    }

    [Fact]
    public void A_Mixer_On_A_Web_Receiver_Is_Refused_For_The_Same_Reason()
    {
        string path = WriteConfig("""
            {"device": "ubersdr:m9psy-1.instance.ubersdr.org", "alsa": {"mixer": {"agc": false}}}
            """);

        DaemonConfig.TryLoad(path, out string error).Should().BeNull();
        error.Should().Contain("which is not a sound card");
    }

    [Fact]
    public void A_Mixer_On_A_Monitor_Is_Refused_Because_A_Monitor_Has_No_Card()
    {
        string path = WriteConfig("""
            {
              "alsa": {"mixer": {"agc": false}},
              "waterfall": {"port": 8099},
              "monitor": {"modems": [{"subChannel": 0, "mode": "afsk1200"}], "receivers": []}
            }
            """);

        DaemonConfig.TryLoad(path, out string error).Should().BeNull();
        error.Should().Contain("A monitor fronts web receivers and has no sound card of its own");
    }

    /// <summary>
    /// A state file aimed at the config file itself is refused at start-up.
    /// </summary>
    /// <remarks>
    /// The one way the "this daemon never writes your config file" promise could be broken. The
    /// first change made on the operator page would replace a hand-written JSONC file, comments
    /// and all, with six lines of levels - and afterwards is too late, because the file it
    /// destroyed was the only copy of what the station was meant to be.
    /// </remarks>
    [Fact]
    public void A_State_File_Aimed_At_The_Config_File_Is_Refused_Before_It_Can_Eat_It()
    {
        string path = WriteConfig("""
            {"device": "plughw:1,0", "alsa": {"mixer": {"stateFile": "soundmodem.json"}}}
            """);

        // Named relatively, from the directory it sits in, which is the shape that would slip
        // past a plain string comparison.
        string was = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_dir);
            DaemonConfig.TryLoad(path, out string error).Should().BeNull();
            error.Should().Contain("which is this configuration file");
            error.Should().Contain("point \"stateFile\" somewhere else");
        }
        finally
        {
            Directory.SetCurrentDirectory(was);
        }
    }

    [Fact]
    public void A_State_File_Somewhere_Else_Is_Perfectly_Ordinary()
    {
        string path = WriteConfig("""
            {"device": "plughw:1,0", "alsa": {"mixer": {"stateFile": "/var/lib/pdn-soundmodem/mixer-state.json"}}}
            """);

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        error.Should().BeEmpty();
        config!.Alsa!.Mixer!.StateFile.Should().Be("/var/lib/pdn-soundmodem/mixer-state.json");
    }

    [Fact]
    public void An_Empty_Alsa_Section_Is_Not_An_Error()
    {
        string path = WriteConfig("""{"device": "plughw:1,0", "alsa": {}}""");

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        error.Should().BeEmpty();
        config!.Alsa!.Mixer.Should().BeNull();
    }

    /// <summary>
    /// A typo has to be said out loud, not merely captured. Capturing it and never surfacing it
    /// is the exact defect the comment above <c>CollectWarnings</c> was written about.
    /// </summary>
    [Fact]
    public void A_Key_The_Daemon_Does_Not_Know_Is_Reported_Rather_Than_Dropped()
    {
        string path = WriteConfig("""
            {"device": "plughw:1,0", "alsa": {"mixer": {"micGain": 6}, "cardName": "x"}}
            """);

        DaemonConfig? config = DaemonConfig.TryLoad(path, out _);

        config!.Alsa!.Mixer!.UnknownSettings.Should().ContainKey(
            "micGain", "a typo must not look like an accepted setting");
        config.Alsa.UnknownSettings.Should().ContainKey("cardName");

        // Which is only worth anything if the operator is told, and start-up prints these.
        config.Warnings.Should().ContainSingle(w => w.Contains("\"micGain\"", StringComparison.Ordinal)
            && w.Contains("alsa mixer", StringComparison.Ordinal)
            && w.Contains("IGNORED", StringComparison.Ordinal));
        config.Warnings.Should().ContainSingle(w => w.Contains("\"cardName\"", StringComparison.Ordinal)
            && w.Contains("alsa:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_File_With_A_Good_Mixer_Block_Warns_About_Nothing()
    {
        string path = WriteConfig("""
            {"device": "plughw:1,0", "alsa": {"mixer": {"captureGainDb": 6, "agc": false}}}
            """);

        DaemonConfig.TryLoad(path, out _)!.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void Control_Name_Overrides_Come_Through_And_An_Empty_List_Falls_Back()
    {
        string path = WriteConfig("""
            {
              "device": "plughw:1,0",
              "alsa": {"mixer": {"captureControls": ["Line In Gain", " "], "agcControls": []}}
            }
            """);

        MixerSettings settings = DaemonConfig.TryLoad(path, out _)!.Alsa!.Mixer!.ToSettings();

        settings.CaptureControls.Should().Equal("Line In Gain");
        settings.AgcControls.Should().BeSameAs(
            MixerSettings.DefaultAgcControls, "an empty list is not a request to look nowhere");
    }

    [Fact]
    public void The_Example_Config_Documents_The_Mixer_Without_Turning_It_On()
    {
        // The shipped example is copied onto real stations. It must show the shape and change
        // nothing: an example that set a capture gain would silently retune every fresh install.
        string example = File.ReadAllText(
            Path.Combine(RepoRoot(), "soundmodem.example.json"));

        example.Should().Contain("\"alsa\"", "the section has to be discoverable in the example");
        example.Should().NotContain(
            "captureGainPercent", "the example must not still teach the key that went away");
        DaemonConfig? config = DaemonConfig.TryLoad(
            WriteConfig(example), out string error);
        error.Should().BeEmpty();
        (config!.Alsa?.Mixer?.ToSettings().SetsAnything ?? false).Should().BeFalse(
            "the example must not set a level on a card it has never heard");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CONFIG.md")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("the tests run inside the repository");
        return dir!.FullName;
    }
}
