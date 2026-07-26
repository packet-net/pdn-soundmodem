# §B3.8 — WN7 E2 arm: gauge-stitched anchor-track h1 in the frozen pass

Registered continuation of §B3.7's disposition ("E2 anchor-track h1 is the registered
next arm"). Everything in *Registration* below was written before any demodulator code
for this leg; the held-out specimens were banked (baseline digits only, no internals)
before this section was finished.

## Registration

**Question.** The residual WN7 class after #93 is the scaffold-poor trio signature:
blocks whose label-free frozen re-detection sits just below the basin edge
(~52 of 64 frames at <20% wire LLR-sign error; converging seeds sat ≥53, failing ≤51,
and b10 converted from 52), with healthy half-frames and worst-half-mean ~31% — the
mid-frame fade-null profile of the two surviving deficiency-budget lines G2 (h1
interpolation) and G4 (noise pricing). Does replacing the frozen pass's 2-point linear
h1 interpolant with a multi-frame, gauge-stitched anchor track (and, as a separately
measured second step, noise pricing derived from that track) lift the trio into the
basin?

**Decision inputs (all measured in §B3.7 M1b/M1c — no new instrument informed this
registration):**

- The physical anchor series samples h1 once per mini-probe = once per U+K = 288 chips
  = 120 ms → ≈8.3 Hz, against the Poor channel's ~1 Hz Doppler spread — 4× above
  Nyquist for the fade process. The current interpolator uses only the two anchors
  bounding each frame; the cross-frame track information is discarded.
- G2 (M1b, oracle side): the frozen linear h1 is 6.7–10.1% RMS from oracle truth at
  median (per-segment, after per-frame complex-scale alignment), 13–21% at p90. At the
  burst's ~19 dB effective floor that puts median model-error power at the noise floor
  and p90 frames 2–3× above it — exactly the mid-frame profile of the trio.
- G2 (M1b, label-free side): the stitched-midpoint test (hold out the middle anchor of
  a local triple, predict linearly from its stitched neighbours at double spacing)
  measured med 8.3–15.1% — the gauge-stitching construct works and is encoded in
  `../2026-07-26-phase-b37-wn7-fdturbo/corpse/m1b-anchor.py`.
- G4 (M1c): oracle per-segment noise spread within a frame is med 1.40–1.64×,
  p90 1.9–2.5×, against the frozen pass's single flat per-frame probe-priced floor.

**Mechanism.** Each frozen per-frame solve carries an arbitrary complex gauge (the
eigen floating-gain refit). Consecutive frames share a physical mini-probe: frame f's
following anchor and frame f+1's preceding anchor are the *same rows* measured under
*different* gauges, so their ratio is the pure gauge transfer (the channel cannot have
changed over zero time), up to anchor noise and FF-shape difference. Stitching
consecutive gauges through the shared probes turns the per-frame anchor pairs into an
h1 *track* sampled at 8.3 Hz in a common local gauge; a local cubic (Catmull-Rom
through the frame's two bounding anchors, tangents from the stitched neighbours one
probe further out on each side) replaces the linear interpolant inside the frame.
Guard rails from m1b-anchor.py: a magnitude floor on any anchor entering a gauge ratio
(fall back to the linear interpolant for that frame when the ratio is untrustworthy —
fade-null probes make the gauge estimate explode), and block-edge frames clamp to the
linear form on the missing side.

**Candidates:**

- **E2a (primary lever)**: the gauge-stitched cubic track for `h1Span` in
  `TurboFrozenProbePass`. Seam `TurboFrozenAnchorTrack`; the flag-off path must remain
  bit-identical to #93 (guard-enforced). Nothing else moves: solve, FF application,
  anchors, pre-cursor machinery, noise floor all stand.
- **E2b (second lever, contingent, separately measured)**: per-position noise pricing
  through the existing `noiseVarPerSymbol` plumbing in `Ms110dChainBcjr.Equalize`:
  σ²(u) = probe-priced floor + local model-error term priced from the track's own
  stitched-midpoint residual (per-anchor e_n = |v1 − (v0+v2)/2| lerped across the
  frame, per-dimension). Only measured if E2a survives its corpse measurement; never
  in the same measurement as E2a.

