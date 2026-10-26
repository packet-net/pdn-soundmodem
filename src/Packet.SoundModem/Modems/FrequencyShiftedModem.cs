using M0LTE.Dsp;
using Packet.SoundModem.Audio;

namespace Packet.SoundModem.Modems;

/// <summary>
/// Moves a fixed-centre modem to a different audio centre by analytic frequency translation at
/// the channel rate: transmitted bursts are shifted up (or down) after the inner modem renders
/// them, received audio is shifted back before the inner modem sees it. The inner modem is
/// unaware, which is the point - its validated DSP chain does not change by one coefficient.
/// </summary>
/// <remarks>
/// <para>This is how the spec-fixed families (<c>ms110d-*</c> on 1800 Hz, <c>freedv-*</c> on
/// their OFDM centres) become placeable anywhere in a wide passband. On air nothing changes:
/// interoperability is set by the RF centre, and the RF centre is exactly what the operator
/// configured - the audio offset is a private matter between this station's modem and its own
/// dial. The spec centre only matters against a peer whose modem cannot be told the dial (a
/// MIL radio on a voice plug), and it remains the default; this wrapper only exists when an
/// override asked for something else. Same pattern as the daemon's <c>ArdopChannelBridge</c>.</para>
/// <para>The Hilbert FIR defaults to 639 taps, far above <see cref="FrequencyShifter"/>'s own
/// 129: a Type III Hilbert has nulls at DC and Nyquist, and at a 48 kHz channel rate MS110D's
/// band starts at 170 Hz - 0.7 % of Nyquist, well inside a short FIR's DC skirt, which would
/// leak an image of the band's bottom back into the passband. 639 taps holds the response flat
/// down to below that edge; <c>FrequencyShiftedModemTests</c> measures the image rejection
/// rather than trusting this arithmetic.</para>
/// <para><b>Receive must bandpass BEFORE it downshifts, and this is load-bearing, not
/// hygiene.</b> Downshifting a real signal by delta folds everything below delta onto the
/// wanted band: the analytic mix only suppresses the input's negative-frequency conjugate,
/// and genuine channel noise at f &lt; delta lands at delta - f with nothing wrong anywhere.
/// Unfiltered, a 3200 Hz shift doubles the noise across the whole demod band - a measured
/// 3 dB knee movement (0/30 decodes at 5 dB against 27/30 native), which raising the Hilbert
/// from 639 to 1279 taps did not touch, because it is not a Hilbert defect. The bandpass at
/// the on-air band removes the below-delta noise first, and the fold with it. ARDOP's
/// sibling shift never met this because its shifts are a few hundred Hz, so the folded
/// region sits below its band.</para>
/// <para>Transmit pads the inner burst with the FIR's group delay of zeros before shifting, so
/// the delayed tail (EOM/EOT in the MS110D case) flushes out instead of being truncated.
/// Receive holds one persistent bandpass + shifter (the stream is continuous; group delay is
/// just latency) and processes through fixed scratch chunks, so the steady state allocates
/// nothing.</para>
/// </remarks>
public sealed class FrequencyShiftedModem : IModem, IHardwareControllable, IFrameSpanSource, IFramePackingModem, CarrierSense.IBusyReadingSource
{
    /// <summary>See the type remarks for why this is so much longer than the shifter's default.</summary>
    private const int HilbertTaps = 639;

    /// <summary>Receive bandpass length; its ~250 Hz transition is what the passband margin
    /// below covers.</summary>
    private const int BandpassTaps = 639;

    /// <summary>How far the receive bandpass opens beyond the measured band edges, so the
    /// probe's 99 % OBW edges plus the real -30 dB skirts sit in the flat passband and only
    /// the fold-producing region below (and noise above) is in the stopband.</summary>
    private const double BandpassMarginHz = 300;

    /// <summary>How close to DC or Nyquist a shifted band edge may sit. Two things hide inside
    /// it: the probe reports 99 % occupied-bandwidth edges, which sit a couple of hundred Hz
    /// inside the real skirts (MS110D's OBW low is ~400 Hz where its -30 dB extent reaches
    /// 170 Hz, both pinned by Ms110dObwTests in M0LTE.Ms110d), and the Hilbert response rolls off close to DC
    /// and Nyquist even at 639 taps, taking image rejection with it.</summary>
    private const double EdgeGuardHz = 350;

    /// <summary>For a clipping wrap: how close to DC or Nyquist transmitted energy may reach.</summary>
    private const double ClipFloorHz = 50;

    /// <summary>For a clipping wrap: where the transmit band-limit's -6 dB point is put, in shifted
    /// terms. The FIR's transition is about 250 Hz wide, so putting the half-way point this far
    /// above <see cref="ClipFloorHz"/> leaves the stopband (40 dB or more down) below the floor.</summary>
    private const double ClipCutHz = 175;

