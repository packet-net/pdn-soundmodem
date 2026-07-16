using Packet.SoundModem.Dsp;
using Packet.SoundModem.Il2p;
using Packet.SoundModem.Ofdm;

namespace Packet.SoundModem.Modems;

/// <summary>
/// FreeDV datac OFDM (all six datac modes) as an <see cref="IModem"/> — the codec2 burst waveform
/// carrying IL2P+CRC-framed AX.25. Each <see cref="Modulate"/> call emits one burst,
/// <c>[TXDELAY silence][preamble][packet × N][postamble][guard silence]</c>, exactly as
/// codec2's <c>freedv_data_raw_tx</c> frames a transmission; receive runs
/// <see cref="DatacReceiver"/> in burst mode with the pdn end-of-burst extension so the
/// packet count per burst can vary with the frame size.
/// </summary>
/// <remarks>
/// <para>
/// <b>Payload content — IL2P+CRC (the family-standard framing; a pdn convention at this
/// layer).</b> The datac payload is FIXED-size (datac0 14 B, datac1 510 B, datac3 126 B,
/// datac4 54 B, datac13 14 B, datac14 3 B per packet) and FreeDV defines NO framing at the
/// raw-data layer — FreeDATA layers its own ARQ protocol on top. Rather than invent one,
/// this modem fills the payloads with
/// exactly what every NinoTNC-lineage mode in this library puts on the wire:
/// <see cref="Il2pCodec"/> IL2P+CRC (<c>Encode(frame, appendCrc: true)</c>) behind the
/// 24-bit IL2P sync word (<see cref="Il2pFramer"/> with <c>preambleBits: 0</c> — the
/// payload is a clean byte pipe, so the sync word alone delimits; no training preamble is
/// needed). The bit stream is packed MSB-first into as many payloads as the frame needs —
/// sizes are exact, IL2P being unstuffed — and unused payload space is zero fill, which
/// the sync hunt ignores by design (the PSK modes' preamble is zeros for the same reason).
/// Receive concatenates a burst's decoded payloads into one bit stream through an
/// <see cref="Il2pDeframer"/> (<c>crcMode: true</c>), giving frames that span packet
/// boundaries (essential on datac0, whose 14-byte payload is smaller than any AX.25
/// frame), several frames per packet, and the family's per-frame
/// <see cref="FrameQuality"/> (Reed-Solomon corrected bytes + trailing-CRC state). The
/// <em>waveform</em> is fully FreeDV-compatible — a stock <c>freedv_data_raw_rx</c>
/// recovers the payload bytes unchanged — but their <em>interpretation</em> is pdn's own:
/// two pdn-soundmodem stations interoperate; FreeDATA or other raw-data peers would see
/// an IL2P bit stream. Frame size limits are IL2P's (≈1023-byte payload;
/// <see cref="Il2pCodec.Encode"/> rejects beyond it and the channel drops the frame
/// without killing the transmitter).
/// </para>
/// <para>
/// <b>Burst shape:</b> one AX.25 frame per burst, ⌈IL2P bits / payload bits⌉ packets per
/// burst. The receiver sets its packets-per-burst to the mode's maximum and ends each
/// burst via the pdn end-of-burst extension (<see cref="OfdmDemodulator.EndOfBurstUwDrop"/>
/// + a CRC backstop in <see cref="DatacReceiver"/>). The trailing guard silence covers
/// that detection window (the UW check runs <c>Nuwframes</c> frames into the phantom
/// packet after the postamble) so back-to-back bursts in one key-up do not land their
/// preamble inside a still-synced receiver — far shorter than the two packets of silence
/// codec2's own file tools append. Caveat, datac1: its 16-bit unique word gives the
/// end-of-burst check a ~10&#160;% chance per burst of lingering one phantom packet (the
/// CRC backstop then ends it); a back-to-back burst inside that ~4&#160;s window would be
/// lost. Bench-tested; not yet HF-proven.
/// </para>
/// <para>
/// <b>Rate bridge:</b> the OFDM engine is native 8&#160;kHz; this modem runs on the
/// 48&#160;kHz DSP path (48000 = 6·8000 — integer both ways): receive decimates ÷6 through
/// the anti-aliased <see cref="Decimator"/>, transmit renders the burst at 8&#160;kHz and
/// upsamples ×6 through the image-rejecting <see cref="Upsampler"/>. The 12&#160;kHz path
/// is unusable (8000&#160;∤&#160;12000). Any integer multiple of 8&#160;kHz is accepted,
/// including 8&#160;kHz itself (no resampling — the native/test path).
/// </para>
/// <para>
/// <b>DCD:</b> <see cref="CarrierDetect"/> is the demodulator's burst sync state
/// (Trial/Synced — a preamble/postamble correlation confirmed by the unique word). It is
/// honest but late: it asserts about one modem frame (~110&#160;ms) into a burst, once the
/// preamble correlator fires, and it never sees a burst it fails to acquire.
/// <see cref="EnergyBusyDetector"/> on the decimated 8&#160;kHz band is the practical
/// carrier-sense source (asserts within ~20&#160;ms of burst energy);
/// <see cref="ChannelBusy"/> is the OR of both.
/// </para>
/// </remarks>
public sealed class FreeDvDatacModem : IModem
{
    /// <summary>The engine's native sample rate — 8 kHz for every datac mode.</summary>
    private const int NativeRate = 8000;

