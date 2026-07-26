# §B3.7 — WN7 FD-turbo design leg: resolving the C2b escalation

**Registered before any instrument or candidate code.** Branch `ms110d-phase-b37-wn7-fdturbo` from main 8c30392 (#92 = B3.6).

## The question

§B3.6's disposition moved WN7's binding constraint from loop *structure* to **label-free detection quality on scaffold-poor blocks**: the salvage rung converts every block whose frozen-probe re-detection lands inside the basin, and fails exactly on the blocks where it doesn't. The standing FD-turbo escalation (C2b) asked whether *exact application of the true channel* — per-bin frequency-domain MMSE over per-segment LTI windows ≤27 ms — is the missing power. This leg resolves that escalation: either a concrete detection upgrade ships under the registered bars, or C2b closes design-only with a written verdict.

## Decision inputs (all measured, banked in B3.6)

- **Residual anatomy.** Canonical corpse w0/b0 (channelSeed 508): salvage converts b4/b5/b7, fails b10 → 18,239 coded / 10c/1r/3v. Held-out w1/b0 (channelSeed 1000508): salvage converts b4/b10 to exact 0, fails b2/b7/b8 → 54,616 / 8c/3r/2v. Nine salvage attempts across the two specimens = the basin-edge calibration set (5 converge / 4 fail).
- **Frozen-decode starts (w0/b0, coded errors / 36,864):** b4 15,162 (41.1%) ✓, b5 14,515 (39.4%) ✓, b7 8,288 (22.5%) ✓, b10 16,554 (44.9%) ✗. The w1/b0 frozen line is not yet banked (M0 produces it).
- **The probe-only TIR solve works.** 2×(K−fb) = 40 rows per frame against 24 FF taps + 1 echo coefficient — rank was the B3.6 open question and it is answered. Adding the straddle pair costs ONE more complex parameter in a 40-row solve, behind the existing 4·SSE₀/rows acceptance margin.
- **The straddle-pair solve already exists** (`Dfe.SolveTrainingTir`, §B3.3): Lag2 = Lag ± 1, registered mechanism = the Poor channel's 2 ms path ≈ **4.8 T at 2400 Bd** — a fractional delay that *must* split across two adjacent taps. It is suppressed in the shipped hard iteration 0 by the §B3.3 **label-trust gate** (cancelling the adjacent tap with ~half-wrong re-encoded labels injects unpriced observation error; measured flipping a marginal WN6 block, 146×). **That rationale does not apply to the frozen pass**: its solve rows are probe symbols — known truth — so the pair *solve* is fully trusted; only the *application* on the data span needs care.
- **The oracle floor already includes everything.** The oracle path runs `allowPair: trustedLabels` with per-segment h1/h2/h2b (4 segments = 64 symbols = **26.7 ms** — the escalation's LTI window already exists in the label-full path) and §B4.1 per-segment noise pricing. b5:15 is the ceiling of this whole family; the oracle-floor model tail is **not** this leg's target.

## The architecture verdict on FD-MMSE proper (registered)

Per-bin FD MMSE is a **linear** application of the measured channel. This codebase's chain BCJR is **exact MAP** on the two-tap model y[t] = h1[t]·x[t] + h2[t]·x[t−d] — strictly stronger given identical channel knowledge. The only channel structure the chains cannot carry is the *adjacent* second echo tap (Lag2 breaks the residue-class decomposition), and the label-full path already handles exactly that by soft cancellation outside the trellis with variance-priced uncertainty — the same interference-cancellation structure FD-turbo implementations use for what their linear stage can't model. Every deficiency FD-MMSE would fix on this channel is therefore a channel-**knowledge** deficiency of the frozen pass, not an application-**structure** deficiency.

**Verdict: FD-MMSE per-bin application is dominated in this architecture and will not be built.** Revival condition (the honest escape hatch): if M1a finds SseTir(pair) still ≫ the probe noise floor on the failing blocks — i.e. real post-FF response *beyond* the straddle pair that no {0, d, d±1} model can express — the dominance argument fails and C2b reopens as a build candidate. Absent that, C2b resolves here into: **close the frozen pass's knowledge gap to oracle-quality detection, probe-only, feeding the existing exact detector.**

## The frozen pass's deficiency budget (gap to oracle-quality detection)

| # | Frozen pass today | Label-full/oracle path | Mechanism of the gap |
|---|---|---|---|
| G1 | Single-lag echo, pair OFF | Straddle pair, soft-cancelled, variance-priced | The FF must *suppress* the fractional neighbor (weak-cursor-style noise enhancement); its leftover rides in the floor as white noise while actually being data-correlated ISI |
| G2 | h1 = linear interp between TWO probe anchors ~120 ms apart | 4-segment h1 through segment centres | A 1 Hz fade moves substantially within a 120 ms frame; linear-2-point misses mid-frame fade nulls — exactly where first-pass errors concentrate (the donut) |
| G3 | h2 frame-constant | Per-segment h2 (frame-constant form measured REGRESSING b10 oracle 6 → 19) | Same fade physics on the echo path |
| G4 | Noise frame-constant, probe-priced *at the boundaries* | §B4.1 per-segment/per-position pricing | A mid-frame fade null's model error is invisible to boundary probes → over-confident LLRs exactly where the model is worst |

Note on G2: consecutive frames share probes, so the pass already measures the anchor series at one anchor per 288 symbols ≈ **8.3 Hz — oversampling the ~1 Hz fade process 4× above Nyquist**. The information to reconstruct h1(t) mid-frame exists in the probes; the current 2-point linear interpolator throws it away.

## Candidates (ordered by cost; ONE lever per arm)

- **E1 — pair-priced frozen solve.** `allowPair: true` in the frozen solve; minor tap NOT cancelled (no labels) but **priced**: noiseVar += |c2|² (unit-power PSK), the zero-information limit of the soft path's cancellation-with-variance-bump. Wins twice: the FF stops fighting the neighbor (SseTir drop), and the leftover ISI is correctly attributed. ~30 lines, frozen-pass-contained.
- **E2 — anchor-track h1 (+ derived noise pricing).** Replace within-frame linear-2-point with an interpolator over the multi-frame anchor series (quadratic through 3 anchors, or 4-point raised-cosine); optionally price per-segment noise from the tracked model (B4.1 analog, probe-only). Requires a pre-sweep collecting all frames' anchors before detection — restructuring, still frozen-pass-contained. ~100 lines.
- **E3 — one round of self-IC (turbo equalization without the outer code).** Chain-posterior E[x] from the frozen detection feeds ONE round of the label-full estimation machinery (per-segment h1/h2, pair cancellation with variance pricing, per-segment noise), then a second chain run. The soft path's exact structure with self-soft-symbols instead of decode labels — nothing here can be laundered by a wrong decode. Largest (~200+ lines incl. posterior export from the chain BCJR); built only if M1 says the deficiency mass needs it and a cheaper arm moved-but-not-enough.
- **FD-MMSE per-bin:** registered dominated (above). Not built absent the revival condition.

## Measurement order

- **M0 — specimens + frozen scaffold anatomy.** (a) Guard-exact re-establishment of both specimens — w0/b0 must reproduce **18,239 / 10c/1r/3v + oracle b5:15**, w1/b0 must reproduce **54,616 / 8c/3r/2v**; any drift STOPS the leg. (b) New instrument: frozen-biterrs CSV (per-position frozen LLR-sign errors, same schema as autopsy biterrs) → scaffold.py on the FROZEN decode for all 22 specimen blocks. (c) Calibrate the salvage basin edge in **scaffold terms** from the 9 attempts (the B3.6 lesson: basin coordinates are frame-structural, not label-percentage; the first-pass split was ≥35 vs ≤33 of 64 frames under 20% — the frozen-seed split gets its own numbers).
- **M1 — deficiency budget on the failing blocks.** (a) *Pair mass:* diagnostic second solve per frame with pair ON (log-only; applied path unchanged) → accepted-pair rate, |c2|/|c1|, SseTir(single) vs SseTir(pair) vs SseNull vs noise floor. (b) *h1 interp error:* complex anchors in the diag line → predict each anchor from its neighbors (linear midpoint test) vs actual; plus oracle per-segment h1 comparison on the corpse. (c) *Noise under-pricing:* frozen frame-constant floor vs oracle B4.1 per-segment residuals on fade-crossing frames. The budget RANKS E1/E2/E3 by deficiency mass; arms run in that order, sequentially, with corpse verdicts between.
- **M2+ — candidate arms.** Behind a flag, corpse first (both specimens), held-out judgment per the bars, one arm at a time. Composition of two green-ish arms ships only through a pre-registered amendment (B3.5b/B3.6 template).

## Bars and kill conditions (registered before M1 numbers exist)

- **Per-arm GREEN:** converts ≥2 of the 4 failing specimen blocks (w0/b0 b10; w1/b0 b2/b7/b8) to salvage convergence, with all 5 currently-salvaged blocks (w0/b0 b4/b5/b7; w1/b0 b4/b10) STILL converging — their seeds change, so this regression risk is real — and zero reachable change outside the revert path (structural, by construction).
- **Per-arm AMBER:** converts exactly 1, or lifts the frozen scaffold count on ≥3 of the 4 failing blocks by ≥3 frames (M0-calibrated) without a conversion — evidence the next arm inherits.
- **Per-arm RED:** neither. An arm that goes RED is dead; no unregistered variant stacking.
- **M1 kill (leg-level):** pair mass median < 5% of |c1|² on failing-block frames AND anchor midpoint-prediction error < 10% of |h1| RMS AND noise under-pricing < 1.5× per-segment — all three gaps negligible → the frozen detection is already at the probe information limit → **design-only verdict**, C2b closed, residual attributed to model-front (WN8-class) work.
- **Leg RED:** two consecutive RED arms → design-only verdict, same closure.
- **Ship bars (any green arm):** guards (WN7 corpse re-pinned with disclosed numbers; WN6 corpse 0/11c0r/0v; WN13sp 0/11c0r/0v; suite 697/0), FULL house battery (b36 lane.sh template, WN0 gated), all non-WN7 censuses byte-identical to the #92 baseline, WN7 both families ≤ 5.63E-2 / 6.14E-2 (no regression).

## Honest mask expectations

Converting all four failing specimen blocks is worth ≈ −72.9k canonical (508 → ~0, 1000508 → ~0): 182,579 → ~1.10E5 ≈ **3.4E-2**, plus whatever the arms do to the other bursts' reverted blocks — call the plausible landing zone 2E-2–4.5E-2 canonical if a candidate generalizes. That is still ~3.3 decades above the 1E-5 mask: **WN7 stays OPEN/measured whatever happens here.** This leg's win condition is specimen conversion + census improvement with zero collateral, not gating.

## M0 — specimens re-established, frozen scaffold anatomy (measured)

All five pins exact on the fresh tree: w0/b0 **18,239 / 10c/1r/3v** (bare AND with the frozen+pair instruments armed — instrument bit-identity confirmed by identical summaries), w1/b0 **54,616 / 8c/3r/2v** (both), oracle **b5:15**. Fresh frozen info-decode errors per block: w0/b0 b4 15,572 / b5 13,161 / b7 9,725 / b10 16,219; w1/b0 b2 17,460 / b7 17,289 / b8 17,967 / b4 15,293 / b10 13,613 (of 36,864; differs slightly from the B3.6 M2a numbers — the diag pass inherits pipeline tracking state, which differs pre/post-salvage; noted, not a drift).

**The frozen-seed basin edge is perfectly clean in frozen-scaffold terms** (frames of 64 with <20% wire LLR-sign error, from the new frozen-biterrs instrument): every converging seed ≥53 (b4/b5/b7 w0: 53/57/55; b4/b10 w1: 56/53), every failing seed ≤51 (b10 w0: 51; b7/b2/b8 w1: 50/42/39). Worst-half-mean separates identically (≤21.8% vs ≥23.5%). **The candidate bar in M0-calibrated terms: lift a failing block's frozen scaffold count to ≥53.** Gaps: b10(w0) +2, b7(w1) +3 — at the edge; b2(w1) +11, b8(w1) +14 — deep.

## M1 — deficiency budget (measured)

- **M1a (G1, pair): the registered E1 is DEAD.** Straddle-pair acceptance in the probe-only solve: 4 of 1,408 frames (0.3%) — far under the 5% line. The pair cannot be established from 40 rows. But the same instrument found the real G1: **single-lag acceptance starvation + a spurious lag-11 cluster.** Frozen accepted lags: ~71% at 5 (true: 4.8 T), **~27% at 10–11**, and ~40% of frames reject entirely (null → full-inversion FF, the §B3.3-measured weak-cursor pathology). The oracle path picks lag 5 on 574 frames, lag 11 on zero.
- **The lag-11 mechanism (the leg's discovery): 16-periodic probe aliasing of the pre-cursor.** The K=32 mini-probe is Table D-XXI base-16 *cyclically extended* — probe-row regressors repeat mod 16, so a −5 pre-cursor column is IDENTICAL to the causal lag-11 column on probe rows. The DFE tap-sizing note already documents the physics: when the lock rides the later path, the earlier path returns at −2 ms = −4.8 T. The frozen solve searches causal lags only, and on lock-on-late-path frames the pre-cursor aliases into an accepted lag-11 "echo" — a structurally wrong chain model on ~27% of accepting frames. The label-full path is immune (scrambled data rows are aperiodic); the frozen probe-only solve is maximally exposed. On the failing blocks the damage concentrates: b7(w1) gets the correct lag-5 model on only 36% of frame-solves (43% of its acceptances aliased, 38% null).
- **M1b (G2, h1 interp): alive, moderate.** Direct oracle comparison (per-frame complex-scale-aligned): frozen linear h1 is 6.7–10.1% RMS from oracle truth at median, 13–21% at p90 — model-error power comparable to the 19 dB noise floor at median and 2–3× above it in the worst frames. Label-free anchor midpoint test agrees (med 8.3–15.1% at double spacing → ~2–4% at actual spacing, curvature-scaled). Above the 10% negligibility line; not the dominant term.
- **M1c (G4, noise): alive, marginal.** Oracle nseg within-frame spread med 1.40–1.64×, p90 1.89–2.52× — at the registered 1.5× line.
- **FD-MMSE revival condition: NOT met.** After the single-lag solve the probe residual per row sits at ~0.8–1.4× the priced floor — no residual mass beyond the {0, d} model demanding per-bin application. The dominance verdict stands.

**Budget ranking: G1-revised (acceptance starvation + probe aliasing) ≫ G2 > G4.** The M1 kill condition is not met; the arm order is re-ranked below.

## Amendment 1 — E1 re-formed as E1′: burst-consensus constrained frozen solve (registered before build)

M1a killed E1's mechanism (the pair) and replaced it with a sharper one. E1′ attacks the measured G1 directly, label-free:

- **Vote sweep** (new, per block): every frame runs the free probe-only solve exactly as today, but only its accepted lag is recorded — no application. **Consensus = the modal accepted lag** across the block's 64 frames (on both specimens: 5, by 2.6–4.5×).
- **Detection sweep** (today's sweep, one change): each frame's applied solve is constrained to test ONLY the consensus lag (`onlyLag`), with the single-candidate acceptance margin — 4·ln 2·SSE₀/rows instead of 4·ln 12 (≈3.6× lower bar), justified because the echo delay is a burst-level physical constant (the Watterson path delay does not move frame-to-frame) and the constrained test pays no L-fold selection premium. Frames that still reject stay null, exactly today's fallback. No consensus (no accepted votes) → today's behavior unchanged.
- **What it kills:** the ln 12 starvation (lower margin → the ~40% null frames get their real echo back) and the lag-11 aliasing (11 is never the modal lag; aliased frames get the constrained lag-5 test, which the eigen refit can satisfy by putting the cursor on the early path and the echo at +5 — the physically-correct decomposition for every 2-path frame regardless of which path the lock rode).
- **Scope:** `Dfe.SolveTrainingTir` gains an `onlyLag` parameter (default 0 = today; every existing call site bit-identical). `TurboFrozenProbePass` gains the vote sweep + constrained detection solve. Reachable only from the salvage rung and the frozen diag pass — converging blocks stay bit-identical by construction; the five currently-salvaged blocks may change seeds and are covered by the bars.
- **Bars:** the registered per-arm bars apply unchanged (GREEN ≥2 of 4 conversions with all 5 current conversions retained; AMBER 1 conversion or ≥3-frame scaffold lift on ≥3 of 4; RED neither — arm dead, no variant stacking). Mechanism checks that must ALSO hold for GREEN/AMBER: post-E1′ frozen accepted-lag histogram shows the 10/11 cluster ~eliminated on specimen blocks, and echo-modeled (lag-5) frame fraction rises materially (expectation: from ~50% toward ≥75%).
- **Cost:** ~60 lines. **Consequence if RED:** E1′ dead; E2 (anchor-track h1, G2) runs next on its registered form; two consecutive REDs → design-only closure per the leg bars.

## Buildable-in-window call (honest)

E1 and E2 are comfortably buildable and testable in-window; E3 is buildable if reached. FD-MMSE proper is neither in-window nor, per the dominance argument, worth building. The design-only exit is registered and respectable: it would close C2b — the last standing structural escalation on WN7 — with a measured verdict, leaving WN7's residual formally attributed to detection-information limits rather than unexplored architecture.
