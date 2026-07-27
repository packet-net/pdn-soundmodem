using M0LTE.Ofdm;
using Packet.SoundModem.Tests.Channel;

namespace Packet.SoundModem.Ota;

/// <summary>
/// The packet-layer probe for the FreeDV datac family: one raw datac packet per burst through the
/// <em>same</em> <see cref="DatacTransmitter"/>/<see cref="DatacReceiver"/> engine
/// <see cref="Packet.SoundModem.Modems.FreeDvDatacModem"/> wraps, scored on the packet's own CRC and
/// LDPC output rather than on the IL2P frame layered above it.
/// </summary>
/// <remarks>
/// <para><b>Why a second layer.</b> FreeDV publishes its operating points per <em>packet</em>
/// ("N/100 packets received on MPP at the operating-point SNR", codec2 <c>README_data.md</c>), and
/// its clean AWGN thresholds per packet at 10&#160;% PER. The pdn frame layer bolts IL2P+CRC on top
/// and lets one AX.25 frame span several datac packets — so a narrow-mode frame only decodes when
/// <em>every</em> packet does, a stricter bar than FreeDV's per-packet figure. To compare our engine
/// against FreeDV's numbers on the same terms, this probe measures exactly what they measure: the
/// per-packet CRC success rate, and the post-LDPC coded bit-error rate against the transmitted
/// payload. It is not a re-implementation of the demodulator — it is the library's own
/// <see cref="DatacReceiver"/>, driven <c>Nin</c>-by-<c>Nin</c> the way <c>FreeDvDatacModem</c> drives
/// it — so it grades the same code the frame layer does, one level down.</para>
/// <para>One packet per burst (preamble + one packet + postamble) makes each trial an independent
/// draw of payload and — via the seed — channel realisation, so a point is reproducible and the
/// per-packet count is unambiguous (0 or 1 result per burst).</para>
/// </remarks>
internal sealed class DatacPacketProbe
{
    private readonly OfdmMode _mode;

    public DatacPacketProbe(string mode)
    {
        _mode = mode switch
        {
            "freedv-datac0" => OfdmMode.Datac0,
            "freedv-datac1" => OfdmMode.Datac1,
            "freedv-datac3" => OfdmMode.Datac3,
            "freedv-datac4" => OfdmMode.Datac4,
            "freedv-datac13" => OfdmMode.Datac13,
            "freedv-datac14" => OfdmMode.Datac14,
            _ => throw new ArgumentException($"'{mode}' is not a FreeDV datac mode", nameof(mode)),
        };
    }

    /// <summary>Engine-native sample rate — 8000&#160;Hz, the rate FreeDV's own figures are measured
    /// at. The probe runs natively (no ×6/÷6 resampling) so nothing but the engine and the channel
    /// stands between the payload and the score.</summary>
    public int Rate => (int)_mode.Fs;

    /// <summary>Payload bytes carried by one packet of this mode.</summary>
    public int PayloadBytes => new DatacTransmitter(_mode).PayloadBytes;

    /// <summary>Runs one packet through the channel and scores it.</summary>
    /// <param name="seed">Draws both the payload and (offset) the channel realisation.</param>
    /// <param name="kind">Channel geometry.</param>
    /// <param name="snrDb">SNR in a 3&#160;kHz noise bandwidth.</param>
    public PacketResult Run(int seed, SimChannelKind kind, double snrDb)
    {
        var tx = new DatacTransmitter(_mode);
        byte[] payload = new byte[tx.PayloadBytes];
        new Random(seed).NextBytes(payload);

        Cf[] burst = tx.ModulateBurst([payload]);
        float[] active = new float[burst.Length];
        for (int i = 0; i < burst.Length; i++)
        {
            active[i] = burst[i].Re * (1f / 32768f);
        }

        // Same channel rig as every other waterfall, calibrated against the active burst power.
        float[] rx = SimChannel.Apply(active, Rate, kind, snrDb, seed + 5_000_000);

        // Float → short on codec2's ±32767 scale, then hand the demodulator exactly Nin at a time —
        // the FreeDvDatacModem.FeedNative contract, so the probe drives the engine identically.
        short[] samples = new short[rx.Length];
        for (int i = 0; i < rx.Length; i++)
        {
            float scaled = rx[i] * 32767f;
            samples[i] = scaled >= 32767f ? (short)32767
                : scaled <= -32768f ? (short)-32768
                : (short)scaled;
        }

        var receiver = new DatacReceiver(_mode, packetsPerBurst: 1, endOfBurstDetection: true);
        DatacRxResult? decoded = null;
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
                decoded ??= result;
            }

            pos += nin;
        }

        int payloadBits = payload.Length * 8;
        if (decoded is not { } r)
        {
            // No packet recovered at all: CRC fail and every payload bit counts as an error.
            return new PacketResult(false, payloadBits, payloadBits, 0, 0);
        }

        int bitErrors = BitErrors(r.Bytes, payload);
        return new PacketResult(r.CrcOk, bitErrors, payloadBits, r.Iterations, r.ParityChecks);
    }

    private static int BitErrors(byte[] got, byte[] sent)
    {
        int errors = 0;
        int n = Math.Min(got.Length, sent.Length);
        for (int i = 0; i < n; i++)
        {
            errors += System.Numerics.BitOperations.PopCount((uint)(byte)(got[i] ^ sent[i]));
        }

        // Bytes that never arrived (a short/absent LDPC output) are wholly wrong.
        errors += (sent.Length - n) * 8;
        return errors;
    }
}

/// <summary>What one datac packet did on the channel.</summary>
/// <param name="CrcOk">The packet's own CRC passed — FreeDV's "packet received" criterion.</param>
/// <param name="BitErrors">Post-LDPC payload bit errors against what was sent.</param>
/// <param name="PayloadBits">Payload bits compared (a lost packet = all bits wrong).</param>
/// <param name="LdpcIterations">LDPC iterations the decoder ran (a soft margin indicator).</param>
/// <param name="ParityChecks">LDPC parity checks satisfied at exit (a soft margin indicator).</param>
internal readonly record struct PacketResult(
    bool CrcOk, int BitErrors, int PayloadBits, int LdpcIterations, int ParityChecks);
