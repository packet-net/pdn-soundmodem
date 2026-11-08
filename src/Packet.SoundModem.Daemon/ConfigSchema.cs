using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// A description of every key the configuration file has, for the configuration page to draw
/// its form from.
/// </summary>
/// <remarks>
/// <para><b>Made from the classes, not written down.</b> The page has no list of settings of its
/// own: it asks for this and draws a field for each key here. The description is read off the
/// deserialiser's own view of <see cref="DaemonConfig"/>, with the options the file is read with,
/// so a key added to a config class is on the page the next time it is built and a key taken
/// away is gone from it. Nobody has to remember the page.</para>
/// <para><b>Shape.</b> <c>types</c> maps a class name to its keys, and <c>root</c> names the
/// class the file is. Each key has its JSON <c>name</c>, a <c>kind</c> (<c>string</c>,
/// <c>integer</c>, <c>number</c>, <c>boolean</c>, <c>object</c>, <c>array</c> or <c>json</c>),
/// <c>nullable</c> when it may be absent, the value a fresh object carries as <c>default</c>,
/// <c>ref</c> for an object, <c>items</c> for an array, <c>choices</c> where the code knows them,
/// <c>secret</c> where the value is never served, and the reference page's <c>help</c>,
/// <c>docType</c> and <c>docDefault</c> where it has a row for the key.</para>
/// </remarks>
internal static class ConfigSchema
{
    /// <summary>The options the file is read with, read-only so the type metadata can be asked for.</summary>
    private static readonly Lazy<JsonSerializerOptions> Options = new(() =>
    {
        var options = new JsonSerializerOptions(DaemonConfig.Options);
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    });

    private static readonly Lazy<string> Cached = new(() =>
        Build().ToJsonString(new JsonSerializerOptions { WriteIndented = false }));

    /// <summary>The description as JSON, built once per process.</summary>
    public static string Json => Cached.Value;

    /// <summary>Builds the description.</summary>
    public static JsonObject Build()
    {
        JsonSerializerOptions options = Options.Value;
        var types = new JsonObject();
        var canonical = new Dictionary<Type, string>();
        var pending = new Queue<(Type Type, string Path)>();
        pending.Enqueue((typeof(DaemonConfig), ""));
        canonical[typeof(DaemonConfig)] = "";

        while (pending.Count > 0)
        {
            (Type type, string path) = pending.Dequeue();
            var keys = new JsonArray();
            JsonTypeInfo info = options.GetTypeInfo(type);
            object? fresh = info.CreateObject?.Invoke();

            foreach (JsonPropertyInfo property in Settable(info))
            {
                string keyPath = path.Length == 0 ? property.Name : $"{path}.{property.Name}";
                JsonObject key = Describe(property, keyPath, fresh, options, nested =>
                {
                    if (canonical.TryAdd(nested.Type, nested.Path))
                    {
                        pending.Enqueue(nested);
                    }
                });

                AddHelp(key, keyPath, canonical[type]);
                key["name"] = DisplayName(property, key);
                if (ConfigKeys.For(type.Name, key["name"]!.GetValue<string>()) is { } keyInfo)
                {
                    ConfigKeys.AddTo(key, keyInfo);
                }

                keys.Add(key);
            }

            types[type.Name] = new JsonObject { ["keys"] = keys };
        }

        return new JsonObject
        {
            ["root"] = nameof(DaemonConfig),
            ["hidden"] = ConfigApi.Hidden,
            ["areas"] = ConfigKeys.AreasJson(),
            ["deviceKinds"] = new JsonArray([.. ConfigKeys.DeviceKinds.Select(k => (JsonNode)k)]),
            ["types"] = types,
        };
    }

    /// <summary>
    /// The properties a file can set: what the deserialiser would write, less the extension-data
    /// bag of unknown keys and anything ignored. A get-only property such as
    /// <see cref="DaemonConfig.CaptureDeviceInUse"/> is served when serialising but never read.
    /// </summary>
    internal static IEnumerable<JsonPropertyInfo> Settable(JsonTypeInfo info) =>
        info.Properties.Where(p => p.Set is not null && !p.IsExtensionData && !IsIgnored(p));

