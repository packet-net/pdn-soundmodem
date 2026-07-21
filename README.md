# pdn-soundmodem

A headless soundcard packet-radio modem in C# / .NET 10. No GUI, no Qt — a modem engine
designed to serve two masters from one core:

- **Integrated**: an in-process transport for the [PDN node](https://github.com/packet-net/packet.net)
  (`kind: soundmodem` port), with native DCD fed straight into the AX.25 stack's carrier-sense
  seam, sample-accurate TX-complete, and a browser waterfall served through the node's web UI.
- **Standalone**: a headless-first KISS-TCP modem daemon any host application (LinBPQ, APRS
  software, …) can attach to, in the niche QtSoundModem serves today.

## Status

All planned modem families are implemented and bench-proven. What exists today:

- **IL2P codec** (spec draft v0.6, including IL2P+CRC): full frame encode/decode — Type 0/1
  headers, packet-synchronous scrambling, Reed-Solomon FEC (GF(2⁸) 0x11D), payload block
  segmentation, Hamming-protected trailing CRC. Byte-exact against all three example packets
  in the spec, with error-correction and fuzz roundtrip coverage.

Modem coverage completes the NinoTNC mode table: all 15 of its DIP-selectable modes are
implemented and bench-proven bidirectionally against a real NinoTNC (firmware 3.44) over a
wired CM108 loop — 9600 GFSK (AX.25 + IL2P+CRC), 4800 GFSK, 3600/2400/600 QPSK,
1200/300 BPSK, 1200 AFSK (AX.25 + IL2P+CRC) and 300 HF AFSK (AX.25/IL2P/IL2P+CRC), plus
FX.25 on 1200 AFSK and C4FSK (modes 1/3, 9600 + 19200). See
[docs/ninotnc-loop.md](docs/ninotnc-loop.md) § Coverage.

**Interop is per-mode and explicit — never traded away.** Every mode states which peers it
interoperates with, and NinoTNC compatibility is never given up to suit another modem:

- **Universal** — 1200/300 AFSK (Bell 202) and 9600 GFSK (G3RUH): interoperate with NinoTNC,
  Dire Wolf and QtSoundModem alike.
- **NinoTNC / QtSM V26A** — the BPSK/QPSK IL2P modes use the V.26A phase map, so they pair
  with a NinoTNC and with QtSoundModem's **V26A** modes (not its legacy UZ7HO QPSK maps).
- **NinoTNC + Dire-Wolf RUH** — 4800 GFSK IL2P+CRC: NinoTNC-derived, and cross-validated
  both ways against QtSoundModem's Dire-Wolf RUH-4800.
- **NinoTNC / MMDVM-TNC** — C4FSK 9600 + 19200 (modes 1/3): the MMDVM-TNC "Mode 2" wire
  format, bench-proven 8/8 bidirectionally against a NinoTNC at first live attempt.
- **FreeDV datac (waveform) / pdn (payload)** — `freedv-datac0/1/3/4/13/14` (all six
  datac modes; datac4/13/14 are the narrow RX-band-pass-filtered set): HF OFDM burst modes
  whose *waveform* is codec2/FreeDV-compatible (validated in both directions against
  codec2 1.2.0's own `freedv_data_raw_tx`/`rx`), while the *payload content* is the
  family-standard IL2P+CRC bit stream — a pdn↔pdn convention, since FreeDV defines no
  framing at the raw-data layer (FreeDATA layers its own ARQ there instead). Frames span
  packet boundaries within a burst, so even datac14's 3-byte packets carry full AX.25
  frames. Runs on the 48 kHz DSP path (the engine is native 8 kHz; 48000 = 6·8000).
- **ARDOP (Winlink) — ardopcf-compatible virtual TNC** — `--ardop <port>`: a complete
  ARDOP 1 implementation (4FSK/4PSK/8PSK/16QAM at 200–2000 Hz, FEC + full ARQ with
  bandwidth negotiation and gearshift) behind a byte-compatible clone of
  [ardopcf](https://github.com/pflarue/ardop)'s TCP host interface (command port +
  data port = port+1), so **Pat, Winlink Express, ARIM/gARIM and hamChat connect
  unmodified** — validated by a real Pat↔Pat message exchange (our modem one side,
  ardopcf the other), a 107-command host-transcript diff against a live ardopcf
  (byte-identical), full-stack ARQ sessions against ardopcf in both roles, and an
  RXO (receive-only monitor) leg decoding a third-party ardopcf↔ardopcf session.
  PROTOCOLMODE ARQ, FEC and RXO are all supported. The ARDOP channel is dedicated
  (`--ardop` is exclusive with `--modem`/`--paging`): ARDOP runs its own channel
  discipline, and the daemon's CSMA is bypassed (persistence forced to 255) while
  PTT keying and sample-domain TX-complete still come from the shared channel path.
  Documented divergences from ardopcf: no busy detector (BUSY TRUE/FALSE never sent;
  BUSYDET/BUSYBLOCK accepted but inert), log-level and CWID commands accepted but
  inert, TXFRAME (dev command) unimplemented, VERSION reports `pdn-soundmodem`.
- **DAPNET / POCSAG pagers** — `pocsag1200` (plus 512/2400): the paging waveform (CCIR
  Radiopaging Code No. 1, 2-FSK NRZ + BCH(31,21)), implemented spec-first and
  cross-validated against multimon-ng (every page byte-exact; `samples/pocsag/`).
  1200 bd is the DAPNET amateur paging network's rate (439.9875 MHz). This is a *paging*
  feature beside the packet modes — pages, not AX.25 frames — so it is not a KISS port:
  the library ships `PocsagEncoder`/`PocsagDecoder`, the `sm-pocsag` CLI encodes/decodes
  WAVs, and the daemon's `--paging <port>` endpoint takes
  `PAGE <ric> <function> ALPHA|NUMERIC|TONE [text]` over TCP (one UTF-8 line per
  command, `OK <id>`/`ERR <reason>` replies), transmits through the same CSMA/PTT
  channel-access path as everything else, and broadcasts every page heard on channel to
  its clients as `HEARD …` lines — a local paging API (pdn). Speaking the DAPNET-core
  transmitter protocol is a possible future follow-up.
- **MIL-STD-188-110D App D (waveform) / pdn (payload)** — `ms110d-wn0/1/2/3/4/5/6/7/8/13`:
  the public 3 kHz serial-tone HF waveform of MIL-STD-188-110D Appendix D (the
  Distribution-A counterpart of NATO STANAG 5069) — single-carrier 1800 Hz / 2400 Bd,
  SRRC-shaped, an autobaud preamble, tail-biting convolutional FEC + interleaving, from a
  Walsh-orthogonal 75 bps floor up through BPSK/QPSK/8PSK/16-QAM (6400 bps). **Phase A**
  (Walsh/BPSK/QPSK, WN 0–6/13) and **Phase B** (8PSK WN 7, 16-QAM WN 8) are implemented;
  all 10 waveform numbers pass the standard's Table D-LXIV AWGN performance masks at full
  statistical budget (3 M bits, 0 errors). The equalizer stack is probe-trained with a
  fractionally-spaced (T/2) decision-feedback (DFE) equaliser — batch regularized
  least-squares training, NLMS adaptation, and RLS tracking for fading channels — augmented
  by iterative turbo re-equalization (decode → re-encode → re-equalize, up to 5 passes with
  early exit) and a BCJR (MAP) equalizer for frequency-selective fading on BPSK, gated by
  block-level fading detection from residual variance so that AWGN channels take the cheaper
  DFE path. The Poor-channel (Watterson 2-path Rayleigh) masks are the current research
  frontier. **Phase C** (higher-order QAM, WN 9–12) is still to come. No open App-D
  receiver existed before this one, so there is no external oracle: the interop-critical spec
  tables were transcribed twice independently and diffed to zero value conflicts, and a
  from-scratch Watterson/CCIR channel simulator plus the spec masks stand in for one. Like
  the FreeDV modes it carries the family-standard IL2P+CRC payload (a pdn↔pdn convention —
  App D defines no data-link framing; STANAG 5066 is that layer and is not implemented), so
  it is a robust HF *bit pipe*, not a connected-ARQ port (ARDOP is the connected one). Runs
  on the 48 kHz DSP path (native 9600 Hz). Design + verified tables: [docs/ms110d/](docs/ms110d/).

**Per-modem audio centre (QtSoundModem-style).** Each narrow modem's audio centre is
settable with the third field of `--modem N:MODE:FREQ` (or `"frequency"` in the config), on
both transmit and receive — e.g. `--modem 0:bpsk300:1459` places 300 BPSK at 1459 Hz to meet
a peer that sits off the usual centre, exactly as QtSoundModem's per-modem *Freq* does. It
applies to the AFSK tone-pair modes (`afsk*`, centre = the mark/space midpoint, default
1700 Hz) and the BPSK/QPSK carrier modes (`bpsk*`/`qpsk*`, default 1500 Hz; 1650 for
`qpsk3600`). The baseband FSK families (`fsk*`/`c4fsk*`) fill DC-to-Nyquist and have no
audio centre, and the spec-fixed waveforms (`freedv-*`, `ms110d-*`, POCSAG, ARDOP) are
pinned by their standards — a `:FREQ` on any of those is rejected, not silently ignored.

The QtSoundModem cross-validation matrix (which QtSM `ModemType` each of our modes pairs
with, both directions) is in [docs/qtsm-loop.md](docs/qtsm-loop.md) § Results.

**Hear it:** [samples/demo/](samples/demo/) holds one representative WAV per mode family —
each produced by the real transmit path, carrying a genuine frame, and decoded back to its
payload with the reference tool a ham would use (multimon-ng, codec2 `freedv_data_raw_rx`,
ardopcf `--decodewav`) where one exists, or our own receiver where none does.

The research that scoped this project lives in
[packet.net `docs/research/headless-soundmodem.md`](https://github.com/packet-net/packet.net/blob/main/docs/research/headless-soundmodem.md).

## Building

```sh
dotnet build
dotnet test
```

## Licence, provenance and credits

**GPL-3.0-or-later** (see [COPYING](COPYING)). This project stands on the shoulders of GPL
prior art and stays GPL:

- **UZ7HO SoundModem** (Andrei Kopanchuk, UZ7HO) via **QtSoundModem** (John Wiseman, G8BPQ) —
  GPLv3+ — the reference for the demodulator family this project ports.
- **Dire Wolf** (John Langner, WB2OSZ) — GPL-2.0-or-later — reference for the IL2P wire
  behaviour, the 9600 RUH modem design, and the DPLL DCD algorithm.
- **IL2P** is by Nino Carrillo (KK4HEJ) — [spec draft v0.6](https://tarpn.net/t/il2p/il2p-specification_draft_v0-6.pdf);
  the IL2P implementation here is written from that spec and validated against its example
  packets (provided by Jon Naylor, G4KLX).
- **MMDVM-TNC** (Jonathan Naylor, G4KLX) — GPL-2.0-or-later — the "Mode 2" C4FSK wire
  format (preamble, sync, symbol mapping) that the NinoTNC's C4FSK modes inherit and that
  `C4fskModem` implements.

The sibling [packet.net](https://github.com/packet-net/packet.net) repo is AGPL-3.0; the two
combine under GPLv3 §13 / AGPLv3 §13. Nothing MIT-licensed may depend on this package.
**[PROVENANCE.md](PROVENANCE.md) records, per component, what this code is truly based on.**

The NuGet package id is `pdn-soundmodem`; the assembly and namespace are `Packet.SoundModem`.
