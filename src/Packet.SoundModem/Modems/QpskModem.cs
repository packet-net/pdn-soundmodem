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
        var deframer = new Il2pDeframer((frame, _) => frameReceived(frame), crc);
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
    public static QpskModem Qpsk600(
        int sampleRate, Action<byte[]> frameReceived, bool crc = true,
        double rollOff = QpskModulator.DefaultRollOff) =>
        new(sampleRate, 300, 1500, frameReceived, crc, rollOff);

    /// <summary>Creates the 2400 bps mode (1200 baud, 1500 Hz centre).</summary>
    public static QpskModem Qpsk2400(
        int sampleRate, Action<byte[]> frameReceived, bool crc = true,
        double rollOff = QpskModulator.DefaultRollOff) =>
        new(sampleRate, 1200, 1500, frameReceived, crc, rollOff);

    /// <summary>Creates the 3600 bps mode (1800 baud; the conventional 1650 Hz centre).</summary>
    public static QpskModem Qpsk3600(
        int sampleRate, Action<byte[]> frameReceived, bool crc = true,
        double rollOff = QpskModulator.DefaultRollOff) =>
        new(sampleRate, 1800, 1650, frameReceived, crc, rollOff);

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
