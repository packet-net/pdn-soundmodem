using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Packet.SoundModem.Waterfall;

/// <summary>
/// One of this assembly's embedded HTML pages, loaded once and stamped with a version.
/// </summary>
/// <remarks>
/// The version is a hash of the page's own text, so it changes with every edit and never has to
/// be remembered by anyone. Each page carries the same version back to the browser - the
/// waterfall in its config message, the picker in its instances snapshot - and a tab that hears
/// a version other than the one it was served reloads itself once. That covers the tab that
/// never navigates, which is how both of these pages are normally left open.
/// </remarks>
internal static class EmbeddedPage
{
    /// <summary>Loads the embedded page whose resource name ends with <paramref name="fileName"/>
    /// and writes its version into the <c>__PAGE_VERSION__</c> placeholder.</summary>
    internal static (byte[] Bytes, string Version) Load(string fileName)
    {
        Assembly assembly = typeof(EmbeddedPage).Assembly;
        string name = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith(fileName, StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(name)!;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        byte[] raw = memory.ToArray();
        string version = Convert.ToHexStringLower(SHA256.HashData(raw))[..12];
        string text = Encoding.UTF8.GetString(raw)
            .Replace("__PAGE_VERSION__", version, StringComparison.Ordinal);
        return (Encoding.UTF8.GetBytes(text), version);
    }
}
