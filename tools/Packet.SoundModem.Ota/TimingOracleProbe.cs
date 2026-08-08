using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Ota;

/// <summary>What one oracle burst scored, arm by arm.</summary>
/// <param name="Causal">The deployed receiver: the DPLL recovers symbol timing from the noisy
/// audio, as it does on the air.</param>
/// <param name="Oracle">The same receiver reading its symbols at the instants a noise-free view of
/// this very burst says they are.</param>
/// <param name="Grid">The same receiver on a perfect constant-rate clock at the noise-free phase -
/// the transmitted symbol grid itself, which is what a smoothed non-causal timing estimate is
/// trying to be.</param>
/// <param name="Control">The oracle schedule deliberately slipped half a symbol - the instrument's
/// own falsification arm (see <see cref="TimingOracleProbe"/>).</param>
/// <param name="Symbols">Symbols the noise-free pass scheduled.</param>
internal readonly record struct OracleBurstResult(
    bool Causal, bool Oracle, bool Grid, bool Control, int Symbols);

/// <summary>
/// The symbol-timing ceiling probe for rx-roadmap workstream 4 (two-pass, non-causal burst
/// processing): how much of the receiver's loss is the symbol clock's, and therefore how much any
/// timing improvement - a second pass over the buffered burst above all - could ever return.
/// </summary>
/// <remarks>
/// <para><b>The oracle.</b> The fading realisation is drawn from the burst seed <em>before</em> the
/// noise is (see <c>WattersonChannel.Apply</c>), so the same seed at
/// <see cref="double.PositiveInfinity"/> SNR yields the identical channel with no noise on it. A
/// first pass over that noise-free copy records the sample index of every symbol instant its DPLL
/// chose; the measured arm then decodes the <em>noisy</em> copy reading its symbols at exactly
/// those instants. Nothing else changes: the DPLL still runs, still sees every transition and still
/// drives <see cref="PacketDcd"/>, so carrier detection - which gates the deframer - is held
/// constant and the arms differ in one variable only, when each symbol is read.</para>
/// <para>This is an upper bound on the timing half of workstream 4, and a generous one: no causal
/// or non-causal estimator can do better than a noise-free view of the burst it is about to decode.
/// If the oracle arm barely beats the causal arm, no amount of two-pass machinery will either, and
/// the workstream's ~0.5 dB timing claim is dead where it stands. It is not a bound on the other
/// halves of that workstream (converged equaliser taps from symbol 0, fade-envelope erasure flags),
/// which this probe says nothing about.</para>
/// <para><b>Why a control arm.</b> A seam that quietly does nothing produces exactly the shape of a
/// negative result - two arms that agree - so the probe carries its own falsification: the same
/// schedule slipped half a symbol, which must destroy the decode. If the control arm decodes as
/// well as the others the instrument is not driving the receiver and no verdict may be read from
/// it (the campaign's standing lesson: audit the instrument as hard as the code).</para>
/// <para><b>Single branch on purpose.</b> The probe drives one <see cref="BpskModem"/>, not the
/// deployed diversity bank: a bank's branches each recover their own clock and the winner is chosen
/// by content dedupe, which would blur what the oracle is measuring. Both arms use the same single
/// branch, so the A/B is internally fair; the absolute rates sit below the bank's ladder and are
/// not comparable to it.</para>
/// </remarks>
internal sealed class TimingOracleProbe
{
    private readonly string _mode;
    private readonly int _rate;
    private readonly int _baud;
    private readonly int _frameBytes;
    private readonly int _txDelayMs;
    private readonly SimModem _sim;

    /// <summary>Builds the probe for a BPSK catalogue mode.</summary>
    /// <param name="mode">A bpsk* catalogue mode (the probe reaches into the PSK receive chain).</param>
    /// <param name="frameBytes">AX.25 frame size, as the sim ladder's <c>--frame-bytes</c>.</param>
    /// <param name="txDelayMs">Preamble run-in, as the sim ladder's <c>--txdelay</c>.</param>
    public TimingOracleProbe(string mode, int frameBytes = 60, int txDelayMs = 0)
    {
        if (!IsSupported(mode))
        {
            throw new ArgumentException(
                $"the timing oracle drives the BPSK receive chain; '{mode}' is not a bpsk mode",
                nameof(mode));
        }

        _mode = mode;
        _baud = int.Parse(mode.AsSpan("bpsk".Length), null);
        _rate = ModemCatalog.DspRateFor(mode);
        _frameBytes = frameBytes;
        _txDelayMs = txDelayMs;
        _sim = new SimModem(mode);
    }

