using System.Runtime.InteropServices;
using M0LTE.Dsp;
using Packet.SoundModem.Channel;
using Packet.SoundModem.UberSdr;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// Bringing up flavour B: the checks a <c>monitor</c> configuration has to pass, the band plan
/// every receiver shares, and then the <see cref="MonitorHost"/> that owns the rest of the run.
/// </summary>
/// <remarks>
/// A file with a <c>monitor</c> section is not describing a station, so none of the daemon's
/// start-up applies to it: there is no device to open, no PTT to check, no KISS port to bind and
/// no transmitter to plan a filter for. It gets its own path from the moment the file is read,
/// which is also what keeps the single-station path exactly as it was.
/// </remarks>
internal static class MonitorStartup
{
    /// <summary>The file a monitor run came from, for the settings page.</summary>
    /// <param name="ConfigPath">The file the run was asked to read.</param>
    /// <param name="RunningJson">What this process is running, verbatim, which may be a one-run
    /// change and not the file.</param>
    /// <param name="EphemeralInForce">Whether the run is on a one-run change.</param>
    /// <param name="Recent">The recent console output, for the report dialog; null for none.</param>
    internal sealed record MonitorFile(
        string ConfigPath, string RunningJson, bool EphemeralInForce, RecentOutput? Recent = null);

    /// <summary>Runs the monitor. Returns the process exit code.</summary>
    /// <param name="config">The file, already loaded and checked.</param>
    /// <param name="into">
    /// Where the start-up says what it is doing and why it will not. The console's, which is what
    /// the service's journal is; a test's own, so that asserting on a refusal does not mean
    /// redirecting the console out from under every other test in the run.
    /// </param>
    /// <param name="file">
    /// The configuration file this run was read from, so the settings page can show it and write
    /// to it; null for a run with no page to offer, which is every test that is not about it.
    /// </param>
    internal static async Task<int> RunAsync(
        DaemonConfig config, StationJournal? into = null, MonitorFile? file = null)
    {
        MonitorConfig monitor = config.Monitor!;
        WaterfallConfig waterfall = config.Waterfall!;
        StationJournal journal = into ?? StationJournal.Console();
        journal.Write("monitor: many receivers behind one page, receive only");

        List<ModemConfig> modems = monitor.Modems;
        if (!StationFactory.TryResolveDspRate(modems, journal, out int dspRate))
        {
            return 2;
        }

        // Every receiver gets the same modems on the same dial, so the plan is drawn once and
        // shared. It is also what says which part of the band a receiver has to be able to tune
        // for this monitor to have any use for it.
        RfPlan.Result? plan;
        try
        {
            plan = BandPlanner.Plan(modems, config.Sideband, config.DialFrequency, dspRate);
        }
        catch (InvalidDataException planFailure)
        {
            journal.WriteError($"band plan: {planFailure.Message}");
            return 2;
        }

        if (plan is null)
        {
            journal.WriteError(
                "\"monitor\".\"modems\" has no \"rfFrequency\". A web receiver has no dial already "
                + "set to read off, so the band plan is the only thing that can tune it: give "
                + "every modem an \"rfFrequency\" and the dial is worked out from them.");
            return 2;
        }

        // The same refusal a station with a web receiver gets, and for the same reason - except
        // that here every receiver is a web receiver, so there is no half of this site that FM
        // could work on. Left to run, the tuning below would take "not USB" as LSB and this
        // whole monitor would demodulate one sideband of an FM signal without saying so.
        if (plan.IsFm)
        {
            journal.WriteError(
                "\"sideband\": \"fm\" cannot be served by a monitor: its web receivers are SSB. "
                + "Remove \"sideband\", or run FM on a station.");
            return 2;
        }

        // Built once, here, against a channel nothing will ever read. The mode-name and rate
        // checks above do not build anything, so a modem this configuration cannot actually make
        // - "acceptPlainIl2p" on a mode with no separate plain reading, a plugin refusing its own
        // geometry - used to get all the way through start-up, and then fail once per station,
        // once per request, as a 404 with a stack trace behind it. One exit 2 instead. Its lines
        // are swallowed because every station prints its own; its refusals are not.
        if (!StationFactory.TryAddModems(
                new SoundModemChannel(dspRate), modems, dspRate,
                detectorOverride: null,
                new StationJournal("", _ => { }, journal.WriteError)))
        {
            return 2;
        }

        BandPlanner.Report(plan, Console.Out, radioIsSelfTuning: true);
        foreach (string warning in plan.Warnings)
        {
            journal.WriteError($"band plan: {warning}");
        }

        // What a receiver has to cover to be worth listing: the whole span the modems occupy,
        // edge to edge, in RF terms.
        double windowLowHz = plan.Modems.Min(m => m.Slot.LowEdgeHz);
        double windowHighHz = plan.Modems.Max(m => m.Slot.HighEdgeHz);

        string frameLogDirectory = "";
        if (config.FrameLog is { } frameLog
            && !TryPrepareFrameLogDirectory(frameLog.Path, journal, out frameLogDirectory))
        {
            return 2;
        }

        var tuning = new UberSdrTuning
        {
            // The receiver is tuned to the dial itself, so the suppressed carrier lands at DC in
            // the IQ and the demodulator's own NCO has nothing left to do.
            FrequencyHz = (int)Math.Round(plan.DialHz),
            Sideband = plan.IsUpperSideband ? Sideband.Upper : Sideband.Lower,
            OutputRate = dspRate,
            Mode = config.UberSdr?.Mode ?? "iq48",
            Password = config.UberSdr?.Password,
            SsbLowHz = config.UberSdr?.SsbLowHz ?? 150,
            SsbHighHz = config.UberSdr?.SsbHighHz ?? 3450,
            StartupGuardMs = config.UberSdr?.StartupGuardMs ?? 1000,
            Gain = (float)(config.UberSdr?.Gain ?? 1.0),
        };
        var linger = TimeSpan.FromSeconds(monitor.LingerSeconds);

        journal.Write(
            $"monitor: {tuning.Mode} IQ at {RfPlan.Mhz(plan.DialHz)} -> "
            + $"{plan.Sideband.ToUpperInvariant()} {tuning.SsbLowHz:F0}-{tuning.SsbHighHz:F0} Hz "
            + $"audio at {dspRate} Hz, on demand, each receiver held {monitor.LingerSeconds} s "
            + "after its last viewer leaves");
        journal.Write(
            $"monitor: listing receivers that cover {RfPlan.Mhz(windowLowHz)} to "
            + $"{RfPlan.Mhz(windowHighHz)} and offer {tuning.Mode}"
            + (monitor.Allow.Count > 0 ? $", from an allow list of {monitor.Allow.Count}" : "")
            + (monitor.Deny.Count > 0 ? $", less {monitor.Deny.Count} denied" : ""));

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };
        using var sigterm = PosixSignalRegistration.Create(
            PosixSignal.SIGTERM,
            context =>
            {
                context.Cancel = true;
                cancellation.Cancel();
            });

