using System.Reflection;
using System.Runtime.Loader;

namespace Packet.SoundModem.Modems;

/// <summary>
/// Loads a modem plugin from an explicit assembly path and registers it.
/// </summary>
/// <remarks>
/// <para><b>Explicit paths only, never ambient discovery.</b> There is no directory scan, no
/// probing beside the executable and no environment variable, because a file appearing on disk
/// must never change what a station transmits. Something told this loader a path, and the config
/// that said so is the audit trail. See <c>docs/modem-binding.md</c>.</para>
/// <para><b>Failures are named and non-fatal.</b> <see cref="Load"/> does not throw for anything
/// a plugin or an operator can get wrong: it returns a <see cref="ModemPluginLoad"/> carrying the
/// reason, so a station starts without that mode and says why, rather than refusing to start at
/// all over an experimental modem.</para>
/// </remarks>
public static class ModemPluginLoader
{
    /// <summary>
    /// The assemblies a plugin always gets from the <em>host</em> rather than from its own folder.
    /// </summary>
    /// <remarks>
    /// <para>This is the one rule that makes the mechanism work rather than fight. A plugin's build
    /// output normally contains a copy of everything it references, this library included; loading
    /// that copy privately would give the plugin its own unrelated <see cref="IModem"/> type, and
    /// the failure reads "IModem cannot be converted to IModem", which costs an afternoon every
    /// time somebody meets it.</para>
    /// <para>So: the contract assembly, plus every assembly whose types appear in the contract's
    /// public surface. <c>M0LTE.Il2p</c> is on the list because
    /// <see cref="FrameQuality.HeaderType"/> is one of its enums, so every plugin that reports
    /// frame quality touches it. <c>ModemPluginContractTests</c> walks that surface and fails if it
    /// grows a dependency this list does not name.</para>
    /// <para>Framework assemblies need no entry: a normal framework-dependent plugin's
    /// <c>.deps.json</c> does not list them, so the resolver finds nothing and the request falls
    /// through to the host's shared framework anyway.</para>
    /// </remarks>
    public static IReadOnlyCollection<string> HostProvidedAssemblies { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            typeof(IModemPlugin).Assembly.GetName().Name!,
            // Naming the type rather than the string also forces the assembly to be loaded here,
            // so "the host has it" is true before any plugin can ask.
            typeof(M0LTE.Il2p.Il2pHeaderType).Assembly.GetName().Name!,
        };

    /// <summary>
    /// Loads the plugin assembly at <paramref name="path"/> and registers it with
    /// <see cref="ModemPluginRegistry"/>.
    /// </summary>
    /// <param name="path">Path to the plugin assembly. Relative paths are resolved against the
    /// current directory and reported absolute, so a log line names a file somebody can go and
    /// look at.</param>
    /// <returns>What happened: on success the plugin id and the modes it registered, on failure a
    /// one-line reason. Never null, and never throws for a bad path, a bad assembly or a
    /// misbehaving plugin.</returns>
    public static ModemPluginLoad Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return ModemPluginLoad.Failed("", "no path given");
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception malformed) when (malformed is ArgumentException or NotSupportedException
            or PathTooLongException)
        {
            return ModemPluginLoad.Failed(path, malformed.Message);
        }

        if (!File.Exists(full))
        {
            return ModemPluginLoad.Failed(full, "no such file");
        }

        // Constructed inside the try, not before it. AssemblyDependencyResolver reads and parses
        // the plugin's .deps.json in its constructor and throws if it is missing or malformed, so
        // a context built outside would break this method's whole contract - it would throw for
        // exactly the kind of half-installed plugin it exists to report calmly.
        PluginLoadContext? context = null;
        try
        {
            context = new PluginLoadContext(full);
            Assembly assembly = context.LoadFromAssemblyPath(full);
            IModemPlugin plugin = Construct(assembly);

            // Everything that reads the plugin's own properties happens BEFORE registering, so
            // that nothing between the registration and the return can throw. A property that
            // threw there would leave the plugin registered while this method unloads its assembly
            // context and reports FAILED - a set of modes the catalogue offers and nothing can
            // build, which no operator would have any way to see.
            string id = plugin.Id;
            string[] modes = [.. QualifiedModes(plugin)];
            IDisposable registration = ModemPluginRegistry.Register(plugin);
            return ModemPluginLoad.Succeeded(full, id, modes, registration, context);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            // The context is unloaded rather than left holding a half-initialised plugin: a
            // second attempt at the same path should meet the same state as the first.
            if (context is not null)
            {
                Unload(context);
            }

            return ModemPluginLoad.Failed(full, Describe(failure));
        }
    }

    /// <summary>The qualified mode strings a plugin declares, read once.</summary>
    private static IEnumerable<string> QualifiedModes(IModemPlugin plugin) =>
        (plugin.Modes ?? []).Select(m => $"{plugin.Id}:{m.Name}");

    /// <summary>Finds and constructs the assembly's plugin.</summary>
    private static IModemPlugin Construct(Assembly assembly)
    {
        Type type = FindPluginType(assembly);
        if (type.IsAbstract || type.IsInterface)
        {
            throw new InvalidOperationException(
                $"'{type.FullName}' is not something that can be constructed");
        }

        if (!typeof(IModemPlugin).IsAssignableFrom(type))
        {
            // Almost always the shared-contract rule having gone wrong: the plugin implements an
            // IModemPlugin from its own private copy of this library, which is a different type.
            throw new InvalidOperationException(
                $"'{type.FullName}' does not implement {nameof(IModemPlugin)} - if it looks like "
                + "it does, the plugin was built against a different Packet.SoundModem and is "
                + "carrying its own copy, which makes two unrelated types of the same name");
        }

        if (type.GetConstructor(Type.EmptyTypes) is null)
        {
            throw new InvalidOperationException(
                $"'{type.FullName}' has no public parameterless constructor");
        }

        return (IModemPlugin)Activator.CreateInstance(type)!;
    }

    private static Type FindPluginType(Assembly assembly)
    {
        if (assembly.GetCustomAttribute<ModemPluginAttribute>() is ModemPluginAttribute declared)
        {
            return declared.PluginType
                ?? throw new InvalidOperationException(
                    $"[assembly: {nameof(ModemPluginAttribute)}] names no type");
        }

        Type[] candidates = [.. Types(assembly)
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IModemPlugin).IsAssignableFrom(t))];

        return candidates.Length switch
        {
            1 => candidates[0],
            0 => throw new InvalidOperationException(
                $"no {nameof(IModemPlugin)} in it"),
            _ => throw new InvalidOperationException(
                $"{candidates.Length} types implement {nameof(IModemPlugin)} "
                + $"({string.Join(", ", candidates.Select(t => t.FullName))}) - say which with "
                + $"[assembly: {nameof(ModemPluginAttribute)}(typeof(...))]"),
        };
    }

    /// <summary>Every type the assembly will admit to, tolerating the ones that will not load.
    /// A plugin referencing something absent should still be searchable for its entry point;
    /// failing the whole load on an unrelated broken type says nothing useful.</summary>
    private static IEnumerable<Type> Types(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException partial)
        {
            return partial.Types.OfType<Type>();
        }
    }

    /// <summary>A one-line reason, with the inner exception's message where the outer one is a
    /// wrapper that says nothing (reflection's usual habit).</summary>
    private static string Describe(Exception failure) => failure switch
    {
        TargetInvocationException { InnerException: Exception inner } =>
            $"its constructor threw: {inner.Message}",
        BadImageFormatException => "not a managed assembly this runtime can load",
        _ => failure.Message,
    };

    private static void Unload(AssemblyLoadContext context)
    {
        try
        {
            context.Unload();
        }
        catch (InvalidOperationException)
        {
            // Not collectible, or already unloaded. Nothing to do about it and nothing worth
            // failing a load over.
        }
    }

    /// <summary>
    /// One plugin's own load context: its dependencies come from its own directory, the contract
    /// comes from the host. Collectible, so a failed or finished load leaves nothing behind.
    /// </summary>
    private sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;

        public PluginLoadContext(string pluginPath)
            : base(name: Path.GetFileNameWithoutExtension(pluginPath), isCollectible: true) =>
            _resolver = new AssemblyDependencyResolver(pluginPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Returning null hands the request to the default context, which is exactly what
            // "the host provides this one" means.
            if (assemblyName.Name is string simple
                && HostProvidedAssemblies.Contains(simple))
            {
                return null;
            }

            string? path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}

