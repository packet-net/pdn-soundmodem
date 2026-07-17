using System.Globalization;

namespace Packet.SoundModem.FlexRadio;

/// <summary>Options for setting up a <see cref="FlexStation"/> (attach-only: we bind DAX +
/// PTT to a slice the operator has already configured in SmartSDR — no tune/mode/filter
/// driving).</summary>
public sealed record FlexStationOptions
{
    /// <summary>The station name to bind DAX audio to (nDAX default "Flex").</summary>
    public string Station { get; init; } = "Flex";

    /// <summary>The slice letter to attach to (default "A").</summary>
    public string SliceLetter { get; init; } = "A";

    /// <summary>The DAX channel number to claim (default "1").</summary>
    public string DaxChannel { get; init; } = "1";

    /// <summary>RX audio gain 0–100 (default 50).</summary>
    public int Gain { get; init; } = 50;

    /// <summary>Enable keepalive + ping so a wedged radio is detected (default true).</summary>
    public bool Keepalive { get; init; } = true;

    /// <summary>How long to wait for a client/slice object to appear (default 5 s).</summary>
    public TimeSpan SetupTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Drives the DAX enable sequence on a connected <see cref="FlexClient"/> and hands back
/// the RX/TX/PTT triplet, all sharing that one session. Binds to a station, finds the
/// slice by its letter, and runs the eight-step enable (docs/flex-integration.md §2.4).
/// </summary>
/// <remarks>
/// The bind → find-slice → enable-DAX sequence is a port of nDAX <c>bindClient</c>,
/// <c>findSlice</c> and <c>enableDax</c> (© Andrew Rodland KC2G, MIT).
/// </remarks>
public sealed class FlexStation : IAsyncDisposable
{
    private readonly FlexClient _client;
    private readonly DaxStreamFormat _format;

    private FlexStation(FlexClient client, DaxStreamFormat format)
    {
        _client = client;
        _format = format;
    }

    /// <summary>The shared session.</summary>
    public FlexClient Client => _client;

    /// <summary>The DAX transport in use.</summary>
    public DaxStreamFormat Format => _format;

    /// <summary>The numeric slice index that was attached (e.g. "0").</summary>
    public string SliceIndex { get; private set; } = "";

    /// <summary>The DAX-RX stream id.</summary>
    public uint RxStreamId { get; private set; }

    /// <summary>The DAX-TX stream id.</summary>
    public uint TxStreamId { get; private set; }

    /// <summary>Connects the session (init UDP), binds to the station, finds the slice, and
    /// runs the DAX enable sequence.</summary>
    public static async Task<FlexStation> SetUpAsync(
        FlexClient client, DaxStreamFormat format, FlexStationOptions options,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(options);

        var station = new FlexStation(client, format);
        await client.InitUdpAsync(cancellation).ConfigureAwait(false);

        string clientHandle = await station.BindClientAsync(options, cancellation).ConfigureAwait(false);
        await station.FindSliceAsync(options, clientHandle, cancellation).ConfigureAwait(false);
        await station.EnableDaxAsync(options, cancellation).ConfigureAwait(false);

        if (options.Keepalive)
        {
            await client.EnableKeepaliveAsync(TimeSpan.FromSeconds(1), cancellation).ConfigureAwait(false);
        }

        return station;
    }

    /// <summary>Creates the DAX-RX audio source.</summary>
    public FlexAudioInput CreateAudioInput(int packetBuffer = 3) =>
        new(_client, RxStreamId, _format, packetBuffer);

    /// <summary>Creates the DAX-TX audio sink.</summary>
    public FlexAudioOutput CreateAudioOutput(bool paceRealTime = true) =>
        new(_client, TxStreamId, _format, paceRealTime);

    /// <summary>Creates the slice PTT.</summary>
    public FlexPtt CreatePtt(bool confirmInterlock = false) =>
        new(_client, SliceIndex, confirmInterlock);

    private async Task<string> BindClientAsync(FlexStationOptions options, CancellationToken cancellation)
    {
        _client.SendCommandNoWait("sub client all");
        string clientObject = await WaitForObjectAsync(
            "client ",
            state => state.GetValueOrDefault("station") == options.Station
                && state.ContainsKey("client_id"),
            $"client for station '{options.Station}'",
            options.SetupTimeout,
            cancellation).ConfigureAwait(false);

        _client.TryGetObject(clientObject, out IReadOnlyDictionary<string, string> client);
        string clientId = client["client_id"];
        string clientHandle = clientObject["client ".Length..];

        await _client.SendCommandExpectOkAsync($"client bind client_id={clientId}", cancellation)
            .ConfigureAwait(false);
        return clientHandle;
    }

    private async Task FindSliceAsync(
        FlexStationOptions options, string clientHandle, CancellationToken cancellation)
    {
        _client.SendCommandNoWait("sub slice all");
        string sliceObject = await WaitForObjectAsync(
            "slice ",
            state => state.GetValueOrDefault("index_letter") == options.SliceLetter
                && (!state.TryGetValue("client_handle", out string? handle) || handle == clientHandle),
            $"slice '{options.SliceLetter}'",
            options.SetupTimeout,
            cancellation).ConfigureAwait(false);
        SliceIndex = sliceObject["slice ".Length..];
    }

    private async Task EnableDaxAsync(FlexStationOptions options, CancellationToken cancellation)
    {
        if (_format.IsReducedBandwidth)
        {
            await _client.SendCommandExpectOkAsync("client set send_reduced_bw_dax=true", cancellation)
                .ConfigureAwait(false);
        }

        await _client.SendCommandExpectOkAsync(
            $"slice set {SliceIndex} dax={options.DaxChannel}", cancellation).ConfigureAwait(false);
        await _client.SendCommandExpectOkAsync(
            $"dax audio set {options.DaxChannel} slice={SliceIndex} tx=1", cancellation).ConfigureAwait(false);

        FlexResult rx = await _client.SendCommandExpectOkAsync(
            $"stream create type=dax_rx dax_channel={options.DaxChannel}", cancellation).ConfigureAwait(false);
        RxStreamId = ParseStreamId(rx.Message);

        await _client.SendCommandExpectOkAsync(
            $"audio stream 0x{RxStreamId:X8} slice {SliceIndex} gain {options.Gain}", cancellation)
            .ConfigureAwait(false);

        FlexResult tx = await _client.SendCommandExpectOkAsync("stream create type=dax_tx", cancellation)
            .ConfigureAwait(false);
        TxStreamId = ParseStreamId(tx.Message);
    }

    private async Task<string> WaitForObjectAsync(
        string prefix, Func<IReadOnlyDictionary<string, string>, bool> predicate, string what,
        TimeSpan timeout, CancellationToken cancellation)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (true)
        {
            if (_client.TryFindObject(prefix, predicate, out string objectName))
            {
                return objectName;
            }

            if (Environment.TickCount64 > deadline)
            {
                throw new FlexProtocolException($"timed out waiting for {what}");
            }

            await Task.Delay(20, cancellation).ConfigureAwait(false);
        }
    }

    private static uint ParseStreamId(string message)
    {
        string hex = message.Trim();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            hex = hex[2..];
        }

        if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint id))
        {
            throw new FlexProtocolException($"expected a stream id, got '{message}'");
        }

        return id;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
