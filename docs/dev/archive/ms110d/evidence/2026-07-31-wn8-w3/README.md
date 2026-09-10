# W3 — label-free trajectory estimation physics + the MFB pricing curve (WN8 redesign program)

Registered 2026-07-31, after W2's pivot ([../2026-07-31-wn8-w2/](../2026-07-31-wn8-w2/): the FF sandwich is the bottleneck; the MFB-form receiver leads), before any instrument code or run. This leg fuses the plan's W3 (bootstrap physics) with W4's pricing instrument — after the pivot they are one measurement.

## Registration

**Question.** Can the channel trajectory be estimated **label-free** — probe anchors first, moment observables if needed — at the accuracy the MFB-form receiver requires? Equivalently: where do label-free estimators land on the MFB's coded-ceiling-vs-trajectory-error curve, whose 0-NMSE anchor is the banked W1b result (0/0)?

**Decision inputs.** W1b (MFB 0/0, calibration closed); W2's ranked budget (detection representation dominant → the MFB-form receiver is the candidate this leg prices); the program plan §2 physics bar (~−30 dB NMSE healthy-bulk for ≤1 dB detection loss); the banked B3.4 verdict ("mid-frame accuracy only labels provide" — measured in the OLD sandwich; the probe-only MFB-form number is genuinely open).

**Instrument + method.** Extensions of the W1b instrument (`Ms110dMfbBound`), test-side only, both specimens:

- **Trajectory source knob** (`MS110D_MFB_TRAJ`): `truth` (the banked anchor), `truth+noise` (`MS110D_MFB_TRAJNOISE=<relative rms>`: complex white perturbation on the 96 Hz truth grid at a prescribed relative error — maps the ceiling-vs-NMSE curve without estimator specifics; white-vs-shaped caveat recorded), `probes` (label-free: per-probe 2-path LS on the probe's ISI-clean interior span — chips ≥8 in from each probe edge so no data pulse reaches the rows — linearly interpolated between probe centres across the burst).
- Every run reports: per-path trajectory NMSE vs truth (pooled + by-|g| quartile — the fade-depth split), MF slicer SER, and per-block coded errors.
- **Pre-committed reads.** (1) The curve: the NMSE at which the MFB ceiling crosses ~2E-5-class on both specimens is the **requirement** the label-free estimator must meet. (2) Probe-interp lands at some measured NMSE: if it meets the requirement outright, the MFB-form receiver is priced viable on probes alone and W5's build is registered next (Wall 2 reduces to engineering, not physics). (3) If it misses, the gap in dB is the **moment-observable budget** — W3b registers the y¹²/|y|² correction stage against exactly that gap; if the gap exceeds what any data-driven correction can plausibly close (≥10 dB beyond the probe floor at the fade depths that carry the errors), the stop-and-reassess writes the options (per-survivor/joint estimation, or exit (ii) with the honest ceiling).
- **Carried caveats.** The MFB retains genie ISI cancellation — this leg prices *estimation*, not the full receiver loop; W5 must bring the cancellation story (code-feedback iterations, whose convergence-from-good-starts the b34 oracle proved only for the old sandwich). Estimation error from real estimators is correlated, not white — the trajnoise curve is indicative, the probes point is real.

**Budget.** ~150 lines on the W1b instrument; ~10 corpse runs (minutes each); no battery (nothing ships).

## Measurements (2026-07-31, [summaries/](summaries/))

**The requirement curve** (canonical unless noted; coded errors over 540,672 info bits):

| Trajectory | NMSE (path 0 / 1) | MF slicer SER | Coded errors |
|---|---|---|---|
| truth | (exact) | 2.44E-3 | **0** |
| truth + 3% white | −30.3 / −30.7 dB | — | **0** |
| truth + 6% | −24.3 / −24.7 dB | — | **0** |
| truth + 12% (both specimens) | −18.5 / −18.4 dB | — | **0 / 0** |
| truth + 25% | −11.9 / −12.3 dB | — | 316 = 5.8E-4 |
| **probes (label-free), canonical** | **−29.4 / −28.7 dB** | 3.21E-3 | **0** |
| **probes (label-free), disjoint** | **−30.0 / −29.8 dB** | 4.40E-3 | **0** |

The MFB-form receiver tolerates trajectory error to ~−18 dB NMSE before the coded ceiling leaves the mask class; the ceiling breaks between −18 and −12 dB. The probe-anchor estimator — per-probe LS on the 8-chip ISI-clean interior, plain linear interpolation across the ~120 ms anchor gaps, seeing nothing but the known probe chips — lands at **−29/−30 dB on both specimens: 11 dB inside the requirement, zero coded errors**. The program plan §2's −30 dB physics bar is met label-free, essentially exactly.

## Verdict

**Wall 2's physics is measured dead in the MFB frame — the proceed branch fires with margin.** The banked B3.4 verdict ("16QAM detection at 23 dB Poor REQUIRES mid-frame channel accuracy that only labels provide") was a statement about the old sandwich's estimators: probes alone provide −29 dB mid-frame accuracy by linear interpolation, and the MFB-form receiver needs only −18 dB. The moment observables (y¹², |y|²) are not needed at this operating point — banked as reserve margin, unregistered. W4's pricing question is answered by the same table: the leading candidate is priced viable 11 dB inside its requirement, on both seed families.

**What remains is engineering, not physics — W5**: the MFB-form receiver as a shipped WN8 path — probe-anchor trajectory estimation + per-symbol matched projection with whitened elliptical pricing + interference cancellation. The honest gap this leg does NOT close: the genie cancellation. The real receiver must supply it iteratively, but the basin arithmetic has transformed since b34: iteration-0 with a probe-estimated channel is the W1-class pass (~1E-4 wrong info bits, 99.9%-correct labels — not coin-flip), and cancellation iterations seeded from 99.9%-correct decisions converge toward the MFB, not away from it. Carried caveats: white-vs-shaped perturbation (the curve is indicative; the probes point is real), single-burst specimens (rate statements wait for W6's §5.3 budgets), and the W5 registration must price the first-pass ISI story explicitly.
