namespace Packet.SoundModem.Modems.OfdmFm.Link;

/// <summary>A recommendation as a report carries it: the format, or none when the sender has nothing to recommend yet, and whether the burst it answers failed.</summary>
public readonly record struct LinkReport(int? Format, bool Failed);

/// <summary>The format for the next burst to a neighbour, and whether to set "report wanted" in it.</summary>
public readonly record struct LinkChoice(int Format, bool ReportWanted);

/// <summary>
/// What one station holds of one other station (the prototype's <c>Neighbour</c>), in both roles. As a receiver: a running estimate
/// of quality on each carrier, a margin steered by results, and the format it recommends. As a sender: the last report received
/// and when, and what silence has done. Held and changed by <see cref="LinkAdaptation"/> only; read it to see what is known.
/// </summary>
public sealed class LinkNeighbour
{
    private readonly double[] db = new double[StandardFormats.FullBandCarriers];

    internal LinkNeighbour() => Reset();

    /// <summary>Quality on each of the 333 carriers, in dB, as a full-band burst at full drive would show it; NaN where nothing has been measured.</summary>
    public ReadOnlySpan<double> EstimateDb => db;

    /// <summary>The margin, dB: where the recommendation keeps from the edge of what a format holds.</summary>
    public double Margin { get; internal set; }

    /// <summary>The format last recommended to them.</summary>
    public int? Recommended { get; internal set; }

    /// <summary>A faster format waiting for its second burst running.</summary>
    public int? Pending { get; internal set; }

    /// <summary>When they were last heard: a data burst of theirs to anyone, or a report to this station (seconds).</summary>
    public double? LastHeard { get; internal set; }

    /// <summary>The recommendation they were last sent, in a prelude or a report.</summary>
    public int? Told { get; internal set; }

    /// <summary>After a failure: nothing faster than this until two bursts running decode.</summary>
    public int? Cap { get; internal set; }

    /// <summary>Bursts decoded running.</summary>
    public int Good { get; internal set; }

    /// <summary>Bursts that have gone into the estimate (restarted after <see cref="LinkAdaptation.FreshSeconds"/> without one).</summary>
    public int Measures { get; internal set; }

    /// <summary>When the last of them did (seconds).</summary>
    public double? MeasuredAt { get; internal set; }

    /// <summary>The format last recommended to this station by them, or null.</summary>
    public int? ReportFormat { get; internal set; }

    /// <summary>When that report arrived (seconds), or null.</summary>
    public double? ReportTime { get; internal set; }

    /// <summary>Bursts that asked for a report and got none since the last report that gave a recommendation.</summary>
    public int Silences { get; internal set; }

    /// <summary>Bursts sent to them since anything at all was heard from them.</summary>
    public int Unanswered { get; internal set; }

    /// <summary>The capability word from their last decoded burst that carried one.</summary>
    public int? Capability { get; internal set; }

    internal Span<double> Estimate => db;

    internal void Reset()
    {
        db.AsSpan().Fill(double.NaN);
        Margin = LinkAdaptation.MarginStart;
        Recommended = null;
        Pending = null;
        LastHeard = null;
        Told = null;
        Cap = null;
        Good = 0;
        Measures = 0;
        MeasuredAt = null;
        ReportFormat = null;
        ReportTime = null;
        Silences = 0;
        Unanswered = 0;
        Capability = null;
    }
}

/// <summary>
/// One station's side of adaptation over the air, as <c>docs/dev/ofdm-fm/adaptation-protocol.md</c> sets it out and the prototype
/// runs it: what it remembers of each other station it hears, the format it recommends to each, and the format it chooses to send
/// at. Logic only: no audio, no modem, and no clock of its own. Every method that needs the time takes it as a parameter, a
/// <see cref="double"/> of seconds from any fixed starting point (the prototype's <c>now</c>); nothing here reads a clock or waits.
/// Station numbers are 1 to 63; 0 as a destination means every station. Not thread safe. The methods mirror the prototype's
/// <c>Station</c> one for one: <see cref="HeardData"/>, <see cref="ReportFor"/>, <see cref="HeardReport"/>, <see cref="Choose"/>,
/// <see cref="Sent"/> and <see cref="NoAnswer"/>. Once a station's record exists, none of them allocates.
/// </summary>
public sealed class LinkAdaptation
{
    /// <summary>Seconds a received report counts in full; one rung slower for each further <see cref="FreshSeconds"/>.</summary>
    public const double FreshSeconds = 30.0;

