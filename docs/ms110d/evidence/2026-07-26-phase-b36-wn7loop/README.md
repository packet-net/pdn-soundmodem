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

## M1 — measured (bars FAILED; the mechanism is the finding)

**M1a: no separation.** The registered primary statistic (final-iterate channel priced
on the preceding probes' rows) does NOT discriminate: wander end states price at
0.71–1.03 vs converged blocks at 0.95–1.84 — overlapping, if anything inverted; the
statistic is dominated by per-block channel state, not decode quality. Decode-changes at
the cap separate converged from wander trivially (convergence itself) and nothing finer.
A label-free selector for C1 does not exist in the registered statistics.

**M1b: 0 basin hits in 32 reverting-block trials — and the ensemble destroyed every
deep convergence too.** All 8 perturbed runs came back 1c/10r: only b0 (19.2% start)
survived; b1/b2/b3/b6/b8/b9 — including b2 from a 35.3% start — lost convergence in ALL
8 runs, every end state at the ~50% deep attractor. The perturbation was measurably not
the small trajectory nudge the registration intended: a 2% info-domain flip re-encodes
through the K7 code and interleaver into ~7–8% of wire labels changed UNIFORMLY across
frames — and that locates the real mechanism:

**The scaffold discovery.** Per-frame first-pass label-error distributions separate the
converge/wander split PERFECTLY on this corpse: every converged block has ≥35 of 64
frames under 20% label error; every reverting block has ≤33. Even b9 (48.9%, converges)
vs b7 (48.8%, wanders) dissolves — b9 has 36 scaffold frames, b7 has 33. Convergence is
not trajectory luck in label space; it rides a **healthy-frame scaffold**: frames with
nearly-clean labels anchor their per-frame solves, their chains emit correct LLRs, and
the code propagates correction into the smeared frames. The uniform perturbation
poisoned every scaffold frame's solve at once — hence uniform destruction; first-pass
errors are frame-concentrated (the B1 donut statistic), which is what leaves the
scaffold standing.

**C1 verdict: DEAD**, on both the registered bar (0/32) and the mechanism — outcomes
are set by start STRUCTURE, which restarts cannot manufacture; perturbation can only
destroy scaffold, never create it. No amendment can rescue restart-sampling; the axis
is wrong. (Observed across 88 perturbed block-runs + M0: zero convergences to a wrong
decode — "converged ⇒ correct" held in every observation.)

## C2a — measured (M2a bar FAILED as a proxy; the M2b diagnostic overturned the proxy)

**M2a (frozen pass alone): bar failed, 1/4.** Per-block info-error % of the label-free
re-detection (first pass → frozen): b0 19.2→11.0, b1 47.9→29.8, b2 35.3→31.2, b3
45.7→29.1, b4 49.1→41.1, b5 49.7→39.4, b6 47.7→30.5, b7 48.8→22.5, b8 45.4→34.9, b9
48.9→36.3, b10 49.5→44.9. A real, uniform, label-free detection gain — but only b7 of
the four reverting blocks came under the registered ≤30% basin-entry bar. Formal
outcome per the matrix: C2a RED.

**M2b (diagnostic, run after the formal RED, disclosed as such): the composed candidate
converts 3 of the 4 dead blocks.** Seeding the shipped loop with the frozen decodes:
**10c/1r** — b4 18,100 → 0, b5 18,330 → 3, b7 18,000 → 0; only b10 (44.9% frozen
start) still reverts. Corpse coded errors 72,666 → 18,239 (4.0×). This measurement
falsifies the M2a bar AS A PROXY: b4 (41.1%) and b5 (39.4%) converged from above the
30% line — the label-space basin boundary (46–49%, measured on first-pass-structured
errors) does not transfer to frozen-start error structure, exactly as the scaffold
mechanism predicts (frozen errors distribute differently across frames than DD-collapse
errors). The registered ≤30 GREEN bar for M2b is also not met (18,239 ≫ 30 — b10's
revert dominates), so C2a does not ship on this registration as written.

## Amendment 1 — the revert-path salvage rung (registered 2026-07-26, BEFORE the
held-out measurement)

