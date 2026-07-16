namespace Packet.SoundModem.Ofdm;

/// <summary>The demodulator's acquisition state (codec2 <c>ofdm_internal.h:55</c>).</summary>
public enum SyncState
{
    /// <summary>Searching for the pilot correlation peak (acquisition).</summary>
    Search,

    /// <summary>A candidate was found; confirming via the unique word.</summary>
    Trial,

    /// <summary>Locked; producing decoded frames.</summary>
    Synced,
}

/// <summary>
/// The FreeDV datac OFDM demodulator and streaming sync state machine — a port of codec2's
/// <c>ofdm_sync_search_stream</c>, <c>ofdm_demod_core</c> and
/// <c>ofdm_sync_state_machine_data_streaming</c> (codec2 1.2.0, git 310777b, <c>ofdm.c</c>;
/// LGPL-2.1 — see PROVENANCE.md). It consumes a stream of 8&#160;kHz real audio (fed as
/// <see cref="Cf"/> with zero imaginary part, matching codec2's <c>rxbuf[i] = short/32767</c>)
/// and, once locked, emits one modem frame of phase-corrected QPSK symbols (<see cref="RxNp"/>)
/// plus per-carrier amplitudes (<see cref="RxAmp"/>) at a time. Packet assembly → LLRs is
/// <see cref="OfdmPacketAssembler"/>; the end-to-end driver is <see cref="DatacReceiver"/>.
/// </summary>
/// <remarks>
/// <para>
/// Single-precision throughout to track the reference bit-for-bit on clean inputs and
/// statistically (CRC-valid / packet-error-rate) on noisy ones. The design is transcribed in
/// docs/ofdm-design.md §4 and the demodulator design note. Not thread-safe.
/// </para>
/// <para>
/// The RX band-pass filter used by datac4/13/14 (codec2 <c>quisk_ccfFilter</c>) is not ported;
/// those modes are therefore not validated here. datac0/1/3 (no RX BPF) are the supported set.
/// </para>
/// </remarks>
public sealed class OfdmDemodulator
{
    private readonly OfdmDemodConfig _c;

    private readonly Cf[] _rxbuf;
    private int _rxbufst;
    private int _nin;

    private readonly Cf[][] _rxSym;         // (ns+3) x (nc+2)
    private readonly Cf[] _rxNp;            // rowsperframe*nc
    private readonly float[] _rxAmp;        // rowsperframe*nc
    private readonly byte[] _rxBits;        // bitsperframe

    // reused scratch
    private readonly Cf[] _wvec;            // samplespersymbol
    private readonly float[] _corr;         // max(samplesperframe, ftwindowwidth)
    private readonly Cf[] _ftWork;          // fine-timing de-rotated window
    private readonly Cf[] _work;            // per-symbol down-convert (m)
    private readonly float[] _aphase;       // nc+2
    private readonly float[] _aamp;         // nc+2

    private float _foffEstHz;
    private float _coarseFoffEstHz;
    private int _timingEst;
    private int _samplePoint;
    private float _timingMx;
    private bool _timingValid;
    private float _meanAmp;
    private int _clockOffsetCounter;

    // sync state machine
    private int _modemFrame;
    private int _syncCounter;
    private int _packetCount;
    private int _uwErrors;

    /// <summary>Creates a demodulator for <paramref name="config"/>.</summary>
    public OfdmDemodulator(OfdmDemodConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _c = config;

        _rxbuf = new Cf[config.NrxBuf];
        _rxbufst = config.NrxBufHistory;
        _nin = config.SamplesPerFrame;

        _rxSym = new Cf[config.Ns + 3][];
        for (int i = 0; i < config.Ns + 3; i++)
        {
            _rxSym[i] = new Cf[config.Nc + 2];
        }

        _rxNp = new Cf[config.RowsPerFrame * config.Nc];
        _rxAmp = new float[config.RowsPerFrame * config.Nc];
        _rxBits = new byte[config.Mode.BitsPerFrame];

        _wvec = new Cf[config.SamplesPerSymbol];
        int ftLen = config.SamplesPerFrame - 1 + config.SamplesPerSymbol + config.FtWindowWidth;
        _ftWork = new Cf[ftLen];
        _corr = new float[Math.Max(config.SamplesPerFrame, config.FtWindowWidth) + 1];
        _work = new Cf[config.M];
        _aphase = new float[config.Nc + 2];
        _aamp = new float[config.Nc + 2];

        State = SyncState.Search;
        LastSyncState = SyncState.Search;
    }

