namespace Packet.SoundModem.Daemon;

/// <summary>One modem's place in the band plan, in absolute RF terms.</summary>
/// <param name="SubChannel">The modem's KISS sub-channel, for naming it in messages.</param>
/// <param name="Mode">Its mode, likewise.</param>
/// <param name="RfCentreHz">Where on the band the operator wants it.</param>
/// <param name="BandwidthHz">
/// How much room it occupies. Measured from the modem itself where one exists; for ARDOP,
/// whose bandwidth is negotiated per session, the configured cap or the widest it can reach.
/// </param>
/// <param name="FixedCentreHz">
/// The audio centre this mode is pinned to by its own standard, or null where the centre is the
/// planner's to choose. A pinned one turns the modem around: it cannot be moved to suit a dial, so
/// it <i>is</i> the dial - the only one placing it on the RF frequency asked for. On a channel
/// radio every modem's centre is pinned, because there is no dial to trade it against: FM slots
/// carry the centre the modem will actually use here, and the planner moves none of them.
/// </param>
/// <param name="MinCentreHz">
/// The lowest audio centre the modem will build at, from the modem's own guard against DC (see
/// <c>ModemCatalog.AudioCentreRange</c>), or null where it has none. The planner never places the
/// modem below it.
/// </param>
/// <param name="MaxCentreHz">The highest audio centre the modem will build at, likewise.</param>
internal sealed record RfSlot(
    int SubChannel, string Mode, double RfCentreHz, double BandwidthHz, double? FixedCentreHz = null,
    double? MinCentreHz = null, double? MaxCentreHz = null)
{
    /// <summary>
    /// Whether the modem itself limits where its audio centre may be put (it refuses to build
    /// outside <see cref="MinCentreHz"/> to <see cref="MaxCentreHz"/>), as opposed to a modem that
    /// goes anywhere the rig passes.
    /// </summary>
    internal bool HasCentreRange => MinCentreHz is not null && MaxCentreHz is not null;

    internal double LowEdgeHz => RfCentreHz - (BandwidthHz / 2);

    internal double HighEdgeHz => RfCentreHz + (BandwidthHz / 2);
}

/// <summary>Where a modem ended up once the dial was chosen.</summary>
internal sealed record PlannedModem(RfSlot Slot, double AudioCentreHz);

/// <summary>
/// The audio window a plan has to fit inside - the usable part of the radio's passband.
/// </summary>
/// <param name="LowHz">Lower edge; no modem may sit below it.</param>
/// <param name="HighHz">Upper edge; no modem may reach above it.</param>
internal readonly record struct Passband(double LowHz, double HighHz)
{
    /// <summary>
    /// What an ordinary SSB rig passes. Nominal - the daemon cannot know an unknown rig's filter -
    /// but conservative enough that a plan fitting inside it works on ordinary gear.
    /// </summary>
    internal static Passband Nominal => new(300, 2700);

    /// <summary>
    /// The widest window a radio whose filters the daemon sets can be asked for. The transmit
    /// filter clamps here (measured on a FLEX-6500, docs/dev/archive/flex-integration.md §10.2); the slice's
    /// receive filter is asked for the same width and read back, since its own limit is unmeasured.
    /// </summary>
    internal const double WideCeilingHz = 10_000;

    /// <summary>Headroom kept either side of the modems when a window is widened to fit them.</summary>
    private const double GrowthMarginHz = 200;

    internal double WidthHz => HighHz - LowHz;

    internal bool IsNominal => this == Nominal;

    /// <summary>
    /// The window to plan <paramref name="slots"/> in, given how wide the radio can be opened.
    /// </summary>
    /// <remarks>
    /// Conventional first: anything that fits an ordinary SSB passband is planned in one, so a
    /// station that worked before this existed lands on exactly the same dial. Only a plan that
    /// cannot fit - a 3 kHz waveform, or modems deliberately spread apart - reaches wider, and
    /// then only as far as it has to, because every extra Hz of window is extra noise bandwidth on
    /// receive and a wider filter to open on transmit.
    /// </remarks>
    internal static Passband Fit(IReadOnlyList<RfSlot> slots, double ceilingHighHz)
    {
        Passband nominal = Nominal;
        if (slots.Count == 0 || ceilingHighHz <= nominal.HighHz)
        {
            return nominal;
        }

        double span = slots.Max(s => s.HighEdgeHz) - slots.Min(s => s.LowEdgeHz);
        return span <= nominal.WidthHz
            ? nominal
            : new Passband(nominal.LowHz, Math.Min(ceilingHighHz, nominal.LowHz + span + GrowthMarginHz));
    }
}

