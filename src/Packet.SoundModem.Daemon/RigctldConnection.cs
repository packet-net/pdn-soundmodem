using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace Packet.SoundModem.Daemon;

/// <summary>Where rigctld listens: a host and a TCP port.</summary>
/// <param name="Host">A name or an address. An IPv6 address is written in brackets in the
/// config, <c>[::1]:4532</c>, and held here without them.</param>
/// <param name="Port">The TCP port, 4532 by default.</param>
internal sealed record RigctldEndpoint(string Host, int Port)
{
    /// <summary>rigctld's own default port.</summary>
    internal const int DefaultPort = 4532;

    /// <summary>Where a <c>rig</c> section with no <c>rigctld</c> key looks.</summary>
    internal const string Default = "127.0.0.1:4532";

    /// <summary>
    /// Reads <c>host:port</c>, <c>host</c> (the default port), <c>[v6]:port</c> or <c>[v6]</c>.
    /// </summary>
    /// <returns>Null, with <paramref name="why"/> saying what is wrong, for anything else.</returns>
    internal static RigctldEndpoint? TryParse(string? text, out string why)
    {
        why = "";
        string value = (text ?? "").Trim();
        if (value.Length == 0)
        {
            why = "it is empty";
            return null;
        }

        string host;
        string? port = null;
        if (value.StartsWith('['))
        {
            int close = value.IndexOf(']', StringComparison.Ordinal);
            if (close < 0)
            {
                why = "an IPv6 address opened with [ is never closed with ]";
                return null;
            }

            host = value[1..close];
            string rest = value[(close + 1)..];
            if (rest.Length > 0)
            {
                if (!rest.StartsWith(':'))
                {
                    why = "only \":port\" may follow an IPv6 address in brackets";
                    return null;
                }

                port = rest[1..];
            }
        }
        else
        {
            int colon = value.LastIndexOf(':');
            if (colon >= 0 && value.IndexOf(':', StringComparison.Ordinal) != colon)
            {
                why = "an IPv6 address has to be written in brackets, as [::1]:4532";
                return null;
            }

            host = colon >= 0 ? value[..colon] : value;
            port = colon >= 0 ? value[(colon + 1)..] : null;
        }

        if (host.Length == 0 || host.Any(char.IsWhiteSpace))
        {
            why = "there is no host before the port";
            return null;
        }

        int number = DefaultPort;
        if (port is not null
            && (!int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out number)
                || number is < 1 or > 65535))
        {
            why = $"\"{port}\" is not a TCP port (1-65535)";
            return null;
        }

        return new RigctldEndpoint(host, number);
    }

    /// <inheritdoc />
    public override string ToString() =>
        Host.Contains(':', StringComparison.Ordinal) ? $"[{Host}]:{Port}" : $"{Host}:{Port}";
}

/// <summary>rigctld answered a command with an error code rather than <c>RPRT 0</c>.</summary>
internal sealed class RigctldException(string command, int code)
    : Exception($"rigctld refused \"{command}\": RPRT {code} ({Describe(code)})")
{
    /// <summary>The command, as sent.</summary>
    internal string Command { get; } = command;

    /// <summary>Hamlib's error code, negative as rigctld prints it.</summary>
    internal int Code { get; } = code;

    /// <summary>Hamlib's name for an error code, from <c>rig_errcode_e</c> in rig.h.</summary>
    internal static string Describe(int code) => Math.Abs(code) switch
    {
        1 => "invalid parameter",
        2 => "invalid configuration",
        3 => "out of memory",
        4 => "not implemented by this rig's backend",
        5 => "the rig did not answer in time",
        6 => "input/output error talking to the rig",
        7 => "internal Hamlib error",
        8 => "protocol error",
        9 => "the rig rejected the command",
        10 => "argument truncated",
        11 => "not available on this rig",
        12 => "that VFO cannot be addressed",
        13 => "bus error",
        14 => "bus collision",
        15 => "missing argument",
        16 => "invalid VFO",
        17 => "argument out of range",
        18 => "deprecated",
        19 => "security error",
        20 => "the rig is not powered on",
        _ => "an error Hamlib has no name for here",
    };
}

