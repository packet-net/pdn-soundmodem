using Packet.SoundModem.Modems.OfdmFm;
namespace Packet.SoundModem.Tests.Modems.OfdmFm;

/// <summary>
/// Peak reduction must buy level without breaking the constellations it is meant to help.
/// </summary>
/// <remarks>
/// This waveform is injected past the radio's limiter, so the drive is set once against the burst's
/// loudest instant and post-detection signal to noise goes as deviation squared. A decibel off the
/// crest factor is therefore a decibel of link, for no bandwidth and no air time. The cost is
/// distortion on our own carriers, and these tests pin both halves of that trade.
/// </remarks>
public class PeakReductionTests
{
    [Theory]
    [InlineData(OfdmFmConstellation.Bpsk)]
    [InlineData(OfdmFmConstellation.Qpsk)]
    [InlineData(OfdmFmConstellation.Qam16)]
    [InlineData(OfdmFmConstellation.Qam64)]
    [InlineData(OfdmFmConstellation.Qam128)]
    [InlineData(OfdmFmConstellation.Qam256)]
    public void Every_Constellation_Still_Round_Trips_With_Peaks_Reduced(
        OfdmFmConstellation constellation)
    {
        // The floor on how hard peaks may be pulled down. Measured: 7 dB and 6 dB round trip every
        // constellation, and 5 dB loses QAM-128 and QAM-256, so the default sits at 7 with margin.
        // UNCODED and on the automatic limit, which is the hard case: there is no code to absorb
        // the distortion clipping leaves on our own carriers, so this is what sets the floor for
        // every constellation and what the automatic table was measured against.
        var codec = new OfdmFmBurstCodec(OfdmFmTestProfiles.Narrow);
        var payload = new byte[64];
        new Random(9).NextBytes(payload);

        float[] audio = codec.Modulate(payload, constellation);
        OfdmFmBurst? burst = codec.Demodulate(audio);

        burst.Should().NotBeNull();
        burst!.Payload.Should().Equal(
            payload,
            "peak reduction distorts our own carriers, and a dense constellation is where that "
            + "shows first - if this fails the limit has been set too aggressively");
    }

    [Fact]
    public void The_Crest_Factor_Is_Actually_Reduced()
    {
        OfdmFmParameters profile = OfdmFmTestProfiles.Narrow;
        var payload = new byte[64];
        new Random(1).NextBytes(payload);

        double With = Crest(new OfdmFmBurstCodec(profile), profile, payload);
        double Without = Crest(
            new OfdmFmBurstCodec(profile with { PeakToAverageLimitDb = double.PositiveInfinity }),
            profile,
            payload);

        Without.Should().BeGreaterThan(
            9.5,
            "an untreated OFDM burst of this width sits around 10 dB uncoded and 12 dB coded, the "
            + "coded case being peakier because its bits are more evenly random");
        With.Should().BeLessThan(
            Without - 2.5,
            "the whole point is to take decibels off the crest factor, because on a peak-limited "
            + "transmitter each one is a decibel of link");

        static double Crest(OfdmFmBurstCodec codec, OfdmFmParameters p, byte[] payload)
        {
            float[] audio = codec.Modulate(payload, OfdmFmConstellation.Qpsk);
            double peak = 0;
            double sumSq = 0;
            int from = 2 * p.SymbolSamples;
            for (int n = from; n < audio.Length; n++)
            {
                peak = Math.Max(peak, Math.Abs(audio[n]));
                sumSq += audio[n] * (double)audio[n];
            }

            return 20 * Math.Log10(peak / Math.Sqrt(sumSq / (audio.Length - from)));
        }
    }
}