        // The settings page, and only with a key. A site is reachable from the internet, so there
        // is no open mode here whatever a station on a private network may do: with no key the
        // page is simply not built.
        bool restartRequested = false;
        Func<System.Net.HttpListenerContext, string, Task<bool>>? configHandler = ConfigHandlerFor(
            config.Api?.Key ?? "", file,
            () =>
            {
                // Exit 1, the door systemd's Restart=on-failure reopens, as a station's applied
                // change uses: exit 2 would mean the new file is wrong.
                restartRequested = true;
                cancellation.Cancel();
            },
            file?.Recent);
        if (configHandler is not null
            && Environment.GetEnvironmentVariable("INVOCATION_ID") is null)
        {
            journal.WriteError(
                "api: WARNING - not running under systemd, so an applied change will stop the daemon, not restart it");
        }

        if (file is { EphemeralInForce: true })
        {
            journal.WriteError(
                $"api: running a one-run configuration applied over the API; a restart returns to {file.ConfigPath}");
        }

        await using var host = new MonitorHost(
            new MonitorHostOptions
            {
                ConfigHandler = configHandler,
                Directory = new UberSdrDirectoryOptions
                {
                    Url = monitor.Directory,
                    Refresh = TimeSpan.FromMinutes(monitor.RefreshMinutes),
                    IqMode = tuning.Mode,
                    WindowLowHz = windowLowHz,
                    WindowHighHz = windowHighHz,
                    Allow = new HashSet<string>(monitor.Allow, StringComparer.OrdinalIgnoreCase),
                    Deny = new HashSet<string>(monitor.Deny, StringComparer.OrdinalIgnoreCase),
                },
                Port = waterfall.Port,
                Bind = config.Bind,
                Modems = modems,
                PublicUrl = monitor.PublicUrl,
                Uplinks = monitor.Uplinks,
                Linger = linger,
                DspRate = dspRate,
                DialHz = waterfall.DialFrequencyHz != 0 ? waterfall.DialFrequencyHz : plan.DialHz,
                Sideband = plan.Sideband,
                FrameLogDirectory = frameLogDirectory,
                Title = waterfall.Title,
                About = waterfall.About,
                LinesPerSecond = waterfall.LinesPerSecond,
                FftSize = waterfall.FftSize,
                IdBeacons = config.IdBeacons,
                DeadFeed = config.DeadFeed,
                OpenInput = (receiver, log, token) => OnDemandUberSdrInput.OpenAsync(
                    receiver.Endpoint, tuning, linger, log, token),
            },
            cancellation.Token);

