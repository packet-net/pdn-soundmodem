# G3 - production-path coverage: every waveform through FrequencyShiftedModem (MS110D Poor-gate successor program)

Registered 2026-08-20 per [poor-gate-successor-plan.md](../../poor-gate-successor-plan.md) §3/§4, before any code. Independent of G2 (different code, different instruments); run alongside it.

## Registration

**Question.** Does every MS110D waveform survive `FrequencyShiftedModem` at the GB7RDG tenant centre (3750 Hz audio, the 2026-08-06 plan) within the shift gate's statistical criterion of its native AWGN knee, does the shifted WN6 hold on the D.6.1 Poor channel at its mask SNR, and does the product say what the ledger says about WN7/WN8?

**Decision inputs (banked).** PR #221's shift gate (`FrequencyShiftedModemTests.The_Shift_Ladder_Shows_No_Knee_Movement`): WN6 only, centres 3000/5000, rungs 4/5/6/8 dB, 50 frames per cell, the first cut's 3 dB noise-folding penalty caught and fixed by band-passing receive before the downshift. The 2026-07-27 campaign's AWGN knees per waveform (WN0 -3.5, WN1 -2.5, WN2 -1.6, WN3 -0.6, WN4 +1.5, WN5 +3.7, WN6 +5.7, WN7 +8.8, WN13 +2.6, WN8 ~+13.5 dB clean thresholds, on the rig; the sim knees sit a little below). The production config: `{ "mode": "ms110d-wn6", "rfFrequency": 7053500 }` on subChannel 3 at 3750 Hz audio above the packet pair. Nothing on air at a moved centre yet (ledger 2026-08-05). `ms110d-wn7`/`wn8` are first-class catalogue modes with no runtime statement of their Poor status.

**Instrument + method.**
1. Extend the shift ladder to take a mode list and per-mode rungs (`MS110D_SHIFT_GATE_MODES`, default all ten `ms110d-wn*`; rungs default to a straddle around each waveform's measured knee, overridable), with centres native / 3750 / 5000. Same A/B/B' criterion: no rung more than two-sigma binomial below native, aggregate within 5 %; the ladder must straddle the knee or it is vacuous.
2. A shifted Poor arm: the same frame-count A/B for WN6 at 3750 Hz against native, through a 48 kHz `WattersonChannel` at the D.6.1 Poor geometry and +14 dB, enough frames to be decidable (a smoke at hermetic budget; the deep run env-gated like the AWGN ladder).
3. A journal line at config and SETHW time for `ms110d-wn7`/`wn8` stating their Poor status as the ledger has it (WN7: sim hard-gated since 2026-08-20, no hardware confirmation at +19 dB; WN8: measured-only at 2.90E-4 / 1.75E-2 on Poor, and on 2026-08-03 did not carry over a 48 W NVIS path). Plain ASCII, one line, no behaviour change.

**Kill/proceed rule (pre-committed).** Every waveform within the criterion at 3750 and 5000, and the shifted WN6 Poor arm within the criterion -> G3 closes with the ladder as a standing env-gated instrument and the hermetic smoke in CI. Any waveform or the Poor arm outside the criterion -> it is a receive-path defect in the shifter (the #221 first-cut lesson) and is diagnosed and fixed before the tenant goes on air; the leg does not close on a documented exception.

## Measurements

*(appended as the leg runs)*
