using Packet.SoundModem.Audio;
using M0LTE.Dsp;
using Packet.SoundModem.Modems;

// sm-samples: one reference WAV per mode, for sharing and for regression material.
//
//   sm-samples <outputDir> [--txdelay 50] [--gap-ms 1000] [--source Q0AAA] [--dest TEST]
//                          [--only <mode>] [--native-rate]
//
// Each file holds 10 UI frames of increasing payload (10..200 bytes) separated by silence,
// rendered at 48 kHz mono — the card rate we actually transmit at, so the file is what
// goes on the wire, not an idealised version of it.
//
// --only <mode> renders just one mode; --native-rate writes at the modem's DSP rate (no
// upsample) — e.g. 12 kHz for the audio-band PSK/AFSK modes, for the QtSM snd-aloop rig
// (docs/qtsm-loop.md), whose PSK modems run at 12 kHz. The default set (48 kHz, all modes)
// is unchanged so samples/pdn regenerates byte-for-byte.

string outDir = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal)
    ? args[0]
    : "samples";
int txDelayMs = 50, gapMs = 1000;
string source = "Q0AAA", dest = "TEST";
string? only = null;
bool nativeRate = false;
for (int i = 0; i < args.Length; i++)
{
    string Next() => ++i < args.Length ? args[i] : throw new ArgumentException($"{args[i - 1]} needs a value");
    switch (args[i])
    {
        case "--txdelay": txDelayMs = int.Parse(Next()); break;
        case "--gap-ms": gapMs = int.Parse(Next()); break;
        case "--source": source = Next(); break;
        case "--dest": dest = Next(); break;
        case "--only": only = Next(); break;
        case "--native-rate": nativeRate = true; break;
    }
}

Directory.CreateDirectory(outDir);
const int CardRate = 48000;
int[] payloadLengths = [10, 30, 50, 70, 90, 110, 130, 150, 175, 200];

static byte[] Address(string call, int ssid, bool last, int commandBit)
{
    var b = new byte[7];
    for (int i = 0; i < 6; i++)
    {
        b[i] = (byte)((i < call.Length ? char.ToUpperInvariant(call[i]) : ' ') << 1);
    }

    b[6] = (byte)((commandBit << 7) | 0x60 | (ssid << 1) | (last ? 1 : 0));
    return b;
}

byte[] Frame(string mode, int seq, int payloadLength)
{
    // Readable, self-describing payload: whoever decodes these should be able to tell at a
    // glance which file, which frame and what length they are looking at.
    var payload = new byte[payloadLength];
    string tag = $"PDN {mode} #{seq:D2} len={payloadLength} ";
    for (int i = 0; i < payload.Length; i++)
    {
        payload[i] = i < tag.Length ? (byte)tag[i] : (byte)('A' + ((i - tag.Length) % 26));
    }

    return
    [
        .. Address(dest, 0, last: false, commandBit: 1),
        .. Address(source, 0, last: true, commandBit: 0),
        0x03, 0xF0,                        // UI, no layer 3
        .. payload,
    ];
}

// (our mode, NinoTNC DIP mode, DSP rate, factory)
var modes = new (string Name, int NinoMode, int Rate, Func<int, IModem> Make)[]
{
    ("fsk9600",       0, 48000, r => FskModem.Fsk9600(r, _ => { }, FskFraming.ClassicHdlc)),
    ("fsk9600-il2p",  2, 48000, r => FskModem.Fsk9600(r, _ => { }, FskFraming.Il2pCrc)),
    ("fsk4800-il2p",  4, 48000, r => FskModem.Fsk4800(r, _ => { })),
    ("qpsk3600",      5, 12000, r => QpskModem.Qpsk3600(r, _ => { })),
    ("afsk1200",      6, 12000, r => new Afsk1200Modem(r, _ => { })),
    ("afsk1200-il2p", 7, 12000, r => new Afsk1200Il2pModem(r, _ => { })),
    ("bpsk300",       8, 12000, r => BpskModem.Bpsk300(r, _ => { })),
    ("qpsk600",       9, 12000, r => QpskModem.Qpsk600(r, _ => { })),
    ("bpsk1200",     10, 12000, r => BpskModem.Bpsk1200(r, _ => { })),
    ("qpsk2400",     11, 12000, r => QpskModem.Qpsk2400(r, _ => { })),
    ("afsk300",      12, 12000, r => new Afsk300Modem(r, _ => { }, Afsk300Framing.Ax25)),
    ("afsk300-il2p", 13, 12000, r => new Afsk300Modem(r, _ => { }, Afsk300Framing.Il2p)),
    ("afsk300-il2pc",14, 12000, r => new Afsk300Modem(r, _ => { }, Afsk300Framing.Il2pCrc)),
};

Console.WriteLine($"{source}>{dest}, {payloadLengths.Length} frames of {payloadLengths[0]}..{payloadLengths[^1]} bytes, "
    + $"{txDelayMs} ms TXDELAY, {gapMs} ms gaps, 48 kHz mono\n");
Console.WriteLine("file                                        mode  duration   frames");

foreach (var (name, ninoMode, rate, make) in modes)
{
    if (only is not null && name != only)
    {
        continue;
    }

    IModem modem = make(rate);
    var samples = new List<float>();
    int gap = rate * gapMs / 1000;
    samples.AddRange(new float[gap / 2]);
    foreach (int length in payloadLengths)
    {
        samples.AddRange(modem.Modulate(Frame(name, Array.IndexOf(payloadLengths, length) + 1, length), txDelayMs));
        samples.AddRange(new float[gap]);
    }

    float[] audio = samples.ToArray();
    int outRate = nativeRate ? rate : CardRate;
    if (!nativeRate && rate != CardRate)
    {
        // Render at the card rate through the same upsampler the daemon transmits with.
        var up = new Upsampler(CardRate, CardRate / rate);
        var rendered = new float[up.OutputLength(audio.Length)];
        up.Process(audio, rendered);
        audio = rendered;
    }

    string file = Path.Combine(outDir, $"pdn-mode{ninoMode:D2}-{name}-{txDelayMs}ms-{outRate / 1000}k.wav");
    WavFile.WriteMono(file, audio, outRate);
    Console.WriteLine($"{Path.GetFileName(file),-43} {ninoMode,4}  {audio.Length / (double)outRate,7:F1}s  {payloadLengths.Length,6}");
}