    private readonly Action<byte[]> _frameReceived;
    private readonly DatacTransmitter _tx;
    private readonly DatacReceiver _rx;
    private readonly Il2pDeframer _deframer;
    private readonly EnergyBusyDetector _energyBusy;
    private readonly Decimator? _decimator;
    private readonly float[] _decimated;
    private readonly int _chunk;
    private readonly int _sampleRate;
    private readonly int _factor;
    private readonly int _guardTailSamples;

    // Receive staging at 8 kHz: the demodulator consumes exactly Nin samples at a time, so
    // arbitrary-size Process blocks are queued here and handed over Nin by Nin.
    private short[] _fifo;
    private int _fifoStart;
    private int _fifoEnd;

    /// <summary>Creates the modem.</summary>
    /// <param name="sampleRate">Channel DSP rate; must be an integer multiple of 8000
    /// (48000 on the daemon's 48 kHz path; 8000 runs the engine natively).</param>
    /// <param name="frameReceived">Receives each decoded AX.25 frame.</param>
    /// <param name="mode">Any of the six datac modes (datac0/1/3/4/13/14).</param>
    public FreeDvDatacModem(int sampleRate, Action<byte[]> frameReceived, OfdmMode mode)
    {
        ArgumentNullException.ThrowIfNull(frameReceived);
        ArgumentNullException.ThrowIfNull(mode);
        if (sampleRate < NativeRate || sampleRate % NativeRate != 0)
        {
            throw new ArgumentException(
                $"sample rate must be an integer multiple of {NativeRate} (the engine's native " +
                "rate); use the 48 kHz DSP path — 12 kHz has no integer ratio to 8 kHz",
                nameof(sampleRate));
        }

        _frameReceived = frameReceived;
        _sampleRate = sampleRate;
        _factor = sampleRate / NativeRate;
        _tx = new DatacTransmitter(mode);
        _rx = new DatacReceiver(mode, MaxPacketsPerBurst(_tx), endOfBurstDetection: true);
        _deframer = new Il2pDeframer(
            (frame, info) =>
            {
                _frameReceived(frame);
                FrameDecoded?.Invoke(frame, new FrameQuality(
                    Mode, frame.Length, info.CorrectedSymbols, info.CrcValid,
                    FrequencyOffsetHz: _rx.Demod.FoffEstHz));
            },
            crcMode: true);
        _energyBusy = new EnergyBusyDetector(NativeRate);

        // Guard silence appended after the postamble: the receiver's end-of-burst UW check
        // fires Nuwframes frames after the last data packet (the postamble is the first of
        // those), so cover the remaining frames plus the upsampler's FIR flush and timing
        // slop, keeping a following burst's preamble out of the still-synced window.
        _guardTailSamples = (_rx.Demod.Nuwframes * mode.SamplesPerFrame) + (NativeRate / 50);

        if (_factor > 1)
        {
            _decimator = new Decimator(sampleRate, _factor);
            _decimated = new float[1024];
            _chunk = (_decimated.Length - 1) * _factor;
        }
        else
        {
            _decimated = [];
            _chunk = int.MaxValue;
        }

        // Big enough for the demodulator's largest possible request (a burst preamble skip,
        // up to two frames) plus one decimated daemon block; grows if ever exceeded.
        _fifo = new short[(4 * mode.SamplesPerFrame) + 1024];
    }

