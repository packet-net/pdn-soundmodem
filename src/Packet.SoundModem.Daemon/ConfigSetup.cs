using System.Text.Json;
using System.Text.Json.Nodes;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// Whether a station has ever been set up. The configuration page opens its guided setup by
/// itself on a station that has not, and nowhere else decides this.
/// </summary>
/// <remarks>
/// <para><b>The definition.</b> A station has never been configured when its running
/// configuration file either (a) <i>chooses nothing</i>: no <c>modems</c>, no <c>ptt</c>, no
/// <c>rig</c> and no <c>ardop</c> section, which is the empty file, a file of comments, or a file
/// of plumbing only (a port, a title); or (b) <i>is the shipped example</i>: the packaging seeds
/// <c>/etc/pdn-soundmodem/soundmodem.json</c> from <c>soundmodem.example.json</c> on first install
/// (<c>packaging/build-deb.sh</c>), and an operator who has not touched it has a file that
/// chooses exactly what the example chooses: the <c>default</c> sound device, one
/// <c>afsk1200</c> modem on sub-channel 0 and a CM108 PTT on <c>/dev/hidraw0</c>. Only those
/// choices are compared, so an example that gained a comment or a port in a later version still
/// counts, and one changed value among them (another mode, another hidraw, a second modem, a
/// named sound card) makes it a configured station.</para>
/// <para>A <c>monitor</c> file is never "never configured": it is a site, not a station, and
/// does not serve the page at all.</para>
/// </remarks>
internal static class ConfigSetup
{
    private static readonly JsonDocumentOptions Lenient = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The definition above, applied to the text of a configuration file.</summary>
    internal static bool NeverConfigured(string configJson)
    {
        JsonNode? parsed;
        try
        {
            // A file with nothing in it, or only comments, has no first token at all.
            // Not the final block, so that running out of input is "no token yet" and not an error.
            var reader = new Utf8JsonReader(
                System.Text.Encoding.UTF8.GetBytes(configJson),
                isFinalBlock: false,
                new JsonReaderState(new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }));
            if (!reader.Read())
            {
                return true;
            }

            parsed = JsonNode.Parse(configJson, null, Lenient);
        }
        catch (JsonException)
        {
            return false;
        }

        if (parsed is null)
        {
            return true;
        }

        if (parsed is not JsonObject doc || Get(doc, "monitor") is not null)
        {
            return false;
        }

        JsonNode? modems = Get(doc, "modems");
        bool hasModems = modems is JsonArray { Count: > 0 };
        if (!hasModems && Get(doc, "ptt") is null && Get(doc, "rig") is null && Get(doc, "ardop") is null)
        {
            return true;
        }

        return IsTheShippedExample(doc, modems);
    }

    private static bool IsTheShippedExample(JsonObject doc, JsonNode? modems)
    {
        if (Get(doc, "rig") is not null || Get(doc, "captureDevice") is not null
            || Get(doc, "playbackDevice") is not null || Get(doc, "ardop") is not null)
        {
            return false;
        }

        JsonNode? device = Get(doc, "device");
        if (device is not null && !Is(device, "default"))
        {
            return false;
        }

        if (Get(doc, "ptt") is not JsonObject ptt || ptt.Count != 2
            || !Is(Get(ptt, "type"), "cm108") || !Is(Get(ptt, "device"), "/dev/hidraw0"))
        {
            return false;
        }

        if (modems is not JsonArray { Count: 1 } list || list[0] is not JsonObject modem)
        {
            return false;
        }

        JsonNode? sub = Get(modem, "subChannel");
        bool subIsZero = sub is null || (sub is JsonValue v && v.TryGetValue(out int n) && n == 0);
        return modem.Count <= 2 && subIsZero && Is(Get(modem, "mode"), "afsk1200");
    }

    private static bool Is(JsonNode? node, string text) =>
        node is JsonValue v && v.TryGetValue(out string? s)
        && string.Equals(s, text, StringComparison.OrdinalIgnoreCase);

    /// <summary>A key by name, the way the loader reads them: any case.</summary>
    private static JsonNode? Get(JsonObject obj, string name)
    {
        foreach (KeyValuePair<string, JsonNode?> p in obj)
        {
            if (string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return p.Value;
            }
        }

        return null;
    }
}
