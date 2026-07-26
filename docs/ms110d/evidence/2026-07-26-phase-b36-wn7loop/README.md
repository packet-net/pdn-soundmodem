# Phase B3.6 — WN7 loop structure (design, pre-registered 2026-07-26)

Written BEFORE any implementation, per the Phase B discipline. This is the leg the §B3.3
closing notes kept pointing at: every start-side and pricing-side lever on the WN7 turbo
loop is measured dead, and "the loop structure itself (FD-turbo, damping schedules) is
what remains". This note registers what "loop structure" concretely means, the candidate
structures with their mechanisms, and the measurements — with bars and consequence
clauses — that decide which one (if any) gets built. The B3.5b lesson governs throughout:
bound the mechanism first, keep every consequence clause written before its measurement.

## The question

WN7 Poor (8PSK, +19 dB) ships at 1.73E-1 canonical / 1.95E-1 disjoint against a corpse
oracle floor of 15 coded errors (b5 only) — the channel model supports near-perfect
decodes, and the gap is entirely the soft loop's converge-or-wander split. Which change
to the loop's STRUCTURE (not its inputs, not its pricing) removes or escapes the wrong
attractors, and what does each candidate measure on the corpse before anything ships?

## The anatomy this note answers to (all banked)

1. **Mechanism** (`../2026-07-25-phase-b33-wn7-oracle/`): median |Δθ| ≈ 15–16° from
   MMSE noise-enhancement + residual ISI at two-path notches + intra-frame movement
   between 120 ms anchors; 8PSK's ±22.5° sector budget converts that spread to SER ≈ 0.39
   inside smeared frames. Genie-immune — estimation quality is not the residual.
2. **Model**: the oracle walk 238 → 209 (two-adjacent-lag) → **15** (eigen-TIR,
   `../2026-07-25-phase-b33-fadecross/`) collapsed the model floor to b5's last 15.
   Given labels, today's chain-BCJR model decodes the corpse essentially clean.
3. **Basin** (`../2026-07-25-phase-b33-basin/`): deep blocks' first DECODES are 35–49%
   info errors — at the outer code's breakdown. The boundary is razor-thin (b3's 46%
   recovered; 48–49% wander) and trajectory-chaotic (b9 flipped OUT of convergence by an
   IMPROVED start). Numbers are pre-TIR; M0 re-measures them on the current tree.
