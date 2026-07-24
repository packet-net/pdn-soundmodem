using M0LTE.Fec;
using M0LTE.Dsp;
using Packet.SoundModem.Ms110d.Fec;
using M0LTE.Ofdm;

namespace Packet.SoundModem.Ms110d;

/// <summary>
/// Appendix D 3 kHz serial-tone receiver: autobaud — one receiver decodes the whole ladder
/// (D.4). Native 9600 Hz in; complex baseband at T/2 (4800 Hz) behind an 1800 Hz mixer and
/// the SRRC receive filter; matched-filter acquisition of the preamble Fixed subsection over
/// a ±75 Hz coarse CFO grid; downcount/WID Walsh decode with checksum verification; then
/// probe-trained fractionally-spaced NLMS DFE demodulation (design §2.5/§2.6) with the
/// mandatory D.5.4.5 exits (unconditional EOM scan, block limit, terminate command).
/// </summary>
/// <remarks>
/// Phase A limitations, stated: acquisition requires the 9-Walsh-symbol Fixed subsection,
/// i.e. transmissions with M ≥ 2 super-frames (the M = 1 single-symbol preamble is generated
/// on TX but not yet acquired); clock skew is followed by the slow per-probe timing tracker —
/// measured (Ms110dClockSkewTests, 2026-07-23): ±50 ppm decodes bit-exact with wide margin
/// (breaking points ±700 ppm on ~4 s bursts, ±300–400 ppm on ~11 s — tolerance is
/// burst-duration-dependent; longer transmissions unmeasured); WN 0 has no timing tracker at
/// all (chip clock assumed nominal, as in loopback and the D.6 simulation rigs).
/// </remarks>
public sealed class Ms110dDemodulator
{
    private const int RingBits = 16;
    // 65536 T/2 samples ≈ 13.7 s. Must exceed the longest 3 kHz Long-interleaver block
    // (WN 1/2: 256 frames × 96 symbols = 10.24 s on air) plus tail: TurboReequalize
    // re-reads the whole block from the ring at FinishBlock time, and Interpolate cannot
    // detect overwritten slots (BlockSamplesResident is the backstop).
    private const int RingSize = 1 << RingBits;
    private const int ChipsFixed = 288;           // 9 × 32 (M ≥ 2)
    private const int ChipsSuperframe = 576;
    private const int InterpHalf = 4;             // 8-tap interpolator
    private static readonly double[] BinsHz = [-75, -50, -25, 0, 25, 50, 75];

    private readonly Ms110dDemodOptions _options;
    private FirFilter _rxFilterRe;
    private FirFilter _rxFilterIm;
    private readonly float[] _rxPulse;
    private readonly Cf[] _mixTable;
    private readonly Cf[][] _mfReference; // [bin][288] conj(fixed chip)·e^{−jθ_bin·i}

    // T/2 ring.
    private readonly Cf[] _ring = new Cf[RingSize];
    private long _written;
    private int _mixPhase;
    private bool _decimateToggle;
    private readonly double[] _combEnergy = new double[2];

    // Channel-truth genie (diagnostic instrument, phase-b-plan §B0). A noise-free copy of
    // the SAME channel realization runs through an identical front end (its own
    // filter/mixer state; the shared carrier/timing model applies to both), and the
    // ESTIMATION reads — training rows, carrier refinement, probe timing, turbo channel
    // estimation — come from this ring while DETECTION stays on the noisy one. A genie run
    // is the achievability bound of the current detector under perfect channel
    // observation: genie ≪ measured says tracking/estimation is the deficit, genie ≈
    // measured says detection is. Acquisition and the WN 0 Walsh path take no estimation
    // reads and are unaffected. Genie numbers are a diagnostic bound, never performance
    // evidence.
    private Cf[]? _genieRing;
    private FirFilter? _genieFilterRe;
    private FirFilter? _genieFilterIm;
    private int _genieMixPhase;
    private bool _genieDecimateToggle;
    private long _genieWritten;
    // Measured per-T/2-sample additive-noise power: mean |noisy − clean|² between the two
    // rings. Genie LS solves add σ²·Σweight to the FF Gram diagonal — the term the noisy
    // rows contribute implicitly — so the genie computes the true-channel MMSE equalizer,
    // not the zero-forcing one (first WN4 Poor genie run: ZF inverted the fading notches
    // and came out 30× WORSE than the baseline it is meant to bound).
    private double _genieNoiseSum;
    private long _genieNoiseCount;

    // Search state. The metric ring keeps recent per-candidate metrics so the accepted
    // peak can be moved back to the EARLIEST multipath arrival (design §2.6): on the
    // D-LXV static channels the equalizer geometry only works cursor-first — echoes are
    // post-cursor for the feedback taps; locking a later path puts them outside the
    // feed-forward span.
    private double _bestMetric;
    private long _bestStart = -1;
    private int _bestBin;
    private readonly double[] _metricRing = new double[256];
    private readonly byte[] _metricBinRing = new byte[256];

    /// <summary>Highest matched-filter metric seen since construction/reset — an
    /// acquisition diagnostic (normalized 0…1).</summary>
    public double PeakSearchMetric { get; private set; }

    // Lock state.
    private Ms110dRxState _state = Ms110dRxState.Searching;
    private double _chip0;        // absolute T/2 position of chip 0 of the matched super-frame
    private double _tau;          // slow timing correction, T/2 units
    private double _omega;        // carrier phase increment per T/2 sample (residual CFO)
    private double _omegaAcquired; // ω at data start — the tracking loop's clamp centre
    private double _thetaBase;    // carrier phase at _chip0
    private Ms110dLockInfo? _lock;
    private Ms110dMode? _mode;
    private Ms110dInterleaverParams? _il;
    private long _dataStartChip;
    private bool _trackingInitialized;

    // Tracking state (WN ≥ 1).
    private Dfe? _dfe;
    private int _ffLead;
    private float _initRidge;      // initial LS ridge (MMSE-scaled per mini-probe class)
    private float _trackRidge;     // per-probe re-solve ridge
    private Cf[] _known = [];
    private Cf[] _decisions = [];  // ring of past FbTaps decisions, newest at [0]
    private readonly Ms110dScrambler _scrambler = new();
    private long _frameChip;
    private int _frameInBlock;
    private int _badProbes;
    private double _probeMse;
    private double _probeGainRef;
    private double _probeEnergyRef;
    private int _collapsedProbes;

    // Tracking state (WN 0).
    private Wid0WalshModem? _walsh;
    private long _symbolChip;
    private int _symbolInBlock;
    private int _weakSymbols;
    private Cf _walshPhaseAcc;
    private int _walshPhaseCount;

    // Block/burst assembly.
    private TailBitingViterbiDecoder? _viterbi;
    private PunctureSpec? _puncture;
    private Ms110dInterleaver? _interleaver;
    private ConvolutionalCode? _code;
    private float[] _blockLlrs = [];
    private int _blockLlrCount;
    private int _blockIndex;
    private readonly List<byte> _burstBits = [];
    private readonly List<long> _blockFrameChips = [];

    // Fading detector state (see ProcessFrame). The per-frame statistic (CFO-immune
    // fractional tap change) has heavily overlapping LEVEL distributions between AWGN at
    // mask SNR and Poor between fades (measured WN4: AWGN median 0.045/max 0.12; Poor
    // median 0.033/max 0.33) — the discriminator is temporal structure: fading recurs as
    // excursions above a min-tracking noise floor (the EnergyBusyDetector pattern), AWGN
    // stays in a tight band. Enter on 2 excursions ≤ 24 frames apart (one 1 Hz fade event
    // spans several frames), exit after 32 excursion-free frames.
    private const float FadeExcursionRatio = 3.5f;
    private const int FadeEnterWindowFrames = 24;
    private const int FadeExitFrames = 32;
    private double _fadeFloor;
    private bool _fadeFloorSeeded;
    private int _framesSinceExcursion = int.MaxValue / 2;
    private bool _fading;
    private bool _fadingLatched;
    private bool _terminate;

    /// <summary>Creates the receiver.</summary>
    public Ms110dDemodulator(Ms110dDemodOptions? options = null)
    {
        _options = options ?? new Ms110dDemodOptions();
        _rxPulse = RxPulse();
        _rxFilterRe = new FirFilter(_rxPulse);
        _rxFilterIm = new FirFilter(_rxPulse);

        // 1800 Hz = 3/16 of a cycle per 9600 Hz sample: sixteenth-turn table, index step 3.
        _mixTable = new Cf[16];
        for (int i = 0; i < 16; i++)
        {
            _mixTable[i] = Cf.CmplxConj((float)(2.0 * Math.PI * i / 16.0));
        }

        byte[] fixedChips = new PreambleGenerator(0, 2).FixedSectionChips();
        _mfReference = new Cf[BinsHz.Length][];
        for (int b = 0; b < BinsHz.Length; b++)
        {
            var reference = new Cf[ChipsFixed];
            for (int i = 0; i < ChipsFixed; i++)
            {
                double theta = 2.0 * Math.PI * BinsHz[b] * i / Ms110dTables.SymbolRate;
                reference[i] = Ms110dTables.Psk8[fixedChips[i]].Conj() * Cf.CmplxConj((float)theta);
            }

            _mfReference[b] = reference;
        }
    }

    /// <summary>Debug-only: fires per equalized, descrambled data symbol.</summary>
    public event Action<Cf>? DataSymbolEqualized;

    /// <summary>Diagnostic: one formatted line per processed frame (probe gain/MSE,
    /// timing, carrier, fading state). Formatted only when subscribed — the library
    /// itself writes no console output (issue #65); hosts that want the old
    /// <c>MS110D_DEBUG</c> stderr behaviour subscribe and print.</summary>
    public event Action<string>? FrameDiagnostics;

    /// <summary>Diagnostic: fires once per interleaver block BEFORE the first decode, with
    /// the block index and the first-pass wire-order LLRs (fetch order, pre-deinterleave,
    /// pre-turbo). The buffer is reused per block — receivers must copy what they keep.
    /// Comparing sign(LLR) against the re-encoded transmitted stream gives the uncoded
    /// channel-bit error rate, the §5.3 uncoded-vs-coded split (phase-b-plan §B0).</summary>
    public event Action<int, float[]>? FirstPassBlockLlrs;

    /// <summary>Turbo blocks that reached a decode fixed point (since construction/Reset).</summary>
    public int TurboConverged { get; private set; }

    /// <summary>Turbo blocks reverted to the first-pass decode (no fixed point in 5 iterations).</summary>
    public int TurboReverted { get; private set; }

    /// <summary>Turbo blocks aborted mid-re-equalization (samples no longer available).</summary>
    public int TurboAborted { get; private set; }

    /// <summary>Blocks where the turbo gate declined to run (QAM16, non-resident samples,
    /// or the WN 0 path — the flat-channel skip retired with the DFE-re-solve fallback,
    /// §B2.3).</summary>
    public int TurboSkipped { get; private set; }

