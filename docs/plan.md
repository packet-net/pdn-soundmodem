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
- ✅ Real-corpus benchmark — **ahead of the reference on Track 2** (2026-07-15, WA8LMF
  TNC Test CD Tracks 1+2, off-air 1200 AFSK APRS, kept locally in corpus/,
  redistribution TBC). At 12 kHz (the daemon's native rate), multi+emphasis bank:
  **Track 2 ours 972 vs atest 970; Track 1 ours 959 vs atest 999 (96 %)**. The path:
  flat single 60 → emphasis branches (the twist killer, 267→970) → sub-sample DPLL
  crossing interpolation (single 60→269; Track 1 937→959; Track 2 970→972). Frame-set
  diffs show the remaining Track-1 misses are marginal-SNR frames spread across many
  stations (direwolf's multi-slicer edge); next levers if wanted: slicer-level branches,
  per-tone AGC, dual-threshold + Memory-ARQ. Negative results banked in code comments:
  searching/locked inertia switching regressed badly (268→31), and crossing
  interpolation on the 9600 baseband chases ISI jitter into the eye at 5 samples/bit —
  both documented in BitDpll/Fsk9600Modem. 44.1 kHz full-bank: 955 with
  interpolation (954 before; atest 983) — at 36.75 samples/bit the quantisation jitter
  was already small, confirming the interpolation win is concentrated at the coarse
  native 12 kHz rate and the residual 44.1 kHz gap is direwolf's multi-slicer margin,
  not timing.
- ⬜ Phase 0 hardware corpus validation for the IL2P modes (needs rig time).
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
- ✅ Live RX soak (2026-07-15): 60 s daemon run on this box's real HDA codec via the
  fresh audio group — 48 kHz capture → decimator → 21-branch multi bank, KISS TCP up,
  clean exit. Found+fixed on first contact: consumer cards refuse direct 12 kHz
  playback opens ("snd_pcm_set_params: Invalid argument") — TX now plays at the
  card-native rate through a new image-rejecting Upsampler/UpsamplingAudioOutput
  (the mirror of the capture decimator), covered by a full simulated-card-path
  roundtrip test. Longer soaks + a decode of real off-air audio still worthwhile
  when an RF source is nearby.

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
  (spec-exact output remains available; our RX ignores the bit). ✅ Bench confirmed
  against NinoTNC firmware 3.41 (2026-07-15): all four IL2P pairs decode our frames
  with `IL2PRxUnCr` = 0.
- ✅ CM108 hidraw PTT (`--ptt cm108:/dev/hidraw0[:gpio]`, direwolf/QtSM-compatible
  5-byte report; 2026-07-15).
- ✅ **Wired NinoTNC interop, every supported mode, both directions, sustained**
  (2026-07-15, firmware 3.41, CM108 loop per docs/ninotnc-loop.md § Results): afsk1200,
  bpsk300, qpsk2400, qpsk3600, fsk9600 classic + IL2P — 100% each way after three rig
  fixes (AlsaPcm start/re-prepare; continuous-time QPSK phase synthesis; ×2 DPLL
  interpolation on 9600 RX). DCD assert/release lags measured and CSMA-safe.
- ⬜ PDN `IRigControl` PTT (packet.net side); over-air (RF) NinoTNC runs when a radio
  pair is available — the wired loop already answers the baseband/phase-map/FEC-bit
  questions.

### Phase 4 — breadth 🟡
- ✅ Multi-decoder offset bank (2026-07-15): `Afsk1200MultiModem` — 2·pairs+1 branches at
  30 Hz steps with content dedupe (daemon mode `afsk1200-multi`). On direwolf's 100-frame
  noise battery: **38 = exact atest parity** (single decoder: 34). Off-tune-transmitter
  and dedupe tests.
- ✅ CM108 PTT (logged under Phase 3).
- ✅ Daemon config file (2026-07-15): `--config soundmodem.json` (comments + trailing
  commas tolerated; `soundmodem.example.json` in repo root); CLI flags still work and
  append.
- ✅ FX.25 (2026-07-15): codec (11 correlation tags, RS fcr=1 at 16/32/64 check bytes,
  rotating-flag fill, format auto-pick) + streaming deframer with miscorrection guard.
  Cross-validated bidirectionally vs Dire Wolf: 4/4 decoding gen_packets -X audio
  (fixture committed), 3/3 of our TX decoded by atest and explicitly labelled FX.25.
  Not yet surfaced as a modem/daemon option (parallel-RX + dedupe wiring pending).
- ✅ FX.25 modem/daemon wiring (2026-07-15): Afsk1200Modem fx25 option (Receive /
  TransmitReceive with dedupe across the FX.25 and embedded-HDLC paths); daemon modes
  afsk1200-fx25 / afsk1200-fx25rx; transparency + dedupe tests.
- ✅ .deb packaging (2026-07-15): packaging/build-deb.sh (amd64/arm64/armhf,
  self-contained single file, Depends: libasound2 only, systemd unit + example config,
  pdn-soundmodem system user with audio+dialout). amd64 package binary smoke-tested
  (4/4 on the direwolf fixture); arm64 built ready for the Pi.
- ⬜ DCD-over-KISS extension (awaiting an agreed NinoTNC-ecosystem format); Windows
  audio backend (deferred 2026-07-15); extra decode-only listeners; multi-decoder banks
  for the PSK modes.

## Blocked on Tom / hardware (updated 2026-07-15 later)

- ~~NuGet~~ **RESOLVED**: NUGET_API_KEY granted; 0.1.0 and 0.1.1 published (0.1.0
  confirmed indexed on nuget.org).
- ~~audio group~~ **RESOLVED**: `usermod -aG audio tf` run; both ALSA hardware smoke
  tests now pass on this box's real sound card (via `sg audio` until re-login).
- ~~soundcard on the NinoTNC bench rig~~ **RESOLVED** (2026-07-15): CM108 widget wired
  to the NinoTNC per docs/ninotnc-loop.md; every supported mode validated bidirectionally
  (see § Results there). The open wire questions are answered: NinoTNC's 9600 GFSK
  matches the direwolf-validated baseband both ways, the spec QPSK phase map is
  NinoTNC-compatible (no pairwise-negotiation divergence), and the legacy-max-FEC bit
  default is confirmed right.
- **Hardware still pending**: a Pi for the DSP benchmark and .deb trial; over-air (RF)
  NinoTNC runs; per-mode WAV corpus recording off the rig (bench decode counts exist,
  committed corpora don't yet).

## Amendment log

### 2026-07-15 (later still) — NinoTNC loop: all six pairs bidirectional, sustained

The wired CM108↔NinoTNC rig (docs/ninotnc-loop.md) ran its first full campaign against
firmware 3.41 via the new `nino-bench` tool, which reads NinoTNC-side truth from the
GETALL diagnostic registers. Every supported pair (afsk1200:6, bpsk300:8, qpsk2400:11,
qpsk3600:5, fsk9600:0, fsk9600-il2p:2) now passes 100% both directions in sustained
runs, with DCD assert/release lag measured against the audio envelope (assert ≤ tens of
ms, release always late — CSMA-safe). Three defects found and fixed, none of which any
loopback/WAV test caught: AlsaPcm needed an explicit capture `snd_pcm_start` (CM108B
EIO) and a `snd_pcm_prepare` after drain (second TX EBADFD); QpskModulator's
integer-boundary synthesis jittered 1800-baud symbol edges by ±½ sample and collapsed
the phase ramp to a hard step (56–88% NinoTNC decode → 100% after continuous-time
rewrite; `TxRampFraction` default 0.25 — 0.5 drops to ~7%, the NinoTNC wants sharp
transitions); Fsk9600Modem RX now interpolates ×2 before the DPLL à la direwolf
(classic-HDLC 88% → 100%, DCD assert lag → ≤2 ms). Also learned: QPSK-from-cold wants
≥500 ms TXDELAY (NinoTNC demod lock); the bench initially mis-blamed audio for what was
a `SerialPort.ReadTimeout` TimeoutException silently killing its serial pump — GETALL
before/after each direction now makes that class of error self-diagnosing. Level
verdict for the rig as wired: RX peak 0.17–0.28 FS across modes, no pot changes needed.

### 2026-07-15 (later) — FX.25 + multi-decoder + daemon config + .deb; publish staged

Multi-decoder AFSK bank lands at exact atest parity (38/38-reference on the direwolf noise
battery, from 34 single-decoder). FX.25 codec + deframer cross-validated bidirectionally
with direwolf and wired into the AFSK modem/daemon with transparent-dedupe. Daemon gains a
JSON config file and .deb packaging (amd64 smoke-tested, arm64 built). NuGet publish
workflow added and v0.1.0 tagged with Tom's authorization — pack+tests green on the org
runner; push skipped pending the NUGET_API_KEY secret being granted to this repo (see
Blocked on Tom). 147 tests green.

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
