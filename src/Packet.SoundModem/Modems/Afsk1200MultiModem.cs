using Packet.SoundModem.Hdlc;

namespace Packet.SoundModem.Modems;

/// <summary>
/// UZ7HO-style multi-decoder AFSK: 2·pairs+1 parallel demodulators spaced at
/// <c>offsetHz</c> steps around the channel centre (SoundModem's celebrated
/// off-frequency tolerance - QtSoundModem runs up to 16 such decoders per channel at
/// 30 Hz spacing), optionally multiplied by three emphasis variants (flat, +6 dB/oct,
/// +12 dB/oct input pre-filters - QtSM's <c>emph_all</c>, the counter to real-world
/// pre/de-emphasis "twist" where one tone arrives much weaker than the other), with
/// content-based deduplication across the bank. Off-tune and tone-imbalanced
/// transmitters decode on whichever branch fits best. Transmit uses the centre
/// frequency only.
/// </summary>
public sealed class Afsk1200MultiModem : IModem
{
    /// <summary>Bell 202 deviation of each tone from the centre (mark = centre − 500,
    /// space = centre + 500); the demodulators' default shift.</summary>
    private const double Bell202ToneShift = 500;

    private readonly AfskDemodulator[] _demodulators;
    private readonly EmphasisFilter[] _preFilters;
    private readonly AfskModulator _modulator;
    private float[] _scratch = [];
    private readonly Action<byte[]> _frameReceived;
    private readonly FrameDeduper _deduper;
    private readonly int _dedupeChunk;
    private readonly List<Candidate> _candidates = [];
    private long _samplesProcessed;

    /// <summary>One branch's copy of a frame, held until the chunk ends and the branches can
    /// be compared. <paramref name="ResidualHz"/> is that branch's demodulator's own
    /// measurement of how far the signal sat from <em>its</em> centre, so branch + residual
    /// is the station's offset from the bank's centre however far out the branch that copied
    /// it happened to be (the issue #202 model, as in <see cref="Afsk300MultiModem"/>).</summary>
    private readonly record struct Candidate(
        byte[] Frame, double BranchOffsetHz, double ResidualHz, int EmphasisDb);

    /// <summary>Creates the bank.</summary>
    /// <param name="sampleRate">Channel DSP rate.</param>
    /// <param name="frameReceived">Receives each unique decoded AX.25 frame once.</param>
    /// <param name="offsetPairs">Extra decoder pairs either side of centre (0 = single
    /// decoder; QtSoundModem default spacing is 30 Hz).</param>
    /// <param name="offsetHz">Frequency step between adjacent decoders.</param>
    /// <param name="centerFrequency">Channel centre for the middle decoder and TX.</param>
    /// <param name="emphasisVariants">Run each frequency branch three times - flat,
    /// +6 dB/oct and +12 dB/oct input pre-emphasis - to decode tone-imbalanced
    /// transmitters (QtSM's emph_all). Triples CPU for a large real-world decode gain.</param>
    public Afsk1200MultiModem(
        int sampleRate,
        Action<byte[]> frameReceived,
        int offsetPairs = 2,
        double offsetHz = 30,
        double centerFrequency = 1700,
        bool emphasisVariants = true)
    {
        ArgumentNullException.ThrowIfNull(frameReceived);
        ArgumentOutOfRangeException.ThrowIfNegative(offsetPairs);
        _frameReceived = frameReceived;
        _deduper = new FrameDeduper(3L * sampleRate);
        _dedupeChunk = sampleRate / 10;
        _modulator = new AfskModulator(
            sampleRate, 1200, centerFrequency - Bell202ToneShift, centerFrequency + Bell202ToneShift);

        int emphasisCount = emphasisVariants ? 3 : 1;
        int frequencyCount = 2 * offsetPairs + 1;
        _demodulators = new AfskDemodulator[frequencyCount * emphasisCount];
        _preFilters = new EmphasisFilter[_demodulators.Length];
        for (int i = 0; i < _demodulators.Length; i++)
        {
            int step = (i % frequencyCount) - offsetPairs;
            double offset = step * offsetHz;
            int emphasisDb = (i / frequencyCount) * 6;   // EmphasisFilter variants: 0/+6/+12 dB/oct
            int branch = i;
            var deframer = new HdlcDeframer(frame => OnFrame(frame, offset, emphasisDb, branch));
            var nrzi = new NrziDecoder();
            _demodulators[i] = new AfskDemodulator(
                sampleRate,
                level => deframer.PushBit(nrzi.Decode(level)),
                centerFrequency + step * offsetHz);
            _preFilters[i] = new EmphasisFilter(i / frequencyCount);
        }
    }

    /// <inheritdoc />
    public event Action<byte[], FrameQuality>? FrameDecoded;

    /// <inheritdoc />
    public string Mode => $"afsk1200-multi{_demodulators.Length}";

    /// <inheritdoc />
    public bool CarrierDetect => _demodulators.Any(d => d.CarrierDetect);

    /// <inheritdoc />
    public bool ChannelBusy => _demodulators.Any(d => d.ChannelBusy);

