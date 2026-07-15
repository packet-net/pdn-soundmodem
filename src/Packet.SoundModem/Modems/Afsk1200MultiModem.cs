using Packet.SoundModem.Hdlc;

namespace Packet.SoundModem.Modems;

/// <summary>
/// UZ7HO-style multi-decoder AFSK: 2·pairs+1 parallel demodulators spaced at
/// <c>offsetHz</c> steps around the channel centre (SoundModem's celebrated
/// off-frequency tolerance — QtSoundModem runs up to 16 such decoders per channel at
/// 30 Hz spacing), optionally multiplied by three emphasis variants (flat, +6 dB/oct,
/// +12 dB/oct input pre-filters — QtSM's <c>emph_all</c>, the counter to real-world
/// pre/de-emphasis "twist" where one tone arrives much weaker than the other), with
/// content-based deduplication across the bank. Off-tune and tone-imbalanced
/// transmitters decode on whichever branch fits best. Transmit uses the centre
/// frequency only.
/// </summary>
public sealed class Afsk1200MultiModem : IModem
{
    private readonly Afsk1200Demodulator[] _demodulators;
    private readonly EmphasisFilter[] _preFilters;
    private readonly Afsk1200Modulator _modulator;
    private float[] _scratch = [];
    private readonly Action<byte[]> _frameReceived;
    private readonly FrameDeduper _deduper;
    private readonly int _dedupeChunk;
    private long _samplesProcessed;

    /// <summary>Creates the bank.</summary>
    /// <param name="sampleRate">Channel DSP rate.</param>
    /// <param name="frameReceived">Receives each unique decoded AX.25 frame once.</param>
    /// <param name="offsetPairs">Extra decoder pairs either side of centre (0 = single
    /// decoder; QtSoundModem default spacing is 30 Hz).</param>
    /// <param name="offsetHz">Frequency step between adjacent decoders.</param>
    /// <param name="centerFrequency">Channel centre for the middle decoder and TX.</param>
    /// <param name="emphasisVariants">Run each frequency branch three times — flat,
    /// +6 dB/oct and +12 dB/oct input pre-emphasis — to decode tone-imbalanced
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
        _modulator = new Afsk1200Modulator(sampleRate);

        int emphasisCount = emphasisVariants ? 3 : 1;
        int frequencyCount = 2 * offsetPairs + 1;
        _demodulators = new Afsk1200Demodulator[frequencyCount * emphasisCount];
        _preFilters = new EmphasisFilter[_demodulators.Length];
        for (int i = 0; i < _demodulators.Length; i++)
        {
            int step = (i % frequencyCount) - offsetPairs;
            var deframer = new HdlcDeframer(OnFrame);
            var nrzi = new NrziDecoder();
            _demodulators[i] = new Afsk1200Demodulator(
                sampleRate,
                level => deframer.PushBit(nrzi.Decode(level)),
                centerFrequency + step * offsetHz);
            _preFilters[i] = new EmphasisFilter(i / frequencyCount);
        }
    }

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
        // advances with the audio even when a caller hands over one huge buffer —
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
        }
    }

    /// <inheritdoc />
    public float[] Modulate(ReadOnlySpan<byte> ax25Frame, int txDelayMilliseconds)
    {
        int openingFlags = Math.Max(2, (int)(txDelayMilliseconds * 1200L / (8 * 1000)));
        return _modulator.Modulate(HdlcFramer.FrameBits(ax25Frame, openingFlags, closingFlags: 2));
    }

    /// <inheritdoc />
    public void ResetCarrierState()
    {
        foreach (Afsk1200Demodulator demodulator in _demodulators)
        {
            demodulator.ResetCarrierState();
        }
    }

    /// <summary>Streaming first-difference pre-emphasis of order 0/1/2 (flat, +6, +12
    /// dB/oct) — QtSM applies exactly these as parallel decode attempts.</summary>
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

    private void OnFrame(byte[] frame)
    {
        // Several branches usually decode the same transmission within a frame-time of
        // each other; emit the first and drop content-identical repeats in the window.
        if (_deduper.ShouldEmit(frame, _samplesProcessed))
        {
            _frameReceived(frame);
        }
    }
}
