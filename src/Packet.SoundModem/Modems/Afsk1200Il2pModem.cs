using M0LTE.Il2p;

namespace Packet.SoundModem.Modems;

/// <summary>
/// 1200 baud AFSK carrying IL2P (NinoTNC mode 7 "1200 AFSK IL2P+CRC") as an
/// <see cref="IModem"/>. Same Bell-202 tones and demodulator as
/// <see cref="Afsk1200Modem"/>, but the bit layer is IL2P: demodulated levels feed the
/// deframer raw - no NRZI - matching Dire Wolf, whose IL2P receiver taps the bit before
/// NRZI decoding (hdlc_rec_bit), and the proven 9600 IL2P framing choice here.
/// Transparency comes from IL2P's packet-synchronous scrambler, not bit stuffing.
/// </summary>
public sealed class Afsk1200Il2pModem : IModem, IFrameSpanSource
{
    private const int Baud = 1200;

    /// <summary>Bell 202 deviation of each tone from the centre (mark = centre − 500,
    /// space = centre + 500); the demodulator's default shift.</summary>
    private const double Bell202ToneShift = 500;

    /// <summary>Dedupe window across the timing phases' deframers, in bits: shorter than the
    /// shortest IL2P frame (a 15-byte header alone is 120 bits), longer than the trailer a held
    /// plain reading waits for (32 bits). See <see cref="Afsk300Modem"/>.</summary>
    private const int DedupeWindowBits = 64;

    private readonly AfskDemodulator _demodulator;
    private readonly AfskModulator _modulator;
    private readonly bool _crc;
    /// <summary>
    /// Where the frame just delivered was in the receive audio, for the channel's per-frame
    /// level: marked at the sync its deframer locked on and at the sample its last bit was taken
    /// on. See <see cref="FrameSpan"/>.
    /// </summary>
    private readonly FrameSpan _span = new(AfskDemodulator.TimingPhaseCount);

    /// <summary>What a reading leaves off the end of one of this modem's spans; see
    /// <see cref="FrameSpan.MarginSamplesFor"/>.</summary>
    private readonly int _spanMargin;

    private readonly FrameDeduper _deduper = new(DedupeWindowBits);
    private long _bitsSeen;

    /// <summary>Creates the modem.</summary>
    /// <param name="sampleRate">Channel DSP rate.</param>
    /// <param name="frameReceived">Receives each decoded AX.25 frame.</param>
    /// <param name="crc">Expect/emit the Hamming-protected trailing CRC ("IL2P+CRC" -
    /// what the NinoTNC modes use).</param>
    /// <param name="centerFrequency">Mark/space midpoint; 1700 Hz standard.</param>
    /// <param name="acceptPlainIl2p">Pass frames that arrive as plain IL2P, with no trailing CRC,
    /// to <paramref name="frameReceived"/> as well as reporting them (off by default, and inert
    /// unless <paramref name="crc"/> is on). They are read either way - see
    /// <see cref="Il2pReceiver"/> for what that buys and what it costs.</param>
    public Afsk1200Il2pModem(
        int sampleRate, Action<byte[]> frameReceived, bool crc = true,
        double centerFrequency = 1700, bool acceptPlainIl2p = false)
    {
        ArgumentNullException.ThrowIfNull(frameReceived);
        _spanMargin = FrameSpan.MarginSamplesFor(sampleRate, 1200);
        _crc = crc;
        // Declared ahead of the deframers because they mark the frame's span against its
        // count of input samples; assigned below, and only ever read from inside a decode.
        AfskDemodulator? demodulator = null;

        // One deframer per timing phase (see AfskDemodulator.TimingPhaseCount), behind a
        // bit-clocked content dedupe - the Afsk300Modem/QpskModem arrangement.
        var deframers = new Il2pReceiver[AfskDemodulator.TimingPhaseCount];
        for (int phase = 0; phase < deframers.Length; phase++)
        {
            int reading = phase;
            deframers[phase] = new Il2pReceiver(
                (frame, info, delivery) =>
                {
                    if (!_deduper.ShouldEmit(frame, _bitsSeen, !delivery.MonitorOnly))
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
                        PlainIl2p: delivery.PlainIl2p,
                        TrailerNearBits: delivery.TrailerNearBits,
                        MonitorOnly: delivery.MonitorOnly));
                },
                crcMode: crc, acceptPlainIl2p: acceptPlainIl2p);
            deframers[phase].SyncFound = () => _span.Sync(reading, demodulator!.InputSamplePosition);
        }

        // Reset the deframers on the DCD falling edge - same rationale as BpskModem:
        // a carrier that drops mid-collection leaves the deframer consuming the next
        // transmission's sync word as phantom payload.
        bool previousDcd = false;
        demodulator = new AfskDemodulator(
            sampleRate, static _ => { }, centerFrequency,
            phaseBitSink: (bit, phase) =>
            {
                if (phase == 0)
                {
                    _bitsSeen++;
                    bool dcd = demodulator!.CarrierDetect;
                    if (previousDcd && !dcd)
                    {
                        foreach (Il2pReceiver receiver in deframers)
                        {
                            receiver.Reset();
                        }
                    }

                    previousDcd = dcd;
                }

                deframers[phase].PushBit(bit);
            });
        _demodulator = demodulator;
        _modulator = new AfskModulator(
            sampleRate, Baud, centerFrequency - Bell202ToneShift, centerFrequency + Bell202ToneShift);
    }

    /// <inheritdoc />
    public event Action<byte[], FrameQuality>? FrameDecoded;

    /// <inheritdoc />
    public string Mode => _crc ? "afsk1200-il2pc" : "afsk1200-il2p";

    /// <inheritdoc />
    public bool CarrierDetect => _demodulator.CarrierDetect;

    /// <inheritdoc />
    public bool ChannelBusy => _demodulator.ChannelBusy;

    /// <inheritdoc />
    public int FrameSpanMarginSamples => _spanMargin;

    /// <inheritdoc />
    public bool TryTakeFrameSpan(out long fromSample, out long toSample) =>
        _span.TryTakeFrameSpan(out fromSample, out toSample);

    /// <inheritdoc />
    public void Process(ReadOnlySpan<float> samples) => _demodulator.Process(samples);

    /// <inheritdoc />
    public float[] Modulate(ReadOnlySpan<byte> ax25Frame, int txDelayMilliseconds)
    {
        byte[] wire = Il2pCodec.Encode(ax25Frame, appendCrc: _crc);
        int preambleBits = Math.Max(16, txDelayMilliseconds * Baud / 1000);
        byte[] bits = Il2pFramer.FrameBits(wire, preambleBits, Il2pFramer.PreambleStyle.Alternating);
        return _modulator.ModulateLevels(bits);
    }

    /// <inheritdoc />
    public void ResetCarrierState() => _demodulator.ResetCarrierState();
}
