# FM-native coverage — sim validation + on-air deferral (2026-07-28)

The six FM-native modes (`afsk1200`, `fsk9600`, `fsk4800-il2p`, `c4fsk9600`, `c4fsk19200`, `qpsk3600`) taken through the harness. Two headlines:

1. **The FM modem is sound** — all six decode through the software FM round trip (mod → discriminate → demod), and the `#114` unit tests prove the round trip bit-exact.
2. **On-air is blocked by a Flex transmitter hardware wall, not the modem.** The Flex 6500 (an SSB/voice radio) has no route that faithfully carries wideband FM *data*: the waveform IQ path is single-sideband ≤10 kHz one-sided (too narrow for the two-sided FM Carson bandwidths), and the DAX→NFM route corrupts the data with voice DSP (deviation limiter, pre-emphasis, audio filter). Full analysis in **[issue #118](https://github.com/packet-net/pdn-soundmodem/issues/118)**. Per operator (2026-07-28): sim-validate + defer on-air to FM-capable hardware.

FM is **CFO-immune** (the discriminator turns a carrier offset into a harmless DC in the recovered audio), so — unlike the narrow SSB modes (#116) — the RSP1 drift is *not* the FM blocker. The transmitter is.

## Sim AWGN thresholds

Two instruments, because the fast classic-HDLC FSK detectors need a preamble run-in the baseband `sim` command does not render (TXDELAY 0 → 2 opening flags):

**`sm-ota sim`** (CFO-free baseband BER-vs-SNR, 100 bursts/pt) — the modes that lock without a long run-in:

| mode | AWGN 90 %-frame |
|---|---|
| `afsk1200` | ~+8 dB (100 % @ +9) |
| `fsk4800-il2p` | ~+11 dB (96 % @ +12) |
| `qpsk3600` | ~+14 dB (96 % @ +15) |

**FM dry-run ladder** (full FM round trip with a 150 ms run-in — the faithful FM instrument, includes the discriminator noise) — the fast/wideband FSK modes:

| mode | Δf (kHz) | AWGN decode threshold |
|---|---|---|
| `fsk9600` | 2.4 | ~+15–18 dB |
| `c4fsk9600` | 2.5 | ~+24–30 dB |
| `c4fsk19200` | 5.0 | ~+30 dB |

The wideband modes' high thresholds are the **FM discriminator noise triangle** — the discriminator amplifies high-frequency noise, so a wide-audio FM data mode pays a real bandwidth-for-threshold penalty. This is physically expected, not a defect; it is also part of why these modes want a clean, faithful FM transmitter on-air.

## Poor (D.6.1: 2-path / 2 ms / 1 Hz fade)

Fails for all six FM modes (afsk1200 best 7 % @ +15 dB; the rest 0 % in-range) — as with the SSB audio modes, these narrowband FM data carriers have no equaliser/interleaver for frequency-selective fading. Poor is a mode limitation.

## Data

- `data/*-sim.csv` — the `sm-ota sim` per-point CSVs for all six modes (the fast-FSK three read 0 % from the TXDELAY-0 artifact — see above).
- `data/sim-command-output.txt` — the run log.

## Bottom line for the coverage matrix

- **All six FM modes: FM modem proven in sim; AWGN thresholds above; D.6.1 Poor a mode limit.**
- **On-air: deferred to FM-capable hardware — Flex 6500 TX hardware wall (#118).**
