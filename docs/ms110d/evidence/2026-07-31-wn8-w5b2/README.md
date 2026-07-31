# W5b2 — the MFB-form receiver ships (WN8 redesign program)

Registered 2026-07-31, after W5b1 fixed the design by measurement ([../2026-07-31-wn8-w5b1/](../2026-07-31-wn8-w5b1/)). This is the program's first shipped-demod change; the full closeout-§6 ladder applies: this registration → corpse → guard pins → full three-lane battery (non-target censuses byte-identical to the W0 baseline) → merge.

## Registration

**Change.** A QAM16-only block decoder (`Ms110dMfbBlockDecoder`, owned lazily by `Ms110dDemodulator`) replacing the block decode for WN8 in FinishBlock, exactly the W5a/W5b1 prototype made shipped: per-burst composite-FIR delay-profile scan (first QAM16 block; ridged wide LS over [−18,+19) on 8 probes, energy-accumulated; 26-tap window over the support), per-probe anchor LS on ISI-clean interiors, per-tap linear interpolation, per-symbol matched projection with believed-Gram pricing, and the measured schedule: SISO-soft cancellation rungs → one convergence-gated decision-directed anchor re-fit → hard rungs to an exact fixed point, revert to the first-pass decode if no fixed point by the cap (the §B3.3 revert principle). Structural scoping: only `Qam16` blocks reach the path — non-target byte-identity holds by construction.

**Constants and their provenance** (each documented in code; the off-rig direction check runs in W5c): window scan bounds ±18/threshold 0.1/ridges — structural (raised-cosine tail + pulse support arithmetic, percent-scale thresholds); WinLen 26 — support arithmetic (±2.3 ms spread + RC tails at T/2); schedule soft-cap 30 / total cap 48 — cap-class constants (the b34 Amendment 3 argument: caps trade wall-clock, not correctness; fixed-point detection does the real termination); refit gate 1% decode churn — the W5b1 label-quality condition, percent-scale.

**Pre-committed acceptance (the §6 merge bar).**
- Corpses: WN8 w0/b0 canonical ≤ ~100-class coded errors and disjoint ≤ ~30-class (the W5b1 fixed points, allowing shipped-integration variance), both reaching fixed points; WN7 w0/b0 + w1/b0, WN6, WN13sp, WN0 guard pins at their exact W0 digits; the truth/oracle instrument summaries for non-WN8 corpses byte-identical.
- Off-rig direction check at WN8 ({1 ms, 0.5 Hz}, {3 ms, 2 Hz}): measured, direction sane (better-or-similar vs the shipped baseline — no rig-fitted constant may help only on the D.6.1 geometry).
- Hermetic suite 0 failed.
- Full three-lane battery: every non-WN8 census byte-identical to the W0 baseline both families; AWGN WN8 stays 0-error at full budget; **WN8 Poor measured at full §5.3 budget both families — reported whatever it is**. The gate decision (arming `Poor_Channel_Mask_Gate` for WN8) is W6's, taken on the battery numbers per the B4 false-red criterion; this leg merges on improvement + integrity, not on mask.

**Budget.** ~600 lines shipped (new class + FinishBlock integration + burst-state plumbing), corpse iteration, one three-lane battery (~3.5 h on this box).

## Measurements

(after the runs)
