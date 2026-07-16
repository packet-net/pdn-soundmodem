using Packet.SoundModem.Il2p;

namespace Packet.SoundModem.Modems;

/// <summary>QPSK carrying IL2P — the NinoTNC 600 (300 baud, 1500 Hz), 2400 (1200 baud,
/// 1500 Hz) and 3600 (1800 baud, 1650 Hz) mode family — as an <see cref="IModem"/>.
/// Symbol rates and carriers are Nino's, per the v3/4.43 mode-switch mapping in
/// flashtnc's release-notes.txt.</summary>
public sealed class QpskModem : IModem, IConstellationSource
{
    private readonly QpskDemodulator _demodulator;
    private readonly QpskModulator _modulator;
    private readonly int _bitRate;
    private readonly bool _crc;

    private QpskModem(
        int sampleRate, int baud, double carrier, Action<byte[]> frameReceived, bool crc,
        double rollOff = QpskModulator.DefaultRollOff,
        PskDetector detector = PskDetector.Coherent, double? loopBandwidthHz = null)
    {
        _bitRate = baud * 2;
        _crc = crc;
        var deframer = new Il2pDeframer(
            (frame, info) =>
            {
                frameReceived(frame);
                FrameDecoded?.Invoke(frame, new FrameQuality(
                    Mode, frame.Length, info.CorrectedSymbols, info.CrcValid));
            },
            crc);
        _demodulator = new QpskDemodulator(
            sampleRate, baud,
            (first, second) =>
            {
                deframer.PushBit(first);
                deframer.PushBit(second);
            },
            carrier, detector, loopBandwidthHz);
        _modulator = new QpskModulator(sampleRate, baud, carrier, rollOff);
        _demodulator.SymbolPlotted = (i, q) => SymbolPlotted?.Invoke(new ConstellationPoint(i, q));
    }

    /// <inheritdoc />
    public event Action<ConstellationPoint>? SymbolPlotted;

    /// <summary>Creates the 600 bps mode (300 baud, 1500 Hz centre) — NinoTNC mode 9,
    /// an SSB-friendly 500 Hz-OBW mode sharing its symbol rate with 300 BPSK.</summary>
    /// <remarks>Roll-off 0.20 rather than the default: it puts us at 322 Hz, just inside
    /// the 328 Hz a NinoTNC's own mode-9 transmission measures on the bench. The rule is
    /// that we are never wider than the TNC we share a channel with.</remarks>
    public static QpskModem Qpsk600(
        int sampleRate, Action<byte[]> frameReceived, bool crc = true, double rollOff = 0.20,
        PskDetector detector = PskDetector.Coherent) =>
        new(sampleRate, 300, 1500, frameReceived, crc, rollOff, detector);

    /// <summary>Creates the 2400 bps mode (1200 baud, 1500 Hz centre).</summary>
    /// <remarks>
    /// Keeps the default 0.35, deliberately NOT copying the NinoTNC here: its own mode-11
    /// signal measures 1852 Hz where ours is 1400 Hz, so matching it would mean widening
    /// for no gain. Bench evidence agrees — sweeping our roll-off up toward its width made
    /// its decode of us worse, not better (0.35 → 4/6, 0.6 → 0/6, 0.9 → 0/6 at a short
    /// preamble).
    /// </remarks>
    public static QpskModem Qpsk2400(
        int sampleRate, Action<byte[]> frameReceived, bool crc = true,
        double rollOff = QpskModulator.DefaultRollOff,
        PskDetector detector = PskDetector.Coherent) =>
        new(sampleRate, 1200, 1500, frameReceived, crc, rollOff, detector);

    /// <summary>Creates the 3600 bps mode (1800 baud; the conventional 1650 Hz centre).</summary>
    /// <remarks>
    /// <para>
    /// Roll-off 0.25 — tighter than the other modes, matching the NinoTNC's own mode-5
    /// signal, which sits near the Nyquist floor for 1800 sym/s (how 3600 bps fits a
    /// voice channel). Measured like-for-like (whole burst, same frame — issue #2 fixed
    /// an earlier window mismatch that mis-read this mode as "9 % wider"), we transmit
    /// 1808 Hz against the TNC's 1887 Hz: narrower, and CI-enforced against the
    /// checked-in reference recording.
    /// </para>
    /// <para>
    /// 0.25 remains a receiver limit rather than a free choice: ~0.10 shaping is
    /// decodable by the NinoTNC (bench: 10/10) but not by our own demodulator at 6⅔
    /// samples per symbol — bench-swept, 0.10 fails a clean loopback, 0.15/0.20 fail
    /// under noise. A matched receive filter or a higher DSP rate for this mode would
    /// buy margin, but with our TX already narrower than the reference hardware there is
    /// no compliance need.
    /// </para>
    /// </remarks>
    public static QpskModem Qpsk3600(
        int sampleRate, Action<byte[]> frameReceived, bool crc = true, double rollOff = 0.25,
        PskDetector detector = PskDetector.Coherent, double? loopBandwidthHz = null) =>
        // The Costas loop is narrower here than the 6 % default: at 6⅔ samples/symbol and
        // the 0.25 roll-off, the wider loop tracks noise instead of carrier and loses even
        // at low SNR (bench: 0.06×baud scored 25/40 at σ0.08 where 0.03×baud scored 40/40).
        // 54 Hz keeps the coherent noise win and still pulls in a ~5 Hz offset.
        new(sampleRate, 1800, 1650, frameReceived, crc, rollOff, detector, loopBandwidthHz ?? 1800 * 0.03);

    /// <inheritdoc />
    public event Action<byte[], FrameQuality>? FrameDecoded;

    /// <inheritdoc />
    public string Mode => $"qpsk{_bitRate}{(_crc ? "-il2pc" : "-il2p")}";

    /// <inheritdoc />
    public bool CarrierDetect => _demodulator.CarrierDetect;

    /// <inheritdoc />
    public bool ChannelBusy => _demodulator.ChannelBusy;

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
