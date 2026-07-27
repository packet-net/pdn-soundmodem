using M0LTE.Fec;
using Packet.SoundModem.Ms110d;
using Packet.SoundModem.Ms110d.Fec;

namespace Packet.SoundModem.Ota;

/// <summary>
/// Regenerates exactly what went on the air for one scheduled burst: the payload from its seed,
/// and the channel bits of every block in wire order.
/// </summary>
/// <remarks>
/// <para>This is what makes an over-the-air pass measure something. Comparing decoded payload
/// against transmitted payload gives coded BER, which on a good channel is 0 and on a bad one is
/// 1 — it saturates at both ends and says almost nothing in between. The informative number is
/// the <em>uncoded</em> channel-bit error rate: sign(first-pass LLR) against the bit that was
/// actually transmitted at that wire position. It degrades smoothly, it is comparable directly
/// against the Watterson rig's own uncoded telemetry, and it separates "the channel was bad"
/// from "the equaliser lost the plot".</para>
/// <para>The encode path here is the production one — <see cref="Ms110dFraming"/>,
/// <see cref="Ms110dPuncture"/>, <see cref="Ms110dInterleaver"/> — reached through
/// <c>InternalsVisibleTo</c>, and assembled the same way <c>Ms110dMaskTests</c> assembles it.
/// Reimplementing the wire order for the scorer would produce a scorer that grades the
/// demodulator against its own reimplementation, which is the de-rigging failure this project
/// has already paid for once.</para>
/// <para>The payload is drawn from <see cref="Random"/> with the schedule's seed, one
/// <c>Next(2)</c> per bit, which is the same construction the transmit side uses — so a manifest
/// needs to record the seed and the length, not the payload.</para>
/// </remarks>
public sealed class Ms110dReferenceBits
{
    private readonly byte[][] _blocks;

    /// <summary>Builds the reference for a burst of <paramref name="payloadBits"/> bits drawn
    /// from <paramref name="seed"/>.</summary>
    public Ms110dReferenceBits(Ms110dTxSettings settings, int payloadBits, int seed)
        : this(settings, Payload(payloadBits, seed))
    {
        Seed = seed;
    }

    /// <summary>Builds the reference for a burst carrying a payload that is already known.</summary>
    public Ms110dReferenceBits(Ms110dTxSettings settings, byte[] payloadBits)
    {
        Settings = settings;
        PayloadBits = payloadBits;

        var modulator = new Ms110dModulator(settings);
        Ms110dInterleaverParams il = Ms110dInterleaverParams.Get3k(
            settings.WaveformNumber, settings.Interleaver);

        ConvolutionalCode code = settings.ConstraintLength == 9
            ? ConvolutionalCode.K9
            : ConvolutionalCode.K7;
        PunctureSpec puncture = Ms110dPuncture.Get(code, modulator.Mode.CodeRate);
        var interleaver = new Ms110dInterleaver(il.SizeBits, il.Increment);

        byte[] txBits = Ms110dFraming.BuildTxBits(payloadBits, appendEom: true, il.InputBits);
        _blocks = new byte[txBits.Length / il.InputBits][];
        for (int b = 0; b < _blocks.Length; b++)
        {
            _blocks[b] = Ms110dFraming.EncodeBlock(
                code, puncture, interleaver, txBits.AsSpan(b * il.InputBits, il.InputBits));
        }
    }

    /// <summary>Payload bit count for a burst of <paramref name="blocks"/> whole interleaver
    /// blocks — the largest payload that fits, less the 32-bit EOM.</summary>
    public static int PayloadBitsForBlocks(int waveformNumber, Ms110dInterleaverKind interleaver, int blocks)
        => (blocks * Ms110dInterleaverParams.Get3k(waveformNumber, interleaver).InputBits) - 32;

    /// <summary>The payload bits, in transmit order.</summary>
    public byte[] PayloadBits { get; }

    /// <summary>The transmit settings this reference was built for.</summary>
    public Ms110dTxSettings Settings { get; }

    /// <summary>The seed the payload was drawn from, when it was drawn from one.</summary>
    public int? Seed { get; }

    /// <summary>Blocks the burst occupies, EOM included.</summary>
    public int BlockCount => _blocks.Length;

    /// <summary>Channel bits of one block, in wire order — directly comparable, position for
    /// position, against <c>Ms110dDemodulator.FirstPassBlockLlrs</c>.</summary>
    public ReadOnlySpan<byte> Block(int index) => _blocks[index];

    /// <summary>Draws a payload the way the transmit side draws it.</summary>
    public static byte[] Payload(int bits, int seed)
    {
        var random = new Random(seed);
        var payload = new byte[bits];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)random.Next(2);
        }

        return payload;
    }
}
