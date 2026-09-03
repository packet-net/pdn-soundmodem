using Packet.SoundModem.Iq;
using M0LTE.Dsp;

namespace Packet.SoundModem.UberSdr;

/// <summary>One UberSDR instance's HTTP/WebSocket endpoint.</summary>
/// <param name="Host">Hostname or address.</param>
/// <param name="Port">TCP port (443 for the public instances).</param>
/// <param name="Ssl">True for <c>https</c>/<c>wss</c>.</param>
public readonly record struct UberSdrEndpoint(string Host, int Port, bool Ssl)
{
    /// <summary>Base URL for the REST pre-flight calls.</summary>
    public string HttpBase => $"{(Ssl ? "https" : "http")}://{Host}:{Port}";

    /// <summary>WebSocket scheme for the stream.</summary>
    public string WebSocketScheme => Ssl ? "wss" : "ws";

    /// <summary>The receiver's own page, as a link for a visitor: the scheme and host, with the
    /// default port left off.</summary>
    public string PublicUrl => $"{(Ssl ? "https" : "http")}://{this}/";

    /// <summary>The endpoint as an operator would write it, with the default port left off.</summary>
    public override string ToString() =>
        Port == (Ssl ? 443 : 80) ? Host : $"{Host}:{Port}";
}

/// <summary>How to receive from an UberSDR instance: where to tune, which sideband to
/// demodulate out of the IQ, and at what audio rate to deliver it.</summary>
public sealed record UberSdrTuning
{
    /// <summary>Where to tune the receiver, in Hz. The IQ arrives centred here, so for SSB work
    /// this is the dial frequency - ±24 kHz of complex baseband around it for <c>iq48</c>.</summary>
    public required int FrequencyHz { get; init; }

    /// <summary>Which sideband to demodulate. USB is the data-mode norm.</summary>
    public Sideband Sideband { get; init; } = Sideband.Upper;

    /// <summary>The audio rate to deliver, Hz - the channel's DSP rate. Must divide the IQ
    /// rate (48000 for <c>iq48</c>).</summary>
    public int OutputRate { get; init; } = 12000;

    /// <summary>The receiver's IQ mode: <c>iq48</c> (48 kHz, universally available) or
    /// <c>iq96</c> where the instance allows it.</summary>
    public string Mode { get; init; } = "iq48";

    /// <summary>Password for a protected instance; null for a public one.</summary>
    public string? Password { get; init; }

    /// <summary>Lower edge of the synthesised SSB passband, Hz above the dial.</summary>
    /// <remarks>
    /// Because we hold the complex baseband, <em>we</em> choose the receive filter rather than
    /// inheriting a rig's. The default spans the daemon's nominal 300-2700 Hz band plan with
    /// room either side, so nothing a plan can legally place gets clipped on the way in.
    /// </remarks>
    public double SsbLowHz { get; init; } = 150;

    /// <summary>Upper edge of the synthesised SSB passband, Hz above the dial. See
    /// <see cref="SsbLowHz"/>.</summary>
    public double SsbHighHz { get; init; } = 3450;

    /// <summary>Audio discarded after each connect, in milliseconds. The instances ramp their
    /// level over the first ~0.7-1.0 s of a stream (measured 2026-07-24), so a second of guard
    /// keeps that transient out of the modems and out of the waterfall.</summary>
    public int StartupGuardMs { get; init; } = 1000;

    /// <summary>Linear gain applied to the demodulated audio. 1.0 delivers the receiver's own
    /// scaling; raise it to bring a quiet instance up to soundcard-like levels.</summary>
    public float Gain { get; init; } = 1.0f;
}

/// <summary>
/// Parses the <c>--device ubersdr:&lt;instance&gt;</c> device string.
/// </summary>
/// <remarks>
/// <para>The instance may be written as a bare host (<c>ubersdr:m9psy-1.instance.ubersdr.org</c>),
/// a host and port (<c>…:8080</c>), or the URL an operator would paste out of a browser
/// (<c>ubersdr:https://m9psy-1.instance.ubersdr.org/</c>) - all three name the same thing, and
/// the third is the one that is actually to hand.</para>
/// <para>HTTPS on 443 is assumed unless the string says otherwise, because that is what every
/// public instance runs.</para>
/// </remarks>
public static class UberSdrDevice
{
    private const string Prefix = "ubersdr:";

    /// <summary>True when <paramref name="device"/> selects an UberSDR instance.</summary>
    public static bool IsUberSdr(string device) =>
        device.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Splits a <c>ubersdr:</c> device string into its endpoint.</summary>
    /// <exception cref="ArgumentException">The string is not a <c>ubersdr:</c> device at all.</exception>
    /// <exception cref="InvalidDataException">It is, but names no reachable instance - an
    /// operator's typo, phrased for them to act on.</exception>
    public static UberSdrEndpoint Parse(string device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!IsUberSdr(device))
        {
            throw new ArgumentException($"'{device}' is not a ubersdr: device string", nameof(device));
        }

        string rest = device[Prefix.Length..].Trim();
        if (rest.Length == 0)
        {
            throw new InvalidDataException(
                "\"device\": \"ubersdr:\" names no instance. Give it the receiver's address, e.g. "
                + "\"ubersdr:m9psy-1.instance.ubersdr.org\", or the URL you would open in a browser.");
        }

        // The pasted-URL form. Anything with a scheme goes through Uri so credentials, paths and
        // explicit ports are handled by something that already knows the grammar.
        if (rest.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(rest, UriKind.Absolute, out Uri? url)
                || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidDataException(
                    $"\"device\": \"{device}\" - '{rest}' is not an http:// or https:// URL. Give the "
                    + "receiver's address, or the URL you would open in a browser.");
            }

            return new UberSdrEndpoint(url.Host, url.Port, url.Scheme == Uri.UriSchemeHttps);
        }

        // host[:port], HTTPS assumed - every public instance is behind TLS.
        int colon = rest.LastIndexOf(':');
        if (colon < 0)
        {
            return new UberSdrEndpoint(rest, 443, Ssl: true);
        }

        string host = rest[..colon];
        if (!int.TryParse(rest[(colon + 1)..], out int port) || port is < 1 or > 65535)
        {
            throw new InvalidDataException(
                $"\"device\": \"{device}\" - '{rest[(colon + 1)..]}' is not a TCP port. Write the "
                + "instance as host, host:port, or the https:// URL you would open in a browser.");
        }

        return new UberSdrEndpoint(host, port, Ssl: true);
    }
}
