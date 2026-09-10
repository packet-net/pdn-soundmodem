# Phase B3.3 — WN7 mechanism + the oracle-labels turbo ceiling (2026-07-25)

B3.3 opens on the detector program the B3.1 genie map assigned it: WN7 Poor (+19 dB, 8PSK) fails at 4.59E-1 with the genie ≈ normal, WN6 and the WN13 fade-cluster specimen queued behind the same soft-feedback direction. This directory banks (1) the WN7 corpse-pair mechanism autopsy and (2) the first measurement of the NEW oracle-labels turbo instrument — the ceiling a CONVERGED soft-feedback turbo could reach with the existing chain-BCJR channel model. The oracle splits B3.3 into two measured fronts: labels (soft feedback — sufficient for the WN13 specimen, measured 8,392 → 0) and model fidelity (WN7's binding constraint even under perfect labels).

## The WN7 mechanism (corpse pair `corpse/`, w0/b0, channelSeed 508; census `census/`)

Census on current main: all 8 bursts uniformly dead — 172,691–195,333 coded errors per 405,472-bit burst, first error ≈ bit 0, uncoded 1.67E-1, 9–15 §B2.1c collapses per burst, turbo 1c/87r. Burst-for-burst ≈ the B3.1 genie census (seed 508: 89,559 uncoded errors normal vs 86,325 genie).

What the corpse pair measured, hypothesis by hypothesis:

1. **Whole-frame phase smear carries everything, still, and it is genie-immune**: 564/704 frames are donuts by the B1 8th-power concentration statistic (genie 549), holding 99% of errors at SER 0.483 inside vs 0.021 outside. Symbol SER 0.391 normal / 0.378 genie.
2. **Not fade-shaped**: deep fades (P<0.25) hold only ~7% of errors; SER is 0.32 even at P>1.5. Worst bin is comparable-power paths (|g1|/|g0| ∈ [1,2]: SER 0.523) — notch depth, not signal shortage.
3. **Not anchor corruption**: signed-phase-error correlation across the probe (tail of frame f vs head of f+1) is r = 0.19 (genie 0.31) — probes DO reset the reference. Kills the B1 "anchored solve cannot re-lock" residue as the current mechanism.
4. **Not chord/sagitta error and not DFE error propagation**: SER vs in-frame position is flat (0.374–0.405 over u = 0…255, head ≈ mid ≈ tail), where sagitta error would vanish at the anchors and propagation would grow from the head. Error run-lengths modest (28% of errors in runs ≥ 5), identical under genie.
5. **The number that explains everything**: median |Δθ| = phase(y·conj(ref)) over ALL symbols is 15–16°, flat in u, genie-identical. The worst frames wander ±40–100° within 120 ms (two-path beating between anchors). At 8PSK's ±22.5° sector budget, a ±20°-scale phase spread converts directly to SER ≈ P(|Δθ| > 22.5°) ≈ 0.39. QPSK's ±45° budget survives the same spread — which is why the identical channel machinery holds WN13 at mask and loses WN7 by 4½ decades.

Mechanism statement: **the first-pass hard-DFE detection on the probe-anchored trajectory leaves a residual complex perturbation (MMSE noise-enhancement + residual ISI at two-path notches + intra-frame movement between 120 ms anchors) whose phase projection is ~±20°; 8PSK's ring density pays full price for it everywhere, independent of estimation quality.** Detector-side work is confirmed as the only road — but see the oracle for how far the CURRENT detector model can carry it.

## The oracle-labels instrument (§B3.3, landed this PR)

`Ms110dDemodulator.OracleInfo` + `OracleBlockLlrs`: when the hook is set (autopsy rig: `MS110D_AUTOPSY_ORACLE=1`), FinishBlock runs ONE extra `TurboReequalize` trained on the TRUE info bits after the normal pipeline finishes — perfect labels through the existing estimation machinery (per-frame FF batch-LS re-solve, 4-segment per-position h1, scrambler-exact single-lag echo, residual noiseVar, chain BCJR). The shipped decode is untouched; bit-identical when unset (re-verified: corpse reproduces 172,691/first 139/last 405468 exactly). Oracle LLRs land in the llrstats CSV as pass `oracle`; per-block oracle coded errors land in the summary. This measures the CEILING of any soft-feedback (SISO outer decode → BCJR re-estimation) iteration: converged soft feedback can at best approach oracle labels.

## Oracle results — the program-defining split

**WN13 fade-cluster specimen (w3/b5, channelSeed 3015514, `wn13-oracle/`): oracle decodes EVERY block to 0 coded errors — including dead block 8** (normal 8,392, genie 12, oracle 0). With true mid-frame labels, the existing chain-BCJR turbo model repairs the block completely. The B3.2 conclusion ("probe-information-limited, needs code-domain soft feedback") now has its existence proof: the information the detector lacks is exactly what labels supply, and the model is already good enough there. Soft-feedback turbo is a SUFFICIENT lever for the specimen class if the iteration converges from a 45%-wrong first decode.

**WN7 (w0/b0): oracle labels are a ~3× errBits lever but the model saturates far above the cliff.** Per-block first-pass → oracle errBits (of 49,152): 11,010 → 3,465 (b4), 10,629 → 3,307 (b5), 3,321 → 1,759 (b0); wrong-sign LLR-mass ratio 0.028–0.145 → 0.001–0.008 (the oracle stream is HONEST — miscalibration is not the residual). But coded errors per block remain 73–3,072 (burst total ≈ 15,100 ≈ BER 3.7E-2 vs first-pass 172,691): uncoded 3.6–7.5% errBits sits at/beyond the rate-3/4 K7 cliff. **Genie+oracle ≈ oracle** (b4: 2,921 vs 2,968) — additive noise on the estimation reads contributes nothing. The WN7 residual under perfect labels and perfect reads is the CHANNEL MODEL: frame-static FF solve + 27 ms h1 segments + one discrete echo tap against a channel that moves tens of degrees inside both granularities.

**WN6 (w0/b0, QPSK 3/4 @ +14, `wn6/`): normal 7,572 → oracle 75 coded errors** (b5:49, b7:14, b9:7, b0:5, rest zero; errBits 6.4–9.6% → 1.2–3.1%, wrong-mass ratio ≈ 0.001). WN6 is essentially labels-limited — it joins WN13 on the soft-feedback front, with only a small model tail.

**Model-residual split on the WN7 oracle (`seg-sweep/`, temporary edits, not committed)**: oracle burst-total coded errors walk 15,136 (baseline: 4 h1 segments, frame-constant h2) → 13,675 (16 h1 segments: −10%) → 11,415 (16 segments for h1 AND h2: −25% cumulative). h1 granularity alone is NOT the binding constraint; time-varying h2 is a real second lever; together they still leave ~4× too many errBits for the rate-3/4 cliff. The per-frame echo-state diagnostic (`turbo-echo-state-seg16-segh2.log`) then locates the floor: **65% of frames have h2 zeroed by the 0.04·|h1|² significance floor** (pre-floor |h2|/|h1| distribution: 235 frames < 0.1, 220 in 0.1–0.2, 241 in 0.2–0.3) — the chain BCJR degenerates to a per-symbol matched filter on two-thirds of WN7 — and the surviving echo picks cluster at **delay 5 (374 frames) AND delay 10 (251 frames)**: the FF's inversion of the 2 ms (4.8-symbol) second path leaves a truncated geometric echo TRAIN (lags d, 2d, …) plus T/2 fractional sidelobes, which a single-lag model cannot represent. Mean BCJR noiseVar 0.045 at |h1| = 0.847 → **effective detection SNR ≈ 9.5 dB at a +19 dB operating point** — 13× the additive noise. The model residual IS the ~15° phase spread of the mechanism autopsy.

## Consequences for B3.3

1. **Soft-feedback turbo (SISO outer decode driving the chain-BCJR re-estimation) is the WN13-specimen lever, full stop** — oracle 0 errors. Convergence from a garbage first decode is the engineering risk, not the ceiling.
2. **WN7 additionally needs model fidelity** — with today's model even perfect labels leave 3.7E-2. The split measurement (segments sweep) directs whether that work goes into h1 granularity, the echo model, or the FF solve.
3. The llrstats `oracle` pass + per-block oracle error lines are the acceptance rig for the soft-feedback implementation: an iteration that works should walk each block's errBits from the `first` line toward the `oracle` line.

## Files

- `census/` — normal WN7 Poor census on current main (mask log + per-burst CSVs)
- `corpse/` — w0/b0 corpse set: normal, genie, oracle, genie+oracle (symbols/biterrs/frames/gains/llrstats/summary)
- `wn13-oracle/` — the B3.2 specimen under oracle labels (summary: 0 errors on all 11 blocks)
- `wn6/` — WN6 w0/b0 normal + oracle pair
- `analyze_wn7.py` — pair analysis (per-block SER, in-frame profile, donut stat, path-ratio/fade conditioning, run lengths, |y|)
- `phase_traj.py` — per-symbol phase-error trajectory analysis (head/mid/tail, probe-reset correlation, worst-frame profiles)
