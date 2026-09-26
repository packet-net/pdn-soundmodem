using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using static Packet.SoundModem.Windows.Native.NativeMethods;

namespace Packet.SoundModem.Windows.Native;

/// <summary>One present device interface: its path, the physical device it belongs to, and
/// (for a COM port interface) the port name.</summary>
internal sealed record DeviceInterface(string Path, Guid? ContainerId, string? PortName);

/// <summary>Walks SetupAPI's present device interfaces of one class.</summary>
[SupportedOSPlatform("windows")]
internal static class DeviceInterfaces
{
    public static IReadOnlyList<DeviceInterface> Enumerate(Guid interfaceClass, bool readPortName = false)
    {
        var found = new List<DeviceInterface>();
        nint set = SetupDiGetClassDevs(interfaceClass, 0, 0, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == -1)
        {
            return found;
        }

        try
        {
            for (int index = 0; ; index++)
            {
                var iface = new SpDeviceInterfaceData { CbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, 0, interfaceClass, index, ref iface))
                {
                    break;
                }

                var info = new SpDevinfoData { CbSize = Marshal.SizeOf<SpDevinfoData>() };
                SetupDiGetDeviceInterfaceDetail(set, ref iface, null, 0, out int required, ref info);
                if (required <= 0)
                {
                    continue;
                }

                // SP_DEVICE_INTERFACE_DETAIL_DATA_W: a DWORD cbSize then the path. cbSize is the
                // size of the fixed part as the native struct lays it out: 8 on 64-bit (4 + one
                // WCHAR, padded to pointer alignment), 6 on 32-bit.
                var detail = new byte[required];
                BitConverter.TryWriteBytes(detail, IntPtr.Size == 8 ? 8 : 6);
                if (!SetupDiGetDeviceInterfaceDetail(set, ref iface, detail, detail.Length, out _, ref info))
                {
                    continue;
                }

                string path = MemoryMarshal.Cast<byte, char>(detail.AsSpan(4)).ToString().TrimEnd('\0');
                Guid? container = ReadContainerId(set, ref info);
                string? port = readPortName ? ReadPortName(set, ref info) : null;
                found.Add(new DeviceInterface(path, container, port));
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return found;
    }

    private static Guid? ReadContainerId(nint set, ref SpDevinfoData info)
    {
        var buffer = new byte[16];
        return SetupDiGetDeviceProperty(set, ref info, ContainerIdKey, out _, buffer, buffer.Length, out _, 0)
            ? new Guid(buffer)
            : null;
    }

    private static string? ReadPortName(nint set, ref SpDevinfoData info)
    {
        nint key = SetupDiOpenDevRegKey(set, ref info, DICS_FLAG_GLOBAL, 0, DIREG_DEV, KEY_READ);
        if (key == 0 || key == -1)
        {
            return null;
        }

        using RegistryKey registry = RegistryKey.FromHandle(new SafeRegistryHandle(key, ownsHandle: true));
        return registry.GetValue("PortName") as string;
    }
}
