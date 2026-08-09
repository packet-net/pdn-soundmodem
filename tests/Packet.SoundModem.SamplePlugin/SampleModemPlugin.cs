using Packet.SoundModem.Modems;

[assembly: ModemPlugin(typeof(Packet.SoundModem.SamplePlugin.SampleModemPlugin))]

namespace Packet.SoundModem.SamplePlugin;

/// <summary>
/// A modem plugin that exists to be loaded: the smallest thing that can prove the seam carries a
/// frame across an assembly boundary and back.
/// </summary>
/// <remarks>
/// <para>Not a modem anybody should transmit. Its waveform is one sample per bit at plus or minus
/// one, which is not a waveform at all - it is a bit stream wearing a float array, and it exists so
/// the tests can assert on frames rather than on signal processing.</para>
/// <para>What it does model faithfully is the shape of a real plugin: three modes with genuinely
/// different descriptors, so both sides of the centre-frequency rule and both sides of the
/// IL2P+CRC rule get exercised, and one mode that throws on construction, because a plugin
/// blowing up in <c>Create</c> is a failure mode the host has to survive by name.</para>
/// </remarks>
public sealed class SampleModemPlugin : IModemPlugin
{
    /// <summary>The mode that round-trips a frame: a carrier-ish mode with a settable centre.</summary>
    public const string Loopback = "loopback";

    /// <summary>A baseband mode - no centre to move, and it claims IL2P+CRC, so it is the mode
    /// that accepts plain-IL2P tolerance while <see cref="Loopback"/> refuses it.</summary>
    public const string Baseband = "baseband";

    /// <summary>A mode whose construction always throws, to pin what the host does about it.</summary>
    public const string Explodes = "explodes";

    /// <inheritdoc/>
    public string Id => "sample";

    /// <inheritdoc/>
    public IReadOnlyList<ModemDescriptor> Modes { get; } =
    [
        new(Loopback, DspRate: 12000, AcceptsCentre: true, DefaultCentreHz: 1500, RunsIl2pCrc: false),
        new(Baseband, DspRate: 48000, AcceptsCentre: false, DefaultCentreHz: null, RunsIl2pCrc: true),
        new(Explodes, DspRate: 12000, AcceptsCentre: false, DefaultCentreHz: null, RunsIl2pCrc: false),
    ];

    /// <inheritdoc/>
    public IModem Create(
        string mode, int dspRate, Action<byte[]> frameReceived, ModemOptions options) => mode switch
    {
        Loopback or Baseband => new SampleModem($"{Id}:{mode}", frameReceived),
        Explodes => throw new InvalidOperationException("this mode is meant to fail"),
        _ => throw new ArgumentException($"'{mode}' is not one of this plugin's modes", nameof(mode)),
    };
}
