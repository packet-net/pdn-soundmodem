namespace Packet.SoundModem.Fec.Ldpc;

/// <summary>
/// RA (repeat-accumulate) LDPC encoder — a byte-exact port of codec2's <c>encode()</c>
/// (<c>mpdecode_core.c:68-87</c>). Pure integer XOR over the systematic H_rows entries with a
/// running single-bit accumulator, so it is trivially bit-identical.
/// </summary>
internal static class LdpcEncoder
{
    /// <summary>Computes the <c>NumberParityBits</c> parity bits for the
    /// <c>NumberRowsHcols</c> information bits.</summary>
    /// <param name="code">The LDPC code.</param>
    /// <param name="ibits">Information bits (length = <c>NumberRowsHcols</c>).</param>
    /// <param name="pbits">Parity bits out (length = <c>NumberParityBits</c>).</param>
    public static void Encode(LdpcCode code, ReadOnlySpan<byte> ibits, Span<byte> pbits)
    {
        int m = code.NumberParityBits;
        ushort[] rows = code.HRows;
        int prev = 0;
        for (int p = 0; p < m; p++)
        {
            int par = 0;
            for (int i = 0; i < code.MaxRowWeight; i++)
            {
                int ind = rows[p + i * m];
                if (ind != 0)
                {
                    par += ibits[ind - 1];
                }
            }

            prev = (par + prev) & 1;   // only retain the LSB; running accumulator
            pbits[p] = (byte)prev;
        }
    }
}
