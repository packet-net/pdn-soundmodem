namespace Packet.SoundModem.Modems;

/// <summary>
/// The timing phases a PSK demodulator decides every symbol at: the recovered clock instant
/// itself first (it drives DCD, the constellation and the hard bit sink), then a little early
/// and a little late. Each phase decides the same symbols with its own reference state and
/// feeds its own deframer through the soft sink's phase index; the modem delivers whichever
/// copy passes, once. The matched filter, the mixer, the clock and the offset window are
/// shared, so the cost is the decision stage and two deframers per branch.
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
    /// <summary>How far the early and late phases sit from the recovered instant, in symbols.
    /// Five per cent is two samples at 40 samples per symbol. Measured against 3 and 8 per cent
    /// on the qpsk600, qpsk2400, bpsk300 and bpsk1200 knee rows (N=200) and found equivalent
    /// within a few frames; the middle value is kept.</summary>
    internal const double Fraction = 0.05;

    /// <summary>Offsets from the recovered instant, in symbols, phase 0 being the instant.</summary>
    internal static readonly double[] PhaseFractions = [0, -Fraction, Fraction];

    /// <summary>How many phases the soft sinks are fed: the index they carry runs 0 to this
    /// minus one.</summary>
    internal static int PhaseCount => PhaseFractions.Length;
}
