using Packet.SoundModem.Audio;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.Tests.Audio;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// The mixer state file: where it lives, what it holds, and the precedence that decides what a
/// station actually comes up on.
/// </summary>
/// <remarks>
/// <para>Tom, 2026-09-06: "If specified in the config file I'm happy for this value to be applied
/// at startup. If not specified in the config file then persist between runs in some kind of
/// state file." So the order per control is config, then state file, then leave the card exactly
/// as it is - and that ordering is the whole safety property. The state file is written without
/// asking, so it must never be able to override something an operator wrote down on purpose, and
/// it must never invent a setting for a control nobody has ever touched.</para>
/// <para>The config file itself is never written by any of this, which is what lets it stay
/// JSONC full of an operator's comments.</para>
/// </remarks>
public class MixerStateFileTests : IDisposable
{
    private const string Device = "plughw:CARD=Device,DEV=0";

    private readonly string _dir = Directory.CreateTempSubdirectory("pdnsm-mixer-state").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string json)
    {
        string path = Path.Combine(_dir, MixerStateFile.DefaultName);
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void The_Config_File_Wins_Over_The_State_File_Control_By_Control()
    {
        var config = new AlsaMixerConfig { CaptureGainDb = -3, Agc = false };
        var state = new MixerState
        {
            Device = Device,
            CaptureGainDb = 6,
            Agc = true,
            PlaybackDb = -8,
        };

        MixerSettings settings = MixerStateFile.Combine(config, state);

        settings.CaptureGainDb.Should().Be(-3, "the file is the description of the intended station");
        settings.Agc.Should().BeFalse();
        settings.PlaybackDb.Should().Be(-8, "and the state file fills in what the file left out");
        settings.MicBoost.Should().BeNull("neither one mentioned it, so the card keeps it");

        settings.Sources.CaptureGain.Should().Be(MixerSource.Config);
        settings.Sources.Agc.Should().Be(MixerSource.Config);
        settings.Sources.Playback.Should().Be(MixerSource.StateFile);
        settings.Sources.MicBoost.Should().Be(MixerSource.None);
    }

    [Fact]
    public void With_No_State_File_A_Station_Is_Exactly_What_Its_Config_File_Says()
    {
        MixerSettings settings = MixerStateFile.Combine(
            new AlsaMixerConfig { CaptureGainDb = -3 }, state: null);

        settings.CaptureGainDb.Should().Be(-3);
        settings.Agc.Should().BeNull();
        settings.Sources.CaptureGain.Should().Be(MixerSource.Config);
    }

    /// <summary>
    /// The property every station deployed before this existed depends on: no config block, no
    /// state file, nothing written to the card at all.
    /// </summary>
    [Fact]
    public void With_Neither_Nothing_Is_Asked_Of_The_Card()
    {
        MixerSettings settings = MixerStateFile.Combine(config: null, state: null);

        settings.SetsAnything.Should().BeFalse();

        FakeMixer card = FakeMixer.Cm108();
        double? before = card.CaptureDb("Mic");
        MixerSetup.Apply(card, settings);
        card.CaptureDb("Mic").Should().Be(before);
    }

    [Fact]
    public void The_Control_Name_Lists_From_The_Config_File_Survive_The_Combination()
    {
        MixerSettings settings = MixerStateFile.Combine(
            new AlsaMixerConfig { CaptureControls = ["Line In Gain"] },
            new MixerState { Device = Device, CaptureGainDb = 6 });

        settings.CaptureControls.Should().Equal("Line In Gain");
        settings.CaptureGainDb.Should().Be(6);
    }

    [Fact]
    public void A_State_File_Round_Trips_Through_Its_Own_Reader()
    {
        string path = Path.Combine(_dir, MixerStateFile.DefaultName);
        var state = new MixerState
        {
            Device = Device,
            WrittenAt = new DateTimeOffset(2026, 9, 6, 11, 22, 33, TimeSpan.Zero),
            CaptureGainDb = -3.5,
            Agc = false,
        };

        MixerStateFile.TryWrite(path, state, out string why).Should().BeTrue(why);

        MixerState? read = MixerStateFile.TryRead(path, Device, out string ignored);
        ignored.Should().BeEmpty();
        read!.CaptureGainDb.Should().Be(-3.5);
        read.Agc.Should().BeFalse();
        read.MicBoost.Should().BeNull("an absent key stays absent rather than becoming a default");

        // And it is readable by a person, which is half of why it is JSON.
        File.ReadAllText(path).Should().Contain("\"captureGainDb\": -3.5")
            .And.NotContain("micBoost", "a null is left out rather than written as null");
    }

    /// <summary>
    /// A state file written for one card is not applied to the different card that turned up in
    /// its place.
    /// </summary>
    /// <remarks>
    /// A capture level chosen against one interface's audio is not a level for another's, and a
    /// playback level even less so - it is what drives the transmitter. Ignored with a line, not
    /// applied hopefully.
    /// </remarks>
    [Fact]
    public void A_State_File_For_Another_Card_Is_Ignored_And_Said_So()
    {
        string path = Write("""
            {"device": "plughw:1,0", "captureGainDb": 6}
            """);

        MixerState? read = MixerStateFile.TryRead(path, Device, out string ignored);

        read.Should().BeNull();
        ignored.Should().Contain("was written for \"plughw:1,0\" and this station is")
            .And.Contain("a level chosen for one card is not a level for another");
    }

    [Fact]
    public void A_State_File_That_Will_Not_Parse_Is_Ignored_And_Said_So()
    {
        string path = Write("""{"device": "plughw:CARD=Device,DEV=0", "captureGainDb":""");

        MixerState? read = MixerStateFile.TryRead(path, Device, out string ignored);

        read.Should().BeNull();
        ignored.Should().Contain("will not parse").And.Contain("delete it to start again");
    }

    [Fact]
    public void No_State_File_At_All_Is_The_Ordinary_Case_And_Says_Nothing()
    {
        MixerState? read = MixerStateFile.TryRead(
            Path.Combine(_dir, "not-here.json"), Device, out string ignored);

        read.Should().BeNull();
        ignored.Should().BeEmpty("a station that has never had a change made on its page is normal");
    }

    [Fact]
    public void A_State_File_Holding_Nothing_Is_The_Same_As_None()
    {
        string path = Write($$"""{"device": "{{Device}}"}""");

        MixerStateFile.TryRead(path, Device, out string ignored).Should().BeNull();
        ignored.Should().BeEmpty();
    }

    /// <summary>
    /// The whole start-up path in one case: a config file that pins one control, a state file
    /// that holds another, and the card ending up with both and a journal that says which is
    /// which.
    /// </summary>
    [Fact]
    public void Start_Up_Applies_Both_Sources_And_Journals_Where_Each_Came_From()
    {
        string statePath = Write($$"""
            {
              "device": "{{Device}}",
              "writtenAt": "2026-09-06T11:22:33+00:00",
              "captureGainDb": 12,
              "playbackDb": -8
            }
            """);

        FakeMixer card = FakeMixer.Cm108();
        var journal = new List<string>();
        MixerRuntime runtime = MixerRuntime.Start(
            card,
            new AlsaMixerConfig { CaptureGainDb = -3, StateFile = statePath },
            Path.Combine(_dir, "soundmodem.json"), Device, journal.Add, out string why)!;

        why.Should().BeEmpty();
        card.CaptureDb("Mic").Should().Be(-3, "the config file wins for the control it names");
        card.PlaybackDb("Speaker").Should().Be(-8, "and the state file fills in the one it does not");

        journal.Should().Contain(
            $"alsa: mixer: {statePath} holds captureGainDb 12.00 dB, playbackDb -8.00 dB from "
            + "2026-09-06 11:22:33Z");
        runtime.StartUpReport.Summary.Should().Be(
            "alsa: mixer: Mic capture -3.00 dB of -12.00 to 23.00 dB (set -3.00 dB, config), "
            + "Auto Gain Control on (left as found), "
            + "Speaker playback -8.00 dB of -37.00 to 0.00 dB (set -8.00 dB, state file)");
    }

    [Fact]
    public void Start_Up_Journals_The_State_File_Even_When_There_Is_Nothing_In_It_Yet()
    {
        string statePath = Path.Combine(_dir, MixerStateFile.DefaultName);
        var journal = new List<string>();

        MixerRuntime.Start(
            FakeMixer.Cm108(), new AlsaMixerConfig { StateFile = statePath },
            Path.Combine(_dir, "soundmodem.json"), Device, journal.Add, out _);

        journal.Should().Contain(
            $"alsa: mixer: page and API changes are remembered in {statePath} (nothing there yet)",
            "an operator has to be able to find the file before there is one");
    }

    [Fact]
    public void A_State_File_For_Another_Card_Is_Not_Applied_At_Start_Up()
    {
        string statePath = Write("""{"device": "plughw:9,0", "captureGainDb": 20}""");
        FakeMixer card = FakeMixer.Cm108();
        var journal = new List<string>();

        MixerRuntime.Start(
            card, new AlsaMixerConfig { StateFile = statePath },
            Path.Combine(_dir, "soundmodem.json"), Device, journal.Add, out _);

        card.CaptureDb("Mic").Should().Be(8, "the card is left exactly as it was found");
        journal.Should().ContainSingle(line => line.Contains("so it is ignored", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("soundmodem.json", "./mixer-state.json")]
    [InlineData("./soundmodem.json", "./mixer-state.json")]
    [InlineData("/etc/pdn-soundmodem/soundmodem.json", "/etc/pdn-soundmodem/mixer-state.json")]
    public void Without_A_State_Directory_The_File_Sits_Beside_The_Config(string config, string expected)
    {
        // Path.GetDirectoryName("soundmodem.json") is the EMPTY STRING and not null, which is the
        // shape that gave Directory.CreateDirectory("") on the bench in 0.57.0. So a bare file
        // name has to come out as a path with a directory that can be created.
        string path = WithoutStateDirectory(() => MixerStateFile.PathFor(null, config));

        path.Should().Be(expected.Replace('/', Path.DirectorySeparatorChar));
        Path.GetDirectoryName(Path.GetFullPath(path)).Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void The_Systemd_State_Directory_Is_Used_When_There_Is_One()
    {
        // What the packaged unit gives the daemon: StateDirectory=pdn-soundmodem, which systemd
        // creates and chowns to the service user. So the package needs no change at all for this
        // to be writable, and /etc stays read-only under ProtectSystem=full.
        string path = WithStateDirectory("/var/lib/pdn-soundmodem", () =>
            MixerStateFile.PathFor(null, "/etc/pdn-soundmodem/soundmodem.json"));

        path.Should().Be(Path.Combine("/var/lib/pdn-soundmodem", "mixer-state.json"));
    }

    [Fact]
    public void Several_State_Directories_Take_The_First_Which_Is_The_Units_Own()
    {
        string path = WithStateDirectory("/var/lib/pdn-soundmodem:/var/lib/other", () =>
            MixerStateFile.PathFor(null, "/etc/pdn-soundmodem/soundmodem.json"));

        path.Should().Be(Path.Combine("/var/lib/pdn-soundmodem", "mixer-state.json"));
    }

    [Fact]
    public void The_Configuration_Can_Name_The_File_And_That_Beats_Everything()
    {
        string path = WithStateDirectory("/var/lib/pdn-soundmodem", () =>
            MixerStateFile.PathFor("/srv/levels.json", "/etc/pdn-soundmodem/soundmodem.json"));

        path.Should().Be("/srv/levels.json");
    }

    /// <summary>
    /// Runs something with <c>STATE_DIRECTORY</c> set, and puts it back afterwards.
    /// </summary>
    /// <remarks>
    /// Restored in a finally rather than left, because the tests share a process and a leaked
    /// STATE_DIRECTORY would silently move every other test's state file.
    /// </remarks>
    private static string WithStateDirectory(string value, Func<string> what)
    {
        string? before = Environment.GetEnvironmentVariable("STATE_DIRECTORY");
        try
        {
            Environment.SetEnvironmentVariable("STATE_DIRECTORY", value);
            return what();
        }
        finally
        {
            Environment.SetEnvironmentVariable("STATE_DIRECTORY", before);
        }
    }

    private static string WithoutStateDirectory(Func<string> what) => WithStateDirectory("", what);
}
