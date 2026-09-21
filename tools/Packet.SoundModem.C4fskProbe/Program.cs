using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text;
using M0LTE.Dsp;
using Packet.SoundModem.Audio;
using Packet.SoundModem.CarrierSense;
using Packet.SoundModem.Modems;

// sm-c4fskprobe: a bench instrument, not a decoder. It drives a real C4fskModem over a WAV
// file one sample at a time and prints what the modem itself made of the audio: its energy
// gate, its symbol clock, its envelope tracker, the eye as its own slicer sees it, its
// decisions, every sync-word match in every timing phase's decided bit stream, and any frame
// that came out. Written for issue #518, where c4fsk9600 decodes a virtual loopback
// byte-identically and nothing at all over a real FM radio link.
//
//   sm-c4fskprobe <file.wav> [--rate 4800|9600] [--from <s>] [--to <s>]
//                            [--csv <path>] [--envelope-every <symbols>] [--channel <n>]
//                            [--lead-in <s>]
//
// --lead-in prepends that many seconds of digital silence to the audio before the modem sees
// it, which seeds the energy gate's noise floor from silence instead of from whatever the
// window happens to start with. It is a bench lever, not a fix: it FORCES the gate open so
// that the rest of the chain can be looked at on audio the gate would otherwise refuse. Every
// time printed below is a time in the original file, whatever the lead-in is.
//
// --rate is the SYMBOL rate: 4800 is NinoTNC mode 3 (c4fsk9600, 9600 bps) and is the default,
// 9600 is mode 1 (c4fsk19200, 19200 bps).

const int HuntBits = 24;
const uint SyncMask = 0xFFFFFF;
const uint SyncWord = 0x57DF7F;
const int SyncWarmupBits = 23;

if (args.Length < 1 || args[0].StartsWith("--", StringComparison.Ordinal))
{
    Console.Error.WriteLine(
        "usage: sm-c4fskprobe <file.wav> [--rate 4800|9600] [--from <s>] [--to <s>] "
        + "[--csv <path>] [--envelope-every <symbols>] [--channel <n>] [--lead-in <s>]");
    return 2;
}

string path = args[0];
int symbolRate = 4800;
double? fromSeconds = null;
double? toSeconds = null;
string? csvPath = null;
int envelopeEvery = 200;
bool envelopeEveryGiven = false;
int channel = 0;
double leadInSeconds = 0;

for (int i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--rate":
            if (!TryNextInt(args, ref i, out symbolRate) || symbolRate is not (4800 or 9600))
            {
                Console.Error.WriteLine("--rate needs 4800 or 9600 (the symbol rate)");
                return 2;
            }

            break;
        case "--from":
            if (!TryNextDouble(args, ref i, out double f))
            {
                Console.Error.WriteLine("--from needs a time in seconds");
                return 2;
            }

            fromSeconds = f;
            break;
        case "--to":
            if (!TryNextDouble(args, ref i, out double t))
            {
                Console.Error.WriteLine("--to needs a time in seconds");
                return 2;
            }

            toSeconds = t;
            break;
        case "--csv":
            if (i + 1 >= args.Length)
            {
                Console.Error.WriteLine("--csv needs a path");
                return 2;
            }

            csvPath = args[++i];
            break;
        case "--envelope-every":
            if (!TryNextInt(args, ref i, out envelopeEvery) || envelopeEvery < 1)
            {
                Console.Error.WriteLine("--envelope-every needs a symbol count");
                return 2;
            }

            envelopeEveryGiven = true;
            break;
        case "--lead-in":
            if (!TryNextDouble(args, ref i, out leadInSeconds) || leadInSeconds < 0)
            {
                Console.Error.WriteLine("--lead-in needs a non-negative time in seconds");
                return 2;
            }

            break;
        case "--channel":
            if (!TryNextInt(args, ref i, out channel) || channel < 0)
            {
                Console.Error.WriteLine("--channel needs a channel index");
                return 2;
            }

            break;
        default:
            Console.Error.WriteLine($"unknown option: {args[i]}");
            return 2;
    }
}

