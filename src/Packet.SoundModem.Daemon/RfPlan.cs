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
internal sealed record RfSlot(
    int SubChannel, string Mode, double RfCentreHz, double BandwidthHz, double? FixedCentreHz = null)
{
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
    /// filter clamps here (measured on a FLEX-6500, docs/flex-integration.md §10.2); the slice's
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
    /// Modems the plan places outside that window. Only ever produced for a dial the operator
    /// pinned: they chose it knowing their radio, and an unset window is a nominal figure the
    /// daemon cannot verify, so this informs rather than obstructs.
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

        double dial = pinnedDialHz ?? DialFixedBy(slots, upper) ?? Choose(slots, upper, passband);
        var planned = slots
            .Select(s => new PlannedModem(s, AudioFor(s.RfCentreHz, dial, upper)))
            .OrderBy(p => p.AudioCentreHz)
            .ToList();

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

        if (offenders.Count > 0 && pinnedDialHz is null)
        {
            throw new InvalidDataException(
                Explain(slots, offenders, dial, upper, pinnedDialHz, passband, dialFixer, planned));
        }

        List<string> warnings = offenders.Count == 0
            ? []
            : [Explain(slots, offenders, dial, upper, pinnedDialHz, passband, dialFixer, planned)];
        return new Result(dial, upper ? "usb" : "lsb", planned, passband, warnings);
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
        $"modem {p.Slot.SubChannel} ({p.Slot.Mode}) at {Mhz(p.Slot.RfCentreHz)} would sit at "
        + $"{p.AudioCentreHz:F0} Hz audio, occupying {p.AudioCentreHz - (p.Slot.BandwidthHz / 2):F0}"
        + $"-{p.AudioCentreHz + (p.Slot.BandwidthHz / 2):F0} Hz";

    private static string Explain(
        IReadOnlyList<RfSlot> slots, List<string> offenders, double dial, bool upper, double? pinned,
        Passband passband, RfSlot? dialFixer, IReadOnlyList<PlannedModem> planned)
    {
        double span = slots.Max(s => s.HighEdgeHz) - slots.Min(s => s.LowEdgeHz);
        double room = passband.WidthHz;
        var text = new System.Text.StringBuilder();

        if (pinned is not null)
        {
            text.AppendLine(
                $"with the dial pinned to {Mhz(dial)} {(upper ? "USB" : "LSB")}, these fall outside "
                + $"the nominal {passband.LowHz:F0}-{passband.HighHz:F0} Hz passband. That is only a "
                + "nominal figure - if your rig passes them, ignore this; omit \"dialFrequency\" "
                + "and the dial will be chosen to fit them:");
        }
        else if (span > room)
        {
            // No dial can help: the modems are simply spread wider than one passband. On a radio
            // whose filters the daemon opens, the window has already been widened as far as the
            // radio goes before this is reached, so the figure quoted is the real ceiling.
            string ceiling = passband.IsNominal
                ? $"the {room:F0} Hz a single SSB passband can carry"
                : $"the {room:F0} Hz this radio can be opened to pass in one go "
                  + $"({passband.LowHz:F0}-{passband.HighHz:F0} Hz, already widened for these modems)";
            text.AppendLine(
                $"these modems span {span:F0} Hz of RF ({Mhz(slots.Min(s => s.LowEdgeHz))} to "
                + $"{Mhz(slots.Max(s => s.HighEdgeHz))}), which is more than {ceiling}. No dial "
                + "frequency can place them all on air at once - split them across separate "
                + "radios, or move them closer together:");
        }
        else
        {
            text.AppendLine(
                $"no dial frequency places every modem inside the {passband.LowHz:F0}-{passband.HighHz:F0} Hz passband:");
        }

        foreach (string offender in offenders)
        {
            text.AppendLine($"  {offender}");
        }

        // A modem below the dial is not a passband problem at all - it is on the wrong side of the
        // carrier, which reads as a nonsensical negative audio frequency unless it is named. It
        // happens when a spec-fixed modem, which dictates the dial, is not the lowest in the plan.
        if (dialFixer is not null && planned.Any(p => p.AudioCentreHz < 0))
        {
            string below = string.Join(", ", planned
                .Where(p => p.AudioCentreHz < 0)
                .Select(p => $"modem {p.Slot.SubChannel} ({p.Slot.Mode}) at {Mhz(p.Slot.RfCentreHz)}"));
            text.AppendLine(
                $"  modem {dialFixer.SubChannel} ({dialFixer.Mode}) has its audio centre fixed at "
                + $"{dialFixer.FixedCentreHz:F0} Hz by its own standard, so it is what puts the dial "
                + $"at {Mhz(dial)} - and on {(upper ? "USB" : "LSB")} every other modem then has to "
                + $"sit {(upper ? "above" : "below")} that. {below} "
                + $"{(below.Contains(',') ? "are" : "is")} on the wrong side of it. Put the "
                + $"spec-fixed modem {(upper ? "lowest" : "highest")} in the plan.");
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
