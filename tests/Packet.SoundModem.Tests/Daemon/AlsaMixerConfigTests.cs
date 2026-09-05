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
                  "captureGainPercent": 60,
                  "agc": false,
                  "micBoost": false,
                  "playbackPercent": 70
                }
              }
            }
            """);

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        error.Should().BeEmpty();
        MixerSettings settings = config!.Alsa!.Mixer!.ToSettings();
        settings.CaptureGainPercent.Should().Be(60);
        settings.Agc.Should().BeFalse();
        settings.MicBoost.Should().BeFalse();
        settings.PlaybackPercent.Should().Be(70);
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

        settings.CaptureGainPercent.Should().BeNull();
        settings.MicBoost.Should().BeNull();
        settings.PlaybackPercent.Should().BeNull();

        FakeMixer card = FakeMixer.Cm108();
        int before = card.Find("Mic")!.Capture!.Value;
        MixerSetup.Apply(card, settings);

        card.Find("Mic")!.Capture.Should().Be(before, "only the AGC was named");
        card.Find("Auto Gain Control")!.On.Should().BeFalse();
    }

    [Theory]
    [InlineData("captureGainPercent", 101)]
    [InlineData("captureGainPercent", -1)]
    [InlineData("playbackPercent", 250)]
    public void A_Percentage_Outside_Nought_To_A_Hundred_Is_A_Configuration_Error(string key, int value)
    {
        string path = WriteConfig(
            "{\"device\": \"plughw:1,0\", \"alsa\": {\"mixer\": {\""
            + key + "\": " + value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "}}}");

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().BeNull("a bad value stops start-up with exit 2, as every other one does");
        error.Should().Contain($"\"alsa\".\"mixer\".\"{key}\" is {value}");
        error.Should().Contain("use 0-100");
        error.Should().Contain(path, "the operator has to know which file to edit");
        error.Should().NotContain("Exception", "a stack trace is not an explanation");
    }

    [Fact]
    public void Nought_And_A_Hundred_Are_Both_Allowed()
    {
        string path = WriteConfig("""
            {"device": "plughw:1,0", "alsa": {"mixer": {"captureGainPercent": 0, "playbackPercent": 100}}}
            """);

        DaemonConfig.TryLoad(path, out string error).Should().NotBeNull();
        error.Should().BeEmpty();
    }

    [Fact]
    public void A_Mixer_On_A_Flexradio_Is_Refused_Rather_Than_Silently_Ignored()
    {
        string path = WriteConfig("""
            {"device": "flex:discover", "alsa": {"mixer": {"captureGainPercent": 60}}}
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
            {"device": "plughw:1,0", "alsa": {"mixer": {"micGain": 60}, "cardName": "x"}}
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
            {"device": "plughw:1,0", "alsa": {"mixer": {"captureGainPercent": 60, "agc": false}}}
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
