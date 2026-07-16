# FreeDV `datac` OFDM in pdn-soundmodem — Phase 1 design

Status: design, implementation-ready. Target: `.NET 10`, pure-managed, **GPL-3.0-or-later**, matching pdn-soundmodem idioms. This document becomes `docs/ofdm-design.md`.

**Source provenance.** Every codec2 citation below is to the shallow reference clone used during design — **codec2 1.2.0, git `310777b1c6f1af0bc7c72f5b32f80f6fd9136962`** — cited as `file:line` / `file:function` relative to its `src/`. pdn-soundmodem citations are relative to `src/Packet.SoundModem/` and `tests/Packet.SoundModem.Tests/`. Do **not** re-clone or rebuild codec2 to read these; the line numbers are pinned to that commit.

The six `datac{0,1,3,4,13,14}` modes are all **OFDM** with **QPSK per subcarrier** (`config->bps = 2`, `ofdm_mode.c:35`), **Fs = 8000 Hz** (`ofdm_mode.c:33`), **tx/rx centre 1500 Hz** (`ofdm_mode.c:31-32`), `ns = 5`, `edge_pilots = 0`, `txtbits = 0`, `state_machine = "data"`, `data_mode = "streaming"`. "QPSK @ 8000/1500" is the per-carrier constellation — these are **not** single-carrier QPSK, so the existing `Modems/QpskModem` is **not** reused.

---

## 1. Overview, goals, and the port-vs-wrap decision

### 1.1 Goal

Ship a pure-managed C# modem family in pdn-soundmodem that is **interop-exact** with FreeDV `freedv_data_raw_tx`/`freedv_data_raw_rx` for all six `datac` modes: our TX audio decodes in reference FreeDV, and reference FreeDV TX audio decodes in ours, bit-for-bit at the payload/CRC layer. Phase 1 lands the shared OFDM engine + datac0/datac1/datac3 end-to-end (modulate → LDPC → framing → demodulate → CRC) plus the test-oracle apparatus; datac4/13/14 are Phase 2 (they share a distinct narrow-BPF regime).

### 1.2 The decision: **port**, not wrap

We **transliterate** the codec2 DSP into managed C#; libcodec2 is used **only** as a test oracle, linked into the test assembly, never a runtime/shipped dependency.

| | Port (chosen) | Wrap libcodec2 |
|---|---|---|
| Runtime dep | none — pure managed | native `libcodec2.so` in every `.deb`/NuGet |
| Cross-platform | trivially (managed) | per-arch native builds, P/Invoke marshalling |
| Fits pdn idioms | yes (`IModem`, `Dsp/*`, `Fec/*`) | foreign — no other modem wraps native |
| Greenfield reuse (§2) | engine is ours to re-parameterise | opaque |
| Cost | high — must reproduce float DSP exactly | low |
| Interop risk | mitigated by the oracle (§7) | n/a |

The port carries a real cost — reproducing single-precision float DSP — which the oracle (§7) and the tiered tolerance policy (§7.5) exist to contain.

### 1.3 Interop-exactness — what "bit-for-bit" actually means (honest caveat)

Bit-for-bit is achievable in **algorithm, constants, table values, and arithmetic ordering**, but **not** as literal IEEE-754 equality of intermediate floats, because codec2 uses C `cosf`/`sinf`/`cabsf`/`cargf`/`atan2f`/`hypotf` (`ofdm_internal.h:51-52` `cmplx`/`cmplxconj`) whose last-ULP results differ from .NET `MathF`. Therefore the exactness contract is **tiered** (defined once, §7.5, applied everywhere):

- **Integer layers are exact, no tolerance:** framing, MSB-first pack/unpack, CRC-16, UW placement, `uw_ind_sym[]`, interleaver indices, all sample counts, returned-byte-count, `rx_status`, and — critically — **decoded payload bytes + CRC-valid decision on high-SNR clean input.**
- **Float waveforms/LLRs are tolerance-matched:** TX `.s16` within a named per-mode LSB/xcorr tolerance; `rx_np`/`rx_amp`/LLR within relative tolerance.
- **Noisy/faded inputs are statistical only:** never sample- or bit-exact (a single differently-rounded float can flip a marginal LLR sign → different hard decision). Assert CRC-valid + PER within a binomial CI.

### 1.4 LGPL-2.1 lineage — flag for legal review

- FreeDV API + codec2 DSP are **LGPL-2.1** (`freedv_api.h:22-31`, `freedv_api.c`, `ofdm.c` headers). pdn-soundmodem is **GPL-3.0-or-later**.
- **Test-only dynamic linking** of `libcodec2.so` from the test assembly is unambiguously fine (GPL-3 is a compatible downstream of LGPL-2.1; nothing vendored/redistributed).
- **The port itself is the legal question:** transliterating LGPL-2.1 algorithms/tables into GPL-3.0-or-later source produces a **derivative work of LGPL-2.1 code**. GPL-3-or-later is a permitted relicensing target for LGPL-2.1 (LGPL-2.1 §3 → GPL-2-or-later → GPL-3), so this is very likely clean — **but it must be reviewed and the relicensing basis recorded** before merge, with per-file provenance headers citing the codec2 source file + commit and the LGPL-2.1→GPL relicensing clause. **Open item R-1 (§10).**
- No codec2 `.c`/`.h` is copied into the repo; we transliterate and cite. Checked-in `.s16`/fading vectors are *generated output* (facts/measurements), committed like the existing NinoTNC/Dire Wolf WAV fixtures, with a `PROVENANCE.md` row (codec2 SHA + exact commands).

### 1.5 Goals / non-goals

**In scope (Phase 1):** shared OFDM engine; modulator; demodulator + streaming sync; LDPC codec + H-matrix port; framing (interleaver/UW/preamble/CRC/shortening); test-oracle + checked-in vectors; `IModem` integration for datac0/1/3; OBW + channel-model validation harness.
**Out of scope:** ARQ/segmentation/addressing (that is the FreeDATA/Mercury layer above the raw KISS boundary); voice modes (700D/2020); the SDL/AX.25 stack. **Phase 2:** datac4/13/14 (narrow `filtP200S400` BPF + `rx_bpf` regime).

### 1.6 Critique-fix traceability (every finding folded in)

| # | Sev | Finding | Folded into | Verified vs source |
|---|---|---|---|---|
| 1 | blocker | RX BPF datac4/13/14 must be ported exactly, not approximated | §3.7, §4.9 | ✓ `ofdm.c:1467-1471,1535-1539,570-575` |
| 2 | major | `max_star0` AJIAN/TJIAN double promotion | §5.4, §5.6, §4.11 | ✓ `mpdecode_core.c:122-139` |
| 3 | major | `init_c_v_nodes` edge/socket ordering verbatim | §5.6 | ✓ `mpdecode_core.c:142-353,480-486` |
| 4 | major | exactness must be tiered by SNR | §1.3, §7.5, §8.6 | (policy) |
| 5 | major | OBW pinned to measured golden, not README | §8.5 | ✓ `README_data.md:144-149` |
| 6 | major | golden-fixture provenance (silence) inconsistency | §7.4 | ✓ `freedv_data_raw_tx.c:344-355,395-404` |
| 7 | major | 48k↔8k resampler outside bit-exact envelope | §2.4, §8.3, §8.6 | ✓ `freedv_700.c:246` |
| 8 | minor | `disassemble_..._with_text_amps` + amp deinterleave | §4.10, §6.1 | ✓ `freedv_700.c:493-502` |
| 9 | minor | datac3 & datac4 have no standalone LDPC vector | §5.9, §8.4 | ✓ `H_1024_2048_4f.c` (no `_input`) |
| 10 | minor | oracle `Demodulate` must handle `nin==0` | §7.3 | ✓ `ofdm.c:1956-1961` |
| 11 | minor | `quisk_ccfFilter` equivalence rationale corrected | §3.7 | ✓ `filter.c:53,263-288` |
| 12 | minor | BPF centre: float-sum `find_carrier_centre` vs closed form | §3.7 | ✓ `ofdm.c:570-575` |
| 13 | minor | `MathF.Round` must use `AwayFromZero` for `tx_nlower` | §3.4 | ✓ `ofdm.c:376` |

---

## 2. Architecture — the shared OFDM engine

### 2.1 Component boundary

```
                       ┌─────────────────────── FreeDvDataModem : IModem (§8) ───────────────────────┐
KISS frame ──payload──▶│  CRC16 append │ pack │ LDPC.Encode │ QPSK-map │ GP-interleave │ UW-assemble │ OFDM-modulate │──audio▶
                       │      §6            §6       §5          §6           §6              §6            §3        │
                       │  ◀CRC check ◀ unpack ◀ LDPC.Decode ◀ symbols→LLR ◀ GP-deinterleave ◀ UW-disassemble ◀ OFDM-demod+sync │
                       │      §6            §6       §5          §4              §6                §6            §4         │
                       └───────────────────────────────────────────────────────────────────────────────────────────────┘
                                                              ▲ pinned by ▼
                                         libcodec2 test-oracle + checked-in .s16 vectors (§7)
```

Sub-components (each its own §): **Modulator** (§3), **Demodulator + sync** (§4), **LDPC** (§5), **Framing** (§6), **Oracle** (§7), **Integration/validation** (§8). Namespaces: `Packet.SoundModem.Ofdm` (engine), `Packet.SoundModem.FreeDv` (framing), `Packet.SoundModem.Fec.Ldpc` (codec), `Packet.SoundModem.Modems.FreeDv` (`IModem` wrapper).

### 2.2 The engine is parameterised by one immutable per-mode record

A single OFDM engine drives all six modes via an `OfdmMode`/`OfdmModeConfig` record (§3.4, §4.3). All primary constants come verbatim from `ofdm_mode.c:122-275`; all sizes are derived by the exact `ofdm_create` formulas (`ofdm.c:247-318,374-377`). This mirrors codec2's own "one engine + a mode table" shape.

### 2.3 Reusable DSP primitives (the greenfield-shared surface)

These are written mode-agnostic so a future greenfield FM/HF OFDM waveform can re-parameterise them:

- **Direct per-symbol DFT/iDFT** over `Nc+2` occupied bins (`ofdm.c:642-692`) — **not** a radix-2 FFT (§3.0).
- **Pilot-correlation timing** `EstTiming` + `TimingNorm` normalisation (§4.6a); **coarse-freq DFT-peak** `EstFreqOffsetPilotCorr` (§4.6c).
- **Known-sequence burst detector** `est_timing_and_freq` (§4.7) — generic joint timing+freq matched filter.
- **Per-carrier pilot phase/channel estimation**, both `high_bw` and `low_bw` (§4.8d) — the Doppler-vs-SNR knob (`PhaseEstBandwidth`).
- **Integer sample-clock tracking** (§4.8f) — no fractional interpolator; robustness "for free".
- **QPSK soft-demap** `Demod2D`/`Somap`/`MaxStar0` (§4.11) + **golden-prime interleaver** (§6.4) — code-agnostic.
- **Sync state machine** `Search/Trial/Synced` with UW confirmation (§4.12).
- **`ComplexBandpassFir`** (`quisk_cfTune`/`quisk_ccfFilter` port) — shared by tx BPF (§3.7) and rx BPF (§4.9).

### 2.4 Greenfield FM/HF reuse — what changes