Disclosure first: the original matrix says both-red → design-only verdict. Both
registered candidates ARE red as registered. But the M2b diagnostic showed the composed
structure converting previously-unconvertible blocks with revert protection intact, and
the M2a proxy failure has a measured mechanism (wrong basin coordinate). Per the B3.5b
precedent (a consequence clause may be overridden by mechanism evidence, in an
amendment, with everything disclosed), this amendment registers the shipped form and
judges it on evidence the diagnostic has NOT seen: a held-out census burst, the guard
set, and the full battery. No bar below is evaluated on w0/b0's already-measured
composed outcome; w0/b0's role becomes guard-pinning only.

**Shipped form (the salvage rung)**: 8PSK only. On revert-at-cap — and only there —
run the frozen probe pass, decode it, and re-enter the full soft loop (cap 24) seeded
with that decode. Fixed point ⇒ accept its decode (counted `v` = salvaged, plus
converged); no fixed point or mid-block abort ⇒ revert to the TRUE first pass exactly
as today. Converging blocks never touch the salvage path — bit-identical by
construction. The only 8PSK Poor point is WN7; 8PSK AWGN/static legs are zero-error,
zero-revert baselines that must stay byte-identical. The turbo summary line gains a
`/{v}v` field (one-time guard-format change, disclosed here).

**Bars (all registered before measurement):**

- **S1 (safety, held-out corpse w1/b0)**: baseline first, then salvaged, same tree.
  No block's output coded errors may increase vs baseline; every baseline-converged
  block's decode byte-identical. Fail ⇒ salvage does not ship; design-only verdict
  plus an instrument audit of the wrong convergence.
- **S2 (efficacy, held-out corpse w1/b0)**: total coded errors ≤ 0.75× baseline AND
  ≥1 reverting block converts to exact 0. Fail with S1 green ⇒ report; the ship
  decision falls to S4's census bars alone (a structure that sometimes helps and never
  hurts may ship on census evidence — decided by the bars below, not re-litigated).
- **S3 (guards)**: WN6 corpse and WN13sp byte-identical (they have zero reverts); the
  WN7 w0/b0 corpse numbers change BY DESIGN — pinned on the first salvaged run
  (expected block-for-block ≈ the M2b composed outcomes; a deviation is an instrument
  audit, not a shrug) and guard-exact thereafter; suite 697/0 (105 env-gated skips).
- **S4 (battery)**: FULL house three-lane battery, WN0 legs gated. WN7 Poor BER must
  strictly improve on BOTH families; every non-WN7 census byte-identical to the
  #90-era baseline; AWGN/static/Doppler legs unchanged. Any non-WN7 movement ⇒ stop,
  audit, no ship.

**Honest expectations, unchanged**: b10-class blocks (frozen start ≥ ~44%) stay
reverted; the oracle floor implies ~15-error-grade residual per burst even at full
conversion. The ship criterion is census improvement with zero collateral — the 1E-5
gate is measured afterward, not promised.

**Cost**: frozen pass + ≤24 soft iterations, reverting 8PSK blocks only; nil on
converged paths and every non-8PSK mode.

## Amendment 1 — measured (S1/S2/S3 corpse-level: ALL GREEN)

The salvage rung landed as registered (`TrySalvageRevert` + `TurboFrozenProbePass`,
8PSK-only, revert-path-only; `TurboSalvaged` counter, summary `/{v}v` field).

**Held-out corpse w1/b0** (baseline banked BEFORE implementation: 90,829 coded,
6c/5r, reverts {b2,b4,b7,b8,b10}, near-correct fixed points b3:6/b6:14):

- **S1 GREEN** — per-block decoded-stream comparison: every non-salvaged block
  byte-identical to baseline (all deltas exactly 0), zero regressions anywhere.
