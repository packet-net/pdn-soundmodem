using M0LTE.Dsp;
using M0LTE.Il2p;

namespace Packet.SoundModem.Modems;

/// <summary>
/// Coherent 4-level FSK baseband ("C4FSK") - the NinoTNC modes 1 (19200 bps, 9600 sym/s)
/// and 3 (9600 bps, 4800 sym/s), both IL2P+CRC, added upstream in firmware 3/4.42. Like
/// the GFSK modes this is a direct baseband for an FM radio's 9600 port: a 4-PAM pulse
/// train (levels ±1, ±⅓) carrying two bits per symbol, shaped to 20 kHz / 10 kHz OBW.
/// </summary>
/// <remarks>
/// <para>
/// Wire truth from a real NinoTNC (firmware 3.44, CM108 loop, 2026-07-16): symbol-instant
/// k-means of its mode-3 transmission shows four levels at ratios −0.99/−0.37/+0.42/+1.00
/// with near-equal occupancy, and an outer-pair preamble (+1,+1,−1,−1 repeating). The
/// dibit→level mapping was established by brute force against those recordings - every
/// candidate mapping was run over captured bursts with known content and the IL2P
/// CRC arbitrated; see the C4FSK mapping test.
/// </para>
/// <para>
/// RX follows the proven FskModem chain (LPF → adaptive slicer → shared DPLL →
/// Il2pDeframer), widened to four levels: the outer envelope is tracked the same way and
/// the three slice thresholds sit at 0 and ±⅔ of it. Mode 1 at 48 kHz has only 5 samples
/// per symbol, so it takes the same ×2 interpolation the 9600 GFSK RX needs.
/// </para>
/// <para>
/// The decision stage is run at seven timing phases per symbol - the recovered clock instant
/// and 5, 10 and 15 % of a symbol either side, interpolated from a ring of the slicer's input
/// - each with its own equalizer and its own <see cref="Il2pReceiver"/>, and whichever phase
/// reads a frame delivers it once behind a symbol-clocked content dedupe. The front end (the
/// filter, the energy gate, the envelope and the clock) is shared, so the cost is the
/// decision stage and six extra deframers. That is <see cref="TimingDiversity"/>'s technique
/// from the PSK modes at this mode's coarser resolution; the step is twice theirs because at
/// 10 decision points per symbol a 2.5 % phase is a quarter of a point away from the instant
/// and decides the same symbol. See the 2026-08-21 (later5) entry in docs/mode-validation.md.
/// </para>
/// </remarks>
public sealed class C4fskModem : IModem
{
    private readonly int _sampleRate;
    private readonly int _symbolRate;
    private readonly bool _crc;
    private readonly FirFilter _rxFilter;
    private readonly Il2pReceiver[] _deframers;
    private readonly FrameDeduper _deduper;
    private readonly PacketDcd _packetDcd = new();
    private readonly EnergyBusyDetector _energyBusy;
    private readonly int _upsample;
    private readonly double _clockIncrement;
    private readonly double _pointsPerSymbol;
    private readonly float[] _slicerRing;
    private readonly int _ringLead;
    private long _pointIndex;
    private long _pendingInstant = -1;
    private long _symbolsSeen;
    private double _clockPhase;
    private int _lastSign;
    private bool _previousEnergyBusy;
    private float _peakHigh;
    private float _peakLow;
    private float _previousFiltered;

