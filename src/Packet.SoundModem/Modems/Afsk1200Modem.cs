using Packet.SoundModem.Hdlc;

namespace Packet.SoundModem.Modems;

/// <summary>Classic 1200 baud AFSK AX.25 (Bell 202 + NRZI + HDLC) as an <see cref="IModem"/>.</summary>
public sealed class Afsk1200Modem : IModem
{
    private readonly Afsk1200Demodulator _demodulator;
    private readonly Afsk1200Modulator _modulator;

    /// <summary>Creates the modem.</summary>
    /// <param name="sampleRate">Channel DSP rate.</param>
    /// <param name="frameReceived">Receives each decoded AX.25 frame.</param>
    /// <param name="centerFrequency">Mark/space midpoint; 1700 Hz standard.</param>
    public Afsk1200Modem(int sampleRate, Action<byte[]> frameReceived, double centerFrequency = 1700)
    {
        ArgumentNullException.ThrowIfNull(frameReceived);
        var deframer = new HdlcDeframer(frameReceived);
        var nrzi = new NrziDecoder();
        _demodulator = new Afsk1200Demodulator(
            sampleRate, level => deframer.PushBit(nrzi.Decode(level)), centerFrequency);
        _modulator = new Afsk1200Modulator(sampleRate);
    }

    /// <inheritdoc />
    public string Mode => "afsk1200";

    /// <inheritdoc />
    public bool CarrierDetect => _demodulator.CarrierDetect;

    /// <inheritdoc />
    public bool ChannelBusy => _demodulator.ChannelBusy;

    /// <inheritdoc />
    public void Process(ReadOnlySpan<float> samples) => _demodulator.Process(samples);

    /// <inheritdoc />
    public float[] Modulate(ReadOnlySpan<byte> ax25Frame, int txDelayMilliseconds)
    {
        // TXDELAY as opening flags: one flag = 8 bits ≈ 6.67 ms at 1200 baud.
        int openingFlags = Math.Max(2, (int)(txDelayMilliseconds * 1200L / (8 * 1000)));
        return _modulator.Modulate(HdlcFramer.FrameBits(ax25Frame, openingFlags, closingFlags: 2));
    }

    /// <inheritdoc />
    public void ResetCarrierState() => _demodulator.ResetCarrierState();
}
