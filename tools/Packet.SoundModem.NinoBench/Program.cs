using System.Diagnostics;
using System.IO.Ports;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Dsp;
using Packet.SoundModem.Kiss;
using Packet.SoundModem.Modems;

// nino-bench: the wired CM108 <-> NinoTNC interop rig driver (docs/ninotnc-loop.md).
//
//   nino-bench --serial <dev> [--audio plughw:3,0] --pair <ourmode>:<ninomode>
//              [--frames 10] [--txdelay-ms 300] [--payload 40] [--level-check]
//
// Per pair: sets the NinoTNC mode via KISS SETHW (+16 non-persist) and TXDELAY, then
//   A) us -> NinoTNC: modulate frames to the card; expect them on the serial KISS.
//   B) NinoTNC -> us: send frames down serial KISS; expect our demod to decode them;
//      while they play, sample our carrier-sense to measure DCD assert/deassert lag
//      against the audio envelope (the end-of-DCD accuracy the CSMA stack relies on).
// Reports decode rates, RX audio peak (deviation/pot verdict) and DCD timing.

string serialDevice = "";
string audioDevice = "plughw:3,0";
string pair = "afsk1200:6";
int frames = 10;
int txDelayMs = 300;
int payloadLength = 40;
bool levelCheckOnly = false;

for (int i = 0; i < args.Length; i++)
{
    string Next() => ++i < args.Length ? args[i] : throw new ArgumentException($"{args[i - 1]} needs a value");
    switch (args[i])
    {
        case "--serial": serialDevice = Next(); break;
        case "--audio": audioDevice = Next(); break;
        case "--pair": pair = Next(); break;
        case "--frames": frames = int.Parse(Next()); break;
        case "--txdelay-ms": txDelayMs = int.Parse(Next()); break;
        case "--payload": payloadLength = int.Parse(Next()); break;
        case "--level-check": levelCheckOnly = true; break;
        default: Console.Error.WriteLine($"unknown option {args[i]}"); return 2;
    }
}

if (serialDevice.Length == 0)
{
    Console.Error.WriteLine("--serial <NinoTNC device> is required");
    return 2;
}

string[] pairParts = pair.Split(':');
string ourMode = pairParts[0];
byte ninoMode = byte.Parse(pairParts[1]);
int dspRate = ourMode.StartsWith("fsk9600", StringComparison.Ordinal) ? 48000 : 12000;
const int CaptureRate = 48000;

// ---- our modem ----
var received = new List<byte[]>();
var receivedGate = new object();
IModem modem = ourMode switch
{
    "afsk1200" => new Afsk1200Modem(dspRate, OnFrame),
    "afsk1200-multi" => new Afsk1200MultiModem(dspRate, OnFrame, offsetPairs: 3),
    "bpsk300" => new Bpsk300Modem(dspRate, OnFrame, crc: true),
    "qpsk2400" => QpskModem.Qpsk2400(dspRate, OnFrame),
    "qpsk3600" => QpskModem.Qpsk3600(dspRate, OnFrame),
    "fsk9600" => new Fsk9600Modem(dspRate, OnFrame, Fsk9600Framing.ClassicHdlc),
    "fsk9600-il2p" => new Fsk9600Modem(dspRate, OnFrame, Fsk9600Framing.Il2pCrc),
    _ => throw new ArgumentException($"unknown our-mode '{ourMode}'"),
};

void OnFrame(byte[] frame)
{
    lock (receivedGate)
    {
        received.Add(frame);
    }
}

// ---- serial KISS to the NinoTNC ----
using var serial = new SerialPort(serialDevice, 57600) { ReadTimeout = 200 };
serial.Open();
var serialFrames = new List<byte[]>();
var serialGate = new object();
var kissDecoder = new KissDecoder(f =>
{
    if (f.Command == KissCommand.Data)
    {
        lock (serialGate)
        {
            serialFrames.Add(f.Payload);
        }
    }
});
var serialPump = new Thread(() =>
{
    var buffer = new byte[4096];
    while (serial.IsOpen)
    {
        try
        {
            int got = serial.BaseStream.Read(buffer, 0, buffer.Length);
            if (got > 0)
            {
                kissDecoder.Push(buffer.AsSpan(0, got));
            }
        }
        catch (TimeoutException)
        {
            // ReadTimeout elapsed with no traffic — keep listening.
        }
        catch (Exception)
        {
            return;
        }
    }
})
{ IsBackground = true };
serialPump.Start();

void SerialSend(ReadOnlySpan<byte> raw) => serial.BaseStream.Write(raw.ToArray(), 0, raw.Length);
void SerialKiss(KissFrame frame) => SerialSend(KissCodec.Encode(frame));

string AsciiPreview(byte[] payload)
{
    var sb = new System.Text.StringBuilder(payload.Length);
    foreach (byte b in payload)
    {
        sb.Append(b is >= 32 and < 127 ? (char)b : '·');
    }

    return sb.ToString();
}

