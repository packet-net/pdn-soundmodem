using M0LTE.Il2p;

namespace Packet.SoundModem.Modems;

/// <summary>BPSK carrying IL2P - the NinoTNC 300 (mode 8, 300 baud) and 1200 (mode 10,
/// 1200 baud) mode family, both phase-modulating a 1500 Hz tone - as an
/// <see cref="IModem"/>. Symbol rates and carrier are Nino's, per the v3/4.43
/// mode-switch mapping in flashtnc's release-notes.txt.</summary>
public sealed class BpskModem : IModem, IConstellationSource, IFrameSpanSource
{
    private readonly BpskDemodulator _demodulator;
    private readonly BpskModulator _modulator;

    /// <summary>Dedupe window across the timing phases' deframers, in symbols: shorter than the
    /// shortest IL2P frame (a 15-byte header alone is 120 symbols at one bit each), longer than
    /// the trailer a held plain reading waits for (32 bits).</summary>
    private const int DedupeWindowSymbols = 48;
    private readonly Il2pReceiver[] _deframers;
    /// <summary>
    /// Where the frame just delivered was in the receive audio, for the channel's per-frame
    /// level: marked at the sync its deframer locked on and at the sample its last bit was taken
    /// on, both from the demodulator's own count of input samples. See <see cref="FrameSpan"/>.
    /// </summary>
    private readonly FrameSpan _span = new(TimingDiversity.PhaseCount);

    private readonly FrameDeduper _deduper;
    private readonly int _baud;
    private long _symbolsSeen;
    private readonly bool _crc;