    // Symbol-spaced 5-tap decision-directed equalizer over the decision-instant
    // normalised values (NLMS, centre-tap identity start, decisions delayed two
    // symbols). Real NinoTNC transmissions leave pattern-dependent ISI that a plain
    // centre-sample slicer cannot survive: outer symbols squeezed by opposite-going
    // neighbours sample at 0.53-0.67 normalised - under the 2/3 boundary - so whole
    // frames die on payload content alone (2026-07-31 bench corpus: txd50 0/3 and
    // txd120 2/3 from recordings whose levels/spectra/DC are indistinguishable from
    // the txd250 capture that decodes clean; every error in the instrumented trace
    // was an outer→inner demotion). With this equalizer the same nine bursts drop
    // from 103 symbol errors to 25, and every burst's distinct-bad-byte count falls
    // inside the RS 8-per-block budget. Adaptation is silence-safe because the
    // energy gate already stops symbols flowing when the channel is idle.
    //
    // One set of taps, history, freeze state and last decision PER TIMING PHASE (see
    // PhaseFractions): the phases decide the same symbol from different instants, so each
    // needs its own adapted equalizer, and the freeze hysteresis is a per-phase reading of
    // the decision stream. Flat arrays, phase-major, allocated once.
    private readonly float[] _ffeTaps;
    private readonly float[] _ffeHistory;
    private readonly int[] _ffeCount;
    private readonly int[] _alternatingOuterRun;
    private readonly int[] _nonAlternatingRun;
    private readonly bool[] _ffeFrozen;
    private readonly int[] _previousLevel;

    private const int FfeLength = 5;
    private const float FfeMu = 0.05f;

    /// <summary>Dedupe window across the timing phases' deframers, in symbols: shorter than the
    /// shortest IL2P frame (a 15-byte header alone is 60 symbols at 2 bits a symbol), longer
    /// than the trailer a held plain reading waits for (32 bits, 16 symbols).</summary>
    private const int DedupeWindowSymbols = 32;

    /// <summary>Step between adjacent timing phases, in symbols, and how many pairs either
    /// side of the recovered instant - <see cref="TimingDiversity"/>'s idea at this mode's own
    /// resolution.</summary>
    /// <remarks>
    /// The PSK modes step 2.5 % of a symbol either side, three pairs. That step is a quarter
    /// of a decision point here: both C4FSK modes run 10 points per symbol at 48 kHz (mode 3
    /// natively, mode 1 through the x2 interpolation), and c4fsk19200's underlying audio is 5
    /// samples per symbol, so a 2.5 % phase is an eighth of a real sample from the instant and
    /// very nearly the same number. Measured over eight knee rows of 200 (2026-08-21, seed 1):
    /// no diversity 681, the PSK step 1223, 5 % 1339, 7.5 % 1341, 10 % over two pairs 1292, a
    /// fourth pair at 5 % 1366. So the PSK step is real but leaves half the gain on the table,
    /// reach past ~15 % of a symbol adds nothing, and a ninth phase is inside the noise. See
    /// the 2026-08-21 (later5) mode-validation.md entry for the rows themselves.
    /// </remarks>
    private const double PhaseStep = 0.05;

    /// <summary>Phases either side of the instant, per <see cref="PhaseStep"/>.</summary>
    private const int PhasePairs = 3;

    /// <summary>Offsets from the recovered clock instant, in symbols, phase 0 being the
    /// instant itself. Built from compile-time constants, so no static-initialiser ordering
    /// can flatten it (PR #330's [0, -0, 0] slip); the phases are asserted distinct, and
    /// asserted to make different errors on a real burst, in C4fskTimingDiversityTests.</summary>
    private static readonly double[] PhaseFractions = TimingDiversity.Build(PhaseStep, PhasePairs);

    /// <summary>Clock inertia while acquiring: Dire Wolf's locked value, as BitDpll.</summary>
    private const double AcquireInertia = 0.74;

    /// <summary>Clock inertia once DCD says the clock has converged (30 of 32 transitions
    /// well timed). A burst that is established does not need its clock steered any more, and
    /// a clock that keeps still is the only kind the timing phases either side of it can
    /// reach; 0.5 % of a transition's phase error still follows a real symbol-rate error. Same
    /// value, same gate, as <see cref="BpskDemodulator"/>.</summary>
    private const double HoldInertia = 0.995;

    /// <summary>
    /// MMDVM-TNC "Mode 2" sync, 4 bytes chosen to be outer-symbol-only: 0x5D 0x57 0xDF
    /// 0x7F. The deframer hunts 24 bits, so it takes the low three bytes; the first byte
    /// acts as extra preamble. Verified symbol-for-symbol against a NinoTNC mode-3
    /// recording (16 outer symbols +,+,−,+, +,+,+,−, −,+,−,−, +,−,−,−).
    /// </summary>
    private const int SyncWord = 0x57DF7F;

