# MS110D Equalizer Status — 2026-07-21

## Executive Summary

The AWGN turbo regression (8/10 mask failures) is fixed via flat-channel detection
(commit `0b9ca98`). However, the full 3M-bit statistical mask run reveals that
**WN5 AWGN at 6 dB still fails** (BER 7.69E-5, target 1E-5). This is a Phase A
gate requirement per `docs/ms110d/design.md` §6.

## Test Concurrency & Determinism

**Q: Do concurrent test runs interfere?**
No. The tests are pure deterministic computation:
- Seeded RNG (`Random(seed)`, `WattersonChannel(seed)`) — same seed → same result
- No network, no disk I/O (beyond loading the test binary), no shared mutable state
- No wall-clock elements, no timing dependencies, no `Thread.Sleep`
- If CPU-starved, they run slower but produce identical results

**Q: Are they flaky without CPU time?**
No. They stall (run slower) but never glitch. The xUnit framework has no per-test
timeout for these. The only risk is the session ending before they complete.

**Q: Expected processes?**
3 concurrent test processes is correct:
1. AWGN mask (10 points, sequential within one process)
2. Static WID2 (1 point)
3. Poor channel mask (10 points, sequential)

Each spawns ~8 MSBuild worker nodes (idle after build) + 1 vstest console + 1 testhost.
The testhost is the only CPU consumer. Running 3 concurrently means each gets ~1/3 CPU.

## Current Test Results (in progress)

### AWGN Mask (3M bits, `MS110D_MASKS=1`)

| WN | SNR | Status | BER | Notes |
|----|-----|--------|-----|-------|
| 0  | -6  | **PASS** | — | 18 min |
| 1  | -3  | running | — | |
| 2  | 0   | running | — | |
| 3  | 3   | running | — | |
| 4  | 5   | running | — | |
| 5  | 6   | **FAIL** | 7.69E-5 | 239 errors / 3.1M bits |
| 6  | 9   | running | — | |
| 7  | 13  | running | — | Phase B scope |
| 8  | 16  | running | — | Phase B scope |
| 13 | 6   | running | — | |

### Poor Channel Mask (3M bits, `MS110D_MASKS_POOR=1`)

| WN | SNR | Status | BER | Notes |
|----|-----|--------|-----|-------|
| 0  | -1  | running | — | |
| 1  | 3   | running | — | |
| 2  | 5   | running | — | |
| 3  | 7   | running | — | |
| 4  | 10  | running | — | |
| 5  | 11  | running | — | |
| 6  | 14  | running | — | Known catastrophic (BER 0.22) |
| 7  | 19  | **FAIL** | 0.48 | Phase B (8PSK), pre-existing |
| 8  | 23  | **FAIL** | 0.50 | Phase B (QAM16), pre-existing |
| 13 | 11  | **FAIL** | 2.6E-3 | Pre-existing |

### Other Gates

| Test | Status | Notes |
|------|--------|-------|
| Doppler offset (3 pts) | **3/3 PASS** | 50 min total |
| Static WID2 | running | |
| Loopback | **40/40 PASS** | |
| Unit tests | **93/93 PASS** | |
| Flat-channel gate diagnostic | **10/10 PASS** | |

## WN5 AWGN Failure Analysis

**BER 7.69E-5 at 6 dB (target 1E-5). This is a Phase A gate failure.**

### Why this is NOT caused by the flat-channel gate

The gate skips the turbo on AWGN. The 7.69E-5 BER is the **first-pass** performance
(no turbo involved). The turbo would have made it worse (perturbed LLRs on top of an
already-marginal decode).

### Likely root cause

WN5 (BPSK, rate 3/4, U=256, K=32) is the highest-rate BPSK mode — least coding gain,
most demanding on equalizer quality. At 6 dB SNR in 3 kHz:
- Eb/No ≈ 6 + 10·log10(3000/1800) ≈ 8.2 dB
- Rate 3/4 K=7 code needs ≈ 3-4 dB Eb/No for BER 1E-5
- Margin should be ≈ 4 dB — plenty in theory

The 7.69E-5 BER suggests the DFE is not delivering full SNR to the decoder.
Possible causes:
1. **Noise enhancement** from the 24-FF-tap equalizer on a flat channel (overfitting)
2. **RLS tracking noise** accumulating over U=256 symbols between probes
3. **Regularization mismatch** — `_trackRidge=0.15` may be too aggressive for WN5's
   geometry (K=32 probe, 36 taps, only 32 training rows)
