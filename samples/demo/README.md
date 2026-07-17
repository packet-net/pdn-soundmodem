<!-- Generated 2026-07-17 from pdn-soundmodem's real TX paths; every file decoded back to prove it. Regenerate: see the modem TX APIs / sm-pocsag. -->
# pdn-soundmodem — demonstration WAV set

Nine WAV files, one per transmit-mode family in `pdn-soundmodem` (main @ `94d09f9`). Each
was produced by the modem's real **transmit path** (the same `IModem.Modulate` / encoder
code the daemon keys the radio with — not a hand-crafted signal), each carries a genuine,
decodable amateur-radio frame, and **every one has been decoded back** to recover its
payload. Where a decoder a ham would actually reach for exists, the file was proved with
**that reference tool** as well as with pdn-soundmodem's own receiver; three modes have no
external decoder in existence (the two NinoTNC-family data modes and the MIL-STD-188-110D
waveform) and are proved with our own receiver only — that gap is the point, and is called
out below.

All files are **16-bit mono PCM WAV** at each mode's native sample rate, so the reference
decoders accept them directly — but they are ordinary WAVs and play in any audio app or on
any phone. Each begins with a few hundred ms of true silence plus the mode's real TXDELAY /
preamble, so it sounds like an actual over-air burst keying up.

Callsign throughout: **M0LTE**.

## The set

| # | File | Mode (plain English) | What's inside (payload) | Rate / duration | Decoded by — reference tool | Decoded by — our receiver |
|---|------|----------------------|--------------------------|-----------------|------------------------------|----------------------------|
| 1 | `01-afsk1200-aprs-position.wav` | **AFSK1200** — classic 1200-baud VHF FM packet / APRS (the bread-and-butter) | APRS position UI frame `M0LTE>APRS` info `!5132.07N/00005.79W-pdn-soundmodem demo` | 48 kHz / 1.2 s | **multimon-ng 1.3.0** `-a AFSK1200`: `fm M0LTE-0 to APRS-0 UI pid=F0` / `!5132.07N/00005.79W-pdn-soundmodem demo` | `sm-decode`/`Afsk1200Modem`: `M0LTE>APRS:!5132.07N/00005.79W-pdn-soundmodem demo` |
| 2 | `02-fsk9600-aprs-status.wav` | **FSK9600 (G3RUH)** — 9600-baud GFSK, fast VHF/UHF + satellite packet | APRS status UI frame `M0LTE>APRS` info `>pdn-soundmodem 9600 G3RUH GFSK demo de M0LTE` | 48 kHz / 0.9 s | **multimon-ng 1.3.0** `-a FSK9600`: `fm M0LTE-0 to APRS-0 UI pid=F0` / `>pdn-soundmodem 9600 G3RUH GFSK demo de M0LTE` | `FskModem.Fsk9600`: `M0LTE>APRS:>pdn-soundmodem 9600 G3RUH GFSK demo de M0LTE` |
| 3 | `03-qpsk3600-ax25-ui.wav` | **QPSK3600** — NinoTNC-family PSK data mode (3600 bps on a normal FM channel), IL2P+CRC | AX.25 UI frame `M0LTE>PDNODE` info `pdn-soundmodem QPSK3600 NinoTNC mode 5 data demo de M0LTE` | 48 kHz / 1.0 s | *(none exists — NinoTNC-family mode; needs a NinoTNC or this modem)* | `sm-decode qpsk3600`: `M0LTE>PDNODE:pdn-soundmodem QPSK3600 NinoTNC mode 5 data demo de M0LTE` |
| 4 | `04-c4fsk9600-ax25-ui.wav` | **C4FSK9600** — NinoTNC C4FSK mode 3 (9600 bps 4-level FSK), IL2P+CRC | AX.25 UI frame `M0LTE>PDNODE` info `pdn-soundmodem C4FSK 9600 NinoTNC mode 3 data demo de M0LTE` | 48 kHz / 0.9 s | *(none exists — NinoTNC-family mode)* | `C4fskModem.C4fsk9600`: `M0LTE>PDNODE:pdn-soundmodem C4FSK 9600 NinoTNC mode 3 data demo de M0LTE` |
| 5 | `05-freedv-datac1-ax25-ui.wav` | **FreeDV datac1** — the HF OFDM throughput workhorse | AX.25 UI frame `M0LTE>PDNODE` info `pdn-soundmodem FreeDV datac1 HF OFDM demo de M0LTE`, carried as IL2P+CRC in the datac payload (85-byte IL2P wire, 1 packet/burst) | 8 kHz / 5.3 s | **codec2 `freedv_data_raw_rx datac1`**: `bytes: 510  Frms: 1  SNRAv 11.15` — recovered the 510 payload bytes **byte-identical** to what we transmitted; those bytes fed back through our IL2P deframer give `M0LTE>PDNODE:pdn-soundmodem FreeDV datac1 HF OFDM demo de M0LTE` | `FreeDvDatacModem.Datac1`: `M0LTE>PDNODE:pdn-soundmodem FreeDV datac1 HF OFDM demo de M0LTE` |
| 6 | `06-freedv-datac4-ax25-ui.wav` | **FreeDV datac4** — weak-signal HF (narrow ~250 Hz, robust, low-SNR) | AX.25 UI frame `M0LTE>PDNODE` info `pdn-soundmodem FreeDV datac4 HF demo de M0LTE` (80-byte IL2P wire, 2 packets/burst) | 8 kHz / 11.6 s | **codec2 `freedv_data_raw_rx datac4`**: `bytes: 108  Frms: 2  SNRAv 16.77` — recovered both 54-byte payloads **byte-identical**; deframed → `M0LTE>PDNODE:pdn-soundmodem FreeDV datac4 HF demo de M0LTE` | `FreeDvDatacModem.Datac4`: `M0LTE>PDNODE:pdn-soundmodem FreeDV datac4 HF demo de M0LTE` |
| 7 | `07-ardop-fec-text.wav` | **ARDOP** — connectionless FEC-mode HF data frame (4FSK.500.100) | Text `DE M0LTE - pdn-soundmodem ARDOP demo` (36 bytes) | 12 kHz / 4.2 s | **ardopcf 1.0.4.1.3** `--decodewav`: `H4A:4FSK.500.100.E … frame received OK. frameLen = 36 … Quality=100`, UTF-8 text `DE M0LTE - pdn-soundmodem ARDOP demo` (payload byte-exact) | `ArdopDemodulator`/`ArdopFecReceiver`: `"DE M0LTE - pdn-soundmodem ARDOP demo"` |
| 8 | `08-pocsag1200-dapnet-page.wav` | **POCSAG1200** — DAPNET-style pager page | Alphanumeric page, RIC 1234567, function 3, text `M0LTE de pdn-soundmodem` | 22.05 kHz / 1.8 s | **multimon-ng 1.3.0** `-a POCSAG1200`: `Address: 1234567  Function: 3  Alpha:   M0LTE de pdn-soundmodem` | `PocsagDecoder`: `RIC 1234567 fn 3: M0LTE de pdn-soundmodem` |
| 9 | `09-ms110d-wn2-ax25-ui.wav` | **MIL-STD-188-110D App. D** — WN2 serial-tone BPSK (300 bps, 3 kHz HF) | AX.25 UI frame `M0LTE>PDNODE` info `pdn-soundmodem MIL-STD-188-110D App D WN2 demo de M0LTE`, IL2P+CRC | 9.6 kHz / 4.8 s | *(**none exists anywhere** — this waveform previously only ran on RapidM / Rockwell-Collins hardware)* | `Ms110dModem` (WN2): `M0LTE>PDNODE:pdn-soundmodem MIL-STD-188-110D App D WN2 demo de M0LTE` |

