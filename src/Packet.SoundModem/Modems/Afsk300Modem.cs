using M0LTE.Dsp;
using Packet.SoundModem.Hdlc;
using M0LTE.Il2p;

namespace Packet.SoundModem.Modems;

/// <summary>Framing carried over the 300 baud HF AFSK baseband - one per NinoTNC "SSB
/// AFSK" mode.</summary>
public enum Afsk300Framing
{
    /// <summary>Legacy HF packet: NRZI + HDLC, no FEC (NinoTNC mode 12).</summary>
    Ax25,

    /// <summary>IL2P without the CRC trailer (NinoTNC mode 13).</summary>
    Il2p,

    /// <summary>IL2P with the Hamming-protected trailing CRC (NinoTNC mode 14).</summary>
    Il2pCrc,
}

/// <summary>
/// 300 baud HF AFSK - the NinoTNC "SSB AFSK" mode family (12 AX.25, 13 IL2P, 14
/// IL2P+CRC): 1600/1800 Hz tone FSK filtered to 500 Hz occupied bandwidth, per Nino's
/// v3/4.43 mode-switch mapping in flashtnc's release-notes.txt. Same demodulator chain as
/// the Bell 202 modes - a quadrature FM discriminator does not care about the shift - with
/// per-mode filters and bit clock.
/// </summary>
/// <remarks>
/// Tone assignment (mark = 1600) is only a convention here: AX.25 rides NRZI, which is
/// polarity-agnostic by construction, and both IL2P receivers hunt sync in both
/// polarities (ours since the 9600 IL2P work; the NinoTNC's since firmware 2.42's "IL2P
/// receive inversion detection"). Bench-confirmed against a NinoTNC either way.
/// </remarks>
public sealed class Afsk300Modem : IModem, IFrameSpanSource
{
    private const int Baud = 300;

    // Bench-tuned against recorded NinoTNC mode-12 audio (a 7×7 sweep of both values over
    // six real bursts): ±300 Hz band-pass, 300 Hz I/Q low-pass sits mid-plateau at a full
    // score. Nino filters these modes to 500 Hz OBW and the measured energy spans
    // 1520-1870 Hz, so ±300 passes the signal whole with room for the filter's own
    // transition width, while staying tight enough to keep the discriminator clean.
    //
    // These were shipped at 400 for a year of bench work, where the extra width cost
    // nothing - one clean signal at high SNR. On the real 40 m channel it cost frames: a
    // quadrature discriminator follows the strongest thing in its passband, and ±400
    // around the band-plan centre reaches far enough to swallow the neighbouring QSO that
    // in practice lives ~200 Hz below the slot (measured 2026-08-02 on m9psy-1 off-air
    // capture: with a comparable-power neighbour in-passband, ±400 needed the packet
    // +6 dB above the interference to decode where ±250 managed −3 dB and a ±175 Hz
    // passband −12 dB). 300 is the bench plateau midpoint and the single-modem balance of
    // interference rejection against carrier-offset range; the Afsk300MultiModem bank
    // runs tighter 250 Hz branches and buys the offset range back with diversity.
    private const double DefaultBandPassHalfWidth = 300;
    private const double DefaultLowPassCutoff = 300;
    private const double ToneShift = 100;

    /// <summary>
    /// Transmit band-limit. Nino publishes 500 Hz for these modes, but his own mode-12
    /// transmission measures 305 Hz on the bench - so 500 is a ceiling, not what he
    /// actually does, and filtering to it left us 10 % wider than the TNC we share the
    /// channel with. 400 Hz puts us at 325 Hz, inside the 305-328 Hz his own two 300 AFSK
    /// modes span, and the tones only need ±100 Hz. This is a floor set by the signal, not
    /// the filter: 360 Hz reaches only 319 Hz and starts eating the modulation - our own
    /// receiver stops decoding it.
    /// </summary>
    private const double ObwHz = 400;

    /// <summary>Dedupe window across the timing phases' deframers, in bits: shorter than the
    /// shortest frame either framing can carry (an IL2P header alone is 120 bits, a minimal
    /// AX.25 frame 136), longer than the trailer a held plain IL2P reading waits for (32
    /// bits). The phases decide the same bits, so their copies of a frame arrive within a bit
    /// of each other and a genuine repeat of even the shortest frame is much further away.</summary>
    private const int DedupeWindowBits = 64;

    private readonly AfskDemodulator _demodulator;
    private readonly AfskModulator _modulator;
    private readonly Afsk300Framing _framing;
    private readonly int _sampleRate;
    private readonly double _centerFrequency;
    private readonly FrameDeduper _deduper = new(DedupeWindowBits);
    private long _bitsSeen;
    /// <summary>
    /// Where the frame just delivered was in the receive audio, for the channel's per-frame
    /// level: marked at the sync its deframer locked on and at the sample its last bit was taken
    /// on. See <see cref="FrameSpan"/>.
    /// </summary>
    private readonly FrameSpan _span = new();


