using M0LTE.Dsp;

namespace Packet.SoundModem.Modems;

/// <summary>
/// Measures how much audio bandwidth a modem actually occupies, by modulating a representative
/// frame and metering the result.
/// </summary>
/// <remarks>
/// Measured rather than tabulated on purpose: a table of 38 modes' bandwidths would be one more
/// thing to keep in step with the modems, and would be wrong the first time one was retuned.
/// This asks the modem itself.
///
/// Used by the browser waterfall to draw each modem's band, and by the RF band planner to work
/// out whether a set of modems fits inside one SSB passband.
/// </remarks>
public static class ModemBandProbe
{
    /// <summary>
    /// Measures <paramref name="modem"/>'s occupied band in Hz. False when the mode cannot
    /// modulate the probe frame, or produces too little audio to meter - the caller then has
    /// no measurement rather than a bad one.
    /// </summary>
    public static bool TryMeasure(IModem modem, int sampleRate, out double lowHz, out double highHz)
    {
        ArgumentNullException.ThrowIfNull(modem);
        lowHz = highHz = 0;

        float[] audio;
        try
        {
            audio = modem.Modulate(BuildProbeFrame(), txDelayMilliseconds: 60);
        }
        catch (ArgumentException)
        {
            return false;
        }

        // Skip the leading third: preamble/training, which some modes shape differently from
        // steady-state modulation, and the OBW meter wants steady state.
        int skip = audio.Length / 3;
        int usable = audio.Length - skip;

        // One window's worth is the real requirement, and the fastest modes are the ones that fall
        // short of a fixed one: the probe frame is a fixed number of *bytes*, so at 3600 baud on a
        // 12 kHz channel it is over in well under 2048 samples. Refusing to measure there is worse
        // than measuring coarsely - an unmeasured mode falls back to a nominal 500 Hz width, which
        // for qpsk3600 is out by the better part of a passband and lands in the band plan.
        int window = sampleRate >= 24000 ? 4096 : 1024;
        while (window > 256 && usable < window)
        {
            window /= 2;
        }

        if (usable < window)
        {
            return false;
        }

        (lowHz, highHz, _, _) = OccupiedBandwidth.Measure(audio.AsSpan(skip), sampleRate, fftSize: window);
        return true;
    }

    /// <summary>
    /// A representative little frame: PDNSM&gt;PDNSM with a short payload - enough symbols for a
    /// stable Welch estimate in every mode.
    /// </summary>
    private static byte[] BuildProbeFrame()
    {
        var payload = new byte[32];
        for (int n = 0; n < payload.Length; n++)
        {
            payload[n] = (byte)((n + 16) * 37); // arbitrary non-repeating payload
        }

        return Waterfall.Ax25UiFrame.Build("PDNSM", "PDNSM", payload);
    }
}
