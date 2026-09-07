using System.Diagnostics;
using System.Globalization;
using System.Text;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Channel;

/// <summary>
/// The measurement run behind <c>docs/receive-levels.md</c>: for every receive mode that can
/// place its own frames, how much link margin the mode loses at each receive level, from well
/// past full scale down to the converter's own floor.
/// </summary>
/// <remarks>
/// <para><b>What it measures, and why that rather than a decode count.</b> A decode count at a
/// fixed signal-to-noise ratio says a mode works or does not; it says nothing about how close
/// the level put it to not working. So each cell is a knee instead: the lowest SNR at which the
/// mode still copies three quarters of its frames at that level. The knee is flat wherever the
/// level does not matter, and the number that matters is how far it has risen above its flat
/// value - that is dB of link margin the capture gain has cost the operator, in the units a
/// station is already run in.</para>
/// <para>Set <c>RX_LEVELS=1</c> to run it, <c>RX_LEVELS_MODES</c> to pick modes,
/// <c>RX_LEVELS_TRIALS</c> for the frames per cell and <c>RX_LEVELS_OUT</c> for where the table
/// goes. Minutes per mode, which is why it is a probe. What is committed as a test is
/// <see cref="ReceiveLevelCliffTests"/>, which re-runs the two cells each threshold rests on.</para>
/// </remarks>
public class ReceiveLevelSweepProbe
{
    /// <summary>The reference level: the middle of the meter's target zone, where nothing about
    /// the level is in question and the knee is the mode's own.</summary>
    private const double ReferenceDbFs = -18;

    /// <summary>Where the SNR ladder gives up and the cell is reported as no knee at all.</summary>
    private const double CeilingDb = 45;

