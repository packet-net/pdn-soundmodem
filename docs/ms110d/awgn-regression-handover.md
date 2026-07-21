# AWGN Regression Handover

## Problem

The turbo equalization (BCJR) degrades AWGN performance. With the turbo enabled,
8/10 AWGN mask gates fail. The turbo's per-frame re-solve produces slightly different
DFE taps than the first pass, giving different LLRs that cause the Viterbi decoder to
make worse decisions on flat channels.

## Root Cause

The first pass trains the DFE using the **probe** (K=32 known symbols per frame).
The turbo re-solves using **expected symbols** (U=96 decoded symbols per frame).
On AWGN, both training sets are correct, but they produce different Gram matrices
→ different tap solutions → different LLRs → Viterbi decodes differently (worse).

The first-pass decode on AWGN is already perfect (0 errors). The turbo cannot improve
on perfect, and any perturbation to the LLRs introduces errors.

## What Was Tried (All Failed)

| Approach | Why it failed |
|----------|---------------|
| h2 threshold (|h2|² > 0.04) | h2 noise on AWGN (~0.01) overlaps with Poor (~0.09). False positives. |
| LLR magnitude threshold (avg |LLR| > N) | AWGN avg |LLR| ≈ 4 for BPSK, ≈ 2.8 for QPSK. Overlaps with Poor. No clean separator. |
| LLR confidence comparison (turbo vs first-pass) | BCJR LLRs use different scale (1/noiseVar) than first-pass (4*Re). Comparison unreliable. |
| No re-solve (use first-pass taps) | Breaks loopback — per-frame taps are necessary for multi-frame blocks. |
| Very high regularization (100.0) | Breaks loopback — taps too conservative for some modes (WN7 K=9). |
| Residual variance detection | AWGN residual ≈ noise power ≈ 0.1 at +10 dB. Overlaps with Poor. |

## Why Detection Is Hard

On AWGN, the DFE already cancels all ISI (flat channel). The residual after equalization
is pure noise. On Poor, the DFE also cancels most ISI (it's designed for the 2-path channel).
The residual is noise + small residual ISI. The two residuals are statistically similar —
no metric reliably separates them at the per-frame level.

## Proposed Solutions (Untried)

### Option A: Per-frame h1 variance across frames (Recommended)

On AWGN, h1 is constant across all frames in a block (flat channel doesn't change).
On Poor, h1 varies across frames (Rayleigh fading). Accumulate h1 estimates across
all frames in TurboReequalize, compute variance. If var(h1) < threshold → AWGN →
revert to first-pass decode.

Implementation:
- In TurboReequalize, store per-frame h1Avg in an array
- After all frames: compute var(h1 across frames)
- If var < 0.001: revert to first-pass LLRs and re-decode
- Threshold tuning: AWGN var ≈ 0 (only noise in estimate), Poor var ≈ 0.01-0.1

Risk: on very slow fading (deep fade lasting entire block), h1 might be constant
across frames even on Poor. Mitigation: also check h2 variance.

### Option B: Two-pass turbo with probe-based first iteration

First turbo iteration uses probe-based training (same as first pass → same taps →
same LLRs → same decode). Second iteration uses data-based training (refines on Poor).
On AWGN: second iteration gives same result as first (no change). On Poor: second
iteration improves.

Implementation:
- Iteration 0: solve with probe (same as first pass), BCJR with probe-based h1/h2
- Iteration 1+: solve with expected symbols, BCJR with data-based h1/h2
- On AWGN: iteration 0 gives same decode as first pass. Iteration 1 gives same
  expected symbols → same h1/h2 → same decode. No change.
- On Poor: iteration 1 uses better expected symbols → better h1/h2 → better decode.

Risk: complex implementation. The probe-based h1/h2 might not capture within-frame
variation (probe is at frame boundary, not spread across data block).

### Option C: Accept AWGN margin loss

The AWGN mask SNRs have ~11 dB margin above the code's minimum Eb/No. A small
degradation (0.5-1 dB) from the turbo might still pass with adjusted thresholds.
Check if the AWGN failures are at the mask SNR (no margin) or if there's headroom.

## Current State

- Commit: `27ea8e7` (main branch)
- Poor WN4: BER 1.13E-5 (at target)
- AWGN: 8/10 fail
- Loopback: 40/40 pass
- The turbo is always enabled (no detection/gating)

## Files

- `src/Packet.SoundModem/Ms110d/Ms110dDemodulator.cs` — TurboReequalize method (line ~1300)
- `src/Packet.SoundModem/Ms110d/Ms110dBcjr.cs` — BCJR MAP equalizer
- `tests/Packet.SoundModem.Tests/Ms110d/Ms110dMaskTests.cs` — mask test definitions