    /// <summary>Samples the demodulator wants on the next call (codec2 <c>ofdm->nin</c>). May be
    /// 0, meaning another frame can be produced from buffered history with no new input.</summary>
    public int Nin => _nin;

    /// <summary>Current acquisition state.</summary>
    public SyncState State { get; private set; }

    /// <summary>Acquisition state before the last <see cref="SyncStateMachine"/> call.</summary>
    public SyncState LastSyncState { get; private set; }

    /// <summary>The last frame's phase-corrected QPSK data symbols (codec2 <c>rx_np</c>),
    /// <c>RowsPerFrame·Nc</c> of them.</summary>
    public ReadOnlySpan<Cf> RxNp => _rxNp;

    /// <summary>The last frame's per-symbol amplitudes (codec2 <c>rx_amp</c>).</summary>
    public ReadOnlySpan<float> RxAmp => _rxAmp;

    /// <summary>The last frame's hard QPSK bits (test/uncoded use only).</summary>
    public ReadOnlySpan<byte> RxBits => _rxBits;

    /// <summary>Running mean amplitude for LDPC LLR scaling (codec2 <c>ofdm->mean_amp</c>).</summary>
    public float MeanAmp => _meanAmp;

    /// <summary>Current frequency-offset estimate (Hz).</summary>
    public float FoffEstHz => _foffEstHz;

    /// <summary>Current integer timing estimate.</summary>
    public int TimingEst => _timingEst;

    /// <summary>Last timing-correlation maximum (a ~[0,1] matched-filter score).</summary>
    public float TimingMx => _timingMx;

    /// <summary>Whether the last acquisition/timing update declared a valid timing peak.</summary>
    public bool TimingValid => _timingValid;

    /// <summary>Modem-frame counter within the current packet (0 … Np−1).</summary>
    public int ModemFrame => _modemFrame;

    /// <summary>How many modem frames the UW spans (from the config).</summary>
    public int Nuwframes => _c.Nuwframes;

    /// <summary>Unique-word bit errors counted by the last state-machine call.</summary>
    public int UwErrors => _uwErrors;

    /// <summary>Set for one frame when acquisition first succeeds (drives stat reset).</summary>
    public bool SyncStart { get; private set; }

    /// <summary>The demod config in use.</summary>
    public OfdmDemodConfig Config => _c;

    // -----------------------------------------------------------------------------------------
    // Buffer feed (codec2 ofdm_sync_search / ofdm_demod: memmove left by nin, append nin).
    // -----------------------------------------------------------------------------------------
    private void FeedShiftAppend(ReadOnlySpan<Cf> input)
    {
        if (input.Length != _nin)
        {
            throw new ArgumentException($"expected {_nin} samples, got {input.Length}", nameof(input));
        }

        int n = _c.NrxBuf;
        if (_nin > 0)
        {
            Array.Copy(_rxbuf, _nin, _rxbuf, 0, n - _nin);
            for (int j = 0, i = n - _nin; i < n; j++, i++)
            {
                _rxbuf[i] = input[j];
            }
        }
    }

