using M0LTE.Il2p;

namespace Packet.SoundModem.Modems;

/// <summary>QPSK carrying IL2P - the NinoTNC 600 (300 baud, 1500 Hz), 2400 (1200 baud,
/// 1500 Hz) and 3600 (1800 baud, 1650 Hz) mode family - as an <see cref="IModem"/>.
/// Symbol rates and carriers are Nino's, per the v3/4.43 mode-switch mapping in
/// flashtnc's release-notes.txt.</summary>
public sealed class QpskModem : IModem, IConstellationSource
{
    private readonly QpskDemodulator _demodulator;
    private readonly QpskModulator _modulator;

    /// <summary>Dedupe window across the timing phases' deframers, in symbols: shorter than the
    /// shortest IL2P frame (a 15-byte header alone is 60 symbols), longer than the trailer a
    /// held plain reading waits for (32 bits, 16 symbols).</summary>
    private const int DedupeWindowSymbols = 32;
    private readonly Il2pReceiver[] _deframers;
    private readonly FrameDeduper _deduper;
    private readonly int _bitRate;
    private long _symbolsSeen;
    private readonly bool _crc;

    private QpskModem(
        int sampleRate, int baud, double carrier, Action<byte[]> frameReceived, bool crc,
        double rollOff = QpskModulator.DefaultRollOff,
        PskDetector detector = PskDetector.Coherent, double? loopBandwidthHz = null,
        bool acceptPlainIl2p = false, bool decisionFeedback = true)
    {
        ArgumentNullException.ThrowIfNull(frameReceived);
        _bitRate = baud * 2;
        _crc = crc;
        QpskDemodulator? demodulator = null;
        // One deframer per timing phase (see QpskDemodulator.TimingPhaseCount): the phases
        // decide the same symbols at slightly different instants, so their deframers run in
        // lockstep and a frame that any of them reads is delivered once, the first copy to
        // arrive winning (usually all three arrive on the same symbol). The short content
        // window behind it catches the one way a copy can arrive later: a phase whose own
        // IL2P+CRC reading failed holds a plain reading until the trailer bits pass and
        // would otherwise show it as a second, monitor-only row.
        // Clocked in symbols, with a window shorter than any frame: the copies to merge arrive
        // within a symbol of each other (a held plain reading within a trailer's worth), and
        // a genuine repeat of even the shortest frame is further away than that.
        _deduper = new FrameDeduper(DedupeWindowSymbols);
        _deframers = new Il2pReceiver[QpskDemodulator.TimingPhaseCount];
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
                        // The measurement BpskModem has carried since issue #202; without it
                        // the whole live QPSK family reported no offset at all.
                        FrequencyOffsetHz: demodulator!.CarrierOffsetHz,
                        HeaderType: info.HeaderType,
                        PlainIl2p: delivery.PlainIl2p,
                        TrailerNearBits: delivery.TrailerNearBits,
                        ErasedBytes: info.ErasedSymbols > 0 ? info.ErasedSymbols : null,
                        ChasedBits: info.ChasedBits > 0 ? info.ChasedBits : null,
                        MonitorOnly: delivery.MonitorOnly));
                },
                crc, acceptPlainIl2p);
        }

        // Reset the deframers when the carrier goes - same rationale as BpskModem: a
        // carrier that drops mid-collection leaves the deframer consuming the next
        // transmission's preamble and sync word as phantom payload. On the coherent path
        // that is DCD's falling edge, as it always was. On the differential path DCD is
        // scored on decision quality (QpskDecisionDcd) and can dip on a burst that still
        // decodes while the carrier is plainly still there, so the carrier is taken to have
        // gone only when packet DCD and in-band energy are both down (ChannelBusy's falling
        // edge). Measured for issue #329: with the DCD's release level at 0.25 the reset on
        // DCD alone cost 43 of 200 qpsk3600 frames at +8 dB; at the shipped 0.10 it still
        // cost one or two frames on two knee rows, and the quiet rule gives them back.
        bool previousBusy = false;
        demodulator = new QpskDemodulator(
            sampleRate, baud, static (_, _) => { },
            carrier, detector, loopBandwidthHz, rollOff, decisionFeedback,
            softDibitSink: (first, second, confidence, phase) =>
            {
                if (phase == 0)
                {
                    _symbolsSeen++;
                    bool busy = detector == PskDetector.Coherent
                        ? demodulator!.CarrierDetect
                        : demodulator!.ChannelBusy;
                    if (previousBusy && !busy)
                    {
                        foreach (Il2pReceiver deframer in _deframers)
                        {
                            deframer.Reset();
                        }
                    }

                    previousBusy = busy;
                }

                _deframers[phase].PushBit(first, confidence);
                _deframers[phase].PushBit(second, confidence);
            });
        _demodulator = demodulator;
        _modulator = new QpskModulator(sampleRate, baud, carrier, rollOff);
        _demodulator.SymbolPlotted = (i, q) => SymbolPlotted?.Invoke(new ConstellationPoint(i, q));
    }

    /// <inheritdoc />
    public event Action<ConstellationPoint>? SymbolPlotted;

    /// <summary>Creates the 600 bps mode (300 baud, 1500 Hz centre) - NinoTNC mode 9,
    /// an SSB-friendly 500 Hz-OBW mode sharing its symbol rate with 300 BPSK.</summary>
    /// <remarks>Roll-off 0.20 rather than the default: it puts us at 322 Hz, just inside
    /// the 328 Hz a NinoTNC's own mode-9 transmission measures on the bench. The rule is
    /// that we are never wider than the TNC we share a channel with.
    /// <paramref name="carrierFrequency"/> (1500 Hz convention) moves the modem within the
    /// audio passband, QtSoundModem-style.</remarks>
    public static QpskModem Qpsk600(
        int sampleRate, Action<byte[]> frameReceived, bool crc = true, double rollOff = 0.20,
        PskDetector detector = PskDetector.Coherent, double carrierFrequency = 1500,
        bool acceptPlainIl2p = false) =>
        new(sampleRate, 300, carrierFrequency, frameReceived, crc, rollOff, detector,
            acceptPlainIl2p: acceptPlainIl2p);

    /// <summary>Creates the 2400 bps mode (1200 baud, 1500 Hz centre).</summary>
    /// <remarks>
    /// Keeps the default 0.35, deliberately NOT copying the NinoTNC here: its own mode-11
    /// signal measures 1852 Hz where ours is 1400 Hz, so matching it would mean widening
    /// for no gain. Bench evidence agrees - sweeping our roll-off up toward its width made
    /// its decode of us worse, not better (0.35 → 4/6, 0.6 → 0/6, 0.9 → 0/6 at a short
    /// preamble). <paramref name="carrierFrequency"/> (1500 Hz convention) moves the modem
    /// within the audio passband, QtSoundModem-style.
    /// </remarks>
    public static QpskModem Qpsk2400(
        int sampleRate, Action<byte[]> frameReceived, bool crc = true,
        double rollOff = QpskModulator.DefaultRollOff,
        PskDetector detector = PskDetector.Coherent, double carrierFrequency = 1500,
        bool acceptPlainIl2p = false) =>
        new(sampleRate, 1200, carrierFrequency, frameReceived, crc, rollOff, detector,
            acceptPlainIl2p: acceptPlainIl2p);

    /// <summary>Creates the 3600 bps mode (1800 baud; the conventional 1650 Hz centre).</summary>
    /// <remarks>
    /// <para>
    /// Roll-off 0.25 - tighter than the other modes, matching the NinoTNC's own mode-5
    /// signal, which sits near the Nyquist floor for 1800 sym/s (how 3600 bps fits a
    /// voice channel). Measured like-for-like (whole burst, same frame - issue #2 fixed
    /// an earlier window mismatch that mis-read this mode as "9 % wider"), we transmit
    /// 1808 Hz against the TNC's 1887 Hz: narrower, and CI-enforced against the
    /// checked-in reference recording.
    /// </para>
    /// <para>
    /// 0.25 remains a receiver limit rather than a free choice: ~0.10 shaping is
    /// decodable by the NinoTNC (bench: 10/10) but not by our own demodulator at 6⅔
    /// samples per symbol - bench-swept, 0.10 fails a clean loopback, 0.15/0.20 fail
    /// under noise. A matched receive filter or a higher DSP rate for this mode would
    /// buy margin, but with our TX already narrower than the reference hardware there is
    /// no compliance need.
    /// </para>
    /// <para>
    /// <paramref name="carrierFrequency"/> (the 1650 Hz mode-5 convention) moves the modem
    /// within the audio passband, QtSoundModem-style.
    /// </para>
    /// </remarks>
    public static QpskModem Qpsk3600(
        int sampleRate, Action<byte[]> frameReceived, bool crc = true, double rollOff = 0.25,
        PskDetector detector = PskDetector.Coherent, double? loopBandwidthHz = null,
        double carrierFrequency = 1650, bool acceptPlainIl2p = false) =>
        // The Costas loop is narrower here than the 6 % default: at 6⅔ samples/symbol and
        // the 0.25 roll-off, the wider loop tracks noise instead of carrier and loses even
        // at low SNR (bench: 0.06×baud scored 25/40 at σ0.08 where 0.03×baud scored 40/40).
        // 54 Hz keeps the coherent noise win and still pulls in a ~5 Hz offset.
        // decisionFeedback off: the 2026-08-07 QPSK campaign's reference detector measured
        // a clean-signal regression on this mode's FM chain at 6⅔ samples per symbol
        // (22/30 vs the plain product's 30/30). Re-measured for #326 with the reference
        // deciding on the interpolated instant: the clean-signal loss is gone (98-100 %
        // either way at +10-12 dB CNR) but at the FM knee the product still wins, fm-mic
        // +8 dB 78 % against the reference's 68 %, fm-data 80 % against 72 % (N=50), so
        // the mode keeps the detector it was validated with (see QpskDemodulator's
        // parameter). The #326 timing fixes reach it regardless: fm-mic +8 dB 62 -> 78 %.
        new(sampleRate, 1800, carrierFrequency, frameReceived, crc, rollOff, detector,
            loopBandwidthHz ?? 1800 * 0.03, acceptPlainIl2p, decisionFeedback: false);

    /// <inheritdoc />
    public event Action<byte[], FrameQuality>? FrameDecoded;

    /// <inheritdoc />
    public string Mode => $"qpsk{_bitRate}{(_crc ? "-il2pc" : "-il2p")}";

    /// <inheritdoc />
    public bool CarrierDetect => _demodulator.CarrierDetect;

    /// <inheritdoc />
    public bool ChannelBusy => _demodulator.ChannelBusy;

    /// <summary>How far the current signal sits from this modem's carrier centre, in Hz
    /// (positive = above it), or null when nothing coherent enough to measure is present -
    /// <see cref="QpskDemodulator.CarrierOffsetHz"/> read live. Decoded frames already carry
    /// the reading in <see cref="FrameQuality.FrequencyOffsetHz"/>; this property is for the
    /// bursts that never produce one, polled while <see cref="CarrierDetect"/> holds, and it
    /// is what a <see cref="QpskMultiModem"/> branch is ranked on.</summary>
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

    /// <summary>Cumulative recovered-but-trailing-CRC-refused count summed over the timing
    /// phases' own IL2P+CRC readings - see <see cref="Il2pReceiver.CrcFailures"/>.</summary>
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

    /// <summary>Bench seam: this modem's demodulator. Not part of the deployment surface.</summary>
    internal QpskDemodulator Demodulator => _demodulator;

    /// <inheritdoc />
    public void Process(ReadOnlySpan<float> samples) => _demodulator.Process(samples);

    /// <inheritdoc />
    public float[] Modulate(ReadOnlySpan<byte> ax25Frame, int txDelayMilliseconds)
    {
        byte[] wire = Il2pCodec.Encode(ax25Frame, appendCrc: _crc);
        int preambleBits = Math.Max(32, txDelayMilliseconds * _bitRate / 1000);
        if (preambleBits % 2 != 0)
        {
            preambleBits++;
        }

        byte[] bits = Il2pFramer.FrameBits(wire, preambleBits, Il2pFramer.PreambleStyle.Zeros);
        return _modulator.Modulate(bits);
    }

    /// <inheritdoc />
    public void ResetCarrierState() => _demodulator.ResetCarrierState();
}
