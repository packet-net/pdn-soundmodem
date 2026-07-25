# Phase B3.2 — WN2's benign tail: the anchor-ridge fix (2026-07-25, small hours)

B3.1's genie attribution said WN2's 484-error benign tail (1.59E-4) is 100% tracking deficit (genie: 0 errors on the same 3.04M bits). This directory banks the autopsy that localized the deficit, the measured mechanism, and the fix: the K=48 per-probe anchor ridge raised 1.0 → 8.0.

## The autopsy chain (worst tail burst: w0/b5, channelSeed 5503, 54 errors in one block)

1. **Corpse pair** (`corpse-r1/`): normal 54 coded errors, all in block 1; genie 0 on the identical burst. Uncoded 13,558 vs 11,557 (+17%). Block 1 is the hardest block in BOTH runs (uncoded SER 0.186 normal / 0.171 genie) — the coded cliff sits between those two numbers, and every other block (up to 0.165) decodes clean in both. The tail class is blocks whose local fade density pushes local SER just over the decode threshold.
2. **Fade-phase decomposition**: the normal-vs-genie SER excess is NOT fade-edge lag — deep-fade frames are equally drowned (0.353 vs 0.360), and the fade-entry/recovery classes hold only ~8% of the excess. The excess is a FLAT +0.02 SER tax across healthy frames (92.5% of it): per-probe solve estimation noise, paid everywhere.
3. **LLR honesty check**: deep-fade symbols already shrink naturally (mean |y| 0.357 vs 1.036 healthy; errored symbols |Re| 0.19 in fades vs 0.39 healthy) — the Class-D-style confident-garbage poison is NOT present in this class, so LLR recalibration is not the lever here. And since genie mode shares the noisy-detection decisions, DD decision poison cancels in the pair — the deficit is regressor observation noise in the solves, full stop.
4. **λ probe**: `MS110D_MASK_RLS_LAMBDA=0.995` → 43 errors (baseline 42). The RLS recursion is not the memory that matters (its intra-frame deviations are discarded at frame end); the solve memory lives in the anchored ridge.

## The measured mechanism of the fix

The anchor ridge IS the equalizer's cross-frame memory (Kalman-style prior toward current taps). Sweep on the WN2 +5 dB smoke (490,880 bits, canonical seeds, `ridge-ab.log` + `sweep-census/`):

| track ridge | 0.5 | 1.0 (old) | 2.0 | 4.0 | **8.0** | 16.0 |
|---|---|---|---|---|---|---|
| coded errors | 43 | 42 | 20 | 5 | **1** | 23 |
| uncoded BER | 1.24E-1 | 1.26E-1 | 1.27E-1 | 1.34E-1 | 1.45E-1 | 1.72E-1 |

Coded errors fall 42× while uncoded RISES — the mechanism is error-confidence, not error-count. At ridge 8 the corpse (`corpse-r8/`) decodes 0 (was 54) with MORE symbol errors (14,186 vs 13,558), because the wrong-sign LLR mass the Viterbi pays drops 2.35× (4,579 → 1,948): the anchored equalizer coasts instead of chasing fades with noisy solves, and wherever the true channel deviates from the anchored estimate the output AMPLITUDE collapses — every error self-reports low confidence (mean |Re| on errors: healthy 0.388→0.174, deep fade 0.186→0.078). Estimation uncertainty becomes soft erasures instead of confident coin-flips. Deep-fade SER worsens to 0.414 (near-random) at |Re| 0.078 — erasures the rate-1/4 code eats.

The 40 ms K=48 frame makes ~8-frame anchor memory ≈ 300 ms, inside the 1 Hz Poor coherence time. The same move is FORBIDDEN at U=256 (120 ms frames), measured both ways:

- WN13 at 4× its ridge (0.6): 4.94E-2 — catastrophic lag, from 5.5E-6.
- WN5 at 0.6: 17 errors vs 6 — worse.

This doubles as a consistency check of the B3.1 attribution: the detector-ceiling points do not respond to more solve memory (they respond negatively), while the tracking-deficit point responds 42×. The per-K ridge table encodes U-dependent memory correctly; the value is per-K, never global.

## Validation battery (ridge 8.0 as the K=48 default, `validate.log` + `validate-census/`)

| point | result | state |
|---|---|---|
| WN2 Poor canonical, 3.04M | **18 errors, 5.91E-6, bound 9.35E-6** (was 484 / 1.59E-4) | **AT MASK** — residual bursts 6/5/3/3/1 |
| WN2 Poor disjoint (+10000), 3.04M | **12 errors, 3.94E-6, bound 6.89E-6** | **AT MASK** — no seed overfit |
| WN1 Poor canonical, 3.04M | 2 errors, 6.58E-7 | at mask (was 0; the ridge trades WN1's last margin for WN2's 27×) |
| WN1 Poor disjoint (+10000), 3.04M | **0 errors, bound 1.21E-6** | pristine |
| WN5 / WN4 Poor smokes | 6 / 6 errors — bit-identical to baseline | K=32/24 untouched, as the per-K switch guarantees |
| AWGN gates, all 10 WNs, full budget | 0 errors everywhere (incl. WN1 −3 dB, WN2 0 dB — the gates the old ridge defended) | green |
| Static WID2 (0/3/9 ms) +9 dB, 3.04M | 0 errors | green |
| Doppler ±75 Hz | 0 errors | green |

**WN2 Poor passes the §5.3 accept rule on both seed sets — the fifth sub-8PSK Poor point at mask.**

## Files

- `ridge-ab.log` — the sweep lines (all labelled `[track ridge=… §B3.2 A/B]`); `lambda995.log` — the λ elimination
- `sweep-census/` — per-burst censuses for the sweep points (incl. the WN1@8 smoke)
- `corpse-r1/` — w0/b5 normal + genie pair on ridge 1.0 (the mechanism autopsy)
- `corpse-r8/` — the same burst at ridge 8.0 (0 coded errors, wrong-sign LLR mass halved twice)
- New instrument: `MS110D_MASK_TRACK_RIDGE` / `MS110D_AUTOPSY_TRACK_RIDGE` → `Ms110dDemodOptions.TrackRidge` (A/B knob, same report-only rule as the λ knob)
