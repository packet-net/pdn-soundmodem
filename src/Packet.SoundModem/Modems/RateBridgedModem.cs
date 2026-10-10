using M0LTE.Dsp;

namespace Packet.SoundModem.Modems;

/// <summary>
/// Runs a modem at its own DSP rate on a channel that runs faster: received audio is decimated
/// down before the inner modem sees it, and each transmitted burst is upsampled back to the
/// channel rate. The inner modem is unaware, and its DSP chain is the one it was validated with.
/// </summary>
/// <remarks>
/// <para><b>Why (issue #648).</b> The shared channel runs at 48 kHz as soon as any one modem on
/// it needs 48 kHz (MS110D, FreeDV, the fsk/c4fsk families), and every 12 kHz-native mode used
/// to be built at the channel rate. That works, and costs four times what it should: on GB7RDG
/// adding one ms110d modem put two 11-branch AFSK banks and a BPSK bank at 48 kHz, the receive
/// loop fell to about 0.3x real time and the input dropped the rest of the audio. Here the inner
/// modem runs at 12 kHz and the price of the faster channel is one decimator per modem.</para>
/// <para><b>The same resampling as every 12 kHz station on a 48 kHz card.</b> The decimator and
/// upsampler are M0LTE.Dsp's, as used by <c>Station</c> for a card captured at 48 kHz and by
/// <c>UpsamplingAudioOutput</c> for its playback, so what the inner modem hears is what it hears
/// on an ALSA station. The ARDOP engine already shares a 48 kHz channel this way
/// (<c>ArdopChannelBridge</c>).</para>
/// <para><b>Only where the band fits.</b> The decimator's low-pass cuts off at 0.44 of the
/// lower rate (5280 Hz at 12 kHz) with a transition about 1.6 kHz wide, so a modem placed high
/// in a wide 48 kHz passband would lose its top edge. <see cref="TryWrap"/> measures the inner
/// modem's band with the same probe the band planner uses and refuses one that reaches past
/// <see cref="BandLimitFraction"/> of the lower rate; the caller then builds it at the channel
/// rate as before.</para>
/// <para>Receive holds one decimator for the life of the modem (the stream is continuous) and
/// works through a fixed scratch, so the steady state allocates nothing. Transmit uses a fresh
/// upsampler per burst, padded with zeros so the filter's tail flushes, as
/// <see cref="FrequencyShiftedModem"/> does with its shifter.</para>
/// </remarks>
public sealed class RateBridgedModem : IModem, IFrameSpanSource, IConstellationSource
{
    /// <summary>
    /// The highest a bridged modem's measured band may reach, as a fraction of the inner rate:
    /// 4320 Hz at 12 kHz. The probe reports 99 % occupied-bandwidth edges, which sit a couple of
    /// hundred Hz inside the real skirts, and the decimator is flat to about 4.4 kHz, so this
    /// leaves the skirts in the passband.
    /// </summary>
    public const double BandLimitFraction = 0.36;

    /// <summary>The decimator's and upsampler's FIR length, M0LTE.Dsp's default for both.</summary>
    private const int FilterTaps = 96;

    /// <summary>Inner-rate samples of decimator output scratch.</summary>
    private const int ScratchLength = 4096;

    private readonly IModem _inner;
    private readonly int _channelRate;
    private readonly int _factor;
    private readonly Decimator _decimator;
    private readonly float[] _decimated = new float[ScratchLength];

    private RateBridgedModem(IModem inner, int innerRate, int channelRate)
    {
        _inner = inner;
        _channelRate = channelRate;
        _factor = channelRate / innerRate;
        _decimator = new Decimator(channelRate, _factor, FilterTaps);
    }

    /// <summary>
    /// Wraps <paramref name="inner"/>, built at <paramref name="innerRate"/>, for a channel at
    /// <paramref name="channelRate"/>. False, with <paramref name="bridged"/> null, when the
    /// rates are not an integer ratio above one, when the inner modem's band cannot be measured
    /// or does not fit under <see cref="BandLimitFraction"/> of its rate, or when it carries a
    /// surface this wrapper does not forward (frame packing, hardware control); the caller
    /// builds the mode at the channel rate instead.
    /// </summary>
    public static bool TryWrap(IModem inner, int innerRate, int channelRate, out IModem? bridged)
    {
        ArgumentNullException.ThrowIfNull(inner);
        bridged = null;
        if (innerRate <= 0 || channelRate <= innerRate || channelRate % innerRate != 0)
        {
            return false;
        }

        // None of the 12 kHz modes packs frames or takes hardware control today. Forwarding them
        // would make every bridged modem answer "is IFramePackingModem" true, which is the test
        // the station uses to refuse maxBurstSeconds on a mode that cannot pack.
        if (inner is IFramePackingModem or IHardwareControllable)
        {
            return false;
        }

        if (!ModemBandProbe.TryMeasure(inner, innerRate, out _, out double highHz)
            || highHz > BandLimitFraction * innerRate)
        {
            return false;
        }

        bridged = new RateBridgedModem(inner, innerRate, channelRate);
        return true;
    }

