namespace Packet.SoundModem.Modems;

/// <summary>
/// Frequency-diversity BPSK: 2·<c>offsetPairs</c>+1 parallel <see cref="BpskModem"/> branches
/// spaced <c>offsetHz</c> apart around the channel centre, with content-based deduplication
/// across the bank - the QtSoundModem/UZ7HO multi-decoder model (see
/// <see cref="Afsk1200MultiModem"/>) applied to the PSK modes.
/// </summary>
/// <remarks>
/// <para>
/// A bank of stepped branches helps both detectors on real HF. For <b>coherent</b>, the narrow
/// Costas tracking loop only pulls a few Hz of carrier offset in within a short (~150 ms on-air)
/// preamble, yet real carriers arrive tens of Hz off (dial resolution, TX tone tolerance, drift);
/// a branch near the carrier acquires it without widening the loop (which would forfeit coherent's
/// margin). For <b>differential</b> (the default detector), which already tolerates wide offset on
/// one branch, the diversity still helps in multi-signal conditions - measured live on the GB7RDG
/// 40 m channel, a single differential modem matched 116/117 NinoTNC frames while the bank matched
/// 117/117 and decoded 2 more the NinoTNC missed (the extra frame was a beacon overlapping other
/// traffic that an offset branch resolved). The reported carrier offset is the winning branch's
/// step plus that branch's own measurement of the residual (see <see cref="EmitBestOfChunk"/>).
/// Transmit uses the centre branch only.
/// </para>
/// <para>
/// The step is sized to the single-branch offset tolerance, which scales with the symbol rate:
/// ≈±(baud/60) Hz for a 150 ms preamble measured against the loopback modulator. The default
/// <c>offsetHz</c> = baud/40 keeps the worst-case residual (half a step) inside that, so coverage
/// is continuous across ±<c>offsetPairs</c>·<c>offsetHz</c>. More branches widen coverage at a
/// linear CPU cost (each branch is a full band-pass + Costas chain).
/// </para>
/// </remarks>
public sealed class BpskMultiModem : IModem, IConstellationSource
{
    private readonly BpskModem[] _branches;
    private readonly BpskModem _transmit;
    private readonly Action<byte[]> _frameReceived;
    private readonly FrameDeduper _deduper;
    private readonly int _dedupeChunk;
    private readonly int _baud;
    private readonly bool _crc;
    private readonly List<Candidate> _candidates = [];
    private long _samplesProcessed;

    /// <summary>One branch's copy of a frame, held until the chunk ends and the branches can be
    /// compared. <paramref name="ResidualHz"/> is that branch's own measurement of how far the
    /// signal sat from <em>its</em> centre - null where the branch could not measure it - so
    /// branch + residual is the station's offset from the bank's centre however far out the
    /// branch that copied it happened to be.</summary>
    private readonly record struct Candidate(
        byte[] Frame, double BranchOffsetHz, double? ResidualHz, FrameQuality Quality);

    /// <summary>Creates the bank.</summary>
    /// <param name="sampleRate">Channel DSP rate (multiple of <paramref name="baud"/>).</param>
    /// <param name="frameReceived">Receives each unique decoded AX.25 frame once.</param>
    /// <param name="crc">IL2P+CRC mode (both stations must agree). On for NinoTNC networks.</param>
    /// <param name="centreFrequency">Channel centre - the middle branch and the TX carrier.</param>
    /// <param name="baud">Symbol rate: 300 (mode 8) or 1200 (mode 10).</param>
    /// <param name="offsetPairs">Extra branches either side of centre (0 = a single branch,
    /// i.e. a plain <see cref="BpskModem"/>).</param>
    /// <param name="offsetHz">Frequency step between adjacent branches; defaults to baud/40,
    /// sized to the single-branch offset tolerance.</param>
    /// <param name="detector">Differential (default) or coherent detection.</param>
    /// <param name="acceptPlainIl2p">Pass frames that arrive as plain IL2P, with no trailing CRC,
    /// to <paramref name="frameReceived"/> as well as reporting them (off by default, and inert
    /// unless <paramref name="crc"/> is on). They are read either way - see
    /// <see cref="Il2pReceiver"/>. Every branch reads both ways; the bank's content dedupe is
    /// what keeps a transmission two branches read differently to one delivery.</param>
    /// <param name="secondDetector">When set (and different from <paramref name="detector"/>),
    /// every offset position gets a second branch under this detector and the bank delivers
    /// the UNION of what the two detectors decode - rx-roadmap workstream 2's ensemble,
    /// bought with the existing content dedupe. Doubles the bank's CPU; null (the default)
    /// keeps the single-detector bank.</param>
    public BpskMultiModem(
        int sampleRate, Action<byte[]> frameReceived, bool crc = true,
        double centreFrequency = 1500, int baud = 300, int offsetPairs = 4,
        double? offsetHz = null, PskDetector detector = PskDetector.Differential,
        bool acceptPlainIl2p = false, PskDetector? secondDetector = null)
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

