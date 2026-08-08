using Packet.SoundModem.Modems.OfdmAb;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// The audio-band OFDM machinery, exercised on synthetic geometry.
/// </summary>
/// <remarks>
/// Every profile here is invented. The real OFDM-AB geometry is neither public nor final, and
/// deliberately does not live in this repository (see <see cref="OfdmAbParameters.Synthetic"/>),
/// so these tests prove the machinery rather than any particular waveform: that a symbol survives
/// its own transform, that the constellations map and demap, that timing is found, that the
/// channel estimate corrects a distorted path, and that a bad CRC is refused. All of that is what
/// a specification cannot change.
/// </remarks>
public class OfdmAbModemTests
{
    private static readonly OfdmAbParameters Small = OfdmAbParameters.Synthetic;

    private static byte[] Payload(int length, int seed = 1)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    [Theory]
    [InlineData(OfdmAbConstellation.Bpsk)]
    [InlineData(OfdmAbConstellation.Qpsk)]
    [InlineData(OfdmAbConstellation.Psk8)]
    [InlineData(OfdmAbConstellation.Qam16)]
    [InlineData(OfdmAbConstellation.Qam32)]
    [InlineData(OfdmAbConstellation.Qam64)]
    [InlineData(OfdmAbConstellation.Qam128)]
    [InlineData(OfdmAbConstellation.Qam256)]
    public void A_Burst_Round_Trips_At_Every_Constellation(OfdmAbConstellation constellation)
    {
        var modem = new OfdmAbModem(Small);
        byte[] payload = Payload(32);

        float[] audio = modem.Modulate(payload, constellation);
        OfdmAbBurst? burst = modem.Demodulate(audio);

        burst.Should().NotBeNull();
        burst!.Constellation.Should().Be(constellation);
        burst.Payload.Should().Equal(payload);
    }

    [Fact]
    public void Every_Constellation_Maps_And_Demaps_Every_Symbol_Value()
    {
        foreach (OfdmAbConstellation constellation in Enum.GetValues<OfdmAbConstellation>())
        {
            (float I, float Q)[] points = OfdmAbMapper.Points(constellation);
            points.Should().HaveCount(1 << constellation.BitsPerCarrier());

            for (int value = 0; value < points.Length; value++)
            {
                OfdmAbMapper.Demap(points, points[value].I, points[value].Q)
                    .Should().Be(value, "{0} point {1} must demap to itself", constellation, value);
            }

            // Unit mean power, so a constellation change does not change the drive level.
            double power = points.Sum(p => ((double)p.I * p.I) + ((double)p.Q * p.Q)) / points.Length;
            power.Should().BeApproximately(1.0, 0.02);
        }
    }

    [Fact]
    public void The_Burst_Is_Found_Wherever_It_Sits_In_The_Audio()
    {
        var modem = new OfdmAbModem(Small);
        byte[] payload = Payload(24);
        float[] burstAudio = modem.Modulate(payload, OfdmAbConstellation.Qpsk);

        foreach (int offset in new[] { 0, 7, 133, 1000 })
        {
            var padded = new float[offset + burstAudio.Length + 500];
            burstAudio.CopyTo(padded, offset);

            OfdmAbBurst? burst = modem.Demodulate(padded);

            burst.Should().NotBeNull("a burst offset by {0} samples must still be found", offset);
            burst!.Payload.Should().Equal(payload);
        }
    }

    [Fact]
    public void A_Distorted_Channel_Is_Equalised_By_The_Preamble_Estimate()
    {
        // The whole point of OFDM with a channel estimate: a path that tilts and delays the audio
        // is corrected per subcarrier, and a mode with no equaliser could not read this.
        var modem = new OfdmAbModem(Small);
        byte[] payload = Payload(24);
        float[] audio = modem.Modulate(payload, OfdmAbConstellation.Qam16);

        var distorted = new float[audio.Length];
        float previous = 0;
        for (int n = 0; n < audio.Length; n++)
        {
            // A one-pole tilt plus an echo: frequency-selective, which is exactly what a
            // per-subcarrier estimate handles and a flat correction does not.
            float shaped = (0.6f * audio[n]) + (0.4f * previous);
            previous = audio[n];
            distorted[n] = shaped + (n >= 3 ? 0.25f * audio[n - 3] : 0f);
        }

        OfdmAbBurst? burst = modem.Demodulate(distorted);

        burst.Should().NotBeNull();
        burst!.Payload.Should().Equal(payload);
    }

