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
/// on TX but not yet acquired); clock-skew tolerance is limited to the slow per-probe timing
/// tracker (±ppm-scale, not the ±50 ppm adversarial case); WN 0 has no timing tracker at all
/// (chip clock assumed nominal, as in loopback and the D.6 simulation rigs).
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
    private static readonly bool DebugTrace =
        Environment.GetEnvironmentVariable("MS110D_DEBUG") == "1";
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
        PeakSearchMetric = 0; // documented as "since construction/reset"
        EndBurst();
    }

    /// <summary>Feeds received audio at 9600 Hz.</summary>
    public void Process(ReadOnlySpan<float> samples)
    {
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
            !IsSupported(wn))
        {
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
                c += ReadChip(baseChip + chip) * Ms110dTables.Psk8[knownChips[chip]].Conj();
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
        var value = Interpolate(pos);
        double theta = _thetaBase + (_omega * (pos - _chip0));
        return value * Cf.CmplxConj((float)theta);
    }

    private Cf ReadChip(double chip)
    {
        return ReadT2(2.0 * chip);
    }

    private Cf Interpolate(double pos)
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
            if (idx >= 0 && idx < _written)
            {
                acc += _ring[idx & (RingSize - 1)] * (float)w;
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
            FillWindow(baseChip + n, window);
            for (int j = 0; j < fb; j++)
            {
                past[j] = _known[n - 1 - j];
            }

            _dfe.AddTrainingRow(window, past, _known[n]);
        }

        _dfe.BeginRls((float)(1.0 - Math.Log(10.0) / _mode.U), pInit: 1.0f);
        _dfe.SeedRlsFromTraining(_initRidge, pFallback: 1.0f);
        _dfe.SolveTraining(regularization: _initRidge);
        _dfe.BeginTraining();

        // Seed the decision history with the probe tail and measure the training MSE.
        _decisions = new Cf[fb];
        for (int j = 0; j < fb; j++)
        {
            _decisions[j] = _known[ChipsSuperframe + k - 1 - j];
        }

        double mse = 0;
        var gain = Cf.Zero;
        for (int i = 0; i < k; i++)
        {
            int n = ChipsSuperframe + i;
            FillWindow(baseChip + n, window);
            for (int j = 0; j < fb; j++)
            {
                past[j] = _known[n - 1 - j];
            }

            Cf y = _dfe.Equalize(window, past);
            gain += y * _known[n].Conj();
            Cf err = y - _known[n];
            mse += err.Cnorm();
        }

        _probeMse = mse / k;
        _probeGainRef = gain.Abs() / k;
        _badProbes = 0;
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

    private void ProcessFrame()
    {
        Ms110dMode mode = _mode!;
        Dfe dfe = _dfe!;
        Span<Cf> window = stackalloc Cf[dfe.FfTaps];
        Span<Cf> probePast = stackalloc Cf[dfe.FbTaps];

        // Probe-directed batch-LS: known symbols accumulate into the Gram with
        // authoritative weight; the ridge-regularized solve (anchored to current taps)
        // handles the probe's rank deficiency (≤16 distinct patterns for 36+ taps).
        bool boundary = (_frameInBlock + 2) % _il!.Frames == 0;
        Cf[] probe = MiniProbe.Get(mode.K, boundary);
        long probeChip = _frameChip + mode.U;
        Cf[] startTaps = dfe.SnapshotTaps();
        var probePhase = Cf.Zero;
        double mse = 0;
        int statRows = 0;
        for (int i = dfe.FbTaps; i < mode.K; i++)
        {
            FillWindow(probeChip + i, window);
            for (int j = 0; j < dfe.FbTaps; j++)
            {
                probePast[j] = probe[i - 1 - j];
            }

            Cf y = dfe.Equalize(window, probePast);
            probePhase += y * probe[i].Conj();
            mse += (y - probe[i]).Cnorm();
            statRows++;
            dfe.AddTrainingRow(window, probePast, probe[i], weight: 6f);
        }

        dfe.SeedRlsFromTraining(_trackRidge, pFallback: 1.0f);
        dfe.SolveTraining(regularization: _trackRidge, anchorToCurrentTaps: true);
        Cf[] endTaps = dfe.SnapshotTaps();

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

        double tapChange = startNorm > 1e-12 ? changeNorm / startNorm : 0;
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

        // RLS weight: 1.0 on fading channels (full tracking), 0.1 on static/AWGN
        // (minimal noise accumulation while still providing some adaptation).
        bool fading = _fading;
        float rlsWeight = fading ? 1.0f : 0.1f;

        _scrambler.Reset();
        float ddGate = DdGateRadius(mode.Modulation);
        if (mode.Modulation == Ms110dModulation.Qam16)
        {
            for (int u = 0; u < mode.U; u++)
            {
                FillWindow(_frameChip + u, window);
                int scrambleNibble = _scrambler.NextQam(0, 4);
                Cf y = dfe.Equalize(window, _decisions);
                Cf clean = Slice(y, mode.Modulation);
                DataSymbolEqualized?.Invoke(y);
                PushMaxLogLlrs(y, Ms110dTables.Qam16, null, 4, 10.0f, scrambleNibble);
                dfe.RlsUpdate(window, _decisions, clean, weight: rlsWeight);
                if ((y - clean).Cnorm() < ddGate)
                {
                    dfe.AddTrainingRow(window, _decisions, clean, weight: 0.25f);
                }

                PushDecision(clean);
            }
        }
        else if (mode.U <= 96 || !fading)
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
                dfe.RlsUpdate(window, _decisions, clean * rotor, weight: rlsWeight);
                if ((descrambled - clean).Cnorm() < ddGate)
                {
                    dfe.AddTrainingRow(window, _decisions, clean * rotor, weight: 0.25f);
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
                dfe.RlsUpdate(window, _decisions, clean * rotor, weight: rlsWeight);
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
                dfe.RlsUpdate(window, _decisions, clean * rotor, weight: rlsWeight);
                PushDecision(clean * rotor);
            }
        }
        else
        {
            // Fading channel: single-pass RLS from endTaps.
            // Tracks the channel within one pass — bidirectional averaging would
            // mix different fading states and degrade performance.
            for (int u = 0; u < mode.U; u++)
            {
                FillWindow(_frameChip + u, window);
                Cf rotor = Ms110dTables.Psk8[_scrambler.NextPsk(0)];
                Cf y = dfe.Equalize(window, _decisions);
                Cf descrambled = y * rotor.Conj();
                Cf clean = Slice(descrambled, mode.Modulation);
                DataSymbolEqualized?.Invoke(descrambled);
                PushLlrs(descrambled, mode.Modulation);
                dfe.RlsUpdate(window, _decisions, clean * rotor, weight: rlsWeight);
                if ((descrambled - clean).Cnorm() < ddGate)
                {
                    dfe.AddTrainingRow(window, _decisions, clean * rotor, weight: 0.25f);
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

        _probeMse = (0.7 * _probeMse) + (0.3 * (mse / statRows));

        TrackProbeTiming(probeChip, probe);

        double probeGain = probePhase.Abs() / mode.K;
        if (DebugTrace)
        {
            Console.Error.WriteLine(
                $"frame@{_frameChip}: gain={probeGain:F3} ref={_probeGainRef:F3} mse={mse / mode.K:F3} " +
                $"tau={_tau:F3} omega={_omega:E2} bad={_badProbes} " +
                $"tapChange={tapChange:F4} floor={_fadeFloor:F4} fading={_fading}");
        }

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
                c += ReadT2((2.0 * (probeChip + i)) + offset) * probe[i].Conj();
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

        var info = new byte[_il.InputBits];
        Ms110dFraming.DecodeBlock(_viterbi!, _puncture!, _interleaver!, _blockLlrs, info);

        // Turbo re-equalization: re-encode decoded info, use as known training,
        // re-equalize with BCJR, and decode again.
        // Skip on flat channels (AWGN) where the turbo's DFE re-solve perturbs LLRs,
        // EXCEPT for BPSK U>48 (WN 3/4/5 — the predicate admits U=96 too) where the
        // BCJR path provides needed LLR refinement.
        bool flatChannel = IsFlatChannel();
        bool turboBcjrCandidate = _mode is not null &&
            _mode.Modulation == Ms110dModulation.Bpsk && _mode.U > 48;
        if (_dfe is not null && _mode is not null &&
            _mode.Modulation is not Ms110dModulation.Qam16 &&
            _blockFrameChips.Count == _il.Frames &&
            BlockSamplesResident() &&
            (!flatChannel || turboBcjrCandidate))
        {
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

            if (!converged && !aborted)
            {
                // Five decode→re-equalize→decode rounds without a fixed point: the loop
                // is oscillating, and a self-trained iterate with no fixed point is not
                // evidence (issue #65). Keep the first-pass decode.
                Array.Copy(firstPass, info, info.Length);
            }
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

    private bool IsFlatChannel()
    {
        // The fading detector spans block boundaries, so this classifies from the first
        // frame of a burst — the previous per-block variance of a single FF-edge tap
        // could never return true for interleavers with fewer than 4 frames per block,
        // which silently re-enabled turbo on AWGN for every UltraShort mode (issue #64).
        return _fadeFloorSeeded && !_fading;
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

                FillWindow(frameChip + u, window);
                dfe.AddTrainingRow(window, past, expected[u], weight: 1.0f);
            }

            // Solve with _trackRidge for all modes.
            dfe.SolveTraining(regularization: _trackRidge, anchorToCurrentTaps: true);

            // BPSK U>48 always takes the BCJR path (on flat channels h2≈0 degrades it
            // to a soft-output matched filter with better-calibrated LLRs than the DFE
            // fallback); other modulations use the DFE re-solve below.
            bool useBcjr = false;
            _scrambler.Reset();
            if (mode.Modulation == Ms110dModulation.Bpsk && mode.U > 48)
            {
                for (int j = 0; j < fb; j++) past[j] = Cf.Zero;
                var rxBlock = new Cf[mode.U];
                var expectedBpsk = new Cf[mode.U];
                for (int u = 0; u < mode.U; u++)
                {
                    FillWindow(frameChip + u, window);
                    Cf rotor = Ms110dTables.Psk8[_scrambler.NextPsk(0)];
                    Cf y = dfe.Equalize(window, past);
                    rxBlock[u] = y * rotor.Conj();
                    expectedBpsk[u] = expected[u] * rotor.Conj();
                }

                // h1 from the full block, then an echo-delay search: estimate the residual
                // echo tap at each candidate lag and model the strongest. The previous
                // code hard-coded lag 5 = 2.083 ms, exactly the D.6.1 Poor rig's path
                // spacing — on any other channel geometry it modelled a nonexistent echo
                // (issue #64). The search caps at lag 8 (3.3 ms): the trellis carries
                // 2^delay states, so lag 8 = 256 states ≈ the affordable ceiling — longer
                // echoes (the D-LXV 9 ms static spread) are beyond this 2-tap model and
                // remain the DFE feedback span's job.
                Cf sumZ = Cf.Zero, sumZw = Cf.Zero;
                int countW = 0;
                for (int u = 0; u < mode.U; u++)
                {
                    sumZ += rxBlock[u] * expectedBpsk[u].Conj();
                }

                Cf h1Avg = sumZ * (1f / mode.U);
                int delay = 1;
                Cf h2Avg = Cf.Zero;
                int maxLag = Math.Min(8, mode.U / 4);
                for (int lag = 1; lag <= maxLag; lag++)
                {
                    Cf acc = Cf.Zero;
                    for (int u = lag; u < mode.U; u++)
                    {
                        acc += (rxBlock[u] - (h1Avg * expectedBpsk[u])) * expectedBpsk[u - lag].Conj();
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
                // "echo" is a noise pick: run the trellis echo-free (matched-filter mode).
                if (h2Avg.Cnorm() < 0.04f * h1Avg.Cnorm())
                {
                    h2Avg = Cf.Zero;
                    delay = 1;
                }

                // Always use BCJR for BPSK U>48: on flat channels (h2≈0) it acts as
                // a soft-output matched filter with better LLR calibration than DFE fallback.
                {
                    useBcjr = true;
                    var h1 = new Cf[mode.U];
                    var h2 = new Cf[mode.U];
                    for (int u = 0; u < mode.U; u++) { h1[u] = h1Avg; h2[u] = h2Avg; }

                    float noiseVar = 0;
                    for (int u = delay; u < mode.U; u++)
                    {
                        Cf predicted = h1Avg * expectedBpsk[u] + h2Avg * expectedBpsk[u - delay];
                        noiseVar += (rxBlock[u] - predicted).Cnorm();
                    }

                    // Ms110dBcjr documents noiseVar per complex dimension; Cnorm() sums
                    // both dimensions, so halve — passing the total made every LLR ~2×
                    // under-confident, damping the tanh soft-symbol refinement (issue #65).
                    noiseVar = Math.Max(0.5f * noiseVar / Math.Max(1, mode.U - delay), 1e-6f);

                    // BCJR pass 1 + soft refinement + pass 2.
                    float[] bcjrLlrs = Ms110dBcjr.Equalize(rxBlock, h1, h2, delay, noiseVar);
                    sumZ = Cf.Zero; sumZw = Cf.Zero; countW = 0;
                    for (int u = delay; u < mode.U; u++)
                    {
                        float soft = (float)Math.Tanh(bcjrLlrs[u] * 0.5);
                        float softD = (float)Math.Tanh(bcjrLlrs[u - delay] * 0.5);
                        Cf z = rxBlock[u] * soft;
                        sumZ += z;
                        sumZw += z * (softD * soft);
                        countW++;
                    }
                    if (countW > 0)
                    {
                        h1Avg = sumZ * (1f / countW);
                        h2Avg = sumZw * (1f / countW);
                        for (int u = 0; u < mode.U; u++) { h1[u] = h1Avg; h2[u] = h2Avg; }
                    }
                    bcjrLlrs = Ms110dBcjr.Equalize(rxBlock, h1, h2, delay, noiseVar);
                    for (int u = 0; u < mode.U; u++)
                    {
                        AddLlr(bcjrLlrs[u]);
                    }
                }
            }

            if (!useBcjr)
            {
                // DFE re-equalization: FF-only (no feedback) to avoid error propagation.
                _scrambler.Reset();
                for (int j = 0; j < fb; j++) past[j] = Cf.Zero;
                for (int u = 0; u < mode.U; u++)
                {
                    FillWindow(frameChip + u, window);
                    Cf rotor = Ms110dTables.Psk8[_scrambler.NextPsk(0)];
                    Cf y = dfe.Equalize(window, past);
                    Cf descrambled = y * rotor.Conj();
                    PushLlrs(descrambled, mode.Modulation);
                }
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
