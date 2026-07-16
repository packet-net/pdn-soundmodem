# FreeDV OFDM (datac) — Phase 1 design

The lead item of the [waveform roadmap](waveform-roadmap.md): a **pure-managed C# port** of
FreeDV/Codec2's OFDM `datac` data modes, interoperating bit-for-bit with the FreeDATA/FreeDV
ecosystem, and — as importantly — the vehicle by which we build the **shared OFDM engine** our
own greenfield FM and HF modes will reuse.

## Why a port, not a wrap
`libcodec2` is the reference, but we do **not** P/Invoke-wrap it into the shipping library (that
would add a native dependency and teach us nothing transferable). We port it to managed C# and
**validate bit-for-bit against `libcodec2` as a test-only oracle** (linked in the test project,
never shipped) — the same pattern by which `OccupiedBandwidthTests` validates against checked-in
NinoTNC recordings. Rationale: the LDPC is the *easy* part (~300 lines of self-contained
sum-product, H-matrices checked into `drowe67/codec2` as transliterable arrays); the hard part is
the OFDM sync/timing/channel-estimation engine — exactly what our own FM/HF modes need — so the
port makes FreeDV the training ground. *Licence:* `libcodec2` is LGPL-2.1; the ported LDPC carries
LGPL-2.1 lineage/attribution (Rowe / Valenti / Cowley), workable via the relicensing clause — flag
for a real FOSS-licence review before release.

