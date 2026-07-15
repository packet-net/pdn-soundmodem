using Packet.SoundModem.Hdlc;

namespace Packet.SoundModem.Modems;

/// <summary>
/// UZ7HO-style multi-decoder AFSK: 2·pairs+1 parallel demodulators spaced at
/// <c>offsetHz</c> steps around the channel centre (SoundModem's celebrated
/// off-frequency tolerance — QtSoundModem runs up to 16 such decoders per channel at
/// 30 Hz spacing), with content-based deduplication across the bank. Off-tune signals
/// and tone-imbalanced transmitters decode on whichever branch fits best. Transmit uses
/// the centre frequency only.
/// </summary>
public sealed class Afsk1200MultiModem : IModem
{
    private readonly Afsk1200Demodulator[] _demodulators;
    private readonly Afsk1200Modulator _modulator;
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
    public Afsk1200MultiModem(
        int sampleRate,
        Action<byte[]> frameReceived,
        int offsetPairs = 2,
        double offsetHz = 30,
        double centerFrequency = 1700)
    {
        ArgumentNullException.ThrowIfNull(frameReceived);
        ArgumentOutOfRangeException.ThrowIfNegative(offsetPairs);
        _frameReceived = frameReceived;
        _deduper = new FrameDeduper(3L * sampleRate);
        _dedupeChunk = sampleRate / 10;
        _modulator = new Afsk1200Modulator(sampleRate);

        _demodulators = new Afsk1200Demodulator[2 * offsetPairs + 1];
        for (int i = 0; i < _demodulators.Length; i++)
        {
            int step = i - offsetPairs;
            var deframer = new HdlcDeframer(OnFrame);
            var nrzi = new NrziDecoder();
            _demodulators[i] = new Afsk1200Demodulator(
                sampleRate,
                level => deframer.PushBit(nrzi.Decode(level)),
                centerFrequency + step * offsetHz);
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
            foreach (Afsk1200Demodulator demodulator in _demodulators)
            {
                demodulator.Process(slice);
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
