# Off-air captures

Real over-the-air signals, captured for regression/interop evidence. Unlike the reference
vectors elsewhere in `samples/`, these are **live RF**, so they carry real-world imperfections
(frequency offset, channel noise, whatever encoding the transmitting station actually uses).

## `gb7rdg-ninotnc-bpsk300-il2pc.wav`

**GB7RDG** (a **NinoTNC**-equipped node, 40m slot 3), mode **AX.25 BPSK300 IL2P+CRC**, captured
2026-07-17 through a **FlexRadio 6500** — RF → near-field coupling into an ANT1 dummy load →
Flex demodulates USB → DAX network audio → `WavFile`. **16-bit mono PCM, 48 kHz, ~20 s.**

- Dial 7050.100 kHz USB. The **connected-mode frame** in this file sits ~**8 Hz** off the 1500 Hz
  audio centre (tone ≈1508 Hz, measured by a symbol-spaced squaring estimate over the burst; SNR
  ≈16 dB — not a weak signal).
- Opens with a long **steady carrier** — that is the NinoTNC test button held down during the
  capture, *not* something that appears in normal on-air traffic; ignore it. Real transmissions
  lead with a short (~150 ms) preamble only.
- **Decodes to `GB7RDG-2>EI0RSI-1`** (CRC-valid IL2P, 15 bytes:
  `8A9260A4A692E28E846EA4888E6571`) with `BpskModem` at 300 baud using
  `PskDetector.Differential`. Guarded by `OffAirBpskTests`.

**Why coherent doesn't decode _this_ capture (#40/#42, corrected diagnosis).** The original theory
— that our coherent path omits the differential-decode step — is wrong: it *does* differentially
decode the recovered absolute symbols (`BpskDemodulator.ProcessCoherent` + the DPLL sink), exactly
as the NinoTNC does. The real cause is **acquisition**: the coherent Costas loop runs a narrow
tracking bandwidth (the bandwidth that earns coherent's ~1–2 dB noise margin and keeps the QtSM
interop clean), and that loop cannot pull an offset carrier onto frequency within a short (~150 ms)
preamble. This captured frame's preamble is too short for the narrow loop to lock even on-frequency;
only a wide loop gets it, and a wide loop forfeits the margin and breaks the QtSM corpus.

The **general** off-frequency case (normal ~150 ms preamble, tens-of-Hz offset) is handled by
`BpskMultiModem` — a bank of ordinary narrow-loop branches at stepped centres (the QtSoundModem
`afsk1200-multi` model applied to PSK); whichever branch sits within a few Hz of the signal
acquires it. See `BpskMultiModemTests`. Reproduce this file with a `BpskModem`/`BpskMultiModem`
unit test decimating 48 kHz → 12 kHz.

## `m9psy-40m-afsk300-il2pc-qrm.wav`

**M0LTE** connecting to **GB7IOW-1**, mode **afsk300-il2pc** (NinoTNC mode 14), captured
2026-08-02 20:39:42 UTC off the live 40 m slot through the `m9psy-1.instance.ubersdr.org`
public web receiver (RX888 + 40 m full-wave loop, GPSDO-disciplined, Dalgety Bay): iq48 →
`StreamingSsbDemodulator` (dial 7.049450 USB, 150–3450 Hz) → 12 kHz — the daemon's exact
`ubersdr:` receive path. **16-bit mono PCM, 12 kHz, 10 s.**

- Slot 7.050300, so the packet centre sits at **850 Hz** audio; the station is ~3 Hz off.
  SNR ≈ 20 dB — not a weak signal.
- The point of this clip is what ELSE is in it: the slot's everyday neighbour, a QSO at
  ~7.0500 (audio ≈ 475–700 Hz), runs right through the transmission at comparable power.
  That is inside a ±400 Hz receive passband and largely outside a ±300 one.
- **Decodes to `M0LTE>GB7IOW-1`** (SABM+P, CRC-valid IL2P+CRC, 15 bytes:
  `8E846E929EAEE29A6098A88A40613F`) with the current `Afsk300Modem` defaults and with
  `Afsk300MultiModem`; the ±400 Hz filters the mode shipped with for its first year decode
  **nothing** from it — discriminator capture by the neighbour. Guarded by
  `OffAirAfsk300Tests`.

This is the capture that motivated the 2026-08-02 afsk300 receive-filter narrowing and the
`Afsk300MultiModem` bank: on the first 30 minutes of this slot (13 decodable frames from 3
stations), the wide single modem decoded 3, the bank 13. Full corpus + analysis harness
notes in the mode-validation ledger entry of the same date.
