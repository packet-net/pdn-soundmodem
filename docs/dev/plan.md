# pdn-soundmodem - plan

Living status document. Keep current in the same PR as the work (packet.net §18 discipline).
Founding research: [packet.net `docs/research/headless-soundmodem.md`](https://github.com/packet-net/packet.net/blob/main/docs/research/headless-soundmodem.md) -
read it before substantive work; the decisions in its §Decisions bind this repo.

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

### Phase 0 - feasibility bench ⬜
Pi 4/5 DSP benchmark (the i7 numbers from the research need Pi confirmation); ALSA
capture/playback soak on a CM108-class dongle (period size, xruns, TX-release latency);
record the **WAV corpus** through the packet.net NinoTNC bench rig (every NinoTNC mode,
clean + attenuated + noisy) - the decode-regression suite everything else is judged by.
WA8LMF Track 2 for AFSK (redistribution terms TBC).

### Phase 1 - frame codecs + offline RX 🟡 in progress
- ✅ IL2P codec (spec v0.6 incl. IL2P+CRC): Type 0/1 headers, scrambler, RS(0x11D) FEC,
  block segmentation, Hamming CRC trailer. Byte-exact vs all three spec example packets;
  error-correction + fuzz roundtrip tests. (2026-07-14)
- ✅ HDLC bit layer (flags, stuffing, abort, NRZI, FCS) + streaming IL2P deframer
  (±1-bit sync tolerance). (2026-07-14)
- ✅ WAV 16-bit PCM read/write offline harness. (2026-07-14)
- ✅ 300 BPSK modulator + demodulator (IL2P symbol map; QtSM P300 filter plan) -
  clean/noisy/offset/multi-block loopbacks green. (2026-07-14; coherent default 2026-07-16
  per #5, **reverted to differential default 2026-07-18 per #40/#42** - on real off-air HF
  benchmarked against a NinoTNC, differential + the frequency-diversity bank matches/beats
  coherent because real carriers arrive off-frequency with short preambles. Coherent stays a
  detector option; QPSK keeps its coherent default.)
- ✅ 1200 AFSK modulator + demodulator (UZ7HO Mux3 chain: BPF → mix → I/Q LPF →
  cross-multiply discriminator, power-normalised, envelope slicer, direwolf-style DPLL) -
  clean/noisy/quiet/back-to-back loopbacks green. (2026-07-14)
- ✅ Cross-validation vs Dire Wolf (independent implementation): 4/4 decode parity with
  atest on gen_packets AFSK and **IL2P-over-AFSK** fixtures (committed as regression
  tests); direwolf's RESERVED-bit convention tolerated as designed. On the 100-frame
  increasing-noise battery: ours 34 vs atest 38 (single decoder vs multi-slicer - the
  Phase 4 multi-decoder bank is the path to parity+). `tools/Packet.SoundModem.Decode`
  (sm-decode) is our atest equivalent. (2026-07-14)
- ✅ Real-corpus benchmark - **ahead of the reference on Track 2** (2026-07-15, WA8LMF
  TNC Test CD Tracks 1+2, off-air 1200 AFSK APRS, kept locally in corpus/,
  redistribution TBC). At 12 kHz (the daemon's native rate), multi+emphasis bank:
  **Track 2 ours 972 vs atest 970; Track 1 ours 959 vs atest 999 (96 %)**. The path:
  flat single 60 → emphasis branches (the twist killer, 267→970) → sub-sample DPLL
  crossing interpolation (single 60→269; Track 1 937→959; Track 2 970→972). Frame-set
  diffs show the remaining Track-1 misses are marginal-SNR frames spread across many
  stations (direwolf's multi-slicer edge); next levers if wanted: slicer-level branches,
  per-tone AGC, dual-threshold + Memory-ARQ. Negative results banked in code comments:
  searching/locked inertia switching regressed badly (268→31), and crossing
  interpolation on the 9600 baseband chases ISI jitter into the eye at 5 samples/bit -
  both documented in BitDpll/Fsk9600Modem. 44.1 kHz full-bank: 955 with
  interpolation (954 before; atest 983) - at 36.75 samples/bit the quantisation jitter
  was already small, confirming the interpolation win is concentrated at the coarse
  native 12 kHz rate.
- ✅ **Ahead of the reference at BOTH rates** (2026-07-15, after the per-mode
  discriminator clamp - see the §17 entry): **Track 2 @12 kHz 983 vs atest 970; @44.1 kHz
  987 vs atest 983**. This supersedes the conclusion recorded above that the residual
  44.1 kHz gap (955 vs 983) was "direwolf's multi-slicer margin, not timing" - it was
  neither. It was our own fixed ±1 discriminator clamp letting silence pin the slicer's
  envelope trackers; a mode-aware clamp took 44.1 kHz 955 → 987 and 12 kHz single-decoder
  269 → 426. A conclusion that stopped at "the remaining gap is the other implementation's
  margin" was the thing that kept it hidden.
- ✅ **The corpus benchmark is a tool in the tree, and Track 1 is now ahead of the
  reference too** (2026-09-07). `tools/Packet.SoundModem.TncTest` (sm-tnctest,
  [docs/dev/bench/tnc-test-cd.md](bench/tnc-test-cd.md)) plays a track through any catalogue mode and scores
  it; it reads the corpus FLAC directly, so there is no conversion step to get wrong, and it
  shares pdn-decode's resampler so a benchmark score and a forensic decode of the same file
  cannot disagree about the audio. Re-measured at 12 kHz, multi+emphasis bank vs single
  decoder: **Track 1 (flat) 1007 / 968** and **Track 2 (de-emphasised) 1011 / 534**, against
  the 999 and 970 recorded for atest above - so the receive-path work since July has taken
  Track 1 from 959 to 1007 and Track 2 from 972 to 1011, past the reference on both. The
  100-flat-Mic-E-burst track, the only one whose true count is known, scores **100 of 100**.
  Three things the tool surfaced that were not written down anywhere. **The tags on this rip
  are not reliable**: the file tagged "100 Mic-E Bursts DE-Emphasized" is 25:49 long, decodes
  1011 frames and yields the same 845 distinct contents from the same 119 callsigns as
  Track 1 - it is the de-emphasised copy of the traffic recording, which is also what makes
  it the file the Track 2 figures above were measured on. **This copy of Track 1 is 25:49
  despite a title tag saying 40 minutes** (the file's own MD5 verifies the decode, so the
  audio is what it is; scores off it are not comparable with figures from a 40 minute copy).
  And **the measured station offset moves with the emphasis, not with the transmitters** -
  centred near -30 Hz on the flat file and on 0 with a tail past +150 on the de-emphasised
  one, the same stations both times, which is the discriminator's DC level following the
  twist. The atest comparison rests on the numbers recorded above rather than a re-run; Dire
  Wolf is not built on the machine this was measured on.
- ⬜ Phase 0 hardware corpus validation for the IL2P modes (needs rig time).
- Exit: corpus decode rates ≥ QtSoundModem and ≥ NinoTNC on identical recordings
  (needs Phase 0 recordings - loopback tests alone do not demonstrate this).

### Phase 2 - live RX + DCD + waterfall 🟡 in progress
- ✅ Native DCD (2026-07-14): `PacketDcd` (direwolf DPLL transition-quality scoring,
  30/32-6/32 hysteresis) + `EnergyBusyDetector` (display-decoupled block power vs
  min-tracking noise floor, 6/3 dB hysteresis, hold, warm-up-aware seeding). Exposed on
  both demodulators as `CarrierDetect` / `ChannelBusy` + `ResetCarrierState()` - the
  surface the PDN `ICarrierSense` adapter consumes. Behavioural tests incl. the
  steady-carrier-is-busy-but-not-DCD case headless QtSM cannot see.
- ✅ Spectrum feed groundwork (2026-07-14): native radix-2 `Fft` + `SpectrumSource`
  (Hann, 4096-pt, dB-scaled u8 bins ≈2 kB/line ~3/s per channel).
- ✅ Constellation side channel (2026-07-16, issue #9): `ConstellationSource` - the PSK
  demodulators' per-symbol decision point (the differential product they already compute)
  tapped via `IConstellationSource`, batched into auto-ranged scope frames (256 pts, 2
  signed bytes/pt ≈5/s at qpsk2400). Wired per-modem on `SoundModemChannel`, for the PSK
  modes only. Diagnostic-only (no wire/interop impact); the debugging surface #5 builds on.
- ✅ ALSA layer (2026-07-14): `AlsaPcm` (libasound P/Invoke, capture+playback, xrun
  recovery, `Drain` for sample-domain PTT release) + `Decimator` (real anti-aliased
  48 k→12 k ÷4; aliasing-suppression test). Hardware smoke tests are SkippableFact -
  NOTE: they skip on this dev box because user `tf` lacks the `audio` group
  (`sudo usermod -aG audio tf` to enable); they will run on the bench/Pi.
- ✅ SoundModemChannel (2026-07-15): multiplex composition - N modems per audio side
  behind IModem (AfskModem family, BpskModem), aggregated CarrierDetect/ChannelBusy,
  spectrum tap, TX queue with classic p-persistent CSMA, PTT bracketing, per-frame
  TX-complete tasks, half-duplex RX suppression + carrier reset after TX.
- ✅ Standalone KISS-TCP daemon (2026-07-15): `pdn-soundmodem` binary - in-repo KISS
  framing (no AGPL dependency), multi-client TCP server, sub-channel nibble ↔ modem mux,
  ACKMODE with true TX-complete echo (post-drain, not a timer), KISS parameter commands
  actually honoured (TXDELAY/P/SLOTTIME/TXTAIL - QtSM ignores these), serial RTS/DTR PTT,
  ALSA capture→decimate→RX loop, `--wav` offline mode (smoke-tested: 4/4 on the direwolf
  fixture). End-to-end tests: KISS-in → audio → independent demod, RX → broadcast to all
  clients, ACKMODE echo ordering, param plumbing. Not yet: config file, CM108 PTT,
  spectrum-over-TCP, stereo second channel, live-audio soak (hardware).
- ✅ Daemon-side browser waterfall (2026-08-01, PR #157): `WaterfallWebServer` - an
  HttpListener + WebSocket server in the library (the KISS-server pattern) serving a single
  embedded page: 30 fps spectrum + waterfall (`WaterfallSource`, overlapping Hann FFTs at
  hop = rate/30, 2048-pt @ 12 kHz / 8192-pt @ 48 kHz ≈ 5.9 Hz/bin), per-modem band overlays
  measured off each modem's own modulator via the SM.443 OBW meter at start-up, operator-set
  dial frequency + sideband for an absolute-RF scale, and per-frame burst attribution
  (callsign parsed display-grade from the AX.25 address field, SNR + burst extent from
  `BandActivityTracker` min-tracking over the display's own lines, carrier offset measured by
  the winning bank branch). Daemon `--waterfall PORT` / `--dial HZ` / `"waterfall"` config;
  `--wav-loop FILE` replays a recording as the live capture device for hardware-free demos.
  The decoded-frames panel lists this station's own transmissions too, marked **TX**, and
  opens on the last 50 rows of the [`frameLog`](../../CONFIG.md#framelog) where the station keeps
  one (2026-08-04).
- ✅ AX.25 links pane (2026-09-02): the waterfall page reads every AX.25 frame it lists into
  packet.net's `Ax25LinkObserver` and shows the result as one card per pair of stations per
  modem, each with its own feed narrated in plain words ("resends #3", "polls, no answer
  yet", "accepts the call; link up") rather than the classic monitor line. Slides up over the
  waterfall (L / Esc), resizable, detaches into its own window at `/links` which asks the
  server for no spectrum. Warmed from the frame log on start. The observer shipped in
  packet.net `lib-v0.31.0`; the `Packet.Ax25` and `Packet.Core` pins moved from 0.23.0 to
  0.31.0 with it, and `-p:Ax25SourcePath=...` remains for iterating against a checkout.
- ⬜ packet.net side: `kind: soundmodem` transport + `transport is ICarrierSense` probe at
  PortSupervisor (seam mapped in the research doc §5), spectrum + constellation SSE
  endpoints + waterfall/constellation UI (PdnPortTuningApi is the template; add to the SSE
  token allowlist; node-api.yaml). The `constellationSink` on `SoundModemChannel` is the
  node-side seam, mirroring `spectrumSink`.
- ✅ Live RX soak (2026-07-15): 60 s daemon run on this box's real HDA codec via the
  fresh audio group - 48 kHz capture → decimator → 21-branch multi bank, KISS TCP up,
  clean exit. Found+fixed on first contact: consumer cards refuse direct 12 kHz
  playback opens ("snd_pcm_set_params: Invalid argument") - TX now plays at the
  card-native rate through a new image-rejecting Upsampler/UpsamplingAudioOutput
  (the mirror of the capture decimator), covered by a full simulated-card-path
  roundtrip test. Longer soaks + a decode of real off-air audio still worthwhile
  when an RF source is nearby.

### Phase 3 - TX 🟡 software done for all Phase-1..3 modes
- ✅ TX for AFSK 1200 / BPSK 300 / QPSK 2400 / QPSK 3600 / 9600 (classic + IL2P), with
  modem-side p-persistent CSMA, serial RTS/DTR PTT, sample-domain TX-complete (drain) and
  TX tail - all in SoundModemChannel + the daemon (2026-07-15).
- ✅ QPSK 2400/3600 modem pair (spec QPSK symbol map, coherent Costas detection default +
  differential opt-in, fractional one-symbol delay for 1800 Bd at 12 kHz); loopbacks incl.
  noise/offset/multi-block.
- ✅ 9600 baseband modem, both framings, cross-validated BOTH WAYS vs Dire Wolf:
  classic G3RUH (NRZI→scramble TX order confirmed empirically; 4/4 their audio, 3/3 ours
  in atest) and IL2P (4/4 their audio via the new polarity-agnostic sync hunt; 3/3 ours
  in atest after the legacy-max-FEC discovery below).
- 🔎 **Interop discovery (desk-found, exactly the class the research predicted):** the
  v0.6-RESERVED header bit is still read by Dire Wolf (and the NinoTNC lineage) as the
  pre-v0.6 max-FEC selector - cleared, they parse payload blocks with the legacy
  2/4/6/8-parity plan and reject 16-parity frames (the spec's own example packets would
  not decode!). `Il2pCodec.Encode` now defaults `legacyMaxFecBit: true` for interop
  (spec-exact output remains available; our RX ignores the bit). ✅ Bench confirmed
  against NinoTNC firmware 3.41 (2026-07-15): all four IL2P pairs decode our frames
  with `IL2PRxUnCr` = 0.
- ✅ CM108 hidraw PTT (`--ptt cm108:/dev/hidraw0[:gpio]`, direwolf/QtSM-compatible
  5-byte report; 2026-07-15).
- ✅ **Wired NinoTNC interop - 13 of 15 DIP modes, both directions** (2026-07-15,
  firmware **3.44**, CM108 loop; full tables in docs/dev/bench/ninotnc-loop.md § Results +
  § Coverage). Every NinoTNC mode except the two C4FSK ones now has a counterpart here
  and passes bidirectionally: fsk9600 (0), fsk9600-il2p (2), fsk4800-il2p (4), qpsk3600
  (5), afsk1200 (6), afsk1200-il2p (7), bpsk300 (8), qpsk600 (9), bpsk1200 (10), qpsk2400
  (11), afsk300 (12), afsk300-il2p (13), afsk300-il2pc (14). DCD assert/release lags
  measured and CSMA-safe throughout.
- ⬜ **C4FSK (modes 1/3) is the remaining coverage gap** - coherent 4-level FSK (19200 in
  20 kHz OBW, 9600 in 10 kHz; 2079/1039 Hz outer deviation), new in firmware 3/4.42. A
  genuinely new modem, not a reparameterisation of an existing one.
- ⬜ PDN `IRigControl` PTT (packet.net side); over-air (RF) NinoTNC runs when a radio
  pair is available - the wired loop already answers the baseband/phase-map/FEC-bit
  questions.

### Phase 4 - breadth 🟡
- ✅ Multi-decoder offset bank (2026-07-15): `Afsk1200MultiModem` - 2·pairs+1 branches at
  30 Hz steps with content dedupe (daemon mode `afsk1200-multi`). On direwolf's 100-frame
  noise battery: **38 = exact atest parity** (single decoder: 34). Off-tune-transmitter
  and dedupe tests.
- ✅ BPSK frequency-diversity bank (2026-07-18, #40/#42): `BpskMultiModem` - the same
  stepped-centre model for the coherent PSK modes (daemon `bpsk300-multi`/`bpsk1200-multi`).
  Coherent's narrow tracking loop can't pull a tens-of-Hz offset carrier onto frequency
  within a ~150 ms preamble without forfeiting its noise margin / QtSM interop, so a bank of
  ordinary branches (step ≈ baud/40) covers the offset range instead - a single centred
  coherent modem misses ±12-24 Hz, the bank decodes it. Corrected the #42 diagnosis: the
  coherent path already differential-decodes (it was never the missing step); the real gap is
  short-preamble acquisition of an offset carrier. The committed GB7RDG off-air frame (~8 Hz
  offset, 16 dB, but a preamble too short for the narrow loop even on-frequency) decodes via
  `PskDetector.Differential` - guarded by `OffAirBpskTests`. Bank step/span are tuneable
  (`offsetPairs`/`offsetStepHz` in the daemon modem config). `BpskCarrierOffsetEstimator`
  (symbol-spaced squaring, measured the fixture at +8 Hz / 0.98 confidence) characterises
  per-station offset to size the default step. `tools/Packet.SoundModem.NinoCompare` is the
  benchmark harness: capture a NinoTNC's decodes off MQTT, decode the same audio with the bank,
  diff (matched / we-missed / we-extra) to drive tuning + regression tests to NinoTNC parity.
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
- ✅ UberSDR as a receive-only device (2026-08-02): `--device ubersdr:<instance>` streams a
  public web receiver's iq48 and demodulates SSB from it in-process, so an ordinary band-plan
  config runs unchanged on somebody else's antenna. Receive only, and the channel says so
  once (`ReceiveOnlyReason`) rather than each host interface finding out separately. See the
  amendment log entry below.
- ✅ Public monitor over an UberSDR receiver (2026-09-03): `"ubersdr": { "onDemand": true }`
  holds a session on the receiver only while somebody has the waterfall open (held for a
  linger after the last leaves), and `"waterfall": { "public": true }` dresses the page for a
  visitor: a title, an about paragraph, a credit and link for the receiver, no KISS host
  badges. Built for https://m9psy-1-monitor.ukpacketradio.network; see
  [docs/dev/archive/40m-monitor-plan.md](archive/40m-monitor-plan.md).
- ✅ Public monitor over many UberSDR receivers (2026-09-03): a `"monitor"` config section fronts the receivers the UberSDR directory lists, with a picker at `/`, each receiver's page at `/r/<slug>/`, and at most one session per receiver however many visitors are watching it. Same binary and same package as the single-station flavour, which is unchanged. Live at https://monitor.ukpacketradio.network from CT 146, which replaces the single-receiver site; an overnight soak and a word with the receivers' operators are still to come. See [docs/dev/archive/monitor-plan.md](archive/monitor-plan.md) and the amendment log entry below.
- ⬜ DCD-over-KISS extension (awaiting an agreed NinoTNC-ecosystem format); Windows
  audio backend (deferred 2026-07-15); extra decode-only listeners; multi-decoder banks
  for the PSK modes.

## Blocked on Tom / hardware (updated 2026-07-15 later)

- ~~NuGet~~ **RESOLVED**: NUGET_API_KEY granted; 0.1.0 and 0.1.1 published (0.1.0
  confirmed indexed on nuget.org).
- ~~audio group~~ **RESOLVED**: `usermod -aG audio tf` run; both ALSA hardware smoke
  tests now pass on this box's real sound card (via `sg audio` until re-login).
- ~~soundcard on the NinoTNC bench rig~~ **RESOLVED** (2026-07-15): CM108 widget wired
  to the NinoTNC per docs/dev/bench/ninotnc-loop.md; every supported mode validated bidirectionally
  (see § Results there). The open wire questions are answered: NinoTNC's 9600 GFSK
  matches the direwolf-validated baseband both ways, the spec QPSK phase map is
  NinoTNC-compatible (no pairwise-negotiation divergence), and the legacy-max-FEC bit
  default is confirmed right.
- **Hardware still pending**: a Pi for the DSP benchmark and .deb trial; over-air (RF)
  NinoTNC runs; per-mode WAV corpus recording off the rig (bench decode counts exist,
  committed corpora don't yet).
