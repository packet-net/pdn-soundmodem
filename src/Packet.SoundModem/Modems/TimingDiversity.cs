namespace Packet.SoundModem.Modems;

/// <summary>
/// The timing phases a PSK demodulator decides every symbol at: the recovered clock instant
/// itself first (it drives DCD, the constellation and the hard bit sink), then a little early
/// and a little late in steps. Each phase decides the same symbols with its own reference state
/// and feeds its own deframer through the soft sink's phase index; the modem delivers whichever
/// copy passes, once. The matched filter, the mixer, the clock and the offset window are
/// shared, so the cost is the decision stage and a deframer per extra phase per branch.
/// </summary>
/// <remarks>
/// What it buys is the burst that sits on the Reed-Solomon edge. On the 2026-08-21 off-air
/// qpsk600 fixture a fixed clock swept through all 40 sample phases decodes at exactly one
/// phase, two samples from where the DPLL settles, with exactly the 8 corrected bytes the code
/// allows - and nowhere else. A receiver gets one clock; this gives its decision stage a look
/// either side of it, which is the same idea as the frequency-diversity banks applied to time.
/// </remarks>
internal static class TimingDiversity
{
    /// <summary>Step between adjacent phases, in symbols: one sample at 40 samples per symbol.
    /// A single pair at 5 % was first measured against 3 and 8 % and found equivalent; three
    /// pairs at 2.5 % then measured better again on every knee row (qpsk600 -1/0 dB 131/185 ->
    /// 146/187 of 200, bpsk300 -5/-4 dB 164/195 -> 174/197, bpsk1200 +1/+2 dB 160/197 ->
    /// 173/198), reaching 7.5 % either side.</summary>
    internal const double Step = 0.025;

    /// <summary>Phases either side of the instant.</summary>
    internal const int Pairs = 3;

    /// <summary>Offsets from the recovered instant, in symbols, phase 0 being the instant, then
    /// early/late pairs stepping outward.</summary>
    internal static readonly double[] PhaseFractions = Build();

    /// <summary>How many phases the soft sinks are fed: the index they carry runs 0 to this
    /// minus one.</summary>
    internal static int PhaseCount => PhaseFractions.Length;

    /// <summary>The widest offset, in symbols.</summary>
    internal static double Reach => Pairs * Step;

    private static double[] Build() => Build(Step, Pairs);

    /// <summary>Builds a phase set with a mode-specific step and pair count, in the shared
    /// order: the instant first, then early/late pairs stepping outward. The PSK modes take
    /// the constants above; a mode whose decision stage runs at a much coarser
    /// samples-per-symbol ratio measures its own step and says what it measured (see
    /// <see cref="C4fskModem"/>).</summary>
    internal static double[] Build(double step, int pairs)
    {
        var fractions = new double[(2 * pairs) + 1];
        fractions[0] = 0;
        for (int pair = 1; pair <= pairs; pair++)
        {
            fractions[(2 * pair) - 1] = -pair * step;
            fractions[2 * pair] = pair * step;
        }

        return fractions;
    }
}
