using System.Globalization;
using System.Numerics;

namespace Packet.SoundModem.Ota;

/// <summary>How a <see cref="ReceiveFilter"/> is built.</summary>
internal enum ReceiveFilterKind
{
    /// <summary>No filter: the audio reaches the modem as the channel left it.</summary>
    None,

    /// <summary>An analogue crystal (or mechanical) IF filter: an 8-pole Chebyshev with 0.5 dB
    /// ripple, arithmetically symmetric about its centre as an IF filter is, minimum phase, so
    /// it has the group-delay peaks at its edges a real ladder filter has.</summary>
    Crystal,

    /// <summary>A DSP rig's IF filter: a long linear-phase FIR (Kaiser-windowed sinc, about
    /// 60 dB stopband), flat group delay and a much steeper skirt than the crystal.</summary>
    Dsp,
}

/// <summary>
/// A model of a receiver's SSB filter as the audio sees it, for the MS110D filter study. Both
/// shapes put their -6 dB points at the stated edges, the way rig manufacturers quote an SSB
/// filter's width. Each <see cref="Apply"/> runs on a whole buffer with fresh state.
/// </summary>
internal sealed class ReceiveFilter
{
    private const int CrystalPoles = 8;
    private const double CrystalRippleDb = 0.5;
    private const int DspTaps = 255;
    private const double DspKaiserBeta = 5.65; // about 60 dB stopband

    private readonly Biquad[] _sections = [];
    private readonly double[] _fir = [];

    private ReceiveFilter(ReceiveFilterKind kind, double lowHz, double highHz, int rate, string name)
    {
        Kind = kind;
        LowHz = lowHz;
        HighHz = highHz;
        Rate = rate;
        Name = name;
        if (kind == ReceiveFilterKind.Crystal)
        {
            _sections = DesignChebyshevLowPass(CrystalPoles, CrystalRippleDb, (highHz - lowHz) / 2, rate);
        }
        else if (kind == ReceiveFilterKind.Dsp)
        {
            _fir = DesignKaiserBandPass(DspTaps, DspKaiserBeta, lowHz, highHz, rate);
        }
    }

    /// <summary>The filter's construction.</summary>
    public ReceiveFilterKind Kind { get; }

    /// <summary>Lower -6 dB edge, Hz audio (0 for no filter).</summary>
    public double LowHz { get; }

    /// <summary>Upper -6 dB edge, Hz audio (0 for no filter).</summary>
    public double HighHz { get; }

    /// <summary>Sample rate the filter runs at.</summary>
    public int Rate { get; }

    /// <summary>The spec string it was parsed from, e.g. <c>xtal:300-2700</c>.</summary>
    public string Name { get; }

    /// <summary>-6 dB width in Hz (0 for no filter).</summary>
    public double WidthHz => HighHz - LowHz;