var (allSamples, sampleRate, channels) = WavFile.ReadChannel(path, channel);
if (sampleRate % symbolRate != 0)
{
    Console.Error.WriteLine(
        $"sample rate {sampleRate} is not a multiple of the symbol rate {symbolRate}");
    return 2;
}

int fromSample = fromSeconds is { } fs ? Math.Clamp((int)Math.Round(fs * sampleRate), 0, allSamples.Length) : 0;
int toSample = toSeconds is { } ts ? Math.Clamp((int)Math.Round(ts * sampleRate), 0, allSamples.Length) : allSamples.Length;
if (toSample <= fromSample)
{
    Console.Error.WriteLine("the --from/--to window is empty");
    return 2;
}

int windowSamples = toSample - fromSample;
double windowSeconds = (double)windowSamples / sampleRate;

// A quarter second of silence after the window so the receive filters flush and the energy
// gate closes; counted separately from the window in everything reported below.
int tailSamples = sampleRate / 4;
int leadInSamples = (int)Math.Round(leadInSeconds * sampleRate);
float[] fed = new float[leadInSamples + windowSamples + tailSamples];
Array.Copy(allSamples, fromSample, fed, leadInSamples, windowSamples);

int phases = C4fskModem.TimingPhaseCount;
int pointsPerSymbol = sampleRate / symbolRate < 8 ? (sampleRate / symbolRate) * 2 : sampleRate / symbolRate;

// ---- collectors -------------------------------------------------------------------------
var rows = new List<Row>(capacity: Math.Min(windowSamples / Math.Max(pointsPerSymbol, 1) + 16, 4_000_000));
var frames = new List<FrameRow>();
var gateSegments = new List<(long Rise, long Fall)>();
var syncHits = new List<SyncHit>[phases];
var realSync = new List<(long Symbol, long Sample)>[phases];
long[] bitIndex = new long[phases];
uint[] shift = new uint[phases];
int[] warmup = new int[phases];
long[] levelCounts = new long[4];
long frozenDecisions = 0;
long currentSample = 0;
long currentSymbol = 0;

for (int p = 0; p < phases; p++)
{
    syncHits[p] = [];
    realSync[p] = [];
}

var modem = new C4fskModem(sampleRate, _ => { }, symbolRate, crc: true);

modem.FrameDecoded += (frame, quality) =>
    frames.Add(new FrameRow(currentSample, currentSymbol, frame, quality));

modem.DecisionObserver = d =>
{
    if (d.Phase != 0)
    {
        return;
    }

    currentSymbol = d.Symbol;
    rows.Add(new Row(
        d.Symbol, currentSample, d.Normalised, d.Equalized, d.Level, d.Frozen, d.PeakHigh,
        d.PeakLow, modem.CarrierDetect));
    levelCounts[d.Level]++;
    if (d.Frozen)
    {
        frozenDecisions++;
    }
};

modem.PhaseDibitObserver = (phase, first, second) =>
{
    PushHuntBit(phase, first);
    PushHuntBit(phase, second);
};

// The modem's own Il2pReceiver instances, if they can be reached: their SyncFound watcher is
// the production answer to "did this link ever acquire", and their RsFailures counter is the
// only record of "sync found, frame unrecoverable". Chained, never replaced, so the modem's
// own frame-span marking still runs.
var receiversField = typeof(C4fskModem).GetField("_deframers", BindingFlags.NonPublic | BindingFlags.Instance);
var receivers = receiversField?.GetValue(modem) as Il2pReceiver[];
if (receivers is not null)
{
    for (int p = 0; p < receivers.Length; p++)
    {
        int phase = p;
        Action? existing = receivers[p].SyncFound;
        receivers[p].SyncFound = () =>
        {
            existing?.Invoke();
            realSync[phase].Add((currentSymbol, currentSample));
        };
    }
}

