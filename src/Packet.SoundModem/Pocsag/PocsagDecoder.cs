using Packet.SoundModem.Dsp;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Pocsag;

/// <summary>
/// POCSAG receiver: baseband FSK audio → sliced bits → frame-sync hunt → batches of
/// BCH-corrected codewords → <see cref="PocsagPage"/>s. The front end is the direct-FSK
/// receive chain (low-pass, envelope-midpoint slicer, shared DPLL — see
/// <see cref="FskModem"/>); the deframer follows the batch structure of CCIR Radiopaging
/// Code No. 1, with behaviour cross-checked against multimon-ng's <c>pocsag.c</c>.
/// </summary>
/// <remarks>
/// Both polarities are accepted: sync is hunted as the frame-sync codeword and its
/// complement (multimon-ng's auto-polarity approach), and the detected sense is applied
/// for the rest of the transmission and reported on each page. Like multimon-ng, an
/// uncorrectable codeword abandons the batch (flushing any partial page as truncated)
/// and the decoder re-hunts sync — the next batch's frame-sync word recovers it. Message
/// codewords that arrive with no owning address codeword (sync acquired mid-page) are
/// discarded.
/// </remarks>
public sealed class PocsagDecoder
{
    private readonly int _baud;
    private readonly FirFilter _rxFilter;
    private readonly BitDpll _dpll;
    private readonly Action<PocsagPage> _pageReceived;
    private float _peakHigh;
    private float _peakLow;

    // Deframer: 32-bit shift register of raw sliced bits, batch slot counter.
    private uint _shift;
    private bool _synced;
    private bool _inverted;
    private int _bitCount;
    private int _slot;

    // The page being assembled.
    private bool _pageOpen;
    private uint _address;
    private int _function;
    private int _bitErrors;
    private readonly List<uint> _groups = [];

    /// <summary>Creates the decoder.</summary>
    /// <param name="sampleRate">Input sample rate (need not divide evenly by the baud).</param>
    /// <param name="pageReceived">Receives each decoded page.</param>
    /// <param name="baud">Bit rate: 512, 1200 (DAPNET) or 2400.</param>
    public PocsagDecoder(int sampleRate, Action<PocsagPage> pageReceived, int baud = 1200)
    {
        ArgumentNullException.ThrowIfNull(pageReceived);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(baud, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, baud * 4);
        _baud = baud;
        _pageReceived = pageReceived;
        double samplesPerBit = (double)sampleRate / baud;
        _rxFilter = new FirFilter(FilterDesign.LowPass(0.55 * baud, sampleRate, (int)(8 * samplesPerBit) | 1));
        _dpll = new BitDpll(baud, sampleRate, OnBit);
    }

    /// <summary>The mode label, e.g. "pocsag1200".</summary>
    public string Mode => $"pocsag{_baud}";

    /// <summary>Feeds baseband audio through the decoder.</summary>
    /// <param name="samples">Audio samples, −1…1.</param>
    public void Process(ReadOnlySpan<float> samples)
    {
        foreach (float sample in samples)
        {
            float filtered = _rxFilter.Next(sample);

            // Envelope-midpoint slicer (as the AFSK/FSK demods): tracks DC offset and
            // level without assuming a centred signal.
            _peakHigh += (filtered - _peakHigh) * (filtered > _peakHigh ? 0.08f : 0.0002f);
            _peakLow += (filtered - _peakLow) * (filtered < _peakLow ? 0.08f : 0.0002f);
            float excess = filtered - ((_peakHigh + _peakLow) * 0.5f);

            // POCSAG polarity: '0' is the high frequency, i.e. the positive baseband
            // level (multimon-ng likewise inverts its slicer output, pocsag.c
            // pocsag_rxbit). The sync hunt still catches reversed wiring.
            _dpll.Sample(excess > 0 ? 0 : 1);
        }
    }

    /// <summary>Flushes a partially assembled page (as truncated). Only useful at the
    /// hard end of an input — a live transmission always ends its pages with idle fill.</summary>
    public void Flush()
    {
        FlushPage(truncated: true);
        _synced = false;
        _slot = 0;
        _bitCount = 0;
    }

    private void OnBit(int bit)
    {
        _shift = (_shift << 1) | (uint)bit;

        if (!_synced)
        {
            // Hunt the frame-sync codeword at every bit position, in both polarities,
            // tolerating what the BCH can correct (multimon-ng does the same).
            uint candidate = _shift;
            if (PocsagCodeword.TryCorrect(ref candidate, out _) && candidate == PocsagCodeword.FrameSync)
            {
                AcquireSync(inverted: false);
                return;
            }

            candidate = ~_shift;
            if (PocsagCodeword.TryCorrect(ref candidate, out _) && candidate == PocsagCodeword.FrameSync)
            {
                AcquireSync(inverted: true);
            }

            return;
        }

        if (++_bitCount < 32)
        {
            return;
        }

        _bitCount = 0;
        ProcessWord(_inverted ? ~_shift : _shift);
    }

    private void AcquireSync(bool inverted)
    {
        _synced = true;
        _inverted = inverted;
        _bitCount = 0;
        _slot = 0;
    }

    private void ProcessWord(uint word)
    {
        if (_slot == 16)
        {
            // Between batches: the frame-sync codeword, or the transmission is over.
            _slot = 0;
            if (PocsagCodeword.TryCorrect(ref word, out _) && word == PocsagCodeword.FrameSync)
            {
                return;
            }

            FlushPage(truncated: true);
            _synced = false;
            return;
        }

        int frame = _slot >> 1;
        _slot++;

        if (!PocsagCodeword.TryCorrect(ref word, out int errors))
        {
            // ≥3 bit errors: abandon the batch and re-hunt (multimon-ng's policy) —
            // better than guessing at content the code can no longer vouch for.
            FlushPage(truncated: true);
            _synced = false;
            return;
        }

        if (word == PocsagCodeword.Idle)
        {
            FlushPage(truncated: false);
        }
        else if ((word & 0x80000000) == 0)
        {
            FlushPage(truncated: false);
            _pageOpen = true;
            _address = (((word >> 13) & 0x3FFFF) << 3) | (uint)frame;
            _function = (int)(word >> 11) & 3;
            _bitErrors = errors;
        }
        else if (_pageOpen)
        {
            _groups.Add((word >> 11) & 0xFFFFF);
            _bitErrors += errors;
        }
    }

    private void FlushPage(bool truncated)
    {
        if (!_pageOpen)
        {
            return;
        }

        _pageReceived(new PocsagPage(_address, _function, [.. _groups], _bitErrors, _inverted, truncated));
        _pageOpen = false;
        _groups.Clear();
        _bitErrors = 0;
    }
}
