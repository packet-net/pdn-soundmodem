using M0LTE.Dsp;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// Adapts the ARDOP TNC's native audio to the shared channel, in two independent ways: moves it
/// to a different centre so it can share a passband with the packet modems instead of sitting on
/// top of one, and bridges the engine's fixed 12 kHz to a wider channel rate when one of the
/// 48 kHz families (fsk9600, freedv-*, ms110d-*) has set the channel there.
/// </summary>
/// <remarks>
/// <para>ARDOP's waveforms are pinned to a 1500 Hz centre and M0LTE.Ardop exposes no way to move
/// them - or to run at anything but 12 kHz - so both adaptations happen outside it: transmitted
/// audio is shifted from 1500 Hz to the configured centre and upsampled to the channel rate;
/// received audio is decimated back to 12 kHz and shifted back to 1500 Hz before the TNC ever
/// sees it. The TNC is unaware, which is the point - nothing inside the ARQ engine has to
/// change. The same pattern as <c>FreeDvDatacModem</c>, whose engine is native 8 kHz.</para>
///
/// <para>The shift runs at the engine rate on both sides (cheaper, and the constraint it has to
/// respect - the engine's Nyquist - lives there too): receive decimates first, transmit shifts
/// first. The 1500 Hz figure is measured, not assumed: <c>ArdopModulator.TwoToneTest()</c> emits
/// the ardopcf calibration pair, which lands on 1475.1 and 1524.9 Hz - a midpoint of 1500.0 Hz
/// and the documented ±25 Hz spacing. <c>ArdopChannelBridgeTests</c> re-measures it, so this
/// constant cannot drift away from the package underneath us.</para>
/// </remarks>
internal sealed class ArdopChannelBridge
{
    /// <summary>The centre ARDOP's own modulator works to. Measured; see the type remarks.</summary>
    internal const double NativeCentreHz = 1500.0;

    /// <summary>Widest bandwidth ARDOP 1 negotiates, and so the most that has to fit.</summary>
    internal const double WidestBandwidthHz = 2000.0;

    /// <summary>
    /// The nominal SSB transmit passband a shifted centre has to live inside. Nyquist is the
    /// wrong yardstick here: the channel would happily carry 3500 Hz, but the radio's SSB
    /// filter will not pass it, so a centre that only fits under Nyquist is still unusable.
    /// A nominal window rather than a measured one - the daemon cannot know the rig's filter.
    /// </summary>
    internal const double PassbandLowHz = 300.0;
    internal const double PassbandHighHz = 2700.0;

    /// <summary>Receive bandpass length: at 12 kHz its ~60 Hz transition is far sharper than
    /// the 300 Hz margin below needs, and it matches the FrequencyShiftedModem's choice so the
    /// two shift paths read the same.</summary>
    private const int ReceiveBandpassTaps = 639;

    /// <summary>How far the receive bandpass opens beyond the widest session's band edges,
    /// so real skirts sit in the flat passband.</summary>
    private const double ReceiveBandpassMarginHz = 300.0;

    /// <summary>Hilbert length of the per-burst transmit shifter - the package default,
    /// which the persistent shifter this replaces also ran.</summary>
    private const int TransmitHilbertTaps = 129;

    private readonly FrequencyShifter? _receive;

    /// <summary>Bandpass to the on-air band, applied BEFORE the receive unshift - and that
    /// order is load-bearing, not hygiene. Unshifting a centre above 1500 Hz is a downshift
    /// by delta = centre - 1500, and downshifting a real signal folds all channel noise below
    /// delta onto delta - f: at centre 2400 that measured +2.9 dB across the bottom of a
    /// 2000 Hz session's band (the same fold the FrequencyShiftedModem BER gate caught at
    /// 3 dB, where more Hilbert taps changed nothing because it is not a Hilbert defect).
    /// Sized for the widest session plus margin, the bandpass's low edge sits above every
    /// fold-source frequency, so the fold's input is stopband noise. Null when unshifted -
    /// that path stays bit-for-bit what it always was.</summary>
    private readonly FirFilter? _receiveBandpass;