    /// <summary>Fresh non-anchored probe re-solves after tracking collapse (phase-b-plan
    /// §B2.1c; since construction/Reset). Zero on healthy runs — a nonzero count says
    /// decision-directed tracking collapsed and was restarted from the probe alone.</summary>
    public int CollapseResolves { get; private set; }

    /// <summary>Fires for every decoded input-data block.</summary>
    public event Action<Ms110dRxBlock>? BlockDecoded;

    /// <summary>Fires when a burst ends (any D.5.4.5 exit).</summary>
    public event Action<Ms110dBurst>? BurstCompleted;

    /// <summary>Current receiver state.</summary>
    public Ms110dRxState State => _state;

    /// <summary>Autobaud result while locked, else null.</summary>
    public Ms110dLockInfo? Lock => _lock;

    /// <summary>True from preamble detection until the burst ends.</summary>
    public bool CarrierDetect => _state != Ms110dRxState.Searching;

    /// <summary>Terminate-receive command (D.5.4.5.2): ends any in-progress burst and
    /// returns to acquisition.</summary>
    public void Terminate()
    {
        _terminate = true;
    }

    /// <summary>Clears all receive state back to Searching.</summary>
    public void Reset()
    {
        _written = 0;
        _mixPhase = 0;
        _decimateToggle = false;
        _combEnergy[0] = _combEnergy[1] = 0;
        Array.Clear(_ring);
        _rxFilterRe = new FirFilter(_rxPulse);
        _rxFilterIm = new FirFilter(_rxPulse);
        if (_genieRing is not null)
        {
            Array.Clear(_genieRing);
            _genieFilterRe = new FirFilter(_rxPulse);
            _genieFilterIm = new FirFilter(_rxPulse);
            _genieMixPhase = 0;
            _genieDecimateToggle = false;
            _genieWritten = 0;
            _genieNoiseSum = 0;
            _genieNoiseCount = 0;
        }

        PeakSearchMetric = 0; // documented as "since construction/reset"
        TurboConverged = 0;
        TurboReverted = 0;
        TurboAborted = 0;
        TurboSkipped = 0;
        CollapseResolves = 0;
        EndBurst();
    }

    /// <summary>Channel-truth genie seam (diagnostic): feeds the noise-free copy of the
    /// same channel realization. Feed in bounded chunks interleaved with
    /// <see cref="Process"/> — genie ahead of every read, but never more than ~1.7 s ahead
    /// of the noisy stream (the rings are ~13.7 s circles and turbo re-reads whole
    /// interleaver blocks, so running far ahead would overwrite history both streams still
    /// need; this throws rather than silently wrap). While enabled, estimation reads use
    /// this stream and detection keeps the noisy one — see the field comment.</summary>
    public void WriteGenie(ReadOnlySpan<float> samples)
    {
        if (_genieRing is null)
        {
            _genieRing = new Cf[RingSize];
            _genieFilterRe = new FirFilter(_rxPulse);
            _genieFilterIm = new FirFilter(_rxPulse);
        }

        if (_genieWritten + (samples.Length / 2) - _written > 8192)
        {
            throw new InvalidOperationException(
                "genie stream too far ahead of the noisy stream: interleave WriteGenie/Process in chunks");
        }

        foreach (float sample in samples)
        {
            Cf mixed = _mixTable[_genieMixPhase] * sample;
            _genieMixPhase = (_genieMixPhase + 3) & 15;
            float re = _genieFilterRe!.Next(mixed.Re);
            float im = _genieFilterIm!.Next(mixed.Im);
            _genieDecimateToggle = !_genieDecimateToggle;
            if (_genieDecimateToggle)
            {
                continue;
            }

            _genieRing[_genieWritten & (RingSize - 1)] = new Cf(re, im);
            _genieWritten++;
        }
    }

    /// <summary>Feeds received audio at 9600 Hz.</summary>
    public void Process(ReadOnlySpan<float> samples)
    {
        if (_genieRing is not null && _genieWritten < _written + (samples.Length / 2))
        {
            throw new InvalidOperationException(
                "genie stream behind the noisy stream: call WriteGenie with the covering span before Process");
        }

        foreach (float sample in samples)
        {
            Cf mixed = _mixTable[_mixPhase] * sample;
            _mixPhase = (_mixPhase + 3) & 15;
            float re = _rxFilterRe.Next(mixed.Re);
            float im = _rxFilterIm.Next(mixed.Im);
            _decimateToggle = !_decimateToggle;
            if (_decimateToggle)
            {
                continue;
            }

            PushT2(new Cf(re, im));
        }
    }

    private void PushT2(Cf sample)
    {
        long p = _written;
        int parity = (int)(p & 1);
        _combEnergy[parity] += sample.Cnorm();
        if (p >= 576)
        {
            _combEnergy[parity] -= _ring[(p - 576) & (RingSize - 1)].Cnorm();
        }

        _ring[p & (RingSize - 1)] = sample;
        if (_genieRing is not null && p < _genieWritten)
        {
            _genieNoiseSum += (sample - _genieRing[p & (RingSize - 1)]).Cnorm();
            _genieNoiseCount++;
        }

        _written = p + 1;

        if (_terminate)
        {
            _terminate = false;
            if (_state == Ms110dRxState.Tracking)
            {
                CompleteBurst(Ms110dBurstEndReason.Terminated);
                return;
            }

            if (_state == Ms110dRxState.ReadingPreamble)
            {
                BackToSearch();
            }
        }

        switch (_state)
        {
            case Ms110dRxState.Searching:
                SearchStep();
                break;
            case Ms110dRxState.ReadingPreamble:
                TryReadPreamble();
                break;
            case Ms110dRxState.Tracking:
                TryTrack();
                break;
        }
    }

    // ------------------------------------------------------------------ acquisition

    private void SearchStep()
    {
        long p = _written - 1;
        if (p < 574)
        {
            return;
        }

        long s = p - 574;
        double energy = _combEnergy[(int)(s & 1)];
        if (energy <= 1e-12)
        {
            return;
        }

        double best = 0;
        int bestBin = 0;
        for (int b = 0; b < BinsHz.Length; b++)
        {
            double metric = Metric(s, b, energy, null);
            if (metric > best)
            {
                best = metric;
                bestBin = b;
            }
        }

        _metricRing[s & 255] = best;
        _metricBinRing[s & 255] = (byte)bestBin;

        if (best > PeakSearchMetric)
        {
            PeakSearchMetric = best;
        }

        if (best > _options.SyncThreshold && best > _bestMetric)
        {
            _bestMetric = best;
            _bestStart = s;
            _bestBin = bestBin;
        }

        if (_bestStart >= 0 && s - _bestStart > 192)
        {
            AcceptPeak();
        }
    }

    private double Metric(long s, int bin, double energy, Cf[]? segments)
    {
        Cf[] reference = _mfReference[bin];
        double sum = 0;
        for (int k = 0; k < 9; k++)
        {
            var c = Cf.Zero;
            int chip = 32 * k;
            for (int i = 0; i < 32; i++, chip++)
            {
                c += _ring[(s + (2 * chip)) & (RingSize - 1)] * reference[chip];
            }

            if (segments is not null)
            {
                segments[k] = c;
            }

            sum += c.Abs();
        }

        return sum / (Math.Sqrt(ChipsFixed) * Math.Sqrt(energy) + 1e-12);
    }

    private void AcceptPeak()
    {
        long s = _bestStart;
        int bin = _bestBin;

        // Earliest-arrival selection: walk back up to 9 ms (the widest D-LXV spread,
        // 21.6 symbols = 44 T/2 samples) and take the first candidate within −4.4 dB of
        // the best — on equal-power multipath every arrival correlates comparably, and
        // the DFE needs the cursor on the FIRST one. On a clean channel the matched-filter
        // response collapses within ±1 sample, so this is a no-op there.
        double floor = Math.Max(_options.SyncThreshold, 0.6 * _bestMetric);
        for (long candidate = Math.Max(574, s - 44); candidate < s; candidate++)
        {
            if (_metricRing[candidate & 255] >= floor)
            {
                s = candidate;
                bin = _metricBinRing[candidate & 255];
                break;
            }
        }

        double energy = _combEnergy[(int)(s & 1)];

        // Sub-sample timing from a parabolic fit through the metric at s−1, s, s+1.
        var segments = new Cf[9];
        double m0 = Metric(s, bin, energy, segments);
        double mm = Metric(s - 1, bin, _combEnergy[(int)((s - 1) & 1)], null);
        double mp = Metric(s + 1, bin, _combEnergy[(int)((s + 1) & 1)], null);
        double denom = mm - (2 * m0) + mp;
        double delta = Math.Abs(denom) > 1e-9 ? 0.5 * (mm - mp) / denom : 0;
        delta = Math.Clamp(delta, -1, 1);

        // Fine CFO from the phase progression across the nine 32-chip segment correlations
        // (±37.5 Hz unambiguous — inside the 25 Hz grid residual).
        var rotation = Cf.Zero;
        for (int k = 0; k < 8; k++)
        {
            rotation += segments[k + 1] * segments[k].Conj();
        }

        double fineHz = rotation.Arg() * Ms110dTables.SymbolRate / (2.0 * Math.PI * 32.0);
        double cfoHz = BinsHz[bin] + fineHz;

        _chip0 = s + delta;
        _tau = 0;
        _omega = 2.0 * Math.PI * cfoHz / (2.0 * Ms110dTables.SymbolRate);
        _thetaBase = 0;
        _lock = new Ms110dLockInfo(-1, Ms110dInterleaverKind.Short, 7, cfoHz);
        _state = Ms110dRxState.ReadingPreamble;
        _bestMetric = 0;
        _bestStart = -1;
    }

    private void TryReadPreamble()
    {
        // Need the whole matched super-frame plus interpolation margin.
        if (_written < (long)Math.Ceiling(_chip0 + (2 * ChipsSuperframe * 1.0)) + (2 * InterpHalf) + 2)
        {
            return;
        }

        Span<byte> countDibits = stackalloc byte[4];
        for (int j = 0; j < 4; j++)
        {
            countDibits[j] = ReadWalshSymbol(ChipsFixed + (32 * j), Ms110dTables.CntPn, 32 * j);
        }

        Span<byte> widDibits = stackalloc byte[5];
        for (int j = 0; j < 5; j++)
        {
            widDibits[j] = ReadWalshSymbol(ChipsFixed + 128 + (32 * j), Ms110dTables.WidPn, 32 * j);
        }

        if (!PreambleGenerator.TryDecodeCount(countDibits, out int count) ||
            !PreambleGenerator.TryDecodeWid(widDibits, out int wn, out Ms110dInterleaverKind il, out int k) ||
            !IsSupported(wn) ||
            !Ms110dInterleaverParams.Has3k(wn, il))
        {
            // Has3k: at low SNR a corrupted WID can pass its checksum yet name a
            // (waveform, interleaver) pair Table D-XXXVII does not define — e.g.
            // (WN 0, UltraShort). That is a failed acquisition candidate, not a
            // crash: Get3k throwing here killed the receiver mid-burst (found by
            // the Poor WN0 mask run at −1 dB, seed 500, ~2.8M bits in).
            BackToSearch();
            return;
        }

        RefineCarrier(0, SuperframeChips(count, wn, il, k));
        _lock = new Ms110dLockInfo(wn, il, k, _lock!.CfoHz);
        _mode = Ms110dMode.Mode3k(wn);
        _il = Ms110dInterleaverParams.Get3k(wn, il);
        ConvolutionalCode code = k == 9 ? ConvolutionalCode.K9 : ConvolutionalCode.K7;
        _code = code;
        _viterbi = new TailBitingViterbiDecoder(code);
        _puncture = Ms110dPuncture.Get(code, _mode.CodeRate);
        _interleaver = new Ms110dInterleaver(_il.SizeBits, _il.Increment);
        _blockLlrs = new float[_il.SizeBits];
        _blockLlrCount = 0;
        _blockIndex = 0;
        _burstBits.Clear();
        _dataStartChip = ChipsSuperframe * (long)(count + 1);
        _trackingInitialized = false;
        _state = Ms110dRxState.Tracking;
    }

