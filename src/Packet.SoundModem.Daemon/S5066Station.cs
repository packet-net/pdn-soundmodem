using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using M0LTE.Dsp;
using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Host;
using M0LTE.S5066.Host.Audio;
using M0LTE.S5066.Host.Modem;
using Packet.SoundModem.Channel;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// A STANAG 5066 node on this station's channel: M0LTE.S5066's <see cref="NodeHost"/>, serving the
/// Subnetwork Interface on TCP and sending over the MIL-STD-188-110D waveform through this
/// station's sound card, PTT and carrier sense. Started only when the file has an
/// <c>"s5066"</c> section.
/// </summary>
/// <remarks>
/// <para><b>What is ours and what is the package's.</b> The engine, the SIS server and the modem
/// adapter (<see cref="Ms110dHostModem"/>) are M0LTE.S5066's, used as published. What lives here is
/// the glue the ARDOP TNC also needs: the channel's audio at the modem's 9600 Hz
/// (<see cref="ChannelAudio"/>) and the channel's carrier sense as the node's external busy input
/// (<see cref="BusyInput"/>). Nothing amateur reaches the node: its address and every protocol
/// setting come from the section's <c>"node"</c> object, in the package's own names.</para>
/// <para><b>Channel access.</b> The node runs its own (Annex K CSMA), so its bursts go out like
/// ARDOP's: <c>ownsChannelTiming</c>, never waiting on the packet modems' p-persistence roll, and
/// keyed as one source so nothing joins one of its keyups.</para>
/// </remarks>
internal sealed class S5066Station : IAsyncDisposable
{
    private readonly NodeHost _host;
    private readonly BusyInput _modem;

    private S5066Station(NodeHost host, BusyInput modem)
    {
        _host = host;
        _modem = modem;
    }

    /// <summary>The SIS endpoint actually listening.</summary>
    public IPEndPoint SisEndpoint => _host.SisEndpoint ?? throw new InvalidOperationException("not started");

    /// <summary>The node's address, dotted.</summary>
    public string Address => M0LTE.S5066.Core.Dts.DpduAddress.ToDotted(_host.Config.LocalAddress);

    /// <summary>
    /// Reads the section's <c>"node"</c> object: merged over M0LTE.S5066's defaults and read by its
    /// own reader, then checked by <see cref="NodeHost.CheckConfig(NodeConfig, IModemPort)"/>
    /// against the modem the node will run on, so every setting the node would stop on later is
    /// refused when the file is read rather than when the node starts.
    /// </summary>
    /// <exception cref="InvalidDataException">The object is missing or a setting is wrong; the
    /// message names it as <c>s5066.node.&lt;key&gt;</c>.</exception>
    internal static HostConfig ReadNode(JsonElement? node)
    {
        if (node is not { ValueKind: JsonValueKind.Object } element)
        {
            throw new InvalidDataException(
                "\"s5066\" needs a \"node\" object with at least \"local_address\" and \"csma\": {\"mode\": ...}. "
                + "See docs/16-stanag-5066.md.");
        }

        if (JsonNode.Parse(element.GetRawText()) is not JsonObject obj)
        {
            throw new InvalidDataException("\"s5066\".\"node\" must be an object");
        }

        if (obj.ContainsKey("host"))
        {
            throw new InvalidDataException(
                "\"s5066\".\"node\" has \"host\": this station is the host, so the SIS port is "
                + "\"s5066\".\"port\" on the \"bind\" address and the modem is the station's own. Remove it.");
        }

        HostConfig parsed;
        try
        {
            parsed = HostConfig.Parse(obj.ToJsonString());
        }
        catch (ConfigException wrong)
        {
            throw new InvalidDataException($"\"s5066\".\"node\": {wrong.Message}");
        }
        catch (Exception wrong) when (wrong is JsonException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException($"\"s5066\".\"node\": {wrong.Message}");
        }

        try
        {
            // The modem only plans here: nothing listens, plays or keys.
            NodeHost.CheckConfig(parsed.Node, new Ms110dHostModem(NoAudio.Instance));
        }
        catch (ConfigException wrong)
        {
            throw new InvalidDataException($"\"s5066\".\"node\": {wrong.Message}");
        }

        return parsed;
    }