// GETALL (KISS command 0x0B): the NinoTNC answers with a diagnostic-register report.
// Doubles as proof the serial receive path works, and registers 07 (AX.25 RX count)
// and 0E (cumulative DCD-on ms) discriminate "no audio at RXA" from "audio but no
// decode" when direction A misses.
string? GetAll(string label)
{
    int before;
    lock (serialGate) { before = serialFrames.Count; }
    SerialKiss(new KissFrame(0, (KissCommand)0x0B, [0x00]));
    var sw = Stopwatch.StartNew();
    while (sw.ElapsedMilliseconds < 2500)
    {
        lock (serialGate)
        {
            foreach (var f in serialFrames.Skip(before))
            {
                string text = AsciiPreview(f);
                int eq = text.IndexOf('=');
                if (eq >= 0)
                {
                    Console.WriteLine($"  [diag {label}] {text[eq..]}");
                    return text[eq..];
                }
            }
        }

        Thread.Sleep(50);
    }

    Console.WriteLine($"  [diag {label}] GETALL: no response in 2.5 s (serial RX unproven!)");
    return null;
}

// Set mode (SETHW payload = mode + 16 => applied without touching flash) + TXDELAY.
SerialKiss(new KissFrame(0, KissCommand.SetHardware, [(byte)(ninoMode + 16)]));
Thread.Sleep(300);
SerialKiss(new KissFrame(0, KissCommand.TxDelay, [(byte)(txDelayMs / 10)]));
Thread.Sleep(200);
Console.WriteLine($"NinoTNC mode {ninoMode} set (non-persist), TXDELAY {txDelayMs} ms; our mode {ourMode} @ {dspRate} Hz");

// ---- audio ----
using var capture = AlsaPcm.Open(audioDevice, AlsaPcm.Direction.Capture, 1, CaptureRate);
IAudioOutput playback = dspRate == CaptureRate
    ? new AlsaAudioOutput(audioDevice, CaptureRate)
    : new UpsamplingAudioOutput(new AlsaAudioOutput(audioDevice, CaptureRate), dspRate);

var decimator = dspRate == CaptureRate ? null : new Decimator(CaptureRate, CaptureRate / dspRate);
double capturePeak = 0;
double envelope = 0;
var envelopeGate = new object();
bool pumpRunning = true;
var capturePump = new Thread(() =>
{
    var pcm = new short[CaptureRate / 50]; // 20 ms
    var floats = new float[pcm.Length];
    var dsp = new float[decimator?.MaxOutput(pcm.Length) ?? pcm.Length];
    try
    {
        Pump();
    }
    catch (Exception) when (!pumpRunning)
    {
        // Torn down mid-read (the main thread disposed the PCM) — expected at exit.
    }

    void Pump()
    {
    while (pumpRunning)
    {
        int got = capture.Read(pcm);
        double peak = 0;
        for (int i = 0; i < got; i++)
        {
            floats[i] = pcm[i] / 32768f;
            peak = Math.Max(peak, Math.Abs(floats[i]));
        }

        lock (envelopeGate)
        {
            envelope = peak;
            capturePeak = Math.Max(capturePeak, peak);
        }

        if (decimator is null)
        {
            modem.Process(floats.AsSpan(0, got));
        }
        else
        {
            int produced = decimator.Process(floats.AsSpan(0, got), dsp);
            modem.Process(dsp.AsSpan(0, produced));
        }
    }
    }
})
{ IsBackground = true, Name = "bench-capture" };
capturePump.Start();

