namespace Packet.SoundModem.Audio;

/// <summary>
/// The receive level over the last few seconds, in cells of half a millisecond, so that a level
/// can be quoted for one decoded frame rather than for whatever the meter's interval covered.
/// </summary>
/// <remarks>
/// <para><b>Why this exists alongside <see cref="InputLevelMeter"/></b> (Tom, 2026-09-06): "it's
/// actually very hard to use the volume meter to check the received audio level of frames when
/// the frames are incredibly short on the fast modes". The meter reads the whole input five times
/// a second, which is a useful thing to point a capture-gain slider at and a useless thing to ask
/// about a qpsk3600 frame that is over in a fraction of one of its intervals - and on an FM radio
/// with the squelch open the noise between the frames is louder than the frames, so the meter's
/// bar is a reading of the hiss.</para>
/// <para><b>Cells, not a running peak.</b> Each cell holds the loudest magnitude in its half
/// millisecond of audio and whether the card clipped during it, and the ring holds
/// <see cref="MemorySeconds"/> of them. That is what lets a caller come back after a frame has
/// decoded and ask about the stretch of audio the frame occupied, which is the whole point: the
/// question is always asked in arrears.</para>
/// <para><b>Where the two halves are measured</b> is the same split, and for the same reason, as
/// <see cref="InputLevelMeter"/>: the peak comes from the audio the modems hear (the channel's
/// own rate, past the decimator on a 48 kHz card) and the clip flag from the card's own samples,
/// because the decimating FIR's ripple moves peaks either way and "the converter ran out of
/// codes" is only a fact before it. <see cref="NoteCardClipping"/> takes one block of card
/// samples and <see cref="Add"/> the channel-rate block that came out of it.</para>
/// <para><b>Cost.</b> One absolute value and one comparison per channel-rate sample, one
/// comparison per card-rate sample, no allocation after construction and no LINQ. Single
/// threaded, like the receive path it hangs off.</para>
/// </remarks>
public sealed class InputLevelHistory
{
    /// <summary>How much audio one cell covers.</summary>
    /// <remarks>
    /// <para><b>Half a millisecond, because only whole cells inside a frame's span are read</b>
    /// and the shortest frames are very short indeed. A 15-byte supervisory frame - RR, RNR,
    /// REJ, UA, DM, SABM, the most common frame on a working link - is 7.9 ms of air on
    /// c4fsk19200 and 42 ms on qpsk3600, so a cell of 10 ms threw away more than the reading
    /// contained and those frames got no level at all. That is what the bench found on radio1:
    /// seven real GB7RDG frames in ten minutes, every one of them 15 bytes, every one of them
    /// with no figure. Rounding inwards now costs under a millisecond in total.</para>
    /// <para>Half a millisecond is 6 samples at 12 kHz and 24 at 48 kHz. A single cell's peak is
    /// therefore a coarse thing on the slowest modes - six samples of a 1200 Hz tone can miss its
    /// crest by a fraction of a dB - but a reading is the largest of every cell in the span, and
    /// the shortest span here is ten cells, so what is reported is the crest all the same.</para>
    /// <para><b>Cost.</b> The ring is <see cref="MemorySeconds"/> of cells whatever the rate:
    /// 24000 of them, 120 KB per channel, preallocated once and never grown. That is the same
    /// order as the FFT buffers the waterfall keeps per channel beside it.</para>
    /// </remarks>
    public const double CellMilliseconds = 0.5;

    /// <summary>How far back the ring remembers.</summary>
    /// <remarks>
    /// Long enough to hold the longest frame anything here sends whole: a 256-byte frame at 300
    /// bps is nearly seven seconds of air, and a window that ran off the end of the ring would
    /// quietly measure the last part of a burst as though it were all of it.
    /// </remarks>
    public const double MemorySeconds = 12;

    /// <summary>
    /// How finely one block of card samples is positioned within the channel block it became.
    /// </summary>
    /// <remarks>
    /// The two blocks are the same stretch of audio at two rates, and the card block arrives
    /// first, so a clip is remembered as which sixty-fourth of the block it happened in and
    /// placed into cells when the channel block that matches it arrives. Sixty-fourths of a
    /// 100 ms block is 1.6 ms, finer than the cells it is being placed into, which is all this
    /// has to be. It fits in one <c>ulong</c> and costs nothing when nothing clipped.
    /// </remarks>
    private const int ClipSlots = 64;

    private readonly float[] _peak;
    private readonly bool[] _clipped;
    private readonly int _cellSamples;
    private readonly int _cells;
    private long _samples;
    private ulong _pendingClip;
    private bool _cardSeen;

    /// <summary>Creates a history for audio at <paramref name="sampleRate"/>.</summary>
    /// <param name="sampleRate">The rate of the audio <see cref="Add"/> will be given.</param>
    public InputLevelHistory(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        _cellSamples = Math.Max(1, (int)Math.Round(sampleRate * CellMilliseconds / 1000));
        _cells = (int)Math.Ceiling(MemorySeconds * sampleRate / _cellSamples);
        _peak = new float[_cells];
        _clipped = new bool[_cells];
    }

    /// <summary>Samples in one cell.</summary>
    public int CellSamples => _cellSamples;

    /// <summary>How much audio has been taken in, in samples, which is the history's own clock.</summary>
    /// <remarks>
    /// Positions here are sample counts and not wall-clock times on purpose: everything that
    /// asks a question of this history is driven by the same audio, so counting the audio is
    /// exact where a clock would be one scheduling delay out.
    /// </remarks>
    public long Position => _samples;

