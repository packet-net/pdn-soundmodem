namespace Packet.SoundModem.UberSdr;

/// <summary>
/// The far end answered and said no for now: an UberSDR receiver refusing the stream upgrade
/// with HTTP 429 (rate limited, or out of its daily listening quota), or a monitor site refusing
/// a station's uplink (HTTP 401, 403 or 429).
/// </summary>
/// <remarks>
/// <para><b>When an embedding host sees it.</b> <c>UberSdrAudioInput.OpenAsync</c> throws
/// it when the receiver refuses the first connection with HTTP 429. Once the input is open, a
/// later refusal never escapes: the input waits out the refusal on its own, on a long and
/// escalating backoff, and says so in its journal. The message is written for an operator.</para>
/// <para><b>What to do about it.</b> Wait, and try again later. It carries no blame: quota and
/// rate limits are the receiver's to enforce, nothing on this side fixes them, and restarting
/// the host does not help. Every other failure to open a receiver arrives as an
/// <see cref="InvalidOperationException"/>, or as the transport's own
/// <see cref="System.Net.Http.HttpRequestException"/>, <see cref="System.Net.WebSockets.WebSocketException"/>
/// or <see cref="IOException"/>.</para>
/// </remarks>
public sealed class UberSdrRefusedException : Exception
{
    /// <summary>Creates the exception with no further detail.</summary>
    public UberSdrRefusedException()
    {
    }

    /// <summary>Creates the exception with an operator-facing message.</summary>
    public UberSdrRefusedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with an operator-facing message and the transport failure
    /// that carried the refusal.</summary>
    public UberSdrRefusedException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
