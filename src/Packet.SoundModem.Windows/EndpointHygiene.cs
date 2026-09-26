using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Packet.SoundModem.Windows.Native;
using static Packet.SoundModem.Windows.Native.CoreAudioNative;

namespace Packet.SoundModem.Windows;

/// <summary>Something about an endpoint's Windows settings that is wrong for a radio interface.</summary>
public enum EndpointIssueKind
{
    /// <summary>The endpoint level is above 0 dB (or the device's own ceiling): digital gain.</summary>
    LevelAboveCeiling,

    /// <summary>The endpoint is muted.</summary>
    Muted,

    /// <summary>Windows audio enhancements (APOs) are on, reshaping the audio.</summary>
    Enhancements,

    /// <summary>Spatial sound is on for a playback device, reshaping the transmit audio.</summary>
    SpatialSound,

    /// <summary>The device's hardware automatic gain control is on, fighting the modem's levels.</summary>
    AutomaticGainControl,

    /// <summary>A hardware gain stage on the endpoint's path is above 0 dB.</summary>
    HardwareGainAboveZero,

    /// <summary>A hardware monitor path (CM108 mic to speaker) is open, looping received audio
    /// straight into the transmit audio.</summary>
    MonitorPath,

    /// <summary>"Listen to this device" is on, playing received audio out of another device
    /// (possibly this interface's own output, which is the radio's transmit audio).</summary>
    ListenToThisDevice,
}

/// <summary>One finding from <see cref="EndpointHygiene"/>.</summary>
/// <param name="Kind">What is wrong.</param>
/// <param name="Flow">Which side of the interface it is on.</param>
/// <param name="Description">One plain sentence for the operator.</param>
/// <param name="CanFix">Whether <see cref="EndpointHygiene.Fix"/> can put it right. Spatial sound
/// cannot be switched off through any interface known to work, so it is reported with the
/// Settings page that does it.</param>
public sealed record EndpointIssue(EndpointIssueKind Kind, AudioFlow Flow, string Description, bool CanFix);

