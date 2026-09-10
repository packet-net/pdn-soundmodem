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

---

## Measured (same day) + Amendment 1: the corpse bar fails, the oracle floor falls 5× — decision moves to pre-registered battery scale

`corpse/wn7-segnv.txt`: shipped **72,666 — identical to the digit, 7c/4r, same four
wanderers** (no conversion: criterion 1's bar is failed) — but **oracle 15 → 3** (the
corpse's oracle now sits below the 1E-5 line), b7's wander amplitude falls 3×
(1309 → 414 decode-changes at cap), the late convergers converge faster (b1 i14 → i12,
b6 i14 → i13), and nothing regresses anywhere (b4 3472 → 4417 and b10 4755 → 5358
amplitude jitter inside non-converging loops; first decodes bit-identical as designed).

Amendment rather than a silent revert OR a silent ship: the corpse samples only four
wandering blocks in one burst, and the #83 precedent measured exactly this situation —
per-iteration sharpening that flipped 2 corpse blocks flipped ~13 bursts at battery
scale, where the marginal population actually lives. The decision therefore moves to a
PRE-REGISTERED battery-scale measurement, stated before running it:

- **Legs**: WN7 3M canonical and WN6 6M canonical (the flip-sensitive leg), full §5.3.
- **Ship bar**: WN7 shipped BER improves vs #83's 1.73E-1 AND WN6 holds its at-mask
  state (≤ 1E-5 class, no marginal-block catastrophes). Then the FULL battery gates the
  merge as always.
- **Else**: revert the lever, record the negative, fall through to B3.4. The oracle
  15 → 3 cut alone does NOT ship the lever — the shipped path is what the phase gates
  on, and a model-floor gain that never reaches a decode is banked knowledge, not a
  ship.

## Amendment 2 (same day): the decision legs measured a different ship case — WN6
## margin lever, WN7-neutral; the full battery decides with improve-or-hold bars

Decision legs (`corpse/decide-wn7.txt`, `corpse/decide-wn6.txt`): WN7 3M canonical
**1.73E-1, 57c/31r — statistically identical to #83** (562,621 vs 562,609 errors on
IDENTICAL channel/noise realizations — canonical seeds pair the comparison; the basin
hypothesis is dead at battery scale too, and Amendment 1's bar fails as written). But
**WN6 6M canonical 57 → 35 errors (8.79E-6 → 5.40E-6, 264c/0r)** — paired evidence on
identical realizations of a genuine 39% residue cut on a GATED waveform, nearly
doubling the at-mask margin (1.14× → 1.85×). Mechanism-consistent: WN6's residue lives
in CONVERGED blocks — the near-perfect-label regime where the corpse measured the
oracle floor falling 5× — honest within-frame pricing sharpens exactly those final
LLRs.

Reverting a measured 39% margin gain because the bar was registered against a
different hypothesis (WN7 conversion) would be rigor pointed the wrong way; shipping
it silently would be goalpost-moving. The resolution, stated BEFORE the deciding runs:
the lever's ship case is reformulated as **WN6 margin lever with WN7-neutral
behavior**, and the standard FULL battery decides with explicit improve-or-hold bars
on every gated leg (the two completed decision legs stand as their battery slots —
same build, same seeds):

- WN6 6M disjoint: ≤ 57 errors (improve-or-hold vs #83).
- WN7 3M disjoint: 1.90E-1 class ±5% (neutral bar; canonical already measured
  identical).
- WN13 canonical + disjoint 6M: 0-error class holds (≤ 2).
- WN2 ×2, WN1 ×2, smokes, AWGN, static, Doppler: hold their #83 states.
- Any marginal-block catastrophe (the #82 w3/b3 class) anywhere: revert.

Unit suite already green (696/0); guards WN6/WN13sp corpses 0/0 (`corpse/`).

## Amendment 3 (same day): WN2 canonical FAILS its hold bar — raw pricing regresses
## flat-floor frames; the heteroscedasticity gate (derived 2× threshold)

The battery caught it (`corpse/wn2-regressed.txt`): WN2 3M canonical 15 → 32 errors on
paired seeds (1.05E-5 direct — above the line). Census: all four #83 clusters persist
IDENTICALLY and five NEW thin clusters (2–6 errors, firstErr ≈ lastErr, every burst
8c/0r) appear — the design note's pre-registered lucky-window risk, measured. Mechanism:
WN2 at +5 dB is AWGN-dominated — the true within-frame floor is FLAT, so the 4×
~64-symbol segment estimates carry χ² jitter (σ ≈ 12% relative at ~128 dof) with no real
structure to track, and an occasionally-low segment floor inflates wrong-sign LLR
magnitudes in converged blocks. The contrast with WN6 (both families improved 57 → 35 /
57 → 39) isolates the regime split exactly: pricing helps where heteroscedasticity is
REAL (fade-crossing frames, |h1| swinging 2–3×, residual ratios ≫ 2) and hurts where the
floor is flat.

The amended form — a DERIVED gate, not a tuned knob (same construction style as the
0.04·|h1|² echo floor and the 4·ln(L) margins): per-segment pricing engages only when
max(segNv)/min(segNv) > 2. The noise-only spread of 4 such estimates sits at ~1.3–1.4
at the 99th percentile (χ² with ~128 dof per segment), so 2× carries the usual safety
factor; the measured fade-frame swings sit well above it. Flat-floor frames take the
frame-constant path BIT-IDENTICALLY to #83.

Pre-registered acceptance for the amended form (before any re-measurement): on paired
seeds, WN2 ×2 must show NO new error bursts beyond their #83 sets; WN6 6M both families
must RETAIN the improvement (≤ 45 errors each — if the gate excludes the fade frames the
ship case evaporates and the lever reverts); WN7 corpse oracle ≤ 15; guards 0/0; the
full battery re-runs from scratch on the gated build and every remaining leg holds its
#83 state.

## Amendment 4 (same day, FINAL): the gated form still kills WN2's disjoint family —
## REVERTED; the lever closes as a banked negative with real knowledge

Gated build measurements (`corpse/gated-*.txt`, battery `gated-battery.log`): the gate
kept its promises where promised — WN7 neutral both families (1.73E-1 57c/31r /
1.90E-1 54c/34r), WN6 retained the improvement both families (**41 / 39 vs #83's
57 / 57**, 264c/0r), WN13 perfect both families, WN7 corpse oracle stayed 3 with the
gate engaging on 108/704 frames, guards 0/0. WN2 canonical passed its mask at 17 errors
with ONE new 2-error cluster (w3/b8) that a dedicated corpse adjudicated as genuine
heteroscedastic pricing (37% of the burst's frames gate-engaged — at +5 dB the Poor
channel's swings often exceed 2× within frames). But **WN2 disjoint failed its mask
outright: 12 → 46 errors, 1.51E-5 direct** — the three #83 clusters persist identically
and FOUR new clusters appear (10/4/7/13 errors). The gate is not a sufficient guard:
WN2 is the MARGINAL waveform (+5 dB BPSK), and the LLR sensitivity to floor-estimate
error scales with marginality — a ±12% χ² floor error that saturated waveforms shrug
off moves WN2's decision boundaries even in genuinely-heteroscedastic frames. Raising
the gate threshold on battery feedback would be knob-tuning; the pre-registered
consequence applies: **REVERTED** (commits cb5c032 + 3fc5153 reverted in-branch; tree
verified bit-identical to main; corpse baseline reproduced 72,666 / 7c/4r / oracle 15).

Banked knowledge (patches `segnoise-pricing.patch` raw, `segnoise-gated.patch` gated):
within-frame noise-floor honesty is worth ~30% of WN6's residue (41/39 vs 57/57, paired
seeds, both families, reproducible) and 5× on the WN7 oracle floor (15 → 3) — the
near-perfect-label regime genuinely benefits. What a shippable form needs is a floor
ESTIMATOR whose error doesn't scale into marginal waveforms' decision boundaries:
candidates for a future pre-registration are cross-frame smoothing of segment floors
(the fade process is ~1 Hz bandlimited; 33 Hz anchor-rate smoothing could cut estimator
variance ~8×) or coupling the floor to the |h1|-trajectory model rather than windowed
residuals. Not knob-tuning the gate.
