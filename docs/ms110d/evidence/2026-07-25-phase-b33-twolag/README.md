# Phase B3.3 model front II — two-adjacent-lag echo (design, pre-registered 2026-07-25)

Written BEFORE the implementation, per the Phase B discipline. Follows the basin campaign
(`../2026-07-25-phase-b33-basin/`, negative result: the converge-or-wander split is set by
the soft loop's attractor structure) and the #81 model front, whose closing note
pre-registered this lever. It now carries BOTH open WN7 fronts:

- **Model floor**: oracle 238 on the w0/b0 corpse, concentrated in b4:99/b5:83 (77%) —
  the blocks where the Poor channel's 2 ms path (4.8 symbols at T) straddles the symbol
  grid, so the single-lag model at the TIR-pinned d leaves the adjacent T/2 fractional
  sidelobe unmodeled.
- **Labels basin**: sharper per-iteration chain-BCJR LLRs strengthen the contraction the
  wander blocks are short on (b9 wanders at ~800–900 decode-changes at the cap — the
  closest state to convergence; b1/b6/b7/b10 have oracle ≤ 29, so the model, not the
  labels ceiling, bounds what each iteration can repair).

## Why NOT literal two-lag chains (correcting the §B2.2 sketch)

The §B2.2 note sketched "two-adjacent-lag chains, M² states per chain". The dependency
analysis does not support an exact construction of that shape: with observation factors
ψ_t(x[t], x[t−d], x[t−d−1]) and gcd(d, d+1) = 1, the coupling graph does not decompose
into independent chains — pairing residue classes (c, c−1) mod d covers class c's factors
but leaves class c−1's factors reaching into pair (c−1, c−2), a cyclic ladder whose exact
elimination has treewidth ~d+1, i.e. M^(d+1) states — the very ceiling §B2.2 removed.
Loopy message passing over the ladder would be approximate AND architecturally invasive.

## The design: exact single-lag chain + EM-consistent adjacent-tap cancellation

The turbo already runs an EM outer loop with per-symbol expectations E[x] and variances.
The two-tap model y[t] = h1·x[t] + h2a·x[t−d] + h2b·x[t−d−1] is handled by:

1. **Chain BCJR unchanged** — exact on (h1, h2a) at the pinned lag d.
2. **Adjacent-tap soft cancellation**: subtract h2b·x̂[t−d−1] from the observation before
   the chains, where x̂ is the iteration's expected symbol (hard re-encoded labels on
   iteration 0 and the oracle path — for the oracle, truth, so the cancellation is exact;
   E[x] on soft iterations), and add |h2b|²·Var[x[t−d−1]] to the EM noise — the identical
   treatment the residual terms already apply to soft-label uncertainty (§B2.3). Sources
   with t−d−1 before the block head are known probe chips (variance 0), the same
   `preceding` convention the chains use.
3. **TIR pair candidates**: `SolveTrainingTir` gains adjacent-pair candidates — FF + FB
   columns {d, d+1} jointly (still linear LS). A pair is accepted only OVER the accepted
   single-lag candidate by the same noise-margin construction with one extra free
   parameter: ΔSSE(pair vs best single) > 4·SSE₀/rows (no ln L factor — the pair's lag is
   tied to the already-established d, so there is no L-fold selection effect; the 4×
   safety factor is retained). Echo-free frames keep the null solve bit-identical;
   single-lag frames keep today's path bit-identical — the cancellation only activates
   where the pair solve proved a second tap on U rows of evidence.
4. **h2b estimation**: direct correlation at lag d+1 on the same stream as h2a (the
   scrambler decorrelates distinct lags on the PSK ring, so separate correlations are
   consistent); h2b frame-constant this lever (per-segment h2b is a follow-on measured
   step only if the corpse shows the fractional tap fading within frames — under TIR the
   pair coefficients derive from one physical path, so their intra-frame trajectories are
   coupled and the h2a per-segment shape already carries most of it).

## Pre-registered acceptance

1. **WN7 corpse w0/b0** (`_WN=7 _SNR=19 _SEED=507 _ORACLE=1`): oracle must fall
   materially from 238 with the reduction concentrated in b4/b5 (the fractional-sidelobe
   blocks) — that is this lever's own front. Shipped must improve or hold vs the 107,938
   / 5c/6r baseline; any wander block reaching a fixed point is the basin dividend.
2. **Guards**: WN6 corpse and WN13 specimen hold 0 shipped / 0 oracle; pair acceptance on
   those corpses should be rare (their echoes are the resolvable-lag kind) — the
   `turbo-tir` diagnostic gains the pair-acceptance count to verify.
3. **Battery**: the standard set (WN13 canonical + disjoint, WN2×2, WN1×2, smokes,
   AWGN×10, static, Doppler) + Phase A regressions gate the merge; AWGN/static must be
   bit-identical (pair never accepted on echo-free channels by the margin construction).
4. **Escalation unchanged**: FD-turbo if WN7 stalls at mask+2 dB after this lever.

Files land here as the measurements run.

---

## Measured results (filled in as the pre-registered steps ran, same day)

### Step 1 — pair TIR + frame-constant h2b cancellation

