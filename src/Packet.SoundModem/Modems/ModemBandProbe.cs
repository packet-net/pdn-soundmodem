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
    /// modulate the probe frame, or produces too little audio to meter — the caller then has
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
        if (audio.Length - skip < 2048)
        {
            return false;
        }

        (lowHz, highHz, _, _) = OccupiedBandwidth.Measure(
            audio.AsSpan(skip), sampleRate, fftSize: sampleRate >= 24000 ? 4096 : 1024);
        return true;
    }

    /// <summary>
    /// A representative little frame: PDNSM&gt;PDNSM with a short payload — enough symbols for a
    /// stable Welch estimate in every mode.
    /// </summary>
    private static byte[] BuildProbeFrame()
    {
        var frame = new byte[48];
        WriteAddress(frame, 0, "PDNSM", last: false);
        WriteAddress(frame, 7, "PDNSM", last: true);
        frame[14] = 0x03;
        frame[15] = 0xF0;
        for (int n = 16; n < frame.Length; n++)
        {
            frame[n] = (byte)(n * 37); // arbitrary non-repeating payload
        }

        return frame;

        static void WriteAddress(byte[] frame, int at, string call, bool last)
        {
            for (int n = 0; n < 6; n++)
            {
                frame[at + n] = (byte)((n < call.Length ? call[n] : ' ') << 1);
            }

            frame[at + 6] = (byte)(0x60 | (last ? 1 : 0));
        }
    }
}
