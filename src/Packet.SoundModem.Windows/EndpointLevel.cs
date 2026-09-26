using System.Runtime.Versioning;
using NAudio.CoreAudioApi;

namespace Packet.SoundModem.Windows;

/// <summary>
/// An endpoint's own level control in dB (what the Windows Sound settings slider moves), capped at
/// 0 dB: the Windows equivalent of the daemon's ALSA mixer sliders.
/// </summary>
/// <remarks>
/// <para><b>Nothing above 0 dB.</b> Driver-reported ranges are not to be trusted: some codecs
/// report up to +30 dB of what is really digital gain, and some report ranges that never reach
/// 0 dB at all. Gain above 0 dB on a capture path is gain applied after the converter, which
/// raises the noise with the signal and clips sooner; on a render path it clips the modem's
/// carefully levelled tones. So the ceiling is 0 dB or the device's maximum, whichever is lower,
/// and <see cref="LevelDb"/> clamps writes to it.</para>
/// <para><b>Device level, never session volume.</b> A capture session's volume is an alias of
/// the device's level on Windows (setting it moves the endpoint), so this deliberately manages
/// the endpoint and nothing else. See M0LTE/altmixer docs/windows-audio.md for the
/// measurements behind both rules.</para>
/// <para>All COM runs on a private MTA thread; the object is safe to use from a UI thread.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class EndpointLevel : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator;
    private readonly MMDevice _device;
    private readonly AudioEndpointVolume _volume;

    private EndpointLevel(MMDeviceEnumerator enumerator, MMDevice device)
    {
        _enumerator = enumerator;
        _device = device;
        _volume = device.AudioEndpointVolume;
        MinDb = _volume.VolumeRange.MinDecibels;
        MaxDb = _volume.VolumeRange.MaxDecibels;
        _volume.OnVolumeNotification += _ => Changed?.Invoke();
    }

    /// <summary>Opens the level control of the endpoint with ID <paramref name="endpointId"/>.</summary>
    public static EndpointLevel Open(string endpointId)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpointId);
        return AudioEndpoints.OnMta(() =>
        {
            var enumerator = new MMDeviceEnumerator();
            try
            {
                return new EndpointLevel(enumerator, enumerator.GetDevice(endpointId));
            }
            catch
            {
                enumerator.Dispose();
                throw;
            }
        });
    }

    /// <summary>The bottom of the driver's range, in dB.</summary>
    public double MinDb { get; }

    /// <summary>The top of the driver's range, in dB, which may be above 0.</summary>
    public double MaxDb { get; }

    /// <summary>The highest level this will set: 0 dB, or <see cref="MaxDb"/> if that is lower.</summary>
    public double CeilingDb => Math.Clamp(0, MinDb, MaxDb);

    /// <summary>Whether the driver's range extends above 0 dB (so the top of it is digital gain
    /// this will not use).</summary>
    public bool HasGainAboveZero => MaxDb > 0;

    /// <summary>The current level in dB. Writes are clamped to [<see cref="MinDb"/>,
    /// <see cref="CeilingDb"/>].</summary>
    public double LevelDb
    {
        get => AudioEndpoints.OnMta(() => (double)_volume.MasterVolumeLevel);
        set
        {
            float clamped = (float)Math.Clamp(value, MinDb, CeilingDb);
            AudioEndpoints.OnMta(() => _volume.MasterVolumeLevel = clamped);
        }
    }

    /// <summary>Whether the endpoint is muted.</summary>
    public bool Muted
    {
        get => AudioEndpoints.OnMta(() => _volume.Mute);
        set => AudioEndpoints.OnMta(() => _volume.Mute = value);
    }

    /// <summary>Raised (on a Windows thread) when the level or mute changes, by this object or by
    /// anything else on the machine. Re-read the properties; the notification's payload is not
    /// passed on because Windows sends duplicates and partial states.</summary>
    public event Action? Changed;

    /// <inheritdoc />
    public void Dispose()
    {
        _volume.Dispose();
        _device.Dispose();
        _enumerator.Dispose();
    }
}
