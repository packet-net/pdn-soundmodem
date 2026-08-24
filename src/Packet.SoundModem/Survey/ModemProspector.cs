using Packet.SoundModem.Modems;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Survey;

/// <summary>How the prospector is set up. Policy, not measurement.</summary>
public sealed class ModemProspectorOptions
{
    /// <summary>How close two readings must sit to be the same station's traffic. A quarter of
    /// a kilohertz, matching the survey's own cooldown bucket: two packet signals that close
    /// together are one modem's problem, not two.</summary>
    public double ClusterHz { get; set; } = 250;

    /// <summary>
    /// Separate captures a cluster needs before it is proposed - separate transmissions, on
    /// separate occasions. One decode is a decode; a modem slot is a standing commitment.
    /// </summary>
    /// <remarks>
    /// <b>Captures, not distinct frames.</b> The obvious gate is "how many different frames",
    /// and it is wrong here: the traffic this is for is largely beacons, and a beacon is the
    /// same bytes every twenty minutes for ever. PD4R-12 - the station that prompted all of
    /// this - would never have been proposed under a distinct-frame gate, having sent one
    /// frame's worth of bytes several hundred times. What a repeated identical beacon actually
    /// evidences is a station that is reliably there, which is exactly what a modem slot is
    /// being asked to commit to.
    /// </remarks>
    public int MinCaptures { get; set; } = 3;

    /// <summary>Clusters kept. A busy band has a tail of one-off readings and an operator has
    /// four slots; past this the weakest are dropped rather than held for ever.</summary>
    public int MaxClusters { get; set; } = 32;

    /// <summary>Clock, injected for tests.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
}

/// <summary>
/// Turns the survey's captures into the question an operator actually has: what should this
/// station be listening to that it is not?
/// </summary>
/// <remarks>
/// <para>
/// The survey answers "something went past that I could not read" and stops there. That is a
/// diagnosis with no prescription, and on the live 40 m station it produced 8,836 unclaimed and
/// 5,402 missed captures in three weeks - a number nobody is going to listen to by hand. Two of
/// them, opened by hand on 2026-08-24, turned out to be the same station beaconing every twenty
/// minutes in a mode the station could read and was not configured for.
/// </para>
/// <para>
/// So: read each capture with every mode that could have carried it (<see cref="CaptureSweep"/>),
/// pointed at the centre the survey already measured; cluster what decodes by mode and
/// frequency; and once a cluster has enough behind it, propose the modem that would have read
/// it. Which is the same reasoning an operator does with a decoder and an afternoon, done
/// continuously and without the afternoon.
/// </para>
/// <para>
/// <b>Two shapes of answer, and the second is easy to get wrong.</b> Traffic on a clear
/// frequency wants a new modem. Traffic <em>inside</em> a configured modem's band that it cannot
/// read wants a framing change, because moving anything would be moving the wrong thing - the
/// PD4R-12 case, where a plain-AX.25 station sat squarely inside an IL2P+CRC modem's passband
/// and was audible, detected and unreadable for a month.
/// </para>
/// <para>
/// <b>Threading.</b> This type is not thread-safe and expects to be driven from one worker (see
/// the daemon's prospector worker, which owns the throttle). It never touches the receive path.
/// </para>
/// </remarks>
public sealed class ModemProspector
{
    private readonly ModemProspectorOptions _options;
    private readonly IReadOnlyList<ModemBand> _bands;
    private readonly double? _dialHz;
    private readonly string _sideband;
    private readonly List<Cluster> _clusters = [];
    private long _examined;
    private long _read;

