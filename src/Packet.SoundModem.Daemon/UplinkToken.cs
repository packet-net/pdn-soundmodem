using System.Security.Cryptography;
using System.Text;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// <c>pdn-soundmodem --uplink-token</c>: mints one uplink token and prints it with the hash that
/// goes in the monitor's config.
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

    /// <summary>Prints a fresh token and its hash. Returns the process exit code.</summary>
    internal static int Print()
    {
        (string token, string hash) = Mint();

        Console.WriteLine("A new uplink token, and the hash of it for the monitor's config.");
        Console.WriteLine();
        Console.WriteLine("Give this to the station's operator, once, for their \"publish\" block:");
        Console.WriteLine();
        Console.WriteLine($"  \"token\": \"{token}\"");
        Console.WriteLine();
        Console.WriteLine("Keep this in the monitor's own config, under \"monitor\".\"uplinks\":");
        Console.WriteLine();
        Console.WriteLine("  {");
        Console.WriteLine("    \"callsign\": \"GB7RDG-2\",");
        Console.WriteLine("    \"slug\": \"gb7rdg-2\",");
        Console.WriteLine($"    \"tokenSha256\": \"{hash}\"");
        Console.WriteLine("  }");
        Console.WriteLine();
        Console.WriteLine(
            "The site stores only the hash and never the token, so this is the one time the "
            + "token is shown.");
        Console.WriteLine("Nothing has been written to any file. See CONFIG.md, \"monitor.uplinks\".");
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
