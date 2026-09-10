# W2 — sparse-model residual decomposition (WN8 redesign program)

Registered 2026-07-31, after W1b banked the proceed branch ([../2026-07-31-wn8-w1b/](../2026-07-31-wn8-w1b/): MFB 0/0 — the waveform is not the floor), before any variant code or run.

## Registration

**Question.** Which model term carries the W1 truth-pass residual (100 canonical / 36 disjoint, hot blocks b0/b2/b6/b9 + b3/b6, clean-block fit rms ~3.5× the AWGN floor): (a) response beyond the {cursor, echo d, straddle d₂} tap set — the B3.7 FD revival condition re-measured at 16QAM; (b) within-frame drift of the per-frame static gauge; (c) the uncompensated constant offset in the truth lookup (RX front-end group delay); or (d, residual by elimination) pricing/detection tail?

**Decision inputs.** W1's banked specimens and truth-frame rms logs; the W1b attribution ladder; closeout §5 (the FD-MMSE banked negative's revival condition: "real post-FF response beyond {0, d, d±1}").

**Instrument + method.** Three registered variants of the W1 truth pass — all reachable only inside the truth pass (passive by construction, no shipped-path change), all corpse-only:

- **V-offset** (rig-side env `MS110D_MFB`-family knob on the truth lookup): scan the constant lookup offset over {−8, −4, +4, +8} samples against the banked 0, canonical specimen. Reads the actual front-end group delay directly as the fit-rms bowl minimum.
- **V-split** (`MS110D_AUTOPSY_TRUTH_SPLIT=2`): the per-frame gauge fits per half-frame (128 rows each, 2× constants). Reads within-frame gauge drift.
- **V-xtaps** (`MS110D_AUTOPSY_TRUTH_XTAPS=1`): the gauge basis gains cursor±1 tap pairs (responses to x[u∓1], 2 bases each), soft-cancelled from the observation before the chains exactly like the banked straddle handling. Reads beyond-model response at the adjacent cursor lags — the 16QAM re-measurement of the B3.7 revival condition.
- Combined best-of, both specimens.

**Pre-committed reads** (diagnostic leg — the verdict is a ranked budget, not a kill): rank variants by Δ(total corpse coded errors) and Δ(mean truth-frame rms on the hot blocks), label budgets stated alongside (V0 6, split 12, xtaps 10 complex constants/frame — a variant that wins only by spending labels is noted as such). If V-offset moves totals ≥2×, the offset is adopted as an instrument correction and W1's numbers are re-banked under it. If no variant moves the hot blocks materially, the residual is priced as detection/pricing tail and the stop-and-reassess escalation writes the options. Outcome feeds W4's candidate scoping (per-symbol tracker granularity; whether candidate (a) needs the wider-tap cancellation form).

**Budget.** Rig/demod instrument-side edits (~150 lines), ~10 corpse runs (minutes each), no battery (nothing ships).

## Measurements (2026-07-31, [summaries/](summaries/); baselines = the banked W1 numbers)

| Variant (canonical, baseline **100**: b0:26 b2:10 b6:24 b9:40) | Total | Movement |
|---|---|---|
| V-offset −8/−4/+4 samples | 100 | error patterns **byte-identical** — at 100 audio samples per gain step a group-delay-scale shift perturbs the truth basis <0.3%; term (c) immaterial |
| V-split (12 constants) | 91 | b9 40→28, b6 24→27 — drift alone is minor |
| V-xtaps (10 constants) | 92 | b6 24→15, b9 40→28, **but clean b1 breaks 0→9** and b2 doubles |
| V-split × V-xtaps (20 constants) | **60** | b2 and b6 fully clean, b1 collateral gone, b9 40→28 — **the terms compose**; but b0 26→32 |
| MFB (W1b, raw samples) | **0** | — |

Disjoint (baseline **36**: b3:1 b6:35): combined variant → **29** — b6 35→7, b3 clean, **but previously-clean b4 breaks 0→22**. Same double-edged signature both families: widening the fitted post-FF cancellation model recovers hot blocks and injects error where the wider fit is imperfect (the §B3.3 label-cancellation lesson reproduced at truth level).

(V-offset +8 hit the box's known transient CLR startup crash; redundant given −8/−4/+4 identical.)

## Verdict — the ranked deficiency budget

1. **Detection representation beyond any widened post-FF gauge — dominant.** Even at 20 label-fitted constants per frame with cursor±1 exact cancellation, 60/29 errors persist (≈1.1E-4 / 5.4E-5) and the model-widening trade breaks fresh blocks as it fixes hot ones. The same channel knowledge through the raw-sample MFB scores **zero on every block**: the FF + sparse-chain + segment-pricing sandwich destroys ~1E-4-class information the raw samples retain.
2. **Within-frame gauge drift × adjacent-lag response — real, composed, secondary.** Neither term alone moves the floor; together they recover ~40% canonical. The B3.7 revival condition re-measured at 16QAM: beyond-{0, d, d±1} response **exists** — but the winning form is not per-bin FD MMSE (the banked negative stands); it is leaving the FF sandwich.
3. **Truth-lookup offset — nil** at group-delay scale.

**Consequence for the candidate ladder.** Candidate (a) as registered (a better tracker feeding the existing chains) inherits the ~1E-4-class sandwich floor — 10× mask, cannot gate; it is demoted. The leading architecture is the **MFB-form receiver**: per-symbol matched projection on an estimated channel trajectory with exact per-symbol whitened pricing, feeding the code — the W1b instrument run with estimates in place of truth. That makes **W3 (label-free trajectory estimation physics) the critical leg**, and gives W4 a cheap, sharp pricing instrument: the MFB re-run with degraded/estimated trajectories, mapping ceiling vs estimation NMSE. Variant iteration on the post-FF gauge stops here per the escalation discipline — further widening is tuning, and the budget says the floor lives elsewhere.