    private static readonly byte[] SyncBytes = [0x5D, 0x57, 0xDF, 0x7F];

    /// <summary>Preamble byte, per MMDVM-TNC Mode2Defines: 0x77 = dibits 01 11 01 11 =
    /// +3 −3 +3 −3, a symbol-rate outer alternation.</summary>
    private const byte PreambleByte = 0x77;

    /// <summary>Dibit → PAM level (0 = −outer … 3 = +outer), from MMDVM-TNC Mode2Defines'
    /// sync table as observed on the wire: 01→+3, 00→+1, 10→−1, 11→−3. (Mode2TX.cpp's
    /// writeByte is the opposite polarity; the on-air sense depends on the radio chain,
    /// and the deframer hunts both sync polarities so an inverted path still decodes.)</summary>
    private static readonly int[] DibitToLevel = [2, 3, 1, 0];

    private static readonly int[] LevelToDibit = BuildInverse();

    /// <summary>Creates the modem.</summary>
    /// <param name="sampleRate">Sample rate; must be a multiple of the symbol rate
    /// (48000 typical).</param>
    /// <param name="frameReceived">Receives each decoded AX.25 frame.</param>
    /// <param name="symbolRate">4800 (mode 3, 9600 bps) or 9600 (mode 1, 19200 bps).</param>
    /// <param name="crc">IL2P+CRC (both NinoTNC C4FSK modes run CRC).</param>
    /// <param name="acceptPlainIl2p">Pass frames that arrive as plain IL2P, with no trailing CRC,
    /// to <paramref name="frameReceived"/> as well as reporting them (off by default, and inert
    /// unless <paramref name="crc"/> is on). They are read either way - see
    /// <see cref="Il2pReceiver"/> for what that buys and what it costs.</param>
    public C4fskModem(
        int sampleRate, Action<byte[]> frameReceived, int symbolRate = 4800, bool crc = true,
        bool acceptPlainIl2p = false)
    {
        ArgumentNullException.ThrowIfNull(frameReceived);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(symbolRate, 0);
        if (sampleRate % symbolRate != 0)
        {
            throw new ArgumentException($"sample rate must be a multiple of {symbolRate}", nameof(sampleRate));
        }

        _sampleRate = sampleRate;
        _symbolRate = symbolRate;
        _crc = crc;
        // 1.5× the symbol rate, NOT the 0.55× the binary FSK modes use: a 4-level eye is
        // three times tighter, and the extra ISI of a tight low-pass on the already
        // Gaussian-shaped signal collapses the inner levels - measured on real NinoTNC
        // recordings as 0/8 at 0.55×, 7-8/8 from 1.0× up. 1.5× keeps some noise rejection.
        _rxFilter = new FirFilter(FilterDesign.LowPass(1.5 * symbolRate, sampleRate, 48 * sampleRate / 48000));
        _energyBusy = new EnergyBusyDetector(sampleRate);

        // One deframer per timing phase: the phases decide the same symbols at slightly
        // different instants and run their deframers in lockstep, so a frame that any of them
        // reads is delivered once, the first copy to arrive winning (usually every phase that
        // reads it arrives on the same symbol). The short content window behind it catches the
        // one way a copy can arrive later: a phase whose own IL2P+CRC reading failed holds a
        // plain reading until the trailer bits pass, and would otherwise show it as a second,
        // monitor-only row.
        _deduper = new FrameDeduper(DedupeWindowSymbols);
        _deframers = new Il2pReceiver[PhaseFractions.Length];
        for (int phase = 0; phase < _deframers.Length; phase++)
        {
            _deframers[phase] = new Il2pReceiver(
                (frame, info, delivery) =>
                {
                    if (!_deduper.ShouldEmit(frame, _symbolsSeen, !delivery.MonitorOnly))
                    {
                        return;
                    }

                    if (!delivery.MonitorOnly)
                    {
                        frameReceived(frame);
                    }

                    FrameDecoded?.Invoke(frame, new FrameQuality(
                        Mode, frame.Length, info.CorrectedSymbols, info.CrcValid,
                        HeaderType: info.HeaderType,
                        PlainIl2p: delivery.PlainIl2p,
                        TrailerNearBits: delivery.TrailerNearBits,
                        MonitorOnly: delivery.MonitorOnly));
                },
                crcMode: crc, acceptPlainIl2p: acceptPlainIl2p, syncWord: SyncWord);
        }

        _ffeTaps = new float[PhaseFractions.Length * FfeLength];
        _ffeHistory = new float[PhaseFractions.Length * FfeLength];
        _ffeCount = new int[PhaseFractions.Length];
        _alternatingOuterRun = new int[PhaseFractions.Length];
        _nonAlternatingRun = new int[PhaseFractions.Length];
        _ffeFrozen = new bool[PhaseFractions.Length];
        _previousLevel = new int[PhaseFractions.Length];

        _upsample = sampleRate / symbolRate < 8 ? 2 : 1;
        _clockIncrement = (double)symbolRate / (sampleRate * _upsample);
        _pointsPerSymbol = 1.0 / _clockIncrement;

        // The decision is taken three points AFTER the clock's instant, because the late
        // phases read slicer input that has not arrived yet at the instant itself; the ring
        // holds the normalised slicer input either side of it, and nothing downstream of a
        // decision notices that it is emitted three tenths of a symbol late.
        _ringLead = (int)Math.Ceiling(PhaseStep * PhasePairs * _pointsPerSymbol) + 1;
        _slicerRing = new float[(2 * _ringLead) + 4];
        ResetFfe();
    }

