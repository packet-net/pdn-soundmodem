using System.Diagnostics;
using System.Net.Sockets;
using Packet.SoundModem.Kiss;

// qtsm-bench: the QtSoundModem <-> pdn-soundmodem KISS-TCP interop driver
// (docs/qtsm-loop.md). Pure KISS-over-TCP — no audio, no modems: it assumes the two
// modems are already talking over an snd-aloop cable and each is serving KISS TCP, and it
// only pushes frames in and counts what comes out the other side.
//
//   qtsm-bench --qtsm-port 8300 --our-port 8310 [--host 127.0.0.1] --label <mode>
//              [--frames 10] [--payload 40] [--direction both|ours2qtsm|qtsm2ours]
//              [--frame-timeout-ms 6000] [--settle-ms 800]
//
// Direction "ours2qtsm": a Q0AAA UI frame is written to OUR daemon's KISS TCP (which
//   modulates it onto the cable); we wait for the same frame to surface on QtSM's KISS TCP
//   (QtSM having demodulated it). "qtsm2ours" is the mirror. Both KISS servers broadcast
//   received frames to every connected client, and neither echoes a client's own transmit
//   frame back, so a frame appearing on the *other* end's socket is a genuine over-the-air
//   decode, not a loopback of our own send.
//
// A frame is matched by a per-run nonce embedded in its 40-byte self-describing payload,
// so stale frames left in a socket buffer from an earlier cell can never be miscounted.

string host = "127.0.0.1";
int qtsmPort = 8300;
int ourPort = 8310;
string label = "afsk1200";
int frames = 10;
int payloadLength = 40;
string direction = "both";
int frameTimeoutMs = 6000;
int settleMs = 800;

for (int i = 0; i < args.Length; i++)
{
    string Next() => ++i < args.Length ? args[i] : throw new ArgumentException($"{args[i - 1]} needs a value");
    switch (args[i])
    {
        case "--host": host = Next(); break;
        case "--qtsm-port": qtsmPort = int.Parse(Next()); break;
        case "--our-port": ourPort = int.Parse(Next()); break;
        case "--label": label = Next(); break;
        case "--frames": frames = int.Parse(Next()); break;
        case "--payload": payloadLength = int.Parse(Next()); break;
        case "--direction": direction = Next(); break;
        case "--frame-timeout-ms": frameTimeoutMs = int.Parse(Next()); break;
        case "--settle-ms": settleMs = int.Parse(Next()); break;
        default: Console.Error.WriteLine($"unknown option {args[i]}"); return 2;
    }
}

// A short run nonce keeps this cell's frames distinct from any other run's.
string nonce = Random.Shared.Next(0x1000, 0xFFFF).ToString("X4");

var qtsm = new KissEndpoint("QtSM", host, qtsmPort);
var ours = new KissEndpoint("ours", host, ourPort);
try
{
    qtsm.Connect();
    ours.Connect();
}
catch (SocketException ex)
{
    Console.Error.WriteLine($"connect failed: {ex.Message}");
    return 1;
}

// Let both receive paths settle (KISS servers, capture threads, DCD) before driving.
Thread.Sleep(settleMs);

int okOursToQtsm = -1;
int okQtsmToOurs = -1;

if (direction is "both" or "ours2qtsm")
{
    okOursToQtsm = RunDirection("ours -> QtSM", ours, qtsm);
}

if (direction is "both" or "qtsm2ours")
{
    okQtsmToOurs = RunDirection("QtSM -> ours", qtsm, ours);
}

Console.WriteLine(
    $"== {label} nonce {nonce}  " +
    (okQtsmToOurs >= 0 ? $"qtsm->ours {okQtsmToOurs}/{frames}  " : "") +
    (okOursToQtsm >= 0 ? $"ours->qtsm {okOursToQtsm}/{frames}" : ""));

qtsm.Dispose();
ours.Dispose();
return 0;

