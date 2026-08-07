using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// Unit tests for the workstream-5 MLSE equaliser, feeding synthetic symbol-instant samples
/// directly - the modem-level behaviour (front end, DPLL, deframer) is covered by
/// <see cref="MlseDetectionTests"/>. The emitted bit stream carries an unspecified small
/// delay (the traceback, plus whatever lag the LMS parked the main tap at), so sequence
/// checks look for the payload's differential bits as a contiguous substring rather than
/// pinning an alignment.
/// </summary>
public class MlseEqualiserTests
{
    /// <summary>Builds the absolute polarity sequence for a preamble of
    /// <paramref name="preambleSymbols"/> reversals followed by seeded random payload bits,
    /// returning the polarities and the payload's differential bits (1 = repeat).</summary>
    private static (double[] Polarities, int[] PayloadBits) Sequence(
        int preambleSymbols, int payloadBits, int seed)
    {
        var random = new Random(seed);
        var polarities = new double[preambleSymbols + payloadBits];
        var bits = new int[payloadBits];
        double previous = 1.0;
        for (int i = 0; i < preambleSymbols; i++)
        {
            previous = -previous;
            polarities[i] = previous;
        }

        for (int i = 0; i < payloadBits; i++)
        {
            int bit = random.Next(2);
            bits[i] = bit;
            previous = bit == 1 ? previous : -previous;
            polarities[preambleSymbols + i] = previous;
        }

        return (polarities, bits);
    }

    private static (List<int> Bits, List<float> Margins) Run(
        double[] polarities, Func<int, (double R, double Q)> channel)
    {
        var bits = new List<int>();
        var margins = new List<float>();
        var equaliser = new MlseEqualiser((bit, margin) =>
        {
            bits.Add(bit);
            margins.Add(margin);
        });

        for (int k = 0; k < polarities.Length; k++)
        {
            (double r, double q) = channel(k);
            equaliser.Push((float)r, (float)q, seededRotation: 0, burstActive: true);
        }

        equaliser.Flush();
        return (bits, margins);
    }

    private static string AsString(IEnumerable<int> bits) => string.Concat(bits);

    /// <summary>Additive noise from one deterministic stream.</summary>
    private static Func<int, (double, double)> FlatChannel(
        double[] a, double gain, double noiseSigma, int seed)
    {
        var random = new Random(seed);
        return k => (
            (gain * a[k]) + (noiseSigma * Gaussian(random)),
            noiseSigma * Gaussian(random));
    }

    private static double Gaussian(Random random)
    {
        double u1 = 1.0 - random.NextDouble();
        double u2 = random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    [Fact]
    public void A_Flat_Channel_Payload_Is_Recovered()
    {
        (double[] a, int[] payload) = Sequence(preambleSymbols: 48, payloadBits: 200, seed: 3);

        (List<int> bits, _) = Run(a, FlatChannel(a, gain: 1.0, noiseSigma: 0.05, seed: 7));

        // Allow the acquisition region its convergence: the assertion is that the payload
        // past the first 20 bits comes through bit-perfect, wherever it lands in the stream.
        AsString(bits).Should().Contain(AsString(payload.Skip(20)));
    }

    [Fact]
    public void A_Deep_Post_Cursor_Echo_Is_Equalised()
    {
        (double[] a, int[] payload) = Sequence(preambleSymbols: 48, payloadBits: 200, seed: 11);

        // Two static paths: unit main plus a 0.85 echo one symbol later at 40 degrees - far
        // more inter-symbol interference than any symbol-by-symbol decision can absorb.
        double echoR = 0.85 * Math.Cos(0.7);
        double echoQ = 0.85 * Math.Sin(0.7);
        var random = new Random(13);
        (List<int> bits, _) = Run(a, k =>
        {
            double previous = k > 0 ? a[k - 1] : 1.0;
            return (
                a[k] + (echoR * previous) + (0.05 * Gaussian(random)),
                (echoQ * previous) + (0.05 * Gaussian(random)));
        });

        AsString(bits).Should().Contain(AsString(payload.Skip(20)));
    }

    [Fact]
    public void A_Slow_Carrier_Rotation_Is_Tracked()
    {
        (double[] a, int[] payload) = Sequence(preambleSymbols: 48, payloadBits: 200, seed: 17);

        // 0.05 rad/symbol is ~2.4 Hz at 300 Bd - a mid-branch residual, unseeded, left
        // entirely to the equaliser's own tracker.
        var random = new Random(19);
        (List<int> bits, _) = Run(a, k =>
        {
            double phase = 0.05 * k;
            return (
                (a[k] * Math.Cos(phase)) + (0.05 * Gaussian(random)),
                (a[k] * Math.Sin(phase)) + (0.05 * Gaussian(random)));
        });

        AsString(bits).Should().Contain(AsString(payload.Skip(40)));
    }

    [Fact]
    public void Flush_Emits_Every_Bit_The_Push_Stream_Owes()
    {
        (double[] a, _) = Sequence(preambleSymbols: 24, payloadBits: 100, seed: 23);

        (List<int> bits, _) = Run(a, FlatChannel(a, gain: 1.0, noiseSigma: 0.02, seed: 29));

        // Every adjacent pair of symbols yields exactly one differential bit.
        bits.Should().HaveCount(a.Length - 1);
    }

    [Fact]
    public void The_Same_Input_Reproduces_The_Same_Output()
    {
        (double[] a, _) = Sequence(preambleSymbols: 32, payloadBits: 150, seed: 31);

        (List<int> first, List<float> firstMargins) =
            Run(a, FlatChannel(a, gain: 0.7, noiseSigma: 0.1, seed: 37));
        (List<int> second, List<float> secondMargins) =
            Run(a, FlatChannel(a, gain: 0.7, noiseSigma: 0.1, seed: 37));

        second.Should().Equal(first);
        secondMargins.Should().Equal(firstMargins);
    }

    [Fact]
    public void A_Faded_Symbol_Ranks_Among_The_Least_Confident()
    {
        (double[] a, _) = Sequence(preambleSymbols: 48, payloadBits: 200, seed: 41);

        // One symbol deep in the payload collapses to 5 % amplitude - a one-symbol fade.
        int faded = 150;
        var random = new Random(43);
        (List<int> bits, List<float> margins) = Run(a, k =>
        {
            double gain = k == faded ? 0.05 : 1.0;
            return (
                (gain * a[k]) + (0.03 * Gaussian(random)),
                0.03 * Gaussian(random));
        });

        // The fade touches two differential bits (the symbol against both neighbours).
        // Wherever the stream's small delay put them, the two smallest margins in the
        // settled region must sit within a couple of bits of the fade.
        int settled = 60;
        List<int> ranked = Enumerable.Range(settled, bits.Count - settled)
            .OrderBy(i => margins[i])
            .Take(2)
            .ToList();

        ranked.Should().OnlyContain(i => Math.Abs(i - faded) <= 4);
    }
}
