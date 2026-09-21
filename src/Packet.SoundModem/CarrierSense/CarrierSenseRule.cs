namespace Packet.SoundModem.CarrierSense;

/// <summary>
/// The one rule that turns a radio's opinion and the modems' own detects into "this channel is
/// occupied". Written down once so that the station and any modem that asks the question separately
/// cannot answer it differently.
/// </summary>
/// <remarks>
/// <para><b>It belongs to the station, not to a mode.</b> Every modem on a channel is listening to
/// the same receiver, so whether the channel is occupied is a fact about the receive path and not
/// about any waveform. It used to be decided per modem, which produced exactly the failure that
/// shape invites: on this bench a station running an OFDM-FM mode and an unused <c>afsk1200</c>
/// sub-channel alongside it would not transmit at all, because the AFSK modem heard every OFDM
/// burst, latched its energy detector for ten seconds on the return of the open-squelch noise, and
/// the transmit gate is the OR across every modem on the channel. A modem nobody was using held the
/// transmitter shut. See <c>docs/dev/carrier-sense.md</c>.</para>
/// </remarks>
public static class CarrierSenseRule
{
    /// <summary>
    /// Whether the channel is occupied.
    /// </summary>
    /// <param name="radio">What something outside the audio path says, or null for "no source, or
    /// a source with no opinion". See <see cref="IChannelBusySource"/>.</param>
    /// <param name="anyCarrierDetect">Whether any modem's demodulator has a coherent packet signal
    /// (<see cref="Modems.IModem.CarrierDetect"/>). A real lock, not an energy guess.</param>
    /// <param name="anyAudioBusy">Whether any modem's own audio answer says busy
    /// (<see cref="Modems.IModem.ChannelBusy"/>), which for every modem here is its carrier detect
    /// ored with an in-band energy detector.</param>
    /// <remarks>
    /// <para><b>When the radio knows, the audio energy detect is dropped entirely rather than
    /// ored in.</b> Not because it is redundant but because on an FM path it is actively harmful,
    /// and this is measured rather than argued. An FM receiver with the squelch open, which is how
    /// a data station runs, is LOUD when idle and QUIET when a carrier arrives: the carrier
    /// captures the discriminator and replaces band noise with modulation. Measured on radio1 in
    /// 5 ms blocks, idle reads -15.9 dBFS, a far end modulating -25, and a far end's unmodulated
    /// carrier -55. A detector waiting for a rise cannot fire on any of that, and worse, its floor
    /// sags to the quieted level during the burst, so the return of the noise at the END of the
    /// burst reads as a signal: simulated at these levels, busy for 9.6 s after a 0.4 s burst.
    /// Ored in, that is a station which cannot answer anybody for ten seconds after being spoken
    /// to.</para>
    /// <para><b>The carrier detect is kept, because it is not an energy test.</b> It is "a burst
    /// somebody is sending is on the air right now", which is a thing worth not transmitting over
    /// and is not fooled by the level going the wrong way.</para>
    /// <para><b>Null is not busy.</b> A source that has lost its link, a port that will not open, a
    /// radio in the wrong mode: all answer null, and null falls back to the audio answer, which
    /// leaves the station exactly where it was before it had a radio to ask. A carrier sense that
    /// can silence a station by losing a USB cable is the worse failure by a distance, and this
    /// bench has already met it once.</para>
    /// </remarks>
    public static bool Occupied(bool? radio, bool anyCarrierDetect, bool anyAudioBusy) =>
        radio is bool known ? known || anyCarrierDetect : anyAudioBusy;
}
