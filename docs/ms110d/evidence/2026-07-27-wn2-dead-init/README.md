# WN2 (BPSK r1/4, K=48) DFE dead-init — investigation & TRADEOFF finding (2026-07-27)

Issue #101. **Verdict: TRADEOFF — not shipped, #101 stays open.** The mechanism is confirmed and a lever that greens the failure is demonstrated, but the lever regresses the razor-thin WN2 sim mask (disjoint 12 → 31 errors = 1.02E-5, **fails the 1E-5 mask**). Per the closeout §5.3 consequence rule and the issue's hard constraint, a change that trades the OTA-Poor win for a sim-mask loss is a net negative and is not shipped. This directory is the honest record of the mechanism, the lever, and the measured regression that blocks it.

## Mechanism (confirmed, reproduced bit-exactly in sim)

The K=48 DFE solves its per-probe least squares with a Tikhonov ridge `λ = reg · trace/n` scaled by the **mean Gram diagonal**. That diagonal is dominated by the *feedback* regressors — the last super-frame's KNOWN symbols, fixed unit magnitude, channel- and level-independent — while the ridge shrinks the *feed-forward* taps, whose signal rides at the (possibly low) absolute receive level. There is no AGC ahead of the equalizer. So when the received level is low, `λ` stays ~constant (feedback-set) while the FF signal shrinks, and the solve returns a near-zero-gain (**dead**) filter.

Two solve sites are **cold restarts** — they have no anchor to carry the receive-level scale, so both go dead at a low level:

1. **The init solve** (`InitializeDfe`, `initRidge = 1.0`). Reproduced exactly by the issue's own method — scale a clean-sim WN2 Poor burst down: init gain **0.081 (full level) → 0.014 (−20 dB)**, matching the real-RF capture's `gain≈0.005 ref≈0.014`. See `data/scale-calibration-main.log`.
2. **The freshSolve collapse-recovery** (`ProcessFrame`, zeroes the taps and re-solves toward zero with `trackRidge = 8`). Even after the init is repaired, the burst tracks healthy for ~20 frames then dies at the first fade that trips collapse detection: the cold restart returns `gain = 0.004` and the burst ends `SignalLost`. See `data/frame-trace-scale0.1.log` (frame@13584 onward).

The anchored *steady-state* per-probe solve is scale-robust (the anchor carries the scale) — only the two cold restarts break.

## The lever (demonstrated): FF-block-scaled ridge on the cold restarts

Scale the cold-restart ridge by the **feed-forward block diagonal only** (`Dfe.SolveTraining(ridgeFromFfBlock: true)`), not the whole trace. The ridge then tracks the received signal energy, so the solved filter is **scale-invariant**. Wired as a dead-restart guard at both cold-restart sites (init gain-floor + freshSolve post-solve gain-floor), armed for K=48 only.

Result — the red test (`Ms110dDeadInitTests.Wn2_Poor_Dead_Init_Recovers_At_Low_Receive_Level`, a WN2 Poor burst scaled −20 dB): **RED on `main`** (`SignalLost`, 24 544 coded errors, init gain 0.014) → **GREEN with the lever** (`Eom`, 0 coded errors, init gain scale-invariant at 0.409, decodes as cleanly as full level since scaling is level-only). All scales −6 → −40 dB green. See `data/scale-calibration-fixed.log`, `data/ff-scaled-ridge-sweep.log`.

## Why it can't ship — the freshSolve fix regresses the razor-thin mask

The freshSolve fires **~34–43 times per burst** in the WN2 sim mask (during nominal-level fades). FF-scaling fundamentally **cannot distinguish a globally-weak signal from a nominal-level fade** — both present low FF energy — so at a nominal fade the FF-scaled freshSolve drops its ridge and **fits noise**, injecting errors. Measured on the shipped-ridge b32 baseline (WN2 Poor 3M, both families, `data/*-3m.log`, `data/*-perburst-diff.txt`):

| family | baseline (main, b32) | with the lever | verdict |
|--------|----------------------|----------------|---------|
| canonical 3M | 18 errors | **10** (net better, but individual bursts regress **0→2, 0→2, 0→6**) | passes, not byte-identical |
| disjoint 3M | 12 errors (3.94E-6) | **31 = 1.02E-5** | **FAILS the 1E-5 mask** |

The disjoint regression is the killer: the lever converts **five previously-perfect (0-error) bursts** into error bursts (0→7, 0→9, 0→4, 0→3, 0→3), for a net 12 → 31. This is the exact razor-thin behaviour the §B3.2 anchor-ridge sweep and the closeout warn about — WN2 rides the ridge-8 knee, and any change to the cold-restart regularization that fires in-family moves it off.

## Why the init guard alone is not a fix either

The init guard is mask-neutral (fires only when the init solve is dead, which nominal mask bursts are not) — but it is **insufficient and, in sim, inert**:

- **Natural init-window fades self-heal on `main`.** A census scan of **934 WN2 Poor bursts** at the mask SNR (canonical + a disjoint offset) found **zero `SignalLost`** — a faded init is recovered by the very freshSolve cold-restart above, because the burst *body* is at nominal level so the recovery solve is healthy. So a genuine sim dead-init → `SignalLost` is rarer than 1/934 and the init guard has no realizable sim effect.
- **Real-RF is globally weak**, so the body is low too and the freshSolve recovery is *also* dead — which is why fixing only the init leaves the burst dying at the first fade (the frame trace above). The init guard cannot fix real-RF without the freshSolve fix, and the freshSolve fix regresses the mask.

## The path forward (out of scope for this surgical lever)

A mask-neutral fix must **distinguish global-low-level from a nominal fade** and arm the scale-invariant cold restarts only for the former. The only stable distinguisher is the burst's *healthy* signal reference (fade-independent), i.e. an SNR estimate or a level reference — but any such gate carries an **absolute level threshold that overfits the sim's nominal level and does not generalize to real-RF's arbitrary receive level** (a different radio/attenuator sets a different "nominal"). The robust form is an **input AGC** normalizing the signal to the level the ridges were tuned for (making init, tracking, and freshSolve all see nominal), or a per-probe noise/SNR estimate driving the ridge — both larger, front-end / architecture changes with their own full mask-reverification burden, not a one-lever DFE tweak. Recommended as the Phase-C-class direction; registered here so it is not re-attempted as a ridge tweak.

## Files

- `Ms110dDeadInitTests.cs` (test project) — the red test (RED on main, GREEN with the lever) + the scale/ridge/frame calibration rigs (env-gated).
- Code: `Ms110dDemodulator.cs` (`InitializeDfe` init guard, `ProcessFrame` freshSolve dead-restart guard, `DeadInitResolves` counter) + `Dfe.cs` (`ridgeFromFfBlock` FF-block ridge) + `Ms110dDemodOptions` (`DeadInitFloor`/`DeadInitRidge` A/B knobs). This is the **demonstration branch of the rejected lever**, kept as the investigation artifact — NOT for merge.
- `data/` — the measurements: `scale-calibration-main.log` / `-fixed.log` (the −20 dB reproduction, main vs lever), `ff-scaled-ridge-sweep.log`, `frame-trace-scale0.1.log` (the freshSolve death), `fixed-canonical-3m.log` / `fixed-disjoint-3m.log` (the mask runs), `canonical-perburst-diff.txt` / `disjoint-perburst-diff.txt` (the per-burst regression against b32).