WN7 corpse: oracle 238 → 224 (b1 15→6, b5 83→75, b7 29→19; **b4 unchanged at 99; b10
REGRESSED 6→19**), shipped exactly baseline (107,938, 5c/6r — the same convergence split).
The pair is real: accepted on ~half the TIR frames across all three corpses (WN7 b0:
25–36 of 45–54, WN6 11–15, WN13sp 6–27) at mean |c2| ≈ 0.12–0.18. Guards 0/0. The
Dfe pair solve is unit-tested (exact recovery of a 0.7/0.35 straddle; single-lag echoes
keep Lag2 = 0). Verdict vs pre-registration: the fall is real but NOT concentrated in
b4/b5 — the b4/b5 attribution to adjacent-lag sidelobes was wrong or incomplete — and
b10's regression is the frame-constant-cancellation signature (a stale coefficient on a
fading tap injects error where the fade moves).

### Localization — where the b4/b5 oracle floor actually lives

New rig instrument (oracle wrong-bit positions): b4's 1,851 raw oracle errors spread over
a dozen frames at 50–130 each (up to 17% of a frame's bits), roughly uniform WITHIN
frames. Joining per-frame error counts against the first-pass frame log: the top-error
frames have probe gain 0.12–0.45 and preMse 0.6–1.3 — **partially-faded frames with high
residual even under oracle labels**, mixed tapChange. Not tail-staleness, not a single
catastrophic fade — a fade-frame floor.

### Step 2 — per-segment h2b (pre-registered follow-on, triggered by b10's regression)

Oracle 224 → **209** (b5 75→60; b10 stays 19, b4 stays 99); shipped unchanged; guards
0/0. Composed two-lag verdict: **oracle 238 → 209 (−12%)**, shipped holds, the cut lands
in b1/b5/b7, and the small b10 regression (6→19) is the honest residual cost. Ships on
this evidence; the battery gates the merge.

### Step 3 — 16-segment anchors (the banked #81 seg-sweep lever, composed per its
pre-registration)

The fade-frame localization (phase moves fastest through partial fades; 4 anchors per
256-symbol frame under-resolve it) is exactly the mechanism this banked lever targets.
Segments 4 → 16 for h1/h2/h2b jointly. **Measured: clear regression — REVERTED.** Oracle
209 → 495 (b4 99→209, b1 6→85, b10 19→62), shipped 107,938 → 125,966 (a convergence
lost), and even the WN13 specimen picked up 5 oracle errors. The #81 banked −10% was
measured under FULL INVERSION, where the segments only refine h1 against an
already-inverted stream; under TIR the pinned-echo model's h1/h2/h2b correlations carry
the detection model itself, and 16-symbol windows are too noisy to carry it. The banked
lever is retired as regime-specific, not composed.

## Battery incident — the label-trust gate (same day)

The first battery run at mask scale caught what the single-burst corpses could not:
**WN6 canonical 6M regressed to 1.78E-3** — 11,447 of 11,526 errors in ONE burst's final
block (w3/b3 seed 3003507, 10c/1r), a marginal deep-start block that converged under #81
flipping to non-convergence, exactly the b9-class chaos the basin campaign characterized.
Meanwhile the same build's **WN6 disjoint 6M IMPROVED to 26 errors / 4.01E-6, 264c/0r**
(#81: 51 / 7.86E-6 — the thin converged-residual class HALVED) and WN13 canonical was
perfect (0/3.24M, 176c/0r) — the pair model genuinely helps where it converges.

Mechanism of the flip: on the shipped hard iteration 0, the cancellation subtracts
h2b·x̂ using re-encoded labels that are up to ~49% wrong on deep-start blocks — unpriced
error injected into the very observation the chains equalize (the EM variance bump only
exists on soft iterations). A worse start loses recoverable blocks — the basin campaign's
2A result, reproduced at mask scale.

Fix (no knobs): **pair candidates require trusted labels** — the oracle path (truth,
cancellation exact) and the soft iterations (E[x], uncertainty priced by the variance
bump). The shipped hard iteration 0 runs the #81 single-lag path bit-exactly; the
converged fixed point still carries the pair model through the soft iterations.
`SolveTrainingTir(..., allowPair)`, threaded through `TurboReequalize(trustedLabels)`.
Acceptance: w3/b3 recovers; WN7 oracle stays 209; guards hold; the full battery re-runs
from scratch.

Composed ship state: pair TIR + per-segment h2b at 4 segments — **WN7 oracle 238 → 209
(−12%)**, shipped unchanged, guards clean. The remaining oracle floor is the fade-frame
class (b4 99, b5 60, b10 19, b7 19, b1/b6 6): partially-faded frames at 17% raw errors
under oracle labels, uniform within the frame, where finer segmentation adds estimation
noise faster than resolution (step 3) and the straddle pair is already modeled. Candidate
mechanisms for the next leg: the probe-anchored solve itself during partial fades (the
per-frame TIR/FF re-solve trains on U rows spanning the fade — a fade-crossing frame's
single FF solve may be the binding approximation), or FD-turbo (the pre-registered
escalation) which re-equalizes per-subband and sidesteps the time-domain solve's
stationarity assumption entirely. The labels basin remains attractor-bound
(`../2026-07-25-phase-b33-basin/`): shipped WN7 stays 2.49E-1-class until either the
model floor cut reaches the iterations (not yet: the split held 5c/6r throughout) or
FD-turbo changes the per-iteration map.
