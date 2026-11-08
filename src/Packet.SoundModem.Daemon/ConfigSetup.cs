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
/// <c>soundmodem.example.json</c> or to the older example in <see cref="LegacyExample"/></i> (parsed, so comments, whitespace and key order do not
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
/// (it opens its station page on every interface and KISS on a fixed port, which a test machine cannot have to itself). An operator never sets
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
    internal static bool NeverConfigured(string configJson) =>
        NeverConfigured(configJson, ExampleText()) || NeverConfigured(configJson, LegacyExample);

    /// <summary>The example earlier versions shipped: it named a sound card and a PTT device most
    /// machines lack, so the service never started. A station still holding it was seeded by an
    /// older package and never set up, so it counts as never configured too.</summary>
    internal const string LegacyExample = """
        {
          "device": "default",
          "modems": [
            { "subChannel": 0, "mode": "afsk1200" }
          ],
          "ptt": { "type": "cm108", "device": "/dev/hidraw0" },
          "waterfall": { "port": 8107 }
        }
        """;

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

    private static readonly Lazy<string> Example = new(ReadExample);

    /// <summary>The shipped example's text: the one in this assembly, or the test seam's file.
    /// Read once, when first needed.</summary>
    internal static string ExampleText() => Example.Value;

    private static string ReadExample()
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

    /// <summary>The line the daemon journals at start-up when the test seam is in force, else
    /// null. A named file that is not there is said, because the shipped example is then used.</summary>
    internal static string? StartUpNotice() => NoticeFor(Environment.GetEnvironmentVariable(ExampleVariable));

    /// <summary>The notice for a value of the variable.</summary>
    internal static string? NoticeFor(string? path) =>
        string.IsNullOrEmpty(path)
            ? null
            : File.Exists(path)
                ? $"api: setup is comparing the configuration with the example in {path} ({ExampleVariable}), not the shipped one"
                : $"api: {ExampleVariable} names {path}, which is not there; using the shipped example";

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
