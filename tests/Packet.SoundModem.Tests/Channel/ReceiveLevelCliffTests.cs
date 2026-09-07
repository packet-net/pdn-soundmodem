using AwesomeAssertions;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Channel;

/// <summary>
/// The cells the two frame level badges rest on, re-run: the thresholds in
/// <see cref="FrameLevelLimits"/> are only right while each mode's cliff is still outside the
/// band <c>docs/receive-levels.md</c> declares safe, and a demodulator change could move one
/// without touching a number.
/// </summary>
/// <remarks>
/// <para>A handful of cells rather than the sweep. <see cref="ReceiveLevelSweepProbe"/> is what
/// produced the tables and takes upwards of an hour over the catalogue; this takes seconds,
/// because what has to be guarded is not the shape of the curve but the two or three points a
/// threshold was read off. Each assertion below is a row of one of the doc's tables.</para>
/// <para>The SNRs are each mode's own reference knee from the doc plus a little, so a cell that
/// fails has failed because of the level and not because the ladder moved: a mode that got 2 dB
/// better or worse in AWGN still passes, and one whose cliff walked into the safe band does not.
/// </para>
/// </remarks>
public class ReceiveLevelCliffTests
{
    /// <summary>Frames per cell. Twelve is the sweep's own count; the assertions are
    /// three-quarters and a quarter of it, which is four frames clear of either.</summary>
    private const int Trials = 12;

