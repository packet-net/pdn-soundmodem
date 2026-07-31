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

## Measurements (2026-07-31)

**Corpses (the registered bar: ≤~100-class canonical / ≤~30-class disjoint, fixed points):**

| Specimen | Shipped before | Shipped now | Termination |
|---|---|---|---|
| canonical (509) | 269,237 (coin-flip) | **112** | 9 exact fixed points + 2 cycle-accepts (b3 @R36, b6 @R40), max 40 rungs |
| disjoint (10509) | 269,154 | **32** | 10 fixed points + 1 cycle-accept (b4 @R40) |

Two mechanisms surfaced by the instrumented port and fixed with measured provenance:
- **Period-2 limit cycles**: the deep-fade-lottery blocks end in exact two-cycles (b3: churn frozen at 53/49,152, σ² alternating 0.4%) that the strict fixed-point test reverted to coin-flip. Fix: accept a *detected exact* 2-cycle by choosing the member whose reconstruction explains the ring better — label-free likelihood selection; the §B3.4 confident-wrong attractor cannot satisfy decode == decode-two-rungs-ago exactly, so the revert protection stands for genuine wander.
- **The refit gate recalibrated**: measured refit-good handovers sit at ≤2% decode churn and the W5b1 poison case at ~30%; the gate moves from my guessed 1% (which skipped b6's refit at 2%) to 5% — inside the measured gap with margin both sides.
- Port defect caught by the hermetic suite: the delay-profile scan read 8 probes unconditionally and crashed short-interleaver loopback blocks — clamped to the block's frame count (six QAM16 loopback tests red → green; the 64-frame corpse shape byte-unchanged).

**Guard pins**: all five byte-identical to the W0 pin outputs on the final binaries. **Hermetic suite: 790 / 0 / 110.**

**Off-rig direction check (WN8, 500k-bit smoke)**: {3 ms, 2 Hz} 7.55E-4 (650× better than the old path's coin-flip); {1 ms, 0.5 Hz} 9.09E-2 (5× better — two blocks fail under the ~2× longer deep fades of the 0.5 Hz process). Both directions improve; no constant helps only the D.6.1 geometry. **Slow-fade estimation is the measured weak edge** — recorded as the standing engineering note for any future leg (candidate: fade-adaptive refit cadence), not a merge blocker (the direction bar is met).

**Battery** (three-lane, launched 09:58:58 on the final binaries): (appended on completion)
