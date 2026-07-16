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
  native 12 kHz rate.
- ✅ **Ahead of the reference at BOTH rates** (2026-07-15, after the per-mode
  discriminator clamp — see the §17 entry): **Track 2 @12 kHz 983 vs atest 970; @44.1 kHz
  987 vs atest 983**. This supersedes the conclusion recorded above that the residual
  44.1 kHz gap (955 vs 983) was "direwolf's multi-slicer margin, not timing" — it was
  neither. It was our own fixed ±1 discriminator clamp letting silence pin the slicer's
  envelope trackers; a mode-aware clamp took 44.1 kHz 955 → 987 and 12 kHz single-decoder
  269 → 426. A conclusion that stopped at "the remaining gap is the other implementation's
  margin" was the thing that kept it hidden.
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
  behind IModem (AfskModem family, BpskModem), aggregated CarrierDetect/ChannelBusy,
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
- ✅ **Wired NinoTNC interop — 13 of 15 DIP modes, both directions** (2026-07-15,
  firmware **3.44**, CM108 loop; full tables in docs/ninotnc-loop.md § Results +
  § Coverage). Every NinoTNC mode except the two C4FSK ones now has a counterpart here
  and passes bidirectionally: fsk9600 (0), fsk9600-il2p (2), fsk4800-il2p (4), qpsk3600
  (5), afsk1200 (6), afsk1200-il2p (7), bpsk300 (8), qpsk600 (9), bpsk1200 (10), qpsk2400
  (11), afsk300 (12), afsk300-il2p (13), afsk300-il2pc (14). DCD assert/release lags
  measured and CSMA-safe throughout.
- ⬜ **C4FSK (modes 1/3) is the remaining coverage gap** — coherent 4-level FSK (19200 in
  20 kHz OBW, 9600 in 10 kHz; 2079/1039 Hz outer deviation), new in firmware 3/4.42. A
  genuinely new modem, not a reparameterisation of an existing one.
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

### 2026-07-16 (later still) — QtSoundModem interop: cross-validated against the ancestor

Built **QtSoundModem** (G8BPQ, UZ7HO lineage — the modem ours descends from) from source and
cross-validated the two over an **snd-aloop** virtual cable — no sound card, no radios. QtSM
runs headless via its genuine `nogui` switch (`QCoreApplication`, `main.cpp:49`). Full recipe,
device strings and results in **docs/qtsm-loop.md**; committed driver
`tools/Packet.SoundModem.QtsmBench` (`qtsm-bench`, a pure KISS-TCP client that frames-in /
counts-out on both modems); QtSM's QPSK transmissions checked in under `samples/qtsm/`.

**Every mode tested interoperates both ways** (qtsm→ours live + ours→QtSM continuous-WAV, both
artifact-free): afsk1200, afsk1200-il2p, bpsk300, qpsk2400, qpsk3600 all 9–10/10 each way.

The headline finding — the QpskModulator doc-comment's "pairwise-negotiated phase map" caveat
made concrete: **our `qpsk2400` pairs with QtSM's V26A/DW2400 (ModemType 12), NOT its legacy
"QPSK AX.25 2400bd" (type 10) or V26B (type 14)** — ours is the V.26A map (as NinoTNC and Dire
Wolf use). `qpsk3600` matches QtSM's legacy type-9 (QtSM has no V26 at 3600). Proven offline:
`sm-decode` reads QtSM's type-12 QPSK 8/8 and its type-10 0/8 (samples/qtsm/). Raised as a
tracking issue.

Two rig lessons worth keeping (both in docs/qtsm-loop.md): QtSM's `soundChannel[ch]=0` means
**channel disabled** (it then neither TX nor RX while looking alive — the bring-up time-sink);
and every audio process here must run under **`sg audio`** (this login shell isn't in the
audio process-group despite `/etc/group`). A real daemon defect surfaced and was **fixed**:
`--capture-rate 12000` (DSP-rate == capture-rate) crashed on a factor-1 `Decimator`; the RX
loop now feeds captured samples straight through when the rates match (Program.cs). This is
what lets the daemon run at the aloop's native 12 kHz. Filed as an issue for the record.

### 2026-07-16 (later) — issue tracker cleared: #1-#4 closed on evidence

