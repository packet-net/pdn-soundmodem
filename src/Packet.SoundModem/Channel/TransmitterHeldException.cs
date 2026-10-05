namespace Packet.SoundModem.Channel;

/// <summary>
/// Thrown by an <c>IPttControl.Key</c> that refuses a keyup for now rather than failing: the
/// radio is fine, but this is not a moment to transmit (a rig retuned somewhere the modems are
/// not, say).
/// </summary>
/// <remarks>
/// The channel refuses only the transmitter whose keyup it was, and leaves every other
/// transmitter's queue alone. Any other exception from <c>Key</c> is a broken keying path and
/// fails everything queued, as it always has.
/// </remarks>
public sealed class TransmitterHeldException : InvalidOperationException
{
    /// <summary>Creates one with the reason, as the journal should say it.</summary>
    public TransmitterHeldException(string message)
        : base(message)
    {
    }

    /// <summary>Creates one with no reason.</summary>
    public TransmitterHeldException()
    {
    }

    /// <summary>Creates one with a reason and a cause.</summary>
    public TransmitterHeldException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