    /// <summary>Creates the modem.</summary>
    /// <param name="sampleRate">Channel DSP rate (multiple of 300).</param>
    /// <param name="frameReceived">Receives each decoded AX.25 frame.</param>
    /// <param name="framing">Which of the three HF modes to run.</param>
    /// <param name="centerFrequency">Mark/space midpoint; 1700 Hz (tones 1600/1800).</param>
    /// <param name="bandPassHalfWidth">Receive band-pass half-width around the centre.
    /// Narrower rejects more of a crowded HF neighbourhood at the cost of carrier-offset
    /// range - see the constant above; <see cref="Afsk300MultiModem"/> passes 250 here.</param>
    /// <param name="lowPassCutoff">Receive I/Q low-pass cutoff, paired with
    /// <paramref name="bandPassHalfWidth"/>.</param>
    /// <param name="acceptPlainIl2p">Pass frames that arrive as plain IL2P, with no trailing CRC,
    /// to <paramref name="frameReceived"/> as well as reporting them (off by default, and inert
    /// unless <paramref name="framing"/> is <see cref="Afsk300Framing.Il2pCrc"/>). They are read
    /// either way - see <see cref="Il2pReceiver"/> for what that buys and what it costs.</param>
    public Afsk300Modem(
        int sampleRate, Action<byte[]> frameReceived, Afsk300Framing framing = Afsk300Framing.Il2pCrc,
        double centerFrequency = 1700,
        double bandPassHalfWidth = DefaultBandPassHalfWidth,
        double lowPassCutoff = DefaultLowPassCutoff,
        bool acceptPlainIl2p = false)
    {
        ArgumentNullException.ThrowIfNull(frameReceived);
        _framing = framing;
        _sampleRate = sampleRate;
        _centerFrequency = centerFrequency;

        // Declared ahead of the deframers because their callbacks need it - the DCD edge below
        // and, on both paths, the carrier-offset reading - while it in turn cannot be built
        // until the bit sink that drives it exists. Nothing dereferences it until audio flows.
        AfskDemodulator? demodulator = null;
        Action<int, int> phaseBitSink;

        // One deframer per timing phase (see AfskDemodulator.TimingPhaseCount): the phases
        // decide the same bits at slightly different instants, so their deframers run in
        // lockstep and a frame that any of them reads is delivered once, the first copy to
        // arrive winning (usually every phase that read it arrives on the same bit). The short
        // content window behind them catches the one way a copy can arrive later: a phase whose
        // own IL2P+CRC reading failed holds a plain reading until the trailer bits pass, and
        // would otherwise show it as a second, monitor-only row.
        int phases = AfskDemodulator.TimingPhaseCount;

        if (framing == Afsk300Framing.Ax25)
        {
            var deframers = new HdlcDeframer[phases];
            var nrzi = new NrziDecoder[phases];
            for (int phase = 0; phase < phases; phase++)
            {
                nrzi[phase] = new NrziDecoder();
                deframers[phase] = new HdlcDeframer(frame =>
                {
                    if (!_deduper.ShouldEmit(frame, _bitsSeen))
                    {
                        return;
                    }

                    frameReceived(frame);

                    // Before the event, so the channel's handler reads this frame's span.
                    _span.Complete(demodulator!.InputSamplePosition);
                    FrameDecoded?.Invoke(frame, new FrameQuality(
                        Mode, frame.Length, null, null,
                        // Read at the end of the burst that carried the frame, while the slicer
                        // envelopes it is derived from still describe that burst.
                        FrequencyOffsetHz: demodulator!.CarrierOffsetHz));
                });
                deframers[phase].FrameOpened = () => _span.Sync(demodulator!.InputSamplePosition);
            }

            phaseBitSink = (level, phase) =>
            {
                if (phase == 0)
                {
                    _bitsSeen++;
                }

                deframers[phase].PushBit(nrzi[phase].Decode(level));
            };
        }
        else
        {
            var deframers = new Il2pReceiver[phases];
            for (int phase = 0; phase < phases; phase++)
            {
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

                        // Before the event, so the channel reads this frame's span.
                        _span.Complete(demodulator!.InputSamplePosition);
                        FrameDecoded?.Invoke(frame, new FrameQuality(
                            Mode, frame.Length, info.CorrectedSymbols, info.CrcValid,
                            HeaderType: info.HeaderType,
                            FrequencyOffsetHz: demodulator!.CarrierOffsetHz,
                            PlainIl2p: delivery.PlainIl2p,
                            TrailerNearBits: delivery.TrailerNearBits,
                            MonitorOnly: delivery.MonitorOnly));
                    },
                    crcMode: framing == Afsk300Framing.Il2pCrc, acceptPlainIl2p: acceptPlainIl2p);
                deframers[phase].SyncFound = () => _span.Sync(demodulator!.InputSamplePosition);
            }