All four open issues resolved and closed. #2's fix is the structural one: the
never-wider-than-a-NinoTNC test now measures its reference **from the checked-in
recordings at test time** — whole burst, identical frame content, explicit sample rates
(a first attempt inferred rate from burst length and mis-measured 48 k as 12 k; the same
error class the test polices). All 9 modes pass including qpsk3600, whose "9 % wider"
reading died with the window mismatch (fairly: ours 1808 Hz vs its 1887 Hz). #1 closed —
shaping fixed + enforced, idle-gap behaviour characterised as the TNC's, mode-5 matched
RX filter demoted to optimisation-without-a-driver. #3 closed: modem floors measured and
parity-enforced; the daemon's 300 ms documented as a radio PTT-to-RF allowance with a
guidance table in ninotnc-loop.md. #4 closed: root causes fixed earlier; the one-word
flag-fill residual priced as an explicit trade (I/Q LPF 750 Hz → 10/10 on that case but
WA8LMF 472 → 410; default stays 650, ctor parameter for ports that know their peer).

### 2026-07-16 (night) — C4FSK lands: 15 of 15 NinoTNC modes

The last coverage gap closed. `C4fskModem` implements NinoTNC modes 1 (19200) and 3
(9600) — which turn out to be **MMDVM-TNC "Mode 2"** (G4KLX; Tom's pointer), inherited
wholesale: 0x77 preamble, outer-only 4-byte sync 0x5D57DF7F (deframer sync now
parameterised), then standard IL2P bytes on shaped 4-PAM (dibits 01/00/10/11 →
+3/+1/−1/−3). The format was cracked against ground-truth recordings captured on the rig
(known frames sent via serial, transmitted by the TNC, one symbol error in 316 at fixed
phase) before any implementation — and MMDVM-TNC's Mode2Defines.h then confirmed every
constant. Three 4-level lessons are recorded in docs/ninotnc-loop.md (the 0.55× RX filter
kills the eye; clock only from sign crossings; gate bits on energy or a 1-heavy sync
false-locks ~12k times per recording of silence). Live: us→NinoTNC 8/8 both modes at
first attempt, NinoTNC→us 6-7/8 (headroom tracked via parity tests). The C4FSK
aspiration criteria graduated to the parity suite the same day they became meetable —
the scoreboard is empty. Daemon + bench wired; packet.net transport follows with the
0.4.0 pin bump.

Same day, other threads: #635 delivered by subagent (FrameQuality → node metrics/API/log,
PR #636); hardware validation of the acquisition work (us→NinoTNC 20 ms everywhere, new
training preamble confirmed; nino→us at ITS 20 ms flag fill remains marginal on bare-HDLC
modes — on #4); Opus-period audit clean (five stale worktrees from the July 8-12 arc
removed, one already-merged branch confirmed landed via PR 588).

### 2026-07-16 (later still) — per-frame receive quality: FrameQuality surfaced end to end

Tom asked whether we get BER from the modems. Answer: the deframers have always computed
the honest version of it and every modem discarded it — `Il2pDecodeInfo` (RS corrected
symbols + CRC state) and the FX.25 corrected-byte count died in `(frame, _) =>` lambdas at
seven call sites. Now surfaced as `FrameQuality` (mode/branch, frame length,
CorrectedBytes, CrcValid, winning multi-decoder offset + emphasis), deliberately NOT named
"BER": true bit-error rate is unobservable from a receiver (errors inside a corrected byte
are invisible; frames beyond the correction budget never report). CorrectedBytes over
frame length is a floor on channel byte-error rate — zero on a clean link, persistently
non-zero = a link consuming its error budget before it starts dropping frames.

Plumbing: `IModem.FrameDecoded` event (all seven modems), `SoundModemChannel.
FrameReceivedWithQuality` (with sub-channel), and — for the standalone daemon — an
**opt-in** KISS extension: `--quality-frames` emits command **0x07 RxQuality** after each
data frame, same port nibble, compact JSON payload. A distinct command rather than a
synthetic data frame, deliberately: the NinoTNC's own habit of sending diagnostics as fake
`TNC>USB` data frames means every host needs a special case to avoid parsing phantom
traffic, and we're not exporting that problem. Off by default so unaware hosts never see
it. HDLC framings report CorrectedBytes = null — an FCS pass proves zero residual errors,
not an error count.

Found while testing: on a clean signal the multi-decoder bank's "winning branch" is
first-past-the-post among many successful branches, so its offset/emphasis is only
directionally meaningful for marginal signals — documented in the test.

PDN-side leg (attach FrameQuality to the node's per-frame metadata via
SoundModemFrameTransport, UI surfacing) needs the next package release; tracked in
packet.net.

### 2026-07-16 (later) — performance criteria as tests: parity floors + aspiration scoreboard

Tom proposed expressing the performance criteria as failing unit tests. Implemented as two
tiers rather than a permanently-red suite (red that never goes green trains people to
ignore red):

