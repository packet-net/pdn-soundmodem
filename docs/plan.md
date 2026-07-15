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
- (2026-07-15) Hardware gates batch up; work continues software-only until the rig/Pi/audio
  group are ready. PDN-side DCD/utilisation reaches operators via a **port-level status
  surface** (new port-scoped API/metric/dashboard fed by any carrier-sense-capable
  transport), not by widening `radio:` read-models. **Linux-only** audio for now; the
  layer's shape admits an SDL3 backend later.

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
- ✅ HDLC bit layer (flags, stuffing, abort, NRZI, FCS) + streaming IL2P deframer
  (±1-bit sync tolerance). (2026-07-14)
- ✅ WAV 16-bit PCM read/write offline harness. (2026-07-14)
- ✅ 300 BPSK modulator + demodulator (differential detection per the IL2P symbol map;
  QtSM P300 filter plan) — clean/noisy/offset/multi-block loopbacks green. (2026-07-14)
- ✅ 1200 AFSK modulator + demodulator (UZ7HO Mux3 chain: BPF → mix → I/Q LPF →
  cross-multiply discriminator, power-normalised, envelope slicer, direwolf-style DPLL) —
  clean/noisy/quiet/back-to-back loopbacks green. (2026-07-14)
- ✅ Cross-validation vs Dire Wolf (independent implementation): 4/4 decode parity with
  atest on gen_packets AFSK and **IL2P-over-AFSK** fixtures (committed as regression
  tests); direwolf's RESERVED-bit convention tolerated as designed. On the 100-frame
  increasing-noise battery: ours 34 vs atest 38 (single decoder vs multi-slicer — the
  Phase 4 multi-decoder bank is the path to parity+). `tools/Packet.SoundModem.Decode`
  (sm-decode) is our atest equivalent. (2026-07-14)
- ⬜ Phase 0 hardware corpus validation (needs rig time).
- Exit: corpus decode rates ≥ QtSoundModem and ≥ NinoTNC on identical recordings
  (needs Phase 0 recordings — loopback tests alone do not demonstrate this).

### Phase 2 — live RX + DCD + waterfall 🟡 in progress
- ✅ Native DCD (2026-07-14): `PacketDcd` (direwolf DPLL transition-quality scoring,
  30/32-6/32 hysteresis) + `EnergyBusyDetector` (display-decoupled block power vs
  min-tracking noise floor, 6/3 dB hysteresis, hold, warm-up-aware seeding). Exposed on
  both demodulators as `CarrierDetect` / `ChannelBusy` + `ResetCarrierState()` — the
  surface the PDN `ICarrierSense` adapter consumes. Behavioural tests incl. the
  steady-carrier-is-busy-but-not-DCD case headless QtSM cannot see.
- ✅ Spectrum feed groundwork (2026-07-14): native radix-2 `Fft` + `SpectrumSource`
  (Hann, 4096-pt, dB-scaled u8 bins ≈2 kB/line ~3/s per channel).
- ✅ ALSA layer (2026-07-14): `AlsaPcm` (libasound P/Invoke, capture+playback, xrun
  recovery, `Drain` for sample-domain PTT release) + `Decimator` (real anti-aliased
  48 k→12 k ÷4; aliasing-suppression test). Hardware smoke tests are SkippableFact —
  NOTE: they skip on this dev box because user `tf` lacks the `audio` group
  (`sudo usermod -aG audio tf` to enable); they will run on the bench/Pi.
- ✅ SoundModemChannel (2026-07-15): multiplex composition — N modems per audio side
  behind IModem (Afsk1200Modem, Bpsk300Modem), aggregated CarrierDetect/ChannelBusy,
  spectrum tap, TX queue with classic p-persistent CSMA, PTT bracketing, per-frame
  TX-complete tasks, half-duplex RX suppression + carrier reset after TX.
- ✅ Standalone KISS-TCP daemon (2026-07-15): `pdn-soundmodem` binary — in-repo KISS
  framing (no AGPL dependency), multi-client TCP server, sub-channel nibble ↔ modem mux,
  ACKMODE with true TX-complete echo (post-drain, not a timer), KISS parameter commands
  actually honoured (TXDELAY/P/SLOTTIME/TXTAIL — QtSM ignores these), serial RTS/DTR PTT,
  ALSA capture→decimate→RX loop, `--wav` offline mode (smoke-tested: 4/4 on the direwolf
  fixture). End-to-end tests: KISS-in → audio → independent demod, RX → broadcast to all
  clients, ACKMODE echo ordering, param plumbing. Not yet: config file, CM108 PTT,
  spectrum-over-TCP, stereo second channel, live-audio soak (hardware).
- ⬜ packet.net side: `kind: soundmodem` transport + `transport is ICarrierSense` probe at
  PortSupervisor (seam mapped in the research doc §5), spectrum SSE endpoint + waterfall
  UI (PdnPortTuningApi is the template; add to the SSE token allowlist; node-api.yaml).
- ⬜ Live RX soak on real audio hardware.

### Phase 3 — TX 🟡 software done for all Phase-1..3 modes
- ✅ TX for AFSK 1200 / BPSK 300 / QPSK 2400 / QPSK 3600 / 9600 (classic + IL2P), with
  modem-side p-persistent CSMA, serial RTS/DTR PTT, sample-domain TX-complete (drain) and
  TX tail — all in SoundModemChannel + the daemon (2026-07-15).