/// <summary>
/// What one <see cref="ModemPluginLoader.Load"/> came to: a registered plugin, or a named reason
/// it is not one. Disposing unregisters the plugin's modes and unloads its context.
/// </summary>
/// <remarks>
/// A daemon keeps the handle for the life of the process and never disposes it - plugins load at
/// start-up and the station runs. Tests must dispose, because registration is process-global and a
/// leak is visible to every catalogue sweep that runs afterwards.
/// </remarks>
public sealed class ModemPluginLoad : IDisposable
{
    private readonly IDisposable? _registration;
    private readonly AssemblyLoadContext? _context;
    private int _disposed;

    private ModemPluginLoad(
        string path,
        string? pluginId,
        IReadOnlyList<string> modes,
        string? failure,
        IDisposable? registration,
        AssemblyLoadContext? context)
    {
        Path = path;
        PluginId = pluginId;
        Modes = modes;
        Failure = failure;
        _registration = registration;
        _context = context;
    }

    /// <summary>The absolute path that was loaded, or the path as given if it could not be
    /// resolved to one.</summary>
    public string Path { get; }

    /// <summary>The plugin's <see cref="IModemPlugin.Id"/>, or null if it did not load.</summary>
    public string? PluginId { get; }

    /// <summary>The qualified mode strings now registered, or empty if it did not load.</summary>
    public IReadOnlyList<string> Modes { get; }

    /// <summary>Why it did not load, or null if it did.</summary>
    public string? Failure { get; }

    /// <summary>True if the plugin is registered.</summary>
    public bool Loaded => Failure is null;

    internal static ModemPluginLoad Failed(string path, string reason) =>
        new(path, null, [], reason, null, null);

    /// <summary>Built from a mode list already read, rather than re-reading the plugin's own
    /// properties after it has been registered: a second read that threw would leave a
    /// registration behind that this reported as a failure and nothing ever removed.</summary>
    internal static ModemPluginLoad Succeeded(
        string path,
        string pluginId,
        IReadOnlyList<string> modes,
        IDisposable registration,
        AssemblyLoadContext context) =>
        new(path, pluginId, modes, null, registration, context);

    /// <summary>Unregisters the plugin's modes and unloads its load context. Idempotent.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _registration?.Dispose();
        if (_context is null)
        {
            return;
        }

        try
        {
            _context.Unload();
        }
        catch (InvalidOperationException)
        {
            // Already unloaded, or not collectible. Neither is worth throwing from a Dispose.
        }
    }
}