/// <summary>
/// Checks, and puts right, the Windows settings that damage a radio interface's audio: gain above
/// 0 dB, mute, audio enhancements, spatial sound, hardware AGC, open monitor paths and "Listen to
/// this device".
/// </summary>
/// <remarks>
/// <para>Everything here works unelevated: endpoint properties go through the undocumented but
/// long-stable <c>IPolicyConfig</c> (what the Sound control panel itself uses), and hardware
/// controls are found by walking the device topology from the endpoint. The rules and the walk
/// are from M0LTE/altmixer, whose docs/windows-audio.md records the measurements behind them.</para>
/// <para><b>Nothing above 0 dB, anywhere.</b> The same rule as <see cref="EndpointLevel"/>,
/// extended to hardware gain stages a driver exposes only through the topology (a CM108's mic
/// PGA reaches +23 dB).</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class EndpointHygiene
{
    private static readonly PropertyKey DisableSysFx = new("1da5d803-d492-4edd-8c23-e0c0ffee7f0e", 5);
    private static readonly PropertyKey Listen = new("24dbb0fc-9311-4b3d-9cf0-18ff155639d4", 1);
    private static readonly PropertyKey Spatial = new("908dba32-edff-4c28-8e45-c918561f6748", 2);
    private static readonly Guid SpatialProvidersFmtid = new("a45429a4-aa63-4480-b7f8-3f2552daee93");

    private static readonly Guid VolumeSubtype = new("3A5ACC00-C557-11D0-8A2B-00A0C9255AC1");
    private static readonly Guid MuteSubtype = new("02B223C0-C557-11D0-8A2B-00A0C9255AC1");
    private static readonly HashSet<Guid> MixerSubtypes =
    [
        new("DA441A60-C556-11D0-8A2B-00A0C9255AC1"), // SUM
        new("E573ADC0-C555-11D0-8A2B-00A0C9255AC1"), // SUPERMIX
        new("2CEAF780-C556-11D0-8A2B-00A0C9255AC1"), // MUX
    ];

    private const int SoftwareIo = 3;
    private const int SoftwareFixed = 4;

    /// <summary>The Settings page where spatial sound is switched off.</summary>
    public const string SoundSettingsUri = "ms-settings:sound";

    /// <summary>What is wrong with the endpoint, changing nothing.</summary>
    public static IReadOnlyList<EndpointIssue> Check(string endpointId, AudioFlow flow) =>
        AudioEndpoints.OnMta(() => Inspect(endpointId, flow, fix: false));

    /// <summary>
    /// Puts right everything that can be, then checks again and returns what is still wrong
    /// (normally only spatial sound, if it was on).
    /// </summary>
    public static IReadOnlyList<EndpointIssue> Fix(string endpointId, AudioFlow flow) =>
        AudioEndpoints.OnMta(() =>
        {
            Inspect(endpointId, flow, fix: true);
            return Inspect(endpointId, flow, fix: false);
        });

    private static List<EndpointIssue> Inspect(string endpointId, AudioFlow flow, bool fix)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpointId);
        var issues = new List<EndpointIssue>();
        var enumerator = Create<IMMDeviceEnumerator>(DeviceEnumeratorClsid);
        var policy = Create<IPolicyConfig>(PolicyConfigClsid);
        try
        {
            Marshal.ThrowExceptionForHR(enumerator.GetDevice(endpointId, out IMMDevice device));
            (double min, double max) range = CheckEndpointVolume(endpointId, flow, fix, issues);
            CheckProperties(policy, device, endpointId, flow, fix, issues);
            CheckTopology(device, flow, range, fix, issues);
            Marshal.FinalReleaseComObject(device);
        }
        finally
        {
            Marshal.FinalReleaseComObject(policy);
            Marshal.FinalReleaseComObject(enumerator);
        }

        return issues;
    }

    private static (double Min, double Max) CheckEndpointVolume(string endpointId, AudioFlow flow, bool fix, List<EndpointIssue> issues)
    {
        using EndpointLevel level = EndpointLevel.Open(endpointId);
        double db = level.LevelDb;
        if (db > level.CeilingDb + 0.01)
        {
            issues.Add(new(EndpointIssueKind.LevelAboveCeiling, flow,
                $"Windows level is {db:+0.0;-0.0} dB, above {level.CeilingDb:0.0} dB: that is digital gain, which only brings clipping closer.",
                CanFix: true));
            if (fix)
            {
                level.LevelDb = level.CeilingDb;
            }
        }

        if (level.Muted)
        {
            issues.Add(new(EndpointIssueKind.Muted, flow, "The device is muted in Windows.", CanFix: true));
            if (fix)
            {
                level.Muted = false;
            }
        }

        return (level.MinDb, level.MaxDb);
    }

    private static void CheckProperties(IPolicyConfig policy, IMMDevice device, string id, AudioFlow flow, bool fix, List<EndpointIssue> issues)
    {
        // Absent or 0 means enhancements are on; 1 means off.
        if (ReadUInt(policy, id, fxStore: true, DisableSysFx) is null or 0)
        {
            issues.Add(new(EndpointIssueKind.Enhancements, flow,
                "Windows audio enhancements are on; they reshape the audio a modem depends on.", CanFix: true));
            if (fix)
            {
                WriteUInt(policy, id, fxStore: true, DisableSysFx, 1);
            }
        }

        if (flow == AudioFlow.Capture && ReadBool(policy, id, Listen) == true)
        {
            issues.Add(new(EndpointIssueKind.ListenToThisDevice, flow,
                "\"Listen to this device\" is on, so received audio is played out of another device.", CanFix: true));
            if (fix)
            {
                WriteBool(policy, id, Listen, false);
            }
        }

        if (flow == AudioFlow.Render && ReadBlob(policy, id, Spatial) is { } selected && SpatialIsOn(device, selected))
        {
            issues.Add(new(EndpointIssueKind.SpatialSound, flow,
                "Spatial sound is on for this output. Turn it off in Settings > System > Sound > this device.", CanFix: false));
        }
    }

    /// <summary>The spatial sound blob embeds the chosen provider's CLSID, and the provider list
    /// (one blob per provider, name first) carries each CLSID; no match means it is off.</summary>
    private static bool SpatialIsOn(IMMDevice device, byte[] selected)
    {
        device.OpenPropertyStore(0, out IPropertyStore store);
        try
        {
            store.GetCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                store.GetAt(i, out PropertyKey key);
                if (key.fmtid != SpatialProvidersFmtid || store.GetValue(ref key, out PropVariant value) != 0)
                {
                    continue;
                }

                byte[]? provider = BlobOf(ref value);
                for (int at = 256; provider is not null && at + 16 <= provider.Length; at += 4)
                {
                    ReadOnlySpan<byte> window = provider.AsSpan(at, 16);
                    if (window.IndexOfAnyExcept((byte)0) >= 0 && selected.AsSpan().IndexOf(window) >= 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        finally
        {
            Marshal.FinalReleaseComObject(store);
        }
    }

    private static void CheckTopology(IMMDevice device, AudioFlow flow, (double Min, double Max) endpointRange, bool fix, List<EndpointIssue> issues)
    {
        IPart start;
        try
        {
            Guid iid = typeof(IDeviceTopology).GUID;
            device.Activate(ref iid, CLSCTX_ALL, 0, out object topologyObject);
            var topology = (IDeviceTopology)topologyObject;
            topology.GetConnector(0, out IConnector connector);
            connector.GetConnectedTo(out IConnector other);
            start = (IPart)other;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // A virtual device has no hardware behind its connector, and nothing to check.
            return;
        }

        // Capture walks outgoing from the device's side of the endpoint connector, render walks
        // incoming; off the endpoint's own path the walk stops at mixer nodes, otherwise a mic
        // would claim the speaker's controls. See altmixer's Topology.cs.
        bool outgoing = flow == AudioFlow.Capture;
        var leaves = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        var order = new List<(IPart Part, string Id, bool PastMixer)>();
        Visit(start, outgoing, leaves, order, 0, false);

        Guid context = Guid.Empty;
        foreach ((IPart part, string gid, bool pastMixer) in order)
        {
            bool main = leaves[gid].Contains(SoftwareIo) || leaves[gid].Contains(SoftwareFixed);
            if (!main && pastMixer)
            {
                continue;
            }

            try
            {
                part.GetName(out string name);
                part.GetSubType(out Guid subtype);
                part.GetControlInterfaceCount(out uint count);
                for (uint i = 0; i < count; i++)
                {
                    part.GetControlInterface(i, out IControlInterface control);
                    control.GetIID(out Guid controlIid);
                    part.Activate(CLSCTX_ALL, ref controlIid, out object instance);
                    switch (instance)
                    {
                        case IAudioAutoGainControl agc when main:
                            agc.GetEnabled(out bool enabled);
                            if (enabled)
                            {
                                issues.Add(new(EndpointIssueKind.AutomaticGainControl, flow,
                                    "The device's automatic gain control is on; it fights the modem's own level tracking.", CanFix: true));
                                if (fix)
                                {
                                    agc.SetEnabled(false, ref context);
                                }
                            }

                            break;

                        case IAudioMute mute when !main && subtype == MuteSubtype:
                            mute.GetMute(out bool muted);
                            if (!muted)
                            {
                                issues.Add(new(EndpointIssueKind.MonitorPath, flow,
                                    $"The hardware monitor path ({name}) is open, looping received audio into the transmit audio.", CanFix: true));
                                if (fix)
                                {
                                    mute.SetMute(true, ref context);
                                }
                            }

                            break;

                        case IAudioVolumeLevel level when main && subtype == VolumeSubtype:
                            level.GetLevelRange(0, out float min, out float max, out _);
                            if (Math.Abs(min - endpointRange.Min) < 0.01 && Math.Abs(max - endpointRange.Max) < 0.01)
                            {
                                // The control the endpoint volume already is; checked above.
                                break;
                            }

                            level.GetLevel(0, out float db);
                            float ceiling = Math.Clamp(0f, min, max);
                            if (db > ceiling + 0.01f)
                            {
                                issues.Add(new(EndpointIssueKind.HardwareGainAboveZero, flow,
                                    $"Hardware gain ({name}) is {db:+0.0;-0.0} dB, above {ceiling:0.0} dB.", CanFix: true));
                                if (fix)
                                {
                                    level.SetLevelUniform(ceiling, ref context);
                                }
                            }

                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                // A part that cannot be read is skipped, as the Sound control panel skips it.
            }
        }
    }

    private static HashSet<int> Visit(
        IPart part, bool outgoing, Dictionary<string, HashSet<int>> leaves, List<(IPart, string, bool)> order, int depth, bool pastMixer)
    {
        part.GetGlobalId(out string gid);
        if (leaves.TryGetValue(gid, out HashSet<int>? known))
        {
            return known;
        }

        var mine = new HashSet<int>();
        leaves[gid] = mine;
        order.Add((part, gid, pastMixer));
        part.GetSubType(out Guid subtype);
        pastMixer |= MixerSubtypes.Contains(subtype);
        if (depth > 40)
        {
            return mine;
        }

        IPartsList? list = null;
        try
        {
            if (outgoing)
            {
                part.EnumPartsOutgoing(out list);
            }
            else
            {
                part.EnumPartsIncoming(out list);
            }
        }
        catch (COMException)
        {
            // The end of the path: E_NOTFOUND.
        }

        uint count = 0;
        list?.GetCount(out count);
        for (uint i = 0; i < count; i++)
        {
            list!.GetPart(i, out IPart child);
            mine.UnionWith(Visit(child, outgoing, leaves, order, depth + 1, pastMixer));
        }

        if (count == 0 && depth > 0 && part is IConnector connector)
        {
            connector.GetConnectorType(out int type);
            mine.Add(type);
        }

        return mine;
    }

    private static uint? ReadUInt(IPolicyConfig policy, string id, bool fxStore, PropertyKey key)
    {
        if (policy.GetPropertyValue(id, fxStore ? 1 : 0, ref key, out PropVariant value) != 0)
        {
            return null;
        }

        try
        {
            return value.vt == VT_UI4 ? (uint)value.i4 : null;
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    private static bool? ReadBool(IPolicyConfig policy, string id, PropertyKey key)
    {
        if (policy.GetPropertyValue(id, 0, ref key, out PropVariant value) != 0)
        {
            return null;
        }

        try
        {
            return value.vt == VT_BOOL ? value.boolVal != 0 : null;
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    private static byte[]? ReadBlob(IPolicyConfig policy, string id, PropertyKey key) =>
        policy.GetPropertyValue(id, 0, ref key, out PropVariant value) != 0 ? null : BlobOf(ref value);

    private static byte[]? BlobOf(ref PropVariant value)
    {
        try
        {
            if (value.vt != VT_BLOB || value.blob == 0)
            {
                return null;
            }

            var bytes = new byte[value.cb];
            Marshal.Copy(value.blob, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    private static void WriteUInt(IPolicyConfig policy, string id, bool fxStore, PropertyKey key, uint value)
    {
        var variant = new PropVariant { vt = VT_UI4, i4 = (int)value };
        Marshal.ThrowExceptionForHR(policy.SetPropertyValue(id, fxStore ? 1 : 0, ref key, ref variant));
    }

    private static void WriteBool(IPolicyConfig policy, string id, PropertyKey key, bool value)
    {
        var variant = new PropVariant { vt = VT_BOOL, boolVal = (short)(value ? -1 : 0) };
        Marshal.ThrowExceptionForHR(policy.SetPropertyValue(id, 0, ref key, ref variant));
    }
}
