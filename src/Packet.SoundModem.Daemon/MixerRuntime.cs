using Packet.SoundModem.Audio;

namespace Packet.SoundModem.Daemon;

/// <summary>What one mixer change over the API came to.</summary>
/// <param name="Report">The card as it reads back now.</param>
/// <param name="Persisted">Whether it was written to the state file.</param>
/// <param name="Warn">Whether <paramref name="Note"/> is something the operator has to read now
/// rather than a plain "and it has been remembered" - a control the config file pins and will
/// take back at the next start-up, or a state file that could not be written. The operator page
/// shows the sentence on the strength of this rather than guessing from the other fields.</param>
/// <param name="Note">What an operator should read: what will happen at the next start-up, and
/// why the state file was not written when it was not.</param>
internal sealed record MixerOutcome(
    MixerReport Report, bool Persisted, bool Warn, string Note);

/// <summary>
/// The station's mixer while it is running: the open card, what pinned each control, and the
/// state file a change from the operator page or <c>/api/mixer</c> is remembered in.
/// </summary>
/// <remarks>
/// <para>One object rather than the four lambdas the endpoint used to be handed, because the
/// three operations are not independent: what a read reports as the source of a level depends on
/// what the last apply wrote, and a refusal has to be decided against the same card the apply
/// would touch. Wiring them separately from the daemon's top-level statements meant the state was
/// spread across three closures that had to agree.</para>
/// <para><b>The order of a change is: card, read back, then disk.</b> The card is what the
/// operator can hear and is the point of the request; the state file only decides what happens at
/// the next start-up. So a state file that cannot be written costs the persistence and never the
/// change, and the answer says exactly that rather than looking like a failure.</para>
/// </remarks>
internal sealed class MixerRuntime
{
    private readonly IAlsaMixer _mixer;
    private readonly AlsaMixerConfig? _config;
    private readonly MixerSettings _baseline;
    private readonly string _device;
    private readonly Action<string> _journal;
    private readonly TimeProvider _clock;
    private MixerState _state;

    private MixerRuntime(
        IAlsaMixer mixer, AlsaMixerConfig? config, MixerSettings baseline, MixerState state,
        string statePath, string device, Action<string> journal, TimeProvider clock)
    {
        _mixer = mixer;
        _config = config;
        _baseline = baseline;
        _state = state;
        _device = device;
        _journal = journal;
        _clock = clock;
        StatePath = statePath;
    }

    /// <summary>Where a change from the page or the API is remembered.</summary>
    public string StatePath { get; }

    /// <summary>What the start-up apply found and read back.</summary>
    public MixerReport StartUpReport { get; private init; } = new() { Card = "" };

    /// <summary>
    /// Opens the station's mixer for the run: read the state file, work out what the config file
    /// and the state file between them ask for, put it on the card, and keep the lot.
    /// </summary>
    /// <remarks>
    /// Guarded the same way the shipped start-up path was, and for the same reason: these are
    /// called from the daemon's top-level statements, which have nothing above them to catch an
    /// <c>EntryPointNotFoundException</c> from one of the twenty <c>libasound</c> entry points the
    /// apply reaches. That would be a crash at every start-up and a systemd restart loop, over a
    /// mixer. It costs the mixer instead.
    /// </remarks>
    /// <param name="mixer">The open card.</param>
    /// <param name="config">The <c>alsa.mixer</c> block, or null when there is none.</param>
    /// <param name="configPath">The config file, for the default state-file location.</param>
    /// <param name="device">The station's device, stamped into the state file.</param>
    /// <param name="journal">Where each line goes as it is produced.</param>
    /// <param name="why">What went wrong, when this returns null.</param>
    /// <param name="clock">The clock the state file is stamped from.</param>
    /// <returns>The runtime, or null when the card could not be read or set at all.</returns>
    public static MixerRuntime? Start(
        IAlsaMixer mixer, AlsaMixerConfig? config, string configPath, string device,
        Action<string> journal, out string why, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(mixer);
        ArgumentNullException.ThrowIfNull(journal);

        string statePath = MixerStateFile.PathFor(config?.StateFile, configPath);
        MixerState? read = MixerStateFile.TryRead(statePath, device, out string ignored);
        journal(MixerSetup.JournalPrefix + MixerStateFile.StartUpLine(statePath, read, ignored));

        MixerSettings wanted = MixerStateFile.Combine(config, read);
        if (MixerSetup.TryApply(mixer, wanted, journal, out why) is not MixerReport report)
        {
            return null;
        }

        // A state file that was ignored is not carried forward: the next change starts a fresh
        // one for this card rather than folding itself into another card's settings.
        return new MixerRuntime(
            mixer, config, wanted.LeaveEverything(), read ?? new MixerState(),
            statePath, device, journal, clock ?? TimeProvider.System)
        {
            StartUpReport = report,
        };
    }

