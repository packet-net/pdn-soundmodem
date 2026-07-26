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
