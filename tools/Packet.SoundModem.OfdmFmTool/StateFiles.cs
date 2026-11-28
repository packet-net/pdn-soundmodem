using Packet.SoundModem.Modems.OfdmFmStation;

namespace Packet.SoundModem.OfdmFmTool;

/// <summary>Where the tool keeps the time of its last identification: under <c>${XDG_STATE_HOME:-$HOME/.local/state}/pdn-ofdm/</c>, one file for each callsign and device. Not a setting.</summary>
internal static class StateFiles
{
    /// <summary>The file for this callsign on this device, or null when there is no home to keep it in.</summary>
    public static string? NotePath(string callsign, string device, Func<string, string?> environment)
    {
        string? state = environment("XDG_STATE_HOME");
        if (string.IsNullOrEmpty(state) || !Path.IsPathRooted(state))
        {
            string? home = environment("HOME");
            if (string.IsNullOrEmpty(home) || !Path.IsPathRooted(home))
            {
                return null;
            }

            state = Path.Combine(home, ".local", "state");
        }

        return Path.Combine(state, "pdn-ofdm", IdentificationNote.FileName(callsign, device));
    }
}
