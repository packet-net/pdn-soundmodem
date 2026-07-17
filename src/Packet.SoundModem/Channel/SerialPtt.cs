using System.IO.Ports;
using M0LTE.Radio.Audio;

namespace Packet.SoundModem.Channel;

/// <summary>PTT via a serial control line (RTS and/or DTR) — the classic homebrew
/// interface and half of the CM108-alternative story (CM108 HID PTT follows).</summary>
public sealed class SerialPtt : IPttControl, IDisposable
{
    private readonly SerialPort _port;
    private readonly bool _rts;
    private readonly bool _dtr;

    /// <summary>Opens <paramref name="device"/> and prepares the chosen lines
    /// (de-asserted).</summary>
    /// <param name="device">Serial device path (e.g. /dev/ttyUSB0).</param>
    /// <param name="useRts">Drive RTS.</param>
    /// <param name="useDtr">Drive DTR.</param>
    public SerialPtt(string device, bool useRts = true, bool useDtr = false)
    {
        if (!useRts && !useDtr)
        {
            throw new ArgumentException("at least one of RTS/DTR must be selected");
        }

        _rts = useRts;
        _dtr = useDtr;
        _port = new SerialPort(device);
        _port.Open();
        Unkey();
    }

    /// <inheritdoc />
    public void Key() => Set(true);

    /// <inheritdoc />
    public void Unkey() => Set(false);

    private void Set(bool asserted)
    {
        if (_rts)
        {
            _port.RtsEnable = asserted;
        }

        if (_dtr)
        {
            _port.DtrEnable = asserted;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Unkey();
        }
        finally
        {
            _port.Dispose();
        }
    }
}