    /// <summary>
    /// Each mode's reference knee at -18 dBFS from docs/receive-levels.md section 4, plus 4 dB -
    /// a working link with a little margin, which is the condition a badge is advice about.
    /// </summary>
    /// <remarks>
    /// Only the fast modes are here on purpose: the diversity banks take minutes a cell and the
    /// families they belong to are represented by a single-branch sibling that shares the
    /// demodulator (afsk1200 for the AFSK banks, qpsk3600 for the PSK ones).
    /// </remarks>
    private static double WorkingSnrFor(string mode) => mode switch
    {
        "afsk1200" => 9,
        "qpsk3600" => 12,
        "fsk9600-il2p" => 13,
        "c4fsk19200" => 24,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "no reference knee recorded"),
    };

    /// <summary>
    /// Six dB of overdrive - a station that much louder than one sitting at the top of the scale -
    /// costs the sign-sliced and angle-sliced modes nothing they cannot spare.
    /// </summary>
    /// <remarks>
    /// This is why <see cref="FrameLevelLimits.Default"/>'s loud edge is 0 dBFS and not under it:
    /// the whole 6 dB of <see cref="FrameLevelLimits.StationSpreadDb"/> is swallowed by the mode,
    /// so a threshold below full scale would badge frames that measurably cost their operator
    /// nothing. Clipping a signal whose bits are decided by a sign leaves the sign where it was.
    /// </remarks>
    [Theory]
    [InlineData("afsk1200")]
    [InlineData("qpsk3600")]
    [InlineData("fsk9600-il2p")]
    public void A_Sign_Sliced_Mode_Still_Copies_Six_dB_Past_Full_Scale(string mode)
    {
        Copies(mode, peakDbFs: 6, WorkingSnrFor(mode)).Should().BeGreaterThanOrEqualTo(
            Trials * 3 / 4,
            $"{mode} loses at most a decibel at six dB of overdrive (docs/receive-levels.md #4), "
                + "which is what lets its loud badge sit at the top of the scale rather than "
                + "under it");
    }

    /// <summary>
    /// The four-level modes do not: c4fsk19200 copies at the top of the scale and copies nothing
    /// at all nine dB past it.
    /// </summary>
    /// <remarks>
    /// The cliff <see cref="FrameLevelLimits.ClipSensitive"/> exists for, and the only one in the
    /// catalogue. C4FSK slices four amplitudes against fixed thresholds at zero and plus or minus
    /// two thirds of a tracked envelope; clipping compresses the outer levels into the inner ones
    /// while the thresholds stay put, and no envelope tracker can put them back. Measured at 4 dB
    /// of margin lost by +4 dB, 10 dB by +6, and no decode at any SNR on the ladder by +9.
    /// </remarks>
    [Fact]
    public void A_Four_Level_Mode_Falls_Off_A_Cliff_Nine_dB_Past_Full_Scale()
    {
        double snrDb = WorkingSnrFor("c4fsk19200");

        Copies("c4fsk19200", peakDbFs: -2, snrDb).Should().BeGreaterThanOrEqualTo(
            Trials * 3 / 4, "the mode is unharmed right up to the top of the scale");
        Copies("c4fsk19200", peakDbFs: 9, snrDb).Should().BeLessThanOrEqualTo(
            Trials / 4,
            "and gone nine dB past it - which is six dB above this mode's loud badge, so the "
                + "badge fires while a station that much louder would still be decodable");
    }

    /// <summary>
    /// A frame at the sign-sliced modes' quiet badge, -78 dBFS, has not lost anything yet.
    /// </summary>
    /// <remarks>
    /// The badge has to sit above the cliff rather than on it, or it says nothing until the frame
    /// has already gone. The cliff for these modes is the 16-bit converter's own floor at about
    /// -84 dBFS and not any demodulator objecting, so this asserts the threshold is still on the
    /// flat part with <see cref="FrameLevelLimits.StationSpreadDb"/> to spare.
    /// </remarks>
    [Theory]
    [InlineData("qpsk3600")]
    [InlineData("fsk9600-il2p")]
    public void A_Sign_Sliced_Mode_Is_Unhurt_At_Its_Quiet_Badge(string mode)
    {
        Copies(mode, FrameLevelLimits.Default.QuietPeakDbFs, WorkingSnrFor(mode))
            .Should().BeGreaterThanOrEqualTo(
                Trials * 3 / 4,
                $"{mode} is flat to -84 dBFS (docs/receive-levels.md #5), so its badge at "
                    + "-78 still has the station spread in hand");
    }

    /// <summary>
    /// The 1200 baud AFSK family is unhurt at its own quiet badge and losing frames well under
    /// it - which is the whole reason that family has a badge 39 dB above everyone else's.
    /// </summary>
    /// <remarks>
    /// The mechanism is <c>AfskDemodulator</c>'s <c>1e-5f</c> normalisation floor, whose own
    /// comment puts half the discriminator's gain at about -44 dBFS for this modulator's
    /// amplitudes. Two cells: at the badge the mode still copies, and 33 dB under it - past where
    /// the sweep has it 4 dB down - it does not. If either moves, the -39 has moved with it.
    /// </remarks>
    [Fact]
    public void The_Twelve_Hundred_Baud_Afsk_Family_Loses_Margin_Below_Its_Quiet_Badge()
    {
        double snrDb = WorkingSnrFor("afsk1200");

        Copies("afsk1200", FrameLevelLimits.QuietSensitive.QuietPeakDbFs, snrDb)
            .Should().BeGreaterThanOrEqualTo(
                Trials * 3 / 4,
                "afsk1200 is flat to -42 dBFS, so its badge at -39 is still on the flat part");
        Copies("afsk1200", peakDbFs: -72, snrDb - 4).Should().BeLessThanOrEqualTo(
            Trials / 4,
            "and four dB of margin down by -72, which is the cliff the badge is placed above");
    }

    /// <summary>
    /// Every mode in the catalogue is on the group the sweep measured it in.
    /// </summary>
    /// <remarks>
    /// A mode added without a thought about its receive level gets
    /// <see cref="FrameLevelLimits.Default"/>, which is the right default and the wrong answer if
    /// its slicer reads an amplitude. This lists all twenty that carry a level, so adding one
    /// fails here and the person adding it has to say which group it is in - or measure it.
    /// </remarks>
    [Fact]
    public void Every_Modes_Frame_Level_Limits_Are_The_Measured_Ones()
    {
        string[] clipSensitive = ["c4fsk9600", "c4fsk19200"];
        string[] quietSensitive =
        [
            "afsk1200", "afsk1200-fx25", "afsk1200-fx25rx", "afsk1200-multi", "afsk1200-il2p",
            "afsk1200-il2p-nocrc",
        ];

        foreach (string mode in ModemCatalog.KnownModes)
        {
            FrameLevelLimits expected =
                clipSensitive.Contains(mode) ? FrameLevelLimits.ClipSensitive
                : quietSensitive.Contains(mode) ? FrameLevelLimits.QuietSensitive
                : FrameLevelLimits.Default;

            ModemCatalog.FrameLevelsFor(mode).Should().Be(
                expected, $"{mode} is measured in docs/receive-levels.md sections 4 and 5");
        }

        // And the numbers themselves, so a change to one is a change to the document it was
        // derived in rather than a constant somebody nudged.
        FrameLevelLimits.Default.Should().Be(new FrameLevelLimits(0, -78));
        FrameLevelLimits.ClipSensitive.Should().Be(new FrameLevelLimits(-6, -78));
        FrameLevelLimits.QuietSensitive.Should().Be(new FrameLevelLimits(0, -39));
        FrameLevelLimits.StationSpreadDb.Should().Be(6);
        InputLevelMeter.HotPeakDbFs.Should().Be(
            FrameLevelLimits.ClipSensitive.LoudPeakDbFs,
            "the bar cannot know which mode the loudest thing on the input belonged to, so it "
                + "warns at the strictest mode's line");

        // A plugin mode nothing here has measured takes the group that badges least, and so does
        // a name that is missing altogether: the lookup runs inside a frame event on the receive
        // thread, so it answers rather than throws.
        ModemCatalog.FrameLevelsFor("not-a-mode").Should().Be(FrameLevelLimits.Default);
        ModemCatalog.FrameLevelsFor(null).Should().Be(FrameLevelLimits.Default);
    }

    /// <summary>
    /// The badge itself: loud at or above the mode's loud edge or with the card clipped, quiet
    /// below its quiet edge, and nothing at all in between.
    /// </summary>
    [Fact]
    public void A_Frames_Badge_Is_Its_Own_Modes_Two_Edges()
    {
        FrameLevelLimits sign = FrameLevelLimits.Default;
        FrameLevelLimits four = FrameLevelLimits.ClipSensitive;

        sign.Tag(peakDbFs: null, clipped: null).Should().BeNull("no level, no verdict");
        sign.Tag(-30, clipped: false).Should().BeNull();
        sign.Tag(-2, clipped: false).Should().BeNull(
            "a sign-sliced mode two dB under full scale has lost nothing");
        four.Tag(-2, clipped: false).Should().Be(
            "loud", "where a four-level one is inside its headroom");
        sign.Tag(-30, clipped: true).Should().Be(
            "loud", "the card running out of codes is a fact and badges whatever the peak was");
        sign.Tag(0, clipped: false).Should().Be("loud");
        sign.Tag(-79, clipped: false).Should().Be("quiet");
        sign.Tag(-77, clipped: false).Should().BeNull();
        FrameLevelLimits.QuietSensitive.Tag(-40, clipped: false).Should().Be(
            "quiet", "which the same level on any other mode would not be");
        sign.Tag(-40, clipped: false).Should().BeNull();
    }

    /// <summary>How many of <see cref="Trials"/> frames copy at one level and one SNR.</summary>
    private static int Copies(string mode, double peakDbFs, double snrDb)
    {
        byte[] frame = ReceiveLevelRig.Supervisory();
        int decoded = 0;
        for (int seed = 1; seed <= Trials; seed++)
        {
            if (ReceiveLevelRig.Decode(mode, frame, peakDbFs, snrDb, seed).Decoded)
            {
                decoded++;
            }
        }

        return decoded;
    }
}
