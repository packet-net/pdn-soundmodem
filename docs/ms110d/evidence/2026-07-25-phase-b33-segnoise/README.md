# Phase B3.3 — per-segment BCJR noise pricing (design, pre-registered 2026-07-25)

Written BEFORE the implementation, per the Phase B discipline. The fall-through lever
from the basin re-measurement (`../2026-07-25-phase-b33-basin2/`): start-side
calibration is dead under this loop structure (twice measured, different victims each
time), so per-iteration LLR sharpening must come from the TURBO side, where it cannot
perturb first-pass trajectories. This lever is the first of the two secondary
structures registered in the fadecross note (`../2026-07-25-phase-b33-fadecross/`).

## Mechanism

TurboCore prices the chain BCJR with ONE noise floor per frame (noiseVar =
0.5·residual/U). Within weak-cursor and fade-crossing frames the true residual is
heteroscedastic: |h1| swings 2–3× across a frame (measured in the fadecross corpse),
the model error tracks the local mismatch, and the EM variance-bump terms are
per-position. A frame-constant floor therefore OVER-confidences the locally-bad spans
(LLR magnitudes too large exactly where the model is worst) and under-confidences the
good spans. The chains propagate the overconfident wrong evidence; the outer-code SISO
receives mis-scaled inputs — and the #83 measurement (two blocks flipped to convergence
by nothing but sharper per-iteration LLRs) says the wander boundary is sensitive to
precisely this scale honesty.

Note the contrast with the dead lever 1: same calibration physics, but applied INSIDE
the turbo iterations (label-trusted, model-priced) rather than to the first pass —
no first-decode trajectory is touched, so the chaos channel that killed lever 1 twice
does not exist here. The first pass is bit-identical by construction.

## Design

1. In TurboCore's assembly loop, accumulate the residual (including the EM
   variance-bump terms, which are already per-position) into `Segments` (= 4) buckets
   on the SAME u/segLen partition as the segH1/segH2 estimation windows, with counts.
2. Price each symbol with the piecewise-linear interpolation of the per-segment floors
   through the segment centres — the identical ia/ib/t scheme the channel spans already
   use — with today's 1e-6 floor per segment. No clamp toward the frame floor and no
   other knobs: the raw form is the pre-registered lever; if the corpse shows
   lucky-window overconfidence damage, that is a finding to record, not a dial to turn.
3. `Ms110dChainBcjr.Equalize` keeps its scalar `noiseVar` parameter (tests unchanged)
   and gains an optional `ReadOnlySpan<float> noiseVarPerSymbol` that overrides it per
   position when non-empty; internally a pooled inv-2σ² array is built once either way,
   so the inner loops stay uniform.
4. The `turbo-frame` instrument line gains the per-segment floors (`nseg=`) so the
   corpse can measure the within-frame spread directly.

## Pre-registered acceptance

1. **WN7 corpse w0/b0** (baseline on main 2badde9: shipped 72,666 / 7c/4r, wanderers
   b4:3472 b5:3953 b7:1309 b10:4755 decode-changes at cap; oracle 15, all b5): at least
   one wanderer converts with NO lost convergences among the seven; oracle ≤ 15;
   shipped improves. Secondary readout either way: the four wanderers' decode-changes
   trajectories (does per-iteration amplitude fall even without conversion?) and the
   measured within-frame floor spread from the new instrument.
2. **Guards**: WN6 corpse and WN13 specimen hold 0 shipped / 0 oracle
   (outcome-identical; bit-exactness is not expected — LLR scales change on every
   TIR-priced frame).
3. **Battery gates any ship**: full standard set; WN6 6M both families hold at-mask.
4. If no conversion or any loss: record the negative, revert, and the roadmap falls
   through to B3.4 (the within-frame tail ramp stays registered for a timing-model leg;
   FD-turbo remains the standing escalation).

Files land here as the measurements run.