        int positions = 2 * offsetPairs + 1;
        int perPosition = secondDetector is not null && secondDetector != detector ? 2 : 1;
        _branches = new BpskModem[positions * perPosition];
        for (int i = 0; i < positions; i++)
        {
            double offset = (i - offsetPairs) * step;
            // Drive everything off FrameDecoded (which carries the CRC/FEC quality); the required
            // frame sink is a no-op so each decode reaches the deduper exactly once.
            _branches[i * perPosition] = new BpskModem(
                sampleRate, static _ => { }, crc, centreFrequency + offset, baud, detector: detector,
                acceptPlainIl2p: acceptPlainIl2p);
            _branches[i * perPosition].FrameDecoded += (frame, quality) => OnFrame(frame, offset, quality);
            if (perPosition == 2)
            {
                // The ensemble twin: the same position under the second detector. The
                // detectors' error sets are quasi-independent (measured on the opening
                // evening of the 40 m capture: a roughly symmetric ~1.5 % exchange of
                // exclusive frames, +1.1 % union - docs/rx-roadmap.md workstream 2), and
                // the bank's content dedupe already reduces N copies of a transmission to
                // one delivery, so the union comes for exactly one more branch's CPU per
                // position and no new machinery.
                _branches[(i * perPosition) + 1] = new BpskModem(
                    sampleRate, static _ => { }, crc, centreFrequency + offset, baud,
                    detector: secondDetector!.Value, acceptPlainIl2p: acceptPlainIl2p);
                _branches[(i * perPosition) + 1].FrameDecoded += (frame, quality) => OnFrame(frame, offset, quality);
            }
        }