    /// <summary>For a clipping wrap: the least of the mode's band that must stay inside the audio
    /// band, as a fraction. Below it the mode is mostly cut off and refused rather than built.</summary>
    private const double ClipKeepFraction = 0.5;

    private readonly IModem _inner;
    private readonly int _sampleRate;
    private readonly double _shiftHz;
    private readonly FirFilter _receiveBandpass;
    private readonly FrequencyShifter _receive;
    private readonly float[] _banded = new float[4096];
    private readonly float[] _scratch = new float[4096];

    private readonly double _transmitLowCutHz;
    private readonly double _transmitHighCutHz;

    private FrequencyShiftedModem(
        IModem inner, int sampleRate, double shiftHz, double onAirLowHz, double onAirHighHz,
        bool clip = false, double nativeLowHz = 0, double nativeHighHz = 0)
    {
        _inner = inner;
        _sampleRate = sampleRate;
        _shiftHz = shiftHz;
        _receiveBandpass = new FirFilter(FilterDesign.BandPass(
            Math.Max(50, onAirLowHz - BandpassMarginHz),
            Math.Min((sampleRate / 2.0) - 50, onAirHighHz + BandpassMarginHz),
            sampleRate, BandpassTaps));
        _receive = new FrequencyShifter(sampleRate, -shiftHz, HilbertTaps);

        // A clipping wrap lets part of the band fall below DC or above Nyquist. A real signal has
        // no energy there to send, but the analytic shift would fold it back into the band as a
        // mirror image, so it is removed from the inner modem's burst BEFORE the shift: the
        // transmit band-limit works in the inner modem's own (unshifted) frequencies.
        if (clip)
        {
            double nyquist = sampleRate / 2.0;
            _transmitLowCutHz = ClipCutHz - shiftHz;
            _transmitHighCutHz = nyquist - ClipCutHz - shiftHz;
            _clipLow = _transmitLowCutHz > nativeLowHz - 1;
            _clipHigh = _transmitHighCutHz < nativeHighHz + 1;
        }
    }

    private readonly bool _clipLow;
    private readonly bool _clipHigh;

    /// <summary>
    /// The audio centres a mode whose measured band is <paramref name="lowHz"/> to
    /// <paramref name="highHz"/> at its native <paramref name="nativeCentreHz"/> may be moved to
    /// and still be accepted by <see cref="Wrap"/>. The one place that rule is applied, shared
    /// with the band planner so the two cannot disagree. Without <paramref name="clip"/> the
    /// shifted band must stay clear of the guard at DC and at Nyquist; with it, any centre that
    /// keeps half the band inside the audio band.
    /// </summary>
    public static (double MinCentreHz, double MaxCentreHz) AllowedCentreRange(
        double lowHz, double highHz, double nativeCentreHz, int sampleRate, bool clip = false)
    {
        if (!clip)
        {
            return (nativeCentreHz + EdgeGuardHz - lowHz,
                    nativeCentreHz + (sampleRate / 2.0) - EdgeGuardHz - highHz);
        }

        double keep = ClipKeepFraction * (highHz - lowHz);
        return (nativeCentreHz + ClipFloorHz + keep - highHz,
                nativeCentreHz + (sampleRate / 2.0) - ClipFloorHz - keep - lowHz);
    }

