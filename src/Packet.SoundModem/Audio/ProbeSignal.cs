using System.Numerics;
using M0LTE.Dsp;

namespace Packet.SoundModem.Audio;

/// <summary>
/// Everything that defines one version of a channel-sounding probe, so a receiver can build the
/// identical reference without reading this renderer's source.
/// </summary>
/// <remarks>
/// <para><b>The waveform, in full.</b> The chips are a Zadoff-Chu sequence,
/// <c>c[n] = exp(-j pi Root n (n + 1) / SequenceLength)</c> for n = 0 to SequenceLength - 1 (the
/// odd-length form), repeated without a break. Chip k sits at time
/// <c>RampSeconds + k / ChipRate</c> from the first rendered sample, with k counted from the
/// first chip of the first period; the sequence carries on periodically on both sides of the
/// periods, so the ramps sit on more of the same signal rather than on silence. Each chip is
/// shaped with a root-raised-cosine pulse of roll-off <see cref="RollOff"/>, evaluated in chip
/// units by <c>M0LTE.Dsp.FilterDesign.RootRaisedCosine</c> and truncated to
/// <see cref="PulseHalfSpanChips"/> chips either side of its centre. That complex envelope is
/// divided by its own peak magnitude (one period evaluated at
/// <see cref="NormalisationPointsPerChip"/> points per chip), multiplied by a raised-cosine ramp
/// of <see cref="RampSeconds"/> at each end, and put on the audio carrier as
/// <c>Re{ peak x envelope(t) x exp(j 2 pi audioHz t) }</c>, with t = 0 at the first rendered
/// sample.</para>
/// <para><b>Why the periods are what they are.</b> The first period is a cyclic prefix: a
/// receiver that drops it and correlates the rest sees each period as a circular convolution of
/// the sequence with the channel, and a Zadoff-Chu sequence's periodic autocorrelation is exactly
/// zero away from lag 0, so every delay inside one period (106.25 ms) reads out cleanly.</para>
/// <para><b>The id is the contract.</b> Change any field and it is a different probe, with a new
/// id; a receiver that does not know an id must not guess at it.</para>
/// </remarks>
/// <param name="Id">The versioned name, e.g. <c>zc255-2400-rrc015-v1</c>.</param>
/// <param name="Kind">The short name a request asks for it by, e.g. <c>zc255</c>.</param>
/// <param name="SequenceLength">Chips in one period.</param>
/// <param name="Root">The Zadoff-Chu root, coprime with the length.</param>
/// <param name="ChipRate">Chips per second.</param>
/// <param name="RollOff">The root-raised-cosine roll-off.</param>
/// <param name="PulseHalfSpanChips">How far either side of its centre a chip's pulse reaches.</param>
/// <param name="Periods">Whole periods sent, the first of them the cyclic prefix.</param>
/// <param name="RampSeconds">The raised-cosine rise before the first period and fall after the
/// last; the rendered probe is this much longer than the periods at each end.</param>
/// <param name="NormalisationPointsPerChip">The density the envelope's peak is found at.</param>
public sealed record ProbeDescriptor(
    string Id,
    string Kind,
    int SequenceLength,
    int Root,
    double ChipRate,
    double RollOff,
    int PulseHalfSpanChips,
    int Periods,
    double RampSeconds,
    int NormalisationPointsPerChip)
{
    /// <summary>One period, in seconds.</summary>
    public double PeriodSeconds => SequenceLength / ChipRate;

    /// <summary>The whole rendered probe, ramps included, in seconds.</summary>
    public double DurationSeconds => (Periods * PeriodSeconds) + (2 * RampSeconds);

    /// <summary>How far either side of the audio centre the shaped spectrum reaches, in Hz:
    /// half the chip rate times one plus the roll-off.</summary>
    public double HalfBandwidthHz => ChipRate * (1 + RollOff) / 2;

    /// <summary>Samples in the rendered probe at <paramref name="sampleRate"/>.</summary>
    public int SampleCount(int sampleRate) => checked((int)Math.Round(DurationSeconds * sampleRate));

    /// <summary>The first sample of the first period (the cyclic prefix), at <paramref name="sampleRate"/>.</summary>
    public int FirstPeriodSample(int sampleRate) => (int)Math.Round(RampSeconds * sampleRate);
}

