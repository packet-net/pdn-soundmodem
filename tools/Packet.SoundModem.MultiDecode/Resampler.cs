namespace Packet.SoundModem.MultiDecode;

/// <summary>
/// Polyphase rational sample-rate conversion, for getting a recording of unknown provenance to
/// the 12000 or 48000 Hz rate a mode's DSP chain wants.
/// </summary>
/// <remarks>
/// <para>The library deliberately has no resampler: a live station's capture rate must be an
/// integer multiple of the DSP rate, and <c>ModemCatalog</c>/the daemon refuse anything else by
/// name rather than quietly resampling, because a station silently running through an
/// interpolator is a performance question nobody asked. That rule is right for a station and
/// wrong for a forensic tool, where the file is whatever the person who recorded it had their
/// soundcard set to and 44100 Hz is a perfectly ordinary answer. So the conversion lives here,
/// in the tool, and is visible in its output rather than assumed.</para>
/// <para>Standard L/M polyphase: interpolate by L, low-pass, decimate by M, with only the one
/// non-zero phase of the prototype evaluated per output sample. The prototype is a
/// Blackman-Harris windowed sinc; its length is chosen from the ratio so the transition band
/// stays about a tenth of the lower Nyquist however severe the decimation, which matters here
/// because 48000 -> 12000 is the common case and a short filter would fold everything from 6 to
/// 24 kHz back onto the very band we are trying to decode.</para>
/// </remarks>
internal static class Resampler
{
    /// <summary>Taps per polyphase branch, per unit of decimation. See the transition-width
    /// reasoning in the type remarks; 80 puts the stopband edge at roughly 1.1x the cutoff.</summary>
    private const int TapsPerPhasePerDecade = 80;

    /// <summary>Cutoff as a fraction of the lower of the two Nyquist frequencies. The 10 %
    /// headroom is the transition band; the modes we care about occupy well under half their
    /// DSP rate, so nothing real lives up there.</summary>
    private const double CutoffFraction = 0.45;

    /// <summary>Refuse rather than allocate a bank this big. Only an awkward rate pair
    /// (a prime-ish ratio) can get here, and converting the file first is the better answer.</summary>
    private const int MaxPrototypeTaps = 4_000_000;

    /// <summary>
    /// Resamples <paramref name="input"/> from <paramref name="fromRate"/> to
    /// <paramref name="toRate"/>. Returns the input array itself when the rates already match.
    /// </summary>
    /// <exception cref="InvalidOperationException">The rate ratio needs an impractically large
    /// polyphase bank.</exception>
    public static float[] Resample(float[] input, int fromRate, int toRate)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fromRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(toRate);

        if (fromRate == toRate || input.Length == 0)
        {
            return input;
        }

        int divisor = Gcd(fromRate, toRate);
        int interpolate = toRate / divisor;   // L
        int decimate = fromRate / divisor;    // M

        int lowerRate = Math.Min(fromRate, toRate);
        int tapsPerPhase = Math.Clamp(
            (int)Math.Ceiling((double)TapsPerPhasePerDecade * fromRate / lowerRate), 32, 8192);

        long prototypeTaps = (long)interpolate * tapsPerPhase;
        if (prototypeTaps > MaxPrototypeTaps)
        {
            throw new InvalidOperationException(
                $"{fromRate} Hz to {toRate} Hz needs a {prototypeTaps}-tap polyphase bank, which is "
                + "not a sensible thing to build - convert the file first, e.g. "
                + $"'sox in.wav -r {toRate} out.wav'");
        }

        float[] prototype = BuildPrototype((int)prototypeTaps, interpolate, decimate, fromRate, lowerRate);

        // One extra filter span of output so the tail of the input flushes through the group
        // delay rather than being truncated with the last frame's closing flag still inside it.
        long outputLength = ((long)(input.Length + tapsPerPhase) * interpolate) / decimate;
        var output = new float[outputLength];

        for (long n = 0; n < outputLength; n++)
        {
            long scaled = n * decimate;
            long start = scaled / interpolate;            // newest input sample this output sees
            int phase = (int)(scaled % interpolate);

            double sum = 0;
            for (int j = 0; j < tapsPerPhase; j++)
            {
                long index = start - j;
                if (index < 0)
                {
                    break;
                }

                if (index < input.Length)
                {
                    sum += prototype[(j * interpolate) + phase] * input[index];
                }
            }

            output[n] = (float)sum;
        }

        return output;
    }

    /// <summary>Blackman-Harris windowed sinc at the interpolated rate, scaled by L so the
    /// single phase each output sample uses has unit DC gain.</summary>
    private static float[] BuildPrototype(
        int taps, int interpolate, int decimate, int fromRate, int lowerRate)
    {
        // Normalised to the interpolated rate L*fromRate: the cutoff has to sit below BOTH
        // Nyquists, which is what makes this one filter serve as interpolation image rejection
        // and decimation anti-aliasing at once.
        double interpolatedRate = (double)interpolate * fromRate;
        double cutoff = CutoffFraction * lowerRate / interpolatedRate;
        _ = decimate;

        var h = new float[taps];
        double centre = (taps - 1) / 2.0;
        for (int i = 0; i < taps; i++)
        {
            double x = i - centre;
            double ideal = 2 * cutoff * Sinc(2 * cutoff * x);

            // 4-term Blackman-Harris: about -92 dB sidelobes, so the stopband is well below
            // anything a discriminator's noise floor is doing.
            double t = 2 * Math.PI * i / (taps - 1);
            double window = 0.35875
                - (0.48829 * Math.Cos(t))
                + (0.14128 * Math.Cos(2 * t))
                - (0.01168 * Math.Cos(3 * t));

            h[i] = (float)(ideal * window * interpolate);
        }

        return h;
    }

    private static double Sinc(double x) =>
        Math.Abs(x) < 1e-12 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);

    private static int Gcd(int a, int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }
}
