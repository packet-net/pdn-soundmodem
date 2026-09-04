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
    /// <summary>Runs the monitor. Returns the process exit code.</summary>
    internal static async Task<int> RunAsync(DaemonConfig config)
    {
        MonitorConfig monitor = config.Monitor!;
        WaterfallConfig waterfall = config.Waterfall!;
        var journal = StationJournal.Console();
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
            journal.WriteError($"band plan: WARNING - {warning}");
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

        await using var host = new MonitorHost(
            new MonitorHostOptions
            {
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

        return await host.RunAsync().ConfigureAwait(false);
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
        string path, StationJournal journal, out string directory)
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
