using System.Globalization;
using System.Text;
using M0LTE.Ofdm;
using Packet.SoundModem.Tests.Channel;

namespace Packet.SoundModem.Ota;

/// <summary>
/// <c>sm-ota sim-stream</c> — the <b>continuous-stream</b> counterpart to <see cref="SimCommand"/>'s
/// independent-burst packet layer, and the seam that lets codec2's own <c>ch</c> channel simulator
/// carry the FreeDV datac waveform. It exists to re-anchor the datac Poor/MPP thresholds against
/// codec2's published operating points by separating two confounded effects the #104 baseline could
/// not: the <b>methodology</b> (our independent-per-burst fade vs codec2's one continuous fade over a
/// whole run of bursts) and the <b>channel model</b> (our MIL-STD Watterson Poor vs codec2's <c>ch</c>
/// MPP).
/// </summary>
/// <remarks>
/// <para>codec2 measures its datac MPP operating points (README_data.md, <c>unittest/raw_data_curves/
/// snr_curves.sh</c>) as <em>packets received / packets sent</em> for a run of N single-packet bursts
/// pushed back-to-back through <em>one</em> continuous MPP fading realisation (the 60 s
/// <c>fast_fading_samples.float</c> file, read sequentially), with SNR read back from <c>ch</c>'s own
/// <c>SNR3k</c>. This command reproduces that framing for our managed engine: it renders N single-packet
/// datac bursts (each its own pre/postamble, exactly as <see cref="Modems.FreeDvDatacModem"/> frames
/// one), concatenated with a short inter-burst gap, then either</para>
/// <list type="bullet">
///   <item><description>applies <em>one</em> managed <see cref="WattersonChannel"/> realisation over
///   the whole stream (the continuous-fade methodology on <em>our</em> channel), or</description></item>
///   <item><description>emits the clean concatenated int16 stream to a file so an external process —
///   codec2's real <c>ch --mpp</c> — is the channel, then decodes what comes back
///   (<c>--emit-raw</c> / <c>--decode-raw</c>).</description></item>
/// </list>
/// <para>Either way the receive side is our own <see cref="DatacReceiver"/> in single-packet burst
/// mode, driven <c>Nin</c>-at-a-time exactly as the deployment modem drives it, scoring each recovered
/// CRC-OK packet against the transmitted payloads — codec2's "packet received" currency. No modem code
/// is touched; this is pure measurement scaffolding.</para>
/// </remarks>
internal static class SimStreamCommand
{
    // Codec2's ±16384 datac amplitude on the ±1.0 float convention (peak ≈ 0.5); ×32767 → int16.
    private const int Rate = 8000;

