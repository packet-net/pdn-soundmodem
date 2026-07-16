# pdn-soundmodem

A headless soundcard packet-radio modem in C# / .NET 10. No GUI, no Qt — a modem engine
designed to serve two masters from one core:

- **Integrated**: an in-process transport for the [PDN node](https://github.com/packet-net/packet.net)
  (`kind: soundmodem` port), with native DCD fed straight into the AX.25 stack's carrier-sense
  seam, sample-accurate TX-complete, and a browser waterfall served through the node's web UI.
- **Standalone**: a headless-first KISS-TCP modem daemon any host application (LinBPQ, APRS
  software, …) can attach to, in the niche QtSoundModem serves today.

## Status

Early — Phase 1 (see [docs/plan.md](docs/plan.md)). What exists today:

- **IL2P codec** (spec draft v0.6, including IL2P+CRC): full frame encode/decode — Type 0/1
  headers, packet-synchronous scrambling, Reed-Solomon FEC (GF(2⁸) 0x11D), payload block
  segmentation, Hamming-protected trailing CRC. Byte-exact against all three example packets
  in the spec, with error-correction and fuzz roundtrip coverage.

Modem coverage tracks the NinoTNC mode table: 13 of its 15 DIP-selectable modes are
implemented and bench-proven bidirectionally against a real NinoTNC (firmware 3.44) over a
wired CM108 loop — 9600 GFSK (AX.25 + IL2P+CRC), 4800 GFSK, 3600/2400/600 QPSK,
1200/300 BPSK, 1200 AFSK (AX.25 + IL2P+CRC) and 300 HF AFSK (AX.25/IL2P/IL2P+CRC), plus
FX.25 on 1200 AFSK. The gap is C4FSK (modes 1/3). See
[docs/ninotnc-loop.md](docs/ninotnc-loop.md) § Coverage.

**Interop is per-mode and explicit — never traded away.** Every mode states which peers it
interoperates with, and NinoTNC compatibility is never given up to suit another modem:

- **Universal** — 1200/300 AFSK (Bell 202) and 9600 GFSK (G3RUH): interoperate with NinoTNC,
  Dire Wolf and QtSoundModem alike.
- **NinoTNC / QtSM V26A** — the BPSK/QPSK IL2P modes use the V.26A phase map, so they pair
  with a NinoTNC and with QtSoundModem's **V26A** modes (not its legacy UZ7HO QPSK maps).
- **NinoTNC + Dire-Wolf RUH** — 4800 GFSK IL2P+CRC: NinoTNC-derived, and cross-validated
  both ways against QtSoundModem's Dire-Wolf RUH-4800.
- **NinoTNC / MMDVM-TNC** — C4FSK (once implemented): the MMDVM-TNC "Mode 2" wire format.
- **FreeDV datac (waveform) / pdn (payload)** — `freedv-datac0/1/3`: HF OFDM burst modes
  whose *waveform* is codec2/FreeDV-compatible (validated in both directions against
  codec2 1.2.0's own `freedv_data_raw_tx`/`rx`), while the *payload content* is the
  family-standard IL2P+CRC bit stream — a pdn↔pdn convention, since FreeDV defines no
  framing at the raw-data layer (FreeDATA layers its own ARQ there instead). Frames span
  packet boundaries within a burst, so even datac0's 14-byte packets carry full AX.25
  frames. Runs on the 48 kHz DSP path (the engine is native 8 kHz; 48000 = 6·8000).

The QtSoundModem cross-validation matrix (which QtSM `ModemType` each of our modes pairs
with, both directions) is in [docs/qtsm-loop.md](docs/qtsm-loop.md) § Results.

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
  GPLv3+ — the reference for the demodulator family this project will port.
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
