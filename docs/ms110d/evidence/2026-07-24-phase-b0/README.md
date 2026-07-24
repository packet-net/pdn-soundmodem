# MS110D Phase B0 gate evidence — 2026-07-24

Full-budget sweep (AWGN 10 + Poor 10 + static + Doppler; `run-masks.sh all`, workers 4) on the `ms110d-phase-b0` branch after the B0 instrument program (phase-b-plan §B0): weighted-RLS consistency fix, channel-truth genie, evidence-chain telemetry, off-rig harness, accounting honesty. Every evidence line now carries the new telemetry columns: `uncoded` (channel-bit SER from sign(first-pass LLR) vs the re-encoded TX stream), `deep-fade` (share of uncoded errors inside <−6 dB composite fades vs share of bits there, from the rig's recorded tap trajectory), `turbo Nc/Nr/Na/Ns` (converged/reverted/aborted/skipped).

## Phase A hard gates — all re-pass on the B0 code

AWGN 10/10 at ≥3M bits with zero errors each; static WID2 (0/3/9 ms @ 9 dB) zero errors; Doppler ±75 Hz checks clean. The Phase A regression bar for merging demod-path changes is met.

## Poor re-baseline (B0 code; canonical seeds; measured-not-gated)

| WN | closeout (07-23) | B0 (this sweep) | uncoded | deep-fade err share | turbo |
|----|------------------|------------------|---------|---------------------|-------|
| 0 | 8.13E-2 | 1.12E-1 | 1.42E-1 | 20 % in 9 % of bits | 5255s (no DFE path) |
| 1 | 2.85E-2 | 2.11E-2 | 1.88E-1 | 19 % / 9 % | 1947s (U=48 excluded) |
| 2 | 3.67E-2 | 2.07E-2 | 1.74E-1 | 20 % / 9 % | 953s (U=48 excluded) |
| 3 | 8.68E-3 | **0 errors / 3.19M** | 5.49E-2 | 38 % / 9 % | 780c |
| 4 | 2.36E-5 | 1.91E-5 | 3.54E-2 | 48 % / 9 % | 416c |
| 5 | 2.18E-2 | 2.09E-2 | 4.96E-2 | 34 % / 9 % | 253c/11r |
| 6 | 1.29E-1 | 1.07E-1 | 8.91E-2 | 30 % / 9 % | 98c/33r |
| 7 | 4.66E-1 | 4.62E-1 | 1.63E-1 | 19 % / 9 % | 0c/88r |
| 8 | 4.96E-1 | 4.97E-1 | 2.60E-1 | 12 % / 9 % | 88s (QAM excluded) |
| 13 | 6.16E-4 | 3.14E-4 | 1.06E-1 | 26 % / 8 % | 175c |

The BPSK-family improvements (WN1/2/13 ~1.5–2×, WN3 to zero errors) trace to the weighted-RLS consistency fix — between-fade stretches previously froze adaptation under the advisory-weight P-update bug (#64); fading frames (weight 1) were unaffected, which is why the broken tier didn't move. WN0's small worsening is the honest-accounting price (surplus post-EOM decode now counts) plus realization noise at −1 dB.

What the telemetry columns say about the regimes (the B1 autopsy feedstock):

- **Passing/near points die in fades**: WN3/WN4 concentrate 38–48 % of uncoded errors in the 9 % of bits under deep fade — tracking-through-fades is their remaining margin problem.
- **Broken-tier points die everywhere**: WN7 (19 %), WN8 (12 %) sit at/near the uniform 9 % — errors uncorrelated with fades = structural detection failure, not fade tracking.
- **WN7's decode is anti-coded**: coded BER 4.62E-1 from uncoded 1.63E-1, and turbo NEVER converges (0c/88r) — the LLR chain actively destroys information (mis-mapping/rotation class defect).
- **WN0's Walsh detector barely codes**: 1.42E-1 uncoded → 1.12E-1 coded, low fade concentration — consistent with the coherent-correlation-through-fades + intra-symbol-echo diagnosis in phase-b-plan §B3.5.

Also in this directory's period: genie validation (bit-identical seam proof; static rig 0 errors in 589k [GENIE]; Poor WN4 genie 3 errors vs baseline 7 on matched seeds after the MMSE calibration fix), and the off-rig harness first light (WN4 clean on {1 ms, 0.5 Hz}, BER 3.0E-1 on {3 ms, 2 Hz}).

## Disjoint-seed cross-checks (`xseed-poor-wn3.mask` / `xseed-poor-wn4.mask`)

- **WN3 Poor @ +7 dB, seed+10000: 0 errors in 3,192,960 bits** (bound 1.15E-6) — the canonical-seed zero is confirmed on a fresh realization set. WN3 Poor is at mask on the B0 code, under the full §5.3 rule, on two disjoint seed sets. It is NOT gate-armed yet (`MS110D_POOR_GATED` stays off until the whole table holds — design §6), but it is the first Poor point to cross.
- **WN4 Poor @ +10 dB, seed+10000: 11 errors → 3.23E-6** (bound 5.78E-6, would pass) vs the canonical seeds' 65 errors → 1.91E-5 (fails the direct rule). WN4 straddles the mask: near, not closed — and per the genie split (detection-dominated) the remaining margin is B2/B3 detection work, as planned.
