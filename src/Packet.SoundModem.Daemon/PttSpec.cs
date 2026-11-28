namespace Packet.SoundModem.Daemon;

/// <summary>
/// The <c>--ptt</c> command-line spelling: <c>serial:DEVICE[:rts|:dtr]</c> or <c>cm108:HIDRAW[:GPIO]</c>. One reading, shared by the daemon and by tools that open a radio the same way.
/// </summary>
internal static class PttSpec
{
    /// <summary>What is said for a spec that is not of the form above.</summary>
    internal const string Expected = "--ptt expects serial:<dev>[:rts|:dtr] or cm108:<hidraw>[:gpio]";

    /// <summary>Reads a spec. Null, with the words for the operator in <paramref name="problem"/>, when it is not one.</summary>
    public static PttConfig? TryParse(string spec, out string problem)
    {
        problem = Expected;
        string[] parts = spec.Split(':');
        if (parts.Length < 2)
        {
            return null;
        }

        int? gpio = null;
        if (parts[0] == "cm108" && parts.Length > 2)
        {
            if (!int.TryParse(parts[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int pin))
            {
                return null;
            }

            gpio = pin;
        }

        problem = "";
        return new PttConfig
        {
            Type = parts[0],
            Device = parts[1],
            Line = parts[0] == "serial" && parts.Length > 2 ? parts[2] : null,
            Gpio = gpio,
        };
    }
}
