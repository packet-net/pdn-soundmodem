using Packet.SoundModem.Audio;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.Dsp;
using Packet.SoundModem.Kiss;
using Packet.SoundModem.Modems;

// pdn-soundmodem: headless soundcard packet modem daemon.
//
//   pdn-soundmodem [--config soundmodem.json]
//   pdn-soundmodem [--device default] [--capture-rate 48000] [--kiss 8105]
//                  [--modem N:MODE[:FREQ]]... [--ptt serial:/dev/ttyUSB0[:rts|:dtr]]
//                  [--ptt cm108:/dev/hidraw0[:gpio]]
//                  [--txdelay MS] [--wav FILE]
//
// Modes: afsk1200, bpsk300 (IL2P+CRC), bpsk300-nocrc, qpsk2400, qpsk3600 (both IL2P+CRC),
// fsk9600 (classic G3RUH), fsk9600-il2p (IL2P+CRC). Multiple --modem options share the
// audio channel and are addressed by the KISS port nibble (QtSoundModem multiplex model).
// --wav decodes a file instead of live audio (testing/corpus runs) and exits.

// 9600-family modems need 48 kHz; everything else runs at 12 kHz.

string device = "default";
int captureRate = 48000;
int kissPort = 8105;
int txDelay = 300;
string? wavPath = null;
string? pttSpec = null;
string? configPath = null;
var modemSpecs = new List<string>();

for (int i = 0; i < args.Length; i++)
{
    string Next() => ++i < args.Length
        ? args[i]
        : throw new ArgumentException($"{args[i - 1]} needs a value");
    switch (args[i])
    {
        case "--config": configPath = Next(); break;
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

var modems = new List<ModemConfig>();
CsmaConfig csma = new() { TxDelayMilliseconds = txDelay };
PttConfig? pttConfig = null;

if (configPath is not null)
{
    DaemonConfig config = DaemonConfig.Load(configPath);
    device = config.Device;
    captureRate = config.CaptureRate;
    kissPort = config.KissPort;
    modems = config.Modems;
    csma = config.Csma;
    pttConfig = config.Ptt;
    Console.WriteLine($"config: {configPath}");
}

foreach (string spec in modemSpecs)
{
    string[] specParts = spec.Split(':');
    modems.Add(new ModemConfig
    {
        SubChannel = int.Parse(specParts[0]),
        Mode = specParts.Length > 1 ? specParts[1] : "afsk1200",
        Frequency = specParts.Length > 2 ? double.Parse(specParts[2]) : null,
    });
}

if (modems.Count == 0)
{
    modems.Add(new ModemConfig());
}

int DspRate = modems.Any(m => m.Mode.Contains("9600", StringComparison.Ordinal)) ? 48000 : 12000;

if (captureRate % DspRate != 0)
{
    Console.Error.WriteLine($"--capture-rate must be a multiple of {DspRate}");
    return 2;
}

var channel = new SoundModemChannel(DspRate);
channel.Csma.TxDelayMilliseconds = csma.TxDelayMilliseconds;
channel.Csma.Persistence = csma.Persistence;
channel.Csma.SlotTimeMilliseconds = csma.SlotTimeMilliseconds;
channel.Csma.TxTailMilliseconds = csma.TxTailMilliseconds;

foreach (ModemConfig modemConfig in modems)
{
    int subChannel = modemConfig.SubChannel;
    string mode = modemConfig.Mode;
    double? frequency = modemConfig.Frequency;
    channel.AddModem(subChannel, sink => mode switch
    {
        "afsk1200" => new Afsk1200Modem(DspRate, sink, frequency ?? 1700),
        "afsk1200-fx25" => new Afsk1200Modem(DspRate, sink, frequency ?? 1700, Fx25Mode.TransmitReceive),
        "afsk1200-fx25rx" => new Afsk1200Modem(DspRate, sink, frequency ?? 1700, Fx25Mode.Receive),
        "afsk1200-multi" => new Afsk1200MultiModem(DspRate, sink, offsetPairs: 3, centerFrequency: frequency ?? 1700),
        "afsk1200-il2p" => new Afsk1200Il2pModem(DspRate, sink, crc: true, frequency ?? 1700),
        "afsk1200-il2p-nocrc" => new Afsk1200Il2pModem(DspRate, sink, crc: false, frequency ?? 1700),
        "bpsk300" => new BpskModem(DspRate, sink, crc: true, frequency ?? 1500),
        "bpsk300-nocrc" => new BpskModem(DspRate, sink, crc: false, frequency ?? 1500),
        "bpsk1200" => BpskModem.Bpsk1200(DspRate, sink),
        "qpsk600" => QpskModem.Qpsk600(DspRate, sink),
        "qpsk2400" => QpskModem.Qpsk2400(DspRate, sink),
        "qpsk3600" => QpskModem.Qpsk3600(DspRate, sink),
        "fsk9600" => FskModem.Fsk9600(DspRate, sink, FskFraming.ClassicHdlc),
        "fsk9600-il2p" => new FskModem(DspRate, sink, FskFraming.Il2pCrc),
        "fsk4800-il2p" => FskModem.Fsk4800(DspRate, sink),
        _ => throw new ArgumentException($"unknown mode '{mode}'"),
    });
    Console.WriteLine($"modem {subChannel}: {mode}{(frequency is { } f ? $" @ {f} Hz" : "")}");
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

if (pttSpec is not null)
{
    string[] parts = pttSpec.Split(':');
    if (parts.Length >= 2)
    {
        pttConfig = new PttConfig
        {
            Type = parts[0],
            Device = parts[1],
            Line = parts[0] == "serial" && parts.Length > 2 ? parts[2] : null,
            Gpio = parts[0] == "cm108" && parts.Length > 2 ? int.Parse(parts[2]) : null,
        };
    }
    else
    {
        Console.Error.WriteLine("--ptt expects serial:<dev>[:rts|:dtr] or cm108:<hidraw>[:gpio]");
        return 2;
    }
}

IPttControl ptt = new NullPtt();
switch (pttConfig?.Type)
{
    case null:
        break;
    case "serial":
        string line = pttConfig.Line ?? "rts";
        ptt = new SerialPtt(pttConfig.Device, useRts: line != "dtr", useDtr: line == "dtr");
        Console.WriteLine($"ptt: serial {pttConfig.Device} ({line})");
        break;
    case "cm108":
        int gpio = pttConfig.Gpio ?? 3;
        ptt = new Cm108Ptt(pttConfig.Device, gpio);
        Console.WriteLine($"ptt: cm108 {pttConfig.Device} (gpio {gpio})");
        break;
    default:
        Console.Error.WriteLine($"unknown ptt type '{pttConfig.Type}'");
        return 2;
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

// Transmit side: modulate at the DSP rate; play at the card-native capture rate through
// the image-rejecting upsampler (cards commonly refuse to open 12 kHz playback directly).
IAudioOutput playback = captureRate == DspRate
    ? new AlsaAudioOutput(device, DspRate)
    : new UpsamplingAudioOutput(new AlsaAudioOutput(device, captureRate), DspRate);
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
(playback as IDisposable)?.Dispose();
return 0;