    /// <param name="options">Policy.</param>
    /// <param name="bands">The modems the station is running, so a proposal can say whether the
    /// frequency is clear or the framing is the problem.</param>
    /// <param name="dialFrequencyHz">The dial, for turning an audio centre into the band
    /// frequency a config file wants; 0 for a station in audio frequencies only.</param>
    /// <param name="sideband">"usb" or "lsb", for the same.</param>
    public ModemProspector(
        ModemProspectorOptions options,
        IReadOnlyList<ModemBand> bands,
        double dialFrequencyHz = 0,
        string sideband = "usb")
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(bands);
        _options = options;
        _bands = bands;
        _dialHz = dialFrequencyHz == 0 ? null : dialFrequencyHz;
        _sideband = sideband;
    }

    /// <summary>Captures examined.</summary>
    public long Examined => _examined;

    /// <summary>Captures that yielded at least one frame - the interesting number, and the one
    /// that says whether this is earning its CPU.</summary>
    public long Read => _read;

    /// <summary>Raised when a cluster crosses its evidence thresholds and becomes a proposal, so
    /// a station can say so at the moment it knows rather than when somebody next asks.</summary>
    public event Action<ModemProposal>? Proposed;

    /// <summary>
    /// Raised for every capture examined, with whatever came out of it - empty included.
    /// </summary>
    /// <remarks>
    /// Work that leaves no trace until it succeeds is indistinguishable from work that is not
    /// happening, and it was: asked "any evidence it was picked up?" about a capture on the live
    /// station, the honest answer was a thread's CPU counter in <c>/proc</c>. A station gets to
    /// say what it looked at.
    /// </remarks>
    public event Action<BurstCapture, IReadOnlyList<CaptureReading>>? ExaminedCapture;

    /// <summary>
    /// Examines one capture: reads it every way it might have been sent, and files what comes
    /// back. Returns the readings, for a caller that wants to log them.
    /// </summary>
    /// <param name="capture">The sidecar the survey wrote, which carries the measured centre.</param>
    /// <param name="audio">Its audio.</param>
    /// <param name="modes">Modes to try; <see cref="CaptureSweep.ModesFor"/> if null.</param>
    /// <param name="shouldStop">Polled between modes, so shutdown is prompt.</param>
    public IReadOnlyList<CaptureReading> Examine(
        BurstCapture capture,
        float[] audio,
        IReadOnlyList<string>? modes = null,
        Func<bool>? shouldStop = null)
    {
        ArgumentNullException.ThrowIfNull(capture);
        _examined++;

        IReadOnlyList<CaptureReading> readings = CaptureSweep.Run(
            audio,
            capture.SampleRate,
            capture.AudioCentreHz,
            modes ?? CaptureSweep.ModesFor(capture.SampleRate),
            shouldStop);

        if (readings.Count > 0)
        {
            _read++;
            Record(capture, Best(readings));
        }

        ExaminedCapture?.Invoke(capture, readings);
        return readings;
    }

    /// <summary>
    /// Everything worth filing out of one capture's readings: the best reading of each distinct
    /// frame.
    /// </summary>
    /// <remarks>
    /// Several modes reading one burst is the normal case rather than a problem - a diversity
    /// bank and its single-branch sibling both read plain AX.25 - and filing all of them would
    /// count one transmission as evidence for four different modems. The winner is the mode that
    /// read it most confidently: a verified CRC beats Reed-Solomon standing alone, and both beat
    /// a frame the receiver read and would not have handed to a host.
    /// </remarks>
    private static IEnumerable<CaptureReading> Best(IReadOnlyList<CaptureReading> readings)
    {
        var byFrame = new Dictionary<string, CaptureReading>(StringComparer.Ordinal);
        foreach (CaptureReading reading in readings)
        {
            string key = Convert.ToHexString(reading.Frame);
            if (!byFrame.TryGetValue(key, out CaptureReading? held) || Rank(reading) < Rank(held))
            {
                byFrame[key] = reading;
            }
        }

        return byFrame.Values;
    }

    private static int Rank(CaptureReading reading) => DecodeConfidence.Rank(reading.Quality);

    /// <summary>Whether a reading is evidence somebody transmitted - see
    /// <see cref="DecodeConfidence.IsEvidence"/>, which the pdn-decode report shares.</summary>
    private static bool IsEvidence(CaptureReading reading) =>
        DecodeConfidence.IsEvidence(reading.Quality);

    private void Record(BurstCapture capture, IEnumerable<CaptureReading> readings)
    {
        foreach (IGrouping<string, CaptureReading> byMode in readings.GroupBy(r => r.Mode, StringComparer.Ordinal))
        {
            Cluster? cluster = _clusters.Find(
                c => string.Equals(c.Mode, byMode.Key, StringComparison.Ordinal)
                    && Math.Abs(c.MeanCentreHz - capture.AudioCentreHz) <= _options.ClusterHz / 2);

            if (cluster is null)
            {
                cluster = new Cluster(byMode.Key);
                _clusters.Add(cluster);
            }

            // One capture is one transmission however many frames came out of it, which is what
            // makes the count a count of occasions rather than of bytes.
            bool wasProposal = IsProposal(cluster);
            cluster.Add(capture, byMode);
            Trim();

            if (!wasProposal && IsProposal(cluster))
            {
                Proposed?.Invoke(Build(cluster));
            }
        }
    }

    private bool IsProposal(Cluster cluster) => cluster.EvidencedCaptures >= _options.MinCaptures;

    /// <summary>
    /// How close the traffic in <paramref name="reading"/> is to being proposed: the occasions
    /// banked so far and the occasions needed. Null when the reading is not the kind that counts
    /// (see <see cref="IsEvidence"/>) - which is itself worth being told, since a station reading
    /// something every ten minutes and never proposing it is otherwise a mystery.
    /// </summary>
    public (int Banked, int Needed)? Progress(BurstCapture capture, CaptureReading reading)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(reading);
        if (!IsEvidence(reading))
        {
            return null;
        }

        Cluster? cluster = _clusters.Find(
            c => string.Equals(c.Mode, reading.Mode, StringComparison.Ordinal)
                && Math.Abs(c.MeanCentreHz - capture.AudioCentreHz) <= _options.ClusterHz / 2);

        return cluster is null ? null : (cluster.EvidencedCaptures, _options.MinCaptures);
    }

    /// <summary>Drops the weakest clusters once there are more than the budget allows.</summary>
    private void Trim()
    {
        if (_clusters.Count <= _options.MaxClusters)
        {
            return;
        }

        _clusters.Sort((a, b) => b.Captures.CompareTo(a.Captures));
        _clusters.RemoveRange(_options.MaxClusters, _clusters.Count - _options.MaxClusters);
    }

    /// <summary>Everything with enough behind it, best-evidenced first.</summary>
    public IReadOnlyList<ModemProposal> Proposals()
    {
        var built = new List<ModemProposal>();
        foreach (Cluster cluster in _clusters)
        {
            if (IsProposal(cluster))
            {
                built.Add(Build(cluster));
            }
        }

        // Framing changes first: they are the cheapest to act on (nothing moves) and they are
        // the case an operator is least likely to have worked out unaided.
        built.Sort((a, b) => a.Kind != b.Kind
            ? b.Kind.CompareTo(a.Kind)
            : b.Captures.CompareTo(a.Captures));
        return built;
    }

    private ModemProposal Build(Cluster cluster)
    {
        double centre = cluster.MeanCentreHz;

        // Which configured modems could already hear this. A band overlapping the traffic means
        // the station is listening there and reading nothing, which is a different problem with
        // a different answer from an empty frequency.
        var conflicts = new List<int>();
        foreach (ModemBand band in _bands)
        {
            if (centre >= band.LowHz && centre <= band.HighHz)
            {
                conflicts.Add(band.SubChannel);
            }
        }

        return new ModemProposal(
            cluster.Mode,
            Math.Round(centre, 1),
            _dialHz is double dial
                ? Math.Round(
                    string.Equals(_sideband, "lsb", StringComparison.OrdinalIgnoreCase)
                        ? dial - centre
                        : dial + centre,
                    1)
                : null,
            conflicts.Count > 0 ? ProposalKind.FramingChange : ProposalKind.NewModem,
            cluster.Captures,
            cluster.Frames,
            cluster.Stations(),
            Math.Round(cluster.MeanSnrDb, 1),
            cluster.FirstHeard,
            cluster.LastHeard,
            conflicts);
    }

    /// <summary>One mode heard repeatedly in one part of the passband.</summary>
    private sealed class Cluster(string mode)
    {
        private double _centreSum;
        private double _snrSum;

        public string Mode { get; } = mode;

        /// <summary>Captures this mode read anything at all out of.</summary>
        public int Captures { get; private set; }

        /// <summary>Captures it read a frame out of whose own check sequence verified - the
        /// count a proposal rests on. See <see cref="IsEvidence"/>.</summary>
        public int EvidencedCaptures { get; private set; }

        /// <summary>Frames read, in total. Reported rather than gated on: the traffic this
        /// exists to find is largely beacons, and a beacon's bytes never change.</summary>
        public int Frames { get; private set; }

        public Dictionary<string, int> Heard { get; } = new(StringComparer.OrdinalIgnoreCase);

        public DateTimeOffset FirstHeard { get; private set; } = DateTimeOffset.MaxValue;

        public DateTimeOffset LastHeard { get; private set; } = DateTimeOffset.MinValue;

        public double MeanCentreHz => Captures == 0 ? 0 : _centreSum / Captures;

        public double MeanSnrDb => Captures == 0 ? 0 : _snrSum / Captures;

        public void Add(BurstCapture capture, IEnumerable<CaptureReading> readings)
        {
            Captures++;
            _centreSum += capture.AudioCentreHz;
            _snrSum += capture.PeakSnrDb;
            FirstHeard = capture.CapturedAt < FirstHeard ? capture.CapturedAt : FirstHeard;
            LastHeard = capture.CapturedAt > LastHeard ? capture.CapturedAt : LastHeard;

            bool evidenced = false;
            foreach (CaptureReading reading in readings)
            {
                Frames++;
                evidenced |= IsEvidence(reading);
                if (reading.Source is string source)
                {
                    Heard[source] = Heard.GetValueOrDefault(source) + 1;
                }
            }

            if (evidenced)
            {
                EvidencedCaptures++;
            }
        }

        public IReadOnlyList<string> Stations()
        {
            var names = new List<string>(Heard.Keys);
            names.Sort((a, b) => Heard[b] != Heard[a]
                ? Heard[b].CompareTo(Heard[a])
                : string.CompareOrdinal(a, b));
            return names;
        }
    }
}
