namespace Packet.SoundModem.Channel;

/// <summary>
/// A mode on a KISS sub-channel that is not an <see cref="Modems.IModem"/>: it takes whole frames
/// and decides for itself when they go on air. Added with <see cref="SoundModemChannel.AddPort"/>;
/// what it receives it hands up with <see cref="SoundModemChannel.DeliverReceived"/>.
/// </summary>
/// <remarks>
/// KISS servers, ACKMODE, the frame log, the journal and the in-process transport reach it through
/// <see cref="SoundModemChannel.EnqueueTransmit(int, byte[])"/> and the channel's receive events, exactly as
/// they reach a modem. A port may also implement <see cref="Modems.IHardwareControllable"/> to
/// take SETHW. The channel's CSMA loop, transmit lease and sample feed never see a port.
/// </remarks>
public interface ISubChannelPort
{
    /// <summary>The mode's name, as shown wherever a modem's would be.</summary>
    string Mode { get; }

    /// <summary>Sends one frame. The task completes when the frame has gone on air and faults
    /// when it was refused or dropped; the channel announces the frame accordingly.</summary>
    Task Transmit(byte[] frame);
}