**Reuse as-is (re-parameterise via config):** the DFT/iDFT engine (any `Nc,M`), pilot timing + coarse-freq, per-carrier phase est, integer clock tracking, QPSK soft-demap, GP interleaver, burst detector, sync skeleton, Es/No & SNR estimators (`ofdm.c:1967-2007`).
**Must change per greenfield mode:** the `{−40,0,+40}` coarse-freq grid and 40 Hz `wval` table (tuned to HF drift on 8 kHz/62.5 Hz-Rs — collapse to `fcoarse=0` for crystal-stable FM); LDPC codes; the `filtP*` BPF prototypes (HF-narrowband); the `edge_pilots=0`/`pilotvalues` choice and exact UW (FreeDV-interop constraints — a greenfield mode picks its own, but then keep the code paths separate so FreeDV compatibility is not silently broken).
**The 8 kHz↔channel-rate resampler (§8.3, fix 7) is a per-deployment concern**, not part of the engine.

---

## 3. Modulator

### 3.0 The IFFT is **not** pdn's `Fft` — port the direct iDFT

codec2 modulates with a hand-rolled inverse DFT, `idft` (`ofdm.c:642-667`), summing over only `Nc+2` occupied bins with a per-row phasor recurrence (`c *= delta`), called from `ofdm_txframe` (`ofdm.c:1004`). Port **that**, not a radix-2 FFT:

1. **Correctness:** `Dsp/Fft.Forward` (`Dsp/Fft.cs:11`) throws for non-power-of-two lengths. Five modes have `M=128`, but **datac14 has `M=144`** — a radix-2 IFFT cannot do it.
2. **Bit-exactness:** even at `M=128`, a butterfly cascade produces a different float rounding sequence than codec2's `sum += vector[col]*c; c *= delta` accumulation over ≤29 bins.
3. **Cost is a non-issue:** `O(M·(Nc+2))` with `Nc+2 ≤ 29` beats a 128-point FFT over a mostly-zero spectrum.

`Dsp/Fft.Forward` is still correct for the OBW meter and any analysis path — but it is **not** the oracle path. An `Fft`-based iDFT helper (conjugate trick) is provided for OB measurement/prototyping only, flagged non-oracle and unusable for datac14.

### 3.1 Component boundary

Real TX pipeline (`interldpc.c:322` `ofdm_ldpc_interleave_tx`): `LDPC encode → QPSK-map → GP-interleave → ofdm_assemble_qpsk_modem_packet_symbols → ofdm_txframe → ofdm_hilbert_clipper`. **This component owns** the last three stages + preamble/postamble (`ofdm_generate_preamble`, `ofdm.c:2592`) + burst assembly (`freedv_data_raw_tx.c`, `freedv_api.c:519-591`). LDPC (§5), GP-interleave/CRC (§6) are siblings; the modulator accepts already-assembled complex symbols (or raw packet bits for the test path).

### 3.2 Exact per-mode parameter table

Fixed for all six (`ofdm_mode.c` + `ofdm_create`); RX-only fields carried for parity but consumed by §4.

| Param | datac0 | datac1 | datac3 | datac4 | datac13 | datac14 |
|---|---|---|---|---|---|---|
| set at (`ofdm_mode.c`) | :122 | :145 | :169 | :195 | :222 | :249 |
| `Nc` | 9 | 27 | 9 | 4 | 3 | 4 |
| `Ns` | 5 | 5 | 5 | 5 | 5 | 5 |
| `Np` | 4 | 38 | 29 | 47 | 18 | 4 |
| `Ts` (s) | .016 | .016 | .016 | .016 | .016 | **.018** |
| `Tcp` (s) | .006 | .006 | .006 | .006 | .006 | **.005** |
| `Nuwbits` | 32 | 16 | 40 | 32 | 48 | 32 |
| `bad_uw_errors` | 9 | 6 | 10 | 12 | 18 | 12 |
| `timing_mx_thresh` | 0.08 | 0.10 | 0.10 | 0.5 | 0.45 | 0.45 |
| `amp_scale` | 300e3 | 145e3 | 300e3 | 2·300e3 | 2.5·300e3 | 2.0·300e3 |
| `clip_gain1` | 2.2 | 2.7 | 2.2 | 1.2 | 1.2 | 2.0 |
| `clip_gain2` | 0.85 | 0.8 | 0.8 | 1.0 | 1.0 | 1.0 |
| `rx_bpf_en` | false | false | false | true | true | true |
| tx BPF proto | filtP400S600 | filtP900S1100 | filtP400S600 | filtP200S400 | filtP200S400 | filtP200S400 |
| `Rs = 1/Ts` | 62.5 | 62.5 | 62.5 | 62.5 | 62.5 | 55.5556 |
| `M = (int)(Fs·Ts)` (`:248`) | 128 | 128 | 128 | 128 | 128 | **144** |
| `Ncp = (int)(Tcp·Fs)` (`:249`) | 48 | 48 | 48 | 48 | 48 | **40** |
| `SamplesPerSymbol = M+Ncp` (`:303`) | 176 | 176 | 176 | 176 | 176 | 184 |
| `SamplesPerFrame = Ns·(M+Ncp)` (`:304`) | 880 | 880 | 880 | 880 | 880 | 920 |
| `SamplesPerPacket = Np·SamplesPerFrame` | 3520 | 33440 | 25520 | 41360 | 15840 | 3680 |
| `BitsPerFrame = (Ns−1)·Nc·2` (`:297`) | 72 | 216 | 72 | 32 | 24 | 32 |
| `BitsPerPacket = Np·BitsPerFrame` (`:299`) | 288 | 8208 | 2088 | 1504 | 432 | 128 |
| `SymsPerPacket = BitsPerPacket/2` | 144 | 4104 | 1044 | 752 | 216 | 64 |
| `tx_nlower` (`:376`) | 19 | 10 | 19 | 21 | 22 | 24 |

`amp_scale` are literal C expressions (`ofdm_mode.c:216,243,270`). **tx_uw words:** datac0/datac1 use the 16-symbol word `{1,1,0,0,1,0,1,0,1,1,1,1,0,0,0,0}` (datac0 zero-padded to 32; datac1 exactly 16). datac3/4/13/14 use the 24-bit word copied to front and tail (`ofdm_mode.c:187-188,213-214,240-241,267-268`); store each mode's resolved word verbatim (§6.5a gives every resolved array).

### 3.3 Carrier → bin mapping (exact)

`ofdm_create` (`ofdm.c:374-376`): `doc = 2π/M`; `tx_nlower = roundf(tx_centre/Rs − Nc/2) − 1` (C `roundf` = half-away-from-zero). `idft` (`ofdm.c:655`) places column `col∈[0,Nc+1]` at bin `k = tx_nlower+col`, audio `f = k·Rs`. `col 0` and `col Nc+1` are edge pilots — **zeroed** (`edge_pilots=0`, `ofdm.c:369-370`); data/pilot energy in `cols 1..Nc`.

| Mode | tx_nlower | data bins | data freqs (Hz) | group centre |
|---|---|---|---|---|
| datac0/3 | 19 | 20…28 | 1250…1750 | 1500 |
| datac1 | 10 | 11…37 | 687.5…2312.5 | 1500 |
| datac4 | 21 | 22…25 | 1375…1562.5 | 1468.75 |
| datac13 | 22 | 23…25 | 1437.5…1562.5 | 1500 |
| datac14 | 24 | 25…28 | 1388.9…1555.6 | 1472.2 |

The **even-`Nc` half-bin offset** (datac4, datac14 sit ½ carrier below 1500) is exactly what `roundf(…)−1` yields — reproduce, don't "correct".

### 3.4 Class design (with the `AwayFromZero` fix baked in — fix 13)

```csharp
namespace Packet.SoundModem.Ofdm;

public sealed record OfdmMode
{
    public required string Name { get; init; }
    public required int Nc { get; init; }
    public required int Ns { get; init; }
    public required int Np { get; init; }
    public required double Ts { get; init; }
    public required double Tcp { get; init; }
    public required int Nuwbits { get; init; }
    public required byte[] TxUw { get; init; }            // length == Nuwbits
    public required double AmpScale { get; init; }
    public required double ClipGain1 { get; init; }
    public required double ClipGain2 { get; init; }
    public required float[] TxBpfProto { get; init; }     // 100-tap real LP prototype
    public required bool RxBpfEnabled { get; init; }
    public double Fs { get; init; } = 8000.0;
    public double TxCentre { get; init; } = 1500.0;
    public int    Bps { get; init; } = 2;

    public double Rs               => 1.0 / Ts;
    public int    M                => (int)(Fs / Rs);                     // ofdm.c:248
    public int    Ncp              => (int)(Tcp * Fs);                    // ofdm.c:249
    public int    SamplesPerSymbol => M + Ncp;                           // :303
    public int    SamplesPerFrame  => Ns * SamplesPerSymbol;             // :304
    public int    SamplesPerPacket => Np * SamplesPerFrame;
    public int    BitsPerFrame     => (Ns - 1) * Nc * Bps;               // :297
    public int    BitsPerPacket    => Np * BitsPerFrame;                 // :299
    public int    SymsPerPacket    => BitsPerPacket / Bps;
    public double Doc              => (2.0 * Math.PI) / (Fs / Rs);       // :374

    // FIX 13: C roundf is half-AWAY-from-zero. The load-bearing halves are
    // 19.5→20 (datac0/3), 10.5→11 (datac1), 22.5→23 (datac13); banker's rounding
    // would put datac1/datac13 carriers on the wrong bin. Pin with a unit test
    // asserting {19,10,19,21,22,24}.
    public int TxNlower => (int)MathF.Round((float)(TxCentre / Rs) - Nc / 2.0f,
                                            MidpointRounding.AwayFromZero) - 1;

    public static OfdmMode Datac0()  => …;   // one static factory per mode, §3.2 values
    public static OfdmMode Datac1()  => …;
    public static OfdmMode Datac3()  => …;
    public static OfdmMode Datac4()  => …;
    public static OfdmMode Datac13() => …;
    public static OfdmMode Datac14() => …;
}

public readonly record struct Cf(float Re, float Im) { /* +,-,* operators; MathF hot path */ }

public sealed class OfdmModulator
{
    public OfdmModulator(OfdmMode mode);
    public OfdmMode Mode { get; }

    public Cf[] AssembleModemPacket(ReadOnlySpan<Cf> payloadSyms);          // ofdm.c:2412
    public void ModulatePacket(ReadOnlySpan<Cf> modemPacketSyms, Span<Cf> outSamples); // len SamplesPerPacket
    public Cf[] ModulatePacketBits(ReadOnlySpan<byte> packetBits);         // test path (ofdm_mod)
    public Cf[] EmitBurst(IReadOnlyList<Cf[]> assembledPackets, bool resetFilter = true);

    public ReadOnlyMemory<Cf> PreambleRaw  { get; }   // len SamplesPerFrame, seed 2
    public ReadOnlyMemory<Cf> PostambleRaw { get; }   // len SamplesPerFrame, seed 3
}
```

Use parallel `float[]` re/im in the hottest inner loops to match `Fft.Forward(Span<float> real, Span<float> imaginary)`; `Cf` for symbol-level clarity. Never promote to `System.Numerics.Complex` (double) in the hot path.

### 3.5 Modem-frame symbol assembly (exact)

