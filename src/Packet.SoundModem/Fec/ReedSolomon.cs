namespace Packet.SoundModem.Fec;

/// <summary>
/// Reed-Solomon codec over GF(2^8) with field generator polynomial
/// x^8 + x^4 + x^3 + x^2 + 1 (0x11D), primitive element α = 2, as specified by
/// IL2P (spec draft v0.6 § Reed Solomon Forward Error Correction: "The RS encoder
/// uses zero as its first root", i.e. fcr = 0) and by FX.25 (fcr = 1). Codes are
/// systematic and shortened: a codeword is the data bytes followed by
/// <see cref="ParitySymbols"/> parity bytes, total at most 255.
/// </summary>
/// <remarks>
/// Encoding is validated byte-exact against the IL2P spec's example packets
/// (2-parity header blocks and 16-parity payload blocks). Decoding corrects up to
/// <c>ParitySymbols / 2</c> erroneous bytes per codeword via Berlekamp-Massey,
/// Chien search and Forney's algorithm.
/// </remarks>
public sealed class ReedSolomon
{
    private const int FieldPoly = 0x11D;

    private static readonly byte[] Exp = new byte[510];
    private static readonly byte[] Log = new byte[256];

    private readonly byte[] _generator; // ascending coefficients g[0..ParitySymbols-1]; leading coefficient (1) implicit

    static ReedSolomon()
    {
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            Exp[i] = (byte)x;
            Log[x] = (byte)i;
            x <<= 1;
            if ((x & 0x100) != 0)
            {
                x ^= FieldPoly;
            }
        }

