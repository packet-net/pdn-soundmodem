using M0LTE.Flex;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Packet.SoundModem.Ota;

/// <summary>
/// A deliberately independent probe of the FlexRadio meter path: it speaks the ASCII TCP
/// protocol and reads the raw UDP datagrams itself, using <b>none</b> of
/// <c>M0LTE.Flex</c>.
/// </summary>
/// <remarks>
/// <para><b>Why it exists.</b> The transmit hardware meters (FWDPWR, REFPWR, SWR, HWALC) were
/// never arriving under any transmit condition — waveform or plain DIGU tune, interlock
/// confirmed <c>TRANSMITTING</c>. Six hypotheses were disproved without finding the cause,
/// because every diagnostic ran <em>downstream</em> of the library's own packet handling and
/// so could not distinguish "the radio never sent it" from "we discarded it before looking".
/// An instrument built on the suspect component cannot exonerate it.</para>
/// <para>This probe settled it in one run by logging <b>every datagram, whole and
/// unfiltered</b> — the radio was sending all 25 meters, in one stream, in one packet shape
/// (<c>type=3 C=1 T=0 tsi=1 tsf=1</c>, a 28-byte preamble). The fault was a consumer applying
/// <c>PayloadOffset</c> to an array that had already been sliced to the payload; see
/// <c>VitaPacket</c> in <c>M0LTE.Flex</c>, whose whole purpose is now to make that
/// unrepresentable.</para>
/// <para>It is kept because that independence is worth having on hand: the next time a
/// diagnosis stalls, this is the thing that reports what the radio actually sent, owing
/// nothing to the code under suspicion.</para>
/// </remarks>
public static class RawMeterProbe
{
    /// <summary>Runs the probe: connect, register a UDP port, become a GUI client, list and
    /// subscribe to meters, optionally key a tune carrier, and dump every datagram.</summary>
    public static async Task RunAsync(
        string host, string frequencyMHz, string antenna, int tunePower, double tuneSeconds,
        Action<string> log, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(log);

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, 4992, cancellation).ConfigureAwait(false);
        NetworkStream stream = tcp.GetStream();
        var reader = new StreamReader(stream, Encoding.ASCII);
        var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true };

        // Bind our own UDP socket first: the radio needs a port to stream to, and we want the
        // datagrams before anything else touches them.
        using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        udp.Bind(new IPEndPoint(IPAddress.Any, 0));
        int localPort = ((IPEndPoint)udp.LocalEndPoint!).Port;

        var lengths = new Dictionary<int, long>();
        var byClass = new Dictionary<ushort, long>();
        var idCounts = new Dictionary<int, long>();
        var idLast = new Dictionary<int, short>();
        long shortDatagrams = 0;
        long total = 0;
        var gate = new Lock();

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task receive = Task.Run(async () =>
        {
            var buffer = new byte[16384];
            while (!stop.IsCancellationRequested)
            {
                int n;
                try
                {
                    n = await udp.ReceiveAsync(buffer, SocketFlags.None, stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (SocketException)
                {
                    return;
                }

                lock (gate)
                {
                    total++;
                    lengths[n] = lengths.TryGetValue(n, out long c) ? c + 1 : 1;

                    // Anything below the library's 20-byte floor is exactly what it would have
                    // silently discarded — count it separately and print it in full.
                    if (n < 20)
                    {
                        shortDatagrams++;
                        log($"SHORT DATAGRAM ({n} bytes): {Convert.ToHexString(buffer.AsSpan(0, n))}");
                    }

                    ParseAndTally(buffer.AsSpan(0, n), byClass, idCounts, idLast);
                }
            }
        }, stop.Token);

        // --- prologue -------------------------------------------------------------------
        string? version = await reader.ReadLineAsync(cancellation).ConfigureAwait(false);
        string? handle = await reader.ReadLineAsync(cancellation).ConfigureAwait(false);
        log($"prologue: {version} / {handle}");

        var results = new Dictionary<int, TaskCompletionSource<string>>();
        int seq = 0;
        var seqGate = new Lock();

        Task readLines = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (line is null)
                {
                    return;
                }

                if (line.StartsWith('R'))
                {
                    string[] parts = line[1..].Split('|', 3);
                    if (int.TryParse(parts[0], out int rseq))
                    {
                        lock (seqGate)
                        {
                            if (results.Remove(rseq, out TaskCompletionSource<string>? tcs))
                            {
                                tcs.TrySetResult(parts.Length > 2 ? $"{parts[1]}|{parts[2]}" : parts[1]);
                            }
                        }
                    }
                }
                else if (line.StartsWith('M'))
                {
                    log($"radio message: {line}");
                }
                else if (line.StartsWith('S') && line.Contains("interlock", StringComparison.Ordinal)
                         && line.Contains("state=", StringComparison.Ordinal))
                {
                    log($"status: {line[..Math.Min(line.Length, 120)]}");
                }
            }
        }, stop.Token);

        async Task<string> SendAsync(string command)
        {
            int s;
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (seqGate)
            {
                s = ++seq;
                results[s] = tcs;
            }

            await writer.WriteAsync($"C{s}|{command}\n").ConfigureAwait(false);
            Task done = await Task.WhenAny(tcs.Task, Task.Delay(5000, cancellation)).ConfigureAwait(false);
            if (done != tcs.Task)
            {
                log($"  '{command}' TIMED OUT");
                return "timeout";
            }

            string r = await tcs.Task.ConfigureAwait(false);
            log($"  '{command}' → {r[..Math.Min(r.Length, 160)]}");
            return r;
        }

        // --- session --------------------------------------------------------------------
        await SendAsync(string.Create(CultureInfo.InvariantCulture, $"client udpport {localPort}"));
        log($"UDP bound on {localPort}");
        await SendAsync("client gui");

        string meterList = await SendAsync("meter list");
        IReadOnlyList<FlexMeterDescriptor> descriptors = FlexMeters.ParseMeterList(
            meterList.Contains('|') ? meterList[(meterList.IndexOf('|') + 1)..] : meterList);
        log($"{descriptors.Count} meters described");

        await SendAsync("sub meter all");
        foreach (FlexMeterDescriptor d in descriptors)
        {
            await SendAsync(string.Create(CultureInfo.InvariantCulture, $"sub meter {d.Id}"));
        }

        await SendAsync("sub radio all");
        await SendAsync("sub tx all");
        await SendAsync("sub slice all");

        // A slice is needed before the radio will transmit.
        await SendAsync("radio set band_persistence_enabled=0");
        await SendAsync(string.Create(CultureInfo.InvariantCulture,
            $"slice create freq={frequencyMHz} ant={antenna} mode=DIGU rxant={antenna}"));
        await SendAsync(string.Create(CultureInfo.InvariantCulture, $"slice t 0 {frequencyMHz}"));

        log("--- idle for 3 s ---");
        await Task.Delay(3000, cancellation).ConfigureAwait(false);
        long idleTotal = total;

        await SendAsync(string.Create(CultureInfo.InvariantCulture, $"transmit set tunepower={tunePower}"));
        await SendAsync(string.Create(CultureInfo.InvariantCulture, $"transmit set rfpower={tunePower}"));
        log($"--- tune ON at {tunePower}% for {tuneSeconds:F1} s ---");
        await SendAsync("transmit tune on");
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(tuneSeconds), cancellation).ConfigureAwait(false);
        }
        finally
        {
            await SendAsync("transmit tune off");
            log("--- tune OFF ---");
        }

        await Task.Delay(500, cancellation).ConfigureAwait(false);
        await stop.CancelAsync().ConfigureAwait(false);
        udp.Close();
        await Task.WhenAny(Task.WhenAll(receive, readLines), Task.Delay(1000)).ConfigureAwait(false);

        // --- verdict --------------------------------------------------------------------
        lock (gate)
        {
            log("");
            log($"datagrams: {total} total ({idleTotal} before tune), {shortDatagrams} shorter than 20 bytes");
            log("lengths seen: " + string.Join(", ",
                lengths.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}B×{kv.Value}")));
            log("VITA classes: " + string.Join(", ",
                byClass.OrderBy(kv => kv.Key).Select(kv => $"0x{kv.Key:X4}×{kv.Value}")));
            log("");
            log("meter packet header shapes:");
            foreach ((string shape, long n) in Shapes.OrderByDescending(kv => kv.Value))
            {
                log($"  ×{n,-6} {shape}");
            }

            log("");
            log($"{"id",4} {"name",-14} {"count",7}  last raw   scaled");
            var byId = descriptors.ToDictionary(d => d.Id);
            foreach ((int id, long count) in idCounts.OrderBy(kv => kv.Key))
            {
                string name = byId.TryGetValue(id, out FlexMeterDescriptor? d) ? d.Name : "(unknown)";
                string unit = d?.Unit ?? "";
                short raw = idLast[id];
                log($"{id,4} {name,-14} {count,7}  {raw,8}   {FlexMeters.Scale(raw, unit),8:F3} {unit}");
            }

            string missing = string.Join(", ", descriptors
                .Where(d => d.Fps > 0 && !idCounts.ContainsKey(d.Id))
                .Select(d => $"{d.Name}({d.Id})"));
            log("");
            log($"described at >0 fps but NEVER seen on the wire: {(missing.Length == 0 ? "(none)" : missing)}");
        }
    }

    /// <summary>Parses a datagram far more permissively than the library does — no minimum
    /// length, and it reports what it found rather than discarding on doubt.</summary>
    private static void ParseAndTally(
        ReadOnlySpan<byte> data, Dictionary<ushort, long> byClass,
        Dictionary<int, long> idCounts, Dictionary<int, short> idLast)
    {
        if (data.Length < 4)
        {
            return;
        }

        uint header = (uint)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]);
        int packetType = (int)(header >> 28);
        bool hasClassId = (header & 0x08000000) != 0;
        bool hasTrailer = (header & 0x04000000) != 0;
        int tsi = (int)((header >> 22) & 0x03);
        int tsf = (int)((header >> 20) & 0x03);

        int index = 4;
        if (packetType is 1 or 3)
        {
            index += 4; // stream id
        }

        ushort packetClass = 0;
        if (hasClassId)
        {
            if (data.Length < index + 8)
            {
                return;
            }

            packetClass = (ushort)((data[index + 6] << 8) | data[index + 7]);
            index += 8;
        }

        index += tsi != 0 ? 4 : 0;
        index += tsf != 0 ? 8 : 0;

        byClass[packetClass] = byClass.TryGetValue(packetClass, out long c) ? c + 1 : 1;
        if (packetClass != 0x8002)
        {
            return;
        }

        uint streamId = 0;
        if (packetType is 1 or 3)
        {
            streamId = (uint)((data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7]);
        }

        int end = data.Length - (hasTrailer ? 4 : 0);
        var ids = new List<int>();
        for (int i = index; i + 3 < end; i += 4)
        {
            int id = (data[i] << 8) | data[i + 1];
            var raw = (short)((data[i + 2] << 8) | data[i + 3]);
            idCounts[id] = idCounts.TryGetValue(id, out long n) ? n + 1 : 1;
            idLast[id] = raw;
            ids.Add(id);
        }

        // Record the exact header shape carrying each group of ids. Two kinds of meter packet
        // clearly exist (the id 1–9 hardware set arrives at a different rate from the 10+
        // slice set), and the library receives only one of them — so whatever differs in
        // these fields is the bug.
        string shape = $"type={packetType} C={(hasClassId ? 1 : 0)} T={(hasTrailer ? 1 : 0)} " +
                       $"tsi={tsi} tsf={tsf} stream=0x{streamId:X8} len={data.Length} " +
                       $"ids={(ids.Count == 0 ? "-" : $"{ids.Min()}..{ids.Max()}")}";
        Shapes[shape] = Shapes.TryGetValue(shape, out long s) ? s + 1 : 1;
    }

    /// <summary>Header shapes observed on meter packets, by frequency.</summary>
    public static readonly Dictionary<string, long> Shapes = [];
}
