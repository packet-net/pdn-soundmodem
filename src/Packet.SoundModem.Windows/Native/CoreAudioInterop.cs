using System.Runtime.InteropServices;

namespace Packet.SoundModem.Windows.Native;

// Core Audio COM definitions, including the undocumented IPolicyConfig, taken from
// M0LTE/altmixer (src/AltMixer.Core/Interop/ComInterfaces.cs, AGPL-3.0, same author), where they
// were verified against real CM108 and AIOC devices on Windows 11 26100. Only what
// EndpointHygiene needs is here. Every string parameter carries LPWStr and every bool Bool: COM
// defaults to BSTR and VARIANT_BOOL, and getting either wrong corrupts silently.

#pragma warning disable CA1707, CA1712, CA1711, CA1716, CA1720, CA1051, IDE1006, CS0649

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class CoreAudioNative
{
    public const uint CLSCTX_ALL = 0x17;
    public const ushort VT_BOOL = 11;
    public const ushort VT_UI4 = 19;
    public const ushort VT_BLOB = 65;

    [DllImport("ole32.dll")]
    public static extern int PropVariantClear(ref PropVariant pv);

    /// <summary>
    /// Creates a COM object from its CLSID behind a wrapper of its own. The device enumerator is a
    /// process-wide singleton, and the runtime keeps one wrapper per COM identity: a cached wrapper
    /// made here would be the one NAudio's own <c>new MMDeviceEnumerator()</c> got back, typed
    /// wrongly for its cast, which then throws. <see cref="Marshal.GetUniqueObjectForIUnknown"/>
    /// makes a wrapper that is never cached, so the two libraries cannot see each other's.
    /// </summary>
    public static T Create<T>(string clsid)
    {
        Guid classId = new(clsid);
        Guid iunknown = new("00000000-0000-0000-C000-000000000046");
        Marshal.ThrowExceptionForHR(CoCreateInstance(ref classId, 0, CLSCTX_ALL, ref iunknown, out nint unknown));
        try
        {
            return (T)Marshal.GetUniqueObjectForIUnknown(unknown);
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid clsid, nint outer, uint context, ref Guid iid, out nint instance);

    public const string DeviceEnumeratorClsid = "BCDE0395-E52F-467C-8E3D-C4579291692E";
    public const string PolicyConfigClsid = "870af99c-171d-4f9e-af0d-e63df40c2bc9";
}

internal enum EDataFlow { Render = 0, Capture = 1, All = 2 }

internal enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

[Flags]
internal enum DeviceState : uint { Active = 1, Disabled = 2, NotPresent = 4, Unplugged = 8, All = 0xF }

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid fmtid;
    public int pid;
    public PropertyKey(string g, int p) { fmtid = new Guid(g); pid = p; }
    public override string ToString() => $"{{{fmtid}}},{pid}";
}

[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariant
{
    [FieldOffset(0)] public ushort vt;
    [FieldOffset(8)] public IntPtr ptr;
    [FieldOffset(8)] public int i4;
    [FieldOffset(8)] public long i8;
    [FieldOffset(8)] public short boolVal;
    [FieldOffset(8)] public float r4;
    [FieldOffset(8)] public uint cb;
    [FieldOffset(16)] public IntPtr blob;

    public static PropVariant FromUInt(uint v) => new() { vt = 19, i4 = (int)v };
}

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    void EnumAudioEndpoints(EDataFlow flow, DeviceState mask, out IMMDeviceCollection devices);
    [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow flow, ERole role, out IMMDevice device);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    void RegisterEndpointNotificationCallback(IMMNotificationClient client);
    void UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    void GetCount(out uint count);
    void Item(uint index, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    void Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    void OpenPropertyStore(uint stgmAccess, out IPropertyStore store);
    void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    void GetState(out DeviceState state);
}

[ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    void GetCount(out uint count);
    void GetAt(uint index, out PropertyKey key);
    [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
    [PreserveSig] int Commit();
}

[ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMNotificationClient
{
    void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, DeviceState state);
    void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
    void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
    void OnDefaultDeviceChanged(EDataFlow flow, ERole role, [MarshalAs(UnmanagedType.LPWStr)] string? id);
    void OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PropertyKey key);
}

[ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDeviceTopology
{
    void GetConnectorCount(out uint count);
    void GetConnector(uint index, out IConnector connector);
    void GetSubunitCount(out uint count);
    void GetSubunit(uint index, out IntPtr subunit);
    void GetPartById(uint id, out IPart part);
    void GetDeviceId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    void GetSignalPath(IntPtr from, IntPtr to, int rejectMixed, out IntPtr parts);
}

[ComImport, Guid("9c2c4058-23f5-41de-877a-df3af236a09e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IConnector
{
    void GetConnectorType(out int type);
    void GetDataFlow(out int flow);
    void ConnectTo(IConnector other);
    void Disconnect();
    void IsConnected([MarshalAs(UnmanagedType.Bool)] out bool connected);
    void GetConnectedTo(out IConnector other);
    void GetConnectorIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
    void GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
}

[ComImport, Guid("AE2DE0E4-5BCA-4F2D-AA46-5D13F8FDB3A9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPart
{
    void GetName([MarshalAs(UnmanagedType.LPWStr)] out string name);
    void GetLocalId(out uint id);
    void GetGlobalId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    void GetPartType(out int type);
    void GetSubType(out Guid subtype);
    void GetControlInterfaceCount(out uint count);
    void GetControlInterface(uint index, out IControlInterface ci);
    void EnumPartsIncoming(out IPartsList parts);
    void EnumPartsOutgoing(out IPartsList parts);
    void GetTopologyObject(out IDeviceTopology topo);
    void Activate(uint clsCtx, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    void RegisterControlChangeCallback(ref Guid iid, IntPtr cb);
    void UnregisterControlChangeCallback(IntPtr cb);
}

[ComImport, Guid("6DAA848C-5EB0-45CC-AEA5-998A2CDA1FFB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPartsList
{
    void GetCount(out uint count);
    void GetPart(uint index, out IPart part);
}

[ComImport, Guid("45d37c3f-5140-444a-ae24-400789f3cbf3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IControlInterface
{
    void GetName([MarshalAs(UnmanagedType.LPWStr)] out string name);
    void GetIID(out Guid iid);
}

[ComImport, Guid("7FB7B48F-531D-44A2-BCB3-5AD5A134B3DC"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioVolumeLevel
{
    void GetChannelCount(out uint count);
    void GetLevelRange(uint ch, out float minDb, out float maxDb, out float stepDb);
    void GetLevel(uint ch, out float db);
    void SetLevel(uint ch, float db, ref Guid ctx);
    void SetLevelUniform(float db, ref Guid ctx);
    void SetLevelAllChannels(IntPtr levels, uint count, ref Guid ctx);
}

[ComImport, Guid("85401FD4-6DE4-4b9d-9869-2D6753A82F3C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioAutoGainControl
{
    void GetEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);
    void SetEnabled([MarshalAs(UnmanagedType.Bool)] bool enabled, ref Guid ctx);
}

[ComImport, Guid("DF45AEEA-B74A-4B6B-AFAD-2366B6AA012E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioMute
{
    void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
    void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

[ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfig
{
    [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr fmt);
    [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int bDefault, out IntPtr fmt);
    [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
    [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpointFmt, IntPtr mixFmt);
    [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int bDefault, out long defaultPeriod, out long minPeriod);
    [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, ref long period);
    [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
    [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
    [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, int bFxStore, ref PropertyKey key, out PropVariant pv);
    [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, int bFxStore, ref PropertyKey key, ref PropVariant pv);
    [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, ERole role);
    [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
}
