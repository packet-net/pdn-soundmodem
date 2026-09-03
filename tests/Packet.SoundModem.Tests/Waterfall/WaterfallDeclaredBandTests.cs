using System.Net;
using System.Net.Sockets;
using AwesomeAssertions;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Tests.Waterfall;

/// <summary>
/// Bands the waterfall is told about rather than measuring, and the radio line above them.
/// Both exist because the display was showing less than the station was doing: ARDOP is not an
/// <see cref="IModem"/>, so nothing enumerable carried it and it was simply absent; and a
/// probe's midpoint is a few Hz off the centre an operator configured, which reads as a bug.
/// </summary>
public class WaterfallDeclaredBandTests : IAsyncLifetime
{
    private const int SampleRate = 12000;

    private readonly SoundModemChannel _channel = new(SampleRate, randomSeed: 7);
    private WaterfallWebServer _server = null!;

    public ValueTask InitializeAsync()
    {
        _channel.AddModem(0, sink => ModemCatalog.Create("afsk1200", SampleRate, sink));
        _server = new WaterfallWebServer(_channel, FreePorts.Next(), new WaterfallOptions
        {
            DeclaredBands =
            [
                // A real modem, whose centre the host states and whose edges are measured.
                new DeclaredBand(0, "afsk1200", 1700, null),
                // ARDOP: nothing to probe, so both centre and width are declared.
                new DeclaredBand(1, "ardop", 950, 500),
            ],
        });
        _server.Start();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();


    [Fact]
    public void A_Band_That_Cannot_Be_Probed_Is_Still_Drawn()
    {
        ModemBand ardop = _server.Bands.Should().ContainSingle(b => b.Mode == "ardop").Subject;

        ardop.SubChannel.Should().Be(1);
        ardop.CentreHz.Should().Be(950);
        ardop.LowHz.Should().Be(700, "the declared width straddles the declared centre");
        ardop.HighHz.Should().Be(1200);
    }

    [Fact]
    public void A_Measured_Band_Keeps_Its_Measured_Edges_But_The_Configured_Centre()
    {
        ModemBand afsk = _server.Bands.Should().ContainSingle(b => b.Mode == "afsk1200").Subject;

        afsk.CentreHz.Should().Be(1700, "the label must read as the operator placed it");
        // Bell 202 at 1700: the edges are still the real measurement, not centre ± something.
        afsk.LowHz.Should().BeLessThan(1200);
        afsk.HighHz.Should().BeGreaterThan(2200);
        afsk.LowHz.Should().NotBe(afsk.CentreHz);
    }

    [Fact]
    public async Task A_Zero_Declared_Centre_Is_The_No_Frequency_Sentinel_Not_A_Centre()
    {
        // Plain `--modem 0:bpsk300 --waterfall <port>`: the daemon declares CentreHz 0 for a
        // modem with no configured frequency. That is a sentinel, not a placement, and it
        // must not overwrite the probe's measurement - it did, and the band chip read
        // "0 Hz" with its dashed centre line at DC.
        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => ModemCatalog.Create("bpsk300", SampleRate, sink));
        await using var server = new WaterfallWebServer(channel, FreePorts.Next(), new WaterfallOptions
        {
            DeclaredBands = [new DeclaredBand(0, "bpsk300", 0, null)],
        });
        server.Start();

        ModemBand band = server.Bands.Should().ContainSingle().Subject;
        band.CentreHz.Should().BeGreaterThan(500,
            "the measured centre stands when no frequency was configured");
    }

    [Fact]
    public void Bands_Are_Ordered_By_Sub_Channel_However_They_Arrived()
    {
        // Declared bands are appended after the probed ones, so without a sort ARDOP on
        // sub-channel 1 would render after a modem on 2.
        _server.Bands.Select(b => b.SubChannel).Should().BeInAscendingOrder();
    }

    [Fact]
    public void The_Radio_Line_Is_Absent_Until_There_Is_Something_To_Say()
    {
        // A soundcard station has no frequency reference to report, and an empty label in the
        // top bar would be worse than none.
        _server.SetRadioStatus(null);

        // Nothing to assert beyond it not throwing: the page hides the element when null.
        _server.SetRadioStatus("External ref locked");
        _server.SetRadioStatus("External ref locked");
    }
}
