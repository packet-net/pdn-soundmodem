namespace Packet.SoundModem.Modems;

/// <summary>
/// Frequency-diversity 300 baud HF AFSK: 2·<c>offsetPairs</c>+1 parallel
/// <see cref="Afsk300Modem"/> branches spaced <c>offsetHz</c> apart around the channel
/// centre, each with a deliberately tight (±250 Hz) receive passband, with content-based
/// deduplication across the bank - the <see cref="Afsk1200MultiModem"/>/
/// <see cref="BpskMultiModem"/> multi-decoder model applied to the NinoTNC "SSB AFSK"
/// family. Transmit uses the centre branch only.
/// </summary>
/// <remarks>
/// <para>
/// The bank exists for a different reason than the 1200 baud one. Bell 202's problem is
/// twist; this mode's problem is <b>neighbours</b>. A 500 Hz-OBW slot on 40 m lives a
/// couple of hundred Hz from other users, and a quadrature FM discriminator follows the
/// strongest signal in its passband - so receive selectivity is worth real frames.
/// Measured on off-air capture of the live 7.0503 MHz slot (m9psy-1 web receiver,
/// 2026-08-02, a neighbouring QSO ~200 Hz below the slot): with the interferer at equal
/// power, ±400 Hz filters needed the packet +6 dB above it, ±250 managed −3 dB. Tight
/// filters, though, shrink the carrier-offset range each branch can serve (real stations
/// on the slot measured up to ±35 Hz off, and the single-demod tolerance is ±60-70 Hz),
/// so the bank steps narrow branches across the offset range instead of widening any one
/// of them: selectivity per branch, coverage from diversity. On the first 30 minutes of
/// captured traffic the shipped single modem decoded 3 of 13 frames; this bank shape
/// decoded 13 of 13.
/// </para>
/// <para>
/// No emphasis variants: across a 200 Hz tone spacing, real-world response tilt is
/// fractions of a dB - the twist machinery that transforms Bell 202 has nothing to work
/// on here.
/// </para>
/// </remarks>
public sealed class Afsk300MultiModem : IModem, IFrameSpanSource
{
    /// <summary>Per-branch receive filter width. Tighter than the single modem's 300
    /// default: a branch only has to serve half an <c>offsetHz</c> step of residual
    /// offset, so it can spend the margin on interference rejection instead.</summary>
    private const double BranchFilterHz = 250;

    private readonly Afsk300Modem[] _branches;
    private readonly Afsk300Modem _transmit;
    private readonly Action<byte[]> _frameReceived;
    private readonly FrameDeduper _deduper;
    private readonly int _dedupeChunk;
    private readonly Afsk300Framing _framing;
    private readonly List<Candidate> _candidates = [];
    /// <summary>
    /// Where the frame just delivered was in the receive audio, taken over from the branch that
    /// decoded it, for the channel's per-frame level. See <see cref="FrameSpan"/>.
    /// </summary>
    private readonly FrameSpan _span = new();

    /// <summary>What a reading leaves off the end of one of this modem's spans; see
    /// <see cref="FrameSpan.MarginSamplesFor"/>.</summary>
    private readonly int _spanMargin;

    private long _samplesProcessed;
    private bool _carrierWasPresent;

    /// <summary>One branch's copy of a frame, held until the chunk ends and the branches can
    /// be compared. <paramref name="ResidualHz"/> is that branch's own measurement of how far
    /// the signal sat from <em>its</em> centre, so branch + residual is the station's offset
    /// from the bank's centre however far out the branch that copied it happened to be.</summary>
    private readonly record struct Candidate(
        byte[] Frame, double BranchOffsetHz, double ResidualHz, FrameQuality Quality,
        long SpanFrom, long SpanTo);

