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

## Measurements

(after the runs)
