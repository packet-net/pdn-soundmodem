using M0LTE.Ofdm;

namespace Packet.SoundModem.Ms110d;

/// <summary>
/// Waveform-ID 0 Walsh orthogonal data modulation (75 bps): after the preamble, WN 0 sends
/// no mini-probes — each channel symbol is a 32-chip Walsh sequence carrying one coded and
/// interleaved di-bit (D.5.2 final paragraph, <c>docs/ms110d/tables/walsh-data-sequence-prose.md</c>),
/// chip-wise modulo-8 combined with the Trinomial (159, 31) scramble sequence (D.5.1.4).
/// </summary>
/// <remarks>
/// Di-bit order within a channel symbol is not stated by the spec for the data path; the QPSK
/// rule (leftmost = older = fetched first, D.5.1.2.1.2) is adopted — checklist L6, recorded
/// open interpretation. The Table D-XIV map itself is dual-transcribed (10→0044, 11→0440).
/// </remarks>
public sealed class Wid0WalshModem
{
    /// <summary>Chips per WN 0 data channel symbol at 3 kHz.</summary>
    public const int ChipsPerSymbol = 32;

    private readonly Trinomial159Scrambler _scrambler = new();

    /// <summary>Resets the scramble sequence — call at every interleaver boundary.</summary>
    public void Reset()
    {
        _scrambler.Reset();
    }

    /// <summary>Modulates fetched (interleaved) bits — two per channel symbol, first bit =
    /// MSB of the di-bit — into scrambled 8PSK chips
    /// (<paramref name="psk8Chips"/>.Length = fetchedBits.Length × 16).</summary>
    public void Modulate(ReadOnlySpan<byte> fetchedBits, Span<byte> psk8Chips)
    {
        if (fetchedBits.Length % 2 != 0 || psk8Chips.Length != fetchedBits.Length * 16)
        {
            throw new ArgumentException("need whole di-bits and 32 chips per di-bit", nameof(psk8Chips));
        }

        int o = 0;
        for (int n = 0; n < fetchedBits.Length; n += 2)
        {
            int dibit = (fetchedBits[n] << 1) | fetchedBits[n + 1];
            byte[] walsh = Ms110dTables.Walsh[dibit];
            for (int i = 0; i < ChipsPerSymbol; i++)
            {
                psk8Chips[o++] = (byte)((walsh[i & 3] + _scrambler.Next()) & 7);
            }
        }
    }

    /// <summary>Demodulates one received 32-chip channel symbol (complex, carrier-corrected)
    /// into two max-log LLRs (positive ⇒ bit 0; [0] = di-bit MSB). The instance's own
    /// scramble sequence is consumed — feed symbols in order and <see cref="Reset"/> at
    /// interleaver boundaries. Also reports the winning di-bit and its correlation, for
    /// decision-directed carrier tracking.</summary>
    public void Demodulate(ReadOnlySpan<Cf> chips, Span<float> llrs, out int bestDibit, out Cf bestCorrelation)
    {
        if (chips.Length != ChipsPerSymbol || llrs.Length != 2)
        {
            throw new ArgumentException("expected 32 chips and 2 LLRs", nameof(chips));
        }

        // Descramble (rotate each chip by the conjugate of its scramble symbol), then
        // correlate against the four Walsh candidates, whose chips are 0 or 4 → ±1 real.
        Span<Cf> descrambled = stackalloc Cf[ChipsPerSymbol];
        for (int i = 0; i < ChipsPerSymbol; i++)
        {
            descrambled[i] = chips[i] * Ms110dTables.Psk8[_scrambler.Next()].Conj();
        }

        Span<Cf> corr = stackalloc Cf[4];
        for (int s = 0; s < 4; s++)
        {
            byte[] walsh = Ms110dTables.Walsh[s];
            var acc = Cf.Zero;
            for (int i = 0; i < ChipsPerSymbol; i++)
            {
                acc = walsh[i & 3] == 0 ? acc + descrambled[i] : acc - descrambled[i];
            }

            corr[s] = acc;
        }

        // Candidate selection is non-coherent (magnitude): it stays reliable while the
        // carrier loop is still pulling in, and the winner's argument is then exactly the
        // phase-error observable the loop needs. The LLRs below stay coherent (Re).
        bestDibit = 0;
        for (int s = 1; s < 4; s++)
        {
            if (corr[s].Cnorm() > corr[bestDibit].Cnorm())
            {
                bestDibit = s;
            }
        }

        bestCorrelation = corr[bestDibit];

        // Max-log per-bit LLRs from the coherent (real) correlations.
        llrs[0] = Math.Max(corr[0].Re, corr[1].Re) - Math.Max(corr[2].Re, corr[3].Re);
        llrs[1] = Math.Max(corr[0].Re, corr[2].Re) - Math.Max(corr[1].Re, corr[3].Re);
    }
}
