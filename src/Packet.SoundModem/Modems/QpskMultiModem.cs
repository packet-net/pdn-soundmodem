namespace Packet.SoundModem.Modems;

/// <summary>
/// Frequency-diversity QPSK: 2·<c>offsetPairs</c>+1 parallel <see cref="QpskModem"/> branches
/// spaced <c>offsetHz</c> apart around the channel centre, with content-based deduplication
/// across the bank - <see cref="BpskMultiModem"/>'s arrangement applied to the NinoTNC QPSK
/// family (issue #326), which had run as a single modem while both BPSK modes and afsk300 had
/// banks.
/// </summary>
/// <remarks>
/// <para>
/// What the bank buys differs by detector, as it does for BPSK. The <b>coherent</b> Costas loop
/// pulls in only a few hertz within a short preamble, so a branch near the carrier is the only
/// way it acquires an off-frequency station. The <b>differential</b> detector (the default)
/// tracks a residual of up to one branch step on any branch and seeds the far offsets from its
/// offset window, so for it the bank is selection diversity: every signal is decided by several
/// branches whose noise-driven decision errors are quasi-independent, and the best-centred copy
/// wins. The reported carrier offset is the winning branch's step plus that branch's own
/// measurement of the residual, or null where no branch could measure (see
/// <see cref="EmitBestOfChunk"/>). Transmit uses the centre branch only.
/// </para>
/// <para>
/// The step is baud/40 by default, the BPSK bank's sizing, which is also exactly the tracker
/// clamp in <see cref="QpskDemodulator"/>: adjacent branches overlap by a full step, so any
/// residual is tracked by at least two of them. qpsk600 therefore covers ±30 Hz on 4 pairs and
/// qpsk2400 ±120 Hz, with each branch's per-burst seed reaching a further ±baud/8 beyond the
/// comb when the signal is strong enough to seed. CPU scales linearly with the branch count;
/// qpsk3600 runs its decode chain threefold upsampled and arrives through FM, where audio tones
/// land on frequency whatever the RF offset, so the catalogue gives it no offset pairs by
/// default (a one-branch bank is a plain modem).
/// </para>
/// </remarks>
public sealed class QpskMultiModem : IModem, IConstellationSource
{
    private readonly QpskModem[] _branches;
    private readonly QpskModem _transmit;
    private readonly Action<byte[]> _frameReceived;
    private readonly FrameDeduper _deduper;
    private readonly int _dedupeChunk;
    private readonly int _bitRate;
    private readonly bool _crc;
    private readonly double _stepHz;
    private readonly int _offsetPairs;
    private readonly int _perPosition;
    private readonly List<Candidate> _candidates = [];
    private long _samplesProcessed;
    private bool _carrierWasPresent;

    /// <summary>One branch's copy of a frame, held until the chunk ends and the branches can be
    /// compared. <paramref name="ResidualHz"/> is that branch's own measurement of how far the
    /// signal sat from <em>its</em> centre - null where the branch could not measure it - so
    /// branch + residual is the station's offset from the bank's centre however far out the
    /// branch that copied it happened to be.</summary>
    private readonly record struct Candidate(
        byte[] Frame, double BranchOffsetHz, double? ResidualHz, FrameQuality Quality);

