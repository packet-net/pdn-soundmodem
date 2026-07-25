# Phase B3.3 model front III — the weak-cursor floor and the floating-gain (eigen) TIR (design, pre-registered 2026-07-25)

Written BEFORE the implementation, per the Phase B discipline. Follows the two-lag leg
(`../2026-07-25-phase-b33-twolag/`, PR #82), whose closing note localized the remaining
WN7 oracle floor (209 ≈ 5.2E-4 on the w0/b0 corpse) to "partially-faded frames" and named
the per-frame solve's stationarity assumption through fade crossings as the leading
suspect, with FD-turbo the pre-registered escalation.

## The corpse falsified the fade-crossing hypothesis first

All measurements on the standing WN7 corpse (`_WN=7 _SNR=19 _SEED=507 _ORACLE=1`,
baseline oracle 209 = b4:99 b5:60 b7:19 b10:19 b1:6 b6:6, shipped 107,938 / 5c/6r),
joined against the recorded Watterson path-gain truth (96 Hz trajectories, aligned to the
demod symbol clock at +75 symbols by maximizing the anti-correlation of raw oracle error
density with instantaneous combined power; the frame-gain EWMA is lag-contaminated and was
not used for alignment). Corpse artifacts in `corpse/`.

1. **Total channel power is healthy where the errors are.** 74% of the raw oracle error
   mass sits in frames whose minimum combined power exceeds 0.3 (36% above 0.75); the
   discriminator is the CURSOR path alone — 74% of error mass has min|p0| < 0.45
   (`wn7-frame-classification.txt`). The "fade-frame" class is really a WEAK-CURSOR
   class: the energy is present but sitting in the echo path.
2. **Within-frame channel motion is acquitted.** Splitting frames on cursor level ×
   complex tap motion: the BCJR noise floor and the error counts follow the LEVEL
   (median n 0.011–0.013 / ~1–2 errs at strong cursor vs 0.037–0.039 / ~27–36 errs at
   weak cursor) and barely respond to motion at fixed level (corr(n, motion) ≤ 0.19
   within level classes, `wn7-level-vs-motion.txt`). The pre-registered "stationarity
   through fade crossings" mechanism is dead: split solves, fade-aware row weighting,
   and probe-anchored trajectories all target motion, and motion is not the driver.
3. **Representation and anchor estimation are both acquitted.** A 4-anchor
   piecewise-linear fit of the TRUE complex trajectory is exact to relative MSE ≤ 1E-4
   (1 Hz fading over 107 ms frames; a single line per frame is already ≤ 0.5% worst
   case), and the estimated segH1 anchors deviate from a per-frame scaled truth by a
   fraction of the frame's own noise floor, no worse in error frames than clean ones
   (`wn7-anchor-vs-truth.txt`).
4. **The mechanism is the monic TIR target.** New `turbo-frame` instrument (per-frame
   anchors, BCJR noise floor n, FF tap energy ffE, training SSEs, emitted during the
   oracle pass): by cursor-strength quartile, median ffE climbs 10.1 → 24.5, n climbs
   0.0089 → 0.0471 (5.3×), errors climb 0.5 → 48.7 per frame, and n/ffE climbs
   0.00085 → 0.00173 (`wn7-ffe-quartiles.txt`). The shortening solve trains
   ff·window + b·x[u−d] ≈ x[u] with the lag-0 target tap PINNED AT 1 (monic): when the
   physical cursor path fades, the FF must invert a weak tap to manufacture unit cursor
   gain, bounded only by the ridge — the boost amplifies the AWGN (the ffE factor,
   ~44% of the excess) and every unmodeled component (fractional sidelobes beyond the
   {d, d±1} pair, the rest). The classic channel-shortening result (Melsa/Younce/Rohrs;
   Al-Dhahir's MMSE variants) says exactly this: monic-constrained shortening is
   noise-boosting; unit-norm target constraints are the optimal family.
5. Secondary structures, noted and deliberately NOT targeted this lever: a monotone
   within-frame error ramp toward the frame tail (bin 604 → 1231 over 16-symbol bins,
   ~12% of error mass asymmetry — consistent with staleness against the head-anchored
   timing/phase grid) and the frame-constant BCJR noise floor across heteroscedastic
   frames (`wn7-inframe-profile.txt`).

## The design: floating-gain TIR (unit-norm target eigen solve)

Replace the monic target with a floating one in `Dfe.SolveTrainingTir` only (the turbo
re-solve; the first pass and all tracking paths are untouched):

    min over (ff, t) of Σ w·|ff·window[u] − t·(x[u], x[u−d], …)|²  s.t. ‖t‖ = 1

- **The blocks already exist.** The training accumulation carries the full Gram over
  [W | P] and the RHS against x: A = WᴴW (+ genie noise diag + ridge), B = Wᴴ[x, P_d…]
  (RHS slice + Gram columns), D = [x, P_d…]ᴴ[x, P_d…] (target energy, RHS conjugates,
  P-block Gram entries). For each lag candidate, R = D − BᴴA⁻¹B is a 2×2 (single lag)
  or 3×3 (pair) Hermitian PSD matrix; the minimizer t is its smallest eigenvector, the
  FF is A⁻¹Bt, and the exact data SSE is computed against the unridged blocks exactly
  as today. Smallest eigenpair by inverse iteration on the Cholesky of R + εI
  (ε = 1e-6·trace/dim), fixed start e₀, two iterations — deterministic and it reuses
  the existing Cholesky helpers. A is factorized ONCE per frame (with λ = ridge·tr/f,
  identical to today's null-candidate λ) and shared across all lag candidates.
- **The null path is bit-identical by construction.** For the feed-forward-only
  candidate the target is the scalar g with |g| = 1 — a phase that cancels in the
  BCJR's likelihoods and is pinned to today's g = 1 by simply keeping the existing
  `SolveSubset(default)` call. Echo-free frames (AWGN, static, every frame the margin
  rejects) run today's solve exactly.
- **The acceptance margins carry over unchanged.** Free-parameter count per candidate
  is IDENTICAL to today: monic adds one free complex coefficient per lag (2 real);
  unit-norm adds (g, b) minus the norm and global-phase gauges — also 2 real per lag.
  The 4·ln(L)·SSE₀/rows single-lag threshold and the 4·SSE₀/rows pair gate keep their
  derivations; SSEs remain comparable because the null target is also unit-norm.
- **The ridge anchor is dropped for lag candidates only.** Today's solve ridge-anchors
  toward the tracking taps (a Kalman-style prior for the sequential probe solve). The
  anchor term is linear in the solution and would both break the eigen reduction and
  phase-pin the floating target toward the tracking FF; the turbo's 256-row batch
  dominates the tiny λ anyway. The null candidate keeps the anchor (bit-exactness).
- **Downstream is already scale-free.** TurboCore re-estimates segH1/segH2/segH2b by
  correlation on the FF output and prices the BCJR noise from the assembly residual —
  none of it assumes unit cursor gain. `TirSolve.Coefficient` keeps its "post-FF echo
  relative to cursor" meaning as c/g (guarded when |g|² < 1e-4: the raw c is reported);
  the label-trust pair gate (§B3.3, PR #82) is untouched.

Why this fixes the measured mechanism: at weak cursor the unit-norm optimum shifts
target weight onto the strong echo column (|g| shrinks, |c| grows) instead of forcing
the FF to invert the fade — the FF relaxes toward a matched filter for the composite
channel, the noise gain falls, and the chain BCJR (exact for h2 ≥ h1) collects the
information from both paths in the trellis, where it belongs.

## Amendment 1 (same day, before any corpse measurement): eigen candidates are
## miscalibrated for selection — float the gain as a refit on the accepted lag set

The unit test caught it immediately: with eigen solves used as the CANDIDATES, the
echo-free channel test accepted a fake lag-1 echo. The margin argument above ("same
free-parameter count, so the 4·ln(L) threshold carries over") is wrong for the eigen
family: the target columns x[u] and x[u−d] have post-projection residuals that are
MUTUALLY CORRELATED through the shared equalizer window, so the 2×2 R has an
off-diagonal of order ρ·√(R₀₀R₁₁) even on echo-free noise, and λmin dips below
min(R₀₀, R₁₁) by a FIRST-ORDER gauge gain |R₀₁| — not the second-order χ² noise fit
the monic threshold was calibrated for. Recalibrating the margin for correlated
residual Gram eigenvalues is analytically messy; instead the design becomes two-stage:

- **Selection and acceptance stay monic and bit-identical to #82** — same candidates,
  same SSEs, same thresholds, same label-trust pair gate. Echo-free frames and every
  rejected frame keep today's solve exactly (the AWGN/static bit-identity promise now
  holds trivially).
- **The floating-gain eigen solve runs as a REFIT on the accepted lag set only**,
  replacing the installed FF and the reported coefficients (c/g ratios) for frames
  where the monic evidence already established the echo. The weak-cursor fix lives
  exactly in the accepted-TIR population — which the corpse showed is where the
  weak-cursor frames sit. If the refit degenerates numerically the monic solution
  stands. `SseTir` continues to report the monic acceptance evidence.

The pre-registered acceptance criteria below are unchanged.

## Pre-registered acceptance

1. **WN7 corpse w0/b0**: oracle falls materially from 209 with the fall concentrated in
   the weak-cursor frame class; the instrument must show the mechanism (loP0 median ffE
   drops from ~23 toward the ~10–13 strong-cursor range on TIR-accepted frames, loP0
   median n toward the 0.011–0.013 floor). Shipped improves or holds vs 107,938 /
   5c/6r; any wander block reaching convergence is the basin dividend (sharper
   per-iteration LLRs strengthen the contraction — the standing coupling from the twolag
   note).
2. **Guards**: WN6 corpse and WN13 specimen hold 0 shipped / 0 oracle; AWGN and static
   battery legs bit-identical (null-path exactness); Phase A regressions green.
3. **Unit tests**: straddle-pair recovery and single-lag rejection keep passing
   (Coefficient = c/g preserves the values); new weak-cursor test — cursor 0.3, echo
   1.0: the eigen solve must accept the lag with |g| < |c| and an FF energy well below
   the monic solve's, and recover the true ratio.
4. **Battery**: the full standard set gates the merge (WN7 3M×2, WN6 6M both families,
   WN13 canonical + disjoint, WN2×2, WN1×2, smokes, AWGN, static, Doppler) — §5.3
   budgets, disjoint seeds +10000.
5. **Escalation unchanged**: FD-turbo if WN7 stalls at mask+2 dB after this lever.

Files land here as the measurements run.

---

## Measured results (same day, on the amended two-stage design)

### Unit tests

All 12 DfeTests green: the restored monic selection keeps the echo-free rejection and
the straddle-pair values; the new weak-cursor test (cursor 0.3, echo 1.0) recovers the
3.33 echo-to-cursor ratio with FF energy < 3 (the monic solve would sit near 11). Full
suite 696/0.

### WN7 corpse w0/b0 — the floor collapses

**Oracle 209 → 15 (−93%)**: b4 99→0, b5 60→15, b7 19→0, b10 19→0, b1 6→0, b6 6→0
(`wn7-eigen-summary.txt`). **Shipped 107,938 → 72,666 with turbo 5c/6r → 7c/4r** — two
wander blocks reached convergence, the predicted basin dividend (sharper per-iteration
LLRs strengthening the contraction); first decodes bit-identical (the first pass is
untouched). Instrument signature exactly as pre-registered
(`wn7-eigen-ffe-quartiles.txt`): weakest-quartile median ffE 24.5 → 16.6 and loP0
median 22.9 → 13.1 (into the pre-registered 10–13 band), weakest-quartile n
0.0471 → 0.0254, and n/ffE now FLAT across quartiles (0.00137–0.00159, previously
0.00085 → 0.00173) — the boosted-unmodeled-residual gradient is gone.

### Guards

WN6 corpse (w0/b0 @14 dB) and WN13 specimen (w3/b5 @11 dB): 0 shipped / 0 oracle,
turbo accounting and uncoded counts bit-identical to the #82 baselines
(`wn6-eigen-guard.txt`, `wn13sp-eigen-guard.txt`). Echo-free/rejected frames never
reach the refit by construction.

### Battery (full §5.3 budgets, canonical + disjoint; `battery.log`)

All green. **WN6 6M canonical 57 errs = 8.79E-6 — a DIRECT formal pass** (stuck at
79/1.22E-5 since #81) **and disjoint 57 = 8.79E-6** (equal totals a coincidence — the
error-carrying bursts are entirely different; census CSVs here, the old canonical
clusters incl. w2/b5's 31 are gone): WN6 moves from AT THE BOUNDARY to **AT MASK, both
families**. WN13 0/3.24M + 0/6.49M, 352c/0r (the #82 disjoint 2-error residue also
gone). WN7 shipped 2.44E-1 → **1.73E-1** canonical / 2.55E-1 → **1.90E-1** disjoint
(57c/31r / 54c/34r from 44c/44r / 42c/46r) — the first shipped movement since the basin
campaign closed. WN2 15/12 (bounds 8.13E-6/6.89E-6), WN1 0/0 (1984c/0r each), smokes
WN5 6 / WN4 0 / WN3 0, AWGN zero across WN0 −6 dB / WN1 −3 dB / K=48 legs (first
attempt SIGTERMed by a concurrent agent's process kill on the shared box; clean
detached retry), static WID2 zero, Doppler 3/3 zero.
