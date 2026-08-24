using Packet.SoundModem.Modems;

namespace Packet.SoundModem.MultiDecode;

/// <summary>One entry in the sweep: a catalogue mode plus the knobs to run it with.</summary>
/// <param name="Label">What the report calls this attempt. Usually the mode name, but a mode run
/// twice under different options needs telling apart, so it is separate from
/// <paramref name="Mode"/>.</param>
/// <param name="Mode">The <see cref="ModemCatalog"/> mode string.</param>
/// <param name="Options">Per-mode knobs, or <c>default</c> for the mode's documented defaults.</param>
internal sealed record SweepEntry(string Label, string Mode, ModemOptions Options = default);

/// <summary>One frame, as one sweep entry heard it.</summary>
/// <param name="Frame">The decoded frame bytes.</param>
/// <param name="Quality">The receiver's own diagnostics for this decode.</param>
/// <param name="Label">The sweep entry that produced it.</param>
/// <param name="Order">Position in the sweep's overall decode order, for stable reporting.</param>
internal sealed record Decode(byte[] Frame, FrameQuality Quality, string Label, int Order);

/// <summary>A sweep entry that could not be run at all, and why.</summary>
/// <param name="Label">The sweep entry.</param>
/// <param name="Mode">Its catalogue mode, for reporting a family of them together.</param>
/// <param name="Reason">What the modem said.</param>
/// <param name="OutOfBand">Whether the only thing wrong was where it was asked to sit: a
/// 2.8 kHz waveform cannot be centred at 1100 Hz without running off the bottom of the
/// passband. Expected rather than interesting, so the report collapses these into one line
/// instead of a screen of them.</param>
internal sealed record SweepFailure(
    string Label, string Mode, string Reason, bool OutOfBand = false);

/// <summary>Everything one file's sweep produced.</summary>
internal sealed record SweepResult(
    IReadOnlyList<Decode> Decodes,
    IReadOnlyList<SweepFailure> Failures,
    IReadOnlyList<string> Silent,
    TimeSpan Elapsed);

/// <summary>
/// Runs every mode in a sweep set over one file's audio and collects what each one heard.
/// </summary>
/// <remarks>
/// <para>The reading of any one mode is <see cref="ModeReader"/>'s, which the station's own
/// <see cref="Packet.SoundModem.Survey.CaptureSweep"/> shares: the choice to listen on
/// <see cref="IModem.FrameDecoded"/> rather than the frame sink, the block size a diversity bank
/// needs, and the flush that gets the last frame out of the pipeline are decisions that must not
/// differ between what this tool reports and what a station acts on. What is this class's own is
/// the <em>set</em>: which modes, at which centres, under which detector, and how the answers
/// are attributed and counted.</para>
/// <para>The plain-IL2P tolerance is left at its default rather than switched on: turning it on
/// would deliver monitor-only frames and hide the fact that a real link would not have.</para>
/// <para>Modes run one after another over the whole file rather than together over a stream.
/// Nothing here is real time, and a bank that has seen the entire recording is strictly better
/// placed than one racing it.</para>
/// </remarks>
internal static class Sweep
{
    /// <summary>
    /// Runs <paramref name="entries"/> over <paramref name="samples"/>.
    /// </summary>
    /// <param name="samples">The recording, as normalised floats.</param>
    /// <param name="sampleRate">The recording's own sample rate.</param>
    /// <param name="entries">The modes to try.</param>
    /// <param name="progress">Called with each label as it starts, for a status line.</param>
    public static SweepResult Run(
        float[] samples, int sampleRate, IReadOnlyList<SweepEntry> entries, Action<string>? progress = null)
    {
        long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();

        var decodes = new List<Decode>();
        var failures = new List<SweepFailure>();
        var silent = new List<string>();

        // Two distinct DSP rates across the whole catalogue (12000 and 48000), so this caches at
        // most two conversions per file however many modes are in the sweep.
        var byRate = new Dictionary<int, float[]>();

        foreach (SweepEntry entry in entries)
        {
            progress?.Invoke(entry.Label);
            int dspRate = ModemCatalog.DspRateFor(entry.Mode);

            float[] audio;
            try
            {
                if (!byRate.TryGetValue(dspRate, out float[]? converted))
                {
                    converted = Resampler.Resample(samples, sampleRate, dspRate);

                    // Half a second of silence so a file that ends flush with its last closing
                    // flag still flushes that frame out of the demodulator's FIR pipeline. A live
                    // stream never ends, so no modem does this for itself.
                    Array.Resize(ref converted, converted.Length + (dspRate / 2));
                    byRate[dspRate] = converted;
                }

                audio = converted;
            }
            catch (InvalidOperationException error)
            {
                failures.Add(new SweepFailure(entry.Label, entry.Mode, error.Message));
                continue;
            }

            int before = decodes.Count;
            try
            {
                RunOne(entry, audio, dspRate, decodes);
            }
            catch (ArgumentException error)
                when (string.Equals(error.ParamName, "centreHz", StringComparison.Ordinal))
            {
                // The mode is wider than the room left either side of the centre it was given.
                // Nothing is wrong with the sweep or the file, so this is not news.
                failures.Add(new SweepFailure(entry.Label, entry.Mode, error.Message, OutOfBand: true));
                continue;
            }
#pragma warning disable CA1031 // one broken mode must not take the rest of the sweep down with it
            catch (Exception error)
#pragma warning restore CA1031
            {
                failures.Add(new SweepFailure(entry.Label, entry.Mode, error.Message));
                continue;
            }

            if (decodes.Count == before)
            {
                silent.Add(entry.Label);
            }
        }

        return new SweepResult(
            decodes, failures, silent, System.Diagnostics.Stopwatch.GetElapsedTime(startedAt));
    }

