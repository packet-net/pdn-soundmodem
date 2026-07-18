using M0LTE.Ofdm;

namespace Packet.SoundModem.Tests.Channel;

public class WattersonChannelTests
{
    private const int Rate = 9600;

    [Fact]
    public void Awgn_Snr_Calibration_Is_Accurate()
    {
        // A pure 1800 Hz tone through the AWGN-only channel at 10 dB (3 kHz noise
        // bandwidth): recover the tone by projection, measure the residual, convert back
        // to the 3 kHz-bandwidth SNR definition (house pattern, docs/ofdm-design.md §8.6).
        var input = new float[Rate * 4];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = 0.3f * MathF.Sin(2 * MathF.PI * 1800 * i / Rate);
        }

        var channel = new WattersonChannel(Rate, seed: 42);
        float[] output = channel.Apply(input, snrDb: 10);

        double dot = 0, dotQ = 0, sigPower = 0;
        for (int i = 0; i < input.Length; i++)
        {
            double c = Math.Sin(2 * Math.PI * 1800 * i / (double)Rate);
            double q = Math.Cos(2 * Math.PI * 1800 * i / (double)Rate);
            dot += output[i] * c;
            dotQ += output[i] * q;
            sigPower += c * c;
        }

        double amp = dot / sigPower;
        double ampQ = dotQ / sigPower;
        double recoveredPower = (amp * amp + ampQ * ampQ) / 2;

        double residual = 0;
        for (int i = 0; i < input.Length; i++)
        {
            double c = Math.Sin(2 * Math.PI * 1800 * i / (double)Rate);
            double q = Math.Cos(2 * Math.PI * 1800 * i / (double)Rate);
            double e = output[i] - (amp * c) - (ampQ * q);
            residual += e * e;
        }

        double noisePowerNyquist = residual / input.Length;
        double noisePower3k = noisePowerNyquist * 3000.0 / (Rate / 2.0);
        double measuredSnrDb = 10 * Math.Log10(recoveredPower / noisePower3k);

        measuredSnrDb.Should().BeApproximately(10, 0.7);
    }

    [Fact]
    public void Fading_Gains_Have_The_Requested_Doppler_Spread()
    {
        // Gaussian Doppler PSD of σ: autocorrelation R(τ)/R(0) = exp(−2π²σ²τ²). Estimate σ
        // from R(0.2 s) over a 10-minute realization and pin it within 15 %.
        var channel = new WattersonChannel(Rate, seed: 7);
        Cf[] gains = channel.FadingGains(Rate * 600, sigmaHz: 0.5);

        int lag = (int)(0.2 * Rate);
        var r0 = 0.0;
        var rl = Cf.Zero;
        for (int i = 0; i < gains.Length - lag; i++)
        {
            r0 += gains[i].Cnorm();
            rl += gains[i + lag] * gains[i].Conj();
        }

        double ratio = rl.Abs() / r0;
        ratio.Should().BeInRange(0.05, 0.99);
        double sigma = Math.Sqrt(-Math.Log(ratio) / (2 * Math.PI * Math.PI * 0.2 * 0.2));
        sigma.Should().BeInRange(0.425, 0.575);
    }

    [Fact]
    public void Fading_Gains_Are_Unit_Power_Rayleigh()
    {
        var channel = new WattersonChannel(Rate, seed: 11);
        Cf[] gains = channel.FadingGains(Rate * 600, sigmaHz: 0.5);
        double meanSquare = 0, meanAbs = 0;
        foreach (Cf g in gains)
        {
            meanSquare += g.Cnorm();
            meanAbs += g.Abs();
        }

        meanSquare /= gains.Length;
        meanAbs /= gains.Length;
        meanSquare.Should().BeInRange(0.8, 1.2);

        // Rayleigh: E|g| = √(π/4)·√(E|g|²) ≈ 0.886 for unit power.
        (meanAbs / Math.Sqrt(meanSquare)).Should().BeInRange(0.85, 0.92);
    }

    [Fact]
    public void Static_Multipath_Places_Echoes_At_The_Specified_Delays()
    {
        // The WID 2 static rig: 3 equal paths at 0 / 3.0 / 9.0 ms (Table D-LXV). Drive the
        // channel with in-band noise and look for cross-correlation peaks at the delays.
        var random = new Random(3);
        var input = new float[Rate * 4];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = (float)((random.NextDouble() - 0.5) * Math.Sin(2 * Math.PI * 1800 * i / (double)Rate) * 2);
        }

        var channel = new WattersonChannel(
            Rate, seed: 5, new WattersonPath(0), new WattersonPath(3.0), new WattersonPath(9.0));
        float[] output = channel.Apply(input, snrDb: double.PositiveInfinity);

        double Xcorr(int lag)
        {
            double sum = 0;
            for (int i = 0; i < input.Length - lag; i++)
            {
                sum += (double)output[i + lag] * input[i];
            }

            return Math.Abs(sum);
        }

        double reference = Xcorr(0);
        foreach (double delayMs in new[] { 3.0, 9.0 })
        {
            int expected = (int)Math.Round(delayMs * Rate / 1000);
            double best = 0;
            for (int lag = expected - 2; lag <= expected + 2; lag++)
            {
                best = Math.Max(best, Xcorr(lag));
            }

            (best / reference).Should().BeGreaterThan(
                0.4, $"an equal-power echo must appear at {delayMs} ms");
        }

        // And nothing significant where no path exists.
        (Xcorr((int)Math.Round(6.0 * Rate / 1000)) / reference).Should().BeLessThan(0.25);
    }
}
