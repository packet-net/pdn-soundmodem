<!-- Generated 2026-07-17 from pdn-soundmodem's real TX paths; every file decoded back to prove it. Regenerate: see the modem TX APIs / sm-pocsag. -->
# pdn-soundmodem — demonstration recordings

One WAV per transmit-mode family, each **produced by the modem's real transmit path** (the same
`IModem.Modulate` / encoder code the daemon keys the radio with — not a hand-crafted signal),
each carrying a genuine amateur-radio frame from **M0LTE**, and **every one decoded back** to
recover its payload. Where a decoder a ham would actually reach for exists, the file was proved
with **that reference tool** as well as pdn-soundmodem's own receiver. Three modes have no
external decoder in existence — that's the point, and it's flagged.

All files are **16-bit mono PCM WAV** at each mode's native rate (so the reference decoders
ingest them directly), and play in any audio app or on a phone. Each opens with true silence
plus the mode's real TXDELAY/preamble, so it sounds like an over-air burst keying up.

## Listen / download

| | Mode | Band & use | ▶ File | Proved with |
|---|------|-----------|--------|-------------|
| 1 | **AFSK1200** | VHF FM packet / APRS — the bread-and-butter | [🔊 01-afsk1200-aprs-position.wav](01-afsk1200-aprs-position.wav) | multimon-ng |
| 2 | **FSK9600 (G3RUH)** | fast VHF/UHF + satellite packet | [🔊 02-fsk9600-aprs-status.wav](02-fsk9600-aprs-status.wav) | multimon-ng |
| 3 | **QPSK3600** | NinoTNC-family PSK data, one FM channel | [🔊 03-qpsk3600-ax25-ui.wav](03-qpsk3600-ax25-ui.wav) | *our RX only\** |
| 4 | **C4FSK9600** | NinoTNC C4FSK (4-level FSK) | [🔊 04-c4fsk9600-ax25-ui.wav](04-c4fsk9600-ax25-ui.wav) | *our RX only\** |
| 5 | **FreeDV datac1** | HF OFDM throughput workhorse | [🔊 05-freedv-datac1-ax25-ui.wav](05-freedv-datac1-ax25-ui.wav) | **codec2's own RX** |
| 6 | **FreeDV datac4** | weak-signal HF (narrow, robust) | [🔊 06-freedv-datac4-ax25-ui.wav](06-freedv-datac4-ax25-ui.wav) | **codec2's own RX** |
| 7 | **ARDOP** | open HF ARQ (FEC-mode frame) | [🔊 07-ardop-fec-text.wav](07-ardop-fec-text.wav) | ardopcf `--decodewav` |
| 8 | **POCSAG1200** | DAPNET-style pager page | [🔊 08-pocsag1200-dapnet-page.wav](08-pocsag1200-dapnet-page.wav) | multimon-ng |
| 9 | **MIL-STD-188-110D App D** | military-grade 3 kHz HF serial-tone (WN2) | [🔊 09-ms110d-wn2-ax25-ui.wav](09-ms110d-wn2-ax25-ui.wav) | *our RX only\** |

\* No external decoder exists: QPSK3600/C4FSK9600 are NinoTNC-lineage modes (only a NinoTNC or
this modem speaks them), and MIL-STD-188-110D App D never had a software receiver before
pdn-soundmodem. Proved with our own receiver — which is exactly what makes them noteworthy.

## What each file contains, and the exact decode