    private static void RunOne(SweepEntry entry, float[] audio, int dspRate, List<Decode> decodes) =>
        ModeReader.Run(
            entry.Mode,
            audio,
            dspRate,
            entry.Options,
            (frame, quality) => decodes.Add(new Decode(frame, quality, entry.Label, decodes.Count)),

            // The caller already padded, once, for every mode at this rate.
            flushSilence: false);

    /// <summary>
    /// The HF data waveform families, which <c>--packet</c> leaves out. Prefixes, so every
    /// waveform number and datac mode goes with its family.
    /// </summary>
    /// <remarks>
    /// These are not packet-radio-lineage modes and they are the expensive half of the catalogue
    /// to run (the MS110D MFB receiver alone is most of a whole-catalogue sweep's wall clock).
    /// Nothing coming off a VHF or UHF radio is one of them, which is the whole basis for offering
    /// the narrowing - the default sweep runs them anyway.
    /// </remarks>
    public static readonly string[] HfWaveformPrefixes = ["freedv-", "ms110d-"];

    /// <summary>
    /// Every packet mode in the catalogue - the whole NinoTNC-lineage set, AFSK and GFSK and
    /// C4FSK and the shaped-PSK family - leaving out only the HF data waveforms in
    /// <see cref="HfWaveformPrefixes"/>. The <c>--packet</c> sweep: most of the answer for a fraction
    /// of <see cref="AllModes"/>'s running time, since the MS110D MFB receiver alone is most of a
    /// whole-catalogue sweep's wall clock.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this is not the FM-native set, and why neither is the default.</b> The obvious reading of "it came off an FM
    /// radio" is <see cref="FmModeProfiles.IsFmMode"/>, and that reading is wrong here. That table
    /// answers "which modes reach the air as frequency modulation", which is a question about
    /// modulators and deviation targets; the question this tool is asked is "what can arrive
    /// through an FM receiver", and the shaped-PSK modes answer yes to the second and no to the
    /// first. Nino's own switch map says so outright - switch 1000 is grouped "Shaped PSK - SSB
    /// radios, or FM radios" (`docs/mode-modulation-reference.md`). It is not a hypothetical: the
    /// first real off-air corpus this tool was pointed at turned out to be <c>bpsk1200</c> through
    /// an FM radio, and an FM-native sweep read exactly none of it while the wider one recovered a
    /// whole BPQ chat session. That is the argument for sweeping wide, and it is why the tool now
    /// defaults to <see cref="AllModes"/> outright: the cost of guessing the set wrong is a silent
    /// miss, and the cost of guessing it too wide is seconds.</para>
    /// <para>Stated as an exclusion rather than a list so a mode added to the catalogue joins this
    /// sweep automatically. That is the safe direction for a tool whose failure mode is a silent
    /// miss, and <c>SweepTests</c> pins it.</para>
    /// <para><c>qpsk3600</c> runs twice. Its default detector is differential and that is the
    /// right default (the catalogue's own note records differential copying 9 of 9 corpus files
    /// where coherent copied 0 to 2 of 3), but a file being swept here is by definition one
    /// something else struggled with, which is the case where the losing branch occasionally
    /// wins. It costs one more pass over a short file.</para>
    /// </remarks>
    public static IReadOnlyList<SweepEntry> PacketModes() =>
        Build(ModemCatalog.KnownModes.Where(IsPacketMode));

    /// <summary>The modes that reach the air as frequency modulation, per
    /// <see cref="FmModeProfiles"/>: a narrower and faster sweep for a recording you already know
    /// is an FM-native mode. Read <see cref="PacketModes"/> before reaching for it - this set does
    /// not include the shaped-PSK modes, which an FM radio carries perfectly well.</summary>
    public static IReadOnlyList<SweepEntry> FmNativeModes() =>
        Build(ModemCatalog.KnownModes.Where(FmModeProfiles.IsFmMode));

    /// <summary>Every built-in mode, HF data waveforms included. The default: the whole point of
    /// this tool is not having to have guessed right, and on a short file the widest net costs
    /// only wall clock.</summary>
    public static IReadOnlyList<SweepEntry> AllModes() => Build(ModemCatalog.KnownModes);

