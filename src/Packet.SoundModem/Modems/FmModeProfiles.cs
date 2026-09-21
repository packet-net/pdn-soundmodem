namespace Packet.SoundModem.Modems;

/// <summary>
/// Which catalogue modes reach the air as frequency modulation, and what each needs from the link.
/// </summary>
/// <remarks>
/// <para><b>One table, because there were three.</b> The target deviations lived in
/// <c>FmModeCatalog</c> as a dictionary of exact mode names and again in <c>SimChannel</c> as a
/// switch on prefixes, and the channel spacings lived in a third switch beside the second. Three
/// copies of one fact drift, and the ladder and the simulator disagreeing about how hard a mode is
/// driven would be invisible in both.</para>
/// <para>Figures are Nino's, from the NinoTNC firmware release notes and his published modulator
/// tables. Where a figure is derived or assumed the profile says so rather than presenting it at
/// the same confidence.</para>
/// </remarks>
public static class FmModeProfiles
{
    private const double Narrow = 12500;
    private const double Wide = 25000;

    private static readonly Dictionary<string, FmModeProfile> _profiles =
        new(StringComparer.Ordinal)
        {
            ["afsk1200"] = new(
                TargetPeakDeviationHz: 3000, BesselNullToneHz: 1248,
                ChannelSpacingHz: Narrow, OccupiedBandwidthHz: 10000,
                Source: FmFigureSource.NinoTnc,
                Remark: "Bell 202 tones, 1200 and 2200 Hz, into the FM modulator; the deviation is "
                    + "a level applied to that audio rather than the modulation itself. Carson at "
                    + "3 kHz deviation and 2.2 kHz audio is 10.4 kHz, which is Nino's own 10 kHz "
                    + "occupied bandwidth bucket and fits 12.5 kHz spacing."),
            ["fsk9600"] = new(
                TargetPeakDeviationHz: 2400, BesselNullToneHz: 999,
                ChannelSpacingHz: Wide, OccupiedBandwidthHz: 20000,
                Source: FmFigureSource.NinoTnc,
                Remark: "Deviation is baud/4, which is minimum shift keying, and chosen rather than "
                    + "conceded: \"The tones selected in 4800 and 9600 mode provide Minimum Shift "
                    + "Keying, but more deviation may work well too.\" So 48 % of full deviation "
                    + "here is a modulation index, not wasted headroom."),
            ["fsk4800"] = new(
                TargetPeakDeviationHz: 1200, BesselNullToneHz: 500,
                ChannelSpacingHz: Narrow, OccupiedBandwidthHz: 10000,
                Source: FmFigureSource.NinoTnc,
                Remark: "Minimum shift keying, as fsk9600: deviation is baud/4."),
            ["c4fsk9600"] = new(
                TargetPeakDeviationHz: 2500, BesselNullToneHz: 1039,
                ChannelSpacingHz: Narrow, OccupiedBandwidthHz: 10000,
                Source: FmFigureSource.NinoTnc,
                Remark: "Four-level FSK, so the figure is the OUTER deviation. The tuning tone was "
                    + "raised to 1039 Hz in 3/4.42 to set 2.5 kHz outer deviation."),
            ["c4fsk19200"] = new(
                TargetPeakDeviationHz: 5000, BesselNullToneHz: 2079,
                ChannelSpacingHz: Wide, OccupiedBandwidthHz: 20000,
                Source: FmFigureSource.NinoTnc,
                Remark: "Outer deviation, as c4fsk9600. Note that not many transmitters reach "
                    + "5.0 kHz; Nino: \"don't worry too much\"."),
            ["qpsk3600"] = new(
                TargetPeakDeviationHz: 5000, BesselNullToneHz: 2079,
                ChannelSpacingHz: Narrow, OccupiedBandwidthHz: null,
                Source: FmFigureSource.NinoTnc,
                RecommendedPeakDeviationHz: 2500,
                RecommendationRemark:
                    "Nino publishes 5.0 kHz, which is 200 % of full deviation for the 12.5 kHz "
                    + "channel he puts this mode on, and Carson bandwidth 15.4 kHz on a 12.5 kHz "
                    + "channel. Measured, 2500 Hz is +0.4 dB at the 50 % knee and takes Carson to "
                    + "10.4 kHz, so it is free to slightly better AND stops the mode over-running "
                    + "its channel. The published figure is also the one already doubted on "
                    + "provenance, apparently inherited from the c4fsk19200 row this mode was "
                    + "wrongly grouped with; a station running 1.2 kHz reports it working, which "
                    + "our ladder puts 3.8 dB worse than 2500 Hz. Consequence for anyone following "
                    + "the documented procedure: the Bessel null tone becomes 1039 Hz, not 2079. "
                    + "Ask Nino before treating this as settled.",
                Remark: "Phase modulation of a 1650 Hz carrier at 1800 symbols a second, grouped by "
                    + "Nino with the FM speaker/mic modes. It SHARES c4fsk19200's 2079 Hz tuning "
                    + "tone, which is a tuning tone and not a carrier - reading that as a carrier "
                    + "produced a false alarm that our 1650 Hz was incompatible with v44 firmware. "
                    + "The release notes settle it: \"0101 3600 AQPSK IL2Pc 1800 sym/sec on 1650Hz "
                    + "carrier\". Its 5 kHz target is 200 % of full deviation for a 12.5 kHz "
                    + "channel, which is odd for a narrow-channel mode and is the one figure here "
                    + "worth asking Nino about; a station running 1.2 kHz reports it working "
                    + "fine."),
        };

