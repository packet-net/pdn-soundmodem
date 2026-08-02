using System.Buffers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Packet.SoundModem.UberSdr;

/// <summary>Options for one capture session (one WAV file).</summary>
/// <summary>Receives each block of capture PCM as it arrives, for live processing.</summary>
/// <param name="littleEndianStereoPcm">Interleaved int16 I/Q, exactly as written to the WAV.</param>
public delegate void IqBlockHandler(ReadOnlySpan<byte> littleEndianStereoPcm);

public sealed class UberSdrCaptureOptions
{
    /// <summary>
    /// Called with every block as it is recorded, after the startup guard and after any trim to
    /// the target duration — so what the handler sees is exactly what the WAV holds.
    /// </summary>
    /// <remarks>This is what lets a pass be watched live instead of only scored afterwards. It
    /// runs on the receive loop, so it must not block: anything slower than real time will stall
    /// the socket and the capture will fall behind.</remarks>
    public IqBlockHandler? OnBlock { get; init; }

    public required string Host { get; init; }
    public int Port { get; init; } = 443;
    public bool Ssl { get; init; } = true;
    public required int FrequencyHz { get; init; }
    public string Mode { get; init; } = "iq48";
    public string? Password { get; init; }
    public string? Name { get; init; }
    public string OutputDir { get; init; } = ".";

    /// <summary>Seconds of audio to write (0 = until cancelled or the server closes).</summary>
    public int DurationSeconds { get; init; }

    /// <summary>Discard this much audio after connect before opening the file. C0 found a
    /// ~0.7–1.0 s level ramp at stream start (see the OTA capture-client plan); 1 s clears it.</summary>
    public int StartupGuardMs { get; init; } = 1000;
}

/// <summary>Result of a completed capture.</summary>
public sealed record CaptureResult(
    string WavPath,
    string SidecarPath,
    long Frames,
    int SampleRate,
    DateTime Sample0Utc,
    string WavSha256);

/// <summary>
/// Captures an IQ (or PCM) stream from a ka9q_ubersdr / UberSDR instance to a 16-bit stereo
/// WAV plus a JSON sidecar. Protocol and framing per docs/ms110d/ota-capture-client-plan.md;
/// this is an independent C# implementation of the flow in
/// <c>clients/iq-recorder/main.go</c> from https://github.com/madpsy/ka9q_ubersdr (GPL-3.0).
/// One instance per session/file; the caller reconnects (a fresh instance) per ladder pass.
/// </summary>
public sealed class UberSdrIqClient
{
    private const string UserAgent = "UberSDR IQ Recorder (pdn-soundmodem sm-iqcapture)";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly Action<string>? _log;

    public UberSdrIqClient(Action<string>? log = null) => _log = log;

    public async Task<CaptureResult> CaptureAsync(UberSdrCaptureOptions opt, CancellationToken ct)
    {
        string sessionId = Guid.NewGuid().ToString();
        string httpBase = $"{(opt.Ssl ? "https" : "http")}://{opt.Host}:{opt.Port}";

        ConnectionResponse conn = await CheckConnectionAsync(httpBase, sessionId, opt.Password, ct);
        if (!conn.Allowed)
        {
            throw new InvalidOperationException($"connection rejected: {conn.Reason}");
        }
        if (conn.AllowedIqModes is { Count: > 0 } modes && !conn.Bypassed && !modes.Contains(opt.Mode))
        {
            throw new InvalidOperationException(
                $"mode '{opt.Mode}' not allowed by server (allowed: {string.Join(", ", modes)})");
        }
        _log?.Invoke($"connection allowed (ip {conn.ClientIp}, bypassed {conn.Bypassed}, " +
                     $"max session {conn.MaxSessionTime}s, iq modes [{string.Join(",", conn.AllowedIqModes ?? [])}])");

        string? description = await FetchDescriptionAsync(httpBase, ct);

        string wsScheme = opt.Ssl ? "wss" : "ws";
        var query = new StringBuilder()
            .Append("frequency=").Append(opt.FrequencyHz)
            .Append("&mode=").Append(Uri.EscapeDataString(opt.Mode))
            .Append("&user_session_id=").Append(sessionId)
            .Append("&format=pcm-zstd");
        if (!string.IsNullOrEmpty(opt.Password))
        {
            query.Append("&password=").Append(Uri.EscapeDataString(opt.Password));
        }
        var wsUri = new Uri($"{wsScheme}://{opt.Host}:{opt.Port}/ws?{query}");

        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("User-Agent", UserAgent);
        if (!string.IsNullOrEmpty(opt.Password))
        {
            ws.Options.SetRequestHeader("X-Password", opt.Password);
        }

        _log?.Invoke($"connecting to {wsUri.Scheme}://{opt.Host}:{opt.Port}/ws (mode {opt.Mode})…");
        await ws.ConnectAsync(wsUri, ct);
        _log?.Invoke($"connected; recording {opt.Mode} at {opt.FrequencyHz} Hz");

        return await ReceiveLoopAsync(ws, opt, sessionId, conn, description, ct);
    }

