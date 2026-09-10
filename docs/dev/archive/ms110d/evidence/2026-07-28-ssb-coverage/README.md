# SSB audio-carrier coverage — sim characterisation + on-air findings (2026-07-28)

The three NinoTNC-lineage SSB audio-carrier modes — `bpsk1200`, `qpsk600`, `afsk300` — taken through the OTA harness (sim + Flex→RSP1 rig). Two headlines:

1. **These are AWGN / clean-channel modes.** In simulation none survive the D.6.1 Poor fading channel — they have no equaliser or interleaver for 2-path multipath, unlike the MS110D (DFE + FEC) and FreeDV-OFDM (cyclic-prefix + LDPC) waveforms.
2. **On-air coverage over the RSP1 rig is bounded by the receiver's undisciplined LO.** `bpsk1200` has wide carrier-offset tolerance and validates on the air; `afsk300`/`qpsk600` are CFO-fragile and are blocked by the RSP1's thermal offset drift — see **[issue #116](https://github.com/packet-net/pdn-soundmodem/issues/116)**. Per operator decision (2026-07-28) these two are characterised in **sim only**, with on-air deferred to a disciplined receiver.

## Sim characterisation (CFO-free)

`sm-ota sim`, 100 bursts/point, frame layer (full IL2P+CRC through the IModem surface), 3 kHz-reference SNR. Data: [`data/*-sim.csv`](data/).

### AWGN — frame success vs SNR

| SNR dB | bpsk1200 | qpsk600 | afsk300 |
|---:|---:|---:|---:|
| 0 | 0 % | 41 % | 0 % |
| +3 | 71 % | 97 % | 0 % |
| +6 | 100 % | 100 % | 55 % |
| +9 | 100 % | 100 % | 100 % |

**90 % frame thresholds: qpsk600 ≈ +3, bpsk1200 ≈ +4, afsk300 ≈ +8 dB.** (afsk300 — 300 baud AFSK — is the least efficient; qpsk600 the most.)

### Poor (D.6.1: 2-path / 2 ms / 1 Hz fade) — all three fail

| mode | best success in −6…+15 dB |
|---|---|
| bpsk1200 | 14 % @ +15 dB |
| qpsk600 | 7 % @ +15 dB |
| afsk300 | 5 % @ +15 dB |

None reaches 50 %, let alone 90 %. This is a **mode limitation, not a rig or harness fault**: these narrowband, uncoded/lightly-coded audio carriers have no mechanism for frequency-selective fading. It is the same reason `bpsk1200` returned **0 Poor decodes on the air** (below) — the mode, not the channel model.

## On-air (Flex → 125 dB → RSP1, IQ route, rf-power 12 ≈ 11.8 W, dial 0)

The Flex TX reference is GPS-disciplined and perfect (`oscillator.state=external, locked=1, freq_error_ppb=0`). The RSP1 receiver LO is **undisciplined** and drifts thermally — its offset had moved from +35 Hz (this morning's MS110D campaign) to ≈ −90 Hz by this session, bouncing ±30–60 Hz between measurements.

| mode / channel | acquired | decoded | note |
|---|---|---|---|
| bpsk1200 AWGN | 7/12 (dial 0) → see clean run below | matches sim (~+3 dB threshold) | CFO −60…−120 Hz on acquired bursts; **rides the drift** |
| bpsk1200 Poor | 2/12 | 0/12 | consistent with sim (mode fails Poor) |
| qpsk600 AWGN/Poor | 0/12 | 0/12 | CFO-fragile — no lock (self-cal 0.9 dB → signal present) |
| afsk300 AWGN/Poor | 0/12 | 0/12 | CFO-fragile — no lock (self-cal 0.35–0.86 dB → signal present) |

### Clean bpsk1200 AWGN waterfall (18 bursts, dial 0, rf-power 12)

| asked SNR | delivered (`got`) | decode |
|---:|---:|:--:|
| +8 | +5.5…+5.8 | 3/3 ✓ |
| +6 | +3.5…+3.7 | 3/3 ✓ |
| +4 | +1.9…+2.1 | 3/3 ✓ |
| +2 | −0.2…−0.3 | 2/3 |
| 0 | −2.0…−2.6 | 0/3 |
| −2 | −4.4…−4.9 | 0/3 |

**11/18 decoded** — clean down to delivered ≈ +2 dB, marginal at ≈ −0.2, gone by −2. Acquired-burst CFO spanned **−120…+60 Hz**: bpsk1200 rides the RSP1 drift. Corrected for the ~2.3 dB self-cal offset (delivered `got` reads ~2.3 dB under asked, `mean |Δ| = 2.29 dB`), the true threshold is ≈ **+3 dB**, matching the sim +3–4 dB. **bpsk1200 AWGN is validated on-air.**

## The CFO wall — issue #116

The sim dry-run (which injects a realistic per-burst CFO modelling the rig) already predicts the on-air result:

- `bpsk1200` decodes at injected CFO −120/−90/−30 Hz — **tolerant to ±120 Hz**.
- `afsk300` decodes **only** the burst that lands at CFO ≈ 0; fails every non-zero injection.

So `afsk300`/`qpsk600` acquisition has essentially no carrier-offset search, and the undisciplined RSP1 (no external-reference input on the basic RSP1) cannot be held at CFO ≈ 0. The signal reaches the receiver at the correct level (self-cal 0.3–0.9 dB) — the demod simply never syncs. Correcting the dial so `bpsk1200` read CFO +30 then −60 Hz kept `bpsk1200` decoding 3/3 but left `afsk300` at 0/6 both times. Full write-up and the future modem-side fix (add CFO tracking to these modes) are in **issue #116**.

## Harness follow-ups surfaced here (tracked in #116)

- **First burst of every ladder run reports delivered SNR ≈ −250 dB** — a first-burst noise-calibration artifact; it inflates the Poor-run self-cal figure to 22–25 dB (all other bursts fine).
- **`bpsk1200` delivered SNR ran ~2.3 dB below asked** on the air (mode-dependent self-cal offset; `afsk300` only 0.9 dB). Thresholds are read off the *measured* delivered SNR, so plots stay valid.

## Bottom line for the coverage matrix

- **bpsk1200 (+ variants): AWGN validated on-air.** Poor is not applicable (mode limitation, sim-confirmed).
- **qpsk600, afsk300: AWGN characterised in sim; on-air deferred (#116).** Poor is a mode limitation for both.