4. **Every non-structural lever is measured dead**: first-pass LLR calibration (basin
   lever 1, twice — second time on the eigen-TIR map, `../2026-07-25-phase-b33-basin2/`),
   soft iteration 0 (2A — measurably worse), agreement-weighted rows (2B — active but
   washed out by iteration 1), damped priors γ = 0.75 (3 — every trajectory shifts, the
   split doesn't), per-segment noise pricing (`../2026-07-25-phase-b33-segnoise/`; its
   χ²-honest §B4.1 descendant shipped for WN6 and left WN7's split untouched).
5. **The wander states are self-consistent wrong attractors**: label-trained refits hold
   first-pass-grade error counts at 3–12× the first pass's wrong-bit confidence. WN8's
   Amendment 2 (`../2026-07-25-phase-b34-wn8/`) is the harshest replication — a
   label-free-bootstrapped loop at 16QAM descending MONOTONICALLY to a 50.1% coin-flip
   fixed point at cap 96. Revert-at-cap is protective and stays.
6. **The loop today** (`Ms110dDemodulator.FinishBlock`, cap 24): iteration 0 trains on
   hard re-encoded labels, iterations 1+ on SISO soft expectations with extrinsic-only
   chain priors; no fixed point by the cap ⇒ revert to the first pass. Corpse guard:
   72,666 coded / 7c/4r, oracle b5:15 exact.

## What "loop structure" means — the two feedback paths

The loop closes through two distinct paths, and the anatomy indicts them differently:

- **P-chan (channel laundering)**: decode labels → per-frame batch-LS TIR solve +
  h1/h2 correlations → channel → chain LLRs → decode. This is the wrong attractor's
  self-consistency engine: a channel refit to wrong labels makes those labels look
  right, and it corrupts every in-sample statistic (basin lever 1 measured the
  post-solve residual's in-sample optimism directly). Only label-free evidence — the
  probes — survives it.
- **P-prior (extrinsic agreement)**: decode LLRs → SISO extrinsics → chain priors →
  LLRs. Extrinsic-only discipline already mitigates double-counting, but WN8 proved the
  SISO-chains agreement loop can still descend to a confident wrong fixed point carrying
  zero information. No structural change removes this path; only real detection
  information starves it.

A "loop structure" candidate is a change to which of these paths exist, when they run,
or how their end states are selected — as opposed to the dead lever class, which changed
what flowed along them.

## Candidates

### C2a — label-free-channel re-detection stage on the revert path (primary)

**Mechanism.** Give the revert path a second chance whose channel knowledge cannot have
been laundered: re-detect the block with the exact chain BCJR on a channel derived from
probes and first-pass state ONLY, then re-enter the ordinary label-trained loop seeded
with that decode. P-chan is severed during the stage (nothing label-trained touches the
channel); the label-trained finisher afterwards is harmless-by-construction if the stage
lands inside the basin — the echo chamber launders wrong labels, and the oracle floor
(15) already prices the finisher's endpoint given good labels.

**Construction** (registered; mechanics belong to the implementation commit):

- **Channel source, primary form (ii)**: a probe-only TIR shortening solve — the
  existing `SolveTrainingTir` machinery fed exclusively probe rows (the frame's bounding
  mini-probes), pair candidates off (`allowPair` stays trusted-labels-only), h1 anchors
  interpolated probe-to-probe as the first pass already does. Rank is the honest risk:
  ~2×K probe rows against the FF span, Tikhonov-regularized; WN8's Amendment 2 ran
  probe-row solves at K = 32 and the rank starvation was measurable. If the solve is
  rank-unusable at WN7's geometry, that is a finding, not a tuning invitation.
- **Channel source, control form (i)**: the first pass's own tap/h1 state replayed
  per frame (snapshot during the first pass, autopsy-gated). Mechanically cheap but
  regime-mismatched: the first-pass FF is a full-inversion DFE, so the post-FF stream
  has its echo smeared into the truncated train the chains' single-lag model cannot
  represent (§B3.3 twolag). Run as the control arm; reported, never the bar.
- **Detection**: chain BCJR as no-prior exact-MAP over the stage channel — echo carried
  in the chain state, head symbols probe-known, feedback history from first-pass DD
  decisions as lived. No SISO priors enter the stage (P-prior severed too): the stage's
  entire value is uncontaminated detection information.
- **Re-entry**: the stage's decode seeds the ordinary loop (iteration-0 labels), full
  soft iterations, revert protection retained with the ORIGINAL first pass as the final
  fallback. Mounted strictly on the revert path: blocks that converge today are
  bit-identical by construction.

**Measurement plan** (this IS the candidate on the corpse — no oracle assistance, so the
numbers are performance, not bounds):

- **M2a (stage alone)**: per-block info-error % of the stage decode. Bar: ≤30% (inside
  the measured basin with margin against its 46–49% boundary) on ≥3 of the 4 reverting
  blocks. Converging blocks recorded, not judged.
- **M2b (composed)**: stage decode → seeded loop → final. GREEN: total corpse coded
  errors ≤30 (≤2× the oracle floor) with the 7 converging blocks decode-exact and all
  guards exact. AMBER: 30–300 — report only, do not ship, fall through to C1. RED:
  >300, or M2a bar failed.

### C1 — perturbed restarts + convergence selection on the revert path (fallback)

**Mechanism.** The basin evidence says trajectories near the boundary are chaotic — b9
flipped out on an improved start, and "any perturbation of the iteration-0 labels
reshuffles a wander that deep". Chaos is a sampler: restart the loop from deterministic
perturbations of the iteration-0 labels and accept a converged end state. The wrong
attractors observed on WN7 are OSCILLATING (3–12k decode-changes at the cap, never fixed
points), so "converged" is itself a candidate selector — IF the ensemble shows
convergence implies correctness. The probes are the incorruptible judge if it does not.

**Measurement plan**:

- **M1a (instrument, shared)**: label-free end-state statistics per block at the cap —
  primary: the final iterate's channel priced on the frames' bounding probes (known
  symbols, the only rows P-chan cannot launder); secondary: decode-changes at the cap
  (already in FrameDiagnostics). Descriptive on the baseline corpse — no bar; it
  characterizes what a selector could see.
- **M1b (ensemble = the candidate in ensemble form)**: R = 8 corpse runs with
  iteration-0 labels perturbed at p = 2% (deterministic per-block LCG, seeds k = 1..8 —
  all registered here, no sweeps). Bars: **(b1) basin-hit** — ≥1 restart converges to a
  correct decode (≤1% info errors) on ≥2 of the 4 reverting blocks; **(b2) safety** —
  ZERO observed convergences to a wrong decode (>1% info errors) across the ensemble.
  If b1 passes and b2 fails, C1 is unsafe without a selector: an amendment must register
  a discriminator from M1a's statistics (with the numbers on the table, before any
  selection rule is scored) or C1 dies.

**Shipped form if green**: on revert-at-cap, up to 4 perturbed restarts (deterministic
seeds from the block index), accept the first converged decode; no convergence ⇒ revert
as today. Revert-path-only: converging blocks bit-identical by construction.

### C2b — frequency-domain turbo equalization (standing escalation; design sketch only)

The generalization of C2a-(ii): per-frame overlap-save FD processing on the T/2 grid,
per-bin MMSE with soft-interference cancellation, priors entering as reliability (v̄)
rather than values — the Tüchler/Falconer structure whose convergence EXIT analysis
governs. Its value over C2a is exact channel application (no single-lag chain
constraint, no FIR truncation of the inverse, no significance floor); its risks are the
LTI-per-window assumption against WN7's intra-frame movement (windows would have to be
≤ the existing 27 ms segment granularity) and the same label-free channel source C2a
needs. **Decision inputs registered**: if C2a-(ii)'s probe-only solve is rank-unusable,
FD-turbo's honest (label-free-channel) form dies with it; if C2a converts the basin but
its residual concentrates where the single-lag model fails, FD-turbo is the escalation
and gets its own design leg. NOT buildable this window — no code on this candidate now.