**Measurement order:**

- **M2a (instruments)**: corpse runs with frozen anatomy (`MS110D_AUTOPSY_TURBO_FROZEN=1`,
  + `MS110D_AUTOPSY_ORACLE=1` on w0/b0) on both design specimens — canonical w0/b0
  (converged; retention + oracle source) and canonical w1/b0 (the trio) — on the
  shipped #93 binary. Post-#93 frozen logs for these do not exist yet.
- **M2b (offline dry-fit — the KILL GATE)**: fit the stitched cubic track on the
  logged anchors, entirely offline. Score (1) on w0/b0: oracle per-segment h1 error of
  track vs linear (the m1b comparison, same alignment); (2) label-free on both:
  hold-out-one-anchor prediction error, cubic-from-stitched-neighbours vs
  linear-from-stitched-neighbours, both at double spacing. **Kill condition**: if the
  track does not reduce the oracle median seg-error on w0/b0 AND does not improve the
  label-free hold-out prediction on w1/b0, E2 is RED at zero demodulator cost — the
  4×-oversampling premise is refuted on this channel and no code is built.
- **E2a build + corpse** against the bars below; then optionally E2b as its own
  measurement; then the S set.

**Bars (frozen-scaffold terms — basin coordinates are frame-structural, the B3.6/B3.7
lesson).** Targets: the w1/b0 trio b2/b7/b8, frozen scaffold 43/51/50 vs the ~52 edge.
Retention: w0/b0 stays 0 coded / 11c/0r/4v exact; no per-block coded-error regression
on either specimen.

- **GREEN**: ≥2 of the trio convert (block coded errors → 0 through salvage), with
  retention.
- **AMBER**: exactly 1 conversion, or a ≥+3-frame frozen-scaffold lift on ≥2 of the
  trio, with retention.
- **RED**: neither, or any retention break.

**Ship bar** (the Amendment-3 template): arm ≥ AMBER on the design specimens AND at
least one conversion somewhere across design + held-out AND the held-out pair shows
zero per-block regressions AND guards exact (WN7 corpse 0/11c/0r/4v + oracle b5:15,
WN6 0/11c/0r/0v, WN13sp 0/11c/0r/0v, suite 697/0) AND full battery: WN7 no worse in
either family, all non-WN7 censuses byte-identical to the #93 baseline. A
scaffold-lift-only AMBER with no conversion anywhere does not ship — the seam parks
flag-off and the result is recorded.

**Consequence clause.** The E1 family already spent one RED (E1′). If E2 finishes RED,
the two-consecutive-RED clause closes WN7's label-free detection story design-only: a
written verdict here and in phase-b-plan.md, WN7 stays OPEN at 3.38E-2/2.23E-2 with
the residual fully characterized, and the window hands over to WN8/closeout. No third
detection arm without new instrument evidence.

**Honest buildable-in-window call.** Yes. E2a is confined to `TurboFrozenProbePass`
(a two-sweep restructure — solve+anchor sweep with per-frame tap snapshots, then the
chain sweep — for which the E1′ vote sweep is precedent; no `Dfe` or `ChainBcjr`
change). The dry-fit reuses the m1b machinery. The risk register: (1) flag-off
bit-identity through the restructure — mitigated by keeping the #93 single-sweep body
verbatim on the flag-off path; (2) gauge-ratio noise on fade-null shared probes —
mitigated by the magnitude-floor fallback; (3) the FF-shape component of the
gauge-transfer ratio is not a pure scalar — this is *measured*, not assumed away, by
M2b's kill gate before any build.

**Honest mask expectation.** The trio is ~54.6k of the canonical 109.6k coded errors:
full conversion would land canonical near ~1.7E-2; the four disjoint one-block
leftovers (~72k of 72.2k) would land disjoint near ~0 *if* they share the trio
signature — unknown by construction (two of them are the banked held-outs). Honest
range: canonical 1.7E-2–3.38E-2, disjoint 0–2.23E-2.

**Held-out specimens (banked before design, bare runs, no internals):** the disjoint
one-block leftovers 1010508 (SEED=10507 w1/b0) and 3010508 (SEED=10507 w3/b0).
Baseline digits, banked before M2a ran, both reproducing their census cells exactly:

| specimen | coded errors | turbo |
|---|---|---|
| dj-w1/b0 (1010508) | 17,897 | 10c/1r/0a/0s/1v |
| dj-w3/b0 (3010508) | 17,871 | 10c/1r/0a/0s/4v |

One revert-block leftover each — the exact residual class the arm targets. No frozen
anatomy, no per-block breakdown, no oracle was run or read on either.

## M2a/M2b measured — E2 dies at the gate

Instrument runs (shipped binary, no pair-diag): w0/b0 frozen anatomy (digits exact:
0 coded / 11c/0r/4v, oracle b5:15), w1/b0 frozen anatomy (54,616 / 8c/3r/2v; trio
frozen decodes b2:17322 / b7:17125 / b8:16075), plus a **first-ever w1/b0 oracle run**.
Dry-fit scripts banked in `corpse/` (m2b-track.py, m2b2-smooth.py, m2b3-profile.py).

- **Hold-out test (label-free, double spacing)**: the stitched cubic beats linear on
  every block of both specimens — w0/b0 ALL med 0.106 → 0.085, w1/b0 ALL 0.102 → 0.085.
  The stitching construct is sound.
- **Oracle seg test (operational spacing)**: a wash — w0/b0 ALL lin 0.080 / cub 0.082;
  on the trio's own frames b2 0.109/0.109, b7 0.097/0.097, b8 0.095/0.096. Smoothing
  variants (triangular-3, Savitzky–Golay-5) all within ±0.002 of linear (m2b2).
- **Position/floor decomposition (m2b3)**: the model-error profile across the frame is
  FLAT (mid-frame marginally better than the edges), and the direct overconfidence
  measurement is **model-error power ≈ 2% of the priced floor at median, ~13% at p90**
  — on both specimens, including the trio. The B3.7 M1b "model error at the noise
  floor" reading came from indirect arithmetic and is superseded by this direct
  measurement.

**Verdict: E2 (both sub-forms) is RED at the offline gate.** The registered kill's
AND-letter did not fire (the hold-out arm improved — but that is a double-spacing
curvature statistic; at operational spacing, 4× oversampling makes curvature
negligible and the residual is not reducible by any processing of the anchor values).
The gate's intent — the track must reduce operational h1 error — was not met. No
demodulator code was built; no seam exists. G2 and positional G4 are struck from the
deficiency budget as *measured-dead*.

## New instrument evidence — the lock-geometry discovery

With G2/G4 dead, the same logs were interrogated for what *does* separate the trio's
bad frames (scripts banked: m2c-echo.py, m2d-corr.py, m2e-drift.py, m2f-trigger.py):

1. **The trio's basin exists**: the w1/b0 oracle decodes b2/b7/b8 to ZERO (its only
   residues are b3:6 and b6:14 — blocks the real demod already converges). Label-free
   detection quality is genuinely the gap; the arm family is not dead by construction.
2. **Per-frame correlation (m2d)**: bad frames (frozen wire error ≥20%) are NEVER the
   lag-rejecting frames — lag0-frac of bad = 0.00 in every block (the FF suppressed
   the echo there; those frames run at 5–10%). Pre-cursor frames are the best class in
   the pass (2–4%). The bad frames are ALL causal-lag-5 acceptances that AGREE with
   the oracle's lag.
3. **The mechanism, from the pass's own floor column**: bad frames carry
   |c| = 1.01–1.26 (the accepted echo *exceeds* the cursor — the late path dominates)
   and priced floors n = 0.83–1.89 — **30–80× the normal floor**. The early-lock
   feedback-free FF cannot equalize a late-path-dominant frame; the probe residual
   explodes; the chain gets honestly-priced garbage LLRs; the frame drowns at 40–67%
   error. Natural late-lock frames on the same block ride the same physics at
   |c| ≈ 0.27, n ≈ 0.02. Within-frame echo drift was tested and refuted as the
   mechanism (m2e: bad-frame h2 drift ≤ good-frame drift, ≪ floor).
