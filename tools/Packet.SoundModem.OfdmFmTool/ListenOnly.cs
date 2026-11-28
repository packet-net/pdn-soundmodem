using Packet.SoundModem.Channel;

namespace Packet.SoundModem.OfdmFmTool;

/// <summary>
/// A listening station with no PTT cannot key. Two things say so: the channel refuses every transmission, whoever asks, and the tool never starts the channel's transmitter or opens a PTT at all.
/// </summary>
internal static class ListenOnly
{
    public const string Reason = "no --ptt given: receive only";

    public static void Apply(SoundModemChannel channel) => channel.ReceiveOnlyReason = Reason;
}
