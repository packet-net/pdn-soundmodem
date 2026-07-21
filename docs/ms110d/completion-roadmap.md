# MS110D Modem Completion Roadmap

## Current State (2026-07-21)

### What Works
- **AWGN masks**: WN0-8, WN13 all pass (without turbo)
- **Static WID2**: passes (3-path 0/3/9 ms)
- **Poor WN4**: BER 1.13E-5 (at 1E-5 target) with turbo BCJR
- **Loopback**: 40/40 pass
- **Doppler ±75 Hz**: passes for WN2, WN6

### What Doesn't Work
- **AWGN with turbo enabled**: 8/10 fail (turbo degrades flat channels)
- **Poor WN6** (QPSK): BER 2.1E-1 (needs QPSK BCJR)
- **Poor WN8** (16QAM): BER 5.0E-1 (needs carrier recovery)
- **Poor WN0-3, WN5**: untested with turbo (likely similar to WN4)

---

## Priority 1: Fix AWGN Regression

**Blocker**: Turbo degrades AWGN. See `awgn-regression-handover.md` for details.

**Recommended approach**: Per-frame h1 variance detection (Option A in handover doc).
Accumulate h1 estimates across frames in TurboReequalize. If variance is below
threshold → AWGN → revert to first-pass decode.

**Effort**: ~1 day. Low risk.

---

## Priority 2: Poor Channel — QPSK BCJR (WN6, WN13)

**Problem**: WN6 (QPSK, +14 dB) gives BER 2.1E-1 on Poor. The current BCJR only
handles BPSK (2 possible symbols per trellis transition). QPSK needs 4 possible
symbols per transition.

**Approach**: Extend `Ms110dBcjr` to handle QPSK:
- Trellis has 4^L states (L=5 delay → 1024 states for QPSK vs 32 for BPSK)
- Branch metric: 4 possible transmitted symbols per transition
- LLR computation: 2 bits per symbol, max-log over 4 symbols per bit

**Complexity**: 1024 states × 4 transitions × 96 symbols = ~393K operations per frame.
With 128 frames × 5 turbo iterations = ~250M operations. Feasible in C# (~1-2 seconds).

**Effort**: ~2-3 days. Medium risk (new code, needs validation).

**Dependencies**: Priority 1 (AWGN fix) should land first.

---

## Priority 3: Poor Channel — 16QAM Carrier Recovery (WN8)

**Problem**: WN8 (16QAM, +23 dB) gives BER 5.0E-1 on Poor. The channel phase
rotates by up to 43° over the 96-symbol frame (1 Hz Doppler × 40 ms). 16QAM's
minimum angular separation is 30° — the rotation exceeds decision regions.

**Approach**: Data-aided carrier recovery before the BCJR:
1. Use the probe (known symbols at frame boundary) to estimate phase at frame end
2. Extrapolate phase backward across the data block (linear or Kalman-filtered)
3. Derotate the received signal before BCJR equalization
4. Alternatively: joint phase+data estimation (EM algorithm or particle filter)

**Key insight from prototyping**: The phase rotation is the dominant impairment for
16QAM. Even with perfect amplitude equalization, the phase error causes BER ≈ 0.5.
The fix must track phase within the frame, not just at the probe.

**Complexity**: High. Joint phase+data estimation is research-level.

**Effort**: ~1-2 weeks. High risk.

**Alternative**: Accept WN8 Poor as a stretch goal. The spec requires it, but the
design doc notes "C ships only if a real use appears."

---

## Priority 4: Full Poor Mask Validation

Once Priorities 1-3 are addressed, run the full Poor mask suite:
- All WNs (0-8, 13) at their Poor SNRs
- 3M bits per point (full statistical budget)
- 97.5% Poisson CI must be below 1E-5

**Effort**: ~1 day (test execution). Requires Priorities 1-3 complete.

---

## Priority 5: Multi-Frame BCJR (Performance Improvement)

**Problem**: Current per-frame BCJR gives BER ~1.13E-5 on Poor WN4 (at target but
no margin). Multi-frame BCJR (processing all 128 frames jointly) gives ~2.4×
improvement in Python prototype.

**Approach**: Run BCJR on the entire concatenated block (12,288 symbols) instead of
per-frame (96 symbols). The trellis state propagates across frame boundaries,
eliminating cold-start effects.

**Challenge**: The per-frame channel estimates create discontinuities at frame
boundaries. Need smooth channel interpolation (Kalman filter or spline).

**Effort**: ~3-5 days. Medium risk.

**Note**: Only needed if the per-frame result (1.13E-5) doesn't reliably pass the
3M-bit mask. If it passes consistently, this is optional.

---

## Priority 6: 8PSK BCJR (WN7)

**Problem**: WN7 (8PSK, +19 dB) on Poor is untested but likely fails (similar to
WN6 but with 8 possible symbols per transition).

**Approach**: Extend BCJR to 8PSK (8^L states = 32768 for L=5). This is large but
feasible with optimized C# (~5-10 seconds per frame).

**Alternative**: Use reduced-state BCJR (keep only the most likely states) or
frequency-domain turbo equalization.

**Effort**: ~3-4 days. Medium-high risk.

---

## Summary Timeline

| Priority | Task | Effort | Risk | Dependency |
|----------|------|--------|------|------------|
| 1 | AWGN regression fix | 1 day | Low | None |
| 2 | QPSK BCJR (WN6, WN13) | 2-3 days | Medium | P1 |
| 3 | 16QAM carrier recovery (WN8) | 1-2 weeks | High | P1 |
| 4 | Full Poor mask validation | 1 day | Low | P1-3 |
| 5 | Multi-frame BCJR | 3-5 days | Medium | P4 (if needed) |
| 6 | 8PSK BCJR (WN7) | 3-4 days | Medium-High | P2 |

**Critical path**: P1 → P2 → P4 (QPSK is the next highest-impact target after AWGN fix).
P3 (16QAM) is the hardest and can be deferred per the design doc's "ships only if a
real use appears" note.