## Notes

- **"Reference tool a ham would use" vs "no decoder exists".** Files 1, 2, 8 decode in
  **multimon-ng** (the de-facto AFSK/GFSK/POCSAG decoder); file 7 decodes in **ardopcf**
  (the reference ARDOP TNC); files 5, 6 decode in **codec2's own `freedv_data_raw_rx`** — the
  strongest possible interop proof for the FreeDV modes, since it demodulates our OFDM with
  the FreeDV project's own code and recovers the datac payload **byte-for-byte**. Files 3,
  4, 9 genuinely have **no external decoder**: QPSK3600 and C4FSK9600 are NinoTNC-lineage
  IL2P+CRC modes (only a real NinoTNC or this modem speaks them), and MS110D App. D never
  had a software receiver before pdn-soundmodem — so those three are proved with our own
  receiver, which is exactly what makes them noteworthy.

- **FreeDV raw-layer interop (files 5 & 6).** The datac payload is pdn-soundmodem's
  family-standard IL2P+CRC-framed AX.25. codec2's `freedv_data_raw_rx` recovered the raw
  datac payload bytes (510 for datac1, 2×54 for datac4) **identical** to what we
  transmitted — so the *waveform* is fully FreeDV-compatible; only the *interpretation* of
  those bytes (IL2P) is ours. Passing codec2's recovered bytes back through our IL2P
  deframer reproduces the original M0LTE AX.25 frame, closing the loop
  codec2 → bytes → AX.25.

- **Durations.** All modest except datac4 at 11.6 s — that is simply how long a full frame
  takes over the narrow, very-low-SNR datac4 waveform; it is representative, not padding.

- **Formats.** 16-bit mono PCM WAV. Native rates chosen so the reference decoders ingest
  them without resampling: FreeDV 8 kHz, ARDOP 12 kHz, MS110D 9.6 kHz, POCSAG 22.05 kHz,
  the VHF modes 48 kHz (card rate — what actually goes on the wire). They all still play in
  any audio app.

## Reference tools used

- **multimon-ng 1.3.0** (`/usr/bin/multimon-ng`) — AFSK1200, FSK9600, POCSAG1200.
- **ardopcf 1.0.4.1.3** (`ardop-ref/ardopcf`) — ARDOP `--decodewav`.
- **codec2 `freedv_data_raw_rx`** (libcodec2 1.2.x, `codec2-ref/build/src/`) — FreeDV datac1/datac4.
- **pdn-soundmodem** own receivers (`sm-decode` + the `IModem` receive path), main @ `94d09f9`.