### C3 — iteration-order / damping-schedule variants (conditionally dead)

Flat γ = 0.75 is measured dead (trajectories shift, split unchanged) and schedules add
knobs to a lever class that failed at its principled value. Registered dead unless BOTH
C2a and C1 die AND M1b shows the correct basin has substantial mass under perturbation
(evidence an annealed schedule could reach it deterministically) — in which case a new
note registers the schedule from literature values only.

## Measurement order and decision matrix

**Order: M0 → M1a → C2a arm (M2a/M2b) → C1 arm (M1b).** M1b runs regardless of C2a's
outcome (its basin-robustness data is part of the record either way), but C1 only BUILDS
if C2a fails to go green.

- **M0 (re-validation + fresh anatomy)**: corpse normal + oracle on this branch. Bar:
  guard-exact (72,666 coded / 7c/4r; oracle b5:15). Extract per-block first-decode
  info-error %, the current revert set, decode-change trajectories — the basin numbers
  in this note are pre-TIR and M0 replaces them. Bar failure ⇒ stop, audit the
  instrument before reading anything (the B3.5b clause, verbatim).
- **C2a green** ⇒ ship-candidate; FULL battery (both families, WN2/WN5/WN6 6M, WN0 legs
  gated); §5.3 decides any gate claim.
- **C2a amber/red, C1 green** ⇒ C1 ships on the same battery terms.
- **Both red** ⇒ design-only verdict, written into this README and phase-b-plan: WN7
  stays OPEN with the loop-structure class measured; the remaining registered directions
  are FD-turbo-with-soft-channel-estimation (a reshaped echo chamber — requires its own
  EXIT-style convergence design before any code) or documenting WN7 above mask in the
  App D operating guidance (the waveform ladder steps down through gated modes).

