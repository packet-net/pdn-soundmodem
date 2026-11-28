using System.Reflection;

namespace Packet.SoundModem.OfdmFmTool;

/// <summary>The one start-up line saying which tool and which modem library a run used, read from the assemblies.</summary>
internal static class Versions
{
    public static string Line() => Line(typeof(Versions).Assembly, typeof(M0LTE.OfdmFm.Station).Assembly);

    public static string Line(Assembly tool, Assembly library) => $"pdn-ofdm {Of(tool)}, M0LTE.OfdmFm {Of(library)}";

    /// <summary>The assembly's informational version without the build metadata after a plus, else its assembly version.</summary>
    private static string Of(Assembly assembly)
    {
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        string version = informational ?? assembly.GetName().Version?.ToString() ?? "unknown";
        int plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? version : version[..plus];
    }
}
