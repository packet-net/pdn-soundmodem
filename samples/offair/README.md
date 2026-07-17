# Off-air captures

Real over-the-air signals, captured for regression/interop evidence. Unlike the reference
vectors elsewhere in `samples/`, these are **live RF**, so they carry real-world imperfections
(frequency offset, channel noise, whatever encoding the transmitting station actually uses).

## `gb7rdg-ninotnc-bpsk300-il2pc.wav`

**GB7RDG** (a **NinoTNC**-equipped node, 40m slot 3), mode **AX.25 BPSK300 IL2P+CRC**, captured
2026-07-17 through a **FlexRadio 6500** — RF → near-field coupling into an ANT1 dummy load →
Flex demodulates USB → DAX network audio → `WavFile`. **16-bit mono PCM, 48 kHz, ~20 s.**

- Dial 7050.100 kHz USB; the signal's carrier tone measured at **1541 Hz** in the audio (the
  RF tone centre is ~7051.64 kHz, ~41 Hz above the nominal 7051.60 — see #39, variable centre).
- Contains a **steady carrier tone** (78 dB over the noise median — strong) followed by a
  connected-mode frame.
- **Decodes to `GB7RDG-2>EI0RSI-1`** (CRC-valid IL2P) with `BpskModem` at 300 baud —
  **but only with `PskDetector.Differential`; `PskDetector.Coherent` (the current default)
  recovers 0 frames even at the matched 1541 Hz centre with the signal strong.**

This is the evidence for the coherent-detector interop gap: the NinoTNC BPSK modes use a
(modified) Costas loop for **coherent demodulation with differential encoding** to resolve the
Costas 180° phase ambiguity; our coherent mode does the carrier recovery but omits the
differential-decode step, so it can't resolve the ambiguity. See the tracking issue and #40 /
the #5 default-detector decision. Decode it with the flex-smoke `wavdecode` sweep or a
`BpskModem` unit test to reproduce.
