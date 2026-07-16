using Packet.SoundModem.Fx25;
using Packet.SoundModem.Hdlc;

namespace Packet.SoundModem.Modems;

/// <summary>FX.25 participation for <see cref="Afsk1200Modem"/>.</summary>
public enum Fx25Mode
{
    /// <summary>Plain AX.25 only.</summary>
    None,

    /// <summary>Decode FX.25 blocks alongside plain AX.25 (always safe: FX.25 is
    /// transparent to non-participating stations).</summary>
    Receive,

    /// <summary>Also wrap transmissions in FX.25 (16 check bytes by default).</summary>
    TransmitReceive,
}

/// <summary>Classic 1200 baud AFSK AX.25 (Bell 202 + NRZI + HDLC) as an
/// <see cref="IModem"/>, with optional FX.25 forward error correction.</summary>
public sealed class Afsk1200Modem : IModem
{
    private readonly AfskDemodulator _demodulator;
    private readonly AfskModulator _modulator;
    private readonly Fx25Mode _fx25;
    private readonly int _fx25CheckBytes;
    private long _samplesProcessed;

    /// <summary>Creates the modem.</summary>
    /// <param name="sampleRate">Channel DSP rate.</param>
    /// <param name="frameReceived">Receives each decoded AX.25 frame (deduplicated when
    /// FX.25 reception is on, since a clean FX.25 block also decodes as plain HDLC).</param>
    /// <param name="centerFrequency">Mark/space midpoint; 1700 Hz standard.</param>
    /// <param name="fx25">FX.25 participation.</param>
    /// <param name="fx25CheckBytes">FX.25 FEC strength for transmit (16/32/64).</param>
    public Afsk1200Modem(
        int sampleRate,
        Action<byte[]> frameReceived,
        double centerFrequency = 1700,
        Fx25Mode fx25 = Fx25Mode.None,
        int fx25CheckBytes = 16)
    {
        ArgumentNullException.ThrowIfNull(frameReceived);
        _fx25 = fx25;
        _fx25CheckBytes = fx25CheckBytes;

        Action<byte[]> deliver = frameReceived;
        if (fx25 != Fx25Mode.None)
        {
            var deduper = new FrameDeduper(3L * sampleRate);
            deliver = frame =>
            {
                if (deduper.ShouldEmit(frame, _samplesProcessed))
                {
                    frameReceived(frame);
                }
            };
        }

        var deframer = new HdlcDeframer(deliver);
        var fx25Deframer = fx25 != Fx25Mode.None
            ? new Fx25Deframer((frame, _) => deliver(frame))
            : null;
        var nrzi = new NrziDecoder();
        _demodulator = new AfskDemodulator(
            sampleRate,
            level =>
            {
                int bit = nrzi.Decode(level);
                deframer.PushBit(bit);
                fx25Deframer?.PushBit(bit);
            },
            centerFrequency);
        _modulator = new AfskModulator(sampleRate);
    }

    /// <inheritdoc />
    public string Mode => _fx25 switch
    {
        Fx25Mode.None => "afsk1200",
        Fx25Mode.Receive => "afsk1200-fx25rx",
        _ => "afsk1200-fx25",
    };

    /// <inheritdoc />
    public bool CarrierDetect => _demodulator.CarrierDetect;

    /// <inheritdoc />
    public bool ChannelBusy => _demodulator.ChannelBusy;

    /// <inheritdoc />
    public void Process(ReadOnlySpan<float> samples)
    {
        _demodulator.Process(samples);
        _samplesProcessed += samples.Length;
    }

    /// <inheritdoc />
    public float[] Modulate(ReadOnlySpan<byte> ax25Frame, int txDelayMilliseconds)
    {
        if (_fx25 != Fx25Mode.TransmitReceive)
        {
            return _modulator.Modulate(TrainingPreamble.Prepend(
                HdlcFramer.FrameBits(ax25Frame, openingFlags: 2, closingFlags: 2),
                txDelayMilliseconds, 1200));
        }

        int openingFlags = Math.Max(2, (int)(txDelayMilliseconds * 1200L / (8 * 1000)));

        // FX.25: flag-pattern preamble (TXDELAY), then the tagged, RS-protected block.
        byte[] block = Fx25Codec.EncodeBits(ax25Frame, _fx25CheckBytes);
        var bits = new byte[openingFlags * 8 + block.Length];
        for (int i = 0; i < openingFlags * 8; i++)
        {
            bits[i] = (byte)((0x7E >> (i & 7)) & 1);
        }

        block.CopyTo(bits, openingFlags * 8);
        return _modulator.Modulate(bits);
    }

    /// <inheritdoc />
    public void ResetCarrierState() => _demodulator.ResetCarrierState();
}
