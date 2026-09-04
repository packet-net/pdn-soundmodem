using System.Security.Cryptography;
using System.Text;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// <c>pdn-soundmodem --uplink-token CALLSIGN</c>: mints one uplink token for one station and
/// prints it with the hash that goes in the monitor's config.
/// </summary>
/// <remarks>
/// <para>Fifteen lines, and it is the difference between tokens that are 256 random bits and
/// tokens that are somebody's cat. It exists because the site issues the token rather than the
/// station minting one: a station behind NAT has no public URL for the site to call back on, so
/// possession of the token is the whole credential, and it has to be worth possessing.</para>
/// <para>Both halves are printed together on purpose. The site owner keeps the hash, which is
/// what a leaked config file gives away and which is useless on its own, and hands the token to
/// the station's operator once. Nothing here writes either of them anywhere.</para>
/// </remarks>
internal static class UplinkToken
{
    /// <summary>The prefix every token carries, so one is recognisable in a config file.</summary>
    internal const string Prefix = "pdnsm_";

    /// <summary>
    /// Prints a fresh token for one station and its hash. Returns the process exit code.
    /// </summary>
    /// <param name="callsign">
    /// The station the token is for, which is the callsign the printed entry carries. Asked for
    /// rather than left as an example, because the entry is pasted into a config as it stands and
    /// an example callsign in that position is one more thing somebody has to remember to change
    /// - and the example this printed was a real station on this site.
    /// </param>
    internal static int Print(string? callsign) => Print(callsign, Console.Out, Console.Error);

    /// <summary>The same, writing where a test can read it back.</summary>
    /// <param name="callsign">The station the token is for.</param>
    /// <param name="output">Where the token and the entry go.</param>
    /// <param name="error">Where a missing or unusable callsign is said.</param>
    internal static int Print(string? callsign, TextWriter output, TextWriter error)
    {
        if (!DaemonConfig.IsPlausibleCallsign(callsign))
        {
            // Ascii because this came off a command line: the sentence is going to a terminal and
            // possibly to a journal, and both want plain bytes.
            error.WriteLine(
                "--uplink-token needs the callsign of the station the token is for"
                + (string.IsNullOrWhiteSpace(callsign)
                    ? ""
                    : $", and \"{UberSdrDirectory.Ascii(callsign)}\" is not one")
                + ". One to six letters and digits with an optional -SSID, e.g. "
                + "\"pdn-soundmodem --uplink-token GB7RDG-2\". The callsign goes in the entry "
                + "this prints, and the token is issued against it.");
            return 2;
        }

        callsign = callsign!.ToUpperInvariant();
        string slug = UberSdrDirectory.SlugForCallsign(callsign);
        (string token, string hash) = Mint();

        output.WriteLine($"A new uplink token for {callsign}, and the hash of it for this site.");
        output.WriteLine();
        output.WriteLine($"Give this to {callsign}'s operator, once, for their \"publish\" block:");
        output.WriteLine();
        output.WriteLine($"  \"token\": \"{token}\"");
        output.WriteLine();
        output.WriteLine("Keep this in the monitor's own config, under \"monitor\".\"uplinks\":");
        output.WriteLine();
        output.WriteLine("  {");
        output.WriteLine($"    \"callsign\": \"{callsign}\",");
        output.WriteLine($"    \"slug\": \"{slug}\",");
        output.WriteLine($"    \"tokenSha256\": \"{hash}\"");
        output.WriteLine("  }");
        output.WriteLine();
        output.WriteLine($"That station's page will be at /r/{slug}/.");
        output.WriteLine(
            "The site stores only the hash and never the token, so this is the one time the "
            + "token is shown.");
        output.WriteLine("Nothing has been written to any file. See CONFIG.md, \"monitor.uplinks\".");
        return 0;
    }

    /// <summary>
    /// A fresh token and its SHA-256, as the config file wants them.
    /// </summary>
    /// <remarks>
    /// 256 bits from <see cref="RandomNumberGenerator"/>, URL-safe base64 with the padding
    /// stripped, which is 43 characters after the prefix. There is no dictionary to defend
    /// against at that size, which is the whole reason the hash at rest is plain SHA-256 with no
    /// work factor: a KDF would only cost the monitor time on every connection.
    /// </remarks>
    internal static (string Token, string Hash) Mint()
    {
        Span<byte> entropy = stackalloc byte[32];
        RandomNumberGenerator.Fill(entropy);
        string token = Prefix + Convert.ToBase64String(entropy)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
        return (token, Hash(token));
    }

    /// <summary>The hash a monitor's config holds for a token, lower-case hex.</summary>
    internal static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
