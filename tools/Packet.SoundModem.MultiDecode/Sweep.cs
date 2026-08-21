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
internal sealed record SweepFailure(string Label, string Reason);

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
/// <para><b>Driven off <see cref="IModem.FrameDecoded"/>, not the constructor's frame sink.</b>
/// The event is a superset: every frame that reaches the sink also raises it, and a frame the
/// receiver read but would not hand to a host - plain IL2P heard by an IL2P+CRC link, marked
/// <see cref="FrameQuality.MonitorOnly"/> - raises it and never reaches the sink at all. A tool
/// asking "what is in this recording" wants exactly that frame, and wants to be told it was
/// held back, which is why the plain-IL2P tolerance is left at its default rather than switched
/// on: turning it on would deliver such frames and hide the fact that a real link would not
/// have.</para>
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
                failures.Add(new SweepFailure(entry.Label, error.Message));
                continue;
            }

            int before = decodes.Count;
            try
            {
                RunOne(entry, audio, dspRate, decodes);
            }
#pragma warning disable CA1031 // one broken mode must not take the rest of the sweep down with it
            catch (Exception error)
#pragma warning restore CA1031
            {
                failures.Add(new SweepFailure(entry.Label, error.Message));
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

    private static void RunOne(SweepEntry entry, float[] audio, int dspRate, List<Decode> decodes)
    {
        // The sink is required by the catalogue and deliberately ignored: see the type remarks
        // for why FrameDecoded is the honest source here.
        IModem modem = ModemCatalog.Create(entry.Mode, dspRate, _ => { }, entry.Options);
        try
        {
            modem.FrameDecoded += (frame, quality) =>
                decodes.Add(new Decode(frame, quality, entry.Label, decodes.Count));

            // In blocks, not one span: a bank holding per-chunk candidate state (the afsk1200 and
            // bpsk banks compare branches at chunk boundaries) behaves as it does on the air only
            // if it is fed in something like air-sized pieces.
            const int block = 4096;
            for (int offset = 0; offset < audio.Length; offset += block)
            {
                modem.Process(audio.AsSpan(offset, Math.Min(block, audio.Length - offset)));
            }
        }
        finally
        {
            (modem as IDisposable)?.Dispose();
        }
    }

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

    private static IReadOnlyList<SweepEntry> Build(IEnumerable<string> modes)
    {
        var entries = new List<SweepEntry>();
        foreach (string mode in modes)
        {
            entries.Add(new SweepEntry(mode, mode));
            if (mode == "qpsk3600")
            {
                entries.Add(new SweepEntry(
                    mode + " (coherent)", mode, new ModemOptions(Detector: PskDetector.Coherent)));
            }
        }

        return entries;
    }

    /// <summary>
    /// How good a decode is, lowest best, for choosing which mode to name as the one that read a
    /// frame several modes all read. A frame the receiver would have handed to its host beats one
    /// it held back; a verified CRC beats Reed-Solomon standing alone; and among equals, fewer
    /// bytes repaired means a cleaner copy.
    /// </summary>
    public static (int Rank, int Corrected, int Order) Confidence(Decode decode)
    {
        FrameQuality quality = decode.Quality;
        int rank = quality switch
        {
            { MonitorOnly: true } => 3,
            { CrcValid: true } => 0,
            { PlainIl2p: true, TrailerNearBits: not null } => 1,
            { PlainIl2p: true } => 2,
            { CrcValid: false } => 2,
            _ => 0, // HDLC or FX.25: the FCS passed, which is the whole guarantee that framing has
        };

        return (rank, quality.CorrectedBytes ?? 0, decode.Order);
    }
}
