using M0LTE.Dsp;
using Packet.SoundModem.Hdlc;
using M0LTE.Il2p;

namespace Packet.SoundModem.Modems;

/// <summary>Framing carried over the direct-FSK baseband.</summary>
public enum FskFraming
{
    /// <summary>Classic G3RUH: HDLC, NRZI, free-running x¹⁷+x¹²+1 scrambler.</summary>
    ClassicHdlc,

    /// <summary>IL2P with trailing CRC (NinoTNC "9600 GFSK IL2P+CRC"): raw bits - no
    /// NRZI, no G3RUH scrambler (IL2P scrambles packet-synchronously itself).</summary>
    Il2pCrc,

    /// <summary>IL2P without the CRC trailer.</summary>
    Il2p,
}

/// <summary>
/// Direct baseband FSK ("RUH") modem, Dire Wolf demod_9600 lineage: the receive chain is
/// a low-pass filter, an envelope-tracking slicer and the shared DPLL; transmit shapes a
/// ±1 NRZ pulse train through the same low-pass design. Runs at 48 kHz (5 samples per bit
/// at 9600, 10 at 4800). Framing per <see cref="FskFraming"/>. Covers the NinoTNC 9600
/// GFSK (modes 0 AX.25 / 2 IL2P+CRC) and 4800 GFSK (mode 4, IL2P+CRC) modes; the classic
/// and IL2P legs are cross-validated against Dire Wolf audio and bench-proven against a
/// NinoTNC.
/// </summary>
/// <remarks>
/// Every symbol is decided at seven timing phases (see <see cref="TimingDiversity"/>): the
/// recovered clock instant and 10, 20 and 30 % of a symbol either side, interpolated from a
/// short ring of the slicer's input. Each phase carries its own deframer - and, on the classic
/// leg, its own descrambler and NRZI state - and a frame any phase reads is delivered once,
/// behind a symbol-clocked content dedupe. The filter, the slicer's envelope tracker, the clock
/// and DCD are shared, so the cost is the decision stage and six extra deframers.
/// </remarks>
public sealed class FskModem : IModem
{
    private readonly int _baud;
    private readonly int _sampleRate;
    private readonly FskFraming _framing;
    private readonly FirFilter _rxFilter;
    private readonly BitDpll _dpll;
    private readonly PacketDcd _packetDcd = new();
    private readonly EnergyBusyDetector _energyBusy;
    private readonly int _upsample;
    private float _peakHigh;
    private float _peakLow;
    private float _previousFiltered;
    private float _previousExcess;

    /// <summary>
    /// The clock's inertia, fixed: <see cref="BitDpll"/>'s own default, which is what this mode
    /// has always run.
    /// </summary>
    /// <remarks>
    /// <para><b>The DCD-gated hold that the PSK modes took (rx-roadmap workstream 10, issue
    /// #331) was built here, measured, and rejected.</b> On the sim rows it is neutral, exactly
    /// as it is on BPSK: timing diversity on, hold at 0.995 against no hold, N=200 at TXDELAY
    /// 150, fsk9600-il2p AWGN +9/+10/+11 dB 135/183/196 against 135/183/197; fsk4800-il2p AWGN
    /// +6/+7/+8 dB 51/147/180 both ways; fsk9600 fm-data +12/+13/+14 dB CNR 136/188/198 both
    /// ways; fsk9600-il2p fm-data at N=600, +10/+11 dB CNR, 520/564 with it against 524/569
    /// without, about one sigma down. With diversity off it is neutral to a frame as well
    /// (AWGN +9/+10/+11 dB 45/128/176 against the 45/128/177 this mode has always measured).
    /// </para>
    /// <para><b>And it costs an order of magnitude of sample-clock tolerance</b>, which is what
    /// settled it: <c>The_Clock_Tracks_A_Mistuned_Transmitter</c> passes +-2000 ppm as the mode
    /// stands and fails from +-500 ppm with the hold in (both bauds, both signs, clean signal).
    /// The reason is this chain's one deliberate difference from the PSK chains: it does not
    /// interpolate its zero crossings (see the note in <see cref="Process"/>), so a nudge is
    /// quantised to a tenth of a symbol and the loop's whole correction is (1 - inertia) times
    /// the measured phase. At 0.74 that is 26 % a transition and follows any real crystal; at
    /// 0.995 it is 0.5 %, the loop's bandwidth drops by twenty, and a mistuned transmitter walks
    /// the clock out past DCD's own window - so DCD drops, the clock re-acquires, DCD asserts,
    /// the hold re-engages, and the frame dies in the limit cycle. A stiff clock and an
    /// unresolved crossing do not go together. If this chain ever gets matched-filter timing
    /// (the standing note in <see cref="Process"/>), the hold is worth trying again.</para>
    /// </remarks>
    private const double ClockInertia = 0.74;

