using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Packet.SoundModem.UberSdr;

namespace Packet.SoundModem.Ota;

/// <summary>Options for one RSP1 capture session (one WAV file). Mirrors
/// <see cref="UberSdrCaptureOptions"/> so the two backends are interchangeable behind the
/// ladder's capture seam.</summary>
internal sealed class RspCaptureOptions
{
    /// <summary>Called with every block as it is written, after the startup guard and the trim to
    /// the target duration — exactly what the WAV holds. Runs on the read loop, so it must not
    /// block or the capture falls behind real time. Parity with <see cref="UberSdrCaptureOptions"/>.</summary>
    public IqBlockHandler? OnBlock { get; init; }

    /// <summary>SSH target. A bare hostname is prefixed with <c>tf@</c>; pass <c>user@host</c> to
    /// override the user.</summary>
    public string Host { get; init; } = "studybox";

    public string SshKeyPath { get; init; } = DefaultSshKeyPath();

    public required int FrequencyHz { get; init; }

    /// <summary>Complex sample rate. Any of the RSP1's low rates covers the 3 kHz waveform; 96000
    /// is proven and the default.</summary>
    public int SampleRate { get; init; } = 96000;

    /// <summary>rx_sdr <c>-g</c> gain string. Default disables AGC and sets a fixed IFGR/RFGR, so the
    /// receive level is deterministic rather than AGC-controlled. See
    /// <see cref="RspIqClient.DefaultGain"/> for the measured level curve and the rx_sdr build this
    /// needs.</summary>
    public string Gain { get; init; } = RspIqClient.DefaultGain;

    public string? Name { get; init; }

    public string OutputDir { get; init; } = ".";

    /// <summary>Seconds of audio to KEEP (0 = until cancelled or the stream ends). Measured after
    /// the startup guard, so the file holds this many seconds of settled samples.</summary>
    public int DurationSeconds { get; init; }

    /// <summary>Discard this much of the stream after connect before opening the file — the
    /// SDRplay front-end settles over the first samples after tune. 1 s clears it, matching the
    /// UberSDR client's guard.</summary>
    public int StartupGuardMs { get; init; } = 1000;

    internal static string DefaultSshKeyPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "id_ed25519");
}

/// <summary>
/// Captures an IQ stream from the RSP1 on studybox to a 16-bit stereo (interleaved I/Q) WAV plus
/// a JSON sidecar — the same <see cref="CaptureResult"/> and file format as
/// <see cref="UberSdrIqClient"/>, so a capture drops straight into <c>sm-ota score</c> with no
/// scorer change. Where the UberSDR client reads a websocket, this runs <c>rx_sdr</c> remotely
/// over SSH and reads its <c>CF32</c> stdout in real time; the streaming structure (startup
/// guard, on-the-fly int16 conversion, trim to target, live block handler) is otherwise the same.
/// One instance per session; the caller makes a fresh one per pass.
/// </summary>
internal sealed class RspIqClient
{
    /// <summary>Default rx_sdr <c>-g</c> string: AGC off with a fixed IFGR/RFGR, so the receive
    /// level is deterministic rather than AGC-controlled. <c>AGC=false</c> disables automatic gain
    /// via <c>setGainMode</c>, and the fixed IFGR/RFGR are then honoured — this needs the patched
    /// rx_sdr on studybox (fork <c>github.com/M0LTE/rx_tools</c>, branch <c>agc-gainmode</c>, which
    /// teaches <c>-g</c> an <c>AGC=</c> pseudo-element). Stock rx_tools has no such handling, leaves
    /// AGC on and ignores IFGR (logging <c>Not updating IFGR gain because AGC is enabled</c>).
    /// <para>Measured determinism at 18.1 MHz (AGC off, RFGR=0, noise-floor RMS): IFGR 20 → −48,
    /// 25 → −53, 30 → −58, 40 → −67, 50 → −75, 59 → −78 dBFS — about 1 dB per IFGR unit in the
    /// working region. AGC on self-levels to ~−48 dBFS regardless of IFGR, i.e. the level the old
    /// AGC-pinned campaign actually ran at, which equals fixed IFGR=20. The default is IFGR=20:
    /// two live passes at 18.1 MHz decoded WN4 clean there with the capture peaking ~−22 dBFS
    /// (22 dB clipping headroom) and the signal ~22 dB over the floor — the validated operating
    /// point, now deterministic. A higher IFGR (quieter) buys clipping headroom but pushes the
    /// delivered signal toward the RSP1's own in-band noise, worsening the low-SNR self-calibration;
    /// go quieter only if a burst is at risk of clipping. Override with <c>--rsp-gain</c>.</para></summary>
    public const string DefaultGain = "AGC=false,IFGR=20,RFGR=0";

