using System.Runtime.Versioning;
using NAudio.CoreAudioApi;

namespace Packet.SoundModem.Windows;

/// <summary>Which way audio flows through an endpoint.</summary>
public enum AudioFlow
{
    /// <summary>A recording device: audio from the radio.</summary>
    Capture,

    /// <summary>A playback device: audio to the radio.</summary>
    Render,
}

/// <summary>One active audio endpoint.</summary>
/// <param name="Id">The endpoint ID. Stable across reboots, and across USB ports for a device
/// with a serial number (the AIOC has one); a serial-less CM108 gets a new ID on a new port.</param>
/// <param name="Name">The full name Windows shows, e.g. "Microphone (AIOC Audio)".</param>
/// <param name="DeviceName">The device's own name, e.g. "AIOC Audio".</param>
/// <param name="Flow">Capture or render.</param>
/// <param name="ContainerId">The physical device this endpoint belongs to.</param>
public sealed record AudioEndpoint(string Id, string Name, string DeviceName, AudioFlow Flow, Guid? ContainerId);

/// <summary>Lists audio endpoints. Safe to call from any thread.</summary>
[SupportedOSPlatform("windows")]
public static class AudioEndpoints
{
    private static readonly PropertyKey ContainerIdKey = new(new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);

    /// <summary>Every active endpoint of one flow.</summary>
    public static IReadOnlyList<AudioEndpoint> List(AudioFlow flow) =>
        OnMta(() =>
        {
            using var enumerator = new MMDeviceEnumerator();
            var list = new List<AudioEndpoint>();
            foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(ToDataFlow(flow), DeviceState.Active))
            {
                using (device)
                {
                    list.Add(Describe(device, flow));
                }
            }

            return list;
        });

    /// <summary>The default communications endpoint of one flow, or null if there is none.</summary>
    public static AudioEndpoint? DefaultFor(AudioFlow flow) =>
        OnMta(() =>
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!enumerator.HasDefaultAudioEndpoint(ToDataFlow(flow), Role.Communications))
            {
                return null;
            }

            using MMDevice device = enumerator.GetDefaultAudioEndpoint(ToDataFlow(flow), Role.Communications);
            return Describe(device, flow);
        });

    internal static DataFlow ToDataFlow(AudioFlow flow) => flow == AudioFlow.Capture ? DataFlow.Capture : DataFlow.Render;

    /// <summary>
    /// Runs Core Audio work on an MTA thread. Called from a WPF UI thread (an STA), MMDevice
    /// objects would be created in that apartment and every later call from anywhere else
    /// marshalled back through it, so enumeration hops off it rather than rely on the caller.
    /// </summary>
    internal static T OnMta<T>(Func<T> work)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA)
        {
            return work();
        }

        return Task.Run(work).GetAwaiter().GetResult();
    }

    private static AudioEndpoint Describe(MMDevice device, AudioFlow flow)
    {
        Guid? container = null;
        try
        {
            if (device.Properties.Contains(ContainerIdKey) && device.Properties[ContainerIdKey].Value is Guid id)
            {
                container = id;
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or NotImplementedException or InvalidCastException)
        {
            // A driver that does not publish the container just cannot be paired with its HID.
        }

        return new AudioEndpoint(device.ID, device.FriendlyName, device.DeviceFriendlyName, flow, container);
    }
}