**QPSK map** (`ofdm.c:76,106`): `qpsk[4] = {1+0j, 0+1j, 0−1j, −1+0j}`, symbol = `qpsk[(b0<<1)|b1]`. Verified via `ofdm_mod` (`ofdm.c:1194-1197`): first bit is the index MSB → `00→1, 01→j, 10→−j, 11→−1`.

**Grid placement** (`ofdm_txframe:1023-1039`): build `aframe[Np·Ns][Nc+2]` zero-filled; row `r` is a **pilot row** when `r%Ns==0` (copy `pilots[0..Nc+1]`), else a **data row** (next `Nc` input symbols into `cols 1..Nc`). Input consumed row-major; length `Np·(Ns−1)·Nc = SymsPerPacket`. `dpsk_en` is always false for datac (the `aframe[r][j]*=aframe[r−1][j]` branch at `:1035` is dead).

**Pilots** (`ofdm_create:360-371`): `pilots[i] = (float)pilotvalues[i]`, then `pilots[0]=pilots[Nc+1]=0`. Port `pilotvalues[64]` verbatim (`ofdm.c:88-92`); a mode uses the first `Nc+2`.

**UW + payload interleave** (`ofdm_assemble_qpsk_modem_packet_symbols:2412`): scatter `Nuwsyms=Nuwbits/2` UW symbols at `uw_ind_sym[]` (computed in `ofdm_create:445-463`, given resolved in §6.5c), payload symbols fill the rest. `tx_uw_syms[k] = qpsk[(tx_uw[2k]<<1)|tx_uw[2k+1]]` (`ofdm_create:475-480`). Precompute `tx_uw_syms` and `uw_ind_sym` in the ctor.

### 3.6 iDFT + cyclic prefix (the oracle path)

```csharp
// vector = one row of Nc+2 complex bins; result = M complex time samples (ofdm.c:642-667)
void Idft(ReadOnlySpan<Cf> vector, Span<Cf> result) {
    float invM = 1f / M;
    Cf acc0 = default; for (int c = 0; c < Nc + 2; c++) acc0 += vector[c];
    result[0] = acc0 * invM;                                    // DC == scaled sum
    for (int row = 1; row < M; row++) {
        float a0 = (float)(TxNlower * Doc * row), d = (float)(Doc * row);
        Cf c     = new(MathF.Cos(a0), MathF.Sin(a0));           // cmplx(tx_nlower*doc*row)
        Cf delta = new(MathF.Cos(d),  MathF.Sin(d));            // cmplx(doc*row)
        Cf acc = default;
        for (int col = 0; col < Nc + 2; col++) { acc += vector[col] * c; c *= delta; }
        result[row] = acc * invM;
    }
}
```

Preserve the `c *= delta` recurrence and accumulation order exactly. **CP + concat** (`ofdm_txframe:1043-1064`): per row, `out[0..Ncp-1]=sym[M-Ncp..M-1]`, `out[Ncp..Ncp+M-1]=sym[0..M-1]`; concatenate all `Np·Ns` rows → `SamplesPerPacket`, then run §3.7.

### 3.7 Hilbert clipper / scaling / BPF chain (exact)

`ofdm_hilbert_clipper` (`ofdm.c:1072-1100`), applied to every emitted block (preamble, each packet, postamble), `OFDM_PEAK = 16384` (`codec2_ofdm.h:45`):

1. `tx[i] *= amp_scale`.
2. `clip_en` (true): `tx[i] *= clip_gain1`; `ofdm_clip(tx, 16384)`.
3. `tx_bpf_en` (true): complex band-pass FIR, in place.
4. `tx_bpf_en && clip_en`: `tx[i] *= clip_gain2`.
5. `ofdm_clip(tx, 16384)` (final).

`ofdm_clip` (`ofdm.c:2683`): soft magnitude clip `if |sam|>thresh: sam *= thresh/|sam|` (uses `cabsf`).

**`ComplexBandpassFir` — port `quisk_cfTune` + `quisk_ccfFilter` (`filter.c:232-247,263-288`). This class is shared with the rx BPF (§4.9, fix 1).**

- **Init** (`quisk_filt_cfInit`, `filter.c:50-53`): copies `dCoefs` **verbatim, no normalisation** (`:50`) and **zeroes `cSamples`** (`:53`). Port the 100 taps as-is.
- **Tune once** (`filter.c:243-246`): `cpxCoefs[i] = cmplx(2π·freq·(i−D)) · dCoefs[i]`, `D = (nTaps−1)/2 = 49.5`, `freq = centre/Fs`.
  - **centre — fix 12:** datac0/1/3 tune to **literal `TxCentre = 1500.0`** (`find_carrier_centre` is only called for datac4/13/14 — `allocate_rx_bpf` asserts otherwise, `ofdm.c:585-593`). datac4/13/14 tune to `find_carrier_centre` (`ofdm.c:570-575`), which is a **float summation** `Σ_{c=0..Nc+1}(tx_nlower+c)·doc`, `×(Fs/2π)/(Nc+2)` — **replicate the summation, not the algebraically-equal closed form**, so the tuned `cpxCoefs` match in the low bits. Numerically: datac4 ≈ 1468.75, datac13 ≈ 1500, datac14 ≈ 1472.22 Hz.
- **Filter — fix 11 (corrected rationale):** `quisk_ccfFilter` (`filter.c:263-288`) is a stateful complex FIR over a circular history (newest at `ptcSamp`, walk `k` backward over samples, forward over coefs). It equals ordinary convolution `y[n] = Σ_k cpxCoefs[k]·x[n−k]` **because of that circular-buffer index arithmetic with zero-initialised history — NOT because the coefficients are symmetric** (the tuned `cpxCoefs` are not symmetric; do not add a symmetry "optimization"). Implement as a new `ComplexBandpassFir` mirroring `Dsp/FirFilter.cs`'s circular buffer, but complex-in/out with complex taps.

**tx BPF prototypes to port verbatim (100 floats each, `filter.h:42-47` declares `[100]`):** `filtP400S600` (`filter_coef.h:95`, datac0/3), `filtP900S1100` (`filter_coef.h:186`, datac1), `filtP200S400` (`filter_coef.h:279`, datac4/13/14).

**State persistence is interop-critical (§3.9):** one persistent `ComplexBandpassFir` per direction; the ~49-sample group-delay tail is never flushed between segments — that asymmetry is part of the reference waveform.

### 3.8 Preamble / postamble (exact)

`ofdm_create:532-538` builds them once via `ofdm_generate_preamble(ofdm, buf, seed)`, **seed 2 (preamble)**, **seed 3 (postamble)**. `ofdm_generate_preamble` (`ofdm.c:2592-2609`): force `Np=1`, generate `bitsperframe` bits from the LCG (`ofdm_rand_seed`, `ofdm.c:2574-2578`): `seed = (1103515245·seed + 12345) % 32768; bit = seed > 16384` (64-bit intermediate), then run `ofdm_mod` with `amp_scale=1, tx_bpf_en=false, clip_en=false` → the stored frame is the **raw idft+CP output**, length `SamplesPerFrame`. Content is deterministic per mode; store both raw `Cf[SamplesPerFrame]`.

**At emit** (`freedv_api.c:519-591`): the stored raw frame is re-run through the **real** `ofdm_hilbert_clipper` (full scale/clip/**persistent BPF**/gain2/final clip) — preamble/postamble come out at the same level and through the same filter as data.

### 3.9 Burst assembly + BPF state

`freedv_data_raw_tx.c:370-405`: `[preamble][data packet]×frames[postamble]` (+ trailing silence, not filtered). Each block goes through `ofdm_hilbert_clipper`, all sharing one persistent BPF. `EmitBurst(resetFilter:true)` zeroes the BPF (matches a fresh single-burst codec2 process); `resetFilter:false` matches burst N>1 of a continuous run. `EmitBurst` routes preamble/postamble re-clip and data-packet clip through the **same** `ComplexBandpassFir` field.

### 3.10 Constants to port verbatim

| Constant | Source |
|---|---|
| `qpsk[4]={1,j,−j,−1}` | `ofdm.c:76` |
| `pilotvalues[64]` | `ofdm.c:88-92` |
| `OFDM_PEAK=16384` | `codec2_ofdm.h:45` |
| LCG `1103515245`,`12345`,mod`32768` | `ofdm.c:2576` (seeds 2/3) |
| `filtP400S600[100]` | `filter_coef.h:95` |
| `filtP900S1100[100]` | `filter_coef.h:186` |
| `filtP200S400[100]` | `filter_coef.h:279` |
| per-mode `tx_uw[]` | `ofdm_mode.c` / §6.5a |

---

## 4. Demodulator + sync state machine

### 4.0 The `amp_est_mode` overwrite (interop-critical, easy to get wrong)

In `ofdm_demod_core`, `ofdm.c:1859-1860` **unconditionally** overwrite the per-carrier estimates after both branches:
```c
1859    aphase_est_pilot[i] = cargf(aphase_est_pilot_rect);
1860    aamp_est_pilot[i]   = cabsf(aphase_est_pilot_rect);
```
So `amp_est_mode` is **dead code** in this revision — amplitude is always `|aphase_est_pilot_rect|` and phase always `arg(aphase_est_pilot_rect)`, where `rect` is the `low_bw` 12-pilot or `high_bw` 2-pilot average. A "faithful" reading of the `amp_est_mode==1` block would silently diverge. Reproduce: compute the branch, then overwrite. (Also note `cabsf(a)+cabsf(b)/2.0` at `:1854-1855` is `|a|+|b|/2`, not an average — but dead anyway.) **Verified `ofdm.c:1855-1862`.**

### 4.1 Exact mode parameter table (RX view)

Derived as §3.2 plus RX-only fields (`ftwindowwidth=80` all modes; `codename`; thresholds; `rx_bpf`):

| mode | nc | ns | np | m | Ncp | SpS | SpF | bits/pkt | nuwbits | bad_uw | mx_thresh | codename | rx_bpf |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| datac0 | 9 | 5 | 4 | 128 | 48 | 176 | 880 | 288 | 32 | 9 | 0.08 | H_128_256_5 | false |
| datac1 | 27 | 5 | 38 | 128 | 48 | 176 | 880 | 8208 | 16 | 6 | 0.10 | H_4096_8192_3d | false |
| datac3 | 9 | 5 | 29 | 128 | 48 | 176 | 880 | 2088 | 40 | 10 | 0.10 | H_1024_2048_4f | false |
| datac4 | 4 | 5 | 47 | 128 | 48 | 176 | 880 | 1504 | 32 | 12 | 0.50 | H_1024_2048_4f | **true** |
| datac13 | 3 | 5 | 18 | 128 | 48 | 176 | 880 | 432 | 48 | 18 | 0.45 | H_256_512_4 | **true** |
| datac14 | 4 | 5 | 4 | 144 | 40 | 184 | 920 | 128 | 32 | 12 | 0.45 | HRA_56_56 | **true** |

`RxNlower` = `TxNlower` (tx_centre==rx_centre): {19,10,19,21,22,24}.

### 4.2 Fixed tables to port