    /// <summary>How many trailing preamble super-frames the carrier re-fit may use: all
    /// the super-frames we know exist (from the matched one to data start), capped at 4
    /// (~1 s — enough baseline to average Rayleigh phase drift out of the CFO fit).</summary>
    private int TailRefineSuperframes()
    {
        return (int)Math.Clamp(_dataStartChip / ChipsSuperframe, 1, 4);
    }

    private static bool IsSupported(int wn)
    {
        return wn is >= 0 and <= 8 or 13;
    }

    private byte ReadWalshSymbol(int startChip, ReadOnlySpan<byte> pn, int pnOffset)
    {
        Span<Cf> corr = stackalloc Cf[4];
        byte[] w1 = Ms110dTables.Walsh[1];
        byte[] w2 = Ms110dTables.Walsh[2];
        byte[] w3 = Ms110dTables.Walsh[3];
        for (int i = 0; i < 32; i++)
        {
            Cf r = ReadChip(startChip + i) * Ms110dTables.Psk8[pn[pnOffset + i]].Conj();
            corr[0] += r;
            corr[1] = w1[i & 3] == 0 ? corr[1] + r : corr[1] - r;
            corr[2] = w2[i & 3] == 0 ? corr[2] + r : corr[2] - r;
            corr[3] = w3[i & 3] == 0 ? corr[3] + r : corr[3] - r;
        }

        byte bestDibit = 0;
        float best = corr[0].Cnorm();
        for (byte s = 1; s < 4; s++)
        {
            float m = corr[s].Cnorm();
            if (m > best)
            {
                best = m;
                bestDibit = s;
            }
        }

        return bestDibit;
    }

    /// <summary>Known chips of the last <paramref name="superframes"/> preamble
    /// super-frames before data start (downcounts n−1 … 0).</summary>
    private static byte[] TailSuperframeChips(int superframes, int wn, Ms110dInterleaverKind il, int k)
    {
        var chips = new byte[superframes * ChipsSuperframe];
        for (int i = 0; i < superframes; i++)
        {
            SuperframeChips(superframes - 1 - i, wn, il, k).CopyTo(chips, i * ChipsSuperframe);
        }

        return chips;
    }

    private static byte[] SuperframeChips(int count, int wn, Ms110dInterleaverKind il, int k)
    {
        byte[] known = new byte[ChipsSuperframe];
        new PreambleGenerator(0, 2).FixedSectionChips().CopyTo(known, 0);
        PreambleGenerator.CountSectionChips(count).CopyTo(known, ChipsFixed);
        PreambleGenerator.WidSectionChips(wn, il, k).CopyTo(known, ChipsFixed + 128);
        return known;
    }

    /// <summary>Least-squares fits the residual carrier phase/frequency over fully known
    /// preamble chips at <paramref name="baseChip"/> and re-tunes the carrier model
    /// anchored inside the measurement window. Longer windows (several super-frames)
    /// matter on fading channels: a single 240 ms super-frame reads Rayleigh phase drift
    /// as CFO — a ~1 s baseline averages it out.</summary>
    private void RefineCarrier(long baseChip, byte[] knownChips)
    {
        int count = knownChips.Length / 32;
        Span<double> phases = count <= 72 ? stackalloc double[72] : new double[count];
        Span<double> weights = count <= 72 ? stackalloc double[72] : new double[count];
        phases = phases[..count];
        weights = weights[..count];
        double previous = 0;
        for (int j = 0; j < count; j++)
        {
            var c = Cf.Zero;
            for (int i = 0; i < 32; i++)
            {
                int chip = (32 * j) + i;
                // Estimation read: the carrier fit is channel estimation (genie-eligible).
                c += ReadChipEst(baseChip + chip) * Ms110dTables.Psk8[knownChips[chip]].Conj();
            }

            double phi = c.Arg();
            while (phi - previous > Math.PI)
            {
                phi -= 2 * Math.PI;
            }

            while (phi - previous < -Math.PI)
            {
                phi += 2 * Math.PI;
            }

            phases[j] = phi;
            weights[j] = c.Abs();
            previous = phi;
        }

        // Weighted linear regression of phase vs. symbol index.
        double sw = 0, sx = 0, sy = 0, sxx = 0, sxy = 0;
        for (int j = 0; j < count; j++)
        {
            double w = weights[j];
            sw += w;
            sx += w * j;
            sy += w * phases[j];
            sxx += w * j * j;
            sxy += w * j * phases[j];
        }

        double det = (sw * sxx) - (sx * sx);
        if (Math.Abs(det) < 1e-9 || sw < 1e-9)
        {
            return;
        }

        double slope = ((sw * sxy) - (sx * sy)) / det;      // rad per channel symbol
        double intercept = ((sxx * sy) - (sx * sxy)) / det; // rad at symbol index 0

        // Anchor the correction at the window midpoint (symbol j's phase sits at its
        // centre, chip 32j+16); slope converts to rad per T/2 sample by ÷64.
        double mid = (count - 1) / 2.0;
        double href = (2.0 * (baseChip + 16 + (32 * mid))) + _tau;
        RetuneCarrier(href, intercept + (slope * mid), slope / 64.0);
        _lock = _lock! with { CfoHz = _lock.CfoHz + (slope / 64.0 * 2.0 * Ms110dTables.SymbolRate / (2.0 * Math.PI)) };
    }

    private void BackToSearch()
    {
        _state = Ms110dRxState.Searching;
        _lock = null;
        _bestMetric = 0;
        _bestStart = -1;
    }

    // ------------------------------------------------------------------ sample access

    private double PositionOfChip(double chip)
    {
        return _chip0 + (2.0 * chip) + _tau;
    }

    private Cf ReadT2(double halfChips)
    {
        double pos = _chip0 + halfChips + _tau;
        var value = Interpolate(pos, _ring, _written);
        double theta = _thetaBase + (_omega * (pos - _chip0));
        return value * Cf.CmplxConj((float)theta);
    }

    /// <summary>Estimation-side read: the genie ring when the genie is enabled, otherwise
    /// the normal noisy read. Same carrier/timing model either way — the genie replaces
    /// the DATA under the estimators, not the receiver's own state.</summary>
    private Cf ReadT2Est(double halfChips)
    {
        if (_genieRing is null)
        {
            return ReadT2(halfChips);
        }

        double pos = _chip0 + halfChips + _tau;
        // Bound by the NOISY stream's availability: the genie replaces sample values, not
        // the receiver's causality — the interpolator's head truncation must be identical
        // in both rings or a genie run differs even when fed the noisy stream itself.
        var value = Interpolate(pos, _genieRing, Math.Min(_genieWritten, _written));
        double theta = _thetaBase + (_omega * (pos - _chip0));
        return value * Cf.CmplxConj((float)theta);
    }

    private Cf ReadChip(double chip)
    {
        return ReadT2(2.0 * chip);
    }

    private Cf ReadChipEst(double chip)
    {
        return ReadT2Est(2.0 * chip);
    }

    private Cf Interpolate(double pos, Cf[] ring, long written)
    {
        long i0 = (long)Math.Floor(pos);
        double frac = pos - i0;
        var acc = Cf.Zero;
        for (int j = -InterpHalf + 1; j <= InterpHalf; j++)
        {
            double u = j - frac;
            double w;
            if (Math.Abs(u) < 1e-9)
            {
                w = 1;
            }
            else
            {
                w = Math.Sin(Math.PI * u) / (Math.PI * u) * (0.5 + (0.5 * Math.Cos(Math.PI * u / InterpHalf)));
            }

            long idx = i0 + j;
            if (idx >= 0 && idx < written)
            {
                acc += ring[idx & (RingSize - 1)] * (float)w;
            }
        }

        return acc;
    }

    private bool HaveSamplesForChip(double chip)
    {
        return _written > (long)Math.Ceiling(PositionOfChip(chip)) + InterpHalf + 1;
    }

    // ------------------------------------------------------------------ tracking

    private void TryTrack()
    {
        if (_mode!.Wn == 0)
        {
            TrackWalsh();
            return;
        }

        if (!_trackingInitialized)
        {
            if (!HaveSamplesForChip(_dataStartChip + _mode.K + 4))
            {
                return;
            }

            InitializeDfe();
            _trackingInitialized = true;
        }

        while (_state == Ms110dRxState.Tracking &&
               HaveSamplesForChip(_frameChip + _mode.U + _mode.K + 4))
        {
            ProcessFrame();
        }
    }

