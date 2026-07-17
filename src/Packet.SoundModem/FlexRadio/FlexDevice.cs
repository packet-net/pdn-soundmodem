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

/// <summary>Flex slice/DAX parameters the daemon passes to the bring-up. The
/// frequency/antenna/mode configure the <b>headless</b> slice the daemon creates (ignored in
/// attach mode — SmartSDR owns the slice there); <see cref="DaxChannel"/> applies to
/// <b>both</b> paths (the DAX channel the client claims). Defaults match
/// docs/flex-integration.md §8 (14.100000 MHz / ANT1 / DIGU / DAX 1).</summary>
public sealed record FlexTuning
{
    /// <summary>Slice frequency (MHz, six-decimal Flex form). Default "14.100000".
    /// Headless only.</summary>
    public string Frequency { get; init; } = "14.100000";

    /// <summary>RX/TX antenna. Default "ANT1". Headless only.</summary>
    public string Antenna { get; init; } = "ANT1";

    /// <summary>Slice demod mode. Default "DIGU" (a data mode). Headless only.</summary>
    public string Mode { get; init; } = "DIGU";

    /// <summary>The DAX channel the client claims (both headless and attach). Default "1". A
    /// headless client sharing a box with a running SmartSDR must pick a channel SmartSDR is not
    /// using (SmartSDR grabs DAX 1) — see docs/flex-integration.md §8.</summary>
    public string DaxChannel { get; init; } = "1";
}

/// <summary>
/// Parses <c>--device flex:&lt;radio&gt;[:slice][@station]</c> and opens the Flex triplet: a
/// shared <see cref="FlexClient"/> feeding a <see cref="FlexAudioInput"/>, a
/// <see cref="FlexAudioOutput"/> (wrapped in an <see cref="UpsamplingAudioOutput"/> for the
/// 12 kHz modes) and a <see cref="FlexPtt"/>. <c>radio</c> is <c>discover</c>, an IP
/// (<c>host[:port]</c>), a discovery spec (<c>serial=…</c>/<c>name=…</c>), or <c>mock</c>
/// (an in-process fake). <b>Selection policy:</b> with no <c>@station</c> the daemon owns the
/// radio and brings it up <b>headless</b> (register as a GUI client, create its own slice —
/// the "pdn at the radio, no SmartSDR" deployment, the default). A trailing <c>@station</c>
/// selects <b>attach</b> mode: coexist with a running SmartSDR by binding that station's
/// existing slice. See docs/flex-integration.md §4/§8.
/// </summary>
public static class FlexDevice
{
    private const string Prefix = "flex:";

    /// <summary>True when <paramref name="device"/> selects a FlexRadio.</summary>
    public static bool IsFlex(string device) =>
        device.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The parsed radio spec, slice letter and (attach-only) station.</summary>
    /// <param name="RadioSpec">The radio portion (<c>discover</c>/IP/<c>serial=…</c>/<c>mock</c>).</param>
    /// <param name="SliceLetter">The slice letter (default "A").</param>
    /// <param name="Station">The SmartSDR station to attach to (from a <c>@station</c> suffix),
    /// or null for the headless default.</param>
    public readonly record struct FlexSpec(string RadioSpec, string SliceLetter, string? Station)
    {
        /// <summary>True when no <c>@station</c> was given — the daemon owns the radio and
        /// brings it up headless.</summary>
        public bool Headless => Station is null;
    }

    /// <summary>Splits a <c>flex:</c> device string into its radio, slice and station parts. A
    /// trailing <c>@station</c> (anywhere after the radio) selects attach mode and names the
    /// SmartSDR station; a trailing single letter A–H is the slice; everything else is the
    /// radio.</summary>
    public static FlexSpec Parse(string device)
    {
        string rest = device[Prefix.Length..];

        string? station = null;
        int at = rest.IndexOf('@', StringComparison.Ordinal);
        if (at >= 0)
        {
            station = rest[(at + 1)..];
            rest = rest[..at];
        }

        string[] segments = rest.Split(':');
        if (segments.Length >= 2 && IsSliceLetter(segments[^1]))
        {
            return new FlexSpec(string.Join(':', segments[..^1]), segments[^1].ToUpperInvariant(), station);
        }

        return new FlexSpec(rest, "A", station);
    }

    /// <summary>Opens the Flex triplet for the given device string and DSP rate.</summary>
    /// <param name="device">The <c>flex:…</c> device string.</param>
    /// <param name="dspRate">The channel DSP rate (picks the DAX transport).</param>
    /// <param name="packetBuffer">DAX-RX reorder-ring depth.</param>
    /// <param name="tuning">Headless slice params (frequency/antenna/mode); null = defaults.
    /// Ignored in attach mode.</param>
    /// <param name="cancellation">Cancels the connect + bring-up.</param>
    public static async Task<FlexRuntime> OpenAsync(
        string device, int dspRate, int packetBuffer, FlexTuning? tuning, CancellationToken cancellation)
    {
        FlexSpec spec = Parse(device);
        DaxStreamFormat format = DaxStreamFormat.ForDspRate(dspRate);
        tuning ??= new FlexTuning();

        MockFlexRadio? mock = null;
        FlexClient client;
        if (spec.RadioSpec.Equals("mock", StringComparison.OrdinalIgnoreCase))
        {
            mock = new MockFlexRadio(
                format, MockRxMode.Loopback,
                spec.Headless ? MockSetupMode.Headless : MockSetupMode.Attach,
                station: spec.Station ?? "Flex", sliceLetter: spec.SliceLetter);
            mock.Start();
            client = await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort, cancellation)
                .ConfigureAwait(false);
            // The mock is a hardware-free fake; deliver its DAX audio in-process (lossless)
            // rather than self-looping over UDP, so flex:mock is deterministic on a loaded box.
            mock.RxDelivery = client.DeliverVitaPacket;
            client.VitaSendHook = mock.DeliverTxPacket;
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

        var options = new FlexStationOptions
        {
            SliceLetter = spec.SliceLetter,
            Station = spec.Station ?? "Flex",
            Frequency = tuning.Frequency,
            Antenna = tuning.Antenna,
            SliceMode = tuning.Mode,
            DaxChannel = tuning.DaxChannel,
        };
        FlexStation station = spec.Headless
            ? await FlexStation.SetUpHeadlessAsync(client, format, options, cancellation).ConfigureAwait(false)
            : await FlexStation.SetUpAsync(client, format, options, cancellation).ConfigureAwait(false);

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