- ✅ QPSK 2400/3600 modem pair (spec QPSK symbol map, differential detection, fractional
  one-symbol delay for 1800 Bd at 12 kHz); loopbacks incl. noise/offset/multi-block.
- ✅ 9600 baseband modem, both framings, cross-validated BOTH WAYS vs Dire Wolf:
  classic G3RUH (NRZI→scramble TX order confirmed empirically; 4/4 their audio, 3/3 ours
  in atest) and IL2P (4/4 their audio via the new polarity-agnostic sync hunt; 3/3 ours
  in atest after the legacy-max-FEC discovery below).
- 🔎 **Interop discovery (desk-found, exactly the class the research predicted):** the
  v0.6-RESERVED header bit is still read by Dire Wolf (and the NinoTNC lineage) as the
  pre-v0.6 max-FEC selector — cleared, they parse payload blocks with the legacy
  2/4/6/8-parity plan and reject 16-parity frames (the spec's own example packets would
  not decode!). `Il2pCodec.Encode` now defaults `legacyMaxFecBit: true` for interop
  (spec-exact output remains available; our RX ignores the bit). Bench must confirm
  NinoTNC behaviour.
- ✅ CM108 hidraw PTT (`--ptt cm108:/dev/hidraw0[:gpio]`, direwolf/QtSM-compatible
  5-byte report; 2026-07-15).
- ⬜ PDN `IRigControl` PTT (packet.net side); over-air NinoTNC interop runs for every
  mode (hardware).

### Phase 4 — breadth 🟡
- ✅ Multi-decoder offset bank (2026-07-15): `Afsk1200MultiModem` — 2·pairs+1 branches at
  30 Hz steps with content dedupe (daemon mode `afsk1200-multi`). On direwolf's 100-frame
  noise battery: **38 = exact atest parity** (single decoder: 34). Off-tune-transmitter
  and dedupe tests.
- ✅ CM108 PTT (logged under Phase 3).
- ✅ Daemon config file (2026-07-15): `--config soundmodem.json` (comments + trailing
  commas tolerated; `soundmodem.example.json` in repo root); CLI flags still work and
  append.
- ⬜ FX.25; .deb packaging; DCD-over-KISS extension (awaiting an agreed
  NinoTNC-ecosystem format); Windows audio backend (deferred by decision 2026-07-15);
  extra decode-only listeners; multi-decoder banks for the PSK modes.

## Amendment log

### 2026-07-15 — QPSK + 9600 modems; the legacy-max-FEC interop discovery

QPSK 2400/3600 (spec symbol map, fractional-delay differential detection) and the 9600
baseband modem (classic G3RUH + IL2P framings) land with loopback suites; sm-decode grows
all modes; the daemon registers them (48 kHz auto-selected for 9600). Bidirectional Dire
Wolf cross-validation added for 9600 both framings (fixtures committed). Two wire-truth
finds: IL2P baseband polarity differs between implementations → the deframer now hunts the
sync word in both polarities (spec-recommended); and the v0.6 RESERVED header bit is still
a live max-FEC selector in Dire Wolf's decoder — clear = legacy variable-parity plan →
16-parity frames rejected. Encode now defaults the bit ON (`legacyMaxFecBit`), spec-exact
mode retained for the vector tests. 131 tests green.

### 2026-07-14 (later) — Phase 1 complete in software; DCD, spectrum, ALSA land

Same-day continuation: HDLC bit layer + IL2P streaming deframer; WAV harness; AFSK 1200 and
BPSK 300 modulator/demodulator pairs with loopback suites (noise, offset, quiet, multi-block,
back-to-back); cross-validation vs Dire Wolf built from source — 4/4 parity with atest on
clean AFSK and IL2P-over-AFSK fixtures (committed as regression tests), 34-vs-38 on the
100-frame noise battery (single decoder vs multi-slicer; multi-decoder bank is the Phase 4
answer). Two real-world demod fixes came out of direwolf audio: discriminator clamping
(silence noise over near-zero power deafened the envelope slicer) and flush-tail handling.
Then Phase 2 groundwork: native DCD (PacketDcd + EnergyBusyDetector on both demods),
radix-2 FFT + SpectrumSource waterfall feed, AlsaPcm P/Invoke + anti-aliased ÷4 Decimator.
`tools/Packet.SoundModem.Decode` (sm-decode) added as our atest equivalent. 101 tests
(99 pass + 2 ALSA smoke tests that need the audio group). Remaining Phase 1 exit gate —
hardware corpus ≥ QtSM/NinoTNC — needs bench-rig time (Phase 0).

### 2026-07-14 — repo founded; IL2P codec lands
Repo created from the packet.net research + decisions. Scaffold (net10.0, CPM, xunit +
AwesomeAssertions, self-hosted CI) plus the first functional layer: complete IL2P frame codec
written from spec draft v0.6, validated byte-exact against the spec's S/UI/I example packets,
with RS error-correction tests (1-byte header repair, 8-byte payload-block repair, fuzz)
and encode/decode roundtrip fuzz across frame types, Type 0 fallbacks and multi-block
payloads. Wire nuance recorded: spec vectors leave the RESERVED header bit clear (Dire Wolf
sets it) — we encode clear, ignore on RX. CRC variant pinned as CRC-16/X-25 by the S-frame
vector (0xF0DB).
