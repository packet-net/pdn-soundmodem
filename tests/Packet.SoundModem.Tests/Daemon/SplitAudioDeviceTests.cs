using System.Text.Json.Nodes;
using AwesomeAssertions;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.Tests.Audio;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// A station that receives on one sound card and transmits through another: <c>captureDevice</c>
/// and <c>playbackDevice</c>, what the file may say about them, and the two mixers that follow.
/// </summary>
/// <remarks>
/// The PCM half is two strings handed to two constructors that already took one each, so what is
/// worth pinning is the configuration's refusals and the mixer: a capture gain has to land on the
/// receive card and a playback level on the transmit card, and never the other way round.
/// </remarks>
public class SplitAudioDeviceTests : IDisposable
{
    private const string Receive = "plughw:CARD=Device,DEV=0";
    private const string Transmit = "plughw:CARD=Device_1,DEV=0";

    private readonly string _dir = Directory.CreateTempSubdirectory("pdnsm-split").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteConfig(string json)
    {
        string path = Path.Combine(_dir, "soundmodem.json");
        File.WriteAllText(path, json);
        return path;
    }

    private string StatePath => Path.Combine(_dir, MixerStateFile.DefaultName);

    [Fact]
    public void A_File_With_Neither_Key_Uses_Device_Both_Ways()
    {
        DaemonConfig config = DaemonConfig.Load(WriteConfig($$"""{"device": "{{Receive}}"}"""));

        config.CaptureDeviceInUse.Should().Be(Receive);
        config.PlaybackDeviceInUse.Should().Be(Receive);
    }

    [Fact]
    public void Each_Key_Overrides_Device_For_Its_Own_Direction_Only()
    {
        DaemonConfig transmitMoved = DaemonConfig.Load(WriteConfig(
            $$"""{"device": "{{Receive}}", "playbackDevice": "{{Transmit}}"}"""));
        DaemonConfig receiveMoved = DaemonConfig.Load(WriteConfig(
            $$"""{"device": "{{Receive}}", "captureDevice": "{{Transmit}}"}"""));

        transmitMoved.CaptureDeviceInUse.Should().Be(Receive);
        transmitMoved.PlaybackDeviceInUse.Should().Be(Transmit);
        receiveMoved.CaptureDeviceInUse.Should().Be(Transmit);
        receiveMoved.PlaybackDeviceInUse.Should().Be(Receive);
    }

    [Theory]
    [InlineData("captureDevice", "flex:10.45.0.76")]
    [InlineData("playbackDevice", "ubersdr:example.org")]
    [InlineData("playbackDevice", "pipe:/tmp/in,/tmp/out")]
    public void A_Key_Naming_Something_That_Is_Not_A_Sound_Card_Is_Refused(string key, string value)
    {
        string path = WriteConfig($$"""{"device": "{{Receive}}", "{{key}}": "{{value}}"}""");

        DaemonConfig.TryLoad(path, out string error).Should().BeNull();
        error.Should().Contain($"\"{key}\" is \"{value}\", which is not a sound card");
    }

    [Fact]
    public void A_Split_Beside_A_Device_That_Is_Not_A_Sound_Card_Is_Refused()
    {
        string path = WriteConfig(
            $$"""{"device": "flex:10.45.0.76", "playbackDevice": "{{Transmit}}"}""");

        DaemonConfig.TryLoad(path, out string error).Should().BeNull();
        error.Should().Contain(
            "\"playbackDevice\" is set but \"device\" is \"flex:10.45.0.76\", which is not a sound card");
    }

    [Fact]
    public void A_Split_Beside_A_Monitor_Is_Refused()
    {
        string path = WriteConfig($$"""{"monitor": {}, "captureDevice": "{{Receive}}"}""");

        DaemonConfig.TryLoad(path, out string error).Should().BeNull();
        error.Should().Contain("sets both \"captureDevice\" and \"monitor\"");
    }

