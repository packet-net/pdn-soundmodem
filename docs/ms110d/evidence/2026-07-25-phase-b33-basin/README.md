# Phase B3.3 labels basin — first-pass calibration and bootstrap quality (design, pre-registered 2026-07-25)

Written BEFORE the implementation, per the Phase B discipline. The model front closed in
`../2026-07-25-phase-b33-tir/` (PR #81); this leg attacks the other half of the WN7 split:
the **labels basin** — shipped 107,938 coded errors on the w0/b0 corpse against an oracle
ceiling of 237. The mechanism below is read off the banked #81 instruments; the levers and
their acceptance are fixed here before the first run.

## The mechanism (banked evidence, `../2026-07-25-phase-b33-tir/corpse/`)

The turbo loop's outcome is decided by the quality of its iteration-0 start:

1. **The first decode varies 6.8–22.4% coded errBits per block** (llrstats `first` pass,
   `wn7-tir-priors-llrstats.csv`), and the outcome cleaves on that number: every block
   starting ≤14.8% converged (b0/b2/b3/b8; b9 at 17.6% converged at the cap's edge, i19);
   every block starting ≥17% except b9 wandered to the cap and reverted (b1/b4/b5/b6/b7/b10).
   The basin boundary sits near **15–17% first-decode errBits**.
2. **The wander is self-reinforcing, not iteration-starved**: cap 96 converts nothing
   (`../2026-07-25-phase-b33-tir/cap96/`) — b4/b5/b10 plateau at 4–6k decode-changes,
   b1/b6/b7 fall to a ~300–700-change wander without fixed points. Iteration 0 trains the
   TIR solve and channel estimates on hard labels that are 17–22% wrong (worse still in the
   coded/wire domain after the encoder spreads each info error over a constraint length),
   so segH1/h2 come out corrupted exactly where repair is needed, the chain BCJR equalizes
   against the wrong channel with full confidence, and the SISO round-trips a near-miss
   self-consistent state.
3. **The first-pass LLRs are far under-confident and barely separate right from wrong**:
   mean |LLR| on correct bits 1.6–1.9 where the calibrated chain-BCJR oracle stream runs
   19–46 on the same blocks (5–25×), and mean |LLR| on wrong bits is 0.8–1.1 — barely a 2×
   separation. This is the banked #80 measurement that forced the hybrid hard bootstrap.

## Root cause of (3), and why it also causes (1)

The first-pass LLR scales are fixed constants: BPSK `4·Re`, QPSK `2·(Re±Im)`, 8PSK max-log
`scale 2.0` (`Ms110dDemodulator.PushLlrs`/`PushMaxLogLlrs`). A max-log LLR is
`(min1 − min0)/(2σ²)`; the constants correspond to an assumed post-equalizer noise
σ² ≈ 0.25–0.7 per complex dimension — a design-floor assumption, 5–25× pessimistic at a
+19 dB operating point, and **frame-invariant**. Two consequences:

- **Inter-frame misweighting** — the soft-decision Viterbi is invariant to a global LLR
  scale but NOT to per-frame relative weights. On the Poor channel the post-equalizer
  noise varies strongly across the block (fades); with a fixed scale, symbols from a
  fade-corrupted frame enter the trellis with the same weight as clean-frame symbols. The
  interleaver spreads both across the block, so the fade errors are what drag the worst
  blocks to 17–22% first-decode errBits.
- **The timid absolute scale** is what made a soft bootstrap unviable (#80: E[x] blobs at
  half-magnitude, three iterations spent rediscovering confidence) and forced the hard
  iteration-0.

The fix is not a new constant — it is removing the constant: the demod already **measures**
the post-equalizer residual per frame on known symbols (the probe MSE that the frame
diagnostics print, computed by re-equalizing each probe with the post-solve taps). That
measured σ̂² is the calibration, genie-free, no rig knob.

## Levers, in measurement order (one lever, one corpse, one commit)

### Lever 1 — per-frame first-pass LLR calibration from probe residuals

Replace the fixed scales for the PSK modes with `1/(2σ̂²_u)`, σ̂²_u interpolated across the
data span between the bracketing probes' post-solve residual MSE (both are already computed
by the time the span is equalized — the fading branch processes data after the closing
probe's solve). Unify BPSK/QPSK/8PSK onto the exact max-log metric (`PushMaxLogLlrs`) so
the constellation geometry is by construction rather than per-mode shortcuts; σ̂² floored
at machine epsilon only (a genuinely clean probe SHOULD mint high confidence). QAM16 keeps
its live fixed 10.0 scale — its calibration belongs to B3.4 where the 5×-scale trap is
already documented.

Flat-channel branch note: the 3-pass averaged symbol reuses the same received samples, so
its noise is correlated across passes — no averaging bonus is claimed; the single-pass
probe MSE is the honest (conservative) σ̂².

Blast radius: the first Viterbi decode (via inter-frame weighting — this is the lever's
point), the shipped bits of reverted/skipped blocks, and llrstats. The turbo's own LLR
streams are chain-BCJR-calibrated already and untouched. On AWGN/static channels σ̂² is
frame-constant → a global rescale → the hard decode is bit-identical by Viterbi scale
invariance; the battery verifies this claim.

**Pre-registered acceptance:** on the WN7 corpse (`MS110D_AUTOPSY_WN=7 _SNR=19 _SEED=507
_ORACLE=1`), first-decode per-block errBits must fall, pulling worst starts toward the
measured ≤15% convergence basin; shipped (107,938 / 5c/6r baseline) must improve or hold
with more blocks converging. **The oracle number (237) must not move** — the oracle path
trains on truth and never reads first-pass LLRs; movement means an unintended leak.
Guards: WN6 corpse (`_WN=6 _SNR=14 _SEED=506`) holds 0 shipped / 0 oracle; WN13 specimen
w3/b5 holds 0/0.

### Lever 2 — iteration-0 training-row quality weighting

Iteration 0 currently trains weight-1.0 on every re-encoded hard label. With calibrated
first-pass LLRs (lever 1), the raw LLR stream carries a per-bit agreement signal against
the re-encoded label: where the channel observation disagrees with the decoded label (or
agrees only weakly), the row is down-weighted. Exact weighting form to be fixed after
lever 1's measurement (it needs the calibrated LLR distributions to justify the squash);
candidates: per-symbol min-bit-|LLR| squashed to [w_min, 1], or SISO posterior magnitudes
from the iteration-0 decode. The EM variance diagonal (`AddTrainingRow` overload from #81)
is the soft-iteration analogue already in place; this lever brings the same honesty to the
hard bootstrap.

**Pre-registered acceptance:** WN7 corpse shipped falls further / more blocks converge;
oracle 237 may move only if the lever is also applied to the oracle path (it must NOT be —
the oracle trains on truth; weighting truth rows by first-pass confidence would corrupt
the instrument). Same WN6/WN13 guards.

### Lever 3 — damped extrinsic feedback (only if wander persists)

Standard turbo-equalization practice: scale the extrinsics fed as chain-BCJR priors by a
damping factor γ < 1 (literature range 0.7–0.75) to break limit-cycle wander — the b4/b5/b10
plateau signature. Applied to the prior path only (`_softWireExt`), not to the detector
extrinsics returned to the SISO. Taken up only if levers 1–2 leave wandering blocks, and
measured at a single literature-standard γ before any sweep; a γ sweep on the corpse would
be tuning-to-the-corpse and is out of bounds without a disjoint confirmation burst.

**Pre-registered acceptance:** converts at least one of the wandering blocks to
convergence on the WN7 corpse without regressing converged blocks; WN6/WN13 guards hold.

## Instrument addition (measurement-only, lands with this note)

llrstats currently records `first` and `oracle` passes; the basin walk needs the missing
middle: a `final` pass — the LLR stream the turbo settled on (on reverted blocks this is
the last iterate's stream, which is exactly the wander-state view the mechanism analysis
needs). One event on the demod (`TurboBlockLlrs`), wired into the autopsy rig's existing
`WriteLlrStats`. No shipped-path effect.

## Sequencing and the exit

Levers land one at a time; after the basin levers, the **model floor** front (two-adjacent-
lag chains for the d/d+1 fractional sidelobes, pre-registered in §B2.2 and in the #81
"where WN7 stands" note) gets its own design note — it is NOT part of this one. The
FD-turbo architecture decision remains the pre-registered escalation if both fronts stall
at mask+2 dB. Full §5.3 mask runs (canonical + disjoint) for WN7 and WN6, the standard
battery, and Phase A regressions gate the merge, as always.

Files land here as the measurements run.

---

## Amendment 1 (same day, before lever 1 landed): σ̂ must be the PRE-solve probe residual

The design above specified the post-solve probe residual as the calibrator. Measured on
the WN7 corpse, that estimator is wrong, and the first lever-1 attempt regressed shipped
107,938 → 125,845 (4c/7r): the marginal block b9 (17.6% start, converged i15 at baseline)
tipped into wander. Oracle stayed exactly 238 (the leak guard held) and the raw uncoded
errors were bit-identical (positive scaling cannot flip signs) — the regression came
purely through the Viterbi's inter-frame reweighting, i.e. through the σ̂ model.

Mechanism: the post-solve residual is measured on the same probe rows the solve just
fitted at weight 6 — in-sample optimism — so σ̂ under-estimated the data-span noise and
the calibrated LLRs were over-confident. Measured against per-symbol truth
(`corpse/wn7-cal-*`): data-span mean |y−ref|² = 0.438, flat across the frame (no chord
shape); the PRE-solve probe MSE (incoming taps, out-of-sample like the span) averages
0.460 — ratio 0.95, per-frame correlation r = 0.58 at the correct span alignment (r
collapses to 0.10 two frames off, confirming the alignment). The pre-solve measurement is
both honest in scale and frame-tracking. Incidentally the burst-mean truth (≈ 0.44) is
close to the old fixed scale's assumed N0 = 0.5 — the fixed 8PSK scale was right ON
AVERAGE for this channel; the lever's value is the per-frame redistribution, not a global
confidence change.

Correction: σ̂ anchors switch to the pre-solve probe MSE (`preMse`, already computed),
interpolated head→tail across the span; the flat branch uses the existing `_probeMse`
EWMA of the same quantity. The post-solve machinery added for the first attempt is
removed. Acceptance criteria unchanged.

## Lever 1 measured (pre-solve σ̂) — premise validated, standalone acceptance FAILED

WN7 corpse shipped 125,478 (4c/7r) vs baseline 107,938 (5c/6r) — statistically the same
outcome as the biased first attempt (125,845): the entire regression is b9's marginal
convergence flipping, and a new rig instrument (per-block FIRST-DECODE errors, decoded
rig-side from the first-pass stream) shows it was not caused by a worse start — b9's
first decode IMPROVED 18,025 → 17,835. Its baseline i15 convergence was trajectory luck;
any perturbation of the iteration-0 labels reshuffles a wander that deep.

The premise held everywhere else: first decodes improve on 9/11 blocks, total 199,648 →
196,275, with the healthiest block improving 33% (b0 7,083 → 4,728). Right-bit confidence
went from the flat fixed-scale 1.91–2.00 to a block-quality-resolving 2.62–5.73 with 3–5×
right/wrong separation (`corpse/wn7-*-llrstats.csv`). Guards: WN6 0/0, WN13sp 0/0, oracle
exactly 238 in every run.

The load-bearing discovery is the basin's true scale: the deep blocks' first DECODES are
35–49% info errors, and the baseline turbo recovered b3 from a 46% start while 48–49%
starts wander — the boundary is razor-thin in label space, so no first-pass reweighting
can pull a 49% start under it. Lever 1 standalone therefore fails its shipped-improves
acceptance and is retained only as lever 2's enabler: the soft bootstrap does not need
better labels, it needs honest per-bit confidence, which is exactly what calibration
provides. Decision point: the composed 1+2 stack vs baseline on the same corpses.

## Lever 2 form A (soft iteration 0) — measured WORSE, rejected

Retiring the hybrid outright (TurboReequalizeSoft on iteration 0, E[x] rows from the
SISO on calibrated first-pass LLRs) regressed WN7 to 158,598 (2c/9r): b3 lost the 46%
recovery the hard bootstrap achieves, b8 (44%) lost too, and every wander level rose
(4–9k vs baseline's 1–5k). Guards were unaffected (WN6 and WN13sp all-blocks converged,
0/0 — their starts are ≤22% wrong). Mechanism: beyond the code's threshold the SISO
posterior is LESS calibrated than the channel LLRs it consumed — at 35–49%-wrong decodes
its E[x] is confidently wrong where it matters and shrinks excitation where it is merely
unsure; hard ±1 labels at least keep full excitation. `corpse/wn7-softboot-regressed.txt`.

## Lever 2 form B (agreement-weighted hard rows) — measured NO-OP, reverted

Iteration 0 kept hard re-encoded labels and full excitation, with each LS training row
weighted w_u = Π_b σ(±llr_b) — the probability the label is correct given the calibrated
first-pass channel LLRs alone (no knobs; oracle path weight-1, its labels true by
construction; scope: LS solve rows only, since the scalar h1/h2 correlations self-shrink
under wrong labels while wrong LS rows actively bias the solve). Measured: the weights
ARE active — iteration-0 TIR solves shift (b0: tir 46→49 frames, meanLag 5.6→6.1) — but
by iteration 1 the soft loop washes the perturbation out and every block lands in the
same attractor: identical 4c/7r split, shipped identical to the digit (125,478). Reverted
as unevidenced code.

**The basin verdict from levers 2A+2B together:** the converge-or-wander split is decided
by the soft-iteration attractor structure, not by iteration-0 estimation quality — a
worse start loses recoverable blocks (2A), a better-quality start changes nothing (2B).
The remaining pre-registered basin lever is therefore the one aimed at the attractors:
damped extrinsic priors (lever 3) — the `final`-pass llrstats measured the wander states
holding first-pass-grade error counts at 3–12× the first pass's wrong-bit confidence,
which is the prior loop self-reinforcing.

## Lever 3 (damped extrinsic priors, γ = 0.75) — trajectories shift, split unchanged

Damping at the single pre-registered literature value is measurably active — every
wandering block's decode-change trajectory differs (b4's cap state 4,290 → 5,594, b1
3,540 → 2,985, b9 819 → 883) — and converts nothing: the same six blocks wander, the
same five converge, shipped identical (125,478 with lever 1 in the tree). The attractors
survive γ = 0.75, and a γ sweep on the corpse is pre-registered out of bounds
(tuning-to-the-corpse). `corpse/wn7-damped-*`.

## Campaign verdict — a decisive negative result; no shipped-path code lands

All three pre-registered basin levers are measured. None moves the converge-or-wander
split: better starts don't (1: −1.7% first-decode errors, one marginal block flipped OUT
by chaos), worse starts lose recoverable blocks (2A), start-quality information doesn't
(2B), and attractor softening at the literature γ doesn't (3). Per discipline, none
ships: levers 1+3's implementation is preserved in
`lever1-calibration-lever3-damping.patch` for resurrection if later evidence (a stronger
per-iteration model changing the contraction margin) reopens the case.

What the campaign BOUGHT: the basin's mechanism is now pinned. The six deep WN7 blocks
start at 46–49%-wrong first decodes — at the outer code's breakdown — while their
oracles (b1:15, b6:6, b7:29, b10:6 of 49,152) prove the channel model supports
near-perfect decodes there. The gap is purely the soft loop's attractor structure, which
iteration-0 quality and prior damping demonstrably cannot reach. The remaining
pre-registered lever that plausibly widens the basin is a SHARPER PER-ITERATION MODEL —
the §B2.2 two-adjacent-lag echo model for the T/2 fractional sidelobes (the b4/b5 oracle
residual, 77% of the floor) — because more accurate chain-BCJR LLRs strengthen every
iteration's contraction, which is the quantity the wander blocks are short on (b9 sits
at ~800–900 decode-changes at the cap, the closest wander state to convergence). That
front opens next with its own pre-registered note (`../2026-07-25-phase-b33-twolag/`);
FD-turbo remains the escalation after it.