// ---- the run ----------------------------------------------------------------------------
// A replica of the modem's own front end, built from the same lines of its constructor, so the
// gate can be reported with timestamps. The modem keeps no public view of it.
//
// BOTH halves, because either opens it: the energy detector on a wired loop or a virtual cable,
// and the spectral shape detector on an open-squelch FM receiver, where a signal makes the audio
// QUIETER and the energy detector therefore never fires at all. Reporting the energy half alone
// was this instrument's own bug for a while, and it reads as "the gate never opened" on a file
// the modem is decoding perfectly well.
var replicaFilter = new FirFilter(FilterDesign.LowPass(1.5 * symbolRate, sampleRate, 48 * sampleRate / 48000));
var replicaGate = new EnergyBusyDetector(sampleRate, blockMilliseconds: 20);
var replicaShape = new FmShapeBusyDetector(sampleRate, splitHz: symbolRate, warmUpSeconds: 0.4);
long energyOnlySamples = 0;
long shapeOnlySamples = 0;

float[] one = new float[1];
bool previousBusy = false;
long riseAt = 0;
long busySamples = 0;
long busySamplesInWindow = 0;
long dcdSamples = 0;
long dcdFirstSample = -1;
long dcdLastSample = -1;

for (int i = 0; i < fed.Length; i++)
{
    currentSample = i;
    replicaGate.Process(replicaFilter.Next(fed[i]));
    one[0] = fed[i];
    replicaShape.Process(one);
    bool energy = replicaGate.Busy;
    bool shape = replicaShape.Busy == true;
    bool busy = energy || shape;
    if (energy && !shape)
    {
        energyOnlySamples++;
    }
    else if (shape && !energy)
    {
        shapeOnlySamples++;
    }

    if (busy && !previousBusy)
    {
        riseAt = i;
    }
    else if (!busy && previousBusy)
    {
        gateSegments.Add((riseAt, i));
        for (int p = 0; p < phases; p++)
        {
            shift[p] = 0;
            warmup[p] = 0;
        }
    }

    previousBusy = busy;
    if (busy)
    {
        busySamples++;
        if (i >= leadInSamples && i < leadInSamples + windowSamples)
        {
            busySamplesInWindow++;
        }
    }

    one[0] = fed[i];
    modem.Process(one);

    // DCD is only accounted inside the window: it latches while the energy gate is shut
    // (no symbols flow, so nothing can drop it), and counting the lead-in or the flush
    // tail would put the figure over 100 % of the window and date the last assert to the
    // end of the feed rather than to anything in the audio.
    if (modem.CarrierDetect && i >= leadInSamples && i < leadInSamples + windowSamples)
    {
        dcdSamples++;
        if (dcdFirstSample < 0)
        {
            dcdFirstSample = i;
        }

        dcdLastSample = i;
    }
}

if (previousBusy)
{
    gateSegments.Add((riseAt, fed.Length));
}

// ---- report -----------------------------------------------------------------------------
var output = new StringBuilder();
string mode = symbolRate == 4800 ? "c4fsk9600 (NinoTNC mode 3, 9600 bps)" : "c4fsk19200 (NinoTNC mode 1, 19200 bps)";

output.AppendLine($"sm-c4fskprobe {path}");
output.AppendLine($"  file       : {sampleRate} Hz, {channels} channel(s), {Dur(allSamples.Length)} s total, reading channel {channel}");
output.AppendLine($"  window     : {Dur(fromSample)} s to {Dur(toSample)} s ({windowSamples} samples, {windowSeconds.ToString("F4", CultureInfo.InvariantCulture)} s)");
output.AppendLine($"  plus       : {Dur(tailSamples)} s of appended silence at the end to flush the filters");
output.AppendLine($"  lead-in    : {Dur(leadInSamples)} s of digital silence prepended{(leadInSamples > 0 ? " - THE ENERGY GATE IS BEING FORCED, see --lead-in" : "")}");
output.AppendLine("  times      : every timestamp below is a time in the original file");
output.AppendLine($"  mode       : {mode}, {symbolRate} sym/s, {pointsPerSymbol} decision points/symbol, {phases} timing phases");
output.AppendLine($"  modem      : {modem.Mode}");
output.AppendLine();