`qpsk[4]` (`ofdm.c:76`), `pilotvalues[64]` (`ofdm.c:88-92`), `ofdm_wval[200]` = `exp(-j·2π·40·i/8000)` (`wval.h`; regenerate as `MathF.Cos/Sin`, first `SamplesPerSymbol≤184` used), `S_matrix[4]={(1,0),(0,1),(0,-1),(-1,0)}` (`mpdecode_core.c:32-33`). Constants: `TAU=2π`, `ROT45=π/4` (`ofdm_internal.h:47-48`); `AJIAN=-0.24904163195436`, `TJIAN=2.50681740420944` (`mpdecode_core.c:122-123`); `foff_est_gain=0.1` (`ofdm.c:417`); `EsNo=3.0f` (`ofdm_demod.c:411`, `freedv_700.c:451`).

### 4.3 Class architecture

```csharp
public enum SyncState { Search, Trial, Synced }        // ofdm_internal.h:55
public enum PhaseEstBandwidth { Low, High }            // ofdm_internal.h:65-68

public sealed class OfdmDemodulator
{
    public OfdmDemodulator(OfdmMode cfg);
    public int Nin { get; private set; }               // may be 0 (§4.5)
    public SyncState State { get; private set; }
    public bool SyncSearch(ReadOnlySpan<Cf> input);    // ofdm_sync_search; returns timing_valid
    public void Demod(ReadOnlySpan<Cf> input);         // ofdm_demod
    public ReadOnlySpan<Cf>    RxNp  { get; }
    public ReadOnlySpan<float> RxAmp { get; }
    public float MeanAmp { get; }
    public float FoffEstHz { get; }
    public int   TimingEst { get; }  public float TimingMx { get; }
    public int   ModemFrame { get; } public int Nuwframes { get; }
    public void  SyncStateMachine(ReadOnlySpan<byte> rxUw);
    public bool  SyncStart { get; }
}

public sealed class OfdmPacketAssembler
{
    public void PushFrame(ReadOnlySpan<Cf> rxNp, ReadOnlySpan<float> rxAmp);
    public void ExtractUw(Span<byte> rxUw);
    public bool PacketReady { get; }
    // FIX 8: deinterleave BOTH syms and amps, same b; then symbols_to_llrs.
    public void ToLlrs(Span<float> llr, float meanAmp, float esNo = 3.0f);
}
```

`float`/`Cf` hot path throughout — never `System.Numerics.Complex` (double promotion breaks interop).

### 4.4 Direct DFT/iDFT (per-symbol, not FFT)

Port `ofdm.c:642-692` exactly. `Idft` builds the pilot samples at init (§4 uses it for `PilotSamples`); `Dft` (`ofdm.c:674-692`) does freq←time per symbol:
```csharp
static void Dft(OfdmMode c, Span<Cf> result /*nc+2*/, ReadOnlySpan<Cf> vec /*m*/) {
    for (int col = 0; col < c.Nc + 2; col++) {
        float tval = (c.RxNlower + col) * (float)c.Doc;
        Cf cc = CmplxConj(tval), delta = cc; Cf acc = vec[0];      // cmplxconj = cos - j sin
        for (int row = 1; row < c.M; row++) { acc += vec[row] * cc; cc *= delta; }
        result[col] = acc;
    }
}
```
**Pilot init** (`ofdm.c:495-528`): `Idft(pilots)→temp[m]`; `PilotSamples[0..Ncp)=0`; `PilotSamples[Ncp..SpS)=temp`; `TimingNorm = SamplesPerSymbol · Σ|PilotSamples[i]|²`.

### 4.5 Buffer model (exact — enables `Nin==0`)

Persistent `Cf[] rxbuf` length `NrxBuf`, window start `rxbufst`, `nin`. `NrxBufHistory=(np+2)·SamplesPerFrame`, `NrxBufMin=3·SamplesPerFrame+3·SamplesPerSymbol`, `NrxBuf=History+Min`; `rxbufst₀=History`, `nin₀=SamplesPerFrame` (`ofdm.c:305-326,423`). Feed: left-shift by `nin`, append `nin` new. End of `Demod` (`ofdm.c:1956-1961`): `if (rxbufst+nin+NrxBufMin <= NrxBuf) { rxbufst += nin; nin = 0; }` — **`nin=0` drains multiple frames from history with no new input**; the channel wrapper (and oracle §7.3) must honour `Nin==0`.

### 4.6 Streaming acquisition (the datac default)

FreeDV opens datac as `data_mode="streaming"` (`freedv_700.c:453-459`) → `ofdm_sync_search_stream` (`ofdm.c:1394-1464`), acquisition on the **pilot waveform**, not a preamble.

**4.6a `est_timing` (`ofdm.c:794-923`):** window search over `Ncorr = length−(SamplesPerFrame+SamplesPerSymbol)`. `avLevel = 1/(2·sqrt(TimingNorm·Σ|rx|²/length)+ε)`; `wvec_pilot[j]` = `conj(pilot)` at `fcoarse=0`, `Wval[j]·conj(pilot)` at +40, `conj(Wval[j]·pilot)` at −40. `corr[i]=(|Σ rx·w @i|+|Σ rx·w @i+SpF|)·avLevel`. **Dot product is non-conjugating** (`ofdm_complex_dot_product`, `ofdm.c:726-782` — conj pre-baked into `w`; port the scalar `#else` branch `:776-778`, the reference order). `timing_valid = |rx[est]|>0 && timing_mx > TimingMxThresh`.

**4.6b Coarse-freq grid + fine (`ofdm.c:1394-1463`):** search `fcoarse∈{−40,0,+40}` (step 2), keep max `timing_mx`; `coarse_foff = EstFreqOffsetPilotCorr(...) + fcoarse`. If `timing_valid`: `nin=ct_est; timing_est=0; foff_est_hz=coarse_foff`, else `nin=SamplesPerFrame`.

**4.6c `est_freq_offset_pilot_corr` (`ofdm.c:930-997`):** integer-Hz DFT-peak `f∈[−20,20)`, `delta=exp(-j2πf/fs)`, maximise `|corr_st|+|corr_en|`. Net acquisition range −60…+59 Hz.

### 4.7 Burst acquisition (opt-in; reusable greenfield detector)

Entered only if the app calls `freedv_set_frames_per_burst` → `ofdm_sync_search_burst` (`ofdm.c:1325-1388`). `est_timing_and_freq` (`ofdm.c:1253-1293`): 2-D search over `known` (= preamble/postamble modem-frame samples), `mvec[i]=conj(known[i]·cmplx(wf·i))`, maximise `|Σ rx·mvec|`; normalised metric `maxCorr²/(mag1·mag2+ε)`. Two-stage (`ofdm.c:1297-1323`): coarse `tstep=4,fstep=5,[−50,50]`; refine `tstep=1,fstep=1`. Preamble hit: `nin=SpF+ct_est−1`; postamble hit: back up `rxbufst -= np·SpF; rxbufst += ct_est; nin=0`.

### 4.8 Demod core (`ofdm_demod_core`, `ofdm.c:1531-1962`)

**4.8a Fine timing (`:1548-1582`):** `woff=2π·FoffEstHz/Fs`; `work[j]=rxbuf[i]·cmplxconj(woff·i)` with **absolute rxbuf index `i`** (`:1563`); `ft_est=EstTiming(work,…,fcoarse=0,step=1)`; `timing_est += ft_est − ceil(ftw/2)+1`; slew-limit `sample_point` inside CP.

**4.8b Down-convert + DFT (`:1622-1738`):** `rx_sym[(ns+3)×(nc+2)]`; row bases into rxbuf (all `+1+sample_point`) per the offsets in source; each row `work[k]=rxbuf[j]·cmplxconj(woff·j)` then `Dft`. For datac (ns=5): rows `[0]=prev pilot, [1]=this pilot, [2..5]=4 data, [6]=next pilot, [7]=future pilot`.

**4.8c Foff tracking (`:1747-1769`):** `freq_err_rect = conj(Σrx_sym[1]) · Σrx_sym[ns+1] + 1e-6`; `freq_err_hz = arg·Rs/(2π·Ns)`; `FoffEstHz += 0.1·freq_err_hz` (`foff_limiter=false` for datac → no ±1 Hz clamp). **Keep the `+1e-6`.**

**4.8d Phase/channel est (`:1771-1861`) — with §4.0 overwrite:** for `i∈[1,nc+1)`: `high_bw` (default; **datac never switches to low_bw** — the streaming machine never sets it, §4.12): `rect=(rx_sym[1][i]+rx_sym[ns+1][i])·conj(pilots[i])/2`. `low_bw`: 4 pilots × 3 neighbours /12. **Then unconditionally** `aphase_est_pilot[i]=arg(rect); aamp_est_pilot[i]=|rect|`. Equalisation is **phase-only** de-rotation; magnitude carried as `rx_amp` for LLR weighting.

**4.8e Equalise → symbols/bits (`:1873-1928`):** `rx_np[rr·nc+(i-1)] = rx_sym[rr+2][i]·cmplxconj(aphase_est_pilot[i])`; `rx_amp[...] = aamp_est_pilot[i]`; `qpsk_demod` (`ofdm.c:115-120`): `rot=sym·cmplx(π/4); bit0=Re(rot)≤0; bit1=Im(rot)≤0`. `mean_amp = 0.9·mean_amp + 0.1·mean(amp)`.

**4.8f Sample-clock tracking (`:1937-1961`) — integer only, no resampler:** `nin=SamplesPerFrame`; if `timing_est > SpS/8: nin=SpF+SpS/4; timing_est-=SpS/4`; symmetric below `−SpS/8`. `clock_offset_counter` feeds only a reported ppm estimate (`ofdm.c:2354-2358`). Robustness to clock error is inherited "for free".

### 4.9 RX BPF for datac4/13/14 — **exact port required (fix 1, blocker)**

Verified: `rx_bpf` is applied **in place** to the freshly-arrived `nin` samples at the rxbuf tail (`&rxbuf[nrxbuf-nin]`) via the single persistent `ofdm->rx_bpf`, **before** `est_timing`/`est_freq` run — in **both** `ofdm_sync_search_core` (`ofdm.c:1467-1471`) and `ofdm_demod_core` (`ofdm.c:1535-1539`). It is therefore in the acquisition + fine-timing + down-convert path: **any deviation shifts `timing_est`/`ct_est`/`foff` and changes the decoded bits deterministically for datac4/13/14, not just at low SNR.** An approximate biquad/`FilterDesign.BandPass` will never match libcodec2.

**Port `quisk_cfTune`+`quisk_ccfFilter` exactly** (the `ComplexBandpassFir` of §3.7): 100-tap `filtP200S400` tuned to `find_carrier_centre` (float summation, §3.7), history zero-initialised (`filter.c:53`), applied once per `nin`-batch at the rxbuf tail in both `SyncSearch` and `Demod`, sharing one persistent instance — identical treatment to the tx BPF. **No approximation is admissible.** (datac0/1/3 have `rx_bpf_en=false`; nothing to do.)

### 4.10 Packet assembly → LLRs (`freedv_700.c:490-506`) — **fix 8**

Per decoded frame, roll `rx_syms`/`rx_amps` (slide left by `Nsymsperframe`, append). When `modem_frame==np−1`: call **`ofdm_disassemble_qpsk_modem_packet_with_text_amps`** (the datac RX path, `freedv_700.c:493`), then **deinterleave BOTH symbols and amps** with the same `b`:
```c
gp_deinterleave_comp (payload_syms_de,  payload_syms,  Npayloadsymsperpacket);   // freedv_700.c:499
gp_deinterleave_float(payload_amps_de,  payload_amps,  Npayloadsymsperpacket);   // :501
symbols_to_llrs(llr, payload_syms_de, payload_amps_de, EsNo=3.0, mean_amp, N);   // :506
```
`rx_amp` feeds `symbols_to_llrs`' per-symbol weighting (`mpdecode_core.c:581-584`), so the amp deinterleave is **load-bearing**. (For datac `ntxtbits=0`, so the `with_text_amps` and plain variants are behaviourally identical — but cite/use the `with_text_amps` reference and never omit the amp deinterleave.) Add a test that a non-uniform `rx_amp` pattern round-trips through interleave/deinterleave.

