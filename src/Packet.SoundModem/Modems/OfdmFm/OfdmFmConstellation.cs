namespace Packet.SoundModem.Modems.OfdmFm;

/// <summary>The constellations an OFDM-FM subcarrier can carry, from one bit to eight.</summary>
public enum OfdmFmConstellation
{
    /// <summary>1 bit per carrier.</summary>
    Bpsk = 1,

    /// <summary>2 bits.</summary>
    Qpsk = 2,

    /// <summary>3 bits, as 8-PSK.</summary>
    Psk8 = 3,

    /// <summary>4 bits, 16-QAM.</summary>
    Qam16 = 4,

    /// <summary>5 bits, 32-QAM (rectangular 8x4).</summary>
    Qam32 = 5,

    /// <summary>6 bits, 64-QAM.</summary>
    Qam64 = 6,

    /// <summary>7 bits, 128-QAM (rectangular 16x8).</summary>
    Qam128 = 7,

    /// <summary>8 bits, 256-QAM.</summary>
    Qam256 = 8,
}

/// <summary>Maps bits to constellation points and back.</summary>
/// <remarks>
/// <para>Square and rectangular QAM with Gray coding on each axis, except 8-PSK for the 3-bit
/// case where a ring beats a 4x2 rectangle. Odd bit counts above three use a rectangular
/// arrangement (8x4 for 32, 16x8 for 128) rather than a cross constellation: the peak-to-average
/// penalty is slightly worse but the mapping is far simpler to state, and OFDM's own crest factor
/// dominates either way.</para>
/// <para>Demapping is a nearest-point search over the table. At 256 points that is not the way to
/// write a hot loop, but it is obviously correct, and correctness is what a modem built against a
/// specification that is not final should be spending first.</para>
/// </remarks>
public static class OfdmFmMapper
{
    /// <summary>Bits carried by one subcarrier at this constellation.</summary>
    public static int BitsPerCarrier(this OfdmFmConstellation constellation) => (int)constellation;

    /// <summary>The constellation table, unit mean power, index = the Gray-coded symbol value.</summary>
    public static (float I, float Q)[] Points(OfdmFmConstellation constellation)
    {
        int bits = constellation.BitsPerCarrier();
        int count = 1 << bits;
        var points = new (float I, float Q)[count];

        if (constellation == OfdmFmConstellation.Bpsk)
        {
            points[0] = (-1f, 0f);
            points[1] = (1f, 0f);
            return points;
        }

        if (constellation == OfdmFmConstellation.Psk8)
        {
            for (int s = 0; s < 8; s++)
            {
                int gray = s ^ (s >> 1);
                double angle = 2 * Math.PI * gray / 8;
                points[s] = ((float)Math.Cos(angle), (float)Math.Sin(angle));
            }

            return points;
        }

        // Rectangular: the high half of the bits on I, the low half on Q, Gray per axis.
        int iBits = (bits + 1) / 2;
        int qBits = bits / 2;
        int iLevels = 1 << iBits;
        int qLevels = 1 << qBits;
        double power = 0;
        for (int s = 0; s < count; s++)
        {
            int iIndex = s >> qBits;
            int qIndex = s & (qLevels - 1);
            double i = (2 * GrayToLevel(iIndex, iLevels)) - (iLevels - 1);
            double q = qLevels == 1 ? 0 : (2 * GrayToLevel(qIndex, qLevels)) - (qLevels - 1);
            points[s] = ((float)i, (float)q);
            power += (i * i) + (q * q);
        }

        float scale = (float)(1.0 / Math.Sqrt(power / count));
        for (int s = 0; s < count; s++)
        {
            points[s] = (points[s].I * scale, points[s].Q * scale);
        }

        return points;
    }

    /// <summary>Nearest point to (i,q), as its symbol value.</summary>
    public static int Demap((float I, float Q)[] points, float i, float q)
    {
        int best = 0;
        double bestDistance = double.MaxValue;
        for (int s = 0; s < points.Length; s++)
        {
            double di = points[s].I - i;
            double dq = points[s].Q - q;
            double distance = (di * di) + (dq * dq);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = s;
            }
        }

        return best;
    }

    /// <summary>
    /// Soft metrics for one received point: a log-likelihood ratio per bit, most significant
    /// first, positive meaning zero - the convention the Viterbi decoder expects.
    /// </summary>
    /// <remarks>
    /// Max-log: the squared distance to the nearest point carrying a one, less the distance to the
    /// nearest carrying a zero. Exhaustive over the table, which at 256 points is not a hot loop's
    /// idea of a good time but is exactly right, and being right is what a decoder's input has to
    /// be before anything downstream means anything.
    /// </remarks>
    public static void SoftBits(
        (float I, float Q)[] points, int bits, float i, float q, float scale, Span<float> llrs)
    {
        for (int b = 0; b < bits; b++)
        {
            double nearestZero = double.MaxValue;
            double nearestOne = double.MaxValue;
            int mask = 1 << (bits - 1 - b);
            for (int s = 0; s < points.Length; s++)
            {
                double di = points[s].I - i;
                double dq = points[s].Q - q;
                double distance = (di * di) + (dq * dq);
                if ((s & mask) == 0)
                {
                    nearestZero = Math.Min(nearestZero, distance);
                }
                else
                {
                    nearestOne = Math.Min(nearestOne, distance);
                }
            }

            llrs[b] = (float)((nearestOne - nearestZero) * scale);
        }
    }

    private static int GrayToLevel(int grayIndex, int levels)
    {
        // The index is a Gray code; walk it back to a plain level so neighbouring levels differ
        // in one bit, which is what keeps a one-level slip to a one-bit error.
        int binary = grayIndex;
        for (int shift = 1; shift < levels; shift <<= 1)
        {
            binary ^= binary >> shift;
        }

        return binary;
    }
}
