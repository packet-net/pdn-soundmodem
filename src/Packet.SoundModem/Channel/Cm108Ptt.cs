using M0LTE.Radio.Audio;
namespace Packet.SoundModem.Channel;

/// <summary>
/// PTT via a CM108/CM119-family USB audio codec's GPIO pin (Digirig, DRA boards, DIY
/// dongles), by writing the 5-byte HID output report straight to the Linux hidraw device
/// - the same convention QtSoundModem and Dire Wolf use, no HID library required.
/// GPIO3 drives PTT on virtually all interfaces.
/// </summary>
/// <remarks>
/// <para>The four report bytes are the ones Hamlib's `cm108.c` quotes from the C-Media
/// documentation, and the ones Dire Wolf's `cm108_write` sends: a write-GPIO command byte, the
/// GPIO output values, the data-direction register (1 = output) and an SPDIF byte. A hidraw
/// caller prepends a report-number byte, which is where the leading zero comes from, so the
/// write is { report-id 0, 0, io-data, io-mask, 0 }.</para>
/// <para>Data before mask, and the order matters on the unkey only: asserting sets both bytes
/// to the same pin bit, so the two orderings are indistinguishable on the wire and a swapped
/// implementation keys a radio perfectly. It is the release that breaks. This class had them
/// swapped until 2026-09-17, which de-asserted by writing the direction register as 0, turning
/// the pin into an input and letting it float rather than driving it low. On a board with no
/// gate pull-down (the CM108 Radio Widget is one, see
/// docs/dev/hardware/cm108-widget-netlist.md) a floating gate holds its charge, so that is a
/// transmitter released by leakage or not at all. Nobody has yet watched the pin on a scope to
/// say which it was; docs/dev/roadmap.md carries that as open bench work.</para>
/// </remarks>
public sealed class Cm108Ptt : IPttControl, IDisposable
{
    private readonly FileStream _device;
    private readonly byte _mask;

    /// <summary>Opens the hidraw device (e.g. /dev/hidraw0) and de-asserts PTT.</summary>
    /// <param name="hidrawPath">The CM108's hidraw node. Identify it via
    /// /sys/class/hidraw/*/device/uevent (vendor 0d8c).</param>
    /// <param name="gpio">GPIO pin 1-8; PTT is wired to GPIO3 on common interfaces.</param>
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
        ReadOnlySpan<byte> report = [0x00, 0x00, asserted ? _mask : (byte)0x00, _mask, 0x00];
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
