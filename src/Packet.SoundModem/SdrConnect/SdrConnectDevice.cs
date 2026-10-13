namespace Packet.SoundModem.SdrConnect;

/// <summary>An SDRconnect instance's WebSocket API address.</summary>
/// <param name="Host">Hostname or address (an IPv6 address without its brackets).</param>
/// <param name="Port">TCP port.</param>
public readonly record struct SdrConnectEndpoint(string Host, int Port)
{
    /// <summary>The address as an operator would write it.</summary>
    public override string ToString() =>
        Host.Contains(':', StringComparison.Ordinal) ? $"[{Host}]:{Port}" : $"{Host}:{Port}";

    /// <summary>The WebSocket to connect to. The API document names a port and no path, so the
    /// root is asked for.</summary>
    public Uri WebSocketUri => new($"ws://{this}/");

    /// <summary>
    /// Reads an SDRconnect address, <c>host</c> or <c>host:port</c>, an IPv6 address in brackets
    /// (<c>[fd00::5]:5454</c>); the port defaults to SDRconnect's, 5454. For a program that takes
    /// the address from its own setting: <paramref name="problem"/> says what is wrong without
    /// naming any setting.
    /// </summary>
    /// <returns>True with <paramref name="endpoint"/> set, or false with <paramref name="problem"/> set.</returns>
    public static bool TryParse(
        string? address, out SdrConnectEndpoint endpoint,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? problem)
    {
        endpoint = default;
        string rest = address?.Trim() ?? "";
        if (rest.Length == 0)
        {
            problem = "no host is given; give the machine SDRconnect runs on, e.g. shack-pc.local or 192.168.1.20:5454";
            return false;
        }

        problem = $"'{rest}' is not a host and a TCP port. Write the machine running SDRconnect as host or "
            + $"host:port (SDRconnect's WebSocket port, {SdrConnectProtocol.DefaultPort}, is the default), and an IPv6 "
            + "address in brackets, e.g. [fd00::5]:5454";
        if (rest.Contains('/', StringComparison.Ordinal))
        {
            return false;
        }

        string host;
        string? port = null;
        if (rest.StartsWith('['))
        {
            int close = rest.IndexOf(']', StringComparison.Ordinal);
            if (close < 2)
            {
                return false;
            }

            host = rest[1..close];
            if (Uri.CheckHostName(host) != UriHostNameType.IPv6)
            {
                return false; // brackets are for an IPv6 address only
            }

            string after = rest[(close + 1)..];
            if (after.Length > 0)
            {
                if (after[0] != ':')
                {
                    return false;
                }

                port = after[1..];
            }
        }
        else
        {
            int colon = rest.IndexOf(':', StringComparison.Ordinal);
            if (colon >= 0 && rest.IndexOf(':', colon + 1) >= 0)
            {
                // More than one colon and no brackets: an IPv6 address that cannot carry a port.
                return false;
            }

            host = colon < 0 ? rest : rest[..colon];
            port = colon < 0 ? null : rest[(colon + 1)..];
        }

        // The host goes into a ws:// URI, which throws at the connect for a name it cannot parse
        // (a space, a '@' taken as user information): refused here, in words, instead.
        if (host.Length == 0 || Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            return false;
        }

        int number = SdrConnectProtocol.DefaultPort;
        if (port is not null && (!int.TryParse(port, out number) || number is < 1 or > 65535))
        {
            return false;
        }

        endpoint = new SdrConnectEndpoint(host, number);
        problem = null;
        return true;
    }
}

/// <summary>Parses the <c>--device sdrconnect:HOST[:PORT]</c> device string.</summary>
/// <remarks>
/// A bare host takes SDRconnect's own WebSocket port, 5454. An IPv6 address goes in brackets, as
/// in a URL: <c>sdrconnect:[fd00::5]:5454</c>.
/// </remarks>
public static class SdrConnectDevice
{
    private const string Prefix = "sdrconnect:";

    /// <summary>True when <paramref name="device"/> selects an SDRconnect instance.</summary>
    public static bool IsSdrConnect(string device) =>
        device.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Splits an <c>sdrconnect:</c> device string into its endpoint
    /// (<see cref="SdrConnectEndpoint.TryParse"/> on what follows the prefix).</summary>
    /// <exception cref="ArgumentException">The string is not an <c>sdrconnect:</c> device at all.</exception>
    /// <exception cref="InvalidDataException">It is, but names no SDRconnect, in words for the
    /// operator.</exception>
    public static SdrConnectEndpoint Parse(string device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!IsSdrConnect(device))
        {
            throw new ArgumentException($"'{device}' is not an sdrconnect: device string", nameof(device));
        }

        string rest = device[Prefix.Length..].Trim();
        if (rest.Length == 0)
        {
            throw new InvalidDataException(
                "\"device\": \"sdrconnect:\" names no SDRconnect. Give the host SDRconnect runs on, "
                + "e.g. \"sdrconnect:shack-pc.local\" or \"sdrconnect:192.168.1.20:5454\".");
        }

        if (!SdrConnectEndpoint.TryParse(rest, out SdrConnectEndpoint endpoint, out _))
        {
            throw new InvalidDataException(
                $"\"device\": \"{device}\" - '{rest}' is not a host and a TCP port. Write the PC running "
                + $"SDRconnect as host or host:port (SDRconnect's WebSocket port, {SdrConnectProtocol.DefaultPort}, "
                + "is the default), and an IPv6 address in brackets, e.g. \"sdrconnect:[fd00::5]:5454\".");
        }

        return endpoint;
    }
}
