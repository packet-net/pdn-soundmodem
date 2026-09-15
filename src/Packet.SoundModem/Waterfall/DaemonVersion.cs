using System.Reflection;

namespace Packet.SoundModem.Waterfall;

/// <summary>
/// What this build is: the version and the commit it was built from, read once from the
/// assembly's own metadata.
/// </summary>
/// <remarks>
/// <para>Three places need this - <see cref="UplinkClient"/>'s <c>hello</c> to the monitor, the
/// daemon's <c>--version</c> and its start-up journal line, and the waterfall page's
/// <c>config</c> message - and each used to carry its own copy of the reflection call. This is
/// the one place it is read now.</para>
/// <para><c>packaging/build-deb.sh</c> passes <c>-p:Version="$VERSION"</c>, so a released build's
/// <see cref="AssemblyInformationalVersionAttribute"/> is the release number plus the commit the
/// .NET SDK embeds automatically (<c>&lt;release&gt;+&lt;40-hex-char sha&gt;</c>). A build that
/// named no version of its own - a plain <c>dotnet build</c>, or a binary copied by hand - gets
/// <see cref="UnversionedDefault"/> in the version's place, with the same commit suffix, which is
/// exactly the case #480 was filed over: a station that had "definitely updated" and had no way
/// to say to what. <see cref="IsRelease"/> and <see cref="Describe"/> exist so that build never
/// gets presented as though it were a numbered release.</para>
/// <para><see cref="UnversionedDefault"/> is deliberately not the SDK's own out-of-the-box
/// default of 1.0.0: that is a real version this project can release one day, so a build that
/// happened to carry it would be indistinguishable from that release. <c>Directory.Build.props</c>
/// sets every project's <c>&lt;Version&gt;</c> to <c>0.0.0-dev</c> instead - a number no tagged
/// release (v0.1.0 and up) can ever take, so the constant here can check for that one spelling
/// and never risk aliasing a real release again. The point is the version number itself, not the
/// exact digits, so if the default in <c>Directory.Build.props</c> ever needs to move, move this
/// constant to match rather than special-casing a second value here.</para>
/// </remarks>
public static class DaemonVersion
{
    /// <summary>
    /// The version every project in this repo gets when nothing names one of its own: what
    /// <c>Directory.Build.props</c> sets <c>&lt;Version&gt;</c> to, overridden by
    /// <c>-p:Version</c> for a real build. Not the SDK's own default (1.0.0), which is a real
    /// version this project can release; a build carrying this one is never a numbered release,
    /// whatever commit rides beside it.
    /// </summary>
    public const string UnversionedDefault = "0.0.0-dev";

    /// <summary>Printed when the assembly carries no version metadata at all - not a build this
    /// repo's own tooling produces, but handled rather than thrown on, as the reflection call
    /// this replaces always did.</summary>
    public const string Unknown = "unknown";

    /// <summary>
    /// The version part of the assembly's informational version: what <c>-p:Version</c> set for
    /// a released build, or <see cref="UnversionedDefault"/> for one that got none.
    /// <see cref="Unknown"/> only if the assembly carries no version metadata at all.
    /// </summary>
    public static string Version { get; }

    /// <summary>
    /// The full commit hash this was built from - the .NET SDK embeds it automatically from the
    /// working tree's HEAD when a version is generated - or null if the assembly carries none: a
    /// build with no git repository behind it, or one where that embedding was turned off.
    /// </summary>
    public static string? Commit { get; }

    /// <summary>
    /// True for a version worth showing as a release. False for <see cref="UnversionedDefault"/>
    /// and for <see cref="Unknown"/>, which are exactly the two cases nothing set a real number.
    /// </summary>
    public static bool IsRelease { get; }

    static DaemonVersion()
    {
        Assembly assembly = typeof(DaemonVersion).Assembly;
        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (informational is not { Length: > 0 })
        {
            // No attribute at all: the fallback the original reflection call used, for an
            // assembly built with GenerateAssemblyInfo turned off. Nothing here sets Version to
            // anything that could pass for a release, so IsRelease stays false.
            Version = assembly.GetName().Version?.ToString() ?? Unknown;
            Commit = null;
            IsRelease = false;
            return;
        }

        int plus = informational.IndexOf('+');
        if (plus < 0)
        {
            Version = informational;
            Commit = null;
        }
        else
        {
            Version = informational[..plus];
            Commit = informational[(plus + 1)..] is { Length: > 0 } commit ? commit : null;
        }

        IsRelease = Version != UnversionedDefault && Version != Unknown;
    }

    /// <summary>
    /// One line for a human: what <c>--version</c> prints and the daemon's start-up journal line
    /// ends with. Never claims a release it cannot back - <see cref="UnversionedDefault"/> and a
    /// missing commit are both said plainly rather than left to read like one.
    /// </summary>
    public static string Describe()
    {
        if (Version == Unknown)
        {
            return "version unknown (no build metadata)";
        }

        string commit = Commit ?? Unknown;
        return IsRelease
            ? $"{Version}, commit {commit}"
            : $"{Version} (dev build, not a numbered release), commit {commit}";
    }
}
