namespace Packet.SoundModem.Daemon;

/// <summary>
/// The start-up warning for a CW ident that would go somewhere other than its own modem's
/// centre: the tone, the data and the ident are all meant to sit on the signal's frequency.
/// Warning only, nothing is refused.
/// </summary>
internal static class IdentCentreCheck
{
    /// <summary>How far the ident may sit from the modem's centre before it is worth a line.</summary>
    internal const double ToleranceHz = 50;

    /// <summary>
    /// The warning line, or null when there is nothing to say.
    /// </summary>
    /// <param name="subChannel">The modem's number, for the line's prefix.</param>
    /// <param name="identToneHz">Where the ident goes, in audio Hz.</param>
    /// <param name="centreHz">The modem's own audio centre, or null when it has none (baseband).</param>
    /// <param name="isFm">True on FM, where the ident is a bare audio tone with no RF meaning.</param>
    /// <param name="dialHz">The dial, or null when there is no band plan.</param>
    /// <param name="upperSideband">True when audio Hz are added to the dial.</param>
    /// <param name="fromToneHz">True when the ident's place came from <c>toneHz</c> or
    /// <c>rfFrequency</c> rather than defaulting to the centre.</param>
    internal static string? Warning(
        int subChannel, double identToneHz, double? centreHz, bool isFm,
        double? dialHz, bool upperSideband, bool fromToneHz)
    {
        if (isFm || !fromToneHz || centreHz is not double centre
            || Math.Abs(identToneHz - centre) <= ToleranceHz)
        {
            return null;
        }

        string Rf(double audio) => dialHz is double dial
            ? $" ({RfPlan.Mhz(upperSideband ? dial + audio : dial - audio)})"
            : "";

        return $"modem {subChannel}: WARNING - the CW ident is at {identToneHz:F0} Hz audio{Rf(identToneHz)} "
            + $"but the modem's signal is centred at {centre:F0} Hz{Rf(centre)}. "
            + "Remove \"toneHz\" from \"identify\" to send it on the signal's centre.";
    }
}