    /// <summary>Channel rate -> engine rate, receive side. One instance for the life of the
    /// bridge: the RX stream is continuous, so the decimator's filter state must carry across
    /// blocks. Null when the channel already runs at the engine rate.</summary>
    private readonly Decimator? _receiveDecimator;

    private readonly int _engineRate;
    private readonly int _channelRate;
    private readonly int _factor;
    private readonly double _centreHz;

    /// <summary>Decimator output scratch, grown to the largest block seen and then reused.</summary>
    private float[] _decimated = [];

    /// <summary>Bandpass output scratch for <see cref="Unshift"/>, likewise reused.</summary>
    private float[] _banded = [];

    /// <summary>Engine-rate output scratch for <see cref="Receive"/>, likewise reused: the
    /// receive path runs per 20 ms block for the life of the daemon, and a fresh array per
    /// block was steady-state garbage on the audio thread.</summary>
    private float[] _engine = [];

    private ArdopChannelBridge(double centreHz, int engineRate, int channelRate)
    {
        if (channelRate < engineRate || channelRate % engineRate != 0)
        {
            throw new ArgumentException(
                $"channel rate {channelRate} is not an integer multiple of ARDOP's "
                + $"{engineRate} Hz engine rate", nameof(channelRate));
        }

        _engineRate = engineRate;
        _channelRate = channelRate;
        _factor = channelRate / engineRate;
        _centreHz = centreHz;
        double delta = centreHz - NativeCentreHz;
        if (delta != 0)
        {
            _receive = new FrequencyShifter(engineRate, -delta);
            _receiveBandpass = new FirFilter(FilterDesign.BandPass(
                Math.Max(50, centreHz - (WidestBandwidthHz / 2) - ReceiveBandpassMarginHz),
                Math.Min((engineRate / 2.0) - 50, centreHz + (WidestBandwidthHz / 2) + ReceiveBandpassMarginHz),
                engineRate, ReceiveBandpassTaps));
        }

        if (_factor > 1)
        {
            _receiveDecimator = new Decimator(channelRate, _factor);
        }
    }

    /// <summary>
    /// A bridge to <paramref name="centreHz"/> at <paramref name="channelRate"/>, or a
    /// pass-through when no centre was configured and the channel runs at the engine rate.
    /// </summary>
    internal static ArdopChannelBridge For(double? centreHz, int engineRate, int channelRate) =>
        new(centreHz ?? NativeCentreHz, engineRate, channelRate);

    /// <summary>Whether the centre shift is doing anything.</summary>
    internal bool IsShifted => _receive is not null;

    /// <summary>Whether the rate bridge is doing anything.</summary>
    internal bool IsBridged => _factor > 1;

    /// <summary>
    /// ARDOP's audio (engine rate), moved to the configured centre and brought up to the
    /// channel rate. The identity pass-through when neither is needed.
    /// </summary>
    internal float[] Transmit(float[] audio)
    {
        float[] shifted = audio;
        if (IsShifted)
        {
            // A fresh shifter per burst, fed one group delay of zeros so the delayed tail
            // flushes - the same treatment FrequencyShiftedModem.Modulate and the per-burst
            // upsampler below get, and for the same reason. The persistent shifter this
            // replaces truncated the last (taps-1)/2 samples of every burst (~5.3 ms of
            // ARDOP's trailer tones never went on air) and carried them in its filter state
            // to emerge at the START of the next burst, inside its leader.
            const int groupDelay = (TransmitHilbertTaps - 1) / 2;
            var shifter = new FrequencyShifter(
                _engineRate, _centreHz - NativeCentreHz, TransmitHilbertTaps);
            shifted = new float[audio.Length + groupDelay];
            shifter.Process(audio, shifted.AsSpan(0, audio.Length));
            shifter.Process(new float[groupDelay], shifted.AsSpan(audio.Length));
        }

        if (_factor == 1)
        {
            return shifted;
        }

        // Per burst, like FreeDvDatacModem's TX: an upsampler carries filter state, and a fresh
        // one per burst means no stale tail from the previous transmission leaks into this one.
        var upsampler = new Upsampler(_channelRate, _factor);
        var bridged = new float[upsampler.OutputLength(shifted.Length)];
        upsampler.Process(shifted, bridged);
        return bridged;
    }