        int exit = await host.RunAsync().ConfigureAwait(false);
        return restartRequested && exit == 0 ? 1 : exit;
    }

    /// <summary>
    /// The settings page's handler, or null when the site has none.
    /// </summary>
    /// <remarks>
    /// Null without a key, and that is the whole rule. A site is reachable from the internet, so
    /// there is no open mode here whatever a station on a private network may do: with no key the
    /// page and its API are simply not built, and their paths are a 404.
    /// </remarks>
    /// <param name="apiKey">The file's <c>api.key</c>, or empty.</param>
    /// <param name="file">The file this run came from, or null for a run with no page to offer.</param>
    /// <param name="requestRestart">What a change that has been validated and written does next.</param>
    internal static Func<System.Net.HttpListenerContext, string, Task<bool>>? ConfigHandlerFor(
        string apiKey, MonitorFile? file, Action requestRestart, RecentOutput? recent = null)
    {
        if (file is null || string.IsNullOrEmpty(apiKey))
        {
            return null;
        }

        var api = new ConfigApi(
            apiKey, file.ConfigPath, ConfigApi.EphemeralPathFor(file.ConfigPath),
            runningJson: () => file.RunningJson,
            ephemeralInForce: file.EphemeralInForce,
            requestRestart: requestRestart,
            openConfiguration: false);
        if (recent is not null)
        {
            api.ServeReport(recent);
        }

        return api.HandleAsync;
    }

    /// <summary>
    /// Whether a site could keep its frame logs at <paramref name="path"/>, asked without making
    /// anything the site would make: null when it could, else the sentence to show. The same
    /// refusals as <see cref="TryPrepareFrameLogDirectory"/>, and for a directory that does not
    /// exist yet, whether its nearest existing parent can be written to, which is what creating
    /// it needs. Writability is tried with one empty file that is deleted at once, in a
    /// directory that is already there; no directory is created.
    /// </summary>
    internal static string? FrameLogProblem(string path)
    {
        var said = new List<string>();
        if (File.Exists(path)
            || (!Directory.Exists(path)
                && Path.GetExtension(path).Equals(".db", StringComparison.OrdinalIgnoreCase)))
        {
            TryPrepareFrameLogDirectory(path, new StationJournal("", _ => { }, said.Add), out _, create: false);
            return said.Count > 0 ? said[0] : null;
        }

        string? probe = Path.GetFullPath(path);
        while (probe is not null && !Directory.Exists(probe))
        {
            probe = Path.GetDirectoryName(probe);
        }

        if (probe is null)
        {
            return $"The site cannot keep its frame logs at {path}: no part of that path exists.";
        }

        try
        {
            string trial = Path.Combine(probe, $".pdn-write-check-{Guid.NewGuid():N}");
            using (new FileStream(trial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"The site cannot keep its frame logs at {path}: {probe} is not writable by the service "
                + $"({e.Message}). Pick a directory it can write to, or remove \"frameLog\".";
        }

        return null;
    }

    /// <summary>
    /// The directory each station's frame log goes in, created if it is missing.
    /// </summary>
    /// <remarks>
    /// <c>"frameLog"."path"</c> is a file in the single-station flavour and a directory here,
    /// because a monitor writes one log per receiver: <c>frames-m9psy-1.db</c>,
    /// <c>frames-g4eyr.db</c>. Getting that wrong should be one sentence and an exit 2, not a
    /// SQLite error from somewhere inside the first station that happened to be picked.
    /// </remarks>
    internal static bool TryPrepareFrameLogDirectory(
        string path, StationJournal journal, out string directory, bool create = true)
    {
        directory = "";

        if (File.Exists(path))
        {
            journal.WriteError(
                $"\"frameLog\".\"path\" is {path}, which is a file. A monitor keeps one log per "
                + "receiver, so this is a DIRECTORY here and not a file as it is for a single "
                + "station: each receiver gets frames-<slug>.db inside it. Point it at a "
                + "directory, e.g. \"/var/lib/pdn-soundmodem\".");
            return false;
        }

        // A path ending .db that does not exist is a single-station path pasted into a monitor
        // config, and creating a directory called frames.db would be obeying the letter of it and
        // leaving somebody to work out later why their log was not where they put it.
        if (!Directory.Exists(path)
            && Path.GetExtension(path).Equals(".db", StringComparison.OrdinalIgnoreCase))
        {
            journal.WriteError(
                $"\"frameLog\".\"path\" is {path}, which names a database file. A monitor keeps "
                + "one log per receiver and needs a DIRECTORY to put them in - it writes "
                + "frames-<slug>.db inside it. Drop the file name, e.g. "
                + "\"/var/lib/pdn-soundmodem\".");
            return false;
        }

        if (!create)
        {
            return true;
        }

        try
        {
            System.IO.Directory.CreateDirectory(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            journal.WriteError(
                $"cannot use {path} for the frame logs\n"
                + $"  {e.Message}\n"
                + "  Set by \"frameLog\".\"path\", which is a directory in a monitor. The service\n"
                + "  user must be able to write to it; remove the \"frameLog\" section to run\n"
                + "  without one.");
            return false;
        }

        directory = path;
        journal.Write($"frame log: one per receiver under {path}");
        return true;
    }
}
