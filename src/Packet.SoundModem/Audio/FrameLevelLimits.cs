namespace Packet.SoundModem.Audio;

/// <summary>
/// The two levels at which one decoded frame's own peak is worth a badge on its row: at or above
/// <see cref="LoudPeakDbFs"/> it is called too loud, below <see cref="QuietPeakDbFs"/> too quiet,
/// and anywhere between the two it says nothing at all.
/// </summary>
/// <remarks>
/// <para><b>Per mode, because the modems differ.</b> Tom, on the pair that shipped in v0.60.0:
/// "the two thresholds should be determined by examining the modems and determining what the
/// desired/optimal levels are". They now are - by decoding real frames through a real channel at
/// every level from 24 dB past full scale down to the converter's own floor, and measuring how
/// much link margin each mode loses there. The method, the tables and the arithmetic behind every
/// number below are in <c>docs/receive-levels.md</c>; the summary is that the catalogue splits
/// into three, and the split is a property of the slicer.</para>
/// <list type="bullet">
/// <item><description><see cref="Default"/> - every mode whose slicer is a sign or an angle test,
/// which is all of AFSK, BPSK, QPSK and two-level FSK. Measured flat: at most 1 dB of margin lost
/// at 24 dB of overdrive, and nothing at all down to -84 dBFS.</description></item>
/// <item><description><see cref="ClipSensitive"/> - the two C4FSK modes, the only ones here that
/// slice four amplitude levels against fixed thresholds at zero and plus or minus two thirds of a
/// tracked envelope. Clipping compresses the outer levels into the inner ones and no envelope
/// tracker can undo it: 1 to 4 dB of margin lost at 4 dB of overdrive, 5 to 10 dB at 6, and no
/// decode at all at 9.</description></item>
/// <item><description><see cref="QuietSensitive"/> - the 1200 baud AFSK family, whose
/// discriminator divides by its own in-band power with a floor of 1e-5 added
/// (<c>AfskDemodulator</c>). That floor takes half the discriminator's gain at about -44 dBFS by
/// its own arithmetic, and the sweep finds the family losing margin from about -45.</description>
/// </item>
/// </list>
/// <para><b>The clip flag is a separate, unconditional trigger</b> and always has been: a
/// converter that ran out of codes is a fact about the station rather than a prediction, and it
/// costs at least a decibel on every mode measured. <see cref="LoudPeakDbFs"/> is the other
/// half - the headroom warning that has to fire before the clipping starts.</para>
/// </remarks>
/// <param name="LoudPeakDbFs">At or above this, the frame is badged too loud.</param>
/// <param name="QuietPeakDbFs">Below this, the frame is badged too quiet.</param>
public readonly record struct FrameLevelLimits(double LoudPeakDbFs, double QuietPeakDbFs)
{
    /// <summary>
    /// How much louder than the frame just heard the next one may reasonably be, in dB - the
    /// headroom a threshold has to leave above a level that is otherwise fine.
    /// </summary>
    /// <remarks>
    /// Not a preference: the sum of what the hardware notes in this tree already say a receiving
    /// station cannot control. <c>docs/hardware/tm8100-cm108-interface-notes.md</c> sets the
    /// interface at -12 dBFS for 60% of class deviation and works out that 100% then lands at
    /// -7.6, so the deviation stations actually run spans 4.4 dB; the same radio's published
    /// receive-tap level is a plus or minus 10% band, which is another 1.8 dB across it. That is
    /// 6.2 dB before anything else, and the radio1 bench measured a further 1.4 dB of
    /// frame-to-frame spread from one station at a fixed gain (2026-09-07). Six is also the
    /// headroom figure this tree already works to elsewhere.
    /// </remarks>
    public const double StationSpreadDb = 6;

    /// <summary>
    /// Every mode whose decisions are a sign or an angle test: AFSK 300, both BPSK families, all
    /// three QPSK rates, and the two-level G3RUH FSK modes.
    /// </summary>
    /// <remarks>
    /// <para><b>Loud: the top of the scale, and nothing below it.</b> These lose at most 1 dB of
    /// margin at 24 dB of overdrive - clipping a signal whose bits are decided by a sign leaves
    /// the sign alone - so <see cref="StationSpreadDb"/> of headroom buys nothing and a threshold
    /// under 0 dBFS would badge frames that measurably cost their operator nothing. The reading
    /// is clamped at 0 dBFS (<see cref="InputLevelMeter.DbFs"/>), so this fires exactly when the
    /// frame's own audio reached the top of the scale.</para>
    /// <para><b>Quiet: -78 dBFS.</b> The sweep finds no mode in this group losing anything above
    /// -84 dBFS, which is a 16-bit converter running out of codes to describe the signal with
    /// rather than any demodulator objecting, and <see cref="StationSpreadDb"/> above that is
    /// -78. <b>This is a quantisation-only floor</b>: a real card's analogue noise sits above it
    /// by an amount nothing in this repository has measured, so the true figure on real hardware
    /// is somewhere higher and this threshold is optimistic by that much.</para>
    /// </remarks>
    public static FrameLevelLimits Default { get; } = new(0, -78);

    /// <summary>
    /// The two C4FSK modes, whose four-level slicer is the only one here that reads an amplitude.
    /// </summary>
    /// <remarks>
    /// Loud at <see cref="InputLevelMeter.HotPeakDbFs"/>, written as that constant rather than as
    /// another -6 because they are the same claim: the strictest mode in the catalogue wants the
    /// whole of <see cref="StationSpreadDb"/> as headroom, and the meter's bar - which cannot
    /// know which mode the loudest thing on the input belonged to - turns red at the strictest
    /// mode's line. Quiet is <see cref="Default"/>'s: c4fsk9600 and c4fsk19200 are flat down to
    /// -84 dBFS like everything else, and it is only the loud end that sets them apart.
    /// </remarks>
    public static FrameLevelLimits ClipSensitive { get; } =
        new(InputLevelMeter.HotPeakDbFs, Default.QuietPeakDbFs);

    /// <summary>
    /// The 1200 baud AFSK family, in every framing and both banks.
    /// </summary>
    /// <remarks>
    /// Quiet at -39 dBFS: the sweep has afsk1200 flat to -42 and 2 dB down by -48, so the
    /// decibel is lost at about -45, and <see cref="StationSpreadDb"/> above that is -39. The
    /// mechanism is in the demodulator and is not in dispute - the discriminator divides by its
    /// own in-band power plus a floor of 1e-5, whose own comment puts half gain at about -44 dBFS
    /// for this modulator's amplitudes. Loud is <see cref="Default"/>'s: the discriminator's
    /// output feeds a sign test, so clipping costs this family 1 dB at 6 dB of overdrive and 3 dB
    /// at 24.
    /// </remarks>
    public static FrameLevelLimits QuietSensitive { get; } = new(Default.LoudPeakDbFs, -39);

    /// <summary>
    /// What one frame's level is worth saying about it: <c>loud</c>, <c>quiet</c>, or nothing.
    /// </summary>
    /// <remarks>
    /// Nothing between the two, which is the point (issue #426): a badge is for a level that has
    /// started to cost the mode something, and a row that says nothing is a row with nothing
    /// wrong with it. Decided in the daemon so that a station's own page and a monitor's copy of
    /// its rows read the same rule.
    /// </remarks>
    /// <param name="peakDbFs">The frame's own peak, or null where its audio could not be placed.</param>
    /// <param name="clipped">Whether the card railed during it, null where nothing could judge.</param>
    /// <returns><c>loud</c>, <c>quiet</c>, or null.</returns>
    public string? Tag(double? peakDbFs, bool? clipped)
    {
        if (peakDbFs is not { } peak)
        {
            return null;
        }

        if (clipped is true || peak >= LoudPeakDbFs)
        {
            return "loud";
        }

        return peak < QuietPeakDbFs ? "quiet" : null;
    }
}
