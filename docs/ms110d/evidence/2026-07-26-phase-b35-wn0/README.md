# B3.5 — WN0 detector family (design note; registered before any fix)

WN0 (75 bps, 32-chip 4-ary Walsh, no mini-probes, no DFE, no turbo) is the last OPEN
waveform with a registered detector-family fix direction (phase-b-autopsies §WN0). This
note pins the fresh mapping, the mechanism, the detector design, and the pre-registered
acceptance ladder + consequence clause, before any demodulator change.

## Fresh mapping (branch point e126ed7, canonical seed 500, 2026-07-26)

Full-budget Poor census + isolation diagnostics (`corpse/*.mask`, `corpse/poor-census.csv`):

| Channel @ −1 dB          | uncoded  | coded       | deep-fade concentration    |
|--------------------------|----------|-------------|----------------------------|
| Static 2-path (echo only)| 8.83E-4  | **0**/100k  | —                          |
| Flat Rayleigh (fade only)| 9.98E-2  | 1.20E-2     | 65 % of errors in 22 % of bits |
| Poor (echo + fade), 3M   | 1.43E-1  | **7.96E-2** | 20 % in 9 % — spread everywhere |

- The B1-autopsy structure reproduces exactly on today's HEAD: echo alone free, fading
  alone codes 8.3× (1.0E-1 → 1.2E-2), echo+fading destroys the *code's* leverage
  (1.8×) because errors stop being fade-localized.
- Coded Poor improved 1.12E-1 → 7.96E-2 since the B3-entry rebaseline with uncoded
  identical (1.42/1.43E-1) — shared decode-path work (B3.2/3.3) reached WN0's Viterbi;
  the detector itself is untouched and still the binding constraint.
- **Failure is uniform, not specimen-class**: 476/476 bursts have coded errors
  (per-burst coded BER min 3.8E-3, median 7.0E-2, max 5.5E-1). No burst near mask.
- **B1's corrupt-WID misacquisition finding is resolved on HEAD**: 0 acquisition
  failures and turbo 0c/0r/0a across all 476 bursts / 3M bits (the old rebaseline
  showed 48 turbo-converged blocks = bursts decoding down wrong-WN paths). No
  acquisition-side work needed in B3.5.

## Mechanism (confirmed B1, quantified here)

The shipped detector is a coherent **single-reference** correlator: descramble aligned
to the direct path, one 32-chip correlation per candidate, winner by magnitude, LLRs
from the coherent Re() against a single carrier reference tracked by an 8-symbol DD-PLL
(107 ms average ≈ the fade coherence time — always a lap behind).

On Poor this wastes the channel twice:

1. **The echo path is turned into noise.** The 2 ms (4.8-chip) second path is
   scramble-misaligned in the direct correlation window, so its entire power (half the
   received energy on this channel) lands as self-noise instead of signal. Per-symbol
   correlation SNR drops ~4–5 dB below flat-Rayleigh at the same total SNR — before
   fading is even considered. (Static 2-path decodes clean because the self-noise is
   small against a non-fading direct path: uncoded 8.8E-4.)
2. **The single phase reference chases the resultant of two independent phasors.** With
   both paths Rayleigh-fading at 1 Hz, the combined correlation phase wanders
   continuously; the slow PLL tracks a compromise, and coherent Re() projection loses
   sign integrity *everywhere*, not just in deep fades — which is why Poor's errors are
   spread (20 % concentration) while flat's are fade-localized (65 %), and why the
   Viterbi gets so little traction. A combining detector would instead get dual
   diversity from exactly this independence (chi-4 fade depth instead of Rayleigh).

The interleaver is not the problem: WN0 Long spans 7.68 s ≈ 7 fade cycles. With
CSI-carrying LLRs, fade erasures are exactly what rate-1/2 K=7 + this depth absorbs.

## The lever: DD-MRC multi-finger Walsh detector ("Walsh RAKE")

One detector change, confined to the WN0-only path (`Wid0WalshModem` + `TrackWalsh`):

