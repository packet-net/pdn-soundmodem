# NinoTNC mode capture corpus — 2026-07-31

Real NinoTNC transmit-audio captures of every DIP mode, recorded on the studybox bench (NinoTNC N9600A4, firmware v3.44 per the 2026-07-15 flash; DIPs at 1111 = software mode select; CM108 widget, TNC TXA → CM108 IN). Captured by pdn-soundmodem's Claude session; the capture driver is `~/nino_capture.py` beside this file.

## Recording conditions

- TNC TXA range: **MIC (0–200 mV)**; CM108 **Auto Gain Control OFF**, Mic capture 32 (+8 dB) → peak ≈ 0.17–0.28 full-scale (deterministic, no AGC opinion in the levels).
- 48 kHz, 16-bit signed LE, mono.
- KISS settings per capture: **PERSIST 255, SLOTTIME 0**, TXDELAY per the file name.
- Mode set via KISS SetHardware (payload mode+16) and **verified via GETALL** (`BrdSwchMod`) before every capture; two sacrificial frames flush the TNC's stale-parameter behaviour (see Gotchas) before recording starts.

## File naming

`{DIP}-{baud}{modulation}-{framing}-txd{N}ms.wav`, e.g. `1011-2400qpsk-il2pc-txd200ms.wav`. Three TXDELAY tiers per mode — aggressive / standard / conservative — scaled to the symbol rate (acquisition budget ∝ symbol time):

| Baud | aggressive | standard | conservative |
|---|---|---|---|
| 300 | 240 ms | 500 | 1000 |
| 600 | 160 | 300 | 600 |
| 1200 | 120 | 240 | 500 |
| 2400 | 100 | 200 | 400 |
| 3600 | 80 | 160 | 300 |
| 4800 | 60 | 150 | 300 |
| 9600 | 50 | 120 | 250 |
| 19200 | 40 | 100 | 200 |

## Contents per file

Three AX.25 UI frames (QST < M0LTE-1, PID F0), short/medium/long info fields (~70 / ~130 / ~180–255 B; exact sizes in `MANIFEST.tsv` — each frame's text self-describes the file's parameters). For IL2P modes the TNC performs the IL2P encoding itself (KISS in = AX.25).

## Decode provenance (QC)

Every file was energy-segmented (all 3 bursts physically present) and decoded through pdn-soundmodem's paired catalog mode (`NinoCorpusQcTests`, report `nino-qc-final.txt`): **40/45 decode 3/3** (as of the 2026-07-31 QPSK detector default reversal — differential detection copies all nine QPSK files; under the earlier coherent default `qpsk600` copied 0–1/3 and `qpsk3600` 1–2/3, the diagnosis that drove the reversal). The 5 remaining exceptions are pdn-soundmodem limitations on good captures, kept deliberately as interop test material: `c4fsk9600` needs ≥~250 ms TXDELAY (its detectors' known run-in floor — the txd50 file decodes 0/3, txd250 3/3), plus single-frame misses on `afsk300`@240 ms, `afsk1200-il2p`@120 ms, `bpsk1200`@500 ms.

## TNC behaviours discovered during capture (N9600A4 v3.44)

1. **The first frame after a TXDELAY change transmits with the previous TXDELAY** (measured via burst-preamble durations across tier files). Flush with a sacrificial frame.
2. **SETHW mode select is fire-and-forget and can silently not apply** — verify via GETALL (`BrdSwchMod` low byte → mode, mapping in the driver) with retries.
3. **In mode 1100 (300 Bd AFSK AX.25) the TNC never transmitted a ~253 B-info AX.25 frame** (short/medium aired, the long burst simply absent; the 300 Bd IL2P modes transmit comparable-duration frames fine). The corpus's 1100 long frame is ~183 B as a workaround; the mechanism is unexplained — possibly a mode-specific length/time limit worth a look.
