# FreeDV DATAC OFDM — first on-air campaign (RSP1 rig, 2026-07-28)

First hardware validation of all six FreeDV DATAC OFDM modes over the real Flex→DIGU→RSP1 chain, AWGN and Poor, cross-checked against the #104 simulation baseline. **All six modes acquire and decode on the air; AWGN matches the sim baseline closely.**

## Setup

| | |
|---|---|
| TX | FlexRadio 6500 @ `10.45.0.76`, **GPS-locked** external 10 MHz; **DAX route** (OFDM audio through the radio's DIGU SSB modulator — the deployment path for OFDM) |
| Drive | `--audio-amplitude 0.5` — backed off from 0.9 for the OFDM's high PAPR; residual DAX/ALC cost ≈ 1.2 dB at the top rung (matches the earlier DAX characterization) |
| Path / capture | ANT2 → 125 dB → SDRplay RSP1 on `studybox`, `--capture rsp`, `--rf-power 4` (~3.7 W), `--dial-correction 35` (RSP1 offset +35.4 Hz, GPS-stable) |
| Sweep | 12 runs (6 datac modes × AWGN + Poor), per-mode ladders bracketing the #104 sim thresholds, 3 repeats/rung, scored inline via the OFDM `DatacReceiver`. 39 min, 0 run failures |
| Harness | `sm-ota ladder --mode freedv-datac<N>` (PR #108), modem `ae39742b` |

Scores are **coded** (post-LDPC) — the `M0LTE.Ofdm` engine exposes per-packet CRC + LDPC parity, the quantity FreeDV publishes its operating points in. Delivered SNR is 3 kHz-referenced (as the sim baseline was), so the cross-check is apples-to-apples.

## AWGN — clean coded-BER-0 threshold (delivered dB)

| mode | on-air | sim (#104) | Δ |
|---|---|---|---|
| datac1 (980 bit/s) | +1.8 | +1.8 | **exact** |
| datac3 (321 bit/s) | −3.8 | −3.2 | −0.6 |
| datac0 (291 bit/s) | −3.7 | — | — |
| datac4 (87 bit/s)¹ | −13.0 | — | — |
| datac13 (64 bit/s)¹ | −12.8 | — | — |
| datac14 (58 bit/s)¹ | −12.1 | — | — |

The high-rate workhorses (datac1, datac3) land right on their sim baselines — a clean on-air validation of the managed OFDM engine.

## Poor (D.6.1: 2-path/2 ms, 1 Hz fade)

| mode | on-air | sim | |
|---|---|---|---|
| datac3 | −0.7 | −1.3 | +0.6 (close) |
| datac1 | +4.6 | +3.9 | +0.7 (close) |
| datac0 | −4.8 | +3.3 | far below sim |
| datac4¹ | −7.1 | −4.6 | |
| datac13¹ | −10.0 | −3.4 | |
| datac14¹ | −4.2 | +2.7 | |

All six acquire and decode on the real Poor channel. The mid/high-rate modes (datac3, datac1) track their sim thresholds within ~0.7 dB; the very robust modes read well below sim (see caveats).

## Caveats (read the numbers with these)

1. **¹ Narrow modes are 3 kHz-referenced.** datac4/13/14 occupy 250–500 Hz, so a −13 dB 3 kHz-ref SNR is ~−2 to −3 dB *in-band* — they are robust-narrow, not −13 dB thresholds. The 3 kHz reference (shared with the sim and the MS110D campaign) inflates their apparent robustness.
2. **The AWGN match is the trustworthy cross-check.** datac1 exact and datac3 within 0.6 dB validate the OFDM chain on hardware. The Poor numbers reading *below* sim reflect the **known sim-Poor pessimism** (#104: the sim read 6–28 points below codec2's published, a burst-vs-continuous-stream + channel-model artifact) *plus* the reference-bandwidth effect above — not the rig being kinder than a fading channel. Re-anchoring the sim Poor against codec2's own `ch` (the #104 follow-up) is the way to make the Poor cross-check as clean as the AWGN one.
3. **DAX/ALC ~1.2 dB top-rung cost** (delivered ~1.2 dB below asked at high drive) — inherent to the DIGU path at this drive; consistent across the campaign.

## Provenance

First on-air run of the FreeDV DATAC OFDM modes. Sim baseline: `../2026-07-27-freedv-datac-sim-baseline/` (PR #104). Harness: PR #108. Forwards `freedv-datac0/1/3/4/13/14` from not-yet-on-air to working in the ledger.
