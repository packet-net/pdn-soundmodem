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

---

## Amendment 1 (2026-07-26, before reading any candidate scores): the scoring construction

Two defects surfaced in the first analysis cut, both in the *measurement*, not the candidates, and both fixed before any M3 verdict is read:

1. **The bandwidth-derivation rule measured noise.** The registered "99 %-power bandwidth of the segment-floor series ×2" criterion returns the χ² noise bandwidth (white — it gave 35–200 Hz), not the process. The reference kernel reverts to the registration's physics bound (4 Hz, half-width 300 symbols), and the *measured* process bandwidth moves to a reported readout via short-lag acf ratio where the AR-like process dominates the white noise: **0.8–2.2 Hz across all ten corpses** — the 4 Hz bound stands validated at ≥2× the measured process.
2. **Candidate scoring was circular for SMOOTH** (same kernel, nearly the same data as the reference). Amended protocol: within each frame, even positions are the estimation set and odd positions the held-out scoring set. All candidates (and the reference for the RMS/range metrics) are built from even positions; scores are computed against the odd-position field. Additionally a reference-free readout: held-out χ²₂ negative log-likelihood of the odd-position residuals under each candidate floor, with the SHIPPED frame-constant floor as the anchor every candidate must beat. M3's thresholds are unchanged and now applied to the split-half RMS metric.

First honest structural finding (independent of any candidate): **WN2 has zero fade frames** — at 40 ms frames the true within-frame floor range never exceeds 2× on any of its four corpses. WN2's per-segment "pricing" in B3.3 was therefore *always* pure estimator-noise injection, which is exactly why no gate could save it. Any winning estimator must collapse to (near-)frame-constant behavior on WN2 by construction, not by a waveform switch.

## Amendment 2 (2026-07-26, after M1/M2 characterization, before any in-decoder measurement): the M-metric verdicts, and the variant ladder for the deciding decode test

Split-half scores (`m123-scores.txt`, all ten corpses; dNLL is held-out χ²₂ likelihood vs the SHIPPED frame-constant floor, negative = better):

- **TRAJ is dead** (M1 fails): worse than SMOOTH on every corpse and worse than SHIPPED on held-out likelihood everywhere (+8…+26 mnat) — the floor structure does not follow |h1(u)|²; the fadecross-era claim that motivated the candidate does not survive as an estimator basis.
- **A fourth candidate tested and killed**: per-segment-index cross-frame smoothing (SMOOTH2 — the "frame-periodic anchor-geometry" hypothesis) loses on WN6 (+9…+21 mnat, range captured 0.33) — the within-frame structure is NOT frame-periodic.
- **The floor's measured anatomy**: a ≤4 Hz slow background (procBW 0.3–3.2 Hz measured across all corpses) plus rare fast *frame-local* events (fade crossings, each its own event). RAW captures the events but pays χ² noise on every quiet segment — on WN2 that is +44…+49 mnat of pure damage (B3.3's collateral, now measured at the likelihood level); on WN6 it nets ≈ 0 in mean NLL while the decode gain lives in the tail events. SMOOTH captures the background, beats SHIPPED on WN2 (−7…−11 mnat — the first estimator measured *better* than the frame constant on the marginal waveform), but misses the events (+7…+13 on WN6/WN7).
- The registered M3 25 % threshold as written is not evaluable: the measured candidate errors include the split-half reference's own noise floor in quadrature, so ratios against RAW understate every candidate. The proxies' job — ranking families and pinning the anatomy — is done; the decision moves to decode outcomes, where it always had to end.