    /// <summary>Creates the datac0 mode — 500 Hz OBW, 14-byte packets; every AX.25 frame
    /// spans several packets of its burst.</summary>
    public static FreeDvDatacModem Datac0(int sampleRate, Action<byte[]> frameReceived) =>
        new(sampleRate, frameReceived, OfdmMode.Datac0);

    /// <summary>Creates the datac1 mode — 1700 Hz OBW, 510-byte packets, the throughput
    /// workhorse.</summary>
    public static FreeDvDatacModem Datac1(int sampleRate, Action<byte[]> frameReceived) =>
        new(sampleRate, frameReceived, OfdmMode.Datac1);

    /// <summary>Creates the datac3 mode — 500 Hz OBW, 126-byte packets, low-SNR.</summary>
    public static FreeDvDatacModem Datac3(int sampleRate, Action<byte[]> frameReceived) =>
        new(sampleRate, frameReceived, OfdmMode.Datac3);

    /// <summary>Creates the datac4 mode — 250 Hz OBW, 54-byte packets, very low SNR
    /// (RX band-pass filtered).</summary>
    public static FreeDvDatacModem Datac4(int sampleRate, Action<byte[]> frameReceived) =>
        new(sampleRate, frameReceived, OfdmMode.Datac4);

    /// <summary>Creates the datac13 mode — 200 Hz OBW, 14-byte packets, the narrowest mode
    /// (RX band-pass filtered).</summary>
    public static FreeDvDatacModem Datac13(int sampleRate, Action<byte[]> frameReceived) =>
        new(sampleRate, frameReceived, OfdmMode.Datac13);

    /// <summary>Creates the datac14 mode — 250 Hz OBW, 3-byte packets, short-burst signalling
    /// (RX band-pass filtered; every AX.25 frame spans many packets).</summary>
    public static FreeDvDatacModem Datac14(int sampleRate, Action<byte[]> frameReceived) =>
        new(sampleRate, frameReceived, OfdmMode.Datac14);

    /// <inheritdoc />
    public event Action<byte[], FrameQuality>? FrameDecoded;

    /// <inheritdoc />
    public string Mode => $"freedv-{_tx.Mode.Name}";

    /// <inheritdoc />
    public bool CarrierDetect => _rx.Demod.State != SyncState.Search;

    /// <inheritdoc />
    public bool ChannelBusy => CarrierDetect || _energyBusy.Busy;

    /// <inheritdoc />
    public void Process(ReadOnlySpan<float> samples)
    {
        if (_decimator is null)
        {
            FeedNative(samples);
            return;
        }

        while (!samples.IsEmpty)
        {
            int take = Math.Min(samples.Length, _chunk);
            int produced = _decimator.Process(samples[..take], _decimated);
            FeedNative(_decimated.AsSpan(0, produced));
            samples = samples[take..];
        }
    }

    /// <inheritdoc />
    public float[] Modulate(ReadOnlySpan<byte> ax25Frame, int txDelayMilliseconds)
    {
        // The family-standard framing, composed exactly as the other il2pc modes do —
        // except preambleBits: 0 (a clean byte pipe needs no training bits; the sync word
        // delimits). Il2pCodec rejects empty/oversize frames; the channel drops those.
        byte[] wire = Il2pCodec.Encode(ax25Frame, appendCrc: true);
        byte[] bits = Il2pFramer.FrameBits(wire, preambleBits: 0);

        // Pack MSB-first (the order Il2pDeframer consumes) into exactly as many payloads as
        // needed — IL2P is unstuffed, so the size is deterministic. Unused space stays
        // zero: safe fill the sync hunt ignores, like the PSK modes' zero preamble.
        int bitsPerPacket = _tx.PayloadBytes * 8;
        int packets = (bits.Length + bitsPerPacket - 1) / bitsPerPacket;
        var payloads = new byte[packets][];
        for (int p = 0; p < packets; p++)
        {
            payloads[p] = new byte[_tx.PayloadBytes];
        }

        for (int i = 0; i < bits.Length; i++)
        {
            if (bits[i] != 0)
            {
                payloads[i / bitsPerPacket][(i % bitsPerPacket) >> 3] |= (byte)(0x80 >> (i & 7));
            }
        }

        Cf[] burst = _tx.ModulateBurst(payloads);

        // TXDELAY is leading silence — the radio's PTT-to-RF allowance; the burst brings
        // its own acquisition preamble. The guard tail covers the receiver's end-of-burst
        // window (see the constructor).
        int delaySamples = NativeRate * Math.Max(0, txDelayMilliseconds) / 1000;
        var native = new float[delaySamples + burst.Length + _guardTailSamples];
        for (int i = 0; i < burst.Length; i++)
        {
            // codec2's ±16384 short-scale amplitude mapped onto the channel's ±1.0 float
            // convention (peak ≈ 0.5, in family with the other modems' 0.8 headroom).
            native[delaySamples + i] = burst[i].Re * (1f / 32768f);
        }

        if (_factor == 1)
        {
            return native;
        }

        var upsampler = new Upsampler(_sampleRate, _factor);
        var output = new float[upsampler.OutputLength(native.Length)];
        upsampler.Process(native, output);
        return output;
    }

