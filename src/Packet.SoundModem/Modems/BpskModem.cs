using Packet.SoundModem.Il2p;

namespace Packet.SoundModem.Modems;

/// <summary>BPSK carrying IL2P — the NinoTNC 300 (mode 8, 300 baud) and 1200 (mode 10,
/// 1200 baud) mode family, both phase-modulating a 1500 Hz tone — as an
/// <see cref="IModem"/>. Symbol rates and carrier are Nino's, per the v3/4.43
/// mode-switch mapping in flashtnc's release-notes.txt.</summary>
public sealed class BpskModem : IModem, IConstellationSource
{
    private readonly BpskDemodulator _demodulator;
    private readonly BpskModulator _modulator;
    private readonly int _baud;
    private readonly bool _crc;

    /// <summary>Creates the modem.</summary>
    /// <param name="sampleRate">Channel DSP rate (multiple of <paramref name="baud"/>).</param>
    /// <param name="frameReceived">Receives each decoded AX.25 frame.</param>
    /// <param name="crc">IL2P+CRC mode (both stations must agree). On for NinoTNC
    /// networks.</param>
    /// <param name="carrierFrequency">Carrier centre; 1500 Hz convention.</param>
    /// <param name="baud">Symbol rate — also the bit rate, BPSK carrying one bit per
    /// symbol.</param>
    /// <param name="rollOff">RRC roll-off.</param>
    public BpskModem(
        int sampleRate, Action<byte[]> frameReceived, bool crc = true,
        double carrierFrequency = 1500, int baud = 300,
        double rollOff = BpskModulator.DefaultRollOff)
    {
        ArgumentNullException.ThrowIfNull(frameReceived);
        _crc = crc;
        _baud = baud;
        var deframer = new Il2pDeframer(
            (frame, info) =>
            {
                frameReceived(frame);
                FrameDecoded?.Invoke(frame, new FrameQuality(
                    Mode, frame.Length, info.CorrectedSymbols, info.CrcValid));
            },
            crc);
        _demodulator = new BpskDemodulator(sampleRate, deframer.PushBit, carrierFrequency, baud);
        _demodulator.SymbolPlotted = (i, q) => SymbolPlotted?.Invoke(new ConstellationPoint(i, q));
        _modulator = new BpskModulator(sampleRate, baud, carrierFrequency, rollOff);
    }

    /// <inheritdoc />
    public event Action<ConstellationPoint>? SymbolPlotted;

    /// <summary>Creates the 300 bps mode (300 baud, 1500 Hz centre) — NinoTNC mode 8.</summary>
    /// <remarks>Roll-off 0.20, matching the 328 Hz a NinoTNC's own mode-8 transmission
    /// measures; the 0.35 default put us at 352 Hz, wider than the TNC we share the
    /// channel with.</remarks>
    public static BpskModem Bpsk300(int sampleRate, Action<byte[]> frameReceived, bool crc = true) =>
        new(sampleRate, frameReceived, crc, 1500, 300, 0.20);

    /// <summary>Creates the 1200 bps mode (1200 baud, 1500 Hz centre) — NinoTNC mode 10,
    /// sharing its 1200 sym/s and 2400 Hz OBW with 2400 QPSK.</summary>
    public static BpskModem Bpsk1200(int sampleRate, Action<byte[]> frameReceived, bool crc = true) =>
        new(sampleRate, frameReceived, crc, 1500, 1200);

    /// <inheritdoc />
    public event Action<byte[], FrameQuality>? FrameDecoded;

    /// <inheritdoc />
    public string Mode => $"bpsk{_baud}{(_crc ? "-il2pc" : "-il2p")}";

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
        int preambleBits = Math.Max(24, txDelayMilliseconds * _baud / 1000);
        byte[] bits = Il2pFramer.FrameBits(wire, preambleBits, Il2pFramer.PreambleStyle.Zeros);
        return _modulator.Modulate(bits);
    }

    /// <inheritdoc />
    public void ResetCarrierState() => _demodulator.ResetCarrierState();
}
