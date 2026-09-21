using AwesomeAssertions;
using Packet.SoundModem.CarrierSense;
using Packet.SoundModem.Daemon;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// The station's <c>carrierSense</c> section, from the file to the source the audio channel reads.
/// </summary>
/// <remarks>
/// <b>Every test here is about the same property: this must never stop a station working.</b> The
/// source is opened during daemon start-up, so anything that throws does not cost carrier sense,
/// it costs the station its whole service. And anything that wrongly reports busy silences the
/// transmitter, which is the failure this bench has already met once.
/// </remarks>
public class StationCarrierSenseTests : IDisposable
{
    private readonly List<string> _said = [];

    public void Dispose() => ChannelBusySources.Host = null;

    private bool _audioFallback;

    private IChannelBusySource? Open(CarrierSenseConfig? config, out IDisposable? owned) =>
        StationCarrierSense.Open(config, _said.Add, out owned, out _audioFallback);

    [Fact]
    public void No_Section_And_No_Station_File_Opens_Nothing_And_Says_Nothing()
    {
        ChannelBusySources.Host = null;

        IChannelBusySource? source = Open(null, out IDisposable? owned);

        source.Should().BeNull();
        owned.Should().BeNull();
        _said.Should().BeEmpty(
            "a station that never asked for this should not be told about it at start-up");
    }

    [Fact]
    public void A_Host_That_Owns_The_Radio_Wins_And_Is_Not_Disposed_From_Here()
    {
        // In-process under packet.net the host already holds the serial link. Opening it a second
        // time would at best fail and at worst fight the owner for the port, and disposing it from
        // here would take the host's own radio away.
        var host = new StubSource(true);
        ChannelBusySources.Host = host;

        IChannelBusySource? source = Open(
            new CarrierSenseConfig { Port = "/dev/ttyUSB0" }, out IDisposable? owned);

        source.Should().BeSameAs(host);
        owned.Should().BeNull("the host owns it");
        _said.Should().ContainSingle().Which.Should().Contain("host");
    }

    [Fact]
    public void Radio_None_Switches_The_Section_Off_Without_Deleting_It()
    {
        IChannelBusySource? source = Open(
            new CarrierSenseConfig { Radio = "none", Port = "/dev/ttyUSB0" },
            out IDisposable? owned);

        source.Should().BeNull();
        owned.Should().BeNull();
        _said.Should().ContainSingle().Which.Should().Contain("none");
    }

    [Fact]
    public void A_Section_With_No_Port_Opens_Nothing_And_Says_So()
    {
        // Said out loud rather than passed over: an operator who wrote the section meant to get
        // carrier sense, and a silent nothing is how a whole feature goes missing unnoticed.
        IChannelBusySource? source = Open(new CarrierSenseConfig(), out IDisposable? owned);

        source.Should().BeNull();
        owned.Should().BeNull();
        _said.Should().ContainSingle().Which.Should().Contain("port");
    }

    [Fact]
    public void A_Port_That_Is_Not_There_Costs_Carrier_Sense_And_Nothing_Else()
    {
        // The property that matters most. A modem is built while the daemon starts, so an
        // unopenable port has to produce a source with no opinion rather than an exception.
        var config = new CarrierSenseConfig
        {
            Port = $"/dev/nonexistent-{Guid.NewGuid():N}",
            BusyAboveDbm = -75,
        };

        IChannelBusySource? source = Open(config, out IDisposable? owned);

        source.Should().NotBeNull();
        source!.Busy.Should().BeNull("no opinion, which the station treats as clear");
        owned.Should().NotBeNull("this call opened it, so this call's caller closes it");
        owned!.Dispose();
    }

    [Fact]
    public void Carrier_Detect_Alone_Is_Called_Out_As_The_Cold_Start_It_Is()
    {
        // The radio's carrier-detect line reports nothing at all until the first squelch edge
        // after unsolicited reporting is enabled, and a data station holds its squelch open, so
        // on a quiet channel that edge may never come. A station configured that way should be
        // told rather than left wondering why nothing ever reads busy.
        var config = new CarrierSenseConfig { Port = $"/dev/nonexistent-{Guid.NewGuid():N}" };

        Open(config, out IDisposable? owned)?.Busy.Should().BeNull();
        owned?.Dispose();

        _said.Should().Contain(line => line.Contains("busyAboveDbm"));
    }

    [Fact]
    public void A_Station_With_A_Radio_Does_Not_Also_Read_The_Audio()
    {
        // A measurement beats an inference, and running both would only give the inference a way
        // to overrule the measurement.
        var config = new CarrierSenseConfig
        {
            Port = $"/dev/nonexistent-{Guid.NewGuid():N}",
            BusyAboveDbm = -75,
        };

        Open(config, out IDisposable? owned);
        owned?.Dispose();

        _audioFallback.Should().BeFalse();
    }

    [Fact]
    public void A_Station_With_No_Radio_Reads_The_Audio_Unless_Told_Not_To()
    {
        // On by default, because the alternative is not neutral: without it an FM station
        // consults an energy detector that reads clear through every transmission and busy for
        // about ten seconds after it. Measured on this bench, 9.9 s to air against 0.5 s on a
        // quiet channel.
        Open(null, out _);
        _audioFallback.Should().BeTrue("no section at all still means an FM station gets it");

        Open(new CarrierSenseConfig(), out _);
        _audioFallback.Should().BeTrue("a section with no port has no radio either");

        Open(new CarrierSenseConfig { AudioFallback = false }, out _);
        _audioFallback.Should().BeFalse("and an operator can still say no");
    }

    private sealed class StubSource(bool? busy) : IChannelBusySource
    {
        public bool? Busy => busy;
    }
}