    /// <summary>
    /// Wraps <paramref name="inner"/> so it works at <paramref name="centreHz"/> instead of its
    /// native <paramref name="nativeCentreHz"/>. The shifted band is measured off the inner
    /// modem's own modulator and checked clear of DC and Nyquist before anything is built -
    /// a centre that would fold the band over is refused with the numbers, not discovered on air.
    /// </summary>
    /// <param name="inner">The modem at its native centre.</param>
    /// <param name="sampleRate">The channel rate.</param>
    /// <param name="centreHz">The audio centre wanted.</param>
    /// <param name="nativeCentreHz">The centre the inner modem is built on.</param>
    /// <param name="clip">
    /// True for a mode that tolerates its edges being cut (MS110D): any centre that keeps at least
    /// half its band inside the audio band is accepted, the part below 50 Hz or above Nyquist is
    /// simply not transmitted, and nothing of it folds back as a mirror image. False keeps the
    /// guard against DC and Nyquist.
    /// </param>
    /// <exception cref="ArgumentException">The shifted band would reach within
    /// <see cref="EdgeGuardHz"/> of DC or Nyquist (or, with <paramref name="clip"/>, leave less than
    /// half the band inside the audio band).</exception>
    public static IModem Wrap(
        IModem inner, int sampleRate, double centreHz, double nativeCentreHz, bool clip = false)
    {
        double shift = centreHz - nativeCentreHz;
        if (shift == 0)
        {
            return inner;
        }

        // Measured, not tabulated: the same probe the band planner and the waterfall trust.
        // Falls back to the native centre ± half the channel's nominal width only if the modem
        // will not render a probe frame, which none of the wrappable modes refuses.
        double low = nativeCentreHz - 1500, high = nativeCentreHz + 1500;
        if (ModemBandProbe.TryMeasure(inner, sampleRate, out double measuredLow, out double measuredHigh))
        {
            (low, high) = (measuredLow, measuredHigh);
        }

        double nyquist = sampleRate / 2.0;
        (double minCentre, double maxCentre) =
            AllowedCentreRange(low, high, nativeCentreHz, sampleRate, clip);
        if (centreHz < minCentre - 1e-6 || centreHz > maxCentre + 1e-6)
        {
            throw new ArgumentException(
                $"centre {centreHz:F0} Hz would put {inner.Mode}'s band at "
                + $"{low + shift:F0}-{high + shift:F0} Hz, "
                + (clip
                    ? "which leaves less than half of it inside the audio band "
                    : $"which is not clear of the {EdgeGuardHz:F0} Hz guard against DC and the {nyquist:F0} Hz Nyquist edge ")
                + $"(the mode occupies {low:F0}-{high:F0} Hz at its native "
                + $"{nativeCentreHz:F0} Hz centre; it accepts centres from {minCentre:F0} to "
                + $"{maxCentre:F0} Hz)",
                nameof(centreHz));
        }

        return new FrequencyShiftedModem(inner, sampleRate, shift, low + shift, high + shift, clip, low, high);
    }

    /// <summary>The wrapped modem, for in-process hosts that want its typed surface
    /// (e.g. <c>Ms110dModem.SetTxWaveform</c>) rather than the byte-payload
    /// <see cref="IHardwareControllable"/> path, which is forwarded either way.</summary>
    public IModem Inner => _inner;

    /// <inheritdoc/>
    public string Mode => _inner.Mode;

    /// <inheritdoc/>
    public event Action<byte[], FrameQuality>? FrameDecoded
    {
        add => _inner.FrameDecoded += value;
        remove => _inner.FrameDecoded -= value;
    }

    /// <inheritdoc/>
    public bool CarrierDetect => _inner.CarrierDetect;

    /// <inheritdoc/>
    public bool ChannelBusy => _inner.ChannelBusy;

    /// <inheritdoc/>
    public CarrierSense.BusyReading? BusyReading => (_inner as CarrierSense.IBusyReadingSource)?.BusyReading;

    /// <inheritdoc />
    /// <remarks>
    /// The inner modem's, plus this wrapper's own Hilbert group delay, which is 319 samples on
    /// top of whatever the inner front end already smears. Zero where the inner modem reports no
    /// span at all, which today is every mode this wrapper is used for.
    /// </remarks>
    public int FrameSpanMarginSamples =>
        _inner is IFrameSpanSource source ? source.FrameSpanMarginSamples + HilbertGroupDelay : 0;

    /// <inheritdoc />
    /// <remarks>
    /// The inner modem's, unchanged: this wrapper band-passes and shifts sample for sample and
    /// changes no amplitude, so the slicer that decides the bits is the inner one's and so are
    /// the levels it wants. <see cref="FrameLevelLimits.Default"/> where the inner modem reports
    /// no span at all, which is every mode this wrapper is used for today and a case that carries
    /// no level to judge anyway.
    /// </remarks>
    public FrameLevelLimits FrameLevels =>
        _inner is IFrameSpanSource levels ? levels.FrameLevels : FrameLevelLimits.Default;

    /// <summary>Half the Hilbert pair's length, which is what it delays by.</summary>
    private const int HilbertGroupDelay = (HilbertTaps - 1) / 2;

    /// <inheritdoc />
    /// <remarks>
    /// The inner modem's own, unchanged: this wrapper band-passes and shifts sample for sample,
    /// so the two count the same audio and a mark taken inside means the same thing outside. Its
    /// filters add their group delay to the span's lateness, which is what the reading's margin
    /// covers (<c>FrameLevelMonitor.MarginMilliseconds</c>).
    /// </remarks>
    public bool TryTakeFrameSpan(out long fromSample, out long toSample)
    {
        fromSample = 0;
        toSample = 0;
        return _inner is IFrameSpanSource source && source.TryTakeFrameSpan(out fromSample, out toSample);
    }

