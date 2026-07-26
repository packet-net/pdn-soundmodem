# Phase B4.1 — WN6 margin: a shippable noise-floor estimator for per-segment BCJR pricing (design, pre-registered 2026-07-26)

**Branch:** `ms110d-b41-wn6floor` off main 9002d1b (#88 merged). Written BEFORE any instrument or lever code, per the Phase B discipline.

## Why this leg, and what is banked

B4 left WN6 AT THE LINE: §5.3 draws pass at 8.79E-6 both families, but the pooled bound is 1.14E-5 — the point needs *margin*, and the flip criterion needs pooled ≤ 96/12.97M. The B3.3-segnoise leg ([../2026-07-25-phase-b33-segnoise/](../2026-07-25-phase-b33-segnoise/)) measured exactly such a margin lever and reverted it with the mechanism fully characterized: within-frame BCJR noise-floor honesty is worth ~30 % of WN6's residue (57/57 → 41/39 on paired seeds, both families) and 5× on the WN7 oracle floor (15 → 3) — but the 4-per-frame *windowed* floor estimates carry χ² jitter (~12 % relative at WN6/WN7's 128 dof; ~29 % at WN2's 24 dof — WN2 partitions U=48 into 12-symbol segments), and that estimator error moves the marginal waveform's decision boundaries even inside the derived 2× heteroscedasticity gate (WN2 disjoint 12 → 46, Amendment 4, REVERTED). The bank names two estimator candidates and forbids gate-knob tuning. This leg is the pre-registered follow-through: **fix the estimator, not the gate.** The banked patches (`segnoise-pricing.patch`, `segnoise-gated.patch`) no longer apply after B3.4/B3.5 moved the tree — resurrection is re-implementation guided by them.

## The estimator problem, stated precisely

TurboCore prices the chain BCJR with one floor per frame (`noiseVar = 0.5·residual/U`). The true floor is heteroscedastic in fade-crossing frames (|h1| swings 2–3× within a frame). Pricing with per-segment windowed estimates helps exactly where heteroscedasticity is real and hurts where the floor is flat, because a windowed estimate is an unbiased but noisy sample: relative error ~ √(2/dof) with dof ≈ 2·(segment symbols). What a shippable form needs is an estimator whose (a) *error* is small enough to stay out of WN2's decision boundaries and (b) *bandwidth* is high enough to keep the within-frame fade-null structure that carries the WN6 gain. The raw windowed form fails (a); a naive strong smoother fails (b) — fade nulls are fast local excursions (the envelope crosses a null quickly even at 1 Hz Doppler) on a slow background.

Timing facts the candidates build on: WN6/WN7 frames are 120 ms (U=256, K=32 → 8.33 Hz frame rate; 4×64-symbol segments → 33 Hz segment cadence); WN2 frames are 40 ms at 2400 Bd (U=K=48 → 25 Hz frames, 100 Hz segment cadence, but only 12 symbols per segment). The residual-power process is bandlimited by the squared fade envelope (~2–4 Hz for the 1 Hz Poor rig).

## Phase 1 — instrument and characterize (no behavior change)

Diagnostics-only addition on the oracle/turbo path: per-segment residual floors (`nseg=`) and per-segment mean |h1| appended to the existing `turbo-frame` line. On the **oracle pass** (`MS110D_AUTOPSY_ORACLE=1`, true labels), the per-position squared-residual field is a truth sample; a two-sided bandwidth-matched kernel over it (width derived from the ~4 Hz residual bandwidth, not tuned) is the **reference floor** each candidate is scored against.

Corpus (all reproducible from committed censuses): the four WN6 B4-census residue bursts (canonical w0/b2 8 errs, w1/b2 14, w1/b3 16, w2/b5 19) + one healthy WN6 burst; WN2 cluster corpses from the B4 census (w0/b34, w0/b51, w1/b16) + one healthy; the WN7 w0/b0 standard corpse.

Registered measurements:

- **M1 (trajectory form):** does `floor(u) = A + B·|h1(u)|²` — A per block, B per frame, shape from the already-estimated h1 trajectory — capture the measured within-frame structure? Readout: per-frame R² and residual-error distribution vs the reference floor.
- **M2 (smoothing form):** measured autocorrelation/bandwidth of the segment-floor series across frames; error and null-tracking of a two-sided bandwidth-matched smoother (the turbo pass holds the whole block, so non-causality is free).
- **M3 (the decision rule, derived not tuned):** a candidate advances iff its measured floor error on the WN2 corpses is **≤ 25 % of the raw windowed estimator's** (the raw form is *measured* to break WN2; the ~8× variance-cut arithmetic says 4× headroom is available) AND it preserves **≥ 80 % of the reference floor's within-frame dynamic range** on the WN6 fade-crossing frames (the Amendment-3 lesson: flatten the nulls and the ship case evaporates). If both candidates pass, the one with lower WN2 error ships to Phase 2; a hybrid (trajectory shape + smoothed scalars) is admissible only if M1/M2 both show partial fits whose failure modes are complementary — and is then registered as an amendment before coding.

**Consequence clause 1:** if no candidate passes M3, the leg closes as a measured negative — no lever is coded, WN6 stays AT THE LINE, and WN6's remaining paths (detector/model-front work; FD-turbo for the WN7 story) are recorded. Estimator-threshold shopping after seeing the corpses is forbidden.

## Phase 2 — the lever (only for a Phase-1-passing estimator)

Re-implement per-segment pricing (the banked `Ms110dChainBcjr.Equalize` per-symbol `noiseVarPerSymbol` span form) with the winning estimator replacing raw windowed floors. The 2× heteroscedasticity gate is NOT resurrected unless the winning estimator's Phase-1 characterization shows it is still needed — the gate was a patch over estimator noise, and carrying it forward without measured need would be superstition.

Corpse bars: WN7 w0/b0 shipped 72,666 / 7c/4r held or improved, oracle ≤ 15 (the raw form measured 3 — regression above 15 means the estimator broke something the raw form didn't); WN6 corpse and WN13sp guards outcome-identical 0 shipped / 0 oracle (bit-exactness not expected on TIR-priced frames); the three WN2 cluster corpses: no new errors on their bursts; first decodes bit-identical everywhere (pricing is turbo-side only, by construction).

## Phase 3 — battery (B4 three-lane form) and ship/flip bars

Pre-committed budgets: the B4 table unchanged (WN2/WN5/WN6 at 6M/family, the rest per B4). Bars, all on paired seeds vs the B4 censuses:

- **WN2 6M ×2 (the hard bar):** canonical ≤ 30 and disjoint ≤ 29 AND zero new error bursts vs the B4 census sets. Any new cluster → **revert, and consequence clause 2**.
- **WN6 6M ×2 (the ship case):** ≤ 45 errors each family (retain the banked ~30 % class). This implies pooled ≤ 90 ≤ 96, so **ship ⇔ WN6 enters the default-gated set under the B4 flip criterion in the same PR** (b: bound at 90/12.97M ≈ 8.4E-6 ✓; c: mean ≤ 45/6M → P(k > 60) ≤ 2 % ✓).
- **WN7 3M ×2:** neutral (±5 % class, per the segnoise Amendment-2 precedent).
- Everything else holds its B4 state; suite 696/0; zero acquisition failures.

**Consequence clause 2:** a WN2 failure here is the lever class's third strike (raw form, gated form, estimator form) — the per-segment-pricing family CLOSES for good absent new science, WN6 stays AT THE LINE, and this is recorded as the definitive negative. No fourth variant.

**Consequence clause 3:** WN6 lands in (45, 57] — improvement short of the ship bar: revert, record the measured shortfall against the banked 41/39 (it would mean the estimator traded away part of the gain for its error reduction), and close per clause 1's terms.

Files land here as the measurements run.
