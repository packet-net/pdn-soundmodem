using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Ota;

/// <summary>
/// Folds the receiver's own burst-level signals - <see cref="IModem.CarrierDetect"/> polled
/// once per feed block, the frames <see cref="IModem.FrameDecoded"/> delivers, and (for the
/// BPSK banks) the carrier-offset reading and the deframers' failure counters - into one
/// verdict row per burst: when it was there, whether it decoded, and what the receiver could
/// still say about it when it did not.
/// </summary>
/// <remarks>
/// <para><b>The calibration claim this instrument stands on:</b> DCD asserting IS the
/// bpsk-transmission verdict. <see cref="PacketDcd"/> scores DPLL transition-timing coherence -
/// it asserts only when slicer transitions keep landing where a signal at this symbol rate puts
/// them - and a foreign FSK or multi-tone signal sharing the spectrum does not produce
/// transition-timing coherence at this symbol rate, however much energy it carries. So a DCD
/// burst that never decodes is a damaged BPSK transmission, not a foreign signal; the analysis
/// sanity-checks the claim against the decoded rows (every decode must sit inside a DCD burst)
/// rather than taking it on faith.</para>
/// <para><b>Burst boundaries.</b> A burst opens at the first poll that sees DCD asserted and
/// closes when DCD has been down for <see cref="CloseAfter"/> - so a brief mid-burst dropout
/// (a fade riding through <see cref="PacketDcd"/>'s hysteresis) does not split one transmission
/// into two rows, which would poison the retry-twin analysis downstream. <c>utc_end</c> is the
/// last DCD-asserted poll, not the close decision; <c>dcd_seconds</c> accumulates only the
/// polls DCD actually held through, so a dropout is visible as <c>dcd_seconds</c> falling short
/// of <c>end - start</c>. Edge times are quantised to the poll cadence (100 ms feed blocks,
/// against bursts of 0.7 s and up).</para>
/// <para><b>Verdicts.</b> <c>decoded</c> is a delivered <see cref="IModem.FrameDecoded"/> event
/// attached to the burst - monitor-only copies count, because the station did read them. For a
/// burst with no decode, the deframer counters are what remains: an
/// <see cref="BpskMultiModem.RsFailures"/> delta means sync was found and Reed-Solomon refused
/// the frame; no delta means the receiver never even found sync in it. There is no
/// per-transmission "sync found" event to tap - the counters are bank-level event counts (see
/// their remarks) - so the deltas are recorded as the honest, if coarser, substitute.</para>
/// <para>All times come from the caller (the replay clock derived from chunk filenames);
/// nothing here reads a wall clock.</para>
/// </remarks>
internal sealed class DcdBurstTracker
{
    /// <summary>DCD-down time that closes a burst. Genuine inter-transmission gaps on a polled
    /// channel are seconds (TXDELAY alone is ~0.3 s); mid-burst DCD dropouts from a fade are a
    /// few symbol times. One second separates the two with margin either side.</summary>
    private static readonly TimeSpan CloseAfter = TimeSpan.FromSeconds(1);

    /// <summary>How far past a burst's end a frame may still attach to it. Frames normally
    /// arrive while the burst is open (delivery lags the last symbol by under a feed block plus
    /// the bank's 100 ms chunk hold), and the burst stays open for <see cref="CloseAfter"/>
    /// beyond its last asserted poll anyway; this window only catches the release-on-reset
    /// stragglers.</summary>
    private static readonly TimeSpan AttachWindow = TimeSpan.FromSeconds(2);

    private readonly List<DcdBurst> _bursts = [];
    private DcdBurst? _open;
    private DateTimeOffset _previousPollUtc;
    private bool _hasPreviousPoll;
    private long _previousRs;
    private long _previousCrc;

    /// <summary>Every burst seen, open one last, in time order.</summary>
    public IReadOnlyList<DcdBurst> Bursts => _bursts;

    /// <summary>Frames delivered with no burst open and none recently closed - each one is a
    /// decode the DCD envelope missed, i.e. direct evidence against the calibration claim, so
    /// the count is surfaced rather than swallowed.</summary>
    public int OrphanFrames { get; private set; }

