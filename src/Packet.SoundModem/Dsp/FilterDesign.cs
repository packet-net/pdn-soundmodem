namespace Packet.SoundModem.Dsp;

/// <summary>
/// Runtime FIR design by the windowed-sinc method with a Hann window — the same approach
/// QtSoundModem uses (its init_LPF/make_core_BPF); coefficients are designed at startup,
/// not baked into tables.
/// </summary>
public static class FilterDesign
{
    /// <summary>Designs a low-pass filter with unity DC gain.</summary>
    public static float[] LowPass(double cutoffHz, double sampleRate, int taps)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(taps, 3);
        var h = new double[taps];
        double fc = cutoffHz / sampleRate;
        double centre = (taps - 1) / 2.0;
        double sum = 0;
        for (int i = 0; i < taps; i++)
        {
            double x = i - centre;
            double sinc = x == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * x) / (Math.PI * x);
            double window = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (taps - 1));
            h[i] = sinc * window;
            sum += h[i];
        }

        var result = new float[taps];
        for (int i = 0; i < taps; i++)
        {
            result[i] = (float)(h[i] / sum);
        }

        return result;
    }

    /// <summary>Designs a band-pass filter (difference of two low-passes), gain ≈ 1 in band.</summary>
    public static float[] BandPass(double lowHz, double highHz, double sampleRate, int taps)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(highHz, lowHz);
        float[] upper = LowPass(highHz, sampleRate, taps);
        float[] lower = LowPass(lowHz, sampleRate, taps);
        var result = new float[taps];
        for (int i = 0; i < taps; i++)
        {
            result[i] = upper[i] - lower[i];
        }

        return result;
    }
}
