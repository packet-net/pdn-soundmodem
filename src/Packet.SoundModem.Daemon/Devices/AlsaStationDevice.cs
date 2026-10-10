namespace Packet.SoundModem.Daemon;

/// <summary>
/// The sound card kind: an ALSA device name. The default, so it claims every device string no
/// other kind does.
/// </summary>
internal sealed class AlsaDeviceKind : DeviceKind
{
    /// <inheritdoc/>
    public override string Name => "alsa";

    /// <inheritdoc/>
    public override string Spelling => "plughw:CARD=Device,DEV=0";

    /// <inheritdoc/>
    public override bool Matches(string device) => true;

    /// <inheritdoc/>
    public override StationDevice Create(string device, DeviceSettings settings) =>
        new AlsaStationDevice(this, device);

    /// <inheritdoc/>
    public override bool HasMixer => true;
}

/// <summary>An ALSA sound card, on a radio keyed by a PTT line or not at all.</summary>
internal sealed class AlsaStationDevice(DeviceKind kind, string spec) : StationDevice(kind, spec)
{
}
