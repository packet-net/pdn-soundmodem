using Packet.SoundModem.Il2p;

namespace Packet.SoundModem.Modems;

/// <summary>QPSK carrying IL2P — the NinoTNC 600 (300 baud, 1500 Hz), 2400 (1200 baud,
/// 1500 Hz) and 3600 (1800 baud, 1650 Hz) mode family — as an <see cref="IModem"/>.
/// Symbol rates and carriers are Nino's, per the v3/4.43 mode-switch mapping in
/// flashtnc's release-notes.txt.</summary>
public sealed class QpskModem : IModem
{
    private readonly QpskDemodulator _demodulator;
    private readonly QpskModulator _modulator;
    private readonly int _bitRate;
    private readonly bool _crc;

    private QpskModem(
        int sampleRate, int baud, double carrier, Action<byte[]> frameReceived, bool crc,
        double rollOff = QpskModulator.DefaultRollOff)
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
            carrier);
        _modulator = new QpskModulator(sampleRate, baud, carrier, rollOff);
    }

    /// <summary>Creates the 600 bps mode (300 baud, 1500 Hz centre) — NinoTNC mode 9,
    /// an SSB-friendly 500 Hz-OBW mode sharing its symbol rate with 300 BPSK.</summary>
    /// <remarks>Roll-off 0.20 rather than the default: it puts us at 322 Hz, just inside
    /// the 328 Hz a NinoTNC's own mode-9 transmission measures on the bench. The rule is
    /// that we are never wider than the TNC we share a channel with.</remarks>
    public static QpskModem Qpsk600(
        int sampleRate, Action<byte[]> frameReceived, bool crc = true, double rollOff = 0.20) =>
        new(sampleRate, 300, 1500, frameReceived, crc, rollOff);

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
        double rollOff = QpskModulator.DefaultRollOff) =>
        new(sampleRate, 1200, 1500, frameReceived, crc, rollOff);

    /// <summary>Creates the 3600 bps mode (1800 baud; the conventional 1650 Hz centre).</summary>
    /// <remarks>
    /// <para>
    /// Roll-off 0.25 — tighter than the other modes, chasing the NinoTNC's own mode-5
    /// signal, which measures 1828 Hz for 1800 sym/s: within a whisker of the Nyquist
    /// floor a symbol rate can occupy at all, and how the mode fits 3600 bps through a
    /// voice channel. We reach 1995 Hz, still ~9 % wider than the TNC.
    /// </para>
    /// <para>
    /// 0.25 is a receiver limit, not a choice. Copying his width needs ~0.10, which our
    /// own demodulator cannot decode at this rate — bench-swept, 0.10 fails even a clean
    /// loopback and 0.15/0.20 fail under noise or on multi-block frames, because 1800 Bd
    /// at 12 kHz leaves only 6⅔ samples per symbol and a near-Nyquist pulse needs symbol
    /// timing finer than that. The fixes are a matched RRC receive filter, or running this
    /// mode at a higher DSP rate; until one lands, 0.25 is the tightest shaping we can
    /// both transmit and hear. Tracked in issue #1.
    /// </para>
    /// </remarks>
    public static QpskModem Qpsk3600(
        int sampleRate, Action<byte[]> frameReceived, bool crc = true, double rollOff = 0.25) =>
        new(sampleRate, 1800, 1650, frameReceived, crc, rollOff);

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