    /// <summary>Parses <c>none</c>, <c>xtal:LO-HI</c> or <c>dsp:LO-HI</c> (edges in Hz audio).</summary>
    public static ReceiveFilter Parse(string spec, int rate)
    {
        string s = spec.Trim().ToLowerInvariant();
        if (s == "none")
        {
            return new ReceiveFilter(ReceiveFilterKind.None, 0, 0, rate, "none");
        }

        string[] parts = s.Split(':');
        string[] edges = parts.Length == 2 ? parts[1].Split('-') : [];
        if (edges.Length != 2
            || !double.TryParse(edges[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double lo)
            || !double.TryParse(edges[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double hi)
            || lo <= 0 || hi <= lo || hi >= rate / 2.0)
        {
            throw new ArgumentException($"receive filter '{spec}': expected none, xtal:LO-HI or dsp:LO-HI");
        }

        ReceiveFilterKind kind = parts[0] switch
        {
            "xtal" => ReceiveFilterKind.Crystal,
            "dsp" => ReceiveFilterKind.Dsp,
            _ => throw new ArgumentException($"receive filter '{spec}': kind must be xtal or dsp"),
        };
        return new ReceiveFilter(kind, lo, hi, rate, s);
    }

    /// <summary>Filters a buffer (fresh state, output the same length; the FIR is
    /// delay-compensated, the IIR is causal like the real thing).</summary>
    public float[] Apply(ReadOnlySpan<float> input)
    {
        var output = new float[input.Length];
        if (Kind == ReceiveFilterKind.None)
        {
            input.CopyTo(output);
            return output;
        }

        if (Kind == ReceiveFilterKind.Dsp)
        {
            int half = _fir.Length / 2;
            for (int i = 0; i < input.Length; i++)
            {
                double acc = 0;
                int jLo = Math.Max(0, i + half - (input.Length - 1));
                int jHi = Math.Min(_fir.Length - 1, i + half);
                for (int j = jLo; j <= jHi; j++)
                {
                    acc += _fir[j] * input[i + half - j];
                }

                output[i] = (float)acc;
            }

            return output;
        }

        // The crystal sits at IF, where its passband is arithmetically symmetric: run the
        // lowpass prototype on the complex envelope about the filter's centre, then back.
        double centre = (LowHz + HighHz) / 2;
        var stateRe = new double[_sections.Length * 2];
        var stateIm = new double[_sections.Length * 2];
        for (int i = 0; i < input.Length; i++)
        {
            double theta = 2 * Math.PI * centre * i / Rate;
            double c = Math.Cos(theta);
            double s = Math.Sin(theta);
            double re = input[i] * c;
            double im = -input[i] * s;
            for (int k = 0; k < _sections.Length; k++)
            {
                re = _sections[k].Step(re, ref stateRe[2 * k], ref stateRe[(2 * k) + 1]);
                im = _sections[k].Step(im, ref stateIm[2 * k], ref stateIm[(2 * k) + 1]);
            }

            output[i] = (float)(2 * ((re * c) - (im * s)));
        }

        return output;
    }

    /// <summary>Complex response at <paramref name="hz"/>.</summary>
    public Complex Response(double hz)
    {
        if (Kind == ReceiveFilterKind.None)
        {
            return Complex.One;
        }

        Complex zInv = Complex.FromPolarCoordinates(1, -2 * Math.PI * hz / Rate);
        if (Kind == ReceiveFilterKind.Dsp)
        {
            // Delay-compensated: report the zero-phase response.
            Complex acc = Complex.Zero;
            Complex zk = Complex.One;
            for (int j = 0; j < _fir.Length; j++)
            {
                acc += _fir[j] * zk;
                zk *= zInv;
            }

            return acc * Complex.FromPolarCoordinates(1, 2 * Math.PI * hz / Rate * (_fir.Length / 2));
        }

        // The lowpass prototype's response, moved up to the filter centre.
        Complex zLp = Complex.FromPolarCoordinates(1, -2 * Math.PI * (hz - ((LowHz + HighHz) / 2)) / Rate);
        Complex h = Complex.One;
        foreach (Biquad b in _sections)
        {
            h *= b.Response(zLp);
        }

        return h;
    }

    /// <summary>Gain in dB at a frequency.</summary>
    public double GainDb(double hz) => 20 * Math.Log10(Math.Max(1e-12, Response(hz).Magnitude));

    /// <summary>Group delay in milliseconds at a frequency (numerical phase derivative).</summary>
    public double GroupDelayMs(double hz)
    {
        const double d = 0.5;
        double p1 = Response(hz - d).Phase;
        double p2 = Response(hz + d).Phase;
        double dp = p2 - p1;
        while (dp > Math.PI)
        {
            dp -= 2 * Math.PI;
        }

        while (dp < -Math.PI)
        {
            dp += 2 * Math.PI;
        }

        return -dp / (2 * Math.PI * 2 * d) * 1000;
    }

    /// <summary>One line describing the realised filter: measured -6 and -60 dB points and the
    /// group-delay spread across its -6 dB passband. ASCII.</summary>
    public string Describe()
    {
        if (Kind == ReceiveFilterKind.None)
        {
            return "none: no receive filter";
        }

        (double lo6, double hi6) = Edges(-6);
        (double lo60, double hi60) = Edges(-60);
        double gdMin = double.MaxValue, gdMax = double.MinValue;
        for (double f = lo6 + 50; f <= hi6 - 50; f += 10)
        {
            double gd = GroupDelayMs(f);
            gdMin = Math.Min(gdMin, gd);
            gdMax = Math.Max(gdMax, gd);
        }

        // Both shapes are symmetric about their centre, so the shape factor comes from the upper
        // skirt (a crystal's lower -60 dB point can sit below 0 Hz audio).
        double centre = (LowHz + HighHz) / 2;
        string lower = lo60 <= 1 ? "below 0" : lo60.ToString("0", CultureInfo.InvariantCulture);
        string delay = Kind == ReceiveFilterKind.Dsp
            ? "group delay flat (linear phase)"
            : string.Create(CultureInfo.InvariantCulture,
                $"group delay {gdMin:0.00}-{gdMax:0.00} ms inside -6 dB (+-50 Hz)");
        return string.Create(CultureInfo.InvariantCulture,
            $"{Name}: -6 dB {lo6:0}-{hi6:0} Hz, -60 dB {lower}-{hi60:0} Hz, shape factor (6/60 dB) "
            + $"{2 * (hi60 - centre) / (hi6 - lo6):0.00}, {delay}");
    }

    /// <summary>The outermost frequencies where the gain falls through <paramref name="db"/>.</summary>
    public (double Lo, double Hi) Edges(double db)
    {
        double centre = (LowHz + HighHz) / 2;
        double lo = centre, hi = centre;
        while (lo > 1 && GainDb(lo) > db)
        {
            lo -= 1;
        }

        while (hi < (Rate / 2.0) - 1 && GainDb(hi) > db)
        {
            hi += 1;
        }

        return (lo, hi);
    }

    private static Biquad[] DesignChebyshevLowPass(int poles, double rippleDb, double cutoffHz, int rate)
    {
        // Lowpass prototype, scaled so its -6 dB point (not its ripple edge) is at the cutoff.
        double eps = Math.Sqrt(Math.Pow(10, rippleDb / 10) - 1);
        double w6 = Math.Cosh(Math.Acosh(Math.Sqrt(Math.Pow(10, 6.0206 / 10) - 1) / eps) / poles);
        double v = Math.Asinh(1 / eps) / poles;

        // Bilinear transform with the cutoff prewarped, so the -6 dB point lands exactly.
        double fs2 = 2.0 * rate;
        double wc = fs2 * Math.Tan(Math.PI * cutoffHz / rate);
        var sections = new List<Biquad>();
        for (int k = 1; k <= poles / 2; k++)
        {
            double theta = Math.PI * ((2 * k) - 1) / (2.0 * poles);
            Complex p = new Complex(-Math.Sinh(v) * Math.Sin(theta), Math.Cosh(v) * Math.Cos(theta)) / w6 * wc;
            Complex z = (fs2 + p) / (fs2 - p);
            // Two zeros at z = -1 per section (the lowpass's zeros at s = infinity).
            sections.Add(new Biquad(1, 2, 1, -2 * z.Real, z.Magnitude * z.Magnitude, 1));
        }

        // Normalise the passband peak to unity gain.
        double peak = 0;
        for (double f = 0; f <= cutoffHz; f += 1)
        {
            Complex zInv = Complex.FromPolarCoordinates(1, -2 * Math.PI * f / rate);
            Complex h = Complex.One;
            foreach (Biquad b in sections)
            {
                h *= b.Response(zInv);
            }

            peak = Math.Max(peak, h.Magnitude);
        }

        double perSection = Math.Pow(1 / peak, 1.0 / sections.Count);
        return [.. sections.Select(b => b with { Gain = perSection })];
    }

    private static double[] DesignKaiserBandPass(int taps, double beta, double lowHz, double highHz, int rate)
    {
        // Windowed ideal bandpass; a windowed sinc's -6 dB point sits at its cutoff.
        var h = new double[taps];
        int half = taps / 2;
        double i0Beta = BesselI0(beta);
        for (int n = 0; n < taps; n++)
        {
            int m = n - half;
            double ideal = m == 0
                ? 2 * (highHz - lowHz) / rate
                : (Math.Sin(2 * Math.PI * highHz * m / rate) - Math.Sin(2 * Math.PI * lowHz * m / rate)) / (Math.PI * m);
            double r = (double)m / half;
            double window = BesselI0(beta * Math.Sqrt(Math.Max(0, 1 - (r * r)))) / i0Beta;
            h[n] = ideal * window;
        }

        // Unity gain at the passband centre.
        double centre = (lowHz + highHz) / 2;
        Complex acc = Complex.Zero;
        for (int n = 0; n < taps; n++)
        {
            acc += h[n] * Complex.FromPolarCoordinates(1, -2 * Math.PI * centre * n / rate);
        }

        for (int n = 0; n < taps; n++)
        {
            h[n] /= acc.Magnitude;
        }

        return h;
    }

    private static double BesselI0(double x)
    {
        double sum = 1, term = 1;
        for (int k = 1; k < 50; k++)
        {
            term *= x / (2 * k);
            sum += term * term;
        }

        return sum;
    }

    /// <summary>One direct-form-II-transposed second-order section,
    /// Gain x (b0 + b1 z^-1 + b2 z^-2) / (1 + a1 z^-1 + a2 z^-2).</summary>
    private readonly record struct Biquad(double B0, double B1, double B2, double A1, double A2, double Gain)
    {
        public double Step(double x, ref double s1, ref double s2)
        {
            double xin = x * Gain;
            double y = (B0 * xin) + s1;
            s1 = (B1 * xin) - (A1 * y) + s2;
            s2 = (B2 * xin) - (A2 * y);
            return y;
        }

        public Complex Response(Complex zInv) =>
            Gain * (B0 + (B1 * zInv) + (B2 * zInv * zInv)) / (1 + (A1 * zInv) + (A2 * zInv * zInv));
    }
}
