namespace Packet.SoundModem.Modems;

/// <summary>
/// The modes that arrived at run time, from assemblies this repository has never heard of.
/// <see cref="ModemCatalog"/> consults this after its own built-ins, so a plugin mode answers the
/// same questions and builds the same way without any of it being compiled in.
/// </summary>
/// <remarks>
/// <para>Registration is process-global and one-way per plugin: a station loads its plugins at
/// start-up and runs. It is not a service locator to be reached for from library code - only the
/// catalogue reads it - and it is not ambient discovery: something has to have been told a path
/// and loaded it. See <c>docs/modem-binding.md</c>.</para>
/// <para>Modes are keyed <c>id:mode</c>, which no built-in name can collide with because no
/// built-in contains a colon. That also makes an unloaded plugin diagnosable: a config asking for
/// <c>ofdm-fm:nb</c> gets told no plugin <c>ofdm-fm</c> is registered, rather than that the mode
/// does not exist.</para>
/// <para>Thread-safe. Registration takes a lock and swaps in a fresh immutable snapshot; every
/// lookup reads one reference and never locks, which is what lets the catalogue's per-call
/// questions stay cheap.</para>
/// </remarks>
public static class ModemPluginRegistry
{
    private static readonly Lock Gate = new();
    private static volatile Snapshot _current = Snapshot.Empty;

    /// <summary>Every registered mode string, qualified <c>id:mode</c>, in registration order.
    /// Empty in the usual case where a station runs only the built-in modes.</summary>
    public static IReadOnlyList<string> RegisteredModes => _current.Modes;

    /// <summary>Every registered plugin id, in registration order.</summary>
    public static IReadOnlyList<string> RegisteredPlugins => _current.PluginIds;

    /// <summary>True if <paramref name="mode"/> is a registered plugin mode (ordinal,
    /// case-sensitive, qualified <c>id:mode</c>).</summary>
    public static bool IsRegistered(string mode) =>
        mode is not null && _current.ByMode.ContainsKey(mode);

    /// <summary>True if a plugin with this id has been registered.</summary>
    public static bool IsPluginRegistered(string id) =>
        id is not null && _current.ByPluginId.ContainsKey(id);

    /// <summary>What a registered mode declares about itself, or null if it is not registered.
    /// The descriptor's own <see cref="ModemDescriptor.Name"/> is the bare name, not the qualified
    /// one this was looked up by.</summary>
    public static ModemDescriptor? DescriptorFor(string mode) =>
        mode is not null && _current.ByMode.TryGetValue(mode, out Registration found)
            ? found.Descriptor
            : null;

    /// <summary>
    /// Registers a plugin and every mode it declares.
    /// </summary>
    /// <param name="plugin">The plugin to register.</param>
    /// <returns>A handle that unregisters the plugin when disposed. A daemon registers for the
    /// life of the process and ignores it; a test needs the undo, because this is process-global
    /// state and a leaked registration is visible to every catalogue sweep that runs after it.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="plugin"/> is null.</exception>
    /// <exception cref="ArgumentException">The id is empty or contains <c>:</c>, a plugin with
    /// that id is already registered, the plugin declares no modes, two of its modes share a name,
    /// or one of its descriptors is not self-consistent (see
    /// <see cref="ModemDescriptor.Validate"/>).</exception>
    public static IDisposable Register(IModemPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);

