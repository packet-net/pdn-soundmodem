namespace Packet.SoundModem.Fec.Ldpc;

/// <summary>
/// A FreeDV/codec2 LDPC parity-check code: the immutable sparse H-matrix (column-major,
/// 1-based, 0 = padding) plus its dimensions. The datac modes all use one of five of these,
/// every one an RA / dual-diagonal code (<c>NumberRowsHcols == NumberParityBits ==
/// CodeLength/2</c>, so the decoder's <c>H1=1, shift=0</c> branch). Matrix data is
/// transliterated verbatim from codec2 1.2.0 (git 310777b, LGPL-2.1) by
/// <c>tools/gen-ldpc-tables/gen.py</c> into <c>LdpcTables.g.cs</c> — see
/// <see href="../../../docs/ofdm-design.md">docs/ofdm-design.md §5</see> and PROVENANCE.md.
/// </summary>
/// <param name="Name">codec2 codename, e.g. <c>H_128_256_5</c>.</param>
/// <param name="CodeLength">N — codeword bits (data + parity).</param>
/// <param name="NumberParityBits">M — parity bits.</param>
/// <param name="NumberRowsHcols">K — data bits (== M for these RA codes).</param>
/// <param name="MaxRowWeight">Columns per row in the <see cref="HRows"/> layout.</param>
/// <param name="MaxColWeight">Rows per column in the <see cref="HCols"/> layout.</param>
/// <param name="MaxIter">Sum-product iteration ceiling (100 for all datac codes).</param>
/// <param name="HRows"><c>M * MaxRowWeight</c> entries: <c>HRows[p + i*M]</c> = 1-based
/// data-column index of the i-th systematic nonzero in parity row p (0 = none).</param>
/// <param name="HCols"><c>K * MaxColWeight</c> entries: <c>HCols[c + j*K]</c> = 1-based
/// parity-row index that data-column c participates in (0 = none).</param>
public sealed record LdpcCode(
    string Name,
    int CodeLength,
    int NumberParityBits,
    int NumberRowsHcols,
    int MaxRowWeight,
    int MaxColWeight,
    int MaxIter,
    ushort[] HRows,
    ushort[] HCols);
