# On-air modulation reference - FM vs SSB, and FM deviation targets

How each NinoTNC-lineage mode is carried on the air, for the OTA test harness. Source of truth: **NinoTNC firmware v3/4.44 modulator table** (operator-supplied, 2026-07-28). Companion to [`mode-validation.md`](mode-validation.md).

> **⚠️ Deviation vs channel spacing - do not confuse them.** "Wide" (25 kHz) and "narrow" (12.5 kHz) refer to **channel spacing**, *not* FM deviation. The **actual peak FM deviation** each mode must be transmitted at is the **Tgt Dev** column below (e.g. 3.0 kHz for AFSK 1200, not 12.5/25 kHz). When transmitting an FM mode on the rig, the audio drive **must be calibrated so the achieved peak deviation matches Tgt Dev** - and verified by measurement (FM-demodulate the RSP1 IQ and measure the peak frequency excursion, or a Bessel-null against a single tone). Getting this wrong (under- or over-deviating) degrades or breaks the decode and invalidates the SNR characterisation.

## FM modulators (transmit via the Flex FM mode, at the targeted deviation)

| NinoTNC mode | Tone/subcarrier Hz | **Tgt peak dev kHz** | NinoTNC switch | pdn `ModemCatalog` mode(s) |
|---|---|---|---|---|
| AFSK 1200 | 1248 | **3.0** | 0110, 0111 | `afsk1200` (+`-fx25`, `-fx25rx`, `-il2p`, `-il2p-nocrc`) |
| GFSK 9600 | 999 | **2.4** | 0000, 0010 | `fsk9600` (+`-il2p`) |
| GFSK 4800 | 500 | **1.2** | 0100 | `fsk4800-il2p` |
| C4FSK 9600 | 1039 | **2.5** | 0011 | `c4fsk9600` |
| C4FSK 19200 | 2079 | **5.0** | 0001, 0101 | `c4fsk19200` |
| **QPSK 3600** | 2079 | **5.0** | 0001, 0101 | `qpsk3600` - QPSK audio over FM (shares the C4FSK 19200 modulator) |

## SSB modulators (transmit via SSB/DIGU - the DAX route the harness already drives)

| NinoTNC mode | Tone/centre Hz | NinoTNC switch | pdn `ModemCatalog` mode(s) |
|---|---|---|---|
| AFSK 300 | 1700 | 1100, 1101, 1110 | `afsk300` (+`-il2p`, `-il2pc`) |
| PSK 300 | 1500 | 1000, 1001 | `bpsk300` (+`-nocrc`) |
| PSK 1200 | 1500 | 1010, 1011 | `bpsk1200` (+`-multi`) |

Not in the NinoTNC table but SSB by nature (V.26A / DW audio PSK carriers): `qpsk600`, `qpsk2400`.

## Consequences for the harness

- **SSB modes** ride the existing DAX/DIGU SSB path (audio carrier → SSB) - no deviation concern; the modem's own audio spectrum defines the signal.
- **FM modes** need a **Flex FM-TX path with per-mode deviation calibration** (drive set so peak dev = Tgt Dev, measured back off the RSP1 FM discriminator) plus an **IQ→FM-discriminator RX** feeding the modem. `qpsk3600` transmits at 5.0 kHz dev like C4FSK 19200 - do **not** route it through the SSB path.
- The correct FM/SSB assignment above supersedes any earlier categorisation in session notes (which had `qpsk3600` and `afsk300` on the wrong sides).