    /// <summary>Streaming acquisition (codec2 <c>ofdm_sync_search_stream</c>, <c>ofdm.c:1394</c>).
    /// Slides in <see cref="Nin"/> new samples and searches for the pilot correlation peak over
    /// coarse frequency offsets {−40, 0, +40}&#160;Hz. Returns whether a valid timing peak was
    /// found.</summary>
    public bool SyncSearch(ReadOnlySpan<Cf> input)
    {
        FeedShiftAppend(input);
        return SyncSearchStream();
    }

    private bool SyncSearchStream()
    {
        int st = _rxbufst + _c.SamplesPerFrame + _c.SamplesPerSymbol;
        int en = st + (2 * _c.SamplesPerFrame) + _c.SamplesPerSymbol;
        int length = en - st;

        int fcoarse = 0;
        float timingMx = 0.0f;
        int ctEst = 0;
        bool timingValid = false;

        Span<int> coarse = [-40, 0, 40];
        foreach (int afcoarse in coarse)
        {
            int actEst = EstTiming(_rxbuf.AsSpan(st, length), length, afcoarse, out float atmx, out bool atvalid, 2);
            if (atmx > timingMx)
            {
                ctEst = actEst;
                timingMx = atmx;
                fcoarse = afcoarse;
                timingValid = atvalid;
            }
        }

        _coarseFoffEstHz = EstFreqOffsetPilotCorr(_rxbuf.AsSpan(st), ctEst, fcoarse) + fcoarse;

        _timingValid = timingValid;
        if (timingValid)
        {
            _nin = ctEst;
            _samplePoint = _timingEst = 0;
            _foffEstHz = _coarseFoffEstHz;
        }
        else
        {
            _nin = _c.SamplesPerFrame;
        }

        _timingMx = timingMx;
        return timingValid;
    }

    /// <summary>Demodulates one modem frame (codec2 <c>ofdm_demod_core</c>, <c>ofdm.c:1531</c>):
    /// fine-timing update, down-convert + per-symbol DFT of the 11-row pilot/data matrix,
    /// frequency-offset tracking, per-carrier pilot phase/channel estimation, phase-corrected
    /// symbols into <see cref="RxNp"/>/<see cref="RxAmp"/>, and integer sample-clock tracking.</summary>
    public void Demod(ReadOnlySpan<Cf> input)
    {
        FeedShiftAppend(input);
        DemodCore();
    }

