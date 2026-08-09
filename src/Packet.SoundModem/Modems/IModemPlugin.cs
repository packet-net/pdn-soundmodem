namespace Packet.SoundModem.Modems;

/// <summary>
/// A modem this repository does not contain: one family of modes, named and constructed through a
/// contract that crosses an assembly boundary. See <c>docs/modem-binding.md</c> for why the
/// mechanism exists and what it deliberately does not do.
/// </summary>
/// <remarks>
/// <para>A plugin sees audio in and frames out, and nothing else. It does not see the daemon, the
/// KISS server, the config or the radio: it gets the same <see cref="IModem"/> surface a built-in
/// mode gets and no more, which is the whole point of putting the seam here.</para>
/// <para>Implementations are found by <c>ModemPluginLoader</c> and handed to
/// <see cref="ModemPluginRegistry.Register"/>, after which their modes answer
/// <see cref="ModemCatalog"/> questions like any other - prefixed with <see cref="Id"/>, so
/// <c>ofdm-fm:nb</c> rather than <c>nb</c>.</para>
/// </remarks>
public interface IModemPlugin
{
    /// <summary>
    /// The mode family this plugin provides, e.g. <c>ofdm-fm</c>. Every mode it declares is
    /// addressed as <c>Id:Name</c>, so this is the half of a mode string that says the mode was
    /// not built here. Must be non-empty and must not contain <c>:</c>.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// The modes this plugin can build, for catalogue listing and for answering the rate,
    /// centre-frequency and IL2P questions without constructing anything.
    /// </summary>
    IReadOnlyList<ModemDescriptor> Modes { get; }

    /// <summary>
    /// Builds one of this plugin's modes.
    /// </summary>
    /// <param name="mode">The bare mode name, exactly as this plugin declared it in
    /// <see cref="Modes"/> - <c>nb</c>, not <c>ofdm-fm:nb</c>. The catalogue strips its own prefix
    /// before asking, so a plugin never has to know or reproduce the naming scheme.</param>
    /// <param name="dspRate">The channel DSP rate to run at, in Hz.</param>
    /// <param name="frameReceived">The decoded-frame sink.</param>
    /// <param name="options">The caller's knobs, with
    /// <see cref="ModemOptions.CentreFrequencyHz"/> already resolved to the override or the
    /// descriptor's default, and the options the descriptor refuses already rejected. Everything
    /// else is passed through as the caller gave it.</param>
    /// <exception cref="ArgumentException"><paramref name="mode"/> is not one of this plugin's
    /// modes.</exception>
    IModem Create(string mode, int dspRate, Action<byte[]> frameReceived, ModemOptions options);
}