    /// <summary>Seconds without hearing a neighbour before what was measured of them is dropped.</summary>
    public const double ForgetSeconds = 600.0;

    /// <summary>Where the margin starts, dB.</summary>
    public const double MarginStart = 3.0;

    /// <summary>The margin goes up by this on a failure, dB.</summary>
    public const double MarginUp = 0.5;

    /// <summary>The margin goes down by this on a success, dB.</summary>
    public const double MarginDown = 0.01;

    /// <summary>The most the margin rises to, dB.</summary>
    public const double MarginMax = 12.0;

    /// <summary>Rungs slower than the sender's own estimate when it has no report.</summary>
    public const int Caution = 2;

    /// <summary>How much a new burst's measurement counts against the running estimate.</summary>
    public const double Weight = 0.5;

    /// <summary>Decibels a burst must fall below the estimate, across its band, to replace it outright.</summary>
    public const double DropDb = 2.0;

    /// <summary>Bursts from a new neighbour that count in full and may raise the recommendation at once.</summary>
    public const int Settling = 3;

    /// <summary>Decibels taken off what a failed burst's opening measures, which is coarse and tends to flatter.</summary>
    public const double CoarseDb = 3.0;

    private const int Carriers = StandardFormats.FullBandCarriers;
    private const int Neighbours = 64;

    // The frequency of each carrier, Hz: bins 9 up, 48000 / 2048 apart.
    private static readonly double[] CarrierHz = BuildCarrierHz();

    private static readonly int OpeningCarriers3000 = CarriersUpTo(3000.0);
    private static readonly int OpeningCarriers1500 = CarriersUpTo(1500.0);

    private readonly LinkNeighbour?[] near = new LinkNeighbour?[Neighbours];

    // Working space, so that no call allocates.
    private readonly double[] measured = new double[Carriers];
    private readonly double[] seenNew = new double[Carriers];
    private readonly double[] seenOld = new double[Carriers];
    private readonly double[] profile = new double[Carriers];
    private readonly double[] window = new double[7];

    /// <summary>A station with this number (1 to 63).</summary>
    public LinkAdaptation(int number)
    {
        if (number is < 1 or > 63)
        {
            throw new ArgumentOutOfRangeException(nameof(number), "a station number is 1 to 63.");
        }

        Number = number;
    }

    /// <summary>This station's number.</summary>
    public int Number { get; }

    /// <summary>
    /// Decibels by which a burst of format <paramref name="number"/> has more power on each carrier than a full-band burst at full
    /// drive: its drive, and its power being spread over fewer carriers (the payload's, or the opening's if <paramref name="opening"/> is set).
    /// </summary>
    public static double Footing(int number, bool opening = false)
    {
        FormatSpec s = StandardFormats.Describe(number);
        int carriers = s.Carriers;
        if (opening)
        {
            carriers = s.Opening switch
            {
                FormatOpening.UpTo1500Hz => OpeningCarriers1500,
                FormatOpening.UpTo3000Hz => OpeningCarriers3000,
                _ => Carriers,
            };
        }

        return (10 * Math.Log10((double)Carriers / carriers)) + s.DriveDb;
    }

    /// <summary>What is held about a neighbour, or false when nothing is.</summary>
    public bool TryGetNeighbour(int who, out LinkNeighbour? neighbour)
    {
        neighbour = (uint)who < Neighbours ? near[who] : null;
        return neighbour is not null;
    }

    // ---- receiving ----

