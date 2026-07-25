# Phase B3.2 — WN13 fade-cluster specimen: probe-anchor trajectory smoothing (2026-07-25)

The B3.1 attribution left one tracking-deficit point where the B3.2 anchor-ridge lever is measured-forbidden: the WN13-disjoint fade-cluster specimen (w3/b5, channelSeed 3015514, 8,392 coded errors in one block; genie 12). U=256 frames are 120 ms — more causal solve memory is catastrophic lag (WN13 at 4× ridge: 4.9E-2). This directory banks the mechanism autopsy and the fix: non-causal ±1 probe-anchor smoothing on the fading path's tap trajectory.

## The measured mechanism (corpse pair, `corpse-normal/` + `corpse-genie/`)

1. **Reproduction**: normal 8,392 coded errors (46,564/360,448 uncoded, 15 collapses, turbo 10c/1r — the dead block is the revert); genie 12 (38,588 uncoded, 11c/0r). All coded errors in block 8 (bits 147,456–165,887) in BOTH runs.
2. **The excess is flat, not fade-shaped**: normal-vs-genie uncoded SER by fade phase — deep (gain<0.25) 0.523 vs 0.487 (+0.035), edge 0.281 vs 0.243 (+0.038), healthy 0.146 vs 0.107 (+0.039). The same flat per-probe solve-noise tax the WN2 autopsy measured, paid everywhere — NOT fade-edge lag (which would concentrate in edge/deep).
3. **No confidence dishonesty**: median |y| on errored symbols is identical between the pair (deep 0.557 vs 0.560, edge 0.572 vs 0.568, healthy 0.682 vs 0.665). Unlike WN2, this is a raw-SER cliff problem: block 8 dies at 0.360 while genie's 0.313 on the same block decodes to 12 errors — the coded cliff sits between them, and shaving the flat tax is the whole game.
4. **The solve noise measured directly**: per-frame tap change (common rotation removed) — normal median 0.0495, genie 0.0363. The genie number IS the true channel movement per 120 ms frame (its estimation rows are perfect), so the normal per-probe solve carries noise ≈ √(0.0495²−0.0363²) ≈ 0.034/frame — comparable to the true movement itself. The anchor sequence has ~0 dB estimation SNR at frame rate.
5. **Turbo cannot save it**: the dead block's first decode is 45% wrong, so the re-encoded turbo training is garbage there (the measured 1 revert). The fix must land in the first-pass LLRs.

## The fix: §B3.2b non-causal trajectory-anchor smoothing

The fading path (§B2.1a) interpolates the tap base between bracketing per-probe solves. Each anchor is one noisy solve; the trajectory inherits its full noise. But the anchors are a TIME SERIES with ≈0 dB SNR, and the block-buffered architecture has the whole block's anchors in hand at `FinishBlock` — a symmetric ±1 average over rotation-aligned neighbours cuts anchor noise ~√3 with ZERO group delay (the causal ridge's fatal flaw at U=256 was lag; a symmetric window has none). A 1 Hz fade component sampled at 120 ms loses only ~18% amplitude through a uniform 3-point window, and the weight is tunable below uniform.

Implementation (first pass untouched — bit-identical when the knob is off):

- Per frame, record the per-probe anchors (start/end tap snapshots), the fresh-solve flag, and the carrier/timing state at data-equalization time (`_tau`, `_omega`, `_thetaBase` — three scalars that make a retrospective `FillWindow` read BIT-IDENTICAL to the first pass, no staleness).
- At `FinishBlock`, before the first decode, on fading-latched PSK bursts: rotation-align each anchor's ±1 neighbours (align phase via Σ aⱼ·āₙᵦ; a neighbour across a fresh-solve discontinuity or with negligible correlation is excluded), form ã = (a + w·Σa′ₙᵦ)/(1 + w·n), re-equalize every fading frame's data span down the smoothed trajectory as a PURE detection pass (no solves, no RLS, no training rows; decision history re-seeded from the preceding probe's known tail), and overwrite that frame's slice of the block LLRs. Frames that ran the non-fading 3-pass path keep their first-pass LLRs.
- Knob: `Ms110dDemodOptions.TrajectorySmoothing` (w ∈ (0, 1]; null = off), env `MS110D_MASK_TRAJ_SMOOTH` / `MS110D_AUTOPSY_TRAJ_SMOOTH` — same report-only rule as the λ/ridge knobs until the full-budget A/B lands a default.
- QAM16 is excluded (same gate as turbo; WN8 is measured detector-limited — genie ≈ normal — so smoothing buys nothing there and B3.4 owns it).

## Results

(filled in as the sweep and validation batteries run)