    /// <summary>Dedupe window across the timing phases' deframers, in symbols: shorter than
    /// the shortest frame either framing can carry (an IL2P header alone is 120 symbols at one
    /// bit each, and the shortest AX.25 frame is longer still), longer than the trailer a held
    /// plain IL2P reading waits for (32 bits).</summary>
    private const int DedupeWindowSymbols = 48;

    // One bit sink per timing phase (see TimingDiversity): the phases decide the same symbols
    // at slightly different instants, each with its own deframer - and, on the classic leg, its
    // own descrambler and NRZI state, since both are bit-serial and a phase that reads a
    // different bit must carry the difference forward. A frame any phase reads is delivered
    // once, the first copy to arrive winning, behind the content dedupe.
    private readonly Action<int>[] _sinks;
    private readonly FrameDeduper _deduper;
    private readonly Action? _resetDeframers;
    private long _symbolsSeen;
    private bool _previousDcd;

    // Recent slicer excess (the envelope-midpoint-removed value the level is sliced from),
    // indexed by decision point modulo the ring length, so a decision can be taken a little
    // before or after the clock's instant. The late phases need points that arrive after the
    // instant, hence the deferral in DecidePending.
    private readonly float[] _ring;
    private readonly int _ringLead;
    private readonly int _pointsPerSymbol;
    private long _pointIndex = -1;
    private long _pendingInstant = -1;

    /// <summary>Bench seam: receives (timing phase, decided level) for every symbol at every
    /// phase, so a test can score a known frame phase by phase and prove the phases actually
    /// differ. Set before the first <see cref="Process(ReadOnlySpan{float})"/> call. Not part
    /// of the deployment surface.</summary>
    internal Action<int, int>? PhaseDecisionObserver { get; set; }

    /// <summary>Creates the modem.</summary>
    /// <param name="sampleRate">Sample rate; must be a multiple of <paramref name="baud"/>
    /// (48000 typical).</param>
    /// <param name="frameReceived">Receives each decoded AX.25 frame.</param>
    /// <param name="framing">Wire framing (classic G3RUH vs IL2P).</param>
    /// <param name="baud">Baseband symbol rate: 9600 (modes 0/2) or 4800 (mode 4).</param>
    /// <param name="acceptPlainIl2p">Pass frames that arrive as plain IL2P, with no trailing CRC,
    /// to <paramref name="frameReceived"/> as well as reporting them (off by default, and inert
    /// unless <paramref name="framing"/> is <see cref="FskFraming.Il2pCrc"/>). They are read
    /// either way - see <see cref="Il2pReceiver"/> for what that buys and what it costs.</param>
    public FskModem(
        int sampleRate, Action<byte[]> frameReceived, FskFraming framing, int baud = 9600,
        bool acceptPlainIl2p = false)
    {
        ArgumentNullException.ThrowIfNull(frameReceived);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(baud, 0);
        if (sampleRate % baud != 0)
        {
            throw new ArgumentException($"sample rate must be a multiple of {baud}", nameof(sampleRate));
        }

        _baud = baud;
        _sampleRate = sampleRate;
        _framing = framing;
        _rxFilter = new FirFilter(FilterDesign.LowPass(0.55 * baud, sampleRate, 48 * sampleRate / 48000));
        _energyBusy = new EnergyBusyDetector(sampleRate);

        // At 48 kHz there are only 5 samples per bit at 9600 - each quantised DPLL nudge
        // is ±10% of a bit. Dire Wolf's demod_9600 interpolates ×2 before its PLL for the
        // same reason ("upsample" in demod_9600.c); do likewise so timing corrections
        // land on a 10-points-per-bit grid. 4800 already has 10 samples/bit at 48 kHz, so
        // it needs no interpolation.
        _upsample = sampleRate / baud < 8 ? 2 : 1;
        _pointsPerSymbol = sampleRate * _upsample / baud;
        // The latest phase reads up to ceil(reach) points past the instant, plus one for the
        // interpolation's upper neighbour; the ring spans both sides of the instant with room.
        _ringLead = (int)Math.Ceiling(TimingDiversity.FskReach * _pointsPerSymbol) + 1;
        _ring = new float[(2 * _ringLead) + 4];

        // Clocked in symbols, with a window shorter than any frame: the copies to merge arrive
        // within a symbol of each other (a held plain reading within a trailer's worth), and a
        // genuine repeat of even the shortest frame is further away than that.
        _deduper = new FrameDeduper(DedupeWindowSymbols);
        _sinks = new Action<int>[TimingDiversity.PhaseCount];
        if (framing == FskFraming.ClassicHdlc)
        {
            for (int phase = 0; phase < _sinks.Length; phase++)
            {
                var deframer = new HdlcDeframer(frame =>
                {
                    if (!_deduper.ShouldEmit(frame, _symbolsSeen))
                    {
                        return;
                    }

                    frameReceived(frame);
                    // HDLC has no FEC: an FCS pass proves zero residual errors, not how many
                    // the channel had - CorrectedBytes is honestly null. What the phases buy
                    // here is exactly "any phase whose FCS checks", which is how Dire Wolf's
                    // multi-slicer decoders earn theirs.
                    FrameDecoded?.Invoke(frame, new FrameQuality(Mode, frame.Length, null, null));
                });
                var descrambler = new G3ruhScrambler();
                var nrzi = new NrziDecoder();
                _sinks[phase] = level => deframer.PushBit(nrzi.Decode(descrambler.Descramble(level)));
            }
        }
        else
        {
            var deframers = new Il2pReceiver[TimingDiversity.PhaseCount];
            for (int phase = 0; phase < deframers.Length; phase++)
            {
                var deframer = new Il2pReceiver(
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
                    crcMode: framing == FskFraming.Il2pCrc, acceptPlainIl2p: acceptPlainIl2p);
                deframers[phase] = deframer;
                _sinks[phase] = bit => deframer.PushBit(bit);
            }

            // Reset the deframers on the DCD falling edge - same rationale as BpskModem:
            // a carrier that drops mid-collection leaves the deframer consuming the next
            // transmission's sync word as phantom payload.
            _resetDeframers = () =>
            {
                foreach (Il2pReceiver deframer in deframers)
                {
                    deframer.Reset();
                }
            };
        }

        _dpll = new BitDpll(
            baud, sampleRate * _upsample,
            _ =>
            {
                // The decision waits until the points either side of this instant are in the
                // ring - see DecidePending. The level the DPLL sliced is the phase-0 reading
                // and is re-derived there from the ring, identically.
                if (_pendingInstant >= 0)
                {
                    DecidePending();
                }

                _pendingInstant = _pointIndex;
            },
            inertia: ClockInertia,
            transitionObserver: _packetDcd.OnTransition,
            // The slicer excess at the symbol's own point: a run of one scrambled bit value
            // holds its full eye excess (no transitions, but plainly not silence - see
            // PacketDcd, issue #339), while silence collapses it toward the decayed
            // envelope midpoint.
            symbolObserver: () => _packetDcd.OnSymbol(Math.Abs(_previousExcess)));
    }