    [Fact]
    public void An_Empty_Value_Is_Refused_Rather_Than_Taken_As_Device()
    {
        string path = WriteConfig($$"""{"device": "{{Receive}}", "captureDevice": " "}""");

        DaemonConfig.TryLoad(path, out string error).Should().BeNull();
        error.Should().Contain("\"captureDevice\" is empty");
    }

    [Fact]
    public void Each_Level_Lands_On_Its_Own_Card_And_Nothing_Touches_The_Other()
    {
        FakeMixer receive = FakeMixer.Cm108("hw:1");
        FakeMixer transmit = FakeMixer.Cm108("hw:2");
        var journal = new List<string>();

        MixerReport report = MixerSetup.Apply(
            receive, transmit,
            new MixerSettings { CaptureGainDb = 6, PlaybackDb = -10, ForceAgcAndBoostOff = true },
            journal.Add);

        receive.CaptureDb("Mic").Should().Be(6);
        transmit.PlaybackDb("Speaker").Should().Be(-10);
        receive.PlaybackDb("Speaker").Should().Be(
            -20, "the receive card's own speaker is not the station's transmit level");
        transmit.CaptureDb("Mic").Should().Be(8, "nor is the transmit card's mic its capture gain");
        receive.Find("Auto Gain Control")!.On.Should().BeFalse("AGC is on the receive side");
        transmit.Find("Auto Gain Control")!.On.Should().BeTrue(
            "and the transmit card's AGC does nothing to what the station hears");

        report.Card.Should().Be("hw:1");
        report.PlaybackCard.Should().Be("hw:2");
        journal.Should().Contain("alsa: mixer: hw:1 (receive) has Mic, Auto Gain Control, Speaker");
        journal.Should().Contain("alsa: mixer: hw:2 (transmit) has Mic, Auto Gain Control, Speaker");
    }

    [Fact]
    public void One_Mixer_Passed_Twice_Is_The_One_Card_Station_Exactly()
    {
        var split = new List<string>();
        var single = new List<string>();
        var settings = new MixerSettings { CaptureGainDb = 6, PlaybackDb = -10 };

        FakeMixer twice = FakeMixer.Cm108();
        MixerReport pair = MixerSetup.Apply(twice, twice, settings, split.Add);
        MixerSetup.Apply(FakeMixer.Cm108(), settings, single.Add);

        split.Should().Equal(single);
        pair.PlaybackCard.Should().BeNull();
    }

    [Fact]
    public void A_Playback_Level_Is_Judged_Against_The_Transmit_Cards_Range()
    {
        // The CM108 stops at 0 dB; a card with headroom above it takes +3.
        FakeMixer receive = FakeMixer.Cm108("hw:1");
        var transmit = new FakeMixer(
            "hw:2",
            new FakeControl
            {
                Name = "PCM",
                Playback = new FakeLevel { Min = 0, Max = 26, Raw = 20, MinDb = -20, MaxDb = 6 },
            });

        MixerSetup.WhyRefused(receive, transmit, new MixerSettings { PlaybackDb = 3 })
            .Should().BeNull();
        MixerSetup.WhyRefused(receive, receive, new MixerSettings { PlaybackDb = 3 })
            .Should().Contain("outside the range of \"Speaker\" on hw:1");
    }

    [Fact]
    public void A_Transmit_Card_With_No_Mixer_Costs_Only_The_Transmit_Level()
    {
        FakeMixer receive = FakeMixer.Cm108("hw:1");
        var journal = new List<string>();

        MixerRuntime runtime = MixerRuntime.Start(
            receive, new AbsentMixer("hw:2"),
            new AlsaMixerConfig { CaptureGainDb = 3, PlaybackDb = -6, StateFile = StatePath },
            Path.Combine(_dir, "soundmodem.json"), MixerStateFile.StampFor(Receive, Transmit),
            journal.Add, out string why)!;

        why.Should().BeEmpty();
        receive.CaptureDb("Mic").Should().Be(3);
        receive.PlaybackDb("Speaker").Should().Be(-20, "the receive card is not the transmit card");
        runtime.StartUpReport.Playback.Should().BeNull();
        journal.Should().Contain("alsa: mixer: hw:2 (transmit) has no controls");
    }

