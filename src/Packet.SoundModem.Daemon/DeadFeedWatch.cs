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
    public bool Observe(ReadOnlySpan<float> samples)
    {
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