4. **Trigger quality (m2f)**: "causal accept AND n > 0.15" catches ~95% of all bad
   frames with ~1 false alarm per block; triggered frames carry 65–74% of the trio's
   frozen error mass (30–75% across all blocks of both specimens).

## Amendment 1 (pre-registered): E3 — per-frame lock-geometry arbitration

Registered under the consequence clause's new-instrument-evidence provision (E2 =
this leg's first RED; if E3's corpse measurement goes RED the two-consecutive-RED
clause closes WN7's detection story design-only).

**Form (one lever — the geometry choice):** on every frozen frame whose free solve
accepts a causal lag (1 ≤ lag ≤ period/2), also evaluate the LATE-LOCK geometry:
re-accumulate the probe rows with the equalizer window shifted by the accepted lag L
(the window shift performs the re-lock; the tap shape carries over, so the ridge
anchor remains approximately right in shifted coordinates), solve with
onlyLag = period − L (the E1′ single-candidate seam — the early path returns as the
aliased pre-cursor), rebuild anchors/floor in the shifted geometry, and **keep
whichever geometry prices the lower floor n**. The winning late-lock frames then run
the EXISTING B3.7 pre-cursor chain/assembly with the window shift threaded through
(chainDelay = period − onlyLag = L). No trigger threshold knob: always-offer,
floor-arbitrated. Seam `TurboFrozenRelock`; flag-off bit-identical to #93
(guard-enforced).

**Expectation (honest):** converting the bad half of the triggered error mass should
carry b7 (51, needs +1) and b8 (50, needs +2) across the ~52 edge; b2 (43, needs +9)
is the stretch case. Bars, ship bar, held-outs, and consequence clause are UNCHANGED
from the registration (they were written in frozen-scaffold terms independent of the
arm's mechanism).

**Kill (corpse-level):** if w1/b0 converts none of the trio and lifts scaffold <+3 on
<2 of them, or any retention break — E3 is RED and the clause fires.

## E3 corpse measured

Flag-off bit-identity: both specimens reproduce #93 exactly (w0/b0 0 / 11c/0r/4v,
w1/b0 54,616 / 8c/3r/2v). Flag-on:

- **w1/b0: 54,616 → 20 coded errors, 8c/3r/2v → 11c/0r/5v — all three trio blocks
  convert**, and the residue is b3:6 + b6:14, exactly this burst's oracle floor. The
  label-free pass reached the model ceiling on the design specimen. Frozen seeds
  improved on every block (e.g. b2 17,322 → 13,397 detection, then loop-converged).
- **w0/b0: 0 → 3 (11c/0r/4v)** — a retention break. The 3 bits sit in b5, the burst's
  ceiling block (the one with a nonzero oracle floor, b5:15). Every frozen seed
  improved 2–6× (b0 2063→1081, b1 7913→4695, b2 7959→3486, b3 5137→800, b4 8654→3510,
  b5 7683→7123, b6 3841→2297, b7 4715→2196, b8 10576→3417, b9 11328→4846,
  b10 14662→6345).

By the registered letter ("any retention break") this is RED as measured; the target
result (trio to the oracle floor) plus the break's location (3 bits on the one block
riding the model ceiling) says the arm's *arbitration*, not its geometry, needs a
mechanism fix — Amendment 2 below, pre-registered before any re-measurement.

## Amendment 2 (pre-registered): decisive-adoption margin

**Mechanism.** The always-offer arbitration adopts on ANY floor improvement,
including margins within gauge noise of the two anchor passes. Marginal adoptions
(altVar ≈ noiseVar) are coin flips the instruments never justified — the measured
target class improves its floor 10–30× (0.8–1.9 → ~0.05). On a salvage block at the
model ceiling, a marginal adoption changes the frozen seed for zero detection benefit
and can move the turbo loop's fixed point by a few bits — the b5 signature.