    /// <summary>Whether <paramref name="mode"/> has a receive chain this probe can drive.</summary>
    public static bool IsSupported(string mode) =>
        mode.StartsWith("bpsk", StringComparison.Ordinal)
        && ModemCatalog.IsKnown(mode)
        && int.TryParse(mode.AsSpan("bpsk".Length), out _);

    /// <summary>Samples per symbol at this mode's DSP rate.</summary>
    public int SamplesPerSymbol => _rate / _baud;

    /// <summary>Runs one burst through all three arms.</summary>
    /// <param name="seed">Burst seed - payload, fade and noise realisation, as the sim ladder.</param>
    /// <param name="kind">Channel geometry.</param>
    /// <param name="snrDb">SNR in a 3 kHz bandwidth.</param>
    /// <param name="cfoHz">Carrier offset.</param>
    /// <param name="control">Run the half-symbol-slipped falsification arm as well.</param>
    public OracleBurstResult Run(
        int seed, SimChannelKind kind, double snrDb, double cfoHz = 0, bool control = true)
    {
        byte[] frame = SimModem.Frame(_frameBytes, seed);
        float[] active = _sim.RenderBurst(frame, _txDelayMs);

        // The same channel seed as the sim ladder uses, so a probe point sits on the ladder's own
        // realisations; +inf SNR replays that realisation with the noise left off.
        int channelSeed = seed + 3_000_000;
        float[] clean = SimChannel.Apply(
            active, _rate, kind, double.PositiveInfinity, channelSeed, cfoHz: cfoHz);
        float[] noisy = SimChannel.Apply(active, _rate, kind, snrDb, channelSeed, cfoHz: cfoHz);

        List<long> schedule = RecordInstants(clean);
        int leadIn = (int)(0.5 * _rate);
        List<long> grid = Grid(schedule, leadIn, leadIn + active.Length, noisy.Length);
        bool causal = Decode(noisy, frame, schedule: null);
        bool oracle = Decode(noisy, frame, schedule);
        bool gridded = Decode(noisy, frame, grid);

        bool slipped = false;
        if (control)
        {
            int half = SamplesPerSymbol / 2;
            var offset = new List<long>(schedule.Count);
            foreach (long instant in schedule)
            {
                offset.Add(instant + half);
            }

            slipped = Decode(noisy, frame, offset);
        }

        return new OracleBurstResult(causal, oracle, gridded, slipped, schedule.Count);
    }

    /// <summary>
    /// Decodes the burst on a constant-rate clock at the transmitted grid displaced by
    /// <paramref name="offsetSamples"/>, for one fixed displacement applied to every burst alike.
    /// </summary>
    /// <remarks>
    /// This is the difference between a finding and a lottery: <see cref="AnyPhaseDelivers"/> picks
    /// its winning phase per burst, knowing which one decoded, so it measures diversity a receiver
    /// cannot select. A displacement that wins at the SAME value across every burst is instead a
    /// systematic bias in where the clock samples, which any receiver can correct once and keep.
    /// </remarks>
    public bool DisplacedGridDelivers(
        int seed, SimChannelKind kind, double snrDb, int offsetSamples, double cfoHz = 0)
    {
        byte[] frame = SimModem.Frame(_frameBytes, seed);
        float[] active = _sim.RenderBurst(frame, _txDelayMs);
        int channelSeed = seed + 3_000_000;
        float[] clean = SimChannel.Apply(
            active, _rate, kind, double.PositiveInfinity, channelSeed, cfoHz: cfoHz);
        float[] noisy = SimChannel.Apply(active, _rate, kind, snrDb, channelSeed, cfoHz: cfoHz);
        int leadIn = (int)(0.5 * _rate);
        List<long> grid = Grid(
            RecordInstants(clean), leadIn, leadIn + active.Length, noisy.Length);
        for (int i = 0; i < grid.Count; i++)
        {
            grid[i] += offsetSamples;
        }

        return Decode(noisy, frame, grid);
    }

