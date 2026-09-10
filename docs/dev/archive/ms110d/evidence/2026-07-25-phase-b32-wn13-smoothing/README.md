# Phase B3.2 — WN13 fade-cluster specimen: probe-anchor trajectory smoothing (2026-07-25) — measured NEGATIVE, reverted

The B3.1 attribution left one tracking-deficit point where the B3.2 anchor-ridge lever is measured-forbidden: the WN13-disjoint fade-cluster specimen (w3/b5, channelSeed 3015514, 8,392 coded errors in one block; genie 12). U=256 frames are 120 ms — more causal solve memory is catastrophic lag (WN13 at 4× ridge: 4.9E-2). This directory banks the mechanism autopsy and the A/B of the candidate fix — non-causal ±1 probe-anchor smoothing on the fading path's tap trajectory — which the measurements REFUTED as a sufficient fix and showed harmful as a default. The implementation is preserved in this PR branch's history (`feat(ms110d): B3.2b …` commit) and reverted in the next commit; only the autopsy's per-block LLR-stats instrument landed. The specimen is re-attributed: probe-information-limited, needs code-domain soft feedback — the B3.3 detector program.

## The measured mechanism (corpse pair, `corpse-normal/` + `corpse-genie/`)

1. **Reproduction**: normal 8,392 coded errors (46,564/360,448 uncoded, 15 collapses, turbo 10c/1r — the dead block is the revert); genie 12 (38,588 uncoded, 11c/0r). All coded errors in block 8 (bits 147,456–165,887) in BOTH runs.
2. **The excess is flat, not fade-shaped**: normal-vs-genie uncoded SER by fade phase — deep (gain<0.25) 0.523 vs 0.487 (+0.035), edge 0.281 vs 0.243 (+0.038), healthy 0.146 vs 0.107 (+0.039). The same flat per-probe solve-noise tax the WN2 autopsy measured, paid everywhere — NOT fade-edge lag (which would concentrate in edge/deep).
3. **No confidence dishonesty**: median |y| on errored symbols is identical between the pair (deep 0.557 vs 0.560, edge 0.572 vs 0.568, healthy 0.682 vs 0.665). Unlike WN2, this is a raw-SER cliff problem: block 8 dies at 0.360 while genie's 0.313 on the same block decodes to 12 errors — the coded cliff sits between them, and shaving the flat tax is the whole game.
4. **The solve noise measured directly**: per-frame tap change (common rotation removed) — normal median 0.0495, genie 0.0363. The genie number IS the true channel movement per 120 ms frame (its estimation rows are perfect), so the normal per-probe solve carries noise ≈ √(0.0495²−0.0363²) ≈ 0.034/frame — comparable to the true movement itself. The anchor sequence has ~0 dB estimation SNR at frame rate.
5. **Turbo cannot save it**: the dead block's first decode is 45% wrong, so the re-encoded turbo training is garbage there (the measured 1 revert). The fix must land in the first-pass LLRs.

## The candidate: §B3.2b non-causal trajectory-anchor smoothing

The fading path (§B2.1a) interpolates the tap base between bracketing per-probe solves. Each anchor is one noisy solve; the trajectory inherits its full noise. But the anchors are a TIME SERIES with ≈0 dB SNR, and the block-buffered architecture has the whole block's anchors in hand at `FinishBlock` — a symmetric ±1 average over rotation-aligned neighbours cuts anchor noise ~√3 with ZERO group delay (the causal ridge's fatal flaw at U=256 was lag; a symmetric window has none). A 1 Hz fade component sampled at 120 ms loses only ~18% amplitude through a uniform 3-point window, and the weight is tunable below uniform.

Implementation (first pass untouched — bit-identical when the knob is off):