    /// <summary>Creates the bank.</summary>
    /// <param name="sampleRate">Channel DSP rate.</param>
    /// <param name="frameReceived">Receives each unique decoded AX.25 frame once.</param>
    /// <param name="crc">IL2P+CRC mode (both stations must agree). On for NinoTNC networks.</param>
    /// <param name="centreFrequency">Channel centre - the middle branch and the TX carrier.</param>
    /// <param name="baud">Symbol rate: 300 (qpsk600, NinoTNC mode 9), 1200 (qpsk2400, mode 11)
    /// or 1800 (qpsk3600, mode 5). Each branch is that mode's own <see cref="QpskModem"/>
    /// factory, so the per-mode roll-off, loop bandwidth and detector configuration travel with
    /// it.</param>
    /// <param name="offsetPairs">Extra branches either side of centre (0 = a single branch,
    /// i.e. a plain <see cref="QpskModem"/>).</param>
    /// <param name="offsetHz">Frequency step between adjacent branches; defaults to baud/40,
    /// the differential detector's tracker range.</param>
    /// <param name="detector">Differential (default) or coherent detection.</param>
    /// <param name="acceptPlainIl2p">Pass frames that arrive as plain IL2P, with no trailing CRC,
    /// to <paramref name="frameReceived"/> as well as reporting them (off by default, and inert
    /// unless <paramref name="crc"/> is on). Every branch reads both ways; the bank's content
    /// dedupe is what keeps a transmission two branches read differently to one delivery.</param>
    /// <param name="secondDetector">When set (and different from <paramref name="detector"/>),
    /// every offset position gets a second branch under this detector and the bank delivers
    /// the UNION of what the two detectors decode - the ensemble decode-any of rx-roadmap
    /// workstream 2, bought with the existing content dedupe. Coherent or differential; the
    /// MLSE stage is BPSK-only and <see cref="QpskDemodulator"/> refuses it. Doubles the bank's
    /// CPU; null (the default) keeps the single-detector bank.</param>
    public QpskMultiModem(
        int sampleRate, Action<byte[]> frameReceived, bool crc = true,
        double centreFrequency = 1500, int baud = 300, int offsetPairs = 4,
        double? offsetHz = null, PskDetector detector = PskDetector.Differential,
        bool acceptPlainIl2p = false, PskDetector? secondDetector = null)
    {
        ArgumentNullException.ThrowIfNull(frameReceived);
        ArgumentOutOfRangeException.ThrowIfNegative(offsetPairs);
        if (baud is not (300 or 1200 or 1800))
        {
            throw new ArgumentOutOfRangeException(
                nameof(baud), baud, "QPSK symbol rate must be 300, 1200 or 1800 (qpsk600/2400/3600)");
        }

        _frameReceived = frameReceived;
        _bitRate = baud * 2;
        _crc = crc;
        _dedupeChunk = Math.Max(1, sampleRate / 10);
        // The dedupe window only has to span the skew between branches delivering copies of ONE
        // transmission, and those cluster tightly: measured across the widest bank the catalogue
        // builds (4 offset pairs under both detectors, issue #342's probe), the copies of a burst
        // spread at most 130 samples at 12 kHz, nearly all of it the coherent-differential
        // detector gap. Add up to one feed slice of clock quantisation, because candidates are
        // stamped at slice ends and a straddling pair lands one slice (at most _dedupeChunk)
        // apart, and the worst case is a little over one chunk. Two chunks covers it roughly
        // twice over, and two DISTINCT transmissions of the same bytes deliver at least one
        // burst-time apart, which stays outside this window for every mode at any realistic
        // TXDELAY (only a minimal frame behind an aggressively short preamble on the fastest
        // mode can land two bursts inside 200 ms, and the acquisition clear below delivers
        // even that case). The previous 3 s constant was wider than any ARQ retry interval
        // and swallowed byte-identical retransmissions whole (issue #342). The deduper is
        // additionally cleared whenever the bank re-acquires a carrier (see Process): a
        // burst that arrives after the carrier dropped is a new transmission whatever the
        // clock says.
        _deduper = new FrameDeduper(2L * _dedupeChunk);
        double step = offsetHz ?? baud / 40.0;
        _stepHz = step;

        int positions = (2 * offsetPairs) + 1;
        int perPosition = secondDetector is not null && secondDetector != detector ? 2 : 1;
        _offsetPairs = offsetPairs;
        _perPosition = perPosition;
        _branches = new QpskModem[positions * perPosition];
        for (int i = 0; i < positions; i++)
        {
            double offset = (i - offsetPairs) * step;
            // Drive everything off FrameDecoded (which carries the CRC/FEC quality); the required
            // frame sink is a no-op so each decode reaches the deduper exactly once.
            _branches[i * perPosition] = Branch(
                sampleRate, crc, baud, centreFrequency + offset, detector, acceptPlainIl2p);
            _branches[i * perPosition].FrameDecoded += (frame, quality) => OnFrame(frame, offset, quality);
            if (perPosition == 2)
            {
                // The ensemble twin: the same position under the second detector. The bank's
                // content dedupe already reduces N copies of a transmission to one delivery, so
                // the union comes for exactly one more branch's CPU per position.
                _branches[(i * perPosition) + 1] = Branch(
                    sampleRate, crc, baud, centreFrequency + offset, secondDetector!.Value, acceptPlainIl2p);
                _branches[(i * perPosition) + 1].FrameDecoded += (frame, quality) => OnFrame(frame, offset, quality);
            }
        }

        _transmit = _branches[offsetPairs * perPosition]; // the centre (offset 0) primary branch
    }

