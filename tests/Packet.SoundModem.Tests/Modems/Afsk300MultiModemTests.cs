using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// Frequency-diversity 300 baud HF AFSK: <see cref="Afsk300MultiModem"/> runs tight-filtered
/// branches at stepped centres so a station well off the slot centre (real 40 m stations
/// measured up to ±35 Hz off, and dial conventions vary further) decodes on whichever branch
/// fits. These tests show the bank decoding off-frequency signals a single modem misses,
/// deduping to one frame per transmission, and reporting the winning branch's offset. The
/// interference case that motivates the tight branch filters is guarded separately by
/// <see cref="OffAirAfsk300Tests"/> against a real off-air capture.
/// </summary>
public class Afsk300MultiModemTests
{
    private const int SampleRate = 12000;

    private static readonly byte[] Frame =
        Convert.FromHexString("968264888AAEE4969668908A9465B8CF303132333435363738");

    private static float[] OffTune(int offsetHz, Afsk300Framing framing = Afsk300Framing.Il2pCrc)
    {
        // A transmitter whose audio centre sits offsetHz from the 1700 Hz channel centre -
        // how an off-dial station arrives - with a realistic 240 ms preamble, padded with
        // lead-in/out silence.
        var transmitter = new Afsk300Modem(SampleRate, _ => { }, framing, 1700 + offsetHz);
        float[] audio = transmitter.Modulate(Frame, txDelayMilliseconds: 240);
        int pad = SampleRate / 2;
        var padded = new float[audio.Length + 2 * pad];
        audio.CopyTo(padded, pad);
        return padded;
    }

    [Theory]
    [InlineData(-160)]
    [InlineData(-130)]
    [InlineData(130)]
    [InlineData(160)]
    public void An_Off_Frequency_Signal_A_Single_Modem_Misses_Decodes_On_The_Bank(int offsetHz)
    {
        float[] audio = OffTune(offsetHz);

        var single = new List<byte[]>();
        new Afsk300Modem(SampleRate, single.Add).Process(audio);
        single.Should().BeEmpty("a single centred modem cannot follow a {0} Hz offset", offsetHz);

        var banked = new List<byte[]>();
        new Afsk300MultiModem(SampleRate, banked.Add).Process(audio);
        banked.Should().ContainSingle("the bank has a branch within tolerance of {0} Hz", offsetHz)
            .Which.Should().Equal(Frame);
    }

    [Fact]
    public void An_On_Frequency_Signal_Is_Emitted_Exactly_Once()
    {
        var frames = new List<byte[]>();
        new Afsk300MultiModem(SampleRate, frames.Add).Process(OffTune(0));

        frames.Should().ContainSingle("branches decode the same transmission but dedupe to one")
            .Which.Should().Equal(Frame);
    }

    [Theory]
    [InlineData(Afsk300Framing.Ax25)]
    [InlineData(Afsk300Framing.Il2p)]
    public void The_Other_Framings_Round_Trip_Through_The_Bank(Afsk300Framing framing)
    {
        var frames = new List<byte[]>();
        new Afsk300MultiModem(SampleRate, frames.Add, framing).Process(OffTune(70, framing));

        frames.Should().ContainSingle().Which.Should().Equal(Frame);
    }

    [Theory]
    [InlineData(-160)]
    [InlineData(-140)]
    [InlineData(-105)]
    [InlineData(-70)]
    [InlineData(-35)]
    [InlineData(-17)]   // equidistant between two branches: neither is "the" branch
    [InlineData(0)]
    [InlineData(17)]
    [InlineData(35)]
    [InlineData(70)]
    [InlineData(105)]
    [InlineData(140)]
    [InlineData(160)]
    public void The_Reported_Offset_Is_A_Measurement_Of_The_Signal(int offsetHz)
    {
        // It used to be a branch index, and it read like one: this bank emitted the first branch
        // to finish, which on a clean signal is a branch well below the carrier rather than the
        // one nearest it, so a signal exactly on frequency reported −105 Hz and the number moved
        // with buffer framing. Now every branch measures the carrier against its own centre (the
        // slicer's envelope midpoint is that offset - see AfskDemodulator.CarrierOffsetHz), the
        // best-matched one is chosen on the smallest residual, and branch + residual is the
        // answer. Measured across the bank the worst error is ~2 Hz, so the whole span is pinned
        // rather than one convenient point: a per-step regression would show up here as a ~35 Hz
        // jump, which is what a return to naming branches would look like.
        var qualities = new List<FrameQuality>();
        var modem = new Afsk300MultiModem(SampleRate, _ => { });
        modem.FrameDecoded += (_, quality) => qualities.Add(quality);

        modem.Process(OffTune(offsetHz));

        qualities.Should().ContainSingle()
            .Which.FrequencyOffsetHz.Should().BeApproximately(
                offsetHz, 6, "the bank reports where the station actually was");
    }

    [Fact]
    public void A_Single_Branch_Measures_The_Offset_Too()
    {
        // The measurement belongs to the demodulator, not to the bank - so a lone modem, which
        // reported nothing at all before, now says where the station sat relative to it.
        var qualities = new List<FrameQuality>();
        var modem = new Afsk300Modem(SampleRate, _ => { });
        modem.FrameDecoded += (_, quality) => qualities.Add(quality);

        modem.Process(OffTune(35));

        qualities.Should().ContainSingle()
            .Which.FrequencyOffsetHz.Should().BeApproximately(35, 6);
    }

    [Fact]
    public void Offset_Pairs_Zero_Collapses_To_A_Single_Tight_Branch()
    {
        var centred = new List<byte[]>();
        var modem = new Afsk300MultiModem(SampleRate, centred.Add, offsetPairs: 0);
        modem.Mode.Should().Be("afsk300-il2pc-multi1");
        modem.Process(OffTune(0));
        centred.Should().ContainSingle().Which.Should().Equal(Frame);

        var offTune = new List<byte[]>();
        new Afsk300MultiModem(SampleRate, offTune.Add, offsetPairs: 0).Process(OffTune(160));
        offTune.Should().BeEmpty("one tight branch has no diversity to reach +160 Hz");
    }

    [Fact]
    public void Identical_Content_Later_Is_Not_Deduplicated()
    {
        float[] one = OffTune(0);
        // Same frame content twice, 4 s apart - both must be delivered (the dedupe window is short).
        var audio = new float[one.Length + 4 * SampleRate + one.Length];
        one.CopyTo(audio, 0);
        one.CopyTo(audio, one.Length + 4 * SampleRate);

        var frames = new List<byte[]>();
        new Afsk300MultiModem(SampleRate, frames.Add).Process(audio);

        frames.Should().HaveCount(2);
    }

    [Fact]
    public void Transmit_Uses_The_Centre_And_Round_Trips_Through_A_Single_Modem()
    {
        // A bank TX is the centre branch only, so a plain centred modem must decode it.
        var modem = new Afsk300MultiModem(SampleRate, _ => { });
        float[] audio = modem.Modulate(Frame, txDelayMilliseconds: 240);
        var padded = new float[audio.Length + 2 * (SampleRate / 2)];
        audio.CopyTo(padded, SampleRate / 2);

        var received = new List<byte[]>();
        new Afsk300Modem(SampleRate, received.Add).Process(padded);
        received.Should().ContainSingle().Which.Should().Equal(Frame);
    }
}
