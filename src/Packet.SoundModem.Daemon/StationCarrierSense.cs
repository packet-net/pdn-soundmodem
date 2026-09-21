using Packet.SoundModem.CarrierSense;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// Turns a station's <c>carrierSense</c> section into the one <see cref="IChannelBusySource"/> its
/// audio channel consults, and says out loud what it did.
/// </summary>
/// <remarks>
/// <para><b>Once per station, not once per modem.</b> Whether the channel is occupied is a fact
/// about the receive path, so every modem on that path gets the same answer and one serial port is
/// opened for all of them. See <see cref="CarrierSenseRule"/> for what the answer is used for.</para>
/// <para><b>Nothing here may stop the daemon starting.</b> A station that cannot reach its radio
/// still has to work: every failure below ends with a printed line and a null, which leaves the
/// station exactly where it was before this feature existed.</para>
/// </remarks>
internal static class StationCarrierSense
{
    /// <summary>
    /// Opens the station's carrier-sense source, or returns null if it has none.
    /// </summary>
    /// <param name="config">The <c>carrierSense</c> section, or null if the file has none.</param>
    /// <param name="say">Where the start-up line goes.</param>
    /// <param name="owned">What this call opened and the caller must dispose. Null when the
    /// source came from somewhere that owns it already, which must not be disposed from here.
    /// </param>
    /// <param name="audioFallback">Whether the channel should read carrier sense off the shape
    /// of the received audio for want of anything better. False whenever a radio answered, since
    /// a measurement beats an inference.</param>
    public static IChannelBusySource? Open(
        CarrierSenseConfig? config,
        Action<string> say,
        out IDisposable? owned,
        out bool audioFallback)
    {
        owned = null;
        audioFallback = config?.AudioFallback ?? true;

        // An in-process host owns its radio and registered it before building anything. It wins
        // over the file, because opening the same serial port a second time would at best fail
        // and at worst fight the owner for it.
        if (ChannelBusySources.Host is { } host)
        {
            say("carrier sense: using the radio the host registered");
            audioFallback = false;
            return host;
        }

        if (config is null)
        {
            // No section. The untracked station file is the path the ofdm-fm bench stations were
            // set up with before there was a section, and it keeps working so that upgrading a
            // station does not quietly take its carrier sense away.
            IChannelBusySource? legacy = ChannelBusySources.Resolve();
            if (legacy is not null)
            {
                say($"carrier sense: from {StationRadio.FileName}. Move it into the "
                    + "\"carrierSense\" section of this station's config file.");
                owned = legacy as IDisposable;
                audioFallback = false;
            }

            return legacy;
        }

        if (config.Radio.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            say("carrier sense: \"radio\": \"none\", so no serial port is opened");
            return null;
        }

        if (string.IsNullOrWhiteSpace(config.Port))
        {
            // Said rather than passed over: an operator who wrote the section meant to get this,
            // and a section with no port does nothing at all.
            say("carrier sense: the \"carrierSense\" section has no \"port\", so no radio is "
                + "opened");
            return null;
        }

        var station = new StationRadio(
            config.Port, config.Baud, config.BusyAboveDbm, config.PollMilliseconds);

        // Every message about what it found, and every failure, comes from the source itself.
        TaitCarrierSense? sense = TaitCarrierSense.ForStation(station);
        owned = sense;
        audioFallback = false;

        if (config.BusyAboveDbm is null)
        {
            say("carrier sense: no \"busyAboveDbm\", so the radio's carrier-detect line alone. "
                + "On a station whose squelch is held open that reports nothing until the first "
                + "squelch edge, which on a quiet channel may never come.");
        }

        return sense;
    }
}
