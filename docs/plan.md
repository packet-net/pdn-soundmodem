# pdn-soundmodem — plan

Living status document. Keep current in the same PR as the work (packet.net §18 discipline).
Founding research: [packet.net `docs/research/headless-soundmodem.md`](https://github.com/packet-net/packet.net/blob/main/docs/research/headless-soundmodem.md)
— read it before substantive work; the decisions in its §Decisions bind this repo.

## Decisions (Tom, 2026-07-14)

- Separate GPL-3.0-or-later repo (this one); packet.net consumes via NuGet (`pdn-soundmodem`).
- Phase 1 modes: **300 BPSK IL2P+CRC + 1200 AFSK**; QPSK 2400/3600 + 9600 GFSK follow with
  NinoTNC-interop exit gates.
- **QtSM-style multiplex channel model**: up to 4 logical modems per audio side, KISS
  sub-channel nibble addressing (the PDN adapter may still expose one transport per modem).
- Both deployment shapes are goals: integrated PDN port and standalone KISS-TCP daemon,
  one core, headless-first.
- Naming: repo/package/daemon `pdn-soundmodem`; assembly/namespace `Packet.SoundModem`.

## Phases

### Phase 0 — feasibility bench ⬜
Pi 4/5 DSP benchmark (the i7 numbers from the research need Pi confirmation); ALSA
capture/playback soak on a CM108-class dongle (period size, xruns, TX-release latency);
record the **WAV corpus** through the packet.net NinoTNC bench rig (every NinoTNC mode,
clean + attenuated + noisy) — the decode-regression suite everything else is judged by.
WA8LMF Track 2 for AFSK (redistribution terms TBC).

### Phase 1 — frame codecs + offline RX 🟡 in progress
- ✅ IL2P codec (spec v0.6 incl. IL2P+CRC): Type 0/1 headers, scrambler, RS(0x11D) FEC,
  block segmentation, Hamming CRC trailer. Byte-exact vs all three spec example packets;
  error-correction + fuzz roundtrip tests. (2026-07-14)
- ⬜ HDLC bit layer (flags, stuffing, NRZI, FCS) for classic AX.25.
- ⬜ WAV/raw-PCM offline harness.
- ⬜ 300 BPSK demodulator (UZ7HO lineage) + IL2P sync-word hunter (±1 bit).
- ⬜ 1200 AFSK demodulator (UZ7HO discriminator design; direwolf papers as cross-ref).
- Exit: corpus decode rates ≥ QtSoundModem and ≥ NinoTNC on identical recordings.

### Phase 2 — live RX + DCD + waterfall ⬜
ALSA capture (48 kHz native, polyphase ÷4 to 12 kHz), per-channel DCD (DPLL
popcount-hysteresis + decoupled energy busy detector), spectrum feed; PDN `kind: soundmodem`
port (RX-only) with `ICarrierSense` via the PortSupervisor probe; waterfall UI in the node.

### Phase 3 — TX ⬜
Modulators, sample-domain PTT timing (RTS/DTR + CM108 hidraw + PDN `IRigControl`),
modem-side CSMA (`ICsmaChannelParams`), `ITxCompletionTransport`; bench-rig loop tests;
then QPSK 2400/3600 and 9600 GFSK legs, each gated on NinoTNC over-air interop.

### Phase 4 — breadth ⬜
Multi-decoder offset bank (RCVR pairs), FX.25, standalone KISS-TCP daemon + .deb, DCD-over-KISS
extension (aligned with whatever format the NinoTNC ecosystem agrees), Windows audio backend,
extra decode-only listeners per passband.

## Amendment log

### 2026-07-14 — repo founded; IL2P codec lands
Repo created from the packet.net research + decisions. Scaffold (net10.0, CPM, xunit +
AwesomeAssertions, self-hosted CI) plus the first functional layer: complete IL2P frame codec
written from spec draft v0.6, validated byte-exact against the spec's S/UI/I example packets,
with RS error-correction tests (1-byte header repair, 8-byte payload-block repair, fuzz)
and encode/decode roundtrip fuzz across frame types, Type 0 fallbacks and multi-block
payloads. Wire nuance recorded: spec vectors leave the RESERVED header bit clear (Dire Wolf
sets it) — we encode clear, ignore on RX. CRC variant pinned as CRC-16/X-25 by the S-frame
vector (0xF0DB).