    /// <inheritdoc />
    /// <remarks>Clears the energy detector and the staged receive samples. The OFDM sync
    /// state has no reset seam; it self-drops after every burst and a stale trial decays to
    /// search within a couple of frames of post-transmission audio, so it is deliberately
    /// left to decay rather than reached into.</remarks>
    public void ResetCarrierState()
    {
        _energyBusy.Reset();
        _fifoStart = 0;
        _fifoEnd = 0;
    }

    /// <summary>Packets the largest IL2P frame can occupy — the receiver's packets-per-burst
    /// ceiling; real bursts end early via the UW/CRC end-of-burst detection.</summary>
    private static int MaxPacketsPerBurst(DatacTransmitter tx)
    {
        int maxWireBytes = 3 /* sync */ + Il2pCodec.HeaderWireLength
            + Il2pBlockLayout.Compute(Il2pCodec.MaxPayloadBytes).WireLength
            + Il2pCodec.TrailingCrcWireLength;
        int bitsPerPacket = tx.PayloadBytes * 8;
        return ((maxWireBytes * 8) + bitsPerPacket - 1) / bitsPerPacket;
    }

    /// <summary>Feeds 8 kHz samples: energy detection, float→short (codec2's ±32767 scale),
    /// stage in the FIFO, then hand the demodulator exactly <c>Nin</c> samples at a time.</summary>
    private void FeedNative(ReadOnlySpan<float> native)
    {
        // Compact before appending (the tail is at most one demod request, ≤ two frames).
        if (_fifoStart > 0)
        {
            Array.Copy(_fifo, _fifoStart, _fifo, 0, _fifoEnd - _fifoStart);
            _fifoEnd -= _fifoStart;
            _fifoStart = 0;
        }

        if (_fifoEnd + native.Length > _fifo.Length)
        {
            Array.Resize(ref _fifo, Math.Max(_fifo.Length * 2, _fifoEnd + native.Length));
        }

        foreach (float sample in native)
        {
            _energyBusy.Process(sample);
            float scaled = sample * 32767f;
            _fifo[_fifoEnd++] = scaled >= 32767f ? (short)32767
                : scaled <= -32768f ? (short)-32768
                : (short)scaled;
        }

        // Feeding exactly Nin per call is behaviourally identical to one long call: the
        // receiver's internal loop consumes the whole span and self-drains any nin == 0
        // states (postamble rewinds) before returning, so Nin here is always ≥ 1.
        while (true)
        {
            int nin = _rx.Demod.Nin;
            if (_fifoEnd - _fifoStart < nin)
            {
                break;
            }

            IReadOnlyList<DatacRxResult> results = _rx.Process(_fifo.AsSpan(_fifoStart, nin));
            _fifoStart += nin;
            foreach (DatacRxResult result in results)
            {
                Deliver(result);
            }
        }
    }

    /// <summary>Streams one decoded datac packet's payload bits (MSB-first, the IL2P wire
    /// order) into the IL2P deframer, which emits any completed frames. Each burst begins a
    /// fresh IL2P stream, so packet 0 resets the deframer — otherwise a frame truncated by
    /// a lost packet at the end of one burst would consume the head of the next burst as
    /// its missing body. CRC-failed packets contribute nothing; the hole makes the affected
    /// frame fail Reed-Solomon or its trailing CRC — the family's standard loss semantics.</summary>
    private void Deliver(in DatacRxResult result)
    {
        if (!result.CrcOk)
        {
            return;
        }

        if (result.PacketInBurst == 0)
        {
            _deframer.Reset();
        }

        foreach (byte value in result.Payload)
        {
            for (int k = 7; k >= 0; k--)
            {
                _deframer.PushBit((value >> k) & 1);
            }
        }
    }
}
