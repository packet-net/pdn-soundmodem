using M0LTE.Radio.Audio;
using System.Threading.Channels;
using M0LTE.Dsp;
using Packet.SoundModem.Dsp;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Channel;

/// <summary>KISS channel-access parameters, in KISS units where noted.</summary>
public sealed class CsmaParameters
{
    /// <summary>Preamble length in milliseconds. Default 300.</summary>
    public int TxDelayMilliseconds { get; set; } = 300;

    /// <summary>p-persistence parameter, 0–255 (p = (value+1)/256). Default 63 (p=0.25).</summary>
    public int Persistence { get; set; } = 63;

    /// <summary>Slot time in milliseconds. Default 100.</summary>
    public int SlotTimeMilliseconds { get; set; } = 100;

    /// <summary>Audio kept flowing after the last frame, in milliseconds. Software modems
    /// need a non-zero tail so they do not clip their own transmissions. Default 20.</summary>
    public int TxTailMilliseconds { get; set; } = 20;
}

/// <summary>Receives the same audio the channel's modems see (see
/// <see cref="SoundModemChannel.AddReceiveTap"/>).</summary>
public delegate void ReceiveTap(ReadOnlySpan<float> samples);

/// <summary>
/// One audio channel hosting up to 16 logical modems (the QtSoundModem multiplex model,
/// addressed by KISS sub-channel): fans received audio into every modem plus the spectrum
/// source, aggregates carrier sense, and runs the transmit side — classic AX.25 §6
/// p-persistent CSMA gated on the aggregated <see cref="ChannelBusy"/>, PTT keying, and
/// device-paced audio with a drain before unkey (sample-domain TX-complete).
/// </summary>
public sealed class SoundModemChannel
{
    private readonly Dictionary<int, IModem> _modems = [];
    private readonly List<ReceiveTap> _receiveTaps = [];
    private readonly Channel<(Func<int, float[]> Modulate, TaskCompletionSource Done, Action<Exception>? Rejected)> _txQueue =
        System.Threading.Channels.Channel.CreateUnbounded<(Func<int, float[]>, TaskCompletionSource, Action<Exception>?)>();
    private readonly TimeProvider _time;
    private readonly Random _random;
    private readonly SpectrumSource? _spectrum;
    private readonly Action<int, ReadOnlyMemory<byte>>? _constellationSink;
    private volatile bool _transmitting;

    /// <summary>Creates a channel.</summary>
    /// <param name="sampleRate">DSP sample rate all modems and TX audio run at.</param>
    /// <param name="time">Clock for CSMA waits (injectable per repo discipline).</param>
    /// <param name="spectrumSink">Optional waterfall line sink (see
    /// <see cref="SpectrumSource"/>).</param>
    /// <param name="constellationSink">Optional per-symbol constellation-frame sink
    /// (sub-channel, frame). Wired to any PSK modem added to the channel — see
    /// <see cref="ConstellationSource"/>; a no-op for the non-PSK modes.</param>
    /// <param name="randomSeed">Seed for the p-persistence roll (tests); null = random.</param>
    public SoundModemChannel(
        int sampleRate,
        TimeProvider? time = null,
        Action<ReadOnlyMemory<byte>>? spectrumSink = null,
        Action<int, ReadOnlyMemory<byte>>? constellationSink = null,
        int? randomSeed = null)
    {
        SampleRate = sampleRate;
        _time = time ?? TimeProvider.System;
        _random = randomSeed is int seed ? new Random(seed) : new Random();
        if (spectrumSink is not null)
        {
            _spectrum = new SpectrumSource(sampleRate, spectrumSink);
        }

        _constellationSink = constellationSink;
    }

    /// <summary>The channel's DSP sample rate.</summary>
    public int SampleRate { get; }

    /// <summary>Channel-access tunables (KISS parameter commands update these).</summary>
    public CsmaParameters Csma { get; } = new();

    /// <summary>Raised for every received frame, with the sub-channel that decoded it.
    /// Called from the receive-processing thread.</summary>
    public event Action<int, byte[]>? FrameReceived;

    /// <summary>Per-frame receive diagnostics (sub-channel, frame, quality), raised
    /// alongside <see cref="FrameReceived"/> for every decoded frame — FEC corrections,
    /// CRC state, winning decoder branch. See <see cref="Modems.FrameQuality"/>.</summary>
    public event Action<int, byte[], Modems.FrameQuality>? FrameReceivedWithQuality;

    /// <summary>Raised when a queued frame is dropped because its modem refused to
    /// modulate it (sub-channel, frame, reason) — e.g. a frame beyond the mode's size
    /// bound. The frame's <see cref="EnqueueTransmit(int, byte[])"/> task faults with
    /// the same exception; the transmitter keeps running.</summary>
    public event Action<int, byte[], Exception>? TransmitRejected;

    /// <summary>True while any modem sees packet or energy busy, or we are transmitting.</summary>
    public bool ChannelBusy => _transmitting || _modems.Values.Any(m => m.ChannelBusy);

    /// <summary>True while any modem's packet DCD is asserted.</summary>
    public bool CarrierDetect => _modems.Values.Any(m => m.CarrierDetect);

    /// <summary>The modems keyed by sub-channel.</summary>
    public IReadOnlyDictionary<int, IModem> Modems => _modems;

