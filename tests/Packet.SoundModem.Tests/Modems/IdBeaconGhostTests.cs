using System.Text;
using AwesomeAssertions;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// The ghost demodulator that hears a NinoTNC's station identification, which it sends in 300
/// AFSK AX.25 alongside its PSK SSB data modes rather than within them.
/// </summary>
public class IdBeaconGhostTests
{
    private const int SampleRate = 12000;

    [Theory]
    [InlineData("bpsk300")]
    [InlineData("bpsk300-multi")]
    [InlineData("bpsk300-nocrc")]
    [InlineData("bpsk1200")]
    [InlineData("bpsk1200-multi")]
    [InlineData("qpsk600")]
    [InlineData("qpsk2400")]
    public void The_Psk_Ssb_Modes_Are_Identified_Alongside_Rather_Than_Within(string mode) =>
        IdBeaconGhost.AppliesTo(mode).Should().BeTrue();

    [Theory]
    // The three the manual names as self-identifying already.
    [InlineData("afsk300")]
    [InlineData("afsk300-il2pc")]
    [InlineData("afsk1200")]
    // An FM mode, whatever its modulation is called: Nino idents the FM side in 1200 AFSK, not
    // 300, so a 300 baud ghost on this one would listen for something that is never sent.
    [InlineData("qpsk3600")]
    [InlineData("fsk9600")]
    [InlineData("c4fsk19200")]
    // Not NinoTNC modes at all.
    [InlineData("freedv-datac1")]
    [InlineData("ms110d-wn4")]
    [InlineData("ardop")]
    public void Everything_Else_Gets_No_Ghost(string mode) =>
        IdBeaconGhost.AppliesTo(mode).Should().BeFalse();

    [Fact]
    public void An_Unconfigured_Modem_Puts_Its_Ghost_On_The_NinoTNC_Beacon_Frequency() =>
        IdBeaconGhost.CentreHzFor(null).Should().Be(1700);

    [Fact]
    public void A_Retuned_Modem_Carries_Its_Ghost_With_It()
    {
        // The offset travels with the transmitting station's audio layout, the absolute frequency
        // does not: tune the data modem 300 Hz down because your dial sits 300 Hz up, and the
        // ident arrives 300 Hz down too.
        IdBeaconGhost.CentreHzFor(1200).Should().Be(1400);
        IdBeaconGhost.CentreHzFor(1800).Should().Be(2000);
    }

    [Theory]
    [InlineData("bpsk300")]
    [InlineData("bpsk1200")]
    [InlineData("qpsk600")]
    [InlineData("qpsk2400")]
    public void The_Catalogue_Still_Centres_These_Modes_Where_The_Offset_Assumes(string mode)
    {
        // IdBeaconGhost.CentreHzFor treats an unconfigured modem as sitting on Nino's 1500 Hz
        // carrier, which is also the catalogue default. Should that default ever move, the ghost
        // would quietly start listening 200 Hz from the wrong place - so the two are pinned
        // together here rather than left to agree by coincidence.
        IModem modem = ModemCatalog.Create(mode, SampleRate, _ => { });
        ModemBandProbe.TryMeasure(modem, SampleRate, out double low, out double high)
            .Should().BeTrue();

        ((low + high) / 2).Should().BeApproximately(
            IdBeaconGhost.NinoPskCarrierHz, 30,
            "the ghost's placement is derived from this carrier");
    }

    [Theory]
    [InlineData(null, 1700)]    // an unconfigured bpsk300: Nino's own layout
    [InlineData(1200.0, 1400)]  // retuned down 300 Hz; the ident follows
    public void A_Beacon_Is_Heard_On_The_Ghost_Its_Base_Modem_Placed(double? baseCentreHz, double expectedCentreHz)
    {
        IdBeaconGhost ghost = IdBeaconGhost.TryCreate(
            subChannel: 3, "bpsk300", baseCentreHz, SampleRate)!;

        ghost.Should().NotBeNull();
        ghost.CentreHz.Should().Be(expectedCentreHz);
        ghost.SubChannel.Should().Be(3, "the label says which modem the ident rode in on");

        var heard = new List<byte[]>();
        ghost.BeaconHeard += (frame, _) => heard.Add(frame);
        ghost.Process(Beacon("KK4HEJ", expectedCentreHz));

        heard.Should().HaveCount(1);
        Ax25AddressParser.TryParse(heard[0], out string source, out string destination)
            .Should().BeTrue();
        source.Should().Be("KK4HEJ");
        destination.Should().Be("IDENT", "a NinoTNC sends its ident to IDENT");
    }

    [Fact]
    public void The_Modem_The_Ghost_Rides_On_Hears_Nothing_Of_It()
    {
        // The whole point: the ident is in a modulation the data modem cannot read. If the PSK
        // modem could decode it there would be no need for any of this - and if it produced
        // anything at all from that audio it would be delivering a phantom frame to the host.
        var delivered = new List<byte[]>();
        IModem psk = ModemCatalog.Create("bpsk300", SampleRate, delivered.Add);
        psk.Process(Beacon("KK4HEJ", IdBeaconGhost.CentreHzFor(null)));

        delivered.Should().BeEmpty();
    }

    [Fact]
    public void A_Ghost_Takes_No_Sub_Channel_And_Does_Not_Hold_The_Channel_Busy()
    {
        // A tap, not a modem: it must not occupy a KISS nibble (beacons are not traffic) and must
        // not assert carrier sense, or its presence would change when the station may transmit.
        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => ModemCatalog.Create("bpsk300", SampleRate, sink));