- **`NinoTncParityTests`** — criteria already met, asserted forever: every mode acquires
  at TXDELAY 0 from a cold receiver (10/10), fsk9600 classic at 10 ms (the NinoTNC's own
  floor for that mode), and qpsk2400 short-preamble acquisition after 4 s idle with 20 dB
  SNR noise. Red here = regression below reference hardware. The reference numbers are
  from the 2026-07-16 TNC↔TNC survey and cited in the test docs.
- **`NinoTncAspirationTests`** (`Category=Aspiration`) — criteria not yet met, expected
  red: currently the two C4FSK modes (1/3) lacking modems. CI runs the category in a
  separate `continue-on-error` step, so it is a visible scoreboard, not a broken build.
  Discipline in the class doc: a passing aspiration graduates to the parity suite; a
  stale one gets deleted with its reasoning recorded.

The discipline proved itself immediately: the idle-noise qpsk2400 criterion was written
as an aspiration and passed on first run — graduated to parity the same hour, and is now
a floor. Blocking suite: 186 green. Aspiration scoreboard: 2 red (C4FSK), by design.

### 2026-07-16 — RX acquisition: NinoTNC-floor parity (goal: match or better NinoTNC)

Tom set the goal after the NinoTNC↔NinoTNC TXDELAY sweep showed the reference hardware
acquiring from ONE 16-bit word of preamble in 13 of 15 modes, while our receiver needed
100-300 ms in several. Three root causes, found by instrumenting rather than theorising
(a diagnostic tap on the real demodulator; every claim below was observed, and two
plausible fixes that did nothing were removed again):

1. **TX truncated the pulse-shaping filter's tail** (FskModem): output stopped at
   bits×samplesPerBit, chopping the final ~5 bits — the IL2P CRC trailer — off the air.
   Whether the Hamming-coded trailer survived depended on payload, so it presented as the
   receiver deterministically dropping *specific contents* (4/10 at any TXDELAY) while a
   NinoTNC decoded the same audio 10/10. Same bug class as the Afsk300 BandLimit flush.
2. **The discriminator's power-normalisation floor (1e-12) manufactured full-scale garbage
   during the filter-fill transient** (~19 bits of near-zero power at every burst start),
   and the envelope trackers trained on it — slice midpoint measured at 0.65 against a
   real eye of [0.2, 0.65]. Floor raised to 1e-5 (-50 dB below nominal in-band power):
   sub-signal input now yields sub-eye output. This also fixed real off-air decoding:
   WA8LMF Track 2 single 426 → 472, multi-bank 983 → 986 (direwolf: 970).
3. **An all-flags TXDELAY fill trains a cold receiver poorly** (87.5 % one tone; the
   opposite tone appears as 1-bit excursions that barely emerge from the receive LPF —
   observed as periodic errors on every flag boundary for the first ~40 bits). Classic
   HDLC AFSK modes now precede the two opening flags with an NRZI-zeros training run
   (level change every bit), which is what the IL2P framer already did and why those
   modes never suffered. Pre-flag zeros cannot alias to a flag; NinoTNC interop with our
   flag preamble was already proven, re-verification of the new fill is pending hardware.

Negative result recorded in code: a cold-start envelope "warm-up" (both legs at attack
rate) converts the min/max tracker into a mean-follower and loses all discrimination
during flag runs — tried, measured harmful, removed.

Offline sweep after (10×40-byte frames, 1 s gaps, cold): **all 13 modes 10/10 at
TXDELAY 0** except fsk9600 classic at 10 ms — identical to the NinoTNC's own floor
(both bounded by the x^17 scrambler needing >16 bits), and **better than it on
qpsk2400** (ours acquires at 0 where its demodulator needs ~100 ms). samples/pdn
regenerated (the committed set embodied bug 1). Hardware re-validation against a real
NinoTNC pending — the bench TNCs are currently paired for the TXDELAY survey.

### 2026-07-15 (night) — TXDELAY: 20 ms is enough (and the 500 ms claim was wrong)

Tom challenged the "QPSK needs ≥500 ms TXDELAY" note — suspecting it conflated *preamble
length* with *the modem settling after a mode change*, and flagging that the NinoTNC may
send the frame after a TXDELAY change at the old setting. Both suspicions were right, and
the rig can now prove it: GETALL register 0B (`PreamblCnt`) is a readback of the applied
preamble in 16-bit words, and the bench reports per-burst air duration.

- **TXDELAY applies one frame late.** The readback updates immediately; the air does not.
  Moved 300 → 50 ms, burst #00 measured 571 ms and #01+ 330 ms — a 241 ms excess, exactly
  the old setting. Never measure a TXDELAY change on the frame after it.
- **20 ms is enough** for afsk1200, fsk9600 and bpsk300 in both directions (6/6), and
  **our demodulator locks on ~13-20 ms preambles in every mode tested**. Only the
  NinoTNC's QPSK demodulator wants more: QPSK-2400 goes 6/6 at 100 ms and 0/6 at 50 ms.