    /// <summary>The node's load-time warnings for a section read by <see cref="ReadNode"/>, such as a
    /// contention slot no longer than the modem's preamble. Warnings only; nothing is refused.</summary>
    internal static IReadOnlyList<string> NodeWarnings(HostConfig parsed) =>
        NodeHost.ConfigWarnings(parsed.Node, new Ms110dHostModem(NoAudio.Instance));

    /// <summary>
    /// Builds the node on <paramref name="channel"/> and starts it: the SIS server, the modem's
    /// receive tap and the busy input. Call before the channel's audio starts, as the ARDOP TNC is.
    /// </summary>
    /// <param name="channel">The station's channel; its rate must be a multiple of 9600 (48 kHz).</param>
    /// <param name="config">The section, as loaded (its node settings already read).</param>
    /// <param name="listen">The <c>"bind"</c> address.</param>
    /// <param name="say">Where one-line journal messages go.</param>
    /// <param name="sent">Told of each burst once its audio has gone to the device, for the
    /// journal and the frame log. Called on a thread-pool thread.</param>
    /// <param name="heard">Told of each burst the modem locked to, once it has ended, with the
    /// blocks it decoded. Called on the receive thread, so it must return promptly.</param>
    internal static S5066Station Start(
        SoundModemChannel channel, S5066Config config, IPAddress listen, Action<string> say,
        Action<Burst>? sent = null, Action<Burst>? heard = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(config);
        HostConfig node = config.Parsed ?? ReadNode(config.Node);
        void Log(string line) => say($"s5066: {line}");

        BusyInput? modem = null;
        var audio = new ChannelAudio(channel, Log, () => modem?.NoteSent());
        modem = new BusyInput(new Ms110dHostModem(audio, log: Log), channel, sent, heard);
        var host = new NodeHost(node.Node, node.PrngSeed, modem, new NodeHostOptions
        {
            SisEndpoint = new IPEndPoint(listen, config.Port),
            Log = Log,
        });
        host.Start();

        // An exception out of the engine is a bug in it, and it stops the node. The rest of the
        // station carries on, so say so once rather than leave a SIS port that answers nothing.
        _ = host.Completion.ContinueWith(
            t => Log($"the node stopped: {t.Exception!.GetBaseException().Message}. Restart the service to start it again."),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

        return new S5066Station(host, modem);
    }

    /// <summary>Unbinds every client, closes the SIS port and stops the node.</summary>
    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync().ConfigureAwait(false);
        _modem.Dispose();
    }

    /// <summary>One 110D burst the node sent or heard: its bytes (D_PDUs, as the core framed them;
    /// for a heard burst, every decoded block, padding included), waveform and interleaver.</summary>
    /// <param name="Bytes">The bytes.</param>
    /// <param name="Wn">The waveform number.</param>
    /// <param name="Interleaver">The interleaver.</param>
    /// <param name="SnrDb">The modem's SNR for a heard burst, if it measured one.</param>
    internal sealed record Burst(byte[] Bytes, int Wn, Interleaver Interleaver, double? SnrDb)
    {
        /// <summary>How a heard burst ended; null for one we sent.</summary>
        public BurstEndReason? Ended { get; init; }

        /// <summary>The waveform as a journal and the frame log show it, e.g. <c>wn6 short</c>.</summary>
        public string Waveform => $"wn{Wn} {InterleaverNames.ToName(Interleaver).Replace('_', ' ')}";
    }

    /// <summary>An audio path that is never started, for checking the node's settings against the
    /// modem at load.</summary>
    private sealed class NoAudio : IAudioChannel
    {
        internal static readonly NoAudio Instance = new();

        /// <inheritdoc/>
        public void Start(Action<ReadOnlyMemory<float>> received) => throw new InvalidOperationException("only for checking settings");

        /// <inheritdoc/>
        public void Play(float[] samples, Action played) => throw new InvalidOperationException("only for checking settings");
    }

    /// <summary>
    /// The channel's audio at the modem's 9600 Hz (<see cref="IAudioChannel"/>): received audio from
    /// a channel receive tap, decimated; bursts upsampled and queued on the channel's transmitter.
    /// </summary>
    /// <remarks>
    /// The 110D waveform sits at the same audio frequencies either side of the rate change, so this
    /// is a resampler and nothing else, the bridge <c>Ms110dModem</c> uses: 48000 is 5 x 9600.
    /// </remarks>
    internal sealed class ChannelAudio : IAudioChannel
    {
        private readonly SoundModemChannel _channel;
        private readonly Action<string> _log;
        private readonly int _factor;
        private readonly Decimator _decimator;
        private float[] _decimated = [];
        private readonly Action _wentOut;
        private Action<ReadOnlyMemory<float>>? _received;