    /// <summary>Creates the 9600 bps mode - NinoTNC mode 3 (4800 sym/s, 10 kHz OBW).</summary>
    public static C4fskModem C4fsk9600(
        int sampleRate, Action<byte[]> frameReceived, bool acceptPlainIl2p = false) =>
        new(sampleRate, frameReceived, 4800, acceptPlainIl2p: acceptPlainIl2p);

    /// <summary>Creates the 19200 bps mode - NinoTNC mode 1 (9600 sym/s, 20 kHz OBW).</summary>
    public static C4fskModem C4fsk19200(
        int sampleRate, Action<byte[]> frameReceived, bool acceptPlainIl2p = false) =>
        new(sampleRate, frameReceived, 9600, acceptPlainIl2p: acceptPlainIl2p);

    /// <inheritdoc />
    public event Action<byte[], FrameQuality>? FrameDecoded;

    /// <summary>How many timing phases this modem decides every symbol at, phase 0 being the
    /// recovered clock's own instant.</summary>
    internal static int TimingPhaseCount => PhaseFractions.Length;

    /// <summary>The phases' offsets from the clock instant, in symbols - the bench's read-only
    /// view of <see cref="PhaseFractions"/>.</summary>
    internal static ReadOnlySpan<double> TimingPhaseFractions => PhaseFractions;

    /// <summary>Bench seam: every phase's decided dibit, as (phase, first bit, second bit),
    /// alongside the deframer it is pushed into. Null in deployment, and the only way to see
    /// that the phases genuinely decide differently. Not part of the deployment surface.</summary>
    internal Action<int, int, int>? PhaseDibitObserver { get; set; }

    /// <inheritdoc />
    public string Mode => $"c4fsk{_symbolRate * 2}{(_crc ? "-il2pc" : "-il2p")}";

    /// <inheritdoc />
    public bool CarrierDetect => _packetDcd.Asserted;

    /// <inheritdoc />
    public bool ChannelBusy => _packetDcd.Asserted || _energyBusy.Busy;