    /// <summary>Decides the symbol whose clock instant is pending, at each timing phase: every
    /// phase reads its own interpolated slicer excess from the ring, slices it, and feeds its
    /// own deframer. Phase 0 is the clock's own instant and reads exactly the level the DPLL
    /// sliced, so this chain's long-standing behaviour is the phase-0 stream unchanged.</summary>
    private void DecidePending()
    {
        long instant = _pendingInstant;
        _pendingInstant = -1;
        _symbolsSeen++;

        bool dcd = _packetDcd.Asserted;
        if (_previousDcd && !dcd)
        {
            _resetDeframers?.Invoke();
        }

        _previousDcd = dcd;

        int ring = _ring.Length;
        for (int phase = 0; phase < TimingDiversity.PhaseCount; phase++)
        {
            double position = instant + (TimingDiversity.FskPhaseFractions[phase] * _pointsPerSymbol);
            long lower = (long)Math.Floor(position);
            float fraction = (float)(position - lower);
            int a = (int)(((lower % ring) + ring) % ring);
            int b = (a + 1) % ring;
            float excess = _ring[a] + (fraction * (_ring[b] - _ring[a]));
            int level = excess > 0 ? 1 : 0;
            PhaseDecisionObserver?.Invoke(phase, level);
            _sinks[phase](level);
        }
    }

    /// <summary>Creates the 9600 baud mode - NinoTNC mode 0 (classic AX.25) or 2
    /// (IL2P+CRC), 20 kHz OBW.</summary>
    public static FskModem Fsk9600(int sampleRate, Action<byte[]> frameReceived, FskFraming framing) =>
        new(sampleRate, frameReceived, framing, 9600);

    /// <summary>Creates the 4800 baud mode - NinoTNC mode 4 (IL2P+CRC), 10 kHz OBW.</summary>
    public static FskModem Fsk4800(
        int sampleRate, Action<byte[]> frameReceived, FskFraming framing = FskFraming.Il2pCrc,
        bool acceptPlainIl2p = false) =>
        new(sampleRate, frameReceived, framing, 4800, acceptPlainIl2p);

