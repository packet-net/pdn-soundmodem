using Packet.SoundModem.Channel;

namespace Packet.SoundModem.FlexRadio;

/// <summary>The Flex triplet a channel runs through: the DAX-RX input, the DAX-TX output
/// (already wrapped to the DSP rate), the slice PTT, plus the shared station (and the
/// in-process mock, when <c>flex:mock</c>).</summary>
public sealed class FlexRuntime : IAsyncDisposable
{
    internal FlexRuntime(
        MockFlexRadio? mock, FlexStation station, IAudioInput input, IAudioOutput output, IPttControl ptt)
    {
        Mock = mock;
        Station = station;
        Input = input;
        Output = output;
        Ptt = ptt;
    }

    /// <summary>The in-process mock radio, when the device is <c>flex:mock</c>.</summary>
    public MockFlexRadio? Mock { get; }

    /// <summary>The station (shared session + DAX stream ids).</summary>
    public FlexStation Station { get; }

    /// <summary>The DAX-RX audio source (at the DAX rate).</summary>
    public IAudioInput Input { get; }

    /// <summary>The DAX-TX audio sink (at the DSP rate — upsampled internally when needed).</summary>
    public IAudioOutput Output { get; }

    /// <summary>The slice PTT.</summary>
    public IPttControl Ptt { get; }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        (Input as IDisposable)?.Dispose();
        (Output as IDisposable)?.Dispose();
        await Station.DisposeAsync().ConfigureAwait(false);
        if (Mock is not null)
        {
            await Mock.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Parses <c>--device flex:&lt;radio&gt;[:slice]</c> and opens the Flex triplet: a shared
/// <see cref="FlexClient"/> feeding a <see cref="FlexAudioInput"/>, a
/// <see cref="FlexAudioOutput"/> (wrapped in an <see cref="UpsamplingAudioOutput"/> for the
/// 12 kHz modes) and a <see cref="FlexPtt"/>. <c>radio</c> is <c>discover</c>, an IP
/// (<c>host[:port]</c>), a discovery spec (<c>serial=…</c>/<c>name=…</c>), or <c>mock</c>
/// (an in-process fake). See docs/flex-integration.md §4.
/// </summary>
public static class FlexDevice
{
    private const string Prefix = "flex:";

    /// <summary>True when <paramref name="device"/> selects a FlexRadio.</summary>
    public static bool IsFlex(string device) =>
        device.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The parsed radio spec and slice letter.</summary>
    /// <param name="RadioSpec">The radio portion (<c>discover</c>/IP/<c>serial=…</c>/<c>mock</c>).</param>
    /// <param name="SliceLetter">The slice letter (default "A").</param>
    public readonly record struct FlexSpec(string RadioSpec, string SliceLetter);

    /// <summary>Splits a <c>flex:</c> device string into its radio and slice parts. A
    /// trailing single letter A–H is the slice; everything else is the radio.</summary>
    public static FlexSpec Parse(string device)
    {
        string rest = device[Prefix.Length..];
        string[] segments = rest.Split(':');
        if (segments.Length >= 2 && IsSliceLetter(segments[^1]))
        {
            return new FlexSpec(string.Join(':', segments[..^1]), segments[^1].ToUpperInvariant());
        }

        return new FlexSpec(rest, "A");
    }

    /// <summary>Opens the Flex triplet for the given device string and DSP rate.</summary>
    public static async Task<FlexRuntime> OpenAsync(
        string device, int dspRate, int packetBuffer, CancellationToken cancellation)
    {
        FlexSpec spec = Parse(device);
        DaxStreamFormat format = DaxStreamFormat.ForDspRate(dspRate);

        MockFlexRadio? mock = null;
        FlexClient client;
        if (spec.RadioSpec.Equals("mock", StringComparison.OrdinalIgnoreCase))
        {
            mock = new MockFlexRadio(format, MockRxMode.Loopback, sliceLetter: spec.SliceLetter);
            mock.Start();
            client = await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort, cancellation)
                .ConfigureAwait(false);
        }
        else if (spec.RadioSpec.Equals("discover", StringComparison.OrdinalIgnoreCase))
        {
            client = await FlexClient.DiscoverAndConnectAsync(null, TimeSpan.FromSeconds(10), cancellation)
                .ConfigureAwait(false);
        }
        else if (spec.RadioSpec.Contains('=', StringComparison.Ordinal))
        {
            client = await FlexClient.DiscoverAndConnectAsync(
                spec.RadioSpec, TimeSpan.FromSeconds(10), cancellation).ConfigureAwait(false);
        }
        else
        {
            (string host, int port) = SplitHostPort(spec.RadioSpec);
            client = await FlexClient.ConnectAsync(host, port, Vita49.RadioVitaPort, cancellation)
                .ConfigureAwait(false);
        }

        var options = new FlexStationOptions { SliceLetter = spec.SliceLetter };
        FlexStation station = await FlexStation.SetUpAsync(client, format, options, cancellation)
            .ConfigureAwait(false);

        IAudioInput input = station.CreateAudioInput(packetBuffer);
        FlexAudioOutput flexOutput = station.CreateAudioOutput(paceRealTime: true);
        IAudioOutput output = format.SampleRate == dspRate
            ? flexOutput
            : new UpsamplingAudioOutput(flexOutput, dspRate);
        IPttControl ptt = station.CreatePtt();

        return new FlexRuntime(mock, station, input, output, ptt);
    }

    private static (string Host, int Port) SplitHostPort(string spec)
    {
        int colon = spec.IndexOf(':', StringComparison.Ordinal);
        return colon < 0
            ? (spec, Vita49.DiscoveryPort)
            : (spec[..colon], int.Parse(spec[(colon + 1)..]));
    }

    private static bool IsSliceLetter(string segment) =>
        segment.Length == 1 && char.ToUpperInvariant(segment[0]) is >= 'A' and <= 'H';
}