// 1. ENERGY GATE
output.AppendLine("1. SIGNAL-PRESENT GATE (the hard gate on this modem's bit path)");
output.AppendLine($"   busy for {Dur(busySamplesInWindow)} s of the {windowSeconds.ToString("F4", CultureInfo.InvariantCulture)} s window = {Percent(busySamplesInWindow, windowSamples)}");
output.AppendLine($"   busy for {Dur(busySamples)} s of the {fed.Length} samples fed (lead-in + window + flush tail)");
output.AppendLine($"   gate opened {gateSegments.Count} time(s)");
output.AppendLine(
    "   which half: "
    + $"{((double)energyOnlySamples / sampleRate).ToString("F4", CultureInfo.InvariantCulture)} s "
    + "on the energy detector alone (a wired loop or a virtual cable), "
    + $"{((double)shapeOnlySamples / sampleRate).ToString("F4", CultureInfo.InvariantCulture)} s "
    + "on the spectral shape alone (an open-squelch FM receiver)");
if (gateSegments.Count == 0)
{
    output.AppendLine("   NOTHING ELSE MATTERS: the gate never opened, so no bits ever flowed.");
}
else
{
    output.AppendLine("       #   rise (s)   fall (s)   length (s)");
    int shown = 0;
    foreach (var (rise, fall) in gateSegments)
    {
        if (shown >= 40)
        {
            output.AppendLine($"       ... {gateSegments.Count - shown} more segment(s) not shown");
            break;
        }

        output.AppendLine($"     {shown + 1,3}   {At(rise),9}  {At(fall),9}   {Dur(fall - rise),9}");
        shown++;
    }
}

output.AppendLine();

// 2. SYMBOL CLOCK
output.AppendLine("2. SYMBOL CLOCK");
output.AppendLine($"   phase-0 decisions: {rows.Count} symbols");
output.AppendLine($"   implied symbol rate over the window : {Rate(rows.Count, windowSeconds)} sym/s (nominal {symbolRate})");
double gatedSeconds = (double)busySamples / sampleRate;
output.AppendLine($"   implied symbol rate over gated time : {Rate(rows.Count, gatedSeconds)} sym/s over {gatedSeconds.ToString("F4", CultureInfo.InvariantCulture)} s gated");
if (rows.Count > 0)
{
    output.AppendLine($"   first decision at {At(rows[0].Sample)} s, last at {At(rows[^1].Sample)} s");
}

if (dcdFirstSample < 0)
{
    output.AppendLine("   PacketDcd: NEVER asserted");
}
else
{
    output.AppendLine($"   PacketDcd: first asserted at {At(dcdFirstSample)} s, last asserted at {At(dcdLastSample)} s");
    output.AppendLine($"   PacketDcd: asserted for {Dur(dcdSamples)} s = {Percent(dcdSamples, windowSamples)} of the window");
}

output.AppendLine();

// 3. ENVELOPE
output.AppendLine("3. ENVELOPE (the tracked outer peaks the slicer normalises against)");
if (rows.Count == 0)
{
    output.AppendLine("   no decisions, so no envelope readings");
}
else
{
    int every = envelopeEvery;
    if (!envelopeEveryGiven)
    {
        int blocks = (rows.Count + every - 1) / every;
        if (blocks > 80)
        {
            int factor = (blocks + 79) / 80;
            every = envelopeEvery * factor;
            output.AppendLine($"   block widened from 200 to {every} symbols to keep the report readable (pass --envelope-every 200 to force)");
        }
    }

    output.AppendLine("   block starts at        peakHigh (min/mean/max)          peakLow (min/mean/max)           half   mid");
    for (int start = 0; start < rows.Count; start += every)
    {
        int end = Math.Min(start + every, rows.Count);
        float hiMin = float.MaxValue, hiMax = float.MinValue, loMin = float.MaxValue, loMax = float.MinValue;
        double hiSum = 0, loSum = 0;
        for (int i = start; i < end; i++)
        {
            float hi = rows[i].PeakHigh, lo = rows[i].PeakLow;
            hiMin = Math.Min(hiMin, hi); hiMax = Math.Max(hiMax, hi); hiSum += hi;
            loMin = Math.Min(loMin, lo); loMax = Math.Max(loMax, lo); loSum += lo;
        }

        double hiMean = hiSum / (end - start), loMean = loSum / (end - start);
        output.AppendLine(
            $"   sym {rows[start].Symbol,8} {At(rows[start].Sample),9}s  "
            + $"{F4(hiMin)} {F4(hiMean)} {F4(hiMax)}   "
            + $"{F4(loMin)} {F4(loMean)} {F4(loMax)}   "
            + $"{F4((hiMean - loMean) * 0.5)} {F4((hiMean + loMean) * 0.5)}");
    }
}