    /// <summary>
    /// A data burst from <paramref name="sender"/> was heard. <paramref name="decoded"/>: its payload passed its check.
    /// <paramref name="quality"/>: the quality on each carrier as measured from it (333 power ratios, NaN where it gave none: from its
    /// payload's carriers if decoded, from its opening if not), or empty if there was nothing worth measuring.
    /// <paramref name="footingDb"/>: how much more power the measured part of the burst had on each of its carriers than a full-band
    /// burst at full drive would have (see <see cref="Footing"/>); left out, the payload's at the format's own power.
    /// <paramref name="sudden"/>: quality collapsed part-way, or another burst began inside it. <paramref name="theirReport"/>: the
    /// report from its prelude, if it decoded and was for this station. <paramref name="capability"/>: its capability word, if it gave one.
    /// Returns null, or the report to send by itself now (its format null when nothing is yet recommended). A report that is merely
    /// due because the sender asked is left to the caller. The prototype takes the prelude's "report wanted" flag here and does not
    /// look at it; it is not taken.
    /// </summary>
    public LinkReport? HeardData(int sender, int dest, int heardFormat, bool decoded, ReadOnlySpan<double> quality, double now, double? footingDb = null, bool sudden = false, LinkReport? theirReport = null, int? capability = null)
    {
        if ((uint)dest >= Neighbours)
        {
            throw new ArgumentOutOfRangeException(nameof(dest));
        }

        LinkNeighbour nb = Neighbour(sender, now);
        bool mine = dest == Number;
        int? droppedFrom = nb.Recommended;
        if (!quality.IsEmpty && !sudden)
        {
            // a burst something else came on top of says nothing about the path
            Measure(nb, quality, footingDb ?? Footing(heardFormat), !decoded, now);
        }

        nb.LastHeard = now;
        nb.Unanswered = 0;
        if (decoded)
        {
            nb.Margin = Math.Max(0.0, nb.Margin - MarginDown);
            nb.Good++;
            if (nb.Good >= 2)
            {
                nb.Cap = null;
            }

            if (capability is not null)
            {
                nb.Capability = capability;
            }

            if (mine && theirReport is LinkReport report)
            {
                HeardReport(sender, report.Format, report.Failed, now);
            }
        }
        else if (!sudden)
        {
            nb.Good = 0;
            if (nb.Recommended is not int recommended || StandardFormats.Rate(heardFormat) <= StandardFormats.Rate(recommended))
            {
                nb.Margin = Math.Min(MarginMax, nb.Margin + MarginUp);
            }

            int down = StandardFormats.Slower(heardFormat, 1);
            if (nb.Cap is not int cap || StandardFormats.Rate(down) < StandardFormats.Rate(cap))
            {
                nb.Cap = down;
            }
        }

        int? rec = sudden && nb.Recommended is not null ? nb.Recommended : Recommend(nb);
        if (dest == 0 || !mine)
        {
            return null;
        }

        if (!decoded)
        {
            nb.Told = rec;
            return new LinkReport(rec, true);
        }

        if (droppedFrom is int before && rec is int after && StandardFormats.Rate(after) < StandardFormats.Rate(before)
            && nb.Told is int told && StandardFormats.Rate(after) < StandardFormats.Rate(told))
        {
            nb.Told = rec;
            return new LinkReport(rec, false);
        }

        return null;
    }

    /// <summary>The recommendation to carry in a burst to <paramref name="dest"/> (null: nothing to recommend yet), noting that they have been told.</summary>
    public int? ReportFor(int dest)
    {
        LinkNeighbour nb = Neighbour(dest, null);
        if (nb.Recommended is not null)
        {
            nb.Told = nb.Recommended;
        }

        return nb.Recommended;
    }

    // ---- sending ----

    /// <summary>
    /// A report from <paramref name="sender"/>, alone or in a data burst's prelude. <paramref name="recommended"/>: null when the
    /// prelude gave none (the sender has not heard this station). That is word from them but not a report: the silences stand.
    /// <paramref name="failed"/> is not looked at, as in the prototype: the sender obeys the recommendation either way.
    /// </summary>
    public void HeardReport(int sender, int? recommended, bool failed, double now)
    {
        _ = failed;
        LinkNeighbour nb = Neighbour(sender, now);
        if (recommended is int format)
        {
            nb.ReportFormat = format;
            nb.ReportTime = now;
            nb.Silences = 0;
        }

        nb.Unanswered = 0;
        nb.LastHeard = now;
    }