/// <summary>
/// One TCP connection to rigctld, speaking its plain text protocol: one command per line, and
/// either the value asked for or <c>RPRT n</c> back.
/// </summary>
/// <remarks>
/// <para>Only the handful of commands a packet station needs: <c>f</c>/<c>F</c> for the dial,
/// <c>m</c>/<c>M</c> for the mode and passband, <c>t</c>/<c>T</c> for PTT. The plain (not
/// extended) response form is used because every rigctld since Hamlib 1.2 speaks it, and the
/// short forms of the commands for the same reason.</para>
/// <para>Not thread-safe: one command is one write and one or two reads, and two callers
/// interleaving would each read the other's answer. <see cref="RigControl"/> serialises every
/// use under its own lock.</para>
/// <para>An <see cref="IOException"/> from any command means the connection is no longer in a
/// known state (a half-read answer, a closed socket, a reply that never came) and must be thrown
/// away. A <see cref="RigctldException"/> is an answer and leaves it usable.</para>
/// </remarks>
internal sealed class RigctldConnection : IDisposable
{
    /// <summary>How long one reply may take. Real I/O on a real socket, so a real timeout: a
    /// rigctld that accepts and then never answers is hung, and holding a keyup behind it for
    /// longer than this would be worse than giving up on it.</summary>
    internal static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(3);

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly StreamReader _reader;

    private RigctldConnection(TcpClient client, RigctldEndpoint endpoint)
    {
        _client = client;
        Endpoint = endpoint;
        _stream = client.GetStream();
        _stream.ReadTimeout = (int)ReplyTimeout.TotalMilliseconds;
        _stream.WriteTimeout = (int)ReplyTimeout.TotalMilliseconds;
        _reader = new StreamReader(_stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false);
    }

    /// <summary>Where this connection goes.</summary>
    internal RigctldEndpoint Endpoint { get; }