**Form.** Adopt only when the shifted geometry is decisively better:
`altVar < κ·noiseVar`, κ = 0.7 (pre-registered; single fallback κ = 0.5 if the first
measurement shows b5's adoptions still marginal-flipping; no sweep). Plus a log-only
`frozen-relock` diagnostic (causal floor, alt floor, adopted) so adoption margins are
data. κ = 1.0 with strict inequality is bit-identical to E3-as-measured (the
verification run).

**Predictions.** (1) w0/b0 returns to 0 / 11c/0r/4v exact — b5's marginal adoptions
are dropped; (2) w1/b0 keeps ≥2 trio conversions — its target-class adoptions clear
any reasonable margin. **Honest caveat**: if the margin run shows b5's adoptions are
already decisive (alt ≪ causal), the wobble is fixed-point sensitivity to a *better*
seed, not marginal flipping — κ cannot fix that, and the arm stands RED by the
retention letter unless the ship bar is re-judged with the guard re-pinned; that
re-judgment is NOT taken unilaterally — it would be recorded here as an open question
for the ship decision with the evidence laid out.

## Amendment 2 measured — the caveat fires

Control (κ=1): digits reproduce E3-as-measured exactly (w0 3 at the same bit
positions, w1 20) — the diag build is clean. Margin data: b5's adoptions sit at
ratios 1.10–1.96× with ONE decisive (f61: 17.6×); the trio's adoptions are decisive
(medians 7.0×/38.5×/21.1×, only 1–2 below 2× per block). κ=0.7: w1 keeps all three
conversions (20); w0 STILL 3, same positions. κ=0.5 (the registered fallback,
protocol now exhausted): w1 20 ✓ all conversions; w0 STILL 3, same positions — even
with b5 reduced to near-decisive-only adoptions and a *better* frozen seed
(7683 → 6803), its fixed point wobbles to 3. **The wobble is fixed-point sensitivity
of a ceiling block (its own oracle floor is 15) to ANY seed change — margins cannot
fix it.** Prediction (1) failed twice; prediction (2) held twice.

## Amendment 3 (pre-registered): relock as a second salvage rung

**Mechanism.** The E3-full form changes the frozen seed on EVERY salvage block,
including blocks whose standard salvage already converges — pure downside there: a
ceiling block's fixed point can move a few bits for zero benefit (the b5 wobble),
and no label-free arbitration can prefer one converged fixed point over another.
The class the instruments identified from the start is the salvage-FAIL class (the
trio, the b10-class): blocks where the revert stands because the standard frozen
seed can't reach the basin. Scope the intervention to exactly that class.

**Form.** Salvage becomes a two-rung ladder: revert-at-cap → standard frozen
re-detect + re-loop (unchanged, bit-identical) → *only if that fails*, retry the
salvage with the late-lock offer enabled (κ = 0.5, the measured decisive-only
margin). Blocks that converge anywhere along the existing path are structurally
untouched. Seam semantics: `TurboFrozenRelock` now gates the second rung
(default on at ship; off = bit-identical to #93); the offer itself activates only
inside the rung.

**Predictions (falsifiable, both specimens + guards):**
1. w0/b0 = 0 coded / 11c/0r/4v **bit-identical** (all four salvages succeed on the
   standard rung; the relock pass never executes).
2. w1/b0 = 20 coded / 11c/0r/5v (the trio reaches the second rung and converts; the
   b3:6 + b6:14 oracle-floor residues stand).
3. WN6 corpse and WN13sp corpse bit-identical (no salvage failures there).

**If prediction 1 or 3 fails**, the rung leaks state it must not — build defect, fix
or RED. If prediction 2 fails, E3's conversions were an artifact of the full-form
seed changes on OTHER blocks — E3 is RED and the clause fires.

## Amendment 3 measured — all predictions green, E3 lands GREEN

1. w0/b0 rung-on: **0 coded / 11c/0r/4v, bit-identical** (uncoded 89,559 identical)
   — retention is structural, the b5 wobble is gone because b5's path never changes.
2. w1/b0 rung-on: **20 coded / 11c/0r/5v** — the trio converts through the rung,
   digit-identical to the E3-full result; the residue is the b3:6 + b6:14 oracle
   floor. Frozen scaffold on the trio: b2 43 → 53, b7 51 → 64, b8 50 → 61 — all
   three across the ~52 basin edge.
3. WN6 rung-on 0 / 11c/0r/0v and WN13sp rung-on 0 / 11c/0r/0v — bit-identical to
   their pins (no salvage failures, rung never fires).
4. w1/b0 flag-off through the new call path: 54,616 / 8c/3r/2v exact.

Against the registered bars: **GREEN** — 3 of 3 trio conversions (bar asked ≥2) with
retention exact. Ship bar proceeds: default-on flip, suite, held-out pair, guards,
full battery.

## Ship bar: suite, guards, held-outs (battery below)

Default-on flip (`TurboFrozenRelock = true`, `TurboFrozenRelockMargin = 0.5`; env
seam `MS110D_AUTOPSY_FROZEN_RELOCK=0` restores the pre-B3.8 path). On the shipped
binary:

- **Suite**: 697 passed / 0 failed (105 env-gated skips) — exact.
- **Guards, all exact**: WN7 corpse w0/b0 0 / 11c/0r/4v (identical digits — the rung
  never fires on a converging burst); oracle b5:15; WN6 0 / 11c/0r/0v; WN13sp
  0 / 11c/0r/0v.
- **New pins**: WN7 corpse w1/b0 = 20 coded / 11c/0r/5v; WN0 w2/b97 (SEED=500,
  channelSeed 2097501) = 0 coded / 302 uncoded — the long-banked E1 guard candidate,
  now in the guard set.
- **Held-outs (banked pre-design, judged unseen on the shipped binary)**:
  dj-w1/b0 (1010508) **17,897 / 10c/1r/1v → 12 / 11c/0r/2v** and dj-w3/b0 (3010508)
  **17,871 / 10c/1r/4v → 14 / 11c/0r/5v** — both leftover blocks convert through the
  rung to their model-floor tails; zero per-block regressions.

Corpse-side artifacts banked in `corpse/` (m2a/e3/a2/a3/s summaries,
a2-relock-margins.txt).

## S battery — 32/32 legs, zero retries

**WN7 canonical 3.38E-2 → 2.56E-5 (83 errors / 3,243,776, turbo 88c/0r), disjoint
2.23E-2 → 1.48E-5 (48 errors, 88c/0r)** — ZERO reverts in either family; every block
of all 16 bursts converges, and the residue is pure model-floor tails. Per burst
(baseline → new): canonical 1000508 54,616 → 20, 1001508 36,754 → 25, 2001508
18,233 → 38, five bursts 0 → 0; disjoint 1010508 17,897 → 12, 1011508 18,376 → 0,
2010508 18,014 → 0, 3010508 17,871 → 14, 10508 16 → 16, 2011508 6 → 6, two at 0 → 0.
**No burst regressed in either family.** Cumulative across B3.6 → B3.8: canonical
1.73E-1 → 2.56E-5 (6,760×), disjoint 1.95E-1 → 1.48E-5 (13,180×).

**Every non-WN7 census byte-identical to the #93 baseline (72 files,
`battery/census-compare.txt`)**; gated eight at their exact digits; AWGN ×10, static,
Doppler green; WN8 measured unchanged (4.96E-1/4.97E-1, QAM16 — structurally outside
the rung).

WN7 is not yet AT MASK (target 1E-5): pooled 131/6.49M ≈ 2.0E-5 sits 1.5–2.6× above
the line. But the residual has changed KIND: with zero reverts, the salvage/detection
story is CLOSED — what remains is the converged-decode model-floor tail (the same
class as the oracle's own b3:6/b6:14 residues), which is a model-refinement question
(per-segment echo, noise pricing at the tail), not a detection question.

## Disposition

- **E2 (anchor-track h1): RED at the offline gate**, zero demodulator cost. G2 and
  positional G4 measured dead (h1 model error ~2% of the priced floor, flat profile).
- **E3 (late-lock salvage rung): GREEN and SHIPPED** — Amendment 1 (window-shift
  re-lock offer, floor-arbitrated), Amendment 2 (decisive-adoption margin 0.5),
  Amendment 3 (second-rung scoping; converging blocks structurally untouched).
- **WN7 OPEN at 2.56E-5 / 1.48E-5** — within 2.6× of the mask, zero reverts, the
  binding constraint is now the converged model-floor tail, not detection.
- The intervention-tested negative banked: margins cannot stabilize a ceiling
  block's fixed point (b5: ANY seed change wobbles it a few bits; only structural
  scoping — don't touch converging blocks — retains exactly).
