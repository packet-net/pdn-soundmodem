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

## Buildable-in-window call (honest)

E1 and E2 are comfortably buildable and testable in-window; E3 is buildable if reached. FD-MMSE proper is neither in-window nor, per the dominance argument, worth building. The design-only exit is registered and respectable: it would close C2b — the last standing structural escalation on WN7 — with a measured verdict, leaving WN7's residual formally attributed to detection-information limits rather than unexplored architecture.
