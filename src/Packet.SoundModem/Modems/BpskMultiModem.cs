namespace Packet.SoundModem.Modems;

/// <summary>
/// Frequency-diversity BPSK: 2·<c>offsetPairs</c>+1 parallel <see cref="BpskModem"/> branches
/// spaced <c>offsetHz</c> apart around the channel centre, with content-based deduplication
/// across the bank — the QtSoundModem/UZ7HO multi-decoder model (see
/// <see cref="Afsk1200MultiModem"/>) applied to the PSK modes.
/// </summary>
/// <remarks>
/// <para>
/// A bank of stepped branches helps both detectors on real HF. For <b>coherent</b>, the narrow
/// Costas tracking loop only pulls a few Hz of carrier offset in within a short (~150 ms on-air)
/// preamble, yet real carriers arrive tens of Hz off (dial resolution, TX tone tolerance, drift);
/// a branch near the carrier acquires it without widening the loop (which would forfeit coherent's
/// margin). For <b>differential</b> (the default detector), which already tolerates wide offset on
/// one branch, the diversity still helps in multi-signal conditions — measured live on the GB7RDG
/// 40 m channel, a single differential modem matched 116/117 NinoTNC frames while the bank matched
/// 117/117 and decoded 2 more the NinoTNC missed (the extra frame was a beacon overlapping other
/// traffic that an offset branch resolved). The branch's step reports the carrier offset. Transmit
/// uses the centre branch only.
/// </para>
/// <para>
/// The step is sized to the single-branch offset tolerance, which scales with the symbol rate:
/// ≈±(baud/60) Hz for a 150 ms preamble measured against the loopback modulator. The default
/// <c>offsetHz</c> = baud/40 keeps the worst-case residual (half a step) inside that, so coverage
/// is continuous across ±<c>offsetPairs</c>·<c>offsetHz</c>. More branches widen coverage at a
/// linear CPU cost (each branch is a full band-pass + Costas chain).
/// </para>
/// </remarks>
public sealed class BpskMultiModem : IModem
{
    private readonly BpskModem[] _branches;
    private readonly BpskModem _transmit;
    private readonly Action<byte[]> _frameReceived;
    private readonly FrameDeduper _deduper;
    private readonly int _dedupeChunk;
    private readonly int _baud;
    private readonly bool _crc;
    private long _samplesProcessed;

    /// <summary>Creates the bank.</summary>
    /// <param name="sampleRate">Channel DSP rate (multiple of <paramref name="baud"/>).</param>
    /// <param name="frameReceived">Receives each unique decoded AX.25 frame once.</param>
    /// <param name="crc">IL2P+CRC mode (both stations must agree). On for NinoTNC networks.</param>
    /// <param name="centreFrequency">Channel centre — the middle branch and the TX carrier.</param>
    /// <param name="baud">Symbol rate: 300 (mode 8) or 1200 (mode 10).</param>
    /// <param name="offsetPairs">Extra branches either side of centre (0 = a single branch,
    /// i.e. a plain <see cref="BpskModem"/>).</param>
    /// <param name="offsetHz">Frequency step between adjacent branches; defaults to baud/40,
    /// sized to the single-branch offset tolerance.</param>
    /// <param name="detector">Differential (default) or coherent detection.</param>
    public BpskMultiModem(
        int sampleRate, Action<byte[]> frameReceived, bool crc = true,
        double centreFrequency = 1500, int baud = 300, int offsetPairs = 4,
        double? offsetHz = null, PskDetector detector = PskDetector.Differential)
    {
        ArgumentNullException.ThrowIfNull(frameReceived);
        ArgumentOutOfRangeException.ThrowIfNegative(offsetPairs);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(baud, 0);
        _frameReceived = frameReceived;
        _baud = baud;
        _crc = crc;
        // Dedup window a few frame-times wide: branches decode the same transmission within a
        // frame-time of one another, and the sample clock advances with the audio (see Process).
        _deduper = new FrameDeduper(3L * sampleRate);
        _dedupeChunk = Math.Max(1, sampleRate / 10);
        double step = offsetHz ?? baud / 40.0;

        int count = 2 * offsetPairs + 1;
        _branches = new BpskModem[count];
        for (int i = 0; i < count; i++)
        {
            double offset = (i - offsetPairs) * step;
            // Drive everything off FrameDecoded (which carries the CRC/FEC quality); the required
            // frame sink is a no-op so each decode reaches the deduper exactly once.
            _branches[i] = new BpskModem(
                sampleRate, static _ => { }, crc, centreFrequency + offset, baud, detector: detector);
            _branches[i].FrameDecoded += (frame, quality) => OnFrame(frame, offset, quality);
        }

        _transmit = _branches[offsetPairs]; // the centre (offset 0) branch
    }

