# Phase B3.9 — WN7 model-floor tail: M0 anatomy of the 131 residual errors

Branch `ms110d-phase-b39-wn7-modeltail` from `main` @ `0b83fa5` (PRs #76–#95).
Date: 2026-07-26.

## Starting position (from B3.8, PR #95)

WN7 Poor is OPEN at **2.56E-5 canonical / 1.48E-5 disjoint** against the 1E-5 mask
(1.5–2.6× above the line), with **zero reverts in both families** (88c/0r ×2). The
detection/salvage story is closed; the residual is the converged model-floor tail.
The 131 battery errors sit in seven bursts (b38 battery censuses, banked on-box at
`tmp/b38/battery/` and in the #95 evidence):

| family | worker/burst | channelSeed | errors | firstErr | lastErr | span |
|---|---|---|---|---|---|---|
| canonical (SEED 507) | w1/b0 | 1000508 | 20 | 134950 | 247832 | 112,882 |
| canonical | w1/b1 | 1001508 | 25 | 80080 | 273323 | 193,243 |
| canonical | w2/b1 | 2001508 | 38 | 214585 | 331381 | 116,796 |
| disjoint (SEED 10507) | w0/b0 | 10508 | 16 | 182236 | 401750 | 219,514 |
| disjoint | w1/b0 | 1010508 | 12 | 209242 | 339068 | 129,826 |
| disjoint | w2/b1 | 2011508 | 6 | 262513 | 262526 | **13** |
| disjoint | w3/b0 | 3010508 | 14 | 213370 | 213389 | **19** |

Totals: canonical 83/3,243,776, disjoint 48/3,243,776, pooled 131/6,487,552.
Known pin: canonical w1/b0's 20 equals its oracle floor exactly (b3:6 + b6:14, the
B3.8 trio-basin oracle run). Known counter-pin: canonical w0/b0 ships 0 against an
oracle floor of 15 (b5) — **the oracle floor is a reference, not a bound**.

## M0 registration (before any instrument runs)

**Question.** Of the 131 errors, how much mass is *oracle-matched tail* (the
oracle-labels ceiling decode makes errors in the same blocks) vs
*excess-over-oracle* (the ship decode errs where the ceiling does not)? And does
the oracle ceiling itself clear the mask on these bursts?

**Method.** One corpse run per residual burst with `MS110D_AUTOPSY_ORACLE=1`
(7 runs; separate OUT dir per specimen — the B3.8 filename-collision rule). The
oracle side is passive (B3.8 evidence: the w0/b0-oracle summary reproduces the
ship pin bit-for-bit), so each run yields both the ship result and the per-block
oracle floor. Offline: ship per-block coded attribution by diffing the two lines
of `autopsy-decoded-*.txt` (11 blocks × 36,864 info bits); oracle per-block from
the `oracle coded errors per block` summary line. Validity check per specimen:
ship-side coded errors, collapses, and turbo counters must match the b38 battery
census row exactly (same channelSeed, same binary) — any mismatch invalidates the
specimen and is investigated before use.

**Statistics (pre-registered).** Per burst, per block: `ship_b`, `orc_b`;
`matched = Σ min(ship_b, orc_b)`; `excess = Σ max(0, ship_b − orc_b)`; and the
beats-oracle mass `Σ max(0, orc_b − ship_b)` (w0/b0-class, reported for context).
Mask arithmetic: 8 bursts × 405,472 = 3,243,776 bits/family; 1E-5 ⇒ §5.3 direct
budget ≈ **32 errors per family, ≈64 pooled**.

**Honesty gate (decision rule, pre-committed).**
- **Matched mass > budget** (pooled matched > ~64, or a family's matched above its
  §5.3 line): no model-front arm gets registered with a mask-flip bar — reaching
  the oracle's own ceiling would still fail the mask. Verdict in writing: the flip
  needs a different lever class (beating the ceiling the way ship-w0/b0 does, or
  WN7 stays measured-only), and the leg pivots or closes on that verdict.
- **Matched ≤ budget and matched + residual-elsewhere ≤ budget with excess
  covering the gap**: a model-front arm that closes ship→ceiling could flip the
  gate. Register ONE arm from the banked directions — first candidate the
  per-segment echo model in the converged loop (the oracle's own extra degrees of
  freedom: per-segment h2 + h2b vs the shipped single-lag scrambler-exact echo).
- **Grey zone**: any arm registration must state the combined arithmetic
  explicitly (how much excess it must convert, on which bursts) before code.

**Held-out policy (pre-committed).** M0 is instruments-only — no demod change, no
battery. All seven residual bursts necessarily enter the M0 *totals* arithmetic,
so they cannot serve as unseen specimens for a later arm. If the fork leads to an
arm registration: bank fresh held-outs from a **third seed family (SEED=20507)**
before design (scan workers/bursts for nonzero specimens, record totals only);
design may use frame-level evidence from canonical residual bursts only; the
disjoint residual bursts remain judgment-only (their M0 use is totals-only).

**Anatomy collected without decision weight**: oracle wire-error positions
(`autopsy-oracle-biterrs`) per residual block — fade-null vs echo-region vs
uniform; whether the two tight disjoint clusters (spans 13 and 19) are
single-block single-event; collapse/salvage counts per specimen.

## M0 measured (7/7 specimens, census cross-check OK on all)

All seven ship sides reproduce their b38 battery census rows exactly (coded
errors, collapses, turbo counters) — specimens valid. Per-block ship vs oracle
(`corpse/m0-anatomy.txt` for the full table):

| specimen | ship | oracle | matched | excess | beats-oracle | detail |
|---|---|---|---|---|---|---|
| c-w1b0 | 20 | 20 | 20 | 0 | 0 | b3 6/6, b6 14/14 — exact floor (the B3.8 pin) |
| c-w1b1 | 25 | 43 | 13 | 12 | 30 | b2 12/0, b4 6/11, b7 7/11, b8 **0/21** |
| c-w2b1 | 38 | 34 | 34 | 4 | 0 | b5 6/6, b8 32/28 |
| d-w0b0 | 16 | 38 | 6 | 10 | 32 | b4 6/6, b6 **0/18**, b8 **0/14**, b10 10/0 |
| d-w1b0 | 12 | 1 | 1 | 11 | 0 | b5 1/1, b9 11/0 |
| d-w2b1 | 6 | 6 | 6 | 0 | 0 | b7 6/6 — identical single event |
| d-w3b0 | 14 | 0 | 0 | 14 | 0 | b5 14/0 |

Totals: canonical ship 83 / oracle **97** / matched **67**; disjoint ship 48 /
oracle 45 / matched 13; pooled ship 131 / oracle **142** / matched **80**,
excess 51, beats-oracle 62.

**The honesty gate fires, twice over.**
1. Pooled matched 80 > 64; canonical matched 67 > 2× its 32-error family line.
   Even a lever that converted *all* excess-over-oracle mass leaves 80 pooled —
   arithmetic alone excludes any label-convergence arm from carrying a mask-flip
   bar.
2. Stronger: **the oracle ceiling itself fails the mask, worse than ship** —
   oracle canonical 97 (3.0E-5) / disjoint 45 (1.4E-5) / pooled 142 vs ship's
   131. The instrument that was to bound the model-front direction is *above*
   the shipped decoder. The per-segment-echo direction (the oracle's own extra
   degrees of freedom: per-segment h2 + h2b) is measured dead — the model that
   carries it, with true labels, loses to ship.

**Error-event character** (`corpse/m0-anatomy2.txt`): every residual ship error
is a single contiguous decoder error event of 14–24 info bits (c-w2b1 b8 has
three: 14+14+4); wrong wire bits carry mean |LLR| ≈ 1.0–2.8 against 12–68 for
right bits, in every residual block, in both the ship-final and oracle passes —
honest near-zero confidence, **no confidently-wrong / mispricing signature at
any block**. The SPIKE-UP-family pricing direction has nothing to reprice:
measured dead. Ship and oracle draw their events quasi-independently from the
same population (excess 51 vs beats-oracle 62, near-symmetric; in all four
oracle-clean excess blocks ship's *wire* is cleaner than the oracle's — 561 vs
753, 670 vs 916, 1067 vs 1246, 439 vs 602 sign errors — yet ship has the coded
event and the oracle does not; conversely c-w1b1 b8 ships clean through 6,223
wire errors while the oracle's 1,483-error wire yields 21). Coded outcome is
essentially decoupled from wire quality in this regime: the block interleaver
scatters each deep-fade stretch into isolated weak bits, and an error event
occurs where several near-zero LLRs happen to co-locate within a constraint
window — a combinatorial lottery over the block's weak-bit population. One
stretch is truly information-dead (d-w2b1 b7: both decoders make the identical
6-bit event); the wings are marginal stretches that flip in or out under any
perturbation (the B3.8 b5-wobble mechanism, now explained).

## Verdict (per the pre-committed fork): WN7's mask flip closes design-only

No arm is registered. The three candidate directions are all measured dead:
h1-model (B3.8 E2: model error ~2% of the priced floor), per-segment echo
(this M0: the oracle carrying it fails the mask below ship), and tail pricing
(this M0: no mispricing signature exists). Label-side arms are excluded by
arithmetic (matched 80 > 64 pooled even at 100% excess conversion). The
residual is the waveform's own floor at this channel/SNR — decoder error
events over deep-fade weak-bit populations, stochastic in every implementation
detail (redrawing the lottery moves errors between blocks; it does not shrink
the population; ship 131 vs oracle 142 are two draws from the same
distribution). Reducing it requires *adding information* — diversity,
retransmission, outer coding — outside this demodulator's scope.

**WN7 disposition: stays measured-only at 2.56E-5 canonical / 1.48E-5 disjoint
(mask 1E-5), zero reverts, cumulative 6,760×/13,180× across B3.6–B3.8. The
detection story closed in B3.8; the model story closes here.** No demod change
was made this leg, so no held-out was consumed and no battery is required (the
held-out policy's trigger condition never fired).