    /// <summary>Creates the bank.</summary>
    /// <param name="sampleRate">Channel DSP rate (multiple of 300).</param>
    /// <param name="frameReceived">Receives each unique decoded AX.25 frame once.</param>
    /// <param name="framing">Which of the three HF framings to run (NinoTNC modes 12/13/14).</param>
    /// <param name="centerFrequency">Channel centre - the middle branch and TX.</param>
    /// <param name="offsetPairs">Extra branches either side of centre (0 = a single
    /// tight-filtered branch). The default ±5 spans ±175 Hz.</param>
    /// <param name="offsetHz">Frequency step between adjacent branches. The default 35 Hz
    /// keeps the worst-case residual (half a step) well inside a tight branch's measured
    /// ~±35 Hz range.</param>
    /// <param name="acceptPlainIl2p">Pass frames that arrive as plain IL2P, with no trailing CRC,
    /// to <paramref name="frameReceived"/> as well as reporting them (off by default, and inert
    /// unless <paramref name="framing"/> is <see cref="Afsk300Framing.Il2pCrc"/>). They are read
    /// either way - see <see cref="Il2pReceiver"/>. Every branch reads both ways; the bank's
    /// content dedupe is what keeps a transmission two branches read differently to one
    /// delivery.</param>
    public Afsk300MultiModem(
        int sampleRate,
        Action<byte[]> frameReceived,
        Afsk300Framing framing = Afsk300Framing.Il2pCrc,
        double centerFrequency = 1700,
        int offsetPairs = 5,
        double? offsetHz = null,
        bool acceptPlainIl2p = false)
    {
        ArgumentNullException.ThrowIfNull(frameReceived);
        _spanMargin = FrameSpan.MarginSamplesFor(sampleRate, 300);
        ArgumentOutOfRangeException.ThrowIfNegative(offsetPairs);
        _frameReceived = frameReceived;
        _framing = framing;
        _dedupeChunk = Math.Max(1, sampleRate / 10);
        // The dedupe window only has to span the skew between branches delivering copies of
        // ONE transmission, and those cluster tightly: measured across the catalogue's
        // 5-pair bank (issue #342's probe), every branch's copy of a burst landed on the
        // same sample, and the PSK banks' worst case under two detectors is 130 samples at
        // 12 kHz. Add up to one feed slice of clock quantisation, because candidates are
        // stamped at slice ends and a straddling pair lands one slice (at most _dedupeChunk)
        // apart, and the worst case is a little over one chunk. Two chunks covers it roughly
        // twice over, and two DISTINCT transmissions of the same bytes deliver at least one
        // burst-time apart - over a second at this baud, so at 300 baud the window can
        // never reach a second transmission at all. The previous 3 s constant was wider
        // than any ARQ retry interval and swallowed byte-identical retransmissions whole
        // (issue #342). The deduper is additionally cleared whenever the bank re-acquires a
        // carrier (see Process): a burst that arrives after the carrier dropped is a new
        // transmission whatever the clock says.
        _deduper = new FrameDeduper(2L * _dedupeChunk);
        double step = offsetHz ?? 35;

        int count = 2 * offsetPairs + 1;
        _branches = new Afsk300Modem[count];
        for (int i = 0; i < count; i++)
        {
            double offset = (i - offsetPairs) * step;
            // Drive everything off FrameDecoded (which carries the CRC/FEC quality); the
            // required frame sink is a no-op so each decode reaches the deduper exactly once.
            _branches[i] = new Afsk300Modem(
                sampleRate, static _ => { }, framing, centerFrequency + offset,
                bandPassHalfWidth: BranchFilterHz, lowPassCutoff: BranchFilterHz,
                acceptPlainIl2p: acceptPlainIl2p);
            Afsk300Modem branch = _branches[i];
            branch.FrameDecoded += (frame, quality) => OnFrame(branch, frame, offset, quality);
        }

        _transmit = _branches[offsetPairs]; // the centre (offset 0) branch
    }

    /// <inheritdoc />
    public event Action<byte[], FrameQuality>? FrameDecoded;

    /// <inheritdoc />
    public string Mode => $"{_transmit.Mode}-multi{_branches.Length}";

