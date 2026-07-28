# bpsk300 (PSK300) coverage — on-air AWGN waterfall + Poor mode-limit (2026-07-28)

`bpsk300` — 300 Bd BPSK, IL2P+CRC, the **primary NinoTNC live-network mode** — taken through the OTA harness for a proper BER-vs-SNR waterfall. It already had a real GB7RDG off-air *decode* (`OffAirBpskTests.cs`, #40/#42), but no rig characterisation; this adds one.

**Headline: AWGN validated on-air with a robust waterfall (~−3 to −4 dB threshold); Poor (D.6.1) is a mode limitation** — its differential detector + 4-pair frequency-diversity bank ride *carrier offset* but not *multipath fading*, so like the other SSB audio modes it has no answer to D.6.1.

## On-air (Flex GPS-locked → 125 dB → RSP1, IQ route, ~11.8 W, dial 0)

The RSP1 offset had **thermally settled back to ~0** (CFO ±7.5 Hz — the earlier drift was rx_sdr-cycling disturbance, gone after ~90 min undisturbed), and bpsk300 rode it cleanly. High-SNR pilot: 6/6 decode, self-cal **0.10 dB**.

### AWGN waterfall (42 bursts, asked +3 … −15 dB, 6 repeats)

| asked SNR | delivered (`got`) | decode |
|---:|---:|:--:|
| +3 | ~+1.1…+1.7 | 6/6 ✓ |
| 0 | ~−0.8…−1.5 | 6/6 ✓ |
| −3 | ~−4.0…−5.1 | ~2/6 (marginal) |
| −6 | ~−8 | 0/6 |
| ≤ −9 | — | 0/6 |

**Clean decode to delivered ≈ −1.3 dB; ~50 % threshold ≈ −3 to −4 dB (3 kHz-ref)** — a robust negative-SNR mode (IL2P Reed-Solomon FEC + narrow ~400 Hz 300 Bd occupancy; in-band SNR runs ~8 dB above the 3 kHz-ref figure). The 14/42 overall reflects the deliberately deep ladder (many rungs sit below threshold by design). Delivered SNR reads ~1.5 dB under asked at the low rungs (a small self-cal bias where the signal is near the floor; the pilot's 0.10 dB is the clean high-SNR reference).

### Poor (D.6.1: 2-path / 2 ms / 1 Hz fade) — mode limit

**2/36 decoded**, only at delivered ~+7 dB; most bursts acquired but LOST. The 300 Bd symbol (3.3 ms) sees ~0.6-symbol ISI from the 2 ms multipath plus the 1 Hz fade, and bpsk300 has **no equaliser** — the diversity bank addresses carrier offset, not multipath. The two decodes are bursts that landed in a favourable fade. As with bpsk1200 / qpsk600 / afsk300, **Poor is a mode limitation, not a rig fault.**

## Sim cross-reference (`sm-ota sim`, CFO-free, 100 bursts/pt)

| channel | success vs SNR (3 kHz-ref) |
|---|---|
| AWGN | 100 % @ 0 dB · **88 % @ −3** · 0 % @ −6 → 90 % threshold ≈ −3 dB |
| Poor | 11 % @ +3 · 3 % @ 0 · ≤1 % below — **fails** |

The sim **confirms both on-air findings**: the AWGN 90 % threshold ≈ −3 dB matches the rig (clean decode to delivered ~−1.3, marginal at ~−4), and Poor failing in sim (11 % best) matches the rig's 2/36. Data: [`data/bpsk300-sim.csv`](data/bpsk300-sim.csv).

## Bottom line for the matrix

- **`bpsk300` (+ `bpsk300-multi`): AWGN validated on-air** with a BER-vs-SNR waterfall, adding to the existing real GB7RDG off-air decode. **Poor is a mode limitation** (sim-confirmed).