    /// <summary>Whether a mode is packet-radio lineage rather than an HF data waveform.</summary>
    public static bool IsPacketMode(string mode) =>
        !HfWaveformPrefixes.Any(prefix => mode.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>
    /// Whether a mode's receiver has a <see cref="PskDetector"/> to choose, and so is worth
    /// running a second time under the other one.
    /// </summary>
    /// <remarks>
    /// Every PSK mode runs twice, differential and coherent. Differential is the catalogue
    /// default and is the right default - it was measured to copy 9 of 9 of the NinoTNC corpus
    /// where coherent copied 0 to 2 of 3, and BPSK was reversed to it in issues #40/#42 because
    /// coherent's narrow Costas loop cannot acquire real carriers. But a file reaching this tool
    /// is by definition one something else could not read, which is exactly the case where the
    /// losing branch occasionally wins: coherent's advantage is a decibel or two when it can
    /// lock, and a long burst sitting on an on-frequency carrier is where it can. Running both
    /// costs one more pass over a short file and removes a whole class of "we did not try".
    /// </remarks>
    private static bool HasDetectorChoice(string mode) =>
        mode.StartsWith("bpsk", StringComparison.Ordinal)
        || mode.StartsWith("qpsk", StringComparison.Ordinal);

    private static IReadOnlyList<SweepEntry> Build(IEnumerable<string> modes)
    {
        var entries = new List<SweepEntry>();
        foreach (string mode in modes)
        {
            entries.Add(new SweepEntry(mode, mode));
            if (HasDetectorChoice(mode))
            {
                entries.Add(new SweepEntry(
                    mode + " (coherent)", mode, new ModemOptions(Detector: PskDetector.Coherent)));
            }
        }

        return entries;
    }

    /// <summary>
    /// The same sweep, pointed at one or more audio centres instead of each mode's catalogue
    /// default.
    /// </summary>
    /// <param name="entries">The sweep set to re-point.</param>
    /// <param name="centres">Audio centres, in Hz. Empty leaves <paramref name="entries"/> alone.</param>
    /// <param name="keepDefault">Also keep each mode at its own catalogue centre, so a blind
    /// sweep is a strict superset of the default one and can only ever find more.</param>
    /// <remarks>
    /// <para>
    /// Modes with no centre to point are passed through untouched, once: the baseband
    /// <c>fsk*</c>/<c>c4fsk*</c> family occupies DC upwards and
    /// <see cref="ModemCatalog.Create"/> refuses a centre for them outright (issue #39).
    /// That is the right test rather than "is it an FM mode": the two sets happen to coincide
    /// today, and <see cref="FmModeProfiles.IsFmMode"/> answers a question about modulators
    /// that this tool has already been burned by asking (see <see cref="PacketModes"/>).
    /// </para>
    /// <para>
    /// A grid centre that lands on a mode's own default is dropped rather than run twice.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<SweepEntry> AtCentres(
        IReadOnlyList<SweepEntry> entries, IReadOnlyList<double> centres, bool keepDefault = false)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(centres);
        if (centres.Count == 0)
        {
            return entries;
        }

        var built = new List<SweepEntry>(entries.Count * centres.Count);
        foreach (SweepEntry entry in entries)
        {
            if (!ModemCatalog.AcceptsCentreFrequency(entry.Mode))
            {
                built.Add(entry);
                continue;
            }

            double? own = ModemCatalog.DefaultCentreFrequencyFor(entry.Mode);
            if (keepDefault)
            {
                built.Add(entry);
            }

            foreach (double centre in centres)
            {
                if (keepDefault && own is double already && Math.Abs(already - centre) < 1)
                {
                    continue;   // that is the entry just added, under its plain name
                }

                built.Add(entry with
                {
                    Label = $"{entry.Label} @ {centre:F0} Hz",
                    Options = entry.Options with { CentreFrequencyHz = centre },
                });
            }
        }

        return built;
    }

    /// <summary>
    /// The centres <c>--sweep</c> tries when nothing says where the signal is: 500 Hz to 2500 Hz
    /// in 200 Hz steps, which is the SSB passband a station places modems across.
    /// </summary>
    /// <remarks>
    /// The step is set by how far off centre a receiver still copies, measured rather than
    /// assumed: the 2026-08-24 off-air <c>afsk300</c> capture in <c>samples/offair/</c> reads
    /// from 1010 to 1210 Hz for a signal sitting at 1120, so a grid point is never more than
    /// 100 Hz from a signal and every mode's own diversity bank spans further than that again.
    /// Finer would multiply the wall clock for no coverage; coarser leaves holes.
    /// </remarks>
    public static IReadOnlyList<double> BlindCentres()
    {
        var centres = new List<double>();
        for (double centre = 500; centre <= 2500; centre += 200)
        {
            centres.Add(centre);
        }

        return centres;
    }

    /// <summary>
    /// How good a decode is, lowest best, for choosing which mode to name as the one that read a
    /// frame several modes all read. The ordering is <see cref="DecodeConfidence.Rank"/>, shared
    /// with the station; among equals, fewer bytes repaired means a cleaner copy, and the sweep's
    /// own order breaks the remaining ties so a report is stable.
    /// </summary>
    public static (int Rank, int Corrected, int Order) Confidence(Decode decode) =>
        (DecodeConfidence.Rank(decode.Quality), decode.Quality.CorrectedBytes ?? 0, decode.Order);
}
