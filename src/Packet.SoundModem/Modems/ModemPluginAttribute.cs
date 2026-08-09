namespace Packet.SoundModem.Modems;

/// <summary>
/// Names the <see cref="IModemPlugin"/> in an assembly, so the loader does not have to guess:
/// <c>[assembly: ModemPlugin(typeof(MyPlugin))]</c>.
/// </summary>
/// <remarks>
/// Optional. Without it the loader looks for exactly one public concrete
/// <see cref="IModemPlugin"/> with a parameterless constructor, which is the usual case and needs
/// no ceremony. Declare it when an assembly has more than one candidate, or when the plugin type
/// is not public - and prefer it in anything shipped, because it says out loud which type is the
/// entry point instead of leaving it to be inferred from what happens to be in the assembly.
/// </remarks>
/// <param name="pluginType">The type implementing <see cref="IModemPlugin"/>. Must be concrete
/// and have a public parameterless constructor.</param>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class ModemPluginAttribute(Type pluginType) : Attribute
{
    /// <summary>The type implementing <see cref="IModemPlugin"/>.</summary>
    public Type PluginType { get; } = pluginType;
}