    /// <summary>Connects, or throws <see cref="IOException"/> saying why not.</summary>
    internal static async Task<RigctldConnection> OpenAsync(
        RigctldEndpoint endpoint, TimeSpan timeout, CancellationToken cancellation)
    {
        var client = new TcpClient { NoDelay = true };
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(timeout);
        try
        {
            await client.ConnectAsync(endpoint.Host, endpoint.Port, limit.Token).ConfigureAwait(false);
            return new RigctldConnection(client, endpoint);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            client.Dispose();
            throw new IOException($"no answer within {timeout.TotalSeconds:0} s");
        }
        catch (SocketException refused)
        {
            client.Dispose();
            throw new IOException(refused.Message, refused);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>The dial frequency in Hz (<c>f</c>).</summary>
    internal long GetFrequency()
    {
        string answer = Ask("f", 1)[0];
        if (!double.TryParse(answer, NumberStyles.Float, CultureInfo.InvariantCulture, out double hz))
        {
            throw new IOException($"rigctld answered \"f\" with \"{answer}\", which is not a frequency");
        }

        return (long)Math.Round(hz);
    }

    /// <summary>Sets the dial frequency in Hz (<c>F</c>).</summary>
    internal void SetFrequency(long hz) =>
        Command(string.Create(CultureInfo.InvariantCulture, $"F {hz}"));

    /// <summary>The mode and passband (<c>m</c>).</summary>
    internal (string Mode, int PassbandHz) GetMode()
    {
        string[] answer = Ask("m", 2);
        if (!int.TryParse(answer[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int passband))
        {
            throw new IOException($"rigctld answered \"m\" with a passband of \"{answer[1]}\", which is not a number");
        }

        return (answer[0], passband);
    }

    /// <summary>Sets the mode and passband (<c>M</c>). A passband of 0 is the rig's normal
    /// width for that mode.</summary>
    internal void SetMode(string mode, int passbandHz) =>
        Command(string.Create(CultureInfo.InvariantCulture, $"M {mode} {passbandHz}"));

    /// <summary>Keys or unkeys the transmitter (<c>T 1</c>, <c>T 0</c>).</summary>
    internal void SetPtt(bool keyed) => Command(keyed ? "T 1" : "T 0");

    /// <summary>Whether the rig says it is transmitting (<c>t</c>).</summary>
    internal bool GetPtt() => Ask("t", 1)[0] != "0";

    /// <summary>A set command: expects <c>RPRT 0</c>.</summary>
    private void Command(string command)
    {
        Send(command);
        string reply = ReadLine(command);
        int code = ReportCode(reply)
            ?? throw new IOException($"rigctld answered \"{command}\" with \"{reply}\" rather than RPRT");
        if (code != 0)
        {
            throw new RigctldException(command, code);
        }
    }

    /// <summary>A get command: expects <paramref name="lines"/> lines of value, or one
    /// <c>RPRT n</c> line instead when the rig cannot answer.</summary>
    private string[] Ask(string command, int lines)
    {
        Send(command);
        var answer = new string[lines];
        for (int i = 0; i < lines; i++)
        {
            answer[i] = ReadLine(command);
            if (i == 0 && ReportCode(answer[0]) is int code)
            {
                throw new RigctldException(command, code == 0 ? -8 : code);
            }
        }

        return answer;
    }

    private void Send(string command)
    {
        byte[] line = Encoding.ASCII.GetBytes(command + "\n");
        _stream.Write(line);
        _stream.Flush();
    }

    private string ReadLine(string command)
    {
        string? line = _reader.ReadLine()
            ?? throw new IOException($"rigctld closed the connection while answering \"{command}\"");
        return line.Trim();
    }

    /// <summary>The number in an <c>RPRT n</c> line, or null for any other line.</summary>
    private static int? ReportCode(string line) =>
        line.StartsWith("RPRT ", StringComparison.Ordinal)
        && int.TryParse(line.AsSpan(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out int code)
            ? code
            : null;

    /// <inheritdoc />
    public void Dispose()
    {
        _reader.Dispose();
        _client.Dispose();
    }
}

/// <summary>A dial, a mode and a passband: what a rig is tuned to, or is to be.</summary>
/// <param name="DialHz">The dial (carrier) frequency in Hz.</param>
/// <param name="Mode">Hamlib's name for the mode, upper case: <c>USB</c>, <c>PKTUSB</c>,
/// <c>FM</c> and so on.</param>
/// <param name="PassbandHz">The passband in Hz; 0 is the rig's normal width for the mode.</param>
internal sealed record RigTuning(long DialHz, string Mode, int PassbandHz)
{
    /// <summary>Said in a journal line: <c>7.052000 MHz USB (2400 Hz passband)</c>.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{DialHz / 1_000_000.0:F6} MHz {Mode}")
        + (PassbandHz > 0 ? $" ({PassbandHz} Hz passband)" : " (normal passband)");
}

/// <summary>Hamlib's mode names, and which kind of radio each one is.</summary>
internal static class RigModes
{
    /// <summary>The modes this station will ask a rig for, with the sideband each implies (null
    /// for one that is neither, which can be tuned to but never planned with).</summary>
    private static readonly Dictionary<string, string?> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USB"] = "usb",
        ["PKTUSB"] = "usb",
        ["LSB"] = "lsb",
        ["PKTLSB"] = "lsb",
        ["FM"] = "fm",
        ["FMN"] = "fm",
        ["PKTFM"] = "fm",
        ["PKTFMN"] = "fm",
        ["AM"] = null,
        ["AMN"] = null,
        ["PKTAM"] = null,
        ["CW"] = null,
        ["CWR"] = null,
        ["RTTY"] = null,
        ["RTTYR"] = null,
        ["WFM"] = null,
        ["DSB"] = null,
        ["SAM"] = null,
    };

    /// <summary>The list, for a refusal to quote.</summary>
    internal static string List => string.Join(", ", Known.Keys);

    /// <summary>Whether this is a mode the station will ask for.</summary>
    internal static bool IsKnown(string? mode) => mode is not null && Known.ContainsKey(mode);

    /// <summary>The sideband a mode implies: <c>usb</c>, <c>lsb</c>, <c>fm</c> or null.</summary>
    internal static string? SidebandOf(string mode) => Known.GetValueOrDefault(mode);

    /// <summary>The plain mode for a band plan's sideband: USB, LSB or FM.</summary>
    internal static string ForSideband(string sideband) =>
        sideband.ToLowerInvariant() switch
        {
            "lsb" => "LSB",
            "fm" => "FM",
            _ => "USB",
        };
}
