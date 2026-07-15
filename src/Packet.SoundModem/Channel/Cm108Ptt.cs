namespace Packet.SoundModem.Channel;

/// <summary>
/// PTT via a CM108/CM119-family USB audio codec's GPIO pin (Digirig, DRA boards, DIY
/// dongles), by writing the 5-byte HID output report straight to the Linux hidraw device
/// — the same convention QtSoundModem and Dire Wolf use, no HID library required:
/// { report-id 0, 0, io-mask, io-data, 0 }. GPIO3 drives PTT on virtually all interfaces.
/// </summary>
public sealed class Cm108Ptt : IPttControl, IDisposable
{
    private readonly FileStream _device;
    private readonly byte _mask;

    /// <summary>Opens the hidraw device (e.g. /dev/hidraw0) and de-asserts PTT.</summary>
    /// <param name="hidrawPath">The CM108's hidraw node. Identify it via
    /// /sys/class/hidraw/*/device/uevent (vendor 0d8c).</param>
    /// <param name="gpio">GPIO pin 1–8; PTT is wired to GPIO3 on common interfaces.</param>
    public Cm108Ptt(string hidrawPath, int gpio = 3)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(gpio, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(gpio, 8);
        _mask = (byte)(1 << (gpio - 1));
        _device = new FileStream(hidrawPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        Unkey();
    }

    /// <inheritdoc />
    public void Key() => Set(true);

    /// <inheritdoc />
    public void Unkey() => Set(false);

    private void Set(bool asserted)
    {
        ReadOnlySpan<byte> report = [0x00, 0x00, _mask, asserted ? _mask : (byte)0x00, 0x00];
        _device.Write(report);
        _device.Flush();
    }

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
}
