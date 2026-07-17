namespace Packet.SoundModem.Ms110d.Fec;

/// <summary>
/// One of the two MIL-STD-188-110D Appendix D rate-1/2 mother codes, selected by WID bit d3
/// (Table D-XVII: 0 → K=7, 1 → K=9). Polynomials are dual-transcribed from Figures D-9/D-10
/// (<c>docs/ms110d/tables/fig-d09-k7-encoder.md</c>, <c>fig-d10-k9-encoder.md</c>); octal form
/// has the newest bit (x^(K−1) coefficient) as MSB. Output bit b0 (T1) is taken first for each
/// input bit (both figures' prose).
/// </summary>
/// <param name="K">Constraint length (7 or 9).</param>
/// <param name="PolyT1">Generator for the first output bit b0 (T1), MSB = x^(K−1).</param>
/// <param name="PolyT2">Generator for the second output bit b1 (T2).</param>
public sealed record ConvolutionalCode(int K, uint PolyT1, uint PolyT2)
{
    /// <summary>K=7: T1 = x⁶+x⁴+x³+x¹+1 = 0o133, T2 = x⁶+x⁵+x⁴+x³+1 = 0o171
    /// (D.5.3.2.1, Figure D-9 — same code as main-body 5.3.2).</summary>
    public static readonly ConvolutionalCode K7 = new(7, 0b1011011, 0b1111001);

    /// <summary>K=9: T1 = x⁸+x⁶+x⁵+x⁴+1 = 0o561, T2 = x⁸+x⁷+x⁶+x⁵+x³+x¹+1 = 0o753
    /// (D.5.3.2.2, Figure D-10; matches the published max-free-distance (561,753) code).</summary>
    public static readonly ConvolutionalCode K9 = new(9, 0b101110001, 0b111101011);

    /// <summary>Trellis state count, 2^(K−1).</summary>
    public int States => 1 << (K - 1);
}
