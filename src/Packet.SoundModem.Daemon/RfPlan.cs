namespace Packet.SoundModem.Daemon;

/// <summary>One modem's place in the band plan, in absolute RF terms.</summary>
/// <param name="SubChannel">The modem's KISS sub-channel, for naming it in messages.</param>
/// <param name="Mode">Its mode, likewise.</param>
/// <param name="RfCentreHz">Where on the band the operator wants it.</param>
/// <param name="BandwidthHz">
/// How much room it occupies. Measured from the modem itself where one exists; for ARDOP,
/// whose bandwidth is negotiated per session, the configured cap or the widest it can reach.
/// </param>
internal sealed record RfSlot(int SubChannel, string Mode, double RfCentreHz, double BandwidthHz)
{
    internal double LowEdgeHz => RfCentreHz - (BandwidthHz / 2);

    internal double HighEdgeHz => RfCentreHz + (BandwidthHz / 2);
}

/// <summary>Where a modem ended up once the dial was chosen.</summary>
internal sealed record PlannedModem(RfSlot Slot, double AudioCentreHz);

/// <summary>
/// Turns a set of absolute RF centres into a dial frequency and the audio centres that follow
/// from it — the arithmetic an operator would otherwise do by hand, and the check they would
/// otherwise do by eye.
/// </summary>
/// <remarks>
/// An SSB transceiver's dial is shared by everything in the passband, so the modems are not
/// independent: one dial has to place all of them inside the radio's transmit filter. Given
/// only the RF centres, the daemon can choose that dial — and choose it better than the
/// obvious round number, which tends to leave the lowest mode sitting on the filter's skirt.
/// </remarks>
internal static class RfPlan
{
    /// <summary>
    /// The usable part of an SSB passband. Nominal — the daemon cannot know the rig's filter —
    /// but conservative enough that a plan fitting inside it will work on ordinary gear.
    /// </summary>
    internal const double PassbandLowHz = 300.0;
    internal const double PassbandHighHz = 2700.0;

    /// <summary>Dials are chosen on a round step; operators tune in whole hundreds of Hz.</summary>
    internal const double DialStepHz = 50.0;