    /// <inheritdoc />
    public void Process(ReadOnlySpan<float> samples)
    {
        foreach (float sample in samples)
        {
            float filtered = _rxFilter.Next(sample);
            _energyBusy.Process(filtered);

            // No signal, no bits. On silence this slicer saturates to the outer levels
            // (the envelope collapses and the normalised value rails), producing 1-heavy
            // garbage - and the Mode-2 sync is 18/24 ones, so the deframer false-locks
            // continuously between bursts and is mid-garbage-frame when a real sync
            // arrives (measured: ~12k near-sync hits in one recording's silence). The
            // 2-level modes survive the same silence by statistics - balanced garbage
            // rarely matches a balanced sync - but here the bit stream must simply stop
            // when the channel is idle. The energy detector's hold keeps bits flowing
            // through the tail of a burst, so nothing real is lost.
            if (!_energyBusy.Busy)
            {
                // Reset the deframer on the energy-gate falling edge: if it was
                // mid-collection when the carrier stopped, abandon the phantom frame so
                // the next burst's sync word is not consumed as payload. The equalizer
                // resets with it - its taps model the burst that just ended.
                if (_previousEnergyBusy)
                {
                    foreach (Il2pReceiver deframer in _deframers)
                    {
                        deframer.Reset();
                    }

                    _pendingInstant = -1;
                    ResetFfe();
                }

                _previousEnergyBusy = false;
                continue;
            }

            _previousEnergyBusy = true;

            for (int point = 1; point <= _upsample; point++)
            {
                float value = _previousFiltered + ((filtered - _previousFiltered) * point / _upsample);

                // Track the outer envelope exactly as the 2-level FSK slicer does; the
                // four levels sit at ±1 and ±⅓ of it, so the three thresholds are 0 and
                // ±⅔ of the half-swing around the midpoint.
                // Decay is 20× slower than the binary modes': the envelope's job here is
                // to HOLD the outer reference through inner-heavy scrambled stretches, not
                // to track - at 2e-4 it sagged toward the inner levels mid-frame and the
                // ⅔ threshold ate them (5/8; 7-8/8 at 1e-5, measured on the same bursts).
                _peakHigh += (value - _peakHigh) * (value > _peakHigh ? 0.08f : 0.00001f);
                _peakLow += (value - _peakLow) * (value < _peakLow ? 0.08f : 0.00001f);
                float mid = (_peakHigh + _peakLow) * 0.5f;
                float half = Math.Max((_peakHigh - _peakLow) * 0.5f, 1e-6f);
                float normalised = (value - mid) / half;
                _slicerRing[(int)(_pointIndex % _slicerRing.Length)] = normalised;

                // Symbol clock. The shared BitDpll cannot be used as-is for 4-PAM: it
                // nudges on EVERY level change, and an outer-to-outer transition sweeps
                // through the inner thresholds mid-flight, injecting nudges half a symbol
                // off - measured as total clock collapse (0 frames from a recording that
                // decodes with 1 symbol error in 316 at a fixed phase). Only the middle
                // threshold's crossings - sign changes - land at symbol boundaries, so
                // only they steer the clock; the 4-level decision is taken at the wrap,
                // through the equalizer - and, since PR #331, at each timing phase either
                // side of it as well (see DecidePending).
                _clockPhase += _clockIncrement;
                if (_clockPhase >= 0.5)
                {
                    _clockPhase -= 1.0;
                    if (_pendingInstant >= 0)
                    {
                        DecidePending();   // cannot happen at 10 points per symbol; cheap insurance
                    }

                    _pendingInstant = _pointIndex;
                }

                if (_pendingInstant >= 0 && _pointIndex >= _pendingInstant + _ringLead)
                {
                    DecidePending();
                }

                int sign = normalised > 0 ? 1 : 0;
                if (sign != _lastSign)
                {
                    _lastSign = sign;
                    _packetDcd.OnTransition(_clockPhase);

                    // The clock acquires at Dire Wolf's inertia and holds, nearly rigid, once
                    // DCD says it has converged - see HoldInertia.
                    _clockPhase *= _packetDcd.Asserted ? HoldInertia : AcquireInertia;
                }

                _pointIndex++;
            }

            _previousFiltered = filtered;
        }
    }

