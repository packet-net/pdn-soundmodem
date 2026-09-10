# WN2 (BPSK r1/4, K=48) DFE dead-init — input signal-level AGC (2026-07-27)

Issue #101. **Lever: a slow input signal-level AGC ahead of the DFE.** The real-RF WN2 Poor failure is a global receive-level offset (~20 dB below the sim's nominal, no AGC upstream) that over-regularises the K=48 cold-restart LS solves → the equalizer initialises dead → SignalLost. The fix normalizes the global receive level back to nominal ahead of the equalizer. It is a **dead-zone no-op at the sim's nominal level (masks unchanged by construction)** and lifts only globally-low bursts.

The earlier FF-scaled-ridge lever is **banked as a measured negative** (`ff-scaled-ridge-lever.patch`, `data/`): it regressed the razor-thin WN2 disjoint mask (12 → 31 = 1.02E-5, fail). This document supersedes that as the shipped direction; the negative is kept as evidence.

## Mechanism (confirmed, reproduced bit-exactly in sim)

The K=48 DFE ridges its LS solves by `λ = reg · trace/n`, and that trace is dominated by the **feedback** regressors (the known past symbols, fixed unit magnitude, level-independent) while the ridge shrinks the **feed-forward** taps, whose signal rides at the absolute receive level. With no AGC upstream, a low level over-shrinks the FF taps to a near-zero-gain (**dead**) filter → SignalLost. The OTA campaign resolved WN2 to a pure **level** interaction (reproduced by level-scaling, unaffected by phase-noise; `2026-07-27-ota-lab-campaign/`), and the capture sits ~20 dB below sim nominal — WN2's K=48 ridge over-shrinks there while WN4's K=32 ridge survives at the same level.

**Confirmation that global level-norm recovers it (the pre-registered de-risk):** the identical WN2 Poor burst decodes cleanly at nominal (0 coded errors) and dies SignalLost when scaled −20 dB (`data/scale-calibration-*.log`; init gain 0.081 → 0.014, matching the OTA `ref≈0.014`) — so restoring the global level to nominal recovers it by construction. The OTA campaign's own `inject.py --mode level --factor 0.1` reproduction is the real-capture-side of the same statement.

## The lever — slow (global) signal-level AGC

`EstimatePreambleLevel()` correlates the received **Fixed** subsection of each trailing preamble super-frame against the known Fixed chips in 32-chip groups (`|Σ y·k̄|/32`, noise averaged out of each group — a **signal** estimate, not total power) and averages over ~1–2 s of preamble. That long window averages the ~1 Hz Watterson fade out, so it measures the **global** receive level, not the instantaneous fade. Then a per-burst scalar `_agcGain` normalizes it: **at or above `AgcLevelFloor` the gain is exactly 1.0 (dead-zone no-op); below it the burst is boosted to `AgcNominalLevel`** (capped). `_agcGain` is applied in the DFE read path (`ReadT2`), unity during acquisition — so acquisition and every nominal-or-stronger burst are untouched.

**Why this is mask-neutral where the ridge was not:** the ridge scaling was *instantaneous* per solve, so it could not tell a globally-weak signal from a nominal-level fade — both present low FF energy — and it fit noise during nominal fades, regressing the mask. The AGC's estimate is *slow/global*: a nominal ~1 Hz fade averages out over the preamble window, so the estimate stays ~nominal → the dead-zone keeps the gain at exactly 1.0 → **byte-identical**. A real-RF global offset does *not* average out → it is detected and corrected. The slow time constant is the whole difference.

Measured level vs scale (fixture, `data/agc-scale-calibration.log`): nominal (×1) = **0.1186**, linear in scale (×0.5 → 0.0593, ×0.1 → 0.0119). The dead-zone floor is set strictly below the gated mask families' minimum level (census in `data/`), so the AGC fires **zero times in-family** and the masks are byte-identical.

## The bar — INVERTED: mask-neutral (byte-identical), and the low-level fixture greens

A correct level-norm is a no-op at nominal, so the battery bar is that the masks are **unchanged**, not merely "still pass":