    /// <summary>Creates the 600 bps bank (NinoTNC mode 9) around <paramref name="carrierFrequency"/>.</summary>
    public static QpskMultiModem Qpsk600(
        int sampleRate, Action<byte[]> frameReceived, bool crc = true,
        PskDetector detector = PskDetector.Differential, double carrierFrequency = 1500,
        int offsetPairs = 4) =>
        new(sampleRate, frameReceived, crc, carrierFrequency, 300, offsetPairs, detector: detector);

    /// <summary>Creates the 2400 bps bank (NinoTNC mode 11) around <paramref name="carrierFrequency"/>.</summary>
    public static QpskMultiModem Qpsk2400(
        int sampleRate, Action<byte[]> frameReceived, bool crc = true,
        PskDetector detector = PskDetector.Differential, double carrierFrequency = 1500,
        int offsetPairs = 4) =>
        new(sampleRate, frameReceived, crc, carrierFrequency, 1200, offsetPairs, detector: detector);

    /// <summary>Creates the 3600 bps bank (NinoTNC mode 5) around <paramref name="carrierFrequency"/>.
    /// No offset pairs by default: the mode arrives through FM, where the audio tones land on
    /// frequency whatever the RF offset, and its threefold-upsampled decode chain makes each
    /// branch three times the cost of a qpsk2400 one.</summary>
    public static QpskMultiModem Qpsk3600(
        int sampleRate, Action<byte[]> frameReceived, bool crc = true,
        PskDetector detector = PskDetector.Differential, double carrierFrequency = 1650,
        int offsetPairs = 0) =>
        new(sampleRate, frameReceived, crc, carrierFrequency, 1800, offsetPairs, detector: detector);

    /// <inheritdoc />
    public event Action<byte[], FrameQuality>? FrameDecoded;

    /// <inheritdoc />
    /// <remarks>The centre branch's view - see <see cref="BpskMultiModem.SymbolPlotted"/>.</remarks>
    public event Action<ConstellationPoint>? SymbolPlotted
    {
        add => _transmit.SymbolPlotted += value;
        remove => _transmit.SymbolPlotted -= value;
    }

    /// <inheritdoc />
    public string Mode => $"qpsk{_bitRate}{(_crc ? "-il2pc" : "-il2p")}-multi{_branches.Length}";

