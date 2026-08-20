# G2 - WN8's first-block bootstrap (MS110D Poor-gate successor program)

Registered 2026-08-20 after G1d's exit (i) for WN7, per [poor-gate-successor-plan.md](../../poor-gate-successor-plan.md) §3/§4, before any code.

## Registration

**Question.** What defeats block 0 on the three disjoint WN8 bursts that carry the whole 1.75E-2 (w1/b0 channelSeed 1010509, w2/b0 2010509, w3/b0 3010509: ~24.5k errors each, both shipped schedules reverting to coin-flip), and which banked lever prices to the mask class on them?

**Decision inputs (banked).** W6: the three bursts and the W6a-recovered w0/b1; canonical's one leaking burst (w1, channelSeed 1001509, 1,029 errors). W1b: the matched-filter bound on the exact channel decodes both w0/b0 specimens to zero (never run on these three bursts). W3: label-free probe-anchor trajectories at -29/-30 dB NMSE on the w0/b0 specimens, zero coded errors through the bound (never run on these three). W5a: the B3.5b phenomenon at WN8 - acquisition centring the chip clock on the echo, fixed by the per-burst delay-profile window. G1d: the block-0 probe-geometry fault (the preamble's closing probe is unshifted) - it did not touch Long-interleaver blocks, so it is not this mechanism. The shipped decoder's per-rung diagnostics (`mfb-rung`, `mfb-block`) and the prototype's block anatomy (window, per-anchor energy and peak, the demod's lock, anchor-fit and truth-reconstruction residual per block, per-rung coded errors per block).

**Mechanism candidates (pre-registered, to be discriminated by the anatomy, not assumed).**
1. *Window placement* - the per-burst delay-profile window, built from block 0's first eight probes, misplaced for these bursts (the B3.5b class): the b0 anatomy's window and per-anchor peaks against the truth-recon residual of every block would all be bad, not only block 0.
2. *Trajectory at the burst start* - block 0's left boundary has the preamble's closing probe as its only anchor and the demodulator's coarsest CFO/timing state: block 0's truth-recon and anchor-fit residuals would sit above the other blocks', and the bound with probe anchors would fail at b0 where the exact-channel bound passes.
3. *Detection bootstrap* - model and window fine (residuals in family, the probe-anchor bound clean at b0) but the schedule never descends from the coin-flip R0 on that block: a basin question, answered by schedule diversity or a better-seeded first rung.
4. *Acquisition state* - a lock that is wrong in a way later blocks recover from (CFO refinement); visible in the lock line and the residual-per-block profile.

**Instrument + method.** On the four bursts (w0/b0 as the healthy control, then w1/b0, w2/b0, w3/b0 of seed 10508, +23 dB), in parallel: (a) the prototype under the W5b2 schedule (`MS110D_MFBRX_SOFT=1 SOFTUNTIL=30 REFIT=1 ITERS=72`), (b) the shipped decoder's autopsy corpse with its `mfb-*` diagnostics, (c) the matched-filter bound with `MS110D_MFB_TRAJ=truth` and `=probes`. Reads per block: exact-channel bound, probe-anchor bound, prototype fixed point, shipped outcome, truth-recon and anchor-fit residuals, window and lock. Then price the surviving lever against the truth seam before any build (plan §4: build only what prices <= ~2x mask-class on the three).

**Kill/proceed rule (pre-committed).**
- Exact-channel bound clean at b0 on all three, probe-anchor bound clean at b0 on all three -> mechanism 3 (or 1 with a visible window signature): the lever is on the decoder's schedule side (a third schedule, a seeded first rung from the probe-anchor MFB's own R0), registered as G2b with a corpse-first build.
- Exact-channel bound clean, probe-anchor bound failing at b0 -> mechanism 2: the lever is trajectory information at the burst start (the preamble as a leftward anchor, the W3 moment track for block 0); price by re-running the bound with a preamble-extended anchor series before building; G2b builds only if the priced bound is clean at b0.
- Exact-channel bound itself failing at b0 on any of the three -> that block is a waveform floor event at this operating point (an honest erasure the r3/4 code cannot absorb); banked as such, the lever list shrinks to the other two bursts, and if all three are floor events the program's WN8 endpoint is exit (ii) with the measured ceiling re-stated.
- Whatever the route: the first battery that ships anything must keep WN7 and the 112 non-WN8 censuses byte-identical and AWGN WN8 at 0.

## Measurements (2026-08-20, [anatomy/](anatomy/))