    /// <summary>Every FM-native mode's profile, keyed by catalogue mode name.</summary>
    /// <remarks>
    /// Keyed on the base mode, so <c>afsk1200-il2p</c> and its FEC and framing variants all resolve
    /// to <c>afsk1200</c>: framing does not change what the modulator does.
    /// </remarks>
    public static IReadOnlyDictionary<string, FmModeProfile> Profiles => _profiles;

    /// <summary>
    /// The published figures for a mode, or null if there are none.
    /// </summary>
    /// <remarks>
    /// Null does NOT mean "not an FM mode": see <see cref="FmModesWithoutDeviationTarget"/>, whose
    /// members are frequency modulation and have nothing to put in a row here. Ask
    /// <see cref="IsFmMode"/> for the modulation and this for the numbers.
    /// </remarks>
    public static FmModeProfile? For(string? mode)
    {
        if (mode is null)
        {
            return null;
        }

        if (_profiles.TryGetValue(mode, out FmModeProfile? exact))
        {
            return exact;
        }

        // Variants suffix the base mode: afsk1200-il2p, fsk9600-il2p, afsk1200-fx25rx.
        foreach ((string name, FmModeProfile profile) in _profiles)
        {
            if (mode.StartsWith(name, StringComparison.Ordinal)
                && (mode.Length == name.Length || mode[name.Length] == '-'))
            {
                return profile;
            }
        }

        return null;
    }

    /// <summary>
    /// FM-native modes that have no deviation target to publish, because theirs is a drive
    /// decision rather than a property of the waveform.
    /// </summary>
    /// <remarks>
    /// <para>Prefix-matched on a dash, exactly as <see cref="For"/> matches the framing variants,
    /// so <c>ofdm-fm-8k</c> and every other preset resolve from the one entry.</para>
    /// <para><b>Why OFDM-FM cannot have a row above.</b> Every figure in that table is a peak
    /// deviation a NinoTNC modulator is set to, and a tuning tone that sets it. OFDM-FM has
    /// neither. Its peak is a property of the CONSTELLATION and not of the drive: at one bench
    /// drive, QPSK and QAM-64 bursts differ by 4.6 dB of peak, which is the whole of the headroom
    /// between "inside the class limit" and "over it"
    /// (<c>docs/dev/ofdm-fm/geometry-signalling.md</c> section 5.2). So the number an operator
    /// sets depends on the densest constellation the station will ever send, and writing a single
    /// target here would be inventing one. The same page measures this bench at 4.12 kHz peak on
    /// one station and 4.04 on the other, 82 % of the 5 kHz class, which is a measurement of two
    /// radios rather than a recommendation to anybody.</para>
    /// </remarks>
    private static readonly string[] _fmWithoutDeviationTarget = ["ofdm-fm"];

    /// <summary>
    /// Whether a mode reaches the air as frequency modulation.
    /// </summary>
    /// <remarks>
    /// <para><b>This is not the same question as "does this mode have a deviation figure", and
    /// keying one off the other was a real defect.</b> The table above is Nino's published
    /// modulator figures, so it answers the second question; <c>ofdm-fm</c> is frequency
    /// modulation by construction, has no such figure, and therefore used to report that it was
    /// not an FM mode at all. Anything selecting carrier-sense behaviour on "is this FM" would
    /// have excluded precisely the mode family that proved the FM carrier-sense argument
    /// (packet-net/pdn-soundmodem#522).</para>
    /// <para><b>Nor is it "what can arrive through an FM receiver"</b>, which is a third question
    /// and the one a decoder sweep asks. The shaped-PSK modes ride an FM link perfectly well and
    /// are correctly absent here; <c>Sweep.PacketModes</c> in the multi-decode tool records the
    /// corpus that settled it.</para>
    /// <para>Ask <see cref="HasDeviationTarget"/> when what is wanted is a number to set a
    /// transmitter to.</para>
    /// </remarks>
    public static bool IsFmMode(string? mode) =>
        For(mode) is not null || HasNoDeviationTarget(mode);

    /// <summary>
    /// Whether a published peak deviation exists for this mode, which is what a transmitter
    /// calibration or an FM test ladder needs. A subset of <see cref="IsFmMode"/>.
    /// </summary>
    public static bool HasDeviationTarget(string? mode) => For(mode) is not null;

    /// <summary>The FM modes whose deviation is a drive decision; see
    /// <see cref="_fmWithoutDeviationTarget"/> for why they have no row.</summary>
    public static IReadOnlyCollection<string> FmModesWithoutDeviationTarget =>
        _fmWithoutDeviationTarget;

    private static bool HasNoDeviationTarget(string? mode)
    {
        if (mode is null)
        {
            return false;
        }

        foreach (string name in _fmWithoutDeviationTarget)
        {
            if (mode.StartsWith(name, StringComparison.Ordinal)
                && (mode.Length == name.Length || mode[name.Length] == '-'))
            {
                return true;
            }
        }

        return false;
    }
}
