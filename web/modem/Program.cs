using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using M0LTE.Dsp;
using Packet.SoundModem.Modems;

// The whole browser-facing surface of the modem. A page opens a mode, pushes the audio its
// AudioWorklet captured, pulls decoded AX.25 frames, reads carrier sense, and asks for a
// frame to be modulated. Everything crossing this boundary is at the AUDIO DEVICE rate
// (48 kHz from WebAudio, typically): the anti-aliased decimation down to the mode's DSP
// rate and the image-rejecting upsample back out are done here, by the same M0LTE.Dsp
// Decimator/Upsampler the daemon uses on a real card, so the page never resamples and the
// browser's own resampler never touches modem audio.

public static partial class Modem
{
    /// <summary>
    /// One open modem. A page normally has one - one radio, one channel - but the daemon's
    /// model is several modems sharing a channel, and a handle costs nothing to carry, so
    /// nothing here has to be rewritten the day a page wants two.
    /// </summary>
    private sealed class Chain
    {
        public required IModem Modem { get; init; }
        public required int DspRate { get; init; }
        public Decimator? Decimator { get; init; }
        public Upsampler? Upsampler { get; init; }
        public float[] Decimated = [];
        public float[] Upsampled = [];
        public readonly List<byte[]> Frames = [];
    }

    private static readonly Dictionary<int, Chain> _chains = [];
    private static int _nextHandle = 1;

    private static Chain At(int handle) =>
        _chains.TryGetValue(handle, out Chain? chain)
            ? chain
            : throw new ArgumentException($"no open modem with handle {handle}", nameof(handle));

    /// <summary>Every mode the catalogue can build.</summary>
    [JSExport]
    public static string[] Modes() => [.. ModemCatalog.KnownModes];

    /// <summary>The sample rate the catalogue runs this mode's DSP at.</summary>
    [JSExport]
    public static int DspRateFor(string mode) => ModemCatalog.DspRateFor(mode);

    /// <summary>
    /// Opens a mode for audio arriving at, and leaving at, <paramref name="audioRate"/>.
    /// When that is a whole multiple of the mode's DSP rate the audio is decimated into the
    /// chain and upsampled out of it; otherwise the chain simply runs at the audio rate.
    /// Returns a handle every other call takes.
    /// </summary>
    [JSExport]
    public static int Open(string mode, int audioRate)
    {
        int dspRate = ModemCatalog.DspRateFor(mode);
        Decimator? decimator = null;
        Upsampler? upsampler = null;
        if (audioRate != dspRate && audioRate % dspRate == 0)
        {
            int factor = audioRate / dspRate;
            decimator = new Decimator(audioRate, factor);
            upsampler = new Upsampler(audioRate, factor);
        }
        else
        {
            dspRate = audioRate;
        }

        List<byte[]>? frames = null;
        IModem modem = ModemCatalog.Create(mode, dspRate, f => frames!.Add(f));
        var chain = new Chain
        {
            Modem = modem,
            DspRate = dspRate,
            Decimator = decimator,
            Upsampler = upsampler,
        };
        frames = chain.Frames;

        int handle = _nextHandle++;
        _chains[handle] = chain;
        return handle;
    }

    /// <summary>
    /// The rate the chain behind <paramref name="handle"/> runs its DSP at. Recorded when the
    /// mode was opened rather than re-derived from the modem: a modem's own <c>Mode</c> string
    /// is not always the catalogue key that built it (<c>fsk9600-il2p</c> reports itself
    /// differently), and re-deriving quietly returned the wrong rate.
    /// </summary>
    [JSExport]
    public static int DspRateOf(int handle) => At(handle).DspRate;

    /// <summary>Closes a modem and releases it.</summary>
    [JSExport]
    public static void Close(int handle) => _chains.Remove(handle);

    /// <summary>Feeds received audio: little-endian float32 PCM at the audio rate.</summary>
    [JSExport]
    public static void Feed(int handle, byte[] pcm)
    {
        Chain chain = At(handle);
        ReadOnlySpan<float> samples = MemoryMarshal.Cast<byte, float>(pcm);
        if (chain.Decimator is null)
        {
            chain.Modem.Process(samples);
            return;
        }

        int max = chain.Decimator.MaxOutput(samples.Length);
        if (chain.Decimated.Length < max)
        {
            chain.Decimated = new float[max];
        }

        int produced = chain.Decimator.Process(samples, chain.Decimated);
        chain.Modem.Process(chain.Decimated.AsSpan(0, produced));
    }

    /// <summary>
    /// Frames decoded since the last call, each preceded by its length as a little-endian
    /// uint16. Empty when nothing decoded - the common case, so it allocates nothing.
    /// </summary>
    [JSExport]
    public static byte[] TakeFrames(int handle)
    {
        List<byte[]> frames = At(handle).Frames;
        if (frames.Count == 0)
        {
            return [];
        }

        int total = 0;
        foreach (byte[] f in frames)
        {
            total += 2 + f.Length;
        }

        byte[] packed = new byte[total];
        int at = 0;
        foreach (byte[] f in frames)
        {
            packed[at++] = (byte)(f.Length & 0xFF);
            packed[at++] = (byte)(f.Length >> 8);
            f.CopyTo(packed, at);
            at += f.Length;
        }

        frames.Clear();
        return packed;
    }

    /// <summary>True while the demodulator sees a coherent packet signal.</summary>
    [JSExport]
    public static bool CarrierDetect(int handle) => At(handle).Modem.CarrierDetect;

    /// <summary>True while there is in-band energy of any kind - the CSMA input.</summary>
    [JSExport]
    public static bool ChannelBusy(int handle) => At(handle).Modem.ChannelBusy;

    /// <summary>Clears receive carrier state; call on unkey, as the channel does.</summary>
    [JSExport]
    public static void ResetCarrierState(int handle) => At(handle).Modem.ResetCarrierState();

    /// <summary>
    /// Modulates one AX.25 frame (no flags, no FCS) to little-endian float32 PCM at the
    /// audio rate, TXDELAY included in the returned audio.
    /// </summary>
    [JSExport]
    public static byte[] Modulate(int handle, byte[] ax25Frame, int txDelayMilliseconds)
    {
        Chain chain = At(handle);
        float[] audio = chain.Modem.Modulate(ax25Frame, txDelayMilliseconds);
        ReadOnlySpan<float> outgoing = audio;
        if (chain.Upsampler is not null)
        {
            int needed = chain.Upsampler.OutputLength(audio.Length);
            if (chain.Upsampled.Length < needed)
            {
                chain.Upsampled = new float[needed];
            }

            chain.Upsampler.Process(audio, chain.Upsampled.AsSpan(0, needed));
            outgoing = chain.Upsampled.AsSpan(0, needed);
        }

        byte[] bytes = new byte[outgoing.Length * 4];
        MemoryMarshal.AsBytes(outgoing).CopyTo(bytes);
        return bytes;
    }
}

public static class Program
{
    // The bundle is loaded as a library by the page; Main exists so the same bundle runs
    // under Node for the parity harness.
    public static int Main() => 0;
}
