using System.Globalization;
using System.Text.Json.Nodes;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// Where each modem of a proposed configuration would sit on the band, for the configuration
/// page's band strip: the answer to <c>POST /api/config/plan</c>.
/// </summary>
/// <remarks>
/// <para><b>The same planning start-up does.</b> The dial, the passband and every modem's audio
/// centre come from <see cref="ConfigApi.PlanFor"/>, which is what <see cref="ConfigApi.Validate"/>
/// calls, so the picture is what the station would do and not a second opinion of it. Each modem's
/// width is what <see cref="BandPlanner"/> plans with: the modem's own <c>bandwidth</c>, else
/// measured off the modem by <see cref="ModemBandProbe"/>, else a nominal 500 Hz for a mode that
/// will not render a probe; ARDOP is planned for its widest session.</para>
/// <para><b>When the plan is refused.</b> A proposal the loader accepts but the planner refuses
/// (no dial fits all the modems) still gets an answer, with the refusal as <c>problem</c> and each
/// modem where it asked to be, so the page can draw the picture and say why it cannot be built.</para>
/// <para><b>FM.</b> A channel radio has no dial to trade against audio, and the daemon cannot know
/// its audio path, so the answer has no passband and the page draws the audio band.</para>
/// </remarks>
internal static class ConfigPlan
{
    /// <summary>Overlaps and overshoots smaller than this are rounding, not a finding.</summary>
    private const double ToleranceHz = 0.5;

    /// <summary>Moves suggested to the operator land on a round step, as tuning does.</summary>
    private const double SuggestStepHz = 100;

    internal static JsonObject Describe(DaemonConfig proposed)
    {
        int dspRate = ConfigApi.DspRateFor(proposed);
        double?[] asked = [.. proposed.Modems.Select(m => m.RfFrequency)];

        RfPlan.Result? plan = null;
        string? problem = null;
        try
        {
            plan = ConfigApi.PlanFor(proposed);
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException)
        {
            problem = e.Message;
        }

        string radio = (plan?.Sideband ?? proposed.Sideband).ToLowerInvariant();
        bool fm = RfPlan.IsFmRadio(radio);
        bool upper = radio != "lsb";
        double? dial = fm ? null : plan?.DialHz ?? (problem is null ? proposed.DialFrequency : null);

        // The modems, as measured. A modem's audio centre is the plan's where there is one, else
        // what the file says, else the mode's own; a baseband mode has none and occupies from DC.
        var rows = new List<Row>();
        for (int i = 0; i < proposed.Modems.Count; i++)
        {
            ModemConfig m = proposed.Modems[i];
            PlannedModem? planned = plan?.Modems.FirstOrDefault(p => p.Slot.SubChannel == m.SubChannel);
            bool ardop = DaemonConfig.IsArdop(m.Mode);
            bool tunable = ardop || ModemCatalog.AcceptsCentreFrequency(m.Mode);
            double? fallback = ardop ? ArdopChannelBridge.NativeCentreHz : ModemCatalog.DefaultCentreFrequencyFor(m.Mode);
            bool baseband = !tunable && fallback is null;
            double width = planned?.Slot.BandwidthHz ?? BandPlanner.WidthOf(m, dspRate);
            double? centre = problem is not null || baseband
                ? null
                : planned is not null && !fm ? planned.AudioCentreHz : m.Frequency ?? fallback;

            double? rf = planned is not null && !fm ? planned.Slot.RfCentreHz
                : problem is not null ? asked[i]
                : dial is double d && centre is double c ? (upper ? d + c : d - c)
                : null;
            rows.Add(new Row(m, width, tunable, centre, rf, planned is null && m.TransmitOnlyUnderLease));
        }

        Passband? window = fm || problem is not null ? null : plan?.Window ?? AudioWindow(rows, proposed);

        var json = new JsonObject
        {
            ["ok"] = problem is null,
            ["radio"] = radio,
            // What the strip is drawn in: radio frequency where there is a dial (or modems asked
            // for one the plan could not build), else the audio band.
            ["axis"] = !fm && (dial is not null || (problem is not null && rows.Any(r => r.Rf is not null)))
                ? "rf" : "audio",
            ["dialHz"] = dial,
            ["hasDial"] = dial is not null,
            ["problem"] = problem,
            ["notes"] = new JsonArray([.. (plan?.Warnings ?? []).Select(w => (JsonNode)w)]),
        };

        if (window is Passband w)
        {
            double lo = w.LowHz, hi = w.HighHz;
            var pass = new JsonObject { ["audioLowHz"] = Round(lo), ["audioHighHz"] = Round(hi) };
            if (dial is double d)
            {
                pass["rfLowHz"] = Round(upper ? d + lo : d - hi);
                pass["rfHighHz"] = Round(upper ? d + hi : d - lo);
            }

            json["passband"] = pass;
        }
        else
        {
            json["passband"] = null;
        }

        var modems = new JsonArray();
        foreach (Row r in rows)
        {
            var o = new JsonObject
            {
                ["subChannel"] = r.Modem.SubChannel,
                ["mode"] = r.Modem.Mode,
                ["widthHz"] = Round(r.Width),
                ["tunable"] = r.Tunable,
                ["audioCentreHz"] = Round(r.Centre),
                ["audioLowHz"] = r.Centre is null && problem is not null ? null : Round(r.AudioLow),
                ["audioHighHz"] = r.Centre is null && problem is not null ? null : Round(r.AudioHigh),
                ["rfHz"] = Round(r.Rf),
                ["rfLowHz"] = Round(r.Rf - (r.Width / 2)),
                ["rfHighHz"] = Round(r.Rf + (r.Width / 2)),
            };
            if (r.Rf is null)
            {
                o["rfLowHz"] = null;
                o["rfHighHz"] = null;
            }

            if (r.LeaseOnly)
            {
                o["leaseOnly"] = true;
            }

            modems.Add(o);
        }

        json["modems"] = modems;
        json["warnings"] = Warnings(rows, window, dial, upper, problem is not null);
        return json;
    }