/// <summary>
/// The channel-sounding probe a transmitter test can send after its tone: a known, periodic,
/// constant-ish envelope signal a receiver correlates against to read the channel's delay and
/// Doppler structure.
/// </summary>
/// <remarks>
/// <para><b>Public on purpose.</b> pdn-mailcast's receiver builds its correlation reference by
/// calling the same renderer the station transmits with, so the two cannot drift; the
/// <see cref="ProbeDescriptor"/> says the same thing in numbers for anyone building it
/// independently.</para>
/// <para><b>Level.</b> The envelope is normalised to a peak of 1 and then scaled by the peak
/// amplitude, so the probe peaks where a <see cref="TestTone"/> at the same level does: the
/// transmitter sees the same peak drive from both. The Zadoff-Chu envelope is nearly constant,
/// so its passband peak-to-average ratio is close to a sine wave's 3 dB.</para>
/// <para><b>Rendered whole.</b> A probe is a few seconds of audio rendered once per test, and the
/// cost is one pulse sum per sample, so it is computed directly at the channel's rate rather than
/// interpolated from a native rate: any rate gives the same continuous-time signal sampled
/// there.</para>
/// </remarks>
public static class ProbeSignal
{
    /// <summary>The kind a request names to ask for <see cref="Zc255"/>.</summary>
    public const string Zc255Kind = "zc255";

    /// <summary>
    /// The first probe: Zadoff-Chu length 255, root 1, 2400 chips/s, root-raised-cosine roll-off
    /// 0.15, 61 periods of 106.25 ms with 10 ms ramps, 6.50125 s in all. At an 1800 Hz centre it
    /// occupies 420 to 3180 Hz.
    /// </summary>
    public static ProbeDescriptor Zc255 { get; } = new(
        Id: "zc255-2400-rrc015-v1",
        Kind: Zc255Kind,
        SequenceLength: 255,
        Root: 1,
        ChipRate: 2400,
        RollOff: 0.15,
        PulseHalfSpanChips: 32,
        Periods: 61,
        RampSeconds: 0.010,
        NormalisationPointsPerChip: 64);

    /// <summary>Every probe this build can send, by kind.</summary>
    public static IReadOnlyList<ProbeDescriptor> Known { get; } = [Zc255];

    /// <summary>The most distinct pulse phases worth tabulating; past it every sample is summed
    /// from the pulse directly.</summary>
    private const long MaxTapTables = 4096;

    private static readonly Lock Gate = new();
    private static readonly Dictionary<ProbeDescriptor, double> PeakScale = [];

    /// <summary>The probe a kind names, or null for one this build does not know.</summary>
    public static ProbeDescriptor? ForKind(string? kind)
    {
        foreach (ProbeDescriptor known in Known)
        {
            if (string.Equals(known.Kind, kind, StringComparison.Ordinal))
            {
                return known;
            }
        }

        return null;
    }

    /// <summary>One period of the unshaped chips, unit magnitude.</summary>
    public static Complex[] Chips(ProbeDescriptor probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        int length = probe.SequenceLength;
        var chips = new Complex[length];
        for (int n = 0; n < length; n++)
        {
            // n (n + 1) reduced mod 2N before it becomes an angle, so the phase is exact for any
            // length rather than losing bits to a large argument.
            long q = ((long)probe.Root * n * (n + 1)) % (2L * length);
            chips[n] = Complex.FromPolarCoordinates(1.0, -Math.PI * q / length);
        }

        return chips;
    }