    /// <summary>
    /// Channel audio, brought down to the engine rate and moved back to where ARDOP expects to
    /// find its signal. The returned span aliases reused scratch: consume it before the next
    /// call (the TNC's <c>ProcessReceive</c> does, synchronously).
    /// </summary>
    internal ReadOnlySpan<float> Receive(ReadOnlySpan<float> samples)
    {
        if (_receiveDecimator is null)
        {
            if (_receive is null)
            {
                return samples;
            }

            if (_engine.Length < samples.Length)
            {
                _engine = new float[samples.Length];
            }

            Unshift(samples, _engine.AsSpan(0, samples.Length));
            return _engine.AsSpan(0, samples.Length);
        }

        int most = _receiveDecimator.MaxOutput(samples.Length);
        if (_decimated.Length < most)
        {
            _decimated = new float[most];
        }

        int produced = _receiveDecimator.Process(samples, _decimated);
        if (_receive is null)
        {
            return _decimated.AsSpan(0, produced);
        }

        if (_engine.Length < produced)
        {
            _engine = new float[produced];
        }

        Unshift(_decimated.AsSpan(0, produced), _engine.AsSpan(0, produced));
        return _engine.AsSpan(0, produced);
    }

    /// <summary>Bandpass to the on-air band, then move it back to ARDOP's native centre - in
    /// that order; see <see cref="_receiveBandpass"/> for why swapping them costs 3 dB.</summary>
    private void Unshift(ReadOnlySpan<float> onAir, Span<float> output)
    {
        if (_banded.Length < onAir.Length)
        {
            _banded = new float[onAir.Length];
        }

        for (int i = 0; i < onAir.Length; i++)
        {
            _banded[i] = _receiveBandpass!.Next(onAir[i]);
        }

        _receive!.Process(_banded.AsSpan(0, onAir.Length), output);
    }

    /// <summary>The start-up line's suffix.</summary>
    internal string Describe() =>
        (IsShifted ? $", centre {_centreHz:F0} Hz" : "")
        + (IsBridged ? $", engine {_engineRate} Hz bridged to the {_channelRate} Hz channel" : "");

    /// <summary>
    /// Why a centre may not work, or null if it is fine. A warning rather than a refusal: which
    /// bandwidth ARDOP ends up using is negotiated per session, so at start-up all that can be
    /// said is which of them will still fit.
    /// </summary>
    /// <param name="engineRate">The engine's rate, not the channel's: the shift runs at the
    /// engine rate, so its Nyquist is the hard ceiling however wide the channel is.</param>
    internal static string? Concern(double centreHz, int engineRate)
    {
        double nyquist = engineRate / 2.0;
        if (centreHz <= 0 || centreHz >= nyquist)
        {
            return $"centre {centreHz:F0} Hz is outside the 0-{nyquist:F0} Hz band the "
                + $"{engineRate} Hz ARDOP engine can address";
        }

        double widestFits = Math.Max(
            0, Math.Min(centreHz - PassbandLowHz, PassbandHighHz - centreHz) * 2);
        return widestFits >= WidestBandwidthHz
            ? null
            : $"centre {centreHz:F0} Hz leaves room for an ARDOP bandwidth of {widestFits:F0} Hz "
              + $"within a nominal {PassbandLowHz:F0}-{PassbandHighHz:F0} Hz SSB passband; sessions "
              + $"negotiating wider than that (up to {WidestBandwidthHz:F0} Hz) will be clipped by "
              + "the radio's filter. Check it against your rig's actual transmit passband";
    }
}