    /// <summary>Creates the modem.</summary>
    /// <param name="sampleRate">Channel DSP rate (multiple of <paramref name="baud"/>).</param>
    /// <param name="frameReceived">Receives each decoded AX.25 frame.</param>
    /// <param name="crc">IL2P+CRC mode (both stations must agree). On for NinoTNC
    /// networks.</param>
    /// <param name="carrierFrequency">Carrier centre; 1500 Hz convention.</param>
    /// <param name="baud">Symbol rate - also the bit rate, BPSK carrying one bit per
    /// symbol.</param>
    /// <param name="rollOff">RRC roll-off.</param>
    /// <param name="detector">Differential (default) or coherent detection.</param>
    /// <param name="acceptPlainIl2p">Pass frames that arrive as plain IL2P, with no trailing CRC,
    /// to <paramref name="frameReceived"/> as well as reporting them (off by default, and inert
    /// unless <paramref name="crc"/> is on). They are read either way - see
    /// <see cref="Il2pReceiver"/> for what that buys and what it costs.</param>
    public BpskModem(
        int sampleRate, Action<byte[]> frameReceived, bool crc = true,
        double carrierFrequency = 1500, int baud = 300,
        double rollOff = BpskModulator.DefaultRollOff,
        PskDetector detector = PskDetector.Differential,
        bool acceptPlainIl2p = false)
    {
        ArgumentNullException.ThrowIfNull(frameReceived);
        _crc = crc;
        _baud = baud;
        // Declared ahead of the deframer because its callback needs it - the carrier-offset
        // reading below - while it in turn cannot be built until the bit sink that drives it
        // exists. Nothing dereferences it until audio flows.
        BpskDemodulator? demodulator = null;
        // One deframer per timing phase (see TimingDiversity): the phases decide the same
        // symbols at slightly different instants, so their deframers run in lockstep and a
        // frame that any of them reads is delivered once, the first copy to arrive winning. The
        // short content window behind it catches the one way a copy can arrive later: a phase
        // whose own IL2P+CRC reading failed holds a plain reading until the trailer bits pass
        // and would otherwise show it as a second, monitor-only row.
        // Clocked in symbols, with a window shorter than any frame: the copies to merge arrive
        // within a symbol of each other (a held plain reading within a trailer's worth), and
        // a genuine repeat of even the shortest frame is further away than that.
        _deduper = new FrameDeduper(DedupeWindowSymbols);
        _deframers = new Il2pReceiver[TimingDiversity.PhaseCount];
        for (int phase = 0; phase < _deframers.Length; phase++)
        {
            int reading = phase;
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

                    // Before the event, so the channel's handler reads this frame's span.
                    _span.Complete(reading, demodulator!.InputSamplePosition);
                    FrameDecoded?.Invoke(frame, new FrameQuality(
                        Mode, frame.Length, info.CorrectedSymbols, info.CrcValid,
                        HeaderType: info.HeaderType,
                        // Read at the end of the burst that carried the frame, while the window it
                        // is measured over still describes that burst.
                        FrequencyOffsetHz: demodulator!.CarrierOffsetHz,
                        PlainIl2p: delivery.PlainIl2p,
                        TrailerNearBits: delivery.TrailerNearBits,
                        ErasedBytes: info.ErasedSymbols > 0 ? info.ErasedSymbols : null,
                        ChasedBits: info.ChasedBits > 0 ? info.ChasedBits : null,
                        MonitorOnly: delivery.MonitorOnly));
                },
                crc, acceptPlainIl2p);
            _deframers[phase].SyncFound = () => _span.Sync(reading, demodulator!.InputSamplePosition);
        }

        // Reset the deframers on the DCD falling edge. Without this, a frame whose carrier
        // drops mid-collection (the preceding transmission ends, a collision corrupts the
        // tail, or the signal fades) leaves the deframer blindly consuming the expected
        // payload bytes from whatever follows - including the next transmission's preamble
        // and sync word. The deframer then fails RS on the phantom frame and returns to
        // hunting, but the real frame's sync word has already been swallowed. This is the
        // continuous-decode robustness gap: 37 of 74 missed frames in the GB7RDG 24 h
        // benchmark decoded perfectly from isolated audio but were lost in-stream because
        // a preceding signal's collection state masked the new preamble. DCD drops within
        // 24 symbol times (80 ms at 300 Bd) of the carrier stopping - well before the next
        // transmission's sync word arrives - so the reset is always in time.
        bool previousDcd = false;
        demodulator = new BpskDemodulator(sampleRate, static _ => { },
            carrierFrequency, baud, detector, rollOff: rollOff,
            softBitSink: (bit, confidence, phase) =>
            {
                if (phase == 0)
                {
                    _symbolsSeen++;
                    bool dcd = demodulator!.CarrierDetect;
                    if (previousDcd && !dcd)
                    {
                        foreach (Il2pReceiver deframer in _deframers)
                        {
                            deframer.Reset();
                        }
                    }

                    previousDcd = dcd;
                }

                _deframers[phase].PushBit(bit, confidence);
            });
        _demodulator = demodulator;
        _demodulator.SymbolPlotted = (i, q) => SymbolPlotted?.Invoke(new ConstellationPoint(i, q));
        _modulator = new BpskModulator(sampleRate, baud, carrierFrequency, rollOff);
    }

    /// <inheritdoc />
    public event Action<ConstellationPoint>? SymbolPlotted;

    /// <summary>Creates the 300 bps mode (300 baud, 1500 Hz centre) - NinoTNC mode 8.</summary>
    /// <remarks>Runs the 0.35 default roll-off, the value the deployed
    /// <see cref="BpskMultiModem"/> bank has always used. This factory carried 0.20 from
    /// July 2026, chosen when the highest-energy-window method read a NinoTNC's own mode-8
    /// transmission as 328 Hz and 0.35 put us at 352 Hz, apparently wider than the TNC we
    /// share the channel with. The like-for-like whole-burst method that replaced that
    /// measurement (issue #2) reads the same reference recording at 398 Hz, so 0.35 is
    /// comfortably narrower than the TNC after all; and under calibrated AWGN a 0.35
    /// receive filter copies that real recording measurably better than 0.20 (248 vs 210
    /// of 400 bursts at -5 dB, 36 vs 14 at -6 dB) while 0.20 and 0.35 loopback pairs are
    /// indistinguishable at the knee. The real-TNC bench interop (6/6 both ways,
    /// docs/ninotnc-loop.md) was also only ever measured at 0.35, because nino-bench
    /// builds this modem through the constructor default. Issue #340 has the full
    /// investigation. <paramref name="carrierFrequency"/> (1500 Hz convention) moves the
    /// modem within the audio passband, QtSoundModem-style.</remarks>
    public static BpskModem Bpsk300(
        int sampleRate, Action<byte[]> frameReceived, bool crc = true,
        PskDetector detector = PskDetector.Differential, double carrierFrequency = 1500) =>
        new(sampleRate, frameReceived, crc, carrierFrequency, 300, BpskModulator.DefaultRollOff, detector);

    /// <summary>Creates the 1200 bps mode (1200 baud, 1500 Hz centre) - NinoTNC mode 10,
    /// sharing its 1200 sym/s and 2400 Hz OBW with 2400 QPSK.</summary>
    /// <remarks><paramref name="carrierFrequency"/> (1500 Hz convention) moves the modem
    /// within the audio passband, QtSoundModem-style.</remarks>
    public static BpskModem Bpsk1200(
        int sampleRate, Action<byte[]> frameReceived, bool crc = true,
        PskDetector detector = PskDetector.Differential, double carrierFrequency = 1500) =>
        new(sampleRate, frameReceived, crc, carrierFrequency, 1200, BpskModulator.DefaultRollOff, detector);

    /// <inheritdoc />
    public event Action<byte[], FrameQuality>? FrameDecoded;

    /// <inheritdoc />
    public string Mode => $"bpsk{_baud}{(_crc ? "-il2pc" : "-il2p")}";

    /// <inheritdoc />
    public bool CarrierDetect => _demodulator.CarrierDetect;

    /// <inheritdoc />
    public bool ChannelBusy => _demodulator.ChannelBusy;

    /// <summary>How far the current signal sits from this modem's carrier centre, in Hz
    /// (positive = above it), or null when nothing coherent enough to measure is present -
    /// <see cref="BpskDemodulator.CarrierOffsetHz"/> read live. Decoded frames already carry
    /// the reading in <see cref="FrameQuality.FrequencyOffsetHz"/>; this property is for the
    /// bursts that never produce one, polled while <see cref="CarrierDetect"/> holds.</summary>
    public double? CarrierOffsetHz => _demodulator.CarrierOffsetHz;

    /// <summary>Cumulative sync-found-but-Reed-Solomon-failed count summed over this modem's
    /// IL2P readings at every timing phase - see <see cref="Il2pReceiver.RsFailures"/> for
    /// exactly what one tick means and why deltas, not totals, are the usable signal. Three
    /// phases read the same bits, so one damaged transmission typically ticks this three times
    /// over, and a decoded frame can come with ticks from the phases that did not read it.</summary>
    public long RsFailures
    {
        get
        {
            long total = 0;
            foreach (Il2pReceiver deframer in _deframers)
            {
                total += deframer.RsFailures;
            }

            return total;
        }
    }

    /// <summary>Cumulative recovered-but-trailing-CRC-refused count on the link's own IL2P+CRC
    /// reading - see <see cref="Il2pReceiver.CrcFailures"/>.</summary>
    public long CrcFailures
    {
        get
        {
            long total = 0;
            foreach (Il2pReceiver deframer in _deframers)
            {
                total += deframer.CrcFailures;
            }

            return total;
        }
    }

    /// <summary>Bench seam: this modem's demodulator, so the sim oracles can record or drive
    /// its symbol instants (rx-roadmap workstream 4). Not part of the deployment surface.
    /// </summary>
    internal BpskDemodulator Demodulator => _demodulator;

    /// <inheritdoc />
    public bool TryTakeFrameSpan(out long fromSample, out long toSample) =>
        _span.TryTakeFrameSpan(out fromSample, out toSample);

    /// <inheritdoc />
    public void Process(ReadOnlySpan<float> samples) => _demodulator.Process(samples);

    /// <inheritdoc />
    public float[] Modulate(ReadOnlySpan<byte> ax25Frame, int txDelayMilliseconds)
    {
        byte[] wire = Il2pCodec.Encode(ax25Frame, appendCrc: _crc);
        int preambleBits = Math.Max(24, txDelayMilliseconds * _baud / 1000);
        byte[] bits = Il2pFramer.FrameBits(wire, preambleBits, Il2pFramer.PreambleStyle.Zeros);
        return _modulator.Modulate(bits);
    }

    /// <inheritdoc />
    public void ResetCarrierState() => _demodulator.ResetCarrierState();
}
