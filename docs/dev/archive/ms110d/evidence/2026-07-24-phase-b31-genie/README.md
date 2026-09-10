# Phase B3.1 — the K=48 genie instrument: mechanism, verification, trustworthy baselines (2026-07-24, late night)

B3.1 was filed when the §B2.4 λ-A/B banked a WN2 genie reading of 3.12E-2 (../2026-07-24-phase-b2/lambda-ab.log) — the perfect-channel-observation bound reading 360× WORSE than the normal run on the same seeds, which is impossible for a working instrument. The filed hypothesis blamed the heavy-ridge (1.0) K=48 modes' σ²·Σweight Gram compensation over-regularizing clean-row solves. **That hypothesis is refuted by measurement**; the actual mechanism was Class C, and the B3.0 wall-clock SignalLost patience fix repaired the genie as a side effect. This directory banks the proof and the first trustworthy full-budget genie baselines.

## The mechanism (measured, `old-code/`)

Re-running WN2 genie on the pre-B3.0 code (73bc96b, census harness backported) reproduced the 15,328 errors bit-exactly — and the census localizes ALL of them to **one burst**: w0/b1 (channelSeed 1503), **SignalLost-truncated at bit 9,216 of 24,544**, with the entire discarded tail (bits 9,216–24,543 = 15,328) counted as errors, after 7 collapse re-solves spiralled into the abandonment. The other 19 bursts: zero errors, clean Eom.

The genie feeds estimation from the noise-free ring, but the clean ring still FADES — probe gain reads the true faded channel, so the K=48 patience of 25 consecutive bad probes = **1 s** fired mid-fade exactly as in the normal path (§B3 Class C). The genie costume changed nothing about the mechanism. One truncation in 490k bits ≈ 3.1E-2, matching the banked reading.

Why the filed hypothesis is wrong (and should not be re-filed): the σ²·Σweight feed-forward Gram diagonal is exactly the term noisy training rows contribute implicitly — E[(s+n)(s+n)ᴴ] = ssᴴ + σ²I per unit weight — so a clean-row solve with the compensation has the SAME expected Gram, trace, and therefore trace-proportional ridge as the noisy solve it stands in for, at any ridge strength. There was never an over-regularization to fix.

## Verification on current main (feb452a, `repro/`)

Same seeds and budget as the λ-A/B smoke:

| point | stale genie (pre-B3.0) | genie now | normal now |
|---|---|---|---|
| WN2 @ +5 dB, 490,880 bits | 15,328 (3.12E-2) | **0** (bound 7.47E-6) | 42 (8.56E-5) |
| WN1 @ +3 dB, 490,240 bits | — | **0** (bound 7.48E-6) | 0 |
| WN4 @ +10 dB, 851,712 bits | 14 | 14 (bit-identical) | — |
| WN5 @ +11 dB, 1,081,088 bits | — | 6 | — |

The genie is now ≤ normal everywhere — the instrument inequality restored. WN4 bit-identical to the stale run confirms the K=32 control never was broken.

## Known structural limitation (recorded, not fixed — no measured deficit)

The per-symbol RLS recursion between probes runs on clean regressors WITHOUT the σ² compensation the batch solves carry, so its expected fixed point is the zero-forcing solution, not MMSE (at the MMSE point the clean-row recursion sees a systematic mean update σ²·θ_ff). Three things bound the damage: taps are reset to the probe-solve trajectory at every frame end (`LoadTaps(endTaps)`), P is re-seeded from the σ²-compensated Gram at every probe, and the DD gate blocks updates once outputs leave the decision region. Measured impact at every tested operating point: none (table above and baselines below). If a future genie run at a harder operating point reads worse than normal again, start here — with a corpse, not this note.

## Full-budget genie baselines (`baseline-mask.log`, census per point) — the B3 attribution map

Canonical seeds unless marked; normal-path comparators are the banked B3 battery numbers (../2026-07-24-phase-b3-tail-autopsy/).

| point | genie | normal | verdict |
|---|---|---|---|
| WN2 @ +5 dB, 3.04M | **0 errors, 124/124 bursts zero, bound 1.21E-6** | 484 (1.59E-4) | **pure tracking deficit** — perfect observation erases the whole benign tail |
| WN5 @ +11 dB, 3.24M | 19 (5.86E-6; bursts 7/6/6) | 18 (5.55E-6) | **detector ceiling** — estimation is not the deficit |
| WN13 @ +11 dB, 6.49M | 59 (9.10E-6; 10 bursts, max 14) | 24–66 across runs | **detector ceiling** (within the wobble band) |
| WN13 disjoint (+10000), 6.49M | 118 (1.82E-5; 17 bursts, max 18) | 8,507 (one burst holds ~8,400) | **catastrophic class is tracking deficit** — gone under genie; residue is the benign texture |
| WN6 @ +14 dB, 3.24M | 74,744 (2.30E-2) | ~3.5E-2 | **detector-limited** — perfect observation buys ~1.5× |
| WN7 @ +19 dB, 3.24M | 1,467,006 (4.52E-1, turbo 1c/87r) | ~4.57E-1 | **detector-limited, totally** — perfect observation buys nothing |

Consequences for the B3 program, in the plan's own order: B3.2's tracking work aims at WN2 (the biggest measured headroom: 484 → 0) and the fade-cluster class; WN5's and WN13-canonical's residue is NOT reachable by tracking — the lever there is detector-side (LLR calibration — the shelved σ² program — or FEC-visible erasure marking); and B3.3's pre-registered FD-turbo fallback is no longer a fallback, it is the expectation — WN7 fails identically with a PERFECT channel estimate, so re-lock latency work cannot be the fix.

## The fade-cluster specimen under perfect observation (`autopsy/`)

WN13-disjoint w3/b5 (channelSeed 3015514), the banked B3.2/B3.3 specimen, corpse pair on current main:

| | normal | genie |
|---|---|---|
| coded errors | 8,392 (block 8 dead: bits 147,457–165,885) | **12** |
| uncoded errors | 46,564 / 360,448 | 38,588 |
| collapse re-solves | 15 | 10 |
| turbo | 10c/1r | 11c/0r |

Perfect channel observation repairs the dead block almost entirely (8,392 → 12, and the one reverted turbo block converges). The between-dips equalizer degradation (SER 0.3–0.5 at healthy gain) is estimation error, not an unavoidable consequence of the fade geometry — the B3.2 fade-recovery work has real, measured headroom on exactly this corpse.

## Files

- `old-code/` — the archaeology: WN2 genie census on 73bc96b (the one-burst SignalLost truncation)
- `repro/` — smoke-budget genie/normal pairs on current main (WN1/WN2 censuses + mask log)
- `baseline-mask.log` + `census-*` — the full-budget genie baseline battery
- `autopsy/` — the WN13-disjoint w3/b5 specimen corpse pair (normal vs genie) on current main
