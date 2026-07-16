namespace Packet.SoundModem.Ofdm;

/// <summary>
/// Single-precision complex value — the arithmetic unit of the OFDM modem hot path, mirroring
/// codec2's C <c>complex float</c> (<c>ofdm_internal.h</c>). Every operator computes in
/// <see cref="float"/> with the same product/accumulation order as the C code, so a port that
/// uses it tracks codec2 to the last ULP that <see cref="MathF"/> and libm agree on. See
/// docs/ofdm-design.md §1.3 for why literal IEEE-754 equality across the libm boundary is not
/// attainable (and why the oracle asserts a tolerance, not bit equality).
/// </summary>
/// <param name="Re">Real part.</param>
/// <param name="Im">Imaginary part.</param>
public readonly record struct Cf(float Re, float Im)
{
    /// <summary>Complex zero.</summary>
    public static readonly Cf Zero = new(0f, 0f);

    /// <summary>Sum — component-wise, matching C <c>complex float</c> addition.</summary>
    public static Cf operator +(Cf a, Cf b) => new(a.Re + b.Re, a.Im + b.Im);

    /// <summary>Difference — component-wise.</summary>
    public static Cf operator -(Cf a, Cf b) => new(a.Re - b.Re, a.Im - b.Im);

    /// <summary>Product. Uses the naive <c>(ac−bd) + (ad+bc)i</c> form C evaluates for finite
    /// operands (<c>__mulsc3</c>'s NaN/Inf recovery path is unreachable here), each partial
    /// product rounded to <see cref="float"/> exactly as in the C code.</summary>
    public static Cf operator *(Cf a, Cf b) =>
        new((a.Re * b.Re) - (a.Im * b.Im), (a.Re * b.Im) + (a.Im * b.Re));

    /// <summary>Scale by a real scalar (the <c>tx[i] *= amp_scale</c>/gain steps).</summary>
    public static Cf operator *(Cf a, float s) => new(a.Re * s, a.Im * s);

    /// <summary>Magnitude via <c>sqrt(re²+im²)</c>. codec2 uses <c>cabsf</c> (libm
    /// <c>hypotf</c>); the naive form differs only in the last ULP and only feeds the soft
    /// clipper's threshold test, well inside the tolerance the oracle asserts.</summary>
    public float Magnitude => MathF.Sqrt((Re * Re) + (Im * Im));
}
