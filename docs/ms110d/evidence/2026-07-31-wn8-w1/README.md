# W1 — true-channel injection bound (WN8 redesign program)

Registered 2026-07-31, after W0 banked ([../2026-07-31-wn8-w0/](../2026-07-31-wn8-w0/): PASS, no drift) and before any W1 code or run, per [wn8-program-plan.md](../../wn8-program-plan.md) §4.

## Registration

**Question.** With exact per-symbol channel knowledge injected into the shipped detection chain's model class — per-symbol (h₁[t], h₂[t]) in the post-FF gauge plus exact per-symbol noise pricing — what is the WN8 Poor corpse coded BER, (a) first-pass-only and (b) with the banked b34 QAM16 turbo wiring re-armed? This splits the 92×/25× oracle ceiling into knowledge-gap vs detector-class-gap, and answers whether perfect knowledge alone crosses the bootstrap basin (first-decode-with-truth vs the ~49.8% coin flip).

**Decision inputs.** [../2026-07-26-phase-b39-wn8-verdict/](../2026-07-26-phase-b39-wn8-verdict/) (ceiling 9.2E-4/2.5E-4; basin no-crossing); [../2026-07-25-phase-b34-wn8/](../2026-07-25-phase-b34-wn8/) (the observation-bound genie: 268,541 vs 269,237 — indistinguishable, and why that conflates estimator starvation with detector limits); the W0 baseline ([../2026-07-31-wn8-w0/](../2026-07-31-wn8-w0/)).

**Banked-negative clearances (closeout §5).** Not the oracle-labels pass re-dressed: the oracle derives estimates from labels *inside the pipeline's own model class* — this instrument derives them from the recorded channel truth, a different information source; the "reference, not a bound" caveat is carried (a good truth-injected number is necessary evidence, never sufficient). Not the FD-MMSE negative: no detector change — the exact chain BCJR runs unchanged; only its h₁/h₂/noiseVar inputs change. Not a bootstrap attempt: W1 measures bounds; nothing here ships.

**Mechanism.** `WattersonChannel.RecordGains`/`LastPathGains` records the exact per-path complex gain trajectories at 96 Hz (aligned to the channel span, lead-in excluded); `Ms110dChainBcjr.Equalize` already accepts per-position `h1`/`h2` spans and `noiseVarPerSymbol`. The missing piece is only the gauge map from wire-domain truth (gains at path delays, through TX SRRC ⊗ RX front-end ⊗ FF) to the chains' sparse {cursor, cursor+delay} model — fitted per block as a complex LS scale against the pipeline's own probe anchors, reusing the §B3.3 fade-crossing alignment the corpse rig already performs for its estimated-vs-truth comparison.

**Instrument + method.** Corpse-rig-side (`Ms110dTailAutopsy`) + a passive env-gated demod seam (`MS110D_AUTOPSY_TRUTH=1`-class), instrument-only — no shipped-path behavior when unset, proven by a bit-identical seam test (the `WriteGenie`/`OracleInfo` passivity precedent). Runs on both family specimens (canonical w0/b0 channelSeed 509; disjoint w0/b0 channelSeed 10509), plus calibration lanes: (i) seam disabled → corpse byte-identical to the W0-era plain corpse; (ii) truth injected on a healthy corpse (WN6 w0/b0 and WN13sp) → coded errors stay 0 (a wrong gauge map would break even healthy blocks — this is the new instrument's own calibration lane per the B0 rule); (iii) truth-injected WN7 w0/b0 → compare against its oracle b5:15 anatomy for sanity.

**Budget.** ~300–500 lines, all instrument-class; corpse runs only (minutes each); no battery (nothing ships in W1).

**Kill/proceed rule (pre-committed).**

- Truth-injected corpse BER ≤ **2E-5-class on both specimens** (≈2× mask; the specimens are single bursts — the read is block-level error structure, not a §5.3 rate) → Wall 1 is channel-knowledge-only; candidate (a) becomes primary; proceed W2/W3.
- ≥ **1E-4-class (10×)** on either specimen → the sparse-model detector class is itself deficient → run **W1b**: corpse-only full time-varying-MAP on the exact effective channel (test-side, no hot-path constraints, any cost).
- W1b also ≥ 10× → **registered infeasibility verdict for any receiver-only program — exit (iii)**; the program closes with the strengthened WN7-class verdict.
- Between 2× and 10×: written stop-and-reassess (the escalation rule), not more tuning.
- Basin read, recorded alongside either way: does first-decode-with-truth clear coin-flip decisively (report the per-block first-decode error counts vs the b39 anatomy)?

## Measurements

(after the run)