        string id = plugin.Id;
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("a modem plugin needs an id", nameof(plugin));
        }

        if (id.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"plugin id '{id}' contains ':', which separates an id from its mode in a mode "
                + "string - a plugin id has to be one token",
                nameof(plugin));
        }

        IReadOnlyList<ModemDescriptor> modes = plugin.Modes
            ?? throw new ArgumentException($"plugin '{id}' declares no modes", nameof(plugin));
        if (modes.Count == 0)
        {
            throw new ArgumentException($"plugin '{id}' declares no modes", nameof(plugin));
        }

        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (ModemDescriptor descriptor in modes)
        {
            if (descriptor is null)
            {
                throw new ArgumentException(
                    $"plugin '{id}' declares a null mode descriptor", nameof(plugin));
            }

            descriptor.Validate();
            if (!declared.Add(descriptor.Name))
            {
                throw new ArgumentException(
                    $"plugin '{id}' declares mode '{descriptor.Name}' twice", nameof(plugin));
            }
        }

        lock (Gate)
        {
            Snapshot current = _current;
            if (current.ByPluginId.ContainsKey(id))
            {
                // Refused rather than replaced: two plugins claiming one id means one of them is
                // not the modem the operator wrote down, and silently picking the second is how a
                // station transmits something nobody chose.
                throw new ArgumentException(
                    $"a modem plugin with id '{id}' is already registered", nameof(plugin));
            }

            _current = current.With(plugin, modes);
        }

        return new Registrant(id);
    }

    /// <summary>
    /// Builds a registered mode. <see cref="ModemCatalog.Create"/> validates the options against
    /// the descriptor first and calls this; nothing else should.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="mode"/> is not registered.</exception>
    /// <exception cref="InvalidOperationException">The plugin threw, or handed back null.</exception>
    internal static IModem Create(
        string mode, int dspRate, Action<byte[]> frameReceived, ModemOptions options)
    {
        if (!_current.ByMode.TryGetValue(mode, out Registration found))
        {
            throw new ArgumentException($"unknown mode '{mode}'", nameof(mode));
        }

        IModem? modem;
        try
        {
            modem = found.Plugin.Create(
                found.Descriptor.Name, dspRate, frameReceived, options);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            // Named, because the bare exception comes from an assembly whose source the operator
            // does not have and whose name is nowhere in the stack trace they can act on.
            throw new InvalidOperationException(
                $"modem plugin '{found.Plugin.Id}' failed to build mode '{mode}': {failure.Message}",
                failure);
        }

        return modem ?? throw new InvalidOperationException(
            $"modem plugin '{found.Plugin.Id}' returned no modem for mode '{mode}'");
    }

    private static void Unregister(string id)
    {
        lock (Gate)
        {
            _current = _current.Without(id);
        }
    }

    private readonly record struct Registration(IModemPlugin Plugin, ModemDescriptor Descriptor);

    /// <summary>The undo half of <see cref="Register"/>. Idempotent: disposing twice unregisters
    /// once, so a test's using-block and an explicit dispose cannot fight.</summary>
    private sealed class Registrant(string id) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Unregister(id);
            }
        }
    }

    /// <summary>One immutable view of every registration, swapped in whole so a reader never sees
    /// a half-applied one.</summary>
    private sealed class Snapshot
    {
        public static readonly Snapshot Empty = new(
            [], [], new(StringComparer.Ordinal), new(StringComparer.Ordinal));

        private Snapshot(
            IReadOnlyList<string> modes,
            IReadOnlyList<string> pluginIds,
            Dictionary<string, Registration> byMode,
            Dictionary<string, IModemPlugin> byPluginId)
        {
            Modes = modes;
            PluginIds = pluginIds;
            ByMode = byMode;
            ByPluginId = byPluginId;
        }

        public IReadOnlyList<string> Modes { get; }

        public IReadOnlyList<string> PluginIds { get; }

        public Dictionary<string, Registration> ByMode { get; }

        public Dictionary<string, IModemPlugin> ByPluginId { get; }

        public Snapshot With(IModemPlugin plugin, IReadOnlyList<ModemDescriptor> modes)
        {
            var byMode = new Dictionary<string, Registration>(ByMode, StringComparer.Ordinal);
            var names = new List<string>(Modes);
            foreach (ModemDescriptor descriptor in modes)
            {
                string qualified = $"{plugin.Id}:{descriptor.Name}";
                byMode[qualified] = new Registration(plugin, descriptor);
                names.Add(qualified);
            }

            return new Snapshot(
                names,
                [.. PluginIds, plugin.Id],
                byMode,
                new Dictionary<string, IModemPlugin>(ByPluginId, StringComparer.Ordinal)
                {
                    [plugin.Id] = plugin,
                });
        }

        public Snapshot Without(string id)
        {
            if (!ByPluginId.ContainsKey(id))
            {
                return this;
            }

            string prefix = id + ":";
            var byMode = new Dictionary<string, Registration>(StringComparer.Ordinal);
            var names = new List<string>();
            foreach (string mode in Modes)
            {
                if (mode.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                names.Add(mode);
                byMode[mode] = ByMode[mode];
            }

            var byPluginId = new Dictionary<string, IModemPlugin>(ByPluginId, StringComparer.Ordinal);
            byPluginId.Remove(id);
            return new Snapshot(names, [.. PluginIds.Where(p => p != id)], byMode, byPluginId);
        }
    }
}