    /// <summary>
    /// The window to judge a station with no RF-addressed modem by: the nominal SSB passband,
    /// opened as far as a Flex lets it be when the modems need it, as <see cref="Passband.Fit"/>
    /// does for a plan.
    /// </summary>
    private static Passband AudioWindow(List<Row> rows, DaemonConfig proposed)
    {
        bool flex = proposed.Device.StartsWith("flex:", StringComparison.OrdinalIgnoreCase);
        var slots = rows
            .Where(r => r.Centre is not null && !r.LeaseOnly)
            .Select(r => new RfSlot(r.Modem.SubChannel, r.Modem.Mode, r.Centre!.Value, r.Width))
            .ToList();
        return Passband.Fit(slots, flex ? Passband.WideCeilingHz : Passband.Nominal.HighHz);
    }

    private static JsonArray Warnings(
        List<Row> rows, Passband? window, double? dial, bool upper, bool refused)
    {
        var warnings = new JsonArray();
        List<Row> judged = [.. rows.Where(r => !r.LeaseOnly)];

        // Which edge is which: on lower sideband the audio top is the RF bottom, and the words
        // are in the terms the operator sees the modem in.
        string Edge(bool audioTop) => dial is null || upper == audioTop ? "top" : "bottom";

        string Place(double audioCentre) =>
            dial is double d
                ? RfPlan.Mhz(upper ? d + audioCentre : d - audioCentre)
                : string.Create(CultureInfo.InvariantCulture, $"{audioCentre:0} Hz");

        if (window is Passband p && !refused)
        {
            foreach (Row r in judged.Where(r => r.Centre is not null))
            {
                double over = r.AudioHigh - p.HighHz;
                double under = p.LowHz - r.AudioLow;
                if (over <= ToleranceHz && under <= ToleranceHz)
                {
                    continue;
                }

                string text;
                if (r.Width > p.WidthHz + ToleranceHz)
                {
                    text = "It is wider than what the radio hears. Widen the radio's filter on the Radio and audio page.";
                }
                else
                {
                    bool high = over > ToleranceHz;
                    double outside = high ? over : under;
                    double target = high
                        ? Math.Floor((p.HighHz - (r.Width / 2)) / SuggestStepHz) * SuggestStepHz
                        : Math.Ceiling((p.LowHz + (r.Width / 2)) / SuggestStepHz) * SuggestStepHz;
                    text = string.Create(CultureInfo.InvariantCulture,
                        $"Its {Edge(high)} {Math.Ceiling(outside):0} Hz falls outside what the radio hears. Move it to {Place(target)}, or widen the radio's filter on the Radio and audio page.");
                }

                warnings.Add(new JsonObject { ["subChannel"] = r.Modem.SubChannel, ["kind"] = "outside", ["text"] = text });
            }
        }

        // Overlap is judged where the operator sees the modems: on the radio frequency where
        // there is one, else in audio.
        foreach (Row a in judged)
        {
            foreach (Row b in judged)
            {
                if (ReferenceEquals(a, b) || !Overlap(a, b, dial is not null || refused, out double by))
                {
                    continue;
                }

                warnings.Add(new JsonObject
                {
                    ["subChannel"] = a.Modem.SubChannel,
                    ["kind"] = "overlap",
                    ["other"] = b.Modem.SubChannel,
                    ["text"] = string.Create(CultureInfo.InvariantCulture,
                        $"It overlaps the modem on sub-channel {b.Modem.SubChannel} by {Math.Ceiling(by):0} Hz. Move one of them so each has its own part of the band."),
                });
            }
        }

        return warnings;
    }

    private static bool Overlap(Row a, Row b, bool inRf, out double by)
    {
        by = 0;
        double? aLow = inRf ? a.Rf - (a.Width / 2) : a.Centre is null ? null : a.AudioLow;
        double? bLow = inRf ? b.Rf - (b.Width / 2) : b.Centre is null ? null : b.AudioLow;
        if (aLow is not double al || bLow is not double bl)
        {
            return false;
        }

        double top = Math.Min(al + a.Width, bl + b.Width);
        double bottom = Math.Max(al, bl);
        by = top - bottom;
        return by > ToleranceHz;
    }

    private static double? Round(double? value) =>
        value is double v ? Math.Round(v, 3) : null;

    /// <summary>One modem as the answer describes it.</summary>
    private sealed record Row(
        ModemConfig Modem, double Width, bool Tunable, double? Centre, double? Rf, bool LeaseOnly)
    {
        /// <summary>The bottom of its audio band; DC for a baseband mode.</summary>
        internal double AudioLow => Centre is double c ? c - (Width / 2) : 0;

        internal double AudioHigh => Centre is double c ? c + (Width / 2) : Width;
    }
}