    /// <summary>
    /// The complex envelope, sample for sample what <see cref="Render"/> puts on the carrier:
    /// peak magnitude 1 across the periods, ramped at both ends.
    /// </summary>
    /// <remarks>The receiver's reference. Correlate a mixed-down capture against it, or against one
    /// period of it taken from <see cref="ProbeDescriptor.FirstPeriodSample"/> onwards.</remarks>
    public static Complex[] Envelope(ProbeDescriptor probe, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        Complex[] chips = Chips(probe);
        double scale = 1.0 / PeakEnvelope(probe, chips);
        int total = probe.SampleCount(sampleRate);
        int ramp = probe.FirstPeriodSample(sampleRate);
        var envelope = new Complex[total];
        double lead = probe.RampSeconds * probe.ChipRate;
        long chipRate = (long)Math.Round(probe.ChipRate);
        long whole = (long)Math.Round(lead);
        long phases = Math.Abs(chipRate - probe.ChipRate) < 1e-9 && Math.Abs(whole - lead) < 1e-9
            ? sampleRate / Gcd(sampleRate, chipRate)
            : long.MaxValue;
        if (phases > MaxTapTables)
        {
            // A rate with no short repeat between its samples and the chips: every sample has a
            // pulse phase of its own, so there is nothing to share.
            double chipsPerSample = probe.ChipRate / sampleRate;
            for (int n = 0; n < total; n++)
            {
                envelope[n] = Shaped(probe, chips, (n * chipsPerSample) - lead) * (scale * Ramp(n, total, ramp));
            }

            return envelope;
        }

        // The usual case (48 kHz is 20 samples a chip): sample n sits at chip time
        // n C / fs - lead, whose fractional part repeats every fs / gcd(fs, C) samples, so each
        // pulse phase's taps are worked out once and shared. Same sums as Shaped, without
        // evaluating the pulse 65 times a sample on a small processor.
        var tables = new Dictionary<long, (int Start, double[] Taps)>();
        int length = chips.Length;
        for (int n = 0; n < total; n++)
        {
            long position = n * chipRate;
            long residue = position % sampleRate;
            if (!tables.TryGetValue(residue, out (int Start, double[] Taps) table))
            {
                table = TapTable(probe, residue / (double)sampleRate);
                tables[residue] = table;
            }

            long baseChip = (position / sampleRate) - whole + table.Start;
            double re = 0, im = 0;
            double[] taps = table.Taps;
            for (int i = 0; i < taps.Length; i++)
            {
                Complex chip = chips[(int)((((baseChip + i) % length) + length) % length)];
                re += chip.Real * taps[i];
                im += chip.Imaginary * taps[i];
            }

            double weight = scale * Ramp(n, total, ramp);
            envelope[n] = new Complex(re * weight, im * weight);
        }

        return envelope;
    }

    /// <summary>
    /// The probe as audio, centred on <paramref name="audioHz"/> and peaking at
    /// <paramref name="peakAmplitude"/>.
    /// </summary>
    /// <param name="probe">Which probe.</param>
    /// <param name="audioHz">The audio centre. The whole shaped band
    /// (<see cref="ProbeDescriptor.HalfBandwidthHz"/> either side) must sit between 0 and the
    /// Nyquist frequency.</param>
    /// <param name="peakAmplitude">What the envelope peaks at, above 0 and at most 1: the level a
    /// <see cref="TestTone"/> at the same setting peaks at.</param>
    /// <param name="sampleRate">The channel's audio rate.</param>
    public static float[] Render(ProbeDescriptor probe, double audioHz, double peakAmplitude, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(peakAmplitude);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(peakAmplitude, 1.0);
        if (BandProblem(probe, audioHz, sampleRate) is string problem)
        {
            throw new ArgumentOutOfRangeException(nameof(audioHz), audioHz, problem);
        }

        Complex[] envelope = Envelope(probe, sampleRate);
        var audio = new float[envelope.Length];
        double step = 2.0 * Math.PI * audioHz / sampleRate;
        for (int n = 0; n < envelope.Length; n++)
        {
            // The phase from n directly rather than accumulated, so a 6.5 s probe has no drift
            // for a receiver's reference to disagree with.
            double phase = step * n;
            Complex e = envelope[n];
            audio[n] = (float)(peakAmplitude * ((e.Real * Math.Cos(phase)) - (e.Imaginary * Math.Sin(phase))));
        }

        return audio;
    }