    private const string ClientTag = "sm-ota RSP1 capture (rx_sdr over ssh)";

    private readonly Action<string>? _log;

    // rx_sdr echoes its own PID (via `exec`) before it opens the device; captured here so the
    // remote process can be killed by PID on stop — closing the SSH channel does NOT stop it.
    private readonly TaskCompletionSource<int> _remotePid =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RspIqClient(Action<string>? log = null) => _log = log;

    public async Task<CaptureResult> CaptureAsync(RspCaptureOptions opt, CancellationToken ct)
    {
        Directory.CreateDirectory(opt.OutputDir);
        string remoteCommand = BuildRemoteCommand(opt);

        var psi = new ProcessStartInfo("ssh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string arg in BuildSshArguments(opt, remoteCommand))
        {
            psi.ArgumentList.Add(arg);
        }

        _log?.Invoke($"ssh {opt.Host}: rx_sdr driver=sdrplay f={opt.FrequencyHz} Hz " +
                     $"s={opt.SampleRate} CF32 gain=[{opt.Gain}]");

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        Task stderrPump = PumpStderrAsync(proc);

        long guardFrames = GuardFrames(opt);
        long targetFrames = opt.DurationSeconds > 0 ? (long)opt.DurationSeconds * opt.SampleRate : long.MaxValue;

        var converter = new Cf32ToInt16Stereo();
        long framesSeen = 0; // complex frames converted so far, guarded ones included
        PcmWavWriter? wav = null;
        string wavPath = "";
        DateTime sample0Utc = default;

        byte[] readBuf = ArrayPool<byte>.Shared.Rent(64 * 1024);
        byte[] pcmBuf = ArrayPool<byte>.Shared.Rent(64 * 1024); // <= readBuf/2 (8 in-bytes → 4 out)
        try
        {
            Stream stdout = proc.StandardOutput.BaseStream;
            while (!ct.IsCancellationRequested)
            {
                int read;
                try
                {
                    read = await stdout.ReadAsync(readBuf.AsMemory(), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break; // finalise what we have, like the UberSDR client does on Ctrl+C
                }

                if (read <= 0)
                {
                    break; // rx_sdr ended (its -n backstop, or the stream closed)
                }

                int pcmBytes = converter.Convert(readBuf.AsSpan(0, read), pcmBuf);
                if (pcmBytes == 0)
                {
                    continue; // only a partial frame this read; carried to the next
                }

                long framesThisChunk = pcmBytes / 4;

                // Startup guard: drop whole chunks until we are clear of the settling transient;
                // the chunk that crosses the boundary is split, its guarded head discarded.
                long keepFromFrame = 0;
                if (framesSeen < guardFrames)
                {
                    long stillGuarded = guardFrames - framesSeen;
                    if (framesThisChunk <= stillGuarded)
                    {
                        framesSeen += framesThisChunk;
                        continue;
                    }
                    keepFromFrame = stillGuarded;
                }
                framesSeen += framesThisChunk;

                if (wav is null)
                {
                    // Sample0Utc is the wall clock at the first sample KEPT — a real timestamp, not
                    // a guess at rx_sdr's remote start latency. This is why the stream is read live
                    // rather than captured remotely and copied back afterwards.
                    sample0Utc = DateTime.UtcNow;
                    string stem = opt.Name ?? opt.Host;
                    string ts = sample0Utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
                    wavPath = Path.Combine(opt.OutputDir, $"{stem}_{opt.FrequencyHz}_{ts}.wav");
                    wav = new PcmWavWriter(wavPath, opt.SampleRate); // 2 channels, I = left, Q = right
                    _log?.Invoke($"recording from {sample0Utc:HH:mm:ss.fff} UTC → {Path.GetFileName(wavPath)}");
                }

                long remaining = targetFrames - wav.FramesWritten;
                if (remaining <= 0)
                {
                    break;
                }
                long available = framesThisChunk - keepFromFrame;
                long take = Math.Min(available, remaining); // trim the final chunk to hit the target
                ReadOnlySpan<byte> keep = pcmBuf.AsSpan((int)(keepFromFrame * 4), (int)(take * 4));
                wav.Write(keep);
                opt.OnBlock?.Invoke(keep);

                if (wav.FramesWritten >= targetFrames)
                {
                    break;
                }
            }

            if (wav is null)
            {
                throw new InvalidOperationException(
                    "no data captured (never passed the startup guard — did rx_sdr open the device?)");
            }

            long frames = wav.FramesWritten;
            wav.Dispose();
            wav = null;

            DateTime lastSampleUtc = sample0Utc.AddSeconds(frames / (double)opt.SampleRate);
            string sha = await ComputeSha256Async(wavPath).ConfigureAwait(false);
            string sidecarPath = Path.ChangeExtension(wavPath, ".json");
            WriteSidecar(sidecarPath, opt, remoteCommand, wavPath, frames, sample0Utc, lastSampleUtc, sha);

            double dur = frames / (double)opt.SampleRate;
            _log?.Invoke($"stopped: {frames} frames ({dur:F3} s, {frames * 4 / (1024.0 * 1024.0):F2} MB) " +
                         $"→ {Path.GetFileName(wavPath)}");
            return new CaptureResult(wavPath, sidecarPath, frames, opt.SampleRate, sample0Utc, sha);
        }
        finally
        {
            wav?.Dispose();
            ArrayPool<byte>.Shared.Return(readBuf);
            ArrayPool<byte>.Shared.Return(pcmBuf);
            await StopRemoteAsync(proc, opt).ConfigureAwait(false);
            try
            {
                await stderrPump.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // the pipe closing under the pump is the expected way it ends
            }
        }
    }

    /// <summary>The remote shell line: report the PID, then <c>exec</c> rx_sdr streaming CF32 to
    /// stdout. <c>exec</c> makes rx_sdr inherit the shell's PID, so the PID echoed first is
    /// rx_sdr's own — which is what <see cref="StopRemoteAsync"/> kills, because rx_sdr survives a
    /// closed SSH channel and would otherwise hold the single-client RSP1 and block every later
    /// capture. With a fixed duration an <c>-n</c> sample count is added as a backstop, so even a
    /// missed kill self-terminates.</summary>
    internal static string BuildRemoteCommand(RspCaptureOptions opt)
    {
        string nArg = "";
        if (opt.DurationSeconds > 0)
        {
            long n = GuardFrames(opt) + ((long)opt.DurationSeconds * opt.SampleRate) + opt.SampleRate;
            nArg = string.Create(CultureInfo.InvariantCulture, $" -n {n}");
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"echo RXPID:$$ 1>&2; exec rx_sdr -d driver=sdrplay -f {opt.FrequencyHz} " +
            $"-s {opt.SampleRate} -F CF32 -g {opt.Gain}{nArg} -");
    }

    /// <summary>The ssh argv: identity, non-interactive host-key/auth, keepalives, target, and the
    /// remote command last (ssh joins any trailing args into the remote command line).</summary>
    internal static List<string> BuildSshArguments(RspCaptureOptions opt, string remoteCommand) =>
    [
        "-i", opt.SshKeyPath,
        "-o", "StrictHostKeyChecking=accept-new",
        "-o", "BatchMode=yes",
        "-o", "ServerAliveInterval=5",
        "-o", "ServerAliveCountMax=3",
        opt.Host.Contains('@', StringComparison.Ordinal) ? opt.Host : $"tf@{opt.Host}",
        remoteCommand,
    ];

    private static long GuardFrames(RspCaptureOptions opt) =>
        (long)Math.Round(opt.StartupGuardMs / 1000.0 * opt.SampleRate);

    private async Task PumpStderrAsync(Process proc)
    {
        bool agcWarned = false;
        try
        {
            string? line;
            while ((line = await proc.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                string clean = StripAnsi(line);
                if (clean.StartsWith("RXPID:", StringComparison.Ordinal))
                {
                    if (int.TryParse(clean.AsSpan(6).Trim(), NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out int pid))
                    {
                        _remotePid.TrySetResult(pid);
                    }
                    continue;
                }

                if (!agcWarned && clean.Contains("Not updating", StringComparison.OrdinalIgnoreCase)
                    && clean.Contains("AGC is enabled", StringComparison.OrdinalIgnoreCase))
                {
                    agcWarned = true;
                    _log?.Invoke("WARNING: AGC is enabled, so rx_sdr is ignoring the fixed IFGR and the level " +
                                 "is AGC-controlled — pass AGC=false for a deterministic level (the patched " +
                                 "studybox rx_sdr honours it; see RspIqClient.DefaultGain)");
                }

                _log?.Invoke($"rx_sdr: {clean}");
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // the pipe being torn down on stop is the normal end of the pump
        }
        finally
        {
            _remotePid.TrySetResult(0); // unblock the kill path if the PID line never arrived
        }
    }

    /// <summary>Kills the local ssh child, then the remote rx_sdr by the PID it reported —
    /// belt-and-braces, because an orphaned rx_sdr holds the single-client RSP1.</summary>
    private async Task StopRemoteAsync(Process proc, RspCaptureOptions opt)
    {
        int pid = await _remotePid.Task.ConfigureAwait(false);

        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) { /* already gone */ }

        if (pid > 0)
        {
            await RemoteKillAsync(opt, pid).ConfigureAwait(false);
        }
        else
        {
            _log?.Invoke("WARNING: rx_sdr never reported its PID; cannot target the remote kill " +
                         "(check studybox for an orphaned rx_sdr holding the RSP1)");
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* best effort */ }
    }

    private async Task RemoteKillAsync(RspCaptureOptions opt, int pid)
    {
        string cmd = string.Create(CultureInfo.InvariantCulture,
            $"kill {pid} 2>/dev/null; sleep 0.3; kill -9 {pid} 2>/dev/null; exit 0");
        var psi = new ProcessStartInfo("ssh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string arg in BuildSshArguments(opt, cmd))
        {
            psi.ArgumentList.Add(arg);
        }

        try
        {
            using var kill = Process.Start(psi) ?? throw new InvalidOperationException("ssh did not start");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await kill.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            _log?.Invoke($"remote rx_sdr (pid {pid}) terminated; RSP1 released");
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or IOException)
        {
            _log?.Invoke($"WARNING: remote kill of rx_sdr pid {pid} did not confirm: {ex.Message}");
        }
    }

    private static void WriteSidecar(
        string path, RspCaptureOptions opt, string remoteCommand, string wavPath,
        long frames, DateTime sample0Utc, DateTime lastSampleUtc, string sha)
    {
        var capture = new JsonObject
        {
            ["client"] = ClientTag,
            ["host"] = opt.Host,
            ["driver"] = "sdrplay",
            ["device"] = "RSP1",
            ["frequency_hz"] = opt.FrequencyHz,
            ["mode"] = "cf32",
            ["format"] = "cf32→pcm16-stereo",
            ["sample_rate"] = opt.SampleRate,
            ["channels"] = 2,
            ["gain"] = opt.Gain,
            ["frames"] = frames,
            ["duration_s"] = Math.Round(frames / (double)opt.SampleRate, 6),
            ["startup_guard_ms"] = opt.StartupGuardMs,
            ["sample0_utc"] = sample0Utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            ["last_sample_utc"] = lastSampleUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            ["remote_command"] = remoteCommand,
            ["wav_file"] = Path.GetFileName(wavPath),
            ["wav_sha256"] = sha,
        };
        var root = new JsonObject { ["capture"] = capture };
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using FileStream fs = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(fs).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Strips ANSI CSI escape sequences (rx_sdr colours its warnings) so logs and the
    /// AGC-warning match are clean.</summary>
    private static string StripAnsi(string s)
    {
        if (s.IndexOf('\u001b') < 0)
        {
            return s;
        }

        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\u001b')
            {
                i++;
                if (i < s.Length && s[i] == '[')
                {
                    i++;
                    while (i < s.Length && (s[i] < '@' || s[i] > '~'))
                    {
                        i++;
                    }
                }
                continue; // drop the final byte of the sequence too
            }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }
}

/// <summary>
/// Converts a stream of interleaved float32 I/Q (SoapySDR CF32, normalised to about ±1) into
/// interleaved int16 stereo PCM (I = left, Q = right), the WAV payload
/// <see cref="PcmWavWriter"/> writes and <see cref="PcmWavReader"/> reads. Each float scales by
/// 32767 and CLAMPS to the int16 range — a hot signal saturates, it never wraps. Trailing bytes
/// shorter than one 8-byte complex frame are carried to the next call, so a frame split across two
/// reads is not lost.
/// </summary>
internal sealed class Cf32ToInt16Stereo
{
    private const int FrameBytes = 8; // two float32: I then Q

    private readonly byte[] _carry = new byte[FrameBytes];
    private int _carryLen;

    /// <summary>Converts every whole frame available across the carried remainder and
    /// <paramref name="input"/>, writing 4 bytes (two int16) per frame to <paramref name="output"/>.
    /// Returns the number of bytes written. <paramref name="output"/> must hold at least
    /// <c>((_carry + input) / 8) * 4</c> bytes; a buffer half the size of the input read is always
    /// enough.</summary>
    public int Convert(ReadOnlySpan<byte> input, Span<byte> output)
    {
        int outPos = 0;

        // Finish a frame that straddled the previous chunk boundary.
        if (_carryLen > 0)
        {
            int need = FrameBytes - _carryLen;
            if (input.Length < need)
            {
                input.CopyTo(_carry.AsSpan(_carryLen));
                _carryLen += input.Length;
                return 0;
            }
            input[..need].CopyTo(_carry.AsSpan(_carryLen));
            outPos += ConvertFrame(_carry, output);
            input = input[need..];
            _carryLen = 0;
        }

        int whole = input.Length / FrameBytes;
        for (int f = 0; f < whole; f++)
        {
            outPos += ConvertFrame(input.Slice(f * FrameBytes, FrameBytes), output[outPos..]);
        }

        int tail = input.Length - (whole * FrameBytes);
        if (tail > 0)
        {
            input[(whole * FrameBytes)..].CopyTo(_carry);
            _carryLen = tail;
        }
        return outPos;
    }

    private static int ConvertFrame(ReadOnlySpan<byte> frame, Span<byte> output)
    {
        float i = BinaryPrimitives.ReadSingleLittleEndian(frame);
        float q = BinaryPrimitives.ReadSingleLittleEndian(frame[4..]);
        BinaryPrimitives.WriteInt16LittleEndian(output, ToInt16(i));
        BinaryPrimitives.WriteInt16LittleEndian(output[2..], ToInt16(q));
        return 4;
    }

    /// <summary>Scales by full scale and clamps — never wraps.</summary>
    internal static short ToInt16(float v) =>
        (short)Math.Clamp((int)MathF.Round(v * 32767f), short.MinValue, short.MaxValue);
}
