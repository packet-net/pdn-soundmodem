using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Packet.SoundModem.Windows.Native;
using static Packet.SoundModem.Windows.Native.NativeMethods;

namespace Packet.SoundModem.Windows;

/// <summary>One HID top-level collection that can be written to.</summary>
/// <param name="Path">The device interface path, which is what <see cref="HidPtt"/> opens.</param>
/// <param name="VendorId">USB vendor ID.</param>
/// <param name="ProductId">USB product ID.</param>
/// <param name="Product">The product string, when the device reports one.</param>
/// <param name="OutputReportLength">Output report size in bytes, report ID included.</param>
/// <param name="ContainerId">The physical device this collection belongs to, which is how it is
/// paired with that device's audio endpoints and serial ports.</param>
public sealed record HidDevice(
    string Path,
    ushort VendorId,
    ushort ProductId,
    string? Product,
    int OutputReportLength,
    Guid? ContainerId)
{
    /// <summary>C-Media, the CM108/CM119 family and its clones.</summary>
    public const ushort CMediaVendorId = 0x0D8C;

    /// <summary>The AIOC (All-In-One-Cable), pid.codes allocation.</summary>
    public const ushort AiocVendorId = 0x1209;

    /// <inheritdoc cref="AiocVendorId" />
    public const ushort AiocProductId = 0x7388;

    /// <summary>Whether this is an AIOC.</summary>
    public bool IsAioc => VendorId == AiocVendorId && ProductId == AiocProductId;

    /// <summary>Whether this collection speaks the CM108 GPIO protocol: a C-Media part, or an
    /// AIOC (which emulates it from firmware 1.2.0), with an output report big enough to carry
    /// the four GPIO bytes.</summary>
    public bool IsCm108Compatible =>
        (VendorId == CMediaVendorId || IsAioc) && OutputReportLength >= Cm108Report.Length;
}

/// <summary>Finds HID collections that accept output reports.</summary>
[SupportedOSPlatform("windows")]
public static class HidDevices
{
    /// <summary>Every present HID collection with an output report.</summary>
    public static IReadOnlyList<HidDevice> List()
    {
        HidD_GetHidGuid(out Guid hid);
        var devices = new List<HidDevice>();
        foreach (DeviceInterface iface in DeviceInterfaces.Enumerate(hid))
        {
            // Access 0 is a query-only open: enough for attributes and capabilities, and it
            // succeeds on collections Windows holds exclusively (keyboards, mice).
            using SafeFileHandle handle = CreateFile(iface.Path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, 0, OPEN_EXISTING, 0, 0);
            if (handle.IsInvalid)
            {
                continue;
            }

            var attributes = new HiddAttributes { Size = System.Runtime.InteropServices.Marshal.SizeOf<HiddAttributes>() };
            if (!HidD_GetAttributes(handle, ref attributes))
            {
                continue;
            }

            int outputLength = OutputReportLength(handle);
            if (outputLength <= 0)
            {
                continue;
            }

            devices.Add(new HidDevice(
                iface.Path, attributes.VendorID, attributes.ProductID, ProductString(handle), outputLength, iface.ContainerId));
        }

        return devices;
    }

    private static int OutputReportLength(SafeFileHandle handle)
    {
        if (!HidD_GetPreparsedData(handle, out nint preparsed))
        {
            return 0;
        }

        try
        {
            return HidP_GetCaps(preparsed, out HidpCaps caps) == HIDP_STATUS_SUCCESS ? caps.OutputReportByteLength : 0;
        }
        finally
        {
            HidD_FreePreparsedData(preparsed);
        }
    }

    private static string? ProductString(SafeFileHandle handle)
    {
        var buffer = new byte[256];
        if (!HidD_GetProductString(handle, buffer, buffer.Length))
        {
            return null;
        }

        string text = Encoding.Unicode.GetString(buffer);
        int end = text.IndexOf('\0', StringComparison.Ordinal);
        text = (end >= 0 ? text[..end] : text).Trim();
        return text.Length == 0 ? null : text;
    }
}
