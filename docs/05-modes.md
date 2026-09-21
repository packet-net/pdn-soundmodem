# Modes

When you finish this page you will know which mode to write in your config, what it talks to at the far end, and how well proven it is.

A mode is one line of configuration: the `mode` key on a [`modems`](reference/config.md#modems) entry, or `--modem N:MODE` on the command line. The strings in the tables below are what you write, spelt as they appear here. To hear what a family sounds like, play the recordings in [samples/demo](../samples/demo/README.md).

## Which family you want

`afsk1200` and its variants are 1200 baud tones through an FM radio's microphone and speaker. This is APRS and most VHF packet, and it talks to a NinoTNC, Dire Wolf and QtSoundModem. Start here if you have an FM radio and a sound card; [02-first-station.md](02-first-station.md) sets one up.

`afsk300`, `afsk300-il2p` and `afsk300-il2pc` are the same tones slowed to 300 baud for HF SSB, and are what the UK 40 m IL2P network runs.

The BPSK and QPSK modes carry more data through the same HF passband than AFSK 300 does, and ask for a steadier signal in return. They are the NinoTNC PSK modes, and they interoperate with QtSoundModem's V.26A. `qpsk3600` is the odd one of the group: an FM mode that fills one voice channel.

`fsk9600`, `fsk9600-il2p`, `fsk4800-il2p`, `c4fsk9600` and `c4fsk19200` feed a radio's 9600 baud data socket rather than its microphone. G3RUH `fsk9600` is the satellite and fast VHF standard; the two C4FSK modes talk to a NinoTNC and to MMDVM-TNC.

The `freedv-datac*` modes are codec2's HF OFDM bursts, for SSB paths too rough for PSK. They trade rate for reach: `freedv-datac1` moves 980 bps in 1.7 kHz, and `freedv-datac4` moves 87 bps in 250 Hz where the wider modes have run out of margin.

The `ms110d-*` modes are MIL-STD-188-110D Appendix D serial tone, 75 to 3200 bps in a 3 kHz HF channel. Receiving is autobaud, so the `wnN` suffix chooses only what you transmit.

## Reading the tables

Verification is the highest level a mode has reached, and each level implies the ones below it.

- **On-air** means proven over real RF, from off-air captures or live campaigns.
- **Bench** means proven against reference hardware over a wired audio loop, or live against QtSoundModem.
- **Sim** means validated in simulation or cross-checked against another decoder offline.
- **Untested** means the mode builds and nothing more has been shown.
- **Partial** means proven on air on a clean channel, with the harder result its row names shown in simulation only.
- **Caveat** in a row marks something to read before you rely on that mode.

Which tests, captures and campaigns produced each verdict is recorded per mode in the [mode validation ledger](dev/mode-validation.md).

Every mode carries AX.25 frames behind the framing its row states and is addressed by its KISS sub-channel. `DSP rate` is the rate the modem itself runs at, and the sound card's `captureRate` has to be a multiple of it. `Tunable` says the mode accepts an audio centre of your choosing. The BPSK and QPSK modes use the differential detector unless [`--psk-detector`](reference/command-line.md#station-flags) says otherwise.

## NinoTNC-lineage modes

| Mode | Modulation | Bit rate | Framing | DSP rate | Tunable | Radio path | Interops with | Verification |
|---|---|---|---|---|---|---|---|---|
| `afsk1200` | AFSK (Bell 202) | 1200 bps | AX.25 HDLC | 12 kHz | yes | FM/VHF | NinoTNC, Dire Wolf, QtSM | **On-air** - the WA8LMF off-air corpus |
| `afsk1200-multi` | AFSK + 3-pair offset-diversity bank | 1200 bps | AX.25 HDLC | 12 kHz | yes | FM/VHF | as `afsk1200` | **On-air** - same corpus (bank's figures) |
| `afsk1200-fx25` | AFSK + FX.25 FEC (TX+RX) | 1200 bps | AX.25 + FX.25 | 12 kHz | yes | FM/VHF | Dire Wolf FX.25 | **Sim** - roundtrip + FEC correction tests |
| `afsk1200-fx25rx` | AFSK + FX.25 (RX only) | 1200 bps | AX.25 + FX.25 | 12 kHz | yes | FM/VHF | as above | **Sim** |
| `afsk1200-il2p` | AFSK | 1200 bps | IL2P+CRC | 12 kHz | yes | FM/VHF | NinoTNC (0111), Dire Wolf IL2P | **Bench** - NinoTNC corpus + July loop, both directions |
| `afsk1200-il2p-nocrc` | AFSK | 1200 bps | IL2P | 12 kHz | yes | FM/VHF | - | **Untested** - construction only |
| `afsk300` | AFSK (200 Hz shift) | 300 bps | AX.25 HDLC | 12 kHz | yes | SSB/HF | NinoTNC (1100) | **Bench**, caveat - the corpus decodes; not yet proven on air, and a NinoTNC's own 1100 receive has quirks |
| `afsk300-il2p` | AFSK | 300 bps | IL2P | 12 kHz | yes | SSB/HF | NinoTNC (1101) | **Bench** - corpus, both directions |
| `afsk300-il2pc` | AFSK | 300 bps | IL2P+CRC | 12 kHz | yes | SSB/HF | NinoTNC (1110) | **On-air** - first off-air decode 2026-08-02 (GB7BEX-15>GB7IOW-1 via a `ubersdr:` web receiver); bench corpus both directions |
| `bpsk300` | BPSK + 4-pair diversity bank | 300 bps | IL2P+CRC | 12 kHz | yes | SSB/HF | NinoTNC (1000), QtSM V26A | **On-air** - GB7RDG off-air decode + BER waterfall + 7 live 40 m frames via a `ubersdr:` web receiver (2026-08-02); caveat: open residual-miss scoreboard |
| `bpsk300-multi` | alias of `bpsk300` | 300 bps | IL2P+CRC | 12 kHz | yes | SSB/HF | as `bpsk300` | **On-air** |
| `bpsk300-nocrc` | BPSK | 300 bps | IL2P | 12 kHz | yes | SSB/HF | - | **Untested** - construction only |
| `bpsk1200` | BPSK + diversity bank | 1200 bps | IL2P+CRC | 12 kHz | yes | SSB/HF | NinoTNC (1010), QtSM V26A | **On-air** - AWGN campaign 2026-07-28 |
| `bpsk1200-multi` | alias of `bpsk1200` | 1200 bps | IL2P+CRC | 12 kHz | yes | SSB/HF | as `bpsk1200` | **On-air** |
| `qpsk600` | QPSK (V.26A) | 1200 bps | IL2P+CRC | 12 kHz | yes | SSB/HF | NinoTNC (1001), QtSM V26A | **Bench**, caveat - the corpus decodes; a live QtSM retest is still to come |
| `qpsk2400` | QPSK (V.26A/DW2400) | 4800 bps | IL2P+CRC | 12 kHz | yes | SSB/HF | NinoTNC (1011), QtSM V26A type 12 rather than the legacy type 10 | **Bench** - corpus; not yet proven on air |
| `qpsk3600` | QPSK | 7200 bps | IL2P+CRC | 12 kHz | yes | FM (5.0 kHz dev) | NinoTNC (0101) | **Bench** - corpus; not yet proven on air |
| `fsk9600` | GFSK (G3RUH) | 9600 bps | AX.25 HDLC | 48 kHz | no | FM (9600 port) | NinoTNC (0000), Dire Wolf, QtSM | **Bench** - corpus and a head-to-head; not yet proven on air |
| `fsk9600-il2p` | GFSK | 9600 bps | IL2P+CRC | 48 kHz | no | FM (9600 port) | NinoTNC (0010) | **Bench** - corpus; sync-only acquisition (0 ms preamble floor) |
| `fsk4800-il2p` | GFSK (RUH-4800) | 4800 bps | IL2P+CRC | 48 kHz | no | FM | NinoTNC (0100), Dire Wolf/QtSM RUH | **Bench** - corpus + live QtSM both directions; sync-only acquisition |
| `c4fsk9600` | 4-level FSK (MMDVM-TNC Mode 2) | 9600 bps | IL2P+CRC | 48 kHz | no | FM (2.5 kHz dev) | NinoTNC (0011), MMDVM-TNC | **Bench** - a wired loop both ways and the corpus; not yet proven on air |
| `c4fsk19200` | 4-level FSK | 19200 bps | IL2P+CRC | 48 kHz | no | FM (5.0 kHz dev) | NinoTNC (0001), MMDVM-TNC | **Bench** - as `c4fsk9600` |

## FreeDV DATAC (OFDM) modes

Codec2 OFDM burst waveforms for HF SSB. The payload is the same IL2P+CRC bit stream as the other families, which is this modem's own convention, so the far end has to be another pdn-soundmodem.

| Mode | Payload rate | Bandwidth | Verification |
|---|---|---|---|
| `freedv-datac0` | 291 bps | - | **On-air** - 2026-07-28 DAX campaign, AWGN to -3.7 dB, Poor all rungs |
| `freedv-datac1` | 980 bps | 1.7 kHz | **On-air** - AWGN matches the sim baseline (+1.8 dB) |
| `freedv-datac3` | 321 bps | 500 Hz | **On-air** - within 0.6 dB of sim/published points |
| `freedv-datac4` | 87 bps | 250 Hz | **On-air** - the narrow mode with the most margin |
| `freedv-datac13` | 64 bps | narrow | **On-air** - signalling mode |
| `freedv-datac14` | 58 bps | narrow | **On-air** - shortest signalling mode |

## OFDM-FM modes

OFDM across the audio path of an ordinary FM transceiver, so the radio emits standard FM and regulatory compliance rides on that. Real-FFT symbols with a cyclic prefix, a self-correlating sync symbol, a channel estimate from a known preamble, pilot-tracked residual phase, Gray-coded constellations, a convolutional code with soft Viterbi decoding, and a burst whose header announces its own constellation, coding and length, so one station's rate is its own setting and the other end follows it burst by burst.

The payload is **one AX.25 frame, opaque**, with no IL2P: the burst already supplies sync, a length, a CRC and forward error correction. These modes run at 48 kHz and occupy the audio band from just above DC, so they take no centre frequency.

**All eight share one geometry table**, so a receiver on any of them decodes a burst sent on any other and a link can change bandwidth without a handshake. There are three carrier layouts: `ofdm-fm-narrow` is entry 0, the 6 kHz presets entry 1, the 8 kHz presets entry 2. Entry 0 carries the sync, preamble and header of every burst whatever its payload span, and `ofdm-fm-narrow` is entry 0, keyed and delivering every frame since 2026-09-20. `ofdm-fm-8k` is the default and the only one measured across the full range of payload sizes in both directions. Goodput is what a KISS application moved end to end at 1024 / 1900 / 3000 byte frames, scored from both stations' frame logs.

| Mode | Coded rate | Span | Goodput | Verification |
|---|---|---|---|---|
| `ofdm-fm-narrow` | 2.5 kbit/s | 211 Hz to 2.9 kHz | 2.2 / 2.3 | **On-air** - every frame both directions; the only preset a voice-bandwidth path can carry |
| `ofdm-fm-6k` | 5.5 kbit/s | 211 Hz to 6.0 kHz | not measured | **Sim only** - the robust data-port fallback |
| `ofdm-fm-6k-fast` | 21.8 kbit/s | 211 Hz to 6.0 kHz | 13.3 / 16.6 / 17.8 | **On-air** - every frame delivered |
| `ofdm-fm-8k` | 29.4 kbit/s | 211 Hz to 8.0 kHz | 17.5 / 22.3 / 24.7 | **On-air** - every frame at 1024 and 1900 both ways, 19 of 20 at 3000 |
| `ofdm-fm-8k-r56` | 36.7 kbit/s | 211 Hz to 8.0 kHz | 19.2 / 25.5 / 29.8 | **On-air** - every frame or all but one in every cell, both directions |
| `ofdm-fm-8k-r78` | 38.5 kbit/s | 211 Hz to 8.0 kHz | 21.1 / 24.1 / 29.8 | **On-air** - marks where the margin runs out, not a recommendation |
| `ofdm-fm-8k-follow` | 29.4 kbit/s | 211 Hz to 8.0 kHz | 23.9 / 23.2 / 28.3 | **On-air** - follow-on frames; a failed header costs the rest of the keyup |
| `ofdm-fm-8k-adaptive` | up to 29.4 kbit/s | 211 Hz to 8.0 kHz | what the link supports | **On-air** - point-to-point only; one controller per channel |

Design notes, the measured rate ladder and what still limits the waveform are in [docs/dev/ofdm-fm/](dev/ofdm-fm/).

## MIL-STD-188-110D App D (MS110D) modes

3 kHz serial-tone HF waveforms, 75 to 3200 bps, with the same IL2P+CRC payload. "Hard-gated" in a row means the simulation suite holds the mode at its App D performance mask over the standard's Poor channel.

| Mode | Waveform | Verification |
|---|---|---|
| `ms110d-wn0` | 75 bps Walsh fallback | **On-air** - sim hard-gated; 2026-07-27 campaign, Poor at/below mask |
| `ms110d-wn1` | BPSK r1/8 | **On-air** - hard-gated; campaign clean |
| `ms110d-wn2` | BPSK r1/4 | **On-air** - hard-gated; proven live after a receive-level fix |
| `ms110d-wn3` | BPSK r1/3 | **On-air** - hard-gated; campaign clean |
| `ms110d-wn4` | BPSK r2/3 | **On-air** - the strongest on-air-proven MS110D point (Poor 8/9 coded-clean) |
| `ms110d-wn5` | BPSK r3/4 | **On-air** - hard-gated; campaign clean |
| `ms110d-wn6` | QPSK r3/4 | **On-air**, caveat - needs a receiver with a disciplined frequency reference, because receiver phase noise limits it |
| `ms110d-wn7` | 8PSK r3/4 | **Partial**, caveat - proven on air on a clean channel; the Poor channel is hard-gated in simulation only, because neither rig reaches the +19 dB it would need |
| `ms110d-wn8` | 16QAM r3/4 | **Partial**, caveat - proven on air on a clean channel; the Poor channel is hard-gated in simulation only, because neither rig reaches the +23 dB it would need |
| `ms110d-wn13` | QPSK r9/16 | **On-air**, caveat - same disciplined-reference condition as wn6 |

## Running several modems at once

A station can have several modems, and they all listen to the same audio at the same time. Each entry takes its own `subChannel`, 0 to 15, which is the KISS sub-channel your node or APRS software addresses it on. Two entries may not share one, and start-up refuses a file where they do. A 12 kHz mode and a 48 kHz mode can sit side by side; the channel then runs at 48 kHz.

A modem can also be given a `port` of its own, which presents that one modem as sub-channel 0 for software that speaks only one channel. [06-connect-your-software.md](06-connect-your-software.md) has the recipes.

## Where a mode sits in the audio

Each mode has a default audio centre: 1700 Hz for the AFSK modes, 1500 Hz for BPSK and QPSK (1650 Hz for `qpsk3600`), and the standard's own figure for the `freedv-*` and `ms110d-*` modes. Set `frequency` on the modem entry to move it. The baseband modes occupy the audio band from DC upwards and have no centre to move, so a `frequency` on `fsk9600`, `fsk9600-il2p`, `fsk4800-il2p`, `c4fsk9600` or `c4fsk19200` is refused at start-up.

On HF you would rather think in RF than in audio. Give every modem an `rfFrequency` in Hz instead and the modem works out the dial to set and where each modem lands in the passband. [08-hf.md](08-hf.md) covers band placement, and the arithmetic is in [Band placement](reference/config.md#band-placement-sideband-dialfrequency-and-rffrequency).

## Diversity banks

The HF PSK modes and the 300 baud AFSK modes run a bank of decoders rather than one, each branch tuned a few Hz either side of centre, so a station whose transmitter sits a little off frequency still copies. `offsetPairs` sets how many branches there are either side (4 on most PSK modes, 5 on the AFSK 300 modes, 0 on `qpsk3600`) and `offsetStepHz` the spacing between them (the baud rate divided by 40, or 35 Hz on the AFSK 300 modes). Set `offsetPairs` to 0 for a single decoder. `afsk1200-multi` runs a fixed bank of three pairs and ignores both keys. Every default is listed under [`modems`](reference/config.md#modems).

## Accepting plain IL2P

An IL2P+CRC mode checks each frame twice: Reed-Solomon corrects it, and the CRC that NinoTNC-family senders append confirms it. Set `"acceptPlainIl2p": true` on the modem entry to pass frames with no CRC to your software as well. Reed-Solomon alone then stands behind those frames, the journal says `passing plain IL2P (no trailing CRC) to the host: those frames are checked by Reed-Solomon alone` once per modem at start-up, and the station page badges the rows `RS ONLY`. The key is refused on a mode that does not run IL2P+CRC.

## NinoTNC ident beacons

A NinoTNC cannot identify itself inside the PSK modes, so it idents beside them in 300 baud AFSK, 200 Hz above the carrier. For `bpsk300`, `bpsk300-multi`, `bpsk300-nocrc`, `bpsk1200`, `bpsk1200-multi`, `qpsk600` and `qpsk2400` the modem runs a receive-only listener at that offset, which follows the modem when you retune it. An ident arrives as an `id[0] KK4HEJ>IDENT 16 bytes` line in the journal, and the station page marks the burst and its frames-panel row `ID` ([07-station-page.md](07-station-page.md)). The listener takes no KISS sub-channel and does not affect carrier sense. Turn the listeners off with [`"idBeacons": false`](reference/config.md#idbeacons).

## Modes from a plugin

`modemPlugins` loads assemblies from outside the package, and the modes they bring join the list the modem accepts. You write one as `pluginId:mode`; `--modem` cannot spell it, because that flag already uses the colon. Each plugin that loads is named in the journal as `modem plugin: <id> from <path> [mode, mode]`. The keys are under [`modemPlugins`](reference/config.md#modemplugins), and the contract a plugin implements is in [docs/dev/modem-plugins.md](dev/modem-plugins.md).

## ARDOP and POCSAG

Neither is a catalogue mode, so neither appears in the tables above and neither is spelt like one.

| | What you write | Where it listens | What it carries |
|---|---|---|---|
| ARDOP | `"mode": "ardop"` on a `modems` entry | 8515, data on 8516 | HF ARQ sessions for Pat and Winlink Express |
| POCSAG | the top-level `paging` section | 8106 | pages you transmit to POCSAG pagers |

Both take `frequency` or `rfFrequency` like a modem, and both share the channel, the carrier sense and the PTT line with the packet modems. The journal names the paging service `pocsag1200` (or 512 or 2400, by baud), which is a label and not a mode you write.

[06-connect-your-software.md](06-connect-your-software.md) sets up both, and [reference/ports-and-endpoints.md](reference/ports-and-endpoints.md) has the two protocols.

## Related

- [02-first-station.md](02-first-station.md) puts an `afsk1200` station on the air.
- [08-hf.md](08-hf.md) places several modes in one HF passband.
- [06-connect-your-software.md](06-connect-your-software.md) attaches your node or APRS software to a sub-channel.
- [reference/config.md](reference/config.md#modems) lists every key of a `modems` entry.
- [dev/mode-validation.md](dev/mode-validation.md) records how each mode was proven.
