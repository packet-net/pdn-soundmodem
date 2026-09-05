using System.Text.Json;
using System.Text.Json.Nodes;
using Packet.SoundModem.Survey;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// Turns a <see cref="ModemProposal"/> into the configuration document that would act on it.
/// </summary>
/// <remarks>
/// <para><b>One door.</b> This builds a complete configuration and nothing else: applying it
/// goes through <see cref="ConfigApi"/> exactly as an operator's own edit would, so a proposal
/// is validated by the same code, refused with the same wording, and is ephemeral by the same
/// default. There is deliberately no second way to change a station, because a second way is a
/// second set of rules to keep in step and the first set is the one with the safety property.
/// </para>
/// <para><b>Always an addition, never a rewrite.</b> Both kinds of proposal come out as a new
/// modem entry. That is obvious for a clear frequency, and it is the right answer for a framing
/// change too: the modem already there is reading somebody, and changing its framing to catch a
/// station it cannot read would drop the stations it can. Two entries on one frequency running
/// different framings is a configuration a station is expected to hold - it is what the
/// GB7BWR-2 finding of 2026-08-03 asked for and what PD4R-12 got on 2026-08-24.</para>
/// </remarks>
internal static class ProposedConfig
{
    /// <summary>KISS addresses a sub-channel by a nibble, so this is the ceiling on modems and
    /// not a policy this code gets to choose.</summary>
    public const int MaxSubChannel = 15;

    /// <summary>How a configuration document is read: as JSONC, which is what one is.</summary>
    private static readonly JsonDocumentOptions Jsonc = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// The configuration <paramref name="running"/> would become, with a modem added for
    /// <paramref name="proposal"/>. Null when it cannot be built, with
    /// <paramref name="why"/> saying so in an operator's terms.
    /// </summary>
    public static string? Amend(string running, ModemProposal proposal, out string why)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        why = "";

        if (proposal.RfFrequencyHz is not double rf)
        {
            why = "the station is running in audio frequencies only, so there is no band "
                + "frequency to configure; set \"dialFrequency\" (or run a band plan) first";
            return null;
        }

        JsonNode? root;
        try
        {
            // The same reader DaemonConfig loads a file with. A config file here is JSONC and
            // most are full of comments, and a strict parse made every proposal on such a station
            // come back as "the running configuration will not parse" at the first "//".
            root = JsonNode.Parse(running, nodeOptions: null, Jsonc);
        }
        catch (JsonException e)
        {
            why = $"the running configuration will not parse: {e.Message}";
            return null;
        }

        if (root is not JsonObject document || document["modems"] is not JsonArray modems)
        {
            why = "the running configuration has no \"modems\" array to add to";
            return null;
        }

        var used = new HashSet<int>();
        foreach (JsonNode? entry in modems)
        {
            if (entry?["subChannel"]?.GetValue<int>() is int sub)
            {
                used.Add(sub);
            }
        }

        int free = 0;
        while (used.Contains(free))
        {
            free++;
        }

        if (free > MaxSubChannel)
        {
            why = $"every sub-channel 0-{MaxSubChannel} is in use; a KISS nibble addresses no "
                + "more, so something has to come out before anything goes in";
            return null;
        }

        modems.Add(new JsonObject
        {
            ["subChannel"] = free,
            ["mode"] = proposal.Mode,
            ["rfFrequency"] = Math.Round(rf),
        });

        return document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