        for (int i = 255; i < 510; i++)
        {
            Exp[i] = Exp[i - 255];
        }
    }

    /// <summary>Creates a codec appending <paramref name="paritySymbols"/> parity bytes,
    /// with generator roots α^<paramref name="firstConsecutiveRoot"/> … α^(fcr+parity-1).</summary>
    public ReedSolomon(int paritySymbols, int firstConsecutiveRoot = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(paritySymbols, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(paritySymbols, 254);
        ParitySymbols = paritySymbols;
        FirstConsecutiveRoot = firstConsecutiveRoot;

        // g(x) = Π (x + α^(fcr+i)), built up one root at a time.
        var g = new byte[] { 1 };
        for (int i = 0; i < paritySymbols; i++)
        {
            byte root = Exp[(firstConsecutiveRoot + i) % 255];
            var next = new byte[g.Length + 1];
            for (int j = 0; j < g.Length; j++)
            {
                next[j + 1] ^= g[j];
                next[j] ^= Multiply(g[j], root);
            }

            g = next;
        }

        _generator = g[..paritySymbols];
    }

    /// <summary>Number of parity bytes appended per codeword.</summary>
    public int ParitySymbols { get; }

    /// <summary>Exponent of the first generator root (0 for IL2P, 1 for FX.25).</summary>
    public int FirstConsecutiveRoot { get; }

    /// <summary>Maximum number of erroneous bytes this code can correct per codeword.</summary>
    public int MaxCorrectableSymbols => ParitySymbols / 2;

    /// <summary>
    /// Computes parity for <paramref name="data"/> into <paramref name="parity"/>
    /// (length exactly <see cref="ParitySymbols"/>). The transmitted codeword is
    /// <paramref name="data"/> followed by <paramref name="parity"/>.
    /// </summary>
    public void Encode(ReadOnlySpan<byte> data, Span<byte> parity)
    {
        if (parity.Length != ParitySymbols)
        {
            throw new ArgumentException($"parity must be exactly {ParitySymbols} bytes", nameof(parity));
        }

        if (data.Length + ParitySymbols > 255)
        {
            throw new ArgumentException("data + parity exceeds the 255-byte RS block limit", nameof(data));
        }

        int nroots = ParitySymbols;
        Span<byte> rem = stackalloc byte[nroots];
        rem.Clear();

        foreach (byte b in data)
        {
            int feedback = b ^ rem[nroots - 1];
            for (int i = nroots - 1; i > 0; i--)
            {
                rem[i] = rem[i - 1];
            }

            rem[0] = 0;
            if (feedback != 0)
            {
                int lf = Log[feedback];
                for (int j = 0; j < nroots; j++)
                {
                    if (_generator[j] != 0)
                    {
                        rem[j] ^= Exp[lf + Log[_generator[j]]];
                    }
                }
            }
        }

        // Highest-degree remainder coefficient is transmitted first.
        for (int j = 0; j < nroots; j++)
        {
            parity[j] = rem[nroots - 1 - j];
        }
    }

    /// <summary>
    /// Decodes a codeword (data followed by parity) in place, correcting up to
    /// <see cref="MaxCorrectableSymbols"/> erroneous bytes.
    /// </summary>
    /// <returns>The number of bytes corrected (0 if the codeword was clean), or -1 if
    /// the codeword is uncorrectable.</returns>
    public int Decode(Span<byte> codeword)
    {
        int n = codeword.Length;
        if (n <= ParitySymbols || n > 255)
        {
            return -1;
        }

        int nroots = ParitySymbols;

        // Syndromes: S_i = codeword evaluated at α^(fcr+i), coefficient 0 = highest degree.
        Span<byte> synd = stackalloc byte[nroots];
        bool clean = true;
        for (int i = 0; i < nroots; i++)
        {
            byte point = Exp[(FirstConsecutiveRoot + i) % 255];
            byte s = 0;
            foreach (byte b in codeword)
            {
                s = (byte)(Multiply(s, point) ^ b);
            }

            synd[i] = s;
            clean &= s == 0;
        }

        if (clean)
        {
            return 0;
        }

        // Berlekamp-Massey: find the error locator polynomial Λ (ascending coefficients).
        var lambda = new byte[nroots + 1];
        var prev = new byte[nroots + 1];
        lambda[0] = 1;
        prev[0] = 1;
        int errors = 0;
        int m = 1;
        byte b0 = 1;
        for (int step = 0; step < nroots; step++)
        {
            byte delta = synd[step];
            for (int i = 1; i <= errors; i++)
            {
                delta ^= Multiply(lambda[i], synd[step - i]);
            }

            if (delta == 0)
            {
                m++;
                continue;
            }

            if (2 * errors <= step)
            {
                var saved = (byte[])lambda.Clone();
                byte coef = Divide(delta, b0);
                for (int i = 0; i + m <= nroots; i++)
                {
                    lambda[i + m] ^= Multiply(coef, prev[i]);
                }

                errors = step + 1 - errors;
                prev = saved;
                b0 = delta;
                m = 1;
            }
            else
            {
                byte coef = Divide(delta, b0);
                for (int i = 0; i + m <= nroots; i++)
                {
                    lambda[i + m] ^= Multiply(coef, prev[i]);
                }

                m++;
            }
        }

        if (errors > MaxCorrectableSymbols)
        {
            return -1;
        }

        // Chien search: error at degree p ⇔ Λ(α^(-p)) = 0. Degree p maps to index n-1-p.
        Span<int> errorIndices = stackalloc int[nroots];
        Span<byte> errorPowers = stackalloc byte[nroots];
        int found = 0;
        for (int p = 0; p < 255; p++)
        {
            byte x = Exp[(255 - p) % 255]; // α^(-p)
            byte acc = 0;
            for (int i = nroots; i >= 0; i--)
            {
                acc = (byte)(Multiply(acc, x) ^ lambda[i]);
            }

            if (acc != 0)
            {
                continue;
            }

            int index = n - 1 - p;
            if (index < 0)
            {
                return -1; // error located outside the shortened codeword
            }

            if (found == nroots)
            {
                return -1;
            }

            errorIndices[found] = index;
            errorPowers[found] = (byte)p;
            found++;
        }

        if (found == 0 || found != errors)
        {
            return -1;
        }

        // Forney: Ω(x) = S(x)·Λ(x) mod x^nroots; Y_k = X_k^(1-fcr) · Ω(X_k⁻¹) / Λ'(X_k⁻¹).
        Span<byte> omega = stackalloc byte[nroots];
        omega.Clear();
        for (int i = 0; i < nroots; i++)
        {
            if (synd[i] == 0)
            {
                continue;
            }

            for (int j = 0; j <= errors && i + j < nroots; j++)
            {
                omega[i + j] ^= Multiply(synd[i], lambda[j]);
            }
        }

        for (int k = 0; k < found; k++)
        {
            int p = errorPowers[k];
            byte xInv = Exp[(255 - p) % 255];

            byte num = 0;
            for (int i = nroots - 1; i >= 0; i--)
            {
                num = (byte)(Multiply(num, xInv) ^ omega[i]);
            }

            // Λ'(x): formal derivative keeps odd-degree terms only (characteristic 2).
            byte den = 0;
            for (int i = 1; i <= errors; i += 2)
            {
                den ^= Multiply(lambda[i], PowerOf(xInv, i - 1));
            }

            if (den == 0)
            {
                return -1;
            }

            byte magnitude = Divide(num, den);
            int exp1MinusFcr = ((1 - FirstConsecutiveRoot) % 255 + 255) % 255;
            magnitude = Multiply(magnitude, PowerOf(Exp[p % 255], exp1MinusFcr));
            codeword[errorIndices[k]] ^= magnitude;
        }

        // Safety: verify the corrected codeword has all-zero syndromes.
        for (int i = 0; i < nroots; i++)
        {
            byte point = Exp[(FirstConsecutiveRoot + i) % 255];
            byte s = 0;
            foreach (byte b in codeword)
            {
                s = (byte)(Multiply(s, point) ^ b);
            }

            if (s != 0)
            {
                return -1;
            }
        }

        return found;
    }

    private static byte Multiply(byte a, byte b) => a == 0 || b == 0 ? (byte)0 : Exp[Log[a] + Log[b]];

    private static byte Divide(byte a, byte b) => a == 0 ? (byte)0 : Exp[(Log[a] - Log[b] + 255) % 255];

    private static byte PowerOf(byte a, int power)
    {
        if (power == 0)
        {
            return 1;
        }

        return a == 0 ? (byte)0 : Exp[Log[a] * power % 255];
    }
}