output.AppendLine();

// 4. LEVEL HISTOGRAM
output.AppendLine("4. LEVEL HISTOGRAM at phase 0 (this IS the eye as the modem's own slicer sees it)");
output.AppendLine("   slice boundaries are at -0.667, 0.000 and +0.667; clean outer levels sit near -1.000 and +1.000,");
output.AppendLine("   clean inner levels near -0.333 and +0.333.");
output.AppendLine();
output.AppendLine("   Normalised (slicer input, in units of the tracked envelope), ALL phase-0 decisions:");
Histogram(output, rows, r => r.Normalised);
output.AppendLine();
output.AppendLine("   Equalized (what the 5-tap decision-directed equalizer made of it, the value actually sliced), ALL:");
Histogram(output, rows, r => r.Equalized);
output.AppendLine();

// The energy gate holds open past the end of a burst, and the decisions taken in that tail
// are of silence: normalised collapses to the midpoint and every one of them slices as
// +inner. They are real decisions and belong in the totals above, but they are not the eye.
// PacketDcd bounds the part of the run that was actually carrying a signal.
var carrying = rows.FindAll(r => r.Dcd);
output.AppendLine($"   Normalised, restricted to the {carrying.Count} decisions taken while PacketDcd was asserted:");
Histogram(output, carrying, r => r.Normalised);
output.AppendLine();

// 5. DECISIONS
output.AppendLine("5. DECISIONS at phase 0");
string[] levelNames = ["0 = -outer (-1)  ", "1 = -inner (-1/3)", "2 = +inner (+1/3)", "3 = +outer (+1)  "];
for (int level = 0; level < 4; level++)
{
    output.AppendLine($"   level {levelNames[level]} : {levelCounts[level],10}  {Percent(levelCounts[level], rows.Count)}");
}

output.AppendLine($"   equalizer frozen for {frozenDecisions} of {rows.Count} decisions = {Percent(frozenDecisions, rows.Count)}");
output.AppendLine();

// 6. SYNC
output.AppendLine("6. SYNC (24-bit word 0x57DF7F, Hamming distance <= 1, direct or complemented,");
output.AppendLine("   hunted independently over every timing phase's decided bit stream - the same test");
output.AppendLine("   Il2pReceiver applies, including its 23-bit warm-up and its reset on the gate's falling edge)");
long totalHits = 0;
for (int p = 0; p < phases; p++)
{
    totalHits += syncHits[p].Count;
}

if (totalHits == 0)
{
    output.AppendLine("   NO SYNC WORD MATCHED ANYWHERE, IN ANY PHASE, IN EITHER POLARITY.");
    output.AppendLine("   Acquisition never happens: the payload is not the problem.");
}
else
{
    var fractions = C4fskModem.TimingPhaseFractions;
    for (int p = 0; p < phases; p++)
    {
        output.AppendLine($"   phase {p} (offset {fractions[p].ToString("+0.000;-0.000", CultureInfo.InvariantCulture)} symbols): {syncHits[p].Count} match(es)");
        int shown = 0;
        foreach (SyncHit hit in syncHits[p])
        {
            if (shown >= 20)
            {
                output.AppendLine($"       ... {syncHits[p].Count - shown} more not shown");
                break;
            }

            output.AppendLine(
                $"       bit {hit.BitIndex,9}  symbol {hit.Symbol,8}  t={At(hit.Sample)}s  "
                + $"{(hit.Inverted ? "inverted" : "direct  ")}  {hit.Errors} bit error(s)");
            shown++;
        }
    }
}

