namespace Packet.SoundModem.Audio;

/// <summary>
/// What a decoded frame's own audio level was worth saying about it, decided when the frame was
/// decoded rather than when a page draws it.
/// </summary>
/// <remarks>
/// <para><b>Why an enum on the decode and not a rule at the edge</b> (Tom, 2026-09-07): "wonder
/// if the thresholds are low enough in the stack. Have you made them a UI concern or a
/// fundamental property of a decode? The latter would, I think, be favourable." Until this it was
/// the former: the waterfall server looked the limits up by mode <em>name</em> at broadcast time,
/// the frame log stored only the two measurements, and a monitor re-applied its own copy of the
/// rule to a relayed row. Three places to keep in step, one of them a string match - which is
/// exactly how the C4FSK badge came to be silently dead in production for a release
/// (PR #433 review-1). The verdict is now taken once, by the modem that decoded the frame, and
/// everything downstream carries it.</para>
/// <para>Null wherever there is no verdict: the frame's audio could not be placed (the FreeDV and
/// MS110D decoders report frames and not where in the audio they were), the row came from a log
/// or an uplink written before this existed, or it is one of our own transmissions. Null is "not
/// measured" and never "fine" - <see cref="Ok"/> is fine.</para>
/// </remarks>
public enum FrameLevel
{
    /// <summary>Measured, and between the mode's two edges: nothing to say.</summary>
    Ok,

    /// <summary>At or above the mode's loud edge, or the card ran out of codes during it.</summary>
    Loud,

    /// <summary>Below the mode's quiet edge.</summary>
    Quiet,
}

/// <summary>
/// The one spelling of a <see cref="FrameLevel"/> that leaves this process: the frame log's
/// <c>level</c> column and the uplink's <c>level</c> field.
/// </summary>
/// <remarks>
/// Lower case and stable, because both of those are read by software that was not built at the
/// same time as the writer: a monitor reads a station's rows, and a backlog query reads rows this
/// build did not write. An unrecognised word parses to null - "not measured" - rather than
/// throwing, which is what lets a future verdict be added without breaking an old reader.
/// </remarks>
public static class FrameLevelText
{
    /// <summary>The word for a verdict, or null where there is none.</summary>
    public static string? From(FrameLevel? level) => level switch
    {
        FrameLevel.Loud => "loud",
        FrameLevel.Quiet => "quiet",
        FrameLevel.Ok => "ok",
        _ => null,
    };

    /// <summary>The verdict behind a word, or null for a missing or unrecognised one.</summary>
    public static FrameLevel? Parse(string? text) => text switch
    {
        "loud" => FrameLevel.Loud,
        "quiet" => FrameLevel.Quiet,
        "ok" => FrameLevel.Ok,
        _ => null,
    };
}