    /// <inheritdoc/>
    public void Process(ReadOnlySpan<float> samples)
    {
        // Bandpass to the on-air band FIRST (see the type remarks: this is what stops the
        // below-delta noise folding onto the band), then shift down, then hand to the inner
        // modem. Chunked through fixed scratches; both filters are stateful across calls,
        // which is what makes chunking transparent, and the steady state allocates nothing.
        while (samples.Length > 0)
        {
            int count = Math.Min(samples.Length, _scratch.Length);
            for (int i = 0; i < count; i++)
            {
                _banded[i] = _receiveBandpass.Next(samples[i]);
            }

            _receive.Process(_banded.AsSpan(0, count), _scratch.AsSpan(0, count));
            _inner.Process(_scratch.AsSpan(0, count));
            samples = samples[count..];
        }
    }

    /// <inheritdoc/>
    public float[] Modulate(ReadOnlySpan<byte> ax25Frame, int txDelayMilliseconds) =>
        Shift(_inner.Modulate(ax25Frame, txDelayMilliseconds));

    /// <inheritdoc/>
    /// <remarks>
    /// The wrapped modem's, forwarded: moving a modem changes where it sits, not how it frames.
    /// Always null for a wrapped modem that cannot pack, and setting one there is refused.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Set non-null on a wrapped modem that cannot
    /// pack frames.</exception>
    public FramePacking? Packing
    {
        get => (_inner as IFramePackingModem)?.Packing;
        set
        {
            if (_inner is IFramePackingModem packer)
            {
                packer.Packing = value;
            }
            else if (value is not null)
            {
                throw new InvalidOperationException($"mode '{Mode}' sends one frame per burst");
            }
        }
    }

    /// <inheritdoc/>
    public int FramesPerBurst(IReadOnlyList<byte[]> frames) =>
        _inner is IFramePackingModem packer ? packer.FramesPerBurst(frames) : Math.Min(1, frames.Count);

    /// <inheritdoc/>
    public float[] ModulateFrames(IReadOnlyList<byte[]> frames, int txDelayMilliseconds)
    {
        if (_inner is not IFramePackingModem packer)
        {
            throw new InvalidOperationException($"mode '{Mode}' sends one frame per burst");
        }

        return Shift(packer.ModulateFrames(frames, txDelayMilliseconds));
    }

    private float[] Shift(float[] burst)
    {
        // A fresh shifter per burst (no stale tail from the last transmission), fed the burst
        // plus one group delay of zeros so the delayed tail flushes: the FIR delays everything
        // by (taps-1)/2 samples, and without the pad that many samples of burst - the end of
        // the EOT in the MS110D case - would never come out. The leading transient lands in
        // the burst's own TXDELAY silence.
        const int groupDelay = (HilbertTaps - 1) / 2;
        if (_clipLow || _clipHigh)
        {
            burst = LimitForShift(burst);
        }

        var shifter = new FrequencyShifter(_sampleRate, _shiftHz, HilbertTaps);
        var shifted = new float[burst.Length + groupDelay];
        shifter.Process(burst, shifted.AsSpan(0, burst.Length));
        shifter.Process(new float[groupDelay], shifted.AsSpan(burst.Length));
        return shifted;
    }

    /// <summary>
    /// Removes the part of the inner burst that the shift would push below the floor or above
    /// Nyquist, so it cannot fold back as a mirror image. Zero-phase (the FIR's delay is taken out
    /// by padding and skipping) so the burst keeps its timing.
    /// </summary>
    private float[] LimitForShift(float[] burst)
    {
        double nyquist = _sampleRate / 2.0;
        double low = _clipLow ? Math.Max(20, _transmitLowCutHz) : 20;
        double high = _clipHigh ? Math.Min(nyquist - 20, _transmitHighCutHz) : nyquist - 20;
        var filter = new FirFilter(FilterDesign.BandPass(low, high, _sampleRate, HilbertTaps));
        int delay = (HilbertTaps - 1) / 2;
        var limited = new float[burst.Length];
        for (int i = 0; i < burst.Length + delay; i++)
        {
            float y = filter.Next(i < burst.Length ? burst[i] : 0f);
            if (i >= delay)
            {
                limited[i - delay] = y;
            }
        }

        return limited;
    }

    /// <inheritdoc/>
    public void ResetCarrierState() => _inner.ResetCarrierState();

    /// <inheritdoc/>
    public bool TrySetHardware(ReadOnlySpan<byte> payload, out string outcome)
    {
        // Moving a modem changes where it sits, not what it is: hardware control passes
        // straight through to the wrapped instance.
        if (_inner is IHardwareControllable hw)
        {
            return hw.TrySetHardware(payload, out outcome);
        }

        outcome = $"mode '{Mode}' has no hardware settings";
        return false;
    }
}