if (receivers is not null)
{
    output.AppendLine();
    output.AppendLine("   the modem's own Il2pReceiver watchers (production path):");
    for (int p = 0; p < phases; p++)
    {
        string first = realSync[p].Count == 0
            ? "never"
            : $"first at symbol {realSync[p][0].Symbol}, t={At(realSync[p][0].Sample)}s";
        output.AppendLine(
            $"       phase {p}: SyncFound {realSync[p].Count} time(s), {first}; "
            + $"RsFailures {receivers[p].RsFailures}, CrcFailures {receivers[p].CrcFailures}");
    }
}
else
{
    output.AppendLine();
    output.AppendLine("   (the modem's own Il2pReceiver instances were not reachable; the hunt above stands alone)");
}

output.AppendLine();

// 7. FRAMES
output.AppendLine("7. FRAMES");
if (frames.Count == 0)
{
    output.AppendLine("   none decoded");
}
else
{
    foreach (FrameRow frame in frames)
    {
        FrameQuality q = frame.Quality;
        output.AppendLine(
            $"   t={At(frame.Sample)}s symbol {frame.Symbol}: {q.FrameBytes} bytes, "
            + $"corrected {(q.CorrectedBytes is { } c ? c.ToString(CultureInfo.InvariantCulture) : "n/a")}, "
            + $"crc {(q.CrcValid is { } v ? (v ? "ok" : "BAD") : "n/a")}, "
            + $"header {(q.HeaderType is { } h ? h.ToString() : "n/a")}, "
            + $"plainIl2p {q.PlainIl2p}, monitorOnly {q.MonitorOnly}");
        output.AppendLine($"       {FormatFrame(frame.Payload)}");
    }
}

Console.Write(output.ToString());

if (csvPath is not null)
{
    using var writer = new StreamWriter(csvPath);
    writer.WriteLine("symbol,sample,seconds,normalised,equalized,level,frozen,peakHigh,peakLow,dcd");
    foreach (Row r in rows)
    {
        writer.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{r.Symbol},{r.Sample},{(double)(r.Sample - leadInSamples + fromSample) / sampleRate:F6},{r.Normalised:R},{r.Equalized:R},{r.Level},{(r.Frozen ? 1 : 0)},{r.PeakHigh:R},{r.PeakLow:R},{(r.Dcd ? 1 : 0)}"));
    }

    Console.WriteLine();
    Console.WriteLine($"wrote {rows.Count} phase-0 decision rows to {csvPath}");
}

return 0;

void PushHuntBit(int phase, int bit)
{
    shift[phase] = ((shift[phase] << 1) | (uint)(bit & 1)) & SyncMask;
    bitIndex[phase]++;
    if (warmup[phase] < SyncWarmupBits)
    {
        warmup[phase]++;
        return;
    }

    int direct = BitOperations.PopCount(shift[phase] ^ SyncWord);
    int inverted = BitOperations.PopCount((~shift[phase] & SyncMask) ^ SyncWord);
    if (direct <= 1)
    {
        syncHits[phase].Add(new SyncHit(bitIndex[phase] - HuntBits, currentSymbol, currentSample, false, direct));
    }
    else if (inverted <= 1)
    {
        syncHits[phase].Add(new SyncHit(bitIndex[phase] - HuntBits, currentSymbol, currentSample, true, inverted));
    }
}

// A duration, in seconds.
string Dur(long samples) => ((double)samples / sampleRate).ToString("F4", CultureInfo.InvariantCulture);

// A position in the fed stream, as a time in the ORIGINAL file: the lead-in comes off and the
// window's own offset goes back on, so every timestamp printed can be looked up in the WAV.
string At(long fedIndex) =>
    ((double)(fedIndex - leadInSamples + fromSample) / sampleRate).ToString("F4", CultureInfo.InvariantCulture);

static string Percent(long part, long whole) =>
    whole == 0 ? "n/a" : (100.0 * part / whole).ToString("F2", CultureInfo.InvariantCulture) + " %";

static string Rate(long count, double seconds) =>
    seconds <= 0 ? "n/a" : (count / seconds).ToString("F1", CultureInfo.InvariantCulture);

