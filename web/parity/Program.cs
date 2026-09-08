// The native twin of wasm/modem: the same catalogue mode, the same anti-alias decimation and
// image-rejecting upsample, the same 128-sample feed quanta, so the two outputs can be diffed.
// If WebAssembly changed a decode, this is what says so.
//
//   sm-wasm-parity decode <file.wav> <mode>
//   sm-wasm-parity loopback <mode> [audioRate]
using M0LTE.Dsp;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Modems;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: sm-wasm-parity decode <file.wav> <mode> | loopback <mode> [audioRate]");
    return 2;
}

string command = args[0].EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? "decode" : args[0];
string[] rest = args[0].EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? args : args[1..];

if (command == "decode")
{
    string path = rest[0];
    string mode = rest[1];
    (float[] samples, int audioRate) = WavFile.ReadMono(path);
    var chain = new Chain(mode, audioRate);
    chain.Feed(samples);
    chain.Flush();

    int index = 0;
    foreach (byte[] f in chain.Frames)
    {
        Console.WriteLine($"[{++index}] {f.Length} bytes  {Convert.ToHexString(f).ToLowerInvariant()}");
    }

    Console.WriteLine($"{chain.Frames.Count} frames from {path} ({mode}, {audioRate} Hz in, chain at {chain.DspRate} Hz)");
    return 0;
}

if (command == "loopback")
{
    string mode = rest[0];
    int audioRate = rest.Length > 1 ? int.Parse(rest[1]) : 48000;
    var chain = new Chain(mode, audioRate);
    byte[] sent = UiFrame("TEST", "M0LTE", $"hello from wasm over {mode}");
    float[] audio = chain.Modulate(sent, 300);
    chain.Feed(audio);
    chain.Flush();

    string sentHex = Convert.ToHexString(sent);
    bool ok = chain.Frames.Any(f => Convert.ToHexString(f) == sentHex);
    Console.WriteLine($"{mode}: {audio.Length / (double)audioRate:F2} s of TX audio at {audioRate} Hz " +
        $"(chain {chain.DspRate} Hz), {chain.Frames.Count} frame(s) back, round trip {(ok ? "IDENTICAL" : "MISMATCH")}");
    return ok ? 0 : 1;
}

Console.Error.WriteLine($"unknown command '{command}'");
return 2;

static byte[] UiFrame(string dest, string source, string text)
{
    static void Address(Span<byte> into, string call, int ssid, bool last)
    {
        for (int i = 0; i < 6; i++)
        {
            into[i] = (byte)((i < call.Length ? call[i] : ' ') << 1);
        }

        into[6] = (byte)(0x60 | (ssid << 1) | (last ? 1 : 0));
    }

    byte[] payload = System.Text.Encoding.ASCII.GetBytes(text);
    byte[] frame = new byte[16 + payload.Length];
    Address(frame.AsSpan(0, 7), dest, 0, last: false);
    Address(frame.AsSpan(7, 7), source, 1, last: true);
    frame[14] = 0x03;
    frame[15] = 0xF0;
    payload.CopyTo(frame, 16);
    return frame;
}

/// <summary>The native mirror of the wasm module's Open/Feed/Modulate.</summary>
file sealed class Chain
{
    private readonly IModem _modem;
    private readonly Decimator? _decimator;
    private readonly Upsampler? _upsampler;
    private readonly float[] _scratch;
    private readonly int _audioRate;

    public Chain(string mode, int audioRate)
    {
        _audioRate = audioRate;
        int dspRate = ModemCatalog.DspRateFor(mode);
        if (audioRate != dspRate && audioRate % dspRate == 0)
        {
            int factor = audioRate / dspRate;
            _decimator = new Decimator(audioRate, factor);
            _upsampler = new Upsampler(audioRate, factor);
        }
        else
        {
            dspRate = audioRate;
        }

        DspRate = dspRate;
        _scratch = new float[_decimator?.MaxOutput(audioRate) ?? 0];
        _modem = ModemCatalog.Create(mode, dspRate, Frames.Add);
    }

    public int DspRate { get; }

    public List<byte[]> Frames { get; } = [];

    public void Feed(ReadOnlySpan<float> samples)
    {
        for (int at = 0; at < samples.Length; at += Quantum)
        {
            ReadOnlySpan<float> block = samples[at..Math.Min(at + Quantum, samples.Length)];
            if (_decimator is null)
            {
                _modem.Process(block);
                continue;
            }

            int produced = _decimator.Process(block, _scratch);
            _modem.Process(_scratch.AsSpan(0, produced));
        }
    }

    public void Flush()
    {
        float[] quiet = new float[Quantum];
        for (int at = 0; at < _audioRate / 2; at += Quantum)
        {
            Feed(quiet);
        }
    }

    public float[] Modulate(byte[] frame, int txDelayMilliseconds)
    {
        float[] audio = _modem.Modulate(frame, txDelayMilliseconds);
        if (_upsampler is null)
        {
            return audio;
        }

        float[] wide = new float[_upsampler.OutputLength(audio.Length)];
        _upsampler.Process(audio, wide);
        return wide;
    }

    private const int Quantum = 128;
}
