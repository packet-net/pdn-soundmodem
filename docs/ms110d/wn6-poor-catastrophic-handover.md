# WN6 Poor Channel Catastrophic Failure — Handover

## Problem

WN6 (QPSK, rate 3/4, U=256, K=32) produces **BER 0.22** on the Poor channel
(2-path Rayleigh, 0/2 ms, 1 Hz Doppler) at 14 dB SNR. This is catastrophic —
essentially random output. The failure is **pre-existing** (present on commit
`1a2615a` before the flat-channel turbo gate was added).

## Evidence

```
MS110D_MASKS_POOR=1 MS110D_MASK_BITS=400000 dotnet test \
  --filter "FullyQualifiedName~Poor_Channel_Smoke"
```

Result (both with and without the turbo gate fix):

```
POOR (smoke) WN6 @ +14 dB: 540,608 bits, 120,348 errors, 2 bursts
  (0 acquisition failures), 179 s simulated — BER 2.23E-001
```

The test acquires (0 acquisition failures) and produces output, but the output
is ~22% wrong. This is not a marginal SNR issue — 14 dB is well above what
QPSK rate-3/4 needs, and the same channel model works for WN4 (BPSK) at 10 dB
with BER 1.4E-5.

## What Is NOT the Cause

- **Not the flat-channel turbo gate** (commit `0b9ca98`): failure reproduces
  identically on the parent commit `1a2615a` with the gate reverted.
- **Not acquisition**: 0 acquisition failures, 2 bursts decoded.
- **Not the channel model**: WN4 (BPSK, same Poor channel) works at 10 dB.
- **Not the AWGN path**: WN6 decodes bit-exact on AWGN at 9 dB (mask SNR)
  and at 15 dB (loopback test `Moderate_Awgn_Well_Above_Mask_Decodes_Cleanly`).

## Likely Root Cause Candidates

### 1. QPSK turbo BCJR path interaction (most likely)

The turbo's BCJR path is gated on `mode.Modulation == Bpsk && mode.U > 48`.
WN6 is QPSK, so BCJR never runs — only the DFE fallback path. But the DFE
re-solve still happens (line 1347: `SolveTraining` with expected QPSK symbols).

On Poor, the QPSK expected symbols from a noisy first-pass decode may be
wrong enough to corrupt the re-solve, producing garbage taps → garbage LLRs →
garbage Viterbi decode. The turbo then iterates up to 5 times, each iteration
feeding garbage back.

**Test**: disable the turbo entirely for QPSK (add `Ms110dModulation.Qpsk`
to the exclusion at line 1231) and re-run. If WN6 Poor passes, the turbo
DFE fallback is the culprit.

### 2. QPSK LLR computation in turbo path

The turbo's DFE fallback calls `PushLlrs(descrambled, mode.Modulation)` which
computes QPSK LLRs as `2*(Re+Im)` and `2*(Re-Im)`. If the descrambled symbols
are rotated (wrong phase tracking after re-solve), the LLR axes are swapped
or inverted → systematic bit errors.

**Test**: dump the turbo's per-frame LLRs for WN6 Poor and compare to
first-pass LLRs. Look for axis rotation or sign inversion.

### 3. Scrambler state divergence

The turbo rebuilds expected symbols using `_scrambler.Reset()` + `NextPsk()`.
If the scrambler state doesn't match the first pass (e.g., different number
of calls due to QPSK's 2-bits-per-symbol), the expected symbols are wrong →
re-solve trains on garbage.

**Test**: verify that the turbo's expected QPSK symbols match the first-pass
transmitted symbols (re-encode decoded info and compare to known tx).

### 4. Frame geometry / interleaver mismatch for WN6

WN6 has U=256, K=32, 128 frames/block. The interleaver block is large
(256*128 = 32768 symbols). If there's an off-by-one in the turbo's frame
loop or bit indexing for QPSK (2 bits/symbol → 512 bits/frame), the expected
symbols are misaligned → re-solve is garbage.

**Test**: verify `_blockFrameChips.Count == _il.Frames` for WN6 and that
the turbo's `bit` counter advances by exactly `U * bitsPerSymbol` per frame.

## Suggested Investigation Order

1. **Quick check**: disable turbo for QPSK only, re-run WN6 Poor smoke.
   If it passes → turbo DFE fallback is the culprit (candidates 1-3).
2. **If turbo is the culprit**: add debug output in TurboReequalize for WN6
   Poor — dump per-frame MSE between expected and received symbols. High MSE
   means expected symbols are wrong (candidate 3 or 4). Low MSE means the
   re-solve is fine but LLR computation is wrong (candidate 2).
3. **If turbo is NOT the culprit**: the issue is in the first-pass QPSK
   equalization on fading channels. Check RLS tracking stability for QPSK
   (the `rlsWeight` logic at line 803-804 uses tap rotation to detect
   fading — maybe QPSK tap rotation is below the 0.005 rad threshold).

## Files

- `src/Packet.SoundModem/Ms110d/Ms110dDemodulator.cs`
  - Turbo gate: line ~1231
  - TurboReequalize: line ~1305
  - PushLlrs (QPSK path): line ~1039
  - RLS weight / fading detection: line ~803
- `tests/Packet.SoundModem.Tests/Ms110d/Ms110dMaskTests.cs`
  - Poor_Channel_Smoke: line ~113
- `tests/Packet.SoundModem.Tests/Channel/WattersonChannel.cs`
  - Poor channel definition: `WattersonChannel.Poor`

## Reproduction

```bash
MS110D_MASKS_POOR=1 MS110D_MASK_BITS=400000 dotnet test \
  --filter "FullyQualifiedName~Poor_Channel_Smoke"
```

Expected: WN6 fails with BER ~0.22. WN4 and WN8 results vary (WN4 is
marginal at 400k bits due to Poisson confidence; WN8 status unknown).

## Context

This was discovered while validating the flat-channel turbo gate
(commit `0b9ca98`). The gate correctly skips turbo on AWGN and correctly
runs turbo on Poor for BPSK modes. The WN6 Poor failure is orthogonal —
it affects QPSK on fading channels regardless of the gate.