    /// <summary>Takes one block of the audio the modems hear.</summary>
    /// <param name="samples">The block, nominally -1 to +1, at the channel's rate.</param>
    public void Add(ReadOnlySpan<float> samples)
    {
        long blockStart = _samples;
        int offset = 0;
        while (offset < samples.Length)
        {
            long cell = _samples / _cellSamples;
            int index = (int)(cell % _cells);
            if (_samples == cell * _cellSamples)
            {
                // The first sample of a cell, so whatever this slot held is a lap of the ring old.
                _peak[index] = 0;
                _clipped[index] = false;
            }

            int take = Math.Min((int)((cell + 1) * _cellSamples - _samples), samples.Length - offset);
            float peak = _peak[index];
            foreach (float sample in samples.Slice(offset, take))
            {
                float magnitude = Math.Abs(sample);
                if (magnitude > peak)
                {
                    peak = magnitude;
                }
            }

            _peak[index] = peak;
            _samples += take;
            offset += take;
        }

        ApplyPendingClip(blockStart, samples.Length);
    }

    /// <summary>
    /// Takes one block of audio exactly as the card delivered it, for the clip flag alone, and
    /// before the <see cref="Add"/> of the block it becomes.
    /// </summary>
    /// <remarks>
    /// One call describes one block, so a call replaces whatever the last one left rather than
    /// adding to it: a block the receive path then throws away (the half-duplex gate closing
    /// mid-block) must not have its clip land on the next one.
    /// </remarks>
    /// <param name="samples">The block as the device delivered it, at the card's own rate.</param>
    public void NoteCardClipping(ReadOnlySpan<float> samples)
    {
        _cardSeen = true;
        ulong slots = 0;
        for (int n = 0; n < samples.Length; n++)
        {
            if (InputLevelMeter.IsClipped(samples[n]))
            {
                slots |= 1UL << (int)((long)n * ClipSlots / samples.Length);
            }
        }

        _pendingClip = slots;
    }

    /// <summary>
    /// The loudest the audio got over a stretch of it, and whether the card clipped in that
    /// stretch.
    /// </summary>
    /// <param name="fromSample">Start of the stretch, on <see cref="Position"/>'s scale.</param>
    /// <param name="toSample">One past its end, on the same scale.</param>
    /// <param name="peakDbFs">The loudest cell in it, in dBFS - clamped to the scale's ends by
    /// <see cref="InputLevelMeter.DbFs"/>, so nothing reads above 0.</param>
    /// <param name="clipped">Whether the card railed anywhere in it, or null on a station whose
    /// card samples nothing is handing over - "not measured" rather than "no".</param>
    /// <returns>False when the stretch is empty, still ahead of the audio, or wholly off the back
    /// of the ring, in which case the caller has no measurement rather than a bad one. A stretch
    /// that only partly runs off the back is measured over the cells that are left, which is the
    /// honest reading of the part still remembered.</returns>
    public bool TryMeasure(long fromSample, long toSample, out double peakDbFs, out bool? clipped)
    {
        peakDbFs = 0;
        clipped = null;
        if (_samples == 0)
        {
            return false;
        }

        // Cells wholly INSIDE the stretch, not every cell that overlaps it. A cell that
        // straddles either edge is part this stretch and part whatever is next to it, and since
        // the answer is a peak, 20 samples of something louder hanging over the edge takes the
        // whole reading (measured: a bpsk300 frame read -0.9 dBFS - the tone beside it - because
        // the last cell of its window ran 20 samples past the burst). Rounding inwards costs at
        // most one cell at each end of a stretch that is many.
        long newest = (_samples - 1) / _cellSamples;
        long oldest = Math.Max(0, newest - _cells + 1);
        long from = Math.Max(0, fromSample);
        long first = Math.Max(oldest, (from + _cellSamples - 1) / _cellSamples);
        long last = Math.Min(newest, (toSample / _cellSamples) - 1);
        if (toSample <= fromSample || last < first)
        {
            return false;
        }

        float peak = 0;
        bool railed = false;
        for (long cell = first; cell <= last; cell++)
        {
            int index = (int)(cell % _cells);
            if (_peak[index] > peak)
            {
                peak = _peak[index];
            }

            railed |= _clipped[index];
        }

        peakDbFs = InputLevelMeter.DbFs(peak);
        clipped = _cardSeen ? railed : null;
        return true;
    }

    /// <summary>
    /// Places the clip flags of the card block just gone into the cells of the channel block that
    /// came out of it - the two cover the same stretch of audio, so a flag from slot <c>k</c> of
    /// the one lands on the same fraction of the other.
    /// </summary>
    private void ApplyPendingClip(long blockStart, int blockSamples)
    {
        ulong slots = _pendingClip;
        _pendingClip = 0;
        if (slots == 0 || blockSamples == 0)
        {
            return;
        }

        for (int slot = 0; slot < ClipSlots; slot++)
        {
            if ((slots & (1UL << slot)) == 0)
            {
                continue;
            }

            long from = blockStart + ((long)slot * blockSamples / ClipSlots);
            // At least one sample wide, for a block shorter than the sixty-four slots it is
            // being divided into, where several slots land on the same sample.
            long to = Math.Max(from + 1, blockStart + (((long)slot + 1) * blockSamples / ClipSlots));
            for (long sample = from; sample < to; sample += _cellSamples)
            {
                _clipped[(int)(sample / _cellSamples % _cells)] = true;
            }

            // The slot's last sample, in case the slot is shorter than a cell and the step above
            // stepped straight over the cell boundary it sits on.
            _clipped[(int)((to - 1) / _cellSamples % _cells)] = true;
        }
    }
}