    /// <inheritdoc />
    public event Action<byte[], FrameQuality>? FrameDecoded;

    /// <inheritdoc />
    public string Mode => _framing switch
    {
        FskFraming.ClassicHdlc => $"fsk{_baud}",
        FskFraming.Il2pCrc => $"fsk{_baud}-il2pc",
        _ => $"fsk{_baud}-il2p",
    };

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

            for (int point = 1; point <= _upsample; point++)
            {
                // Linear interpolation between successive filtered samples (point ==
                // _upsample is the sample itself) - see the _upsample ctor note.
                float value = _previousFiltered
                    + (filtered - _previousFiltered) * point / _upsample;

                // Envelope-midpoint slicer (as in the AFSK demod): tracks soundcard DC
                // offset and level without assuming a centred signal.
                _peakHigh += (value - _peakHigh) * (value > _peakHigh ? 0.08f : 0.0002f);
                _peakLow += (value - _peakLow) * (value < _peakLow ? 0.08f : 0.0002f);
                float excess = value - (_peakHigh + _peakLow) * 0.5f;
                int level = excess > 0 ? 1 : 0;

                // NOTE: sub-sample crossing interpolation (a measured win for AFSK/BPSK)
                // is deliberately NOT used here. At 5 samples/bit behind the tight
                // 0.55·baud pulse filter, the crossings carry strong data-dependent ISI
                // offsets, and interpolating them faithfully makes the DPLL chase that
                // jitter into the closed eye for unlucky bit patterns (found by the
                // back-to-back loopback test). Quantised nudges average the ISI out;
                // revisit with matched-filter timing against a real off-air 9600 corpus.
                _previousExcess = excess;
                _pointIndex++;
                _ring[(int)(_pointIndex % _ring.Length)] = excess;
                _dpll.Sample(level);
                if (_pendingInstant >= 0 && _pointIndex >= _pendingInstant + _ringLead)
                {
                    DecidePending();
                }
            }

            _previousFiltered = filtered;
        }
    }

    /// <inheritdoc />
    public float[] Modulate(ReadOnlySpan<byte> ax25Frame, int txDelayMilliseconds)
    {
        byte[] wireBits;
        if (_framing == FskFraming.ClassicHdlc)
        {
            int openingFlags = Math.Max(2, txDelayMilliseconds * _baud / (8 * 1000));
            byte[] hdlcBits = HdlcFramer.FrameBits(ax25Frame, openingFlags, closingFlags: 2);
            var nrzi = new NrziEncoder();
            var scrambler = new G3ruhScrambler();
            wireBits = new byte[hdlcBits.Length];
            for (int i = 0; i < hdlcBits.Length; i++)
            {
                wireBits[i] = (byte)scrambler.Scramble(nrzi.Encode(hdlcBits[i]));
            }
        }
        else
        {
            byte[] wire = Il2pCodec.Encode(ax25Frame, appendCrc: _framing == FskFraming.Il2pCrc);
            int preambleBits = Math.Max(16, txDelayMilliseconds * _baud / 1000);
            wireBits = Il2pFramer.FrameBits(wire, preambleBits, Il2pFramer.PreambleStyle.Alternating);
        }

        // ±1 NRZ pulse train through the pulse-shaping low-pass ('1' = positive deviation).
        int samplesPerBit = _sampleRate / _baud;
        int taps = 48 * _sampleRate / 48000;
        var shaper = new FirFilter(FilterDesign.LowPass(0.55 * _baud, _sampleRate, taps));

        // The shaper delays the signal by ~taps/2 samples, so the burst must run past the
        // last bit to flush it - truncating at bits×samplesPerBit chops the final ~5 bits
        // of energy off the air. For IL2P that is the Hamming-coded CRC trailer, and
        // whether the mangled trailer is still correctable depends on frame content: the
        // bug presented as our receiver deterministically dropping *specific payloads*
        // (4/10 at any TXDELAY) while a NinoTNC decoded the same audio 10/10. Classic
        // HDLC escapes by luck - its closing flags sit after the FCS as slack.
        var samples = new float[(wireBits.Length * samplesPerBit) + taps];
        int position = 0;
        foreach (byte bit in wireBits)
        {
            float level = bit != 0 ? 0.8f : -0.8f;
            for (int i = 0; i < samplesPerBit; i++)
            {
                samples[position++] = shaper.Next(level);
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
        _previousExcess = 0;
        _previousDcd = false;
        _pendingInstant = -1;
        Array.Clear(_ring);
    }
}
