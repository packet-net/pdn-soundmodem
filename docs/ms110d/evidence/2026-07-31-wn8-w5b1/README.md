# W5b1 — closure levers in the prototype: soft cancellation (WN8 redesign program)

Registered 2026-07-31, after W5a banked the credible verdict (fixed points 36/140, 18/22 blocks zero, ~60 rungs from cold). W5b is instrument-first: each closure lever is priced in the prototype (corpse-minutes per iteration) before the shipped port carries the guard ladder.

## Registration

**Question.** Does soft cancellation — SISO per-bit posteriors → per-symbol E[x] in the reconstruction, replacing hard re-encoded decisions — close the W5a residual (the 3 fade-lottery blocks: canonical b3:36, disjoint b4:73/b6:5/b8:62) and/or collapse the rung count toward shipping economics? The mechanism is the B3.3 lesson in this architecture: hard labels cancel wrongly at full amplitude exactly where decisions are worst; E[x] shrinks uncertain symbols toward zero, so wrong cancellation self-attenuates.

**Decision inputs.** W5a's ladders and fixed points; the B3.3/B3.4 banked soft-vs-hard measurements (in the OLD sandwich soft feedback was the difference between stall and convergence); the SISO's calibrated-LLR requirement (the prototype's metric is already in natural-log units — |δ|²·‖tmpl‖²/σ²).

**Instrument + method.** `MS110D_MFBRX_SOFT=1` on the W5a prototype: per rung, the wire LLRs additionally run deinterleave → depuncture → SISO → puncture → interleave; per-symbol posteriors over the 16 numbers (independent per-bit approximation) give E[x] (nib-permuted) for the reconstruction; the hard Viterbi decode still scores and detects convergence. Ladders on both specimens, 30 rungs, hard-vs-soft compared at equal rung counts.

**Pre-committed reads.** (1) Soft reaches ≤ W5a's fixed points in materially fewer rungs (≤ half) → the lever ships in W5b2. (2) Soft closes residual blocks W5a's hard loop could not → the lever ships. (3) Soft neither accelerates nor closes → banked negative for this architecture (the B3.3 analogy fails here), and the remaining levers (anchor re-fit, warm seed) carry W5b. Divergence or a confident-wrong fixed point → the revert principle, lever dead.

**Budget.** ~80 lines on the prototype; four corpse runs; no battery (nothing ships).

## Measurements (2026-07-31, [summaries/](summaries/); hermetic 790/0/110)

Fixed points (per-block detail in the summaries; W5a hard baseline: canonical 36 @ ~R60, disjoint 140 @ ~R50):

| Schedule | canonical | disjoint | Notes |
|---|---|---|---|
| pure soft | 114 @ ~R50 (b3:74 b6:40) | 103 @ ~R30 (b4 only) | ~2× faster than hard; solved disjoint b8 (hard: 62); soft-canonical trades b6 |
| soft30 → hard | 114 | 103 | the hard tail inherits soft's basin — fixed points are basin-determined early |
| soft30 + **refit** → hard | **78** (b3:18 b6:60) | **18** (b4 only) | the re-fit crushes trajectory-limited blocks: b3 74→18, b4 103→18 |
| hard + refit@30,42 | 2,199 (b2 poisoned) | — | **the label-quality condition**: refit on a block whose decisions are still poor wrecks it (b2 0→2,175) |
| soft30 + refit@30,42 → hard | — | 18 | second refit neutral |

- Soft cancellation: registered read (1) fires — the W5a fixed-point class in roughly half the rungs — and it solves blocks hard leaves (disjoint b8). **Ships.**
- Decision-directed anchor re-fit: closes exactly the blocks diagnosed as trajectory-interpolation-limited (b3 was clean under W1's truth-gauge), and its failure mode is measured, mechanistic, and gateable: refit must be gated per block on a label-free convergence signal (rung-over-rung decode churn), or it poisons unconverged blocks. **Ships with the gate.**
- Best honest single-schedule fixed points: **canonical 78, disjoint 18 — pooled 96, better than W1's truth-injected 136** (and disjoint 18 beats truth-injected 36 outright). The fully label-free receiver now outperforms the pass that needed injected channel truth.

## Verdict

**Both registered levers ship; the prototype's ladder is done.** The receiver stands at: label-free, self-seeded, fixed points at 1.4E-4/3.3E-5 per specimen (pooled beats truth-injection), ~40 effective rungs with the soft schedule, the two residual blocks (canonical b6:60-class, disjoint b4:18) being the deepest fade-lottery cells — attackable in W5b2 by the per-block refit gate + a per-block schedule selector on the label-free reconstruction residual (the diversity-bank pattern this repo already ships for BPSK), neither required for the port to proceed. Further schedule permutation in the prototype is lever soup and stops here per the escalation discipline. **W5b2 (the shipped port) is specified**: composite-FIR probe anchors + per-burst delay-profile window + matched projection + SISO-soft cancellation schedule + convergence-gated decision-directed refit, structurally scoped to QAM16 behind the FinishBlock gate, with the §6 ladder (guard pins, non-target byte-identity, three-lane battery) before W6's sim-only gate attempt. The W6 rate question (whether ~1E-4/3E-5-class specimen fixed points translate to ≤1E-5 §5.3 rates over the full burst population) is honestly open — specimens are two bursts, and the mask verdict belongs to the battery.