- WN2 Poor both seed families **byte-identical** to the b32 baseline (18 / 12 errors), AGC fires 0× in-family.
- AWGN 10/10 unchanged, static WID2 (0/3/9 ms) unchanged, Doppler ±75 Hz unchanged, non-target waveforms byte-identical, guard-pin corpses intact, hermetic suite green.
- The red fixture `Wn2_Poor_Dead_Init_Recovers_At_Low_Receive_Level` (−20 dB) greens (SignalLost → Eom, 0 errors), and `Wn2_Poor_Full_Level_Is_Agc_No_Op` holds (gain exactly 1.0).

If any mask moves at all, the AGC is chasing fades (wrong time constant / normalizing total power) — iterate the window/floor until it is neutral.

## Verification (measured)

**Red fixture** `Wn2_Poor_Dead_Init_Recovers_At_Low_Receive_Level` (−20 dB): RED on main (SignalLost, 24 544 errors) → **GREEN with the AGC** (Eom, 0 errors; AGC level 0.0119, gain 10.1×, init gain restored 0.014 → 0.082). `Wn2_Poor_Full_Level_Is_Agc_No_Op`: **gain exactly 1.000** at nominal. Both run ungated in the suite. (`data/agc-scale-calibration.log`.)

**WN2 Poor 6M — the razor gate (`data/wn2-6m-*`):**

| family | with AGC | closeout baseline | AGC fired |
|--------|----------|-------------------|-----------|
| canonical | 30 / 6.09M = **4.93E-6** | 30 / 4.93E-6 | **agc 0** |
| disjoint | 29 = **4.76E-6** (bound 6.84E-6) | 29 (bound 6.84E-6) | **agc 0** |

Matches the closeout §1 numbers **exactly**, and `agc 0` on both families proves the AGC never fired in-family → every read `× 1.0f` (bit-exact) → **byte-identical**. (The 3M spot-check reads 15 canonical vs the b32 doc's 18; the one differing burst, seed 3015503, is `agc gain=1.000` at level 0.158 — a pre-existing b32→current-main drift, not the AGC. 123/124 match b32 to the byte.)

**Guard-pin corpses (closeout §6) — all byte-identical:** WN7 w0/b0 `0 coded / 11c/0r/4v`, WN7 w1/b0 `20 coded / 11c/0r/5v`, WN6 w0/b0 `0 / 11c/0r/0v`, WN13sp `0 / 11c/0r/0v`, WN0 w2/b97 `0 coded`. K≠48 (and WN0 Walsh has no DFE), so the AGC is a nominal-level no-op there.

**Hermetic suite: 701 passed / 0 failed** (107 env-gated skips) with the AGC — including the two ungated red tests.

**AWGN gates (WN0–6, 13): all 0 errors, `agc 0`** — WN0 @ −6 dB, WN1 @ −3 dB, WN2 @ 0 dB, WN3 @ +3, WN4 @ +5, WN5 @ +6, WN6 @ +9, WN13 @ +6. **Static WID2 (0/3/9 ms) @ +9 dB: 0 errors, `agc 0`. Doppler ±75 Hz: 0 errors, `agc 0`.** **WN1 Poor (the other K=48, AGC-armed mode), both families: byte-identical, `agc 0`.** Every mask point reports `agc 0` — the AGC is a strict no-op in-family, so the masks are byte-identical by construction. (`data/battery/`.)

The universal `agc 0` is expected: the preamble signal level is set by the modulator and channel, not the SNR or mode, so at the sim's nominal level every mode's estimate (~0.12–0.16) sits far above the 0.04 floor. The AGC engages only for a globally-low receive level (real-RF Poor), which is exactly the −20 dB red fixture it greens.

## Files

- Code: `Ms110dDemodulator.cs` (`_agcGain`, `EstimatePreambleLevel`, the `InitializeDfe` set + `ReadT2` apply). No ridge changes.
- `Ms110dDeadInitTests.cs` — the red fixture + the no-op control + env-gated level calibration/census.
- `ff-scaled-ridge-lever.patch` + `data/*` — the banked ridge negative and its mask-regression measurements (`disjoint-perburst-diff.txt`: five previously-perfect bursts → error bursts).