    /// <summary>The format for the next burst to <paramref name="dest"/>, and whether to set "report wanted".</summary>
    public LinkChoice Choose(int dest, double now)
    {
        LinkNeighbour nb = Neighbour(dest, now);
        int? own = null;
        if (nb.Recommended is int recommended)
        {
            own = StandardFormats.Slower(recommended, Caution);
        }
        else if (BuildProfile(nb))
        {
            // what this station would recommend for the opposite direction, taken as a guess at this one
            own = StandardFormats.Slower(StandardFormats.Recommend(profile, nb.Margin), Caution);
        }

        int floor = own ?? StandardFormats.FirstContact;
        int n;
        bool wanted;
        if (nb.ReportFormat is not int fmt)
        {
            n = floor;
            wanted = true;
        }
        else
        {
            double age = now - nb.ReportTime!.Value;
            n = age < FreshSeconds ? fmt : StandardFormats.Slower(fmt, RungsOfAge(age));

            // ageing takes it down towards the sender's own estimate and no further, and never above what it was told
            if (StandardFormats.Rate(n) < Math.Min(StandardFormats.Rate(floor), StandardFormats.Rate(fmt)))
            {
                n = StandardFormats.Rate(floor) <= StandardFormats.Rate(fmt) ? floor : fmt;
            }

            wanted = age >= FreshSeconds;
        }

        if (nb.Silences >= 3)
        {
            n = StandardFormats.FirstContact;
        }
        else if (nb.Silences == 2)
        {
            n = StandardFormats.Slower(n, 2);
        }

        // asking costs the far end a fifth of a second; it is worth it when nothing has come back from them lately
        return new LinkChoice(n, wanted || nb.Silences != 0 || nb.Unanswered >= 2);
    }

    /// <summary>A data burst went to <paramref name="dest"/>.</summary>
    public void Sent(int dest) => Neighbour(dest, null).Unanswered++;

    /// <summary>A burst that asked for a report was met with silence.</summary>
    public void NoAnswer(int dest) => Neighbour(dest, null).Silences++;

    // ---- the prototype's private methods ----

    /// <summary>What is held about a neighbour. Everything is dropped once they have gone unheard for <see cref="ForgetSeconds"/>, whichever way the station next comes to look at them (calls that take no time never drop anything).</summary>
    private LinkNeighbour Neighbour(int who, double? now)
    {
        if (who is < 1 or > 63)
        {
            throw new ArgumentOutOfRangeException(nameof(who), "a station number is 1 to 63.");
        }

        LinkNeighbour? nb = near[who];
        if (nb is null)
        {
            nb = near[who] = new LinkNeighbour();
        }
        else if (now is double t && nb.LastHeard is double heard && t - heard > ForgetSeconds)
        {
            nb.Reset();
        }

        return nb;
    }

    /// <summary>
    /// Folds one burst's per-carrier quality into the estimate. A neighbour not measured for <see cref="FreshSeconds"/> is measured
    /// as a new one is: its next few bursts count in full and may raise the recommendation at once.
    /// </summary>
    private void Measure(LinkNeighbour nb, ReadOnlySpan<double> quality, double footingDb, bool onlyIfWorse, double now)
    {
        if (quality.Length != Carriers)
        {
            throw new ArgumentException("quality must hold one value for each of the 333 carriers, or none.", nameof(quality));
        }

        if (nb.MeasuredAt is double last && now - last > FreshSeconds)
        {
            nb.Measures = 0;
        }

        Span<double> est = nb.Estimate;
        int seen = 0;
        for (int i = 0; i < Carriers; i++)
        {
            if (double.IsNaN(quality[i]))
            {
                continue;
            }

            double value = (10 * Math.Log10(Math.Max(quality[i], 1e-6))) - footingDb;
            if (onlyIfWorse)
            {
                value -= CoarseDb;
            }

            measured[i] = value;
            if (!double.IsNaN(est[i]))
            {
                seenNew[seen] = value;
                seenOld[seen] = est[i];
                seen++;
            }
        }

        double meanNew = 0;
        double meanOld = 0;
        if (seen > 0)
        {
            meanNew = PairwiseSum.Sum(seenNew.AsSpan(0, seen)) / seen;
            meanOld = PairwiseSum.Sum(seenOld.AsSpan(0, seen)) / seen;
            if (onlyIfWorse && meanNew >= meanOld - DropDb)
            {
                // one or two symbols of opening, read at a level that failed: believed only where it says the path is worse
                return;
            }
        }

        if (seen > 0 && meanNew < meanOld - DropDb)
        {
            // the path has got worse: believe it at once, not by halves, and take the carriers this burst did not measure down by as much
            double delta = meanNew - meanOld;
            for (int i = 0; i < Carriers; i++)
            {
                if (double.IsNaN(quality[i]))
                {
                    est[i] += delta;
                }
            }

            for (int i = 0; i < Carriers; i++)
            {
                if (!double.IsNaN(quality[i]))
                {
                    est[i] = measured[i];
                }
            }
        }
        else
        {
            // a neighbour only just heard has no history worth averaging with: its first few bursts count in full
            double w = nb.Measures < Settling ? 1.0 : Weight;
            int k = 0;
            for (int i = 0; i < Carriers; i++)
            {
                if (double.IsNaN(quality[i]))
                {
                    continue;
                }

                if (double.IsNaN(est[i]))
                {
                    est[i] = measured[i];
                }
                else
                {
                    est[i] = (((1 - w) * est[i]) + (w * measured[i]));
                    seenNew[k++] = est[i];
                }
            }

            if (seen > 0)
            {
                // the carriers this burst did not measure follow the ones it did, up as well as down
                double delta = (PairwiseSum.Sum(seenNew.AsSpan(0, k)) / k) - meanOld;
                for (int i = 0; i < Carriers; i++)
                {
                    if (double.IsNaN(quality[i]))
                    {
                        est[i] += delta;
                    }
                }
            }
        }

        nb.Measures++;
        nb.MeasuredAt = now;
    }