**Instrument seams** (all autopsy-gated, bit-identical when unset, guards exact before
any measurement is read):

- `TurboStartOverride` — internal per-block hook replacing the iteration-0 labels
  (serves M1b's perturbations and M2b's seeding).
- `MS110D_AUTOPSY_TURBO_FROZEN=probe|firstpass` — the C2a stage as an extra
  autopsy-only pass (form (ii) / form (i)), reported per block alongside the oracle.
- M1a's probe-span residual + cap statistics on the existing turbo-frame diag line.

## Honest expectations vs the mask

The oracle floor is 15 coded errors on ONE burst (~3.7E-5 burst-grade), and the census
says every WN7 burst is equally dead at first pass — so even a full basin conversion may
land WN7 at 1E-5..1E-4, above the gate, with the residual owned by the b5-class model
tail. The leg's success bar is therefore STRUCTURAL: deep-block conversion on the corpse
and a census collapse by orders of magnitude, with §5.3 both-family numbers judging any
gate claim afterward. No gate is promised here, and a converted-but-above-mask WN7 is a
successful leg with a new, smaller, different problem.

## M0 — measured (2026-07-26, this branch, `m0/` in the job tmp; bar GREEN)

Guard-exact: 72,666 coded / 7c/4r, oracle b5:15, first 147456 / last 405468, uncoded
89,559/540,672 (16.6%), collapses 14. The fresh anatomy replaces this note's pre-TIR
basin numbers:

| block | first-decode errors (of 36,864) | % | outcome | end state |
|---|---|---|---|---|
| b0 | 7,083 | 19.2% | converged | 0 |
| b1 | 17,673 | 47.9% | converged | 0 |
| b2 | 13,005 | 35.3% | converged | 0 |
| b3 | 16,846 | 45.7% | converged | 0 |
| b4 | 18,100 | 49.1% | REVERT | wander 18,144 |
| b5 | 18,330 | 49.7% | REVERT | wander 17,775 |
| b6 | 17,599 | 47.7% | converged | 0 |
| b7 | 18,000 | 48.8% | REVERT | wander 12,075 |
| b8 | 16,736 | 45.4% | converged | 0 |
| b9 | 18,025 | 48.9% | converged | 0 |
| b10 | 18,251 | 49.5% | REVERT | wander 18,088 |

Three findings sharpen the candidates. **(1) The basin boundary is NOT a threshold in
start-error space**: b9 converges from 48.9% while b7 wanders from 48.8% — outcome is
trajectory-dependent at equal start quality, which is exactly C1's chaos-as-sampler
premise. **(2) Converged ⇒ correct holds 7/7 at exact zero** on the current tree —
the oscillating-wander picture stands (end states 12,075–18,144, first-pass grade;
none reached a wrong fixed point). **(3) The eigen-TIR loop converges routinely from
46–49% starts** (b1, b3, b6, b8, b9) — the basin is wider than the pre-TIR record
suggested; the four losses are trajectory luck at the same depth, not a deeper class.

## Guards and discipline

WN7 corpse 72,666 coded / 7c/4r + oracle b5:15; WN6 corpse 0 / 11c0r; WN13sp
(SEED=10513 w3 b5) 0 / 11c0r; suite 697/0 (105 env-gated skips). One lever per
measurement; §5.3 + disjoint seeds (MS110D_MASK_SEED_OFFSET=10000); FULL battery before
any demod merge (three-lane house form, WN0 legs now GATED); detached long runs
(setsid + Monitor); amendments pre-registered with bars and consequence clauses before
their measurements. Candidate new guard for the next demod PR (registered in B3.5b):
WN0 E1 corpse (SEED=500 w2 b97) 0 errors + two-peak profile.