    /// <inheritdoc />
    public bool CarrierDetect
    {
        get
        {
            foreach (Afsk300Modem branch in _branches)
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
            foreach (Afsk300Modem branch in _branches)
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
    public int FrameSpanMarginSamples => _spanMargin;

    /// <inheritdoc />
    public bool TryTakeFrameSpan(out long fromSample, out long toSample) =>
        _span.TryTakeFrameSpan(out fromSample, out toSample);

    /// <inheritdoc />
    public void Process(ReadOnlySpan<float> samples)
    {
        // Feed the bank in bounded chunks so the dedupe clock advances with the audio
        // even when a caller hands over one huge buffer - otherwise a legitimate repeat
        // later in the same buffer would be suppressed (mirrors the other banks).
        for (int position = 0; position < samples.Length; position += _dedupeChunk)
        {
            var slice = samples.Slice(position, Math.Min(_dedupeChunk, samples.Length - position));
            foreach (Afsk300Modem branch in _branches)
            {
                branch.Process(slice);
            }

            _samplesProcessed += slice.Length;
            EmitBestOfChunk();

            // An acquisition boundary: the carrier dropped and came back, so whatever the
            // deduper remembers belongs to an earlier transmission and must not suppress
            // this one - which is what lets a retransmission through however hard the far
            // end leans on its timers. Checked after the emit so a copy of the previous
            // burst still in flight this slice is merged before the memory goes. The edge
            // can only appear at a burst's start (the OR over the branches rises once and
            // holds), and no decode of that burst can precede its own preamble, so a clear
            // never splits one transmission's copies.
            bool carrier = CarrierDetect;
            if (carrier && !_carrierWasPresent)
            {
                _deduper.CarrierAcquired();
            }

            _carrierWasPresent = carrier;
        }
    }

    /// <inheritdoc />
    public float[] Modulate(ReadOnlySpan<byte> ax25Frame, int txDelayMilliseconds) =>
        _transmit.Modulate(ax25Frame, txDelayMilliseconds);

    /// <inheritdoc />
    public void ResetCarrierState()
    {
        foreach (Afsk300Modem branch in _branches)
        {
            branch.ResetCarrierState();
        }
    }

    // Several branches usually decode the same transmission within a frame-time of each other,
    // which is well inside one chunk. Hold them all and let the chunk end decide, rather than
    // emitting whichever finished first.
    private void OnFrame(
        Afsk300Modem branch, byte[] frame, double offsetHz, FrameQuality quality)
    {
        // The branch's span goes into the candidate rather than being read at the emit below:
        // by then the chunk has moved on, and it is the branch that decoded this copy that knows
        // where it was. Consumed by the asking, so a branch cannot hand the same span twice.
        // Only where the branch had one. The out values of a take that returned false are the
        // branch's PREVIOUS span, so storing them regardless is how a frame ends up measured over
        // an earlier one; an empty range is this bank saying it has nothing to report.
        (long from, long to) = branch.TryTakeFrameSpan(out long marked, out long ended)
            ? (marked, ended)
            : (0, 0);
        _candidates.Add(new Candidate(
            frame, offsetHz, quality.FrequencyOffsetHz ?? 0, quality, from, to));
    }

    /// <summary>
    /// Emits one frame per distinct transmission seen this chunk, from the branch that was
    /// actually tuned closest to it.
    /// </summary>
    /// <remarks>
    /// <para><b>Why not simply the first branch to finish.</b> Branches are fed in ascending
    /// order and a clean signal is copyable by branches well either side of it, so "first" means
    /// "lowest-centred branch that could still read it" - which reported −105 Hz for a signal
    /// exactly on frequency, and moved with buffer framing. That is fine for deciding *whether*
    /// to emit (the bytes are identical either way; the deduper is content-based) and useless as
    /// the frequency reading that reaches an operator's display.</para>
    /// <para>So the branches are compared instead, on each one's own
    /// <see cref="AfskDemodulator.CarrierOffsetHz"/> - how far it measured the signal from its
    /// own centre. The smallest residual is the best-matched branch, and it is also the only
    /// reading guaranteed honest: a branch more than ~40 Hz off has one discriminator peak
    /// against the clamp, and with 35 Hz spacing the nearest branch is never more than half a
    /// step out. Its <c>branch + residual</c> is the station's offset from the bank's centre.</para>
    /// <para>The cost is that a frame waits for the end of its chunk - at most 100 ms, against
    /// frames that take seconds at 300 baud.</para>
    /// </remarks>
    private void EmitBestOfChunk()
    {
        while (_candidates.Count > 0)
        {
            Candidate best = _candidates[0];
            for (int i = 1; i < _candidates.Count; i++)
            {
                if (IsSameFrame(_candidates[i].Frame, best.Frame) && IsBetter(_candidates[i], best))
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
            // do with it. IsBetter has already preferred a deliverable copy where one of the
            // branches produced one, so this asks the winner rather than the group.
            if (!best.Quality.MonitorOnly)
            {
                _frameReceived(best.Frame);
            }

            // The quality keeps the branch's own Mode, which is the bare catalogue name and
            // identical on every branch. The bank's width is receiver construction - it stays
            // on IModem.Mode for the daemon's own use, and FrameQuality.Mode says why an
            // identity cannot carry it (issue #343).
            _span.Set(best.SpanFrom, best.SpanTo);
            FrameDecoded?.Invoke(best.Frame, best.Quality with
            {
                FrequencyOffsetHz = best.BranchOffsetHz + best.ResidualHz,
            });
        }
    }

    /// <summary>Ranks two branches' copies of the same frame: the best-evidenced reading wins
    /// (<see cref="DecodeEvidence"/>), and otherwise the better-centred branch does. See
    /// <see cref="BpskMultiModem"/> for why the evidence test comes first.</summary>
    private static bool IsBetter(in Candidate candidate, in Candidate best)
    {
        int evidence = DecodeEvidence.RankOf(candidate.Quality);
        int bestEvidence = DecodeEvidence.RankOf(best.Quality);
        return evidence != bestEvidence
            ? evidence > bestEvidence
            : Math.Abs(candidate.ResidualHz) < Math.Abs(best.ResidualHz);
    }

    private static bool IsSameFrame(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b);
}