- **Fingers**: integer chip delays k ∈ {0..6} (T-spaced ≈ decorrelated; covers the
  4.8-chip Poor echo via fingers 4+5; flat/AWGN channels leave fingers 1–6 idle).
  Read 38 chips once per symbol; finger k's window = chips[k..k+32].
- **Per-finger correlation**: descramble every window with the *symbol's own* 32
  scramble rotors (precomputed once per symbol — preserves the Reset-per-block
  contract), then 4 Walsh correlations per finger: corr_k(s).
- **Per-finger channel gains** ĝ_k, decision-directed one-pole:
  ĝ_k ← (1−α)·ĝ_k + α·corr_k(d\*)/32, α = 1/6 (≈80 ms — inside the fade coherence
  time; α is the single registered tuning knob). Gains persist across block boundaries
  within a burst (the channel is continuous; only the scrambler resets).
- **MRC decision statistic**: D(s) = Σ_k Re(ĝ_k\* · corr_k(s)); winner d\* = argmax D.
  LLRs keep the max-log pair form on D. This makes the LLRs quadratic-CSI-weighted
  (E[D(truth)] ∝ Σ|g_k|² — the matched-filter LLR with block-constant noise), so faded
  symbols self-report near-erasure instead of confident garbage — the flat-Rayleigh
  concentration says this alone should move the fade-localized 65 %.
- **Warm-up** (first 8 symbols of each burst, ĝ cold): winner by noncoherent
  Σ_k |corr_k(s)|²; LLRs still from D (small during warm-up = near-erasure; same
  bounded-cost shape as the WN2 K=48 ramp verdict, and 8 of 576 symbols per block).
- **Carrier PLL**: retained, driven by the combined C = Σ_k ĝ_k\*·corr_k(d\*)
  (real-positive when locked; arg(C) = residual common CFO error). Fingers absorb
  per-path phase; the PLL carries only common drift.
- **Signal-lost discriminator**: weak only if ALL fingers are weak
  (max_k |corr_k(d\*)| < 0.35·Σ|chip| over the finger-0 window, same 90-symbol
  patience) — the current finger-0-only test would false-fire on a 1.2 s direct-path
  fade with a strong echo, exactly the fades MRC is being added to ride.

Not in scope (available to later rungs if the numbers demand): fractional-delay fingers
(`ReadChip` already takes a double), preamble-derived finger placement, acquisition work.

**Instrument riding the fix commit**: a per-symbol finger-gain/winner-margin
`FrameDiagnostics` line from `TrackWalsh` (existing event, rig-side subscription only),
plus corpse-rig WN0 compatibility already holds for uncoded biterrs + llrstats
(FirstPassBlockLlrs fires from FinishBlock; fetched-bit order matches AddLlr order).

## Pre-registered acceptance ladder