    private void InitializeDfe()
    {
        // Same stale-extrapolation concern as the WN0 path: re-fit the carrier over the
        // final super-frames before training (see TrackWalsh).
        int tail = TailRefineSuperframes();
        RefineCarrier(
            _dataStartChip - (tail * ChipsSuperframe),
            TailSuperframeChips(tail, _lock!.WaveformNumber, _lock.Interleaver, _lock.ConstraintLength));
        _omegaAcquired = _omega;

        // Tap counts per design §2.5. Leads are sized so the feed-forward window spans
        // roughly ±2 ms around the cursor: forward to collect echo energy (the 3 ms path
        // of the WID 2 static rig at K=48), and BACKWARD far enough that a path earlier
        // than the locked one stays equalizable — on the fading Poor channel the lock can
        // land on the later path while the earlier one is faded, and its return puts a
        // −2 ms (−9.6 T/2) pre-cursor into the window.
        (int ff, int fb, int lead, float initRidge, float trackRidge) = _mode!.K switch
        {
            // K=48 (WN1/2, rate 1/8 & 1/4, run at the −3/0 dB and 5 dB static gates) has the
            // widest DFE — 32 FF + 22 FB = 54 complex taps — yet the FEWEST data symbols per
            // frame (U=48) to excite them, at the LOWEST SNR of the ladder. A weak ridge lets
            // the off-cursor feed-forward taps fit noise (measured: WN1 AWGN 4.5E-5 vs the
            // 1E-5 gate; a shrunk 12-tap FF cleared AWGN but starved the static echo). The MMSE
            // ridge at −3 dB is order-1×trace (noise ≈ signal), so K=48 uses a strong ridge
            // toward zero (initial) / toward the current taps (per-probe): off-cursor taps
            // collapse on flat AWGN while the static rig's echo-excited taps, carrying real
            // signal, survive it. K=32/24 keep their original (already-green) light ridge.
            48 => (32, 22, 16, 1.0f, 1.0f),
            24 => (16, 6, 8, 1e-3f, 0.15f),
            _ => (24, 12, 13, 1e-3f, 0.15f),
        };
        _dfe = new Dfe(ff, fb);
        _ffLead = lead;
        _initRidge = initRidge;
        _trackRidge = trackRidge;

        // Known symbols for chips [dataStart−576, dataStart+K): the final super-frame
        // (count = 0) plus the preamble-ending probe (design §2.4).
        int k = _mode.K;
        _known = new Cf[ChipsSuperframe + k];
        byte[] chips = new byte[ChipsSuperframe];
        new PreambleGenerator(0, 2).FixedSectionChips().CopyTo(chips, 0);
        PreambleGenerator.CountSectionChips(0).CopyTo(chips, ChipsFixed);
        PreambleGenerator.WidSectionChips(_lock!.WaveformNumber, _lock.Interleaver, _lock.ConstraintLength)
            .CopyTo(chips, ChipsFixed + 128);
        for (int i = 0; i < ChipsSuperframe; i++)
        {
            _known[i] = Ms110dTables.Psk8[chips[i]];
        }

        MiniProbe.Get(k, boundary: false).CopyTo(_known, ChipsSuperframe);

        // Regularized LS solve over the last half super-frame + probe.
        long baseChip = _dataStartChip - ChipsSuperframe;
        _dfe.BeginTraining();
        Span<Cf> window = stackalloc Cf[ff];
        Span<Cf> past = stackalloc Cf[fb];
        for (int n = ChipsFixed; n < ChipsSuperframe + k; n++)
        {
            FillWindowEst(baseChip + n, window);
            for (int j = 0; j < fb; j++)
            {
                past[j] = _known[n - 1 - j];
            }

            _dfe.AddTrainingRow(window, past, _known[n]);
        }

        // RLS forgetting policy — a DOCUMENTED DEVIATION from design §2.5 (issue #64):
        // λ = 1 − ln10/U ties the exponential window to the frame (memory U/ln10 ≈ 0.43·U
        // symbols, i.e. a 10× down-weight per data span), so the per-probe anchored batch
        // solve, not the RLS recursion, owns cross-frame memory. §2.5 specified a fixed
        // λ = 0.995 (≈200-symbol/83 ms memory, set by the 1 Hz coherence time); for U=48
        // the frame-tied window is only ~21 symbols ≈ 8.7 ms — far shorter than the physics
        // needs, noisier than it has to be. Which policy wins is a measurement question:
        // the Phase B RLS-vs-NLMS A/B (phase-b-plan §B2.4) settles it; until then this is
        // the measured-baseline value, kept so evidence stays comparable.
        _dfe.BeginRls(
            _options.RlsForgettingFactor ?? (float)(1.0 - Math.Log(10.0) / _mode.U), pInit: 1.0f);
        _dfe.SeedRlsFromTraining(_initRidge, pFallback: 1.0f, ffNoisePower: GenieNoisePower());
        _dfe.SolveTraining(regularization: _initRidge, ffNoisePower: GenieNoisePower());
        _dfe.BeginTraining();

        // Seed the decision history with the probe tail and measure the training MSE.
        _decisions = new Cf[fb];
        for (int j = 0; j < fb; j++)
        {
            _decisions[j] = _known[ChipsSuperframe + k - 1 - j];
        }

        double mse = 0;
        double energy = 0;
        var gain = Cf.Zero;
        for (int i = 0; i < k; i++)
        {
            int n = ChipsSuperframe + i;
            FillWindowEst(baseChip + n, window);
            for (int j = 0; j < fb; j++)
            {
                past[j] = _known[n - 1 - j];
            }

            Cf y = _dfe.Equalize(window, past);
            gain += y * _known[n].Conj();
            Cf err = y - _known[n];
            mse += err.Cnorm();
            energy += window[_ffLead].Cnorm();
        }

        _probeMse = mse / k;
        _probeGainRef = gain.Abs() / k;
        _probeEnergyRef = energy / k;
        _badProbes = 0;
        _collapsedProbes = 0;
        _frameChip = _dataStartChip + k;
        _frameInBlock = 0;
    }

    private void FillWindow(double symbolChip, Span<Cf> window)
    {
        double h = 2.0 * symbolChip;
        for (int i = 0; i < window.Length; i++)
        {
            window[i] = ReadT2(h + _ffLead - i);
        }
    }

    /// <summary>Estimation-side window fill (training rows, channel estimation): genie ring
    /// when enabled, otherwise identical to <see cref="FillWindow"/>.</summary>
    private void FillWindowEst(double symbolChip, Span<Cf> window)
    {
        double h = 2.0 * symbolChip;
        for (int i = 0; i < window.Length; i++)
        {
            window[i] = ReadT2Est(h + _ffLead - i);
        }
    }