### 4.11 `symbols_to_llrs` = Demod2D + Somap + max_star0 (`mpdecode_core.c:567-650`)

```
Demod2D (581-585, all float — matches C):
  tempsr = amp[i]*S[j].Re/meanAmp; tempsi = amp[i]*S[j].Im/meanAmp;
  Er = sym[i].Re/meanAmp - tempsr; Ei = sym[i].Im/meanAmp - tempsi;
  symLik[i*4+j] = -EsNo*(Er*Er + Ei*Ei);
Somap (bps=2): mask 2 then 1; num/den[k] via MaxStar0; bitLik=num-den; llr = -bitLik.
```
**`MaxStar0` — fix 2 (double promotion):** `AJIAN`/`TJIAN` are C `double` literals; `diff` is float; `delta2 + AJIAN*(diff − TJIAN)` promotes to double, narrows to float on return.
```csharp
const double AJIAN = -0.24904163195436, TJIAN = 2.50681740420944;
static float MaxStar0(float d1, float d2) {
    double diff = (double)d2 - d1;
    if (diff >  TJIAN) return d2;
    if (diff < -TJIAN) return d1;
    return diff > 0 ? (float)((double)d2 + AJIAN * (diff - TJIAN))
                    : (float)((double)d1 - AJIAN * (diff + TJIAN));
}
```
**LLR bit order (interop-critical):** `k=0`(mask 2)↔`bit1`, `k=1`(mask 1)↔`bit0`, matching `(bits[1]<<1)|bits[0]`. Feed straight to LDPC. `EsNo=3.0f` is hard-coded (`ofdm_demod.c:411`, `freedv_700.c:451`) — do not "improve".

### 4.12 Sync state machine — data streaming (`ofdm.c:2101-2151`)

Dispatched by `state_machine=="data"` + `data_mode=="streaming"` (the datac default):
```
Search + timing_valid → Trial (sync_start=true, sync_counter=0)
uw_errors = Σ (tx_uw ^ rx_uw)
Trial:  uw_errors < bad_uw_errors → Synced (packet_count=0, modem_frame=nuwframes)
        else sync_counter++; if > np → Search
Synced: modem_frame++; if ≥ np { modem_frame=0; packet_count++;
                                  if packetsperburst!=0 && packet_count≥packetsperburst → Search }
```
`packetsperburst` defaults to **0** for streaming (`ofdm.c:414`) ⇒ **never loses sync once acquired** (matches the bench-note stream behaviour). Streaming **never sets `low_bw`** (only voice1 does, `:2069-2071`) → datac runs `high_bw` throughout. Also port voice1/data-burst/voice2 machines + `ofdm_set_sync` for parameterised reuse, but datac uses only the streaming path.

### 4.13 Interop-exactness checklist

`float` hot path; `cmplx/cmplxconj` per `ofdm_internal.h:51-52`; non-conjugating dot product; `roundf` = AwayFromZero; the §4.0 overwrite; the `+1e-6`/`+1e-12` guards; `EsNo=3.0f`; absolute-index phase ramps; scalar dot-product order (`ofdm.c:776-778`) — validate against generated audio→bits within the tiered tolerance (§7.5), not literal float equality.

### 4.14 Sample-rate bridge

datac is `Fs=8000`; the pdn channel runs 12 k/48 k. codec2 feeds **real** input (`rxbuf[i]=(float)short/32767`, im 0, `ofdm.c:1245,1521`) and mixes to analytic internally. So **feed real audio as `Cf{Re=sample, Im=0}`**. The resampler decision and its interop implications are §8.3 (fix 7).

---

## 5. LDPC + H-matrix porting

Namespace `Packet.SoundModem.Fec.Ldpc`. Source root `mpdecode_core.c`, `interldpc.c`, `ldpc_codes.c`, the five `H_*.h/.c`, `phi0.c`.

### 5.1 Mode → code → shortening (ground-truthed)

Mode→codename is in `ofdm_mode.c`; shortening `data_bits = bitsperpacket − nuwbits − ntxtbits − NumberParityBits` (`interldpc.c:80-83`); all datac keep `protection_mode = LDPC_PROT_2020` (`interldpc.c:61`).

| mode | codename | M=parity | K=`NUMBERROWSHCOLS` | N=`CODELENGTH` | data bits | shortened | stuffed | on-air = data+M |
|---|---|---|---|---|---|---|---|---|
| datac0 | H_128_256_5 | 128 | 128 | 256 | 128 | no | 0 | 256 |
| datac1 | H_4096_8192_3d | 4096 | 4096 | 8192 | 4096 | no | 0 | 8192 |
| datac3 | H_1024_2048_4f | 1024 | 1024 | 2048 | 1024 | no | 0 | 2048 |
| datac4 | H_1024_2048_4f | 1024 | 1024 | 2048 | **448** | **yes** | 576 | 1472 |
| datac13 | H_256_512_4 | 256 | 256 | 512 | **128** | **yes** | 128 | 384 |
| datac14 | HRA_56_56 | 56 | 56 | 112 | **40** | **yes** | 16 | 96 |

Cross-check: `data/8 − 2 = {14,510,126,54,14,3}` payload bytes, matching README. All five satisfy `NumberRowsHcols == NumberParityBits == CodeLength/2` ⇒ `run_ldpc_decoder` (`mpdecode_core.c:480-486`) `shift=0`, `H1=1` — the **RA/dual-diagonal branch**. Target exactly that branch (assert it). Scalars: `dec_type=0`, `q/r_scale_factor=1` (**dead** — the multiplies are commented out, `mpdecode_core.c:398,400,423`), `max_iter=100`.

### 5.2 Sparse H storage

Both flat `uint16_t[]`, **column-major, 1-based, 0=pad**: `H_rows` length `M·max_row_weight` (systematic data-column indices per parity row); `H_cols` length `K·max_col_weight` (parity-row indices per data column). Parity part implicit in the `H1` dual-diagonal. `_input[N]`/`_detected_data[N]` present in four `.c` files (a built-in decode oracle) — **absent from `H_1024_2048_4f.c`** (verified: `grep` finds no `_input[]`), i.e. **datac3 AND datac4 have no standalone vector** (§5.9, §8.4).

### 5.3 Transliteration script (nothing hand-copied)

`tools/gen-ldpc-tables/gen.py` (~60 lines, run once, output committed): regex the seven defines from each `.h`; slurp brace bodies from each `.c`; **assert** `len(H_rows)==M·max_row_weight` and `len(H_cols)==K·max_col_weight` (build guard); emit `src/Packet.SoundModem/Fec/Ldpc/LdpcTables.g.cs` (`internal static class {Name}` with `ushort[] HRows/HCols`) and `tests/.../Fec/Ldpc/LdpcOracle.g.cs` (`float[] Input`, `byte[] Detected` for the four codes that have them). phi0 is hand-ported (§5.4). A checked-in SHA / regenerate-and-diff `[Fact]` guards drift.

### 5.4 `Phi0` — exact fixed-point port (`phi0.c`; the default build uses the table, `mpdecode_core.c:17-19`)

```csharp
private static int Si16(float f) => (int)(f * 65536f);   // float multiply then truncate-toward-zero
public static float Compute(float xf) {                  // xf >= 0
    int x = Si16(xf);
    if (x >= Si16(10.0f)) return 0.0f;                   // phi0.c:16
    if (x >= Si16(5.0f))  return Hi [19 - (x >> 15)];    // :20  (10 consts)
    if (x >= Si16(1.0f))  return Mid[79 - (x >> 12)];    // :45  (64 consts)
    return LowTree(x);                                   // :176 nested-if (32 leaves) else 10.0f
}
```
Bit-exactness needs: `Si16` in **float** then truncate; all 10+64+32 constants transcribed; the `≥10→0` and fall-through `→10.0f` edges. Pin with a boundary golden test at {0, 0.000086, 0.007812, 0.088388, 0.25, 0.5, 0.707107, 1.0, 4.9375, 5.0, 9.5, 10.0}.

### 5.5 Encoder — RA accumulator (`mpdecode_core.c:68-87`)

```csharp
internal static void Encode(LdpcCode c, ReadOnlySpan<byte> ibits /*K*/, Span<byte> pbits /*M*/) {
    int prev = 0, M = c.NumberParityBits;
    for (int p = 0; p < M; p++) {
        int par = 0;
        for (int i = 0; i < c.MaxRowWeight; i++) {
            int ind = c.HRows[p + i * M]; if (ind != 0) par += ibits[ind - 1];
        }
        prev = (par + prev) & 1; pbits[p] = (byte)prev;
    }
}
```

### 5.6 Decoder — log-domain sum-product (`mpdecode_core.c:355-450`) with **verbatim graph (fix 3)**

**Tanner graph built once per code, cached** — transliterate `init_c_v_nodes` (`mpdecode_core.c:142-353`) **verbatim for the `H1=1/shift=0` branch** (the only one datac hits), including socket cross-references and **edge append order** (systematic `H_rows` edges first, then the two dual-diagonal parity edges at `sub[degree-2]/sub[degree-1]`, plus reciprocal sockets). `SumProduct` accumulates `phi_sum`/`Qi` in sub-array order, so the float sum order is fixed by this construction — **any reordering silently changes rounding and can flip marginal decodes.** Cite the exact lines:

- c-node degree (`:151-167`): `count` nonzero `HRows[i+j*M]`; `degree = (i==0)? count+1 : count+2`.
- c-node subs (`:189-211`): j∈[0,deg-3)←`HRows[i+j*M]-1`; `i==0`→sub[deg-2]=last systematic; `i>0`→sub[deg-2]=`(N-M)+i-1`, sub[deg-1]=`(N-M)+i`.
- v-node degree (`:261-288`): data i∈[0,K) count nonzero `HCols`; parity i∈[K,N): `degree=(i!=N-1)?2:1`.
- v-node subs (`:296-335`): data→`HCols[i+j*K]-1`; parity i→`i-K+count`,`count++`; init edge `Message=Phi0(|input[i]|)`, `Sign=input[i]<0`; socket = reciprocal-edge position (`:321-349`).

