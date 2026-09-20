namespace Packet.SoundModem.Modems.OfdmFm;

/// <summary>
/// Where a modem gets carrier sense from, and the seam a host uses to supply it.
/// </summary>
/// <remarks>
/// <para><b>Why a static.</b> A modem is deliberately blind: it "does not see the
/// daemon, the KISS server, the config or the radio", and <c>ModemOptions</c> is a fixed record
/// with nowhere to put one modem's own settings. So a host that wants to hand this modem something
/// has exactly two channels: a file this modem reads itself, and a static it sets before modems are
/// built. The file serves a standalone station; this serves a host that already owns the
/// hardware.</para>
/// <para><b>The case this exists for.</b> Run in-process inside packet.net, packet.net owns the
/// serial connection to the radio. Opening it a second time from in here would at best fail and at
/// worst fight the owner for the port. Instead packet.net wraps its own radio and registers it:
/// <code>
/// ChannelBusySources.Host = new RadioBusySource(myRadio, busyAboveDbm: -110);
/// </code>
/// Any <c>IRadioControl</c> works, so this is not Tait-specific even though the standalone opener
/// is.</para>
/// <para>Set it before the first modem is created. It is read once per modem, at construction.</para>
/// </remarks>
public static class ChannelBusySources
{
    /// <summary>
    /// A source supplied by the host, which wins over the station file. Null means there is none,
    /// and a standalone station falls back to opening its own radio.
    /// </summary>
    public static IChannelBusySource? Host { get; set; }

    /// <summary>
    /// The source a modem should use: the host's if it registered one, else the station's own
    /// radio if it configured one, else null for no carrier sense from the radio at all.
    /// </summary>
    public static IChannelBusySource? Resolve()
    {
        if (Host is not null)
        {
            return Host;
        }

        try
        {
            StationRadio? station = StationRadio.Load();
            return station?.WantsCarrierSense == true
                ? TaitCarrierSense.ForStation(station)
                : null;
        }
        catch (Exception e)
        {
            // The belt to TaitCarrierSense's braces. Reading a file and opening a radio must never
            // be able to stop a modem being built, because a modem that cannot be built is a
            // daemon that will not start.
            Console.Error.WriteLine(
                $"ofdm-fm: carrier sense could not be set up ({e.Message}). "
                + "Carrying on without it.");
            return null;
        }
    }
}