        _transmit = _branches[offsetPairs * perPosition]; // the centre (offset 0) primary branch
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
    /// <remarks>The centre branch's view. One branch's decisions are a constellation; the
    /// whole bank's overlaid would be that constellation plus a rotated copy per offset
    /// branch. Forwarded because the catalogue builds this bank for every bpsk mode, and
    /// without it the channel's constellation sink saw a modem with nothing to plot.</remarks>
    public event Action<ConstellationPoint>? SymbolPlotted
    {
        add => _transmit.SymbolPlotted += value;
        remove => _transmit.SymbolPlotted -= value;
    }

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
        // caller hands over one huge buffer - otherwise a legitimate repeat later in the same
        // buffer would be suppressed (mirrors Afsk1200MultiModem).
        for (int position = 0; position < samples.Length; position += _dedupeChunk)
        {
            var slice = samples.Slice(position, Math.Min(_dedupeChunk, samples.Length - position));
            foreach (BpskModem branch in _branches)
            {
                branch.Process(slice);
            }

            _samplesProcessed += slice.Length;
            EmitBestOfChunk();
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

    // Several branches usually decode the same transmission within a frame-time of each other,
    // which is well inside one chunk. Hold them all and let the chunk end decide, rather than
    // emitting whichever finished first.
    private void OnFrame(byte[] frame, double offsetHz, FrameQuality quality) =>
        _candidates.Add(new Candidate(frame, offsetHz, quality.FrequencyOffsetHz, quality));

    /// <summary>
    /// Emits one frame per distinct transmission seen this chunk, from the branch that was
    /// actually tuned closest to it.
    /// </summary>
    /// <remarks>
    /// <para><b>Why not simply the first branch to finish.</b> Branches are fed in ascending
    /// order and the differential detector tolerates wide offset on any one branch, so a normal
    /// signal is copyable by branches well either side of it and "first" means "lowest-centred
    /// branch that could still read it" - index 0 by iteration order alone. Reporting that
    /// branch's nominal step as the carrier offset put 82 % of 431 frames from a GPSDO-locked
    /// station at exactly −30 Hz, the comb's most negative position, and had one station read
    /// +30, −30 and −15 Hz inside 23 seconds (issue #202). That is fine for deciding
    /// <em>whether</em> to emit - the bytes are identical either way and the deduper is
    /// content-based - and useless as a frequency reading.</para>
    /// <para>So the branches are compared instead, on each one's own
    /// <see cref="BpskDemodulator.CarrierOffsetHz"/> - how far it measured the signal from its
    /// own centre. The smallest residual is the best-matched branch, and its
    /// <c>branch + residual</c> is the station's offset from the bank's centre. Where no branch
    /// could measure (nothing coherent enough in the window), the offset is reported as
    /// <c>null</c>: "we did not measure it" is the honest answer, and the comb position is not
    /// a substitute for one.</para>
    /// <para>The cost is that a frame waits for the end of its chunk - at most 100 ms, against
    /// frames that take seconds at 300 baud (mirrors <see cref="Afsk300MultiModem"/>).</para>
    /// </remarks>
    private void EmitBestOfChunk()
    {
        while (_candidates.Count > 0)
        {
            Candidate best = _candidates[0];
            for (int i = 1; i < _candidates.Count; i++)
            {
                if (IsSameFrame(_candidates[i].Frame, best.Frame)
                    && IsBetter(_candidates[i], best))
                {
                    best = _candidates[i];
                }
            }

            for (int i = _candidates.Count - 1; i >= 0; i--)
            {
                if (IsSameFrame(_candidates[i].Frame, best.Frame))
                {
                    _candidates.RemoveAt(i);
                }
            }

            // Still deduped across chunks: a transmission straddling a chunk boundary reaches
            // here twice, and the window is what stops the second copy being delivered. A copy
            // that is only being shown says so, so that it cannot swallow the host's copy of a
            // retransmission a second later - see FrameDeduper.
            if (!_deduper.ShouldEmit(best.Frame, _samplesProcessed, !best.Quality.MonitorOnly))
            {
                continue;
            }

            // A monitor-only copy is reported and not handed on, exactly as a single branch would
            // do with it: the bank is a receiver, not a second opinion about what the host asked
            // for. IsBetter has already preferred a deliverable copy where one of the branches
            // produced one, so this asks the winner rather than the group.
            if (!best.Quality.MonitorOnly)
            {
                _frameReceived(best.Frame);
            }

            FrameDecoded?.Invoke(best.Frame, best.Quality with
            {
                Mode = Mode,
                FrequencyOffsetHz = best.ResidualHz is { } residual
                    ? best.BranchOffsetHz + residual
                    : null,
            });
        }
    }

    /// <summary>Ranks two branches' copies of the same frame: the best-evidenced reading wins
    /// (<see cref="DecodeEvidence"/>), then a measured branch beats an unmeasured one, the
    /// better-centred of two measured branches wins, and two unmeasured copies are separated by
    /// FEC work then by distance from the bank centre - anything but array order, which is the
    /// bias being removed.</summary>
    /// <remarks>
    /// The evidence test comes first, ahead of the centring that the rest of this exists for,
    /// because the two facts are not the same size. A branch whose IL2P+CRC reading claimed the
    /// frame has <em>checked</em> it; a branch that only managed a plain reading of the same bytes
    /// has an intact frame with a trailer that would not verify there, and there is no way to tell
    /// that from a genuinely CRC-less neighbour. When both happen at once the station did verify
    /// those bytes, so that is the copy delivered and the quality reported - whichever branch
    /// produced it and whatever <c>acceptPlainIl2p</c> says, so the answer cannot depend on which
    /// branch happened to be nearest. The cost is that the frequency reading then comes from the
    /// branch that verified it rather than the best-centred one; on the GB7RDG capture that is
    /// 8.56 Hz against 8.37 Hz, which is nothing set beside reporting a verified frame as
    /// standing on Reed-Solomon alone.
    /// </remarks>
    private static bool IsBetter(in Candidate candidate, in Candidate best)
    {
        int evidence = DecodeEvidence.RankOf(candidate.Quality);
        int bestEvidence = DecodeEvidence.RankOf(best.Quality);
        if (evidence != bestEvidence)
        {
            return evidence > bestEvidence;
        }

        if (candidate.ResidualHz is { } residual)
        {
            return best.ResidualHz is not { } bestResidual
                || Math.Abs(residual) < Math.Abs(bestResidual);
        }

        if (best.ResidualHz is not null)
        {
            return false;
        }

        int corrected = candidate.Quality.CorrectedBytes ?? int.MaxValue;
        int bestCorrected = best.Quality.CorrectedBytes ?? int.MaxValue;
        return corrected != bestCorrected
            ? corrected < bestCorrected
            : Math.Abs(candidate.BranchOffsetHz) < Math.Abs(best.BranchOffsetHz);
    }

    private static bool IsSameFrame(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b);
}