    /// <summary>Decides the symbol whose clock instant is pending, at every timing phase: each
    /// reads its own interpolated slicer input from the ring and decides it through its own
    /// equalizer and freeze state. Phase 0 is the clock's own instant, and is the one that
    /// drives DCD and the dedupe's symbol clock.</summary>
    private void DecidePending()
    {
        long instant = _pendingInstant;
        _pendingInstant = -1;
        int ring = _slicerRing.Length;
        for (int phase = 0; phase < PhaseFractions.Length; phase++)
        {
            double position = instant + (PhaseFractions[phase] * _pointsPerSymbol);
            long lower = (long)Math.Floor(position);
            float fraction = (float)(position - lower);
            int a = (int)(((lower % ring) + ring) % ring);
            int b = (a + 1) % ring;
            Decide(phase, _slicerRing[a] + (fraction * (_slicerRing[b] - _slicerRing[a])));
        }
    }

    /// <summary>One timing phase's 4-PAM decision on its own interpolated slicer input.</summary>
    private void Decide(int phase, float normalised)
    {
        int taps = phase * FfeLength;
        for (int t = 0; t < FfeLength - 1; t++)
        {
            _ffeHistory[taps + t] = _ffeHistory[taps + t + 1];
        }

        _ffeHistory[taps + FfeLength - 1] = normalised;
        if (++_ffeCount[phase] < (FfeLength / 2) + 1)
        {
            return;
        }

        float equalized = 0;
        float power = 1e-6f;
        for (int t = 0; t < FfeLength; t++)
        {
            equalized += _ffeTaps[taps + t] * _ffeHistory[taps + t];
            power += _ffeHistory[taps + t] * _ffeHistory[taps + t];
        }

        int level = equalized switch
        {
            < -2f / 3f => 0,
            < 0f => 1,
            < 2f / 3f => 2,
            _ => 3,
        };

        // Our own 0x77 preamble alternates ±outer every symbol, so each neighbour is exactly
        // the negated centre - rank-deficient training in which any taps with
        // w[c] − w[c−1] − w[c+1] = 1 fit the preamble perfectly, and noise random-walks the
        // taps along that null space for the whole run-in. Measured as an INVERTED preamble
        // trend in the sim (25 dB AWGN: txd20 12/12 → txd250 4/12; clean at 60 dB where
        // nothing drives the walk). Cure: freeze adaptation while the decision stream is an
        // outer alternation, with hysteresis both ways - 8 conforming decisions freeze, 4
        // consecutive non-conforming unfreeze - so an isolated noisy preamble decision cannot
        // restart the drift (at 5 samples/symbol the 19200 mode's preamble decisions are noisy
        // enough that a single-run gate leaked: txd250 3/12 vs 12/12 with hysteresis).
        // Scrambled data never freezes (a conforming run of 8 is 4⁻⁸ per position) and a
        // NinoTNC's 2-up-2-down preamble never matches the alternation test, so real-capture
        // behaviour is untouched. NO identity leak: a leak's few-per-cent tap bias measurably
        // costs the most marginal real capture (corpus txd50 burst 1 sits at exactly its RS
        // budget of 8 bad bytes - 45/45 without leak, 44/45 with 0.002).
        if (level is 0 or 3 && level == 3 - _previousLevel[phase])
        {
            _nonAlternatingRun[phase] = 0;
            if (++_alternatingOuterRun[phase] >= 8)
            {
                _ffeFrozen[phase] = true;
            }
        }
        else
        {
            _alternatingOuterRun[phase] = 0;
            if (++_nonAlternatingRun[phase] >= 4)
            {
                _ffeFrozen[phase] = false;
            }
        }

        _previousLevel[phase] = level;
        if (!_ffeFrozen[phase])
        {
            float target = level switch { 0 => -1f, 1 => -1f / 3f, 2 => 1f / 3f, _ => 1f };
            float step = FfeMu / power * (target - equalized);
            for (int t = 0; t < FfeLength; t++)
            {
                _ffeTaps[taps + t] += step * _ffeHistory[taps + t];
            }
        }

        int dibit = LevelToDibit[level];
        int first = (dibit >> 1) & 1;
        int second = dibit & 1;
        _deframers[phase].PushBit(first);
        _deframers[phase].PushBit(second);
        PhaseDibitObserver?.Invoke(phase, first, second);
        if (phase == 0)
        {
            _symbolsSeen++;
            _packetDcd.OnSymbol();
        }
    }