    /// <summary>
    /// The estimate on all 333 carriers as power ratios, into the working profile; false when nothing has been measured. Carriers
    /// never measured (above the band of the narrow formats heard so far) are taken from the highest one that was, falling 20 dB a
    /// decade as FM noise does, less 3 dB of caution.
    /// </summary>
    private bool BuildProfile(LinkNeighbour nb)
    {
        ReadOnlySpan<double> est = nb.EstimateDb;
        int top = -1;
        for (int i = Carriers - 1; i >= 0; i--)
        {
            if (!double.IsNaN(est[i]))
            {
                top = i;
                break;
            }
        }

        if (top < 0)
        {
            return false;
        }

        // the median of the (up to) seven carriers ending at the highest one measured; NaN if any of them was not
        int first = Math.Max(0, top - 6);
        int count = top + 1 - first;
        bool gap = false;
        for (int i = 0; i < count; i++)
        {
            double v = est[first + i];
            gap |= double.IsNaN(v);
            window[i] = v;
        }

        double edge;
        if (gap)
        {
            edge = double.NaN;
        }
        else
        {
            Array.Sort(window, 0, count);
            edge = (count & 1) == 1 ? window[count / 2] : (window[(count / 2) - 1] + window[count / 2]) / 2;
        }

        for (int i = 0; i < Carriers; i++)
        {
            double v = est[i];
            if (i > top)
            {
                v = edge - (20 * Math.Log10(CarrierHz[i] / CarrierHz[top])) - 3.0;
            }
            else if (double.IsNaN(v))
            {
                v = edge;
            }

            profile[i] = Math.Pow(10.0, v / 10);
        }

        return true;
    }

    /// <summary>Step down at once; step up only when the faster format has had its margin for two bursts running.</summary>
    private int? Recommend(LinkNeighbour nb)
    {
        if (!BuildProfile(nb))
        {
            return nb.Recommended;
        }

        int want = StandardFormats.Recommend(profile, nb.Margin, 0.0, nb.Cap);
        if (nb.Recommended is not int current || StandardFormats.Rate(want) <= StandardFormats.Rate(current) || nb.Measures <= Settling)
        {
            nb.Recommended = want;
            nb.Pending = null;
        }
        else if (nb.Pending is int pending)
        {
            // two bursts running have supported something faster: take the slower of the two, which both did
            nb.Recommended = StandardFormats.Rate(want) <= StandardFormats.Rate(pending) ? want : pending;
            nb.Pending = null;
        }
        else
        {
            nb.Pending = want;
        }

        return nb.Recommended;
    }

    /// <summary>int(age // 30) with floor division as the prototype's language does it for doubles.</summary>
    private static int RungsOfAge(double age)
    {
        double mod = age % FreshSeconds;
        double div = (age - mod) / FreshSeconds;
        double floor = Math.Floor(div);
        if (div - floor > 0.5)
        {
            floor += 1.0;
        }

        return (int)Math.Min(floor, int.MaxValue);
    }

    private static double[] BuildCarrierHz()
    {
        var hz = new double[Carriers];
        for (int k = 0; k < Carriers; k++)
        {
            hz[k] = (9 + k) * 48000.0 / 2048;
        }

        return hz;
    }

    private static int CarriersUpTo(double topHz)
    {
        int n = 0;
        foreach (double f in CarrierHz)
        {
            if (f <= topHz)
            {
                n++;
            }
        }

        return n;
    }
}
