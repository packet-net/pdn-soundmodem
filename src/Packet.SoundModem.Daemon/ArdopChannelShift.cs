using M0LTE.Dsp;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// Moves an ARDOP TNC's audio to a different centre so it can share a passband with the packet
/// modems, instead of sitting on top of one of them.
/// </summary>
/// <remarks>
/// ARDOP's waveforms are pinned to a 1500 Hz centre and M0LTE.Ardop exposes no way to move
/// them, so the shift happens outside it: transmitted audio is mixed up or down from 1500 Hz to
/// the configured centre, and received audio is mixed back to 1500 Hz before the TNC ever sees
/// it. The TNC is unaware, which is the point - nothing inside the ARQ engine has to change.
///
/// The 1500 Hz figure is measured, not assumed: <c>ArdopModulator.TwoToneTest()</c> emits the
/// ardopcf calibration pair, which lands on 1475.1 and 1524.9 Hz - a midpoint of 1500.0 Hz and
/// the documented ±25 Hz spacing. <see cref="ArdopCentreFrequencyTests"/> re-measures it, so
/// this constant cannot drift away from the package underneath us.
/// </remarks>
internal sealed class ArdopChannelShift
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

    private readonly FrequencyShifter? _transmit;
    private readonly FrequencyShifter? _receive;
    private readonly double _centreHz;

    private ArdopChannelShift(double centreHz, int sampleRate)
    {
        _centreHz = centreHz;
        double delta = centreHz - NativeCentreHz;
        if (delta != 0)
        {
            _transmit = new FrequencyShifter(sampleRate, delta);
            _receive = new FrequencyShifter(sampleRate, -delta);
        }
    }

    /// <summary>
    /// A shift to <paramref name="centreHz"/>, or a pass-through when no centre was configured.
    /// </summary>
    internal static ArdopChannelShift For(double? centreHz, int sampleRate) =>
        new(centreHz ?? NativeCentreHz, sampleRate);

    /// <summary>Whether the shift is doing anything.</summary>
    internal bool IsShifted => _transmit is not null;

    /// <summary>ARDOP's audio, moved to the configured centre.</summary>
    internal float[] Transmit(float[] audio)
    {
        if (_transmit is null)
        {
            return audio;
        }

        var shifted = new float[audio.Length];
        _transmit.Process(audio, shifted);
        return shifted;
    }

    /// <summary>Channel audio, moved back to where ARDOP expects to find its signal.</summary>
    internal float[] Receive(ReadOnlySpan<float> samples)
    {
        var shifted = new float[samples.Length];
        if (_receive is null)
        {
            samples.CopyTo(shifted);
            return shifted;
        }

        _receive.Process(samples, shifted);
        return shifted;
    }

    /// <summary>The start-up line's suffix.</summary>
    internal string Describe() => IsShifted ? $", centre {_centreHz:F0} Hz" : "";

    /// <summary>
    /// Why a centre may not work, or null if it is fine. A warning rather than a refusal: which
    /// bandwidth ARDOP ends up using is negotiated per session, so at start-up all that can be
    /// said is which of them will still fit.
    /// </summary>
    internal static string? Concern(double centreHz, int sampleRate)
    {
        double nyquist = sampleRate / 2.0;
        if (centreHz <= 0 || centreHz >= nyquist)
        {
            return $"centre {centreHz:F0} Hz is outside the channel's 0-{nyquist:F0} Hz audio band";
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
