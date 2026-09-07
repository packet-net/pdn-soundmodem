using Packet.SoundModem.Audio;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Tests.Channel;

/// <summary>
/// One decode of one real frame at a chosen receive level: what the sweeps behind
/// <c>docs/receive-levels.md</c> are made of, and what the committed cliff tests re-run a few
/// points of.
/// </summary>
/// <remarks>
/// <para>The chain is the one a station has: a real modulator makes the burst, the burst is
/// scaled so its peak sits at the level under test, AWGN is added at a fixed signal-to-noise
/// ratio, the result goes through a 16-bit converter model (scale, saturate, back to float),
/// and a real <see cref="SoundModemChannel"/> with a real demodulator on it reads the lot in
/// 100 ms blocks. The order matters: the noise arrives at the antenna and the converter
/// quantises and clips signal and noise together, which is why a level below the card's own
/// steps costs decodes and a level above full scale does too.</para>
/// <para>Levels are stated as the burst's peak in dBFS, which is the same quantity the per-frame
/// badge reports (<see cref="FrameQuality.PeakDbFs"/>), so a cliff measured here can be compared
/// with a threshold directly. A level above 0 dBFS is an overdrive: the audio is scaled past
/// full scale and the converter model saturates it, exactly as a sound card does when the
/// capture gain is too high.</para>
/// </remarks>
internal static class ReceiveLevelRig
{
    /// <summary>The transmit delay every burst carries, as a real station sends one.</summary>
    private const int TxDelayMilliseconds = 300;

    /// <summary>
    /// A 15-byte AX.25 supervisory frame - RR with the poll bit clear - which is what most of
    /// the traffic on a working link is and what the radio1 bench heard from GB7RDG.
    /// </summary>
    public static byte[] Supervisory()
    {
        byte[] frame = Ax25UiFrame.Build("GB7RDG", "GB7WOD", ReadOnlySpan<byte>.Empty)[..15];
        frame[14] = 0x01;
        return frame;
    }

    /// <summary>A longer frame: a 64-byte UI frame, the other end of ordinary traffic.</summary>
    public static byte[] Information() =>
        Ax25UiFrame.Build("GB7RDG", "GB7WOD", "the quick brown fox jumps over 0123456789ABCDEFGHIJ"u8);

    /// <summary>
    /// Decodes one frame at one level and returns what came back.
    /// </summary>
    /// <param name="mode">The catalogue mode.</param>
    /// <param name="frame">The AX.25 frame to send.</param>
    /// <param name="peakDbFs">Where to put the burst's peak: 0 is full scale, positive is an
    /// overdrive the converter model will clip.</param>
    /// <param name="snrDb">Signal to noise in a 3 kHz reference bandwidth - this tree's own
    /// convention - or <see cref="double.PositiveInfinity"/> for a converter-noise-only run.</param>
    /// <param name="seed">Seeds the noise and the channel.</param>
    /// <returns>Whether the exact frame came back, and the level the channel reported for it.</returns>
    public static (bool Decoded, double? PeakDbFs, bool? Clipped) Decode(
        string mode, byte[] frame, double peakDbFs, double snrDb, int seed)
    {
        int rate = ModemCatalog.DspRateFor(mode);
        float[] audio = Audio(mode, frame, peakDbFs, snrDb, seed, rate);

        var channel = new SoundModemChannel(rate, randomSeed: seed);
        channel.AddModem(0, sink => ModemCatalog.Create(mode, rate, sink));
        bool decoded = false;
        double? peak = null;
        bool? clipped = null;
        channel.FrameReceivedWithQuality += (_, heard, quality) =>
        {
            if (decoded || !heard.AsSpan().SequenceEqual(frame))
            {
                return;
            }

            decoded = true;
            peak = quality.PeakDbFs;
            clipped = quality.Clipped;
        };

        // 100 ms blocks, which is what every station without ARDOP on it reads, and the block
        // size a whole fast-mode frame fits inside.
        int block = rate / 10;
        for (int at = 0; at < audio.Length; at += block)
        {
            int take = Math.Min(block, audio.Length - at);
            channel.NoteCardClipping(audio.AsSpan(at, take));
            channel.ProcessReceive(audio.AsSpan(at, take));
        }

        return (decoded, peak, clipped);
    }

    /// <summary>
    /// The audio one run is made of: half a second of channel, the burst at the level under
    /// test, half a second of channel again, all of it through the converter model.
    /// </summary>
    public static float[] Audio(
        string mode, byte[] frame, double peakDbFs, double snrDb, int seed, int rate)
    {
        float[] burst = ModemCatalog.Create(mode, rate, static _ => { })
            .Modulate(frame, TxDelayMilliseconds);
        float scale = (float)Math.Pow(10, peakDbFs / 20);
        float loudest = 0;
        foreach (float sample in burst)
        {
            loudest = Math.Max(loudest, Math.Abs(sample));
        }

        for (int n = 0; n < burst.Length; n++)
        {
            burst[n] = burst[n] / loudest * scale;
        }

        // Noise in a 3 kHz reference bandwidth, which is the convention every other AWGN ladder
        // in this tree is quoted in (docs/mode-validation.md), so a knee measured here can be
        // read against the ones already written down. Calibrated against the scaled burst, so
        // the ratio holds as the level sweeps and only the converter's own floor changes
        // underneath it - which is the thing the quiet end is measuring.
        var channel = new WattersonChannel(rate, seed);
        float[] audio = channel.Apply(
            burst, snrDb, noiseBandwidthHz: 3000,
            leadInSamples: rate / 2, leadOutSamples: rate / 2);

        // The card: scale to 16 bits, saturate, back to float. Above full scale this clips, and
        // below the bottom code it quantises to nothing.
        for (int n = 0; n < audio.Length; n++)
        {
            audio[n] = Pcm16.ToFloat(Pcm16.FromFloat(audio[n]));
        }

        return audio;
    }
}
