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
