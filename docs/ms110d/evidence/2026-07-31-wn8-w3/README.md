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

## Measurements

(after the runs)
