using M0LTE.Ofdm;
using Packet.SoundModem.Ms110d;

namespace Packet.SoundModem.Tests.Ms110d;

/// <summary>
/// Targeted tests for the <see cref="Dfe"/> RLS recursion — chiefly the weighted-RLS
/// consistency property (issue #64): row weight must enter the gain denominator and the
/// P update together, or sustained advisory-weight rows freeze adaptation.
/// </summary>
public class DfeTests
{
    private static Cf[] RandomBpsk(int count, int seed)
    {
        var random = new Random(seed);
        var symbols = new Cf[count];
        for (int i = 0; i < count; i++)
        {
            symbols[i] = new Cf(random.Next(2) == 0 ? 1f : -1f, 0);
        }

        return symbols;
    }

    /// <summary>Feeds one RLS update for the trivial identity channel (window carries the
    /// current and previous symbol; the desired output is ±current) and returns the
    /// pre-update error magnitude.</summary>
    private static float Update(Dfe dfe, Cf current, Cf previous, Cf desired, float weight)
    {
        Span<Cf> window = [current, previous];
        Cf y = dfe.RlsUpdate(window, [], desired, weight);
        return (desired - y).Abs();
    }

    [Fact]
    public void Rls_Adaptation_Survives_Sustained_Advisory_Weight()
    {
        // The issue-#64 freeze: converge at full weight, then invert the channel and offer
        // only advisory (0.1) rows. The old recursion scaled the tap step by the weight but
        // applied the full-confidence P update, so P collapsed while the taps barely moved —
        // measured ~0.9 residual error after 150 post-flip rows. Consistent weighted RLS is
        // scale-invariant in the weight, so the advisory rows re-converge just like full ones.
        var dfe = new Dfe(ffTaps: 2, fbTaps: 0);
        dfe.BeginRls(lambda: 0.95f, pInit: 1.0f);
        Cf[] symbols = RandomBpsk(260, seed: 7);

        for (int i = 1; i < 100; i++)
        {
            Update(dfe, symbols[i], symbols[i - 1], symbols[i], weight: 1f);
        }

        float error = float.NaN;
        for (int i = 100; i < 250; i++)
        {
            error = Update(dfe, symbols[i], symbols[i - 1], symbols[i] * -1f, weight: 0.1f);
        }

        error.Should().BeLessThan(0.05f,
            "advisory-weight rows must keep adapting (weighted-RLS consistency, issue #64)");
    }

    [Fact]
    public void Rls_Uniform_Weight_Is_Scale_Invariant()
    {
        // Least squares is invariant to a uniform row scaling, so once the P0 prior washes
        // out an all-0.1-weight run must track exactly like an all-1.0 run. Drive both with
        // the same slowly-rotating channel: any weight/P inconsistency shows up as a 10×
        // longer tracking lag in the advisory run and the final taps diverge.
        Cf[] symbols = RandomBpsk(400, seed: 11);
        Cf[][] finals = new Cf[2][];
        float[] weights = [1f, 0.1f];
        for (int run = 0; run < 2; run++)
        {
            var dfe = new Dfe(ffTaps: 2, fbTaps: 0);
            dfe.BeginRls(lambda: 0.95f, pInit: 1.0f);
            for (int i = 1; i < 400; i++)
            {
                Cf gain = Cf.Cmplx(0.01f * i);
                Update(dfe, symbols[i], symbols[i - 1], gain * symbols[i], weights[run]);
            }

            finals[run] = dfe.SnapshotTaps();
        }

        for (int t = 0; t < finals[0].Length; t++)
        {
            (finals[0][t] - finals[1][t]).Abs().Should().BeLessThan(0.15f,
                $"tap {t}: uniform weight must not change the RLS trajectory");
        }
    }

    [Fact]
    public void Rls_Converges_On_The_Identity_Channel()
    {
        var dfe = new Dfe(ffTaps: 2, fbTaps: 0);
        dfe.BeginRls(lambda: 0.95f, pInit: 1.0f);
        Cf[] symbols = RandomBpsk(120, seed: 3);
        float error = float.NaN;
        for (int i = 1; i < 120; i++)
        {
            error = Update(dfe, symbols[i], symbols[i - 1], symbols[i], weight: 1f);
        }

        error.Should().BeLessThan(1e-3f, "noiseless RLS must converge to the exact solution");
    }
}