- **A — combining proof (the autopsy's bar)**: Poor @ −1 dB, 3M canonical census,
  coded **< 1.20E-2** (today's flat-Rayleigh coded). "Any detector still worse than
  flat-fading on Poor is not combining."
- **B — flat non-regression**: Flat-Rayleigh @ −1 coded ≤ 1.20E-2 (expected: improves,
  via quadratic CSI + per-finger phase riding fades).
- **C — AWGN guard**: AWGN WN0 @ −6 dB full budget stays 0 errors; uncoded within 2×
  of 1.61E-3 (bounds the idle-finger estimation-noise cost).
- **D — static guard**: Static 2-path @ −1 stays 0 errors.
- **E — the gate**: Poor @ −1 full §5.3, both seed families, at mask.

Rungs if A fails, in order, one lever per measurement: (1) α ∈ {1/3, 1/12};
(2) margin-gated DD updates (skip gain update when the winner margin is weak);
(3) noncoherent power combining D(s) = Σ_k w_k·|corr_k(s)|², w_k ∝ |ĝ_k|².

**Consequence clause**: if A still fails after rungs 1–3, revert the detector to the
shipped form, bank the patch in this directory, and WN0 joins WN7/WN8 as
model-front-blocked — B4 proceeds with WN0 measured-not-gated.

**Named risk (the B3.4 lesson)**: DD gains are a feedback loop. Unlike WN8's coin-flip
first pass, WN0's DD reference comes from 4-ary Walsh decisions that are majority-right
at the operating point outside deep fades, and in joint deep fades the gains decay
toward zero — self-erasure, not a confident wrong attractor. The llrstats corpse view
is the watch instrument; if census shows attractor signatures (confident wrong runs),
that is rung-2's trigger.

## Amendment 1 (2026-07-26): acceptance A–D pass; the census exposes a preamble-count
## acquisition class — registered fix: joint count vote

**Ladder results (first form, α = 1/6, no rungs needed):**

| Bar | Registered | Measured | Verdict |
|-----|-----------|----------|---------|
| A — combining proof | Poor coded < 1.20E-2 | **1.03E-2** (from 7.96E-2, 7.7×) | PASS |
| B — flat non-regression | ≤ 1.20E-2 | **4.56E-4** (26×) | PASS |
| C — AWGN guard | 0 errors, uncoded ≤ 2× | 0/3M, 2.56E-3 = 1.59× | PASS |
| D — static guard | 0 errors | 0 (uncoded 8.83E-4 → 8.88E-5) | PASS |

Census reshape: 0/476 zero-error bursts → **326/476**; deep-fade concentration 20 % →
32 %. Corpse guards exact (WN7 72,666/7c/4r + oracle b5:15, WN6 0/11c0r, WN13sp
0/11c0r); suite 696/0.

**The residual splits into two classes** (`corpse/poor-census-rake.csv`):
1. **Catastrophic, 4 bursts, 43 % of all errors (4.39E-3)**: coin-flip from bit 0,
   uncoded ≈ 50 %. Pre-existing and detector-independent (same four bursts, same
   counts, on the old detector's census — invisible there because every burst failed).
2. **Residual, 146 bursts (5.91E-3)**: mid-burst fade clusters, healthy finger
   structure (the detector-side remainder).

**Class-1 mechanism, corpse-confirmed** (`count@` diagnostic, five corpses): the
preamble COUNT field is the last single-read acquisition field — 4 Walsh dibits with
3 check bits, read from ONE super-frame (`TryReadPreamble`). A burst-start fade (all
four dead bursts accepted at metric 0.415–0.486 vs healthy 0.578) or a mis-walkback
corrupts the read; 1-in-8 corruptions beat the check and place data start whole
super-frames off with a clean lock — the burst then demodulates preamble/mis-aligned
chips forever (all-finger noise, no recovery path). Healthy b97 reads count=19 (truth);
the dead four read 31, 25, 3, 16. b42's mush dibits `3333` decode to 31 and PASS —
EncodeCount(31) IS 3333, an all-same-symbol attractor. The WID had exactly this Class-D
failure and was vote-hardened (§B3, issue #69); the count was not. Predicted rate
≈ P(deep fade over the count read) × 1/8 ≈ 1 % of bursts ✓ 4/476.

**Registered fix (shared acquisition path)**: joint soft count vote across the same
super-frame span the WID vote already waits for (zero added latency). Per vote frame v
accumulate the four candidate magnitudes per count position; for each candidate value c
score Σ_v Σ_j mag_{v,j}(dibit_j(c−v)) — the decrement-aligned analogue of the WID's
constant-word vote; winner by argmax over c ∈ {votes−1..31} with a margin floor
(initial 0.10; mushy joint = failed candidate → BackToSearch, the WID's safety valve).
The single-read gate stays (it sizes the vote span); a corrupt-low single read shrinks
the vote span but now meets a margin floor where today there is none.

**Pre-registered bars**: the four class-1 corpses joint-decode the true count (or
BackToSearch and re-acquire) — no coin-flip burst survives; Poor census A re-run lands
≈ residual-only (≈ 5.9E-3); the four corpse guards stay EXACT (healthy frames dominate
the joint → same count); AWGN/static/flat unchanged; suite green; FULL battery before
merge (owed anyway — this touches shared acquisition).

## Guards for the leg

WN7 corpse 72,666/7c/4r/oracle-15 exact; WN6 corpse 0/11c0r; WN13sp (SEED=10513 w3 b5)
0/11c0r; suite 696/0; FULL battery (incl. WN6 6M + WN2 both families) before merge.
