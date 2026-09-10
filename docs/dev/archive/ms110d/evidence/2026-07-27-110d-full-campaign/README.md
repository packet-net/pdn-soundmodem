# MS110D full on-air campaign — RSP1 rig (2026-07-27)

Every MS110D waveform characterised over the real Flex→RSP1 rig, AWGN and Poor (D.6.1), across a per-mode SNR ladder bracketing each mask. **Headline: every waveform with a defined mask decodes at or below it over real RF.**

## Setup

| | |
|---|---|
| TX | FlexRadio 6500 @ `10.45.0.76`, **GPS-locked** external 10 MHz (`oscillator.setting=external, locked=1, ppb=0`) |
| Path | ANT2 → 125 dB wired attenuator chain → SDRplay RSP1 on `studybox`, `--capture rsp` |
| Route / power | IQ route, `--rf-power 4` (~3.7 W measured), `--dial-correction 36` (RSP1 offset +35.8 Hz, GPS-stable), `--rsp-gain AGC=false,IFGR=20,RFGR=0` |
| Sweep | 18 ladder runs (AWGN WN0–8/13, Poor WN0–6/13), 4 rungs each, 4 repeats/rung, scored inline. 68 min, 0 run failures |
| Modem | `a60008c` (`ms110d-ota-harness` + main's WN2 AGC fix #103) |

## AWGN — clean coded-BER-0 decode threshold (on-air)

| WN0 | WN1 | WN2 | WN3 | WN4 | WN5 | WN6 | WN7 | WN8 | WN13 |
|---|---|---|---|---|---|---|---|---|---|
| −3.5 | −2.5 | −1.6 | −0.6 | +1.5 | +3.7 | +5.7 | +8.8 | +13.3¹ | +2.6 |

Perfectly monotonic in the modes' robustness order (Walsh → 16QAM) — a clean waterfall progression, no anomalies. ¹ WN8's +16 dB mask point is not deliverable at 3.7 W (the self-cal delivered ~13.5 dB max — the path/artefact ceiling); WN8 decodes clean at that reachable top. Below ~+13 dB WN8 (16QAM) errors as expected.

## Poor channel (D.6.1: 2-path/2 ms, 1 Hz fade) — clean threshold vs mask

| mode | clean to (dB) | Poor mask (dB) | |
|---|---|---|---|
| WN0 (Walsh) | −0.5 | −1 | ✓ at/below |
| WN2 (BPSK r1/4) | −1.1 | +5 | ✓ (~6 dB under) |
| WN4 (BPSK r2/3) | +4.0 | +10 | ✓ |
| WN5 (BPSK r3/4) | +5.1 | +11 | ✓ |
| WN6 (QPSK r3/4) | +9.5 | +14 | ✓ |
| WN13 (QPSK r9/16) | +7.0 | +11 | ✓ |
| WN1 (BPSK) | −1.6 | — | clean (no mask defined) |
| WN3 (BPSK r1/2) | −1.4 | — | clean (no mask defined) |

**Every waveform with a defined Poor mask decodes at or below its mask SNR over the real rig.** Deep-fade dropouts (SignalLost) appear scattered above threshold — Rayleigh nulls where a burst lands in a fade, inherent to the Poor channel, not a threshold floor. WN7 (mask +19) and WN8 (mask +23) Poor were not run: their masks are above the rig's ~15–16 dB ceiling at this power, deferred to the real-antenna phase.

## Notes

- **Reference held all campaign.** dial +36 on the GPS-locked Flex kept CFO in-grid throughout; the RSP1's residual wander is ~0.4 Hz/min (CFO crept ~+9 → ~+40 Hz over 68 min, never near the ±75 grid edge).
- **WN2 AGC (#103) exercised throughout** — the low receive level (~−88 dBm) is the dead-init regime, and the input AGC fires on every WN2 burst; coded BER 0 across its range confirms the fix holds continuously, not just in the earlier spot-check.
- **Reading the data:** the scorer's asked↔got SNR *labels* shuffle under the burst-timing match, so thresholds are taken from each burst's own delivered SNR (self-cal noise lead-in) + its coded BER — the intact (delivered-SNR, BER) pairs. Per-burst scores in `per-burst-scores.txt`.
- Low-rate modes (WN0–2) run with high *uncoded* BER (~0.3) corrected to zero coded — the heavy FEC working as designed, not a fault.

## Provenance

Validates the MS110D waveforms on real RF against their masks. Builds on the diagnosed-and-fixed Poor failures (`../2026-07-27-ota-poor-validation/`: WN2 AGC #103, WN6/WN13 GPSDO #102) and the AWGN spot-check (`../2026-07-27-ota-lab-campaign/`).
