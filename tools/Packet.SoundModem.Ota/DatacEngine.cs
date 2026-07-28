using M0LTE.Ofdm;

namespace Packet.SoundModem.Ota;

/// <summary>
/// The FreeDV datac engine primitives the harness drives the same way in more than one place — the
/// mode map, a seeded packet, its clean float burst, the codec2 short front end, the one-packet
/// decode loop, and the bit-error count. Factored here so the <see cref="DatacPacketProbe"/> (the
/// sim baseline's packet probe), the <see cref="OfdmLadderPass"/> renderer, and the
/// <see cref="OfdmBurstScorer"/> grade the engine through one definition rather than three copies
/// that must be kept in lockstep as the engine changes.
/// </summary>
internal static class DatacEngine
{
    /// <summary>The six datac modes and their <see cref="OfdmMode"/> — the single mode→engine map.</summary>
    /// <exception cref="ArgumentException"><paramref name="mode"/> is not a datac mode.</exception>
    public static OfdmMode Mode(string mode) => mode switch
    {
        "freedv-datac0" => OfdmMode.Datac0,
        "freedv-datac1" => OfdmMode.Datac1,
        "freedv-datac3" => OfdmMode.Datac3,
        "freedv-datac4" => OfdmMode.Datac4,
        "freedv-datac13" => OfdmMode.Datac13,
        "freedv-datac14" => OfdmMode.Datac14,
        _ => throw new ArgumentException($"'{mode}' is not a FreeDV datac mode", nameof(mode)),
    };

    /// <summary>Whether <paramref name="mode"/> is one of the six datac modes — the authoritative
    /// predicate the ladder dispatch shares with <see cref="Mode"/>, so routing and mapping can never
    /// disagree (a bare "freedv" prefix would route names the map then rejects).</summary>
    public static bool IsDatacMode(string? mode) => mode switch
    {
        "freedv-datac0" or "freedv-datac1" or "freedv-datac3" or "freedv-datac4"
            or "freedv-datac13" or "freedv-datac14" => true,
        _ => false,
    };

    /// <summary>A seeded payload of one packet's worth of bytes for <paramref name="mode"/> — the
    /// reproduction key a manifest row carries.</summary>
    public static byte[] Payload(OfdmMode mode, int seed)
    {
        byte[] payload = new byte[new DatacTransmitter(mode).PayloadBytes];
        new Random(seed).NextBytes(payload);
        return payload;
    }

    /// <summary>Modulates one datac burst (preamble + one packet + postamble) to the engine's native
    /// 8&#160;kHz real audio on the channel's ±1.0 float convention (codec2's ±16384 scale, peak
    /// ≈&#160;0.5). No leading or trailing silence — the caller's channel calibrates its noise against
    /// this active-burst power.</summary>
    public static float[] CleanBurst(OfdmMode mode, byte[] payload)
    {
        Cf[] burst = new DatacTransmitter(mode).ModulateBurst([payload]);
        var clean = new float[burst.Length];
        for (int i = 0; i < burst.Length; i++)
        {
            clean[i] = burst[i].Re * (1f / 32768f);
        }

        return clean;
    }

    /// <summary>Quantises real audio to codec2's ±32767 signed-16-bit scale (the format
    /// <see cref="DatacReceiver.Process(ReadOnlySpan{short})"/> consumes), applying an optional
    /// absolute level scale — the <c>FreeDvDatacModem.FeedNative</c> front end, shared so every
    /// caller drives the engine identically.</summary>
    public static short[] ToShort(ReadOnlySpan<float> audio, float gain = 1f)
    {
        var samples = new short[audio.Length];
        for (int i = 0; i < audio.Length; i++)
        {
            float scaled = audio[i] * gain * 32767f;
            samples[i] = scaled >= 32767f ? (short)32767
                : scaled <= -32768f ? (short)-32768
                : (short)scaled;
        }

        return samples;
    }

    /// <summary>Drives a fresh burst-mode receiver over <paramref name="samples"/>, <c>Nin</c> at a
    /// time (the <c>FreeDvDatacModem</c>/<c>freedv_data_raw_rx</c> drive loop), and returns the first
    /// packet it recovers with the frequency-offset estimate at that moment, or null if none.</summary>
    public static DatacRxResult? DecodeFirstPacket(OfdmMode mode, short[] samples, out double cfoHz)
    {
        var receiver = new DatacReceiver(mode, packetsPerBurst: 1, endOfBurstDetection: true);
        DatacRxResult? decoded = null;
        cfoHz = 0;
        int pos = 0;
        while (true)
        {
            int nin = receiver.Demod.Nin;
            if (samples.Length - pos < nin)
            {
                break;
            }

            foreach (DatacRxResult result in receiver.Process(samples.AsSpan(pos, nin)))
            {
                if (decoded is null)
                {
                    decoded = result;
                    cfoHz = receiver.Demod.FoffEstHz; // the offset at the moment the packet landed
                }
            }

            pos += nin;
        }

        return decoded;
    }

    /// <summary>Post-LDPC payload bit errors: <paramref name="got"/> against <paramref name="sent"/>,
    /// with bytes that never arrived counted wholly wrong.</summary>
    public static int BitErrors(ReadOnlySpan<byte> got, ReadOnlySpan<byte> sent)
    {
        int errors = 0;
        int n = Math.Min(got.Length, sent.Length);
        for (int i = 0; i < n; i++)
        {
            errors += System.Numerics.BitOperations.PopCount((uint)(byte)(got[i] ^ sent[i]));
        }

        errors += (sent.Length - n) * 8;
        return errors;
    }
}
