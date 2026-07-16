using Packet.SoundModem.Audio;
using Packet.SoundModem.Fx25;
using Packet.SoundModem.Hdlc;
using Packet.SoundModem.Il2p;
using Packet.SoundModem.Modems;

// sm-decode: offline WAV decoder — this project's equivalent of direwolf's `atest`,
// for corpus benchmarking and cross-validation against other modems.
//
//   sm-decode <file.wav> [afsk1200|bpsk300|bpsk1200|qpsk600|qpsk2400|qpsk3600|
//                         fsk9600|fsk9600-il2p|fsk4800|fsk4800-il2p] [--il2p] [--crc] [--quiet]
//
// afsk1200 (default): classic AX.25 (NRZI + HDLC), or IL2P-over-AFSK with --il2p
// (per the IL2P symbol map AFSK carries raw bits — no NRZI — mark = '1').
// The bpsk/qpsk modes imply IL2P; pass --crc for the IL2P+CRC (NinoTNC) variants.
// Prints one line per decoded frame and a final count.

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: sm-decode <file.wav> [afsk1200|bpsk300] [--il2p] [--crc] [--quiet]");
    return 2;
}

string path = args[0];
string mode = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "afsk1200";
bool il2p = args.Contains("--il2p")
    || mode is "bpsk300" or "bpsk1200" or "qpsk600" or "qpsk2400" or "qpsk3600"
        or "fsk9600-il2p" or "fsk4800-il2p";
bool crc = args.Contains("--crc");
bool fx25 = args.Contains("--fx25");
bool quiet = args.Contains("--quiet");

var (samples, sampleRate) = WavFile.ReadMono(path);

// Flush tail: a file can end flush with the last closing flag, which would otherwise be
// stranded inside the demodulator's FIR pipeline (a live stream never "ends").
Array.Resize(ref samples, samples.Length + sampleRate / 2);

int count = 0;

void OnFrame(byte[] frame)
{
    count++;
    if (!quiet)
    {
        Console.WriteLine($"[{count}] {Monitor.Format(frame)}");
    }
}

Action<int> bitSink;
if (il2p)
{
    var deframer = new Il2pDeframer((frame, _) => OnFrame(frame), crcMode: crc);
    bitSink = deframer.PushBit;
}
else if (fx25)
{
    var deframer = new Fx25Deframer((frame, corrected) =>
    {
        if (!quiet)
        {
            Console.WriteLine($"    (fx25, {corrected} bytes corrected)");
        }

        OnFrame(frame);
    });
    var nrzi = new NrziDecoder();
    bitSink = level => deframer.PushBit(nrzi.Decode(level));
}
else
{
    var deframer = new HdlcDeframer(OnFrame);
    var nrzi = new NrziDecoder();
    bitSink = level => deframer.PushBit(nrzi.Decode(level));
}

switch (mode)
{
    case "afsk1200":
        new AfskDemodulator(sampleRate, bitSink).Process(samples);
        break;
    case "afsk1200-multi":
        new Afsk1200MultiModem(sampleRate, OnFrame, offsetPairs: 3).Process(samples);
        break;
    case "bpsk300":
        new BpskDemodulator(sampleRate, bitSink).Process(samples);
        break;
    case "bpsk1200":
        new BpskDemodulator(sampleRate, bitSink, 1500, 1200).Process(samples);
        break;
    case "qpsk600":
        new QpskDemodulator(sampleRate, 300, (a, b) => { bitSink(a); bitSink(b); }, 1500).Process(samples);
        break;
    case "qpsk2400":
        new QpskDemodulator(sampleRate, 1200, (a, b) => { bitSink(a); bitSink(b); }, 1500).Process(samples);
        break;
    case "qpsk3600":
        // Match QpskModem.Qpsk3600's narrower loop (1800*0.03): at 6⅔ samples/symbol the
        // 6% default tracks noise instead of carrier (issue #5).
        new QpskDemodulator(sampleRate, 1800, (a, b) => { bitSink(a); bitSink(b); }, 1650,
            loopBandwidthHz: 1800 * 0.03).Process(samples);
        break;
    case "fsk9600" or "fsk9600-il2p" or "fsk4800" or "fsk4800-il2p":
    {
        int baud = mode.StartsWith("fsk4800", StringComparison.Ordinal) ? 4800 : 9600;
        bool classic = mode is "fsk9600" or "fsk4800";
        var framing = classic
            ? FskFraming.ClassicHdlc
            : (crc ? FskFraming.Il2pCrc : FskFraming.Il2p);
        var modem = new FskModem(sampleRate, OnFrame, framing, baud);
        modem.Process(samples);
        break;
    }
    default:
        Console.Error.WriteLine($"unknown mode '{mode}'");
        return 2;
}

Console.WriteLine($"{count} frames decoded from {Path.GetFileName(path)} ({mode}{(il2p ? " il2p" : "")})");
return 0;

internal static class Monitor
{
    /// <summary>Formats an AX.25 frame as SRC>DEST[,digis]:info for eyeballing.</summary>
    internal static string Format(byte[] frame)
    {
        if (frame.Length < 15)
        {
            return Convert.ToHexString(frame);
        }

        static string Call(ReadOnlySpan<byte> address)
        {
            var chars = new char[6];
            for (int i = 0; i < 6; i++)
            {
                chars[i] = (char)(address[i] >> 1);
            }

            int ssid = (address[6] >> 1) & 0xF;
            string call = new string(chars).TrimEnd();
            return ssid == 0 ? call : $"{call}-{ssid}";
        }

        string dest = Call(frame.AsSpan(0, 7));
        string source = Call(frame.AsSpan(7, 7));
        int position = 14;
        var digis = new List<string>();
        while ((frame[position - 1] & 0x01) == 0 && position + 7 <= frame.Length)
        {
            digis.Add(Call(frame.AsSpan(position, 7)));
            position += 7;
        }

        string via = digis.Count > 0 ? "," + string.Join(',', digis) : "";
        string payload = position + 2 <= frame.Length
            ? System.Text.Encoding.Latin1.GetString(frame, position + 2, frame.Length - position - 2)
            : "";
        return $"{source}>{dest}{via}:{payload}";
    }
}
