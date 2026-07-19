using M0LTE.Il2p;

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
    /// <param name="detector">Differential (default) or coherent detection.</param>
    public BpskModem(
        int sampleRate, Action<byte[]> frameReceived, bool crc = true,
        double carrierFrequency = 1500, int baud = 300,
        double rollOff = BpskModulator.DefaultRollOff,
        PskDetector detector = PskDetector.Differential)
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

        // Reset the deframer on the DCD falling edge. Without this, a frame whose carrier
        // drops mid-collection (the preceding transmission ends, a collision corrupts the
        // tail, or the signal fades) leaves the deframer blindly consuming the expected
        // payload bytes from whatever follows — including the next transmission's preamble
        // and sync word. The deframer then fails RS on the phantom frame and returns to
        // hunting, but the real frame's sync word has already been swallowed. This is the
        // continuous-decode robustness gap: 37 of 74 missed frames in the GB7RDG 24 h
        // benchmark decoded perfectly from isolated audio but were lost in-stream because
        // a preceding signal's collection state masked the new preamble. DCD drops within
        // 24 symbol times (80 ms at 300 Bd) of the carrier stopping — well before the next
        // transmission's sync word arrives — so the reset is always in time.
        bool previousDcd = false;
        BpskDemodulator? demodulator = null;
        demodulator = new BpskDemodulator(sampleRate, bit =>
        {
            bool dcd = demodulator!.CarrierDetect;
            if (previousDcd && !dcd)
            {
                deframer.Reset();
            }

            previousDcd = dcd;
            deframer.PushBit(bit);
        }, carrierFrequency, baud, detector);
        _demodulator = demodulator;
        _demodulator.SymbolPlotted = (i, q) => SymbolPlotted?.Invoke(new ConstellationPoint(i, q));
        _modulator = new BpskModulator(sampleRate, baud, carrierFrequency, rollOff);
    }

    /// <inheritdoc />
    public event Action<ConstellationPoint>? SymbolPlotted;

    /// <summary>Creates the 300 bps mode (300 baud, 1500 Hz centre) — NinoTNC mode 8.</summary>
    /// <remarks>Roll-off 0.20, matching the 328 Hz a NinoTNC's own mode-8 transmission
    /// measures; the 0.35 default put us at 352 Hz, wider than the TNC we share the
    /// channel with. <paramref name="carrierFrequency"/> (1500 Hz convention) moves the
    /// modem within the audio passband, QtSoundModem-style.</remarks>
    public static BpskModem Bpsk300(
        int sampleRate, Action<byte[]> frameReceived, bool crc = true,
        PskDetector detector = PskDetector.Differential, double carrierFrequency = 1500) =>
        new(sampleRate, frameReceived, crc, carrierFrequency, 300, 0.20, detector);

    /// <summary>Creates the 1200 bps mode (1200 baud, 1500 Hz centre) — NinoTNC mode 10,
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
