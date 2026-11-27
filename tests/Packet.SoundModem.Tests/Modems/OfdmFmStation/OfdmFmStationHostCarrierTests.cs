using AwesomeAssertions;
using M0LTE.OfdmFm;

namespace Packet.SoundModem.Tests.Modems.OfdmFmStation;

/// <summary>
/// The carrier indication: given to the station only from a source that reads the radio, once per heard block, and only when the answer changes. The first call, set or clear, switches the station to short slots
/// for as long as it has an indication, so what is checked is exactly what it is given and when, and what the station does with it.
/// </summary>
public class OfdmFmStationHostCarrierTests
{
    private static readonly LinkAddress Me = LinkAddress.FromAscii("G4ABC-1");

    private sealed class Calls
    {
        private readonly Lock _gate = new();
        private readonly List<bool?> _values = [];
        private int _whileKeyed;

        public StationRig? Rig { get; set; }

        public bool?[] Values { get { lock (_gate) { return [.. _values]; } } }

        public int WhileKeyed => Volatile.Read(ref _whileKeyed);

        public void Add(bool? value)
        {
            lock (_gate)
            {
                _values.Add(value);
            }

            if (Rig?.Air.Keyed == true)
            {
                Interlocked.Increment(ref _whileKeyed);
            }
        }
    }

    private static void Identify(StationRig rig) => rig.Host.SetIdentity("G4ABC").Should().Be(IdentityResult.Accepted);

    [Fact]
    public async Task The_source_is_read_each_block_and_the_station_is_told_only_when_the_answer_changes_including_when_it_stops_knowing()
    {
        var radio = new FakeRadio();
        var calls = new Calls();
        await using var rig = new StationRig(radio: radio, afterExternalBusy: calls.Add);
        rig.WaitForTransmitter = false; // nothing transmits here, and the channel reads busy for as long as the source says so

        await rig.FeedAsync(3);
        calls.Values.Should().BeEmpty("a source that does not know gives the station nothing, and nothing is null to withdraw");

        radio.Set(true);
        await rig.FeedAsync(4);
        calls.Values.Should().Equal([true], "four blocks of the same answer are one call");

        radio.Set(false);
        await rig.FeedAsync(2);
        radio.Set(true);
        await rig.FeedAsync(2);
        calls.Values.Should().Equal([true, false, true]);

        radio.Set(null);
        await rig.FeedAsync(3);
        calls.Values.Should().Equal([true, false, true, null], "the source stopped knowing, so the indication is withdrawn once");

        radio.Set(false);
        await rig.FeedAsync(1);
        calls.Values.Should().Equal([true, false, true, null, false]);
    }

    [Fact]
    public async Task A_busy_source_holds_the_stations_transmission_until_it_clears()
    {
        var radio = new FakeRadio();
        radio.Set(true);
        await using var rig = new StationRig(radio: radio);
        Identify(rig);
        rig.StartTransmitter();
        rig.WaitForTransmitter = false; // the channel reads busy for as long as the source says so; nothing is keyed meanwhile
        rig.Host.Send(new byte[30], LinkAddress.Everyone, Me, LinkProtocol.NoLayer3, new HostTag(1)).Should().Be(SendResult.Accepted);

        await rig.FeedAsync(60 * 50);
        rig.Requests.Should().BeEmpty("a minute of carrier and the station asked for nothing");
        rig.Air.KeyAttempts.Should().Be(0);

        radio.Set(false);
        rig.WaitForTransmitter = true;
        await rig.PumpAsync(() => rig.Transmitted.Length >= 1, "the datagram to go once the carrier cleared");
    }

    [Fact]
    public async Task A_source_that_stops_knowing_releases_the_hold()
    {
        var radio = new FakeRadio();
        radio.Set(true);
        await using var rig = new StationRig(radio: radio);
        Identify(rig);
        rig.StartTransmitter();
        rig.WaitForTransmitter = false;
        rig.Host.Send(new byte[30], LinkAddress.Everyone, Me, LinkProtocol.NoLayer3, new HostTag(1)).Should().Be(SendResult.Accepted);
        await rig.FeedAsync(30 * 50);
        rig.Requests.Should().BeEmpty();

        radio.Set(null);
        rig.WaitForTransmitter = true;
        await rig.PumpAsync(() => rig.Transmitted.Length >= 1, "the datagram to go once the source no longer said busy");
    }

    [Fact]
    public async Task The_source_is_not_read_while_the_radio_is_keyed()
    {
        var radio = new FakeRadio();
        radio.Set(false);
        var calls = new Calls();
        await using var rig = new StationRig(radio: radio, afterExternalBusy: calls.Add);
        calls.Rig = rig;
        // The radio's own carrier shows on its squelch line while it transmits, and is gone when it unkeys.
        rig.Air.OnKey = () => radio.Set(true);
        rig.Air.OnUnkey = () => radio.Set(false);
        Identify(rig);
        rig.StartTransmitter();
        rig.Host.Send(new byte[30], LinkAddress.Everyone, Me, LinkProtocol.NoLayer3, new HostTag(1)).Should().Be(SendResult.Accepted);

        await rig.PumpAsync(() => rig.Air.Keyups.Count >= 1, "the station to key");
        await rig.IdleAsync();
        await rig.FeedAsync(2);

        calls.WhileKeyed.Should().Be(0, "the station is never told anything while its own radio is keyed");
        calls.Values.Should().Equal([false], "the line showed busy only during the keyup, and the station was never told");
    }

    [Fact]
    public async Task With_no_radio_source_the_station_is_never_given_an_indication()
    {
        var calls = new Calls();
        await using var rig = new StationRig(afterExternalBusy: calls.Add);
        Identify(rig);
        rig.StartTransmitter();
        rig.Host.Send(new byte[30], LinkAddress.Everyone, Me, LinkProtocol.NoLayer3, new HostTag(1)).Should().Be(SendResult.Accepted);

        await rig.PumpAsync(() => rig.Transmitted.Length >= 1, "the datagram to go");

        calls.Values.Should().BeEmpty();
    }

    [Fact]
    public async Task The_audio_guess_is_not_a_radio_source_and_the_station_is_never_given_an_indication_from_it()
    {
        var calls = new Calls();
        await using var rig = new StationRig(audioFallback: true, afterExternalBusy: calls.Add);
        rig.Channel.BusySource.Should().BeOfType<Packet.SoundModem.CarrierSense.FmShapeBusyDetector>("the channel fell back to the guess");
        Identify(rig);
        rig.StartTransmitter();
        rig.Host.Send(new byte[30], LinkAddress.Everyone, Me, LinkProtocol.NoLayer3, new HostTag(1)).Should().Be(SendResult.Accepted);

        await rig.PumpAsync(() => rig.Transmitted.Length >= 1, "the datagram to go");

        calls.Values.Should().BeEmpty();
    }
}