    private void ProcessFrame()
    {
        Ms110dMode mode = _mode!;
        Dfe dfe = _dfe!;
        Span<Cf> window = stackalloc Cf[dfe.FfTaps];
        Span<Cf> probePast = stackalloc Cf[dfe.FbTaps];
        // Genie mode fills a second, estimation-side window per symbol: detection reads
        // stay noisy, adaptation rows read the clean stream (see the genie field comment).
        bool genie = _genieRing is not null;
        Span<Cf> estWindow = stackalloc Cf[dfe.FfTaps];

        // Probe-directed batch-LS: known symbols accumulate into the Gram with
        // authoritative weight; the ridge-regularized solve (anchored to current taps)
        // handles the probe's rank deficiency (≤16 distinct patterns for 36+ taps).
        bool boundary = (_frameInBlock + 2) % _il!.Frames == 0;
        Cf[] probe = MiniProbe.Get(mode.K, boundary);
        long probeChip = _frameChip + mode.U;
        Cf[] startTaps = dfe.SnapshotTaps();
        var probePhase = Cf.Zero;
        double mse = 0;
        double probeEnergy = 0;
        int statRows = 0;
        for (int i = dfe.FbTaps; i < mode.K; i++)
        {
            // Estimation reads: the probe solve and its gain/MSE discriminators.
            FillWindowEst(probeChip + i, window);
            for (int j = 0; j < dfe.FbTaps; j++)
            {
                probePast[j] = probe[i - 1 - j];
            }

            Cf y = dfe.Equalize(window, probePast);
            probePhase += y * probe[i].Conj();
            mse += (y - probe[i]).Cnorm();
            probeEnergy += window[_ffLead].Cnorm();
            statRows++;
            dfe.AddTrainingRow(window, probePast, probe[i], weight: 6f);
        }

        // Collapse detection (phase-b-plan §B2.1c; WN7 autopsy: once decision-directed
        // tracking collapses it PERSISTS, because the next probe solve is anchored to the
        // collapsed taps). Collapse = the BAD-PROBE criterion with signal energy present,
        // two consecutive probes:
        //  - probe correlation below the Phase-A signal-lost discriminator's line
        //    (< max(0.10, 0.45·healthy reference)): correlation normalizes by the burst's
        //    own healthy level, so this is SNR-invariant — WN1's −3 dB AWGN probes are
        //    noisy but correlated (an earlier absolute-MSE test misfired there and cost
        //    the AWGN gate), while a hard collapse kills correlation at any SNR, however
        //    early in the burst it strikes.
        //  - probe-window energy ≥ ¼ of its healthy reference (−6 dB, the deep-fade
        //    threshold): signal is PRESENT, so this is a collapse, not a fade. During a
        //    fade, coasting on the anchor is right and a fresh solve would fit noise;
        //    a bad probe WITHOUT energy stays the fade/signal-lost path's business.
        double preMse = mse / statRows;
        double probeEnergyMean = probeEnergy / statRows;
        double probeGain = probePhase.Abs() / mode.K;
        bool collapsed = probeGain < Math.Max(0.10, 0.45 * _probeGainRef) &&
            probeEnergyMean >= 0.25 * _probeEnergyRef;
        _collapsedProbes = collapsed ? _collapsedProbes + 1 : 0;
        bool freshSolve = _collapsedProbes >= 2;
        if (freshSolve)
        {
            // Fresh non-anchored re-solve: discard the accumulator (the collapsed frame's
            // DD rows are poison), rebuild the probe-only rows, and zero the taps so the
            // anchored ridge degenerates to a plain ridge toward zero. The probe alone is
            // rank-deficient; the ridge sends the unobserved directions to zero — a cold
            // restart of tracking on the spot, with P re-seeded from the fresh Gram.
            dfe.BeginTraining();
            for (int i = dfe.FbTaps; i < mode.K; i++)
            {
                FillWindowEst(probeChip + i, window);
                for (int j = 0; j < dfe.FbTaps; j++)
                {
                    probePast[j] = probe[i - 1 - j];
                }

                dfe.AddTrainingRow(window, probePast, probe[i], weight: 6f);
            }

            Span<Cf> zeroTaps = stackalloc Cf[startTaps.Length];
            dfe.LoadTaps(zeroTaps);
            CollapseResolves++;
            _collapsedProbes = 0;
        }

        dfe.SeedRlsFromTraining(_trackRidge, pFallback: 1.0f, ffNoisePower: GenieNoisePower());
        dfe.SolveTraining(regularization: _trackRidge, anchorToCurrentTaps: true, ffNoisePower: GenieNoisePower());

        // §B2.1 per-probe phase re-anchor. The anchored ridge solve corrects the tap
        // SHAPE only fractionally per probe — correct for slow shape drift, but for the
        // channel's common rotation it leaves a steady-state phase lag θ·(1−α)/α that
        // scatter measurement showed parking WN7 in a half-locked limbo (probe gain ≈ 0.4,
        // preMse ≈ 0.75). Phase is a single parameter with ~K known symbols to estimate
        // it: re-equalize the probe with the post-solve taps and take out the residual
        // common phase error at gain 1. Gated on the same 0.10 absolute correlation floor
        // as the signal-lost discriminator — below it the probe carries no usable phase
        // and rotating by its noise during a deep fade would break the coasting rule.
        var postPhase = Cf.Zero;
        for (int i = dfe.FbTaps; i < mode.K; i++)
        {
            FillWindowEst(probeChip + i, window);
            for (int j = 0; j < dfe.FbTaps; j++)
            {
                probePast[j] = probe[i - 1 - j];
            }

            postPhase += dfe.Equalize(window, probePast) * probe[i].Conj();
        }

        if (postPhase.Abs() / statRows >= 0.10)
        {
            dfe.RotateTaps(Cf.CmplxConj((float)postPhase.Arg()));
        }

        Cf[] endTaps = dfe.SnapshotTaps();
        if (freshSolve)
        {
            // The old anchor is the collapsed solution — this frame's data span is
            // equalized with the fresh solve held constant (no trajectory from garbage),
            // and the tap-rotation CFO trim below sees zero rotation.
            startTaps = endTaps;
        }

        // Residual-CFO trim from the mean tap rotation between consecutive probe solves.
        var tapRotation = Cf.Zero;
        for (int i = 0; i < endTaps.Length; i++)
        {
            tapRotation += endTaps[i] * startTaps[i].Conj();
        }

        if (tapRotation.Cnorm() > 1e-12)
        {
            int frameT2 = 2 * (mode.U + mode.K);
            double deltaOmega = -0.1 * tapRotation.Arg() / frameT2;
            double window3Hz = 2.0 * Math.PI * 3.0 / (2.0 * Ms110dTables.SymbolRate);
            deltaOmega = Math.Clamp(
                deltaOmega, (_omegaAcquired - window3Hz) - _omega, (_omegaAcquired + window3Hz) - _omega);
            RetuneCarrier((2.0 * (probeChip + (mode.K / 2))) + _tau, 0, deltaOmega);
        }

        // Start accumulating rows for the NEXT solve: DD rows join the next probe's rows
        // to complete the excitation (the probe alone is rank-deficient).
        dfe.BeginTraining();

        // Fading statistic: fractional tap change per frame BEYOND the common rotation.
        // Pure residual CFO rotates all taps together — after removing the common rotation
        // the change is ≈ solve noise — while fading reshapes the tap vector. (The previous
        // detector thresholded the rotation ANGLE itself, i.e. it was a residual-CFO
        // detector that happened to separate the two simulation rigs — issue #64.)
        // EWMA'd so one noisy solve cannot flip the mode, with hysteresis so per-frame
        // chatter cannot mix bidirectional and single-pass LLR statistics in one block.
        // A fresh re-solve frame is excluded outright: startTaps was reassigned to the
        // fresh solution, so its tapChange is identically zero — folding that into the
        // min-tracking floor would collapse the floor and turn every later frame into an
        // excursion.
        double tapChange = 0;
        if (!freshSolve)
        {
            double startNorm = 0, changeNorm = 0;
            if (tapRotation.Cnorm() > 1e-12)
            {
                Cf rot = tapRotation * (float)(1.0 / tapRotation.Abs());
                for (int i = 0; i < endTaps.Length; i++)
                {
                    startNorm += startTaps[i].Cnorm();
                    changeNorm += (endTaps[i] - (rot * startTaps[i])).Cnorm();
                }
            }

            tapChange = startNorm > 1e-12 ? changeNorm / startNorm : 0;
            if (!_fadeFloorSeeded)
            {
                _fadeFloor = tapChange;
                _fadeFloorSeeded = true;
            }
            else
            {
                // Min-tracking floor: drops instantly, recovers 5 %/frame — so a fade's own
                // excursions cannot drag the floor up to meet them.
                _fadeFloor = Math.Min(tapChange, (_fadeFloor * 1.05) + 1e-4);
            }

            bool excursion = tapChange > FadeExcursionRatio * _fadeFloor;
            if (excursion)
            {
                if (_framesSinceExcursion <= FadeEnterWindowFrames)
                {
                    _fading = true;
                }

                _framesSinceExcursion = 0;
            }
            else if (++_framesSinceExcursion > FadeExitFrames)
            {
                _fading = false;
            }
        }

        // RLS weight: 1.0 on fading channels (full tracking), 0.1 on static/AWGN
        // (minimal noise accumulation while still providing some adaptation).
        // Path selection LATCHES per burst (§B2.1): a channel that faded does not become
        // AWGN mid-burst, but the excursion detector chatters on continuous fading (the
        // min-tracking floor never sees a quiet stretch to settle on — measured 40/130
        // WN7 Poor frames misclassified flat, running the 3-pass path mid-collapse). A
        // collapse latches too: two consecutive uncorrelated probes with signal present
        // cannot happen on a flat channel.
        _fadingLatched |= _fading || freshSolve;
        bool fading = _fadingLatched;
        float rlsWeight = fading ? 1.0f : 0.1f;

        _scrambler.Reset();
        float ddGate = DdGateRadius(mode.Modulation);

        // §B2.1a: probe-anchored retrospective tap trajectory for fading frames. startTaps
        // is anchored at the previous probe's centre (K/2 before the data span), endTaps at
        // the following probe's centre (K/2 beyond it) — both known here, because the probe
        // AFTER the span is solved before any data symbol is equalized (the block-buffered
        // architecture's free non-causality). Fraction for data symbol u:
        // f(u) = (u + K/2)/(U + K). The common rotation φ between the anchors is applied as
        // a phase ramp over linearly-interpolated de-rotated taps — chord interpolation of
        // a rotation understates amplitude (cos(φ/2) at midspan), and tens-of-degrees
        // per-frame rotation on the 1 Hz Poor channel is exactly the WN7/WN8 autopsy
        // mechanism, so the explicit phase ramp is the load-bearing part.
        int nTaps = endTaps.Length;
        Span<Cf> trajectoryEnd = stackalloc Cf[nTaps];
        Span<Cf> tapsPrev = stackalloc Cf[nTaps];
        Span<Cf> tapsCur = stackalloc Cf[nTaps];
        double phi = 0;
        if (fading)
        {
            phi = tapRotation.Cnorm() > 1e-12 ? tapRotation.Arg() : 0.0;
            Cf derot = Cf.CmplxConj((float)phi);
            for (int i = 0; i < nTaps; i++)
            {
                trajectoryEnd[i] = endTaps[i] * derot;
            }
        }

        if (mode.Modulation == Ms110dModulation.Qam16)
        {
            if (fading)
            {
                TapTrajectory(startTaps, trajectoryEnd, phi, FrameFraction(mode, 0), tapsCur);
                dfe.LoadTaps(tapsCur);
            }

            for (int u = 0; u < mode.U; u++)
            {
                if (fading && u > 0)
                {
                    tapsCur.CopyTo(tapsPrev);
                    TapTrajectory(startTaps, trajectoryEnd, phi, FrameFraction(mode, u), tapsCur);
                    dfe.TranslateTaps(tapsPrev, tapsCur);
                }

                FillWindow(_frameChip + u, window);
                int scrambleNibble = _scrambler.NextQam(0, 4);
                Cf y = dfe.Equalize(window, _decisions);
                Cf clean = Slice(y, mode.Modulation);
                DataSymbolEqualized?.Invoke(y);
                PushMaxLogLlrs(y, Ms110dTables.Qam16, null, 4, 10.0f, scrambleNibble);
                Span<Cf> row = window;
                if (genie)
                {
                    FillWindowEst(_frameChip + u, estWindow);
                    row = estWindow;
                }

                // Same decision-confidence gate as the interpolated fading path below:
                // QAM16's tight regions make ungated DD updates the fastest way to
                // self-destruct (WN8 autopsy — phase AND amplitude).
                if ((y - clean).Cnorm() < ddGate)
                {
                    dfe.RlsUpdate(row, _decisions, clean, weight: rlsWeight);
                    dfe.AddTrainingRow(row, _decisions, clean, weight: 0.25f);
                }

                PushDecision(clean);
            }
        }
        else if (!fading)
        {
            // 3-pass equalization from three tap seeds (end, start, midpoint of the
            // probe-to-probe trajectory), outputs averaged. The passes share the frame's
            // noise, so this is non-causal smoothing of the tap estimate rather than
            // diversity; it buys the WN5 rate-3/4 point its margin at the 6 dB AWGN mask.
            // Every pass must start from the frame's true decision history (the previous
            // probe's tail): passes 2/3 previously inherited the PREVIOUS pass's
            // end-of-frame decisions, feeding the frame's head through feedback taps
            // filled with its own tail (issue #64).
            Span<Cf> pass1 = stackalloc Cf[mode.U];
            Span<Cf> pass2 = stackalloc Cf[mode.U];
            Span<Cf> frameStartDecisions = stackalloc Cf[_decisions.Length];
            _decisions.CopyTo(frameStartDecisions);
            for (int u = 0; u < mode.U; u++)
            {
                FillWindow(_frameChip + u, window);
                Cf rotor = Ms110dTables.Psk8[_scrambler.NextPsk(0)];
                Cf y = dfe.Equalize(window, _decisions);
                Cf descrambled = y * rotor.Conj();
                Cf clean = Slice(descrambled, mode.Modulation);
                pass1[u] = descrambled;
                Span<Cf> row = window;
                if (genie)
                {
                    FillWindowEst(_frameChip + u, estWindow);
                    row = estWindow;
                }

                dfe.RlsUpdate(row, _decisions, clean * rotor, weight: rlsWeight);
                if ((descrambled - clean).Cnorm() < ddGate)
                {
                    dfe.AddTrainingRow(row, _decisions, clean * rotor, weight: 0.25f);
                }

                PushDecision(clean * rotor);
            }

            dfe.LoadTaps(startTaps);
            frameStartDecisions.CopyTo(_decisions);
            _scrambler.Reset();
            for (int u = 0; u < mode.U; u++)
            {
                FillWindow(_frameChip + u, window);
                Cf rotor = Ms110dTables.Psk8[_scrambler.NextPsk(0)];
                Cf y = dfe.Equalize(window, _decisions);
                Cf descrambled = y * rotor.Conj();
                Cf clean = Slice(descrambled, mode.Modulation);
                pass2[u] = descrambled;
                Span<Cf> row = window;
                if (genie)
                {
                    FillWindowEst(_frameChip + u, estWindow);
                    row = estWindow;
                }

                dfe.RlsUpdate(row, _decisions, clean * rotor, weight: rlsWeight);
                PushDecision(clean * rotor);
            }

            Cf[] midTaps = new Cf[endTaps.Length];
            for (int i = 0; i < midTaps.Length; i++)
            {
                midTaps[i] = (startTaps[i] + endTaps[i]) * 0.5f;
            }

            dfe.LoadTaps(midTaps);
            frameStartDecisions.CopyTo(_decisions);
            _scrambler.Reset();
            for (int u = 0; u < mode.U; u++)
            {
                FillWindow(_frameChip + u, window);
                Cf rotor = Ms110dTables.Psk8[_scrambler.NextPsk(0)];
                Cf y = dfe.Equalize(window, _decisions);
                Cf descrambled = y * rotor.Conj();
                Cf clean = Slice(descrambled, mode.Modulation);
                Cf averaged = (pass1[u] + pass2[u] + descrambled) * (1f / 3f);
                DataSymbolEqualized?.Invoke(averaged);
                PushLlrs(averaged, mode.Modulation);
                Span<Cf> row = window;
                if (genie)
                {
                    FillWindowEst(_frameChip + u, estWindow);
                    row = estWindow;
                }

                dfe.RlsUpdate(row, _decisions, clean * rotor, weight: rlsWeight);
                PushDecision(clean * rotor);
            }
        }
        else
        {
            // Fading channel (§B2.1a): single pass with the tap base interpolated between
            // the bracketing probe solves and the RLS deviation riding on top
            // (TranslateTaps tracks the residual only). Before Phase B2 this pass
            // equalized the whole span from endTaps — the tail anchor applied statically
            // to the head, the 107 ms staleness every U=256 mode pays and the WN7/WN8
            // smear amplifier — and U≤96 fading frames took the 3-pass average, which
            // mixes fading states.
            TapTrajectory(startTaps, trajectoryEnd, phi, FrameFraction(mode, 0), tapsCur);
            dfe.LoadTaps(tapsCur);
            for (int u = 0; u < mode.U; u++)
            {
                if (u > 0)
                {
                    tapsCur.CopyTo(tapsPrev);
                    TapTrajectory(startTaps, trajectoryEnd, phi, FrameFraction(mode, u), tapsCur);
                    dfe.TranslateTaps(tapsPrev, tapsCur);
                }

                FillWindow(_frameChip + u, window);
                Cf rotor = Ms110dTables.Psk8[_scrambler.NextPsk(0)];
                Cf y = dfe.Equalize(window, _decisions);
                Cf descrambled = y * rotor.Conj();
                Cf clean = Slice(descrambled, mode.Modulation);
                DataSymbolEqualized?.Invoke(descrambled);
                PushLlrs(descrambled, mode.Modulation);
                Span<Cf> row = window;
                if (genie)
                {
                    FillWindowEst(_frameChip + u, estWindow);
                    row = estWindow;
                }

                // Decision-confidence gate on BOTH the RLS update and the training row: an
                // ungated weight-1.0 update on a wrong decision is precisely the DD
                // self-destruction mechanism of the WN7 autopsy — measured on the scatter
                // rig, the §B2.1c fresh re-solve re-collapsed within ONE frame until the
                // RLS update was gated. When decisions are unusable the interpolated base
                // carries the channel; when they are trustworthy RLS tracks the residual.
                if ((descrambled - clean).Cnorm() < ddGate)
                {
                    dfe.RlsUpdate(row, _decisions, clean * rotor, weight: rlsWeight);
                    dfe.AddTrainingRow(row, _decisions, clean * rotor, weight: 0.25f);
                }

                PushDecision(clean * rotor);
            }
        }

        dfe.SymmetrizeP(pMax: 10f);
        dfe.LoadTaps(endTaps);
        for (int i = 0; i < mode.K; i++)
        {
            PushDecision(probe[i]);
        }

        _probeMse = (0.7 * _probeMse) + (0.3 * preMse);

        TrackProbeTiming(probeChip, probe);

        FrameDiagnostics?.Invoke(
            $"frame@{_frameChip}: gain={probeGain:F3} ref={_probeGainRef:F3} mse={mse / mode.K:F3} " +
            $"tau={_tau:F3} omega={_omega:E2} bad={_badProbes} " +
            $"tapChange={tapChange:F4} floor={_fadeFloor:F4} fading={_fading}/{fading} " +
            $"preMse={preMse:F3} energy={probeEnergyMean:F3}/{_probeEnergyRef:F3} fresh={freshSolve}");

        if (probeGain < Math.Max(0.10, 0.45 * _probeGainRef))
        {
            if (++_badProbes >= 25)
            {
                CompleteBurst(Ms110dBurstEndReason.SignalLost);
                return;
            }
        }
        else
        {
            _badProbes = 0;
            _probeGainRef = (0.95 * _probeGainRef) + (0.05 * probeGain);
            // The energy reference tracks HEALTHY probes only: a long fade must not drag
            // it down (fade-vs-collapse discrimination depends on it), and during a
            // collapse it must stay pinned at the pre-collapse level.
            _probeEnergyRef = (0.95 * _probeEnergyRef) + (0.05 * probeEnergyMean);
        }

        _blockFrameChips.Add(_frameChip);
        _frameChip += mode.U + mode.K;
        _frameInBlock++;
        if (_frameInBlock == _il.Frames)
        {
            _frameInBlock = 0;
            FinishBlock();
            _blockFrameChips.Clear();
        }
    }

