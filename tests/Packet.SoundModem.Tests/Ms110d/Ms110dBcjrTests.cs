using M0LTE.Ofdm;
using Packet.SoundModem.Ms110d;

namespace Packet.SoundModem.Tests.Ms110d;

public class Ms110dBcjrTests
{
    [Fact]
    public void Flat_Channel_All_Plus_Ones_Gives_Positive_Llrs()
    {
        // All +1 symbols, flat channel (h1=1, h2=0), low noise.
        // BCJR should give strongly positive LLRs (bit 0 = symbol +1).
        int n = 20;
        int delay = 3;
        var rx = new Cf[n];
        var h1 = new Cf[n];
        var h2 = new Cf[n];
        var rng = new Random(42);

        for (int i = 0; i < n; i++)
        {
            h1[i] = new Cf(1, 0);
            h2[i] = Cf.Zero;
            // rx = +1 + small noise
            rx[i] = new Cf(1f + (float)(rng.NextDouble() - 0.5) * 0.1f,
                           (float)(rng.NextDouble() - 0.5) * 0.1f);
        }

        float[] llrs = Ms110dBcjr.Equalize(rx, h1, h2, delay, noiseVar: 0.01f);

        Assert.Equal(n, llrs.Length);
        for (int i = 0; i < n; i++)
        {
            Assert.True(llrs[i] > 0, $"LLR[{i}] = {llrs[i]} should be positive (symbol +1)");
        }
    }

    [Fact]
    public void Flat_Channel_Alternating_Gives_Correct_Signs()
    {
        // Alternating +1/-1 symbols, flat channel, low noise.
        int n = 20;
        int delay = 3;
        var rx = new Cf[n];
        var h1 = new Cf[n];
        var h2 = new Cf[n];
        var rng = new Random(42);

        for (int i = 0; i < n; i++)
        {
            h1[i] = new Cf(1, 0);
            h2[i] = Cf.Zero;
            float sym = i % 2 == 0 ? 1f : -1f;
            rx[i] = new Cf(sym + (float)(rng.NextDouble() - 0.5) * 0.1f,
                           (float)(rng.NextDouble() - 0.5) * 0.1f);
        }

        float[] llrs = Ms110dBcjr.Equalize(rx, h1, h2, delay, noiseVar: 0.01f);

        for (int i = 0; i < n; i++)
        {
            float expected = i % 2 == 0 ? 1f : -1f;
            if (expected > 0)
                Assert.True(llrs[i] > 0, $"LLR[{i}] = {llrs[i]} should be positive");
            else
                Assert.True(llrs[i] < 0, $"LLR[{i}] = {llrs[i]} should be negative");
        }
    }

    [Fact]
    public void Two_Path_Channel_Exploits_Isi()
    {
        // 2-path channel with ISI. BCJR should outperform symbol-by-symbol detection.
        int n = 30;
        int delay = 3;
        var rng = new Random(123);
        var tx = new float[n];
        for (int i = 0; i < n; i++) tx[i] = rng.Next(2) == 0 ? 1f : -1f;

        var h1 = new Cf[n];
        var h2 = new Cf[n];
        var rx = new Cf[n];
        float h2Mag = 0.5f; // delayed path at half power

        for (int i = 0; i < n; i++)
        {
            h1[i] = new Cf(1, 0);
            h2[i] = new Cf(h2Mag, 0);
            float signal = tx[i];
            if (i >= delay) signal += h2Mag * tx[i - delay];
            rx[i] = new Cf(signal + (float)(rng.NextDouble() - 0.5) * 0.3f,
                           (float)(rng.NextDouble() - 0.5) * 0.3f);
        }

        float[] llrs = Ms110dBcjr.Equalize(rx, h1, h2, delay, noiseVar: 0.05f);

        // Check that most LLRs have the correct sign.
        int correct = 0;
        for (int i = 0; i < n; i++)
        {
            if ((llrs[i] > 0) == (tx[i] > 0)) correct++;
        }

        // BCJR should get most symbols right (at least 80%).
        Assert.True(correct >= n * 8 / 10, $"Only {correct}/{n} correct");
    }
}
