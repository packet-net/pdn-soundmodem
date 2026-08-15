namespace Packet.SoundModem.Daemon;

/// <summary>
/// Detects a dead receive feed: an unbroken run of digital-silence samples longer than a
/// threshold. Born 2026-08-07, when the 40 m capture campaign's Flex feed died silently at
/// 09:26:47Z and the daemon spent 6.8 hours decoding and recording zeros - the paced DAX
/// ring keeps delivering full-rate buffers when the underlying VITA stream stops, it just
/// pads them with exact silence, so "no samples" never happens and nothing anywhere said a
/// word. A real receive slice always carries noise-floor energy (the healthy capture
/// measures RMS ~0.02 against the dead feed's exactly 0.0), so half a minute of unbroken
/// zeros is a dead feed with certainty, not a quiet band.
/// </summary>
/// <remarks>
/// Counted in samples rather than wall clock: sample count IS elapsed feed time, it cannot
/// drift from the audio, and it makes the detector deterministic under test. The caller
/// decides the recovery; the daemon's receive loop logs and triggers its own orderly
/// shutdown so systemd's <c>Restart=always</c> brings the whole stack - Flex session
/// included - back fresh, which is the recovery both real incidents proved out. The cost
/// of a false fire is one clean restart every threshold interval, loud in the journal; a
/// deliberately muted DAX stream would loop that way, and the log line says so.
/// <para><b>The station's own transmissions are not evidence about the receive feed.</b> While
/// keyed, a Flex is not receiving and its DAX stream legitimately delivers exact zeros - which is
/// indistinguishable, sample for sample, from the dead feed this watch exists to catch. Until
/// 2026-08-15 nothing told the two apart, so a station that transmitted enough restarted itself:
/// a FreeDV run pacing frames every 8 s on the GB7RDG node accumulated 30 s of "unbroken digital
/// silence" across its own bursts and took the production node off the air at 10:28:50,
/// mid-campaign. <see cref="Observe"/> now takes whether the station is transmitting, and re-arms
/// instead of accumulating, so only genuinely idle silence counts.</para>
/// <para>The trade-off, stated because it is a real one: a station transmitting more often than
/// the threshold can no longer detect a dead receive feed at all. That is the right way round.
/// The incident this watch was built for was a receive-only capture station, which never
/// transmits and is unaffected; and a node whose transmissions are working is a much better
/// failure than a working node that restart-loops itself off the air.</para>
/// </remarks>
internal sealed class DeadFeedWatch
{
    /// <summary>Below this magnitude a sample counts as digital silence. The dead feed
    /// pads exact zeros; the epsilon only guards against denormal noise in a resampler.
    /// Any genuine RF noise floor sits orders of magnitude above it.</summary>
    private const float SilenceEpsilon = 1e-6f;

    private readonly long _thresholdSamples;
    private long _silentRun;
    private bool _fired;

    /// <summary>Creates the watch.</summary>
    /// <param name="sampleRate">The input's sample rate.</param>
    /// <param name="thresholdSeconds">Unbroken silence that declares the feed dead.</param>
    public DeadFeedWatch(int sampleRate, double thresholdSeconds = 30)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(thresholdSeconds, 0);
        _thresholdSamples = (long)(sampleRate * thresholdSeconds);
    }

    /// <summary>Observes one block of input samples. Returns <see langword="true"/> exactly
    /// once, on the block where the unbroken silent run first crosses the threshold; any
    /// live sample re-arms it.</summary>
    /// <param name="samples">The block just read from the input.</param>
    /// <param name="transmitting">Whether the station was keyed for this block. Silence while
    /// keyed says nothing about the feed - the radio is not receiving - so it re-arms the watch
    /// rather than counting. Re-arming (rather than merely not accumulating) is what also covers
    /// the unkey transition: the receive path takes a moment to resume real audio afterwards, and
    /// counting that as evidence of death is the same mistake one block later.</param>
    public bool Observe(ReadOnlySpan<float> samples, bool transmitting = false)
    {
        if (transmitting)
        {
            _silentRun = 0;
            _fired = false;
            return false;
        }

        foreach (float sample in samples)
        {
            if (sample is > SilenceEpsilon or < -SilenceEpsilon)
            {
                _silentRun = 0;
                _fired = false;
                return false;
            }
        }

        // The whole block was silent. (A block that ends with silence after live samples
        // starts the run at the next all-silent block - block-granular, which at 100 ms
        // blocks against a 30 s threshold is noise.)
        _silentRun += samples.Length;
        if (_fired || _silentRun < _thresholdSamples)
        {
            return false;
        }

        _fired = true;
        return true;
    }
}
