using System.Runtime.CompilerServices;

namespace Packet.SoundModem.Ofdm;

/// <summary>
/// A single-precision complex number — the C# equivalent of codec2's <c>complex float</c>.
/// The FreeDV OFDM demodulator is validated against libcodec2, which is single-precision
/// throughout (<c>cosf</c>/<c>sinf</c>/<c>cabsf</c>/<c>cargf</c>); using <see cref="System.Numerics.Complex"/>
/// (which promotes to <c>double</c>) would drift from the reference after a few frames, so we
/// carry <see cref="float"/> real/imaginary parts and use <see cref="MathF"/> exclusively.
/// Provenance: codec2 1.2.0 (git 310777b) <c>ofdm_internal.h</c> <c>cmplx</c>/<c>cmplxconj</c>
/// macros, LGPL-2.1 — see PROVENANCE.md.
/// </summary>
public readonly struct Cf : IEquatable<Cf>
{
    /// <summary>Real part.</summary>
    public readonly float Re;

    /// <summary>Imaginary part.</summary>
    public readonly float Im;

    /// <summary>Constructs a complex number from its real and imaginary parts.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Cf(float re, float im)
    {
        Re = re;
        Im = im;
    }

    /// <summary>The complex zero.</summary>
    public static Cf Zero => new(0.0f, 0.0f);

    /// <summary><c>cmplx(x) = cos(x) + j·sin(x)</c> (codec2 <c>ofdm_internal.h:51</c>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Cf Cmplx(float angle) => new(MathF.Cos(angle), MathF.Sin(angle));

    /// <summary><c>cmplxconj(x) = cos(x) − j·sin(x)</c> (codec2 <c>ofdm_internal.h:52</c>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Cf CmplxConj(float angle) => new(MathF.Cos(angle), -MathF.Sin(angle));

    /// <summary>Complex addition.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Cf operator +(Cf a, Cf b) => new(a.Re + b.Re, a.Im + b.Im);

    /// <summary>Complex subtraction.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Cf operator -(Cf a, Cf b) => new(a.Re - b.Re, a.Im - b.Im);

    /// <summary>Complex multiplication: <c>(a+bi)(c+di) = (ac−bd) + i(bc+ad)</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Cf operator *(Cf a, Cf b) =>
        new((a.Re * b.Re) - (a.Im * b.Im), (a.Im * b.Re) + (a.Re * b.Im));

    /// <summary>Scalar multiplication.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Cf operator *(Cf a, float s) => new(a.Re * s, a.Im * s);

    /// <summary>Complex conjugate (<c>conjf</c>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Cf Conj() => new(Re, -Im);

    /// <summary>Magnitude (<c>cabsf</c>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Abs() => MathF.Sqrt((Re * Re) + (Im * Im));

    /// <summary>Squared magnitude (<c>cnormf</c>, codec2 <c>ofdm.c:96-101</c>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Cnorm() => (Re * Re) + (Im * Im);

    /// <summary>Argument in radians (<c>cargf</c> = <c>atan2f(im, re)</c>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Arg() => MathF.Atan2(Im, Re);

    /// <inheritdoc/>
    public bool Equals(Cf other) => Re.Equals(other.Re) && Im.Equals(other.Im);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Cf other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Re, Im);

    /// <summary>Value equality.</summary>
    public static bool operator ==(Cf a, Cf b) => a.Equals(b);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(Cf a, Cf b) => !a.Equals(b);

    /// <inheritdoc/>
    public override string ToString() => $"({Re:g6} {(Im < 0 ? "-" : "+")} {MathF.Abs(Im):g6}i)";
}