    /// <summary>
    /// Decodes the burst on a constant-rate clock at every one of the mode's sampling phases and
    /// reports whether any of them delivers - the absolute static-timing ceiling, with hindsight no
    /// estimator has. Nothing that fixes a sampling phase, by any means, can beat this arm.
    /// </summary>
    public bool AnyPhaseDelivers(int seed, SimChannelKind kind, double snrDb, double cfoHz = 0)
    {
        byte[] frame = SimModem.Frame(_frameBytes, seed);
        float[] active = _sim.RenderBurst(frame, _txDelayMs);
        float[] noisy = SimChannel.Apply(active, _rate, kind, snrDb, seed + 3_000_000, cfoHz: cfoHz);

        for (int phase = 0; phase < SamplesPerSymbol; phase++)
        {
            var schedule = new List<long>((noisy.Length / SamplesPerSymbol) + 1);
            for (long t = phase; t < noisy.Length; t += SamplesPerSymbol)
            {
                schedule.Add(t);
            }

            if (Decode(noisy, frame, schedule))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The transmitted symbol grid: a perfect constant-rate clock whose phase is the noise-free
    /// pass's own, taken as the median offset over the burst so a fade that yanks the clean clock
    /// cannot drag the estimate.
    /// </summary>
    /// <remarks>
    /// The rate is exact rather than estimated because the sim shares one clock between modulator
    /// and receiver (there is no sample-clock skew axis yet - rx-roadmap workstream 6 item 4), so
    /// the true instants really are an arithmetic progression and this arm is the strongest timing
    /// genie the rig can offer: what a non-causal estimator would converge on if its smoothing were
    /// perfect. Only instants inside the burst carry timing information - the lead-in of a
    /// noise-free pass is exact zeros, where the DPLL free-runs at an arbitrary phase - so the
    /// estimate is taken from the burst region alone.
    /// </remarks>
    private List<long> Grid(IReadOnlyList<long> instants, int burstStart, int burstEnd, int total)
    {
        int sps = SamplesPerSymbol;
        long reference = -1;
        var deltas = new List<long>(instants.Count);
        foreach (long instant in instants)
        {
            if (instant < burstStart || instant >= burstEnd)
            {
                continue;
            }

            if (reference < 0)
            {
                reference = instant % sps;
            }

            // Median around a reference, not of the raw remainders: a phase near zero puts its
            // samples at both ends of [0, sps) and their plain median lands half a symbol out -
            // exactly the control arm's deliberate error, arrived at by accident.
            long delta = ((instant % sps) - reference + (long)(1.5 * sps)) % sps - (sps / 2);
            deltas.Add(delta);
        }

        long phase = 0;
        if (deltas.Count > 0)
        {
            deltas.Sort();
            phase = ((reference + deltas[deltas.Count / 2]) % sps + sps) % sps;
        }

        var grid = new List<long>((total / sps) + 1);
        for (long t = phase; t < total; t += sps)
        {
            grid.Add(t);
        }

        return grid;
    }

    /// <summary>The symbol instants a noise-free view of this burst reads at.</summary>
    private List<long> RecordInstants(float[] clean)
    {
        var instants = new List<long>(clean.Length / SamplesPerSymbol);
        BpskModem rx = NewModem();
        rx.Demodulator.SymbolInstantObserver = instants.Add;
        Feed(rx, clean);
        return instants;
    }

    private bool Decode(float[] audio, byte[] sentFrame, IReadOnlyList<long>? schedule)
    {
        bool matched = false;
        BpskModem rx = NewModem(f => matched |= f.AsSpan().SequenceEqual(sentFrame));
        rx.Demodulator.SymbolInstantSchedule = schedule;
        Feed(rx, audio);
        return matched;
    }

    private BpskModem NewModem(Action<byte[]>? sink = null) =>
        new(_rate, sink ?? (static _ => { }), crc: true, baud: _baud);

    private void Feed(BpskModem rx, float[] audio)
    {
        // Daemon-sized blocks, as SimModem.Decode: the streaming path, not one giant span.
        int block = Math.Max(1, _rate / 10);
        for (int pos = 0; pos < audio.Length; pos += block)
        {
            rx.Process(audio.AsSpan(pos, Math.Min(block, audio.Length - pos)));
        }
    }

    /// <summary>The mode this probe drives.</summary>
    public string Mode => _mode;
}