    /// <summary>Trajectory fraction of data symbol <paramref name="u"/> between the
    /// bracketing probe-solve anchors: the previous probe's centre sits K/2 before the
    /// data span and the following probe's centre K/2 beyond it (§B2.1a).</summary>
    private static float FrameFraction(Ms110dMode mode, int u)
    {
        return (u + (0.5f * mode.K)) / (mode.U + mode.K);
    }

    /// <summary>Evaluates the §B2.1a probe-anchored tap trajectory at fraction
    /// <paramref name="f"/> ∈ [0,1]: linear interpolation of the de-rotated taps with the
    /// anchors' common rotation φ applied as a phase ramp e^{jφf}. At f = 0 this is
    /// <paramref name="start"/> exactly; at f = 1 the end solve exactly
    /// (<paramref name="endDerotated"/>·e^{jφ}). Internal for the unit test that pins the
    /// magnitude-preserving property chord interpolation lacks.</summary>
    internal static void TapTrajectory(
        ReadOnlySpan<Cf> start, ReadOnlySpan<Cf> endDerotated, double phi, float f, Span<Cf> taps)
    {
        Cf ramp = Cf.Cmplx((float)(phi * f));
        float g = 1f - f;
        for (int i = 0; i < taps.Length; i++)
        {
            taps[i] = ((start[i] * g) + (endDerotated[i] * f)) * ramp;
        }
    }

    /// <summary>Per-modulation DD confidence gate (squared radius). PSK family keeps the
    /// proven 0.4; QAM16 inner-ring min distance is 0.366 so the gate must be tighter to
    /// avoid accepting wrong decisions that self-confirm via feedback.</summary>
    private static float DdGateRadius(Ms110dModulation modulation)
    {
        return modulation switch
        {
            Ms110dModulation.Qam16 => 0.0225f, // (0.15)², ≈0.4× inner-ring min distance 0.366
            Ms110dModulation.Psk8 => 0.16f,    // (0.4)², min distance 0.765 → generous
            _ => 0.4f,                         // BPSK/QPSK proven value
        };
    }

    private void TrackProbeTiming(long probeChip, Cf[] probe)
    {
        Span<double> magnitudes = stackalloc double[3];
        for (int d = 0; d < 3; d++)
        {
            double offset = (d - 1) * 0.5;
            var c = Cf.Zero;
            for (int i = 0; i < probe.Length; i++)
            {
                // Estimation read: probe-peak timing is channel estimation (genie-eligible).
                c += ReadT2Est((2.0 * (probeChip + i)) + offset) * probe[i].Conj();
            }

            magnitudes[d] = c.Abs();
        }

        double denom = magnitudes[0] - (2 * magnitudes[1]) + magnitudes[2];
        if (Math.Abs(denom) < 1e-9)
        {
            return;
        }

        // Slow slew only — real clock skew is ppm-scale; anything faster is estimator noise.
        double delta = 0.5 * (magnitudes[0] - magnitudes[2]) / denom * 0.5;
        _tau += Math.Clamp(0.1 * delta, -0.03, 0.03);
    }

    /// <summary>Applies a phase/frequency correction with the phase model re-anchored at
    /// <paramref name="halfChipsRef"/> — an ω change must not re-rotate history, only the
    /// future (the loop is unstable otherwise, since the anchor sits at chip 0).</summary>
    private void RetuneCarrier(double halfChipsRef, double deltaTheta, double deltaOmega)
    {
        _omega += deltaOmega;
        _thetaBase += deltaTheta - (deltaOmega * halfChipsRef);
    }

    private static Cf Slice(Cf descrambled, Ms110dModulation modulation)
    {
        if (modulation == Ms110dModulation.Bpsk)
        {
            return descrambled.Re >= 0 ? new Cf(1, 0) : new Cf(-1, 0);
        }

        if (modulation == Ms110dModulation.Psk8)
        {
            return NearestPoint(descrambled, Ms110dTables.Psk8);
        }

        if (modulation == Ms110dModulation.Qam16)
        {
            return NearestPoint(descrambled, Ms110dTables.Qam16);
        }

        // QPSK points sit on the axes (Table D-IV → 8PSK symbols 0/2/4/6).
        return Math.Abs(descrambled.Re) >= Math.Abs(descrambled.Im)
            ? new Cf(Math.Sign(descrambled.Re) >= 0 ? 1 : -1, 0)
            : new Cf(0, Math.Sign(descrambled.Im) >= 0 ? 1 : -1);
    }

    private static Cf NearestPoint(Cf y, Cf[] constellation)
    {
        float best = float.MaxValue;
        Cf result = constellation[0];
        for (int s = 0; s < constellation.Length; s++)
        {
            float d = (y - constellation[s]).Cnorm();
            if (d < best)
            {
                best = d;
                result = constellation[s];
            }
        }

        return result;
    }

    private void PushLlrs(Cf descrambled, Ms110dModulation modulation, int scramble = 0)
    {
        if (modulation == Ms110dModulation.Bpsk)
        {
            AddLlr(4f * descrambled.Re);
            return;
        }

        if (modulation == Ms110dModulation.Psk8)
        {
            // Max-log over 8 points. Bit label of ring symbol s = tribit t where
            // Transcode8Psk[t] == s (inverse map precomputed below).
            PushMaxLogLlrs(descrambled, Ms110dTables.Psk8, SymbolToTribit8, 3, 2.0f, 0);
            return;
        }

        if (modulation == Ms110dModulation.Qam16)
        {
            // QAM16 LLRs come from the first-pass PushMaxLogLlrs call with the live 10.0
            // scale; routing QAM16 through here (historically scale 2.0) would silently
            // drop LLR magnitudes 5× — refuse rather than mis-scale.
            throw new InvalidOperationException("QAM16 LLRs must use the first-pass PushMaxLogLlrs path");
        }

        // Table D-IV Gray map: MSB=0 ⇔ {+1, +j}, LSB=0 ⇔ {+1, −j}.
        AddLlr(2f * (descrambled.Re + descrambled.Im));
        AddLlr(2f * (descrambled.Re - descrambled.Im));
    }

    /// <summary>Ring symbol → tribit inverse map for 8PSK LLR bit labels.</summary>
    private static readonly byte[] SymbolToTribit8 = BuildInverseTranscode();

    // Descrambled-domain constellations for the chain-BCJR turbo path (§B2.3). BPSK's
    // labels are the indices themselves; QPSK's points sit on the axes (Table D-IV dibit →
    // ring {0→0, 1→2, 3→4, 2→6}), so ring order [0,2,4,6] carries labels [0,1,3,2].
    private static readonly Cf[] TurboBpsk = [Ms110dTables.Psk8[0], Ms110dTables.Psk8[4]];
    private static readonly Cf[] TurboQpsk =
        [Ms110dTables.Psk8[0], Ms110dTables.Psk8[2], Ms110dTables.Psk8[4], Ms110dTables.Psk8[6]];
    private static readonly byte[] TurboQpskLabels = [0, 1, 3, 2];