    [Fact]
    public void Link_Margin_Against_Receive_Level()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("RX_LEVELS") is null, "bench probe only");

        string[] modes = Environment.GetEnvironmentVariable("RX_LEVELS_MODES") is { } chosen
            ? chosen.Split(',', StringSplitOptions.RemoveEmptyEntries)
            : [.. ModemCatalog.KnownModes.Where(Placeable)];
        int trials = int.Parse(
            Environment.GetEnvironmentVariable("RX_LEVELS_TRIALS") ?? "12",
            CultureInfo.InvariantCulture);
        byte[] frame = Environment.GetEnvironmentVariable("RX_LEVELS_LONG") is null
            ? ReceiveLevelRig.Supervisory()
            : ReceiveLevelRig.Information();
        var report = new StringBuilder();
        var clock = Stopwatch.StartNew();

        foreach (string mode in modes)
        {
            double reference = Knee(mode, frame, ReferenceDbFs, -15, trials);
            report.AppendLine(FormattableString.Invariant(
                $"`{mode}` reference knee {Show(reference)} dB at {ReferenceDbFs:F0} dBFS, {trials} frames a cell"));
            report.AppendLine();
            report.AppendLine("| level dBFS | knee dB | lost dB | reads dBFS | clipped |");
            report.AppendLine("|---|---|---|---|---|");

            // Up from the reference first, then down from it, each search starting a little
            // under the last knee: the knee only ever rises as the level leaves the flat middle,
            // so walking outwards from the reference costs a few ladder steps a cell instead of
            // a full climb every time.
            foreach (double[] arm in new[] { Loud(), Quiet() })
            {
                double from = reference - 3;
                foreach (double level in arm)
                {
                    double knee = Knee(mode, frame, level, from, trials);
                    from = double.IsNaN(knee) ? reference - 3 : knee - 3;
                    (double reads, int clipped) = Reads(
                        mode, frame, level, double.IsNaN(knee) ? reference + 6 : knee + 6, trials);
                    string badge = double.IsNaN(reads)
                        ? "-"
                        : reads.ToString("F1", CultureInfo.InvariantCulture);
                    report.AppendLine(FormattableString.Invariant(
                        $"| {level:+0;-0;0} | {Show(knee)} | {Show(knee - reference)} | {badge} | {clipped}/{trials} |"));
                }
            }

            report.AppendLine();
            report.AppendLine(FormattableString.Invariant($"<!-- {clock.Elapsed:hh\\:mm\\:ss} -->"));
            report.AppendLine();

            // Written out a mode at a time: a run over the whole catalogue is long enough that
            // losing all of it to an interrupted one would be the wrong trade.
            if (Environment.GetEnvironmentVariable("RX_LEVELS_OUT") is { } path)
            {
                File.AppendAllText(path, report.ToString());
                report.Clear();
            }
        }

        Assert.Fail(report.ToString());
    }

    private static string Show(double value) => double.IsNaN(value)
        ? $">{CeilingDb:F0}"
        : value.ToString("+0;-0;0", CultureInfo.InvariantCulture);

    /// <summary>Modes whose demodulator can say where its frames were, so a level exists.</summary>
    private static bool Placeable(string mode) =>
        !mode.StartsWith("freedv-", StringComparison.Ordinal)
        && !mode.StartsWith("ms110d-", StringComparison.Ordinal);

    /// <summary>The loud arm: the reference, then up past full scale into the converter's rails.
    /// Finely near 0 dBFS, where the answer is, and coarsely beyond it.</summary>
    private static double[] Loud() =>
        [ReferenceDbFs, -12, -9, -6, -4, -2, 0, 2, 4, 6, 9, 12, 18, 24];

    /// <summary>The quiet arm: down from the reference to where a 16-bit converter runs out of
    /// codes to describe the signal with.</summary>
    private static double[] Quiet() =>
        [-24, -30, -36, -42, -48, -54, -60, -66, -72, -78, -84, -90];

    /// <summary>
    /// The lowest signal-to-noise ratio, in whole dB and in a 3 kHz reference bandwidth, at
    /// which this mode still copies three quarters of its frames at this level.
    /// </summary>
    /// <param name="from">Where to start climbing; the search drops below it first if that
    /// already copies, so a start above the true knee costs accuracy only when the knee has
    /// fallen, which it does not do here.</param>
    private static double Knee(string mode, byte[] frame, double level, double from, int trials)
    {
        for (double snrDb = Math.Max(-18, from); snrDb <= CeilingDb; snrDb++)
        {
            if (Copies(mode, frame, level, snrDb, trials) * 4 >= trials * 3)
            {
                // Back down as far as it still copies, in case the start was already above the
                // knee - the outward walk starts three below the last one, and three is not
                // always enough on a mode whose knee falls back as the clipping symmetrises.
                double knee = snrDb;
                while (knee > -18 && Copies(mode, frame, level, knee - 1, trials) * 4 >= trials * 3)
                {
                    knee--;
                }

                return knee;
            }
        }

        return double.NaN;
    }

    /// <summary>How many of <paramref name="trials"/> frames copied.</summary>
    private static int Copies(string mode, byte[] frame, double level, double snrDb, int trials)
    {
        int decoded = 0;
        for (int seed = 1; seed <= trials; seed++)
        {
            if (ReceiveLevelRig.Decode(mode, frame, level, snrDb, seed).Decoded)
            {
                decoded++;
            }
        }

        return decoded;
    }

    /// <summary>What the per-frame badge reads at this level, and how often it calls the card
    /// clipped - the same two numbers an operator sees on a row.</summary>
    private static (double PeakDbFs, int Clipped) Reads(
        string mode, byte[] frame, double level, double snrDb, int trials)
    {
        double sum = 0;
        int measured = 0;
        int clipped = 0;
        for (int seed = 1; seed <= trials; seed++)
        {
            (bool decoded, double? peak, bool? railed) =
                ReceiveLevelRig.Decode(mode, frame, level, snrDb, seed);
            if (!decoded)
            {
                continue;
            }

            if (peak is { } dbfs)
            {
                sum += dbfs;
                measured++;
            }

            if (railed is true)
            {
                clipped++;
            }
        }

        return (measured == 0 ? double.NaN : sum / measured, clipped);
    }
}