    public static int Run(string[] argv)
    {
        var a = Args.Parse(argv);
        if (a is null || a.Has("help") || !a.Has("mode"))
        {
            Console.Error.WriteLine("""
                sm-ota sim-stream --mode freedv-datac<N> [options]

                Continuous-stream datac measurement: N single-packet bursts through ONE channel
                realisation, scored as packets-received/sent (codec2's MPP currency). Three roles:

                  managed end-to-end (default):
                    --channel poor|awgn   inject ONE managed WattersonChannel run over the stream
                    --snr <a,b,c>         SNR rungs in dB (3 kHz noise bw), noise calibrated to the
                                          mean ACTIVE-burst power (silence gaps excluded), matching
                                          the independent-burst baseline's convention
                  emit for an external channel (codec2 ch):
                    --emit-raw <file>     write the clean concatenated int16 stream (real, 8 kHz)
                    --manifest <file>     write the per-burst payloads + active/total sample counts
                  decode what the external channel returned:
                    --decode-raw <file>   read a (channel-processed) int16 stream and score it
                    --manifest <file>     the manifest emitted alongside --emit-raw

                Common:
                  --bursts <n>          single-packet bursts in the run (default 100)
                  --gap-ms <n>          inter-burst silence, ms (default 400) — long enough for the
                                        receiver to end-of-burst and re-acquire the next preamble
                  --seed <n>            first burst seed (default 1); burst k uses seed+k
                  --csv <path>          append one row per SNR point (managed mode)
                  --quiet               suppress per-point progress
                """);
            return a is null ? 2 : 0;
        }

        string modeName = a.Req("mode");
        if (!DatacEngine.IsDatacMode(modeName))
        {
            throw new ArgumentException($"sim-stream is datac-only; '{modeName}' is not a datac mode");
        }

        OfdmMode mode = DatacEngine.Mode(modeName);
        int bursts = a.Int("bursts", 100);
        int gapSamples = (int)(a.Int("gap-ms", 400) / 1000.0 * Rate);
        int firstSeed = a.Int("seed", 1);
        bool quiet = a.Has("quiet");

        if (a.Has("decode-raw"))
        {
            return DecodeRaw(mode, a.Req("decode-raw"), a.Req("manifest"), quiet);
        }

        // Build the run once — the payloads and the clean burst audio are shared by every role.
        StreamBuild build = BuildStream(mode, modeName, bursts, gapSamples, firstSeed);

        if (a.Has("emit-raw"))
        {
            EmitRaw(build, a.Req("emit-raw"), a.Req("manifest"));
            Console.Error.WriteLine(
                $"emit {modeName}: {bursts} bursts, {build.Clean.Length} samples "
                + $"({build.ActiveSamples} active, {100.0 * build.ActiveSamples / build.Clean.Length:F1}%), "
                + $"SNR offset {SnrOffsetDb(build):+0.00;-0.00} dB");
            return 0;
        }

        // Managed end-to-end: inject a managed channel at each SNR and score.
        SimChannelKind kind = SimChannel.Parse(a.Str("channel", "poor"));
        double[] snrs = a.Req("snr").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => double.Parse(s.Trim(), CultureInfo.InvariantCulture)).ToArray();

