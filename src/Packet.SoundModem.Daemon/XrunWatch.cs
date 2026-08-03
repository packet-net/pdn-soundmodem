namespace Packet.SoundModem.Daemon;

/// <summary>
/// Watches the sound device's xrun counters and says when audio was lost.
/// </summary>
/// <remarks>
/// <para>ALSA recovers from an overrun or underrun by restarting the stream, silently and lossily:
/// every xrun is a hole where audio should have been. Capture holes drop frames that were on the
/// air; playback holes put a discontinuity in what we transmitted. Both were counted and neither
/// was ever reported, so a station losing audio to a machine that will not schedule it looked
/// exactly like a station on a quiet band — the same blindness the per-frame lines removed.</para>
/// <para>Reported as deltas on a poll rather than an event per xrun: they arrive in bursts when a
/// machine is contended, and one line per hole would bury the journal at the moment it most needs
/// reading. The state and the wording are here, apart from the timer, so both are testable without
/// waiting for a clock.</para>
/// </remarks>
internal sealed class XrunWatch
{
    private int _capture;
    private int _playback;
    private bool _everReported;

    /// <summary>
    /// Folds in the latest counters and returns the line to log, or null when nothing was lost
    /// since the last poll.
    /// </summary>
    internal string? Poll(int captureXruns, int playbackXruns)
    {
        int newCapture = captureXruns - _capture;
        int newPlayback = playbackXruns - _playback;
        _capture = captureXruns;
        _playback = playbackXruns;

        if (newCapture <= 0 && newPlayback <= 0)
        {
            return null;
        }

        var text = new System.Text.StringBuilder("audio: ");
        if (newCapture > 0)
        {
            text.Append($"{newCapture} capture overrun{(newCapture == 1 ? "" : "s")}");
        }

        if (newCapture > 0 && newPlayback > 0)
        {
            text.Append(", ");
        }

        if (newPlayback > 0)
        {
            text.Append($"{newPlayback} playback underrun{(newPlayback == 1 ? "" : "s")}");
        }

        text.Append($" ({_capture + _playback} since start)");

        // Said once, at the first loss: what an xrun costs, and the one thing that fixes it. After
        // that the counts speak for themselves and the advice would just be noise.
        if (!_everReported)
        {
            _everReported = true;
            text.Append(
                " — each one is lost audio: a dropped frame on receive, a discontinuity in what we "
                + "transmitted. This is the machine not scheduling the modem within the ~120 ms of "
                + "buffer it has, not a radio problem; give it more CPU share, or fewer neighbours.");
        }

        return text.ToString();
    }
}
