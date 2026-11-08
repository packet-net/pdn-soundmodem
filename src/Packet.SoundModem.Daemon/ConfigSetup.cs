using System.Text.Json;
using System.Text.Json.Nodes;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// Whether a station has ever been set up. The configuration page opens its guided setup by
/// itself on a station that has not, and nowhere else decides this.
/// </summary>
/// <remarks>
/// <para><b>The definition, and nothing wider.</b> A station has never been configured when its
/// running configuration file is <i>semantically equal to the shipped
/// <c>soundmodem.example.json</c></i> (parsed, so comments, whitespace and key order do not
/// matter, and key names are compared in any case) or is empty (<c>{}</c>, or nothing at all).
/// The packaging seeds <c>/etc/pdn-soundmodem/soundmodem.json</c> from the example on first
/// install (<c>packaging/build-deb.sh</c>), so an untouched file is exactly that. Anything else
/// is a station somebody set up: a device alone, a Flex, a web receiver, the example with one
/// value changed. A completed setup never counts either, because it always writes the KISS port,
/// which the example does not have.</para>
/// <para>The example is the copy compiled into this assembly, so the definition cannot drift from
/// the file; a test checks the two are one. The only other source is the
/// <c>PDN_SOUNDMODEM_EXAMPLE_CONFIG</c> environment variable, which names a file to compare with
/// instead: a seam for tests that run the real daemon, which cannot start on the shipped example
/// (it names a PTT device and a sound card the test machine has not got). An operator never sets
/// it.</para>
/// </remarks>
internal static class ConfigSetup
{
    /// <summary>The variable that names an example file to compare with, for tests.</summary>
    internal const string ExampleVariable = "PDN_SOUNDMODEM_EXAMPLE_CONFIG";

    private static readonly JsonDocumentOptions Lenient = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The definition above, applied to the text of a configuration file.</summary>
    internal static bool NeverConfigured(string configJson) => NeverConfigured(configJson, ExampleText());

    /// <summary>The definition, against a given example.</summary>
    internal static bool NeverConfigured(string configJson, string exampleJson)
    {
        JsonNode? doc;
        JsonNode? example;
        try
        {
            if (IsBlank(configJson))
            {
                return true;
            }

            doc = JsonNode.Parse(configJson, null, Lenient);
            example = JsonNode.Parse(exampleJson, null, Lenient);
        }
        catch (JsonException)
        {
            return false;
        }

        if (doc is JsonObject { Count: 0 } || doc is null)
        {
            return true;
        }

        return Equal(doc, example);
    }

    /// <summary>The shipped example's text: the one in this assembly, or the test seam's file.</summary>
    internal static string ExampleText()
    {
        if (Environment.GetEnvironmentVariable(ExampleVariable) is { Length: > 0 } path && File.Exists(path))
        {
            return File.ReadAllText(path);
        }

        using Stream? stream = typeof(ConfigSetup).Assembly.GetManifestResourceStream("soundmodem.example.json");
        if (stream is null)
        {
            return "{}";
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Nothing in the text but whitespace and comments: no first token.</summary>
    private static bool IsBlank(string text)
    {
        // Not the final block, so that running out of input is "no token yet" and not an error.
        var reader = new Utf8JsonReader(
            System.Text.Encoding.UTF8.GetBytes(text),
            isFinalBlock: false,
            new JsonReaderState(new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }));
        return !reader.Read();
    }

    /// <summary>Equal as parsed JSON, with object keys compared in any case.</summary>
    private static bool Equal(JsonNode? a, JsonNode? b)
    {
        switch (a, b)
        {
            case (null, null):
                return true;
            case (JsonObject x, JsonObject y):
                if (x.Count != y.Count)
                {
                    return false;
                }

                foreach (KeyValuePair<string, JsonNode?> p in x)
                {
                    KeyValuePair<string, JsonNode?> match = y.FirstOrDefault(
                        q => string.Equals(q.Key, p.Key, StringComparison.OrdinalIgnoreCase));
                    if (match.Key is null || !Equal(p.Value, match.Value))
                    {
                        return false;
                    }
                }

                return true;
            case (JsonArray x, JsonArray y):
                return x.Count == y.Count && x.Zip(y).All(pair => Equal(pair.First, pair.Second));
            case (JsonValue x, JsonValue y):
                return x.ToJsonString() == y.ToJsonString();
            default:
                return false;
        }
    }
}