- **The 500 ms claim is retracted.** It was the QPSK modulator bug (since fixed) plus
  unreliable first frames after a mode change, misread as a preamble requirement. The
  bench now settles 1500 ms after SETHW (`--settle-ms`) rather than papering over it with
  a long TXDELAY.

Tables in docs/ninotnc-loop.md § How short can TXDELAY be?. Bench gained `--our-txdelay-ms`
so the two directions can be swept independently — conflating them is what hid this.

### 2026-07-15 (evening) — v44 firmware, 13/15 mode coverage, and the silence bug

Tom pointed at NinoTNC firmware v44 and its mode table. Flashed the bench TNC 3.41 → 3.44
with this repo's own flasher (`packet-tune flash-tnc`, 184 s, clean), re-ran the whole
matrix green on 3.44, then went after **full mode coverage**.

Nino's v3/4.43 mode-switch mapping (in flashtnc's release-notes.txt) turns out to document
every mode's symbol rate, carrier and OBW, so most of the gap was reparameterisation, and
each new mode worked *first try* on the bench: mode 9 (600 QPSK = 300 sym/s on 1500 Hz),
mode 10 (1200 BPSK = 1200 sym/s on 1500), mode 4 (4800 GFSK). The BPSK and direct-FSK
classes were baud-generalised and renamed to the mode families they now are
(`Bpsk300Modem` → `BpskModem` + Bpsk300/Bpsk1200 factories; `Fsk9600Modem` → `FskModem` +
Fsk9600/Fsk4800; `Fsk9600Framing` → `FskFraming`), following the QpskModem precedent.
Modes 12/13/14 (300 AFSK, 1600/1800 Hz — measured off-air to confirm) needed a new
`Afsk300Modem` over a generalised `AfskDemodulator`/`AfskModulator`. **Coverage is now 13
of 15 DIP positions; the gap is C4FSK (modes 1/3).**

The 300 baud bring-up then paid for itself several times over. It stuck at 3-6 of 8 frames
while the FEC modes on the same audio did better — the tell that the *bits* were marginal,
not the signal. Recording the link and decoding it offline showed each burst was actually
perfect when a **fresh** demodulator saw it and lossy when a **long-running** one did;
logging the envelope trackers found the cause. With no signal, the discriminator's power
normalisation divides noise by ~zero power and emits full-scale garbage, and the trackers
learn it — so every burst opened with its peaks pinned and its slice point up to a third
of the eye off centre. **The clamp meant to bound that garbage was a fixed ±1: ~2x the
legitimate ±0.5 at Bell 202's ±500 Hz shift, but 10x the ±0.105 of the ±100 Hz HF modes.**
It now tracks each mode's own full deviation. Result: 300 AFSK 8/8, **and the WA8LMF
benchmark improved at every rate measured — Track 2 @12 kHz single decoder 269 → 426 and
multi-bank 972 → 983 (atest 970); @44.1 kHz multi-bank 955 → 987 (atest 983), taking us
ahead of the reference at both rates for the first time.** A constant that was merely
generous for one mode had been costing real off-air frames for the whole project — and
note what it cost us to have stopped earlier at "the residual 44.1 kHz gap is direwolf's
multi-slicer margin, not timing": that conclusion was wrong, and comfortable enough to
stop the search.

That in turn exposed a latent `PacketDcd` bug: transition scoring can only drop DCD when
it *sees* badly-timed transitions, so it relied on receiver noise to notice a signal had
stopped — on a genuinely quiet channel (squelched radio, wired loop, or our own now-silent
demodulator) **DCD latched on for ever**. It now also drops after 24 transition-free
symbols, which tightened release from a ragged 60-300 ms to a consistent 60-91 ms. Exactly
the end-of-DCD accuracy the CSMA seam depends on.

Negative results, banked in code comments so they are not re-attempted: a **silence
squelch** (zero the discriminator below an absolute power floor) is intuitive and
measurably worthless once the clamp is right — Track 2 scored 269 unclamped / 426 clamped
/ 270 squelched-only / 427 both, so it was dropped rather than kept on plausibility. An
earlier *relative* version of that gate was far worse than useless (Track 2 972 → **65**):
one loud frame parks the tracker and squelches every quieter frame after it, which is
precisely what that track exists to test. And a 7×7 filter-cutoff sweep produced an
erratic, non-monotonic surface I nearly tuned constants against — it was noise thrown off
by the real bug, not a filter optimum. Every fix here is attributed by toggling it alone
against a corpus, because three of them went in together and the tempting story ("the
squelch fixed it") turned out to be the wrong one.

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
