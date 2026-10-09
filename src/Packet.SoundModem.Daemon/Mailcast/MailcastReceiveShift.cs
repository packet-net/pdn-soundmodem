using M0LTE.Dsp;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// Moves the station's own receive audio so the mailcast signal's centre lands on MS110D's
/// native audio centre, upstream of the modem, instead of asking the generic band-plan shift
/// decorator (<see cref="FrequencyShiftedModem"/>) to move the modem itself.
/// </summary>
/// <remarks>
/// <para><see cref="FrequencyShiftedModem.Wrap"/> refuses a centre that would put a moved modem's
/// own occupied band within its guard of DC or Nyquist, rather than fold noise over DC - a real
/// guard, measured, and not this class's business to relax. It exists for a modem a band plan
/// can ask to sit anywhere a station's passband reaches, carrying whatever that passband's own
/// width is. Mailcast is narrower than that: the signal is already band-limited by the sending
/// end to about 2.9 kHz, so once this class has bandpassed to exactly that width there is no
/// below-band noise left to fold, at any shift the sound-card measure-or-type rule accepts (1000
/// to 2000 Hz is quite modest set against MS110D's native 1800 Hz - the largest shift either
/// direction is 800 Hz). Shifting the audio instead of the modem sidesteps the generic guard
/// entirely, because the modem it feeds never leaves its native centre.</para>
/// <para>The same load-bearing order <see cref="FrequencyShiftedModem"/>'s own remarks describe:
/// bandpass to the signal's own width <em>before</em> shifting, never after - downshifting noise
/// that has not been filtered first folds it onto the band, measured as a real SNR cost
/// (pdn-soundmodem issue referenced there). Built once per receiver, from the measured half-width
/// <see cref="MailcastPlacement.HalfWidthHz"/> gives.</para>
/// <para>At the default dial (centre already MS110D's native centre) this does nothing at all,
/// and <see cref="Active"/> says so.</para>
/// </remarks>
internal sealed class MailcastReceiveShift
{
    /// <summary>Hilbert FIR length for the shift itself: <see cref="FrequencyShiftedModem"/>
    /// uses the same length for the same reason (its own remarks: short enough leaks an image of
    /// the band's own bottom back into the passband at MS110D's native centre).</summary>
    private const int HilbertTaps = 639;

    /// <summary>Receive bandpass length; matches the shift's own, as <see cref="FrequencyShiftedModem"/> does.</summary>
    private const int BandpassTaps = 639;

    /// <summary>How far the receive bandpass opens beyond the measured half-width, the same
    /// margin <see cref="FrequencyShiftedModem"/> uses and for the same reason: room for the
    /// measured edges' own skirts.</summary>
    private const double BandpassMarginHz = 300;

    private readonly FirFilter? _bandpass;
    private readonly FrequencyShifter? _shift;
    private readonly float[] _banded = new float[4096];
    private readonly float[] _scratch = new float[4096];

    /// <param name="sampleRate">The channel's DSP rate.</param>
    /// <param name="centreHz">Where the signal actually sits in this station's audio.</param>
    /// <param name="nativeCentreHz">Where the modem itself expects it (its own native centre).</param>
    /// <param name="halfWidthHz">Half the signal's own occupied width, for the bandpass.</param>
    internal MailcastReceiveShift(int sampleRate, double centreHz, double nativeCentreHz, double halfWidthHz)
    {
        double shift = nativeCentreHz - centreHz;
        if (Math.Abs(shift) < 0.5)
        {
            return;
        }

        _bandpass = new FirFilter(FilterDesign.BandPass(
            Math.Max(50, centreHz - halfWidthHz - BandpassMarginHz),
            Math.Min((sampleRate / 2.0) - 50, centreHz + halfWidthHz + BandpassMarginHz),
            sampleRate, BandpassTaps));
        _shift = new FrequencyShifter(sampleRate, shift, HilbertTaps);
    }

    /// <summary>Whether this actually moves anything; false at the default dial, where
    /// <see cref="Process"/> is never called at all.</summary>
    internal bool Active => _shift is not null;

    /// <summary>
    /// Bandpasses then shifts <paramref name="samples"/> to the modem's native centre, and hands
    /// each chunk straight to <paramref name="modem"/> - chunked through fixed scratches so the
    /// steady state allocates nothing, the same pattern <see cref="FrequencyShiftedModem.Process"/>
    /// uses on its own receive path.
    /// </summary>
    internal void Process(ReadOnlySpan<float> samples, IModem modem)
    {
        if (_bandpass is null || _shift is null)
        {
            modem.Process(samples);
            return;
        }

        while (samples.Length > 0)
        {
            int count = Math.Min(samples.Length, _scratch.Length);
            for (int i = 0; i < count; i++)
            {
                _banded[i] = _bandpass.Next(samples[i]);
            }

            _shift.Process(_banded.AsSpan(0, count), _scratch.AsSpan(0, count));
            modem.Process(_scratch.AsSpan(0, count));
            samples = samples[count..];
        }
    }
}
