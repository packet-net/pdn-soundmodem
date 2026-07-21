# AWGN Regression Handover

## Status: RESOLVED (commit `0b9ca98`)

The AWGN regression is fixed via Option A (flat-channel detection using
var(|tap[0]|) across frames). All 9 AWGN mask WNs decode bit-exact at mask
SNR. Loopback 40/40, unit tests 93/93.

## Problem (Historical)

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

## Solution Implemented (Option A)

Per-frame |tap[0]| variance across the interleaver block detects flat channels:
- AWGN: var(|tap[0]|) < 0.004 (only noise in estimate, channel is constant)
- Poor: var(|tap[0]|) > 0.005 (Rayleigh fading moves the channel across frames)
- Clean separation validated across all mask SNRs and 20 Monte Carlo trials

Implementation: `_blockTap0Mag` list accumulates |tap[0]| per frame during first
pass. `IsFlatChannel()` computes variance; if below threshold, the turbo loop is
skipped entirely, preserving the optimal first-pass decode.

## What Was Tried Before (All Failed)

| Approach | Why it failed |
|----------|---------------|
| h2 threshold (|h2|² > 0.04) | h2 noise on AWGN (~0.01) overlaps with Poor (~0.09). False positives. |
| LLR magnitude threshold (avg |LLR| > N) | AWGN avg |LLR| ≈ 4 for BPSK, ≈ 2.8 for QPSK. Overlaps with Poor. No clean separator. |
| LLR confidence comparison (turbo vs first-pass) | BCJR LLRs use different scale (1/noiseVar) than first-pass (4*Re). Comparison unreliable. |
| No re-solve (use first-pass taps) | Breaks loopback — per-frame taps are necessary for multi-frame blocks. |
| Very high regularization (100.0) | Breaks loopback — taps too conservative for some modes (WN7 K=9). |
| Residual variance detection | AWGN residual ≈ noise power ≈ 0.1 at +10 dB. Overlaps with Poor. |

## Open Issue: WN6 Poor Catastrophic Failure

WN6 (QPSK 3/4) produces BER 0.22 on Poor channel at 14 dB. Pre-existing,
unrelated to the turbo gate. See `docs/ms110d/wn6-poor-catastrophic-handover.md`.

---

## IMPORTANT: Test Runtime & Completion Criteria

**Read this section before starting any work on this codebase.**

### The statistical mask tests are SLOW

The mask tests simulate real audio signal processing sample-by-sample at 9600 Hz.
A single mask point (one WN at one SNR) requires ≥3×10⁶ payload bits, which means
minutes of simulated signal per point. The full suite takes **hours**:

| Suite | Env var | Points | Time (approx) |
|-------|---------|--------|---------------|
| AWGN mask | `MS110D_MASKS=1` | 10 WNs | 30-60 min |
| Poor channel mask | `MS110D_MASKS_POOR=1` | 10 WNs | 60-90 min |
| Static WID2 | `MS110D_MASKS=1` | 1 | 10 min |
| Doppler offset | `MS110D_MASKS=1` | 3 | 15 min |
| Loopback | (none) | 40 | ~1 min |
| Unit tests | (none) | 93 | ~30 s |
| Flat-channel gate | (none) | 10 | ~2 min |

You can reduce the bit budget for smoke runs: `MS110D_MASK_BITS=500000` (but note
that <400k bits with 0 errors won't clear the Poisson confidence bound).

### A task is NOT complete until the full statistical masks pass

Quick diagnostic tests (loopback, unit tests, `Ms110dFlatChannelGateTests`) provide
fast functional validation but do NOT constitute proof of correctness. The statistical
mask gates are the ground truth — they exercise the modem at the exact spec SNRs with
enough bits to bound BER with 95% confidence.

**Before declaring any equalizer/demodulator change "done":**

1. Run loopback (40/40) and unit tests (93/93) — fast, catches regressions.
2. Run `Ms110dFlatChannelGateTests` — fast, catches AWGN/Poor gate issues.
3. Run the full AWGN mask suite (`MS110D_MASKS=1`, default 3M bits) — MUST be 10/10.
4. Run the Poor channel mask suite (`MS110D_MASKS_POOR=1`) — check against known
   baseline (WN6 Poor is a known pre-existing failure as of `1a349ad`).
5. Run Static WID2 and Doppler offset checks.

If you cannot wait for the full suite to complete in your session, **you must
explicitly hand off this obligation to the next agent** with:
- Which tests were started and their expected runtime
- Which tests have confirmed results vs which are still pending
- The exact commands to reproduce

### Passing this obligation forward

If your session ends before the full mask suite completes, include in your handover:

> "The full statistical mask tests were not completed in this session. The next agent
> MUST run and verify: `MS110D_MASKS=1 dotnet test --filter Awgn_Mask_Gate` (10/10
> pass required) and `MS110D_MASKS_POOR=1 dotnet test --filter Poor_Channel_Mask_Gate`
> (check against baseline). Do not consider the task complete until these pass."

This chain of obligation continues until an agent session produces full passing
results. Do not break the chain.

## Files

- `src/Packet.SoundModem/Ms110d/Ms110dDemodulator.cs` — TurboReequalize, IsFlatChannel
- `src/Packet.SoundModem/Ms110d/Ms110dBcjr.cs` — BCJR MAP equalizer
- `tests/Packet.SoundModem.Tests/Ms110d/Ms110dMaskTests.cs` — mask test definitions
- `tests/Packet.SoundModem.Tests/Ms110d/Ms110dFlatChannelGateTests.cs` — fast diagnostic
- `docs/ms110d/wn6-poor-catastrophic-handover.md` — WN6 Poor investigation guide