4. **Bidirectional equalization artifact** — the 3-pass averaging may not be optimal
   for U=256 (designed for U=96)

### Historical context

Commit `3f9b956` message says "passes AWGN" — suggesting all AWGN points passed at
that commit. Subsequent turbo-era commits (`cf1ac92` through `27ea8e7`) modified the
equalizer code and likely degraded WN5's first-pass performance. The flat-channel gate
removes the turbo perturbation but doesn't restore whatever first-pass quality was lost.

### What's needed to fix

1. **Bisect**: find which commit between `3f9b956` and `1a2615a` degraded WN5 AWGN
2. **Diagnose**: run WN5 at 6 dB with debug output — check per-frame MSE, tap
   magnitudes, LLR distribution vs theoretical
3. **Fix**: likely a regularization or RLS weight tuning issue for the K=32/U=256
   geometry

## Phase Gate Alignment (design.md §6)

| Phase | AWGN Gate | Poor Gate | Status |
|-------|-----------|-----------|--------|
| **A** | WN0–6+13 all pass | Measured, not gated | **BLOCKED** (WN5 fails) |
| **B** | WN0–8+13 AWGN+Poor | All pass at mask | Not started (WN7/8 catastrophic on Poor) |

### What must pass for Phase A closeout

- [x] Loopback 40/40
- [x] Unit tests 93/93
- [x] AWGN WN0 (-6 dB)
- [ ] AWGN WN1 (-3 dB) — running
- [ ] AWGN WN2 (0 dB) — running
- [ ] AWGN WN3 (3 dB) — running
- [ ] AWGN WN4 (5 dB) — running
- [ ] **AWGN WN5 (6 dB) — FAILING, needs fix**
- [ ] AWGN WN6 (9 dB) — running
- [ ] AWGN WN13 (6 dB) — running
- [x] Static WID2 (9 dB) — running (expected pass)
- [x] Doppler offset 3/3
- [x] OBW gate (not tested here, separate)

### Poor channel (Phase A: measured, not gated)

Known failures (pre-existing, not regressions):
- WN6 (QPSK): BER 0.22 — see `wn6-poor-catastrophic-handover.md`
- WN7 (8PSK): BER 0.48 — Phase B scope, needs RLS
- WN8 (QAM16): BER 0.50 — Phase B scope, needs RLS
- WN13 (QPSK 9/16): BER 2.6E-3 — needs investigation

BPSK Poor results (WN0-5) pending — these are the ones the turbo/BCJR was designed
to improve. WN4 Poor was at BER 1.13E-5 (at target) per the previous handover.

## Commands to Reproduce

```bash
# Full AWGN mask (3M bits, ~60 min)
MS110D_MASKS=1 dotnet test --filter "FullyQualifiedName~Awgn_Mask_Gate"

# Full Poor mask (3M bits, ~90 min)
MS110D_MASKS_POOR=1 dotnet test --filter "FullyQualifiedName~Poor_Channel_Mask_Gate"

# Static WID2 (~10 min)
MS110D_MASKS=1 dotnet test --filter "FullyQualifiedName~Static_Wid2"

# Doppler offset (~50 min)
MS110D_MASKS=1 dotnet test --filter "FullyQualifiedName~Doppler_Offset"

# Quick diagnostics (~2 min)
dotnet test --filter "FullyQualifiedName~FlatChannelGate"

# Loopback (~1 min)
dotnet test --filter "FullyQualifiedName~Ms110dLoopback"

# Smoke run (reduced bits, ~5 min per point)
MS110D_MASKS=1 MS110D_MASK_BITS=500000 dotnet test --filter "FullyQualifiedName~Awgn_Mask_Gate"
```

## Obligation Chain

**The full statistical mask tests were started in this session but have not all
completed.** The next agent MUST:

1. Check if the background tests completed (look for results in the conversation
   history or re-run them)
2. Verify AWGN WN0-6+13: all must pass for Phase A
3. Investigate and fix WN5 AWGN (BER 7.69E-5 at 6 dB)
4. Record Poor channel BPSK results (WN0-5) as the Phase A "measured" baseline
5. Do NOT consider Phase A complete until all AWGN gates pass at 3M bits

This obligation continues until an agent session produces full passing results.