    private async Task<CaptureResult> ReceiveLoopAsync(
        ClientWebSocket ws, UberSdrCaptureOptions opt, string sessionId,
        ConnectionResponse conn, string? description, CancellationToken ct)
    {
        using var decoder = new PcmBinaryDecoder { LastSampleRate = 48000, LastChannels = 2 };
        long guardNs = (long)opt.StartupGuardMs * 1_000_000L;

        ulong firstPacketNs = 0;
        ulong sample0Ns = 0;
        ulong lastNs = 0;
        PcmWavWriter? wav = null;
        string wavPath = "";
        int sampleRate = 48000;
        long targetFrames = opt.DurationSeconds > 0 ? (long)opt.DurationSeconds * sampleRate : long.MaxValue;

        var accumulator = new ArrayBufferWriter<byte>(64 * 1024);
        byte[] rent = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                accumulator.Clear();
                WebSocketReceiveResult res;
                do
                {
                    // A public instance can vanish mid-session (restart, slot reclaim) without
                    // a close handshake. That ends the session; it does not invalidate the
                    // contiguous audio already on disk, so finalise rather than crash.
                    try
                    {
                        res = await ws.ReceiveAsync(rent, ct);
                    }
                    catch (WebSocketException ex)
                    {
                        _log?.Invoke($"connection lost mid-session ({ex.Message}); keeping what was captured");
                        goto done;
                    }
                    if (res.MessageType == WebSocketMessageType.Close)
                    {
                        _log?.Invoke("server closed the connection");
                        goto done;
                    }
                    accumulator.Write(rent.AsSpan(0, res.Count));
                }
                while (!res.EndOfMessage);

                if (res.MessageType != WebSocketMessageType.Binary)
                {
                    continue; // text status frames are irrelevant for iq48
                }

                PcmPacket pkt;
                try
                {
                    pkt = decoder.Decode(accumulator.WrittenSpan, isZstd: true);
                }
                catch (InvalidDataException ex)
                {
                    _log?.Invoke($"skipping undecodable packet ({accumulator.WrittenCount} bytes): {ex.Message}");
                    continue;
                }

                if (pkt.GpsTimestampNanos == 0)
                {
                    continue; // no timestamp yet; can't place it in absolute time
                }
                sampleRate = pkt.SampleRate;
                lastNs = pkt.GpsTimestampNanos;

                if (firstPacketNs == 0)
                {
                    firstPacketNs = pkt.GpsTimestampNanos;
                    _log?.Invoke($"first packet {UnixNanosToUtc(firstPacketNs):yyyy-MM-dd HH:mm:ss.fff} UTC — " +
                                 $"skipping {opt.StartupGuardMs} ms startup guard");
                }

                // Startup guard: discard until we're clear of the connect transient.
                if (pkt.GpsTimestampNanos - firstPacketNs < (ulong)guardNs)
                {
                    continue;
                }

                if (wav is null)
                {
                    sample0Ns = pkt.GpsTimestampNanos;
                    targetFrames = opt.DurationSeconds > 0 ? (long)opt.DurationSeconds * sampleRate : long.MaxValue;
                    string stem = opt.Name ?? opt.Host;
                    string ts = UnixNanosToUtc(sample0Ns).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                    wavPath = Path.Combine(opt.OutputDir, $"{stem}_{opt.FrequencyHz}_{ts}.wav");
                    Directory.CreateDirectory(opt.OutputDir);
                    wav = new PcmWavWriter(wavPath, sampleRate);
                    _log?.Invoke($"recording from {UnixNanosToUtc(sample0Ns):HH:mm:ss.fff} UTC → {Path.GetFileName(wavPath)}");
                }

                ReadOnlySpan<byte> pcm = pkt.Pcm.Span;
                long remainingFrames = targetFrames - wav.FramesWritten;
                if (remainingFrames <= 0)
                {
                    break;
                }
                long pcmFrames = pcm.Length / 4;
                if (pcmFrames > remainingFrames)
                {
                    pcm = pcm[..(int)(remainingFrames * 4)]; // trim the final packet to hit the target exactly
                }
                wav.Write(pcm);
                opt.OnBlock?.Invoke(pcm);

                if (wav.FramesWritten >= targetFrames)
                {
                    break;
                }
            }

