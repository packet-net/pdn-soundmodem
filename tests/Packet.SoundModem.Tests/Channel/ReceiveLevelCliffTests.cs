using System.Reflection;
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
                    + "-72 still has the station spread in hand");
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
                "afsk1200 is flat to -42 dBFS set, and its badge at -34 is a reported peak, "
                    + "which on this family's own links sits about 5 dB above the level set");
        Copies("afsk1200", peakDbFs: -72, snrDb - 4).Should().BeLessThanOrEqualTo(
            Trials / 4,
            "and four dB of margin down by -72, which is the cliff the badge is placed above");
    }

    /// <summary>
    /// Every mode in the catalogue is on the group the sweep measured it in - asked with the name
    /// a frame row actually carries, which is not the name it was configured under.
    /// </summary>
    /// <remarks>
    /// <para><b>Asked of the object, not of a table.</b> The limits belong to the modem
    /// (<see cref="IFrameSpanSource.FrameLevels"/>), so this builds every catalogue mode and asks
    /// it. What it replaces is a lookup keyed on the mode name, which passed a test walking the
    /// configuration spellings while every C4FSK frame in production silently took the
    /// sign-and-angle limits, because a <c>c4fsk19200</c> modem calls itself
    /// <c>c4fsk19200-il2pc</c> (PR #433 review-1). There is no name to get wrong now.</para>
    /// <para><b>Two things are pinned, and the second is not implied by the first.</b> Each of
    /// the 22 modes that carry a level answers with the group the doc measured it into (sections
    /// 4 and 5), and each of them <em>declares</em> the member rather than inheriting
    /// <see cref="IFrameSpanSource"/>'s default. The value check alone cannot stand in for the
    /// declaration check: 14 of the 22 are measured into
    /// <see cref="FrameLevelLimits.Default"/>, which is also what the interface default answers,
    /// so a modem that quietly dropped its member would give the same answer and pass. Before the
    /// default existed the compiler was the guard; the default makes the plugin surface additive
    /// and takes that guard away, so this reflection check puts it back for the built-ins.
    /// "Tested green and unpinned in production" is the exact failure this whole feature exists
    /// to undo.</para>
    /// <para>The 18 modes that cannot place their frames implement nothing and carry no
    /// verdict.</para>
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

        var placeable = new List<string>();
        foreach (string mode in ModemCatalog.KnownModes)
        {
            IModem modem = ModemCatalog.Create(mode, ModemCatalog.DspRateFor(mode), static _ => { });
            if (modem is not IFrameSpanSource source)
            {
                mode.Should().Match(
                    m => m.StartsWith("freedv-", StringComparison.Ordinal)
                        || m.StartsWith("ms110d-", StringComparison.Ordinal),
                    "only the two native block waveforms cannot place their own frames");
                continue;
            }

            placeable.Add(mode);
            DeclaresItsOwnFrameLevels(modem.GetType()).Should().BeTrue(
                $"{mode} must state the group it was measured into rather than inherit "
                    + "IFrameSpanSource's default, which is invisible to the value check below "
                    + "for the fourteen modes whose measured group is Default");

            FrameLevelLimits expected =
                clipSensitive.Contains(mode) ? FrameLevelLimits.ClipSensitive
                : quietSensitive.Contains(mode) ? FrameLevelLimits.QuietSensitive
                : FrameLevelLimits.Default;

            source.FrameLevels.Should().Be(
                expected, $"{mode} is measured in docs/receive-levels.md sections 4 and 5");
        }

        placeable.Should().HaveCount(22, "which is every packet mode in the catalogue");

        // And the numbers themselves, so a change to one is a change to the document it was
        // derived in rather than a constant somebody nudged.
        FrameLevelLimits.Default.Should().Be(new FrameLevelLimits(0, -72));
        FrameLevelLimits.ClipSensitive.Should().Be(new FrameLevelLimits(-6, -72));
        FrameLevelLimits.QuietSensitive.Should().Be(new FrameLevelLimits(0, -34));
        FrameLevelLimits.StationSpreadDb.Should().Be(6);
        InputLevelMeter.HotPeakDbFs.Should().Be(
            FrameLevelLimits.ClipSensitive.LoudPeakDbFs,
            "the bar cannot know which mode the loudest thing on the input belonged to, so it "
                + "warns at the strictest mode's line");
    }

    /// <summary>
    /// The verdict itself: loud at or above the mode's loud edge or with the card clipped, quiet
    /// below its quiet edge, ok in between, and nothing at all where there was no reading.
    /// </summary>
    /// <remarks>
    /// Three outcomes and not two. A page draws a badge for two of them, but the log and the
    /// uplink have to be able to tell a frame that was measured and found fine from one nothing
    /// could measure, which is the distinction <see cref="FrameLevel.Ok"/> against null carries.
    /// </remarks>
    [Fact]
    public void A_Frames_Verdict_Is_Its_Own_Modes_Two_Edges()
    {
        FrameLevelLimits sign = FrameLevelLimits.Default;
        FrameLevelLimits four = FrameLevelLimits.ClipSensitive;

        sign.Classify(peakDbFs: null, clipped: null).Should().BeNull("no reading, no verdict");
        sign.Classify(-30, clipped: false).Should().Be(FrameLevel.Ok);
        sign.Classify(-2, clipped: false).Should().Be(
            FrameLevel.Ok, "a sign-sliced mode two dB under full scale has lost nothing");
        four.Classify(-2, clipped: false).Should().Be(
            FrameLevel.Loud, "where a four-level one is inside its headroom");
        sign.Classify(-30, clipped: true).Should().Be(
            FrameLevel.Loud,
            "the card running out of codes is a fact and badges whatever the peak was");
        sign.Classify(0, clipped: false).Should().Be(FrameLevel.Loud);
        sign.Classify(-73, clipped: false).Should().Be(FrameLevel.Quiet);
        sign.Classify(-71, clipped: false).Should().Be(FrameLevel.Ok);
        FrameLevelLimits.QuietSensitive.Classify(-35, clipped: false).Should().Be(
            FrameLevel.Quiet, "which the same level on any other mode would not be");
        sign.Classify(-35, clipped: false).Should().Be(FrameLevel.Ok);
    }

    /// <summary>
    /// The one spelling that leaves the process round-trips, and an unknown word reads as no
    /// verdict rather than throwing.
    /// </summary>
    /// <remarks>
    /// Both ends of it are read by software built at a different time from the writer: a monitor
    /// reads a station's uplink, and a backlog query reads rows an older build wrote. A word this
    /// build does not know has to mean "not measured" there, or a station one release ahead takes
    /// a monitor's frame panel down.
    /// </remarks>
    [Fact]
    public void A_Verdict_Survives_The_Round_Trip_And_An_Unknown_Word_Does_Not()
    {
        foreach (FrameLevel level in Enum.GetValues<FrameLevel>())
        {
            FrameLevelText.Parse(FrameLevelText.From(level)).Should().Be(level);
        }

        FrameLevelText.From(null).Should().BeNull();
        FrameLevelText.Parse(null).Should().BeNull();
        FrameLevelText.Parse("").Should().BeNull();
        FrameLevelText.Parse("LOUD").Should().BeNull("the spelling on the wire is lower case");
        FrameLevelText.Parse("deafening").Should().BeNull(
            "a verdict a later build invented reads as no verdict here, not as an exception");
    }

    /// <summary>
    /// Whether <paramref name="type"/> supplies <c>FrameLevels</c> itself rather than falling
    /// through to <see cref="IFrameSpanSource"/>'s default implementation.
    /// </summary>
    /// <remarks>
    /// Both spellings count: an ordinary public property on the class, and an explicit interface
    /// implementation, which is invisible to <c>GetProperty</c> and shows up in the interface map
    /// instead. What does not count is the default, whose target method is declared on the
    /// interface itself - which is precisely the case this exists to catch.
    /// </remarks>
    private static bool DeclaresItsOwnFrameLevels(Type type)
    {
        const BindingFlags Own =
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        if (type.GetProperty(nameof(IFrameSpanSource.FrameLevels), Own) is not null)
        {
            return true;
        }

        InterfaceMapping map = type.GetInterfaceMap(typeof(IFrameSpanSource));
        for (int i = 0; i < map.InterfaceMethods.Length; i++)
        {
            if (map.InterfaceMethods[i].Name
                == $"get_{nameof(IFrameSpanSource.FrameLevels)}")
            {
                return map.TargetMethods[i].DeclaringType != typeof(IFrameSpanSource);
            }
        }

        return false;
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
