# MS110D OTA lab campaign — first off-air characterization (2026-07-27)

The first end-to-end over-the-wire characterization of the MS110D modem: transmitted through a real FlexRadio, down a wired attenuator chain, captured on a real SDR, and scored offline against the reference bits. Off-air throughout (no antenna), so this is Phase 0 — it exercises the real transmit chain and capture tooling, not the ionosphere.

## Bottom line

- **On AWGN the real deployment path reproduces simulation.** WN4 (BPSK r2/3), WN6 (QPSK r3/4) and WN13 (QPSK r9/16) all gave real BER-vs-SNR waterfalls whose coded knees sit at the expected thresholds, in the coding-theory-correct order (WN13's stronger code beats WN6 despite both being QPSK).
- **The DAX deployment path is bench-equivalent when driven right.** Driving the DIGU audio hard (0.9) costs ~1.2 dB to ALC compression; backing off to ~0.7 recovers it entirely. This is the single most important operational finding for real deployment.
- **On the Poor (Watterson) channel, most modes fail on the real rig — and instrumentation localized it to DFE fade-tracking, not acquisition.** WN2, WN6 and WN13 all fail over real Poor RF while **WN4 survives**. Diagnostic instrumentation (a `FrameDiagnostics` surface through the harness) refuted the acquisition story outright: **the demod acquires and locks the correct waveform on every failing burst** — preamble metric 0.6–0.9, CFO clean (2–9 Hz), WID vote margins 0.37–0.89 (3–4× above the 0.20 confidence floor), no gate ever rejects. The "WID MISMATCH" in the score table is a **scorer artifact** (the harness records a null WID when nothing decodes, even though the lock was correct). The real failure is the **DFE equalizer failing to track/re-fit through the Watterson fades on the real-RF signal**, while it sails through the *identical* injected fading in simulation. Two sub-signatures: WN2 (K=48) initialises the equalizer *dead* (fitted gain ~50× low — the strong K=48 init ridge over-regularising when the fit window's SNR dips); WN6/WN13 (QPSK) initialise healthy but collapse into the first fade and never recover; WN4 both initialises healthy *and* recovers. The wired attenuator chain has **no multipath**, so the differentiator was narrowed to reference phase-noise or a level/threshold interaction — and a synthetic-injection experiment (`phase-noise-experiment.md`) then **resolved it into two distinct mechanisms**: **(1) WN6/WN13 (QPSK) = reference phase-noise.** Injecting a Wiener random-walk phase into the *clean sim* capture reproduces the real-RF DFE collapse exactly (gain pins 0.005–0.02, `bad` climbs to SignalLost, coded BER ~0.5), reproducibly, at an effective linewidth of **1–3 Hz** (WN13 breaks at 3 Hz, the weaker-coded WN6 at 1–2 Hz) — squarely inside the rig's measured 0–8 Hz reference wander. **A GPSDO-disciplined reference is the indicated fix.** **(2) WN2 (K=48) = its own init-ridge, not the reference.** WN2 already runs on the edge of collapse in *clean sim* (init gain ~0.04, `bad` 43/100); the aggressive K=48 `initRidge=1.0`/`trackRidge=8.0` (from Phase B's WN2-at-mask fix, vs 1e-3/0.15 for the QPSK K=32 modes) leaves almost no margin, and the real path's slight level/margin difference tips it over — reproduced by level-scaling alone, unaffected by phase-noise. **A modem robustness tweak (soften the ridge / add a gain floor) is the fix, independent of the reference.** Every mode still decodes clean in the dry-run, so this is a rig/receiver interaction plus one modem robustness edge, not a modem-in-isolation fault.

## The rig

```
FLEX-6500 (ANT2) → 15 dB/50 W → 10 dB/5 W → 50 dB/2 W → 50 dB/2 W → SDRplay RSP1
                    ─────────────── 125 dB total, wired, off-air ───────────────
```

- **Deterministic and safe.** At the campaign's ~6.8 W (38.3 dBm) the RSP1 sees ~−87 dBm — a strong, repeatable level set by fixed pads, not stray coupling. Every pad sits far inside its rating (the 5 W pad sees ~0.2 W). The RSP1 is on **ANT2**; ANT1 is the dummy-load/UberSDR path. Transmit power is guarded in software (measured-FWDPWR abort) and by the radio's own 15 % cap.
- **Receiver: studybox** (Ubuntu, RSP1), brought up this session: SDRplay API 3.15 + SoapySDR + SoapySDRPlay3 + rx_tools, captured as CF32 streamed over SSH into the sm-ota scorer's WAV contract.
- **AGC defeated.** The stock `rx_sdr` couldn't disable the RSP1 AGC (it never called `setGainMode`); patched at **github.com/M0LTE/rx_tools** (`agc-gainmode`) so a fixed IFGR is honoured. Standardised on **IFGR=20** — live-validated to reproduce the level the AGC had been self-selecting, now deterministic (noise floor tracks IFGR ~1 dB/unit).
- **Frequency calibrated.** The combined Flex-TX + RSP1-RX offset read +44 Hz at 18.1 MHz. Nulled via the Flex `freq_error_ppb` calibration (−1497 → **+934**, set over the API since the SmartSDR UI needs a reference we don't have); residual CFO 0–8 Hz and wandering a few Hz with the RSP1's own reference. Sign convention measured: more-negative ppb *raises* the frequency, ~0.0181 Hz/ppb.
- Operating frequency moved off the 17 m FT8 spot (18.100) to **18.106500** mid-campaign.

## Transmit paths

The harness offers two routes, both re-founded this session on the corrected **M0LTE.Flex 0.8.x** model (the waveform path is single-sideband, ≤10 kHz one-sided — the old "true wideband" claim was retracted):

- **DAX (deployment, default):** real audio through the radio's own DIGU SSB modulator. What a fielded modem actually keys. Native 1800 Hz sub-carrier placement (no software offset).
- **IQ (bench instrument):** complex IQ through a headless RAW waveform, bypassing the radio's SSB modulator, ALC and TX DSP. Software SSB with a +2000 Hz NCO offset.

The manifest/scorer offset is derived from the route, so a DAX pass and an IQ pass score identically without operator intervention. Confirmed: dry-run rehearsals of both routes score at zero BER.

## Findings

### 1. DAX ALC cost — real, and mitigable by drive level

Live DAX initially read ~1.2 dB low at the bottom of the ladder versus the IQ bench route (6 dB → 4.8). Isolated by experiment:

| Test | Result |
|---|---|
| RSP1 tuned 2 kHz off its DC spur | penalty **unchanged** → not the DC spur |
| `--audio-amplitude` sweep 0.9 / 0.7 / 0.5 | 0.9 → 1.2 dB penalty; **0.7 and 0.5 → penalty gone** |

So the cost is **ALC compression on the hot audio drive into DIGU**, not an inherent property of the deployment path. QPSK/higher-PAPR modes are more sensitive, so the campaign drives at **0.7** (below the ALC knee, which sits between 0.7 and 0.9) and raises *output power* (rfpower) rather than drive for margin. The self-calibrating ladder makes measured SNR drive-independent except for exactly this compression, which is what let the sweep isolate it cleanly.

### 2. AWGN waterfalls — real path matches simulation

WN4/6/13, AWGN, drive 0.7, ~6.8 W, capture peaks ~−19 dBFS (no clipping):

| Mode | Real-path coded knee | Design AWGN mask | Reading |
|---|---|---|---|
| WN4 BPSK r2/3 | below 2 dB | +5 dB | clean 2→8 dB |
| WN6 QPSK r3/4 | ~5–6 dB (fail 4, clean 7) | +9 dB | knee below mask, with margin |
| WN13 QPSK r9/16 | ~2–3 dB (fail 0, clean 3) | +6 dB | knee below mask; stronger code beats WN6 |

Every knee is below its design mask with margin, and SNR reads accurately (asked ≈ got) — the real chain, driven correctly, reproduces the simulator.

### 3. Poor channel — BPSK survives, QPSK fails on the real rig

Watterson "Poor" fading injected at the transmitter, Long interleaver, at/around each mode's Poor mask SNR:

| Mode | Over real RF | Same waveform, dry-run (sim) |
|---|---|---|
| WN4 BPSK r2/3 | **8 of 9 bursts coded-clean** (0 errors through 8–25 % uncoded fading); DFE recovers from fades | clean |
| WN2 BPSK r1/4 | **all fail — locks WN2 correctly, then DFE initialises dead** (gain ~50× low, K=48 ridge) → SignalLost | (not dry-run; decodes at mask in Phase B) |
| WN6 QPSK r3/4 | **all fail — locks WN6 correctly, DFE collapses into first fade, never recovers** → SignalLost | **clean** (coded 0) |
| WN13 QPSK r9/16 | **all fail — locks WN13 correctly, DFE collapses into first fade, never recovers** → SignalLost | **clean** (coded 0) |

The failure was diagnosed by elimination. It is **not**:

- **the waveform or config** — the identical WN6/WN13 Poor bursts decode clean in a pure-simulation dry-run;
- **TX audio processing** — the IQ route (which bypasses the SSB modulator, ALC and TX DSP entirely) fails identically to DAX;
- **the drive/ALC** — dropping WN13 to 0.5 drive still fails;
- **RX clipping** — captures peak ~−18 dBFS with zero clipped samples;
- **the delivered level / gain policy** — the failing captures are level-for-level and fade-depth-for-fade-depth identical to WN4's working one (peak ~−18, active-median ~−32, ~7–8 dB fades);
- **the long burst / RX drift alone** — WN13 QPSK on the **Long interleaver over AWGN** decodes 6/6 clean over real RF, so the long-burst duration and slow drift are not the cause; the **fading is essential**;
- **a BPSK-vs-QPSK split** — WN2 is BPSK and fails, so my first-pass "QPSK-only" reading was wrong.

What is established (now with instrumentation, not inference): the failure requires **both the real RF path and the fading** (either alone works); **acquisition is not the problem** — every failing burst locks its scheduled waveform with a confident WID vote and a clean CFO; the failure is the **DFE equalizer's per-frame fit/track collapsing through fades** on the real-RF signal, which the same equalizer survives on the *identical* injected fading in simulation (sim DFE gain snaps back from every deep fade; real-RF DFE pins low and rides `bad`-probe count to the SignalLost limit). WN4 survives because its DFE both initialises healthy and recovers; WN2 dies at init (K=48 ridge over-regularisation), WN6/WN13 die at the first fade. Which property of the real-RF signal breaks the fit is now **established by the phase-noise-injection experiment** (multipath already excluded by the wired chain): **WN6/WN13 are reference phase-noise** — injecting a 1–3 Hz-linewidth Wiener phase into the clean sim reproduces the collapse exactly, within the rig's measured wander, so a **GPSDO on the reference is the fix**; **WN2 is the K=48 init-ridge** — it runs near-collapse even in clean sim and is tipped over by level, not phase, so it needs a **modem ridge/gain-floor tweak** regardless of the reference. Prediction to verify: a GPSDO recovers WN6/WN13 Poor; WN2 Poor needs the ridge softened. See `phase-noise-experiment.md` for the numbers.

## Harness & library work shipped this session

- **M0LTE.Flex 0.3.0 → 0.8.2 consumed**, and two library bugs fixed and released upstream (Tom-authorised): the 0.8.1 waveform starve-tail over-count, and **0.8.2** — `FlexStation.DisposeAsync` now removes the headless slice it created (it was leaking one slice per pass to the radio's 4-slice limit).
- **OTA harness re-founded** on 0.8.x (PR #100, merged): DAX-first with `--route dax|iq`, RSP1 capture backend (`RspIqClient`, streamed rx_sdr), route-aware offset, a measured forward-power ceiling, `--audio-amplitude`, and the raised RSP1 power limits.
- **rx_tools AGC patch** forked to m0lte and installed on studybox.

## Operational notes (for the next session)

- `rx_sdr` survives SIGTERM (needs `kill -9`); an abrupt kill leaves the SDRplay device stuck until `systemctl restart sdrplay`. Do not interrupt passes — give the tool timeout ≥ the pass length.
- Manually clear leaked Flex slices with `slice remove <idx>` (verify SmartSDR — the only *connected* client — owns none; it runs panadapter-only). The 0.8.2 fix makes this rare.

## Next steps

1. **Apply the two fixes now that the Poor failure is fully diagnosed** (acquisition proven fine; DFE fade-tracking mechanism confirmed for both families): **(a)** GPSDO-discipline the reference and re-run — the confirmation-and-fix for WN6/WN13 (predicted to recover); the GPSDO is on the bench, gated only on GPS lock. **(b)** Soften the K=48 init/track ridge (or add a gain floor) — a modem robustness tweak for WN2, whose Phase-B at-mask ridge leaves it on the edge of collapse even in clean sim; this is independent of the reference and worth revisiting against the Phase-B WN2 mask evidence. Until (a) lands, over-air QPSK-Poor is reference-limited (not modem-limited); WN2-Poor is the one genuine modem robustness edge the campaign surfaced.
2. **Phase 1 NVIS** over a real ionospheric path, once the reference question is settled.
3. **Preserve the capture corpus** — these WAVs are permanent regression fixtures re-scoreable against every future modem build; the failing Poor captures are especially worth keeping for offline re-analysis with better instrumentation.