```csharp
public int Decode(ReadOnlySpan<float> input /*N*/, Span<byte> decoded /*N*/, out int parityCheckCount) {
    parityCheckCount = 0;
    for (int i = 0; i < N; i++) { _vn[i].InitialValue = input[i];
        foreach edge: edge.Message = Phi0.Compute(Abs(input[i])); edge.Sign = input[i] < 0; }
    int M = _c.NumberParityBits, result = _c.MaxIter;
    for (int iter = 0; iter < _c.MaxIter; iter++) {
        decoded.Clear(); int ssum = 0;
        // r: check→variable (mpdecode_core.c:375-402) — phi_sum in sub order, sign xor
        for (int j = 0; j < M; j++) {
            ref var s0 = ... bool sign = v0.Sign; float phiSum = v0.Message;
            for (i=1..) { phiSum += vp.Message; sign ^= vp.Sign; }
            if (!sign) ssum++;
            for (i=0..) { float m = Phi0.Compute(phiSum - vp.Message);
                          cp.Message = (sign ^ vp.Sign) ? -m : m; }
        }
        // q: variable→check (405-429)
        for (int i = 0; i < N; i++) {
            float Qi = _vn[i].InitialValue; foreach vp: Qi += cn[vp.Index].Subs[vp.Socket].Message;
            if (Qi < 0) decoded[i] = 1;
            foreach vp: { float t = Qi - cn[...].Message; vp.Message = Phi0.Compute(Abs(t)); vp.Sign = t <= 0; }
        }
        // early termination (431-446)
        bool allZeroData = true; for (i=0..N-M) if (decoded[i]!=0){allZeroData=false;break;}
        if (allZeroData) { result = iter+1; break; }             // QUIRK: data_int≡0 (CALLOC), keep for iter-count parity
        parityCheckCount = ssum;
        if (ssum == M) { result = iter+1; break; }
    }
    return result;
}
```
Preserve: `parityCheckCount` written **only** in the syndrome branch; v→c `Sign = t <= 0`; c→v carries sign, v→c nonneg + separate sign; re-init from `input` each call.

### 5.7 Shortening wrapper (`interldpc.c`, `LDPC_PROT_2020`)

```csharp
public sealed class LdpcFrameCodec {           // per datac mode
    public int DataBits { get; }               // §5.1
    public int CodedBits => DataBits + _c.NumberParityBits;
    private int Unused => _c.NumberRowsHcols - DataBits;
    // Encode (interldpc.c:103-137): pad data→K with 1s, RA-encode, emit data ++ parity (knowns not sent)
    // Decode (interldpc.c:170-186): full[0..DataBits)=llr; full[DataBits..K)=-100f (known 1);
    //                               full[K..N)=llr[i-Unused]; decode; return hard[..DataBits]
}
```
**LLR polarity (`ldpc_enc.c:131-135`):** `1.0 − 2.0·bit` (bit0→+1, bit1→−1); decoder decides `bit=(Qi<0)`. So **positive LLR ⇒ bit 0**; stuffed known `1` → `-100.0f`. The demod (§4.11) produces this polarity.

### 5.8 Public surface

```csharp
public sealed record LdpcCode(string Name, int CodeLength, int NumberParityBits,
    int NumberRowsHcols, int MaxRowWeight, int MaxColWeight, int MaxIter, ushort[] HRows, ushort[] HCols);
public static class LdpcCodes { public static readonly LdpcCode H_128_256_5, H_256_512_4, H_1024_2048_4f, H_4096_8192_3d, HRA_56_56; }
public enum DatacMode { Datac0, Datac1, Datac3, Datac4, Datac13, Datac14 }
public static class DatacLdpc { public static LdpcFrameCodec Create(DatacMode m); }
```

### 5.9 Test plan

phi0 boundary golden; encoder golden (RA recurrence) + clean-channel round-trip; **built-in decode oracle** for H_128_256_5/H_256_512_4/H_4096_8192_3d/HRA_56_56 (feed `_input[N]`, assert hard-decision == `_detected_data[N]`, `iter<MaxIter`); dimension guard; shortening bit-exactness (datac4 448/1024, datac13 128/256, datac14 40/56). **Fix 9:** `H_1024_2048_4f` has **no** built-in vector → datac3 and datac4 self-consistent round-trips prove nothing about matching libcodec2; the **required** interop gate for those two is the end-to-end FreeDV `.s16` oracle (reference TX → our full RX → exact bytes + CRC-valid, §8.4). Optionally generate a one-off `_input/_detected` for `H_1024_2048_4f` from a C harness on the build host and commit it.

---

## 6. Framing — interleaver / UW / preamble / CRC / shortening

Namespace `Packet.SoundModem.FreeDv`. Types: `FreeDvCrc16`, `GoldenPrimeInterleaver`, `DatacModeParams`, `DatacBurstFramer`.

### 6.1 Pipeline (order is load-bearing)

`freedv_rawdatacomptx → freedv_comptx_ofdm → ofdm_ldpc_interleave_tx` (`interldpc.c:333-339`): **interleave on QPSK symbols AFTER LDPC+QPSK-mod, BEFORE UW insertion.**
```
payload → CRC append → unpack MSB-first → LDPC encode → QPSK-map → GP-interleave → UW-assemble → OFDM-modulate
```
RX mirror: `ofdm_extract_uw` → **`ofdm_disassemble_qpsk_modem_packet_with_text_amps`** (fix 8; drop UW positions, deinterleave syms AND amps) → LDPC decode → pack MSB-first → `freedv_check_crc16_unpacked`.

### 6.2 Per-mode parameter table

| mode | bits/pkt | nuwbits | Nuwsyms | parity | data bits | coded bits | N (interleaver) | b | payload bytes | frame bytes |
|---|---|---|---|---|---|---|---|---|---|---|
| datac0 | 288 | 32 | 16 | 128 | 128 | 256 | 128 | 83 | 14 | 16 |
| datac1 | 8208 | 16 | 8 | 4096 | 4096 | 8192 | 4096 | 2531 | 510 | 512 |
| datac3 | 2088 | 40 | 20 | 1024 | 1024 | 2048 | 1024 | 641 | 126 | 128 |
| datac4 | 1504 | 32 | 16 | 1024 | 448 | 1472 | 736 | 457 | 54 | 56 |
| datac13 | 432 | 48 | 24 | 256 | 128 | 384 | 192 | 127 | 14 | 16 |
| datac14 | 128 | 32 | 16 | 56 | 40 | 96 | 48 | 31 | 3 | 5 |

```csharp
public sealed record DatacModeParams(string Name, int Nc, int Ns, int Np, int NuwBits,
    int DataBits, int CodedBits, int ParityBits, ushort[] UwBits, int[] UwIndSym, int InterleaverB)
{
    public int BitsPerFrame => (Ns-1)*Nc*2;  public int BitsPerPacket => Np*BitsPerFrame;
    public int SymsPerPacket => BitsPerPacket/2;  public int NuwSyms => NuwBits/2;
    public int PayloadSyms => CodedBits/2;  public int PayloadBytes => (DataBits-16)/8;  public int FrameBytes => DataBits/8;
}
```

### 6.3 CRC-16 — `freedv_gen_crc16` (`freedv_api.c:1616-1628`)

**CRC-16/CCITT-FALSE** (poly 0x1021, init 0xFFFF, refin/refout=false, xorout 0x0000). **NOT `Fec/Crc16X25`** (reflected 0x8408, xorout 0xFFFF) — a new class. Computed over payload only; result big-endian in the last two bytes (`freedv_data_raw_tx.c:379-382`).
```csharp
public static class FreeDvCrc16 {
    public static ushort Compute(ReadOnlySpan<byte> data) {
        ushort crc = 0xFFFF;
        foreach (byte d in data) { byte x = (byte)((crc >> 8) ^ d); x ^= (byte)(x >> 4);
            crc = (ushort)((crc << 8) ^ (ushort)(x << 12) ^ (ushort)(x << 5) ^ x); }
        return crc;
    }
    // AppendTo(payload) -> payload ++ [crc>>8, crc&0xff];  Check(frame) recomputes over frame[..^2]
}
// MSB-first pack/unpack (freedv_api.c:419/433): bit i -> byte i>>3, position 7-(i&7)
```
Self-check: `"123456789"` → `0x29B1`.

### 6.4 Golden-prime interleaver (`gp_interleaver.c`)

datac uses **`gp_interleave_comp`** over `N = coded_bits/2` symbols (not `gp_interleave_bits`). `choose_interleaver_b`: `b = next_prime(floor(N/1.62))` — **the literal constant is `1.62`, NOT φ=1.618…** (`gp_interleaver.c:57`); the header comment says "Golden section" but the code uses `1.62`; a "correction" breaks interop. `next_prime` = smallest prime strictly greater (`:50-54`); `is_prime` = trial division. Formulas: `interleaved[(b·i) % N] = frame[i]`; `frame[i] = interleaved[(b·i) % N]`. `b` per §6.2 table (pin with a test). Widen `b·i` to `long` before `%` (identical for these N, future-proof). Provide `Interleave/Deinterleave` for both `Complex[]` and `float[]` (the amp path, fix 8).

### 6.5 Unique word

**6.5a Resolved bit arrays** (`ofdm_mode.c`; base 16-bit `A={1,1,0,0,1,0,1,0,1,1,1,1,0,0,0,0}`, 24-bit `B=A++{1,1,1,1,0,0,0,0}`) — ship the **resolved** arrays, assert length == `NuwBits`, don't reproduce the memcpy overlap logic:
- datac0 (32): `A` then 16 zeros.
- datac1 (16): `A`.
- datac3 (40): `B` @0, `B` @16 (overlap 16..23 second wins).
- datac4 (32): `B` @0, `B` @8 (overlap second wins).
- datac13 (48): `B ++ B`.
- datac14 (32): identical to datac4.

**6.5b UW→symbol** (`ofdm.c:475-480`): `s = qpsk[(tx_uw[2s]<<1)|tx_uw[2s+1]]`.

**6.5c `uw_ind_sym[]`** (`ofdm.c:445-463`, **all C integer division** — the `floorf` wraps an int result, a no-op): `uw_step=nc+1` (the `>=` fallback to `nc-1` never fires for any datac mode); `uw_ind_sym[i]=((i+1)*uw_step)/2`. Resolved: datac0 step10→{5,10,…,80}; datac1 step28→{14,28,…,112}; datac3 step10→{5,…,100}; datac4/datac14 step5→{2,5,7,10,…,40}; datac13 step4→{2,4,…,48}. RX `nuwframes = ceil((uw_ind_sym[last]+1)/((ns-1)·nc))` = {3,2,3,3,5,3}.

**6.5d Insertion** (`ofdm.c:2412-2440`): weave UW at `uw_ind_sym`, payload elsewhere; assert `u==NuwSyms`, `pay==PayloadSyms`.

### 6.6 Burst framing (`freedv_data_raw_tx.c:370-405`)

`preamble | packet[0..K-1] | postamble | inter-burst silence`. Preamble/postamble = **one modem frame** each, built once (`ofdm.c:533-538`), seeds **2/3**, via the LCG `seed=(1103515245L·seed+12345)%32768; bit = seed>16384` (port with `long`). Per-packet CRC (per LDPC packet). Inter-burst silence = user `inter_burst_delay_ms` @8000 else `2·n_nom_modem_samples`.

```csharp
public sealed class DatacBurstFramer {
    public static byte[] PreambleBits(int n, long seed) { var b=new byte[n];
        for (int i=0;i<n;i++){ seed=(1103515245L*seed+12345)%32768; b[i]=(byte)(seed>16384?1:0);} return b; }
    public float[] BuildBurst(IReadOnlyList<byte[]> payloads, int interBurstSilenceSamples);
}
```

### 6.7 Framing test vectors

CRC `0x29B1`; `ChooseB(N)` == §6.2; UW arrays == §6.5a (assert datac4==datac14) and `UwIndSym` == §6.5c; round-trips (deinterleave∘interleave == id incl. the amp path; assemble/disassemble recovers interleaved payload; CRC flips on any bit change).