    private static byte[] BuildInverseTranscode()
    {
        var inv = new byte[8];
        for (int t = 0; t < 8; t++)
        {
            inv[Ms110dTables.Transcode8Psk[t]] = (byte)t;
        }

        return inv;
    }

    private void PushMaxLogLlrs(Cf y, Cf[] constellation, byte[]? bitLabels, int bits, float scale, int scramble)
    {
        for (int b = 0; b < bits; b++)
        {
            float min0 = float.MaxValue, min1 = float.MaxValue;
            for (int s = 0; s < constellation.Length; s++)
            {
                int label = bitLabels != null ? bitLabels[s] : (s ^ scramble);
                float d = (y - constellation[s]).Cnorm();
                if (((label >> (bits - 1 - b)) & 1) == 0)
                {
                    if (d < min0) min0 = d;
                }
                else
                {
                    if (d < min1) min1 = d;
                }
            }

            AddLlr(scale * (min1 - min0));
        }
    }

    private void AddLlr(float llr)
    {
        _blockLlrs[_blockLlrCount++] = llr;
    }

    private void PushDecision(Cf wireSymbol)
    {
        for (int j = _decisions.Length - 1; j > 0; j--)
        {
            _decisions[j] = _decisions[j - 1];
        }

        if (_decisions.Length > 0)
        {
            _decisions[0] = wireSymbol;
        }
    }

    // ------------------------------------------------------------------ WN 0 tracking

    private void TrackWalsh()
    {
        if (!_trackingInitialized)
        {
            if (!HaveSamplesForChip(_dataStartChip + 2))
            {
                return;
            }

            // Re-estimate the carrier over the final super-frames: the matched super-frame
            // can be many seconds back (M up to 32) and the extrapolated phase stale.
            int tail = TailRefineSuperframes();
            RefineCarrier(
                _dataStartChip - (tail * ChipsSuperframe),
                TailSuperframeChips(tail, _lock!.WaveformNumber, _lock.Interleaver, _lock.ConstraintLength));

            _walsh = new Wid0WalshModem();
            _walsh.Reset();
            _symbolChip = _dataStartChip;
            _symbolInBlock = 0;
            _weakSymbols = 0;
            _walshPhaseAcc = Cf.Zero;
            _walshPhaseCount = 0;
            _trackingInitialized = true;
        }

        Span<Cf> chips = stackalloc Cf[32];
        Span<float> llrs = stackalloc float[2];
        while (_state == Ms110dRxState.Tracking && HaveSamplesForChip(_symbolChip + 34))
        {
            for (int i = 0; i < 32; i++)
            {
                chips[i] = ReadChip(_symbolChip + i);
            }

            _walsh!.Demodulate(chips, llrs, out _, out Cf correlation);
            AddLlr(llrs[0]);
            AddLlr(llrs[1]);

            // Decision-directed carrier: the winning Walsh correlation should be real and
            // positive after descrambling. Average over 8 channel symbols before applying
            // the correction — the per-symbol phase estimate at the −6 dB operating point
            // is too noisy to drive the frequency integrator directly.
            _walshPhaseAcc += correlation;
            if (++_walshPhaseCount == 8)
            {
                if (_walshPhaseAcc.Cnorm() > 1e-12)
                {
                    double err = _walshPhaseAcc.Arg();
                    RetuneCarrier((2.0 * (_symbolChip + 16)) + _tau, 0.4 * err, 0.06 * err / 512.0);
                }

                _walshPhaseAcc = Cf.Zero;
                _walshPhaseCount = 0;
            }

            // Signal-lost discriminator (WN 0): the winning-correlation-to-chip-energy
            // ratio ≈ 0.5 at the −6 dB mask point but ≈ 0.23 on noise alone.
            double sumMag = 0;
            for (int i = 0; i < 32; i++)
            {
                sumMag += chips[i].Abs();
            }

            if (correlation.Abs() < 0.35 * sumMag)
            {
                // ~1.2 s — long enough to ride a deep Poor-channel fade (see the DFE
                // path's discriminator for the rationale).
                if (++_weakSymbols >= 90)
                {
                    CompleteBurst(Ms110dBurstEndReason.SignalLost);
                    return;
                }
            }
            else
            {
                _weakSymbols = 0;
            }

            _symbolChip += 32;
            _symbolInBlock++;
            if (2 * _symbolInBlock == _il!.SizeBits)
            {
                _symbolInBlock = 0;
                _walsh.Reset(); // scramble sequence resets at the interleaver boundary
                FinishBlock();
            }
        }
    }

    // ------------------------------------------------------------------ block/burst

    private void FinishBlock()
    {
        if (_blockLlrCount != _il!.SizeBits)
        {
            throw new InvalidOperationException("interleaver block LLR accounting error");
        }

        FirstPassBlockLlrs?.Invoke(_blockIndex, _blockLlrs);

        var info = new byte[_il.InputBits];
        Ms110dFraming.DecodeBlock(_viterbi!, _puncture!, _interleaver!, _blockLlrs, info);

        // Turbo re-equalization: re-encode decoded info, use as known training,
        // re-equalize with the chain BCJR, and decode again — for every DFE mode except
        // QAM16 (the 5×-LLR-scale trap remains a throw). The flat-channel skip retired
        // with the DFE-re-solve fallback that motivated it (§B2.3): on a flat channel the
        // chain BCJR degenerates to an exact soft-output matched filter — the reason BPSK
        // U>48 was always allowed through — while the WN2 Poor λ A/B caught the skip
        // misclassifying short-frame fading bursts as flat (turbo 2c/158s, 7× BER cost):
        // the excursion statistic is weakest exactly where probes are densest.
        if (_dfe is not null && _mode is not null &&
            _mode.Modulation is not Ms110dModulation.Qam16 &&
            _blockFrameChips.Count == _il.Frames &&
            BlockSamplesResident())
        {
            // The DD rows accumulated since the last probe solve belong to the NEXT probe's
            // solve ("join the next probe's rows to complete the excitation"); the turbo
            // pass re-trains repeatedly on the same Dfe and used to destroy them, leaving
            // the first post-block probe solve probe-only and rank-deficient (issue #65).
            _dfe.SnapshotTraining();
            var firstPass = new byte[info.Length];
            Array.Copy(info, firstPass, info.Length);
            var prevInfo = new byte[info.Length];
            bool converged = false;
            bool aborted = false;
            for (int iter = 0; iter < 5; iter++)
            {
                TurboReequalize(info);
                if (_blockLlrCount != _il.SizeBits)
                {
                    aborted = true; // partial re-equalization; the current decode stands
                    break;
                }

                Array.Copy(info, prevInfo, info.Length);
                Ms110dFraming.DecodeBlock(_viterbi!, _puncture!, _interleaver!, _blockLlrs, info);
                if (info.AsSpan().SequenceEqual(prevInfo))
                {
                    converged = true;
                    break;
                }
            }

            if (converged)
            {
                TurboConverged++;
            }
            else if (aborted)
            {
                TurboAborted++;
            }
            else
            {
                // Five decode→re-equalize→decode rounds without a fixed point: the loop
                // is oscillating, and a self-trained iterate with no fixed point is not
                // evidence (issue #65). Keep the first-pass decode.
                TurboReverted++;
                Array.Copy(firstPass, info, info.Length);
            }

            _dfe.RestoreTraining();
        }
        else
        {
            TurboSkipped++;
        }

        _blockLlrCount = 0;

        int searchFrom = Math.Max(0, _burstBits.Count - 31);
        _burstBits.AddRange(info);
        BlockDecoded?.Invoke(new Ms110dRxBlock(_blockIndex, info));
        _blockIndex++;

        int eom = Ms110dFraming.FindEom(_burstBits, searchFrom);
        if (eom >= 0)
        {
            var payload = new byte[eom];
            _burstBits.CopyTo(0, payload, 0, eom);
            EmitBurst(payload, Ms110dBurstEndReason.Eom);
            return;
        }

        if (_options.MaxInputDataBlocks > 0 && _blockIndex >= _options.MaxInputDataBlocks)
        {
            CompleteBurst(Ms110dBurstEndReason.MaxInputDataBlocks);
        }
    }

    private bool BlockSamplesResident()
    {
        // Turbo re-reads the whole block; a block that has outlived the ring would
        // silently train against overwritten samples (the head frames degrade to LLR
        // erasures the outer code must then bridge). Never trips for the 3 kHz set
        // with RingBits = 16 — this is the backstop for wider future configs.
        double oldest = PositionOfChip(_blockFrameChips[0]) - _dfe!.FfTaps - InterpHalf;
        return oldest > _written - RingSize;
    }

    /// <summary>Measured additive-noise power per complex T/2 sample (genie mode only:
    /// mean |noisy − clean|² between the rings; 0 when the genie is off, leaving every
    /// solve bit-identical to the normal path).</summary>
    private float GenieNoisePower()
    {
        return _genieRing is null || _genieNoiseCount == 0
            ? 0f
            : (float)(_genieNoiseSum / _genieNoiseCount);
    }