            // Reset the deframers on the DCD falling edge - same rationale as BpskModem:
            // a carrier that drops mid-collection leaves the deframer consuming the next
            // transmission's sync word as phantom payload.
            bool previousDcd = false;
            phaseBitSink = (bit, phase) =>
            {
                if (phase == 0)
                {
                    _bitsSeen++;
                    bool dcd = demodulator!.CarrierDetect;
                    if (previousDcd && !dcd)
                    {
                        foreach (Il2pReceiver deframer in deframers)
                        {
                            deframer.Reset();
                        }
                    }

                    previousDcd = dcd;
                }

                deframers[phase].PushBit(bit);
            };
        }

        demodulator = new AfskDemodulator(
            sampleRate, static _ => { }, centerFrequency, Baud, bandPassHalfWidth, lowPassCutoff,
            toneShift: ToneShift, phaseBitSink: phaseBitSink);
        _demodulator = demodulator;
        _modulator = new AfskModulator(
            sampleRate, Baud, centerFrequency - ToneShift, centerFrequency + ToneShift);
    }

    /// <inheritdoc />
    public event Action<byte[], FrameQuality>? FrameDecoded;

    /// <inheritdoc />
    public string Mode => _framing switch
    {
        Afsk300Framing.Ax25 => "afsk300",
        Afsk300Framing.Il2pCrc => "afsk300-il2pc",
        _ => "afsk300-il2p",
    };

    /// <inheritdoc />
    public bool CarrierDetect => _demodulator.CarrierDetect;

    /// <inheritdoc />
    public bool ChannelBusy => _demodulator.ChannelBusy;

    /// <inheritdoc />
    public bool TryTakeFrameSpan(out long fromSample, out long toSample) =>
        _span.TryTakeFrameSpan(out fromSample, out toSample);

    /// <inheritdoc />
    public void Process(ReadOnlySpan<float> samples) => _demodulator.Process(samples);

    /// <inheritdoc />
    public float[] Modulate(ReadOnlySpan<byte> ax25Frame, int txDelayMilliseconds)
    {
        if (_framing == Afsk300Framing.Ax25)
        {
            return BandLimit(_modulator.Modulate(TrainingPreamble.Prepend(
                HdlcFramer.FrameBits(ax25Frame, openingFlags: 2, closingFlags: 2),
                txDelayMilliseconds, Baud)));
        }

        byte[] wire = Il2pCodec.Encode(ax25Frame, appendCrc: _framing == Afsk300Framing.Il2pCrc);
        int preambleBits = Math.Max(16, txDelayMilliseconds * Baud / 1000);
        byte[] bits = Il2pFramer.FrameBits(wire, preambleBits, Il2pFramer.PreambleStyle.Alternating);
        return BandLimit(_modulator.ModulateLevels(bits));
    }

    /// <summary>
    /// Band-limits the transmission to the mode's 500 Hz occupied bandwidth. Nino's notes
    /// describe these modes as "filtered for 500 Hz occupied bandwidth" and his own
    /// transmissions are visibly filtered - raw phase-continuous FSK on these tones
    /// measures ~519 Hz, just over. Cheap to do and it keeps us inside a spec written for
    /// crowded HF.
    /// </summary>
    private float[] BandLimit(float[] samples)
    {
        var filter = new FirFilter(FilterDesign.BandPass(
            _centerFrequency - (ObwHz / 2), _centerFrequency + (ObwHz / 2),
            _sampleRate, 256 * _sampleRate / 12000));

        // Run the tail through too: the FIR's group delay would otherwise clip the
        // closing flag off the end of the burst.
        int taps = 256 * _sampleRate / 12000;
        var output = new float[samples.Length + taps];
        for (int i = 0; i < output.Length; i++)
        {
            output[i] = filter.Next(i < samples.Length ? samples[i] : 0f);
        }

        float peak = 0;
        foreach (float v in output)
        {
            peak = Math.Max(peak, Math.Abs(v));
        }

        if (peak > 1e-9f)
        {
            float gain = 0.8f / peak;
            for (int i = 0; i < output.Length; i++)
            {
                output[i] *= gain;
            }
        }

        return output;
    }

    /// <inheritdoc />
    public void ResetCarrierState() => _demodulator.ResetCarrierState();
}