---

## 7. libcodec2 test-oracle + checked-in vectors CI

Test-project-only. libcodec2 is never a runtime/shipped dependency. Files under `tests/Packet.SoundModem.Tests/Oracle/`.

### 7.1 Ground-truth facts pinned

Mode IDs (`freedv_api.h:59-64`): DATAC1=10, DATAC3=12, DATAC0=14, DATAC4=18, DATAC13=19, DATAC14=20. `bits_per_modem_frame` from `freedv_get_bits_per_modem_frame` (authoritative at gen time; assert == §5.1). CRC gates RX validity (`freedv_700.c:513-518`): CRC pass → `FREEDV_RX_BITS`, else `FREEDV_RX_BIT_ERRORS`; `freedv_rawdatarx` returns bytes **only** when `RX_BITS` set (`freedv_api.c:1093-1097`). `rx_status` bits (`freedv_api.h:75-79`): TRIAL_SYNC=1, SYNC=2, BITS=4, BIT_ERRORS=8. A plain `freedv_open(mode)` reproduces the reference driver for datac: `tx_amp` is FSK-only (never read on OFDM), `use_clip/use_txbpf=−1` → mode defaults apply.

### 7.2 Build + P/Invoke

CMake: `-DBUILD_SHARED_LIBS=ON -DLPCNET=OFF -DUNITTEST=OFF` → `libcodec2.so`. Record `git rev-parse HEAD` (`310777b1…`) + `freedv_get_version/hash` in every manifest. `LibCodec2.cs`: `[DllImport("codec2")]` mirroring `Audio/AlsaPcm.cs`; a `NativeLibrary.SetDllImportResolver` honouring `PDN_LIBCODEC2` then `libcodec2.so.1.2`/`libcodec2.so`; `IsAvailable` gate. Bind the **real int16** path (`freedv_rawdata{,preamble,postamble}tx`, `freedv_nin`, `freedv_rawdatarx`, `freedv_get_rx_status`, geometry getters, `freedv_gen_crc16`/`freedv_pack`/`freedv_unpack`) — not the COMP variants.

### 7.3 Managed wrapper — with `nin==0` handling (fix 10)

`FreeDvOracle` (disposable) carries managed `Crc16Ccitt`/pack/unpack (proven == `freedv_*` in Tier B before the shipped lib trusts them). `Modulate` builds the silence-free `[preamble][data][postamble]` burst. **`Demodulate` must mirror `freedv_data_raw_rx`'s own `nin` loop:** the OFDM demod deliberately returns `nin=0` to drain buffered frames (`ofdm.c:1956-1961`); a `while (pos+nin <= len)` loop with `nin==0` copies 0 samples and never advances. Fix:
```csharp
int nin = freedv_nin(_f);
while (true) {
    if (nin > 0) { if (pos + nin > modemIn.Length) break; Array.Copy(modemIn, pos, chunk, 0, nin); pos += nin; }
    else Array.Clear(chunk, 0, /*0 samples*/ 0);           // drain: call rawdatarx with 0 new samples
    int nbytes = freedv_rawdatarx(_f, bytesOut, chunk);
    int status = freedv_get_rx_status(_f);
    if (nbytes > 0) frames.Add(new OracleRxFrame(bytesOut[..(nbytes-2)], nbytes, status));
    int next = freedv_nin(_f);
    if (nin == 0 && nbytes == 0 && next == 0) break;        // no-progress guard
    nin = next;
}
```

### 7.4 Vector generation — one provenance (fix 6)

**Standardise on the API-built, silence-free burst** (`freedv_rawdatapreambletx + freedv_rawdatatx + freedv_rawdatapostambletx`), matching modulator §3.9 `EmitBurst`. The CLI `freedv_data_raw_tx --testframes 1` prepends **`2·n_nom_modem_samples` leading silence** and appends trailing silence (verified `freedv_data_raw_tx.c:344-355,395-404`), which would offset a TX-parity comparator by ~2 frames and mismatch everywhere. Record the chosen provenance in `PROVENANCE.md`; if the CLI file is ever used, strip leading/trailing `n_nom = samplesperframe` (`freedv_700.c:246`) silence explicitly. Per mode, fixed seeded payload cases (all-zero, all-0xFF, incrementing, 3× seeded random); emit `datacN-tx-<case>.s16` (LE mono int16) + `.json` manifest (mode, IDs, version/hash, sample counts, payloadHex, frameHex, crc16, sha256) and an RX manifest recording `Demodulate`'s frames/status. Optionally a fixed-AWGN-seed noisy `.s16` to exercise the CRC-reject path (statistical only, §7.5).

### 7.5 Tiered exactness contract (fix 4 — the named tolerance policy)

- **(a) Integer layers — exact, zero tolerance:** framing, pack/unpack, CRC, UW/`uw_ind_sym`/interleaver indices, sample counts, returned-byte-count, `rx_status`; **and payload bytes + CRC-valid decision on clean high-SNR input.**
- **(b) Clean golden `.s16` (high SNR):** `rx_np`/`rx_amp` within relative tol (e.g. 1e-4) AND payload bytes bit-identical AND CRC-valid; TX `.s16` `maxAbs` LSB tolerance (goal 0, start ≤2) + RMS bound + xcorr ≥ 0.99999. The tolerance is a **named, documented knob** per mode, tightened toward 0 as the port matures — not an ad-hoc fudge.
- **(c) Noisy/AWGN/MPP:** assert **only** CRC-valid + PER within a binomial CI — **never** sample- or bit-exact (a single differently-rounded float can flip a marginal LLR → different hard decision/CRC).

### 7.6 Three-tier CI

- **Tier A (default filter, no libcodec2):** checked-in vectors — TX parity, RX parity (recovered payload + byte count + CRC-valid status), layer parity (managed CRC/pack/unpack vs manifest). Gates every PR on the self-hosted runner.
- **Tier B (`[SkippableFact]`, `Skip.IfNot(IsAvailable)`):** vector-integrity regen + hash check; helper equivalence (`Crc16Ccitt`==`freedv_gen_crc16`, pack/unpack); cross round-trips (our TX→ref RX, ref TX→our RX).
- **Tier C (`[Trait("Category","OracleGen")]`, manual):** regenerate fixtures on an intentional codec2 bump; record version/hash in manifests + `PROVENANCE.md`.