    /// <inheritdoc />
    public void Process(ReadOnlySpan<float> samples)
    {
        // Feed the bank in bounded chunks so the dedupe clock (_samplesProcessed)
        // advances with the audio even when a caller hands over one huge buffer -
        // otherwise a legitimate repeat later in the same buffer would be suppressed.
        int chunk = Math.Max(1, _dedupeChunk);
        for (int position = 0; position < samples.Length; position += chunk)
        {
            var slice = samples.Slice(position, Math.Min(chunk, samples.Length - position));
            if (_scratch.Length < slice.Length)
            {
                _scratch = new float[slice.Length];
            }

            for (int branch = 0; branch < _demodulators.Length; branch++)
            {
                var input = _preFilters[branch].Apply(slice, _scratch);
                _demodulators[branch].Process(input);
            }

            _samplesProcessed += slice.Length;
            EmitBestOfChunk();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The TXDELAY fill is <see cref="TrainingPreamble"/> zeros, not flags - the cold-start
    /// fix the single <see cref="Afsk1200Modem"/> and <see cref="Afsk300Modem"/> carry (an
    /// all-flags fill is 87.5 % one tone and trains a cold slicer poorly; see that class).
    /// This bank transmits the identical Bell 202 waveform, so it takes the identical fix.
    /// </remarks>
    public float[] Modulate(ReadOnlySpan<byte> ax25Frame, int txDelayMilliseconds) =>
        _modulator.Modulate(TrainingPreamble.Prepend(
            HdlcFramer.FrameBits(ax25Frame, openingFlags: 2, closingFlags: 2),
            txDelayMilliseconds, 1200));

    /// <inheritdoc />
    public void ResetCarrierState()
    {
        foreach (AfskDemodulator demodulator in _demodulators)
        {
            demodulator.ResetCarrierState();
        }
    }

    /// <summary>Streaming first-difference pre-emphasis of order 0/1/2 (flat, +6, +12
    /// dB/oct) - QtSM applies exactly these as parallel decode attempts.</summary>
    private sealed class EmphasisFilter(int order)
    {
        private float _previous1;
        private float _previous2;

        public ReadOnlySpan<float> Apply(ReadOnlySpan<float> input, float[] scratch)
        {
            if (order == 0)
            {
                return input;
            }

            for (int i = 0; i < input.Length; i++)
            {
                float x = input[i];
                float d1 = x - _previous1;
                _previous1 = x;
                if (order == 1)
                {
                    scratch[i] = d1;
                }
                else
                {
                    scratch[i] = d1 - _previous2;
                    _previous2 = d1;
                }
            }

            return scratch.AsSpan(0, input.Length);
        }
    }

    // Several branches usually decode the same transmission within a frame-time of each
    // other, which is well inside one chunk. Hold them all and let the chunk end decide,
    // rather than emitting whichever finished first.
    private void OnFrame(byte[] frame, double offsetHz, int emphasisDb, int branch) =>
        _candidates.Add(new Candidate(
            frame, offsetHz, _demodulators[branch].CarrierOffsetHz, emphasisDb));

    /// <summary>
    /// Emits one frame per distinct transmission seen this chunk, from the branch that was
    /// actually tuned closest to it.
    /// </summary>
    /// <remarks>
    /// The first branch to finish is the wrong one to report: branches are fed in ascending
    /// order and a clean signal decodes on branches well either side of it, so "first" means
    /// "lowest-centred branch that could still read it" - a construction-time comb position,
    /// not a measurement, and it reported −60 Hz for a GPSDO-locked station squarely on
    /// frequency. Ranking the branches on each one's own
    /// <see cref="AfskDemodulator.CarrierOffsetHz"/> residual instead is the issue #202 fix
    /// <see cref="BpskMultiModem"/> and <see cref="Afsk300MultiModem"/> already carry;
    /// <c>branch + residual</c> is the station's offset from the bank's centre. All copies
    /// here carry equal decode evidence (an HDLC frame is FCS-checked or it does not
    /// exist), but not equal <em>measurement</em> evidence: the emphasis pre-filters tilt
    /// the spectrum by design, which skews the discriminator midpoint the residual is read
    /// from - measured on a clean on-frequency signal, a +6 dB/oct branch 30 Hz out
    /// reported a near-zero residual and stole the win from the flat branch that was
    /// actually matched. So flat outranks emphasised, and the residual decides among
    /// equals. Measured matched-branch honesty is ~±12 Hz at 1200 baud (the envelope
    /// midpoint carries a small mark-duty bias Bell 202's 87.5 %-mark flags produce)
    /// against the ±60-90 Hz comb-position lie this replaces. The cost is a frame waiting
    /// for the end of its chunk: at most 100 ms.
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

            // Still deduped across chunks: a transmission straddling a chunk boundary
            // reaches here twice, and the window is what stops the second copy going out.
            if (!_deduper.ShouldEmit(best.Frame, _samplesProcessed))
            {
                continue;
            }

            _frameReceived(best.Frame);
            FrameDecoded?.Invoke(best.Frame, new FrameQuality(
                Mode, best.Frame.Length, CorrectedBytes: null, CrcValid: null,
                FrequencyOffsetHz: best.BranchOffsetHz + best.ResidualHz,
                EmphasisDb: best.EmphasisDb));
        }
    }

    private static bool IsBetter(in Candidate candidate, in Candidate best) =>
        candidate.EmphasisDb != best.EmphasisDb
            ? candidate.EmphasisDb < best.EmphasisDb
            : Math.Abs(candidate.ResidualHz) < Math.Abs(best.ResidualHz);

    private static bool IsSameFrame(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b);
}
