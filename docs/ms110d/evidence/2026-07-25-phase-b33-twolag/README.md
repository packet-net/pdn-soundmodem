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
