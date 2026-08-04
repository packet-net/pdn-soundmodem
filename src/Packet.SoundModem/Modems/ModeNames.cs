using System.Text;

namespace Packet.SoundModem.Modems;

/// <summary>
/// Turns a modem's own mode string into something to show an operator.
/// </summary>
/// <remarks>
/// <see cref="IModem.Mode"/> reports what the modem actually is, which is more than the mode
/// name you configured: a BPSK modem says <c>bpsk300-il2pc-multi9</c> - the framing it settled
/// on and how many branches its diversity bank has. That is the right thing for a log or a
/// quality frame and the wrong thing for a label on a waterfall, where you want to read
/// "BPSK300 IL2Pc" at a glance and the bank size is not what you are looking for.
/// </remarks>
public static class ModeNames
{
    /// <summary>
    /// A display name for <paramref name="mode"/>, e.g. <c>bpsk300-il2pc-multi9</c> →
    /// "BPSK300 IL2Pc". Unrecognised parts are passed through rather than dropped, so a mode
    /// this has never seen still reads as itself instead of disappearing.
    /// </summary>
    public static string Display(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return "";
        }

        string[] parts = mode.Trim().Split('-', StringSplitOptions.RemoveEmptyEntries);
        var text = new StringBuilder();
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];

            // The diversity bank's branch count is an implementation detail of the receiver,
            // not something an operator is choosing between.
            if (i > 0 && IsBankSuffix(part))
            {
                continue;
            }

            string rendered = i == 0 ? Family(part) : Qualifier(part);
            if (rendered.Length == 0)
            {
                continue;
            }

            if (text.Length > 0)
            {
                text.Append(' ');
            }

            text.Append(rendered);
        }

        return text.ToString();
    }

    /// <summary><c>multi</c> or <c>multi9</c> - the bank's branch count.</summary>
    private static bool IsBankSuffix(string part) =>
        part.StartsWith("multi", StringComparison.OrdinalIgnoreCase)
        && part.AsSpan(5).ToString().All(char.IsAsciiDigit);

    /// <summary>The leading family-and-rate token: afsk1200, bpsk300, c4fsk9600, ms110d…</summary>
    private static string Family(string part) => part.ToLowerInvariant() switch
    {
        "freedv" => "FreeDV",
        "ms110d" => "MS110D",
        "ardop" => "ARDOP",
        "pocsag" => "POCSAG",
        // Everything else is a modulation name run together with its bit rate, and reads best
        // wholly capitalised: afsk1200 → AFSK1200, c4fsk19200 → C4FSK19200.
        _ => part.ToUpperInvariant(),
    };

    /// <summary>A trailing token: the framing, or a waveform number.</summary>
    private static string Qualifier(string part)
    {
        string lower = part.ToLowerInvariant();
        return lower switch
        {
            // IL2P with the trailing CRC is written IL2Pc throughout the project and on air.
            "il2pc" => "IL2Pc",
            "il2p" => "IL2P",
            "nocrc" => "(no CRC)",
            "fx25" => "FX.25",
            "fx25rx" => "FX.25 (RX only)",
            _ when lower.StartsWith("wn", StringComparison.Ordinal) => part.ToUpperInvariant(),
            // freedv-datac1 and friends: the waveform names are lower-case in the standard.
            _ when lower.StartsWith("datac", StringComparison.Ordinal) => lower,
            _ => part,
        };
    }
}
