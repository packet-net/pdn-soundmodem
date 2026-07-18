using M0LTE.Radio.Audio;
using System.Diagnostics;
using System.IO.Ports;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Channel;
using M0LTE.Dsp;
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
double qpskRollOff = QpskModulator.DefaultRollOff;
string? recordPath = null;
int settleMs = 1500;
int? ourTxDelayMs = null;

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
        case "--qpsk-rolloff": qpskRollOff = double.Parse(Next()); break;
        case "--record": recordPath = Next(); break;
        case "--settle-ms": settleMs = int.Parse(Next()); break;
        // Our TX preamble, when it should differ from the NinoTNC's TXDELAY: the two
        // directions have independent minima and conflating them hides which end is the
        // constraint.
        case "--our-txdelay-ms": ourTxDelayMs = int.Parse(Next()); break;
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
int dspRate = ourMode.StartsWith("fsk", StringComparison.Ordinal)
    || ourMode.StartsWith("c4fsk", StringComparison.Ordinal) ? 48000 : 12000;
const int CaptureRate = 48000;

// ---- our modem ----
var received = new List<byte[]>();
var receivedGate = new object();
IModem modem = ourMode switch
{
    "afsk1200" => new Afsk1200Modem(dspRate, OnFrame),
    "afsk1200-multi" => new Afsk1200MultiModem(dspRate, OnFrame, offsetPairs: 3),
    "afsk1200-il2p" => new Afsk1200Il2pModem(dspRate, OnFrame, crc: true),
    "afsk300" => new Afsk300Modem(dspRate, OnFrame, Afsk300Framing.Ax25),
    "afsk300-il2p" => new Afsk300Modem(dspRate, OnFrame, Afsk300Framing.Il2p),
    "afsk300-il2pc" => new Afsk300Modem(dspRate, OnFrame, Afsk300Framing.Il2pCrc),
    "bpsk300" => new BpskModem(dspRate, OnFrame, crc: true),
    "bpsk1200" => BpskModem.Bpsk1200(dspRate, OnFrame),
    "qpsk600" => QpskModem.Qpsk600(dspRate, OnFrame, true, qpskRollOff),
    "qpsk2400" => QpskModem.Qpsk2400(dspRate, OnFrame, true, qpskRollOff),
    "qpsk3600" => QpskModem.Qpsk3600(dspRate, OnFrame, true, qpskRollOff),
    "fsk9600" => FskModem.Fsk9600(dspRate, OnFrame, FskFraming.ClassicHdlc),
    "fsk9600-il2p" => new FskModem(dspRate, OnFrame, FskFraming.Il2pCrc),
    "fsk4800-il2p" => FskModem.Fsk4800(dspRate, OnFrame),
    "c4fsk9600" => C4fskModem.C4fsk9600(dspRate, OnFrame),
    "c4fsk19200" => C4fskModem.C4fsk19200(dspRate, OnFrame),
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
                    ReportPreamble(text[eq..]);
                    return text[eq..];
                }
            }
        }

        Thread.Sleep(50);
    }

    Console.WriteLine($"  [diag {label}] GETALL: no response in 2.5 s (serial RX unproven!)");
    return null;
}

// PreamblCnt (register 0B) is a readback of the configured preamble in 16-bit words, so
// it says what TXDELAY the firmware ACTUALLY applied — the only honest check that a
// KISS TXDELAY landed, and in which units.
void ReportPreamble(string diag)
{
    int i = diag.IndexOf("PreamblCnt:", StringComparison.Ordinal);
    if (i < 0)
    {
        return;
    }

    if (int.TryParse(diag.Substring(i + 11, 8), System.Globalization.NumberStyles.HexNumber, null, out int words))
    {
        int bitRate = NinoBitRate(ninoMode);
        Console.WriteLine(
            $"      applied preamble: {words} words = {words * 16000.0 / bitRate:F0} ms at {bitRate} bps" +
            $" (requested TXDELAY {txDelayMs} ms)");
    }
}

static int NinoBitRate(byte mode) => mode switch
{
    0 or 2 or 3 => 9600, 1 => 19200, 4 => 4800, 5 => 3600,
    6 or 7 or 10 => 1200, 8 or 12 or 13 or 14 => 300, 9 => 600, 11 => 2400, _ => 1200,
};

// Firmware mode byte (low byte of BrdSwchMod) -> DIP mode, per packet.net's
// NinoTncCatalog. The firmware does NOT report the DIP number back.
var firmwareByteToMode = new Dictionary<byte, byte>
{
    [0x00] = 0, [0x41] = 1, [0xB0] = 2, [0x40] = 3, [0xA3] = 4, [0xF1] = 5, [0x02] = 6,
    [0x93] = 7, [0x91] = 8, [0x92] = 9, [0xA0] = 10, [0xA2] = 11, [0x31] = 12, [0x22] = 13,
    [0x23] = 14, [0x90] = 14, [0xF3] = 15,
};

byte? RunningMode(string diag)
{
    int i = diag.IndexOf("BrdSwchMod:", StringComparison.Ordinal);
    if (i < 0 || !int.TryParse(diag.Substring(i + 11, 8), System.Globalization.NumberStyles.HexNumber, null, out int v))
    {
        return null;
    }

    return firmwareByteToMode.TryGetValue((byte)(v & 0xFF), out byte m) ? m : (byte)255;
}

