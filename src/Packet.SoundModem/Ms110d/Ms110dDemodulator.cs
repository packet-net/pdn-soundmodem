using Packet.SoundModem.Dsp;
using Packet.SoundModem.Ms110d.Fec;
using Packet.SoundModem.Ofdm;

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
    private const int RingBits = 15;
    private const int RingSize = 1 << RingBits;   // 32768 T/2 samples ≈ 6.8 s
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

    // Search state.
    private double _bestMetric;
    private long _bestStart = -1;
    private int _bestBin;

    /// <summary>Highest matched-filter metric seen since construction/reset — an
    /// acquisition diagnostic (normalized 0…1).</summary>
    public double PeakSearchMetric { get; private set; }

    // Lock state.
    private Ms110dRxState _state = Ms110dRxState.Searching;
    private double _chip0;        // absolute T/2 position of chip 0 of the matched super-frame
    private double _tau;          // slow timing correction, T/2 units
    private double _omega;        // carrier phase increment per T/2 sample (residual CFO)
    private double _thetaBase;    // carrier phase at _chip0
    private Ms110dLockInfo? _lock;
    private Ms110dMode? _mode;
    private Ms110dInterleaverParams? _il;
    private long _dataStartChip;
    private bool _trackingInitialized;

    // Tracking state (WN ≥ 1).
    private Dfe? _dfe;
    private int _ffLead;
    private Cf[] _known = [];
    private Cf[] _decisions = [];  // ring of past FbTaps decisions, newest at [0]
    private readonly Ms110dScrambler _scrambler = new();
    private long _frameChip;
    private int _frameInBlock;
    private float _ddMu;
    private int _badProbes;
    private double _probeMse;

    // Tracking state (WN 0).
    private Wid0WalshModem? _walsh;
    private long _symbolChip;
    private int _symbolInBlock;
    private int _weakSymbols;

    // Block/burst assembly.
    private TailBitingViterbiDecoder? _viterbi;
    private PunctureSpec? _puncture;
    private Ms110dInterleaver? _interleaver;
    private float[] _blockLlrs = [];
    private int _blockLlrCount;
    private int _blockIndex;
    private readonly List<byte> _burstBits = [];
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

        if (_terminate && _state == Ms110dRxState.Tracking)
        {
            CompleteBurst(Ms110dBurstEndReason.Terminated);
            return;
        }

        _terminate = false;

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

        RefineCarrier(count, wn, il, k);
        _lock = new Ms110dLockInfo(wn, il, k, _lock!.CfoHz);
        _mode = Ms110dMode.Mode3k(wn);
        _il = Ms110dInterleaverParams.Get3k(wn, il);
        ConvolutionalCode code = k == 9 ? ConvolutionalCode.K9 : ConvolutionalCode.K7;
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

    private static bool IsSupported(int wn)
    {
        return wn is >= 0 and <= 6 or 13;
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

    private void RefineCarrier(int count, int wn, Ms110dInterleaverKind il, int k)
    {
        // All 18 channel symbols of the matched super-frame are now known — least-squares
        // fit the residual phase slope over their per-symbol correlations (240 ms baseline).
        byte[] known = new byte[ChipsSuperframe];
        new PreambleGenerator(0, 2).FixedSectionChips().CopyTo(known, 0);
        PreambleGenerator.CountSectionChips(count).CopyTo(known, ChipsFixed);
        PreambleGenerator.WidSectionChips(wn, il, k).CopyTo(known, ChipsFixed + 128);

        Span<double> phases = stackalloc double[18];
        Span<double> weights = stackalloc double[18];
        double previous = 0;
        for (int j = 0; j < 18; j++)
        {
            var c = Cf.Zero;
            for (int i = 0; i < 32; i++)
            {
                int chip = (32 * j) + i;
                c += ReadChip(chip) * Ms110dTables.Psk8[known[chip]].Conj();
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
        for (int j = 0; j < 18; j++)
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

        // Symbol j's correlation phase sits at its centre (chip 32j+16 = half-index 64j+32),
        // so the residual at the chip-0 anchor is intercept − slope/2; the slope converts to
        // rad per T/2 sample by ÷64.
        _omega += slope / 64.0;
        _thetaBase += intercept - (0.5 * slope);
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
        (int ff, int fb, int lead) = _mode!.K switch
        {
            48 => (32, 22, 10),
            24 => (16, 6, 4),
            _ => (24, 12, 6),
        };
        _dfe = new Dfe(ff, fb);
        _ffLead = lead;
        _ddMu = _options.DecisionDirectedMu >= 0
            ? _options.DecisionDirectedMu
            : _mode.Wn switch { 1 or 2 => 0f, 3 or 4 => 0.005f, _ => 0.01f };

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

        _dfe.SolveTraining();

        // Seed the decision history with the probe tail and measure the training MSE.
        _decisions = new Cf[fb];
        for (int j = 0; j < fb; j++)
        {
            _decisions[j] = _known[ChipsSuperframe + k - 1 - j];
        }

        double mse = 0;
        for (int i = 0; i < k; i++)
        {
            int n = ChipsSuperframe + i;
            FillWindow(baseChip + n, window);
            for (int j = 0; j < fb; j++)
            {
                past[j] = _known[n - 1 - j];
            }

            Cf err = _dfe.Equalize(window, past) - _known[n];
            mse += err.Cnorm();
        }

        _probeMse = mse / k;
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

        // Data symbols.
        _scrambler.Reset();
        for (int u = 0; u < mode.U; u++)
        {
            FillWindow(_frameChip + u, window);
            // NextPsk(0) returns the raw scramble value; the receiver descrambles by
            // rotating with its conjugate (D.5.1.3 modulo-8 addition on the TX side).
            Cf rotor = Ms110dTables.Psk8[_scrambler.NextPsk(0)];
            Cf y = dfe.Equalize(window, _decisions);
            Cf descrambled = y * rotor.Conj();
            Cf clean = Slice(descrambled, mode.Modulation);
            if (_ddMu > 0)
            {
                dfe.Nlms(window, _decisions, clean * rotor, _ddMu);
            }

            PushLlrs(descrambled, mode.Modulation);
            PhaseNudge(descrambled, clean, 0.01, 0.0);
            PushDecision(clean * rotor);
        }

        // Mini-probe: known symbols → NLMS refresh, phase/frequency/timing update.
        bool boundary = (_frameInBlock + 2) % _il!.Frames == 0;
        Cf[] probe = MiniProbe.Get(mode.K, boundary);
        long probeChip = _frameChip + mode.U;
        var probePhase = Cf.Zero;
        double mse = 0;
        for (int i = 0; i < mode.K; i++)
        {
            FillWindow(probeChip + i, window);
            Cf y = dfe.Nlms(window, _decisions, probe[i], 0.05f);
            probePhase += y * probe[i].Conj();
            Cf err = y - probe[i];
            mse += err.Cnorm();
            PushDecision(probe[i]);
        }

        _probeMse = (0.7 * _probeMse) + (0.3 * (mse / mode.K));
        double phaseError = probePhase.Arg();
        int frameT2 = 2 * (mode.U + mode.K);
        RetuneCarrier(
            (2.0 * (probeChip + (mode.K / 2))) + _tau,
            0.4 * phaseError,
            0.15 * phaseError / frameT2);

        TrackProbeTiming(probeChip, probe);

        if (mse / mode.K > 1.0)
        {
            if (++_badProbes >= 3)
            {
                CompleteBurst(Ms110dBurstEndReason.SignalLost);
                return;
            }
        }
        else
        {
            _badProbes = 0;
        }

        _frameChip += mode.U + mode.K;
        _frameInBlock++;
        if (_frameInBlock == _il.Frames)
        {
            _frameInBlock = 0;
            FinishBlock();
        }
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

        double delta = 0.5 * (magnitudes[0] - magnitudes[2]) / denom * 0.5;
        _tau += Math.Clamp(0.2 * delta, -0.1, 0.1);
    }

    private void PhaseNudge(Cf descrambled, Cf clean, double gainTheta, double gainOmega)
    {
        Cf product = descrambled * clean.Conj();
        if (product.Cnorm() < 1e-12)
        {
            return;
        }

        double err = product.Arg();
        _thetaBase += gainTheta * err;
        _omega += gainOmega * err;
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

        // QPSK points sit on the axes (Table D-IV → 8PSK symbols 0/2/4/6).
        return Math.Abs(descrambled.Re) >= Math.Abs(descrambled.Im)
            ? new Cf(Math.Sign(descrambled.Re) >= 0 ? 1 : -1, 0)
            : new Cf(0, Math.Sign(descrambled.Im) >= 0 ? 1 : -1);
    }

    private void PushLlrs(Cf descrambled, Ms110dModulation modulation)
    {
        if (modulation == Ms110dModulation.Bpsk)
        {
            AddLlr(4f * descrambled.Re);
            return;
        }

        // Table D-IV Gray map: MSB=0 ⇔ {+1, +j}, LSB=0 ⇔ {+1, −j}.
        AddLlr(2f * (descrambled.Re + descrambled.Im));
        AddLlr(2f * (descrambled.Re - descrambled.Im));
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
            _walsh = new Wid0WalshModem();
            _walsh.Reset();
            _symbolChip = _dataStartChip;
            _symbolInBlock = 0;
            _weakSymbols = 0;
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

            // Decision-directed carrier: the winning Walsh correlation should be real
            // and positive after descrambling.
            if (correlation.Cnorm() > 1e-12)
            {
                double err = correlation.Arg();
                RetuneCarrier((2.0 * (_symbolChip + 16)) + _tau, 0.15 * err, 0.02 * err / 64.0);
            }

            double sumMag = 0;
            for (int i = 0; i < 32; i++)
            {
                sumMag += chips[i].Abs();
            }

            if (correlation.Abs() < 0.15 * sumMag)
            {
                if (++_weakSymbols >= 30)
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