        internal ChannelAudio(SoundModemChannel channel, Action<string> log, Action wentOut)
        {
            if (channel.SampleRate % IAudioChannel.SampleRate != 0)
            {
                throw new ArgumentException(
                    $"the channel runs at {channel.SampleRate} Hz, which is not a multiple of the modem's {IAudioChannel.SampleRate} Hz",
                    nameof(channel));
            }

            _channel = channel;
            _log = log;
            _wentOut = wentOut;
            _factor = channel.SampleRate / IAudioChannel.SampleRate;
            _decimator = new Decimator(channel.SampleRate, _factor);
        }

        /// <inheritdoc/>
        public void Start(Action<ReadOnlyMemory<float>> received)
        {
            _received = received;
            _channel.AddReceiveTap(Receive);
        }

        /// <inheritdoc/>
        public void Play(float[] samples, Action played)
        {
            // Upsampled here, on the modem's worker thread, rather than in the delegate below,
            // which the transmitter calls once the radio is already keyed: a long burst's
            // filtering would otherwise be dead air after the key, on top of TXDELAY.
            float[] burst = Upsample(samples);
            Task queued;
            try
            {
                queued = _channel.EnqueueTransmit(
                    txDelay => WithLeadIn(burst, txDelay),
                    rejected: null,
                    // The node runs its own channel access (Annex K), so its bursts wait on
                    // neither the packet modems' p-persistence roll nor their inhibit.
                    ownsChannelTiming: true,
                    // One source, so its keyups are its own: nothing is appended behind a burst.
                    source: this);
            }
            catch (Exception refused) when (refused is InvalidOperationException or ArgumentException)
            {
                queued = Task.FromException(refused);
            }

            // Once the audio has been handed to the device. The node is told either way: a burst
            // that never went out still has to end, or the node would wait for it for ever.
            _ = queued.ContinueWith(
                t =>
                {
                    if (t.Exception?.GetBaseException() is { } why)
                    {
                        _log($"burst not sent: {why.Message}");
                    }
                    else if (!t.IsCanceled)
                    {
                        _wentOut();
                    }

                    played();
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }

        private void Receive(ReadOnlySpan<float> samples)
        {
            int most = _decimator.MaxOutput(samples.Length);
            if (_decimated.Length < most)
            {
                _decimated = new float[most];
            }

            int produced = _decimator.Process(samples, _decimated);
            if (produced > 0)
            {
                // Scratch reused per block: the modem consumes it before returning.
                _received?.Invoke(_decimated.AsMemory(0, produced));
            }
        }

        /// <summary>
        /// TXDELAY of silence ahead of the burst, as every other transmitter on the channel has
        /// (Ms110dModem's Render): the 110D preamble starts at its first sample, and a radio still
        /// switching to transmit would otherwise clip it.
        /// </summary>
        private float[] WithLeadIn(float[] burst, int txDelayMilliseconds)
        {
            int lead = (int)((long)_channel.SampleRate * Math.Max(0, txDelayMilliseconds) / 1000);
            if (lead == 0)
            {
                return burst;
            }

            var output = new float[lead + burst.Length];
            burst.CopyTo(output, lead);
            return output;
        }

        private float[] Upsample(float[] samples)
        {
            // A fresh upsampler per burst, as every per-burst transmit path here has: no filter
            // state from the last burst leaks into this one.
            var upsampler = new Upsampler(_channel.SampleRate, _factor);
            var output = new float[upsampler.OutputLength(samples.Length)];
            upsampler.Process(samples, output);
            return output;
        }
    }

    /// <summary>
    /// The modem with the channel's carrier sense wired to the node's external busy input
    /// (<see cref="IModemEvents.ChannelBusy"/>). Everything else passes straight through.
    /// </summary>
    /// <remarks>
    /// <para>The answer is the channel's station-wide <see cref="SoundModemChannel.ChannelBusy"/>:
    /// the radio's carrier sense where the station has one, otherwise what the packet modems on the
    /// channel hear. Our own transmission is left out, since it is not somebody else on the
    /// channel and the node knows when it is sending; so is a short tail after each keyup, while
    /// the receivers settle.</para>
    /// <para>Polled every <see cref="PollInterval"/> and raised only on a change.</para>
    /// </remarks>
    internal sealed class BusyInput : IHostModem, IDisposable
    {
        internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

        /// <summary>How long after our own keyup the channel's answer is not taken as somebody
        /// else's: the receivers' detectors release on their own time.</summary>
        internal static readonly TimeSpan AfterKeyup = TimeSpan.FromMilliseconds(250);

        private readonly IHostModem _inner;
        private readonly SoundModemChannel _channel;
        private readonly Action<Burst>? _sent;
        private readonly Action<Burst>? _heard;
        private readonly TimeProvider _time;
        private Burst? _sending;
        private readonly object _gate = new();
        private ITimer? _timer;
        private IModemEvents? _events;
        private bool _keyed;
        private long _unkeyedAt;
        private bool? _reported;

        internal BusyInput(
            IHostModem inner, SoundModemChannel channel, Action<Burst>? sent = null, Action<Burst>? heard = null,
            TimeProvider? time = null)
        {
            _inner = inner;
            _channel = channel;
            _sent = sent;
            _heard = heard;
            _time = time ?? TimeProvider.System;
        }

        /// <inheritdoc/>
        public BurstPlan PlanBurst(int wn, Interleaver interleaver, int byteCount) => _inner.PlanBurst(wn, interleaver, byteCount);

        /// <inheritdoc/>
        public void Start(IModemEvents events)
        {
            _events = events;
            _channel.TransmittingChanged += OnTransmitting;
            _inner.Start(_heard is null ? events : new Listening(events, _heard));
            _timer = _time.CreateTimer(_ => Poll(), null, PollInterval, PollInterval);
        }

        /// <inheritdoc/>
        public void Transmit(ReadOnlyMemory<byte> bytes, int wn, Interleaver interleaver, int k, bool eom)
        {
            _inner.Transmit(bytes, wn, interleaver, k, eom);
            if (_sent is not null)
            {
                Volatile.Write(ref _sending, new Burst(bytes.ToArray(), wn, interleaver, SnrDb: null));
            }
        }

        /// <summary>The burst <see cref="Transmit"/> started has gone to the device.</summary>
        internal void NoteSent()
        {
            if (Interlocked.Exchange(ref _sending, null) is { } burst)
            {
                _sent?.Invoke(burst);
            }
        }

        /// <inheritdoc/>
        public void TerminateReceive() => _inner.TerminateReceive();

        /// <inheritdoc/>
        public void SetMaxInputBlocks(int n) => _inner.SetMaxInputBlocks(n);

        /// <inheritdoc/>
        public void Dispose()
        {
            _timer?.Dispose();
            _channel.TransmittingChanged -= OnTransmitting;
        }

        /// <summary>One look at the channel; internal so a test can step it.</summary>
        internal void Poll()
        {
            bool busy;
            lock (_gate)
            {
                busy = !_keyed
                    && _time.GetElapsedTime(_unkeyedAt) >= AfterKeyup
                    && _channel.ChannelBusy;
                if (_reported == busy || (_reported is null && !busy))
                {
                    _reported = busy;
                    return;
                }

                _reported = busy;
            }

            _events?.ChannelBusy(busy);
        }

        /// <summary>Passes every event on, and gathers each burst's decoded blocks for
        /// <see cref="_heard"/>.</summary>
        private sealed class Listening(IModemEvents inner, Action<Burst> heard) : IModemEvents
        {
            private readonly List<byte> _bytes = [];

            public void BurstStarted()
            {
                _bytes.Clear();
                inner.BurstStarted();
            }

            public void BytesReceived(ReadOnlyMemory<byte> bytes)
            {
                _bytes.AddRange(bytes.Span);
                inner.BytesReceived(bytes);
            }

            public void BurstEnded(BurstEndReason reason, int wn, Interleaver interleaver, int k, int? snrTenthDb, int blocks)
            {
                inner.BurstEnded(reason, wn, interleaver, k, snrTenthDb, blocks);
                heard(new Burst([.. _bytes], wn, interleaver, snrTenthDb / 10.0) { Ended = reason });
                _bytes.Clear();
            }

            public void TransmitDone() => inner.TransmitDone();

            public void ChannelBusy(bool busy) => inner.ChannelBusy(busy);
        }

        private void OnTransmitting(bool keyed)
        {
            lock (_gate)
            {
                _keyed = keyed;
                if (!keyed)
                {
                    _unkeyedAt = _time.GetTimestamp();
                }
            }
        }
    }
}