    /// <inheritdoc />
    public bool CarrierDetect
    {
        get
        {
            foreach (QpskModem branch in _branches)
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
            foreach (QpskModem branch in _branches)
            {
                if (branch.ChannelBusy)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// How far the current signal sits from the bank's centre, in Hz (positive = above it), or
    /// null when no branch has anything coherent enough to measure. The reading is the
    /// best-matched branch's - smallest own residual, exactly the rule
    /// <see cref="EmitBestOfChunk"/> uses to pick a decoded frame's reported offset - as that
    /// branch's step plus its residual. Decoded frames already carry their reading in
    /// <see cref="FrameQuality.FrequencyOffsetHz"/>; this property is for the bursts that never
    /// decode, polled while <see cref="CarrierDetect"/> holds.
    /// </summary>
    public double? CarrierOffsetHz
    {
        get
        {
            double? best = null;
            double bestResidual = double.MaxValue;
            for (int i = 0; i < _branches.Length; i++)
            {
                if (_branches[i].CarrierOffsetHz is { } residual
                    && Math.Abs(residual) < bestResidual)
                {
                    bestResidual = Math.Abs(residual);
                    best = (((i / _perPosition) - _offsetPairs) * _stepHz) + residual;
                }
            }

            return best;
        }
    }

    /// <summary>Cumulative sync-found-but-Reed-Solomon-failed count summed across the bank - a
    /// bank-level event count, not a transmission count; see
    /// <see cref="BpskMultiModem.RsFailures"/> for the caveat in full.</summary>
    public long RsFailures
    {
        get
        {
            long total = 0;
            foreach (QpskModem branch in _branches)
            {
                total += branch.RsFailures;
            }

            return total;
        }
    }

    /// <summary>Cumulative recovered-but-trailing-CRC-refused count summed across the bank -
    /// the same bank-level-event caveat as <see cref="RsFailures"/> applies.</summary>
    public long CrcFailures
    {
        get
        {
            long total = 0;
            foreach (QpskModem branch in _branches)
            {
                total += branch.CrcFailures;
            }

            return total;
        }
    }

    /// <inheritdoc />
    public void Process(ReadOnlySpan<float> samples)
    {
        // Feed the bank in bounded chunks so the dedupe clock advances with the audio even when a
        // caller hands over one huge buffer - otherwise a legitimate repeat later in the same
        // buffer would be suppressed (mirrors BpskMultiModem).
        for (int position = 0; position < samples.Length; position += _dedupeChunk)
        {
            var slice = samples.Slice(position, Math.Min(_dedupeChunk, samples.Length - position));
            foreach (QpskModem branch in _branches)
            {
                branch.Process(slice);
            }

            _samplesProcessed += slice.Length;
            EmitBestOfChunk();

            // An acquisition boundary: the carrier dropped and came back, so whatever the
            // deduper remembers belongs to an earlier transmission and must not suppress this
            // one - which is what lets a retransmission through however hard the far end leans
            // on its timers. Checked after the emit so a copy of the previous burst still in
            // flight this slice is merged before the memory goes. The edge can only appear at
            // a burst's start (the OR over the branches rises once and holds), and no decode
            // of that burst can precede its own preamble, so a clear never splits one
            // transmission's copies.
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
        foreach (QpskModem branch in _branches)
        {
            branch.ResetCarrierState();
        }
    }

    private static QpskModem Branch(
        int sampleRate, bool crc, int baud, double carrier, PskDetector detector, bool acceptPlainIl2p) =>
        baud switch
        {
            300 => QpskModem.Qpsk600(
                sampleRate, static _ => { }, crc, detector: detector, carrierFrequency: carrier,
                acceptPlainIl2p: acceptPlainIl2p),
            1200 => QpskModem.Qpsk2400(
                sampleRate, static _ => { }, crc, detector: detector, carrierFrequency: carrier,
                acceptPlainIl2p: acceptPlainIl2p),
            _ => QpskModem.Qpsk3600(
                sampleRate, static _ => { }, crc, detector: detector, carrierFrequency: carrier,
                acceptPlainIl2p: acceptPlainIl2p),
        };

    // Several branches usually decode the same transmission within a frame-time of each other,
    // which is well inside one chunk. Hold them all and let the chunk end decide, rather than
    // emitting whichever finished first.
    private void OnFrame(byte[] frame, double offsetHz, FrameQuality quality) =>
        _candidates.Add(new Candidate(frame, offsetHz, quality.FrequencyOffsetHz, quality));

    /// <summary>
    /// Emits one frame per distinct transmission seen this chunk, from the branch that was
    /// actually tuned closest to it - <see cref="BpskMultiModem.EmitBestOfChunk"/>'s rule, and
    /// its reason: branches are fed in array order and several copy the same frame, so "first to
    /// finish" is a comb position, not a measurement (issue #202). The branches are compared on
    /// each one's own <see cref="QpskModem.CarrierOffsetHz"/>, the smallest residual wins, and
    /// where no branch could measure the offset is reported as <c>null</c>: "we did not measure
    /// it" is the honest answer, and the comb position is not a substitute for one.
    /// </summary>
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
            // do with it; IsBetter has already preferred a deliverable copy where one exists.
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
    /// FEC work then by distance from the bank centre - anything but array order. See
    /// <see cref="BpskMultiModem"/> for why evidence outranks centring.</summary>
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
