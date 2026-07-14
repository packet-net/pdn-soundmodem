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

Next: HDLC/AX.25 bit layer, then the demodulators (300 BPSK IL2P+CRC and 1200 AFSK first),
DCD, audio I/O (ALSA), KISS TCP, and the spectrum/waterfall feed.

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

The sibling [packet.net](https://github.com/packet-net/packet.net) repo is AGPL-3.0; the two
combine under GPLv3 §13 / AGPLv3 §13. Nothing MIT-licensed may depend on this package.

The NuGet package id is `pdn-soundmodem`; the assembly and namespace are `Packet.SoundModem`.
