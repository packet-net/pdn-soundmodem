# MS110D Phase B2 evidence — 2026-07-24

Stage B2 (the science core) measurement record. All lines came through the `MS110D_MASK_LOG` chain; smoke lines are labelled by the harness and are never gate evidence. Code: `ms110d-phase-b2` branch — `973e5ab` (B2.1 first pass), `9756bd6` (B2.2 chain BCJR), `94edc6f` (B2.3 chain turbo), `471a49c` (flat-channel turbo skip retired), `6da319b` (collapse detection final: the bad-probe criterion with energy present). Gate evidence below is from the FINAL code.

## The B2 exit gate (phase-b-plan §B2): WN4 Poor at mask under the full §5.3 rule — MET

From [gate-wn4-wn13.log](gate-wn4-wn13.log), 6M-bit budget so the ≥30-errors direct rule applies, on the final code (`7cf28e0`):

| Point | Seeds | Bits | Errors | BER | §5.3 |
|---|---|---|---|---|---|
| WN4 Poor +10 dB | canonical | 6,387,840 | 50 | **7.83E-6** | ✔ direct ≤ 1E-5, 5615 s sim, 0 acq fail |
| WN4 Poor +10 dB | +10000 | 6,387,840 | 52 | **8.14E-6** | ✔ direct ≤ 1E-5 |
| WN13 Poor +11 dB | canonical | 6,487,040 | 37 | **5.70E-6** | ✔ full rule (a B3.2 point banked early) |
| WN13 Poor +11 dB | +10000 | 6,487,040 | 17,074 | 2.63E-3 | ✗ — catastrophic-burst tail (below) |

WN13's disjoint seed loses bursts wholesale (~8.5k errors each; the rest run clean) to a mechanism the collapse detector cannot see — probe-correlation MAGNITUDE is invariant to a constant rotation, so a symmetry-locked wrong phase reads healthy. That signature is the entry autopsy for B3.2. The canonical seed's equivalent burst WAS recovered by the collapse machinery (8,254 errors under the blind MSE-relative detector → 37 under the final gain-based one).

## The collapse-detector iteration (three designs, each measured)

1. **Absolute preMse ≥ 1.0** (`e7933e6`-era): physics-correct at high SNR, misfired at WN1's −3 dB AWGN where the noise floor alone exceeds 1 — locked probes read collapsed, the fading latch engaged on AWGN, and the AWGN WN1 gate regressed to 1.15E-5.
2. **3× the burst's own MSE EWMA** (`e7933e6`): fixed WN1, went blind when a burst STARTS collapsed (the reference seeds high) — both WN13 6M seed sets then showed an undetected catastrophic burst, and WN4's disjoint seed drifted to 1.13E-5.
3. **Final (`6da319b`): the Phase-A bad-probe criterion with energy present** — correlation < max(0.10, 0.45·healthy ref) is SNR-invariant, and energy ≥ ¼ ref separates collapse from fade. No new constants; both discriminators pre-existed. WN1 AWGN restored, WN13 canonical recovered, WN4 both seeds at mask.

## Stage-by-stage movement (Poor smokes, 400k–2M bits, seed 500+wn)

| Point | B0 baseline | B2.1 first pass ([poor-b21-firstpass.log](poor-b21-firstpass.log)) | +B2.3 chain turbo ([poor-b23-chainturbo.log](poor-b23-chainturbo.log)) |
|---|---|---|---|
| WN4 (BPSK r2/3) | 1.91E-5 | 7.04E-6 | 7.04E-6 |
| WN13 (QPSK r9/16) | 6.2E-4 | 9.00E-5 | **7.40E-6** |
| WN2 (BPSK r1/4, U=48) | 3.67E-2 | — | **9.78E-5** (375×; U=48 joined turbo) |
| WN6 (QPSK r3/4) | 1.07E-1 | 5.72E-2 | 3.50E-2 |
| WN7 (8PSK r3/4) | 4.62E-1 | 4.59E-1 | 4.57E-1 (first-ever turbo-converged block) |
| WN8 (16QAM r3/4) | 4.96E-1 | 4.97E-1 | — (B3.4: needs gain-ramp + turbo inclusion) |

The WN2 genie pair (in [lambda-ab.log](lambda-ab.log)) shows a ZERO-error floor under perfect channel observation — WN2's residual 1E-4 is pure tracking deficit, the B3.1 target.

*Correction (B3.1, 2026-07-24 late): the banked lambda-ab.log WN2 genie lines actually read 3.12E-2, not zero — this README overclaimed from its own log. The B3.1 archaeology (../2026-07-24-phase-b31-genie/) showed that reading was ONE SignalLost-truncated burst (Class C, the 1 s K=48 patience, firing on the clean ring's fades too) and 19/20 bursts at zero; on the Class-C-fixed code the same seeds read a true 0 errors. The zero-floor CONCLUSION stands, now with evidence behind it.*

## Scatter-instrument findings behind the B2.1 design (WN7 Poor, seed 507, B1 baseline 89/130 smeared frames)

The per-frame 8th-power concentration statistic drove four measured design corrections, in order: (1) naive interpolation alone made things WORSE (107/130 smeared) because fresh re-solves were re-poisoned within one frame by ungated weight-1.0 RLS updates on wrong decisions — the DD gate now covers the RLS update; (2) the fresh re-solve zeroed the fade floor via tapChange=0 — fresh frames now skip the fading statistic; (3) the anchored ridge solve leaves a steady-state common-phase lag parking 8PSK in half-locked limbo (probe gain ≈ 0.4) below every collapse threshold — the per-probe gain-1 phase re-anchor removes it, producing the first solidly-locked frame stretches (concentration 0.5–0.9); (4) the excursion detector misclassified 40/130 continuously-fading frames as flat — the per-burst latch. Final state on the fixed realization: coded BER 0.477 → 0.248; the remaining smear is fade-punctuated relock latency, the B3.3 target.

## Also in this directory

- `awgn-static-doppler.log` — Phase A regression battery on the final B2 code (gate: unharmed).
- `lambda-ab.log` — §B2.4 RLS λ A/B lines (frame-tied vs 0.995, with genie pairs); analysed in the RLS-vs-NLMS report, never gate evidence.
