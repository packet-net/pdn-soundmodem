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

### G2b measurements (2026-08-20, [schedules/](schedules/); prototype, 200 rungs, the failing block's coded errors)

| block | (i) pure soft, no refit | (ii) soft to 80, refit, hard | (iii) soft throughout, refit at 60 | shipped (soft 30, hard to 72) |
|---|---|---|---|---|
| w1 b2 | 1,432 @R60, 466 @R80, **239** from R100 | 1,432 @R60, 406 @R80, **40** from R100 | 1,029 @R60, **6** from R80 | ~24.5k (revert) |
| w2 b6 | 1,969 @R60, **0 @R81** (then b8/b9 drift: 14/43 by R200 - soft never terminates) | 1,969 @R60, **0 @R81**, holds | 1,665 @R60, **0 @R69**, holds | ~24.5k (revert) |
| w3 b9 | 2,733 @R60, 2,180 @R80, 1,586 @R120, **514** from R160 | 1,784 @R80 then *rises* to 2,997 (refit on a still-poor decode poisons it, the W5b1 condition) | 2,403 @R60 then rises to 2,890 (same) | ~24.5k (revert) |
| control w0 b4 | 103 | 18 (the W5b2 class) | 18 | 18 |

**Reading.** The soft phase keeps descending well past the shipped cap of 30 on all three blocks; given the rungs, it converges w2 b6 exactly and takes w1 b2 to a self-consistent fixed point with 6-40 errors, but w3 b9 only to ~500 with the descent flattening from R160, and any refit before the decode is good poisons it (the refit gate would have to be tighter than 5 % churn for this block). Against the family mask (<= 32 errors per 3,243,776 bits, or the Poisson bound) that is 0 + 6 + ~500 on the disjoint family: roughly 1.6E-4, a hundredfold improvement on 1.75E-2 and not a gate. Wall-clock is the other cost: ~0.4 s per block-rung in the prototype, so a 100-rung soft phase is ~40 s for a 7.68 s block, paid only by the stragglers but paid by every one of them.

**Verdict per the rule:** "a variant converges some but not all -> report per block, and price the MMSE cold rung on the remainder before building anything." The model is not the deficiency (the probe-anchor bound decodes all three to zero with exact cancellation); the *iteration* is - its cold rung is an ISI-inclusive matched projection that starts every block at coin-flip and leaves the deep-fade blocks a long, sometimes unfinishable, descent. The next candidate is therefore a linear MMSE first rung on the anchored trajectory (ISI suppressed by the model rather than cancelled by decisions), priced on the prototype on these three blocks before any shipped change; a stability-driven soft handover (stay soft while churn falls) rides along as the schedule half, since it is measured to convert one block on its own. Registered as **G2c** when the build starts; until then WN8 stays at its W6 figures and nothing ships from G2.

## G2c - the MMSE cold rung (registered 2026-08-20 before the runs)

**Question.** Does a linear MMSE first rung on the anchored trajectory - ISI suppressed by the model instead of cancelled by decisions - start the three blocks inside the basin the soft schedule can finish from?

**Mechanism claim.** The cold rung is the one place the MFB-form receiver is not matched to the channel: R0 is a matched projection with every other symbol's interference still present, priced by a crude median, so a deep-fade block begins at coin-flip and must climb the whole way on decision-directed cancellation. A per-symbol MMSE estimate over the 26-tap response window (known probe chips subtracted exactly, neighbouring data chips treated as unit-power unknowns, the anchor-fit residual as the label-free noise estimate) starts each symbol at its linear-equaliser SNR and prices it honestly (effective SNR mu / (1 - mu)); every later rung is unchanged.

**Instrument.** `MS110D_MFBRX_MMSE0=1` on the prototype; calibration lane: with the knob off the prototype must reproduce the G2b summaries byte-identically. Specimens: the three failing blocks and the control, under schedules (ii) soft to 80 + refit + hard and (iii) soft throughout with refit at 60, 200 rungs. Reads: R0 errors per block (the starting point), the rung reaching 0 or the floor, the control's straggler.