    private void DemodCore()
    {
        int prevTimingEst = _timingEst;
        float woffEst = MathF.Tau * _foffEstHz / (float)_c.Fs;

        // --- fine timing update (ofdm.c:1548-1582) --------------------------------------------
        int st = _rxbufst + _c.SamplesPerSymbol + _c.SamplesPerFrame
                 - (int)MathF.Floor(_c.FtWindowWidth / 2.0f) + _timingEst;
        int en = st + _c.SamplesPerFrame - 1 + _c.SamplesPerSymbol + _c.FtWindowWidth;
        int ftLen = en - st;

        for (int j = 0, i = st; i < en; j++, i++)
        {
            _ftWork[j] = _rxbuf[i] * Cf.CmplxConj(woffEst * i);   // absolute index i (ofdm.c:1563)
        }

        int ftEst = EstTiming(_ftWork.AsSpan(0, ftLen), ftLen, 0, out _timingMx, out _timingValid, 1);
        _timingEst += ftEst - (int)MathF.Ceiling(_c.FtWindowWidth / 2.0f) + 1;

        // keep sample_point inside the cyclic prefix (ofdm.c:1579-1581)
        _samplePoint = Math.Max(_timingEst + 4, _samplePoint);
        _samplePoint = Math.Min(_timingEst + _c.Ncp - 4, _samplePoint);

        // --- clear + down-convert the rx_sym matrix (ofdm.c:1622-1738) -------------------------
        for (int i = 0; i < _c.Ns + 3; i++)
        {
            Array.Clear(_rxSym[i]);
        }

        // previous pilot -> rx_sym[0]
        DownconvertDft(_rxSym[0], _rxbufst + _c.SamplesPerSymbol + 1 + _samplePoint, woffEst);

        // this pilot .. next pilot + one -> rx_sym[1 .. ns+1]
        for (int rr = 0; rr < _c.Ns + 1; rr++)
        {
            int baseIdx = _rxbufst + _c.SamplesPerSymbol + _c.SamplesPerFrame
                          + (rr * _c.SamplesPerSymbol) + 1 + _samplePoint;
            DownconvertDft(_rxSym[rr + 1], baseIdx, woffEst);
        }

        // future pilot -> rx_sym[ns+2]
        DownconvertDft(_rxSym[_c.Ns + 2], _rxbufst + _c.SamplesPerSymbol + (3 * _c.SamplesPerFrame) + 1 + _samplePoint, woffEst);

        // --- frequency-offset tracking (ofdm.c:1747-1769) -------------------------------------
        {
            Cf a = VectorSum(_rxSym[1]).Conj();
            Cf b = VectorSum(_rxSym[_c.Ns + 1]);
            Cf freqErrRect = a * b;
            freqErrRect = new Cf(freqErrRect.Re + 1e-6f, freqErrRect.Im);   // stabilise atan2
            float freqErrHz = freqErrRect.Arg() * (float)_c.Rs / (MathF.Tau * _c.Ns);
            // datac: foff_limiter == false, so no ±1 Hz clamp
            _foffEstHz += _c.FoffEstGain * freqErrHz;
        }

        // --- per-carrier pilot phase & channel estimation (ofdm.c:1771-1861) ------------------
        EstimatePilotPhase();

        // --- equalise -> symbols + amps + hard bits (ofdm.c:1873-1933) ------------------------
        float sumAmp = 0.0f;
        int bitIndex = 0;
        for (int rr = 0; rr < _c.RowsPerFrame; rr++)
        {
            for (int i = 1; i < _c.Nc + 1; i++)
            {
                Cf rxCorr = _rxSym[rr + 2][i] * Cf.CmplxConj(_aphase[i]);   // coherent; dpsk off
                int outIdx = (rr * _c.Nc) + (i - 1);
                _rxNp[outIdx] = rxCorr;
                _rxAmp[outIdx] = _aamp[i];
                sumAmp += _aamp[i];

                QpskDemod(rxCorr, out int abit1, out int abit0);
                _rxBits[bitIndex++] = (byte)abit1;
                _rxBits[bitIndex++] = (byte)abit0;
            }
        }

        _meanAmp = (0.9f * _meanAmp) + (0.1f * sumAmp / (_c.RowsPerFrame * _c.Nc));

        // --- integer sample-clock tracking (ofdm.c:1937-1961) ---------------------------------
        _nin = _c.SamplesPerFrame;
        _clockOffsetCounter += prevTimingEst - _timingEst;

        int thresh = _c.SamplesPerSymbol / 8;
        int tshift = _c.SamplesPerSymbol / 4;
        if (_timingEst > thresh)
        {
            _nin = _c.SamplesPerFrame + tshift;
            _timingEst -= tshift;
            _samplePoint -= tshift;
        }
        else if (_timingEst < -thresh)
        {
            _nin = _c.SamplesPerFrame - tshift;
            _timingEst += tshift;
            _samplePoint += tshift;
        }

        // use buffered history if available: advance rxbufst and request no new input
        int rxbufstNext = _rxbufst + _nin;
        if (rxbufstNext + _c.NrxBufMin <= _c.NrxBuf)
        {
            _rxbufst = rxbufstNext;
            _nin = 0;
        }
    }

    private void DownconvertDft(Cf[] outRow, int baseIdx, float woffEst)
    {
        for (int k = 0, j = baseIdx; k < _c.M; k++, j++)
        {
            _work[k] = _rxbuf[j] * Cf.CmplxConj(woffEst * j);
        }

        _c.Dft(outRow, _work);
    }