**First correction, to the premise.** The W6 closeout's "w1/b0, w2/b0, w3/b0" are *burst* indices (worker 1 burst 0, and so on), not block indices; its "first-block" reading was a slip that this registration inherited in candidates 2 and 4. The failing block on each burst is a single mid-burst block: **b2 on w1/b0, b6 on w2/b0, b9 on w3/b0**. Every other block of those bursts converges in 8-33 rungs under the shipped schedule with the same per-burst window, so window placement (candidate 1) and burst-start state (candidates 2 and 4) are out.

**The bound.** Matched-filter bound on the exact channel: **0 on all 11 blocks of all four bursts** (w0/b0 control, w1/b0, w2/b0, w3/b0). With **label-free probe-anchor trajectories** (`MS110D_MFB_TRAJ=probes`, NMSE -29.4 to -30.1 dB on p0, -28.7 to -29.5 on p1): **0 on all 11 blocks of all four bursts** too. The model class is sufficient on the failing blocks with nothing but probes; what it lacks in the shipped decoder is exact cancellation. Prototype residual lanes agree: the failing blocks' anchor-fit residuals (6.65E-5, 6.93E-5, 7.42E-5) and truth-reconstruction residuals (3.03E-4, 2.99E-4, 2.98E-4) sit inside their bursts' ranges, not above them.

**The shipped decoder on those blocks** (`mfb-block` lines): soft schedule to the cap of 30, `handoverChurn` 10,837 / 11,731 / 9,347 (the refit gate correctly refuses), hard tail to the cap of 72 with `finalChurn` 1,519 / 66 / 3,040, no fixed point; the W6a hard-first fallback likewise (final churn 15,814 / 3,999 / 8,460). Revert to the first pass, ~24.5k errors per block, which is the whole disjoint 1.75E-2.

**The prototype's per-rung truth** (soft30 + refit -> hard, 72 rungs), the decisive read:

| block | R0 | R10 | R20 | R29 (last soft) | R30-R34 | R40 | R72 |
|---|---|---|---|---|---|---|---|
| w1 b2 | 24,057 | 12,476 | 8,990 | 6,505 | 5,235 -> 3,028 | 2,366 | 2,756 |
| w2 b6 | 24,054 | 13,529 | 10,291 | 7,744 | 6,318 -> 5,296 | 5,859 | 9,819 |
| w3 b9 | 23,584 | 11,046 | 7,935 | 5,860 | 5,055 -> 4,412 | 4,693 | 6,323 |

**The soft phase is still descending at the handover** - roughly 500 errors per rung at R29 on all three - and it is the hand-over at the fixed cap that stops it: the hard tail flattens w1 b2 at ~2.5k and *reverses* w2 b6 (5.3k back to 9.8k) and w3 b9 (4.4k to 6.3k). On the control burst the same schedule reaches 18 (its b4, the W5b2-known straggler). The mechanism is 3: a schedule that hands over on a rung count rather than on the decode's stability. No trajectory information is missing (the probe-anchor bound is clean), so the preamble-anchor and moment-track candidates are not the lever here; they stay banked.

## G2b - a stability-driven handover (registered 2026-08-20 before the runs)

**Question.** Does a soft phase that continues while the decode is still moving, handing over to the refit + hard tail only when it has stabilised, reach exact fixed points on the three blocks?

**Instrument.** The prototype's schedule knobs, three variants per failing burst plus the control, cap 200 rungs: (i) pure soft, no refit (`SOFTUNTIL=200 REFIT=0`); (ii) long soft then refit + hard (`SOFTUNTIL=80 REFIT=1`); (iii) soft throughout with a mid-soft refit once decisions are good (`SOFTUNTIL=200 REFIT=1 REFITAT=60`). Reads: per-rung errors on the failing block, the rung at which each variant reaches 0 or its floor, and what the control burst's straggler does under each. Then the shipped form of whatever wins is a churn-driven handover (stay soft while rung-over-rung churn keeps falling, hand over when it stops falling or reaches the refit gate), corpse-first, with the caps re-derived from the measured rung counts.

**Kill/proceed rule (pre-committed).** A variant reaches 0 on all three failing blocks within 200 rungs without moving the control burst above its W5b2 class -> G2c registers the shipped port (churn-driven handover behind the existing QAM16 scope), corpse -> pins -> battery, gate decision by §5.3 on both families. No variant converges any of the three -> the lever is not the schedule; the next registered candidate is the cold rung itself (a linear MMSE first rung on the anchored trajectory instead of the ISI-inclusive matched projection), priced the same way. A variant converges some but not all -> report per block, and price the MMSE cold rung on the remainder before building anything.
