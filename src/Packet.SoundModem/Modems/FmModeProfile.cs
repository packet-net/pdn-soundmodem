namespace Packet.SoundModem.Modems;

/// <summary>
/// How confident we are in a figure, because on this platform that varies and matters.
/// </summary>
public enum FmFigureSource
{
    /// <summary>Stated in NinoTNC firmware release notes or Nino's own published tables.</summary>
    NinoTnc,

    /// <summary>Computed from a stated figure, with the arithmetic recorded in the remark.</summary>
    Derived,

    /// <summary>Not published anywhere found; the value is an assumption and is marked as one.
    /// </summary>
    Assumed,
}

/// <summary>
/// What a mode needs from an FM link: how far to deviate, how to set that, and how much of the
/// channel it takes.
/// </summary>
/// <param name="TargetPeakDeviationHz">The mode's recommended peak deviation.
/// <para><b>A recommendation, not a requirement.</b> Nino, #ninotnc-help 11 July 2026: "The target
/// deviations are just recommendations. The modes will work at a range of deviations, though there
/// are reasons to be specific sometimes. Mostly if you are trying to constrain occupied bandwidth
/// of the RF signal. But generally, more deviation will be more effective until you exceed the
/// capability of the transmitter or receiver (IF filter)." The release notes have agreed since
/// 2.46 in 2020: "3kHz deviation should work for most setups", and "2400 Baud mode does not REQUIRE
/// 5kHz deviation (3kHz is fine)".</para></param>
/// <param name="BesselNullToneHz">The audio tone a NinoTNC emits so deviation can be set by a
/// Bessel null, where the carrier vanishes at a modulation index of 2.405.
/// <para><b>This is not a carrier and not a subcarrier.</b> Conflating the two caused a false
/// interop alarm: Nino's v44 tone table groups C4FSK 19200 and QPSK 3600 on one row at 2079 Hz,
/// which reads as a claim that QPSK 3600's carrier moved from 1650 Hz. It did not. See
/// <see cref="DeviationFromTone"/>, which reproduces every published pair to three decimal
/// places.</para></param>
/// <param name="ChannelSpacingHz">The channel this mode belongs on. A property of the mode, not a
/// preference: it follows from the occupied bandwidth.</param>
/// <param name="OccupiedBandwidthHz">Nino's own occupied bandwidth figure, where he has published
/// one.</param>
/// <param name="Source">Where <see cref="TargetPeakDeviationHz"/> came from.</param>
/// <param name="Remark">Anything a reader would otherwise get wrong.</param>
public sealed record FmModeProfile(
    double TargetPeakDeviationHz,
    double? BesselNullToneHz,
    double ChannelSpacingHz,
    double? OccupiedBandwidthHz,
    FmFigureSource Source,
    string Remark)
{
    /// <summary>The modulation index at which a tone-modulated carrier's own component vanishes,
    /// which is the first zero of the Bessel function of the first kind, order zero.</summary>
    public const double FirstBesselNull = 2.405;

    /// <summary>The deviation a Bessel null tone sets.</summary>
    /// <remarks>
    /// Reproduces every figure Nino publishes: 1248 Hz gives 3.001 kHz, 999 gives 2.403, 500 gives
    /// 1.202, 1039 gives 2.499 and 2079 gives 5.000, against stated targets of 3.0, 2.4, 1.2, 2.5
    /// and 5.0 kHz. That agreement is what identifies the column as tuning tones rather than
    /// carriers.
    /// </remarks>
    public static double DeviationFromTone(double toneHz) => toneHz * FirstBesselNull;

    /// <summary>The tone that sets a wanted deviation, for calibrating a transmitter with nothing
    /// more than a receiver that can show a carrier.</summary>
    public static double ToneForDeviation(double deviationHz) => deviationHz / FirstBesselNull;

    /// <summary>Carson bandwidth: twice the sum of peak deviation and the highest modulating
    /// frequency. A rule of thumb for where a signal stops fitting a channel, and generous, since
    /// it counts tails carrying very little power.</summary>
    public static double CarsonBandwidthHz(double peakDeviationHz, double highestAudioHz) =>
        2 * (peakDeviationHz + highestAudioHz);

    /// <summary>This mode's deviation as a percentage of 100 % modulation for its channel, where
    /// 100 % is 2.5 kHz on 12.5 kHz spacing, 4 kHz on 20 kHz and 5 kHz on 25 kHz (Tait
    /// MMA-00072-03 p.6, and the usual convention).</summary>
    /// <remarks>
    /// Under 100 % is not automatically wasted headroom. GFSK 4800 and 9600 sit at deviation
    /// = baud/4, which is minimum shift keying by design; Nino: "The tones selected in 4800 and
    /// 9600 mode provide Minimum Shift Keying, but more deviation may work well too." Changing
    /// those alters the signal's geometry and not only its level.
    /// </remarks>
    public double PercentOfFullDeviation =>
        100 * TargetPeakDeviationHz / FullDeviationHz(ChannelSpacingHz);

    /// <summary>100 % modulation for a channel spacing.</summary>
    public static double FullDeviationHz(double channelSpacingHz) => channelSpacingHz switch
    {
        >= 22500 => 5000,
        >= 15000 => 4000,
        _ => 2500,
    };
}
