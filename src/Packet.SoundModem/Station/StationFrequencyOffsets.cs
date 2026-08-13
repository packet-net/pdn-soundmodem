using System.Diagnostics.CodeAnalysis;

namespace Packet.SoundModem.Station;

/// <summary>What is known about one station's transmit frequency, relative to ours.</summary>
/// <param name="Callsign">The station, as it appeared in the AX.25 source address.</param>
/// <param name="OffsetHz">The recency-weighted mean offset of its recent frames, in Hz.</param>
/// <param name="Samples">How many frames the estimate is drawn from.</param>
/// <param name="SpreadHz">Largest minus smallest offset across those frames: how settled it is.</param>
/// <param name="AgeSeconds">How long ago the most recent of those frames was heard.</param>
public readonly record struct StationOffset(
    string Callsign, double OffsetHz, int Samples, double SpreadHz, double AgeSeconds);

/// <summary>
/// Tracks how far off our channel centre each station we hear is transmitting, so we can
/// transmit back on the frequency their receiver is actually listening on.
/// </summary>
/// <remarks>
/// <para><b>Why this works.</b> A rig's transmit and receive conversions run off one master
/// oscillator, so a station whose reference is high by d transmits d high <em>and listens</em>
/// d high. If their error is d_them and ours is d_us, the offset we measure on their frames is
/// (d_them - d_us), and the shift that lands our signal on the centre their modem expects is
/// exactly the same quantity. So the number we need is the number we already measure, and
/// <b>our own reference error cancels</b>: it appears in the measurement and in the
/// transmission with the same sign, through the same oscillator. An accurate reference is not
/// required for this to be correct, only a reference that does not move appreciably between
/// hearing them and answering.</para>
/// <para><b>Why the estimate is short-window.</b> The correction only has to hold for the
/// exchange it is used in, so recent frames beat a long-run mean: a rig that drifts over a day
/// is usually steady over the next four frames, and averaging across the drift would describe
/// neither end of it. <see cref="MaxSamples"/> and <see cref="MaxAge"/> bound the window;
/// <see cref="StationOffset.SpreadHz"/> reports how settled it was, so a caller can decline to
/// act on a wandering one.</para>
/// <para><b>What this deliberately does not do.</b> It does not decide to transmit anywhere.
/// It measures. The decision, its clamps and its exclusions live with the caller, because
/// aiming at one station's oscillator aims away from everybody else's and only the caller
/// knows whether a given frame is addressed to one station or broadcast to the channel.</para>
/// </remarks>
public sealed class StationFrequencyOffsets
{
    private readonly record struct Sample(double OffsetHz, long AtTicks);

    private readonly Dictionary<string, List<Sample>> _byStation =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly TimeProvider _time;

    /// <summary>Creates a tracker.</summary>
    /// <param name="timeProvider">Clock for the age window; defaults to the system clock.</param>
    public StationFrequencyOffsets(TimeProvider? timeProvider = null) =>
        _time = timeProvider ?? TimeProvider.System;

    /// <summary>Most recent frames kept per station. Default 8.</summary>
    public int MaxSamples { get; init; } = 8;

    /// <summary>How old a frame may be and still count. Default 10 minutes.</summary>
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Largest offset magnitude that will be recorded at all, in Hz. Default 250.
    /// </summary>
    /// <remarks>
    /// An offset-diversity bank reports the branch that won, and a wild branch win on a marginal
    /// frame is noise rather than a measurement of anybody's oscillator. Discarding the absurd
    /// ones on the way in keeps one bad decode out of an average that steers a transmitter.
    /// </remarks>
    public double MaxCredibleOffsetHz { get; init; } = 250;

    /// <summary>Records the frequency offset a frame from <paramref name="callsign"/> decoded at.</summary>
    /// <param name="callsign">The AX.25 source address, SSID included.</param>
    /// <param name="offsetHz">The winning branch's offset in Hz, positive above centre.</param>
    public void Record(string? callsign, double offsetHz)
    {
        if (string.IsNullOrWhiteSpace(callsign)
            || double.IsNaN(offsetHz)
            || Math.Abs(offsetHz) > MaxCredibleOffsetHz)
        {
            return;
        }

        long now = _time.GetUtcNow().UtcTicks;
        lock (_gate)
        {
            if (!_byStation.TryGetValue(callsign, out List<Sample>? samples))
            {
                samples = [];
                _byStation[callsign] = samples;
            }

            samples.Add(new Sample(offsetHz, now));
            if (samples.Count > MaxSamples)
            {
                samples.RemoveRange(0, samples.Count - MaxSamples);
            }
        }
    }

    /// <summary>The current estimate for <paramref name="callsign"/>, or false if nothing recent.</summary>
    /// <param name="callsign">The station to ask about.</param>
    /// <param name="offset">The estimate, when one exists.</param>
    public bool TryGet(string? callsign, [NotNullWhen(true)] out StationOffset? offset)
    {
        offset = null;
        if (string.IsNullOrWhiteSpace(callsign))
        {
            return false;
        }

        long now = _time.GetUtcNow().UtcTicks;
        long oldest = now - MaxAge.Ticks;

        lock (_gate)
        {
            if (!_byStation.TryGetValue(callsign, out List<Sample>? samples))
            {
                return false;
            }

            double sum = 0, min = double.MaxValue, max = double.MinValue;
            long newest = 0;
            int n = 0;
            foreach (Sample s in samples)
            {
                if (s.AtTicks < oldest)
                {
                    continue;
                }

                sum += s.OffsetHz;
                min = Math.Min(min, s.OffsetHz);
                max = Math.Max(max, s.OffsetHz);
                newest = Math.Max(newest, s.AtTicks);
                n++;
            }

            if (n == 0)
            {
                return false;
            }

            offset = new StationOffset(
                callsign, sum / n, n, max - min, (now - newest) / (double)TimeSpan.TicksPerSecond);
            return true;
        }
    }

    /// <summary>Every station with a current estimate, strongest sample count first.</summary>
    public IReadOnlyList<StationOffset> Snapshot()
    {
        List<string> names;
        lock (_gate)
        {
            names = [.. _byStation.Keys];
        }

        var all = new List<StationOffset>(names.Count);
        foreach (string name in names)
        {
            if (TryGet(name, out StationOffset? o) && o is StationOffset value)
            {
                all.Add(value);
            }
        }

        all.Sort((a, b) => b.Samples.CompareTo(a.Samples));
        return all;
    }
}
