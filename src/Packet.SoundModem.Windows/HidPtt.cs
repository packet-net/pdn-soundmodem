using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using M0LTE.Radio.Audio;
using Microsoft.Win32.SafeHandles;
using static Packet.SoundModem.Windows.Native.NativeMethods;

namespace Packet.SoundModem.Windows;

/// <summary>
/// The CM108 GPIO output report, laid out once so that it can be tested without a device.
/// </summary>
/// <remarks>
/// Byte for byte what <see cref="Channel.Cm108Ptt"/> writes on Linux: report ID 0, then the
/// write-GPIO command byte (0), the GPIO output values, the data-direction register (1 =
/// output) and an SPDIF byte. Data before mask; see that class for why the order matters on the
/// release and what getting it wrong did. Windows wants the buffer to be exactly the
/// collection's output report length, so it is zero-padded to that.
/// </remarks>
internal static class Cm108Report
{
    /// <summary>Report ID plus the four GPIO bytes.</summary>
    public const int Length = 5;

    public static byte Mask(int gpio)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(gpio, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(gpio, 8);
        return (byte)(1 << (gpio - 1));
    }

    public static byte[] Build(bool asserted, byte mask, int reportLength)
    {
        var report = new byte[Math.Max(Length, reportLength)];
        report[2] = asserted ? mask : (byte)0;
        report[3] = mask;
        return report;
    }
}

/// <summary>
/// PTT through a CM108-compatible HID GPIO pin on Windows: CM108/CM119 dongles, Digirig-style
/// interfaces, and the AIOC (firmware 1.2.0 and later).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HidPtt : IPttControl, IDisposable
{
    /// <summary>The pin PTT is wired to on almost every CM108 interface, and the AIOC's default.</summary>
    public const int DefaultGpio = 3;

    private readonly SafeFileHandle _device;
    private readonly byte _mask;
    private readonly int _reportLength;
    private readonly Lock _gate = new();

    /// <summary>Opens the collection at <paramref name="devicePath"/> (see
    /// <see cref="HidDevices.List"/>) and de-asserts PTT.</summary>
    /// <param name="devicePath">The HID device interface path.</param>
    /// <param name="gpio">GPIO pin 1-8.</param>
    /// <param name="reportLength">The collection's output report length, report ID included;
    /// see <see cref="HidDevice.OutputReportLength"/>.</param>
    /// <exception cref="Win32Exception">The device could not be opened.</exception>
    public HidPtt(string devicePath, int gpio = DefaultGpio, int reportLength = Cm108Report.Length)
    {
        ArgumentException.ThrowIfNullOrEmpty(devicePath);
        _mask = Cm108Report.Mask(gpio);
        _reportLength = reportLength;
        _device = CreateFile(devicePath, GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, 0, OPEN_EXISTING, 0, 0);
        if (_device.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            _device.Dispose();
            throw new Win32Exception(error, $"cannot open HID device {devicePath}");
        }

        Unkey();
    }

    /// <summary>Opens the CM108-compatible collection <paramref name="device"/> describes.</summary>
    public HidPtt(HidDevice device, int gpio = DefaultGpio)
        : this(device?.Path ?? throw new ArgumentNullException(nameof(device)), gpio, device.OutputReportLength)
    {
    }

    /// <inheritdoc />
    public void Key() => Set(true);

    /// <inheritdoc />
    public void Unkey() => Set(false);

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Unkey();
        }
        catch (IOException)
        {
        }
        finally
        {
            _device.Dispose();
        }
    }

    private void Set(bool asserted)
    {
        byte[] report = Cm108Report.Build(asserted, _mask, _reportLength);
        lock (_gate)
        {
            if (!WriteFile(_device, report, report.Length, out int written, 0) || written != report.Length)
            {
                int error = Marshal.GetLastPInvokeError();
                throw new IOException($"HID PTT write failed ({new Win32Exception(error).Message})", error);
            }
        }
    }
}
