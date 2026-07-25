# Phase B3.3 basin re-measurement — lever 1 on the eigen-TIR per-iteration map (design, pre-registered 2026-07-25)

Written BEFORE the re-measurement, per the Phase B discipline.

## Why the closed basin reopens on new terms

The basin campaign (`../2026-07-25-phase-b33-basin/`) closed as a decisive negative
result: the converge-or-wander split was decided by the soft loop's attractor structure,
and neither start quality (levers 1/2A/2B) nor prior damping (lever 3) moved it. The
lever-1+3 implementation was banked "if a stronger per-iteration model changes the
contraction calculus".

The eigen-TIR refit (#83, `../2026-07-25-phase-b33-fadecross/`) IS that change: it
sharpened the per-iteration channel LLRs enough to flip two corpse blocks and ~13
battery bursts into convergence on identical first decodes. Fresh baseline on main
(2badde9), `corpse/wn7-baseline-main.txt`: shipped 72,666 / 7c/4r — **b9, the campaign's
razor-edge chaotic flip block, now converges at iteration 10** — and the four remaining
wanderers sit at decode-changes 1309 (b7), 3472 (b4), 3953 (b5), 4755 (b10) at cap 24,
versus the old wander states' self-reinforced plateaus. Oracle is 15 (all in b5): the
model floor no longer binds; the shipped-vs-oracle gap is entirely the basin.

## The re-measurement

**Lever 1 exactly as banked** (per-frame first-pass max-log LLR scale = 1/N̂₀ with N̂₀
the PRE-solve probe MSE, head→tail interpolated across the data span; `_probeMse` EWMA
on the flat branch; the in-sample-optimism correction is already in the banked form):
the banked patch applies MINUS its lever-3 hunk (the γ = 0.75 damping line and its
comment) — one lever per measurement, and lever 3 was measured mechanism-dead
independently of the calculus (trajectories shifted, split unchanged).

Mechanism claim being tested: calibrated first-pass LLRs give the hybrid bootstrap's
iteration-0 solve honestly-weighted labels AND give the outer-code SISO
correctly-scaled channel inputs on every soft iteration — on the old map this improved
9/11 first decodes and block-resolving confidence but could not overcome the attractors;
on the new map the attractors are measurably weaker (b9's flip, b7 at 1309), so the
same calibration may now tip marginal wanderers.

## Pre-registered acceptance

1. **WN7 corpse w0/b0**: at least one of {b4, b5, b7, b10} newly converges with NO lost
   convergences among the seven (b9 is the watch case — it flipped chaotically under
   this lever on the old map). Shipped errors improve accordingly; oracle stays ≤ 15.
2. **Guards**: WN6 corpse and WN13 specimen hold 0 shipped / 0 oracle (outcome-identical;
   bit-exactness is NOT expected — the lever rescales every first-pass LLR).
3. **Battery gates any ship**: full standard set; WN6 6M both families must hold their
   new at-mask state (≤ ~8.8E-6 class, no marginal-block flips — the #82 lesson: this
   lever's failure mode is trajectory chaos on marginal blocks, and the battery is the
   only instrument that sees it at scale).
4. If the corpse shows no conversion or any lost convergence: the lever goes back in the
   bank, the negative result is recorded, and the leg falls through to the secondary
   registered structures (per-segment BCJR noise pricing; the within-frame tail ramp) or
   B3.4 per the roadmap.

Files land here as the measurements run.