    private void EstimatePilotPhase()
    {
        for (int i = 0; i < _c.Nc + 2; i++)
        {
            _aphase[i] = 10.0f;
            _aamp[i] = 0.0f;
        }

        // high_bw only: datac streaming never switches to low_bw (ofdm.c:2101-2151 sets no
        // phase_est_bandwidth). rect is the 2-pilot ("this"+"next") average; the unconditional
        // overwrite at ofdm.c:1859-1860 makes amp_est_mode dead, so amp := |rect|, phase := arg(rect).
        for (int i = 1; i < _c.Nc + 1; i++)
        {
            Cf rect = _rxSym[1][i] * _c.Pilots[i].Conj();
            rect += _rxSym[_c.Ns + 1][i] * _c.Pilots[i].Conj();
            rect *= 0.5f;

            _aphase[i] = rect.Arg();
            _aamp[i] = rect.Abs();
        }
    }

    /// <summary>Runs the streaming sync state machine (codec2
    /// <c>ofdm_sync_state_machine_data_streaming</c>, <c>ofdm.c:2101</c>) with the unique-word
    /// bits extracted from the packet buffer this frame.</summary>
    public void SyncStateMachine(ReadOnlySpan<byte> rxUw)
    {
        SyncState nextState = State;
        SyncStart = false;

        if (State == SyncState.Search && _timingValid)
        {
            SyncStart = true;
            _syncCounter = 0;
            nextState = SyncState.Trial;
        }

        _uwErrors = 0;
        for (int i = 0; i < _c.Nuwbits; i++)
        {
            _uwErrors += _c.TxUw[i] ^ rxUw[i];
        }

        if (State == SyncState.Trial)
        {
            if (_uwErrors < _c.BadUwErrors)
            {
                nextState = SyncState.Synced;
                _packetCount = 0;
                _modemFrame = _c.Nuwframes;
            }
            else
            {
                _syncCounter++;
                if (_syncCounter > _c.Np)
                {
                    nextState = SyncState.Search;
                }
            }
        }

        // packetsperburst == 0 for streaming, so once synced we never drop sync.
        if (State == SyncState.Synced)
        {
            _modemFrame++;
            if (_modemFrame >= _c.Np)
            {
                _modemFrame = 0;
                _packetCount++;
            }
        }

        LastSyncState = State;
        State = nextState;
    }

    // -----------------------------------------------------------------------------------------
    // Timing correlation (codec2 est_timing, ofdm.c:794-923).
    // -----------------------------------------------------------------------------------------
    private int EstTiming(ReadOnlySpan<Cf> rx, int length, int fcoarse, out float timingMx, out bool timingValid, int step)
    {
        int sps = _c.SamplesPerSymbol;
        int spf = _c.SamplesPerFrame;
        int ncorr = length - (spf + sps);

        float acc = 0.0f;
        for (int i = 0; i < length; i++)
        {
            acc += rx[i].Cnorm();
        }

        float avLevel = 1.0f / ((2.0f * MathF.Sqrt(_c.TimingNorm * acc / length)) + 1e-12f);

        // wvec_pilot = conj(pilot), or ±40 Hz shifted (ofdm.c:818-833)
        Span<Cf> w = _wvec.AsSpan(0, sps);
        switch (fcoarse)
        {
            case -40:
                for (int j = 0; j < sps; j++)
                {
                    w[j] = (_c.Wval[j] * _c.PilotSamples[j]).Conj();
                }

                break;
            case 0:
                for (int j = 0; j < sps; j++)
                {
                    w[j] = _c.PilotSamples[j].Conj();
                }

                break;
            case 40:
                for (int j = 0; j < sps; j++)
                {
                    w[j] = _c.Wval[j] * _c.PilotSamples[j].Conj();
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(fcoarse));
        }

        for (int i = 0; i < ncorr; i += step)
        {
            Cf corrSt = DotNoConj(rx[i..], w, sps);
            Cf corrEn = DotNoConj(rx[(i + spf)..], w, sps);
            _corr[i] = (corrSt.Abs() + corrEn.Abs()) * avLevel;
        }

        int timingEst = 0;
        timingMx = 0.0f;
        for (int i = 0; i < ncorr; i += step)
        {
            if (_corr[i] > timingMx)
            {
                timingMx = _corr[i];
                timingEst = i;
            }
        }

        timingValid = rx[timingEst].Abs() > 0.0f && timingMx > _c.TimingMxThresh;
        return timingEst;
    }

