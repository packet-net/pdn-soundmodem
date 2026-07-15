using Packet.SoundModem.Audio;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Dsp;
using Packet.SoundModem.Kiss;
using Packet.SoundModem.Modems;

// pdn-soundmodem: headless soundcard packet modem daemon.
//
//   pdn-soundmodem [--device default] [--capture-rate 48000] [--kiss 8105]
//                  [--modem N:MODE[:FREQ]]... [--ptt serial:/dev/ttyUSB0[:rts|:dtr]]
//                  [--txdelay MS] [--wav FILE]
//
// Modes: afsk1200, bpsk300 (IL2P+CRC), bpsk300-nocrc. Multiple --modem options share the
// audio channel and are addressed by the KISS port nibble (QtSoundModem multiplex model).
// --wav decodes a file instead of live audio (testing/corpus runs) and exits.

const int DspRate = 12000;

string device = "default";
int captureRate = 48000;
int kissPort = 8105;
int txDelay = 300;
string? wavPath = null;
string? pttSpec = null;
var modemSpecs = new List<string>();

for (int i = 0; i < args.Length; i++)
{
    string Next() => ++i < args.Length
        ? args[i]
        : throw new ArgumentException($"{args[i - 1]} needs a value");
    switch (args[i])
    {
        case "--device": device = Next(); break;
        case "--capture-rate": captureRate = int.Parse(Next()); break;
        case "--kiss": kissPort = int.Parse(Next()); break;
        case "--modem": modemSpecs.Add(Next()); break;
        case "--ptt": pttSpec = Next(); break;
        case "--txdelay": txDelay = int.Parse(Next()); break;
        case "--wav": wavPath = Next(); break;
        case "--help":
            Console.WriteLine("see source header for usage");
            return 0;
        default:
            Console.Error.WriteLine($"unknown option {args[i]}");
            return 2;
    }
}

if (modemSpecs.Count == 0)
{
    modemSpecs.Add("0:afsk1200");
}

if (captureRate % DspRate != 0)
{
    Console.Error.WriteLine($"--capture-rate must be a multiple of {DspRate}");
    return 2;
}

var channel = new SoundModemChannel(DspRate);
channel.Csma.TxDelayMilliseconds = txDelay;

foreach (string spec in modemSpecs)
{
    string[] parts = spec.Split(':');
    int subChannel = int.Parse(parts[0]);
    string mode = parts.Length > 1 ? parts[1] : "afsk1200";
    double? frequency = parts.Length > 2 ? double.Parse(parts[2]) : null;
    channel.AddModem(subChannel, sink => mode switch
    {
        "afsk1200" => new Afsk1200Modem(DspRate, sink, frequency ?? 1700),
        "bpsk300" => new Bpsk300Modem(DspRate, sink, crc: true, frequency ?? 1500),
        "bpsk300-nocrc" => new Bpsk300Modem(DspRate, sink, crc: false, frequency ?? 1500),
        _ => throw new ArgumentException($"unknown mode '{mode}'"),
    });
    Console.WriteLine($"modem {subChannel}: {mode} @ {frequency ?? (mode == "afsk1200" ? 1700 : 1500)} Hz");
}

channel.FrameReceived += (subChannel, frame) =>
    Console.WriteLine($"rx[{subChannel}] {frame.Length} bytes");

if (wavPath is not null)
{
    var (samples, rate) = WavFile.ReadMono(wavPath);
    Array.Resize(ref samples, samples.Length + rate / 2);
    if (rate != DspRate)
    {
        if (rate % DspRate != 0)
        {
            Console.Error.WriteLine($"wav rate {rate} is not a multiple of {DspRate}");
            return 2;
        }

        var decimator = new Decimator(rate, rate / DspRate);
        var decimated = new float[decimator.MaxOutput(samples.Length)];
        int produced = decimator.Process(samples, decimated);
        samples = decimated[..produced];
    }

    int frames = 0;
    channel.FrameReceived += (_, _) => frames++;
    channel.ProcessReceive(samples);
    Console.WriteLine($"{frames} frames decoded");
    return 0;
}

IPttControl ptt = new NullPtt();
if (pttSpec is not null)
{
    string[] parts = pttSpec.Split(':');
    if (parts.Length < 2 || parts[0] != "serial")
    {
        Console.Error.WriteLine("--ptt expects serial:<device>[:rts|:dtr]");
        return 2;
    }

    string line = parts.Length > 2 ? parts[2] : "rts";
    ptt = new SerialPtt(parts[1], useRts: line != "dtr", useDtr: line == "dtr");
    Console.WriteLine($"ptt: {parts[1]} ({line})");
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

await using var server = new KissTcpServer(channel, kissPort);
server.Start();
Console.WriteLine($"kiss tcp: 127.0.0.1:{server.LocalPort}");

// Transmit side: modulate at the DSP rate; the ALSA plug layer upsamples. (Proper
// interpolation or native-rate modulators are a Phase 3 refinement.)
var playback = new AlsaAudioOutput(device, DspRate);
Task transmitter = channel.RunTransmitterAsync(playback, ptt, cancellation.Token);

// Receive side: capture at the card-native rate, decimate to the DSP rate.
using var capture = AlsaPcm.Open(device, AlsaPcm.Direction.Capture, channels: 1, sampleRate: captureRate);
var rxDecimator = new Decimator(captureRate, captureRate / DspRate);
Console.WriteLine($"audio: {device} capture {captureRate} Hz → {DspRate} Hz");

var pcmBuffer = new short[captureRate / 10]; // 100 ms blocks
var floatBuffer = new float[pcmBuffer.Length];
var dspBuffer = new float[rxDecimator.MaxOutput(pcmBuffer.Length)];
while (!cancellation.IsCancellationRequested)
{
    int got = capture.Read(pcmBuffer);
    for (int i = 0; i < got; i++)
    {
        floatBuffer[i] = pcmBuffer[i] / 32768f;
    }

    int produced = rxDecimator.Process(floatBuffer.AsSpan(0, got), dspBuffer);
    channel.ProcessReceive(dspBuffer.AsSpan(0, produced));
}

await transmitter.ContinueWith(_ => { }, TaskScheduler.Default);
(ptt as IDisposable)?.Dispose();
playback.Dispose();
return 0;
