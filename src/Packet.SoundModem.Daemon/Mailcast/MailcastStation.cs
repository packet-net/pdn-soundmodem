using System.Globalization;
using Packet.SoundModem.FlexRadio;

namespace Packet.SoundModem.Daemon;

/// <summary>The station's side of the mailcast receiver: what start-up knows about the radio,
/// turned into the <see cref="MailcastRadio"/> the placement is decided on.</summary>
internal static class MailcastStation
{
    /// <summary>
    /// What this station hears. A headless Flex hears whatever its slice filter is opened to, and
    /// that filter is the daemon's to set, up to <see cref="Passband.WideCeilingHz"/>; a web
    /// receiver hears its configured SSB window; anything else is taken to hear the band plan's
    /// window, or an ordinary SSB passband without one, which is all that can be known of a rig
    /// the daemon does not set the filters of.
    /// </summary>
    internal static MailcastRadio RadioFor(
        bool deviceIsFlex, bool flexIsHeadless, bool deviceIsUberSdr, RfPlan.Result? bandPlan,
        double? dialFrequency, double? receiveDialHz, string sideband, FlexTuning flexTuning,
        UberSdrConfig? uberSdrConfig, bool hasRig)
    {
        if (flexIsHeadless)
        {
            double? dial = bandPlan?.DialHz
                ?? (double.TryParse(flexTuning.Frequency, NumberStyles.Float, CultureInfo.InvariantCulture, out double mhz) ? Math.Round(mhz * 1e6) : null);
            return new MailcastRadio(
                MailcastRadioKind.FlexHeadless, dial, bandPlan?.Sideband ?? sideband,
                Passband.Nominal.LowHz, Passband.WideCeilingHz, hasRig);
        }

        if (deviceIsUberSdr)
        {
            return new MailcastRadio(
                MailcastRadioKind.UberSdr, receiveDialHz, bandPlan?.Sideband ?? sideband,
                uberSdrConfig?.SsbLowHz ?? 150, uberSdrConfig?.SsbHighHz ?? 3450, hasRig);
        }

        Passband window = bandPlan?.Window ?? Passband.Nominal;
        return new MailcastRadio(
            deviceIsFlex ? MailcastRadioKind.FlexAttach : MailcastRadioKind.SoundCard,
            bandPlan?.DialHz ?? dialFrequency, bandPlan?.Sideband ?? sideband, window.LowHz, window.HighHz, hasRig);
    }

    /// <summary>
    /// Why a configuration's mailcast section could not start, or null if it could: where the
    /// receiver would listen (<see cref="MailcastPlacement.Decide"/>) and whether its state
    /// directory can be written. The same two checks start-up makes, made from the file alone so
    /// that <c>POST /api/config</c> refuses what the restart would.
    /// </summary>
    /// <param name="config">The configuration, already loaded.</param>
    /// <param name="bandPlan">Its band plan, or null for a station placed by audio centre.</param>
    /// <param name="configPath">Where the configuration lives (for the state directory beside it).</param>
    internal static string? Problem(DaemonConfig config, RfPlan.Result? bandPlan, string? configPath)
    {
        if (config.Mailcast is not { } mailcast)
        {
            return null;
        }

        bool flex = FlexDevice.IsFlex(config.Device);
        bool headless = flex && FlexDevice.Parse(config.Device).Headless;
        bool uberSdr = Packet.SoundModem.UberSdr.UberSdrDevice.IsUberSdr(config.Device);
        var flexTuning = new FlexTuning
        {
            Frequency = config.Flex?.Frequency ?? "14.100000",
            Mode = config.Flex?.Mode ?? "DIGU",
        };
        string sideband = headless && RfPlan.SidebandForSliceMode(flexTuning.Mode) is { } implied ? implied : config.Sideband;
        MailcastRadio radio = RadioFor(
            flex, headless, uberSdr, bandPlan, config.DialFrequency, bandPlan?.DialHz ?? config.DialFrequency,
            sideband, flexTuning, config.UberSdr, hasRig: config.Rig is not null);
        if (MailcastPlacement.Decide(mailcast, radio, MailcastPlacement.HalfWidthHz(), out string? refusal) is not { } placement)
        {
            return refusal;
        }

        if (!MailcastPlacement.CentreIsConstructible(placement.AudioCentreHz))
        {
            return $"mailcast: the signal's audio centre would be {placement.AudioCentreHz:F0} Hz, but the "
                + "receive modem cannot be moved that close to the edge of its own occupied band (almost "
                + "2.9 kHz wide) without folding noise over DC. Try a dial giving a centre nearer 2000 Hz, "
                + "or measure the filter again - a wider or DATA filter usually centres higher.";
        }

        return StateDirectoryProblem(mailcast.StateDirectoryFor(StationStateDirectory(configPath)));
    }

    /// <summary>Why the receiver could not keep its files in <paramref name="directory"/>, or null
    /// if it can: the folder is made if need be and a file written to it and removed.</summary>
    internal static string? StateDirectoryProblem(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string probe = Path.Combine(directory, $".write-check-{Environment.ProcessId}.tmp");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"mailcast: cannot keep its state in {directory}: {e.Message}. Make it writable by "
                + "the service, or set \"mailcast\".\"stateDirectory\" to a folder that is.";
        }
    }

    /// <summary>The station's state directory: systemd's, else beside the config file, the same
    /// rule the rig's restore file and the mixer state file follow.</summary>
    internal static string StationStateDirectory(string? configPath) =>
        StateDirectory.Current
            ?? (Path.GetDirectoryName(configPath ?? "") is { Length: > 0 } beside ? beside : ".");

    /// <summary>The journal sink for the receiver: problems to the error stream, the rest to the
    /// ordinary one, as every other part of the station does.</summary>
    internal static Action<string> Journal(StationJournal journal) =>
        line =>
        {
            if (line.Contains("WARNING", StringComparison.Ordinal))
            {
                journal.WriteError(line);
            }
            else
            {
                journal.Write(line);
            }
        };
}