    /// <summary>
    /// Why the probe cannot be centred on <paramref name="audioHz"/> in a channel at
    /// <paramref name="sampleRate"/>, or null when it can.
    /// </summary>
    public static string? BandProblem(ProbeDescriptor probe, double audioHz, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(probe);
        double half = probe.HalfBandwidthHz;
        double nyquist = sampleRate / 2.0;
        if (!double.IsFinite(audioHz) || audioHz - half <= 0 || audioHz + half >= nyquist)
        {
            return $"the {probe.Id} probe is {2 * half:F0} Hz wide, so its centre must be between "
                + $"{half:F0} Hz and {nyquist - half:F0} Hz on this {sampleRate} Hz channel";
        }

        return null;
    }

    /// <summary>The shaped, unnormalised envelope at chip time <paramref name="x"/>.</summary>
    private static Complex Shaped(ProbeDescriptor probe, Complex[] chips, double x)
    {
        int span = probe.PulseHalfSpanChips;
        int length = chips.Length;
        int first = (int)Math.Ceiling(x - span);
        int last = (int)Math.Floor(x + span);
        double re = 0, im = 0;
        for (int k = first; k <= last; k++)
        {
            double pulse = FilterDesign.RootRaisedCosine(x - k, probe.RollOff);
            Complex chip = chips[((k % length) + length) % length];
            re += chip.Real * pulse;
            im += chip.Imaginary * pulse;
        }

        return new Complex(re, im);
    }

    /// <summary>The pulse taps for a sample whose chip time has fractional part
    /// <paramref name="fraction"/>: the chips from <c>Start</c> (relative to the whole part) that
    /// the pulse reaches, and the pulse's value at each.</summary>
    private static (int Start, double[] Taps) TapTable(ProbeDescriptor probe, double fraction)
    {
        int span = probe.PulseHalfSpanChips;
        int first = (int)Math.Ceiling(fraction - span);
        int last = (int)Math.Floor(fraction + span);
        var taps = new double[last - first + 1];
        for (int i = 0; i < taps.Length; i++)
        {
            taps[i] = FilterDesign.RootRaisedCosine(fraction - (first + i), probe.RollOff);
        }

        return (first, taps);
    }

    private static long Gcd(long a, long b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }

    /// <summary>The peak envelope magnitude over one period, worked out once per probe.</summary>
    private static double PeakEnvelope(ProbeDescriptor probe, Complex[] chips)
    {
        lock (Gate)
        {
            if (PeakScale.TryGetValue(probe, out double known))
            {
                return known;
            }
        }

        int points = probe.SequenceLength * probe.NormalisationPointsPerChip;
        double peak = 0;
        for (int i = 0; i < points; i++)
        {
            peak = Math.Max(peak, Shaped(probe, chips, i / (double)probe.NormalisationPointsPerChip).Magnitude);
        }

        lock (Gate)
        {
            PeakScale[probe] = peak;
        }

        return peak;
    }

    /// <summary>The raised-cosine rise and fall, over the samples outside the periods.</summary>
    private static double Ramp(int n, int total, int ramp)
    {
        if (ramp <= 0)
        {
            return 1.0;
        }

        double rise = n < ramp ? 0.5 * (1.0 - Math.Cos(Math.PI * n / ramp)) : 1.0;
        int fromEnd = total - 1 - n;
        double fall = fromEnd < ramp ? 0.5 * (1.0 - Math.Cos(Math.PI * fromEnd / ramp)) : 1.0;
        return Math.Min(rise, fall);
    }
}
