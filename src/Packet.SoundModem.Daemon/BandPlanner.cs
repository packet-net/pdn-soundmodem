using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// Turns a configuration written in RF terms into the dial the operator has to set and the
/// audio centre each modem then needs.
/// </summary>
/// <remarks>
/// Bandwidths come from the modems themselves via <see cref="ModemBandProbe"/> rather than a
/// table, so the plan is checked against what the modes actually occupy. ARDOP is the one
/// exception — it has no fixed width to measure, its bandwidth being negotiated per session —
/// so it plans for the configured cap, or the widest it can reach.
/// </remarks>
internal static class BandPlanner
{
    /// <summary>ARDOP's widest negotiable session, and so what it must be planned for by default.</summary>
    private const double ArdopWidestHz = ArdopChannelShift.WidestBandwidthHz;

    /// <summary>
    /// Plans <paramref name="modems"/>, mutating each entry's <see cref="ModemConfig.Frequency"/>
    /// to the audio centre the plan calls for. Returns null when no modem is RF-addressed, which
    /// leaves the audio-centre configuration alone.
    /// </summary>
    internal static RfPlan.Result? Plan(
        IReadOnlyList<ModemConfig> modems, string sideband, double? pinnedDialHz, int dspRate)
    {
        if (!modems.Any(m => m.RfFrequency is not null))
        {
            return null;
        }

        var slots = modems
            .Select(m => new RfSlot(m.SubChannel, m.Mode, m.RfFrequency!.Value, WidthOf(m, dspRate)))
            .ToList();

        RfPlan.Result plan = RfPlan.Solve(slots, sideband, pinnedDialHz);

        // Hand the answer back to the modems as the audio centre each now needs.
        foreach (PlannedModem planned in plan.Modems)
        {
            modems.Single(m => m.SubChannel == planned.Slot.SubChannel).Frequency = planned.AudioCentreHz;
        }

        return plan;
    }

    /// <summary>
    /// How wide to plan for a modem: what it says, else what it measures, else a nominal figure
    /// when it will not tell us. ARDOP is asked for nothing — it has no fixed answer to give.
    /// </summary>
    private static double WidthOf(ModemConfig modem, int dspRate)
    {
        if (modem.Bandwidth is double stated)
        {
            return stated;
        }

        if (DaemonConfig.IsArdop(modem.Mode))
        {
            return ArdopWidestHz;
        }

        try
        {
            // Built at the mode's own default centre: occupied width does not depend on where
            // the mode is centred, and the centre is what we are trying to work out.
            IModem probe = ModemCatalog.Create(modem.Mode, dspRate, _ => { });
            if (ModemBandProbe.TryMeasure(probe, dspRate, out double low, out double high))
            {
                return high - low;
            }
        }
        catch (ArgumentException)
        {
            // Unknown mode: reported separately and far more usefully by the modem loop.
        }

        // A mode that will not render a probe frame still has to be placed somewhere; assume a
        // narrow one rather than silently treating it as zero-width.
        return 500;
    }

    /// <summary>
    /// The transmit-filter high cut a plan needs, in Hz: the top of the highest modem plus a
    /// little margin, on a round step. The low cut is not settable through the station API.
    /// </summary>
    internal static int TransmitFilterHighHz(RfPlan.Result plan)
    {
        double highestEdge = plan.Modems.Max(m => m.AudioCentreHz + (m.Slot.BandwidthHz / 2));
        return HighCutClearing(highestEdge);
    }

    /// <summary>
    /// The high cut clearing an audio band that ends at <paramref name="highestEdgeHz"/>: a
    /// little margin above it, rounded up to a 50 Hz step so the number reads like a setting
    /// rather than a measurement. Shared with <see cref="TransmitFilterPlan"/>, which asks the
    /// same question of a station placed by audio centre instead of by band plan.
    /// </summary>
    internal static int HighCutClearing(double highestEdgeHz) =>
        (int)(Math.Ceiling((highestEdgeHz + MarginHz) / 50) * 50);

    /// <summary>Headroom above the highest modem, so the filter skirt is not on top of it.</summary>
    private const double MarginHz = 200;

    /// <summary>The start-up report: the dial, and where each modem landed.</summary>
    /// <param name="radioIsSelfTuning">
    /// True when the daemon is setting the dial itself (a headless Flex), so the operator is
    /// told what happened rather than told to go and do it.
    /// </param>
    internal static void Report(RfPlan.Result plan, TextWriter output, bool radioIsSelfTuning = false)
    {
        output.WriteLine(
            $"dial: {RfPlan.Mhz(plan.DialHz)} {plan.Sideband.ToUpperInvariant()}"
            + (radioIsSelfTuning ? "" : " — set your radio to this"));
        foreach (PlannedModem m in plan.Modems)
        {
            output.WriteLine(
                $"  modem {m.Slot.SubChannel} {m.Slot.Mode} at {RfPlan.Mhz(m.Slot.RfCentreHz)} "
                + $"= {m.AudioCentreHz:F0} Hz audio");
        }
    }
}