    /// <summary>The card as it reads back now, with what pinned each control.</summary>
    public MixerReport Read() =>
        MixerSetup.Apply(_mixer, _baseline with { Sources = Sources() }, null);

    /// <summary>
    /// Why a change cannot be put on this card, or null when it can. Asked before anything is
    /// touched, so a refusal costs nothing that was already set.
    /// </summary>
    /// <param name="change">What is being asked for.</param>
    public string? WhyRefused(MixerChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return MixerSetup.WhyRefused(_mixer, change.Over(_baseline));
    }

    /// <summary>
    /// Sets the card, reads it back, and remembers the change for the next start-up.
    /// </summary>
    /// <remarks>
    /// <b>Call this one at a time.</b> It is a read-modify-write of the remembered state and of
    /// the state file, and <see cref="MixerStateFile.TryWrite"/>'s temp name is keyed only on the
    /// process id, so two concurrent callers in one process would interleave the state and
    /// collide on the temp file. <c>ConfigApi</c>'s mixer gate is what serialises them today and
    /// is this method's precondition; the concurrency test drives eight at once through it.
    /// </remarks>
    /// <param name="change">What to set.</param>
    /// <param name="persist">False for a one-run try: the card is set and nothing is written.</param>
    public MixerOutcome Apply(MixerChange change, bool persist)
    {
        ArgumentNullException.ThrowIfNull(change);

        MixerReport report = MixerSetup.Apply(_mixer, change.Over(_baseline), _journal);

        // Said first, because it is the surprising one. The card is set now either way, but a
        // control the config file pins comes back to the file's value at the next start-up, and
        // an operator who has just moved a slider is entitled to know that before they walk away.
        var notes = new List<string>(Pinned(change));
        bool warn = notes.Count > 0;

        bool persisted = false;
        if (!persist)
        {
            _journal(
                $"{MixerSetup.JournalPrefix}{change.Describe()} -> set for this run only "
                + $"(persist=false), not written to {StatePath}");
            notes.Add(
                "Not written down, because this request said persist=false: the card is set and "
                + "stays set until something sets it again, but the next start-up will not set it.");
        }
        else
        {
            MixerState wanted = _state.With(change, _device, _clock.GetUtcNow());
            if (MixerStateFile.TryWrite(StatePath, wanted, out string cannot))
            {
                _state = wanted;
                persisted = true;
                _journal(
                    $"{MixerSetup.JournalPrefix}{change.Describe()} -> written to {StatePath}");
                notes.Add($"Remembered in {StatePath}, so the next start-up sets it again.");
            }
            else
            {
                _journal(
                    $"{MixerSetup.JournalPrefix}{change.Describe()} -> NOT written to "
                    + $"{StatePath}: {cannot}");
                notes.Add(
                    $"The card is set and stays set, but {StatePath} could not be written "
                    + $"({cannot}), so nothing on disk records it and the next start-up will not "
                    + "set it.");
            }
        }

        // A failed write warns; a persist=false the caller asked for does not. Warning about
        // something somebody deliberately requested trains them to stop reading the field.
        return new MixerOutcome(
            report with { Sources = Sources() }, persisted, warn || (persist && !persisted),
            string.Join(" ", notes));
    }

    /// <summary>
    /// The sentence for each control this change touches that the config file also pins.
    /// </summary>
    /// <remarks>
    /// The honesty half of the feature. Precedence at start-up is config, then state file, then
    /// leave the card alone - so a page change to a pinned control is real, is remembered, and is
    /// still overwritten the next time the daemon starts. Silently doing that would look like the
    /// change had been lost.
    /// </remarks>
    private IEnumerable<string> Pinned(MixerChange change)
    {
        if (change.CaptureGainDb is not null && _config?.CaptureGainDb is double capture)
        {
            yield return Level(MixerSetup.CaptureKey, capture);
        }

        if (change.PlaybackDb is not null && _config?.PlaybackDb is double playback)
        {
            yield return Level(MixerSetup.PlaybackKey, playback);
        }

        static string Level(string key, double decibels) =>
            $"{key} is set in the config file as {MixerSetup.Db(decibels)} dB; this change lasts "
            + "until the next start.";
    }

    /// <summary>
    /// What pins each control now: the config file if it names it, else the state file if it
    /// holds it, else nothing.
    /// </summary>
    private MixerSources Sources() => new(
        Source(_config?.CaptureGainDb, _state.CaptureGainDb),
        Source(_config?.PlaybackDb, _state.PlaybackDb));

    private static MixerSource Source(object? fromConfig, object? fromState) =>
        fromConfig is not null ? MixerSource.Config
        : fromState is not null ? MixerSource.StateFile
        : MixerSource.None;
}