/// <summary>
/// Turns a set of absolute RF centres into a dial frequency and the audio centres that follow
/// from it - the arithmetic an operator would otherwise do by hand, and the check they would
/// otherwise do by eye.
/// </summary>
/// <remarks>
/// An SSB transceiver's dial is shared by everything in the passband, so the modems are not
/// independent: one dial has to place all of them inside the radio's transmit filter. Given
/// only the RF centres, the daemon can choose that dial - and choose it better than the
/// obvious round number, which tends to leave the lowest mode sitting on the filter's skirt.
/// </remarks>
internal static class RfPlan
{
    /// <summary>Dials are chosen on a round step; operators tune in whole hundreds of Hz.</summary>
    internal const double DialStepHz = 50.0;

    /// <param name="Window">
    /// The audio window the plan was fitted into - <see cref="Passband.Nominal"/> unless the modems
    /// needed more room than an ordinary SSB passband and the radio's filters are the daemon's to
    /// open. What the radio then has to be set to pass.
    /// </param>
    /// <param name="Warnings">
    /// What the operator should check on their radio: modems a pinned dial places outside the
    /// nominal passband, or modems too wide for it that the plan centred and stretched past both
    /// of its edges. Either way the window is a nominal figure the daemon cannot verify against
    /// an unknown rig, so this informs rather than obstructs.
    /// </param>
    internal sealed record Result(
        double DialHz,
        string Sideband,
        IReadOnlyList<PlannedModem> Modems,
        Passband Window,
        IReadOnlyList<string> Warnings)
    {
        internal bool IsUpperSideband => Sideband.Equals("usb", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Whether this is a channel radio rather than a sideband one. On FM there is no
        /// arithmetic to read out of the plan: <see cref="DialHz"/> is the channel every modem
        /// is on, not a carrier their audio is measured from.
        /// </summary>
        internal bool IsFm => IsFmRadio(Sideband);
    }

    /// <summary>The radio kinds a station can be set to, as a message lists them.</summary>
    internal const string RadioKinds = "\"usb\", \"lsb\" or \"fm\"";

    /// <summary>
    /// Whether a configured <c>sideband</c> names a channel radio. FM rides in the same field as
    /// the two sidebands because it answers the same question - what kind of radio is this, and
    /// does its audio add up to RF - and a second key would only be a second thing to contradict
    /// the first with.
    /// </summary>
    internal static bool IsFmRadio(string? sideband) =>
        sideband is not null && sideband.Equals("fm", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The radio kind a FlexRadio slice mode states, or null where the mode says nothing about
    /// one. The slice knows which it is, so on a headless Flex this is read rather than
    /// configured: DIGU and USB are upper sideband, DIGL and LSB lower, and FM and NFM are a
    /// channel radio, whose audio has no RF offset at all.
    /// </summary>
    internal static string? SidebandForSliceMode(string sliceMode) =>
        sliceMode.ToUpperInvariant() switch
        {
            "DIGU" or "USB" => "usb",
            "DIGL" or "LSB" => "lsb",
            "FM" or "NFM" => "fm",
            _ => null,
        };

    /// <summary>Audio offset of an RF frequency for a given dial and sideband.</summary>
    private static double AudioFor(double rfHz, double dialHz, bool upper) =>
        upper ? rfHz - dialHz : dialHz - rfHz;

    /// <summary>
    /// Chooses a dial for <paramref name="slots"/>, or explains why none exists.
    /// </summary>
    /// <param name="pinnedDialHz">
    /// A dial the operator has fixed - a net frequency, or matching another application. When
    /// given it is used as-is and merely checked, rather than chosen.
    /// </param>
    /// <param name="window">
    /// The audio window to fit them into. Null takes <see cref="Passband.Nominal"/> - what an
    /// ordinary rig passes; callers whose radio they can open pass <see cref="Passband.Fit"/>.
    /// </param>
    internal static Result Solve(
        IReadOnlyList<RfSlot> slots, string sideband, double? pinnedDialHz = null,
        Passband? window = null)
    {
        Passband passband = window ?? Passband.Nominal;
        ArgumentNullException.ThrowIfNull(slots);
        if (slots.Count == 0)
        {
            throw new ArgumentException("no RF-addressed modems to plan", nameof(slots));
        }

        // A channel radio is answered here and goes no further: there is no dial to choose and
        // no offset to work out, so none of the machinery below applies to one.
        if (IsFmRadio(sideband))
        {
            return OnChannel(slots, pinnedDialHz, passband);
        }

        bool upper = sideband.Equals("usb", StringComparison.OrdinalIgnoreCase);
        if (!upper && !sideband.Equals("lsb", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"\"sideband\": \"{sideband}\" is not a radio kind this can plan for. "
                + $"Use {RadioKinds} - FM being a channel radio, whose audio is not an offset "
                + "from a dial at all.");
        }

        (double dialLow, double dialHigh)? limits = ModemDialLimits(slots, upper);
        double dial = pinnedDialHz ?? DialFixedBy(slots, upper) ?? Choose(slots, upper, passband);

        var planned = slots
            .Select(s => new PlannedModem(s, AudioFor(s.RfCentreHz, dial, upper)))
            .OrderBy(p => p.AudioCentreHz)
            .ToList();

        // A modem that cannot be built at the centre the plan gives it is said so here, plainly,
        // rather than left to the modem's own exception. Centring on 1500 Hz is never moved to
        // suit one: a modem that tolerates its edges being cut is simply allowed to be cut.
        if (limits is not null)
        {
            RefuseIfModemCannotBuild(slots, dial, upper, limits, pinnedDialHz is not null);
        }

        List<string> offenders = [.. planned
            .Where(p => Outside(p, passband))
            .Select(p => Describe(p, dial, upper))];

        // A dial the daemon chose and that still does not fit means no dial fits, which is a
        // refusal. A dial the operator pinned is their call against a rig they can see and a
        // passband figure that is only nominal, so it is a warning.
        // Which modem, if any, left no choice about the dial - needed to explain a failure, since
        // "no dial fits" reads very differently when one of the modems chose the dial.
        RfSlot? dialFixer = pinnedDialHz is null
            ? slots.FirstOrDefault(s => s.FixedCentreHz is not null)
            : null;

        // One exception to that: an ensemble wider than an ordinary passband, on a rig whose
        // filters are not ours to open. The 2400 Hz figure is nominal, and most SSB rigs pass
        // something nearer 100-2900 Hz, so a 3 kHz waveform such as MS110D's is placed centred
        // and the operator is told what their filters have to pass, rather than refused for a
        // limit their rig may well not have.
        if (offenders.Count > 0 && pinnedDialHz is null && dialFixer is null && passband.IsNominal
            && Stretched(slots, upper, passband, limits) is { } stretched)
        {
            return stretched;
        }

        if (offenders.Count > 0 && pinnedDialHz is null)
        {
            throw new InvalidDataException(
                Explain(slots, offenders, dial, upper, pinnedDialHz, passband, dialFixer, planned));
        }

        List<string> warnings = offenders.Count == 0
            ? []
            : [Explain(slots, offenders, dial, upper, pinnedDialHz, passband, dialFixer, planned)];

        // A pinned dial under modems wider than the nominal passband: the window still has to
        // say how much the radio must pass, so a rig is asked for the width the modems need,
        // the same width an unpinned plan would ask for, rather than the nominal 2400 Hz.
        Passband planWindow = passband;
        if (pinnedDialHz is not null && passband.IsNominal && Span(slots) > passband.WidthHz)
        {
            planWindow = new Passband(
                Math.Max(0, planned.Min(p => p.AudioCentreHz - (p.Slot.BandwidthHz / 2))),
                planned.Max(p => p.AudioCentreHz + (p.Slot.BandwidthHz / 2)));
        }

        return new Result(dial, upper ? "usb" : "lsb", planned, planWindow, warnings);
    }

    /// <summary>
    /// The plan for a channel radio, where there is nothing to solve. An FM receiver's
    /// demodulated audio is not offset from a carrier - a 1700 Hz tone is 1700 Hz of audio on
    /// whatever channel the set is on, not "channel + 1.7 kHz" - so the channel is the RF of
    /// every modem, each keeps the audio centre it was given, and the plan is only a statement of
    /// which channel they all share.
    /// </summary>
    /// <param name="pinnedChannelHz">
    /// The channel from <c>dialFrequency</c>, where the operator wrote one. On FM that and a
    /// modem's <c>rfFrequency</c> are the same figure, so they have to agree.
    /// </param>
    /// <param name="passband">
    /// Carried through unchanged: what the radio's audio path passes is a question about the
    /// audio path, and being on FM does not answer it differently.
    /// </param>
    private static Result OnChannel(
        IReadOnlyList<RfSlot> slots, double? pinnedChannelHz, Passband passband)
    {
        double asked = slots[0].RfCentreHz;
        if (slots.Any(s => Math.Abs(s.RfCentreHz - asked) > 0.5))
        {
            string channels = string.Join(
                ", ", slots.Select(s => s.RfCentreHz).Distinct().Order().Select(Mhz));
            throw new InvalidDataException(
                "on FM every modem is on the one channel the radio is set to, and these ask "
                + $"for {channels}. Modems that share a channel share a radio; give the others "
                + "a radio of their own.");
        }

        if (pinnedChannelHz is double pinned && Math.Abs(pinned - asked) > 0.5)
        {
            throw new InvalidDataException(
                $"\"dialFrequency\" is {Mhz(pinned)} and the modems' \"rfFrequency\" is "
                + $"{Mhz(asked)}. On FM those are the same thing said twice - the channel - and "
                + "a radio is on one channel at a time. Keep whichever you meant.");
        }

        // The audio centres are not the planner's to set here, so they are reported as they
        // stand: a modem with a centre of its own keeps it, and a baseband mode (c4fsk over FM is
        // the ordinary case) has none to keep.
        return new Result(
            pinnedChannelHz ?? asked,
            "fm",
            [.. slots.Select(s => new PlannedModem(s, s.FixedCentreHz ?? 0))],
            passband,
            []);
    }

    /// <summary>
    /// The dial a spec-fixed modem leaves no choice about, or null when every modem can be moved.
    /// </summary>
    /// <remarks>
    /// A mode whose audio centre is truly pinned cannot be slid up or down to suit a dial the
    /// planner picked for everything else: only one dial puts it on the RF frequency it was
    /// asked for, so it dictates rather than follows, and the movable modems are then placed
    /// around it. No shipped catalogue mode is pinned any longer - <c>ms110d-*</c> and
    /// <c>freedv-*</c>, the modes this was built for, became movable when
    /// <c>FrequencyShiftedModem</c> let them be translated off their spec centres - but the
    /// machinery stays: it is what a genuinely unmovable future mode needs, and
    /// <c>RfPlanTests</c> keeps it honest against hand-built pinned slots.
    /// </remarks>
    /// <exception cref="InvalidDataException">
    /// Two fixed-centre modems want different dials, which no single radio can satisfy.
    /// </exception>
    private static double? DialFixedBy(IReadOnlyList<RfSlot> slots, bool upper)
    {
        var pinned = slots.Where(s => s.FixedCentreHz is not null).ToList();
        if (pinned.Count == 0)
        {
            return null;
        }

        double DialFor(RfSlot s) => upper
            ? s.RfCentreHz - s.FixedCentreHz!.Value
            : s.RfCentreHz + s.FixedCentreHz!.Value;

        double dial = DialFor(pinned[0]);
        RfSlot? disagrees = pinned.Skip(1).FirstOrDefault(s => Math.Abs(DialFor(s) - dial) > 0.5);
        if (disagrees is not null)
        {
            throw new InvalidDataException(
                $"modem {pinned[0].SubChannel} ({pinned[0].Mode}) and modem {disagrees.SubChannel} "
                + $"({disagrees.Mode}) both have an audio centre fixed by their own standard, and "
                + $"the RF frequencies asked for would need the dial at {Mhz(dial)} and "
                + $"{Mhz(DialFor(disagrees))} at the same time. Two spec-fixed modes can share a "
                + "dial only where their RF frequencies differ by exactly the difference of their "
                + "audio centres - move one, or give it a channel of its own.");
        }

        return dial;
    }

    /// <summary>
    /// The plan for modems spread wider than the nominal passband, centred on it and stretched
    /// past both of its edges as far as the audio band allows, or null when even that is not
    /// enough: centred on 1500 Hz, an ensemble wider than 3000 Hz would need audio below 0 Hz.
    /// </summary>
    /// <remarks>
    /// The window the result carries is the stretched one, so whatever sets a rig's filter from
    /// the plan asks for the width the modems actually need.
    /// </remarks>
    private static Result? Stretched(
        IReadOnlyList<RfSlot> slots, bool upper, Passband passband,
        (double dialLow, double dialHigh)? limits)
    {
        // Refused on the span itself, before any dial is tried: rounding a dial moves every modem
        // together, so it can make a too-wide ensemble look as if its bottom edge fits.
        double ceilingHz = passband.LowHz + passband.HighHz;
        if (Span(slots) > ceilingHz + 0.5)
        {
            return null;
        }

        double lowEdge = slots.Min(s => s.LowEdgeHz);
        double highEdge = slots.Max(s => s.HighEdgeHz);
        double ensembleCentre = (lowEdge + highEdge) / 2;
        double passbandCentre = (passband.LowHz + passband.HighHz) / 2;
        double exact = upper ? ensembleCentre - passbandCentre : ensembleCentre + passbandCentre;
        double rounded = Math.Round(exact / DialStepHz) * DialStepHz;

        List<PlannedModem> At(double dial) => [.. slots
            .Select(s => new PlannedModem(s, AudioFor(s.RfCentreHz, dial, upper)))
            .OrderBy(p => p.AudioCentreHz)];
        static double Bottom(List<PlannedModem> planned) =>
            planned.Min(p => p.AudioCentreHz - (p.Slot.BandwidthHz / 2));
        static double Top(List<PlannedModem> planned) =>
            planned.Max(p => p.AudioCentreHz + (p.Slot.BandwidthHz / 2));

        // As Choose does: the round dial only when it still keeps both edges inside the audio band.
        List<PlannedModem> atRounded = At(rounded);
        double dial = Bottom(atRounded) >= 0 && Top(atRounded) <= ceilingHz ? rounded : exact;
        if (limits is not null)
        {
            RefuseIfModemCannotBuild(slots, dial, upper, limits, pinned: false);
        }

        List<PlannedModem> modems = At(dial);
        double low = Math.Max(0, Bottom(modems));
        double high = Math.Min(ceilingHz, Top(modems));
        return new Result(dial, upper ? "usb" : "lsb", modems, new Passband(low, high), []);
    }

    /// <summary>
    /// The dials every modem with a limit of its own will build at, or null when none has one. A
    /// modem's audio centre is the RF offset from the dial, so a range of centres is a range of
    /// dials: <c>rf - centre</c> on USB, <c>rf + centre</c> on LSB.
    /// </summary>
    private static (double dialLow, double dialHigh)? ModemDialLimits(IReadOnlyList<RfSlot> slots, bool upper)
    {
        var ranged = slots.Where(s => s.HasCentreRange).ToList();
        if (ranged.Count == 0)
        {
            return null;
        }

        double low = ranged.Max(s => DialRange(s, upper).low);
        double high = ranged.Min(s => DialRange(s, upper).high);
        return (low, high);
    }

    private static (double low, double high) DialRange(RfSlot s, bool upper) => upper
        ? (s.RfCentreHz - s.MaxCentreHz!.Value, s.RfCentreHz - s.MinCentreHz!.Value)
        : (s.RfCentreHz + s.MinCentreHz!.Value, s.RfCentreHz + s.MaxCentreHz!.Value);

    /// <summary>
    /// <paramref name="preferred"/> if every modem builds there, else the nearest dial where they all
    /// do: on the 50 Hz step when one lies inside the limits, else the exact limit.
    /// </summary>
    private static double FitToModems(double preferred, (double dialLow, double dialHigh)? limits)
    {
        if (limits is not { } l || (preferred >= l.dialLow && preferred <= l.dialHigh))
        {
            return preferred;
        }

        if (preferred < l.dialLow)
        {
            double step = Math.Ceiling(l.dialLow / DialStepHz) * DialStepHz;
            return step <= l.dialHigh ? step : l.dialLow;
        }

        double down = Math.Floor(l.dialHigh / DialStepHz) * DialStepHz;
        return down >= l.dialLow ? down : l.dialHigh;
    }

    private static string NoCommonDial(IReadOnlyList<RfSlot> slots, bool upper) =>
        "no dial suits every modem; move them closer together. "
        + string.Join("; ", slots.Where(s => s.HasCentreRange).Select(s =>
        {
            (double low, double high) = DialRange(s, upper);
            return $"modem {s.SubChannel} ({s.Mode}) needs {Mhz(low)} to {Mhz(high)}";
        }));

    /// <summary>
    /// A dial the operator pinned, or one a spec-fixed mode dictates, cannot be moved; where it
    /// puts a modem outside what that modem builds at, say so here with the dial that would work
    /// rather than leaving the modem to throw its own exception.
    /// </summary>
    private static void RefuseIfModemCannotBuild(
        IReadOnlyList<RfSlot> slots, double dial, bool upper,
        (double dialLow, double dialHigh)? limits, bool pinned)
    {
        List<string> bad = [.. slots
            .Where(s => s.HasCentreRange)
            .Select(s => (Slot: s, Audio: AudioFor(s.RfCentreHz, dial, upper)))
            .Where(x => x.Audio < x.Slot.MinCentreHz!.Value - 0.5 || x.Audio > x.Slot.MaxCentreHz!.Value + 0.5)
            .Select(x => $"{(pinned ? "dialFrequency " : "dial ")}{Mhz(dial)} {(upper ? "USB" : "LSB")} puts "
                + $"modem {x.Slot.SubChannel} ({x.Slot.Mode}) at {x.Audio:F0} Hz audio; it needs "
                + $"{x.Slot.MinCentreHz!.Value:F0}-{x.Slot.MaxCentreHz!.Value:F0} Hz")];
        if (bad.Count == 0)
        {
            return;
        }

        (double dialLow, double dialHigh) = limits!.Value;
        string remedy = dialLow > dialHigh + 0.5
            ? NoCommonDial(slots, upper)
            : $"use {Mhz(FitToModems(dial, limits))}";
        throw new InvalidDataException(string.Join(Environment.NewLine, bad) + "; " + remedy);
    }

    private static bool Outside(PlannedModem p, Passband passband)
    {
        double low = p.AudioCentreHz - (p.Slot.BandwidthHz / 2);
        double high = p.AudioCentreHz + (p.Slot.BandwidthHz / 2);
        return low < passband.LowHz || high > passband.HighHz;
    }

    /// <summary>
    /// Centres the whole ensemble in the passband, rather than jamming its lowest member
    /// against the filter skirt - which is what the obvious round dial tends to do.
    /// </summary>
    private static double Choose(IReadOnlyList<RfSlot> slots, bool upper, Passband passband)
    {
        double lowEdge = slots.Min(s => s.LowEdgeHz);
        double highEdge = slots.Max(s => s.HighEdgeHz);
        double ensembleCentre = (lowEdge + highEdge) / 2;
        double passbandCentre = (passband.LowHz + passband.HighHz) / 2;

        // Put the ensemble's centre at the passband's centre, then round to a dial an operator
        // can actually set. Rounding moves every modem together, so it cannot reorder them.
        double exact = upper ? ensembleCentre - passbandCentre : ensembleCentre + passbandCentre;
        double rounded = Math.Round(exact / DialStepHz) * DialStepHz;

        // Rounding can push a tight plan over an edge; keep it only when it still fits.
        var atRounded = slots.Select(s => new PlannedModem(s, AudioFor(s.RfCentreHz, rounded, upper)));
        return atRounded.Any(p => Outside(p, passband)) ? exact : rounded;
    }

    private static string Describe(PlannedModem p, double dial, bool upper) =>
        $"modem {p.Slot.SubChannel} ({p.Slot.Mode}) at {Mhz(p.Slot.RfCentreHz)} would be "
        + $"{p.AudioCentreHz:F0} Hz audio ("
        + AudioRange(p.AudioCentreHz - (p.Slot.BandwidthHz / 2), p.AudioCentreHz + (p.Slot.BandwidthHz / 2)) + ")";

    /// <summary>
    /// An audio range as every band plan message prints it, rounded outwards to the whole Hz so
    /// the figure an operator sets a filter to never cuts the signal it was quoted for.
    /// </summary>
    internal static string AudioRange(double lowHz, double highHz) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{Math.Floor(lowHz):F0}-{Math.Ceiling(highHz):F0} Hz");

    private static double Span(IReadOnlyList<RfSlot> slots) =>
        slots.Max(s => s.HighEdgeHz) - slots.Min(s => s.LowEdgeHz);

    private static string Explain(
        IReadOnlyList<RfSlot> slots, List<string> offenders, double dial, bool upper, double? pinned,
        Passband passband, RfSlot? dialFixer, IReadOnlyList<PlannedModem> planned)
    {
        double span = Span(slots);
        var text = new System.Text.StringBuilder();
        string nominal = $"{passband.LowHz:F0}-{passband.HighHz:F0} Hz";

        if (pinned is not null)
        {
            text.Append(
                $"dialFrequency {Mhz(dial)} {(upper ? "USB" : "LSB")} puts these outside the "
                + $"nominal {nominal}:");
        }
        else if (span > passband.WidthHz)
        {
            double ceiling = passband.IsNominal && span > passband.LowHz + passband.HighHz
                ? passband.LowHz + passband.HighHz
                : passband.WidthHz;
            text.Append(
                $"modems span {span:F0} Hz of RF, more than the {ceiling:F0} Hz one radio can "
                + "carry; split them across radios or move them closer together:");
        }
        else
        {
            text.Append($"no dial puts every modem inside {nominal}:");
        }

        foreach (string offender in offenders)
        {
            text.AppendLine().Append($"  {offender}");
        }

        // A modem below the dial is on the wrong side of the carrier, which reads as a negative
        // audio frequency; it happens when a spec-fixed modem dictates the dial and is not the
        // lowest (USB) or highest (LSB) in the plan.
        if (dialFixer is not null && planned.Any(p => p.AudioCentreHz < 0))
        {
            text.AppendLine().Append(
                $"  modem {dialFixer.SubChannel} ({dialFixer.Mode}) fixes the dial at {Mhz(dial)}; "
                + $"put it {(upper ? "lowest" : "highest")} in the plan");
        }

        return text.ToString();
    }

    /// <summary>Frequencies read back to an operator the way one is dialled: MHz to the Hz.</summary>
    internal static string Mhz(double hz) =>
        (hz / 1_000_000).ToString("F6", System.Globalization.CultureInfo.InvariantCulture) + " MHz";
}