// Sends `frames` frames into `sender` and counts how many surface on `receiver`.
int RunDirection(string name, KissEndpoint sender, KissEndpoint receiver)
{
    Console.WriteLine($"— {name}: {frames} frames, {payloadLength}-byte payloads");
    int ok = 0;
    for (int seq = 0; seq < frames; seq++)
    {
        byte[] token = System.Text.Encoding.ASCII.GetBytes($"{nonce}-{label}-{seq:D4}");
        byte[] frame = MakeUiFrame(token, payloadLength);

        // Snapshot the receiver's current frame count so we only inspect frames that
        // arrive after we transmit this one.
        int fromIndex = receiver.FrameCount;
        sender.SendData(port: 0, frame);

        var clock = Stopwatch.StartNew();
        bool decoded = false;
        while (clock.ElapsedMilliseconds < frameTimeoutMs)
        {
            if (receiver.AnyContains(token, fromIndex))
            {
                decoded = true;
                break;
            }

            Thread.Sleep(5);
        }

        if (decoded)
        {
            ok++;
        }

        Console.WriteLine($"  #{seq:D2} {(decoded ? $"ok ({clock.ElapsedMilliseconds} ms)" : "MISS")}");
    }

    Console.WriteLine($"  {name}: {ok}/{frames}");
    return ok;
}

// A minimal, spec-valid AX.25 UI frame: Q0AAA > QST, control 0x03 (UI), PID 0xF0, then
// the self-describing payload. The receiving modem strips the FCS and hands addr+ctrl+
// pid+info back over KISS, so `token` survives verbatim for matching.
static byte[] MakeUiFrame(byte[] token, int payloadLength)
{
    var payload = new byte[payloadLength];
    int copy = Math.Min(token.Length, payloadLength);
    Array.Copy(token, payload, copy);
    for (int i = copy; i < payloadLength; i++)
    {
        payload[i] = (byte)('A' + (i % 26));
    }

    return
    [
        .. Addr("QST", 0, last: false, c: 1), .. Addr("Q0AAA", 0, last: true, c: 0),
        0x03, 0xF0, .. payload,
    ];

    static byte[] Addr(string call, int ssid, bool last, int c)
    {
        var b = new byte[7];
        for (int i = 0; i < 6; i++)
        {
            b[i] = (byte)((i < call.Length ? call[i] : ' ') << 1);
        }

        b[6] = (byte)((c << 7) | 0x60 | (ssid << 1) | (last ? 1 : 0));
        return b;
    }
}

// One KISS-over-TCP peer: a socket, a background read pump feeding a KissDecoder, and the
// list of Data frames it has delivered (guarded for the poll loop on the main thread).
sealed class KissEndpoint : IDisposable
{
    private readonly string _name;
    private readonly string _host;
    private readonly int _port;
    private readonly List<byte[]> _received = [];
    private readonly object _gate = new();
    private TcpClient? _client;
    private NetworkStream? _stream;
    private Thread? _pump;
    private volatile bool _running;

    public KissEndpoint(string name, string host, int port)
    {
        _name = name;
        _host = host;
        _port = port;
    }

    public void Connect()
    {
        _client = new TcpClient { NoDelay = true };
        _client.Connect(_host, _port);
        _stream = _client.GetStream();
        _running = true;
        var decoder = new KissDecoder(OnFrame);
        _pump = new Thread(() =>
        {
            var buffer = new byte[4096];
            try
            {
                while (_running)
                {
                    int got = _stream.Read(buffer, 0, buffer.Length);
                    if (got <= 0)
                    {
                        return;
                    }

                    decoder.Push(buffer.AsSpan(0, got));
                }
            }
            catch (Exception)
            {
                // Socket torn down at shutdown — expected.
            }
        })
        { IsBackground = true, Name = $"kiss-{_name}" };
        _pump.Start();
        Console.WriteLine($"connected {_name} -> {_host}:{_port}");
    }

    private void OnFrame(KissFrame frame)
    {
        if (frame.Command != KissCommand.Data)
        {
            return; // ignore TX monitor / quality / ackmode chatter
        }

        lock (_gate)
        {
            _received.Add(frame.Payload);
        }
    }

    public int FrameCount
    {
        get { lock (_gate) { return _received.Count; } }
    }

    // True if any Data frame received at/after `fromIndex` contains `token` as a
    // contiguous byte subsequence.
    public bool AnyContains(byte[] token, int fromIndex)
    {
        lock (_gate)
        {
            for (int i = fromIndex; i < _received.Count; i++)
            {
                if (IndexOf(_received[i], token) >= 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    public void SendData(int port, byte[] frame)
    {
        byte[] encoded = KissCodec.Encode(new KissFrame(port, KissCommand.Data, frame));
        _stream!.Write(encoded, 0, encoded.Length);
        _stream.Flush();
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return -1;
        }

        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j])
            {
                j++;
            }

            if (j == needle.Length)
            {
                return i;
            }
        }

        return -1;
    }

    public void Dispose()
    {
        _running = false;
        try { _stream?.Dispose(); } catch (Exception) { }
        try { _client?.Dispose(); } catch (Exception) { }
    }
}
