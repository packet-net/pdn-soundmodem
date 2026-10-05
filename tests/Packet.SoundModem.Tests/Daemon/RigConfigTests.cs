using Packet.SoundModem.Daemon;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// The <c>rig</c> section and <c>"ptt": {"type": "rigctld"}</c>: what loads, and what is refused
/// before anything opens.
/// </summary>
public sealed class RigConfigTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pdnsm-rig").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private (DaemonConfig? Config, string Error) Load(string json)
    {
        string path = Path.Combine(_dir, "soundmodem.json");
        File.WriteAllText(path, json);
        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);
        return (config, error);
    }

    [Fact]
    public void No_Rig_Section_Is_No_Rig_Control()
    {
        (DaemonConfig? config, string error) = Load("""{"device": "null"}""");

        error.Should().BeEmpty();
        config!.Rig.Should().BeNull();
    }

    [Fact]
    public void An_Empty_Rig_Section_Looks_For_Rigctld_On_Its_Own_Port()
    {
        (DaemonConfig? config, string error) = Load(
            """{"device": "null", "rig": {}, "ptt": {"type": "rigctld"}}""");

        error.Should().BeEmpty();
        config!.Rig!.Rigctld.Should().Be("127.0.0.1:4532");
        config.Rig.Required.Should().BeFalse();
        config.Rig.Mode.Should().BeNull();
        config.Rig.PassbandHz.Should().BeNull();
        config.Ptt!.Type.Should().Be("rigctld");
        config.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void Every_Key_Loads()
    {
        (DaemonConfig? config, string error) = Load(
            """{"device": "null", "rig": {"rigctld": "10.0.0.5:4533", "required": true, "mode": "PKTUSB", "passbandHz": 3000}}""");

        error.Should().BeEmpty();
        config!.Rig!.Rigctld.Should().Be("10.0.0.5:4533");
        config.Rig.Required.Should().BeTrue();
        config.Rig.Mode.Should().Be("PKTUSB");
        config.Rig.PassbandHz.Should().Be(3000);
    }

    [Fact]
    public void A_Misspelt_Key_Is_Reported()
    {
        (DaemonConfig? config, _) = Load("""{"device": "null", "rig": {"rigctl": "host:4532"}}""");

        config!.Warnings.Should().ContainSingle(w => w.StartsWith("rig: \"rigctl\" is not a setting", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{"device": "null", "ptt": {"type": "rigctld"}}""", "has no \"rig\" section")]
    [InlineData("""{"device": "flex:10.45.0.76", "rig": {}}""", "A FlexRadio is tuned and keyed")]
    [InlineData("""{"device": "ubersdr:example.org", "dialFrequency": 7049000, "rig": {}}""", "a web receiver")]
    [InlineData("""{"device": "null", "rig": {"rigctld": "host:port"}}""", "is not a TCP port")]
    [InlineData("""{"device": "null", "rig": {"mode": "DIGU"}}""", "is not a mode this station asks a rig for")]
    [InlineData("""{"device": "null", "rig": {"mode": "LSB"}}""", "which is LSB, and \"sideband\" is \"usb\"")]
    [InlineData("""{"device": "null", "sideband": "fm", "rig": {"mode": "PKTUSB"}}""", "which is USB, and \"sideband\" is \"fm\"")]
    [InlineData("""{"device": "null", "rig": {"mode": "CW"}}""", "not a sideband or FM mode")]
    [InlineData("""{"device": "null", "rig": {"passbandHz": 7052000}}""", "\"rig\".\"passbandHz\" is 7052000")]
    public void A_Rig_That_Cannot_Work_Is_Refused(string json, string why)
    {
        (DaemonConfig? config, string error) = Load(json);

        config.Should().BeNull();
        error.Should().Contain(why);
    }

    [Fact]
    public void A_Data_Mode_That_Agrees_With_The_Sideband_Loads()
    {
        (DaemonConfig? config, string error) = Load(
            """{"device": "null", "sideband": "lsb", "rig": {"mode": "pktlsb"}}""");

        error.Should().BeEmpty();
        config!.Rig!.Mode.Should().Be("pktlsb");
    }
}