    private void TurboReequalize(byte[] info)
    {
        var mode = _mode!;
        var dfe = _dfe!;

        // Save DFE state — turbo must not corrupt tracking for future blocks.
        Cf[] savedTaps = dfe.SnapshotTaps();

        // Re-encode decoded info → fetched (wire-order) bits.
        byte[] fetched = Ms110dFraming.EncodeBlock(_code!, _puncture!, _interleaver!, info);

        // Map fetched bits to expected wire symbols per frame.
        int bitsPerSymbol = mode.Modulation switch
        {
            Ms110dModulation.Bpsk => 1,
            Ms110dModulation.Qpsk => 2,
            Ms110dModulation.Psk8 => 3,
            _ => 4,
        };

        int fb = dfe.FbTaps;
        Span<Cf> window = stackalloc Cf[dfe.FfTaps];
        Span<Cf> past = stackalloc Cf[fb];
        bool genie = _genieRing is not null;
        Span<Cf> estWindow = stackalloc Cf[dfe.FfTaps];
        const int Segments = 4;
        Span<Cf> segH1 = stackalloc Cf[Segments];
        Span<float> segCentre = stackalloc float[Segments];
        _blockLlrCount = 0;
        int bit = 0;

        for (int f = 0; f < _il!.Frames; f++)
        {
            long frameChip = _blockFrameChips[f];
            _scrambler.Reset();

            // Build expected symbols for this frame's data block.
            var expected = new Cf[mode.U];
            for (int u = 0; u < mode.U; u++)
            {
                int symbolNumber = 0;
                for (int b = 0; b < bitsPerSymbol; b++)
                {
                    symbolNumber = (symbolNumber << 1) | (bit < fetched.Length ? fetched[bit++] : 0);
                }

                int wireIndex = mode.Modulation switch
                {
                    Ms110dModulation.Bpsk => _scrambler.NextPsk(symbolNumber == 0 ? 0 : 4),
                    Ms110dModulation.Qpsk => _scrambler.NextPsk(
                        symbolNumber switch { 0 => 0, 1 => 2, 3 => 4, _ => 6 }),
                    Ms110dModulation.Psk8 => _scrambler.NextPsk(
                        Ms110dTables.Transcode8Psk[symbolNumber & 7]),
                    // The FinishBlock gate excludes QAM16 from turbo.
                    _ => throw new InvalidOperationException("turbo re-equalization excludes QAM16"),
                };
                expected[u] = Ms110dTables.Psk8[wireIndex];
            }

            // Batch-LS solve: FF-only (no feedback) for the BPSK BCJR path.
            dfe.BeginTraining();
            for (int j = 0; j < fb; j++)
            {
                past[j] = Cf.Zero;
            }

            for (int u = 0; u < mode.U; u++)
            {
                if (!HaveSamplesForChip(frameChip + u + 2))
                {
                    // Abort mid-block: restore taps AND leave a clean training
                    // accumulator — a half-filled Gram would poison the next probe solve.
                    dfe.LoadTaps(savedTaps);
                    dfe.BeginTraining();
                    return;
                }

                if (genie)
                {
                    FillWindowEst(frameChip + u, window); // training row: estimation read
                }
                else
                {
                    FillWindow(frameChip + u, window);
                }

                dfe.AddTrainingRow(window, past, expected[u], weight: 1.0f);
            }

            // Solve with _trackRidge for all modes.
            dfe.SolveTraining(regularization: _trackRidge, anchorToCurrentTaps: true, ffNoisePower: GenieNoisePower());

            // Chain-BCJR re-equalization for every PSK mode (§B2.2/§B2.3; the FinishBlock
            // gate excludes QAM16). Channel estimation runs in the WIRE domain: the legacy
            // path estimated the echo tap on DESCRAMBLED quantities, where the scrambler's
            // rotor product r(u−d)·r̄(u) phase-scrambles the lag correlation toward zero —
            // the echo model was scrambler-blind (issue #65). The scrambler re-enters the
            // model exactly through the per-position h2 span below.
            _scrambler.Reset();
            var rotors = new Cf[mode.U];
            for (int u = 0; u < mode.U; u++)
            {
                rotors[u] = Ms110dTables.Psk8[_scrambler.NextPsk(0)];
            }

            for (int j = 0; j < fb; j++)
            {
                past[j] = Cf.Zero;
            }

            // Genie: the channel estimates read the clean stream through the SAME
            // re-solved taps; detection (the BCJR input and its noise floor) stays noisy.
            var rxWire = new Cf[mode.U];
            Cf[] estWire = genie ? new Cf[mode.U] : rxWire;
            for (int u = 0; u < mode.U; u++)
            {
                FillWindow(frameChip + u, window);
                rxWire[u] = dfe.Equalize(window, past);
                if (genie)
                {
                    FillWindowEst(frameChip + u, estWindow);
                    estWire[u] = dfe.Equalize(estWindow, past);
                }
            }

            // Per-segment h1 anchors (§B2.1b: per-position h instead of block constants —
            // issue #65). The re-encoded symbols are mid-frame references the probes can
            // never provide, so the intra-frame trajectory is observable HERE — including
            // through fade nulls, where phase moves fastest and the probe-anchored
            // first-pass interpolation is weakest.
            int segLen = (mode.U + Segments - 1) / Segments;
            var h1Avg = Cf.Zero;
            for (int s = 0; s < Segments; s++)
            {
                var acc = Cf.Zero;
                int start = s * segLen;
                int end = Math.Min(mode.U, start + segLen);
                for (int u = start; u < end; u++)
                {
                    acc += estWire[u] * expected[u].Conj();
                }

                segH1[s] = acc * (1f / Math.Max(1, end - start));
                segCentre[s] = 0.5f * (start + end - 1);
                h1Avg += segH1[s];
            }

            h1Avg *= 1f / Segments;

            // Echo-delay search on wire-domain residuals. The lag cap was 8 under the
            // 2^delay trellis (issue #64's ceiling); each chain here carries M states
            // whatever the delay, so the cap is physical, not computational: 24 symbols
            // (10 ms) covers the D-LXV 9 ms static spread, bounded by the probe length so
            // the pre-block echo source stays inside the known probe.
            int maxLag = Math.Min(Math.Min(24, mode.U / 2), mode.K);
            int delay = 1;
            var h2Avg = Cf.Zero;
            for (int lag = 1; lag <= maxLag; lag++)
            {
                var acc = Cf.Zero;
                for (int u = lag; u < mode.U; u++)
                {
                    acc += (estWire[u] - (h1Avg * expected[u])) * expected[u - lag].Conj();
                }

                Cf h2 = acc * (1f / (mode.U - lag));
                if (h2.Cnorm() > h2Avg.Cnorm())
                {
                    h2Avg = h2;
                    delay = lag;
                }
            }

            // Significance floor: each noise-only lag estimate has variance ≈ σ²/U, so
            // the max over ≤24 lags sits near 2·ln24·σ²/U — at the worst gated point
            // (WN3, U=96, ~4 dB Es/N0) that is ≈ 0.027·|h1|². Below 0.04·|h1|² the
            // "echo" is a noise pick: run the chains echo-free (matched-filter mode).
            if (h2Avg.Cnorm() < 0.04f * h1Avg.Cnorm())
            {
                h2Avg = Cf.Zero;
                delay = 1;
            }

            // The probe preceding this frame's data supplies the known echo sources for
            // the first `delay` symbols (§B2.2: exact chain initialization; the legacy
            // trellis assumed all-ones history). Frame f's preceding probe is the one
            // that FOLLOWED frame f−1, whose boundary flag was ((f−1)+2) % Frames == 0.
            // (For the first frame of the first block it is really the preamble-ending
            // probe, boundary false — which (f+1) % Frames == 0 also yields for every
            // multi-frame interleaver.)
            Cf[] precedingProbe = MiniProbe.Get(mode.K, boundary: (f + 1) % _il.Frames == 0);

            // Assemble the descrambled-domain model: z[u] = rxWire[u]·r̄(u) leaves h1
            // unchanged (piecewise-linear through the segment centres) and puts the
            // scrambler into the echo coefficient — h2·r(u−d)·r̄(u) for in-block echoes,
            // h2·r̄(u) against the known wire chip for the pre-block ones.
            var rxDesc = new Cf[mode.U];
            var h1Span = new Cf[mode.U];
            var h2Span = new Cf[mode.U];
            var preceding = new Cf[delay];
            for (int c = 0; c < delay; c++)
            {
                preceding[c] = precedingProbe[(mode.K - delay) + c];
            }

            float residual = 0f;
            for (int u = 0; u < mode.U; u++)
            {
                int s = Math.Min(Segments - 1, u / segLen);
                Cf h1u;
                if (u <= segCentre[0] || Segments == 1)
                {
                    h1u = segH1[0];
                }
                else if (u >= segCentre[Segments - 1])
                {
                    h1u = segH1[Segments - 1];
                }
                else
                {
                    if (u < segCentre[s])
                    {
                        s--;
                    }

                    float t = (u - segCentre[s]) / (segCentre[s + 1] - segCentre[s]);
                    h1u = (segH1[s] * (1f - t)) + (segH1[s + 1] * t);
                }

                h1Span[u] = h1u;
                rxDesc[u] = rxWire[u] * rotors[u].Conj();
                Cf echoWire = u >= delay ? expected[u - delay] : preceding[u];
                h2Span[u] = u >= delay
                    ? h2Avg * rotors[u - delay] * rotors[u].Conj()
                    : h2Avg * rotors[u].Conj();
                Cf predicted = (h1u * expected[u]) + (h2Avg * echoWire);
                residual += (rxWire[u] - predicted).Cnorm();
            }

            // Cnorm() sums both complex dimensions; the BCJR wants σ² per dimension, so
            // halve (the #65 2×-under-confidence lesson).
            float noiseVar = Math.Max(0.5f * residual / mode.U, 1e-6f);

            (Cf[] constellation, byte[] labels) = mode.Modulation switch
            {
                Ms110dModulation.Bpsk => (TurboBpsk, Array.Empty<byte>()),
                Ms110dModulation.Qpsk => (TurboQpsk, TurboQpskLabels),
                _ => (Ms110dTables.Psk8, SymbolToTribit8),
            };
            var frameLlrs = new float[mode.U * bitsPerSymbol];
            Ms110dChainBcjr.Equalize(
                rxDesc, h1Span, h2Span, delay, noiseVar,
                constellation, labels, bitsPerSymbol, preceding, frameLlrs);
            for (int i = 0; i < frameLlrs.Length; i++)
            {
                AddLlr(frameLlrs[i]);
            }
        }

        // Restore DFE state and leave a clean Gram for the next frame.
        dfe.LoadTaps(savedTaps);
        dfe.BeginTraining();
    }

    private void CompleteBurst(Ms110dBurstEndReason reason)
    {
        EmitBurst([.. _burstBits], reason);
    }

    private void EmitBurst(byte[] payload, Ms110dBurstEndReason reason)
    {
        int blocks = _blockIndex;
        EndBurst();
        BurstCompleted?.Invoke(new Ms110dBurst(payload, reason, blocks));
    }

    private void EndBurst()
    {
        _state = Ms110dRxState.Searching;
        _lock = null;
        _mode = null;
        _il = null;
        _dfe = null;
        _walsh = null;
        _trackingInitialized = false;
        _blockLlrCount = 0;
        _blockIndex = 0;
        _burstBits.Clear();
        // A burst ending mid-block (SignalLost/Terminate/EOM) must not leak this
        // block's frame positions or fading state into the next burst's turbo gate
        // and flat-channel classification.
        _blockFrameChips.Clear();
        _fadeFloor = 0;
        _fadeFloorSeeded = false;
        _framesSinceExcursion = int.MaxValue / 2;
        _fading = false;
        _fadingLatched = false;
        _bestMetric = 0;
        _bestStart = -1;
        _terminate = false;
    }

    private static float[] RxPulse()
    {
        const int span = 16;
        const int sps = 4;
        int taps = (span * sps) + 1;
        var pulse = new float[taps];
        double centre = (taps - 1) / 2.0;
        double energy = 0;
        for (int i = 0; i < taps; i++)
        {
            double t = (i - centre) / sps;
            pulse[i] = (float)FilterDesign.RootRaisedCosine(t, Ms110dModulator.RollOff);
            energy += pulse[i] * pulse[i];
        }

        float norm = (float)(1.0 / Math.Sqrt(energy));
        for (int i = 0; i < taps; i++)
        {
            pulse[i] *= norm;
        }

        return pulse;
    }
}