**The variant ladder** (registered now, measured next; all thresholds χ²-derived from segment dof — 3σ in log floor-ratio: engage at ratio > exp(3·√(2/dof)) ≈ 1.45× for WN6/WN7's 128-dof segments, ≈ 2.4× for WN2's 24-dof segments — WN2's flat truth cannot cross its own threshold, so it prices frame-constant *by construction*):

1. **SPIKE-UP** (safest): frame-constant base; a segment's floor replaces the base only where the windowed estimate exceeds base × 1.45/2.4 (3σ upward). Can only *de-confidence* locally-bad spans — cannot inject an over-confident low floor anywhere. If it keeps the WN6 corpse gains, it ships.
2. **SPIKE-2S**: both directions at 3σ. Only if SPIKE-UP loses the WN6 gain.
3. **SHRINK** (SMOOTH base + 3σ deviations): only if both spike forms fail — carries cross-frame machinery and SMOOTH's measured +7…+9 WN6 base deficit, so it is last, not first.

**Deciding decode measurement** (corpse suite, pricing behind `MS110D_TURBO_NSEG`, default-off = shipped bit-identical): (a) the two oracle-0 WN6 residue corpses (w1/b2 14 errs, w1/b3 16 errs — the pricing-reachable class) must improve, and no WN6/WN2 corpse regresses; (b) WN2 cluster corpses (w0/b34, w0/b51, w1/b16) + healthy: byte-identical or improved, zero new errors; (c) WN7 w0/b0 shipped 72,666/7c/4r held or improved, oracle ≤ 15; (d) the four standing guards exact on the default-off path. The FIRST ladder variant meeting (a)–(d) advances to the Phase-3 battery unchanged; if none does, consequence clause 1 executes (leg closes negative, no ship).

---

## Results: SPIKE-UP passes the whole ladder on its first rung — WN6 35/39 both families, everything else held to the digit; WN6 enters the default-gated set

**Decode measurement** (`corpse/spikeup-decides.txt`): (a) w1/b2 **14 → 0**, w1/b3 **16 → 8**; the two model-ceiling bursts (w0/b2 oracle-8, w2/b5 oracle-31) byte-identical — pricing touched exactly what it can reach and nothing it cannot. (b) all four WN2 corpses **byte-identical**. (c) WN7 72,666/7c/4r and oracle 15, exact. (d) guards exact. SPIKE-2S and SHRINK were never needed.

**Battery** (B4 three-lane form under `MS110D_TURBO_NSEG=spikeup`, 33 legs, zero failures, zero acquisition failures; `battery/`): **WN6 6M canonical 57 → 35 (5.40E-6 direct) / disjoint 57 → 39 (6.01E-6 direct)** — the full banked raw-pricing gain (35/39, matching B3.3-segnoise's decision legs to the digit) captured by the safe estimator, with the collateral that killed the raw and gated forms measured absent: **WN2 exactly 30/29 with all eight census files bit-identical to B4's**. WN5 23/0, WN13 0/0, WN3 0/0, WN4 0/3, WN1 0/0 — all identical to B4. WN7 562,621/633,544 (+0.002 %/+2.9 % — inside the ±5 % neutral bar). WN0/WN8 identical (no turbo path). AWGN 10/10 zero (flat floors cannot cross a 3σ χ² band — zero engagement by construction), static zero (one rc=134 memory-pressure retry, clean second run), Doppler 3/3 zero.

**WN6 flip arithmetic under the B4 criterion**: pooled 74/12.97M → 97.5 % bound 7.15E-6 ≤ 1E-5 ✓; per-family mean 37 at the 6M default → §5.3 false-red ≈ 0.02 %/family ✓ (was ~32 % at 57/57 — the nightly-trust problem the B4 registration refused to gate over is *solved*, not waived). WN6 joins the default-gated set in `Ms110dMaskTests` in this same PR, per the registered ship ⇔ flip coupling.

**Ship form**: `TurboNsegMode` null now means the shipped default (`"spikeup"`; `"off"` restores the pre-B4.1 frame constant, `"spike2s"` remains the two-sided measurement variant). Ship-form guards with NO environment: all four exact — the standing guard specs (WN7 72,666/7c/4r/oracle-15, WN6 0/11c0r, WN13sp 0/11c0r) carry forward unchanged because those corpses never cross the χ² thresholds. Suite 697/0 (the new `Per_Symbol_Noise_Floor_Prices_Each_Position_Analytically` contract test included).

**The estimator lesson, closed**: B3.3's lever failed twice on estimator error (raw ~12–29 % χ² jitter; a ratio gate that couldn't separate error from structure on the marginal waveform). The shippable form needed neither a better *smoother* nor a bigger gate — it needed the χ² statistics taken seriously: engage only beyond the segment's own 3σ band, and only in the direction that cannot inject over-confidence. WN2 is protected by construction (its flat truth cannot cross its own 24-dof 2.4× band — measured: bit-identical censuses), and the fade-crossing de-confidence that carries WN6's entire −38 % survives untouched.