- Per frame, record the per-probe anchors (start/end tap snapshots), the fresh-solve flag, and the carrier/timing state at data-equalization time (`_tau`, `_omega`, `_thetaBase` — three scalars that make a retrospective `FillWindow` read BIT-IDENTICAL to the first pass, no staleness).
- At `FinishBlock`, before the first decode, on fading-latched PSK bursts: rotation-align each anchor's ±1 neighbours (align phase via Σ aⱼ·āₙᵦ; a neighbour across a fresh-solve discontinuity or with negligible correlation is excluded), form ã = (a + w·Σa′ₙᵦ)/(1 + w·n), re-equalize every fading frame's data span down the smoothed trajectory as a PURE detection pass (no solves, no RLS, no training rows; decision history re-seeded from the preceding probe's known tail), and overwrite that frame's slice of the block LLRs. Frames that ran the non-fading 3-pass path keep their first-pass LLRs.
- Knob: `Ms110dDemodOptions.TrajectorySmoothing` (w ∈ (0, 1]; null = off), env `MS110D_MASK_TRAJ_SMOOTH` / `MS110D_AUTOPSY_TRAJ_SMOOTH` — same report-only rule as the λ/ridge knobs until the full-budget A/B lands a default.
- QAM16 is excluded (same gate as turbo; WN8 is measured detector-limited — genie ≈ normal — so smoothing buys nothing there and B3.4 owns it).

## Results — the lever is real but ~10× too small, and it costs the healthy tail

Bit-identity first: with the knob unset the corpse reproduces 8,392 coded errors bit-exactly (same first/last error), before and after the revert.

**Corpse sweep** (`smoothing-ab/summary-*.txt`; coded errors on the specimen): off 8,392 → w=0.25: 7,854, w=0.5: 7,987, w=1.0: 8,081, w=0.001 (pure pass, raw anchors — the pass-handicap control): 8,340. The dead block stays ~45% wrong in every variant — the deltas are shuffles inside a hopeless block, plus 4 NEW errors in a previously clean block at w=0.25.

**Per-block LLR stats** (`corpse-pair/autopsy-llrstats-*.csv`, `smoothing-ab/llrstats-*.csv`; the dead block 8, 32,768 uncoded bits):

| pass | errBits | wrong-sign mass | right-sign mass | wrong/right |
|---|---|---|---|---|
| first pass | 6,683 | 5,120 | 41,064 | 0.1247 |
| pure pass, raw anchors (w=0.001) | 6,908 | 4,811 | 39,587 | 0.1215 |
| smoothed w=1.0 | 6,628 | 4,455 | 39,173 | 0.1137 |
| **genie** | **5,812** | **4,377** | **42,523** | **0.1029** → decodes, 12 errors |

Three findings. (1) The genie itself sits AT the cliff edge (12 errors) — there is no margin for a partial fix. (2) Smoothing closes ~50% of the confidence-ratio gap but essentially none of the detection gap (errBits 6,628 vs genie 5,812) — the anchors' curvature loss cancels most of the √3 noise gain at 120 ms spacing. (3) The pure pass is 3–9% worse than the first pass in raw errBits on every healthy block — the first pass's intra-frame gated RLS carries real value the re-equalization pass lacks.

**Mask-level A/B** (`smoothing-ab/mask-smoke-ab.log`, WN13 Poor disjoint 1.6M-bit smoke, labelled): baseline 14 errors → smoothed w=1.0: 22 errors. The lever HURTS the benign tail (the healthy-block errBits cost above, at mask level).

**Where the remaining gap lives**: the genie's advantage over the smoothed pass is clean DATA-row estimation — its RLS/DD rows read the clean stream mid-frame, which no probe-side lever can reach at U=256's probe density (3 probes per 1 Hz coherence time, each a ~0 dB estimate). Reliable mid-frame channel information at local SER 0.36 requires known (or soft-known) data symbols — i.e. code-domain feedback. The existing turbo pass is exactly that but hard-decision-gated: it trains on the re-encoded FIRST decode, which is 45% garbage on this block (the measured 1 turbo revert). The road is a soft-input detector iteration (SISO outer decode driving the chain-BCJR channel re-estimation) — the B3.3 program, which the WN7 genie result (4.52E-1 ≈ normal, detector-limited under PERFECT observation) already owns.

## Verdict

- ±1 non-causal anchor smoothing: REFUTED as the specimen fix (closes ~50% of confidence, ~0% of detection, genie has no margin to spare), and measured-harmful as a default (14 → 22 on the disjoint smoke). Not landed; implementation preserved in branch history.
- The specimen moves from "tracking deficit (B3.2 reachable)" to **probe-information-limited: needs code-domain soft feedback — B3.3**. This also re-frames the B3.1 attribution: "tracking-reachable" at U=256 meant reachable by ESTIMATION improvements generally; the probe-side subset of those is exhausted by this A/B.
- Landed: the autopsy per-block LLR-stats CSV (first-pass uncoded SER + signed LLR mass — the confidence-vs-cliff instrument, always on).
- WN13 Poor's gate therefore stays blocked on B3.3 regardless of the benign tail (the specimen class ≈ once per 6M bits × 8k errors each; no §5.3 budget survives one).

## Files

- `corpse-pair/` — normal + genie corpses (biterrs/frames/gains/summary/llrstats; post-revert binary, bit-identical to pre-change)
- `smoothing-ab/` — sweep summaries (off/0.001/0.25/0.5/1.0), llrstats at w=0.001 and w=1.0, the labelled mask smoke A/B log + censuses
- `compare_pair.py` — the pair-comparison analysis (fade-phase decomposition, per-block SER, |y| stats)
