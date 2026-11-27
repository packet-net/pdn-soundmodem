using AwesomeAssertions;
using M0LTE.OfdmFm;

namespace Packet.SoundModem.Tests.Modems.OfdmFmStation;

/// <summary>
/// Two stations, each on its own real channel, joined by a test air: what one radio plays is what the other hears, sample for sample, on one timeline (<see cref="StationAir.Radiated"/>).
/// This is the proof that the indices and the audio are right end to end: a datagram only arrives if the burst that left the first station is the burst the second decodes,
/// and the replies only line up if each station's count matches what its radio really did.
/// </summary>
public class OfdmFmStationHostTwoStationTests
{
    [Fact]
    public async Task A_datagram_to_everyone_is_received_at_the_other_end_and_the_reply_is_addressed()
    {
        await using var a = new StationRig(seed: 11);
        await using var b = new StationRig(seed: 12);
        var timeline = new Lock();
        a.Air.Timeline = timeline;
        b.Air.Timeline = timeline;
        a.Air.Radiated = chunk => b.Channel.ProcessReceive(chunk);
        b.Air.Radiated = chunk => a.Channel.ProcessReceive(chunk);
        a.Host.SetIdentity("G4ABC").Should().Be(IdentityResult.Accepted);
        b.Host.SetIdentity("M0XYZ").Should().Be(IdentityResult.Accepted);
        a.StartTransmitter();
        b.StartTransmitter();
        LinkAddress addressA = LinkAddress.FromAscii("G4ABC-1");
        LinkAddress addressB = LinkAddress.FromAscii("M0XYZ-1");

        byte[] hello = new byte[48];
        new Random(21).NextBytes(hello);
        a.Host.Send(hello, LinkAddress.Everyone, addressA, LinkProtocol.NoLayer3, new HostTag(1)).Should().Be(SendResult.Accepted);
        await StationRig.PumpAsync(() => b.Received.Length >= 1, "the datagram to reach the other station", a, b);

        (byte[] Datagram, uint FromKey, Addressing Addressing) heard = b.Received.Should().ContainSingle().Subject;
        heard.Datagram.Should().Equal(hello);
        heard.Addressing.Should().Be(Addressing.ToEveryone);
        heard.FromKey.Should().Be(addressA.Key, "the datagram says who it is from");

        // The reply goes to the address the first one came from, by key alone, which the second station can answer now that it has heard the first.
        byte[] reply = new byte[36];
        new Random(22).NextBytes(reply);
        await StationRig.PumpAsync(() => a.Transmitted.Length >= 1, "the first station to be told its burst left", a, b);
        b.Host.Send(reply, LinkAddress.FromKey(heard.FromKey), addressB, LinkProtocol.NoLayer3, new HostTag(2)).Should().Be(SendResult.Accepted);
        await StationRig.PumpAsync(() => a.Received.Length >= 1 && b.Transmitted.Length >= 1, "the reply to reach the first station", a, b);

        (byte[] Datagram, uint FromKey, Addressing Addressing) answered = a.Received.Should().ContainSingle().Subject;
        answered.Datagram.Should().Equal(reply);
        answered.FromKey.Should().Be(addressB.Key);
        answered.Addressing.Should().NotBe(Addressing.ToEveryone, "the reply is addressed, not a broadcast");
        a.Transmitted.Should().ContainSingle();
        b.Transmitted.Should().ContainSingle();
    }

    [Fact]
    public async Task Each_station_identifies_after_its_first_transmission_and_the_other_hears_its_callsign()
    {
        await using var a = new StationRig(seed: 11);
        await using var b = new StationRig(seed: 12);
        var timeline = new Lock();
        a.Air.Timeline = timeline;
        b.Air.Timeline = timeline;
        a.Air.Radiated = chunk => b.Channel.ProcessReceive(chunk);
        b.Air.Radiated = chunk => a.Channel.ProcessReceive(chunk);
        a.Host.SetIdentity("G4ABC").Should().Be(IdentityResult.Accepted);
        b.Host.SetIdentity("M0XYZ").Should().Be(IdentityResult.Accepted);
        a.StartTransmitter();
        b.StartTransmitter();

        a.Host.Send(new byte[40], LinkAddress.Everyone, LinkAddress.FromAscii("G4ABC-1"), LinkProtocol.NoLayer3, new HostTag(1)).Should().Be(SendResult.Accepted);
        b.Host.Send(new byte[40], LinkAddress.Everyone, LinkAddress.FromAscii("M0XYZ-1"), LinkProtocol.NoLayer3, new HostTag(2)).Should().Be(SendResult.Accepted);
        await StationRig.PumpAsync(
            () => a.Identified.Length >= 1 && b.Identified.Length >= 1 && a.MorseSent.Length >= 1 && b.MorseSent.Length >= 1,
            "each station to identify and be heard doing it",
            a,
            b);

        a.Identified.Select(i => i.Callsign).Should().Equal(["M0XYZ"], "the first station heard the second's identification burst");
        b.Identified.Select(i => i.Callsign).Should().Equal(["G4ABC"]);
        a.MorseSent.Should().Equal(["G4ABC"]);
        b.MorseSent.Should().Equal(["M0XYZ"]);
        a.ModemSent.Count(m => m.Kind == ModemSentKind.Identification).Should().Be(1);
        b.ModemSent.Count(m => m.Kind == ModemSentKind.Identification).Should().Be(1);
    }
}
