using M0LTE.Dsp;

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
public sealed class FrequencyShiftedModem : IModem
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
    /// 170 Hz, both pinned by Ms110dObwTests), and the Hilbert response rolls off close to DC
    /// and Nyquist even at 639 taps, taking image rejection with it.</summary>
    private const double EdgeGuardHz = 350;

    private readonly IModem _inner;
    private readonly int _sampleRate;
    private readonly double _shiftHz;
    private readonly FirFilter _receiveBandpass;
    private readonly FrequencyShifter _receive;
    private readonly float[] _banded = new float[4096];
    private readonly float[] _scratch = new float[4096];

    private FrequencyShiftedModem(
        IModem inner, int sampleRate, double shiftHz, double onAirLowHz, double onAirHighHz)
    {
        _inner = inner;
        _sampleRate = sampleRate;
        _shiftHz = shiftHz;
        _receiveBandpass = new FirFilter(FilterDesign.BandPass(
            Math.Max(50, onAirLowHz - BandpassMarginHz),
            Math.Min((sampleRate / 2.0) - 50, onAirHighHz + BandpassMarginHz),
            sampleRate, BandpassTaps));
        _receive = new FrequencyShifter(sampleRate, -shiftHz, HilbertTaps);
    }

    /// <summary>
    /// Wraps <paramref name="inner"/> so it works at <paramref name="centreHz"/> instead of its
    /// native <paramref name="nativeCentreHz"/>. The shifted band is measured off the inner
    /// modem's own modulator and checked clear of DC and Nyquist before anything is built -
    /// a centre that would fold the band over is refused with the numbers, not discovered on air.
    /// </summary>
    /// <exception cref="ArgumentException">The shifted band would reach within
    /// <see cref="EdgeGuardHz"/> of DC or Nyquist.</exception>
    public static IModem Wrap(IModem inner, int sampleRate, double centreHz, double nativeCentreHz)
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
        if (low + shift < EdgeGuardHz || high + shift > nyquist - EdgeGuardHz)
        {
            throw new ArgumentException(
                $"centre {centreHz:F0} Hz would put {inner.Mode}'s band at "
                + $"{low + shift:F0}-{high + shift:F0} Hz, which is not clear of the "
                + $"{EdgeGuardHz:F0} Hz guard against DC and the {nyquist:F0} Hz Nyquist edge "
                + $"(the mode occupies {low:F0}-{high:F0} Hz at its native "
                + $"{nativeCentreHz:F0} Hz centre)",
                nameof(centreHz));
        }

        return new FrequencyShiftedModem(inner, sampleRate, shift, low + shift, high + shift);
    }

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
    public float[] Modulate(ReadOnlySpan<byte> ax25Frame, int txDelayMilliseconds)
    {
        float[] burst = _inner.Modulate(ax25Frame, txDelayMilliseconds);

        // A fresh shifter per burst (no stale tail from the last transmission), fed the burst
        // plus one group delay of zeros so the delayed tail flushes: the FIR delays everything
        // by (taps-1)/2 samples, and without the pad that many samples of burst - the end of
        // the EOT in the MS110D case - would never come out. The leading transient lands in
        // the burst's own TXDELAY silence.
        const int groupDelay = (HilbertTaps - 1) / 2;
        var shifter = new FrequencyShifter(_sampleRate, _shiftHz, HilbertTaps);
        var shifted = new float[burst.Length + groupDelay];
        shifter.Process(burst, shifted.AsSpan(0, burst.Length));
        shifter.Process(new float[groupDelay], shifted.AsSpan(burst.Length));
        return shifted;
    }

    /// <inheritdoc/>
    public void ResetCarrierState() => _inner.ResetCarrierState();
}
