namespace Packet.SoundModem.Fec.Ldpc;

/// <summary>
/// The five LDPC codes the FreeDV datac modes use, bound from the transliterated
/// <see cref="LdpcTables"/> data into <see cref="LdpcCode"/> instances. The mode→code map:
/// datac0 → <see cref="H_128_256_5"/>; datac1 → <see cref="H_4096_8192_3d"/>; datac3/datac4 →
/// <see cref="H_1024_2048_4f"/> (datac4 shortened); datac13 → <see cref="H_256_512_4"/>
/// (shortened); datac14 → <see cref="HRA_56_56"/> (shortened). See docs/ofdm-design.md §5.
/// </summary>
public static class LdpcCodes
{
    /// <summary>datac0 (256,128).</summary>
    public static readonly LdpcCode H_128_256_5 = new(
        nameof(H_128_256_5), LdpcTables.H_128_256_5.CodeLength,
        LdpcTables.H_128_256_5.NumberParityBits, LdpcTables.H_128_256_5.NumberRowsHcols,
        LdpcTables.H_128_256_5.MaxRowWeight, LdpcTables.H_128_256_5.MaxColWeight,
        LdpcTables.H_128_256_5.MaxIter, LdpcTables.H_128_256_5.HRows, LdpcTables.H_128_256_5.HCols);

    /// <summary>datac13 mother code (512,256) — shortened to 128 data bits.</summary>
    public static readonly LdpcCode H_256_512_4 = new(
        nameof(H_256_512_4), LdpcTables.H_256_512_4.CodeLength,
        LdpcTables.H_256_512_4.NumberParityBits, LdpcTables.H_256_512_4.NumberRowsHcols,
        LdpcTables.H_256_512_4.MaxRowWeight, LdpcTables.H_256_512_4.MaxColWeight,
        LdpcTables.H_256_512_4.MaxIter, LdpcTables.H_256_512_4.HRows, LdpcTables.H_256_512_4.HCols);

    /// <summary>datac3/datac4 mother code (2048,1024) — datac4 shortened to 448 data bits.</summary>
    public static readonly LdpcCode H_1024_2048_4f = new(
        nameof(H_1024_2048_4f), LdpcTables.H_1024_2048_4f.CodeLength,
        LdpcTables.H_1024_2048_4f.NumberParityBits, LdpcTables.H_1024_2048_4f.NumberRowsHcols,
        LdpcTables.H_1024_2048_4f.MaxRowWeight, LdpcTables.H_1024_2048_4f.MaxColWeight,
        LdpcTables.H_1024_2048_4f.MaxIter, LdpcTables.H_1024_2048_4f.HRows, LdpcTables.H_1024_2048_4f.HCols);

    /// <summary>datac1 (8192,4096) — the workhorse.</summary>
    public static readonly LdpcCode H_4096_8192_3d = new(
        nameof(H_4096_8192_3d), LdpcTables.H_4096_8192_3d.CodeLength,
        LdpcTables.H_4096_8192_3d.NumberParityBits, LdpcTables.H_4096_8192_3d.NumberRowsHcols,
        LdpcTables.H_4096_8192_3d.MaxRowWeight, LdpcTables.H_4096_8192_3d.MaxColWeight,
        LdpcTables.H_4096_8192_3d.MaxIter, LdpcTables.H_4096_8192_3d.HRows, LdpcTables.H_4096_8192_3d.HCols);

    /// <summary>datac14 mother code (112,56) — shortened to 40 data bits.</summary>
    public static readonly LdpcCode HRA_56_56 = new(
        nameof(HRA_56_56), LdpcTables.HRA_56_56.CodeLength,
        LdpcTables.HRA_56_56.NumberParityBits, LdpcTables.HRA_56_56.NumberRowsHcols,
        LdpcTables.HRA_56_56.MaxRowWeight, LdpcTables.HRA_56_56.MaxColWeight,
        LdpcTables.HRA_56_56.MaxIter, LdpcTables.HRA_56_56.HRows, LdpcTables.HRA_56_56.HCols);
}