byte[] MakeFrame(int seq)
{
    // BENCH-1 > NINO UI frame with a unique, recognisable payload.
    var payload = new byte[payloadLength];
    var tag = System.Text.Encoding.ASCII.GetBytes($"BENCH {ourMode} #{seq:D4} ");
    tag.CopyTo(payload, 0);
    for (int i = tag.Length; i < payload.Length; i++)
    {
        payload[i] = (byte)('A' + (seq + i) % 26);
    }

    return
    [
        .. Addr("NINO", 0, last: false, c: 1), .. Addr("BENCH", 1, last: true, c: 0),
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

double AirtimeSeconds(int frameBytes)
{
    int bitRate = ourMode switch
    {
        "bpsk300" => 300, "qpsk2400" => 2400, "qpsk3600" => 3600,
        "fsk9600" or "fsk9600-il2p" => 9600, _ => 1200,
    };
    // Generous: IL2P/HDLC overhead + preamble + margin.
    return (txDelayMs / 1000.0) + (frameBytes + 80) * 8.0 / bitRate + 0.5;
}

if (levelCheckOnly)
{
    // Command one NinoTNC transmission and meter it.
    lock (envelopeGate) { capturePeak = 0; }
    SerialKiss(new KissFrame(0, KissCommand.Data, MakeFrame(0)));
    Thread.Sleep((int)(AirtimeSeconds(payloadLength + 16) * 1000) + 500);
    double peak;
    lock (envelopeGate) { peak = capturePeak; }
    int decoded;
    lock (receivedGate) { decoded = received.Count; }
    string verdict = peak switch
    {
        < 0.05 => "TOO LOW — raise NinoTNC TX-DEV pot (or our capture gain)",
        > 0.90 => "CLIPPING — lower NinoTNC TX-DEV pot (or our capture gain)",
        _ => "GOOD",
    };
    Console.WriteLine($"level-check: capture peak {peak:F3} full-scale -> {verdict}; decoded {decoded}/1");
    pumpRunning = false;
    (playback as IDisposable)?.Dispose();
    return 0;
}

// ---- Direction B: NinoTNC -> us (with DCD timing) ----
Console.WriteLine($"— NinoTNC -> us: {frames} frames");
int okB = 0;
var dcdAssertLagsMs = new List<double>();
var dcdReleaseLagsMs = new List<double>();
for (int seq = 0; seq < frames; seq++)
{
    byte[] frame = MakeFrame(seq);
    int before;
    lock (receivedGate) { before = received.Count; }

    var clock = Stopwatch.StartNew();
    double audioStart = -1, audioEnd = -1, dcdOn = -1, dcdOff = -1;
    SerialKiss(new KissFrame(0, KissCommand.Data, frame));

    double deadline = AirtimeSeconds(frame.Length) + 1.5;
    bool sawAudio = false, sawDcd = false;
    while (clock.Elapsed.TotalSeconds < deadline)
    {
        double env;
        lock (envelopeGate) { env = envelope; }
        bool busy = modem.ChannelBusy;
        double t = clock.Elapsed.TotalMilliseconds;
        if (!sawAudio && env > 0.04) { audioStart = t; sawAudio = true; }
        if (sawAudio && audioEnd < 0 && env < 0.02 && t > audioStart + 100) { audioEnd = t; }
        if (!sawDcd && busy) { dcdOn = t; sawDcd = true; }
        if (sawDcd && dcdOff < 0 && !busy) { dcdOff = t; }
        if (audioEnd > 0 && (dcdOff > 0 || !sawDcd) && t > audioEnd + 400)
        {
            break;
        }

        Thread.Sleep(2);
    }

    int after;
    lock (receivedGate) { after = received.Count; }
    bool decoded = after > before;
    if (decoded)
    {
        okB++;
    }

    if (audioStart >= 0 && dcdOn >= 0)
    {
        dcdAssertLagsMs.Add(dcdOn - audioStart);
    }

    if (audioEnd >= 0 && dcdOff >= 0)
    {
        dcdReleaseLagsMs.Add(dcdOff - audioEnd);
    }

    Console.WriteLine(
        $"  #{seq:D2} decode={(decoded ? "ok" : "MISS")} audio={audioStart:F0}..{audioEnd:F0}ms dcd={dcdOn:F0}..{dcdOff:F0}ms");
}

// ---- Direction A: us -> NinoTNC ----
Console.WriteLine($"— us -> NinoTNC: {frames} frames");
GetAll("before-A");
int okA = 0;
for (int seq = 100; seq < 100 + frames; seq++)
{
    byte[] frame = MakeFrame(seq);
    int before;
    lock (serialGate) { before = serialFrames.Count; }
    float[] audio = modem.Modulate(frame, txDelayMs);
    playback.Write(audio);
    playback.Drain();

    var clock = Stopwatch.StartNew();
    bool got = false;
    while (clock.Elapsed.TotalSeconds < 3)
    {
        lock (serialGate)
        {
            got = serialFrames.Skip(before).Any(f => f.AsSpan().SequenceEqual(frame));
        }

        if (got)
        {
            break;
        }

        Thread.Sleep(20);
    }

    if (got)
    {
        okA++;
    }

    Console.WriteLine($"  #{seq:D2} nino-decode={(got ? "ok" : "MISS")}");
}

GetAll("after-A");

double peakAll;
lock (envelopeGate) { peakAll = capturePeak; }
Console.WriteLine($"== {ourMode}:{ninoMode}  nino->us {okB}/{frames}  us->nino {okA}/{frames}  rxPeak {peakAll:F3}");
if (dcdAssertLagsMs.Count > 0)
{
    Console.WriteLine(
        $"   DCD assert lag ms: min {dcdAssertLagsMs.Min():F0} avg {dcdAssertLagsMs.Average():F0} max {dcdAssertLagsMs.Max():F0}" +
        $" | release lag ms: min {dcdReleaseLagsMs.Min():F0} avg {dcdReleaseLagsMs.Average():F0} max {dcdReleaseLagsMs.Max():F0} (n={dcdReleaseLagsMs.Count})");
}

pumpRunning = false;
(playback as IDisposable)?.Dispose();
return 0;