/// <summary>
/// The two levels at which one decoded frame's own peak is worth a badge on its row: at or above
/// <see cref="LoudPeakDbFs"/> it is called too loud, below <see cref="QuietPeakDbFs"/> too quiet
/// on the modes whose slicer reads the level (<see cref="PeakWorthShowing"/>), and anywhere
/// between the two it says nothing at all.
/// </summary>
/// <remarks>
/// <para><b>Owned by the modem that decoded the frame.</b> Published through
/// <see cref="Modems.IFrameSpanSource.FrameLevels"/> beside the span margin, for the same reason
/// that is published there: it is a property of the demodulator and only the demodulator knows
/// it. Nothing looks a limit up by mode name any more.</para>
/// <para><b>Per mode, because the modems differ.</b> Tom, on the pair that shipped in v0.60.0:
/// "the two thresholds should be determined by examining the modems and determining what the
/// desired/optimal levels are". They now are - by decoding real frames through a real channel at
/// every level from 24 dB past full scale down to the converter's own floor, and measuring how
/// much link margin each mode loses there. The method, the tables and the arithmetic behind every
/// number below are in <c>docs/dev/receive-levels.md</c>; the summary is that the catalogue splits
/// into three, and the split is a property of the slicer.</para>
/// <list type="bullet">
/// <item><description><see cref="Default"/> - every mode whose slicer is a sign or an angle test,
/// which is all of AFSK, BPSK, QPSK and two-level FSK. Six dB of overdrive costs each of them at
/// most 1 dB of link margin, and 24 dB costs between 1 and 5; none of the fourteen outside the
/// 1200 baud AFSK family loses anything measurable at any level from -72 dBFS up to full
/// scale.</description></item>
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
/// <para><b>Both numbers are in the units the badge reads</b>, which is a peak over the frame's
/// span of the audio the modems hear - signal and channel noise together, not the signal alone.
/// The quiet thresholds are therefore derived from the probe's reported-peak column and not from
/// the level the sweep set, which on a working link sits 1 to 7 dB under it; on a link close to
/// its own decode knee the gap is at the top of that range, so the quiet badge is late by a few
/// dB there. <c>docs/dev/receive-levels.md</c> section 6 does the arithmetic and says how much.</para>
/// <para><b>What the operator is shown differs with the group, and is settled here.</b> The
/// figure itself goes on a row only where these limits say it is worth reading
/// (<see cref="PeakWorthShowing"/>), and the quiet verdict only fires there too. The
/// sign-and-angle group is left with the loud badge alone, which is the one claim its numbers
/// can support: the rail is the converter's, not the slicer's, and it costs every mode
/// something. The measurements go to the frame log and the uplink whatever the group.</para>
/// <para><b>The clip flag is a separate, unconditional trigger</b> and always has been: a
/// converter that ran out of codes is a fact about the station rather than a prediction, and it
/// costs at least a decibel on every mode measured. It is only available where the station has a
/// sound card of its own, though - a Flex or an ubersdr feed reports it null by design
/// (<c>Program.cs</c>'s <c>CardRateTap</c>), so on those stations the loud badge is
/// <see cref="LoudPeakDbFs"/> alone.</para>
/// </remarks>
/// <param name="LoudPeakDbFs">At or above this, the frame is badged too loud.</param>
/// <param name="QuietPeakDbFs">Below this, the frame is badged too quiet - on a mode whose
/// slicer reads the level. On the rest this is the converter's own quantisation floor rather
/// than anything a demodulator objects to, and it badges nothing: see
/// <see cref="PeakWorthShowing"/>.</param>
public readonly record struct FrameLevelLimits(double LoudPeakDbFs, double QuietPeakDbFs)
{
    /// <summary>
    /// How much louder than the frame just heard the next one may reasonably be, in dB - the
    /// headroom a threshold has to leave above a level that is otherwise fine.
    /// </summary>
    /// <remarks>
    /// Not a preference: the sum of what the hardware notes in this tree already say a receiving
    /// station cannot control. <c>docs/dev/hardware/tm8100-cm108-interface-notes.md</c> sets the
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
    /// <para><b>Loud: the top of the scale, and nothing below it.</b> Six dB of overdrive - which
    /// is the whole of <see cref="StationSpreadDb"/> applied to a frame already at full scale -
    /// costs these modes at most 1 dB of margin, because clipping a signal whose bits are decided
    /// by a sign leaves the sign alone. So the headroom buys nothing and a threshold under 0 dBFS
    /// would badge frames that measurably cost their operator nothing. The reading is clamped at
    /// 0 dBFS (<see cref="InputLevelMeter.DbFs"/>), so this fires when the frame's own audio
    /// reached the top of the scale.</para>
    /// <para><b>Quiet: -72 dBFS.</b> The shallowest level at which any mode taking this pair has
    /// lost a decibel is c4fsk19200 at -78 dBFS set, which the badge reads as -77.0, and
    /// <see cref="StationSpreadDb"/> above that is -71.0. Taken out to -72, the nearest level the
    /// sweep's 6 dB quiet ladder actually measured, which is a decibel of a threshold no real
    /// card can reach.
    /// What stops them there is a 16-bit converter running out of codes to describe the signal
    /// with rather than any demodulator objecting. <b>It is a quantisation-only floor and it is
    /// below any real card's</b>: the reading includes the input noise, so it cannot sit under
    /// the card's own idle level, which on CM108-class hardware with the gain up is nearer -60 to
    /// -70 dBFS. On that hardware this badge cannot fire at all, which is the honest consequence
    /// of the measurement rather than a bug - see <c>docs/dev/receive-levels.md</c> section 6.</para>
    /// </remarks>
    public static FrameLevelLimits Default { get; } = new(0, -72);

    /// <summary>
    /// The two C4FSK modes, whose four-level slicer is the only one here that reads an amplitude.
    /// </summary>
    /// <remarks>
    /// Loud at <see cref="InputLevelMeter.HotPeakDbFs"/>, written as that constant rather than as
    /// another -6 because they are the same claim: the strictest mode in the catalogue wants the
    /// whole of <see cref="StationSpreadDb"/> as headroom, and the meter's bar - which cannot
    /// know which mode the loudest thing on the input belonged to - turns red at the strictest
    /// mode's line. Quiet is <see cref="Default"/>'s, and c4fsk19200 is in fact the mode that
    /// sets it: it is the first of the group to lose anything as the level falls, a decibel down
    /// at -78 dBFS set and 4 dB down at -84, and it is that -78 cell - which those frames read as
    /// -77.0 - that the -72 was derived from.
    /// </remarks>
    public static FrameLevelLimits ClipSensitive { get; } =
        new(InputLevelMeter.HotPeakDbFs, Default.QuietPeakDbFs);

    /// <summary>
    /// The 1200 baud AFSK family, in every framing and both banks.
    /// </summary>
    /// <remarks>
    /// Quiet at -34 dBFS. The sweep has afsk1200 flat at -42 dBFS set and 2 dB down by -48, so
    /// the decibel is lost at about -45 set - which the badge reads as about -40.3, because at
    /// this family's knee the channel noise adds some 5 dB to the frame's own peak. Six dB above
    /// that reading is -34.3, published as the whole decibel it rounds to rather than snapped to
    /// the sweep's 6 dB quiet ladder: the measured levels either side of it are -30 and -36, and
    /// moving 2.3 dB to reach one of them would be inventing precision the sweep does not have.
    /// The mechanism is in the demodulator and is not in
    /// dispute: the discriminator divides by its own in-band power plus a floor of 1e-5, whose
    /// own comment puts half gain at about -44 dBFS for this modulator's amplitudes. Loud is
    /// <see cref="Default"/>'s, because the discriminator's output feeds a sign test: clipping
    /// costs this family 1 dB at 6 dB of overdrive and 3 dB at 24.
    /// </remarks>
    public static FrameLevelLimits QuietSensitive { get; } = new(Default.LoudPeakDbFs, -34);

    /// <summary>
    /// The verdict on one frame's measured level, or null where there was nothing to measure.
    /// </summary>
    /// <remarks>
    /// Three outcomes and not two, which is the difference between this and the badge it feeds:
    /// <see cref="FrameLevel.Ok"/> is a frame that was measured and found to be between the
    /// edges, and null is a frame nothing could measure. A page draws neither, but a log and a
    /// monitor need to be able to tell them apart. Called once, by the channel, at the moment of
    /// the decode.
    /// </remarks>
    /// <param name="peakDbFs">The frame's own peak, or null where its audio could not be placed.</param>
    /// <param name="clipped">Whether the card railed during it, null where nothing could judge.</param>
    /// <returns>The verdict, or null where there is no reading to judge.</returns>
    public FrameLevel? Classify(double? peakDbFs, bool? clipped)
    {
        if (peakDbFs is not { } peak)
        {
            return null;
        }

        if (clipped is true || peak >= LoudPeakDbFs)
        {
            return FrameLevel.Loud;
        }

        // The quiet edge only speaks where something actually reads the level. On the
        // sign-and-angle group it is the converter's quantisation floor and nothing else: a frame
        // under it is a weak signal, which is a fact about the band rather than a fault of the
        // station, and no demodulator here has lost a decibel to it. See PeakWorthShowing.
        return peak < QuietPeakDbFs && PeakWorthShowing ? FrameLevel.Quiet : FrameLevel.Ok;
    }

    /// <summary>
    /// Whether a frame's own peak figure, judged against these limits, says anything to the
    /// operator beyond whether it reached the converter's rail.
    /// </summary>
    /// <remarks>
    /// <para><b>False on <see cref="Default"/> alone</b>, and that is the whole of it: both of
    /// that pair's edges are the ends of the scale rather than anything its slicer reads. The
    /// loud one is 0 dBFS, which is where the converter runs out of codes, and the quiet one is
    /// the level at which a 16-bit converter has too few codes left to describe the signal with -
    /// below any real card's own idle noise, so on real hardware it cannot fire at all. Between
    /// them the sign and the angle are what they were: a bit decided by which side of zero a
    /// sample fell on does not care how far from zero it fell, and the measurements say so -
    /// none of the fourteen modes outside the 1200 baud AFSK family loses anything measurable at
    /// any level from -72 dBFS up to full scale. So the number on the row would be a figure with
    /// no action behind it at every value it can take.</para>
    /// <para><b>True on the other two, because their slicers do read the level.</b>
    /// <see cref="ClipSensitive"/> reads four amplitudes against fixed thresholds and loses
    /// margin as soon as clipping compresses the outer pair; <see cref="QuietSensitive"/>
    /// divides by its own in-band power with an absolute floor under it, and that floor takes
    /// half the discriminator's gain at about -44 dBFS. On those the figure is the operator's
    /// warning of a cliff that is actually in front of them.</para>
    /// <para><b>Read off the numbers rather than stored beside them</b>, so it cannot disagree
    /// with the pair it describes: a mode whose measurements moved an edge inward from the pair
    /// that means nothing has, by that act, said its slicer cares. That holds for an out-of-tree
    /// modem publishing its own measured pair as much as for the three here, and it leaves one
    /// source of truth, which is <c>docs/dev/receive-levels.md</c>.</para>
    /// <para>Decided here and consumed at the decode
    /// (<see cref="Modems.FrameQuality.PeakWorthShowing"/>), not at the edge that draws a row.
    /// Tom, 2026-09-12: "On the SSB modes I'd be happy with just 'TOO LOUD' if it's actually too
    /// loud. And not showing the dBFS in SSB modes." The axis is the slicer and not the
    /// modulation: <c>FmModeProfiles.IsFmMode</c> says which modes <em>are</em> frequency
    /// modulation, which is a different question and has been keyed off wrongly before.</para>
    /// <para><b>This hides the figure and not the measurement.</b> The frame log's
    /// <c>peak_dbfs</c> column and the uplink's <c>peakDbFs</c> field carry it either way: it is
    /// evidence, it is read by tools that are not this page, and a question about a capture level
    /// from last Tuesday is answered from the log.</para>
    /// </remarks>
    public bool PeakWorthShowing =>
        LoudPeakDbFs < Default.LoudPeakDbFs || QuietPeakDbFs > Default.QuietPeakDbFs;
}