**Kill/proceed rule (pre-committed).** All three blocks reach 0 under one schedule with the control at its W5b2 class or better -> G2d registers the shipped port (the MMSE cold rung plus a stability-driven soft handover behind the QAM16 scope, caps re-derived from the measured rung counts), corpse -> pins -> battery, gate decision by §5.3 on both families. Two of three -> price what remains (the third block's own floor under this start) and decide in writing whether exit (ii) with the new ceiling is the honest endpoint. None -> the cold rung is not the lever either; the program's WN8 endpoint is exit (ii) at the G2b schedule's ceiling, re-banked, with the three blocks recorded as the measured residual.

### G2c measurements (2026-08-20, [mmse0/](mmse0/); prototype with `MS110D_MFBRX_MMSE0=1`, 200 rungs)

**Calibration lane:** knob off, the prototype reproduces the G1 bank summary for WN8 seed 508 byte-for-byte (`calibration-knob-off-seed508.txt` against `../2026-08-20-poorgate-g1/calibration/wn8-seed508-after.txt`).

| block | R0 (was ~24k) | R1 | R2 | R3 | fixed point | rung reached | whole burst at R200 |
|---|---|---|---|---|---|---|---|
| w1 b2 | 10,297 | 2,491 | 372 | 49 | **0** | R5 | **0** |
| w2 b6 | 12,933 | 5,574 | 1,689 | 520 | **0** | R6 | **0** |
| w3 b9 | 8,713 | 3,121 | 967 | 307 | 34 (60 from R20, 34 from R60-80) | - | 34 |
| control w0 b4 (W5b2's straggler, 18) | 11,450 | 2,137 | 436 | 99 | **0** | R5 | **0** (was 32) |

Schedules (ii) and (iii) give identical numbers: the blocks are done long before either schedule's handover matters.

**Reading.** The cold rung was the deficiency. With ISI suppressed by the model at R0, every block of every specimen starts at 4-13k errors instead of coin-flip and the soft cancellation finishes in five or six rungs where the old start needed thirty to a hundred, or never; two of the three blocks that carried the whole disjoint 1.75E-2 are clean, the control burst's long-standing straggler is clean, and the third block sits at a self-consistent 34. The wall-clock consequence is as large as the accuracy one: a WN8 block now converges inside the soft phase in single-digit rungs.

### G2c verdict, and G2d (registered 2026-08-20 before the shipped code)

Two of three, with the rule's "decide in writing": the honest endpoint is not to bank a prototype number but to ship the cold rung and let the battery say exit (i) or (ii). **G2d:** the MMSE cold rung ported into `Ms110dMfbBlockDecoder.Project` for the uncancelled rung - structurally scoped to QAM16 in this leg, so the 8PSK ensemble's MFB candidates and the WN7 censuses stay byte-identical (8PSK gets its own registration) - with the anchor-fit residual computed in the probe-anchor loop as the label-free noise estimate, preallocated buffers, no dictionary, the schedule and caps unchanged (caps are upper bounds and the blocks now terminate far inside them). Calibration: knob-free, so the corpses are the lane - 509 and 10509 w0/b0, the three G2 bursts, the canonical leaking burst (seed 508 w1/b1, channelSeed 1001509, 1,029 errors at W6), then the five guard pins, then the full battery. Kill/proceed: WN8 Poor §5.3-green on both families -> exit (i) for WN8, gate armed, `MeasuredOnlyBank` emptied; improved but not green -> exit (ii), the new counts re-banked; any WN7 or non-WN8 census moved, or AWGN WN8 off zero -> stop and diagnose.

### G2d measurements (2026-08-20; the shipped cold rung, QAM16 scope; [g2d-corpses/](g2d-corpses/), [g2d-pins/](g2d-pins/))

Hermetic MS110D namespace 297/0 on the build. Corpses through the shipped decoder (every block now converging in **4-7 rungs**, soft phase, exact fixed points):

| corpse | W6 / G0 | G2d |
|---|---|---|
| WN8 509 w0/b0 (canonical specimen) | 112 | **0** |
| WN8 10509 w0/b0 (disjoint specimen) | 32 | **0** |
| WN8 10508 w1/b0 (b2) | 24,819 | **0** |
| WN8 10508 w2/b0 (b6) | 24,465 | **0** |
| WN8 10508 w3/b0 (b9) | 24,468 | 62 |
| WN8 508 w1/b1 (the canonical leaker) | 1,029 | 19 |
| WN7 507 w0/b0, 10507 w0/b0 | 0 / 0 (ensemble 0 / 2 selected) | 0 / 0, same selections (scope held) |
| the five §6 pins | exact | exact (WN7 w1/b0 0 coded / selected 2, as G1d) |

The battery decides the exit; it runs on this build with G0's form.

### G2d battery (2026-08-20, [battery/](battery/), the G0 form on the shipped cold rung, 21:01 to 21:49 under the G3 ladder's load)

- **Poor WN8 canonical: 31 / 4,325,120 = 7.17E-6** (from 1,254 = 2.90E-4) - 31 errors, direct BER below the 1E-5 mask: **the canonical family clears §5.3.** Residue: w0/b1 12, w1/b1 19 (the burst that leaked 1,029 at W6), every block a fixed point.
- **Poor WN8 disjoint: 77 / 4,325,120 = 1.78E-5** (from 75,713 = 1.75E-2) - 1.8x over the mask. Residue: w3/b0 62 (block b9, the one G2c left at 34 in the prototype) and w1/b1 15.
- **AWGN WN8 0 / 4,325,120.** Every other point at its baseline digits; **113 of 120 censuses byte-identical** (G0 for the non-WN7/WN8 points, G1d for WN7 - the 8PSK ensemble is untouched by a QAM16-scoped cold rung, as registered), the seven that differ being WN8's Poor censuses, every line of which now reads 0, 12, 15, 19 or 62. The lanes report rc=1 only because the pre-flip `MeasuredOnlyBank` asserted 1,254 / 75,713, as built.
- Full hermetic suite on the final main build **1794 / 0 / 131**; `SourceTextTests` green.

## Verdict

**Exit (ii) for WN8, per the rule - improved, re-banked, not yet green on both families.** WN8 Poor moves from 2.90E-4 / 1.75E-2 to **7.17E-6 / 1.78E-5**: the canonical family at the mask, the disjoint family 1.8x over on the strength of one block (w3 b9, 62 errors) and one 15. `MeasuredOnlyBank` re-banks to 31 / 77; WN8 stays the one measured-only point; this battery's WN8 censuses are WN8's byte-identity baseline from here on. Sim-only by rig physics (successor plan §6). What the program hands on for WN8: two blocks with self-consistent wrong fixed points in the low tens (w3 b9 at 62 under the shipped schedule, 34 in the prototype; w1/b1 disjoint at 15), reachable now in a handful of rungs, so a successor's corpse iteration is seconds; the candidates in order are the G2b stability handover (measured to matter on exactly these stragglers before the cold rung existed), a second decode schedule for the ensemble-style selection G1d built (the MFB's own two schedules priced by residual), and the W3 moment observables as extra anchors. Also banked: the cold rung scoped to 8PSK is unmeasured (G1d's ensemble candidates would change) and is its own registration; the shipped MFB's per-block wall-clock fell with the rung count (4-7 rungs against 10-40).
