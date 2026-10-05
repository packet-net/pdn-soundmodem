using Packet.SoundModem.Channel;
using Packet.SoundModem.Ident;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// How a modem's Morse identification reaches the channel: queued under the identifier itself,
/// which is attributed to the modem's sub-channel so that the transmit lease treats the ident as
/// that modem's own transmission.
/// </summary>
/// <remarks>
/// Here rather than inline in <c>Program.cs</c> so that the one property the mailcast slot relies
/// on can be tested: while a sub-channel holds the transmit lease, its own ident still keys the
/// radio, and every other modem's waits.
/// </remarks>
internal static class IdentTransmission
{
    /// <summary>Makes <paramref name="identifier"/> part of <paramref name="subChannel"/>'s
    /// traffic for the transmit lease.</summary>
    internal static void Register(SoundModemChannel channel, int subChannel, StationIdentifier identifier) =>
        channel.TransmitLease.Attribute(identifier, subChannel);

    /// <summary>
    /// Whether to queue an ident now: one is owed, and no transmit lease that excludes it is
    /// held. An excluded ident is not even asked for, because it would be refused every time the
    /// poll came round for the length of the lease; it stays owed and goes once the lease ends.
    /// </summary>
    internal static bool ShouldSend(SoundModemChannel channel, StationIdentifier owed) =>
        owed.IdentificationDue && channel.TransmitLease.Admits(owed);

    /// <summary>
    /// Queues one identification. Queued like anything else, so it waits out a busy channel and
    /// a keyup in progress rather than transmitting over somebody. The TXDELAY budget is spent on
    /// silence: an SSB transmitter radiates nothing without audio, which is exactly what the PTT
    /// settling time wants.
    /// </summary>
    internal static Task SendAsync(SoundModemChannel channel, StationIdentifier owed) =>
        channel.EnqueueTransmit(txDelay =>
        {
            float[] tone = owed.Render();
            int lead = (int)Math.Round(txDelay / 1000.0 * channel.SampleRate);
            var audio = new float[lead + tone.Length];
            tone.CopyTo(audio, lead);
            return audio;
        },
        // This sub-channel's identifier is the transmitter, so the CW ident takes its own keyup
        // rather than lengthening somebody else's - the station is deaf for whatever it appends
        // itself to.
        source: owed);

    /// <summary>
    /// What a transmit lease closes with (<see cref="TransmitLease.Closing"/>): the holder's own
    /// ident, if its modem identifies and has transmitted since it last did, sent through
    /// <paramref name="identify"/>; nothing otherwise. Ofcom wants an ident at the end of a
    /// transmission, and the lease is held until it has gone so nobody else keys first.
    /// </summary>
    internal static Func<int, Task?> Closing(
        IReadOnlyDictionary<int, StationIdentifier> identifiers, Func<int, StationIdentifier, Task> identify) =>
        holder => identifiers.TryGetValue(holder, out StationIdentifier? owed) && owed.TransmittedSinceIdentification
            ? identify(holder, owed)
            : null;
}
