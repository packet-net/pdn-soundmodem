using System.Net.Sockets;

namespace Packet.SoundModem.SdrConnect;

/// <summary>Why SDRconnect could not be opened, for a program that wants to act on it rather than
/// read the message.</summary>
public enum SdrConnectFailure
{
    /// <summary>The machine answered and nothing was listening on the port: SDRconnect is not
    /// running, its WebSocket server is switched off, or a firewall rejected the connection.</summary>
    Refused,

    /// <summary>Nothing answered in time: the machine is off or unreachable, or a firewall dropped
    /// the connection (Windows does for SDRconnect on a Public network).</summary>
    NoAnswer,

    /// <summary>The host name does not resolve.</summary>
    NameNotFound,

    /// <summary>The connection failed some other way; the message says how.</summary>
    Unreachable,

    /// <summary>The connection was accepted but did not finish setting up in time: perhaps not
    /// SDRconnect's WebSocket port.</summary>
    NoSetUp,

    /// <summary>The connection closed or failed while it was being set up.</summary>
    Dropped,

    /// <summary>SDRconnect says this client may not control its radio (<c>can_control</c> false).</summary>
    CannotControl,

    /// <summary>SDRconnect did not say its sample rate, so its stream cannot be used.</summary>
    NoSampleRate,
}

/// <summary>SDRconnect could not be reached. An <see cref="IOException"/>, as before it had a
/// reason.</summary>
public sealed class SdrConnectUnreachableException : IOException
{
    /// <summary>Makes one.</summary>
    public SdrConnectUnreachableException(SdrConnectFailure reason, string message, Exception? inner = null)
        : base(message, inner) => Reason = reason;

    /// <summary>Why: <see cref="SdrConnectFailure.Refused"/>, <see cref="SdrConnectFailure.NoAnswer"/>,
    /// <see cref="SdrConnectFailure.NameNotFound"/> or <see cref="SdrConnectFailure.Unreachable"/>.</summary>
    public SdrConnectFailure Reason { get; }

    /// <summary>What a failed connect says, with the socket's own words when they are buried: the
    /// real <c>ClientWebSocket</c> says only "Unable to connect to the remote server", which does not
    /// tell a refused port from a name that does not resolve or a host with no route.</summary>
    internal static string Describe(Exception error)
    {
        for (Exception? e = error.InnerException; e is not null; e = e.InnerException)
        {
            if (e is SocketException socket && !error.Message.Contains(socket.Message, StringComparison.OrdinalIgnoreCase))
            {
                return $"{error.Message} ({socket.Message})";
            }
        }

        return error.Message;
    }

    /// <summary>The reason a failed connect gives, from the exception it threw.</summary>
    internal static SdrConnectFailure Classify(Exception error)
    {
        for (Exception? e = error; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case SdrConnectUnreachableException known:
                    return known.Reason;
                case TimeoutException:
                    return SdrConnectFailure.NoAnswer;
                case SocketException socket:
                    return socket.SocketErrorCode switch
                    {
                        SocketError.ConnectionRefused => SdrConnectFailure.Refused,
                        SocketError.TimedOut or SocketError.HostUnreachable or SocketError.NetworkUnreachable => SdrConnectFailure.NoAnswer,
                        SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => SdrConnectFailure.NameNotFound,
                        _ => SdrConnectFailure.Unreachable,
                    };
            }
        }

        return SdrConnectFailure.Unreachable;
    }
}

/// <summary>SDRconnect answered but cannot be used. An <see cref="InvalidOperationException"/>,
/// as before it had a reason; the message is written for an operator.</summary>
public sealed class SdrConnectRefusedException : InvalidOperationException
{
    /// <summary>Makes one.</summary>
    public SdrConnectRefusedException(SdrConnectFailure reason, string message)
        : base(message) => Reason = reason;

    /// <summary>Why: <see cref="SdrConnectFailure.NoSetUp"/>, <see cref="SdrConnectFailure.Dropped"/>,
    /// <see cref="SdrConnectFailure.CannotControl"/> or <see cref="SdrConnectFailure.NoSampleRate"/>.</summary>
    public SdrConnectFailure Reason { get; }
}