- **S2 GREEN** — total 90,829 → **54,616** (0.601× ≤ 0.75) with TWO exact-0
  conversions: b4 17,990 → 0, b10 18,223 → 0. turbo 8c/3r/2v; b2/b7/b8 stay
  reverted (first pass preserved).

**Guard pin (w0/b0)**: **18,239 coded / 10c/1r/3v** — the in-process salvage
reproduces the two-run M2b composed outcome block-for-block (b4→0, b5→3, b7→0, b10
reverts), which was the registered expectation. This is the new WN7 corpse guard spec;
oracle b5:15 unchanged. WN6 corpse 0 / 11c/0r/0v and WN13sp 0 / 11c/0r/0v —
numerically identical to their specs (the disclosed `/0v` field is the only text
change). Note w1/b0's b10 converts while w0/b0's does not — the frozen-start quality
threshold cuts per-burst, as expected.

S4 (full battery) follows below.

## S4 — the full battery (ALL GREEN; `battery/`)

32/32 legs rc=0, three-lane house form, WN0 legs gated for the first time (green at
the exact B3.5b digits: 0 errors bound 1.22E-6 canonical / 3 errors bound 2.92E-6
disjoint).

**WN7 Poor: canonical 1.73E-1 → 5.63E-2 (182,579/3,243,776), disjoint 1.95E-1 →
6.14E-2 (199,235/3,243,776)** — 3.1×/3.2×, turbo 78c/10r canonical. Per-burst
(census, seed → errors): three canonical bursts collapse to exact ZERO (1508:
54,505 → 0; 2000508: 71,944 → 0; 3001508: 54,513 → 0), the corpse burst 72,666 →
18,239, one burst untouched (2001508: 54,609 — no salvage landed); disjoint: 11508:
17,939 → 0, 2011508: 54,804 → 6, every other burst halves or better. **No burst
regressed in either family.**

**Zero collateral**: every non-WN7 census in both families byte-identical to the
B3.5b baseline (`battery/census-compare.txt`: non-wn7-diffs=0 across 72 files);
AWGN ×10, static, Doppler all green; gated legs reproduce their exact baseline
digits (WN5 23/0, WN6 35/39, WN2 30/29, WN13 0/0, WN1 0/0, WN3 0/0, WN4 0/3,
WN0 0/3).

## Disposition

The salvage rung ships under Amendment 1 (S1–S4 all green). WN7 stays OPEN
(measured, not gated) at 5.63E-2/6.14E-2 — ~3.7 decades above mask, exactly the
honest-expectations shape: the residual is now fully characterized as (a) the
b2/b7/b8/b10-class blocks whose frozen starts sit ≥ ~35% (scaffold-poor even for the
probe-anchored detector) and (b) the oracle-floor model tail (b5:15-class) under
whatever converges. The loop-structure question this leg was registered to answer is
answered: the wander attractor is escapable EXACTLY when a label-free start can get
inside the basin — the binding constraint has moved from the loop's structure to the
label-free detection quality on scaffold-poor blocks, which is C2b/FD-turbo's
question (per-bin exact channel application vs the single-lag chain constraint) and
gets its own registered design leg if opened. C1 (restarts) and C3 (schedules) are
closed with mechanism. The instrument lesson banked: basin coordinates are
frame-structural, not label-percentage — every future WN7 bar should be set in
scaffold terms.

## Guards and discipline

WN7 corpse 72,666 coded / 7c/4r + oracle b5:15; WN6 corpse 0 / 11c0r; WN13sp
(SEED=10513 w3 b5) 0 / 11c0r; suite 697/0 (105 env-gated skips). One lever per
measurement; §5.3 + disjoint seeds (MS110D_MASK_SEED_OFFSET=10000); FULL battery before
any demod merge (three-lane house form, WN0 legs now GATED); detached long runs
(setsid + Monitor); amendments pre-registered with bars and consequence clauses before
their measurements. Candidate new guard for the next demod PR (registered in B3.5b):
WN0 E1 corpse (SEED=500 w2 b97) 0 errors + two-peak profile.