    // Coarse frequency offset via pilot correlation (codec2 est_freq_offset_pilot_corr, ofdm.c:930-997).
    private float EstFreqOffsetPilotCorr(ReadOnlySpan<Cf> rx, int timingEst, int fcoarse)
    {
        int sps = _c.SamplesPerSymbol;
        int spf = _c.SamplesPerFrame;

        Span<Cf> w = _wvec.AsSpan(0, sps);
        switch (fcoarse)
        {
            case -40:
                for (int j = 0; j < sps; j++)
                {
                    w[j] = (_c.Wval[j] * _c.PilotSamples[j]).Conj();
                }

                break;
            case 0:
                for (int j = 0; j < sps; j++)
                {
                    w[j] = _c.PilotSamples[j].Conj();
                }

                break;
            case 40:
                for (int j = 0; j < sps; j++)
                {
                    w[j] = _c.Wval[j] * _c.PilotSamples[j].Conj();
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(fcoarse));
        }

        float foffEst = 0.0f;
        float cabsMax = 0.0f;
        for (int f = -20; f < 20; f++)
        {
            Cf corrSt = Cf.Zero;
            Cf corrEn = Cf.Zero;
            float tmp = MathF.Tau * f / (float)_c.Fs;
            Cf delta = Cf.CmplxConj(tmp);
            Cf wStep = Cf.CmplxConj(0.0f);   // == 1
            for (int i = 0; i < sps; i++)
            {
                Cf csam = w[i] * wStep;
                int est = timingEst + i;
                corrSt += rx[est] * csam;
                corrEn += rx[est + spf] * csam;
                wStep *= delta;
            }

            float cabs = corrSt.Abs() + corrEn.Abs();
            if (cabs > cabsMax)
            {
                cabsMax = cabs;
                foffEst = f;
            }
        }

        return foffEst;
    }

    // Non-conjugating complex dot product (codec2 ofdm_complex_dot_product scalar reference path,
    // ofdm.c:776-778) — conjugation is pre-baked into the pilot weight vector.
    private static Cf DotNoConj(ReadOnlySpan<Cf> left, ReadOnlySpan<Cf> right, int n)
    {
        float re = 0.0f;
        float im = 0.0f;
        for (int i = 0; i < n; i++)
        {
            Cf a = left[i];
            Cf b = right[i];
            re += (a.Re * b.Re) - (a.Im * b.Im);
            im += (a.Im * b.Re) + (a.Re * b.Im);
        }

        return new Cf(re, im);
    }

    private static Cf VectorSum(ReadOnlySpan<Cf> a)
    {
        Cf sum = Cf.Zero;
        for (int i = 0; i < a.Length; i++)
        {
            sum += a[i];
        }

        return sum;
    }

    /// <summary>Gray-coded QPSK hard demod (codec2 <c>qpsk_demod</c>, <c>ofdm.c:115-120</c>):
    /// <c>rot = sym·cmplx(π/4)</c>; <c>bit0 = Re(rot)≤0</c>, <c>bit1 = Im(rot)≤0</c>.</summary>
    internal static void QpskDemod(Cf sym, out int bit1, out int bit0)
    {
        Cf rot = sym * Cf.Cmplx(MathF.PI / 4.0f);
        bit0 = rot.Re <= 0.0f ? 1 : 0;
        bit1 = rot.Im <= 0.0f ? 1 : 0;
    }
}