    /// <summary>Feeds one poll of the receiver's burst-level signals, taken after a feed block
    /// was processed. <paramref name="pollUtc"/> is the block-end time; polls must arrive in
    /// time order.</summary>
    public void Observe(
        DateTimeOffset pollUtc, bool carrierDetect, double? offsetHz,
        long rsFailures, long crcFailures)
    {
        if (carrierDetect)
        {
            if (_open is null)
            {
                _open = new DcdBurst
                {
                    UtcStart = pollUtc,
                    UtcEnd = pollUtc,
                    // The counters as they stood before this burst's first block: the block that
                    // asserted DCD may already have ticked them, and those ticks are the burst's.
                    RsBaseline = _hasPreviousPoll ? _previousRs : rsFailures,
                    CrcBaseline = _hasPreviousPoll ? _previousCrc : crcFailures,
                };
                _bursts.Add(_open);
            }

            // One poll's worth of asserted time. Capped at the close window so a feed
            // discontinuity (a capture gap between chunks) cannot masquerade as minutes of DCD.
            double delta = (pollUtc - _previousPollUtc).TotalSeconds;
            if (_hasPreviousPoll && delta > 0 && delta <= CloseAfter.TotalSeconds)
            {
                _open.DcdSeconds += delta;
            }

            _open.UtcEnd = pollUtc;
            if (offsetHz is { } hz)
            {
                _open.PolledOffsetHz = hz;
            }
        }
        else if (_open is { } open && pollUtc - open.UtcEnd >= CloseAfter)
        {
            Close(open, rsFailures, crcFailures);
        }

        _previousPollUtc = pollUtc;
        _previousRs = rsFailures;
        _previousCrc = crcFailures;
        _hasPreviousPoll = true;
    }

    /// <summary>Attaches one delivered frame to the burst that carried it: the open burst, or
    /// the last closed one when the frame straggled in within <see cref="AttachWindow"/> of its
    /// end. Anything else is an orphan.</summary>
    public void OnFrame(DateTimeOffset utc, FrameQuality quality)
    {
        DcdBurst? burst = _open;
        if (burst is null && _bursts.Count > 0
            && utc - _bursts[^1].UtcEnd <= AttachWindow)
        {
            burst = _bursts[^1];
        }

        if (burst is null)
        {
            OrphanFrames++;
            return;
        }

        burst.Frames++;
        if (burst.FrameOffsetHz is null && quality.FrequencyOffsetHz is { } hz)
        {
            burst.FrameOffsetHz = hz;
        }
    }

    /// <summary>Closes any burst still open, using the last poll's counters - call once when
    /// the feed ends, or the final burst of a pass never gets its verdict.</summary>
    public void Flush()
    {
        if (_open is { } open)
        {
            Close(open, _previousRs, _previousCrc);
        }
    }

    private void Close(DcdBurst open, long rsFailures, long crcFailures)
    {
        open.RsFailures = rsFailures - open.RsBaseline;
        open.CrcFailures = crcFailures - open.CrcBaseline;
        _open = null;
    }
}

/// <summary>One DCD burst's verdict row - see <see cref="DcdBurstTracker"/> for what each
/// field really measures.</summary>
internal sealed class DcdBurst
{
    public DateTimeOffset UtcStart { get; set; }

    public DateTimeOffset UtcEnd { get; set; }

    /// <summary>Seconds DCD actually held through - short of <c>UtcEnd - UtcStart</c> when the
    /// burst rode out mid-burst dropouts.</summary>
    public double DcdSeconds { get; set; }

    /// <summary>Frames attached to this burst (monitor-only copies included: the station read
    /// them).</summary>
    public int Frames { get; set; }

    public bool Decoded => Frames > 0;

    /// <summary>The first decoded frame's own offset reading, when one decoded.</summary>
    public double? FrameOffsetHz { get; set; }

    /// <summary>The last non-null polled bank reading while DCD held - the undecoded burst's
    /// substitute for a frame's reading.</summary>
    public double? PolledOffsetHz { get; set; }

    /// <summary>The offset written to the row: a decoded frame's own reading when there is one,
    /// else the polled one.</summary>
    public double? OffsetHz => FrameOffsetHz ?? PolledOffsetHz;

    /// <summary>Bank-level sync-found-but-Reed-Solomon-failed events during this burst
    /// (see <see cref="BpskMultiModem.RsFailures"/>: positive means sync was found, the
    /// magnitude means nothing more).</summary>
    public long RsFailures { get; set; }

    /// <summary>Bank-level recovered-but-trailing-CRC-refused events during this burst.</summary>
    public long CrcFailures { get; set; }

    internal long RsBaseline { get; init; }

    internal long CrcBaseline { get; init; }
}