    [Fact]
    public void A_Page_Change_Is_Remembered_Against_Both_Devices()
    {
        FakeMixer receive = FakeMixer.Cm108("hw:1");
        FakeMixer transmit = FakeMixer.Cm108("hw:2");
        string stamp = MixerStateFile.StampFor(Receive, Transmit);

        MixerRuntime runtime = MixerRuntime.Start(
            receive, transmit, new AlsaMixerConfig { StateFile = StatePath },
            Path.Combine(_dir, "soundmodem.json"), stamp, _ => { }, out _)!;
        MixerOutcome outcome = runtime.Apply(new MixerChange { PlaybackDb = -12 }, persist: true);

        outcome.Persisted.Should().BeTrue();
        transmit.PlaybackDb("Speaker").Should().Be(-12);
        receive.PlaybackDb("Speaker").Should().Be(-20);
        stamp.Should().Be($"{Receive} (capture), {Transmit} (playback)");
        MixerStateFile.TryRead(StatePath, stamp, out _)!.PlaybackDb.Should().Be(-12);
        MixerStateFile.TryRead(StatePath, Receive, out string ignored).Should().BeNull(
            "a level chosen for the pair is not one for either card on its own");
        ignored.Should().Contain("so it is ignored");
    }

    [Fact]
    public void A_One_Card_Stamp_Is_Unchanged()
    {
        MixerStateFile.StampFor(Receive, Receive).Should().Be(
            Receive, "every state file written before the split existed has to go on matching");
    }

    [Fact]
    public void The_Api_Names_The_Transmit_Card_Whichever_Kind_Of_Station_It_Is()
    {
        FakeMixer one = FakeMixer.Cm108("hw:1");
        JsonObject single = MixerApi.Describe(MixerSetup.Apply(one, new MixerSettings()));
        JsonObject split = MixerApi.Describe(MixerSetup.Apply(
            FakeMixer.Cm108("hw:1"), new FakeMixer("hw:2", new FakeControl { Name = "PCM" }),
            new MixerSettings()));

        single["playbackCard"]!.GetValue<string>().Should().Be("hw:1");
        single["playbackControls"]!.AsArray().Should().HaveCount(3);
        split["card"]!.GetValue<string>().Should().Be("hw:1");
        split["playbackCard"]!.GetValue<string>().Should().Be("hw:2");
        split["playbackControls"]!.AsArray().Select(c => c!.GetValue<string>())
            .Should().Equal("PCM");
    }

    [Fact]
    public void A_Card_That_Will_Not_Open_Is_Named_By_The_Key_That_Chose_It()
    {
        string message = DeviceDiagnostics.Audio(
            Transmit, "/etc/pdn-soundmodem/soundmodem.json", new IOException("No such device"),
            "playbackDevice", split: true);

        message.Should().Contain($"cannot open the sound device \"{Transmit}\"");
        message.Should().Contain("Set by \"playbackDevice\" in /etc/pdn-soundmodem/soundmodem.json");
        message.Should().Contain("the ALSA device the station transmits through");
    }

    [Fact]
    public void Device_Is_Not_Called_Both_Directions_On_A_Station_That_Splits()
    {
        // "playbackDevice" is set, so "device" is only the receive side, and a message saying it
        // is used for both would send the operator to the wrong card.
        string message = DeviceDiagnostics.Audio(
            Receive, "/etc/pdn-soundmodem/soundmodem.json", new IOException("No such device"),
            "device", split: true, capture: true);

        message.Should().Contain("Set by \"device\"");
        message.Should().Contain("the ALSA device the station receives from");
        message.Should().NotContain("both capture and playback");
    }
}