// A frame as SRC>DEST[,digis]:info, for eyeballing. Same shape as sm-decode's, kept local
// because that one is a private type of that tool.
static string FormatFrame(byte[] frame)
{
    if (frame.Length < 15)
    {
        return Convert.ToHexString(frame);
    }

    static string Call(ReadOnlySpan<byte> address)
    {
        char[] chars = new char[6];
        for (int i = 0; i < 6; i++)
        {
            chars[i] = (char)(address[i] >> 1);
        }

        int ssid = (address[6] >> 1) & 0xF;
        string call = new string(chars).TrimEnd();
        return ssid == 0 ? call : $"{call}-{ssid}";
    }

    string dest = Call(frame.AsSpan(0, 7));
    string source = Call(frame.AsSpan(7, 7));
    int position = 14;
    var digis = new List<string>();
    while ((frame[position - 1] & 0x01) == 0 && position + 7 <= frame.Length)
    {
        digis.Add(Call(frame.AsSpan(position, 7)));
        position += 7;
    }

    string via = digis.Count > 0 ? "," + string.Join(',', digis) : "";
    string payload = position + 2 <= frame.Length
        ? Encoding.Latin1.GetString(frame, position + 2, frame.Length - position - 2)
        : "";
    var printable = new StringBuilder();
    foreach (char c in payload)
    {
        printable.Append(c is >= ' ' and <= '~' ? c : '.');
    }

    return $"{source}>{dest}{via}:{printable}";
}

static string F4(double value) => value.ToString("+0.0000;-0.0000", CultureInfo.InvariantCulture);

static bool TryNextInt(string[] args, ref int i, out int value)
{
    value = 0;
    return i + 1 < args.Length
        && int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
}

static bool TryNextDouble(string[] args, ref int i, out double value)
{
    value = 0;
    return i + 1 < args.Length
        && double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}

static void Histogram(StringBuilder into, List<Row> rows, Func<Row, float> select)
{
    const double low = -1.5, high = 1.5, width = 0.05;
    int bins = (int)Math.Round((high - low) / width);
    long[] counts = new long[bins];
    long under = 0, over = 0;
    double sum = 0;
    foreach (Row row in rows)
    {
        double value = select(row);
        sum += value;
        int bin = (int)Math.Floor((value - low) / width);
        if (bin < 0)
        {
            under++;
        }
        else if (bin >= bins)
        {
            over++;
        }
        else
        {
            counts[bin]++;
        }
    }

    if (rows.Count == 0)
    {
        into.AppendLine("      (no decisions)");
        return;
    }

    long peak = 1;
    foreach (long count in counts)
    {
        peak = Math.Max(peak, count);
    }

    into.AppendLine($"      below -1.50: {under,8}  ({(100.0 * under / rows.Count).ToString("F2", CultureInfo.InvariantCulture)} %)");
    for (int bin = 0; bin < bins; bin++)
    {
        double edge = low + (bin * width);
        int bar = (int)Math.Round(50.0 * counts[bin] / peak);
        string marker = Math.Abs(edge - -0.65) < 1e-9 || Math.Abs(edge) < 1e-9 || Math.Abs(edge - 0.65) < 1e-9
            ? "|"
            : " ";
        into.AppendLine($"      {edge,6:F2} {marker} {counts[bin],8}  {new string('#', bar)}");
    }

    into.AppendLine($"      above +1.50: {over,8}  ({(100.0 * over / rows.Count).ToString("F2", CultureInfo.InvariantCulture)} %)");
    into.AppendLine($"      mean {(sum / rows.Count).ToString("F4", CultureInfo.InvariantCulture)}");
}

internal readonly record struct Row(
    long Symbol, long Sample, float Normalised, float Equalized, int Level, bool Frozen,
    float PeakHigh, float PeakLow, bool Dcd);

internal readonly record struct SyncHit(long BitIndex, long Symbol, long Sample, bool Inverted, int Errors);

internal readonly record struct FrameRow(long Sample, long Symbol, byte[] Payload, FrameQuality Quality);
