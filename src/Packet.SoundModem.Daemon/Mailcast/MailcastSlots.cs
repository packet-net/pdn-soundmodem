using System.Globalization;
using Packet.Mailcast;

namespace Packet.SoundModem.Daemon;

/// <summary>One slot as this station heard it.</summary>
/// <param name="Slot">The slot's scheduled start.</param>
/// <param name="Tone">Its opening tone, if heard.</param>
/// <param name="Frames">Mailcast frames decoded in it.</param>
/// <param name="LastFrame">When the newest of them arrived.</param>
internal sealed record MailcastSlotSummary(DateTimeOffset Slot, MailcastTone? Tone, int Frames, DateTimeOffset? LastFrame);

/// <summary>
/// GB7RDG's timetable (from its directory once heard, GB7RDG's own until then) and what this
/// station heard of its latest slot.
/// </summary>
/// <remarks>
/// A frame counts for the slot whose start most recently passed, allowing a minute for a clock a
/// little slow. A tone only counts if it began within 5 minutes of a slot's start: anything else
/// near the signal's centre is someone tuning up. Thread-safe.
/// </remarks>
internal sealed class MailcastSlots
{
    /// <summary>How far from a slot's start a tone may begin and still be the slot's own.</summary>
    internal static readonly TimeSpan ToneWindow = TimeSpan.FromMinutes(5);

    /// <summary>How early by this station's clock a frame may arrive and count for the coming slot.</summary>
    internal static readonly TimeSpan ClockAllowance = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _time;
    private readonly Action<string> _log;
    private readonly Lock _gate = new();
    private SlotTimetable? _heard;
    private MailcastSlotSummary? _last;

    internal MailcastSlots(TimeProvider time, Action<string> log, SlotTimetable? heard)
    {
        _time = time;
        _log = log;
        _heard = heard;
    }

    /// <summary>The timetable in use.</summary>
    internal SlotTimetable Timetable
    {
        get
        {
            lock (_gate)
            {
                return _heard ?? MailcastOnAir.DefaultTimetable;
            }
        }
    }

    /// <summary>Whether <see cref="Timetable"/> came from GB7RDG's directory.</summary>
    internal bool FromDirectory
    {
        get
        {
            lock (_gate)
            {
                return _heard is not null;
            }
        }
    }

    /// <summary>The slot in progress or most recently heard.</summary>
    internal MailcastSlotSummary? Last
    {
        get
        {
            lock (_gate)
            {
                return _last;
            }
        }
    }

    /// <summary>The next slot that runs, starting at or after now.</summary>
    internal DateTimeOffset? Next => Timetable.NextActiveAtOrAfter(_time.GetUtcNow());

    /// <summary>A directory gave GB7RDG's timetable.</summary>
    internal void Heard(SlotTimetable timetable)
    {
        lock (_gate)
        {
            if (timetable == _heard)
            {
                return;
            }

            _heard = timetable;
        }

        _log($"mailcast: the broadcast's directory gives its slots as {Describe(timetable)}; using that");
    }

    /// <summary>A mailcast frame was decoded.</summary>
    internal void OnFrame()
    {
        DateTimeOffset now = _time.GetUtcNow();
        int frames;
        lock (_gate)
        {
            DateTimeOffset slot = Timetable.SlotAtOrBefore(now + ClockAllowance);
            _last = _last is { } last && last.Slot == slot
                ? last with { Frames = last.Frames + 1, LastFrame = now }
                : new MailcastSlotSummary(slot, null, 1, now);
            frames = _last.Frames;
        }

        if (frames == 1 || frames % 50 == 0)
        {
            _log($"mailcast: {frames} frame{(frames == 1 ? "" : "s")} heard in this slot");
        }
    }

    /// <summary>A tone was measured.</summary>
    internal void OnTone(MailcastTone tone)
    {
        DateTimeOffset began = _time.GetUtcNow() - tone.Duration;
        SlotTimetable timetable = Timetable;
        DateTimeOffset before = timetable.SlotAtOrBefore(began);
        DateTimeOffset after = before.AddMinutes(timetable.EveryMinutes);
        DateTimeOffset nearest = began - before <= after - began ? before : after;
        string measured = string.Create(CultureInfo.InvariantCulture,
            $"{tone.OffsetHz:+0.0;-0.0;0.0} Hz from where it should be, SNR {tone.SnrDb:F1} dB in 3 kHz, {tone.Duration.TotalSeconds:F0} s");
        if ((began - nearest).Duration() > ToneWindow)
        {
            _log($"mailcast: a tone {measured}, but it began at {began.UtcDateTime:HH:mm:ss} UTC, not at a slot's start, so it is not the broadcast's; ignored");
            return;
        }

        lock (_gate)
        {
            _last = _last is { } last && last.Slot == nearest
                ? last with { Tone = tone }
                : new MailcastSlotSummary(nearest, tone, 0, null);
        }

        _log($"mailcast: tone {measured}");
    }

    /// <summary>A timetable in words: "every hour on the hour, in daylight from 120 minutes after
    /// sunrise to 30 minutes before sunset at IO91lk".</summary>
    internal static string Describe(SlotTimetable timetable) =>
        (timetable.EveryMinutes switch
        {
            60 when timetable.Anchor.Minute == 0 => "every hour on the hour",
            60 => string.Create(CultureInfo.InvariantCulture, $"every hour at {timetable.Anchor.Minute} minutes past"),
            1440 => string.Create(CultureInfo.InvariantCulture, $"once a day at {timetable.Anchor:HH:mm} UTC"),
            _ => string.Create(CultureInfo.InvariantCulture, $"every {timetable.EveryMinutes} minutes from {timetable.Anchor:HH:mm} UTC"),
        })
        + (timetable.Daylight is { } daylight ? ", in daylight " + daylight.Describe() : "");
}