    /// <inheritdoc />
    public float[] Modulate(ReadOnlySpan<byte> ax25Frame, int txDelayMilliseconds)
    {
        byte[] wire = Il2pCodec.Encode(ax25Frame, appendCrc: _crc);

        // MMDVM-TNC Mode 2 wire layout: 0x77 preamble bytes for the TXDELAY, the 4-byte
        // outer-only sync, then the IL2P bytes.
        int bitRate = _symbolRate * 2;

        // Floor of ~20 ms of preamble regardless of TXDELAY: the receive side's energy
        // gate takes a block or two to assert from silence, and bits emitted before it
        // opens never reach the deframer. At 2 bytes (8 symbols ≈ 2 ms) the entire
        // preamble died inside that latency - cold acquisition failed at TXDELAY 0 while
        // 20 ms decodes 10/10. The NinoTNC's own C4FSK floor is one 16-bit word, but it
        // has no such gate; ours buys silence immunity (12k false sync locks per
        // recording without it) at the price of this minimum.
        int preambleBytes = Math.Max(_symbolRate / 50 / 4, txDelayMilliseconds * bitRate / 1000 / 8);
        var stream = new byte[preambleBytes + SyncBytes.Length + wire.Length];
        Array.Fill(stream, PreambleByte, 0, preambleBytes);
        SyncBytes.CopyTo(stream, preambleBytes);
        wire.CopyTo(stream, preambleBytes + SyncBytes.Length);

        var bits = new byte[stream.Length * 8];
        for (int i = 0; i < stream.Length; i++)
        {
            for (int k = 0; k < 8; k++)
            {
                bits[(i * 8) + k] = (byte)((stream[i] >> (7 - k)) & 1);
            }
        }

        // Dibits → 4-PAM levels → the same pulse-shaping low-pass the receiver assumes,
        // run past the end to flush the filter's group delay (the FskModem trailer lesson).
        // Same eye physics as receive: 0.55× shaping would hand the far receiver a
        // collapsed 4-level eye. 1.0× approximates the Gaussian BT 0.6 pulse MMDVM-TNC
        // transmits (bench-validated against a real NinoTNC rather than matched exactly).
        int samplesPerSymbol = _sampleRate / _symbolRate;
        int taps = 48 * _sampleRate / 48000;
        var shaper = new FirFilter(FilterDesign.LowPass(1.0 * _symbolRate, _sampleRate, taps));
        ReadOnlySpan<float> amplitudes = [-0.8f, -0.8f / 3f, 0.8f / 3f, 0.8f];
        var samples = new float[(bits.Length / 2 * samplesPerSymbol) + taps];
        int position = 0;
        for (int i = 0; i + 1 < bits.Length; i += 2)
        {
            int dibit = ((bits[i] & 1) << 1) | (bits[i + 1] & 1);
            float amplitude = amplitudes[DibitToLevel[dibit]];
            for (int k = 0; k < samplesPerSymbol; k++)
            {
                samples[position++] = shaper.Next(amplitude);
            }
        }

        while (position < samples.Length)
        {
            samples[position++] = shaper.Next(0f);
        }

        return samples;
    }

    /// <inheritdoc />
    public void ResetCarrierState()
    {
        _packetDcd.Reset();
        _energyBusy.Reset();
        _peakHigh = 0;
        _peakLow = 0;
        _previousFiltered = 0;
        _pendingInstant = -1;
        ResetFfe();
    }

    private void ResetFfe()
    {
        Array.Clear(_ffeTaps);
        Array.Clear(_ffeHistory);
        Array.Clear(_ffeCount);
        Array.Clear(_alternatingOuterRun);
        Array.Clear(_nonAlternatingRun);
        Array.Clear(_ffeFrozen);
        Array.Clear(_previousLevel);
        for (int phase = 0; phase < PhaseFractions.Length; phase++)
        {
            _ffeTaps[(phase * FfeLength) + (FfeLength / 2)] = 1;
        }
    }

    private static int[] BuildInverse()
    {
        var inverse = new int[4];
        for (int dibit = 0; dibit < 4; dibit++)
        {
            inverse[DibitToLevel[dibit]] = dibit;
        }

        return inverse;
    }
}