    /// <summary>The wrapped modem, at its own rate.</summary>
    public IModem Inner => _inner;

    /// <summary>How many channel samples make one inner sample.</summary>
    public int Factor => _factor;

    /// <inheritdoc/>
    public string Mode => _inner.Mode;

    /// <inheritdoc/>
    public event Action<byte[], FrameQuality>? FrameDecoded
    {
        add => _inner.FrameDecoded += value;
        remove => _inner.FrameDecoded -= value;
    }

    /// <inheritdoc/>
    /// <remarks>Forwarded when the inner modem plots symbols; never raised otherwise.</remarks>
    public event Action<ConstellationPoint>? SymbolPlotted
    {
        add
        {
            if (_inner is IConstellationSource source)
            {
                source.SymbolPlotted += value;
            }
        }

        remove
        {
            if (_inner is IConstellationSource source)
            {
                source.SymbolPlotted -= value;
            }
        }
    }

    /// <inheritdoc/>
    public bool CarrierDetect => _inner.CarrierDetect;

    /// <inheritdoc/>
    public bool ChannelBusy => _inner.ChannelBusy;

    /// <inheritdoc/>
    /// <remarks>
    /// The inner modem's margin in channel samples, plus the decimator's group delay, which makes
    /// both of the inner marks that much later on the channel's grid. Zero where the inner modem
    /// reports no span.
    /// </remarks>
    public int FrameSpanMarginSamples =>
        _inner is IFrameSpanSource source
            ? (source.FrameSpanMarginSamples * _factor) + ((FilterTaps - 1) / 2)
            : 0;

    /// <inheritdoc/>
    /// <remarks>The inner modem's: the decimator has unity gain in the passband, so the slicer's
    /// levels are the same on either side of it.</remarks>
    public Packet.SoundModem.Audio.FrameLevelLimits FrameLevels =>
        _inner is IFrameSpanSource source ? source.FrameLevels : Packet.SoundModem.Audio.FrameLevelLimits.Default;

    /// <inheritdoc/>
    /// <remarks>
    /// The inner modem counts the samples it was handed at its own rate, and inner sample n is
    /// produced when channel sample <c>n * factor + factor - 1</c> arrives, so that is where its
    /// marks land on the channel's grid.
    /// </remarks>
    public bool TryTakeFrameSpan(out long fromSample, out long toSample)
    {
        fromSample = 0;
        toSample = 0;
        if (_inner is not IFrameSpanSource source || !source.TryTakeFrameSpan(out long from, out long to))
        {
            return false;
        }

        fromSample = (from * _factor) + _factor - 1;
        toSample = (to * _factor) + _factor - 1;
        return true;
    }

    /// <inheritdoc/>
    public void Process(ReadOnlySpan<float> samples)
    {
        // Chunked so the decimator's output always fits the fixed scratch: it writes at most
        // length / factor + 1 samples.
        int chunk = (ScratchLength - 1) * _factor;
        while (samples.Length > 0)
        {
            int count = Math.Min(samples.Length, chunk);
            int produced = _decimator.Process(samples[..count], _decimated);
            if (produced > 0)
            {
                _inner.Process(_decimated.AsSpan(0, produced));
            }

            samples = samples[count..];
        }
    }

    /// <inheritdoc/>
    public float[] Modulate(ReadOnlySpan<byte> ax25Frame, int txDelayMilliseconds) =>
        Upsample(_inner.Modulate(ax25Frame, txDelayMilliseconds));

    private float[] Upsample(float[] burst)
    {
        // Enough zeros after the burst for the upsampler's group delay to flush its tail, which
        // would otherwise be cut off. The leading transient falls in the burst's own TXDELAY.
        int pad = (((FilterTaps - 1) / 2) + _factor) / _factor;
        var upsampler = new Upsampler(_channelRate, _factor, FilterTaps);
        var output = new float[upsampler.OutputLength(burst.Length + pad)];
        upsampler.Process(burst, output.AsSpan(0, burst.Length * _factor));
        upsampler.Process(new float[pad], output.AsSpan(burst.Length * _factor));
        return output;
    }

    /// <inheritdoc/>
    public void ResetCarrierState() => _inner.ResetCarrierState();
}