        IdBeaconGhost ghost = IdBeaconGhost.TryCreate(0, "bpsk300", null, SampleRate)!;
        var heard = new List<byte[]>();
        ghost.BeaconHeard += (frame, _) => heard.Add(frame);
        channel.AddReceiveTap(ghost.Process);

        channel.ProcessReceive(Beacon("KK4HEJ", IdBeaconGhost.CentreHzFor(null)));

        heard.Should().HaveCount(1, "the tap still hears the beacon");
        channel.Modems.Should().HaveCount(1, "the ghost is not addressable");
        channel.CarrierDetect.Should().BeFalse();
        channel.ChannelBusy.Should().BeFalse();
    }

    [Fact]
    public void The_Ghost_Reports_The_Mode_The_Beacon_Was_Sent_In()
    {
        IdBeaconGhost ghost = IdBeaconGhost.TryCreate(0, "qpsk2400", null, SampleRate)!;
        var qualities = new List<FrameQuality>();
        ghost.BeaconHeard += (_, quality) => qualities.Add(quality);
        ghost.Process(Beacon("KK4HEJ", IdBeaconGhost.CentreHzFor(null)));

        qualities.Should().ContainSingle();
        qualities[0].Mode.Should().Be(ghost.Mode);
        ghost.BaseMode.Should().Be("qpsk2400");

        // The display name is what an operator reads, and it is the same either way - the bank's
        // branch count is not something they chose.
        ModeNames.Display(ghost.Mode).Should().Be("AFSK300");
    }

    [Fact]
    public void The_Ghost_Is_The_Diversity_Bank_The_Catalogue_Builds_For_The_Mode()
    {
        // A ghost listens 200 Hz from a PSK carrier by construction, so branch selectivity is
        // load-bearing here rather than incidental: it must be whatever `afsk300` currently
        // means, not a single wide demodulator frozen in at the time this was written.
        IdBeaconGhost ghost = IdBeaconGhost.TryCreate(0, "bpsk300", null, SampleRate)!;
        IModem catalogue = ModemCatalog.Create(
            IdBeaconGhost.BeaconMode, SampleRate, _ => { },
            new ModemOptions(CentreFrequencyHz: IdBeaconGhost.CentreHzFor(null)));

        ghost.Mode.Should().Be(catalogue.Mode);
        ghost.Mode.Should().Contain("multi", "the 300 AFSK family defaults to the bank (#191)");
    }

    [Theory]
    [InlineData(-140)]
    [InlineData(-70)]
    [InlineData(0)]
    [InlineData(70)]
    [InlineData(140)]
    public void A_Beacon_Off_Our_Dial_Is_Still_Heard_Across_The_Bank(double dialErrorHz)
    {
        // The one error this placement really has is a dial that differs from the identifying
        // station's - the offset from their PSK carrier to their ident is fixed inside one TNC
        // and cannot drift. Covering that span is the whole reason the ghost is a bank, so the
        // span is what gets tested, rather than one convenient point in the middle of it.
        IdBeaconGhost ghost = IdBeaconGhost.TryCreate(0, "bpsk300", null, SampleRate)!;
        var qualities = new List<FrameQuality>();
        ghost.BeaconHeard += (_, quality) => qualities.Add(quality);

        ghost.Process(Beacon("KK4HEJ", IdBeaconGhost.CentreHzFor(null) + dialErrorHz));

        qualities.Should().ContainSingle();

        // How far the identifying station's dial sits from ours - a real reading now that the
        // bank measures the carrier rather than naming whichever branch finished first.
        qualities[0].FrequencyOffsetHz.Should().BeApproximately(
            dialErrorHz, 8, "the ident says where the station actually was");
    }

    /// <summary>
    /// A NinoTNC ident as audio: 300 baud AFSK AX.25, <c>call</c>&gt;<c>IDENT</c>, on the tone
    /// pair centred at <paramref name="centreHz"/>. Leading and trailing silence stands in for
    /// the quiet either side of a real burst.
    /// </summary>
    private static float[] Beacon(string call, double centreHz)
    {
        var beacon = new Afsk300Modem(SampleRate, _ => { }, Afsk300Framing.Ax25, centreHz);
        float[] burst = beacon.Modulate(IdentFrame(call), txDelayMilliseconds: 300);

        var audio = new float[SampleRate / 4 + burst.Length + SampleRate / 4];
        burst.CopyTo(audio, SampleRate / 4);
        return audio;
    }

    private static byte[] IdentFrame(string call)
    {
        byte[] text = Encoding.ASCII.GetBytes($"{call} pdn-soundmodem");
        var frame = new byte[16 + text.Length];
        WriteAddress(frame, 0, "IDENT", last: false);
        WriteAddress(frame, 7, call, last: true);
        frame[14] = 0x03;   // UI
        frame[15] = 0xF0;   // no layer 3
        text.CopyTo(frame, 16);
        return frame;

        static void WriteAddress(byte[] frame, int at, string address, bool last)
        {
            for (int n = 0; n < 6; n++)
            {
                frame[at + n] = (byte)((n < address.Length ? address[n] : ' ') << 1);
            }

            frame[at + 6] = (byte)(0x60 | (last ? 1 : 0));
        }
    }
}
