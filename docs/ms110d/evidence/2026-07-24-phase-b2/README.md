# MS110D Phase B2 evidence — 2026-07-24

Stage B2 (the science core) measurement record. All lines came through the `MS110D_MASK_LOG` chain; smoke lines are labelled by the harness and are never gate evidence. Code: `ms110d-phase-b2` branch, commits `973e5ab` (B2.1 first pass), `9756bd6` (B2.2 chain BCJR), `94edc6f` (B2.3 chain turbo).

## The B2 exit gate (phase-b-plan §B2): WN4 Poor at mask under the full §5.3 rule — MET

From [gate-wn4-wn13.log](gate-wn4-wn13.log), 6M-bit budget so the ≥30-errors direct rule applies:

| Point | Seeds | Bits | Errors | BER | §5.3 |
|---|---|---|---|---|---|
| WN4 Poor +10 dB | canonical | 6,387,840 | 37 | **5.79E-6** | ✔ direct ≤ 1E-5, 5615 s sim, 0 acq fail |
| WN4 Poor +10 dB | +10000 | 6,387,840 | 62 | **9.71E-6** | ✔ direct ≤ 1E-5 |
| WN13 Poor +11 dB | canonical | 6,487,040 | 8,295 | 1.28E-3 | ✗ — ONE catastrophic burst (see below) |
| WN13 Poor +11 dB | +10000 | 6,487,040 | 99 | 1.53E-5 | ✗ (1.5× mask) |

WN13's canonical run concentrates ~8k errors in a single broken burst while the other 31 bursts run clean — a QPSK tail event (tracking collapse the burst never recovers from), not a mean-performance problem. That tail is the entry measurement for B3.2 (WN13 → WN6 family closure).

## Stage-by-stage movement (Poor smokes, 400k–2M bits, seed 500+wn)

| Point | B0 baseline | B2.1 first pass ([poor-b21-firstpass.log](poor-b21-firstpass.log)) | +B2.3 chain turbo ([poor-b23-chainturbo.log](poor-b23-chainturbo.log)) |
|---|---|---|---|
| WN4 (BPSK r2/3) | 1.91E-5 | 7.04E-6 | 7.04E-6 |
| WN13 (QPSK r9/16) | 6.2E-4 | 9.00E-5 | **7.40E-6** |
| WN2 (BPSK r1/4, U=48) | 3.67E-2 | — | **9.78E-5** (375×; U=48 joined turbo) |
| WN6 (QPSK r3/4) | 1.07E-1 | 5.72E-2 | 3.50E-2 |
| WN7 (8PSK r3/4) | 4.62E-1 | 4.59E-1 | 4.57E-1 (turbo 1c/87r — first-ever converged block) |
| WN8 (16QAM r3/4) | 4.96E-1 | 4.97E-1 | — (B3.4: needs gain-ramp + turbo inclusion) |

## Scatter-instrument findings behind the B2.1 design (WN7 Poor, seed 507, B1 baseline 89/130 smeared frames)

The per-frame 8th-power concentration statistic drove four measured design corrections, in order: (1) naive interpolation alone made things WORSE (107/130 smeared) because fresh re-solves were re-poisoned within one frame by ungated weight-1.0 RLS updates on wrong decisions — the DD gate now covers the RLS update; (2) the fresh re-solve zeroed the fade floor via tapChange=0 — fresh frames now skip the fading statistic; (3) the anchored ridge solve leaves a steady-state common-phase lag parking 8PSK in half-locked limbo (probe gain ≈ 0.4, preMse ≈ 0.75, below the collapse threshold) — the per-probe gain-1 phase re-anchor removes it, producing the first solidly-locked frame stretches (concentration 0.5–0.9); (4) the excursion detector misclassified 40/130 continuously-fading frames as flat — the per-burst latch. Final state on the fixed realization: coded BER 0.477 → 0.284; the remaining smear is fade-punctuated relock latency, the B3.3 target.

## Also in this directory

- `awgn-static-doppler.log` — Phase A regression battery on the B2 code (gate: unharmed).
- `lambda-ab.log` — §B2.4 RLS λ A/B lines (frame-tied vs 0.995, with genie pairs); analysed in the RLS-vs-NLMS report, never gate evidence.
