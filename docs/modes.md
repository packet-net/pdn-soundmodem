# Mode table

Every mode `ModemCatalog` serves, with its capabilities and current verification level.
This is the **reference view**; the authoritative per-mode validation provenance — which
tests, which captures, which campaigns, with dates and PRs — lives in the
[mode validation ledger](mode-validation.md), and that document's maintenance rule governs
both: when a mode's status changes there, update its row here in the same PR.

**Verification levels** (highest achieved; each level implies the ones below it):

- **On-air** — proven over real RF (off-air captures decoded, or live campaigns over the
  Flex→RSP1 rig or DAX route).
- **Bench** — proven against real reference hardware over a wired audio loop (the NinoTNC
  corpus / head-to-head / July loop, or live QtSoundModem interop).
- **Sim** — validated in simulation and/or offline cross-validation (Dire Wolf `atest`,
  spec vectors, loopback + channel batteries) only.
- **Untested** — constructs and nothing more.
- A ⚠️ marks a documented caveat worth reading before relying on the mode; follow the
  ledger link.

**Common capabilities**: every mode carries AX.25 frames behind its stated framing and is
addressable from the daemon by KISS port nibble. `DSP rate` is the demod/mod chain's
native rate (`ModemCatalog.DspRateFor`); capture rates must be an integer multiple.
`Tunable` = accepts a non-default audio centre frequency (`AcceptsCentreFrequency`:
the `afsk*`/`bpsk*`/`qpsk*` families). All PSK modes default to the differential
detector (coherent selectable per call). Radio-path assignments (FM deviation targets vs
SSB) are pinned in [mode-modulation-reference.md](mode-modulation-reference.md).

## NinoTNC-lineage modes