| # | File | Mode (plain English) | Payload | Rate / dur. | Reference-tool decode | Our-RX decode |
|---|------|----------------------|---------|-------------|-----------------------|---------------|
| 1 | [01-afsk1200-aprs-position.wav](01-afsk1200-aprs-position.wav) | **AFSK1200** — classic 1200-baud VHF FM packet / APRS | APRS position UI frame, info `!5132.07N/00005.79W-pdn-soundmodem demo` | 48 kHz / 1.2 s | **multimon-ng 1.3.0** `-a AFSK1200`: `fm M0LTE-0 to APRS-0 UI` / `!5132.07N/00005.79W-pdn-soundmodem demo` | `M0LTE>APRS:!5132.07N/00005.79W-pdn-soundmodem demo` |
| 2 | [02-fsk9600-aprs-status.wav](02-fsk9600-aprs-status.wav) | **FSK9600 (G3RUH)** — 9600-baud GFSK, satellite/fast VHF-UHF | APRS status UI frame, info `>pdn-soundmodem 9600 G3RUH GFSK demo de M0LTE` | 48 kHz / 0.9 s | **multimon-ng** `-a FSK9600`: `fm M0LTE-0 to APRS-0 UI` / `>pdn-soundmodem 9600 G3RUH GFSK demo de M0LTE` | same string |
| 3 | [03-qpsk3600-ax25-ui.wav](03-qpsk3600-ax25-ui.wav) | **QPSK3600** — NinoTNC PSK data (3600 bps), IL2P+CRC | AX.25 UI, info `pdn-soundmodem QPSK3600 NinoTNC mode 5 data demo de M0LTE` | 48 kHz / 1.0 s | *(none exists — NinoTNC-family)* | `M0LTE>PDNODE:pdn-soundmodem QPSK3600 NinoTNC mode 5 data demo de M0LTE` |
| 4 | [04-c4fsk9600-ax25-ui.wav](04-c4fsk9600-ax25-ui.wav) | **C4FSK9600** — NinoTNC C4FSK mode 3 (9600 bps 4-FSK), IL2P+CRC | AX.25 UI, info `pdn-soundmodem C4FSK 9600 NinoTNC mode 3 data demo de M0LTE` | 48 kHz / 0.9 s | *(none exists — NinoTNC-family)* | `M0LTE>PDNODE:pdn-soundmodem C4FSK 9600 NinoTNC mode 3 data demo de M0LTE` |
| 5 | [05-freedv-datac1-ax25-ui.wav](05-freedv-datac1-ax25-ui.wav) | **FreeDV datac1** — HF OFDM throughput workhorse | AX.25 UI carried as IL2P+CRC in the datac payload (85-byte IL2P wire, 1 packet/burst) | 8 kHz / 5.3 s | **codec2 `freedv_data_raw_rx datac1`**: `bytes: 510  Frms: 1  SNRAv 11.15` — 510 payload bytes recovered **byte-identical**; deframed → the M0LTE frame | `M0LTE>PDNODE:pdn-soundmodem FreeDV datac1 HF OFDM demo de M0LTE` |
| 6 | [06-freedv-datac4-ax25-ui.wav](06-freedv-datac4-ax25-ui.wav) | **FreeDV datac4** — weak-signal HF (narrow ~250 Hz, low-SNR) | AX.25 UI, info `pdn-soundmodem FreeDV datac4 HF demo de M0LTE` (80-byte IL2P wire, 2 packets/burst) | 8 kHz / 11.6 s | **codec2 `freedv_data_raw_rx datac4`**: `bytes: 108  Frms: 2  SNRAv 16.77` — both 54-byte payloads **byte-identical**; deframed → the M0LTE frame | `M0LTE>PDNODE:pdn-soundmodem FreeDV datac4 HF demo de M0LTE` |
| 7 | [07-ardop-fec-text.wav](07-ardop-fec-text.wav) | **ARDOP** — connectionless FEC-mode HF frame (4FSK.500.100) | Text `DE M0LTE - pdn-soundmodem ARDOP demo` (36 bytes) | 12 kHz / 4.2 s | **ardopcf 1.0.4.1.3** `--decodewav`: `frame received OK, frameLen = 36, Quality=100`, text byte-exact | `"DE M0LTE - pdn-soundmodem ARDOP demo"` |
| 8 | [08-pocsag1200-dapnet-page.wav](08-pocsag1200-dapnet-page.wav) | **POCSAG1200** — DAPNET-style pager page | Alphanumeric page, RIC 1234567, function 3, text `M0LTE de pdn-soundmodem` | 22.05 kHz / 1.8 s | **multimon-ng** `-a POCSAG1200`: `Address: 1234567  Function: 3  Alpha:   M0LTE de pdn-soundmodem` | `RIC 1234567 fn 3: M0LTE de pdn-soundmodem` |
| 9 | [09-ms110d-wn2-ax25-ui.wav](09-ms110d-wn2-ax25-ui.wav) | **MIL-STD-188-110D App D** — WN2 serial-tone BPSK (300 bps, 3 kHz HF) | AX.25 UI, info `pdn-soundmodem MIL-STD-188-110D App D WN2 demo de M0LTE`, IL2P+CRC | 9.6 kHz / 4.8 s | *(**none exists anywhere** — previously RapidM / Rockwell-Collins hardware only)* | `M0LTE>PDNODE:pdn-soundmodem MIL-STD-188-110D App D WN2 demo de M0LTE` |

## Notes

- **The strongest proof is files 5 & 6.** codec2's *own* `freedv_data_raw_rx` demodulates our
  OFDM and recovers the datac payload **byte-for-byte** — so the *waveform* is fully
  FreeDV-compatible; only the *interpretation* of those bytes (IL2P+CRC) is ours. Feeding
  codec2's recovered bytes back through our IL2P deframer reproduces the original M0LTE AX.25
  frame, closing the loop codec2 → bytes → AX.25.

- **Durations.** All modest except datac4 at 11.6 s — that is simply how long a full frame
  takes over the narrow, very-low-SNR datac4 waveform; representative, not padding.

- **Formats.** 16-bit mono PCM WAV at native rates so the reference decoders ingest them without
  resampling: FreeDV 8 kHz, ARDOP 12 kHz, MS110D 9.6 kHz, POCSAG 22.05 kHz, the VHF modes 48 kHz
  (the card rate — what actually goes on the wire). They all still play in any audio app.

## Reference tools

- **multimon-ng 1.3.0** — AFSK1200, FSK9600, POCSAG1200.
- **ardopcf 1.0.4.1.3** — ARDOP `--decodewav`.
- **codec2 `freedv_data_raw_rx`** (libcodec2 1.2.x) — FreeDV datac1 / datac4.
- **pdn-soundmodem** own receivers (`sm-decode` + the `IModem` receive path).