    /// <summary>Adds a modem on a KISS sub-channel (0–15).</summary>
    public void AddModem(int subChannel, Func<Action<byte[]>, IModem> factory)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(subChannel);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(subChannel, 15);
        IModem modem = factory(frame => FrameReceived?.Invoke(subChannel, frame));
        modem.FrameDecoded += (frame, quality) =>
            FrameReceivedWithQuality?.Invoke(subChannel, frame, quality);
        if (_constellationSink is { } sink && modem is IConstellationSource psk)
        {
            var constellation = new ConstellationSource(frame => sink(subChannel, frame));
            constellation.Attach(psk);
        }

        _modems.Add(subChannel, modem);
    }

    /// <summary>Adds a non-KISS receive listener — a service decoder (e.g. POCSAG
    /// paging) that shares the channel's audio without occupying a KISS sub-channel.
    /// Called with the same half-duplex-gated samples the modems get.</summary>
    public void AddReceiveTap(ReceiveTap tap)
    {
        ArgumentNullException.ThrowIfNull(tap);
        _receiveTaps.Add(tap);
    }

    /// <summary>Feeds received audio to every modem and the spectrum source. Skipped
    /// while transmitting (half duplex).</summary>
    public void ProcessReceive(ReadOnlySpan<float> samples)
    {
        _spectrum?.Process(samples);
        if (_transmitting)
        {
            return;
        }

        foreach (IModem modem in _modems.Values)
        {
            modem.Process(samples);
        }

        foreach (ReceiveTap tap in _receiveTaps)
        {
            tap(samples);
        }
    }

    /// <summary>Queues a frame for transmission on a sub-channel. The returned task
    /// completes when the frame's audio has fully left the device (ACKMODE's answer).</summary>
    public Task EnqueueTransmit(int subChannel, byte[] frame)
    {
        if (!_modems.TryGetValue(subChannel, out IModem? modem))
        {
            return Task.FromException(new ArgumentException($"no modem on sub-channel {subChannel}"));
        }

        return EnqueueTransmit(
            txDelay => modem.Modulate(frame, txDelay),
            rejection => TransmitRejected?.Invoke(subChannel, frame, rejection));
    }

    /// <summary>Queues an arbitrary transmission — the channel-access path (CSMA, PTT,
    /// pacing, TX-complete) for service transmitters that are not KISS-addressed modems
    /// (e.g. POCSAG paging). The delegate receives the TXDELAY budget in milliseconds
    /// (full on the keyup's first transmission, a token 30 ms after) and returns the
    /// audio at the channel rate.</summary>
    /// <param name="modulate">Renders the transmission; an <see cref="ArgumentException"/>
    /// thrown here drops the item and faults the returned task, as for frames.</param>
    /// <param name="rejected">Optional observer for such a rejection.</param>
    public Task EnqueueTransmit(Func<int, float[]> modulate, Action<Exception>? rejected = null)
    {
        ArgumentNullException.ThrowIfNull(modulate);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_txQueue.Writer.TryWrite((modulate, done, rejected)))
        {
            done.SetException(new InvalidOperationException("transmit queue closed"));
        }

        return done.Task;
    }

    /// <summary>
    /// Runs the transmit side until cancelled: waits for queued frames, acquires the
    /// channel (p-persistent CSMA), keys PTT, plays every queued frame back-to-back,
    /// drains, unkeys.
    /// </summary>
    public async Task RunTransmitterAsync(IAudioOutput output, IPttControl ptt, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(ptt);
        if (output.SampleRate != SampleRate)
        {
            throw new ArgumentException(
                $"output rate {output.SampleRate} != channel rate {SampleRate}", nameof(output));
        }

        var reader = _txQueue.Reader;
        while (await reader.WaitToReadAsync(cancellation).ConfigureAwait(false))
        {
            // Classic p-persistence (AX.25 §6.4): when the channel is clear, roll p; on
            // failure wait one slot and try again; while busy, keep waiting slots.
            while (true)
            {
                if (ChannelBusy)
                {
                    await Delay(Csma.SlotTimeMilliseconds, cancellation).ConfigureAwait(false);
                    continue;
                }

                if (_random.Next(256) <= Csma.Persistence)
                {
                    break;
                }

                await Delay(Csma.SlotTimeMilliseconds, cancellation).ConfigureAwait(false);
            }

            _transmitting = true;
            try
            {
                ptt.Key();
                bool first = true;
                while (reader.TryRead(out var item))
                {
                    // Subsequent frames in one keyup need only a token preamble.
                    int txDelay = first ? Csma.TxDelayMilliseconds : 30;
                    float[] samples;
                    try
                    {
                        samples = item.Modulate(txDelay);
                    }
                    catch (ArgumentException rejection)
                    {
                        // A frame the modem refuses (oversize for the mode, empty) is
                        // dropped — it must not kill the transmitter loop. The enqueuer's
                        // task faults so ACKMODE hosts see the loss.
                        item.Done.TrySetException(rejection);
                        item.Rejected?.Invoke(rejection);
                        continue;
                    }

                    first = false;
                    output.Write(samples);
                    output.Drain();
                    item.Done.TrySetResult();
                }

                if (Csma.TxTailMilliseconds > 0)
                {
                    output.Write(new float[SampleRate * Csma.TxTailMilliseconds / 1000]);
                }

                output.Drain();
            }
            finally
            {
                ptt.Unkey();
                _transmitting = false;
                foreach (IModem modem in _modems.Values)
                {
                    modem.ResetCarrierState();
                }
            }
        }
    }

    private Task Delay(int milliseconds, CancellationToken cancellation) =>
        Task.Delay(TimeSpan.FromMilliseconds(milliseconds), _time, cancellation);
}
