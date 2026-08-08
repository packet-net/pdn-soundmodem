using System.Buffers.Binary;
using System.Globalization;
using Packet.SoundModem.Ms110d;
using Packet.SoundModem.UberSdr;
using Packet.SoundModem.Iq;
using M0LTE.Dsp;

namespace Packet.SoundModem.Ota;

/// <summary>
/// <c>sm-ota monitor</c> - watch a receiver live: capture → convert → demodulate, printing each
/// burst as it lands.
/// </summary>
/// <remarks>
/// <para>Receive-only. Nothing here transmits, so it is safe to point at a receiver at any time
/// and it is the cheapest way to answer "is anything reaching us at all" during a session -
/// a question that otherwise waits for the capture to finish and be scored.</para>
/// <para>It is the same chain a scored pass uses, driven from the socket instead of a file. That
/// matters more than the convenience: if what is printed live disagrees with what the scorer says
/// afterwards, one of them is wrong, and they share enough code that the disagreement would be
/// somewhere worth knowing about. The capture is still written to disk, so a monitored session is
/// also a recorded one.</para>
/// </remarks>
internal static class MonitorCommand
{
    public static async Task<int> RunAsync(string[] argv)
    {
        var a = Args.Parse(argv);
        if (a is null || a.Has("help") || !a.Has("host"))
        {
            Console.Error.WriteLine("""
                sm-ota monitor --host <receiver> --frequency <Hz> [options]

                  --frequency <Hz>     RX tune frequency (apply the session's dial correction)
                  --dial-hz <Hz>       offset of the suppressed carrier in the IQ baseband
                                       (default 2000, matching the transmit side)
                  --seconds <s>        stop after this long (default 0 = until Ctrl+C)
                  --ssb-low/-high      passband edges (default 150 / 3450)
                  --port <n>           default 443; --no-ssl for a plain LAN instance
                  --out-dir <dir>      the capture is recorded as well as watched
                  --name <label>       filename stem

                Receive-only - nothing here keys a transmitter. The capture is still written, so
                a monitored session can be scored afterwards with `sm-ota score`.
                """);
            return a is null ? 2 : 0;
        }

        var converter = new StreamingSsbDemodulator(new SsbDemodulatorOptions
        {
            InputRate = 48000,
            OutputRate = 9600,
            DialHz = a.Dbl("dial-hz", 2000),
            SsbLowHz = a.Dbl("ssb-low", 150),
            SsbHighHz = a.Dbl("ssb-high", 3450),
            NormalisePeak = 0f,
        });

        var demod = new Ms110dDemodulator();
        Ms110dLockInfo? locked = null;
        int bursts = 0;
        long audioSamples = 0;
        double peak = 0;

        demod.BlockDecoded += _ => locked ??= demod.Lock;
        demod.FirstPassBlockLlrs += (_, _) => locked ??= demod.Lock;
        demod.BurstCompleted += burst =>
        {
            bursts++;
            string wid = locked is null
                ? "no lock"
                : $"WN{locked.WaveformNumber} {locked.Interleaver}, CFO {locked.CfoHz:+0.0;-0.0;0} Hz";
            Console.WriteLine(
                $"[{audioSamples / 9600.0,8:F1} s] burst {bursts}: {wid}, "
                + $"{burst.Blocks} block(s), {burst.PayloadBits.Length} bits, {burst.Reason}");
            locked = null;
        };

        // Converted audio is produced on the receive loop, so the buffers are allocated once and
        // the per-block work stays bounded - a monitor that falls behind stalls the socket.
        const int maxFrames = 1 << 16;
        var iq = new short[maxFrames * 2];
        var audio = new float[converter.MaxOutputFor(maxFrames) + converter.MaxFlushOutput];
        var gate = new Lock();

        void OnBlock(ReadOnlySpan<byte> pcm)
        {
            lock (gate)
            {
                int frames = Math.Min(pcm.Length / 4, maxFrames);
                for (int k = 0; k < frames * 2; k++)
                {
                    iq[k] = BinaryPrimitives.ReadInt16LittleEndian(pcm[(k * 2)..]);
                }

                int wrote = converter.Process(iq.AsSpan(0, frames * 2), audio);
                for (int k = 0; k < wrote; k++)
                {
                    peak = Math.Max(peak, Math.Abs(audio[k]));
                }

                demod.Process(audio.AsSpan(0, wrote));
                audioSamples += wrote;
            }
        }

        var options = new UberSdrCaptureOptions
        {
            Host = a.Req("host"),
            Port = a.Int("port", a.Has("no-ssl") ? 80 : 443),
            Ssl = !a.Has("no-ssl"),
            FrequencyHz = a.Int("frequency", 0) is var f && f > 0
                ? f
                : throw new ArgumentException("--frequency <Hz> is required"),
            Name = a.Str("name", "monitor"),
            OutputDir = a.Str("out-dir", "."),
            DurationSeconds = a.Int("seconds", 0),
            OnBlock = OnBlock,
        };

        // Every flag monitor recognises has now been read - reject anything left over before the
        // live capture starts, so a mistyped or unsupported flag fails loudly instead of quietly
        // watching the wrong frequency or passband.
        a.RejectUnknown("monitor");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.Error.WriteLine("interrupt - finalising…");
            cts.Cancel();
        };

        Console.Error.WriteLine(
            $"monitoring {options.Host} at {options.FrequencyHz} Hz, dial {a.Dbl("dial-hz", 2000):F0} Hz "
            + "- receive only");

        CaptureResult result = await new UberSdrIqClient(m => Console.Error.WriteLine($"[rx] {m}"))
            .CaptureAsync(options, cts.Token).ConfigureAwait(false);

        Console.Error.WriteLine();
        Console.Error.WriteLine(
            $"{result.Frames} frames, {audioSamples / 9600.0:F1} s of audio, {bursts} burst(s), "
            + $"audio peak {peak.ToString("F4", CultureInfo.InvariantCulture)}");
        Console.Error.WriteLine($"capture: {result.WavPath}");
        Console.WriteLine(result.WavPath);
        return 0;
    }
}
