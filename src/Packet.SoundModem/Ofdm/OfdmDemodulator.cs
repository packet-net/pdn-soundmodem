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
/// The FreeDV datac OFDM demodulator and sync state machines — a port of codec2's
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
/// Two acquisition modes, matching codec2's <c>data_mode</c>: <b>streaming</b> (the default —
/// pilot-correlation acquisition, never loses sync) and <b>burst</b> (selected via
/// <see cref="SetPacketsPerBurst"/>, codec2 <c>ofdm_set_packets_per_burst</c>, <c>ofdm.c:1165</c>
/// — acquisition correlates against the known preamble/postamble waveforms, and sync is dropped
/// after each burst). Burst mode is what codec2's <c>freedv_data_raw_tx/rx</c> tools and FreeDATA
/// use (<c>freedv_set_frames_per_burst</c>, <c>freedv_api.c:1418</c>).
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

    // burst data mode (codec2 data_mode == "burst"; ofdm_set_packets_per_burst, ofdm.c:1165)
    private bool _burstMode;
    private int _packetsPerBurst;
    private bool _postambleDetectorEn;
    private Cf[]? _txPreamble;
    private Cf[]? _txPostamble;
    private Cf[]? _mvec;                    // known-sequence correlator scratch (samplesperframe)

    // burst acquisition frequency search range (codec2 ofdm->fmin/fmax defaults, ofdm.c:427-428)
    private const float FminHz = -50.0f;
    private const float FmaxHz = 50.0f;

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

    /// <summary>Packets completed since sync was acquired — while synced, the 0-based index
    /// of the packet currently being assembled (burst mode: its index within the burst).</summary>
    public int PacketCount => _packetCount;

    /// <summary>How many modem frames the UW spans (from the config).</summary>
    public int Nuwframes => _c.Nuwframes;

    /// <summary>Unique-word bit errors counted by the last state-machine call.</summary>
    public int UwErrors => _uwErrors;

    /// <summary>Set for one frame when acquisition first succeeds (drives stat reset).</summary>
    public bool SyncStart { get; private set; }

    /// <summary>The demod config in use.</summary>
    public OfdmDemodConfig Config => _c;

    /// <summary>Whether burst data mode is selected (see <see cref="SetPacketsPerBurst"/>).</summary>
    public bool BurstMode => _burstMode;

    /// <summary>Bursts acquired via the preamble detector (codec2 <c>ofdm->pre</c>).</summary>
    public int PreambleDetections { get; private set; }

    /// <summary>Bursts acquired via the postamble detector (codec2 <c>ofdm->post</c>).</summary>
    public int PostambleDetections { get; private set; }

    /// <summary>Trial syncs abandoned on a bad unique word (codec2 <c>ofdm->uw_fails</c>).</summary>
    public int UwFails { get; private set; }

    /// <summary>
    /// Opt-in <b>pdn extension</b> to the burst sync state machine — NOT codec2 behaviour;
    /// default false keeps the CLI-validated port exact. While synced, when a new packet's
    /// unique-word rows are complete (<c>modemFrame == Nuwframes</c>, the same instant the
    /// trial state checks) and the UW fails the same <c>BadUwErrors</c> test, sync drops
    /// back to search with the history cleared. codec2 holds sync for exactly
    /// <c>packetsPerBurst</c> packets because its tools configure both ends with the burst
    /// length; a KISS modem's bursts are variable-length, so the receiver sets
    /// <c>packetsPerBurst</c> to the mode's maximum and relies on this check to end the
    /// burst when the postamble/silence (garbage at the UW positions) follows the last real
    /// packet. A real mid-burst packet re-presents the transmitted UW, so at working SNR
    /// this re-runs a check acquisition already passed; the cost is that a burst whose
    /// mid-burst UW is corrupted is abandoned where codec2 would have held on.
    /// </summary>
    public bool EndOfBurstUwDrop { get; set; }

    /// <summary>Sync drops triggered by <see cref="EndOfBurstUwDrop"/> (diagnostics).</summary>
    public int EndOfBurstDrops { get; private set; }

    /// <summary>
    /// Selects <b>burst</b> data mode and sets the packets-per-burst limit — a port of codec2's
    /// <c>ofdm_set_packets_per_burst</c> (<c>ofdm.c:1165-1169</c>), which
    /// <c>freedv_set_frames_per_burst</c> calls (<c>freedv_api.c:1418</c>; FreeDATA and
    /// <c>freedv_data_raw_rx</c> both use 1). Acquisition switches from pilot search to the
    /// known-sequence preamble/postamble correlator, and after <paramref name="packetsPerBurst"/>
    /// decoded packets sync drops back to search with the sample history cleared. The postamble
    /// detector is always enabled in burst mode.
    /// </summary>
    /// <param name="packetsPerBurst">Packets expected per burst (≥ 1); 0 keeps sync forever once
    /// acquired, as codec2 does.</param>
    /// <param name="txPreamble">The known raw preamble frame (<c>ofdm->tx_preamble</c>, LCG seed 2,
    /// amp_scale 1, no clip/BPF — <see cref="OfdmModulator.PreambleRaw"/>), SamplesPerFrame long.</param>
    /// <param name="txPostamble">The known raw postamble frame (<c>ofdm->tx_postamble</c>, LCG
    /// seed 3 — <see cref="OfdmModulator.PostambleRaw"/>), SamplesPerFrame long.</param>
    public void SetPacketsPerBurst(int packetsPerBurst, ReadOnlySpan<Cf> txPreamble, ReadOnlySpan<Cf> txPostamble)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(packetsPerBurst);
        if (txPreamble.Length != _c.SamplesPerFrame || txPostamble.Length != _c.SamplesPerFrame)
        {
            throw new ArgumentException(
                $"preamble/postamble must be one modem frame ({_c.SamplesPerFrame} samples), " +
                $"got {txPreamble.Length}/{txPostamble.Length}");
        }

        _burstMode = true;
        _packetsPerBurst = packetsPerBurst;
        _postambleDetectorEn = true;   // ofdm.c:1168
        _txPreamble = txPreamble.ToArray();
        _txPostamble = txPostamble.ToArray();
        _mvec = new Cf[_c.SamplesPerFrame];
    }

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

    /// <summary>Acquisition (codec2 <c>ofdm_sync_search</c> → <c>ofdm_sync_search_core</c>,
    /// <c>ofdm.c:1466-1474</c>). Slides in <see cref="Nin"/> new samples, then dispatches on data
    /// mode: streaming searches for the pilot correlation peak over coarse frequency offsets
    /// {−40, 0, +40}&#160;Hz; burst runs the known preamble/postamble correlator. Returns whether
    /// a valid timing peak was found.</summary>
    public bool SyncSearch(ReadOnlySpan<Cf> input)
    {
        FeedShiftAppend(input);
        return _burstMode ? SyncSearchBurst() : SyncSearchStream();
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

    // -----------------------------------------------------------------------------------------
    // Burst acquisition (codec2 ofdm.c:1251-1381) — known-sequence correlation against the
    // deterministic preamble/postamble instead of the pilot waveform.
    // -----------------------------------------------------------------------------------------

    /// <summary>Joint estimation of timing and frequency offset for burst data acquisition
    /// (codec2 <c>est_timing_and_freq</c>, <c>ofdm.c:1253-1295</c>): for each candidate frequency
    /// offset, correlate the de-rotated known sequence against every <paramref name="tstep"/>-th
    /// lag of <paramref name="rx"/> and keep the global magnitude peak. Returns the normalised
    /// timing metric <c>max_corr²/(mag1·mag2)</c>.</summary>
    private float EstTimingAndFreq(
        out int tEst, out float foffEst, ReadOnlySpan<Cf> rx, int nrx,
        ReadOnlySpan<Cf> knownSamples, int npsam, int tstep, float fmin, float fmax, float fstep)
    {
        int ncorr = nrx - npsam + 1;
        float maxCorr = 0.0f;
        tEst = 0;
        foffEst = 0.0f;
        Span<Cf> mvec = _mvec.AsSpan(0, npsam);   // non-null once burst mode is selected

        for (float afcoarse = fmin; afcoarse <= fmax; afcoarse += fstep)
        {
            float w = MathF.Tau * afcoarse / (float)_c.Fs;
            for (int i = 0; i < npsam; i++)
            {
                mvec[i] = (knownSamples[i] * Cf.Cmplx(w * i)).Conj();
            }

            for (int t = 0; t < ncorr; t += tstep)
            {
                Cf corr = DotNoConj(rx[t..], mvec, npsam);
                float mag = corr.Abs();
                if (mag > maxCorr)
                {
                    maxCorr = mag;
                    tEst = t;
                    foffEst = afcoarse;
                }
            }
        }

        // normalised real timing metric (ofdm.c:1281-1287)
        float mag1 = 0.0f;
        float mag2 = 0.0f;
        for (int i = 0; i < npsam; i++)
        {
            mag1 += knownSamples[i].Cnorm();
            mag2 += rx[i + tEst].Cnorm();
        }

        return maxCorr * maxCorr / ((mag1 * mag2) + 1e-12f);
    }

    /// <summary>Two-stage burst acquisition (codec2 <c>burst_acquisition_detector</c>,
    /// <c>ofdm.c:1299-1323</c>): a coarse grid (every 4th lag, 5&#160;Hz steps over
    /// [fmin,&#160;fmax]) over two frames of rxbuf starting at <paramref name="n"/>, then a fine
    /// refinement (every lag, 1&#160;Hz steps, ±3&#160;Hz / ±2 lags around the coarse peak).
    /// <paramref name="ctEst"/> is returned relative to <paramref name="n"/>.</summary>
    private float BurstAcquisitionDetector(int n, ReadOnlySpan<Cf> knownSequence, out int ctEst, out float foffEst)
    {
        int spf = _c.SamplesPerFrame;

        // initial search over coarse grid
        int tstep = 4;
        float fstep = 5.0f;
        EstTimingAndFreq(out ctEst, out foffEst, _rxbuf.AsSpan(n, 2 * spf), 2 * spf,
                         knownSequence, spf, tstep, FminHz, FmaxHz, fstep);

        // refine estimate over finer grid
        float fmin = foffEst - MathF.Ceiling(fstep / 2.0f);
        float fmax = foffEst + MathF.Ceiling(fstep / 2.0f);
        int fineSt = n + ctEst - (tstep / 2);
        float timingMx = EstTimingAndFreq(out ctEst, out foffEst, _rxbuf.AsSpan(fineSt, spf + tstep), spf + tstep,
                                          knownSequence, spf, 1, fmin, fmax, 1.0f);

        // refer ct_est to nominal start of frame rxbuf[n]
        ctEst += fineSt - n;
        return timingMx;
    }

    /// <summary>Burst acquisition search (codec2 <c>ofdm_sync_search_burst</c>,
    /// <c>ofdm.c:1325-1381</c>): run the detector against the known preamble and (in burst mode)
    /// postamble; on a preamble hit skip <c>nin</c> past it to the first modem frame, on a
    /// postamble hit rewind <c>rxbufst</c> one packet into the buffered history so the packet is
    /// demodulated from samples already received (and set <c>nin&#160;=&#160;0</c>).</summary>
    private bool SyncSearchBurst()
    {
        int st = _rxbufst + _c.M + _c.Ncp + _c.SamplesPerFrame;

        float preTimingMx = BurstAcquisitionDetector(st, _txPreamble, out int preCtEst, out float preFoffEst);

        int postCtEst = 0;
        float postFoffEst = 0.0f;
        float postTimingMx = 0.0f;
        if (_postambleDetectorEn)
        {
            postTimingMx = BurstAcquisitionDetector(st, _txPostamble, out postCtEst, out postFoffEst);
        }

        bool isPost;
        int ctEst;
        float foffEst;
        float timingMx;
        if (!_postambleDetectorEn || preTimingMx > postTimingMx)
        {
            (timingMx, ctEst, foffEst, isPost) = (preTimingMx, preCtEst, preFoffEst, false);
        }
        else
        {
            (timingMx, ctEst, foffEst, isPost) = (postTimingMx, postCtEst, postFoffEst, true);
        }

        bool timingValid = timingMx > _c.TimingMxThresh;
        if (timingValid)
        {
            if (isPost)
            {
                PostambleDetections++;
                // we won't need any new samples for a while: back up to the first modem frame of
                // the packet preceding the postamble (ofdm.c:1358-1364)
                _nin = 0;
                _rxbufst -= _c.Np * _c.SamplesPerFrame;
                _rxbufst += ctEst;
            }
            else
            {
                PreambleDetections++;
                // ct_est is the start of the preamble, so advance past it to the start of the
                // first modem frame (ofdm.c:1366-1369)
                _nin = _c.SamplesPerFrame + ctEst - 1;
            }
        }
        else
        {
            _nin = _c.SamplesPerFrame;
        }

        _foffEstHz = foffEst;
        _timingMx = timingMx;
        _timingValid = timingValid;
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

    /// <summary>Runs the data sync state machine for the selected data mode (codec2
    /// <c>ofdm_sync_state_machine</c> dispatcher, <c>ofdm.c:2269-2280</c>) with the unique-word
    /// bits extracted from the packet buffer this frame.</summary>
    public void SyncStateMachine(ReadOnlySpan<byte> rxUw)
    {
        if (_burstMode)
        {
            SyncStateMachineDataBurst(rxUw);
        }
        else
        {
            SyncStateMachineDataStreaming(rxUw);
        }
    }

    /// <summary>The streaming-data sync state machine (codec2
    /// <c>ofdm_sync_state_machine_data_streaming</c>, <c>ofdm.c:2101</c>).</summary>
    private void SyncStateMachineDataStreaming(ReadOnlySpan<byte> rxUw)
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

    /// <summary>The burst-data sync state machine (codec2
    /// <c>ofdm_sync_state_machine_data_burst</c>, <c>ofdm.c:2156-2215</c>). The pre/postamble told
    /// us where the packet starts, so trial waits exactly <c>nuwframes</c> frames then accepts or
    /// abandons on the unique word; after <c>packetsperburst</c> packets sync drops back to search.
    /// Both search transitions clear rxbuf so a postamble is never correlated twice against the
    /// same samples (the de-dupe that prevents double-decoding a burst).</summary>
    private void SyncStateMachineDataBurst(ReadOnlySpan<byte> rxUw)
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

        // The pre or postamble has told us this is the start of the packet. Confirm we have a
        // valid frame by checking the UW after the modem frames containing the UW have arrived.
        if (State == SyncState.Trial)
        {
            _syncCounter++;
            if (_syncCounter == _c.Nuwframes)
            {
                if (_uwErrors < _c.BadUwErrors)
                {
                    nextState = SyncState.Synced;
                    _packetCount = 0;
                    _modemFrame = _c.Nuwframes;
                }
                else
                {
                    nextState = SyncState.Search;
                    ResetRxBufToSearch();   // ofdm.c:2186-2190
                    UwFails++;
                }
            }
        }

        if (State == SyncState.Synced)
        {
            _modemFrame++;

            // pdn extension (see EndOfBurstUwDrop): a packet whose freshly-arrived UW rows
            // fail the trial-state test is not a packet — it is the postamble/silence after
            // a variable-length burst. End the burst here instead of waiting for the
            // packetsPerBurst count. Never fires on the acquisition packet: the trial
            // accept sets modemFrame to Nuwframes, so the first check lands at packet 2+.
            if (EndOfBurstUwDrop && _modemFrame == _c.Nuwframes && _uwErrors >= _c.BadUwErrors)
            {
                nextState = SyncState.Search;
                ResetRxBufToSearch();
                EndOfBurstDrops++;
                // A stale modemFrame == Np−1 (datac0's Nuwframes) would make the receiver
                // decode garbage during the next burst's trial frames.
                _modemFrame = 0;
            }
            else if (_modemFrame >= _c.Np)
            {
                _modemFrame = 0;
                _packetCount++;
                if (_packetsPerBurst > 0 && _packetCount >= _packetsPerBurst)
                {
                    nextState = SyncState.Search;
                    ResetRxBufToSearch();   // ofdm.c:2202-2207
                }
            }
        }

        LastSyncState = State;
        State = nextState;
    }

    /// <summary>
    /// Forces the burst state machine back to search with the history cleared — the
    /// receive-side companion of <see cref="EndOfBurstUwDrop"/> for the case the UW check
    /// misses (a fluke UW match on noise, ~10&#160;% for datac1's 16-bit UW): the phantom
    /// packet then completes and fails its CRC, and the receiver calls this to end the
    /// burst rather than accumulate another phantom. pdn extension, not codec2.
    /// </summary>
    internal void ForceSearch()
    {
        _timingValid = false;   // a stale timing peak must not instantly re-trial
        _modemFrame = 0;        // a stale Np−1 would decode garbage during the next trial
        LastSyncState = State;
        State = SyncState.Search;
        ResetRxBufToSearch();
        EndOfBurstDrops++;
    }

    /// <summary>Resets the rx buffer window and zeroes the buffered history so a burst's
    /// postamble is only ever correlated once against the same samples (codec2
    /// <c>ofdm_sync_state_machine_data_burst</c>, <c>ofdm.c:2186-2190</c>).</summary>
    private void ResetRxBufToSearch()
    {
        _rxbufst = _c.NrxBufHistory;
        Array.Clear(_rxbuf);
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