        done:
            if (wav is null)
            {
                throw new InvalidOperationException("no data captured (never passed the startup guard)");
            }

            long frames = wav.FramesWritten;
            wav.Dispose();
            wav = null;

            DateTime sample0Utc = UnixNanosToUtc(sample0Ns);
            string sha = await ComputeSha256Async(wavPath);
            string sidecarPath = Path.ChangeExtension(wavPath, ".json");
            WriteSidecar(sidecarPath, opt, sessionId, conn, description, wavPath,
                frames, sampleRate, firstPacketNs, sample0Ns, lastNs, sha);

            double dur = frames / (double)sampleRate;
            _log?.Invoke($"stopped: {frames} frames ({dur:F3} s, {frames * 4 / (1024.0 * 1024.0):F2} MB) → {Path.GetFileName(wavPath)}");
            return new CaptureResult(wavPath, sidecarPath, frames, sampleRate, sample0Utc, sha);
        }
        finally
        {
            wav?.Dispose();
            ArrayPool<byte>.Shared.Return(rent);
            if (ws.State == WebSocketState.Open)
            {
                try
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
                }
                catch (WebSocketException) { /* best-effort close */ }
            }
        }
    }

    private static async Task<ConnectionResponse> CheckConnectionAsync(
        string httpBase, string sessionId, string? password, CancellationToken ct)
    {
        var body = new JsonObject { ["user_session_id"] = sessionId };
        if (!string.IsNullOrEmpty(password))
        {
            body["password"] = password;
        }
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{httpBase}/connection")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        using var resp = await Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<ConnectionResponse>(stream, cancellationToken: ct)
               ?? throw new InvalidDataException("empty /connection response");
    }

    private static async Task<string?> FetchDescriptionAsync(string httpBase, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{httpBase}/api/description");
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            using var resp = await Http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null; // metadata is optional — capture proceeds without it
        }
    }

    private static void WriteSidecar(
        string path, UberSdrCaptureOptions opt, string sessionId, ConnectionResponse conn,
        string? description, string wavPath, long frames, int sampleRate,
        ulong firstPacketNs, ulong sample0Ns, ulong lastNs, string sha)
    {
        var capture = new JsonObject
        {
            ["client"] = UserAgent,
            ["host"] = opt.Host,
            ["port"] = opt.Port,
            ["ssl"] = opt.Ssl,
            ["frequency_hz"] = opt.FrequencyHz,
            ["mode"] = opt.Mode,
            ["format"] = "pcm-zstd",
            ["sample_rate"] = sampleRate,
            ["channels"] = 2,
            ["frames"] = frames,
            ["duration_s"] = Math.Round(frames / (double)sampleRate, 6),
            ["startup_guard_ms"] = opt.StartupGuardMs,
            ["first_packet_utc"] = UnixNanosToUtc(firstPacketNs).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["sample0_utc"] = UnixNanosToUtc(sample0Ns).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["last_sample_utc"] = UnixNanosToUtc(lastNs).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["wav_file"] = Path.GetFileName(wavPath),
            ["wav_sha256"] = sha,
            ["session_id"] = sessionId,
            ["max_session_time_s"] = conn.MaxSessionTime,
        };
        var root = new JsonObject { ["capture"] = capture };
        if (description is not null)
        {
            try { root["receiver_description"] = JsonNode.Parse(description); }
            catch (JsonException) { root["receiver_description_raw"] = description; }
        }
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var fs = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(fs);
        return Convert.ToHexStringLower(hash);
    }

    private static DateTime UnixNanosToUtc(ulong ns) =>
        DateTime.UnixEpoch.AddTicks((long)(ns / 100UL)); // 1 tick = 100 ns
}
