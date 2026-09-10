# MS110D Poor-channel DFE collapse — the hardware-free discriminator (2026-07-27)

A decisive, hardware-free experiment to identify *which property of the real-RF signal* breaks the MS110D DFE equalizer's fade-tracking on the Poor (Watterson) channel, where WN2/WN6/WN13 fail over real RF while WN4 survives — and while the *identical* injected fading decodes clean in pure simulation.

Established going in (see this dir's `README.md`): the failure is **not acquisition** (every failing burst locks its scheduled waveform with a confident WID vote and clean CFO), it is the **DFE per-frame fit/track collapsing through the fades on the real-RF signal**. The wired attenuator rig has **no multipath**, so the real-vs-sim differentiator was narrowed to exactly two candidates: **reference phase-noise** on the recovered signal, or a **level / absolute-threshold interaction**. This experiment discriminates them by taking the clean sim path (which decodes) and perturbing it in each candidate way.

## Method — pure post-processing, no modem change

- **Controls:** render the sim Poor/Long bursts at their campaign SNRs (`sm-ota ladder --dry-run`) and score them with `sm-ota score --diagnostics`, reading the per-frame DFE trace (`gain` / `ref` / `bad`). These decode coded-BER 0 with the DFE gain snapping back from every fade.
- **Injector:** `tools/phase-noise/inject.py` reads the sim capture WAV (16-bit interleaved stereo = complex I/Q), perturbs the complex samples, writes a new WAV the scorer reads identically. Three perturbations: **phase** (Wiener random-walk φ, knob = Lorentzian FWHM linewidth Δf Hz), **bandlimited** (stationary filtered-white phase, spectrum cross-check), **level** (pure amplitude scale). No modem code touched — the DFE is exercised against a controlled perturbation of a signal it decodes clean.
- **Read-out:** `tools/phase-noise/dfesum.py` compresses the verbose `--diagnostics` trace to one line per burst: coded BER + end reason, first/min/median DFE gain, reference gain, peak `bad`-probe count, fraction of frames pinned below the 0.10 collapse line.

Frame geometry (sets the per-frame RMS-phase conversion, `sqrt(2π·Δf·T_frame)`): WN2 U48+K48 = 96 sym = **40 ms**; WN6/WN13 U256+K32 = 288 sym = **120 ms**. `bad`-probe SignalLost limit is wall-clock: **34** for the QPSK modes, **100** for WN2.

## Result 1 — phase-noise reproduces the WN6/WN13 QPSK fade-collapse (decisively)

Clean control vs. the phase-noise break, burst 0 of each mode (seed-robust across seeds 1/42/100; ordering across seeds within ±0.1 rad):

| Mode | Perturbation | coded BER | end | g0 | ref0 | g_min | g_med | bad_max | pinned<0.10 |
|---|---|---|---|---|---|---|---|---|---|
| WN13 r9/16 | clean | **0** | Eom | 0.660 | 0.852 | 0.076 | 0.416 | 3 | 2% |
| WN13 | Δf=2.5 Hz (1.37 rad) | **0** | Eom | 0.639 | 0.859 | 0.054 | 0.361 | 4 | 3% |
| WN13 | **Δf=3 Hz (1.50 rad)** | **0.48** | **SignalLost** | 0.632 | 0.853 | 0.005 | 0.254 | **33** | 38% |
| WN6 r3/4 | clean | **0** | Eom | 0.728 | 0.930 | 0.127 | 0.439 | 4 | 0% |
| WN6 | Δf=1 Hz (0.87 rad) | 0 / **0.49** | mixed | 0.72 | 0.77 | 0.016 | 0.24 | **33** | 35% |
| WN6 | **Δf=2 Hz (1.23 rad)** | **0.48** | **SignalLost** | 0.703 | 0.940 | 0.015 | 0.284 | **33** | 35% |

The break signature is **identical to the real captures in every diagnostic**: the equalizer **initialises healthy** (g0/ref0 unchanged from clean — the training solve happens on the clean-level probe before phase accumulates), then the **tracking collapses** — gain pins to 0.005–0.02, median gain falls from ~0.42 to ~0.2, `bad` climbs monotonically to the SignalLost limit (33), coded BER dead at ~0.5. This is exactly "locks the waveform, DFE collapses into the first fade, never recovers → SignalLost."

**Thresholds track coding strength.** WN13's stronger r9/16 code holds to Δf≈2.5 Hz; WN6's weaker r3/4 breaks at Δf≈1–2 Hz — the same order as the campaign's AWGN waterfalls (stronger code beats weaker despite both being QPSK). The **band-limited** cross-check (corner 20 Hz) breaks WN13 at total RMS ≈ 0.6 rad with the same signature, so the driver is **phase-noise per se, not the Wiener model**.

### Plausible for the RSP1? — Yes, right at the measured budget

Break is at **Δf ≈ 1–3 Hz linewidth**, i.e. a **per-frame (120 ms) RMS phase of ~0.9–1.5 rad (50–86°)**, or a short-term RMS of ~0.6 rad for a bounded 20 Hz-corner jitter. The campaign measured the rig at **"residual CFO 0–8 Hz and wandering a few Hz with the RSP1's own reference."** A few Hz of undisciplined short-term reference wander **is** a ~1–3 Hz effective linewidth on the recovered signal — the QPSK break threshold sits **squarely inside the rig's measured phase-wander budget**. The demod's CFO/timing loops already track the slow component (that is why Δf≤1 Hz survives); what breaks the DFE is the fast residual the loop can't follow within a frame. → **disciplining the reference (GPSDO) is the indicated fix for the QPSK collapse.**

## Result 2 — level-scaling reproduces WN2's dead-init (different mechanism)

WN2 (K=48, BPSK r1/4), pure amplitude scale down, no phase perturbation:

| Perturbation | coded BER | end | g0 | ref0 | g_min | g_med | bad_max | pinned<0.10 |
|---|---|---|---|---|---|---|---|---|
| clean | **0** | Eom | 0.043 | 0.063 | 0.005 | 0.126 | 43 | 41% |
| ×0.5 (−6 dB) | 1.0 (weak burst) | SignalLost | 0.016 | 0.026 | 0.001 | 0.034 | **99** | 92% |
| **×0.1 (−20 dB)** | **1.0** | **SignalLost** | **0.005** | 0.014 | 0.004 | 0.011 | **99** | 100% |

Level-scaling down reproduces WN2's real-RF **dead-init** exactly: at −20 dB, g0 = **0.005** — the very number the real WN2 captures showed — with `bad` pinned at the WN2 SignalLost limit (99/100) from the first frames. Note WN2 already runs **near collapse in clean sim** (g0≈0.04, riding bad=43/100): its DFE has almost no gain margin. Phase-noise on WN2 needs Δf≈10 Hz (implausible) to break it and does **not** depress the *init* gain, so phase-noise does not explain WN2's dead-init.

**Mechanism (confirmed in source).** `Ms110dDemodulator` gives K=48 (WN1/2) an `initRidge = 1.0f` / `trackRidge = 8.0f`, versus `1e-3f` / `0.15f` for the K=32 QPSK mode — the ridge is λ = `regularization·trace/n` (`Dfe.SolveTraining`), so K=48's λ equals the **mean diagonal (signal+noise) energy**, a deliberately huge regulariser (the source comment records it was tuned on a WN2 +5 dB sweep to make the anchored equalizer "coast instead of chase fades"). That is why WN2's fitted gain is suppressed to ~0.04 even healthy, and why a modest absolute-margin loss tips it into dead-init.

### Why level is *not* the QPSK cause

Level-scaling **also** breaks the QPSK modes — but at **≤ −14 dB below the working level** and with a **distinct signature**: it depresses g0/ref0 **from the start** (WN13 ×0.2 → g0 0.54/ref 0.72; ×0.02 → g0 0.03), i.e. dead-init, *not* the healthy-then-collapse pattern the real QPSK captures showed. The real failing captures were **level-for-level identical to WN4's working one** and showed **healthy QPSK init**, so level is excluded for QPSK by the level-matched evidence — leaving phase-noise.

## Verdict

Two failing families, **two different root causes**:

- **WN6 / WN13 (QPSK) fade-collapse ⟵ reference PHASE-NOISE.** Reproduced at Δf ≈ 1–3 Hz (per-frame RMS ~0.9–1.5 rad), plausible for the RSP1's undisciplined reference (rig measured a few Hz of wander), signature identical (healthy init → tracking collapse → `bad`→limit → SignalLost), seed-robust and spectrum-robust. **Fix: discipline the reference (GPSDO).**
- **WN2 (K=48) dead-init ⟵ the K=48 `initRidge`/`trackRidge` over-regularising at low absolute margin.** A level/absolute-scale interaction, not phase-noise. **Fix: a modem robustness tweak** — the K=48 mode runs with essentially no gain margin by design; make the ridge less punishing at low absolute energy (scale toward the signal energy, or add a fitted-gain floor / relax the initial ridge), and re-check the WN1/WN2 static gates it was tuned against.

The confident prediction: a GPSDO on the RSP1 reference recovers WN6/WN13 Poor; WN2 Poor needs the DFE ridge softened regardless of the reference.

## Reproduce

```
# clean control
sm-ota ladder --wn 13 --snr 9,11,13 --channel poor --interleaver long --route dax --dry-run --out sim-wn13.wav
sm-ota score --diagnostics --in sim-wn13.wav --schedule sim-wn13.manifest.json | tools/phase-noise/dfesum.py
# phase-noise break
tools/phase-noise/inject.py --in sim-wn13.wav --out pert.wav --mode phase --linewidth-hz 3 --seed 7
sm-ota score --diagnostics --in pert.wav --schedule sim-wn13.manifest.json | tools/phase-noise/dfesum.py
# WN2 dead-init via level
tools/phase-noise/inject.py --in sim-wn2.wav --out pert.wav --mode level --factor 0.1
sm-ota score --diagnostics --in pert.wav --schedule sim-wn2.manifest.json | tools/phase-noise/dfesum.py
```