    [Fact]
    public void A_Robust_Constellation_Tolerates_More_Noise_Than_A_Dense_One()
    {
        // Not a performance claim - the geometry is invented - but the ordering is real and a
        // modem that got it the other way round would be broken. Measured as "how much noise does
        // each still decode through", which is robust to where the absolute levels happen to fall.
        var modem = new OfdmAbModem(Small);
        byte[] payload = Payload(16);

        static float[] Noisy(float[] clean, double sigma, int seed)
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

        double Tolerance(OfdmAbConstellation constellation)
        {
            float[] clean = modem.Modulate(payload, constellation);
            double best = 0;
            foreach (double sigma in new[] { 0.001, 0.002, 0.005, 0.01, 0.02, 0.05, 0.1 })
            {
                bool allSeeds = true;
                for (int seed = 1; seed <= 3 && allSeeds; seed++)
                {
                    byte[]? got = modem.Demodulate(Noisy(clean, sigma, seed))?.Payload;
                    allSeeds = got is not null && got.AsSpan().SequenceEqual(payload);
                }

                if (allSeeds)
                {
                    best = sigma;
                }
            }

            return best;
        }

        double bpsk = Tolerance(OfdmAbConstellation.Bpsk);
        double qam256 = Tolerance(OfdmAbConstellation.Qam256);

        bpsk.Should().BeGreaterThan(qam256,
            "one bit per carrier must ride out noise that eight bits per carrier cannot");
    }

    [Fact]
    public void A_Corrupted_Payload_Is_Refused_Rather_Than_Delivered()
    {
        var modem = new OfdmAbModem(Small);
        byte[] payload = Payload(24);
        float[] audio = modem.Modulate(payload, OfdmAbConstellation.Qpsk);

        // Wreck a run of samples inside the payload symbols, past preamble and header.
        int from = Small.SymbolSamples * 3;
        for (int n = from; n < Math.Min(from + Small.SymbolSamples, audio.Length); n++)
        {
            audio[n] = 0f;
        }

        OfdmAbBurst? burst = modem.Demodulate(audio);

        // Either the burst is rejected outright or its payload is refused by the CRC, but a
        // wrong payload must never be handed up as good.
        (burst?.Payload).Should().BeNull();
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(13)]
    [InlineData(14)]
    public void A_Profile_Works_Whatever_The_Parity_Of_Its_First_Carrier(int firstCarrier)
    {
        // A regression guard for a bug the committed synthetic profile could never have caught.
        // The sync symbol repeats over half a symbol only on EVEN absolute bins; an odd one
        // anti-repeats, so an odd first carrier used to produce two sign-flipped halves and a
        // correlation peaking at -1 where the search looked for +1. The signal looked perfectly
        // healthy and decoded to nothing, and it only appeared when the geometry changed under a
        // profile whose first carrier happened to be even.
        var modem = new OfdmAbModem(Small with { FirstCarrier = firstCarrier });
        byte[] payload = Payload(24);

        OfdmAbBurst? burst = modem.Demodulate(modem.Modulate(payload, OfdmAbConstellation.Qam16));

        burst.Should().NotBeNull("first carrier {0}", firstCarrier);
        burst!.Payload.Should().Equal(payload);
    }

    [Fact]
    public void Silence_Decodes_To_Nothing()
    {
        var modem = new OfdmAbModem(Small);

        modem.Demodulate(new float[Small.SymbolSamples * 6]).Should().BeNull();
    }

    [Fact]
    public void A_Profile_That_Does_Not_Fit_Its_Transform_Is_Refused_With_A_Reason()
    {
        var tooMany = Small with { DataCarriers = Small.FftSize };

        Action build = () => new OfdmAbModem(tooMany);

        build.Should().Throw<InvalidOperationException>().WithMessage("*do not fit*");
    }

    [Fact]
    public void The_Committed_Profile_Is_Synthetic_And_Says_So()
    {
        // A guard against the real geometry being pasted in here one day. The numbers we know
        // unofficially are nowhere near these, and they belong in an untracked local file.
        Small.SampleRate.Should().Be(8000);
        Small.FftSize.Should().Be(128);
        OfdmAbParameters.LoadLocal(Path.Combine(Path.GetTempPath(), "no-such-file.json"))
            .Should().BeNull("a missing local profile file is normal, not an error");
    }
}
