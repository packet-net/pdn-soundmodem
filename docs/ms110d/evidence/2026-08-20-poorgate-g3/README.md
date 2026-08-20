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

## Measurements (2026-08-20)

**The first Poor arm measured the instrument, not the shifter.** The hermetic smoke's first run read native 10/12 against **4/12 at 3750 Hz** on the Poor channel at +14 dB, with the AWGN arms at parity. A centre x SNR x channel matrix ([matrix-wn6-rig-at-1800.txt](matrix-wn6-rig-at-1800.txt), 12 frames per cell) made the shape plain: on Poor, 3000 Hz = native, 3750 Hz = 4-6 of 12, **5000 Hz = 0 of 12 at every SNR including 26 dB**; on AWGN every centre 12/12 at every SNR. Structural, centre-dependent, fading-only, SNR-independent - and `WattersonChannel` forms its complex envelope *about the 1800 Hz sub-carrier* with a 2.2 kHz low-pass, so a signal moved to 3750 Hz has half its spectrum above that cutoff and one at 5000 Hz all of it. The #221 gate never ran a Poor arm, and nobody had pointed the rig at a moved centre before. The rig now takes `CentreHz` (default 1800) and scales its low-pass taps with the sample rate (129 at 9600 Hz exactly as before, so every banked battery digit stands: the five guard pins and four corpses on the changed rig reproduce their digits, [the G2d pins](../2026-08-20-poorgate-g2/g2d-pins/) are that run). With the rig faded about the signal's own centre ([matrix-wn6-rig-at-centre.txt](matrix-wn6-rig-at-centre.txt)): Poor 14 dB native 10 / 3000 10 / 3750 10 / 5000 10; 20 dB 11 / 12 / 11 / 11; 26 dB 11 / 11 / 11 / 11; AWGN 12 everywhere. **The shifter loses nothing on fading at any centre.** The hermetic smoke reads native 10/12, 3750 Hz 10/12 ([poor-smoke.txt](poor-smoke.txt)) and is in the suite.

**The ladder, all ten waveforms, native / 3750 / 5000, 50 frames per cell, rungs re-centred on each waveform's knee:** every waveform passes the per-rung and aggregate criteria (first run: 0 failures in ten processes, 8-26 minutes each; the per-cell table from the second run, which writes its evidence file, follows below).

**The product says what the ledger says:** `Ms110dModem.PoorStatusNote` gives `ms110d-wn7` and `ms110d-wn8` a one-line standing note, printed by the daemon under the modem announcement at start-up and appended to the SETHW outcome in the journal; the other eight waveforms, hard-gated on Poor and on-air proven, print nothing extra.

### The ladder ([ladder/](ladder/), second run, 50 frames per cell; every waveform 0 failures, 6-29 minutes per waveform as one process each)

| waveform | rung (dB) | native | 3750 Hz | 5000 Hz |
|---|---|---|---|---|
| ms110d-wn0 | -12 | 0/50 | 0/50 | 0/50 |
| ms110d-wn0 | -11 | 1/50 | 0/50 | 3/50 |
| ms110d-wn0 | -10 | 17/50 | 23/50 | 20/50 |
| ms110d-wn0 | -8 | 50/50 | 49/50 | 45/50 |
| ms110d-wn1 | -7 | 2/50 | 0/50 | 1/50 |
| ms110d-wn1 | -6 | 29/50 | 26/50 | 27/50 |
| ms110d-wn1 | -5 | 48/50 | 43/50 | 46/50 |
| ms110d-wn1 | -3 | 50/50 | 50/50 | 50/50 |
| ms110d-wn2 | -6 | 0/50 | 0/50 | 0/50 |
| ms110d-wn2 | -5 | 1/50 | 1/50 | 0/50 |
| ms110d-wn2 | -4 | 14/50 | 19/50 | 16/50 |
| ms110d-wn2 | -2 | 48/50 | 49/50 | 48/50 |
| ms110d-wn3 | -3 | 0/50 | 0/50 | 0/50 |
| ms110d-wn3 | -2 | 34/50 | 27/50 | 34/50 |
| ms110d-wn3 | -1 | 49/50 | 48/50 | 50/50 |
| ms110d-wn3 | 1 | 50/50 | 50/50 | 50/50 |
| ms110d-wn4 | -1 | 0/50 | 0/50 | 0/50 |
| ms110d-wn4 | 0 | 3/50 | 6/50 | 8/50 |
| ms110d-wn4 | 1 | 43/50 | 43/50 | 36/50 |
| ms110d-wn4 | 3 | 50/50 | 50/50 | 50/50 |
| ms110d-wn5 | 0 | 3/50 | 1/50 | 3/50 |
| ms110d-wn5 | 1 | 33/50 | 41/50 | 41/50 |
| ms110d-wn5 | 2 | 49/50 | 48/50 | 50/50 |
| ms110d-wn5 | 4 | 50/50 | 50/50 | 50/50 |
| ms110d-wn6 | 4 | 33/50 | 37/50 | 30/50 |
| ms110d-wn6 | 5 | 45/50 | 48/50 | 50/50 |
| ms110d-wn6 | 6 | 50/50 | 50/50 | 50/50 |
| ms110d-wn6 | 8 | 50/50 | 50/50 | 50/50 |
| ms110d-wn7 | 8 | 34/50 | 40/50 | 41/50 |
| ms110d-wn7 | 9 | 46/50 | 48/50 | 46/50 |
| ms110d-wn7 | 10 | 50/50 | 50/50 | 50/50 |
| ms110d-wn7 | 12 | 50/50 | 50/50 | 50/50 |
| ms110d-wn8 | 11 | 11/50 | 9/50 | 9/50 |
| ms110d-wn8 | 12 | 36/50 | 39/50 | 38/50 |
| ms110d-wn8 | 13 | 48/50 | 48/50 | 49/50 |
| ms110d-wn8 | 15 | 50/50 | 50/50 | 50/50 |
| ms110d-wn13 | 0 | 0/50 | 0/50 | 0/50 |
| ms110d-wn13 | 1 | 11/50 | 13/50 | 12/50 |
| ms110d-wn13 | 2 | 44/50 | 46/50 | 44/50 |
| ms110d-wn13 | 4 | 50/50 | 50/50 | 50/50 |

Every rung of every waveform keeps the moved arms within the two-sigma binomial band of native and every aggregate within 5 %; no rung set needed re-centring. The WN7 rows run through the G1d ensemble at both centres.

## Verdict

**G3 closes.** Every waveform survives `FrequencyShiftedModem` at 3750 and 5000 Hz within the gate's criterion; the shifted WN6 holds on the Poor channel at its mask SNR once the rig fades about the signal's own centre; the ladder is a standing env-gated instrument over all ten waveforms (`MS110D_SHIFT_GATE=1`, `_MODES`, `_RUNGS`, `_N`, `_CENTRES`, `_OUT`) and the Poor smoke is in the hermetic suite; the daemon states WN7's and WN8's Poor standing at start-up and on SETHW. The one defect found was in the instrument, and it is the kind the audit lesson warned about: a rig hard-wired to the geometry it was built for, read as a receiver fault until the matrix said otherwise. Nothing on air at a moved centre yet - that remains H1's.