    /// <param name="Warnings">
    /// Modems the plan places outside the nominal passband. Only ever produced for a dial the
    /// operator pinned: they chose it knowing their radio, and the passband here is a nominal
    /// figure the daemon cannot verify, so this informs rather than obstructs.
    /// </param>
    internal sealed record Result(
        double DialHz,
        string Sideband,
        IReadOnlyList<PlannedModem> Modems,
        IReadOnlyList<string> Warnings)
    {
        internal bool IsUpperSideband => Sideband.Equals("usb", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Audio offset of an RF frequency for a given dial and sideband.</summary>
    private static double AudioFor(double rfHz, double dialHz, bool upper) =>
        upper ? rfHz - dialHz : dialHz - rfHz;

    /// <summary>
    /// Chooses a dial for <paramref name="slots"/>, or explains why none exists.
    /// </summary>
    /// <param name="pinnedDialHz">
    /// A dial the operator has fixed — a net frequency, or matching another application. When
    /// given it is used as-is and merely checked, rather than chosen.
    /// </param>
    internal static Result Solve(
        IReadOnlyList<RfSlot> slots, string sideband, double? pinnedDialHz = null)
    {
        ArgumentNullException.ThrowIfNull(slots);
        if (slots.Count == 0)
        {
            throw new ArgumentException("no RF-addressed modems to plan", nameof(slots));
        }

        bool upper = sideband.Equals("usb", StringComparison.OrdinalIgnoreCase);
        if (!upper && !sideband.Equals("lsb", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"\"sideband\": \"{sideband}\" is not a sideband. Use \"usb\" or \"lsb\".");
        }

        double dial = pinnedDialHz ?? Choose(slots, upper);
        var planned = slots
            .Select(s => new PlannedModem(s, AudioFor(s.RfCentreHz, dial, upper)))
            .OrderBy(p => p.AudioCentreHz)
            .ToList();

        List<string> offenders = [.. planned
            .Where(p => Outside(p))
            .Select(p => Describe(p, dial, upper))];

        // A dial the daemon chose and that still does not fit means no dial fits, which is a
        // refusal. A dial the operator pinned is their call against a rig they can see and a
        // passband figure that is only nominal, so it is a warning.
        if (offenders.Count > 0 && pinnedDialHz is null)
        {
            throw new InvalidDataException(Explain(slots, offenders, dial, upper, pinnedDialHz));
        }

        List<string> warnings = offenders.Count == 0
            ? []
            : [Explain(slots, offenders, dial, upper, pinnedDialHz)];
        return new Result(dial, upper ? "usb" : "lsb", planned, warnings);
    }

    private static bool Outside(PlannedModem p)
    {
        double low = p.AudioCentreHz - (p.Slot.BandwidthHz / 2);
        double high = p.AudioCentreHz + (p.Slot.BandwidthHz / 2);
        return low < PassbandLowHz || high > PassbandHighHz;
    }

    /// <summary>
    /// Centres the whole ensemble in the passband, rather than jamming its lowest member
    /// against the filter skirt — which is what the obvious round dial tends to do.
    /// </summary>
    private static double Choose(IReadOnlyList<RfSlot> slots, bool upper)
    {
        double lowEdge = slots.Min(s => s.LowEdgeHz);
        double highEdge = slots.Max(s => s.HighEdgeHz);
        double ensembleCentre = (lowEdge + highEdge) / 2;
        double passbandCentre = (PassbandLowHz + PassbandHighHz) / 2;

        // Put the ensemble's centre at the passband's centre, then round to a dial an operator
        // can actually set. Rounding moves every modem together, so it cannot reorder them.
        double exact = upper ? ensembleCentre - passbandCentre : ensembleCentre + passbandCentre;
        double rounded = Math.Round(exact / DialStepHz) * DialStepHz;

        // Rounding can push a tight plan over an edge; keep it only when it still fits.
        var atRounded = slots.Select(s => new PlannedModem(s, AudioFor(s.RfCentreHz, rounded, upper)));
        return atRounded.Any(Outside) ? exact : rounded;
    }

    private static string Describe(PlannedModem p, double dial, bool upper) =>
        $"modem {p.Slot.SubChannel} ({p.Slot.Mode}) at {Mhz(p.Slot.RfCentreHz)} would sit at "
        + $"{p.AudioCentreHz:F0} Hz audio, occupying {p.AudioCentreHz - (p.Slot.BandwidthHz / 2):F0}"
        + $"-{p.AudioCentreHz + (p.Slot.BandwidthHz / 2):F0} Hz";

    private static string Explain(
        IReadOnlyList<RfSlot> slots, List<string> offenders, double dial, bool upper, double? pinned)
    {
        double span = slots.Max(s => s.HighEdgeHz) - slots.Min(s => s.LowEdgeHz);
        double room = PassbandHighHz - PassbandLowHz;
        var text = new System.Text.StringBuilder();

        if (pinned is not null)
        {
            text.AppendLine(
                $"with the dial pinned to {Mhz(dial)} {(upper ? "USB" : "LSB")}, these fall outside "
                + $"the nominal {PassbandLowHz:F0}-{PassbandHighHz:F0} Hz passband. That is only a "
                + "nominal figure — if your rig passes them, ignore this; omit \"dialFrequency\" "
                + "and the dial will be chosen to fit them:");
        }
        else if (span > room)
        {
            // No dial can help: the modems are simply spread wider than one passband.
            text.AppendLine(
                $"these modems span {span:F0} Hz of RF ({Mhz(slots.Min(s => s.LowEdgeHz))} to "
                + $"{Mhz(slots.Max(s => s.HighEdgeHz))}), which is more than the {room:F0} Hz a "
                + "single SSB passband can carry. No dial frequency can place them all on air at "
                + "once — split them across separate radios, or move them closer together:");
        }
        else
        {
            text.AppendLine(
                $"no dial frequency places every modem inside the {PassbandLowHz:F0}-{PassbandHighHz:F0} Hz passband:");
        }

        foreach (string offender in offenders)
        {
            text.AppendLine($"  {offender}");
        }

        text.Append(
            "  An ARDOP modem's width is its negotiated maximum; \"bandwidth\" on it plans for "
            + "less and caps what it negotiates.");
        return text.ToString();
    }

    /// <summary>Frequencies read back to an operator the way one is dialled: MHz to the Hz.</summary>
    internal static string Mhz(double hz) =>
        (hz / 1_000_000).ToString("F6", System.Globalization.CultureInfo.InvariantCulture) + " MHz";
}