| Mode | Modulation | Bit rate | Framing | DSP rate | Tunable | Radio path | Interops with | Verification |
|---|---|---|---|---|---|---|---|---|
| `afsk1200` | AFSK (Bell 202) | 1200 bps | AX.25 HDLC | 12 kHz | yes | FM/VHF | NinoTNC, Dire Wolf, QtSM | **On-air** — WA8LMF off-air corpus, scores at/above Dire Wolf |
| `afsk1200-multi` | AFSK + 3-pair offset-diversity bank | 1200 bps | AX.25 HDLC | 12 kHz | yes | FM/VHF | as `afsk1200` | **On-air** — same corpus (bank's figures) |
| `afsk1200-fx25` | AFSK + FX.25 FEC (TX+RX) | 1200 bps | AX.25 + FX.25 | 12 kHz | yes | FM/VHF | Dire Wolf FX.25 | **Sim** — roundtrip + FEC correction tests |
| `afsk1200-fx25rx` | AFSK + FX.25 (RX only) | 1200 bps | AX.25 + FX.25 | 12 kHz | yes | FM/VHF | as above | **Sim** |
| `afsk1200-il2p` | AFSK | 1200 bps | IL2P+CRC | 12 kHz | yes | FM/VHF | NinoTNC (0111), Dire Wolf IL2P | **Bench** — NinoTNC corpus + July loop, both directions |
| `afsk1200-il2p-nocrc` | AFSK | 1200 bps | IL2P | 12 kHz | yes | FM/VHF | — | **Untested** — construction only |
| `afsk300` | AFSK (200 Hz shift) | 300 bps | AX.25 HDLC | 12 kHz | yes | SSB/HF | NinoTNC (1100) | **Bench** ⚠ — corpus decodes 3/3; on-air blocked by rig CFO drift (#116); NinoTNC's own 1100 RX has quirks (see bench doc) |
| `afsk300-il2p` | AFSK | 300 bps | IL2P | 12 kHz | yes | SSB/HF | NinoTNC (1101) | **Bench** — corpus, both directions |
| `afsk300-il2pc` | AFSK | 300 bps | IL2P+CRC | 12 kHz | yes | SSB/HF | NinoTNC (1110) | **On-air** — first off-air decode 2026-08-02 (GB7BEX-15→GB7IOW-1 via a `ubersdr:` web receiver); bench corpus both directions |
| `bpsk300` | BPSK + 4-pair diversity bank | 300 bps | IL2P+CRC | 12 kHz | yes | SSB/HF | NinoTNC (1000), QtSM V26A | **On-air** — GB7RDG off-air decode + BER waterfall + 7 live 40 m frames via a `ubersdr:` web receiver (2026-08-02) ⚠ open residual-miss scoreboard |
| `bpsk300-multi` | alias of `bpsk300` | 300 bps | IL2P+CRC | 12 kHz | yes | SSB/HF | as `bpsk300` | **On-air** |
| `bpsk300-nocrc` | BPSK | 300 bps | IL2P | 12 kHz | yes | SSB/HF | — | **Untested** — construction only |
| `bpsk1200` | BPSK + diversity bank | 1200 bps | IL2P+CRC | 12 kHz | yes | SSB/HF | NinoTNC (1010), QtSM V26A | **On-air** — AWGN campaign 2026-07-28 |
| `bpsk1200-multi` | alias of `bpsk1200` | 1200 bps | IL2P+CRC | 12 kHz | yes | SSB/HF | as `bpsk1200` | **On-air** |
| `qpsk600` | QPSK (V.26A) | 1200 bps | IL2P+CRC | 12 kHz | yes | SSB/HF | NinoTNC (1001), QtSM V26A | **Bench** ⚠ — corpus 9/9 since the differential default; live QtSM retest pending (#11); CFO ±10–40 Hz (#116) |
| `qpsk2400` | QPSK (V.26A/DW2400) | 4800 bps | IL2P+CRC | 12 kHz | yes | SSB/HF | NinoTNC (1011), QtSM V26A type 12 (not legacy type 10 — by design, #6) | **Bench** — corpus; on-air blocked by rig CFO (#116) |
| `qpsk3600` | QPSK | 7200 bps | IL2P+CRC | 12 kHz | yes | FM (5.0 kHz dev) | NinoTNC (0101) | **Bench** — corpus; on-air deferred to FM-capable TX (#118) |
| `fsk9600` | GFSK (G3RUH) | 9600 bps | AX.25 HDLC | 48 kHz | no | FM (9600 port) | NinoTNC (0000), Dire Wolf, QtSM | **Bench** — corpus + head-to-head; on-air deferred (#118) |
| `fsk9600-il2p` | GFSK | 9600 bps | IL2P+CRC | 48 kHz | no | FM (9600 port) | NinoTNC (0010) | **Bench** — corpus; sync-only acquisition (0 ms preamble floor) |
| `fsk4800-il2p` | GFSK (RUH-4800) | 4800 bps | IL2P+CRC | 48 kHz | no | FM | NinoTNC (0100), Dire Wolf/QtSM RUH | **Bench** — corpus + live QtSM both directions; sync-only acquisition |
| `c4fsk9600` | 4-level FSK (MMDVM-TNC Mode 2) | 9600 bps | IL2P+CRC | 48 kHz | no | FM (2.5 kHz dev) | NinoTNC (0011), MMDVM-TNC | **Bench** — July loop 8/8 both ways; corpus 3/3 incl. content the NinoTNC itself cannot replay-copy (adaptive equalizer, 2026-08-01); on-air deferred (#118) |
| `c4fsk19200` | 4-level FSK | 19200 bps | IL2P+CRC | 48 kHz | no | FM (5.0 kHz dev) | NinoTNC (0001), MMDVM-TNC | **Bench** — as `c4fsk9600` |

### Station identification on the PSK SSB modes

A NinoTNC cannot identify itself inside 300 BPSK, 600 QPSK, 1200 BPSK or 2400 QPSK, so it
idents alongside them: 300 AFSK AX.25 on 1600/1800 Hz tones, host callsign → `IDENT`, every
9.5 minutes by default while the station is transmitting ([operator's
manual](https://tarpn.net/t/nino-tnc/n9600a/n9600a_operation.html)). The three self-identifying
modes — `afsk300`, `afsk300-il2pc`, `afsk1200` — send no such beacon.

For every PSK SSB modem configured, the daemon attaches a receive-only *ghost* to catch these
(`IdBeaconGhost`; `"idBeacons": false` turns them off). It sits 200 Hz above the modem it
accompanies — Nino's PSK carrier is 1500 Hz and the beacon centre is 1700 — as an offset rather
than an absolute frequency, so a retuned modem carries its ghost with it. The ghost is built
through the catalogue's `afsk300` entry, so it is the narrow-branch diversity bank: selectivity
matters more here than on a data slot, because a ghost sits beside a PSK carrier by construction.

Idents are tagged onto their burst (`KK4HEJ · ID`), listed in the waterfall panel with an **ID**
badge, and written to the frame log. They occupy no KISS sub-channel, do not affect carrier
sense, and get no band of their own on the display — they ride on a modem that already has one.
See [CONFIG.md](../CONFIG.md#idbeacons).

The FM side of the same rule — where a NinoTNC idents in 1200 AFSK AX.25 — is not implemented
yet.

## FreeDV DATAC (OFDM) modes

Codec2 OFDM burst waveforms; payloads carry the family-standard IL2P+CRC bit stream (a
pdn convention — FreeDV defines no framing at the raw-data layer). All 8 kHz-native
engine, 48 kHz deployment path, SSB/HF, fixed centre.

| Mode | Payload rate | Bandwidth | Verification |
|---|---|---|---|
| `freedv-datac0` | 291 bps | — | **On-air** — 2026-07-28 DAX campaign, AWGN to −3.7 dB, Poor all rungs |
| `freedv-datac1` | 980 bps | 1.7 kHz | **On-air** — AWGN matches sim baseline exactly (+1.8 dB) |
| `freedv-datac3` | 321 bps | 500 Hz | **On-air** — within 0.6 dB of sim/published points |
| `freedv-datac4` | 87 bps | 250 Hz | **On-air** — very robust narrow mode |
| `freedv-datac13` | 64 bps | narrow | **On-air** — signalling mode |
| `freedv-datac14` | 58 bps | narrow | **On-air** — shortest signalling mode |

## MIL-STD-188-110D App D (MS110D) modes

3 kHz serial-tone HF waveforms, 75–3200 bps; same IL2P+CRC payload convention. RX is
autobaud — the `wnN` suffix selects the transmit waveform only. 48 kHz path, SSB/HF,
fixed centre. "Hard-gated" = the sim BER mask suite holds the mode at its App D
performance mask under the D.6.1 Poor channel.

| Mode | Waveform | Verification |
|---|---|---|
| `ms110d-wn0` | 75 bps Walsh fallback | **On-air** — sim hard-gated; 2026-07-27 campaign, Poor at/below mask |
| `ms110d-wn1` | BPSK r1/8 | **On-air** — hard-gated; campaign clean |
| `ms110d-wn2` | BPSK r1/4 | **On-air** — hard-gated; real-RF level fix (AGC, PR #103) proven live |
| `ms110d-wn3` | BPSK r1/3 | **On-air** — hard-gated; campaign clean |
| `ms110d-wn4` | BPSK r2/3 | **On-air** — the strongest on-air-proven MS110D point (Poor 8/9 coded-clean) |
| `ms110d-wn5` | BPSK r3/4 | **On-air** — hard-gated; campaign clean |
| `ms110d-wn6` | QPSK r3/4 | **On-air** ⚠ — requires a disciplined RX reference (receiver phase-noise, not a modem defect; #102) |
| `ms110d-wn7` | 8PSK r3/4 | ⚠ **Partial** — AWGN on-air-proven; Poor is architecture-limited (measured floor above mask; needs added information — see Phase B closeout) |
| `ms110d-wn8` | 16QAM r3/4 | ⚠ **Partial** — AWGN on-air-proven at reachable SNR; Poor decodes (MFB-form receiver, 2026-07-31) but measured-only vs mask |
| `ms110d-wn13` | QPSK r9/16 | **On-air** ⚠ — same disciplined-reference condition as wn6 |

## Measured extras

- **Acquisition floors behind real NinoTNC preambles** (per-mode, trim-ladder measured,
  incl. three sync-only modes): [bench corpus doc](bench/ninotnc-corpus-2026-07-31.md).
- **NinoTNC head-to-head**: on the 45-file corpus there is no cell the reference hardware
  decodes that we do not; details and caveats in the same bench doc.