    /// <summary>The kind of value a config type is, and the config class it is made of if any.</summary>
    internal static (string Kind, Type? Class, Type? Item) KindOf(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        if (type == typeof(string))
        {
            return ("string", null, null);
        }

        if (type == typeof(bool))
        {
            return ("boolean", null, null);
        }

        if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(uint))
        {
            return ("integer", null, null);
        }

        if (type == typeof(double) || type == typeof(float) || type == typeof(decimal))
        {
            return ("number", null, null);
        }

        if (type.IsEnum)
        {
            return ("string", null, null);
        }

        if (type == typeof(JsonElement) || type == typeof(JsonNode) || type == typeof(object))
        {
            return ("json", null, null);
        }

        Type? element = type.IsArray
            ? type.GetElementType()
            : type.GetInterfaces().Concat([type])
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                ?.GetGenericArguments()[0];
        if (element is not null)
        {
            return ("array", null, element);
        }

        return ("object", type, null);
    }

    private static JsonObject Describe(
        JsonPropertyInfo property, string keyPath, object? fresh, JsonSerializerOptions options,
        Action<(Type Type, string Path)> found)
    {
        Type type = property.PropertyType;
        (string kind, Type? cls, Type? item) = KindOf(type);
        var key = new JsonObject
        {
            ["name"] = property.Name,
            ["kind"] = kind,
            ["nullable"] = !type.IsValueType || Nullable.GetUnderlyingType(type) is not null,
        };

        if (fresh is not null && kind != "json")
        {
            object? value = property.Get?.Invoke(fresh);
            key["default"] = value is null ? null : JsonSerializer.SerializeToNode(value, type, options);
        }

        if (cls is not null)
        {
            key["ref"] = cls.Name;
            found((cls, keyPath));
        }

        if (item is not null)
        {
            (string itemKind, Type? itemClass, _) = KindOf(item);
            var items = new JsonObject { ["kind"] = itemKind };
            if (itemClass is not null)
            {
                items["ref"] = itemClass.Name;
                found((itemClass, keyPath));
            }

            key["items"] = items;
        }

        if (Choices(type, property.Name, property.DeclaringType) is { } choices)
        {
            key["choices"] = choices;
        }

        if (ConfigApi.IsSecret(keyPath))
        {
            key["secret"] = true;
        }

        return key;
    }

    /// <summary>Values the code itself knows a key takes. Offered, not enforced: the daemon's
    /// validation is the one that decides, and a plugin mode is not in the catalogue.</summary>
    private static JsonArray? Choices(Type type, string name, Type? owner)
    {
        Type bare = Nullable.GetUnderlyingType(type) ?? type;
        if (bare.IsEnum)
        {
            return [.. Enum.GetNames(bare).Select(n => (JsonNode)n)];
        }

        // Only a modem's mode is a catalogue name; a Flex slice mode or an SDRconnect stream type
        // is a different list that the code does not hold.
        if (bare == typeof(string) && string.Equals(name, "mode", StringComparison.OrdinalIgnoreCase)
            && owner == typeof(ModemConfig))
        {
            return [.. ModemCatalog.KnownModes.Append("ardop").Select(n => (JsonNode)n)];
        }

        if (bare == typeof(string) && string.Equals(name, "sideband", StringComparison.OrdinalIgnoreCase))
        {
            return ["usb", "lsb", "fm"];
        }

        return (owner?.Name, name.ToLowerInvariant()) switch
        {
            ("PttConfig", "type") => ["serial", "cm108", "rigctld"],
            ("PttConfig", "line") => ["rts", "dtr"],
            ("PublishConfig", "frames") => ["always", "watched"],
            ("IqPipeConfig", "format") => ["cu8", "cs8", "cs16", "cf32"],
            _ => null,
        };
    }

    /// <summary>
    /// The reference page's row for a key. Looked up where the key is first, then under the path
    /// its class was first met at (<c>monitor.modems[]</c> entries are described under
    /// <c>modems</c>), then in the sections that hold it (<c>alsa.mixer</c>'s keys are in
    /// <c>alsa</c>'s table).
    /// </summary>
    internal static ConfigHelp.Row? HelpFor(string keyPath, string canonicalParent)
    {
        int dot = keyPath.LastIndexOf('.');
        string leaf = dot < 0 ? keyPath : keyPath[(dot + 1)..];
        string parent = dot < 0 ? "" : keyPath[..dot];

        foreach (string scope in Scopes(parent).Concat(Scopes(canonicalParent)).Distinct())
        {
            if (ConfigHelp.In(scope) is { } rows && rows.TryGetValue(leaf, out ConfigHelp.Row? row))
            {
                return row;
            }
        }

        return null;

        static IEnumerable<string> Scopes(string path)
        {
            if (path.Length == 0)
            {
                yield return "";
                yield break;
            }

            string[] parts = path.Split('.');
            for (int i = 0; i < parts.Length; i++)
            {
                yield return string.Join('.', parts[i..]);
            }

            for (int i = parts.Length - 1; i > 0; i--)
            {
                yield return string.Join('.', parts[..i]);
            }
        }
    }

    /// <summary>The row whose key <paramref name="keyPath"/> is the old name of, if the reference
    /// says it is one: "`old`, its name in 0.90.0, is still read".</summary>
    private static ConfigHelp.Row? AliasFor(string keyPath, string canonicalParent)
    {
        int dot = keyPath.LastIndexOf('.');
        string leaf = dot < 0 ? keyPath : keyPath[(dot + 1)..];
        string parent = dot < 0 ? "" : keyPath[..dot];
        string marker = $"`{leaf}`, its name in";
        foreach (string scope in new[] { parent, canonicalParent })
        {
            if (ConfigHelp.In(scope) is { } rows
                && rows.Values.FirstOrDefault(r => r.Text.Contains(marker, StringComparison.OrdinalIgnoreCase)) is { } row)
            {
                return row;
            }
        }

        return null;
    }

    /// <summary>
    /// The key as the page writes it into a file. The file is read without regard to case and
    /// with no naming policy, so the deserialiser's own name is the C# one (<c>KissPort</c>);
    /// the page uses the reference page's spelling instead (<c>kissPort</c>, <c>ubersdr</c>), and
    /// camel case for a key the reference has no row for.
    /// </summary>
    private static string DisplayName(JsonPropertyInfo property, JsonObject key)
    {
        bool named = property.AttributeProvider?
            .GetCustomAttributes(typeof(JsonPropertyNameAttribute), inherit: false).Length > 0;
        if (named)
        {
            return property.Name;
        }

        if (key["docKey"]?.GetValue<string>() is string doc
            && string.Equals(doc, property.Name, StringComparison.OrdinalIgnoreCase))
        {
            key.Remove("docKey");
            return doc;
        }

        key.Remove("docKey");
        return JsonNamingPolicy.CamelCase.ConvertName(property.Name);
    }

    private static void AddHelp(JsonObject key, string keyPath, string canonicalParent)
    {
        if (HelpFor(keyPath, canonicalParent) is null && AliasFor(keyPath, canonicalParent) is { } current)
        {
            // An old name still read for compatibility ("`lfoOffsetHz`, its name in 0.90.0, is
            // still read"). The page shows it only in a file that has it, and says what it became.
            key["aliasOf"] = current.Key;
            key["help"] = $"The old name of `{current.Key}`, still read. Use `{current.Key}` instead.";
            return;
        }

        if (HelpFor(keyPath, canonicalParent) is { } row)
        {
            key["help"] = row.Text;
            key["docKey"] = row.Key;
            key["docType"] = row.Type;
            key["docDefault"] = row.Default;
        }
    }

    private static bool IsIgnored(JsonPropertyInfo property) =>
        property.AttributeProvider?
            .GetCustomAttributes(typeof(JsonIgnoreAttribute), inherit: false)
            .OfType<JsonIgnoreAttribute>()
            .Any(a => a.Condition == JsonIgnoreCondition.Always) == true;
}
