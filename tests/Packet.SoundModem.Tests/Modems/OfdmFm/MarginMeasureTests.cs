using Packet.SoundModem.Modems.OfdmFm;
namespace Packet.SoundModem.Tests.Modems.OfdmFm;

/// <summary>
/// The margin measure a rate decision reads: how much of the code's correcting power a burst spent.
/// </summary>
/// <remarks>
/// These are the properties that make it a measure at all. What it is worth in decibels, and
/// whether one value means the same thing on every rate, is a measurement and lives in
/// <see cref="MarginMeasureProbe"/>; what is pinned here is that it exists, that it is zero when
/// nothing is wrong, that it grows when something is, and that it is absent where there is nothing
/// to measure. Any of those breaking silently would leave a station adapting on a number that had
/// stopped meaning anything.
/// </remarks>
public class MarginMeasureTests
{
    private static readonly OfdmFmParameters Coded = OfdmFmParameters.Synthetic with
    {
        Coding = new OfdmFmCoding(OfdmFmFec.Convolutional, 7, 1, 2, true),
    };

    private static byte[] Payload(int length, int seed = 1)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static float[] Noisy(float[] clean, double sigma, int seed)
    {
        var random = new Random(seed);
        var noisy = new float[clean.Length];
        for (int n = 0; n < clean.Length; n++)
        {
            double u1 = 1.0 - random.NextDouble();
            double u2 = random.NextDouble();
            double gauss = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            noisy[n] = (float)(clean[n] + (sigma * gauss));
        }

        return noisy;
    }

    [Fact]
    public void A_Burst_That_Met_No_Noise_Spent_None_Of_Its_Coding()
    {
        var codec = new OfdmFmBurstCodec(Coded);
        byte[] payload = Payload(48);

        OfdmFmBurst? burst = codec.Demodulate(
            codec.Modulate(payload, OfdmFmConstellation.Qpsk));

        burst.Should().NotBeNull();
        burst!.Payload.Should().Equal(payload);
        burst.PreFecBitErrorRate.Should().Be(
            0, "a clean path gives the decoder nothing to correct, so the measure must read zero "
            + "rather than some small offset that would eat into every margin calculation");
    }

    [Fact]
    public void A_Burst_That_Met_Noise_Spent_More_Of_Its_Coding_Than_One_That_Met_Less()
    {
        // The property the whole scheme rests on: it has to move in the right direction before it
        // is worth calibrating. Both noise levels are inside what the code recovers from, so both
        // bursts still decode and the comparison is between two measurements rather than between a
        // measurement and a failure. QAM-16 rather than QPSK because on this geometry QPSK goes
        // from spending nothing to failing outright over one step of noise, and a measure needs
        // somewhere in between to be read at.
        var codec = new OfdmFmBurstCodec(Coded);
        byte[] payload = Payload(48);
        float[] clean = codec.Modulate(payload, OfdmFmConstellation.Qam16);

        OfdmFmBurst? quiet = codec.Demodulate(Noisy(clean, 0.05, 7));
        OfdmFmBurst? loud = codec.Demodulate(Noisy(clean, 0.08, 7));

        quiet!.Payload.Should().Equal(payload);
        loud!.Payload.Should().Equal(payload);
        loud.PreFecBitErrorRate.Should().BeGreaterThan(
            quiet.PreFecBitErrorRate!.Value,
            "more noise must show as more of the code being spent, or the measure is "
            + "not reading the link at all");
    }

    [Fact]
    public void An_Uncoded_Burst_Has_No_Pre_Fec_Rate_To_Report()
    {
        // Null rather than zero, and the difference matters. There is no decoder to disagree with,
        // so re-encoding reproduces the demodulator's own decisions exactly; reporting zero would
        // say "perfect link" on a burst that might have been at the edge of failing.
        var codec = new OfdmFmBurstCodec(OfdmFmParameters.Synthetic with { Coding = null });
        byte[] payload = Payload(48);

        OfdmFmBurst? burst = codec.Demodulate(
            codec.Modulate(payload, OfdmFmConstellation.Qpsk));

        burst!.Payload.Should().Equal(payload);
        burst.PreFecBitErrorRate.Should().BeNull();
    }

    [Fact]
    public void The_Measure_Survives_A_Burst_Coded_Differently_From_The_Receiver()
    {
        // It is computed with the codec the header named, not the receiver's own, which is the
        // whole point of signalling the rate. Measured against a receiver deliberately configured
        // for a different code.
        var receiver = new OfdmFmBurstCodec(OfdmFmParameters.Synthetic with
        {
            Coding = new OfdmFmCoding(OfdmFmFec.Convolutional, 9, 3, 4, true),
        });
        var sender = new OfdmFmBurstCodec(Coded);
        byte[] payload = Payload(48);

        OfdmFmBurst? burst = receiver.Demodulate(
            sender.Modulate(payload, OfdmFmConstellation.Qpsk));

        burst!.Payload.Should().Equal(payload);
        burst.Coding.Should().Be(Coded.Codes);
        burst.PreFecBitErrorRate.Should().Be(0);
    }
}