## The modes
Six modes, all QPSK @ 8000 Hz, 1500 Hz centre: **datac1** (workhorse, 510 payload bytes/frame,
1700 Hz), **datac3/datac4** (low-SNR), **datac0/datac13/datac14** (small ACK/signalling). Phase 1
lights up **datac0** (first-light, smallest) → **datac1** (workhorse) → **datac3**; Phase 2 (task
#3) adds the rest, including the datac4/datac13 code-shortening.

## Interop is exact-or-nothing
There is no FreeDV protocol spec — the C source **is** the spec. A byte on the air must match, so
the port reproduces exactly: the per-mode unique-word patterns, the LCG-seeded preamble/postamble
(seed 2 / 3), the golden-prime interleaver, the CRC-16 (init 0xFFFF, last two payload bytes), and
the datac4/datac13 shortening. The test-oracle exists to catch any drift from these.

## About this document
The six component designs below were each produced by an agent reading the actual `drowe67/codec2`
source (cited inline). They are presented as authored (implementation-ready), organised into one
document, followed by a consolidated risks + Phase-1 breakdown. The multi-agent synthesis pass that
would have merged them failed on prompt size, so this assembly is by hand — which preserves the
component detail verbatim rather than compressing it.

---


## OFDM modulator

---

### C# OFDM Modulator for codec2 datac modes — implementation-ready design

All citations are to the codec2 shallow clone at `/home/tf/.claude/jobs/553c7662/tmp/codec2-ref/src/`. pdn-soundmodem paths are under `/home/tf/src/pdn-soundmodem/src/Packet.SoundModem/`.

#### 0. One decision that dominates everything: the IFFT is NOT pdn's `Fft`

codec2 does **not** use an FFT to modulate. `ofdm_txframe` (`ofdm.c:1004`) calls a hand-rolled direct inverse DFT, `idft` (`ofdm.c:642-667`), that sums over only the `Nc+2` occupied bins using a per-row phasor recurrence (`c *= delta`). To be interop-exact you must port **that** function, not substitute a radix-2 FFT:

- **Correctness across all 6 modes.** pdn's `Fft.Forward` (`Dsp/Fft.cs:11`) throws unless the length is a power of two. Five datac modes have `M=128` (power of two) but **datac14 has `M=144`** (`= 16·9`, not power of two). A radix-2 IFFT cannot do datac14 at all.
- **Bit-exactness.** Even at `M=128`, a radix-2 butterfly cascade produces a different float rounding sequence than codec2's `sum += vector[col]*c; c *= delta` accumulation over 11 bins. The oracle compares against codec2's actual output, so the arithmetic structure must match.
- **Cost is a non-issue.** The direct DFT is `O(M·(Nc+2))` with `Nc+2 ≤ 29`; it touches ~11–29 bins, far cheaper than a 128-point FFT over a mostly-zero 128-bin spectrum.

**Recommendation:** port `idft` directly (§6). pdn's `Fft.Forward` is still the right tool for the *demod* correlator and the `OccupiedBandwidthTests` meter, and I give an `Fft`-based IFFT helper (§6.3) *for those* — but it is explicitly not the oracle path.

Honesty caveat the task demands: "bit-for-bit" against codec2 is achievable in *algorithm, constants, and ordering*, but not as literal IEEE-754 equality, because codec2 uses C `cosf`/`sinf`/`cabsf` (`ofdm_internal.h:51` `cmplx`) whose last-ULP results differ between libm and .NET's `MathF`. The oracle must assert an RMS/peak tolerance (e.g. `< 1` on the ±16384 scale), not literal short equality. I state this rather than pretend otherwise.

#### 1. Component boundary (what this modulator owns)

From `interldpc.c:322` `ofdm_ldpc_interleave_tx`, the real TX pipeline is:

```
LDPC encode → QPSK-map → interleave → ofdm_assemble_qpsk_modem_packet_symbols → ofdm_txframe → ofdm_hilbert_clipper
                                        └──────────────────── THIS COMPONENT ────────────────────┘
```

**This component owns:** modem-packet symbol assembly (`ofdm_assemble_qpsk_modem_packet_symbols`, `ofdm.c:2412`), the `idft`+CP frame builder (`ofdm_txframe`, `ofdm.c:1004`), the Hilbert clipper / BPF / scaling chain (`ofdm_hilbert_clipper`, `ofdm.c:1072`), preamble/postamble generation (`ofdm_generate_preamble`, `ofdm.c:2592`), and burst assembly (`freedv_data_raw_tx.c`, `freedv_api.c:519-591`).

**Out of scope (sibling components):** LDPC (`ldpc_encode_frame`), the golden interleaver (`gp_interleave_comp`), CRC16, and KISS/IL2P framing. The modulator accepts *already-assembled* complex symbols (or, for the test path, raw packet bits) and returns audio.

#### 2. Exact per-mode parameter table

Fixed for all six datac modes (from `ofdm_mode.c` defaults + per-mode blocks; `data_mode="streaming"`, `state_machine="data"`, `edge_pilots=0`, `bps=2`, `txtbits=0`, `Fs=8000`, `tx_centre=rx_centre=1500`, `clip_en=true`, `tx_bpf_en=true`):

| Param | datac0 | datac1 | datac3 | datac4 | datac13 | datac14 |
|---|---|---|---|---|---|---|
| **Source-set** (`ofdm_mode.c`) | :122 | :145 | :169 | :195 | :222 | :249 |
| `Nc` (carriers) | 9 | 27 | 9 | 4 | 3 | 4 |
| `Ns` (syms/frame) | 5 | 5 | 5 | 5 | 5 | 5 |
| `Np` (frames/packet) | 4 | 38 | 29 | 47 | 18 | 4 |
| `Ts` (s) | 0.016 | 0.016 | 0.016 | 0.016 | 0.016 | **0.018** |
| `Tcp` (s) | 0.006 | 0.006 | 0.006 | 0.006 | 0.006 | **0.005** |
| `Nuwbits` | 32 | 16 | 40 | 32 | 48 | 32 |
| `bad_uw_errors` | 9 | 6 | 10 | 12 | 18 | 12 |
| `timing_mx_thresh` | 0.08 | 0.10 | 0.10 | 0.5 | 0.45 | 0.45 |
| `amp_scale` | 300e3 | 145e3 | 300e3 | 600e3 | 750e3 | 600e3 |
| `clip_gain1` | 2.2 | 2.7 | 2.2 | 1.2 | 1.2 | 2.0 |
| `clip_gain2` | 0.85 | 0.8 | 0.8 | 1.0 | 1.0 | 1.0 |
| `rx_bpf_en` | false | false | false | true | true | true |
| tx BPF prototype | filtP400S600 | filtP900S1100 | filtP400S600 | filtP200S400 | filtP200S400 | filtP200S400 |
| **Derived** (`ofdm_create`) | | | | | | |
| `Rs = 1/Ts` | 62.5 | 62.5 | 62.5 | 62.5 | 62.5 | 55.5556 |
| `M = Fs·Ts` (`:248`) | 128 | 128 | 128 | 128 | 128 | **144** |
| `Ncp = Tcp·Fs` (`:249`) | 48 | 48 | 48 | 48 | 48 | **40** |
| `SamplesPerSymbol = M+Ncp` (`:303`) | 176 | 176 | 176 | 176 | 176 | 184 |
| `SamplesPerFrame = Ns·(M+Ncp)` (`:304`) | 880 | 880 | 880 | 880 | 880 | 920 |
| `SamplesPerPacket = Np·SamplesPerFrame` | 3520 | 33440 | 25520 | 41360 | 15840 | 3680 |
| `BitsPerFrame = (Ns−1)·Nc·2` (`:297`) | 72 | 216 | 72 | 32 | 24 | 32 |
| `BitsPerPacket = Np·BitsPerFrame` (`:299`) | 288 | 8208 | 2088 | 1504 | 432 | 128 |
| `SymsPerPacket = BitsPerPacket/2` | 144 | 4104 | 1044 | 752 | 216 | 64 |
| `tx_nlower` (`:376`, see §3) | 19 | 10 | 19 | 21 | 22 | 24 |

`amp_scale` values are literal C expressions: datac4 `2*300E3`, datac13 `2.5*300E3`, datac14 `2.0*300E3` (`ofdm_mode.c:216,243,270`).

`Nuwbits`, `bad_uw_errors`, `timing_mx_thresh` are receive-side; carry them in the mode record for completeness but the modulator only reads `Nuwbits` (for UW symbol placement, §5.3) and the `tx_uw[]` word.

**tx_uw words** (the QPSK-mapped unique word, `ofdm_mode.c`): datac0/datac1 use the 16-symbol word `{1,1,0,0,1,0,1,0,1,1,1,1,0,0,0,0}` (datac0 zero-padded to 32 via `memcpy` of 16 into a zeroed 32-buffer `:136-137`; datac1 uses exactly 16 `:159-161`). datac3/4/13/14 use the 24-bit word `{…,1,1,1,1,0,0,0,0}` copied to the **front** and again to the **tail** (`tx_uw[Nuwbits−24 …]`) — e.g. `ofdm.c` pattern `memcpy(tx_uw,uw,24); memcpy(&tx_uw[nuwbits-24],uw,24)` (`ofdm_mode.c:187-188, 213-214, 240-241, 267-268`). Store each mode's resolved 32/16/40/32/48/32-bit `tx_uw[]` verbatim.

#### 3. Carrier → IFFT-bin mapping (exact)

From `ofdm_create` (`ofdm.c:374-376`):

```
doc       = TAU / (Fs/Rs)            = 2π / M               // radian bin spacing
tx_nlower = roundf(tx_centre/Rs − Nc/2) − 1                 // C roundf = half-away-from-zero
```

`idft` (`ofdm.c:655`) places frequency-domain column `col ∈ [0, Nc+1]` at digital frequency `(tx_nlower+col)·doc` rad/sample, i.e. **bin index `k = tx_nlower+col`**, audio frequency **`f = k·Rs` Hz** (since `Fs/M = Rs`).

Column layout inside each row (`ofdm_txframe:1024-1038`): `col 0` and `col Nc+1` are the **edge pilots** — zeroed for all datac because `edge_pilots=0` (`ofdm.c:369-370`). Data/pilot energy lives in `cols 1..Nc`.

Resolved occupied bins/frequencies (data carriers = `cols 1..Nc`):

| Mode | tx_nlower | data bins (k) | data freqs (Hz) | group centre |
|---|---|---|---|---|
| datac0 | 19 | 20…28 | 1250…1750 | 1500 (bin 24) |
| datac1 | 10 | 11…37 | 687.5…2312.5 | 1500 (bin 24) |
| datac3 | 19 | 20…28 | 1250…1750 | 1500 (bin 24) |
| datac4 | 21 | 22…25 | 1375…1562.5 | 1468.75 (bin 23.5) |
| datac13 | 22 | 23…25 | 1437.5…1562.5 | 1500 (bin 24) |
| datac14 | 24 | 25…28 | 1388.9…1555.6 | 1472.2 (bin 26.5) |

Note the **even-`Nc` half-bin offset**: datac4 and datac14 carrier groups sit ½ carrier below 1500 Hz — this is exactly what the `roundf(…)−1` math yields and must be reproduced, not "corrected". 1500 Hz is only the nominal design centre used for `tx_nlower` and BPF tuning.

#### 4. Class design (matches pdn idioms)

New folder `src/Packet.SoundModem/Ofdm/` (parallel to `Il2p/`, `Fx25/`). Parallel `float[]` re/im arrays in hot paths to match `Fft.Forward(Span<float> real, Span<float> imaginary)`; a small `readonly record struct Cf(float Re, float Im)` for symbol-level clarity.

```csharp
namespace Packet.SoundModem.Ofdm;

/// <summary>Immutable per-mode OFDM parameters + derived sizes. One instance per datac mode.</summary>
public sealed record OfdmMode
{
    public required string Name { get; init; }          // "datac0"…"datac14"
    // primary (ofdm_mode.c)
    public required int Nc { get; init; }
    public required int Ns { get; init; }
    public required int Np { get; init; }
    public required double Ts { get; init; }
    public required double Tcp { get; init; }
    public required int Nuwbits { get; init; }
    public required byte[] TxUw { get; init; }           // length == Nuwbits
    public required double AmpScale { get; init; }
    public required double ClipGain1 { get; init; }
    public required double ClipGain2 { get; init; }
    public required float[] TxBpfProto { get; init; }    // 100-tap real LP prototype
    public double Fs { get; init; } = 8000.0;
    public double TxCentre { get; init; } = 1500.0;
    public int Bps { get; init; } = 2;

    // derived (computed in ctor exactly per ofdm_create; see §2)
    public double Rs             => 1.0 / Ts;
    public int    M              => (int)(Fs / Rs);                       // ofdm.c:248
    public int    Ncp            => (int)(Tcp * Fs);                      // ofdm.c:249
    public int    SamplesPerSymbol => M + Ncp;                           // :303
    public int    SamplesPerFrame  => Ns * SamplesPerSymbol;            // :304
    public int    SamplesPerPacket => Np * SamplesPerFrame;
    public int    BitsPerFrame   => (Ns - 1) * Nc * Bps;                 // :297
    public int    BitsPerPacket  => Np * BitsPerFrame;                   // :299
    public int    SymsPerPacket  => BitsPerPacket / Bps;
    public double Doc            => (2.0 * Math.PI) / (Fs / Rs);         // :374
    public int    TxNlower       => (int)MathF.Round((float)(TxCentre / Rs) - Nc / 2.0f) - 1; // :376
    public double BpfCentreHz    => Rs * (TxNlower + (Nc + 1) / 2.0);    // find_carrier_centre, ofdm.c:570 (see §7)

    public static OfdmMode Datac0()  => …;   // static factory per mode, values from §2
    public static OfdmMode Datac1()  => …;
    public static OfdmMode Datac3()  => …;
    public static OfdmMode Datac4()  => …;
    public static OfdmMode Datac13() => …;
    public static OfdmMode Datac14() => …;
}
```

`MathF.Round` uses banker's rounding by default — **must pass `MidpointRounding.AwayFromZero`** to match C `roundf` (the half-integer cases 19.5→20, 10.5→11, 22.5→23 are load-bearing). Concretely: `(int)MathF.Round(x, MidpointRounding.AwayFromZero) - 1`.

```csharp
/// <summary>Interop-exact codec2 datac OFDM modulator. Holds precomputed pilots, UW symbols,
/// raw preamble/postamble frames, and the persistent Hilbert-clipper tx BPF.</summary>
public sealed class OfdmModulator
{
    public OfdmModulator(OfdmMode mode);

    public OfdmMode Mode { get; }

    // ── symbol assembly (ofdm_assemble_qpsk_modem_packet_symbols) ──
    // payloadSyms length == SymsPerPacket − Nuwbits/2 − txtsyms(=0). Returns SymsPerPacket symbols.
    public Cf[] AssembleModemPacket(ReadOnlySpan<Cf> payloadSyms);

    // ── one packet → audio (ofdm_txframe + ofdm_hilbert_clipper), uses persistent BPF state ──
    public void ModulatePacket(ReadOnlySpan<Cf> modemPacketSyms, Span<Cf> outSamples); // len SamplesPerPacket

    // ── convenience test path (ofdm_mod): raw BitsPerPacket bits → audio ──
    public Cf[] ModulatePacketBits(ReadOnlySpan<byte> packetBits);   // len BitsPerPacket

    // ── full burst (freedv_data_raw_tx): [preamble][Np-packet]×frames[postamble] ──
    // resetFilter=true starts BPF from zeros (matches a fresh codec2 struct emitting one burst).
    public Cf[] EmitBurst(IReadOnlyList<Cf[]> assembledPackets, bool resetFilter = true);

    // raw stored frames (amp_scale=1, no clip, no bpf) — exposed for tests
    public ReadOnlyMemory<Cf> PreambleRaw  { get; }   // len SamplesPerFrame, seed 2
    public ReadOnlyMemory<Cf> PostambleRaw { get; }   // len SamplesPerFrame, seed 3
}
```

#### 5. Modem-frame symbol assembly (exact)

### 5.1 QPSK map (`ofdm.c:76,106`)
```
qpsk[4] = { 1+0j, 0+1j, 0−1j, −1+0j }          // Gray-coded, indices 0..3
```
For a dibit `(b0,b1)` the symbol is `qpsk[(b0<<1)|b1]`. Verified from `ofdm_mod` (`ofdm.c:1194-1197`): `dibit[0]=tx_bits[s+1]; dibit[1]=tx_bits[s]; qpsk_mod(dibit)=qpsk[(dibit[1]<<1)|dibit[0]]` → index `(tx_bits[s]<<1)|tx_bits[s+1]`. So first bit is the MSB of the index. Mapping: `00→1 (0°)`, `01→j (90°)`, `10→−j (270°)`, `11→−1 (180°)`.

### 5.2 Frame grid placement (`ofdm_txframe:1023-1039`)
Build `aframe[Np·Ns][Nc+2]`, zero-filled. For row `r = 0..Np·Ns−1`:
- **pilot row** when `r % Ns == 0`: copy `pilots[0..Nc+1]` into the whole row.
- **data row** otherwise: copy the next `Nc` symbols from the input array into `cols 1..Nc` (cols 0 and Nc+1 stay zero). Consume input in strict row-major order across all `Np` frames. (`dpsk_en` is always false for datac — the `aframe[r][j] *= aframe[r−1][j]` branch at `:1035` is dead here.)

Input length = `Np·(Ns−1)·Nc = SymsPerPacket`. Pilot rows do not consume input.

### 5.3 Pilots (`ofdm_create:360-371`)
`pilots[i] = (float)pilotvalues[i] + 0j` for `i=0..Nc+1`, then (edge_pilots=0) `pilots[0]=pilots[Nc+1]=0`. Port `pilotvalues[64]` verbatim (`ofdm.c:88-92`) — a mode uses only the first `Nc+2`:
```
{-1,-1, 1, 1,-1,-1,-1, 1,-1, 1,-1, 1, 1, 1, 1, 1,
  1, 1, 1,-1,-1, 1,-1, 1,-1, 1, 1, 1, 1, 1, 1, 1,
  1, 1, 1,-1, 1, 1, 1, 1, 1,-1,-1,-1,-1,-1,-1, 1,
 -1, 1,-1, 1,-1,-1, 1,-1, 1, 1, 1, 1,-1, 1,-1, 1}
```
Pilots are real ±1 BPSK.

### 5.4 UW + payload interleave (`ofdm_assemble_qpsk_modem_packet_symbols:2412`)
The assembled symbol array is UW symbols scattered among payload symbols at fixed indices `uw_ind_sym[]`, computed once in `ofdm_create:445-463`:
```
Nuwsyms          = Nuwbits / 2
Ndatasymsperframe = (Ns−1) * Nc
uw_step = Nc + 1
if (floor(Nuwsyms*uw_step/2) >= Np*Ndatasymsperframe) uw_step = Nc − 1
for i in 0..Nuwsyms−1:
    uw_ind_sym[i] = floor((i+1) * uw_step / 2)       // integer floor
```
Assembly (`:2431-2437`), `s` over `0..SymsPerPacket−1` (Ntxtsyms=0 for datac):
```
if (u < Nuwsyms && s == uw_ind_sym[u]) modem_packet[s] = tx_uw_syms[u++];
else                                    modem_packet[s] = payload[p++];
```
`tx_uw_syms[k] = qpsk[(tx_uw[2k]<<1) | tx_uw[2k+1]]` (`ofdm_create:475-480`; note `dibit[1]=tx_uw[2k], dibit[0]=tx_uw[2k+1]`, same convention as §5.1). Precompute `tx_uw_syms` and `uw_ind_sym` in the ctor; expose `AssembleModemPacket` to insert UW around caller-supplied payload symbols.

#### 6. IDFT + cyclic prefix (exact)

### 6.1 Direct IDFT — the oracle path (`ofdm.c:642-667`)
```csharp
// vector = one row, Nc+2 complex bins; result = M complex time samples
void Idft(ReadOnlySpan<Cf> vector, Span<Cf> result) {
    float invM = 1f / M;
    // row 0: DC == sum of bins, scaled
    Cf acc0 = default; for (int c=0;c<Nc+2;c++) acc0 += vector[c];
    result[0] = acc0 * invM;
    for (int row=1; row<M; row++) {
        float a0 = (float)(TxNlower * Doc * row);
        float d  = (float)(Doc * row);
        Cf c     = new(MathF.Cos(a0), MathF.Sin(a0));      // cmplx(tx_nlower*doc*row)
        Cf delta = new(MathF.Cos(d),  MathF.Sin(d));       // cmplx(doc*row)
        Cf acc = default;
        for (int col=0; col<Nc+2; col++) { acc += vector[col]*c; c *= delta; }
        result[row] = acc * invM;
    }
}
```
Preserve the `c *= delta` recurrence and the accumulation order exactly (this is the interop-critical part). Use `MathF` (single precision) to track codec2's `cmplx`.

### 6.2 CP + concatenation (`ofdm_txframe:1043-1064`)
Per row, after `Idft`: prepend the last `Ncp` samples as the cyclic prefix, giving `SamplesPerSymbol = M+Ncp`:
```
out[0..Ncp-1]        = sym[M-Ncp .. M-1]     // CP
out[Ncp..Ncp+M-1]    = sym[0 .. M-1]
```
Concatenate all `Np·Ns` rows → `SamplesPerPacket` samples, then run §7 on the whole block.

### 6.3 Optional `Fft`-based IDFT (analysis only — NOT oracle)
For OB measurement / prototyping where bit-exactness is not required and `M` is a power of two (all modes except datac14): zero-pad the `Nc+2` bins into a length-`M` spectrum at indices `k=TxNlower+col` (no wraparound: max bin `TxNlower+Nc+1 ≤ 38 < 128`), then invert via the conjugate trick against `Dsp/Fft.cs`:
```csharp
// ifft(X) = conj(fft(conj(X))) / M
for (int k=0;k<M;k++) im[k] = -im[k];
Fft.Forward(re, im);                       // Dsp/Fft.cs:11 (radix-2, forward)
for (int k=0;k<M;k++){ re[k]/=M; im[k]=-im[k]/M; }
```
Flag in XML docs that this diverges from codec2 in the last ULPs and cannot serve datac14; the `Idft` in §6.1 is the modem's real path.

#### 7. Hilbert clipper / scaling / BPF chain (exact)

`ofdm_hilbert_clipper` (`ofdm.c:1072-1100`) is applied to every emitted block (preamble, each data packet, postamble) — see §8/§9 — in this exact order, with `OFDM_PEAK = 16384` (`codec2_ofdm.h:45`):

1. `tx[i] *= amp_scale` (per mode).
2. if `clip_en` (true for all datac): `tx[i] *= clip_gain1`; then `ofdm_clip(tx, 16384)`.
3. if `tx_bpf_en` (true for all datac): complex band-pass filter (below), in place.
4. if `tx_bpf_en && clip_en`: `tx[i] *= clip_gain2`.
5. `ofdm_clip(tx, 16384)` (final, always).

`ofdm_clip` (`ofdm.c:2683`): soft magnitude clip — `if |sam| > thresh: sam *= thresh/|sam|` (uses `cabsf`).

**Complex tx BPF** — port `quisk_cfTune` + `quisk_ccfFilter` (`filter.c:232, 263`):
- Tune once at construction (`filter.c:243-246`): `cpxCoefs[i] = cmplx(2π·freq·(i−D)) · dCoefs[i]`, where `D = (nTaps−1)/2 = 49.5`, `freq = centre/Fs`, `dCoefs` = the 100-tap real prototype.
  - `centre` = `TxCentre` (=1500) for datac0/1/3; for **datac4/13/14** it is `find_carrier_centre` (`ofdm.c:570-575, 558`) = `Rs·(TxNlower + (Nc+1)/2)` → datac4 **1468.75 Hz**, datac13 **1500 Hz**, datac14 **1472.22 Hz** (the `BpfCentreHz` property in §4).
- Filter (`filter.c:263-288`): a **stateful** complex FIR over a circular history initialised to zeros; forward over coeffs, backward over samples. Since the prototype is symmetric this equals ordinary convolution `y[n] = Σ_k cpxCoefs[k]·x[n−k]` with zero pre-roll history. Implement as a new `ComplexBandpassFir` (pdn has only real `Dsp/FirFilter.cs`); mirror `FirFilter`'s circular-buffer idiom but complex-in/complex-out with complex taps.

**State persistence is interop-critical (§9).** The three prototypes to port verbatim (100 floats each) live in `filter_coef.h`: `filtP400S600` (`:95`, datac0/3), `filtP900S1100` (`:186`, datac1), `filtP200S400` (`:279`, datac4/13/14). Confirmed 100 taps each; declared `[100]` in `filter.h:42-47`. (`rx_bpf_en` is receive-side; irrelevant to the modulator.)

#### 8. Preamble / postamble (exact)

`ofdm_create:532-538` builds them once via `ofdm_generate_preamble(ofdm, buf, seed)` with **seed 2 (preamble)** and **seed 3 (postamble)**.

`ofdm_generate_preamble` (`ofdm.c:2592-2609`):
1. Copy the mode, force `Np=1`, `bitsperpacket=bitsperframe` (one modem frame).
2. Generate `bitsperframe` bits from the LCG (`ofdm_rand_seed`, `ofdm.c:2574`), seed = 2 or 3:
   ```
   seed = (1103515245 * seed + 12345) % 32768;   r[i] = seed;   bit[i] = (r[i] > 16384)
   ```
   64-bit unsigned intermediate; result ∈ [0,32767]. (Seed 1 is the test-payload generator `ofdm_rand`, `ofdm.c:2572`.)
3. Set `amp_scale=1.0`, `tx_bpf_en=false`, `clip_en=false`, then run the **normal `ofdm_mod` path** (`ofdm.c:1176` → QPSK-map all `bitsperframe` bits → `ofdm_txframe`). Because scale=1/no-clip/no-bpf, the stored frame is the **raw idft+CP output** (one pilot row + `Ns−1` data rows of random QPSK), length `SamplesPerFrame`.

Preamble/postamble content is thus **fixed per mode** (deterministic LCG). Store both raw `Cf[SamplesPerFrame]` at construction.

**At emit** (`freedv_api.c:519-591`): the stored raw frame is `memcpy`'d out then re-run through the *real* `ofdm_hilbert_clipper` (full `amp_scale`, `clip_gain1`, clip, **persistent BPF**, `clip_gain2`, final clip) — so preamble and postamble come out at the same level and through the same filter as data.

#### 9. Burst assembly + BPF state semantics

`freedv_data_raw_tx.c:370-405` structure per burst:
```
[preamble frame] [data packet]×framesPerBurst [postamble frame]   (+ trailing silence, not filtered)
```
Each of preamble / each data packet / postamble is pushed through `ofdm_hilbert_clipper`, and **all share the one persistent `ofdm->tx_bpf`** allocated in `ofdm_create`. Consequences the C# `EmitBurst` must reproduce exactly:
- The BPF is a single streaming instance; feed segments **in order** (preamble → data… → postamble). The ~49-sample group-delay tail is **never flushed**, so each segment's trailing samples lack the following segment's contribution and each segment's leading samples carry the previous segment's tail. This asymmetry is part of the reference waveform.
- Inter-burst silence is written to the output but **not** pushed through the filter, so for exact *multi-burst* continuation the filter state must **carry** across bursts (codec2 reuses one struct for the whole run). `EmitBurst(resetFilter:true)` zeroes the BPF to match a fresh single-burst codec2 process; `resetFilter:false` matches burst N>1 of a continuous run.
- Data packets are produced by `ModulatePacket` (which internally calls the shared clipper) — so `EmitBurst` must route the preamble/postamble re-clip and the data-packet clip through the *same* `ComplexBandpassFir` field.

#### 10. Constants to port verbatim (checklist)

| Constant | Source | Notes |
|---|---|---|
| `qpsk[4] = {1, j, −j, −1}` | `ofdm.c:76` | Gray map, index `(b0<<1)|b1` |
| `pilotvalues[64]` | `ofdm.c:88-92` | real ±1; use first `Nc+2` |
| `OFDM_PEAK = 16384` | `codec2_ofdm.h:45` | clip threshold |
| LCG `1103515245`, `12345`, mod `32768` | `ofdm.c:2576` | seeds 2=preamble, 3=postamble, 1=test |
| `filtP400S600[100]` | `filter_coef.h:95` | datac0/3 tx BPF |
| `filtP900S1100[100]` | `filter_coef.h:186` | datac1 tx BPF |
| `filtP200S400[100]` | `filter_coef.h:279` | datac4/13/14 tx BPF |
| per-mode `tx_uw[]` words | `ofdm_mode.c` §2 | resolved (front+tail copy) |
| all §2 primary params | `ofdm_mode.c:122-275` | |

#### 11. IModem integration + oracle test (notes; integration is Phase 3)

- **Native rate is 8000 Hz.** The modulator emits complex `Cf[]` at `Fs=8000` on the codec2 amplitude scale (peak ≈16384). For `IModem.Modulate` (`Modems/IModem.cs:31`, returns `float[]` at the channel DSP rate) the wrapper: takes **real part only** (matches `freedv_rawdatatx`'s `mod_out[i]=comp.real`, `freedv_api.c:516`), scales by `1/32768` to land in pdn's ±1 convention, and **resamples 8000→DSP rate** via `Dsp/Upsampler.cs` / `Channel/UpsamplingAudioOutput.cs`. codec2's `comp_to_short` truncates toward zero (C cast) — replicate only in the oracle short-comparison path, not the float audio path.
- **`Mode` string**: `"datac0"…"datac14"` per `IModem.Mode`.
- **TXDELAY**: the datac preamble length is fixed (`SamplesPerFrame`); `txDelayMilliseconds` maps to extra leading silence and/or repeated preamble frames at the burst layer, not into the OFDM frame (unlike the QPSK/IL2P modems).
- **Oracle (task-list Phase 1 "FreeDV oracle")**: drive codec2's `freedv_data_raw_tx` (or `ofdm_mod`) to `.s16`, run the same assembled symbols / packet bits through `OfdmModulator`, compare per-sample within a float tolerance (`cosf/sinf` last-ULP divergence, §0). Good structural checkpoints that *are* exactly reproducible: `SamplesPerPacket`/`SamplesPerFrame` lengths, the raw preamble frame (pre-clip), `uw_ind_sym[]`, and the pre-BPF stage-2 output. Mirror the `OccupiedBandwidthTests` pattern (`tests/.../Dsp/OccupiedBandwidthTests.cs`) for a per-mode OBW guard (expected ≈ `(Nc−1)·Rs` + skirt: datac0/3 ≈500 Hz, datac1 ≈1.6 kHz, datac4 ≈190 Hz, datac13 ≈125 Hz).

#### 12. Explicit caveats / not-in-source

- **Literal bit-for-bit is not attainable** across the C/C# `cosf`/`sinf`/`cabsf` boundary (§0); "interop-exact" here means identical algorithm/constants/ordering validated within a tight tolerance. This is a genuine limit of the sources read, not a design shortcut.
- **`amp` / `FSK_SCALE`, `--amp` CLI scaling** (`freedv_data_raw_tx.c:59,102`, `freedv_set_tx_amp`) is a demo-harness output gain applied *after* the modem; it is not part of the OFDM modulator and I did not trace `freedv_set_tx_amp`'s exact insertion point — exclude it from the component and apply any output gain at the IModem/channel layer.
- **LDPC, `gp_interleave_comp`, CRC16** are sibling components (`interldpc.c:333-336`); not read here beyond confirming the boundary. `AssembleModemPacket` expects already-encoded, already-interleaved payload symbols.
- The `dpsk_en` differential branch (`ofdm_txframe:1034`) and QAM-16 path (`ofdm.c:122`) are dead for datac (QPSK, dpsk off) and intentionally omitted.
- I read only the modulator half; RX-side fields in `OFDM_CONFIG`/`struct OFDM` (`ofdm_internal.h`) are carried in the mode record for parity but unused by this component.

Key source files (all under `/home/tf/.claude/jobs/553c7662/tmp/codec2-ref/src/`): `ofdm.c`, `ofdm_mode.c`, `ofdm_internal.h`, `filter.c`, `filter_coef.h`, `codec2_ofdm.h`, `freedv_data_raw_tx.c`, `freedv_api.c`, `freedv_700.c`, `interldpc.c`. pdn idioms to match: `src/Packet.SoundModem/Dsp/Fft.cs`, `Dsp/FirFilter.cs`, `Dsp/FilterDesign.cs`, `Modems/IModem.cs`, `Modems/QpskModulator.cs`, `Modems/QpskModem.cs`, `tests/Packet.SoundModem.Tests/Dsp/OccupiedBandwidthTests.cs`.


## Demodulator + synchronisation

---

### C# OFDM Demodulator + Sync State Machine — implementation-ready design

**Source provenance.** Every citation below is to the shallow codec2 clone at `/home/tf/.claude/jobs/553c7662/tmp/codec2-ref/src/` (identical to the `codec2/` clone — `diff -q` clean). Primary files: `ofdm.c` (2696 lines), `ofdm_internal.h`, `ofdm_mode.c`, `ofdm_demod.c` (the reference RX driver), `mpdecode_core.c` (LLR demap), `gp_interleaver.c`, `wval.h`, `codec2_ofdm.h`. pdn-soundmodem idioms read from `/home/tf/src/pdn-soundmodem/src/Packet.SoundModem/` (`Dsp/Fft.cs`, `Dsp/FirFilter.cs`, `Dsp/FilterDesign.cs`, `Modems/IModem.cs`, `Modems/QpskDemodulator.cs`).

**The six datac modes are all `bps=2` (QPSK).** None of the six overrides `bps`, so they inherit the 700D default `config->bps = 2` (`ofdm_mode.c:35`). All set `fs=8000`, `tx_centre=rx_centre=1500`, `edge_pilots=0`, `txtbits=0`, `state_machine="data"`, `data_mode="streaming"`, `amp_est_mode=1`.

**CRITICAL CORRECTION grounded in source (not a guess).** In `ofdm_demod_core`, `ofdm.c:1859-1860` unconditionally overwrite the per-branch phase/amp estimates:
```c
1857    }                                        // closes the high_bw else-block
1858
1859    aphase_est_pilot[i] = cargf(aphase_est_pilot_rect);
1860    aamp_est_pilot[i]   = cabsf(aphase_est_pilot_rect);
```
These run for **both** the `low_bw` (1827-1828) and `high_bw` (1848-1856) branches, so **`amp_est_mode` is dead code in this codec2 revision** — the amplitude used for every carrier is always `|aphase_est_pilot_rect|` and the phase is always `arg(aphase_est_pilot_rect)`, where `aphase_est_pilot_rect` is the `low_bw` 12-pilot average or the `high_bw` 2-pilot average. To be bit-exact you must reproduce this: compute the branch, then overwrite. I flag it because a "faithful" reading of the `amp_est_mode==1` block (1854-1855) would silently diverge. (Also note 1854-1855's precedence: `cabsf(a) + cabsf(b) / 2.0` is `|a| + |b|/2`, not an average — but it's dead anyway.)

---

#### 1. Exact mode parameter table

From `ofdm_mode.c` (overrides) + `ofdm_create` derivations (`ofdm.c:247-318, 374-377`). Derived: `m = (int)(fs/rs)`, `rs = 1/ts`, `Ncp = (int)(tcp*fs)`, `SamplesPerSymbol = m+Ncp`, `SamplesPerFrame = ns*SamplesPerSymbol`, `BitsPerFrame = (ns-1)*nc*bps`, `BitsPerPacket = np*BitsPerFrame`, `RowsPerFrame = ns-1`, `doc = 2π/m`, `RxNlower = roundf(centre/rs - nc/2) - 1` (C `roundf` = round-half-away-from-zero).

| mode | nc | ns | np | ts | tcp | m | Ncp | SpS | SpF | bits/frame | bits/packet | nuwbits | bad_uw | ftwin | mx_thresh | codename | rx_bpf |
|------|----|----|----|------|------|-----|-----|-----|-----|-----|------|----|----|----|------|------|------|
| datac0  | 9  | 5 | 4  | .016 | .006 | 128 | 48 | 176 | 880 | 72  | 288  | 32 | 9  | 80 | 0.08 | H_128_256_5   | false |
| datac1  | 27 | 5 | 38 | .016 | .006 | 128 | 48 | 176 | 880 | 216 | 8208 | 16 | 6  | 80 | 0.10 | H_4096_8192_3d| false |
| datac3  | 9  | 5 | 29 | .016 | .006 | 128 | 48 | 176 | 880 | 72  | 2088 | 40 | 10 | 80 | 0.10 | H_1024_2048_4f| false |
| datac4  | 4  | 5 | 47 | .016 | .006 | 128 | 48 | 176 | 880 | 32  | 1504 | 32 | 12 | 80 | 0.50 | H_1024_2048_4f| true  |
| datac13 | 3  | 5 | 18 | .016 | .006 | 128 | 48 | 176 | 880 | 24  | 432  | 48 | 18 | 80 | 0.45 | H_256_512_4   | true  |
| datac14 | 4  | 5 | 4  | .018 | .005 | 144 | 40 | 184 | 920 | 32  | 128  | 32 | 12 | 80 | 0.45 | HRA_56_56     | true  |

`rs`: 62.5 Hz for the ts=.016 modes; 55.5̄ Hz for datac14. `RxNlower`: datac0=19, datac1=10, datac3=19, datac4=21, datac13=22, datac14 (rs=55.56): `round(1500/55.56 - 2) - 1` = `round(27 - 2) - 1` = 24. Carriers are `nc+2` bins (`RxNlower+0 … RxNlower+nc+1`), with bins 0 and `nc+1` forced to **pilot value 0** because `edge_pilots==0` (`ofdm.c:369-371`).

**UW & TX-preamble placement** (`ofdm.c:435-539`): `uw_ind_sym`/`uw_ind` computed by the golden algorithm at 445-463 (step = `nc+1`, falls back to `nc-1` if it overruns `np*(ns-1)*nc`); `nuwframes = ceil((uw_ind_sym[last]+1)/(bits/frame / bps))`. The demod only needs `uw_ind_sym` (symbol indices) and `nuwframes` on RX. Preamble/postamble waveforms (`ofdm.c:532-539`) are one modem-frame of pseudo-random QPSK (`ofdm_generate_preamble`, seed 2 = preamble, seed 3 = postamble) — needed only for **burst** acquisition (§7).

---

#### 2. Fixed tables to port verbatim

- **`qpsk[4]`** (`ofdm.c:76-77`): `{1+0i, 0+1i, 0-1i, -1+0i}`, indexed `(bits[1]<<1)|bits[0]`.
- **`pilotvalues[64]`** (`ofdm.c:88-92`) — the exact int8 BPSK sequence; `pilots[i] = (float)pilotvalues[i]` for `i∈[0,nc+2)`, then edges zeroed. Port the 64-element array literally.
- **`ofdm_wval[200]`** (`wval.h`) = `exp(-j·2π·40·i/8000)`, `i∈[0,200)`. Generated by formula; regenerate in C# as `Complex.FromPolarCoordinates(1, -2*Math.PI*40*i/fs)` (float-cast) — bit-identical to within `cosf/sinf` rounding. Only the first `SamplesPerSymbol` (≤184) entries are used.
- **`S_matrix[4]`** (`mpdecode_core.c:32-33`): `{(1,0),(0,1),(0,-1),(-1,0)}` — LLR constellation.
- **Constants**: `TAU=2π`, `ROT45=π/4` (`ofdm_internal.h:47-48`); `AJIAN=-0.24904163195436`, `TJIAN=2.50681740420944` (`mpdecode_core.c:122-123`); `foff_est_gain=0.1` (`ofdm.c:417`); EsNo `=3.0f` hard-coded (`ofdm_demod.c:411`, `freedv_700.c:451`).

---

#### 3. C# class architecture (matches pdn-soundmodem idioms)

Namespace `Packet.SoundModem.Ofdm`. Pure-managed, `float`/`System.Numerics.Complex`-free hot path — use a `struct Cf { public float Re, Im; }` (or parallel `float[]` re/im) to match codec2's `complex float` and avoid `Complex`'s `double` promotion, which would break interop-exactness. Idioms: static DSP helpers like `Fft`/`FilterDesign`; per-instance streaming state like `FirFilter`; `IModem` for the channel-facing wrapper.

```csharp
// Immutable per-mode constants (the §1 table), built once.
public sealed record OfdmModeConfig(
    string Mode, int Nc, int Ns, int Np, float Ts, float Tcp, float Fs,
    float TxCentre, float RxCentre, int Bps, int NuwBits, int BadUwErrors,
    int FtWindowWidth, float TimingMxThresh, int EdgePilots, int TxtBits,
    string StateMachine, string DataMode, string Codename, bool RxBpfEnable,
    byte[] TxUw)
{
    // Derived (computed in a factory): M, Ncp, SamplesPerSymbol, SamplesPerFrame,
    // BitsPerFrame, BitsPerPacket, RowsPerFrame, Rs, Doc, RxNlower, TxNlower,
    // NrxBuf, NrxBufHistory, NrxBufMin, MaxSamplesPerFrame, Nuwframes,
    // int[] UwIndSym, Cf[] Pilots (nc+2), Cf[] PilotSamples (SamplesPerSymbol),
    // float TimingNorm.
    public static OfdmModeConfig Datac0() => …;   // one factory per mode
    public static OfdmModeConfig Datac1() => …;
    // datac3, datac4, datac13, datac14
}

public enum SyncState { Search, Trial, Synced }          // ofdm_internal.h:55
public enum PhaseEstBandwidth { Low, High }              // ofdm_internal.h:65-68

public sealed class OfdmDemodulator      // ports ofdm.c demod + sync_search
{
    public OfdmDemodulator(OfdmModeConfig cfg);

    // --- buffer feed (ports ofdm_sync_search / ofdm_demod COMP wrappers) ---
    public int Nin { get; private set; }                 // samples wanted next call
    public SyncState State { get; private set; }

    // ofdm_sync_search: slide rxbuf by Nin, append `input` (length==Nin), run core.
    public bool SyncSearch(ReadOnlySpan<Cf> input);      // returns timing_valid

    // ofdm_demod: slide rxbuf by Nin, append, demod one modem frame.
    // Fills RxNp[rowsperframe*nc] (phase-corrected symbols) + RxAmp[...] + hard RxBits.
    public void Demod(ReadOnlySpan<Cf> input);

    public ReadOnlySpan<Cf>    RxNp  { get; }             // ofdm->rx_np
    public ReadOnlySpan<float> RxAmp { get; }             // ofdm->rx_amp
    public float MeanAmp { get; }                         // ofdm->mean_amp
    public float FoffEstHz { get; }
    public int TimingEst { get; }  public float TimingMx { get; }
    public int ModemFrame { get; } public int Nuwframes => cfg.Nuwframes;

    // ofdm_sync_state_machine (data-streaming variant dispatched by cfg)
    public void SyncStateMachine(ReadOnlySpan<byte> rxUw);
    public bool SyncStart { get; }                        // for stat reset
}

// Packet-level assembly (ports ofdm_demod.c 458-506 + mpdecode_core demap)
public sealed class OfdmPacketAssembler
{
    public void PushFrame(ReadOnlySpan<Cf> rxNp, ReadOnlySpan<float> rxAmp); // slide buffer
    public void ExtractUw(Span<byte> rxUw);               // ofdm_extract_uw
    public bool PacketReady { get; }                      // modem_frame == np-1
    public void ToLlrs(Span<float> llr, float meanAmp,    // disassemble+deinterleave+symbols_to_llrs
                       float esNo = 3.0f);
}
```

The channel-facing `IModem` implementation (`DatacModem : IModem`) owns an `OfdmDemodulator` + `OfdmPacketAssembler` + the LDPC decoder (separate component) and the sample-rate bridge (see §12 — the channel runs at 12 k/48 k, datac is defined at 8 k, so a rational resampler like the existing `Dsp/Decimator`/`Dsp/Upsampler` sits in front). `Modulate` is a separate TX component; this design covers RX.

---

#### 4. DFT / iDFT (per-symbol Goertzel, NOT FFT)

codec2 uses direct DFTs of `nc+2` bins over `m` samples — do **not** substitute `Dsp/Fft` (wrong bin geometry; `m=128/144` isn't the bin count). Port `ofdm.c:642-692` exactly.

```csharp
// idft: time <- freq, used ONLY to build PilotSamples at init (ofdm.c:642-667)
static void Idft(OfdmModeConfig c, Span<Cf> result /*m*/, ReadOnlySpan<Cf> vec /*nc+2*/) {
    result[0] = Sum(vec) * (1f/c.M);                       // cexp(j0)==1
    for (int row = 1; row < c.M; row++) {
        Cf cc = Cmplx(c.TxNlower * c.Doc * row);           // cmplx = cos+ j sin
        Cf delta = Cmplx(c.Doc * row);
        Cf acc = default;
        for (int col = 0; col < c.Nc+2; col++) { acc += vec[col]*cc; cc *= delta; }
        result[row] = acc * (1f/c.M);
    }
}
// dft: freq <- time, m samples -> nc+2 bins (ofdm.c:674-692)
static void Dft(OfdmModeConfig c, Span<Cf> result /*nc+2*/, ReadOnlySpan<Cf> vec /*m*/) {
    for (int col = 0; col < c.Nc+2; col++) {
        float tval = (c.RxNlower + col) * c.Doc;
        Cf cc = CmplxConj(tval), delta = cc;               // cmplxconj = cos - j sin
        Cf acc = vec[0];                                   // row 0 term
        for (int row = 1; row < c.M; row++) { acc += vec[row]*cc; cc *= delta; }
        result[col] = acc;
    }
}
```
**Pilot-samples init** (`ofdm.c:495-528`): `Idft(pilots)→temp[m]`; `PilotSamples[0..Ncp)=0`; `PilotSamples[Ncp..SpS)=temp[0..m)`. The CP region is zeroed deliberately ("timing/freq est work better without CP"). Then `TimingNorm = SamplesPerSymbol * Σ|PilotSamples[i]|²` over `i∈[0,SpS)` (523-528).

---

#### 5. Buffer model — exact (`ofdm.c:305-326, 1215-1249, 1487-1522, 1956-1961`)

Persistent `Cf[] rxbuf` of length `NrxBuf`, plus `int rxbufst` window start and `int nin`:
- `NrxBufHistory = (np+2)*SamplesPerFrame` (streaming; 0 otherwise), `NrxBufMin = 3*SamplesPerFrame + 3*SamplesPerSymbol`, `NrxBuf = History + Min`. `rxbufst₀ = NrxBufHistory`, `nin₀ = SamplesPerFrame` (`ofdm.c:423`).
- **Feed** (both `SyncSearch` and `Demod`): `memmove(rxbuf[0], rxbuf[nin], (NrxBuf-nin))` then append `nin` new samples at the tail (`1222-1225` / `1493-1500`). In C# a `Array.Copy` left-shift + append.
- **End of `Demod`** (`1956-1961`): `rxbufst_next = rxbufst + nin; if (rxbufst_next + NrxBufMin <= NrxBuf) { rxbufst = rxbufst_next; nin = 0; }`. This lets multiple frames be demodulated out of the already-buffered history with **no new input** (`nin=0`) until `rxbufst` walks to the end, then a full frame is requested again. Your channel wrapper must honour `Nin==0` (feed empty span) — this is the mechanism by which one audio push can yield several decoded frames.

---

#### 6. Streaming acquisition (the FreeDV-default datac path)

`freedv_ofdm_data_open` does **not** call `ofdm_set_packets_per_burst`, so datac defaults to `data_mode="streaming"` → `ofdm_sync_search_stream` (`ofdm.c:1394-1464`), **not** the burst preamble correlator. Confirmed at `freedv_700.c:453-459`. Acquisition is on the **pilot symbol waveform**, not a preamble.

### 6a. `est_timing` — the correlation metric (`ofdm.c:794-923`)

Window search over `Ncorr = length - (SamplesPerFrame + SamplesPerSymbol)` positions, `step` given by caller.

```csharp
// returns timing_est; also out timing_mx, timing_valid
static int EstTiming(OfdmModeConfig c, ReadOnlySpan<Cf> rx, int length,
                     int fcoarse, out float timingMx, out bool timingValid, int step) {
    int Ncorr = length - (c.SamplesPerFrame + c.SamplesPerSymbol);
    float acc = 0; for (int i=0;i<length;i++) acc += Cnorm(rx[i]);      // |rx|^2
    float avLevel = 1f / (2f*MathF.Sqrt(c.TimingNorm*acc/length) + 1e-12f);

    // wvec_pilot: conj(pilot) at fcoarse 0; ±40 multiplies by ofdm_wval (ofdm.c:818-833)
    Span<Cf> w = stackalloc Cf[c.SamplesPerSymbol];
    for (int j=0;j<c.SamplesPerSymbol;j++)
        w[j] = fcoarse switch {
            0   => Conj(c.PilotSamples[j]),
            40  => Wval[j] * Conj(c.PilotSamples[j]),
           -40  => Conj(Wval[j] * c.PilotSamples[j]),
            _   => throw new ArgumentException() };

    var corr = new float[Ncorr];                                       // step-sparse
    for (int i=0;i<Ncorr;i+=step) {
        Cf st = DotNoConj(rx.Slice(i),                 w, c.SamplesPerSymbol); // Σ rx*w
        Cf en = DotNoConj(rx.Slice(i+c.SamplesPerFrame), w, c.SamplesPerSymbol);
        corr[i] = (Abs(st) + Abs(en)) * avLevel;                       // ofdm.c:895
    }
    int est=0; timingMx=0;
    for (int i=0;i<Ncorr;i+=step) if (corr[i] > timingMx) { timingMx=corr[i]; est=i; }
    timingValid = Abs(rx[est]) > 0f && timingMx > c.TimingMxThresh;    // ofdm.c:914-915
    return est;
}
```
Key exactness points: the dot product is **non-conjugating** `Σ left·right` (`ofdm_complex_dot_product`, `ofdm.c:726-782`) — the conjugation is baked into `w`. Metric combines **two frames'** pilots (`corr_st` at offset `i`, `corr_en` one `SamplesPerFrame` later) so acquisition needs ≥ `SamplesPerFrame + SamplesPerSymbol` extra samples. `avLevel` normalises by received energy and `TimingNorm`, making `timing_mx` a ~[0,1] matched-filter score comparable against `TimingMxThresh` (the 0.08–0.50 per-mode thresholds).

### 6b. Coarse-frequency grid + fine estimate (`ofdm.c:1394-1463`)

```
st = rxbufst + SamplesPerFrame + SamplesPerSymbol;  en = st + 2*SamplesPerFrame + SamplesPerSymbol;
for fcoarse in {-40, 0, +40}:                        // ofdm.c:1411, step=2
    est = EstTiming(rxbuf[st..en], en-st, fcoarse, ...);
    keep (ct_est, timing_mx, fcoarse, timing_valid) of max timing_mx
coarse_foff = EstFreqOffsetPilotCorr(rxbuf[st..], ct_est, fcoarse) + fcoarse;   // ±20 Hz refine
if timing_valid: nin = ct_est; sample_point = timing_est = 0; foff_est_hz = coarse_foff;
else:            nin = SamplesPerFrame;
```

### 6c. `est_freq_offset_pilot_corr` — fine coarse-freq (`ofdm.c:930-997`)

Integer-Hz DFT-peak search `f ∈ [-20, 20)`. For each `f`: `delta = exp(-j·2πf/fs)`, walk `w` from 1, `csam = wvec_pilot[i]·w`, accumulate `corr_st += rx[ct+i]·csam`, `corr_en += rx[ct+i+SamplesPerFrame]·csam`; pick `f` maximising `|corr_st|+|corr_en|`. `wvec_pilot` uses the same `{−40,0,40}` switch as §6a. Net coarse acquisition range: `fcoarse ∈{−40,0,40}` × fine `∈[−20,20)` ⇒ **−60…+59 Hz**.

---

#### 7. Burst acquisition (preamble/postamble correlator — opt-in, but the "preamble detection" the task asks about)

Entered only if the app calls `freedv_set_frames_per_burst` → `ofdm_set_packets_per_burst` (`freedv_api.c:1423`, `ofdm.c:1165-1169`), which sets `data_mode="burst"`, `postambledetectoren=true`. Then `ofdm_sync_search_core → ofdm_sync_search_burst` (`ofdm.c:1325-1388`). This is the known-sequence joint timing+freq detector — directly reusable for greenfield burst waveforms.

### `est_timing_and_freq` (`ofdm.c:1253-1293`) — joint 2-D search

```csharp
static float EstTimingAndFreq(OfdmModeConfig c, out int tEst, out float foffEst,
    ReadOnlySpan<Cf> rx, int Nrx, ReadOnlySpan<Cf> known /*Npsam*/, int Npsam,
    int tstep, float fmin, float fmax, float fstep) {
    int Ncorr = Nrx - Npsam + 1; float maxCorr = 0; tEst=0; foffEst=0;
    for (float f=fmin; f<=fmax; f+=fstep) {
        float wf = c.Tau*f/c.Fs;
        Span<Cf> mvec = stackalloc Cf[Npsam];
        for (int i=0;i<Npsam;i++) mvec[i] = Conj(known[i]*Cmplx(wf*i)); // conj(known·e^{jwi})
        for (int t=0;t<Ncorr;t+=tstep) {
            float mag = Abs(DotNoConj(rx.Slice(t), mvec, Npsam));
            if (mag>maxCorr){ maxCorr=mag; tEst=t; foffEst=f; }
        }
    }
    // normalised metric (ofdm.c:1281-1286)
    float mag1=0, mag2=0;
    for (int i=0;i<Npsam;i++){ mag1+=Abs(known[i]*Conj(known[i])); mag2+=Abs(rx[i+tEst]*Conj(rx[i+tEst])); }
    return maxCorr*maxCorr / (mag1*mag2 + 1e-12f);
}
```
`known` = the full **preamble modem-frame** time samples (`tx_preamble`, `SamplesPerFrame` long), or `tx_postamble`. Two-stage (`burst_acquisition_detector`, `ofdm.c:1297-1323`): coarse `tstep=4, fstep=5, fmin=−50, fmax=+50`; then refine on a `±ceil(fstep/2)` freq window, `tstep=1, fstep=1`. Preamble and (if `postambledetectoren`) postamble are both correlated; the larger `timing_mx` wins (`1342-1354`). `timing_valid = timing_mx > TimingMxThresh` (`1356`) — note datac4/13/14's higher thresholds (0.45–0.50) are tuned for this correlator. On a **preamble** hit: `nin = SamplesPerFrame + ct_est − 1` (advance past preamble). On a **postamble** hit: back up `rxbufst -= np*SamplesPerFrame; rxbufst += ct_est; nin=0` — decode the just-buffered packet retrospectively.

---

#### 8. Demod core — `ofdm_demod_core` (`ofdm.c:1531-1962`)

Runs once per modem frame while `State ∈ {Trial, Synced}`.

### 8a. Fine timing update (`1548-1582`)
```
woff = 2π·FoffEstHz/Fs
st = rxbufst + SamplesPerSymbol + SamplesPerFrame - floor(ftwindowwidth/2) + timing_est
en = st + SamplesPerFrame - 1 + SamplesPerSymbol + ftwindowwidth
work[j] = rxbuf[i] * cmplxconj(woff·i)                     // de-rotate by current foff; i is ABSOLUTE
ft_est = EstTiming(work, en-st, fcoarse=0, step=1)
timing_est += ft_est - ceil(ftwindowwidth/2) + 1
sample_point = max(timing_est+4, sample_point)             // slew-limited, keep inside CP
sample_point = min(timing_est + Ncp - 4, sample_point)
```
The phase ramp uses the **absolute** rxbuf index `i`, not the local `j` — reproduce exactly (`1563`).

### 8b. Down-convert + DFT the 11-row symbol matrix (`1622-1738`)
`rx_sym` is `(ns+3) × (nc+2)`. Geometry (offsets into rxbuf, all `+1 + sample_point`):
- `rx_sym[0]` (previous pilot): base `rxbufst + SamplesPerSymbol`.
- `rx_sym[1..ns+1]` loop `rr=0..ns`: base `rxbufst + SamplesPerSymbol + SamplesPerFrame + rr·SamplesPerSymbol`. → `rx_sym[1]`=this pilot, `rx_sym[2..ns]`=data rows, `rx_sym[ns+1]`=next pilot.
- `rx_sym[ns+2]` (future pilot): base `rxbufst + SamplesPerSymbol + 3·SamplesPerFrame`.
Each row: `work[k] = rxbuf[j]·cmplxconj(woff·j)` for `j∈[base, base+m)`, then `Dft(rx_sym[row], work)`. For datac (ns=5): rows `[0]=prev, [1]=this, [2..5]=4 data, [6]=next, [7]=future`; array size `ns+3=8`.

### 8c. Frequency-offset tracking (`1747-1769`)
```
freq_err_rect = conj(VectorSum(rx_sym[1], nc+2)) * VectorSum(rx_sym[ns+1], nc+2) + 1e-6
freq_err_hz   = arg(freq_err_rect) * Rs / (2π·Ns)
// datac: foff_limiter=false, so no ±1 Hz clamp
FoffEstHz += 0.1f * freq_err_hz          // foff_est_gain
```
Phase change of the summed pilot vector across one frame (`ns` symbols apart) → Hz.

### 8d. Per-carrier phase & channel estimation (`1771-1861`) — **with the §0 overwrite**
For `i ∈ [1, nc+1)` (interior carriers only):
- **high_bw** (default before lock): `rect = (rx_sym[1][i]·conj(pilots[i]) + rx_sym[ns+1][i]·conj(pilots[i]))/2` — this + next pilot, single carrier, fast Doppler tracking.
- **low_bw** (after `Synced` in voice1; **data streaming never switches** — see §10, so datac stays high_bw): `rect = (Σ over 3 neighbours j=i−1..i+1 of [rx_sym[1]+rx_sym[ns+1]+rx_sym[0]+rx_sym[ns+2]]·conj(pilots[j]))/12` — 4 pilots × 3 neighbours, low-SNR accurate.
- **Then unconditionally (1859-1860):** `aphase_est_pilot[i] = arg(rect); aamp_est_pilot[i] = |rect|`.

So the channel estimate per interior carrier is a complex gain `rect`; equalisation is **phase-only** de-rotation (magnitude carried separately as `rx_amp` for LDPC weighting), not full complex division.

### 8e. Equalise → symbols + hard bits (`1873-1928`)
For `rr ∈ [0, RowsPerFrame)`, `i ∈ [1, nc+1)`:
```
rx_corr = rx_sym[rr+2][i] * cmplxconj(aphase_est_pilot[i])   // coherent; dpsk off for datac
rx_np [rr*nc + (i-1)] = rx_corr                              // phase-corrected data symbol
rx_amp[rr*nc + (i-1)] = aamp_est_pilot[i]
qpsk_demod(rx_corr) -> rx_bits[..] = {abit[1], abit[0]}     // hard bits (test/uncoded only)
```
`qpsk_demod` (`ofdm.c:115-120`): `rot = sym·cmplx(π/4); bit0 = Re(rot)≤0; bit1 = Im(rot)≤0`. `mean_amp = 0.9·mean_amp + 0.1·(Σ amp)/(RowsPerFrame·nc)` (`1932-1933`).

### 8f. Sample-clock tracking (`1937-1961`) — integer nudge, **no resampling**
```
nin = SamplesPerFrame
clock_offset_counter += (prev_timing_est - timing_est)
thresh = SamplesPerSymbol/8;  tshift = SamplesPerSymbol/4
if timing_est >  thresh: nin = SpF + tshift; timing_est -= tshift; sample_point -= tshift
if timing_est < -thresh: nin = SpF - tshift; timing_est += tshift; sample_point += tshift
```
**There is no fractional resampler anywhere in the demod.** Sample-clock offset is absorbed by (a) the slewing integer `timing_est`/`sample_point` inside the CP, and (b) adding/dropping `tshift = SamplesPerSymbol/4` whole samples when timing drifts past `±SamplesPerSymbol/8`. The `clock_offset_counter` only feeds a reported ppm estimate (`ofdm_get_demod_stats`, `2354-2358`). This is important for the greenfield reuse note — you inherit robustness to clock error "for free" without a Farrow interpolator.

---

#### 9. Packet assembly → LLRs (`ofdm_demod.c:457-506`, `mpdecode_core.c:567-650`)

Per decoded frame, the driver maintains a rolling `rx_syms[Nsymsperpacket]` / `rx_amps[...]` buffer:
```
slide left by Nsymsperframe; append RxNp / RxAmp at the tail (ofdm_demod.c:458-465)
st_uw = Nsymsperpacket - Nuwframes*Nsymsperframe;  ExtractUw(rx_syms[st_uw..], rx_uw)
if modem_frame == np-1:                            // full packet buffered
    disassemble -> payload_syms/amps (drops UW-carrying symbols by uw_ind_sym; ofdm.c:2455-2494)
    gp_deinterleave_comp/float(payload_syms/amps)  // golden-prime, §9a
    symbols_to_llrs(llr, syms_de, amps_de, EsNo=3.0, mean_amp, Npayloadsyms)
    ldpc_decode_frame(...)                          // separate component
```
`Nsymsperframe = BitsPerFrame/2`, `Nsymsperpacket = BitsPerPacket/2`.

### 9a. Golden-prime de-interleaver (`gp_interleaver.c`)
`b = next_prime(floor(N/1.62))`; deinterleave: `frame[i] = interleaved[(b·i) mod N]` for `i∈[0,N)`. `N = Npayloadsyms`. Port `is_prime`/`next_prime`/`choose_interleaver_b` verbatim (trial division is fine at these sizes; compute `b` once per mode).

### 9b. `symbols_to_llrs` = Demod2D + Somap (`mpdecode_core.c:567-650`)
```csharp
// Demod2D: per symbol i, per constellation j∈[0,4): S = {(1,0),(0,1),(0,-1),(-1,0)}
tempsr = amp[i]*S[j].Re/meanAmp;  tempsi = amp[i]*S[j].Im/meanAmp;
Er = sym[i].Re/meanAmp - tempsr;  Ei = sym[i].Im/meanAmp - tempsi;
symLik[i*4+j] = -EsNo*(Er*Er + Ei*Ei);
// Somap (bps=2): for each symbol, num[k]=den[k]=-1e6; mask starts at 2 then 1
//   if (mask & j) num[k]=MaxStar0(num[k], symLik[i*4+j]) else den[k]=MaxStar0(den[k], ...)
//   bitLik[2i+k] = num[k]-den[k];   llr[2i+k] = -bitLik[2i+k]
```
`MaxStar0` (`mpdecode_core.c:127-140`): `diff=d2-d1; if diff>TJIAN return d2; if diff<-TJIAN return d1; if diff>0 return d2+AJIAN*(diff-TJIAN); else return d1-AJIAN*(diff+TJIAN)`.
**LLR bit order (interop-critical):** `k=0` (mask=2) is the constellation-index MSB = `bit1` of the qpsk mapping; `k=1` (mask=1) = `bit0`. So `llr[2i+0]↔bit1`, `llr[2i+1]↔bit0`, matching `qpsk_mod`'s `(bits[1]<<1)|bits[0]`. Feed this ordering straight to the LDPC decoder.

`EsNo` is a hard-coded `3.0f` in both the standalone (`ofdm_demod.c:411`) and the FreeDV API (`freedv_700.c:451`) — reproduce the constant; do not "improve" it or interop breaks.

---

#### 10. Sync state machine — data streaming (`ofdm_sync_state_machine_data_streaming`, `ofdm.c:2101-2151`)

Dispatched by `state_machine=="data"` + `data_mode=="streaming"` (`2272-2274`). **This is the datac default.** States `Search/Trial/Synced`:

```
sync_start = sync_end = false
if Search and timing_valid:  sync_start=true; sync_counter=0; -> Trial
uw_errors = Σ (tx_uw[i] ^ rx_uw[i])                          // over nuwbits
if Trial:
    if uw_errors < bad_uw_errors: -> Synced; packet_count=0; modem_frame = nuwframes
    else:                          sync_counter++; if sync_counter > np: -> Search
if Synced:
    modem_frame++
    if modem_frame >= np:
        modem_frame=0; packet_count++
        if packetsperburst != 0 and packet_count >= packetsperburst: -> Search
last_sync_state = sync_state; sync_state = next_state
```
`packetsperburst` defaults to **0** for streaming (`ofdm.c:414`) ⇒ **never loses sync once acquired** — matches the auto-memory note about stream testing. `bad_uw_errors` is the per-mode UW tolerance (6–18, see §1). Note the streaming machine **never sets `phase_est_bandwidth = low_bw`** (only voice1 does, `2069-2071`), so datac runs high-bandwidth phase tracking throughout — reproduce by leaving `PhaseEstBandwidth.High` fixed for these modes.

Also port, for completeness / mode-parameterised reuse:
- **voice1** (`2012-2096`): 3-consecutive-good-frames to lock, `low_bw` after lock, `sync_counter>6` drops — the 700D/2020 machine.
- **data burst** (`2156-2215`): confirm UW after `nuwframes`, reset rxbuf on failure (`uw_fails++`), one-shot postamble loop.
- **voice2** (`2217-2266`).
`ofdm_set_sync` (`2293-2319`) exposes `UN_SYNC/AUTO_SYNC/MANUAL_SYNC` for external control (clears rxbuf on unsync).

---

#### 11. Interop-exactness checklist

1. **`float` throughout the hot path.** codec2 is single-precision (`complex float`, `cosf/sinf/cabsf/cargf`). Using C# `double`/`System.Numerics.Complex` will diverge from FreeDV oracle vectors after a few frames. Use a `Cf{float Re,Im}` struct and `MathF.*`.
2. **`cmplx(x)=cos(x)+j·sin(x)`, `cmplxconj(x)=cos(x)−j·sin(x)`** (`ofdm_internal.h:51-52`). `Cnorm=Re²+Im²`, `Abs=hypotf`, `arg=atan2f`.
3. **Non-conjugating dot product** in correlations (conj is pre-baked into `wvec`).
4. **`roundf` = round-half-away-from-zero** for `RxNlower` (C#'s `MathF.Round` defaults to banker's — pass `MidpointRounding.AwayFromZero`).
5. **The §0 overwrite** (`1859-1860`) — amp/phase are always `|rect|`/`arg(rect)`.
6. **`+1e-6` on `freq_err_rect`** and **`+1e-12`** guards — keep them; they change low-level rounding.
7. **EsNo=3.0f constant.**
8. **Absolute-index phase ramps** (`woff·i`/`woff·j` use rxbuf array indices) in fine timing and down-convert.
9. `ofdm_complex_dot_product` has a vectorised path (`ofdm.c:722-769`) that sums in a different order than the scalar path — the scalar `#else` branch (`776-778`) is the reference; port that ordering. The vectorised path can differ in the last ULP; the FreeDV test vectors are generated with whichever build, so validate against **generated audio→bits**, tolerating ≤1 LSB on LLRs rather than requiring bit-identical floats (see §13).

---

#### 12. Sample-rate bridge

datac is defined at `fs=8000` (`ofdm_mode.c:33`); the pdn-soundmodem channel runs its modems at 12 kHz (`OccupiedBandwidthTests.SampleRate=12000`) or 48 kHz. `freedv_comprx_700c` itself resamples the rig's 8 kHz to the modem rate via `quisk_cfInterpDecim` (referenced at `freedv_700.c:308`). For our stack, put a rational resampler (reuse `Dsp/Decimator` + `Dsp/Upsampler`, or a polyphase 3:2 for 12 k→8 k / 6:1 for 48 k→8 k) in `DatacModem.Process` **before** `OfdmDemodulator`, feeding it exactly 8 kHz complex? — note codec2 feeds **real** samples (`short`/`float`) and the down-conversion to complex happens inside via the `cmplxconj(woff·i)` mixing on a real `rxbuf`. Actually `rxbuf` is `complex float` but fed from real input (`rxbuf[i] = (float)short/32767` — imaginary 0, `ofdm.c:1245, 1521`). So: **feed real audio as `Cf{Re=sample, Im=0}`**; the modem's internal mixers produce the analytic signal. The `rx_bpf` (datac4/13/14) is a complex band-pass tuned to `find_carrier_centre` (`ofdm.c:585-593`) applied to the real→complex input before demod (`ofdm.c:1466-1471, 1535-1539`) — port `filtP200S400` and `quisk_cfTune`/`quisk_ccfFilter`, or approximate with `FilterDesign.BandPass` centred per §1 (flag as a named deviation if not bit-exact).

---

#### 13. Test / oracle strategy (fits the existing `QtsmInteropTests` pattern)

The repo already cross-validates against a reference modem (`tests/.../Modems/QtsmInteropTests.cs`, `NinoTncParityTests.cs`) using checked-in WAV/vector fixtures. Mirror that for datac:
1. **Golden vectors from codec2 `ofdm_mod`/`ofdm_demod`** (or `freedv_data_raw_tx/rx`): generate, per mode, (a) TX audio for a fixed payload, (b) the intermediate `rx_np`/`rx_amp` logs (`ofdm_demod.c` already writes octave logs, `2639-2691`), (c) the LLRs, (d) decoded bytes. Store as fixtures under `samples/datac/`.
2. **Layered asserts:** feed the golden TX audio → assert C# `RxNp`/`RxAmp` match codec2's logged values (≤1e-4 rel), then LLR sign/magnitude match, then decoded bytes are bit-identical. This isolates demod bugs from LDPC bugs.
3. **Acquisition sweep:** inject known `foff ∈ {−55…+55 Hz}` and sample-clock ppm, assert `Search→Trial→Synced` within the expected frame count and correct `FoffEstHz`.
4. **OBW guard** (extend `OccupiedBandwidthTests`) for the TX side.
Do **not** run these on this box now (RAM/CPU constraint) — this is the plan, not an execution.

---

#### 14. What is directly reusable for greenfield FM / HF OFDM

**Reuse as-is (mode-parameterised by `OfdmModeConfig`):**
- The whole **per-symbol DFT/iDFT engine** (§4) — pick any `nc, m` for a new grid (e.g. FM at `fs=48000`, wider carrier spacing, more carriers).
- **Pilot-based timing correlation** `EstTiming` + `TimingNorm` normalisation (§6a) and **coarse-freq DFT-peak** `EstFreqOffsetPilotCorr` (§6c). Generic to any pilot-bearing OFDM frame.
- **Per-carrier pilot phase/channel estimation** (§8d), both `high_bw` (fast, for FM multipath/none) and `low_bw` (low-SNR HF). The `PhaseEstBandwidth` switch is exactly the Doppler-vs-SNR knob a greenfield HF mode wants.
- **Integer sample-clock tracking** (§8f) — no interpolator needed; robust and cheap. Good default for FM too.
- **QPSK soft-demap** `Demod2D`/`Somap`/`MaxStar0` (§9b) and the **golden-prime interleaver** (§9a) — code-agnostic; pair with your own LDPC.
- **`est_timing_and_freq` known-sequence burst detector** (§7) — a generic matched-filter joint timing+freq acquisition; drop in any preamble for a greenfield **burst** mode (FM packet bursts, HF ARQ).
- **The sync state-machine skeleton** (§10) — `Search/Trial/Synced` with UW confirmation is reusable; pick your own `nuwbits`/`bad_uw_errors`/`np`.
- **Es/No & SNR estimators** (`ofdm_esno_est_calc`/`ofdm_snr_from_esno`, `ofdm.c:1967-2007`).

**Must change / decide per greenfield mode (not reusable verbatim):**
- The `{−40,0,+40}` coarse-freq grid and the `wval` 40 Hz table are tuned to HF drift on an 8 kHz/62.5 Hz-Rs grid. For **FM** (crystal-stable, small offset) you can collapse to `fcoarse=0` only. For a **wider-Rs** grid, regenerate the grid spacing.
- LDPC codes (`H_*`) come from codec2/`ax25sdl`-style tables — your own modes need their own codes.
- `rx_bpf`/`tx_bpf` prototypes (`filtP*`) are HF-narrowband specific.
- The `edge_pilots=0` null-edge choice and the exact `pilotvalues` sequence are FreeDV-interop constraints; a greenfield mode can choose its own pilot design (but then it's no longer FreeDV-compatible — keep the two code paths separate).

**Not applicable to FM:** the CP-based timing (FM packet is burst, high SNR) still works but is overkill; the `low_bw` averaging and `foff_limiter` are HF-fade features.

---

#### 15. Items NOT determinable from the source I read (no confabulation)

- **datac4/13/14 payload-vs-codeword mismatch.** `Npayloadsyms` (736 / 192 / 48) is smaller than the LDPC codeword symbol count (1024 / 256 / 56). This implies puncturing/`protection_mode` handled in `ldpc_mode_specific_setup`/`count_errors_protection_mode`, which I did **not** read in full. It's an LDPC-layer concern, out of scope for the demodulator, but the demod→LLR interface must emit exactly `Npayloadbitsperpacket = BitsPerPacket − nuwbits − txtbits` LLRs and let the LDPC component handle framing. Flag for the LDPC subagent.
- **`quisk_ccfFilter`/`quisk_cfTune` exact coefficients** for `rx_bpf` (datac4/13/14) — I read the call sites and `find_carrier_centre` (`ofdm.c:570-596`) but not `filter.c`. If bit-exact RX-BPF matters, read `filter.c` before porting; otherwise approximate with `FilterDesign.BandPass` and record it as a named deviation.
- **Whether real-world datac deployments run streaming or burst.** Source fact: FreeDV API opens datac in **streaming** by default (`freedv_ofdm_data_open` never sets burst); burst is opt-in via `freedv_set_frames_per_burst`. I did not inspect downstream apps (freedata etc.) to see which they call — implement **streaming first** (matches the codec2/FreeDV default and the `ofdm_demod.c` reference driver), add burst behind the same `OfdmModeConfig.DataMode` switch.


## LDPC codec + H-matrix porting

---

### C# LDPC Codec Design — FreeDV `datac*` modes

Source root for every citation below: `/home/tf/.claude/jobs/553c7662/tmp/codec2-ref/src/`. Target: `Packet.SoundModem.Fec.Ldpc` (new namespace under the existing `Fec/` folder), `net10.0`, pure-managed, GPL-3.0-or-later.

#### 1. Mode → code → shortening (all six modes, ground-truthed)

Mapping is **not** in `ldpc_codes.c` (that file is just the code registry `struct LDPC ldpc_codes[]`, lines 29–105); the mode→codename binding is in `ofdm_mode.c:ofdm_init_mode`. The shortening (`data_bits_per_frame`) is computed in `interldpc.c:ldpc_mode_specific_setup` (lines 72–84) as:

```
data_bits = ofdm->bitsperpacket - nuwbits - ntxtbits - NumberParityBits
```

with `bitsperframe = (ns-1)*(nc*bps)`, `bitsperpacket = np*bitsperframe`, `bps=2` (`ofdm.c:297–299`). All datac modes keep the default `protection_mode = LDPC_PROT_2020` (`interldpc.c:61`; the `2020`/`2020B` overrides at lines 74–78 never match a `datac*` name).

| mode | codename (`ofdm_mode.c`) | M=parity | K=data (`NumberRowsHcols`) | N=`CodeLength` | ns,nc,np,nuw | data_bits used | **shortened** | unused (stuffed) | on-air coded bits = data+M |
|---|---|---|---|---|---|---|---|---|---|
| datac0  | `H_128_256_5`   (l.135) | 128  | 128  | 256  | 5,9,4,32   | 128  | no  | 0   | 256  |
| datac1  | `H_4096_8192_3d`(l.158) | 4096 | 4096 | 8192 | 5,27,38,16 | 4096 | no  | 0   | 8192 |
| datac3  | `H_1024_2048_4f`(l.180) | 1024 | 1024 | 2048 | 5,9,29,40  | 1024 | no  | 0   | 2048 |
| datac4  | `H_1024_2048_4f`(l.206) | 1024 | 1024 | 2048 | 5,4,47,32  | **448**  | **YES** | **576** | 1472 |
| datac13 | `H_256_512_4`   (l.233) | 256  | 256  | 512  | 5,3,18,48  | **128**  | **YES** | **128** | 384  |
| datac14 | `HRA_56_56`     (l.260) | 56   | 56   | 112  | 5,4,4,32   | **40**   | **YES** | **16**  | 96   |

Note: the task named datac4/datac13 as the shortened pair, but **datac14 is also shortened** (40/56) by the same mechanism — the design handles all three uniformly. Independent cross-check: data_bits/8 = payload bytes = {16,512,128,56,16,5}, which equals FreeDV's documented {14,510,126,54,14,3} payload + 2-byte CRC. This confirms the arithmetic.

All five codes satisfy `NumberRowsHcols == NumberParityBits == CodeLength/2`, so in `run_ldpc_decoder` (`mpdecode_core.c:480–486`): `shift = (M+K)-N = 0` and, because `NumberRowsHcols != CodeLength`, **`H1 = 1`**. Every datac mode therefore takes the **RA / dual-diagonal (`H1=1, shift=0`)** branch. The design targets exactly that branch (assert it; other branches are dead code for datac).

Decoder scalars from the `ldpc_codes[]` initialisers (`ldpc_codes.c`, e.g. l.80–83) and the `_MAX_ITER` defines in each header: `dec_type = 0`, `q_scale_factor = 1`, `r_scale_factor = 1` (both scale factors are **dead** — the multiplies are commented out in `SumProduct`, `mpdecode_core.c:398/400/423`), `max_iter = 100` for all five codes.

#### 2. Sparse H storage format (exact)

Both arrays are flat `const uint16_t[]`, **column-major, 1-based, 0 = padding**, verified element counts:

- **`H_rows`** — length `M * max_row_weight` (verified: HRA_56_56=168, H_256_512_4=1024, H_128_256_5=640, H_1024_2048_4f=12288, H_4096_8192_3d=36864). Entry `H_rows[p + i*M]` = 1-based **data-column** index of the *i*-th systematic nonzero in parity-check row *p* (0 = none). Only the systematic (data) part is stored; the parity/accumulator part is implicit in the `H1` dual-diagonal.
- **`H_cols`** — length `K * max_col_weight` (verified: 168, 1024, 640, 55296, 53248). Entry `H_cols[c + j*K]` = 1-based **parity-row** index that data-column *c* participates in (0 = none). Only the K data columns are stored (parity columns are implicit).

`_input[]` (N floats) and `_detected_data[]` (N chars) also exist in four of the five `.c` files (all except `H_1024_2048_4f.c`, which has only `H_rows`/`H_cols`) — these are a **built-in decode oracle** (verified lengths: input=N, detected=N). Capture them as test vectors.

#### 3. Matrix-porting plan (transliteration script — nothing hand-copied)

A single generator (`tools/gen-ldpc-tables/gen.py`, ~60 lines, run once at authoring time, output committed) converts the C constants to C#:

**Inputs:** the five `.h`+`.c` pairs (`H_128_256_5`, `H_256_512_4`, `H_1024_2048_4f`, `H_4096_8192_3d`, `HRA_56_56`) and `phi0.c`.

**Algorithm:**
1. From each `.h`, regex the seven defines: `#define {NAME}_(NUMBERPARITYBITS|MAX_ROW_WEIGHT|CODELENGTH|NUMBERROWSHCOLS|MAX_COL_WEIGHT|MAX_ITER)\s+(\d+)`.
2. From each `.c`, slurp and extract each brace body with `re.search(rf'{NAME}_H_rows\[\]\s*=\s*\{{(.*?)\}}', txt, re.S)` (and `_H_cols`, `_input`, `_detected_data`); tokenize the body with `re.findall(r'-?[0-9][0-9.eE+\-]*', body)`.
3. **Assert** `len(H_rows) == M*max_row_weight` and `len(H_cols) == K*max_col_weight` (fail the build otherwise — this is the guardrail that the column-major dims are right).
4. Emit `src/Packet.SoundModem/Fec/Ldpc/LdpcTables.g.cs`: one `internal static class {Name}` per code holding the int consts plus `internal static readonly ushort[] HRows = { … };` / `HCols = { … }` (emit as `ushort` literals, 20/line). Total committed size ≈ the C data (~1.5 MB source; acceptable — matches how `ReedSolomon` embeds tables, and these are read-only static arrays with no per-instance cost).
5. Emit `tests/…/Fec/Ldpc/LdpcOracle.g.cs` with the four codes' `float[] Input` (parse `f` suffix, use `InvariantCulture`) and `byte[] Detected`.
6. **phi0 is hand-ported, not generated** (see §4) — it is small, stable, and branch-structured; transcribe the table verbatim and pin it with a golden test.

A `[Fact]` regenerate-and-diff check (or a checked-in SHA of the `.g.cs`) guards against silent drift if codec2 is ever re-pulled.

#### 4. `Phi0` — exact fixed-point port (`phi0.c`)

The default codec2 build does **not** use the inline `log/exp` `phi0` — `mpdecode_core.c:17–19` includes `phi0.h`, so the table version in `phi0.c` is authoritative. Port it byte-for-byte:

```csharp
internal static class Phi0
{
    // C: #define SI16(f) ((int32_t)(f * (1 << 16)))  — float multiply then truncate-toward-zero.
    private static int Si16(float f) => (int)(f * 65536f);

    public static float Compute(float xf)   // xf >= 0 always (all callers pass fabs/nonneg diffs)
    {
        int x = Si16(xf);
        if (x >= Si16(10.0f)) return 0.0f;                    // phi0.c:16
        if (x >= Si16(5.0f))  return Hi[19 - (x >> 15)];      // phi0.c:20  index 0..9
        if (x >= Si16(1.0f))  return Mid[79 - (x >> 12)];     // phi0.c:45  index 0..63
        return LowTree(x);                                    // phi0.c:176 nested-if binary search
    }
    // Hi[10], Mid[64] = the switch return values in order (transcribed from phi0.c:22-174)
    // LowTree = the exact nested if/else on Si16(0.007812f), Si16(0.088388f), … Si16(0.000086f)
    //           returning the 32 leaf constants (phi0.c:177-284), else 10.0f.
}
```

Bit-exactness hinges on: (a) `Si16` using **float** arithmetic (not double) then truncation, so `>>15`/`>>12` land on the same case boundaries; (b) transcribing all 10+64+32 constants exactly; (c) preserving the `>= 10 → 0`, fall-through `→ 10.0f` edges. Pin with a golden test that samples `xf` across the boundaries {0, 0.000086, 0.007812, 0.088388, 0.25, 0.5, 0.707107, 1.0, 4.9375, 5.0, 9.5, 10.0} and asserts the C outputs.

#### 5. Encoder — RA accumulator (`mpdecode_core.c:encode`, l.68–87)

Pure integer XOR; trivially bit-exact.

```csharp
// ibits length = K (ldpc_data_bits_per_frame, all data incl. stuffed knowns)
// pbits length = M
internal static void Encode(LdpcCode c, ReadOnlySpan<byte> ibits, Span<byte> pbits)
{
    int prev = 0, M = c.NumberParityBits;
    for (int p = 0; p < M; p++)
    {
        int par = 0;
        for (int i = 0; i < c.MaxRowWeight; i++)
        {
            int ind = c.HRows[p + i * M];      // 1-based, 0 = none
            if (ind != 0) par += ibits[ind - 1];
        }
        prev = (par + prev) & 1;               // running accumulator
        pbits[p] = (byte)prev;
    }
}
```

#### 6. Decoder — log-domain sum-product (`mpdecode_core.c`)

**Graph** (built once per `LdpcCode`, cached; depends only on H, not on the received signal). Replicate `init_c_v_nodes` for the `H1=1, shift=0` case exactly (`mpdecode_core.c:142–350`), including socket cross-references and **edge ordering** (systematic H_rows entries first, then the two dual-diagonal parity edges; H_cols order for data v-nodes; accumulator order for parity v-nodes). Preserving order keeps floating-point accumulation identical, which keeps iteration counts and early-termination identical.

```csharp
internal struct CSub { public int Index;  public int Socket; public float Message; }
internal struct VSub { public int Index;  public int Socket; public float Message; public bool Sign; }
internal sealed class CNode { public CSub[] Subs = default!; }             // degree = Subs.Length
internal sealed class VNode { public float InitialValue; public VSub[] Subs = default!; }

public sealed class LdpcDecoder            // one per code, reusable, not thread-safe
{
    private readonly LdpcCode _c;
    private readonly CNode[] _cn;          // length M
    private readonly VNode[] _vn;          // length N
    // ctor builds the Tanner graph (§ below); Decode mutates only Message/Sign/InitialValue.
}
```

Graph construction (the parts that differ from a generic decoder — cite `mpdecode_core.c`):

- **c-node degree** (l.151–167): `count` = nonzero `HRows[i + j*M]`; `degree = (i==0)? count+1 : count+2`.
- **c-node subs** (l.189–211): j∈[0,degree-3) ← `HRows[i+j*M]-1`; for `i==0`, sub[degree-2] ← last systematic; for `i>0`, sub[degree-2] ← `(N-M)+i-1` (prev parity col), sub[degree-1] ← `(N-M)+i` (this parity col).
- **v-node degree** (l.261–288): data cols i∈[0,K): count nonzero `HCols[i+j*K]`; parity cols i∈[K,N): `degree = (i != N-1)? 2 : 1`.
- **v-node subs** (l.296–335): data col → `HCols[i+j*K]-1`; parity col i → `i-K+count`, `count++` (⇒ parity node m connects checks m, m+1; last connects only m). Init each edge: `Message = Phi0(|input[i]|)`, `Sign = input[i] < 0` (dec_type==0 path, l.331/333). Socket = position of the reciprocal edge (l.321–326, l.338–349).

**SumProduct** per decode (`mpdecode_core.c:355–450`), exact update equations:

```csharp
// input: llr[N] (already expanded to full codeword — see §7). Returns iteration count.
public int Decode(ReadOnlySpan<float> input, Span<byte> decoded /*N*/, out int parityCheckCount)
{
    parityCheckCount = 0;
    for (int i = 0; i < _c.CodeLength; i++)          // load initial values + reset edges
    { _vn[i].InitialValue = input[i];
      foreach edge: edge.Message = Phi0(Abs(input[i])); edge.Sign = input[i] < 0; }

    int M = _c.NumberParityBits, N = _c.CodeLength, result = _c.MaxIter;
    for (int iter = 0; iter < _c.MaxIter; iter++)
    {
        decoded.Clear();                              // DecodedBits[i]=0 each pass (l.370)
        int ssum = 0;

        // ---- update r : check-node → variable (l.375-402) ----
        for (int j = 0; j < M; j++)
        {
            var s0 = _cn[j].Subs[0]; ref var v0 = ref _vn[s0.Index].Subs[s0.Socket];
            bool sign = v0.Sign;  float phiSum = v0.Message;
            for (int i = 1; i < _cn[j].Subs.Length; i++)
            { var cp = _cn[j].Subs[i]; ref var vp = ref _vn[cp.Index].Subs[cp.Socket];
              phiSum += vp.Message; sign ^= vp.Sign; }
            if (!sign) ssum++;                                        // even parity satisfied
            for (int i = 0; i < _cn[j].Subs.Length; i++)
            { ref var cp = ref _cn[j].Subs[i]; var vp = _vn[cp.Index].Subs[cp.Socket];
              float m = Phi0.Compute(phiSum - vp.Message);
              cp.Message = (sign ^ vp.Sign) ? -m : m; }              // l.397-400
        }

        // ---- update q : variable → check (l.405-429) ----
        for (int i = 0; i < N; i++)
        {
            float Qi = _vn[i].InitialValue;
            foreach (var vp in _vn[i].Subs) Qi += _cn[vp.Index].Subs[vp.Socket].Message;
            if (Qi < 0) decoded[i] = 1;                              // hard decision
            for (int k = 0; k < _vn[i].Subs.Length; k++)
            { ref var vp = ref _vn[i].Subs[k];
              float t = Qi - _cn[vp.Index].Subs[vp.Socket].Message;
              vp.Message = Phi0.Compute(Abs(t));
              vp.Sign = t <= 0; }                                    // C: sign = !(t>0)
        }

        // ---- early termination (l.431-446) ----
        // QUIRK: reference compares first N-M decoded bits against data[]≡0 (data_int is
        //        CALLOC'd zeros in run_ldpc_decoder, l.503, never set). Replicate: halt if the
        //        decoded DATA bits are all zero. Rarely fires except the all-zero codeword,
        //        but affects the returned iter count — keep it for interop parity.
        bool allZeroData = true;
        for (int i = 0; i < N - M; i++) if (decoded[i] != 0) { allZeroData = false; break; }
        if (allZeroData) { result = iter + 1; break; }
        parityCheckCount = ssum;                                     // only set on this path (l.442)
        if (ssum == M) { result = iter + 1; break; }                // syndrome check → converged
    }
    return result;
}
```

Faithful subtleties to preserve for interop parity: (1) `parityCheckCount` is written **only** in the syndrome branch, matching the C (`out` semantics: leave prior value if the all-zero-data branch trips first); (2) `Sign` uses `t <= 0` because C is `if (temp_sum>0) sign=0 else sign=1`; (3) c→v `Message` carries a sign, v→c `Message` is nonnegative with a separate `Sign` (Gallager φ-domain); (4) messages are re-initialised from `input` at the start of each `Decode` call (the C re-inits via `init_c_v_nodes` per `run_ldpc_decoder` call).

#### 7. Shortening wrapper (`interldpc.c`, `LDPC_PROT_2020` branch)

One class handles all six modes; the non-shortened modes fall out as the degenerate `unused == 0` case.

```csharp
public sealed class LdpcFrameCodec               // constructed per datac mode
{
    private readonly LdpcCode _c;
    private readonly int _dataBits;              // data_bits_per_frame (Table §1)
    public int DataBits => _dataBits;
    public int CodedBits => _dataBits + _c.NumberParityBits;   // on-air payload bits
    private int Unused => _c.NumberRowsHcols - _dataBits;      // ldpc_data_bits - data_bits

    // ldpc_encode_frame / LDPC_PROT_2020 (interldpc.c:103-137)
    public void Encode(ReadOnlySpan<byte> data /*_dataBits*/, Span<byte> codeword /*CodedBits*/)
    {
        Span<byte> padded = stackalloc/rent [_c.NumberRowsHcols];
        data.CopyTo(padded);
        for (int i = _dataBits; i < _c.NumberRowsHcols; i++) padded[i] = 1;   // stuff known 1s
        Span<byte> pbits = ...[_c.NumberParityBits];
        LdpcEncoder.Encode(_c, padded, pbits);
        data.CopyTo(codeword);                                    // data bits …
        pbits.CopyTo(codeword[_dataBits..]);                      // … then parity (knowns not sent)
    }

    // ldpc_decode_frame / LDPC_PROT_2020 (interldpc.c:170-186)
    public int Decode(ReadOnlySpan<float> llr /*CodedBits*/, Span<byte> outData /*_dataBits*/,
                      out int parityCheckCount)
    {
        Span<float> full = ...[_c.CodeLength];                    // ldpc_coded_bits_per_frame = N
        for (int i = 0; i < _dataBits; i++) full[i] = llr[i];
        for (int i = _dataBits; i < _c.NumberRowsHcols; i++) full[i] = -100.0f;  // known bit=1
        for (int i = _c.NumberRowsHcols; i < _c.CodeLength; i++) full[i] = llr[i - Unused];
        Span<byte> hard = ...[_c.CodeLength];
        int iter = _decoder.Decode(full, hard, out parityCheckCount);
        hard[.._dataBits].CopyTo(outData);                        // payload = data bits
        return iter;
    }
}
```

LLR sign convention (fixes the whole interface): `ldpc_enc.c:131–135` maps bit→soft as `1.0 - 2.0*bit` (bit 0 → +1, bit 1 → −1), and the decoder decides `bit = (Qi < 0)`. So **positive LLR ⇒ bit 0, negative ⇒ bit 1**, and the known stuffed bit `1` maps to a strongly-negative `-100.0f`. The demodulator upstream must deliver `CodedBits` LLRs in that polarity (the codec2 demod path `symbols_to_llrs`/`sd_to_llr` in `mpdecode_core.c:530–650` produces exactly this; that stage belongs to the QPSK demod, not this codec, but the polarity contract is fixed here).

#### 8. Public surface & registry

```csharp
public sealed record LdpcCode(string Name, int CodeLength, int NumberParityBits,
    int NumberRowsHcols, int MaxRowWeight, int MaxColWeight, int MaxIter,
    ushort[] HRows, ushort[] HCols);

public static class LdpcCodes            // one static instance per code, from LdpcTables.g.cs
{ public static readonly LdpcCode H_128_256_5, H_256_512_4, H_1024_2048_4f,
        H_4096_8192_3d, HRA_56_56; }

public enum DatacMode { Datac0, Datac1, Datac3, Datac4, Datac13, Datac14 }
public static class DatacLdpc            // binds mode → (code, dataBits) per Table §1
{ public static LdpcFrameCodec Create(DatacMode m); }
```

#### 9. Test / oracle plan

1. **phi0 golden** — boundary sweep vs `phi0.c` values (§4).
2. **Encoder golden** — for each shortened mode, feed a fixed data pattern; assert the parity bits match a C `encode()` run (or the RA recurrence recomputed independently). Round-trip: `Encode` then a clean-channel `Decode` (map bits→±large LLR) recovers the data.
3. **Built-in decode oracle** — for `H_128_256_5`, `H_256_512_4`, `H_4096_8192_3d`, `HRA_56_56`: feed the ported `_input[]` (N LLRs) to `LdpcDecoder.Decode` and assert the hard-decision output equals `_detected_data[]` (N bits) and that it converges (`iter < MaxIter`). `H_1024_2048_4f` has **no** built-in vector (stated in the source — the `.c` carries only `H_rows`/`H_cols`); cover datac3/datac4 via encode→noise→decode round-trips instead.
4. **Dimension guard** — the generator's `M*max_row_weight` / `K*max_col_weight` asserts (§3) run at codegen; a runtime test re-checks `HRows.Length`/`HCols.Length`.
5. **Shortening bit-exactness** — datac4 (448/1024, 576 stuffed), datac13 (128/256, 128 stuffed), datac14 (40/56, 16 stuffed): assert `Encode` emits exactly `dataBits + M` bits and that the stuffed positions get value 1 internally; assert `Decode` sets `full[dataBits..K] = -100f` and reindexes parity by `-Unused`.

#### 10. Things NOT determinable from the source I read (flagged, not confabulated)

- The **absolute floating-point identity** of decoder intermediates across C `float` and C# `float`: the algorithm, φ0 table, edge ordering, and iteration/termination logic are fully specified above, so **decoded bits and iteration counts** will match. I did not attempt to prove IEEE-bit-identical intermediate sums (both are 32-bit IEEE-754; sum ordering is preserved, so they should agree, but I cannot assert it without running — and running is barred).
- The **demodulator's LLR scaling** (`sd_to_llr` `estEsN0`, `symbols_to_llrs` `EsNo`/`mean_amp`) lives in the QPSK demod, outside this codec; I fixed only the polarity contract, not the scale, because the datac RX scale factor comes from the OFDM demod path I was not asked to design here.
- `H_1024_2048_4f` (datac3/datac4) ships **without** an `_input`/`_detected_data` oracle vector — confirmed by the array inventory, so those two modes cannot use the built-in-vector test and must rely on round-trip tests.


## Framing — interleaver, unique word, preamble, CRC, shortening

---

### C# FRAMING LAYER — FreeDV OFDM `datac{0,1,3,4,13,14}` (interop-exact)

All facts below are cited to the codec2 shallow clone at `/home/tf/.claude/jobs/553c7662/tmp/codec2/src`. Where a value is *derived* rather than literal in the source, I give the derivation and mark it. Nothing here is confabulated; the "Not in source / assumptions" section at the end lists everything I could not read directly.

Proposed home in pdn-soundmodem, matching the `Fec/`, `Il2p/`, `Fx25/` subdir + `Packet.SoundModem.<Sub>` namespace convention: a new **`FreeDv/`** folder → namespace `Packet.SoundModem.FreeDv`. The four framing types are `FreeDvCrc16`, `GoldenPrimeInterleaver`, `DatacModeParams` (the per-mode table incl. UW), and `DatacBurstFramer`. LDPC encode/decode, QPSK carrier mapping, pilot/IDFT and the actual OFDM modulator are **separate components** (I flag the exact boundary).

---

#### 1. The interop-exact framing pipeline

The datac TX path is `freedv_rawdatacomptx` → `freedv_comptx_ofdm` → `ofdm_ldpc_interleave_tx`. Order is load-bearing — **interleave happens on QPSK symbols AFTER LDPC+QPSK-mod and BEFORE UW insertion** (`interldpc.c:322` `ofdm_ldpc_interleave_tx`, lines 333–339):

```
ldpc_encode_frame  →  qpsk_modulate_frame  →  gp_interleave_comp  →  ofdm_assemble_qpsk_modem_packet_symbols  →  ofdm_txframe
```

Full TX chain, per **packet** (one LDPC codeword):

1. **App payload**: `payloadBytes[payloadBytesPerFrame]` (see §2 table).
2. **CRC** (`freedv_data_raw_tx.c:379-382`): `crc = FreeDvCrc16.Compute(payloadBytes)`; append big-endian → `frameBytes = payload ++ [crc>>8, crc&0xff]`, total `bytes_per_modem_frame` = `data_bits_per_frame/8`.
3. **Unpack MSB-first** (`freedv_api.c:433 freedv_unpack`, called from `freedv_rawdatacomptx:472`): `frameBytes` → `dataBits[data_bits_per_frame]`. This bit array is `f->tx_payload_bits`.
4. **LDPC encode** (separate component; `interldpc.c:88 ldpc_encode_frame`): produces `codeword[coded_bits_per_frame]` = `dataBits ++ parityBits`. Layout is data-then-parity (`interldpc.c:135-136`). (`coded = data + NumberParityBits`.)
5. **QPSK modulate** (`interldpc.c:139 qpsk_modulate_frame`): symbol `i` from `dibit[1]=codeword[2i]`, `dibit[0]=codeword[2i+1]`. → `payloadSyms[coded_bits/2]`.
6. **Golden-prime interleave** (§4): `gp_interleave_comp(payloadSymsInter, payloadSyms, N=coded_bits/2)`.
7. **UW insertion** (§5, `ofdm.c:2412 ofdm_assemble_qpsk_modem_packet_symbols`): weave `Nuwsyms = nuwbits/2` UW symbols into the packet at the `uw_ind_sym[]` positions; interleaved payload symbols fill the rest. → `txSymLin[Nsymsperpacket = bitsperpacket/2]`. (`ntxtbits = 0` for every datac mode, so the txt-bit tail loop is empty.)
8. **OFDM modulate** (separate component; `ofdm.c:1004 ofdm_txframe`): map `txSymLin` onto data carriers, insert pilot rows, IDFT+CP → `samples[np*samplesperframe]`.

RX is the mirror: `ofdm_extract_uw` (sync/verify) → `ofdm_disassemble_qpsk_modem_packet` (drop UW symbol positions) → `gp_deinterleave_comp` → LDPC decode → pack MSB-first → `freedv_check_crc16_unpacked` (`freedv_api.c:455`, compares last-16-bits big-endian to CRC recomputed over the leading payload).

**Burst** (`freedv_data_raw_tx.c:370-405`): `preamble | packet[0] | … | packet[K-1] | postamble | inter-burst silence`. See §6.

---

#### 2. Per-mode parameter table (all values derived from `ofdm_mode.c` + the LDPC `.h` dims)

Constants read literally from `ofdm_mode.c` (`nc, ns, np, nuwbits, codename`) and the code headers (`H_128_256_5.h:9` etc. → `NumberParityBits`). Derived columns use the exact formulas: `bitsperframe=(ns-1)·nc·2` (`ofdm.c:297`), `bitsperpacket=np·bitsperframe` (`ofdm.c:299`), `coded=bitsperpacket−nuwbits` (since `ntxtbits=0`, from `interldpc.c:81-83` + `ldpc_encode_frame`), `data=bitsperpacket−nuwbits−parity` (`interldpc.c:81-82`), `N=coded/2`, `payloadBytes=(data−16)/8`.

| mode | nc | ns | np | bits/frame | **bits/packet** | nuwbits | Nuwsyms | parity (code) | **data bits** | **coded bits** | **N (interleaver)** | **b** | payload bytes | frame bytes |
|------|----|----|----|-----------|-----------------|---------|---------|---------------|---------------|----------------|---------------------|-------|---------------|-------------|
| datac0  | 9  | 5 | 4  | 72  | 288  | 32 | 16 | 128 (H_128_256_5)    | 128  | 256  | 128  | 83   | 14  | 16  |
| datac1  | 27 | 5 | 38 | 216 | 8208 | 16 | 8  | 4096 (H_4096_8192_3d)| 4096 | 8192 | 4096 | 2531 | 510 | 512 |
| datac3  | 9  | 5 | 29 | 72  | 2088 | 40 | 20 | 1024 (H_1024_2048_4f)| 1024 | 2048 | 1024 | 641  | 126 | 128 |
| datac4  | 4  | 5 | 47 | 32  | 1504 | 32 | 16 | 1024 (H_1024_2048_4f)| 448  | 1472 | 736  | 457  | 54  | 56  |
| datac13 | 3  | 5 | 18 | 24  | 432  | 48 | 24 | 256 (H_256_512_4)    | 128  | 384  | 192  | 127  | 14  | 16  |
| datac14 | 4  | 5 | 4  | 32  | 128  | 32 | 16 | 56 (HRA_56_56)       | 40   | 96   | 48   | 31   | 3   | 5   |

Note datac4/13/14 are **shortened** codes: `data < K` (the code's `NumberRowsHcols`), so the LDPC encoder pads unused data bits to `1` before encoding but transmits only `data + parity` bits (`interldpc.c:103-110` `LDPC_PROT_2020` path — the datac modes keep `protection_mode` from `set_up_ldpc_constants:61`, then `ldpc_mode_specific_setup:80-83` re-derives `data_bits_per_frame` from OFDM capacity). datac0/1/3 have `data == K` (full codeword). This padding detail belongs to the **LDPC component**, but the framing layer must expose `data`/`coded`/`N` per the table so both sides agree.

```csharp
public sealed record DatacModeParams(
    string Name, int Nc, int Ns, int Np, int NuwBits,
    int DataBits, int CodedBits, int ParityBits,
    ushort[] UwBits, int[] UwIndSym, int InterleaverB)
{
    public int BitsPerFrame  => (Ns - 1) * Nc * 2;
    public int BitsPerPacket => Np * BitsPerFrame;              // ofdm.c:297,299
    public int SymsPerPacket => BitsPerPacket / 2;              // total, incl UW
    public int NuwSyms       => NuwBits / 2;
    public int PayloadSyms   => CodedBits / 2;                  // == interleaver N
    public int PayloadBytes  => (DataBits - 16) / 8;            // last 16 bits = CRC
    public int FrameBytes    => DataBits / 8;
    public static DatacModeParams Get(string mode) => Table[mode];
    public static readonly IReadOnlyDictionary<string, DatacModeParams> Table = /* the 6 rows above */;
}
```

---

#### 3. CRC-16 — `freedv_gen_crc16`

Source `freedv_api.c:1616-1628`:

```c
unsigned short freedv_gen_crc16(unsigned char *data_p, int length) {
  unsigned char x; unsigned short crc = 0xFFFF;
  while (length--) {
    x = crc >> 8 ^ *data_p++;
    x ^= x >> 4;
    crc = (crc << 8) ^ ((unsigned short)(x << 12)) ^ ((unsigned short)(x << 5)) ^ ((unsigned short)x);
  }
  return crc;
}
```

This is **CRC-16/CCITT-FALSE** (aka CRC-16/IBM-3740): poly `0x1021`, init `0xFFFF`, `refin=false`, `refout=false`, `xorout=0x0000`. **It is NOT the existing `Fec/Crc16X25`** (that one is reflected `0x8408`, xorout `0xFFFF`). A new class is required. Placement of the 16-bit result: big-endian in the last two payload bytes; CRC computed over the *payload only* (`freedv_data_raw_tx.c:379-382`, `payload_bytes_per_modem_frame = bytes_per_modem_frame - 2` at `:300-302`).

Port literally (the C uses a nibble-sliced byte-at-a-time form; keep it exact — a table version must reproduce the same values):

```csharp
namespace Packet.SoundModem.FreeDv;

/// <summary>
/// CRC-16/CCITT-FALSE (poly 0x1021, init 0xFFFF, no reflection, xorout 0x0000).
/// Bit-exact port of codec2 freedv_gen_crc16 (src/freedv_api.c:1616). Distinct from
/// Fec.Crc16X25 (reflected, xorout 0xFFFF) — do not substitute. FreeDV raw-data modes
/// carry this over the payload bytes, big-endian, in the last two bytes of each frame.
/// </summary>
public static class FreeDvCrc16
{
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (byte d in data)
        {
            byte x = (byte)((crc >> 8) ^ d);
            x ^= (byte)(x >> 4);
            crc = (ushort)((crc << 8) ^ (ushort)(x << 12) ^ (ushort)(x << 5) ^ x);
        }
        return crc;
    }

    /// <summary>Appends the 2-byte big-endian CRC. Input = payload only; output length = payload+2.</summary>
    public static byte[] AppendTo(ReadOnlySpan<byte> payload)
    {
        ushort crc = Compute(payload);
        var frame = new byte[payload.Length + 2];
        payload.CopyTo(frame);
        frame[^2] = (byte)(crc >> 8);
        frame[^1] = (byte)(crc & 0xFF);
        return frame;
    }

    /// <summary>RX check: last 2 bytes are the received CRC (freedv_api.c:455-463).</summary>
    public static bool Check(ReadOnlySpan<byte> frameWithCrc)
    {
        ushort rx = (ushort)((frameWithCrc[^2] << 8) | frameWithCrc[^1]);
        return rx == Compute(frameWithCrc[..^2]);
    }
}
```

MSB-first pack/unpack between bytes and the LDPC bit array (`freedv_api.c:419 freedv_pack` / `:433 freedv_unpack`) — bit 7 first, exactly like AX.25/IL2P byte order already used elsewhere in the repo:

```csharp
public static void UnpackMsbFirst(ReadOnlySpan<byte> bytes, Span<byte> bits) { // freedv_unpack
    for (int i = 0; i < bits.Length; i++) bits[i] = (byte)((bytes[i >> 3] >> (7 - (i & 7))) & 1);
}
public static void PackMsbFirst(ReadOnlySpan<byte> bits, Span<byte> bytes) {   // freedv_pack
    bytes.Clear();
    for (int i = 0; i < bits.Length; i++) bytes[i >> 3] |= (byte)(bits[i] << (7 - (i & 7)));
}
```

---

#### 4. Golden-prime interleaver — `gp_interleaver.c`

The datac path uses the **complex-symbol** interleaver `gp_interleave_comp` (`gp_interleaver.c:62`), operating over `N = coded_bits/2` symbols. **Do not use `gp_interleave_bits`** — that variant (`:103`) is only used by `reliable_text.c`, not by datac.

Index formula (`gp_interleaver.c:56-69`):

```c
int choose_interleaver_b(int Nbits) { int b = floor(Nbits / 1.62); b = next_prime(b); return b; }
// interleave:   interleaved[(b*i) % N] = frame[i]   for i in 0..N-1
// deinterleave: frame[i] = interleaved[(b*i) % N]   for i in 0..N-1
```

**Interop gotcha — the constant is the literal `1.62`, NOT the golden ratio φ=1.6180339887** (`gp_interleaver.c:57`). The header comment says "Golden section" but the code uses `1.62`; a "correction" to φ silently breaks interop. `next_prime` returns the smallest prime *strictly greater* than its argument (`:50-54`, it does `x++` first), and `is_prime` is trial division over `[2, x)` (`:43-48`).

`b` per mode (hand-derived from the exact algorithm; verify in a unit test against the ported `ChooseB`):

| N | 128 | 4096 | 1024 | 736 | 192 | 48 |
|---|-----|------|------|-----|-----|----|
| floor(N/1.62) | 79 | 2528 | 632 | 454 | 118 | 29 |
| **b = next_prime** | **83** | **2531** | **641** | **457** | **127** | **31** |

```csharp
public sealed class GoldenPrimeInterleaver
{
    private readonly int _n, _b;
    public GoldenPrimeInterleaver(int n) { _n = n; _b = ChooseB(n); }

    public static int ChooseB(int nBits) => NextPrime((int)Math.Floor(nBits / 1.62)); // gp_interleaver.c:56
    private static bool IsPrime(int x) { for (int i = 2; i < x; i++) if (x % i == 0) return false; return true; }
    private static int NextPrime(int x) { do { x++; } while (!IsPrime(x)); return x; }

    /// <summary>interleaved[(b*i) % N] = frame[i]  (gp_interleave_comp, gp_interleaver.c:62)</summary>
    public void Interleave(ReadOnlySpan<Complex> frame, Span<Complex> interleaved)
    {
        for (int i = 0; i < _n; i++) interleaved[(int)(((long)_b * i) % _n)] = frame[i];
    }

    /// <summary>frame[i] = interleaved[(b*i) % N]  (gp_deinterleave_comp, gp_interleaver.c:71)</summary>
    public void Deinterleave(ReadOnlySpan<Complex> interleaved, Span<Complex> frame)
    {
        for (int i = 0; i < _n; i++) frame[i] = interleaved[(int)(((long)_b * i) % _n)];
    }
}
```

(`b*i` max = 2531·4095 ≈ 1.04e7, well within int32, but I widen to `long` before the `%` so a future larger N can never wrap; results are identical for these six modes.)

---

#### 5. Unique-word tables + insertion

### 5a. Per-mode UW bit arrays (`tx_uw`, length `nuwbits`)

Constructed in `ofdm_mode.c` by `memset(tx_uw,0,MAX_UW_BITS)` (`:54`, `MAX_UW_BITS=64` from `ofdm_internal.h:49`) then one or two `memcpy`s of a base pattern. The base 16-bit pattern is `A = {1,1,0,0,1,0,1,0,1,1,1,1,0,0,0,0}`; the 24-bit pattern is `B = {1,1,0,0,1,0,1,0,1,1,1,1,0,0,0,0,1,1,1,1,0,0,0,0}` (`B[0..15]==A`). Resolving each mode's memcpy overlaps exactly:

- **datac0** (`:130-137`, nuwbits=32): `copy A@0`; rest stays zero →
  `1,1,0,0,1,0,1,0,1,1,1,1,0,0,0,0, 0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0`
- **datac1** (`:153-161`, nuwbits=16): `copy A@0` (asserts `sizeof==nuwbits`) →
  `1,1,0,0,1,0,1,0,1,1,1,1,0,0,0,0`
- **datac3** (`:182-188`, nuwbits=40): `copy B@0`, `copy B@16` (overlap 16..23 → second wins) →
  `1,1,0,0,1,0,1,0,1,1,1,1,0,0,0,0, 1,1,0,0,1,0,1,0,1,1,1,1,0,0,0,0,1,1,1,1,0,0,0,0`
- **datac4** (`:208-214`, nuwbits=32): `copy B@0`, `copy B@8` (overlap 8..23 → second wins) →
  `1,1,0,0,1,0,1,0, 1,1,0,0,1,0,1,0,1,1,1,1,0,0,0,0,1,1,1,1,0,0,0,0`
- **datac13** (`:235-241`, nuwbits=48): `copy B@0`, `copy B@24` (no overlap) → `B ++ B`:
  `1,1,0,0,1,0,1,0,1,1,1,1,0,0,0,0,1,1,1,1,0,0,0,0, 1,1,0,0,1,0,1,0,1,1,1,1,0,0,0,0,1,1,1,1,0,0,0,0`
- **datac14** (`:262-268`, nuwbits=32): identical construction to datac4 →
  `1,1,0,0,1,0,1,0, 1,1,0,0,1,0,1,0,1,1,1,1,0,0,0,0,1,1,1,1,0,0,0,0`

Ship these as literal `ushort[]` constants (don't reproduce the C memcpy-overlap logic — bake the resolved arrays and assert their length equals `NuwBits`).

### 5b. UW → symbol mapping

`ofdm.c:475-480`: UW symbol `s` from `dibit[1]=tx_uw[2s]`, `dibit[0]=tx_uw[2s+1]`, then `qpsk_mod`. Constellation `qpsk[] = {1+0j, 0+1j, 0−1j, −1+0j}` indexed by `(bits[1]<<1)|bits[0]` (`ofdm.c:76,106`). So each UW dibit `(a,b)=(tx_uw[2s],tx_uw[2s+1])` maps: `(0,0)→1`, `(0,1)→j`, `(1,0)→−j`, `(1,1)→−1`. Worked example, datac1 UW → `{−1, 1, −j, −j, −1, −1, 1, 1}`.

```csharp
public static readonly Complex[] Qpsk =        // ofdm.c:76
    { new(1,0), new(0,1), new(0,-1), new(-1,0) };
public static Complex QpskMod(int b1, int b0) => Qpsk[(b1 << 1) | b0];   // ofdm.c:106
public static Complex[] UwSymbols(ushort[] uw) {                         // ofdm.c:475
    var s = new Complex[uw.Length / 2];
    for (int i = 0; i < s.Length; i++) s[i] = QpskMod(uw[2*i], uw[2*i+1]);
    return s;
}
```

### 5c. UW symbol positions `uw_ind_sym[]`

`ofdm.c:445-463`. All arithmetic is **C integer** (the `floorf` wraps an already-integer int-division result, so it is a no-op — do not use floating division):

```
nuwsyms = nuwbits/2;   Ndatasymsperframe = (ns-1)*nc;   uw_step = nc+1;
if ( (nuwsyms*uw_step)/2 >= np*Ndatasymsperframe ) uw_step = nc-1;   // integer div
for i in 0..nuwsyms-1:  uw_ind_sym[i] = ((i+1)*uw_step) / 2;         // integer div (truncate)
```

For all six modes the default `uw_step = nc+1` is kept (the `>=` branch never fires — I checked each). Resolved tables (`b0`-based symbol indices into the packet's data-symbol raster):

| mode | uw_step | uw_ind_sym |
|------|---------|-----------|
| datac0  | 10 | 5,10,15,20,25,30,35,40,45,50,55,60,65,70,75,80 |
| datac1  | 28 | 14,28,42,56,70,84,98,112 |
| datac3  | 10 | 5,10,15,…,100 (step 5, 20 entries) |
| datac4  | 5  | 2,5,7,10,12,15,17,20,22,25,27,30,32,35,37,40 |
| datac13 | 4  | 2,4,6,8,…,48 (step 2, 24 entries) |
| datac14 | 5  | 2,5,7,10,12,15,17,20,22,25,27,30,32,35,37,40 (= datac4) |

(`ofdm.c:461` also derives bit-level `uw_ind[j]=val*2+b`; framing at the symbol level only needs `uw_ind_sym`.) The RX-side sync window `nuwframes = ceil((uw_ind_sym[last]+1)/((ns-1)*nc))` (`ofdm.c:466-468`) works out to `{datac0:3, datac1:2, datac3:3, datac4:3, datac13:5, datac14:3}` — needed only for RX UW extraction, not TX.

### 5d. Insertion (`ofdm_assemble_qpsk_modem_packet_symbols`, `ofdm.c:2412`)

```csharp
/// Weave UW symbols into interleaved payload at uw_ind_sym positions → SymsPerPacket symbols.
/// ntxtbits == 0 for all datac modes, so there is no txt-symbol tail. (ofdm.c:2431-2437)
public static Complex[] AssemblePacket(DatacModeParams p, ReadOnlySpan<Complex> interleavedPayload)
{
    Complex[] uw = UwSymbols(p.UwBits);
    var packet = new Complex[p.SymsPerPacket];   // BitsPerPacket/2
    int u = 0, pay = 0;
    for (int s = 0; s < p.SymsPerPacket; s++)
        packet[s] = (u < uw.Length && s == p.UwIndSym[u]) ? uw[u++] : interleavedPayload[pay++];
    // invariants (ofdm.c:2439-2440): u == NuwSyms, pay == PayloadSyms
    return packet;
}
```
Disassembly (RX) is the same loop dropping the `uw_ind_sym` positions into a `PayloadSyms`-length codeword-symbol buffer (`ofdm.c:2455-2484`), then `Deinterleave`.

---

#### 6. Burst framing

Structure (`freedv_data_raw_tx.c:370-405` testframe path is the general one; the stdin path `:411-430` is the same with framesPerBurst=1):

```
per burst:  PREAMBLE  ·  packet[0] … packet[K-1]  ·  POSTAMBLE  ·  inter-burst silence
```

- **Preamble/postamble** are each exactly **one OFDM modem frame** (`samplesperframe`), not a full packet. Built once at open time (`ofdm.c:533-538`): `tx_preamble = ofdm_generate_preamble(seed=2)`, `tx_postamble = ofdm_generate_preamble(seed=3)`. On transmit they are copied and run through `ofdm_hilbert_clipper` (`freedv_api.c:538-544`, `:567-573`).
- **Preamble generation** (`ofdm.c:2592-2608`): take a fresh OFDM config with `np=1` (so `bitsperpacket = bitsperframe`), fill `bitsperframe` pseudo-random bits, and OFDM-modulate them raw via `ofdm_mod` (no UW, no LDPC — straight bits→QPSK→frame). It sets `amp_scale=1, tx_bpf_en=false, clip_en=false` for the *stored* copy.
- **PRNG** (`ofdm.c:2574-2578`, `ofdm_rand_seed`): a 15-bit LCG, then threshold:
  ```
  seed = (1103515245L * seed + 12345) % 32768;   r[i] = seed;   bit[i] = r[i] > 16384 ? 1 : 0;
  ```
  Seeds: **2** (preamble), **3** (postamble). `seed` is 64-bit during the multiply; `1103515245*32767+12345` fits in int64, so port with `long`.
- **Inter-burst silence** (`freedv_data_raw_tx.c:395-404`): user `inter_burst_delay_ms` at 8000 Hz, else `2 * n_nom_modem_samples` (= `2*samplesperframe`) of zeros.
- **Per-packet CRC**: computed and appended per frame inside the burst (`:379-382`), i.e. the CRC is per LDPC packet, not per burst.

Framing-layer API (waveform synthesis of preamble/postamble and the packet delegate to the modem/engine component; the framer owns structure, seeds, and CRC):

```csharp
public sealed class DatacBurstFramer
{
    private readonly DatacModeParams _p;
    private readonly IDatacModulator _modem;   // OFDM engine: ModulatePacket / ModulatePreambleBits
    public DatacBurstFramer(DatacModeParams p, IDatacModulator modem) { _p = p; _modem = modem; }

    // ofdm.c:2574 — LCG PRNG bits for preamble(seed 2) / postamble(seed 3)
    public static byte[] PreambleBits(int nBits, long seed) {
        var bits = new byte[nBits];
        for (int i = 0; i < nBits; i++) { seed = (1103515245L * seed + 12345) % 32768; bits[i] = (byte)(seed > 16384 ? 1 : 0); }
        return bits;
    }

    public float[] BuildBurst(IReadOnlyList<byte[]> payloads /* each PayloadBytes long */, int interBurstSilenceSamples)
    {
        var outp = new List<float>();
        outp.AddRange(_modem.ModulatePreambleBits(PreambleBits(_p.BitsPerFrame, 2)));       // 1 frame
        foreach (var payload in payloads)
        {
            byte[] frame = FreeDvCrc16.AppendTo(payload);          // payload + 2-byte BE CRC
            var dataBits = new byte[_p.DataBits];
            FreeDvCrc16.UnpackMsbFirst(frame, dataBits);           // freedv_unpack
            outp.AddRange(_modem.ModulatePacket(dataBits));        // LDPC→QPSK→interleave→UW→OFDM
        }
        outp.AddRange(_modem.ModulatePreambleBits(PreambleBits(_p.BitsPerFrame, 3)));       // postamble
        outp.AddRange(new float[interBurstSilenceSamples]);
        return outp.ToArray();
    }
}
```

---

#### 7. Suggested test vectors (interop cross-checks, all computable offline)

1. **CRC**: `FreeDvCrc16.Compute` — CCITT-FALSE check value: `"123456789"` → `0x29B1` (standard for this variant; verify once, then it locks the port).
2. **Interleaver b**: assert `GoldenPrimeInterleaver.ChooseB(N)` == the §4 table for `N∈{128,4096,1024,736,192,48}`.
3. **UW arrays**: assert each `UwBits.Length == NuwBits` and equals the §5a literals; assert datac4==datac14 UW arrays; assert `UwIndSym` equals §5c.
4. **Round-trip**: `Deinterleave(Interleave(x)) == x`; `AssemblePacket` then disassemble recovers the interleaved payload; `FreeDvCrc16.Check(AppendTo(payload))` true, and flips false on any bit change.
5. When the modem component lands, the authoritative oracle is `freedv_data_raw_tx`/`_rx` byte-for-byte (Phase-1 task #2) — this framing layer's outputs (`dataBits`, interleaved symbols, `txSymLin`) are all directly comparable to the C intermediates.

---

#### 8. Not in source / assumptions (explicit)

- **LDPC parity generation itself** (the `encode()` matrix multiply and `H_*_H_rows/cols` tables) is out of framing scope — I read only the codeword *layout* (`data ++ parity`, `interldpc.c:135-136`) and the `NumberParityBits`/`CODELENGTH` dims. The `1`-padding of unused data bits for the shortened modes (datac4/13/14) is an LDPC-encoder responsibility (`ldpc_encode_frame` `LDPC_PROT_2020`, `interldpc.c:103-110`); the framing layer only needs the `data`/`coded`/`N` sizes in §2.
- **Pilot values, IDFT, CP, hilbert-clipper, `amp_scale`/BPF** are modem/engine, not framing — I documented the pilot-row *placement* rule (`ofdm.c:1024-1039`) and the constellation only insofar as UW symbols need `qpsk_mod`. Exact pilot vector is `pilotvalues[]`/`pilots[]` (`ofdm.c:88`) — defer to the modem component.
- The `next_prime`/`is_prime` port is functionally identical to the source's O(x) trial division; an optimized sieve is fine **iff** it returns the same primes — pin with test (2) above.
- `1.62` (not φ) and the CCITT-FALSE-vs-X25 CRC distinction are the two silent-interop-break traps; both are cited to exact lines and must not be "cleaned up".
- I did not build, run, or execute anything (RAM-constrained box constraint honored); all numeric derivations in §2/§4/§5c are hand-computed from the cited integer formulas and are independently re-derivable by the ported code.

**Files cited** (all under `/home/tf/.claude/jobs/553c7662/tmp/codec2/src/`): `gp_interleaver.c` (interleaver + b), `ofdm_mode.c` (per-mode config + UW), `ofdm.c` (UW index/insertion, qpsk_mod/constellation, txframe/pilots, PRNG, preamble), `freedv_api.c` (CRC gen/check, pack/unpack, rawdata tx wrappers, preamble/postamble tx), `interldpc.c` (`ofdm_ldpc_interleave_tx` order, codeword layout, `set_data_bits_per_frame`), `freedv_700.c` (`freedv_comptx_ofdm`, open/`bits_per_modem_frame`), `freedv_data_raw_tx.c` (burst loop + CRC placement), `ldpc_codes.c` + `H_128_256_5.h`/`H_4096_8192_3d.h`/`H_1024_2048_4f.h`/`H_256_512_4.h`/`HRA_56_56.h` (parity/codelength dims), `ofdm_internal.h` (`MAX_UW_BITS`, struct fields). pdn-soundmodem idiom refs: `src/Packet.SoundModem/Fec/Crc16X25.cs`, `Modems/IModem.cs`.


## libcodec2 test-oracle

### libcodec2 Test-Oracle Harness — Implementation-Ready Design

Scope: a **test-project-only** cross-check that pins pdn-soundmodem's pure-managed `datac{0,1,3,4,13,14}` OFDM modem to FreeDV's reference C. libcodec2 is never a runtime dependency of the shipped library — it is linked only into the test assembly, and only where present. All facts below are cited to the shallow clone at `/home/tf/.claude/jobs/553c7662/tmp/codec2-ref` (identical twin at `…/codec2`), **codec2 1.2.0, git `310777b1`**. Where I could not verify something from source I say so.

---

#### 1. Interop-exact ground truth (verified from source)

These are the bit-level facts the oracle pins. Every one is cited; port them verbatim.

### 1.1 Mode IDs — `freedv_api.h:59-64`
```
FREEDV_MODE_DATAC1=10  DATAC3=12  DATAC0=14  DATAC4=18  DATAC13=19  DATAC14=20
```

### 1.2 The six modes are QPSK, Fs=8000, centre 1500 Hz — `ofdm_mode.c:31-35`
Defaults `tx_centre = rx_centre = 1500.0f`, `fs = 8000.0f`, `bps = 2`. None of the datac branches (`ofdm_mode.c:122-275`) override `bps`, so all six are QPSK. `freedv_get_modem_sample_rate` returns `f->modem_sample_rate = f->ofdm->config.fs` (`freedv_700.c:249`, `freedv_api.c:1450`).

### 1.3 Frame dimensions (exact, derived from source and cross-checked against `README_data.md:142-149`)

`bits_per_modem_frame` is **not** `codelen − parity`. `ldpc_mode_specific_setup` (`interldpc.c:80-83`) recomputes it:
```
data_bits_per_frame = bitsperpacket − nuwbits − ntxtbits − NumberParityBits
```
with `bitsperframe = (ns−1)·nc·bps`, `bitsperpacket = np·bitsperframe` (`ofdm.c:297-299`). Using the per-mode `ns/np/nc/nuwbits/txtbits` (`ofdm_mode.c:122-275`), the LDPC parity counts (`HRA_56_56=56`, `H_128_256_5=128`, `H_256_512_4=256`, `H_1024_2048_4f=1024`, `H_4096_8192_3d=4096` — from the `*.h` `NUMBERPARITYBITS`), and `data_bits = codelen − parity` (`interldpc.c:50`):

| Mode | ns·np·nc | nuwbits | parity code | **bits/modem_frame** | bytes | **payload bytes** (=bytes−2) | README |
|---|---|---|---|---|---|---|---|
| datac0 | 5·4·9 | 32 | H_128_256_5 (128) | **128** | 16 | **14** | 14 ✓ |
| datac1 | 5·38·27 | 16 | H_4096_8192_3d (4096) | **4096** | 512 | **510** | 510 ✓ |
| datac3 | 5·29·9 | 40 | H_1024_2048_4f (1024) | **1024** | 128 | **126** | 126 ✓ |
| datac4 | 5·47·4 | 32 | H_1024_2048_4f (1024) | **448** | 56 | **54** | 54 ✓ |
| datac13 | 5·18·3 | 48 | H_256_512_4 (256) | **128** | 16 | **14** | 14 ✓ |
| datac14 | 5·4·4 | 32 | HRA_56_56 (56) | **40** | 5 | **3** | 3 ✓ |

All six reconcile with the README table (datac14 is the trap: naïve `56−56` gives 56, but the mode-specific recompute gives **40**). **The harness treats `freedv_get_bits_per_modem_frame()` as authoritative at generation time** and asserts it equals this table (drift guard).

### 1.4 Byte/bit packing — `freedv_pack`/`freedv_unpack` (`freedv_api.c:419-443`)
MSB-first: bit `i` of the unpacked stream goes to byte `i/8`, bit position `7−(i mod 8)`. `freedv_rawdatatx` calls `freedv_unpack(f->tx_payload_bits, packed, bits_per_modem_frame)` (`:472`); `freedv_rawdatarx` calls `freedv_pack(packed, f->rx_payload_bits, bits_per_modem_frame)` (`:1095`). Port both exactly — this is the byte↔bit order on the wire.

### 1.5 CRC — `freedv_gen_crc16` (`freedv_api.c:1616-1628`)
```
crc = 0xFFFF
for each byte b:  x = (crc>>8) ^ b;  x ^= x>>4;
                  crc = (crc<<8) ^ (x<<12) ^ (x<<5) ^ x
```
This is **CRC-16/CCITT-FALSE** (poly 0x1021, init 0xFFFF, refin/refout=false, xorout=0x0000). **It is NOT the repo's existing `Fec/Crc16X25`** (that is reflected, xorout 0xFFFF). The datac path needs a new `Fec/Crc16Ccitt`. Placement: TX writes `crc>>8` at `bytes[N−2]`, `crc&0xff` at `bytes[N−1]` (`freedv_data_raw_tx.c:379-382`); the CRC is computed over the first `payload_bytes = N−2` bytes only.

### 1.6 The CRC gates RX validity — `freedv_700.c:513-518` (critical)
For data modes (`data_mode="streaming"`, set for all six at `ofdm_mode.c:139/163/189/215/242/269`):
```
if (freedv_check_crc16_unpacked(rx_payload_bits, Ndatabitsperpacket))
      rx_status |= FREEDV_RX_BITS;      // valid frame
else  rx_status |= FREEDV_RX_BIT_ERRORS; // decoded but CRC failed -> no bytes returned
```
`freedv_rawdatarx` returns `bits_per_modem_frame/8` bytes **only when `FREEDV_RX_BITS` is set** (`freedv_api.c:1093-1097`). So the managed demod must run the identical CRC check to decide "frame good", and our returned byte count / rx_status must match. `freedv_check_crc16_unpacked` (`freedv_api.c:455-464`) packs the bits, compares `bytes[N−2..N−1]` big-endian against `freedv_gen_crc16` over the first `nbits−16` bits.

### 1.7 rx_status flag bits — `freedv_api.h:75-79`
`TRIAL_SYNC=0x1  SYNC=0x2  BITS=0x4  BIT_ERRORS=0x8`. Returned by `freedv_get_rx_status` (`freedv_api.c:1489`).

### 1.8 Burst layout & sample counts — `freedv_700.c:244-248`, `freedv_api.c:1593-1611`
- Preamble = one OFDM frame: `freedv_get_n_tx_preamble_modem_samples` → `ofdm->samplesperframe` (`:1599`); built by `memcpy(ofdm->tx_preamble)` + `ofdm_hilbert_clipper` (`freedv_api.c:538-545`).
- Data frame (one `freedv_rawdatatx`) = `freedv_get_n_tx_modem_samples` → `n_nat_modem_samples = ofdm_get_samples_per_packet = samplesperframe·np` (`:1462`, `freedv_700.c:245`, `ofdm.c:1109`).
- Postamble = `ofdm->samplesperframe` (`:1607`).
- RX buffer bound `n_max_modem_samples = 2·max_samplesperframe = 4·samplesperframe` (`freedv_700.c:248`, `ofdm.c:305-308`).
- `samplesperframe = ns·(m+ncp)` where `m=round(fs·ts)`, `ncp=round(fs·tcp)` (`ofdm.c:303-304`). All queryable at runtime — the harness never hardcodes them.

The reference driver emits `[preamble][data][postamble]` per burst (`freedv_data_raw_tx.c:371-393`), each written real-int16 by `mod_out[i] = mod_out_comp[i].real` (`freedv_api.c:516`, `:558`, `:588`) — a C float→short **truncation toward zero**.

### 1.9 A plain `freedv_open(mode)` reproduces the reference driver exactly for datac
Verified: `f->tx_amp` is applied **only** in FSK (`freedv_fsk.c:423-446`); it is never read on the OFDM path (`freedv_comptx_ofdm`, `freedv_700.c:260-300`, no `tx_amp`). The driver's `freedv_set_tx_amp(freedv, FSK_SCALE)` (`freedv_data_raw_tx.c:297`) is therefore a **no-op for datac**. The driver also leaves `use_clip=use_txbpf=−1` by default, so it never calls `freedv_set_clip`/`freedv_set_tx_bpf` and the mode's config defaults apply (`clip_en=true`, per-mode `amp_scale/clip_gain1/clip_gain2` at `ofdm_mode.c:216-218/243-245/270-272`). **Conclusion:** the oracle uses `freedv_open` + `freedv_rawdata{preamble,,postamble}tx` with no setters and is byte-identical to `freedv_data_raw_tx` output. (These clip/amp constants are what the managed *modulator* must port; the harness just pins them via the vectors.)

---

#### 2. Building libcodec2 for test use (CMake)

codec2 1.2.0, `BUILD_SHARED_LIBS` option at `CMakeLists.txt:118`, `LPCNET` off by default (`:124`), `UNITTEST` off (`:120`). datac modes need no LPCNet. One-time build:

```sh
git clone --depth 1 https://github.com/drowe67/codec2 codec2   # already present in this env; do NOT reclone here
cmake -S codec2 -B codec2/build \
  -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=ON -DLPCNET=OFF -DUNITTEST=OFF
cmake --build codec2/build -j
### -> codec2/build/src/libcodec2.so.1.2  (+ symlink libcodec2.so)
```
Record `git rev-parse HEAD` (→ `310777b1…`) and `freedv_get_version()`/`freedv_get_hash()` into every vector manifest. This binary is used by (a) the one-time vector generator and (b) the optional live cross-check; it is **never** referenced by `Packet.SoundModem` and never shipped in a `.deb`/NuGet.

---

#### 3. P/Invoke surface (test-only)

Mirror the repo's existing native idiom (`Audio/AlsaPcm.cs`: `const string Lib`, `[DllImport(Lib)]`, `IntPtr` handles). Place under `tests/Packet.SoundModem.Tests/Oracle/`. Use classic `[DllImport]` with blittable `short[]`/`byte[]` and explicit `[In]`/`[Out]` (source-gen `LibraryImport` also fine; `[DllImport]` matches AlsaPcm and needs no `unsafe`).

```csharp
using System.Runtime.InteropServices;
namespace Packet.SoundModem.Tests.Oracle;

internal static class LibCodec2
{
    internal const string Lib = "codec2";   // resolved by NativeLibrary resolver, §3.1

    // ---- lifecycle ----  freedv_api.h:206-207
    [DllImport(Lib)] internal static extern IntPtr freedv_open(int mode);
    [DllImport(Lib)] internal static extern void   freedv_close(IntPtr f);

    // ---- transmit (real int16 path) ----  freedv_api.h:215,219,221
    [DllImport(Lib)] internal static extern void freedv_rawdatatx(
        IntPtr f, [Out] short[] modOut, [In] byte[] packedPayloadBits);
    [DllImport(Lib)] internal static extern int  freedv_rawdatapreambletx(IntPtr f, [Out] short[] modOut);
    [DllImport(Lib)] internal static extern int  freedv_rawdatapostambletx(IntPtr f, [Out] short[] modOut);

    // ---- receive ----  freedv_api.h:226,232,329
    [DllImport(Lib)] internal static extern int freedv_nin(IntPtr f);
    [DllImport(Lib)] internal static extern int freedv_rawdatarx(
        IntPtr f, [Out] byte[] packedPayloadBits, [In] short[] demodIn);
    [DllImport(Lib)] internal static extern int freedv_get_rx_status(IntPtr f);

    // ---- geometry (all int) ----  freedv_api.h:313-319,340
    [DllImport(Lib)] internal static extern int freedv_get_bits_per_modem_frame(IntPtr f);
    [DllImport(Lib)] internal static extern int freedv_get_modem_sample_rate(IntPtr f);
    [DllImport(Lib)] internal static extern int freedv_get_modem_symbol_rate(IntPtr f);
    [DllImport(Lib)] internal static extern int freedv_get_n_max_modem_samples(IntPtr f);
    [DllImport(Lib)] internal static extern int freedv_get_n_tx_modem_samples(IntPtr f);
    [DllImport(Lib)] internal static extern int freedv_get_n_tx_preamble_modem_samples(IntPtr f);
    [DllImport(Lib)] internal static extern int freedv_get_n_tx_postamble_modem_samples(IntPtr f);

    // ---- helpers (cross-check our managed ports) ----  freedv_api.h:246-248
    [DllImport(Lib)] internal static extern ushort freedv_gen_crc16([In] byte[] data, int length);
    [DllImport(Lib)] internal static extern void   freedv_pack  ([Out] byte[] bytes, [In] byte[] bits, int nbits);
    [DllImport(Lib)] internal static extern void   freedv_unpack([Out] byte[] bits, [In] byte[] bytes, int nbits);

    // modes
    internal const int DATAC1=10, DATAC3=12, DATAC0=14, DATAC4=18, DATAC13=19, DATAC14=20;
    // rx_status
    internal const int RX_TRIAL_SYNC=0x1, RX_SYNC=0x2, RX_BITS=0x4, RX_BIT_ERRORS=0x8;
}
```

Marshalling notes (exact): `short` = C `short` (16-bit), `byte` = `unsigned char`, `IntPtr` = opaque `struct freedv *`. `[Out] short[] modOut` must be pre-sized to `freedv_get_n_tx_modem_samples` (≥ preamble/postamble length); `[In] short[] demodIn` must hold exactly the current `freedv_nin(f)` samples (`freedv_data_raw_rx.c:306` reads `nin` shorts per call); `[Out] byte[] packedPayloadBits` sized `bits_per_modem_frame/8`. **Do not** bind the `COMP`/complex variants — the real path (§1.8) is what our modem produces and what we compare.

### 3.1 Resolver + availability gate
```csharp
static LibCodec2() {
    NativeLibrary.SetDllImportResolver(typeof(LibCodec2).Assembly, (name, asm, path) => {
        if (name != Lib) return IntPtr.Zero;
        var env = Environment.GetEnvironmentVariable("PDN_LIBCODEC2");   // e.g. .../build/src/libcodec2.so
        if (env is not null && NativeLibrary.TryLoad(env, out var h)) return h;
        return NativeLibrary.TryLoad("libcodec2.so.1.2", out h) ? h
             : NativeLibrary.TryLoad("libcodec2.so",     out h) ? h : IntPtr.Zero;
    });
}
internal static bool IsAvailable { get { try { var f=freedv_open(DATAC0); if(f==IntPtr.Zero) return false; freedv_close(f); return true;} catch(DllNotFoundException){return false;} } }
```

---

#### 4. Managed oracle wrapper

`tests/Packet.SoundModem.Tests/Oracle/FreeDvOracle.cs`. Thin, deterministic, disposable; also carries **managed re-implementations** of pack/unpack/CRC so the live oracle can prove those match `freedv_*` before we trust them in the shipped library.

```csharp
public readonly record struct OracleRxFrame(byte[] Payload, int NBytes, int RxStatus);

public sealed class FreeDvOracle : IDisposable
{
    private IntPtr _f;
    public int Mode { get; }
    public int BitsPerModemFrame { get; }
    public int BytesPerModemFrame  => BitsPerModemFrame / 8;
    public int PayloadBytesPerFrame => BytesPerModemFrame - 2;    // last 2 = CRC (§1.5)
    public int SampleRate { get; }                                // == 8000
    public int NPreamble { get; }  public int NData { get; }  public int NPostamble { get; }
    public int NMaxModemSamples { get; }

    public FreeDvOracle(int mode) {
        _f = LibCodec2.freedv_open(mode);
        if (_f == IntPtr.Zero) throw new InvalidOperationException($"freedv_open({mode}) failed");
        Mode = mode;
        BitsPerModemFrame = LibCodec2.freedv_get_bits_per_modem_frame(_f);
        SampleRate        = LibCodec2.freedv_get_modem_sample_rate(_f);
        NPreamble  = LibCodec2.freedv_get_n_tx_preamble_modem_samples(_f);
        NData      = LibCodec2.freedv_get_n_tx_modem_samples(_f);
        NPostamble = LibCodec2.freedv_get_n_tx_postamble_modem_samples(_f);
        NMaxModemSamples = LibCodec2.freedv_get_n_max_modem_samples(_f);
    }

    // Build the frame bytes exactly like freedv_data_raw_tx.c:379-382
    public byte[] BuildFrameBytes(ReadOnlySpan<byte> payload) {
        if (payload.Length != PayloadBytesPerFrame) throw new ArgumentException(nameof(payload));
        var b = new byte[BytesPerModemFrame];
        payload.CopyTo(b);
        ushort crc = Crc16Ccitt(b.AsSpan(0, PayloadBytesPerFrame));   // == freedv_gen_crc16
        b[^2] = (byte)(crc >> 8); b[^1] = (byte)(crc & 0xff);
        return b;
    }

    // Deterministic golden burst: [preamble?][data][postamble?] real int16 (§1.8)
    public short[] Modulate(ReadOnlySpan<byte> payload, bool preamble=true, bool postamble=true) {
        byte[] frame = BuildFrameBytes(payload);
        var scratch = new short[NData];                 // ≥ NPreamble, NPostamble
        var outp = new List<short>(NPreamble + NData + NPostamble);
        if (preamble)  { int n = LibCodec2.freedv_rawdatapreambletx(_f, scratch);  outp.AddRange(scratch[..n]); }
        LibCodec2.freedv_rawdatatx(_f, scratch, frame); outp.AddRange(scratch[..NData]);
        if (postamble) { int n = LibCodec2.freedv_rawdatapostambletx(_f, scratch); outp.AddRange(scratch[..n]); }
        return outp.ToArray();
    }

    // Drive the reference demod exactly like freedv_data_raw_rx.c:305-311
    public IReadOnlyList<OracleRxFrame> Demodulate(short[] modemIn) {
        var frames = new List<OracleRxFrame>();
        var bytesOut = new byte[BytesPerModemFrame];
        int pos = 0, nin = LibCodec2.freedv_nin(_f);
        var chunk = new short[NMaxModemSamples];
        while (pos + nin <= modemIn.Length) {
            Array.Copy(modemIn, pos, chunk, 0, nin); pos += nin;
            int nbytes = LibCodec2.freedv_rawdatarx(_f, bytesOut, chunk);
            int status = LibCodec2.freedv_get_rx_status(_f);
            if (nbytes > 0) frames.Add(new OracleRxFrame(bytesOut[..(nbytes-2)], nbytes, status)); // drop CRC (rx.c:314)
            nin = LibCodec2.freedv_nin(_f);
        }
        return frames;
    }

    // ---- managed ports under test (verified equal to libcodec2 in the live tier) ----
    public static ushort Crc16Ccitt(ReadOnlySpan<byte> d) {         // freedv_api.c:1616-1628
        ushort crc = 0xFFFF;
        foreach (byte v) in ... // for each byte:
        { int x = ((crc >> 8) ^ v) & 0xff; x ^= x >> 4;
          crc = (ushort)((crc << 8) ^ (x << 12) ^ (x << 5) ^ x); }
        return crc;
    }
    public void Dispose() { if (_f != IntPtr.Zero) { LibCodec2.freedv_close(_f); _f = IntPtr.Zero; } }
}
```

---

#### 5. Vector generation strategy

**One-time**, on a box with libcodec2, via an explicit-run xUnit theory `[Trait("Category","OracleGen")]` (never in the default filter — CLAUDE.md test convention) or a tiny `tools/Packet.SoundModem.OracleGen` console. Output committed under `tests/Packet.SoundModem.Tests/Fixtures/freedv/` (already auto-copied by the csproj `<None Include="Fixtures/**" CopyToOutputDirectory="PreserveNewest"/>`).

Per mode, a fixed set of payload cases (seeded, so regen is reproducible):
- `all-zero`, `all-0xFF`, `incrementing 0..N`, and 3× `new Random(seed).NextBytes(payload)` — each exactly `PayloadBytesPerFrame` bytes.

For each case emit two files:

**TX golden** — `datac0-tx-rand7.s16` : little-endian mono int16, the full `[preamble][data][postamble]` burst from `FreeDvOracle.Modulate` (no silence). Plus `…-tx-rand7.json`:
```json
{ "mode":"datac0", "modeId":14, "codec2Version":<freedv_get_version>, "codec2Hash":"310777b1…",
  "sampleRate":8000, "bitsPerModemFrame":128, "payloadBytes":14,
  "nPreamble":720, "nData":2880, "nPostamble":720,
  "payloadHex":"…14 bytes…", "frameHex":"…16 bytes incl CRC…", "crc16":"0xABCD",
  "s16File":"datac0-tx-rand7.s16", "s16Sha256":"…" }
```

**RX golden** — reuse the TX `.s16` as the clean-channel RX input (reference TX audio is exactly what a reference RX decodes). `…-rx-rand7.json` records what `FreeDvOracle.Demodulate` returned on that input:
```json
{ "expectFrames":1, "frames":[{ "payloadHex":"…", "nBytes":16, "rxStatus":6 }] }  // 6 = SYNC|BITS
```
Optionally also commit `…-rx-noisy-rand7.s16` (the same burst plus a fixed AWGN seed) with its recorded decode, to exercise the CRC-reject path (`rxStatus` with `BIT_ERRORS` and zero frames).

Sizes are small: datac1 golden ≈ (720?+8208·? ) — actually datac1 `nData = samplesperframe·38`; the largest burst is a few hundred KB. Fine to commit. datac0/13/14 are tens of KB.

---

#### 6. Bit-exact comparison harness

Two comparators, because exactness is guaranteed at different layers:

```csharp
static class OracleCompare
{
    // Integer layers — MUST be exact.
    public static void ExactBytes(ReadOnlySpan<byte> ours, ReadOnlySpan<byte> golden) =>
        ours.SequenceEqual(golden).Should().BeTrue();

    // DSP int16 — exact if achievable, else bounded.
    public static (int maxAbs, double rms) Diff(ReadOnlySpan<short> a, ReadOnlySpan<short> b) {
        a.Length.Should().Be(b.Length, "sample count is a framing invariant, always exact");
        int max=0; double sq=0;
        for (int i=0;i<a.Length;i++){ int d=a[i]-b[i]; max=Math.Max(max,Math.Abs(d)); sq+=(double)d*d; }
        return (max, Math.Sqrt(sq/a.Length));
    }
}
```

Contract, stated honestly:
- **Framing / packing / CRC / bit-order / returned-byte-count / rx_status**: pure integer ops → **exact**, no tolerance. `BuildFrameBytes`, `Crc16Ccitt`, pack/unpack, `Demodulate` payload+status are asserted byte-equal / value-equal.
- **Sample count** of the burst and of every `nin` step: **exact** (it is `ns·(m+ncp)·(np+2)`, integer).
- **int16 waveform**: the reference does float→short truncation (§1.8). If the managed OFDM engine reproduces libcodec2's float pipeline (IDFT, `ofdm_hilbert_clipper`, per-mode `amp_scale`/`clip_gain1`/`clip_gain2`) in the same summation order, the truncated int16 is bit-identical. Because managed float summation order may differ, the harness asserts `maxAbs == 0` as the **goal** but is parameterised with a per-mode `maxAbsTolerance` (start e.g. ≤2 LSB) and an RMS bound, tightened toward 0 as the port matures. This tolerance is a **named, documented knob** (mirrors the repo's strict-vs-pragmatic discipline), not a silent fudge.
- **RX robustness**: feeding the golden `.s16` into our managed demod must recover the exact payload bytes and produce the same "frame good/bad" decision (the CRC gate, §1.6) — this is exact regardless of internal float differences, because it is a decision, not a waveform.

---

#### 7. CI story (runs without the native lib)

Three tiers, so default CI is self-contained and green on any runner (self-hosted Linux, per CLAUDE.md — no hosted runners):

**Tier A — checked-in vectors (default filter, no libcodec2).** For each committed vector:
- *TX parity*: `ourModulator.Modulate(payloadFromManifest)` vs the golden `.s16` → `ExactBytes`/`Diff` per §6.
- *RX parity*: `ourDemodulator.Demodulate(golden .s16)` → recovered payload == `frames[].payloadHex`, byte count and CRC-valid status == manifest.
- *Layer parity*: `Crc16Ccitt(frameHex[..−2]) == manifest.crc16`; managed pack/unpack round-trips the manifest `frameHex`↔bits.
These are the load-bearing regression tests and need **no** native code — they gate every PR.

**Tier B — live oracle cross-check (`[SkippableFact]`, `Skip.IfNot(LibCodec2.IsAvailable, …)`).** Only where `PDN_LIBCODEC2` resolves:
- *Vector integrity*: regenerate a case in-memory (`FreeDvOracle.Modulate`) and assert it byte-equals the committed `.s16` — catches silent codec2 version/behaviour drift and confirms the committed hash still matches `freedv_get_hash()`.
- *Helper equivalence*: `FreeDvOracle.Crc16Ccitt(x) == LibCodec2.freedv_gen_crc16(x)` and managed pack/unpack == `freedv_pack`/`freedv_unpack` over random inputs (this is what licenses us to ship the managed ports).
- *Cross round-trips*: our TX → `freedv_rawdatarx` recovers payload; reference TX → our RX recovers payload.

**Tier C — vector regeneration** (`[Trait("Category","OracleGen")]`, manual): produces/refreshes Fixtures when codec2 is intentionally bumped; the PR that does so records the new version/hash in each manifest and in `PROVENANCE.md`.

Wire into `tests/Directory.Build.props` conventions: Tier B/C carry `[Trait("Category","Interop")]`-style traits so the default `dotnet test` (which the repo runs for merges) executes only Tier A. Add an `Oracle` trait to the existing filter vocabulary rather than reusing `HardwareLoop`/`Interop` verbatim, to keep intent clear.

---

#### 8. LGPL-2.1 boundary

- libcodec2's FreeDV API is **LGPL-2.1** (`freedv_api.h:22-31`, `freedv_api.c` same header). pdn-soundmodem is **GPL-3.0-or-later** (repo `COPYING`, `CLAUDE.md § Licence rules`). GPL-3.0 is a compatible downstream of LGPL-2.1, so *linking* is fine — but only in the **test assembly**, and the shipped `Packet.SoundModem` (NuGet `pdn-soundmodem`) must not reference it. Enforce structurally: the P/Invoke and `FreeDvOracle` live under `tests/…/Oracle/`, the `ProjectReference` graph never puts libcodec2 on `src/`'s path, and there is no `<PackageReference>`/`<None>` shipping any `.so`.
- The library is **dynamically loaded at test time only** (`NativeLibrary.TryLoad`); no codec2 binary is vendored, redistributed, or packaged. Nothing to relink or offer source for downstream.
- **Provenance**: add a `tests/…/Oracle/README.md` and a `PROVENANCE.md` row stating: the oracle links libcodec2 (LGPL-2.1, © David Rowe) **for testing only**; the checked-in `.s16` vectors are *generated output* of that reference (facts/measurements, committed as test fixtures like the existing NinoTNC `samples/*.wav` and Dire Wolf `Fixtures/*.wav`), not source code; the codec2 licence header and upstream URL/commit `310777b1` are cited in that README. This matches how `PROVENANCE.md` already treats read-and-cross-validated GPL sources.
- Keep libcodec2's own notices intact wherever the build instructions or README reference it; do not copy any codec2 `.c`/`.h` into this repo (we bind the ABI, we don't vendor the code).

---

#### 9. Files to add (all under `tests/`)

```
tests/Packet.SoundModem.Tests/Oracle/LibCodec2.cs            P/Invoke + resolver (§3)
tests/Packet.SoundModem.Tests/Oracle/FreeDvOracle.cs         wrapper + managed ports (§4)
tests/Packet.SoundModem.Tests/Oracle/OracleCompare.cs        comparators (§6)
tests/Packet.SoundModem.Tests/Oracle/README.md               LGPL boundary + provenance (§8)
tests/Packet.SoundModem.Tests/Modems/DatacOracleParityTests.cs   Tier A (§7)
tests/Packet.SoundModem.Tests/Oracle/DatacLiveOracleTests.cs     Tier B, [SkippableFact]
tests/Packet.SoundModem.Tests/Oracle/DatacVectorGenTests.cs      Tier C, [Trait Category=OracleGen]
tests/Packet.SoundModem.Tests/Fixtures/freedv/*.s16 + *.json     committed golden vectors (§5)
```
No `Directory.Packages.props` change (test-only, no NuGet dep). Fixtures auto-copy via the existing csproj glob; locate the repo root with the same `pdn-soundmodem.slnx` walk `OccupiedBandwidthTests.FindRepoRoot` uses (`OccupiedBandwidthTests.cs:227-236`) if you prefer `samples/freedv/` over `Fixtures/`.

---

#### 10. Explicitly not verified / open items (no confabulation)

- I read `bits_per_modem_frame` derivation and confirmed all six values against `README_data.md`, but I did **not** execute anything, so the exact `.s16` byte lengths (e.g. datac0 = 720+2880+720 = 4320 samples) are computed from `ns·(m+ncp)·(np+2)` with `m=fs·ts`, `ncp=fs·tcp`; the generator must take the authoritative counts from `freedv_get_n_tx_*` at run time (the design already does). I did not independently verify `m`/`ncp` rounding in `ofdm.c` beyond `samplespersymbol = m+ncp` (`ofdm.c:303`) — treat the runtime query as truth.
- The DATAC modes' `data_mode="streaming"` (not `"burst"`) is confirmed from `ofdm_mode.c`; I did not trace whether "streaming" vs "burst" changes preamble handling inside `ofdm_sync_search` — irrelevant to the harness since we capture reference output verbatim, but flag it for the modulator/demodulator port.
- Whether managed float→int16 can hit `maxAbs==0` against codec2 is unknown until the engine exists; §6 handles this with a named tolerance rather than assuming exactness.
- I did not open `comp.h`; the real (int16) API path is used throughout, so the `COMP` layout is not needed by the harness.

Reference source root used for every citation above: `/home/tf/.claude/jobs/553c7662/tmp/codec2-ref` (codec2 1.2.0, git `310777b1c6f1af0bc7c72f5b32f80f6fd9136962`).


## Integration, OBW & validation

---

### FreeDV datac — Integration + Validation design (pdn-soundmodem)

Component: the **integration + validation** half. The OFDM engine internals (pilot/UW placement, timing/foff estimators, LDPC codecs, preamble/postamble waveform synthesis) are the DSP sub-agent's deliverable; this document defines the `IModem` wrapper, the KISS/daemon seam, the interop-exact boundary, and the whole validation/acceptance apparatus, and it pins the exact codec2 constants those pieces must hit.

Absolute paths used below: codec2 reference = `/home/tf/.claude/jobs/553c7662/tmp/codec2-ref` (read-only shallow clone); target repo = `/home/tf/src/pdn-soundmodem`.

#### 0. Grounding — what I read, and what I explicitly did not

Read and cited: `src/ofdm_mode.c` (per-mode params), `src/ofdm_internal.h` (`OFDM_CONFIG`/`OFDM`), `src/codec2_ofdm.h` (public API + `OFDM_PEAK`), `src/freedv_api.c` (`freedv_gen_crc16`, `freedv_rawdatatx`/`preambletx`/`postambletx`, sample-count getters), `src/freedv_data_raw_tx.c` (burst assembly, CRC placement), `README_data.md` (published RF-bandwidth / MPP table), `src/ch.c` + `octave/ch_fading.m` + `octave/doppler_spread.m` + `unittest/fading_files.sh` (channel model). On the pdn-soundmodem side: `Modems/IModem.cs`, `Modems/QpskModem.cs`, `Modems/FrameQuality.cs`, `Channel/SoundModemChannel.cs`, `Daemon/{DaemonConfig,Program}.cs`, `tests/.../Dsp/OccupiedBandwidth{,Tests}.cs`, `tests/.../Modems/QtsmInteropTests.cs`, `samples/README.md`, `CLAUDE.md`.

Did **not** read (flagged where they matter — do not confabulate these): `src/ofdm.c` internals (exact preamble/postamble waveform, pilot allocation, `samplesperframe` derivation), the LDPC parity matrices behind the `codename` strings, `fdmdv_freq_shift_coh`, and the FIR tap tables `ht_coeff.h`/`ssbfilt_coeff.h`/`filter_coef.h` (I confirmed their sizes/defines but not the tap values). Those are named as "port verbatim from <file>" tasks below.

#### 1. What the six datac modes actually are (grounded)

All six are **OFDM** (multi-carrier), **QPSK per subcarrier** (`config->bps = 2`, `ofdm_mode.c:35`), **Fs = 8000 Hz** (`ofdm_mode.c:33`), **tx/rx centre 1500 Hz** (`ofdm_mode.c:31-32`), `ns = 5`, `edge_pilots = 0`, `txtbits = 0`, `state_machine = "data"`, `data_mode = "streaming"`, `amp_est_mode = 1`. "QPSK @ 8000/1500" in the brief = the per-carrier constellation; the modes are not single-carrier QPSK like the existing `QpskModem`. This is the single most important design fact: **do not** try to reuse `QpskModem`/`QpskModulator`; this is a new OFDM engine.

Per-mode parameter table — **port exactly from `ofdm_mode.c:122-275`**; payload/FEC/OBW/MPP columns from `README_data.md:144-149`:

| mode | Nc | Np | Ts(s) | Tcp(s) | Nuw | bad_uw | codename (LDPC) | timing_mx_thresh | tx_bpf_proto | rx_bpf | amp_scale | clip g1/g2 | payload bytes | RF BW Hz | MPP test | op SNR |
|---|--:|--:|--:|--:|--:|--:|---|--:|---|:--:|--:|--:|--:|--:|:--:|--:|
| datac0 | 9 | 4 | .016 | .006 | 32 | 9 | H_128_256_5 | 0.08 | filtP400S600 | off | 300E3 | 2.2/0.85 | 14 | 500 | 70/100 | 0 dB |
| datac1 | 27 | 38 | .016 | .006 | 16 | 6 | H_4096_8192_3d | 0.10 | filtP900S1100 | off | 145E3 | 2.7/0.8 | 510 | 1700 | 92/100 | 5 dB |
| datac3 | 9 | 29 | .016 | .006 | 40 | 10 | H_1024_2048_4f | 0.10 | filtP400S600 | off | 300E3 | 2.2/0.8 | 126 | 500 | 74/100 | 0 dB |
| datac4 | 4 | 47 | .016 | .006 | 32 | 12 | H_1024_2048_4f | 0.5 | filtP200S400 | on | 2·300E3 | 1.2/1.0 | 54 | 250 | 90/100 | −4 dB |
| datac13 | 3 | 18 | .016 | .006 | 48 | 18 | H_256_512_4 | 0.45 | filtP200S400 | on | 2.5·300E3 | 1.2/1.0 | 14 | 200 | 90/100 | −4 dB |
| datac14 | 4 | 4 | .018 | .005 | 32 | 12 | HRA_56_56 | 0.45 | filtP200S400 | on | 2.0·300E3 | 2.0/1.0 | 3 | 250 | 90/100 | −2 dB |

Derived sample constants (grounded arithmetic, `m = Ts·Fs`, `ncp = Tcp·Fs`): datac0/1/3/4/13 → `m=128, ncp=48, samplespersymbol=176`; datac14 → `m=144, ncp=40, samplespersymbol=184`. `samplesperframe`/`samplesperpacket` are engine-derived in `ofdm.c` (not read) — treat as `ofdm_get_samples_per_frame`/`ofdm_get_samples_per_packet` outputs; the oracle test (§4) pins them empirically.

Unique-word bit patterns per mode are literal arrays in `ofdm_mode.c` (e.g. datac0/datac1 `{1,1,0,0,1,0,1,0,1,1,1,1,0,0,0,0}` at `:136`/`:159`; datac3/4/13/14 use the 24-bit pattern doubled, `:184-188` etc.). Port verbatim.

Payload↔FEC relationship (confirms the KISS byte counts): the last 2 bytes of the `k`-bit LDPC info field are the CRC (`freedv_data_raw_tx.c:379-382`), so `payload_bytes = k/8 − 2`: datac0 k=128→16−2=**14**, datac3 k=1024→128−2=**126**, datac1 k=4096→512−2=**510**, datac4 k=448→56−2=**54**, datac13 k=128→16−2=**14**. (datac14 payload 3 is from the table; `HRA_56_56` k=56 leaves slack — trust the README's 3.)

#### 2. Modem class design (`IModem`-shaped)

Mirror the codec2 shape (one parameterised engine + a mode table) *and* the pdn-soundmodem `QpskModem` idiom (one sealed class, static per-mode factories). Namespace `Packet.SoundModem.Modems.FreeDv`.

### 2.1 Mode table — `FreeDvDataMode.cs` (mine)

```csharp
namespace Packet.SoundModem.Modems.FreeDv;

/// One row of codec2 ofdm_mode.c for a FreeDV datacN mode. Values ported verbatim from
/// codec2 src/ofdm_mode.c:ofdm_init_mode — cite the mode block per field.
public sealed record FreeDvDataMode(
    string Name,            // "datac0"
    int Nc, int Np, int Ns,
    double Ts, double Tcp,  // seconds; m = Ts*8000, ncp = Tcp*8000
    int NuwBits, int BadUwErrors,
    byte[] Uw,
    double TimingMxThresh,
    string Codename,        // LDPC code id -> selects the parity matrix
    double AmpScale, double ClipGain1, double ClipGain2,
    bool RxBpfEnabled,
    float[] TxBpfProto,     // filter_coef.h prototype (filtP400S600 etc.)
    int PayloadBytes,       // = k/8 - 2 (README_data.md)
    int RfBandwidthHz,      // published OBW (README_data.md)
    double OperatingSnrDb)  // README_data.md MPP operating point
{
    public const int Fs = 8000;                 // ofdm_mode.c:33 — NEVER swept
    public int M   => (int)(Ts  * Fs);
    public int Ncp => (int)(Tcp * Fs);
    public static readonly FreeDvDataMode Datac0  = new(...);
    public static readonly FreeDvDataMode Datac1  = new(...);
    public static readonly FreeDvDataMode Datac3  = new(...);
    public static readonly FreeDvDataMode Datac4  = new(...);
    public static readonly FreeDvDataMode Datac13 = new(...);
    public static readonly FreeDvDataMode Datac14 = new(...);
}
```

### 2.2 The modem — `FreeDvDataModem.cs` (mine; engine members are the DSP sub-agent's)

```csharp
/// FreeDV datacN compatible OFDM data modem (codec2 ofdm.c family). A burst modem:
/// preamble + N packets + postamble, LDPC-FEC'd, own 16-bit CRC. Interop-exact with
/// FreeDV `freedv_data_raw_tx/rx`. ARQ/segmentation is a higher layer (see FreeDATA /
/// Mercury) — this carries fixed-capacity raw payloads only.
public sealed class FreeDvDataModem : IModem, IConstellationSource
{
    public const int NativeSampleRate = FreeDvDataMode.Fs;   // 8000

    private FreeDvDataModem(FreeDvDataMode mode, int sampleRate, Action<byte[]> frameReceived);

    // Factory naming: mode string is "freedv-datac0" etc. (§3).
    public static FreeDvDataModem Datac0 (int sampleRate, Action<byte[]> sink) => new(FreeDvDataMode.Datac0,  sampleRate, sink);
    public static FreeDvDataModem Datac1 (int sampleRate, Action<byte[]> sink) => new(FreeDvDataMode.Datac1,  sampleRate, sink);
    public static FreeDvDataModem Datac3 (int sampleRate, Action<byte[]> sink) => new(FreeDvDataMode.Datac3,  sampleRate, sink);
    public static FreeDvDataModem Datac4 (int sampleRate, Action<byte[]> sink) => new(FreeDvDataMode.Datac4,  sampleRate, sink);
    public static FreeDvDataModem Datac13(int sampleRate, Action<byte[]> sink) => new(FreeDvDataMode.Datac13, sampleRate, sink);
    public static FreeDvDataModem Datac14(int sampleRate, Action<byte[]> sink) => new(FreeDvDataMode.Datac14, sampleRate, sink);

    public string Mode => $"freedv-{_mode.Name}";        // IModem
    public int PayloadBytesPerFrame => _mode.PayloadBytes; // KISS capacity (new surface)

    public event Action<byte[], FrameQuality>? FrameDecoded;   // IModem
    public event Action<ConstellationPoint>? SymbolPlotted;    // IConstellationSource

    public bool CarrierDetect => _demod.Sync is OfdmSync.Trial or OfdmSync.Synced;  // ofdm_internal.h:55
    public bool ChannelBusy   => _busy.Busy || CarrierDetect;

    public void Process(ReadOnlySpan<float> samples);                     // IModem — RX
    public float[] Modulate(ReadOnlySpan<byte> frame, int txDelayMs);     // IModem — one burst
    public void ResetCarrierState();                                      // IModem
}
```

Engine seam (DSP sub-agent supplies these — I only consume them):
- `OfdmModulator.Burst(ReadOnlySpan<byte> packedInfoBits) -> float[]` producing `[preamble | packet | postamble]` at 8000 Hz, scaled to `OFDM_PEAK = 16384` (`codec2_ofdm.h:45`), through the tx BPF + `ofdm_hilbert_clipper` when `clip_en`/`tx_bpf_en`.
- `OfdmDemodulator` with `Sync` state (`OfdmSync {Search,Trial,Synced}` ↔ `ofdm_internal.h:55`), `FoffEstHz`, and a per-packet callback `(byte[] payloadPlusCrcBits, int ldpcCorrectedBits) => …`.
- `OfdmSync` enum, `LdpcCode` (keyed by `Codename`).

### 2.3 Sample-rate integration decision (grounded, load-bearing)

codec2 OFDM is hard-8000 (`ofdm_mode.c:33`); the daemon runs the shared channel at **12000** (audio-band) or **48000** (9600 family) — `Program.cs:105-107`. **12000 is not an integer multiple of 8000** (3:2), so a datac modem on a 12 kHz channel would need a rational resampler that doesn't exist in `Dsp/`.

Decision: the OFDM core **always runs at 8000 internally**; `FreeDvDataModem` owns an integer `Upsampler(8000→sampleRate)` and `Decimator(sampleRate→8000)` (both already in `Dsp/`), and the modem **requires `sampleRate % 8000 == 0`** (throw in the ctor otherwise). The daemon forces the shared `DspRate` to **48000** whenever any `freedv-*` modem is configured (extend the `Program.cs:105` rule) — 48000/8000 = 6, exact, and it's the card-native capture rate anyway. Rationale mirrors the existing 9600 branch; avoids inventing a rational resampler. This keeps the OFDM arithmetic bit-identical to codec2 regardless of channel rate — critical for the oracle (§4).

For the OBW test and the oracle, construct the modem **at 8000 directly** (no resampler in the path) so the measured/compared samples are the pure codec2 waveform.

### 2.4 `FrameQuality` mapping (grounded to `FrameQuality.cs`)

`new FrameQuality(Mode, FrameBytes: payload.Length, CorrectedBytes: ldpcCorrectedBits/8, CrcValid: crcOk, FrequencyOffsetHz: _demod.FoffEstHz)`. `CorrectedBytes` = LDPC-repaired bytes (the honest "corrected symbols" analogue the record documents); `CrcValid` = the FreeDV CRC-16 result (§2.5); `FrequencyOffsetHz` = the OFDM coarse/fine foff estimate (`OFDM.foff_est_hz`, `ofdm_internal.h:221`). Fires exactly like `QpskModem` does (`QpskModem.cs:27-29`).

### 2.5 CRC — `FreeDvCrc16.cs` (mine; interop-exact, tiny)

Port `freedv_gen_crc16` verbatim (`freedv_api.c:1616-1629`): init `0xFFFF`,
```csharp
public static ushort Compute(ReadOnlySpan<byte> data){
    ushort crc = 0xFFFF;
    foreach (byte d in data){
        byte x = (byte)((crc >> 8) ^ d);
        x ^= (byte)(x >> 4);
        crc = (ushort)((crc << 8) ^ (ushort)(x << 12) ^ (ushort)(x << 5) ^ x);
    }
    return crc;
}
```
Placement: computed over `payload_bytes_per_modem_frame` and written **big-endian** into the last two payload bytes — `bytes[k/8−2]=crc>>8; bytes[k/8−1]=crc&0xff` (`freedv_data_raw_tx.c:379-382`). This is the exact contract the RX must re-check (`freedv_check_crc16_unpacked`, `freedv_api.c:455-463`). Note this is **not** the repo's existing `Fec/Crc16X25` (X.25 is reflected, poly 0x1021 with different init/xorout) — FreeDV's is the unreflected StackOverflow variant; keep it separate and comment the divergence per the provenance rule (`CLAUDE.md` §Licence rules).

### 2.6 KISS payload contract (raw, fixed-capacity)

One KISS frame ↔ one FreeDV packet. `Modulate(frame, txDelayMs)`:
1. `if (frame.Length > PayloadBytesPerFrame) throw`/log-drop — a raw modem does not silently fragment; segmentation is the ARQ layer's job (§3).
2. Zero-pad `frame` to `PayloadBytes` (FreeDV frames are fixed-size; the higher layer owns length recovery).
3. Append CRC-16 (§2.5) → the `k/8`-byte info field; LDPC-encode; OFDM-modulate one burst at 8000; upsample; **prepend `txDelayMs` of silence** (PTT settling only — the FreeDV preamble is the acquisition aid, not a KISS TXDELAY tone; this keeps the modulated burst byte-identical to codec2).

`Process`: decimate→8000, run the OFDM sync-search + demod state machine; on each decoded packet, LDPC-decode, re-check CRC; **on CRC pass** emit the leading `PayloadBytes` bytes (CRC stripped) to the frame sink + `FrameDecoded`. CRC-fail packets are dropped (matching `freedv_data_raw_rx`).

Honesty note to document: because frames are fixed-size zero-padded, a variable-length payload does not round-trip its own length through the raw modem — that's inherent to FreeDV raw data and is exactly why an ARQ/segmentation layer (§3) is required for real traffic. Keeping the payload byte-exact (no pdn-added length header) is what preserves bit-for-bit interop with `freedv_data_raw_rx`.

#### 3. KISS / daemon integration

`Daemon/Program.cs` switch (`:126-149`) — add:
```csharp
"freedv-datac0"  => FreeDvDataModem.Datac0 (DspRate, sink),
"freedv-datac1"  => FreeDvDataModem.Datac1 (DspRate, sink),
"freedv-datac3"  => FreeDvDataModem.Datac3 (DspRate, sink),
// Phase 2: freedv-datac4 / -datac13 / -datac14
```
`DspRate` rule (`Program.cs:105-107`) — extend so any `m.Mode.StartsWith("freedv-")` forces **48000**, and add a guard: `captureRate % 48000 == 0` (already required for the 9600 family). Document modes in the header comment (`Program.cs:17-19`) and `DaemonConfig.ModemConfig.Mode` xmldoc (`DaemonConfig.cs:12-14`).

Everything else is free: `SoundModemChannel` already fans RX audio to every modem, aggregates `ChannelBusy`/`CarrierDetect`, runs p-persistent CSMA and PTT keying, and drives the constellation sink for any `IConstellationSource` (`SoundModemChannel.cs:98-107,120-124`). A datac modem drops into a KISS sub-channel like any other. `KissTcpServer` and the `--wav` decode path need no changes.

**ARQ is out of scope (explicit).** This is a *raw* modem: fixed-capacity, no retransmission, no segmentation, no addressing. The equivalent higher layer in the FreeDV ecosystem is **FreeDATA** (its ARQ protocol) or **Mercury** — those sit above the raw-frame KISS boundary and own: multi-packet segmentation with a length/sequence header (the `source_byte`/`sequence_numbers` seam at `freedv_data_raw_tx.c:382-383` is where such a header would live), selective-repeat ARQ, and connection state. Note this in the daemon docs as the designated next layer; do not build it into the modem.

#### 4. Interop-exact boundary (the oracle)

The exact TX layout our modulator must reproduce (`freedv_data_raw_tx.c:371-393`, `freedv_api.c:496-590`):

```
burst = [preamble] [packet_1] [packet_2] ... [packet_N] [postamble]
preamble  = ofdm->samplesperframe samples = ofdm->tx_preamble run through ofdm_hilbert_clipper   (freedv_api.c:537-543)
packet_i  = freedv_get_n_tx_modem_samples = n_nat_modem_samples = one full packet (Np modem frames)
postamble = ofdm->samplesperframe samples = ofdm->tx_postamble through ofdm_hilbert_clipper        (freedv_api.c:568-576)
```
Output is **real int16**: `mod_out[i] = mod_out_comp[i].real` (`freedv_api.c:515-516`) — the OFDM is already at 1500 Hz audio passband, so only the real part is emitted. Peak scaling `OFDM_PEAK = FREEDV_PEAK = 16384` (`codec2_ofdm.h:45`, `freedv_api.h:72`). CRC placed as §2.5. Inter-burst silence `2·n_nom_modem_samples` (`freedv_data_raw_tx.c:353,393-404`).

Oracle fixtures — generated **once on a build host that has codec2 compiled** (NOT this box), checked into `samples/freedv/` with provenance like `samples/README.md`:
- **`datacN-golden-tx.s16`** — `freedv_data_raw_tx --testframes 1 --bursts 1 datacN` output for the deterministic testframe (`ofdm_generate_payload_data_bits`, `freedv_data_raw_tx.c:365-368`). Pins TX sample-exactness.
- **`datacN-payload-<hex>.s16`** — a fixed non-testframe payload, so both the modulator and the byte path are exercised.
- **`test_datac1_006.raw`** — the real off-air Adelaide↔Melbourne datac1 capture referenced in `README_data.md` (`raw/` in codec2 upstream; not present in this shallow clone — fetch from upstream). Free real-world RX oracle for datac1.

Four oracle tests per mode (`[Trait("Category","Interop")]`, idiom of `QtsmInteropTests`):
- **TX parity**: `FreeDvDataModem.DatacN(8000,…).Modulate(testframePayload, 0)` vs `datacN-golden-tx.s16`. C↔C# float math differs, so accept **max |Δ| ≤ 1 int16 LSB and normalised cross-correlation ≥ 0.99999** (state the tolerance and why; tighten to exact if the ported clipper/BPF prove deterministic). This is the interop-exact TX gate.
- **RX-from-oracle**: feed `datacN-golden-tx.s16` (and, datac1 only, `test_datac1_006.raw`) to `Process`; assert exact payload bytes recovered, CRC valid, packet count matches codec2's (`Coded PER: 0.0`, `README_data.md:172-174`). Strongest interop proof — their modulator, our demod.
- **Round-trip**: our TX → our RX in clean loopback → payload bit-exact (unit test, no fixture).
- **Reverse (manual, provenance-only)**: our TX `.s16` decoded by upstream `freedv_data_raw_rx datacN` on the build host; record the run in `PROVENANCE.md`. Not in CI (codec2 isn't built on the runner).

#### 5. Per-mode OBW test

Extend `tests/.../Dsp/OccupiedBandwidthTests.cs` with a FreeDV table, reusing `OccupiedBandwidth.Measure` (ITU 99% OBW, Hann/Welch — `OccupiedBandwidth.cs`). Construct each modem **at 8000** (native; Fs/2 = 4000 > 2350 Hz, the upper edge of the widest mode datac1) and measure the data section (skip preamble/postamble):

```csharp
public static TheoryData<string,double> FreeDvPublishedObw => new() {
    { "freedv-datac0", 500 }, { "freedv-datac3", 500 },
    { "freedv-datac1", 1700 },
    { "freedv-datac4", 250 }, { "freedv-datac13", 200 }, { "freedv-datac14", 250 },
};
// assert: measured <= published * 1.05  AND  measured >= published * 0.80
```
Published targets are the `README_data.md:144-149` RF-bandwidth column (= carriers `Nc·Rs`: e.g. datac1 27·62.5 = 1687.5 ≈ 1700; datac0 9·62.5 = 562.5, BPF-trimmed to ~500). Two-sided (ceiling *and* floor) so a too-narrow bug — a missing/over-tight `tx_bpf_proto` or wrong Nc — fails too. `fftSize`: 4096 for the long modes; **2048** for datac14 (0.69 s = 5520 samples at 8000 — enough for one 4096 window but 2048 gives Welch averaging). This test is the integration check that the ported `tx_bpf_proto` (`filter_coef.h`) + `ofdm_hilbert_clipper` are correct; it is the same class of guard that caught the historical 1200-QPSK splatter (`OccupiedBandwidthTests.cs:5-11`).

#### 6. Channel-model validation (AWGN + MPP)

Port codec2's `ch` tool to `tests/.../Channel/Codec2Channel.cs`. Signal chain, **exactly** `ch.c` main loop (`ch.c:330-508`), order per `ch.c:102`: **Hilbert→complex → magnitude-clip → freq-shift → multipath fade → AWGN → SSB filter → real int16**.
- Hilbert: `ht_coeff.h` (`HT_N = 257`); complex signal has 2× input power (`ch.c:341-352`).
- MPP multipath (`ch.c:405-424`): two paths, delayed by `nhfdelay = floor(2.0·8000/1000) = 16` samples (`MPP_DELAY_MS = 2.0`, `ch.c:45,282`). `ch_fdm[i] = hf_gain · (aspread·direct + aspread_2ms·delayed)`.
- Doppler spread (`aspread`, `aspread_2ms`): read interleaved-float from the MPP fading file. Generation is deterministic: `octave/ch_fading.m` → `randn('seed',1)`, two `doppler_spread(1.0Hz, 8000, 8000·60)` realisations, `hf_gain = 1/sqrt(var(spread)+var(spread_2ms))`, file = `[hf_gain×4][re,im,re2,im2]…` float32. `doppler_spread.m`: Gaussian Doppler PSD `sigma = fd/2`, 100-tap `fir2` at `lowFs = ceil(10·fd)`, filter complex white Gaussian, resample to 8000 (PathSim reference).
- AWGN (`ch.c:232,505`): `No = 10^(NodB/10)·1e6`, `variance = Fs·No`, complex `noise()` = Box-Muller `{gaussian(),gaussian()}` (`ch.c:57-67`). SNR calibration `CNo = 10·log10(tx_pwr/(noise_pwr/Fs))`, and the README's SNR3k = C/No − 10·log10(3000) (`README_data.md` "SNR3k").
- SSB filter: `ssbfilt_coeff.h` (`SSBFILT_N = 100`, centre 1500).

Reproducibility strategy (be honest about which parts are bit-exact):
- **Fading = bit-exact**: reuse codec2's *own* generated MPP file. Generate `fast_fading_samples.float` once via `unittest/fading_files.sh` (needs GNU Octave, on the build host), check it into `samples/freedv/` (≈ 8000·60·4·4 B ≈ 7.7 MB — check in, or gitignore + regenerate in a self-hosted CI step). Same file → same fade realisation as upstream.
- **AWGN = statistical**: C's `rand()`/`RAND_MAX` Box-Muller isn't reproducible in C#; substitute a seeded `System.Random`/xoshiro with the same variance. Parity is therefore Monte-Carlo (many seeds), reported with a confidence interval — not sample-exact.

Validation runs (`Codec2ChannelTests.cs`, likely a dedicated `[Trait("Category","Interop")]` or a new `ChannelModel` category — long-running):
- **MPP parity**: for each mode, transmit ~100 packets, pass through `Codec2Channel(MPP, operatingSnr)`, decode with our RX, count packets-received. **Accept if our count ≥ published within a one-sided binomial CI** of the published p̂ (datac0 70, datac1 92, datac3 74, datac4 90, datac13 90, datac14 90 per 100; SNRs from the table). This is the "channel-model decode-rate parity with FreeDV" gate.
- **AWGN sweep**: PER/BER vs SNR curve; accept if the waterfall knee is within ~1 dB of codec2's published curves (`README_data.md` `doc/c_tx_comp.png`). LDPC's sharp PER knee (`README_data.md:128`) makes ±1 dB a meaningful, tight bound.
- A `Codec2Channel` self-test: unity-gain HT, correct `nhfdelay=16`, measured SNR3k matches the set NodB within tolerance (guards the meter, like `OccupiedBandwidthTests.The_Meter_Agrees_With_A_Known_Signal`).

#### 7. Phasing (grounded in the README use-cases + fixtures)

1. **datac0 — first light.** Shortest burst (0.44 s), smallest FEC (256,128), fewest carriers (Nc=9), 14-byte payload → fastest path to a working end-to-end burst + LDPC + oracle with the least engine surface. Its role (reverse-link ACK) is also the simplest traffic.
2. **datac1 — workhorse.** The forward-link data mode (Nc=27, 510-byte payload, largest LDPC 8192,4096). Do it second specifically because the real off-air `test_datac1_006.raw` gives a *free real-world RX oracle* no other mode has (`README_data.md`), and 1700 Hz OBW is the headline compliance number.
3. **datac3 — low-SNR forward link** (Nc=9 like datac0 but bigger FEC 2048,1024 and 126-byte payload) → reuses datac0's carrier geometry, exercises a second LDPC code.

datac4 / datac13 / datac14 are **Phase 2** (existing task #3): they share the `filtP200S400` BPF + `rx_bpf_en` path and the narrow-mode timing thresholds (0.45–0.5), a distinct engine regime best done as a batch after the Phase-1 three prove the architecture.

#### 8. Per-mode "proven reliable" acceptance (all three legs green)

A mode is *proven reliable* only when:
- **Leg 1 — oracle / bit-exact:** RX recovers exact payload bytes + CRC-valid from codec2 golden bursts at high SNR (zero payload errors); TX matches codec2 golden samples to ≤1 LSB / xcorr ≥ 0.99999; upstream `freedv_data_raw_rx` decodes our TX (manual provenance run). (§4)
- **Leg 2 — channel-model parity:** packets-received on `Codec2Channel` MPP (1 Hz Doppler, 2 ms delay) at the mode's operating SNR ≥ the published figure within a one-sided binomial CI; AWGN PER/BER knee within ~1 dB of codec2's curves. (§6) Plus OBW ≤ published (§5).
- **Leg 3 — real HF loop:** bidirectional over a real radio path (two SSB rigs, or one rig + SDR, through the daemon KISS↔KISS) at the mode's operating SNR, **and** a cross-decode against reference FreeDV on air (our datacN TX decoded by `freedv_data_raw_rx`, and FreeDV's TX decoded by us). `[Trait("Category","HardwareLoop")]`, needs rig time — the "validate full flow, remote==local" discipline (user memory) and the `docs/ninotnc-loop.md` precedent apply.

Legs 1–2 are CI-enforceable on the self-hosted runner; Leg 3 is the manual hardware gate that flips a mode from "oracle-correct" to "proven reliable", exactly as Phase-0 hardware corpus does for the existing modes (`plan.md`).

#### 9. Concrete Phase-1 file/task breakdown

New (mine — integration/validation):
- `src/Packet.SoundModem/Modems/FreeDv/FreeDvDataMode.cs` — the `ofdm_mode.c` table (§2.1), 6 rows, verbatim constants + payload/OBW/SNR.
- `src/Packet.SoundModem/Modems/FreeDv/FreeDvDataModem.cs` — `IModem`/`IConstellationSource` wrapper (§2.2), rate adaptation, KISS payload contract (§2.6).
- `src/Packet.SoundModem/Fec/FreeDvCrc16.cs` — exact `freedv_gen_crc16` port (§2.5) + provenance comment.
- `src/Packet.SoundModem.Daemon/{Program.cs,DaemonConfig.cs}` — switch cases `freedv-datac0/1/3`, 48000 DspRate rule + capture-rate guard, doc comments (§3).

New (DSP sub-agent — engine seam I depend on; listed so the interface is fixed):
- `Modems/FreeDv/OfdmConfig.cs` (port of `OFDM_CONFIG`/`OFDM` derived fields), `OfdmModulator.cs`, `OfdmDemodulator.cs` (`Sync`, `FoffEstHz`, per-packet callback), `OfdmFrame.cs` (preamble/postamble/UW/pilots per `ofdm.c`), `OfdmSync.cs` enum, `Fec/Ldpc/*` (codes `H_128_256_5`, `H_4096_8192_3d`, `H_1024_2048_4f`), and the `filter_coef.h`/`ht_coeff.h`/`ssbfilt_coeff.h` tap tables.

Tests:
- `tests/.../Modems/FreeDv/FreeDvCrc16Tests.cs` — vectors computed from the C algorithm (e.g. CRC of a known 14-byte block).
- `tests/.../Modems/FreeDv/FreeDvOracleTests.cs` — `[Interop]`, the four §4 tests for datac0/1/3.
- `tests/.../Modems/FreeDv/FreeDvLoopbackTests.cs` — our-TX→our-RX bit-exact (unit).
- `tests/.../Dsp/OccupiedBandwidthTests.cs` — extend with the FreeDV OBW table (§5).
- `tests/.../Channel/Codec2Channel.cs` + `Codec2ChannelTests.cs` — the ported model + self-test + MPP/AWGN parity runs (§6).

Fixtures / provenance:
- `samples/freedv/` — `datacN-golden-tx.s16`, `datacN-payload-<hex>.s16`, `test_datac1_006.raw`, `fast_fading_samples.float`, and a `README.md` documenting the codec2 commit SHA + the exact `freedv_data_raw_tx` / `fading_files.sh` commands used (mirrors `samples/README.md`). Add a `PROVENANCE.md` entry recording the codec2 source SHA and the reverse-decode provenance run.
- Optional `tools/Packet.SoundModem.FreeDvOracle/` — a build-host-only harness that shells codec2 binaries to regenerate the golden fixtures (documented as not-run-in-CI).

Docs:
- `docs/freedv-datac.md` — this design (constants, oracle, channel model, acceptance) as the durable reference.
- `docs/plan.md` — add the OFDM/FreeDV phase and an Amendment-log entry (repo discipline, `pdn-soundmodem/CLAUDE.md`/`plan.md`).

Constraints to honour: pure-managed .NET 10, **GPL-3.0-or-later** with per-port provenance comments (`CLAUDE.md` §Licence); no `Version=` on `PackageReference` (CPM); tests xunit + **AwesomeAssertions**, `Snake_Case` names, `TimeProvider` only; zero steady-state allocation / no LINQ in per-sample DSP; self-hosted CI only. No new NuGet dependency is required.

#### 10. Flagged unknowns (do not confabulate — read the source when implementing)

- Exact `samplesperframe`/`samplesperpacket` and the preamble/postamble waveform and pilot allocation: `src/ofdm.c` (not read). The §4 oracle pins them empirically regardless.
- LDPC parity matrices behind `H_128_256_5` / `H_4096_8192_3d` / `H_1024_2048_4f` / `H_256_512_4` / `HRA_56_56`: codec2 generated headers (not read) — the DSP sub-agent owns these.
- FIR tap values: `ht_coeff.h` (HT_N=257), `ssbfilt_coeff.h` (SSBFILT_N=100, centre 1500), `filter_coef.h` (`filtP400S600`/`filtP900S1100`/`filtP200S400`) — sizes/defines confirmed, tap arrays not transcribed.
- `fdmdv_freq_shift_coh` (used by the channel foff injection): `src/fdmdv.c` (not read).
- `octave/doppler_spread.m` was read (Gaussian PSD, `fir2`/`resample`) but the exact `fir2`/`resample` numerics are Octave-specific — hence the recommendation to reuse codec2's generated `fast_fading_samples.float` rather than re-implement the generator, keeping the fade bit-exact.


---

## Open risks & critical review

- **The sync/timing/channel-estimation state machine is the make-or-break block** (Demodulator
  chapter). Gate its acceptance on the oracle across the full datac0/1/3 payload set **and** the
  FreeDV MPP channel model — not just a clean AWGN loopback.
- **Sample-clock offset** between two independent sound cards (a few ppm) rotates phase across
  subcarriers and walks symbol timing; the demod must track it (pilot-aided). This is the item
  most likely to pass in loopback and fail on real hardware — test it explicitly with a deliberate
  ppm offset.
- **datac4/datac13 shortening must be bit-exact** — a same-rate code with the shortening skipped
  will **not** interoperate. The oracle must cover a shortened mode before Phase 2 is "done".
- **CRC placement, the LCG preamble seeds, and the golden-prime constant** are silent-failure
  points: wrong and nothing decodes, with no partial signal to debug. Assert each against a
  `libcodec2`-generated vector as a standalone unit test, independent of the end-to-end path.
- **No oracle for our OWN modes.** The oracle covers the FreeDV modes only; when the shared engine
  is later reused for greenfield FM/HF (tasks #8/#9) there is no external reference — plan
  self-loopback + channel-sim + a real radio loop as the gate there (noted so it is not a surprise).
- **OBW is CI-enforced per mode from day one** — each datac mode must measure within its published
  occupied bandwidth (datac0/3 = 500 Hz, datac1 = 1700 Hz, datac4 = 250 Hz, datac13 = 200 Hz,
  datac14 = 250 Hz), extending `OccupiedBandwidthTests`.

## Phase-1 acceptance ("proven reliable", not "only just working")
A datac mode is *done* only when: (1) bit-exact vs the `libcodec2` oracle on a payload sweep;
(2) decode-rate parity with FreeDV's published MPP figures on the Watterson channel model; (3) OBW
within the published limit; and (4) — for the release gate — validated over a real HF radio loop.

## Phase-1 build order
1. Shared OFDM engine skeleton (IFFT+CP modulator, FFT demod front end) on the existing `Fft`.
2. LDPC port + the H-matrix transliteration script (mechanical, oracle-checked first).
3. Framing (interleaver / UW / preamble / CRC) with per-item unit vectors from `libcodec2`.
4. **datac0 end-to-end first-light** over AWGN, oracle-validated.
5. The sync / channel-estimation state machine → datac0 over the MPP model.
6. datac1 + datac3; OBW tests. (The KISS/IModem integration is task #4.)