    /// <summary>Creates the 300 bps bank (NinoTNC mode 8) around <paramref name="carrierFrequency"/>.</summary>
    public static BpskMultiModem Bpsk300(
        int sampleRate, Action<byte[]> frameReceived, bool crc = true,
        PskDetector detector = PskDetector.Differential, double carrierFrequency = 1500,
        int offsetPairs = 4) =>
        new(sampleRate, frameReceived, crc, carrierFrequency, 300, offsetPairs, detector: detector);

    /// <summary>Creates the 1200 bps bank (NinoTNC mode 10) around <paramref name="carrierFrequency"/>.</summary>
    public static BpskMultiModem Bpsk1200(
        int sampleRate, Action<byte[]> frameReceived, bool crc = true,
        PskDetector detector = PskDetector.Differential, double carrierFrequency = 1500,
        int offsetPairs = 4) =>
        new(sampleRate, frameReceived, crc, carrierFrequency, 1200, offsetPairs, detector: detector);

    /// <inheritdoc />
    public event Action<byte[], FrameQuality>? FrameDecoded;

    /// <inheritdoc />
    public string Mode => $"bpsk{_baud}{(_crc ? "-il2pc" : "-il2p")}-multi{_branches.Length}";

    /// <inheritdoc />
    public bool CarrierDetect
    {
        get
        {
            foreach (BpskModem branch in _branches)
            {
                if (branch.CarrierDetect)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <inheritdoc />
    public bool ChannelBusy
    {
        get
        {
            foreach (BpskModem branch in _branches)
            {
                if (branch.ChannelBusy)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <inheritdoc />
    public void Process(ReadOnlySpan<float> samples)
    {
        // Feed the bank in bounded chunks so the dedupe clock advances with the audio even when a
        // caller hands over one huge buffer — otherwise a legitimate repeat later in the same
        // buffer would be suppressed (mirrors Afsk1200MultiModem).
        for (int position = 0; position < samples.Length; position += _dedupeChunk)
        {
            var slice = samples.Slice(position, Math.Min(_dedupeChunk, samples.Length - position));
            foreach (BpskModem branch in _branches)
            {
                branch.Process(slice);
            }

            _samplesProcessed += slice.Length;
        }
    }

    /// <inheritdoc />
    public float[] Modulate(ReadOnlySpan<byte> ax25Frame, int txDelayMilliseconds) =>
        _transmit.Modulate(ax25Frame, txDelayMilliseconds);

    /// <inheritdoc />
    public void ResetCarrierState()
    {
        foreach (BpskModem branch in _branches)
        {
            branch.ResetCarrierState();
        }
    }

    // Several branches usually decode the same transmission within a frame-time of each other;
    // emit the first and drop content-identical repeats in the window.
    private void OnFrame(byte[] frame, double offsetHz, FrameQuality quality)
    {
        if (!_deduper.ShouldEmit(frame, _samplesProcessed))
        {
            return;
        }

        _frameReceived(frame);
        FrameDecoded?.Invoke(frame, quality with { Mode = Mode, FrequencyOffsetHz = offsetHz });
    }
}