Add an `Oracle` trait to the filter vocabulary (don't overload `HardwareLoop`/`Interop`). Fixtures auto-copy via the existing `Fixtures/**` csproj glob.

### 7.7 LGPL boundary (structural)

P/Invoke + `FreeDvOracle` live under `tests/…/Oracle/`; the `src/` `ProjectReference` graph never puts libcodec2 on its path; no `<PackageReference>`/`<None>` ships a `.so`; the lib is dynamically loaded at test time only. `tests/…/Oracle/README.md` + `PROVENANCE.md` cite codec2 LGPL-2.1 © David Rowe, upstream URL/commit `310777b1`, and record the `.s16` as generated test fixtures. See §1.4 / R-1 for the port's own relicensing review.

---

## 8. Integration + OBW-per-mode + channel validation + "proven reliable"

### 8.1 `IModem` wrapper

Namespace `Packet.SoundModem.Modems.FreeDv`. `FreeDvDataModem : IModem, IConstellationSource`, one sealed class + static per-mode factories (mirrors `QpskModem`). `Mode => $"freedv-{name}"`; `PayloadBytesPerFrame => mode.PayloadBytes`; `CarrierDetect => Sync ∈ {Trial,Synced}` (`ofdm_internal.h:55`); `FrameDecoded`/`SymbolPlotted` events fire like `QpskModem` (`QpskModem.cs:27-29`). `FrameQuality` (from `Modems/FrameQuality.cs`): `FrameBytes`, `CorrectedBytes = ldpcCorrectedBits/8`, `CrcValid`, `FrequencyOffsetHz = demod.FoffEstHz`.

### 8.2 Mode table `FreeDvDataMode.cs`

The §3.2/§4.1 constants + payload/OBW/SNR columns from `README_data.md:144-149`: OBW {500,1700,500,250,200,250} Hz; MPP test {70,92,74,90,90,90}/100; operating SNR {0,5,0,−4,−4,−2} dB. `Fs=8000` const, never swept. Engine seam (from §3/§4/§5): `OfdmModulator`, `OfdmDemodulator` (`Sync`, `FoffEstHz`, per-packet callback), `LdpcFrameCodec`.

### 8.3 Sample-rate integration (fix 7 — the resampler is NOT in the bit-exact envelope)

The OFDM core always runs at **8000** internally; `FreeDvDataModem` owns integer resamplers and requires `sampleRate % 8000 == 0`. The daemon forces `DspRate=48000` whenever any `freedv-*` modem is configured (extend `Program.cs:105`), 48000/8000=6 exact. **But codec2's real RX resamples rig audio to 8 k with `quisk_cfInterpDecim`/`quiskFilt120t480` — a *different* filter than pdn's `Dsp/Decimator`.** So the daemon's real-radio RX path is **not** the path the oracle proves. Two honest options, pick one before shipping datac on real radios (**R-2, §10**):
- **(A) faithful:** port `quisk_cfInterpDecim` + `quiskFilt120t480` for the 48 k→8 k RX stage, keeping the whole chain reference-faithful; or
- **(B) scoped tolerance:** explicitly declare the resampler a tolerance stage and make **§8.6 Leg-3 (cross-decode of a real reference-FreeDV transmission through the daemon)** a **required** gate — and stop describing the daemon path as bit-exact.
For the OBW test and the oracle, construct the modem **at 8000 directly** (no resampler) so the compared samples are the pure codec2 waveform.

### 8.4 KISS payload contract

One KISS frame ↔ one FreeDV packet. `Modulate`: reject/log-drop over-length (no silent fragmentation — segmentation is the ARQ layer); zero-pad to `PayloadBytes`; append CRC-16 (§6.3); LDPC-encode; OFDM-modulate one burst @8000; upsample; prepend `txDelayMs` of **silence** (PTT settling only — the FreeDV preamble is the acquisition aid, so the burst stays byte-identical to codec2). `Process`: decimate→8000, demod+sync; on each packet LDPC-decode + re-check CRC; **CRC pass** → emit leading `PayloadBytes` (CRC stripped); CRC-fail dropped (matches `freedv_data_raw_rx`). Fixed-size zero-pad means variable length does not round-trip its own length — inherent to FreeDV raw data, and why ARQ is a required higher layer (FreeDATA/Mercury). **datac3/datac4 (no LDPC vector, §5.9) rely on this end-to-end oracle as their required interop gate.**

Daemon (`Program.cs`): add `freedv-datac0/1/3` switch cases (Phase 2: datac4/13/14); force 48000 + `captureRate % 48000 == 0` guard; document modes. `SoundModemChannel`/`KissTcpServer`/`--wav` need no changes.

### 8.5 OBW per mode — pin to **measured golden**, not README (fix 5)

Extend `tests/.../Dsp/OccupiedBandwidthTests.cs`, reusing `OccupiedBandwidth.Measure` (ITU 99%). Construct each modem **at 8000** (Fs/2=4000 > widest edge 2350 Hz). The README figures {500,1700,500,250,200,250} are **nominal**, not the measured ITU-99% OBW of the clipped+BPF'd waveform — e.g. datac0's outer-carrier span alone is 8·62.5=500 Hz and the OFDM sinc skirts push true 99% OBW higher (~1.12×), which would false-fail a `≤1.05×` ceiling; a `−20%` floor is too loose to catch clipper splatter. **Fix:** measure the codec2 golden `.s16` DATA section (post amp_scale→clip_gain1→ofdm_clip→BPF→clip_gain2→clip) with the **same** `OccupiedBandwidth` meter and pin the C# waveform to **that measured value ± a tight tolerance**; keep the README number only as a sanity ballpark. This proves "matches the reference spectrum", not "matches a round number", and remains the guard that historically caught 1200-QPSK splatter. `fftSize` 4096 (long modes), 2048 (datac14). Include the meter self-test (`OccupiedBandwidthTests.The_Meter_Agrees_With_A_Known_Signal`).

### 8.6 Channel-model validation (AWGN + MPP)

Port codec2's `ch` tool → `tests/.../Channel/Codec2Channel.cs`, exact order (`ch.c:102,330-508`): Hilbert→complex → magnitude-clip → freq-shift → multipath fade → AWGN → SSB filter → real int16. Hilbert `ht_coeff.h` (`HT_N=257`); MPP two paths `nhfdelay=floor(2.0·8000/1000)=16` (`ch.c:45,282`); AWGN `No=10^(NodB/10)·1e6`, `variance=Fs·No`, Box-Muller complex noise (`ch.c:57-67`); SSB `ssbfilt_coeff.h` (`SSBFILT_N=100`, centre 1500). **Fading = bit-exact** by reusing codec2's own generated MPP file (`unittest/fading_files.sh` on the build host, checked into `samples/freedv/`; same file → same realisation). **AWGN = statistical** (C `rand()` not reproducible in C#; substitute a seeded xoshiro with the same variance → Monte-Carlo parity with a CI, not sample-exact).

**Validation runs (tiered per §7.5):** MPP parity — ~100 packets through `Codec2Channel(MPP, operatingSnr)`, accept if packets-received ≥ published within a **one-sided binomial CI** of the published p̂ (never bit-exact). AWGN sweep — PER/BER knee within ~1 dB of codec2's published curves. Plus OBW ≤ measured golden (§8.5). `Codec2Channel` self-test: unity HT, `nhfdelay=16`, measured SNR3k matches set NodB.

### 8.7 "Proven reliable" acceptance (all three legs green)

- **Leg 1 — oracle / bit-exact (§7.5(a)+(b)):** RX recovers exact payload + CRC-valid from clean high-SNR codec2 golden bursts (zero payload errors); TX matches golden ≤ tol (LSB/xcorr); upstream `freedv_data_raw_rx` decodes our TX (manual provenance run).
- **Leg 2 — channel-model parity (§8.6, statistical):** MPP packets-received ≥ published within a binomial CI at the mode's operating SNR; AWGN knee within ~1 dB; OBW within the measured-golden tolerance.
- **Leg 3 — real HF loop (`[Trait("Category","HardwareLoop")]`):** bidirectional over a real SSB path through the daemon KISS↔KISS at the operating SNR, **and** a cross-decode against reference FreeDV on air (our TX decoded by `freedv_data_raw_rx`, FreeDV's TX decoded by us). **Required** (not optional) if resampler option (B) is chosen (§8.3). Honours the "validate full flow, remote==local" bench discipline.

Legs 1–2 are CI-enforceable on the self-hosted runner; Leg 3 is the manual hardware gate that flips a mode from "oracle-correct" to "proven reliable".

---

## 9. Phase-1 file/task breakdown + phasing

### 9.1 Phasing (grounded in README use-cases + fixtures)

1. **datac0 — first light.** Shortest burst (0.44 s), smallest FEC (256,128), Nc=9, 14-byte payload → fastest end-to-end (modulate+LDPC+framing+demod+CRC+oracle) with least engine surface.
2. **datac1 — workhorse.** Forward-link mode (Nc=27, 510 bytes, largest LDPC 8192,4096); second because the real off-air `test_datac1_006.raw` capture gives a **free real-world RX oracle** no other mode has, and 1700 Hz OBW is the headline compliance number.
3. **datac3 — low-SNR forward link.** Reuses datac0's Nc=9 geometry, exercises a second LDPC code (2048,1024) — and forces the datac3/datac4 end-to-end oracle gate (§5.9/§8.4) since `H_1024_2048_4f` has no built-in vector.

**Phase 2:** datac4/13/14 — the `filtP200S400` tx BPF + **`rx_bpf` regime (fix 1, §4.9)** + narrow-mode thresholds (0.45–0.5), a distinct engine regime best batched after Phase 1 proves the architecture.

### 9.2 Files

**Engine (§3–§5):**
```
src/Packet.SoundModem/Ofdm/OfdmMode.cs                    §3.4/§4.3 mode record (6 factories, AwayFromZero fix)
src/Packet.SoundModem/Ofdm/Cf.cs                          float complex struct
src/Packet.SoundModem/Ofdm/OfdmModulator.cs               §3 (idft, CP, hilbert-clipper, preamble/postamble, burst)
src/Packet.SoundModem/Ofdm/OfdmDemodulator.cs             §4 (DFT, sync-search, demod-core, state machine)
src/Packet.SoundModem/Ofdm/OfdmPacketAssembler.cs         §4.10 (disassemble_with_text_amps + amp deinterleave)
src/Packet.SoundModem/Ofdm/ComplexBandpassFir.cs          §3.7 quisk_cfTune/ccfFilter — SHARED tx+rx BPF
src/Packet.SoundModem/Ofdm/OfdmTables.g.cs                pilotvalues[64], wval, qpsk, filtP* (100-tap each)
src/Packet.SoundModem/Fec/Ldpc/LdpcTables.g.cs            §5.3 generated H_rows/H_cols (5 codes)
src/Packet.SoundModem/Fec/Ldpc/Phi0.cs                    §5.4 hand-ported table
src/Packet.SoundModem/Fec/Ldpc/LdpcEncoder.cs             §5.5
src/Packet.SoundModem/Fec/Ldpc/LdpcDecoder.cs             §5.6 (init_c_v_nodes verbatim, MaxStar0 double)
src/Packet.SoundModem/Fec/Ldpc/LdpcFrameCodec.cs          §5.7 shortening; DatacLdpc registry
tools/gen-ldpc-tables/gen.py                              §5.3 one-time transliteration
```
**Framing + integration (§6, §8):**
```
src/Packet.SoundModem/FreeDv/FreeDvCrc16.cs               §6.3 CCITT-FALSE (not X25)
src/Packet.SoundModem/FreeDv/GoldenPrimeInterleaver.cs    §6.4 (comp + float; 1.62 not φ)
src/Packet.SoundModem/FreeDv/DatacModeParams.cs           §6.2 (UW arrays + uw_ind_sym)
src/Packet.SoundModem/FreeDv/DatacBurstFramer.cs          §6.6 (LCG seeds 2/3)
src/Packet.SoundModem/Modems/FreeDv/FreeDvDataMode.cs     §8.2
src/Packet.SoundModem/Modems/FreeDv/FreeDvDataModem.cs    §8.1/§8.4 IModem + rate bridge
src/Packet.SoundModem.Daemon/{Program.cs,DaemonConfig.cs} §8.4 switch + 48000 rule
```
**Tests/fixtures/docs:**
```
tests/…/Oracle/{LibCodec2,FreeDvOracle,OracleCompare}.cs  §7 (nin==0 fix)
tests/…/Oracle/README.md + PROVENANCE.md                  §7.7 LGPL boundary
tests/…/Modems/FreeDv/{FreeDvCrc16,FreeDvLoopback,DatacOracleParity}Tests.cs  §7.6 Tier A + unit
tests/…/Oracle/{DatacLiveOracle,DatacVectorGen}Tests.cs   §7.6 Tier B/C
tests/…/Fec/Ldpc/{Phi0,LdpcDecoder,LdpcShortening}Tests.cs §5.9 (built-in vectors; datac3/4 via oracle)
tests/…/Dsp/OccupiedBandwidthTests.cs                     §8.5 extend, pin to measured golden
tests/…/Channel/{Codec2Channel,Codec2ChannelTests}.cs     §8.6
tests/…/Fixtures/freedv/*.s16 + *.json + fast_fading_samples.float   §7.4/§8.6
docs/ofdm-design.md (this document); docs/plan.md amendment-log entry
```
Constraints: pure-managed .NET 10; GPL-3.0-or-later + per-port provenance headers; CPM (no `Version=` on `PackageReference`); xUnit + AwesomeAssertions + `SkippableFact`; `Snake_Case` test names; self-hosted CI only; no steady-state alloc / no LINQ in per-sample DSP. No new shipped NuGet dependency.

---

## 10. Open risks & questions

**R-1 (legal, blocking merge). LGPL-2.1 → GPL-3.0-or-later relicensing of the port.** Transliterating LGPL-2.1 codec2 DSP into GPL-3.0-or-later source is a derivative work; the LGPL-2.1 §3 → GPL relicensing path very likely permits it, but it **must be reviewed and the basis recorded** with per-file provenance headers before merge (§1.4). Ask Tom / legal.

**R-2 (interop, blocks real-radio datac). RX resampler faithfulness (fix 7, §8.3).** The oracle proves only the native-8 k core; codec2's real RX uses `quisk_cfInterpDecim`/`quiskFilt120t480`, not pdn's decimator. Decide **(A)** port that resampler (fully faithful) or **(B)** scope it as a tolerance stage and make Leg-3 cross-decode a required gate. Until decided, do not describe the daemon path as bit-exact.

**R-3 (float determinism). Literal IEEE-754 identity is unproven.** Algorithm/constants/tables/ordering are fully specified, so decoded bits + iteration counts should match on clean input; intermediate-sum bit-identity is unproven without running (barred here). Contained by the tiered tolerance policy (§7.5) — but the first oracle run may reveal a tolerance that must be widened; treat the per-mode LSB knob as data, tighten as the port matures.

**Not determinable from the source read (flagged, do not confabulate):**
- **`quiskFilt120t480` / `quisk_cfInterpDecim` taps** — not transcribed (R-2).
- **datac4/13/14 payload-vs-codeword** (`Npayloadsyms` < codeword symbols) implies puncturing in `ldpc_mode_specific_setup`/`count_errors_protection_mode` — not read in full; an LDPC-layer concern. The demod→LLR interface must emit exactly `BitsPerPacket − nuwbits − txtbits` LLRs and let the LDPC component handle framing.
- **Exact `ht_coeff.h` (257) / `ssbfilt_coeff.h` (100) tap values** — sizes/defines confirmed, arrays not transcribed (needed only for the channel model, §8.6; the recommendation reuses codec2's generated fading file to keep it bit-exact).
- **`octave/doppler_spread.m` `fir2`/`resample` numerics** are Octave-specific — hence reusing codec2's generated `fast_fading_samples.float` rather than re-implementing the generator.
- **`fdmdv_freq_shift_coh`** (channel foff injection, `fdmdv.c`) — not read.
- **`freedv_set_tx_amp` insertion point** — confirmed a no-op for datac (FSK-only), so excluded from the modulator; not traced further.

**Deferred critique items:** none. All 13 findings are folded in (§1.6 traceability); the two that touch things outside the pure port — R-1 (legal) and R-2 (resampler) — are carried as explicit open risks with a required decision, not silently dropped.