        void Log(string m)
        {
            if (!quiet)
            {
                Console.Error.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] {m}");
            }
        }

        Log($"sim-stream {modeName} channel={kind} bursts={bursts} gap={gapSamples}sa "
            + $"active={100.0 * build.ActiveSamples / build.Clean.Length:F1}%");

        var rows = new List<(double Snr, int Ok, int N)>();
        Console.WriteLine();
        Console.WriteLine($"=== sim-stream {modeName} ({kind}, continuous) ===");
        Console.WriteLine($"{"SNR",6} {"ok/N",9} {"succ%",6} {"95% CI",13}");
        foreach (double snr in snrs)
        {
            int pointSeed = unchecked((firstSeed * 1_000_003) + (int)Math.Round(snr * 100));
            short[] rx = ApplyManagedChannel(build, kind, snr, pointSeed);
            int ok = ScoreStream(mode, rx, build);
            rows.Add((snr, ok, bursts));
            (double lo, double hi) = SimStats.Wilson(ok, bursts);
            Console.WriteLine($"{snr,6:+0.0;-0.0} {ok,4}/{bursts,-4} {(double)ok / bursts,6:P0} "
                + $"{$"{lo:P0}..{hi:P0}",13}");
        }

        if (a.Str("csv", null) is { } csv)
        {
            WriteCsv(csv, modeName, kind, bursts, rows);
            Console.Error.WriteLine($"wrote {csv}");
        }

        return 0;
    }

    /// <summary>The clean concatenated stream plus the bookkeeping every role needs.</summary>
    private sealed class StreamBuild
    {
        public required string ModeName { get; init; }

        public required byte[][] Payloads { get; init; }

        /// <summary>Clean concatenated real audio (±1.0 float, 8 kHz), lead-in gap + bursts + gaps.</summary>
        public required float[] Clean { get; init; }

        /// <summary>Total non-silent (burst) samples — the power/SNR is calibrated against these.</summary>
        public required int ActiveSamples { get; init; }

        /// <summary>Mean power of the active (burst) samples of the clean stream.</summary>
        public required double ActiveMeanPower { get; init; }
    }

    private static StreamBuild BuildStream(OfdmMode mode, string modeName, int bursts, int gapSamples, int firstSeed)
    {
        var payloads = new byte[bursts][];
        var cleanBursts = new float[bursts][];
        int total = gapSamples; // a lead-in gap so acquisition sees a noise floor before burst 0
        int active = 0;
        for (int k = 0; k < bursts; k++)
        {
            payloads[k] = DatacEngine.Payload(mode, firstSeed + k);
            cleanBursts[k] = DatacEngine.CleanBurst(mode, payloads[k]);
            total += cleanBursts[k].Length + gapSamples;
            active += cleanBursts[k].Length;
        }

        var clean = new float[total];
        int pos = gapSamples;
        double power = 0;
        for (int k = 0; k < bursts; k++)
        {
            float[] b = cleanBursts[k];
            Array.Copy(b, 0, clean, pos, b.Length);
            for (int i = 0; i < b.Length; i++)
            {
                power += (double)b[i] * b[i];
            }

            pos += b.Length + gapSamples;
        }

        return new StreamBuild
        {
            ModeName = modeName,
            Payloads = payloads,
            Clean = clean,
            ActiveSamples = active,
            ActiveMeanPower = active > 0 ? power / active : 0,
        };
    }

    /// <summary>The dB the burst-silence dilutes <c>ch</c>'s whole-stream SNR3k below the true active
    /// SNR: <c>ch</c> averages signal power over every sample, so the true SNR = ch SNR3k − this.</summary>
    private static double SnrOffsetDb(StreamBuild b) =>
        10.0 * Math.Log10((double)b.ActiveSamples / b.Clean.Length);

    /// <summary>Applies one managed <see cref="WattersonChannel"/> realisation over the whole stream,
    /// then AWGN calibrated to the active-burst power (the independent-burst baseline's convention), and
    /// quantises to codec2's ±32767 int16.</summary>
    private static short[] ApplyManagedChannel(StreamBuild b, SimChannelKind kind, double snrDb, int seed)
    {
        float[] faded;
        if (kind == SimChannelKind.Awgn)
        {
            faded = (float[])b.Clean.Clone();
        }
        else
        {
            // snr = +inf → the channel applies fading only; the noise is added below against the
            // active-burst power so the gaps do not dilute the calibration.
            var channel = new WattersonChannel(Rate, seed, SimChannel.Paths(kind));
            faded = channel.Apply(b.Clean, double.PositiveInfinity);
        }

        // Same 3 kHz-bandwidth noise formula as WattersonChannel.AddNoise, but calibrated to the mean
        // ACTIVE power rather than the whole (gap-diluted) stream.
        double snr = Math.Pow(10, snrDb / 10.0);
        double noisePower = b.ActiveMeanPower / snr * (Rate / 2.0) / 3000.0;
        double sigma = Math.Sqrt(noisePower);
        var rng = new Random(seed ^ 0x5f3759df);
        var samples = new short[faded.Length];
        for (int i = 0; i < faded.Length; i++)
        {
            double v = (faded[i] + (sigma * Gaussian(rng))) * 32767.0;
            samples[i] = v >= 32767.0 ? (short)32767 : v <= -32768.0 ? (short)-32768 : (short)v;
        }

        return samples;
    }

    /// <summary>Drives the multi-burst receiver over a channel-processed int16 stream and counts the
    /// distinct transmitted payloads recovered CRC-OK — codec2's packets-received/sent.</summary>
    private static int ScoreStream(OfdmMode mode, short[] samples, StreamBuild build)
    {
        // Distinct-payload matching, so a spurious decode or a re-decoded packet cannot inflate the
        // count, and a lost burst does not misalign a positional match.
        var wanted = new Dictionary<string, bool>(build.Payloads.Length);
        foreach (byte[] p in build.Payloads)
        {
            wanted[Convert.ToHexString(p)] = false;
        }

        var receiver = new DatacReceiver(mode, packetsPerBurst: 1, endOfBurstDetection: true);
        int pos = 0;
        int ok = 0;
        while (true)
        {
            int nin = receiver.Demod.Nin;
            if (samples.Length - pos < nin)
            {
                break;
            }

            foreach (DatacRxResult r in receiver.Process(samples.AsSpan(pos, nin)))
            {
                if (!r.CrcOk)
                {
                    continue;
                }

                // r.Payload is the DATA payload (what was sent); r.Bytes is the full frame including
                // the 2-byte CRC. Match on Payload for an exact per-burst fingerprint, so a spurious
                // decode or a re-decode cannot inflate the count and a lost burst cannot misalign it.
                string key = Convert.ToHexString(r.Payload.ToArray());
                if (wanted.TryGetValue(key, out bool already) && !already)
                {
                    wanted[key] = true;
                    ok++;
                }
            }

            pos += nin;
        }

        return ok;
    }

    private static void EmitRaw(StreamBuild build, string rawPath, string manifestPath)
    {
        short[] samples = DatacEngine.ToShort(build.Clean, 1f);
        var bytes = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        File.WriteAllBytes(rawPath, bytes);

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"# sim-stream manifest v1\n");
        sb.Append(CultureInfo.InvariantCulture, $"mode {build.ModeName}\n");
        sb.Append(CultureInfo.InvariantCulture, $"bursts {build.Payloads.Length}\n");
        sb.Append(CultureInfo.InvariantCulture, $"samples {build.Clean.Length}\n");
        sb.Append(CultureInfo.InvariantCulture, $"active {build.ActiveSamples}\n");
        sb.Append(CultureInfo.InvariantCulture, $"snr_offset_db {SnrOffsetDb(build):F4}\n");
        foreach (byte[] p in build.Payloads)
        {
            sb.Append(CultureInfo.InvariantCulture, $"payload {Convert.ToHexString(p)}\n");
        }

        File.WriteAllText(manifestPath, sb.ToString());
    }

    private static int DecodeRaw(OfdmMode mode, string rawPath, string manifestPath, bool quiet)
    {
        (byte[][] payloads, double snrOffset) = ReadManifest(manifestPath);
        byte[] raw = File.ReadAllBytes(rawPath);
        var samples = new short[raw.Length / 2];
        Buffer.BlockCopy(raw, 0, samples, 0, samples.Length * 2);

        var build = new StreamBuild
        {
            ModeName = mode.Name,
            Payloads = payloads,
            Clean = [],
            ActiveSamples = 0,
            ActiveMeanPower = 0,
        };
        int ok = ScoreStream(mode, samples, build);
        (double lo, double hi) = SimStats.Wilson(ok, payloads.Length);
        if (!quiet)
        {
            Console.Error.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] decode {rawPath}: {ok}/{payloads.Length} "
                + $"({(double)ok / payloads.Length:P0}) snr_offset {snrOffset:+0.00;-0.00} dB");
        }

        // stdout is machine-readable: ok N snrOffsetDb — the shell reads this alongside ch's SNR3k.
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{ok} {payloads.Length} {snrOffset:F4}"));
        return 0;
    }

    private static (byte[][] Payloads, double SnrOffset) ReadManifest(string path)
    {
        var payloads = new List<byte[]>();
        double snrOffset = 0;
        foreach (string line in File.ReadLines(path))
        {
            if (line.StartsWith("payload ", StringComparison.Ordinal))
            {
                payloads.Add(Convert.FromHexString(line[8..].Trim()));
            }
            else if (line.StartsWith("snr_offset_db ", StringComparison.Ordinal))
            {
                snrOffset = double.Parse(line[14..].Trim(), CultureInfo.InvariantCulture);
            }
        }

        return (payloads.ToArray(), snrOffset);
    }

    private static void WriteCsv(
        string path, string mode, SimChannelKind kind, int bursts, IReadOnlyList<(double Snr, int Ok, int N)> rows)
    {
        bool exists = File.Exists(path);
        using var w = new StreamWriter(path, append: true);
        if (!exists)
        {
            w.WriteLine("mode,method,channel,bursts,snrDb,successes,successRate,ciLo,ciHi");
        }

        foreach ((double snr, int ok, int n) in rows)
        {
            (double lo, double hi) = SimStats.Wilson(ok, n);
            w.WriteLine(string.Join(',', mode, "stream", kind, n,
                snr.ToString("G6", CultureInfo.InvariantCulture),
                ok, ((double)ok / n).ToString("G6", CultureInfo.InvariantCulture),
                lo.ToString("G6", CultureInfo.InvariantCulture),
                hi.ToString("G6", CultureInfo.InvariantCulture)));
        }
    }

    private static double Gaussian(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }
}
