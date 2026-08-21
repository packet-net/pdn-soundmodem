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
    /// <summary>Bell 202 deviation of each tone from the centre (mark = centre − 500,
    /// space = centre + 500); the demodulator's default shift.</summary>
    private const double Bell202ToneShift = 500;

    /// <summary>Dedupe window across the timing phases' deframers, in bits: shorter than the
    /// shortest AX.25 frame (a minimal one is 136 bits), long enough to merge the copies the
    /// phases produce, which arrive within a bit of each other. Separate from, and inside, the
    /// seconds-wide FX.25 window below, which exists to merge a frame that decodes twice by two
    /// different routes rather than at two timing phases.</summary>
    private const int DedupeWindowBits = 64;

    private readonly AfskDemodulator _demodulator;
    private readonly AfskModulator _modulator;
    private readonly Fx25Mode _fx25;
    private readonly int _fx25CheckBytes;
    private readonly int _dedupeChunk;
    private long _samplesProcessed;
    private long _bitsSeen;

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
        _dedupeChunk = Math.Max(1, sampleRate / 10);

        // Quality rides with whichever decode the deduper lets through: a clean FX.25
        // block also decodes as plain HDLC, and the consumer should see one frame with
        // the diagnostics of the path that delivered it.
        Action<byte[], FrameQuality> deliver = (frame, quality) =>
        {
            frameReceived(frame);
            FrameDecoded?.Invoke(frame, quality);
        };
        if (fx25 != Fx25Mode.None)
        {
            var deduper = new FrameDeduper(3L * sampleRate);
            Action<byte[], FrameQuality> inner = deliver;
            deliver = (frame, quality) =>
            {
                if (deduper.ShouldEmit(frame, _samplesProcessed))
                {
                    inner(frame, quality);
                }
            };
        }

        // The timing phases decide the same bits at slightly different instants and each runs
        // its own deframer, so whichever phase's copy passes the FCS (or the FX.25 Reed-Solomon)
        // is the one that gets delivered - no FEC on this mode, so "any phase whose FCS checks"
        // is the whole benefit, which is how Dire Wolf's multi-slicer decoders earn theirs.
        var phaseDeduper = new FrameDeduper(DedupeWindowBits);
        Action<byte[], FrameQuality> phased = deliver;
        deliver = (frame, quality) =>
        {
            if (phaseDeduper.ShouldEmit(frame, _bitsSeen))
            {
                phased(frame, quality);
            }
        };

        int phases = AfskDemodulator.TimingPhaseCount;
        var deframers = new HdlcDeframer[phases];
        var fx25Deframers = new Fx25Deframer?[phases];
        var nrzi = new NrziDecoder[phases];
        for (int phase = 0; phase < phases; phase++)
        {
            nrzi[phase] = new NrziDecoder();
            deframers[phase] = new HdlcDeframer(frame =>
                deliver(frame, new FrameQuality(Mode, frame.Length, null, null)));
            fx25Deframers[phase] = fx25 != Fx25Mode.None
                ? new Fx25Deframer((frame, correctedBytes) =>
                    deliver(frame, new FrameQuality(Mode, frame.Length, correctedBytes, null)))
                : null;
        }

        _demodulator = new AfskDemodulator(
            sampleRate,
            static _ => { },
            centerFrequency,
            phaseBitSink: (level, phase) =>
            {
                if (phase == 0)
                {
                    _bitsSeen++;
                }

                int bit = nrzi[phase].Decode(level);
                deframers[phase].PushBit(bit);
                fx25Deframers[phase]?.PushBit(bit);
            });
        _modulator = new AfskModulator(
            sampleRate, 1200, centerFrequency - Bell202ToneShift, centerFrequency + Bell202ToneShift);
    }

    /// <inheritdoc />
    public event Action<byte[], FrameQuality>? FrameDecoded;

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
        // Bounded chunks so the FX.25 deduper's clock advances with the audio even when a
        // caller hands over one huge buffer - the clock used to hold still for the whole
        // buffer, so a genuine repeat seconds later in the same call read as a duplicate
        // and was suppressed (mirrors the multi banks' chunking).
        for (int position = 0; position < samples.Length; position += _dedupeChunk)
        {
            var slice = samples.Slice(position, Math.Min(_dedupeChunk, samples.Length - position));
            _demodulator.Process(slice);
            _samplesProcessed += slice.Length;
        }
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
