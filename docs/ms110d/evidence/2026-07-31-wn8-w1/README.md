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

## Measurements (2026-07-31, instrument in this PR's demod/rig commits)

**Calibration lanes — all pass** ([corpse/](corpse/)):
- Seam passivity: the WN7 w0/b0 oracle guard-pin corpse re-run on the instrument build is **byte-identical** to the W0 pin (0 coded / 11c/0r/4v / oracle b5:15); hermetic suite **790/0/108** unchanged.
- Healthy corpses with truth injected: WN6 w0/b0 and WN13sp both **0 coded errors on every block, truth pass included** — the gauge map and lead-in/gain-rate alignment are right (a wrong gauge would break healthy blocks).
- WN7 w0/b0 with truth: **truth 0 on all 11 blocks — including b5, where the oracle leaves 15**. Truth time-variation with a six-constant gauge already beats the label-trained segment class on WN7's residual block.

**The WN8 specimens** (truth pass = single prior-free chain-BCJR pass, labels used ONLY for the 2·(1+echo+straddle) per-frame gauge constants; oracle line = the banked b39 instrument, same run):

| Specimen | Shipped | Oracle (banked ceiling) | Truth-injected | Clean blocks (truth) |
|---|---|---|---|---|
| canonical (channelSeed 509) | 269,237 (byte-exact b34/b39) | **496** = 9.2E-4 (92×) | **100** = 1.85E-4 (~18×) — b0:26 b2:10 b6:24 b9:40 | 7/11 |
| disjoint (channelSeed 10509) | 269,154 | **136** = 2.5E-4 (25×) | **36** = 6.7E-5 (~6.7×) — b3:1 b6:35 | 9/11 |

- First decodes remain coin-flip (24.0–24.7k/49,152 per block) — untouched, as expected: W1 measures Wall 1, not Wall 2.
- Truth-frame fit residuals ([corpse/truth-frames-*.log](corpse/), 64 frames × 11 blocks × 2 specimens): clean-block mean rms ≈ 7.0E-3 vs the ≈2E-3 AWGN floor — the sparse-tap gauge model leaves ~3.5× beyond-noise residual even where decoding is perfect; error blocks run ~40–65% hotter (b6 1.15E-2, b9 9.7E-3). The beyond-model response is real, priced by the spikeup machinery, and is exactly W2's decomposition target.
- Instrument caveats carried: the constant RX front-end group delay is uncompensated in the truth lookup (sub-percent trajectory error at 1 Hz — see the rig comment); read (b) (truth + SISO iteration) was not run — the single-pass numbers already decide the registered rule, and iteration can only improve them.

## Verdict

**The knowledge gap dominates Wall 1, but the sparse-model class still floors above 1E-4 canonical — the pre-committed rule fires W1b.** Replacing only the channel's time model (label-trained segment anchors → per-symbol-exact truth) moves the ceiling 5.0× / 3.8× on the two specimens and cleans 16 of 22 blocks outright, refuting "the QAM16 ceiling is immovable by this equalizer+chain class" *as a statement about the chains* — the immovability was the estimator's time model, not the detector. What remains (1.85E-4 / 6.7E-5, concentrated in 4 of 22 blocks with elevated fit residual) is beyond the {cursor, d, d±1} sparse gauge model itself. Per the registration: canonical ≥1E-4 → **W1b** (corpse-only full time-varying MAP on the exact effective channel, test-side, no hot-path constraints) decides whether that floor is the waveform's own or the sparse model's, before any candidate build. The banked-negative amendment this measurement earns ("immovable ceiling" → "immovable *given segment-anchored time variation*") lands with the W1b verdict, not before.