// Set mode (SETHW payload = mode + 16 => applied without touching flash — upstream: "To
// prevent an immediate flash memory write, add 16"), let the modem settle, then TXDELAY.
//
// VERIFY the mode took. SETHW is fire-and-forget and has been observed to silently not
// apply, which is indistinguishable at the frame level from "this mode is broken": both
// directions simply score 0 while the TNC happily runs the *previous* mode. Every result
// this rig prints depends on the mode actually being what we asked for, so prove it from
// BrdSwchMod rather than assume it.
bool modeOk = false;
for (int attempt = 1; attempt <= 3 && !modeOk; attempt++)
{
    SerialKiss(new KissFrame(0, KissCommand.SetHardware, [(byte)(ninoMode + 16)]));
    Thread.Sleep(settleMs);
    string? diag = GetAll($"mode-verify attempt {attempt}");
    byte? running = diag is null ? null : RunningMode(diag);
    if (running == ninoMode)
    {
        modeOk = true;
    }
    else
    {
        Console.Error.WriteLine(
            $"  !! SETHW did not take: asked for mode {ninoMode}, TNC reports " +
            $"{(running is null or 255 ? "unknown" : running.ToString())} — retrying");
    }
}

if (!modeOk)
{
    Console.Error.WriteLine($"FAILED to put the NinoTNC in mode {ninoMode} after 3 attempts");
    return 1;
}

SerialKiss(new KissFrame(0, KissCommand.TxDelay, [(byte)(txDelayMs / 10)]));
Thread.Sleep(400);
Console.WriteLine(
    $"NinoTNC mode {ninoMode} set (non-persist, verified), settle {settleMs} ms," +
    $" TXDELAY {txDelayMs} ms; our mode {ourMode} @ {dspRate} Hz");

// ---- audio ----
using var capture = AlsaPcm.Open(audioDevice, AlsaPcm.Direction.Capture, 1, CaptureRate);
IAudioOutput playback = dspRate == CaptureRate
    ? new AlsaAudioOutput(audioDevice, CaptureRate)
    : new UpsamplingAudioOutput(new AlsaAudioOutput(audioDevice, CaptureRate), dspRate);

var decimator = dspRate == CaptureRate ? null : new Decimator(CaptureRate, CaptureRate / dspRate);
var recorded = recordPath is null ? null : new List<float>();
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
            recorded?.AddRange(floats.AsSpan(0, got));
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
        "bpsk300" or "afsk300" or "afsk300-il2p" or "afsk300-il2pc" => 300,
        "qpsk600" => 600, "bpsk1200" => 1200,
        "qpsk2400" => 2400, "qpsk3600" => 3600,
        "fsk9600" or "fsk9600-il2p" or "c4fsk9600" => 9600,
        "fsk4800-il2p" => 4800, "c4fsk19200" => 19200, _ => 1200,
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
    Thread.Sleep(100);
    WriteRecording();
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
        $"  #{seq:D2} decode={(decoded ? "ok" : "MISS")} audio={audioStart:F0}..{audioEnd:F0}ms" +
        $" (burst {audioEnd - audioStart:F0}ms) dcd={dcdOn:F0}..{dcdOff:F0}ms");
}

// ---- Direction A: us -> NinoTNC ----
Console.WriteLine($"— us -> NinoTNC: {frames} frames, our preamble {ourTxDelayMs ?? txDelayMs} ms");
GetAll("before-A");
int okA = 0;
for (int seq = 100; seq < 100 + frames; seq++)
{
    byte[] frame = MakeFrame(seq);
    int before;
    lock (serialGate) { before = serialFrames.Count; }
    float[] audio = modem.Modulate(frame, ourTxDelayMs ?? txDelayMs);
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
Console.WriteLine(
    $"== {ourMode}:{ninoMode}  nino->us {okB}/{frames}  us->nino {okA}/{frames}" +
    $"  rxPeak {peakAll:F3}  captureXruns {capture.Xruns}");
if (dcdAssertLagsMs.Count > 0)
{
    Console.WriteLine(
        $"   DCD assert lag ms: min {dcdAssertLagsMs.Min():F0} avg {dcdAssertLagsMs.Average():F0} max {dcdAssertLagsMs.Max():F0}" +
        $" | release lag ms: min {dcdReleaseLagsMs.Min():F0} avg {dcdReleaseLagsMs.Average():F0} max {dcdReleaseLagsMs.Max():F0} (n={dcdReleaseLagsMs.Count})");
}

pumpRunning = false;
Thread.Sleep(100);
WriteRecording();
(playback as IDisposable)?.Dispose();
return 0;

void WriteRecording()
{
    if (recordPath is null || recorded is null)
    {
        return;
    }

    float[] samples;
    lock (envelopeGate) { samples = recorded.ToArray(); }
    WavFile.WriteMono(recordPath, samples, CaptureRate);
    Console.WriteLine($"recorded {samples.Length / (double)CaptureRate:F1}s of {CaptureRate} Hz capture -> {recordPath}");
}
