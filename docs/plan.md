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
  opens on the last 50 rows of the [`frameLog`](../CONFIG.md#framelog) where the station keeps
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
  firmware **3.44**, CM108 loop; full tables in docs/ninotnc-loop.md § Results +
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

### 2026-09-03 (later still) - a transcript button on every card, and acknowledgements count as a link

Tom, with `v0.52.0` on the station: "Add a transcript export button which reverses the order and outputs a markdown document with four columns in a table. Columns: absolute time, delta time, "classic" decode, modern interpretation", and "yes fix that" to the oddity reported alongside the deployment, a pair heard only acknowledging and asking for resends showing as "unconnected".

**The transcript.** Each card's head carries a `transcript` button, quiet until the pointer is on the card, which saves the card's feed as a markdown file (`M0LTE-9_GB7RDG-2_modem0_2026-09-03_1103.md`): a heading of the pair, a sentence saying which modem, which of the two callsigns this station transmits as, how many lines over what span and the link's state, then one table row per line, oldest first, the reverse of the card. Four columns: the time in UTC to the millisecond, the gap since the line before in the units a person would use for it (`+1.5 s`, `+3m 00s`, `+1h 02m`), the classic decode, and the words. The classic column is the frame as a TNC monitor has printed it for forty years, `GB7RDG-2>EI0RSI-1 <I C P S0 R1 pid=F0 len=49>`, with the text of a data frame beside it in a code span of its own, fenced one backtick longer than any run in the text, and bars and line breaks in the text escaped or flattened so a cell stays a cell. The words column is the sender, the card's narration and the card's tags in brackets (`GB7RDG-2 resends #2 [RESEND]`); the tag words now come from one table that the card rows and the transcript share, so the two cannot drift. The line the observer adds when it gives up on a call has no frame behind it and an empty classic cell. The server now sends the frame's pid on every link event, which is the one field the classic line needed that the page did not have. A card remembers 2,000 events for its transcript while showing 100: the rows are a window and the transcript is the record, and a page left open through a session should be able to save the whole of it. Rendered in headless Chrome: the button, the download (filename and blob) and a 103-line transcript off the recorded session.

**Acknowledgements count.** The "unconnected" pair was a link the station had joined late on which only RR and REJ had been heard; the observer inferred a link from data frames alone. packet.net PR #796 (`lib-v0.33.0`) moves the rule to one place that runs before any numbered frame is read: on a link never heard set up, the frame means it is up (joined late); on a link being taken down, nothing changes, the other side has not heard the DISC yet; on a link heard to come down, or still being called, one end disagrees, the link is taken to be up, the open call is cleared and the frame is tagged NO LINK. That last rule also fixes a data frame crossing a hang-up reopening the link for good: the UA that closed the hang-up used to land on a connected link and be narrated as "acknowledges, though nothing was asked".

**The pin moves to 0.33.0.** No API change; the observer's behaviour is what moved.

### 2026-09-03 (later) - a call nobody answers is given up on, and the card feeds read newest first with a light on arrival

Tom, once `v0.51.0` was on GB7RDG-2 and the pane showed on the main page: "EI0RSI-1<>GB7RDG-2 currently in status "calling", but nothing has been seen since 10:04:17, and it is 10:17:24 now", and "can we make these logs appear in reverse order, newest at the top, oldest scrolling off the bottom, some kind of visual highlighting for a moment to attract attention when a new line appears? (perhaps a background behind the text that pulses once, or similar?)".

**The stuck call was the observer having no idea of time.** packet.net's link observer learns the time only from the frames it is handed, so that a log replays through it at the log's own pace; a call nothing answers is followed by no frame, and stayed a call until the pane forgot the pair an hour later. The reasoning belongs where the rest of it is, so the fix is in packet.net (PR #795, `lib-v0.32.0`): `Expire(now)` gives up on a call or a hang-up that nothing has answered for three minutes (a station's N2 tries at T1 are over inside two even with linear backoff, and every retry heard restarts the clock), takes the link to disconnected, clears its concern, and puts a line in the feed that is not a frame: "got no answer in 3 minutes; the call has failed", or for a hang-up "... link down". The line is timed at the moment the wait ran out, not at whenever the clock next looked. This daemon calls it every ten seconds from the server's own clock, under the same lock as frames, and broadcasts each such line as a `link` message like any frame, so the card drops out of the live group and its feed says why; on the wire the event's `kind` is null, having no frame behind it, and the page tags the line NO ANSWER in the red it uses for a refusal. Connected links are left alone: a link that is up and quiet cannot be told from one whose hang-up went by unheard, and a single unanswered poll on a healthy link would be mistaken for a failure; a link that polls repeatedly with no answer and then falls silent is the same shape and is not aged yet.

**Feeds newest first, lit on arrival.** Each card's feed now puts the latest line at the top and drops the oldest off the bottom, on the argument that a card is a window on the last few exchanges rather than a transcript, and a line that arrives live gets a blue background that fades over a second and a half, taken off on a two-second clock rather than at the animation's end because a line in a card a filter is hiding never animates and would otherwise light up whenever the filter was next turned off. Lines placed while a card is built from its backlog are not lit, or a reconnect would light the whole pane. An operator who has scrolled down to read older lines is kept where they were. The main frames panel already read newest first and is unchanged. Rendered in headless Chrome to check the light comes on and goes off and the failed call sorts below the links that are up; the probe asserts the order and the tag.

**The pin moves to 0.32.0.** `Packet.Ax25` and `Packet.Core`, for `Expire`, `Ax25LinkFlags.Timeout` and the nullable `Ax25LinkEvent.FrameType`, the only consumer change being `kind = evt.FrameType?.Mnemonic()`.

**The first cut of `v0.52.0` did not ship, and the tag moved.** The release run off the merge commit (`b3b0dc4`, run 33741693832) failed its test step on the page probe, which expected the scripted call to sort above the second link and found it below: a live card is ordered by when it was made, and the probe had stamped the second link and the call with `new Date()` a few DOM clicks apart, which on the release runner fell in the same millisecond, where "newer" is a tie and the page keeps the card it already has on top. Locally the two were always a millisecond apart. Not a page bug, a probe clocking itself off the wall: the second link is now stamped half a minute before the call, so the order it asserts is the order it scripted. As with the two cuts before it nothing had been published, so the unreleased tag was deleted and re-cut on the fixed main.

### 2026-09-03 - the links pane after its first day on a real station: links only by default, bigger cards, current first, "Mine", and a stale-tab safeguard

Tom's first feedback, after `v0.50.0` went onto GB7RDG-2: "can't see the links data on the main page, but /links works. Hide the UI frame cards by default, free up some space and make each individual card bigger. Have some kind of obsolescence - maybe new / current connections at the top, pairs that haven't been heard for a while get pushed down the screen and eventually removed. Have a way to only show connections involving 'me' - default off. (We know 'me' by observing the source callsigns on frames recently transmitted, bearing in mind sometimes due to the design of AX.25 that will be set to the callsign of another station connecting through us - obsolescence should mop that up.) The vertical scrollbars don't match the rest of the UI - they are jarring white/light grey."

**The main-page symptom did not reproduce, and the fix is a safeguard rather than a repair.** The served page with the demo station's recorded messages fed to it renders six cards on the main page in headless Chrome, at three window sizes. What fits the report is a tab that was open before the upgrade: the page had no cache headers until 4912edf, and a long-lived tab keeps its socket reconnecting through a daemon restart and never fetches the page again, so it runs the pre-pane script against a server sending it `links` messages it has no handler for, while a fresh window at `/links` gets the new page. That is the same shape as the transmit readout that appeared on a phone and never on the desktop tab. The page now carries a version, a hash of its own text that the server writes in as it serves it and repeats in every config message; a tab that hears a different version reloads itself, once per version and never twice, so a page the server did not stamp (the test probe runs the script straight from the source file) cannot loop. A server test checks the served page and the config agree. If Tom still sees an empty pane on the main page after a hard reload, that theory is wrong and the next suspect is the per-client send queue, which the main page shares between the 30 spectrum lines a second and the text messages and `/links` does not.

**Links only, by default.** Unconnected traffic (beacons, IDs, NODES broadcasts, everything sent as UI) is hidden behind a "UI frames" pill in the pane's bar, which carries the number of cards it is hiding; a hidden card keeps its feed and its place, so turning the filter on shows what was missed rather than what came after. The summary line says "3 of 6 pairs shown" when a filter is hiding something. Both filters persist with the rest of the page's state.

**"Me" is whatever this station has transmitted as, lately.** Every frame the station sends is marked `tx` on the wire already; the page keeps a map of source callsign to when it last went out, and a link is "mine" if either end is in it. Frames the station merely digipeated are excluded (their source is the originator, and our own call is in the path with the repeated mark), which is the case Tom flagged in a different form: a user connecting onward through a node goes out under the user's callsign, and that call is ours only for as long as the station keeps sending under it. Entries age out on the same clock as cards, an hour, so a call that was passing through is dropped when its traffic stops. "Mine" hides everything else, is off by default, tells the operator in its tooltip which callsigns it currently counts as this station's, and the pane says why it is empty when the station has not transmitted at all in the hour. Our own end of a link is drawn in the transmit cyan on the card, the same cue as the feed lines.

**Current at the top, quiet pushed down, gone after an hour.** Cards are now moved in the document rather than sorted by a CSS `order`: links that are up or being set up first, newest connection at the top (ordered by when the link was made rather than by its last frame, so two busy links do not swap places with every exchange), then the rest by when they were last heard. Dim after ten quiet minutes as before; forgotten after an hour rather than six, on arrival as well as over time, because the server's memory is a log's and the pane's is a screen's. The probe's DOM shim grew a real `insertBefore` for this, and the page test drives four links through both filters and asserts the order.

**Bigger cards, less chrome, dark scrollbars.** Columns are at least 560px (or the full width on a phone), rows at least 300px, the pane opens at 55% of the display, and the type is a point larger throughout; the bar wraps on a phone and drops its keyboard hint there. Scrollbars, which the browser was drawing in its light default on every scrolling panel, are now dark through `color-scheme: dark` plus the standard `scrollbar-color`/`scrollbar-width` and the `-webkit-` rules for the engines that still only read those. Headless Chrome under the test runner hides scrollbars altogether, so this one is verified by computed style and not by eye.

**The first cut of `v0.51.0` did not ship either, and the tag moved again.** The release run off the merge commit (`fd1b8eb`, run 33735019292) failed at its test step on one page test, the one that clicks Listen and expects to hear the tone the test feeds the channel: the page reported no audio blocks played, while CI on the very same commit had passed. The same mechanism as the `v0.50.0` first cut, one layer over. The tone feeder was a thread-pool work item that sleeps between blocks, and under the rest of the suite the runner's pool had not started it by the time the page had clicked Listen and, hearing nothing for two and a half seconds, taken silence as the answer. Two changes in #378: the feeder gets a thread of its own (`LongRunning`, as the capture writer did in #374), and the probe is told when audio is expected (`AUDIO=1`) and waits its full budget for it in that one run only, so a late first block cannot be mistaken for none while the other ten page runs still stop early on silence. Nothing had been published from the failed run, so as with `v0.50.0` the unreleased tag was deleted and re-cut on the fixed main rather than spending a version on a release that never existed.

### 2026-09-02 (later still) - the waterfall learns who is talking to whom: an AX.25 links pane, partner-aware and in plain words

Tom: "I'd like to surface some AX.25 state into the pdn-soundmodem browser UI, so I can monitor what's going on across my modems. I'd like it to be partner-aware, rather than a long list ... if a station is requesting a retry of some frame, this UI should make that obvious." And, with emphasis: use the packet.net packages, don't re-implement AX.25 again. **The reasoning went into packet.net, not here.** `Packet.Ax25.Monitor.Ax25LinkObserver` (packet.net branch `feat/ax25-link-observer`, alongside a new `Ax25FrameType` the frame parser now exposes) is a third-party monitor: it is shown every frame a port hears or sends and keeps one link per port per unordered pair of callsigns, with a state each (unconnected, calling, connected, disconnecting, disconnected) and per-direction counts. It reads the things a monitor line makes the operator work out: an I frame whose N(S) this side has already sent is a **resend**; an RR/RNR command with P set is a **poll**, and one that goes unanswered is the link's stated concern; REJ and SREJ are **rejects** with the frame asked for; RNR is **busy**; a UI frame heard twice with one more H bit set is the same frame **digipeated** by the station whose bit it is; an N(S) that jumps is a **gap** we missed rather than the sender's fault; a frame on a link nobody saw come up is narrated on an **inferred** link ("joined late") rather than dropped; and DM to a SABM is **refused**. UI traffic is grouped too, so beacons and APRS-style broadcasts get a card each, narrated as "beacon" when the destination is one of the usual ID/BEACON/CQ/QST/ALL/MAIL/NODES. The choices Tom made when asked: the observer lives in `Packet.Ax25` rather than in this repo; a callsign pair heard on two modems is two links, because the question is "what is going on on this modem"; the pane is cards each with its own feed rather than one interleaved list; and decoded I/UI text is shown inline when it is printable.

**The wire is two messages and one request.** Every AX.25 frame the page already lists as a flat `frame` row is followed by a `link` message carrying the card as it now stands and the one event that changed it, narration and flags included; a browser that connects gets a `links` snapshot after the history, every card with its own recent feed, so a reload on a busy channel opens full. A client may send `{"type":"spectrum","on":false}` and stops getting the 30 lines a second of spectrum and waterfall, which is what the detached window does on every (re)connect since it has nothing to paint them on. `/links` serves the same page; the page reads its own path and opens as the pane alone. The daemon walks the observer through the last 2,000 frames of the log on start (`FrameLog.RecentWithPayload`), with their own timestamps and which side sent them, so a connection that was up when the process last stopped is up on the first page load.

**The pane is the part Tom will judge, and it has not been seen in a browser.** It slides up over the waterfall (button, or L; Esc closes), remembers whether it was open and how tall it was, resizes from a grip, and detaches to a popup window with the inline pane closing behind it. Cards order themselves live-first through a CSS `order` rather than by moving nodes, go dim after ten quiet minutes and are forgotten after six hours; each shows the pair, the modem, the state as a coloured pill, the per-direction figures with the worrying ones in amber, the concern as a banner, and a feed where a resend is a filled amber RESEND tag on the line, a poll a POLL outline, a gap GAP, a repeat AGAIN xN, and rejects, busy, refusals and protocol faults in red. What could not be done from this sandbox is look at it: Chrome cannot open a socket here (EACCES on the UDP socket it needs at start-up), so the layout is verified only through the node:vm page probe, which now drives the pane with a scripted connection and a resend and reads the cards back; the shim gained real `append` and `appendChild` to make that possible. Tom should open it on GB7RDG-2 before trusting the proportions.

**Released the same day, in order.** The observer merged to packet.net main (PR #793) and shipped as `lib-v0.31.0` once `ci` and `interop` were green on the merge commit; after nuget.org indexed it, the `Packet.Ax25` and `Packet.Core` pins here moved from 0.23.0 to 0.31.0 and the build was checked against the published package rather than the checkout before merging. `-p:Ax25SourcePath=/path/to/packet.net/src/Packet.Ax25/Packet.Ax25.csproj` stays as the FlexSourcePath-style override for iterating against a checkout. Licence: 0.23.0 was MIT and Packet.Ax25 from 0.26.0 is AGPL-3.0-or-later, which CLAUDE.md allows alongside this library's GPL (M0LTE.Flex is the precedent); Tom directed the pin move. Tests: five server-side (a heard frame is narrated on its link, after its flat row; a browser opens on the links known; nothing heard sends no empty list; `/links` serves the page; the spectrum opt-out stops the binary lines and nothing else), one frame-log replay round trip, and the page probe's driven pane. 2144 passed with the local checkout.

**The first cut of `v0.50.0` did not ship, and the tag moved.** The release run off the merge commit (`991b311`) failed at its test step on one survey test, the Missed-capture read-back, at 5.1 s: the same failure, the same duration, as run 32752554759 on main on 2026-08-24. Both are the capture writer's five-second Dispose budget running out. The writer loop was a thread-pool work item, and with the OTA mask tests loading the runner's pool the loop had not started when Dispose gave up, so the capture the test reads back was never written; a scratch program that floods the pool shows a `Task.Run` loop not started after 5 s and a `LongRunning` one started in 2 ms. Fixed in #374 by giving the writer its own thread, which is also where a disk writer that lives for the life of the station belongs. Nothing had been published from the failed run (no GitHub release, no package, no `.deb`), so with Tom's say-so the unreleased `v0.50.0` tag was deleted and re-cut on the fixed main (`a691d61`) rather than spending a version number on a release that never existed; the notes list all three PRs. #375 (the `Packet.Ax25` licence comment saying what the pin is now) rode along.

### 2026-09-02 (later) - the transmit side becomes a scheduler: a queue per transmitter, and a hold through the reply window that carrier sense cannot provide

Tom, on the keyup fix below: "we should queue the second modem's TX behind the window in which the response to the first modem's TX would be received." He is right, and the reason is stronger than the fix it replaces. **Carrier sense cannot solve this by construction.** At the instant the transmitter rolls p-persistence, the reply it is about to key over has not started, so the busy detector has nothing to detect and whether we transmit on top of somebody's answer is decided by the roll. Prediction is the only available tool, and the earlier fix only improved the odds. **The window is a calculation, not a fitted constant, and Tom's correction of the wording is the design.** What is being waited out is the far end KEYING UP: its own TXDELAY (it runs the same convention, so ours estimates it), as long again for its rig and its decode, plus a slot of contention - `2 x TXDELAY + SlotTime`, 700 ms on the shipped defaults. Two things worth noticing about that. It is **nearly independent of baud rate**, because none of its terms is a symbol count, so a 9600 Bd link waits about as long as a 300 Bd one and an earlier claim here that it would scale down was simply wrong. And it deliberately leans on TXDELAY rather than slot time, because **a host may set slot time to zero and one does**: sniffing the live KISS session shows LinBPQ configuring this station with TXDELAY 300 ms, persistence 50 and **slot time 0**, which turns the p-persistence backoff into a spin and leaves this hold as the only thing keeping the station off the air after its own transmission. **The numbers behind the shape**: across 8,371 exchanges in the frame log, the peer's carrier appears 0.25 to 0.75 s after our PTT drops (2,090 and 1,876 of them in those two quarter-second buckets), the population is over by about 1.25 s, and everything past 3 s is a reply to a later retry rather than a turnaround - the 3 to 4 s bump is exactly one retry cycle. 700 ms covers 80 % of real turnarounds and another 600 ms buys 11 more points. **Per-transmitter queues, and the generalisation is the point** (Tom: "you're not making an assumption of two modems, right? This should be generalised across the domain of any receiver that goes quiet when any transmitter keys up"). It is not two and they are not necessarily modems: every KISS modem, the paging endpoint, ARDOP and the CW ident is a transmitter with a queue of its own, served round robin. A single FIFO would have made the hold actively harmful, because a deferred frame at the head blocks everything behind it including the transmitter that is not deferred - head-of-line blocking is the argument for per-source queues independent of any window logic. **The protocol knowledge is a hook, not a hard-wiring**, because "will a reply come" is a property of the protocol and not of the modem carrying it: `QuietAfterTransmit` has the same shape and the same rationale as `TransmitTrimHz`, a null hook or a null answer never holds, and anything this channel carries that is not AX.25 keeps today's behaviour exactly. The AX.25 answer is `Ax25ReplyExpectation`, and per Tom it **reimplements nothing** - `Packet.Ax25.Ax25Frame` (MIT, so it sits under this repo's GPL without complication, and the dependency runs the safe way round) does the parsing, including the digipeater path and the modulo-128 two-octet control field that a hand-rolled reader gets wrong. What is left is one policy decision: I-frames and commands that poll draw an answer, link setup and teardown draw a UA or DM, and a **response** draws nothing - that last case matters, because the F bit on a response reads exactly like a poll and would otherwise hold the channel open after every acknowledgement on the link. **When the reply does arrive, the transmitter that was answered keeps priority** (Tom's call), which is self-limiting: the hold renews only while the exchange is alive and lapses when it goes quiet. A `MaxTurnaroundHold` ceiling of 15 s was added on top and is not in the original design; the case it covers is a busy frequency renewing the hold indefinitely and muting every other transmitter for good. **One premise of that decision did not survive checking.** The reasoning was that a held frame is safe because the node knows it has not gone, ACKMODE not having acked it - but a raw-socket sniff of the live KISS sessions shows LinBPQ sending plain `Data` (KISS command 0) on both ports and never `AckModeData` (command 12), in either direction. ACKMODE is not in use on this station, so that safety net is currently the ceiling rather than the host.

### 2026-09-02 - a keyup carries one transmitter: two modems' frames were going out under one PTT, and the station was deaf through the second

Tom, from a look at the waterfall on the 40 m station: when frames interleave on the two virtual modems, "half of the frame gets transmitted on one modem slot and half the other." **The picture is faithful and no frame is being torn.** GB7RDG-2 runs `afsk300-il2pc` at 850 Hz and `bpsk300` at 2150 Hz on one Flex slice (dial 7.049450 MHz USB), and `RunTransmitterAsync` drained whatever happened to be queued into a single keyup: `while (reader.TryRead(...))` took the next item regardless of which modem it belonged to. Reproduced deterministically from the live modem set - one `key`, 3.441 s at 850 Hz, 1.493 s at 2150 Hz, one `unkey`, five seconds of PTT for two whole frames. On a waterfall that is one burst which starts in one modem's band and finishes in the other's, which is exactly what a frame torn in half would look like, so the observation was right and the diagnosis it suggested was wrong. **The harm is the deafness, and the station's own frame log measures it - exactly, not by a heuristic.** Receive is gated for the length of a keyup (half duplex), so appending another link's frame keeps us transmitting for another 0.7 to 3.4 s at 300 baud, straight through the window in which the answer to the frame we just sent arrives; the answers that do arrive come 1.3 to 2.2 s after the burst. Whether any given pair actually shared a keyup is decidable from the log alone, because the two hypotheses predict different gaps and the modem will tell you both: render the second frame's real payload with a token preamble and with a full TXDELAY, and a shared keyup's inter-frame gap is the former while a separate keyup's is the latter plus the 20 ms tail plus the p-persistence wait. For the 15 B RR this station keeps sending to GB7OXF-2 that is **0.667 s against 0.887 s**, and the observed gaps are cleanly bimodal with nothing between 0.725 and 0.802 s - which incidentally is direct on-air confirmation that the token preamble really is being spent across a modem change. Classified that way, **46 % of ident-versus-traffic collisions (137 of 297) end up in one keyup** and the rest just miss and take their own. And of the **83** frames sent with another modem's frame appended behind them, **2 were answered within 6 s (2.4 %), against 35.5 % of the 14,634 that ended their keyup**. (An earlier pass of this analysis used a "next transmission within 1.5 s" heuristic and reported 0 of 22 against 31.6 %; it was directionally right and quantitatively wrong, because 1.5 s straddles both populations. The measured discriminator replaces it.) **The obvious suspect was measured and cleared, which is worth writing down.** Every frame after the first in a keyup gets a token 30 ms TXDELAY (floored at 16 bits, so 53 ms at 300 Bd) on the premise that the far end is already locked to the waveform - a premise that is plainly false when the modem changes mid-keyup. It costs nothing measurable: 24 trials per point, bpsk300 15 B appended behind the 79 B AFSK ident, token against full preamble scored 24/24 against 24/24 from +15 dB down to 0 dB AWGN, 22/22 at -2 dB, 10 against 13 at -4 dB, and 21/21, 19/18, 17/17, 15/14 on ITU-R Moderate at 12/8/6/4 dB. **What does cost is being second at all**: the same frame in its own keyup scored 24 at -2 dB against 22, 22 at -4 dB against 10, and 21/21/19/17 on Moderate against 19/17/15 - a strong adjacent burst ending the instant your own begins is worth a decibel or two at the knee. That penalty is level- and mode-dependent, and did not appear in the other ordering (the 79 B AFSK ident appended behind a bpsk300 frame copied 24/24 down to 0 dB). **So the rule is now one transmitter per keyup.** Every queued transmission carries the identity of whatever is sending it - the `IModem` for a KISS frame, the `PocsagEncoder` for a page, the ARDOP shifter for an ARQ burst, the `StationIdentifier` for a CW ident - and the drain loop peeks before it reads: a different identity ends the keyup and is left queued for its own, where it gets a full TXDELAY and, the part that matters, **its own p-persistence roll**, which is the mechanism by which the reply gets a chance. Back-to-back frames on one modem still share a keyup and still spend the token preamble, because there the premise holds. A transmission that names no source is treated as unique, so an unidentified caller can never ride on somebody else's keyup. Note what this does not do: p-persistence is a roll, not a guarantee, and a station with two busy links on one PA will still sometimes key over an answer - the fix is that it now contends for the channel like any other station instead of bypassing contention by welding itself to a keyup that had already won.

### 2026-08-24 (later7) - mode moves onto the metric series: the info-metric join was a Prometheus idiom that does not survive InfluxQL, and was averaging two links

Tom, looking at the live data an hour after it shipped: "maybe we should have the mode as a tag." He was right twice, and the second reason is the one that matters. The design followed the tidy Prometheus idiom - mode and sub-channel on an info metric, brought in with `* on(station) group_left(mode)` - so the aggregate series carried `station` alone. **That join is PromQL and InfluxQL has none.** His stack scrapes the same endpoint into both stores, so on the InfluxDB side, which is where the dashboards live, the mode was simply unreachable for every aggregate panel: `pdn_station_snr_db_sum` had tags `station` and nothing else, while `pdn_station_info` had `mode`, and no query could put them together. **And it was not really a join to begin with.** One callsign heard on two modes is two links, over different frequencies through different modems with different path budgets, and the live data said so immediately: `GB7BPQ` reads **15.2 dB on afsk300-il2pc and 12.7 dB on bpsk300-il2pc**, and the single series spanning both was the average of two unrelated measurements, describing neither. So the station registry is keyed by callsign *and* mode, and every per-station series carries both labels; `pdn_station_info` stays for the sub-channel and as the list of what is currently heard. Note the asymmetry with SSIDs, which is deliberate and worth stating: SSIDs combine because `GB7IOW-1` and `-9` are one transmitter, one antenna and one path, and modes separate because they are not. The dashboard's legends and its scatter's `GROUP BY` follow (and two of the edits had to be undone: a table builds its columns from the label set, so forcing a `legendFormat` collapses them, and panel 8's legends carry a frames/bytes distinction a blanket replacement destroyed). **The general lesson for anything else exporting metrics from this tree**: an idiom that is correct in one query language can be unusable in another reading the same endpoint, and "which labels belong on the series" is a question about what a measurement *is*, not about tidiness.

### 2026-08-24 (later6) - a station publishes what it hears, in two formats because one cannot do both

Tom asked for per-station SNR into Grafana, generic rather than fitted to his own stack, and then for the thing that decided the design: **individual frames as points on a scatter, each station a series**. A Prometheus-style exposition has one sample per series per scrape and therefore cannot express two frames a second apart - so asking it to plot individual events is asking the wrong question of the right tool. Both are served. `/metrics` is Prometheus text for totals and sums, which is what rates, dashboards and alerting want and what every collector in use can read (his stack scrapes the same endpoints twice, once into Prometheus and once into InfluxDB via Telegraf's `metric_version = 2`, so one endpoint feeds both). `/metrics/frames` is InfluxDB line protocol, one point per frame stamped with the moment it was heard, pulled by a Telegraf `inputs.http` - still pull, so nothing in the daemon knows the address, protocol or credentials of any monitoring system. **Sums and counts, not gauges, and this is the part worth remembering.** The obvious design is a gauge holding each station's latest SNR and it is a trap: a station transmitting every few minutes against a fifteen-second scrape holds its reading across every scrape in between, so a chart draws a flat line and a long-retention store keeps it for ever - one sample smeared across an hour, reading exactly like a continuous measurement of a quiet channel. A `_sum` and a `_count` divide into the mean over any window and produce nothing at all when nothing was heard. A `_last` gauge is published too, named so that anyone charting it can see what they are doing. **Only frames that vouched for themselves count**, which is `DecodeConfidence.IsEvidence` from the prospector reused, and the corpus says why in one line: of the 77 distinct callsigns the 40 m station had ever decoded, **45 were heard exactly once and not one of those 45 ever had a valid check sequence** (`EI0RSI-9`, `EI0RSA-12` and `EI0RSE-1` are all `EI0RSI-1` with a bit wrong; `7B7BPQ` is `GB7BPQ`), while all 21 heard twenty times or more had one. Without the gate every bit error mints a series that exists for ever and appears on a dashboard as a station. What was declined is counted rather than dropped silently, because a large number beside a small station list is a receiver at its limit. **SSIDs are combined**: `GB7IOW-1`, `-2` and `-9` are one transmitter, one antenna and one path, so the label is the base callsign and the SSID travels as a field on the frame. Labels stay thin via an info metric to join on rather than repeating mode and sub-channel onto every series, following the convention his own ADS-B exporter already sets, along with `snake_case`, a project prefix, unit suffixes and HELP/TYPE on everything. A station drops out of the exposition after `stationIdleHours` rather than holding a stale reading; the frame feed is a window rather than a queue, so nothing is consumed by being read and overlapping scrapes are safe (InfluxDB identifies a point by measurement, tags and timestamp, so a repeat replaces rather than duplicates). Served on the waterfall's listener, unauthenticated by design - callsigns and signal reports were transmitted in the clear on a shared channel and the waterfall page already shows them - and off unless configured, because publishing is the operator's decision. A ready-made Grafana dashboard ships in `docs/grafana/`, picking its datasources through variables so it imports against whatever they are called.

### 2026-08-24 (later5) - the survey stops capturing its own transmitter, the ghosts become visible to it, and the two sweeps stop being two

Issues #363 and #364, both from Tom pushing back on an explanation that did not fit the captures he had actually sent. It did not: I had analysed the strongest captures I picked myself, concluded "above the receive filter", and applied that to two files it had nothing to do with. Measured properly they are two different things and neither is the one I named. **The first is the station's own transmitter.** `20260824-165216-2976hz-unclaimed.wav` holds **2,051 consecutive exactly-zero samples** - 170.9 ms - starting at t = 0.963 s, the audio cut off mid-waveform at amplitude 885, against a journal line reading `tx[2] bpsk300` at 16:52:16, which is the capture's own second. Receive is gated while the channel transmits, so the tap is fed nothing and the line clock stops with it - the invariant `SignalSurvey` is built on - but the *radio's* audio does not come back when the daemon clears its flag: a Flex's DAX stream stays muted a little longer, so what arrives is real zero samples and then a step. The clock never stopped, so the gap check misses it; `Reset` is on the keyed edge, so nothing has reset by then. Across 1,309 sampled captures **12% hold at least 20 ms of exact zeros, 71 inside the receiver's passband and 85 outside** - so this is emphatically not a filter-edge effect. A run of exact zeros is now a break in the stream by definition (a receiver does not deliver silence; even a dead band arrives as noise), which catches a device underrun and a dropped DAX packet on the same rule and needs no agreement with the transmitter about when its audio really stops. Writing the test found the consequence that mattered: the zeros were also poisoning the floor, which the tracker follows down fast and then climbs back slowly, so a station went deaf for a minute after each of its own transmissions - a line made of nothing is now kept out of the floor entirely, on the ground that the channel did not change while our radio stopped delivering. **The second is a real signal, and a second ARDOP.** `20260824-165052-2350hz-unclaimed.wav` has one zero sample in the whole file: 270 Hz of flat-topped spectrum at 2206-2476 Hz, 2.1 s, 20 dB over the noise, no tone pair, no squared-carrier line, and 307 mode-and-centre combinations of `pdn-decode` silent on it. 2350 Hz is exactly where modem 2's id-beacon ghost listens, and a ghost is a **receive tap**, so it is not in `channel.Modems`: its band was never in `surveyBands` and `BeaconHeard` never reached `NoteDecode`. An ident the station successfully read was therefore filed as a burst nobody was listening to, captured, and charged to the budget and the cooldown a real unknown signal needs. That is the ARDOP finding of 2026-08-05 exactly, and the comment immediately below the ghost's tap registration already noted that a tap is not one of the channel's modems, for a different reason. Both halves fixed, since either alone still lies. **And Tom spotted that the two sweeps were two.** `CaptureSweep` and the `pdn-decode` tool each carried their own copy of modem construction, the block size a diversity bank needs, the choice to listen on `FrameDecoded` rather than the frame sink, the pipeline flush, and the confidence ordering - and the copies had already diverged. All of it is now `ModeReader` and `DecodeConfidence` in the library, which both build on: a tool telling an operator one thing while the station acts on another is worse than either answer alone. The refactor immediately turned up a latent bug neither copy had noticed - the catalogue's two guards on a centre frequency throw `ArgumentException` and `ArgumentOutOfRangeException` respectively, and both callers matched only the narrow one, so half of the "too wide to sit there" refusals escaped the collapse and were reported to an operator as individual failures. **Still open:** what that 2350 Hz signal is, and why the ghost is deaf to it, which is a different question from the survey mislabelling its frequency.

### 2026-08-24 (later4) - a third of the survey turns out to be captures of nothing, and the prospector says what it looked at

Issues #359 and #360, both opened by Tom looking at what the day's work had produced. **The prospector was working and nothing said so.** Asked "any evidence it was picked up?" about a capture on the live station, the only honest answer available was a thread's CPU counter in `/proc` (302 ticks, 2% of a core - it was working). Work that leaves no trace until it succeeds is indistinguishable from work that is not happening, so the prospector now reports every capture it examines: what read it, whose callsign, and how many occasions of the needed evidence that makes, with a running total every twenty-five captures so a quiet band still accounts for itself, and the counts on `/api/proposals` beside the proposals. **Then the bigger one.** I wrote that half the station's unclaimed captures sat "outside the radio's own receive filter", and Tom asked the right question: how can that be, when the radio evidently heard them? It cannot, and the phrasing was wrong in mechanism. Measured properly: above the station's 2550 Hz slice high cut the audio is not attenuated, it is *empty* - the quiet lines of such a capture sit at **-110 to -117 dBFS**, and `WaterfallSource`'s byte encoding bottoms out at -100 dBFS, so those bins read as byte zero and the tracked floor sits at exactly the bottom of its own scale. Then at one instant every band in the file rises at once, 200 Hz to 6 kHz, to about **-47 dBFS**, for a single 85 ms transform window, while the 50 ms RMS envelope does not move at all. A level change would show in the envelope; this does not. It is a **break in the waveform** - a splice, a repeated block, a phase step where two buffers were joined - broadband by construction and one window long. In the passband it is invisible under real signal; above the cut there is nothing to bury it, so 53 dB over a floor that is measuring nothing reads as a 431 Hz burst at 37 dB SNR. **3,433 of the station's 8,874 unclaimed captures were that**, all above the cut, because that is the only part of the spectrum quiet enough for a discontinuity to show. Two fixes, both narrow and both provable. A bin whose floor has reached `WaterfallSource.FloorDb` cannot support a detection - it is not measuring a quiet channel, it is measuring nothing (threshold 3 dB up; the station's quietest real in-passband bins sit 16 dB up and its ordinary ones 25 to 55). And `minSeconds` now means what it says: `Math.Round(0.15 * 30)` is **4**, not 5, because 4.5 goes to even, so "at least 0.15 s" has always been "at least 0.133 s" - and every one of these captures is 0.133 or 0.167 s, sitting in exactly that gap. **What is not fixed, stated rather than glossed:** the filter's roll-off is a continuum, so the 2600-2900 Hz bins that sit ~16 dB over the byte floor are attenuated hard but genuinely measuring something, the dead-bin rule does not reach them, and a break there can still open a burst. Judging a burst against the channel's noise floor rather than only its own bin's is the next move and is not done here. **One harness lesson worth the entry on its own:** seeding a floor by tiling a capture's quiet lead-in with random sign flips is a white-noise generator - it fills every empty bin at the file's own RMS and hides exactly the hole under investigation. The first replay of this reproduced nothing at all for that reason, and the same harness shape had been used for the day's earlier floor work. Crossfaded tiling keeps the file's spectrum, holes included; `EmptyBandTests.Seed` is the version to copy.

### 2026-08-24 (later2) - the survey stops handing over evidence and starts making a case: the station proposes the modems it should be running

Issue #358. Tom, on the second capture the day's two fixes made readable: "the natural conclusion of this is that pdn-soundmodem should be running code to sweep for these misses and unclaimed and to propose modems." He is right, and the numbers say how right: the 40 m station had **14,267 captures in three weeks** (8,836 unclaimed, 5,402 missed), of which the two opened by hand that afternoon were the same station beaconing every twenty minutes in a mode the station could read and simply was not configured for. A survey that answers "something went past that I could not read" and stops is a diagnosis with no prescription, and nobody is going to open fourteen thousand WAVs. **`CaptureSweep`** reads one capture with every mode that could have carried it, pointed at the centre the survey already measured - the station-side cousin of `pdn-decode`, narrowed to what a station can act on: its own DSP rate only (a 12 kHz station gains nothing from being told it heard a 48 kHz mode) and no HF data waveforms (most of a full sweep's running time, and not what an unread packet burst turns out to be). **`ModemProspector`** clusters what decodes by mode and frequency and, once a cluster has enough behind it, proposes the modem that would have read it - as one of two shapes, and the second is the one worth having: a clear frequency wants a `NewModem`, while traffic *inside* a configured modem's band that it cannot read wants a `FramingChange`, because moving anything would be moving the wrong thing. That is the PD4R-12 case, audible and detected and unreadable for a month inside an IL2P+CRC modem's passband while sending plain AX.25. **Two evidence rules, both learned from a sample run over 240 real captures rather than chosen.** The gate is separate *captures*, not distinct frames: the obvious "how many different frames" is exactly wrong for this traffic, since a beacon is the same bytes for ever and PD4R-12 would never have been proposed under it, having sent one frame's worth of bytes several hundred times. And only readings whose own FCS or CRC verified count towards it: thirty receivers over every capture is thirty chances to find structure that is not there, and the sample run turned up its first Reed-Solomon-only phantom (15 bytes of "qpsk2400" at 3044 Hz, no callsigns) within the afternoon. **`ProspectorWorker`** is the throttle, and it is bounded by construction rather than by a rate limit: one capture at a time, one thread below normal priority, and a sleep after each of nineteen times what that capture cost to sweep, which pins it at a twentieth of one core whatever the station hears and makes a slower box slower rather than busier. A backlog is dropped rather than queued, and counted. **Acting on a proposal goes through the door that already exists.** `GET /api/proposals` returns each one with its evidence and the complete amended configuration - the modem entry spelled, on the lowest free sub-channel - and that document is POSTed back to `/api/config`, so it is validated by the same code, refused in the same words, and ephemeral by the same default, which means a proposal that turns out to be wrong self-heals at the next restart. There is deliberately no apply endpoint: a second way to change a station is a second set of rules to keep in step, and the first set is the one carrying the safety property. Both proposal kinds emerge as an *addition* rather than a rewrite, including the framing change - the modem already there is reading somebody, and changing its framing to catch a station it cannot read would drop the stations it can. Off by default (`"survey": { "propose": true }`): it is DSP work beside a real-time receiver and a station that wants its CPU for its modems should have it. Seventeen tests, the prospector's seven driven off the committed 2026-08-24 capture rather than synthesised audio, because a prospector that works on a signal generator and not on the file that motivated it has proved nothing.

### 2026-08-24 (later) - pdn-decode learns where to listen, and reads a station the survey had been keeping captures of for a month

Issue #355. The capture the floor fix below produced whole went straight into `pdn-decode`, which swept all 46 modes over it and reported silence. The frame was there: `PD4R-12>ALL` UI, 116 bytes, FCS clean, 300 baud AFSK at 1120 Hz audio (7.050570 MHz, 2-FSK, 204 Hz shift, plain AX.25, 21 dB over the noise). Every mode had been listening at its catalogue centre, and `afsk300`'s is 1700 Hz. **A signal survey capture is by definition a signal nothing was tuned to**, so the file this tool exists for was the one file it was guaranteed to get wrong - and the survey had already measured where the signal was and written it into the JSON sidecar beside the WAV, which the tool never read. Three ways to say where, in the order they win: `--centre HZ`; the sidecar, read automatically whenever one sits beside the file and names a centre, so `pdn-decode survey/*.wav` now just works; and `--sweep`, a 500-2500 Hz grid in 200 Hz steps for when nobody knows. The grid step is measured rather than chosen - this capture reads from 1010 to 1210 Hz for a signal at 1120, so a grid point is never further off than a receiver can reach, and the fixture is in fact read at three adjacent grid points. `--sweep` is a strict superset of the default sweep (every mode keeps its own centre, and a grid point landing on it is dropped rather than run twice), and it multiplies the wall clock by the number of centres, which is why it is a flag and why the help says to pair it with `--packet`. Modes with **no** centre are passed through once, untouched: the baseband `fsk*`/`c4fsk*` family occupies DC upwards and the library refuses a centre for it outright (#39). That test is "does this mode have a centre", not "is this an FM mode" - the two sets coincide today, and this tool has already been burned once by asking `FmModeProfiles.IsFmMode` a question about modulators. Modes too wide to sit where they are pointed (a 2.8 kHz `ms110d-*` at 1100 Hz runs off the bottom of the passband) are collapsed into one line naming them rather than a screenful of arithmetic. **Two findings came with it.** The station could have copied PD4R-12 live and did not, because its `afsk300-il2pc` bank reads IL2P+CRC and PD4R-12 sends plain AX.25 - fed the same audio at its own 850 Hz centre that bank decodes the frame when told to read AX.25 and nothing when told to read IL2P+CRC, which is the GB7BWR-2/PD4R-11 finding of 2026-08-03 again, with the same answer: the station gained a fourth modem, `afsk300` at 7.050570, and copied the next beacon off the air eight minutes later at +1 Hz off that centre (`rx[3] afsk300 PD4R-12>ALL 116 bytes snr 16.1 dB +1 Hz`, 15:49:31Z) - the first live off-air decode this mode has ever had. And 824 of that station's 8,836 unclaimed captures sit between 1080 and 1180 Hz, 35 to 90 a day, every day of the month, so this is a station it has been hearing constantly and reading none of. `afsk300`'s ledger row moves from "no full off-air frame decode recorded" to working on receive; #116 (acquisition through an undisciplined receiver's drift) is untouched, the Flex having no such drift. The capture is committed as `samples/offair/2026-08-24/` beside the truncated one of the same beacon an hour earlier, which does not decode at any centre because the frame was cut in half - which is what the floor defect actually cost.

### 2026-08-24 - the survey's floor climbs into a sustained signal: a capture that cut off while the transmission was still going

Issue #353. Tom's 40 m station wrote `20260824-133727-1149hz-unclaimed.wav` (now `samples/offair/2026-08-24/`, a single-carrier 300 baud signal, 533 Hz occupied, 21.5 dB over the noise) and the signal is at full strength in the file's last sample. The writer is not at fault and the WAV is complete: the capture window is `[burst start - MarginSeconds, burst end + MarginSeconds]`, the signal starts 0.58 s in and this detector replayed over the file opens its burst at 1.03 s, so on the 1.0 s default margin the burst the station recorded was 2.17 s and the last second of the file is trailing margin full of a transmission the detector had stopped seeing (the sidecar's `DurationSeconds` states it outright; the WAV alone fixes it only up to the station's configured margin). Replaying the capture's own lines for 34.7 s reproduces the mechanism: the floor in the signal's centre bin climbs **13.6 dB** (-59.1 to -45.5 dBFS), the detector reports fragments of 3.17, 2.40, 0.90, 0.20 and 0.30 s and then nothing at all for the last 22 seconds, and `SignalSurvey` writes one capture and refuses four more on the frequency cooldown - one short WAV of a long transmission, and no sign in the directory that the rest of it happened. **The cause is the test for whether a bin is measuring noise.** It was "was this line under the 6 dB detection threshold", and a modulated signal spends much of every bin's time between 0 and 6 dB over the noise: the gaps between an FSK pair's tones, symbol transitions, the shoulders of a shaped spectrum. Those lines fed the floor the signal's own energy, and neither upward rate is a rate in dB - each step is a fraction of the distance to what the block measured, and that distance is the signal's power, so 0.05 of a 20 dB gap is 1.6 dB in half a second. The 2026-08-05 entry below fixed a floor that could not climb back; this is the same tracker unable to stop. Bins under an open burst are now held out of the average entirely and do not move while the burst runs, which is the design's own stated intent (a bin carrying a transmission is not measuring any noise) with the burst, rather than the 6 dB line test, as the statement of what is carrying one. The hold cannot latch: a burst is closed at `maxSeconds` whatever it is doing, and a burst closed that way has its bins' floors snapped to what the block last measured - the escape the slow creep used to provide, bounded by the timeout instead of by a rate no real transmission could survive. On the fixture: one 19.87 s burst instead of seven fragments, peak SNR 23.0 dB held instead of decaying 22.6 -> 14.7 dB. The 25 s flat-rectangle test in the suite passed throughout, because a 20 dB signal has 13 dB to lose before it falls under the threshold and a flat rectangle never spends a line in the 0-6 dB band; `A_Weak_Signal_Is_Not_Lost_Halfway_Through_By_Its_Own_Floor_Climbing` uses 8 dB, which is where `MinPeakSnrDb` sits, and it, both `OffAirSurveyTests` and `A_Band_Hot_For_Longer_Than_Any_Transmission_Gets_Its_Floor_Back` all fail against the previous detector. **Two operator-facing consequences, neither addressed here**: a transmission longer than `survey.maxSeconds` (20 s) triages as `NotAPacket` and is not captured at all, so a station that was getting truncated captures of long transmissions gets none until that is raised; and nothing counts what triage rejected, where the budget refusals are counted, so the difference is invisible from the panel.

### 2026-08-22 (later6) - qpsk600 moves to the 0.35 default roll-off: the 0.20 cited the same mis-measured NinoTNC figure #340 corrected, and this time the deployed value is the one that moves

Issue #344, the #340 campaign re-run for qpsk600 with its own A/B, because here the factory and the bank agreed at 0.20 (the QPSK bank routes branches through the factory), every mask row was measured at 0.20, and the change moves what goes on air. Re-measured whole-burst and like-for-like, `samples/ninotnc/qpsk600.wav` is 398 Hz - the same 398 as bpsk300's recording, same modulator, same 300 sym/s - not the superseded 328 the July decision cited, so the never-wider rule never forced 0.20 (ours: 0.20 = 328 Hz, 0.35 = 352 Hz, 12 % inside the TNC). The three instruments (`Qpsk600RollOffProbe`): the deployed differential path's tx/rx mismatch matrix is statistically flat at N=400 (a mixed 0.20/0.35 fleet loses nothing during rollout), the real mode-9 capture copies decisively better through the 0.35 matched filter at the knee's foot (131 vs 81 of 400 at -2 dB, about 4 sigma - the deciding instrument, as in #340), and the coherent cross-check prefers the 0.35 transmission outright (120 vs 69 of 400 at -1 dB; its receive filter is fixed, so that is the TX shape alone). nino-bench's `--qpsk-rolloff` default has always sent 0.35 to the factory, so the 6/6 both-ways bench validation inside the very commit that chose 0.20 was in fact at 0.35 and the 0.20 shape was never TNC-validated; 0.35 is the on-air-compatible direction. The deployed ladder moved up or held at every point (N=200, same seeds: -2 dB 50 -> 64, -1 dB 152 -> 157, 0 dB 188 -> 196, ceilings 200 -> 200), the 95-row smoke tier is green with floors untouched, the off-air fixture still copies, `QpskTxShapingParityTests` pins factory, bank and catalogue arm sample-identical for all three QPSK modes, and `samples/pdn/` modes 08 and 09 are regenerated (08 was stale since #340) with a full-set diff confirming exactly those two files moved. Full numbers in the mode-validation ledger (2026-08-22 later5).

### 2026-08-22 (later5) - a quality frame names the mode it was heard on: the diversity banks stop decorating `FrameQuality.Mode` with their branch count

Issue #343, PR #347. A banked receiver reported `qpsk2400-il2pc-multi9` in `FrameQuality.Mode` where a single-branch receiver of the same signal said `qpsk2400-il2pc`; the bpsk300, afsk300 and afsk1200 bank families had done the same for far longer, and qpsk2400 only surfaced it because packet.net had a test pinned to the bare name. The resolution decouples the two fields on purpose. `FrameQuality.Mode` is an identity: consumers correlate it against their configured mode and their mode catalogue, and an identity cannot vary with a receiver-local knob, so every bank now reports the bare catalogue name and `IModem.Mode` alone keeps the `-multiN` decoration, for the daemon's own view of its receiver. No new structured field carries the branch count: the per-frame branch facts were already structured (`FrequencyOffsetHz` and `EmphasisDb` name the winning branch) and the bank's width is static configuration, identical on every frame - a field nobody would read. The frame log's and waterfall's transmit rows, which stamped `IModem.Mode` precisely to share a spelling with the receive column, follow via the new `ModeNames.Identity` (self-description -> catalogue identity), so "everything on bpsk300-il2pc today" stays one query in both directions; `ModeNames.Display` is behaviourally untouched, the operator label having always been suffix-free. Pinned by contract tests in all four bank families' suites plus the `Identity` mapping table. Verified against packet.net's held draft bump (#773): the one red test in its 5199, `Il2p_Frame_Attaches_Zero_Corrected_Bytes_Quality_End_To_End`, passes unmodified against a local dev pack of this branch, along with the rest of its transport suite.

### 2026-08-22 (later4) - the banks' content dedupe stops swallowing ARQ retransmissions: the window shrinks from 3 s to a measured 200 ms and clears on carrier re-acquisition

Issue #342, PR #346. A stop-and-wait retransmission is byte-identical to the frame it retries, and the diversity banks' `FrameDeduper(3L * sampleRate)` discarded any repeat inside 3 seconds, stalling the link; the same constant gated `Afsk1200Modem`'s FX.25 route merge, so 14 catalogue modes delivered 1 of 2 identical bursts at a 600 ms gap (every bank mode plus `afsk1200-fx25`/`-fx25rx`, the latter measured here for the first time). The window only has to span copies of one transmission, so it is now sized from a measurement of that skew (`BankDedupeSkewProbe`: worst case 130 samples at 12 kHz across the two-detector ensemble, plus at most one feed slice of clock quantisation) at two dedupe chunks, and every deduping modem also clears its window when its carrier detect rises after a drop - a re-acquired carrier is a new transmission whatever the clock says, so no timer setting anywhere can outrun the dedupe. The FX.25 route window stays codeblock-wide (its two readings of one block really are up to a block apart) but now only ever suppresses the FX.25 reading; the embedded-HDLC reading is recorded, never checked. `RetransmissionDeliveryTests` pins both properties permanently for all 14 modes: a 600 ms retry delivers twice (all 14 fail against the previous library), one transmission delivers once at the widest configurations including the 18-branch ensemble. Consumer gates verified against a local dev pack: packet.net `Quality_Counters_Accumulate_Across_Frames` and the whole pdn-qso suite (444) pass; the one packet.net failure left is #343's mode string, a separate defect.

### 2026-08-22 (later3) - bpsk300's factory and its deployed bank agree at roll-off 0.35; the 0.20 was calibrated against a mis-measured NinoTNC reference and never went on air

Issue #340: the catalogue's bpsk arms build `BpskMultiModem`, whose branches take `BpskModem`'s 0.35 constructor default, while `BpskModem.Bpsk300` deliberately passed 0.20 - so the bench certified a transmitter the daemon never keyed. Measured before changing anything: the "NinoTNC measures 328 Hz" behind the 0.20 was the pre-issue-#2 window method; whole-burst like-for-like the reference recording is 398 Hz, wider than both our shapes (0.20 = 328, 0.35 = 352). Matched 0.20 and matched 0.35 loopbacks are identical at the knee (268 vs 264 of 400 at -5 dB); against the real NinoTNC burst the 0.35 receive filter is decisively better (248 vs 210 of 400 at -5 dB, 36 vs 14 at -6); and nino-bench always ran bpsk300 at 0.35, including the 6/6 both-ways bench validation, so 0.20 was never TNC-validated. The factory therefore aligned to the deployed 0.35, `OccupiedBandwidthTests` now certifies every mode through `ModemCatalog.Create`, and `BpskTxShapingParityTests` pins factory, bank and catalogue arm to sample-identical TX audio. Deployed behaviour unchanged; masks unmoved (smoke tier 48/48 at or above recorded values). qpsk600's 0.20 (same superseded citation, but both its paths agree) is deferred to #344. See the mode-validation ledger for the full numbers; PR #345.
### 2026-08-22 (later2) - a run of identical scrambled bits no longer reads as silence: the frames a noiseless channel could never decode now copy, on every mode that shared the fault

Issue #339, found by pdn-qso's hermetic rig: bpsk300 deterministically failed to decode 4 of 256 otherwise identical ordinary UI frames over a mathematically silent channel, with RsFailures and CrcFailures both zero, while any noise at all - 120 dB SNR included - made them decode first time. The mechanism, named to the line: each failing frame's IL2P-scrambled stream contains a 48-bit run of one bit value (the scrambler makes long runs improbable, not impossible), a reversal run is a full-strength alternating carrier whose differential product never changes sign, so the slicer sees no transitions, and `PacketDcd.OnSymbol`'s quiet-symbol path counted 24 transition-free symbols as carrier-gone and dropped DCD mid-frame - whereupon the modem's falling-edge deframer reset (the continuous-decode robustness fix) abandoned the frame with its sync word already consumed. Noise "fixes" it because the product grazes zero once per symbol at the reversal envelope nulls and each graze's side is a float residue that decays to nothing on a bit-exact input but chatters - well-timed - under any noise at all: acquisition conditioned on noise being present, exactly as the issue suspected, and at its most fragile where the signal is strongest. The fix is in the shared class: `OnSymbol` now takes the demodulator's own per-symbol decision magnitude and only a symbol whose magnitude has collapsed below a quarter of its ~32-symbol mean counts toward the quiet drop - silence means no transitions AND no signal, not merely no transitions. All five users pass their own evidence (BPSK's differential product or coherent amplitude, AFSK's and FSK's slicer excess, C4FSK's equalized reading, QPSK-coherent's baseband power); the assert path, the transition scoring and the falling-edge deframer resets are untouched, and DCD release into silence lands on exactly the same sample as before (measured: the same bpsk300 burst releases at sample 30601 both sides of the fix). All 38 catalogue modes were swept noiselessly on pre-fix and post-fix builds, over two 2048-frame families (the pdn-qso perf shape and random-content frames). Pre-fix, seven modes lose frames on the perf family: bpsk300/bpsk300-multi/bpsk300-nocrc 4/2048 each, afsk1200-il2p and both direct-FSK IL2P legs 6/2048 each (a held tone or held NRZ level has no grazes, so even noise does not save those - the issue's 300-frame afsk sampling had simply missed a 1-in-341 event), afsk1200-il2p-nocrc 4/2048; the random family corroborates with a frame of its own lost by the same three graze-free modes; single Afsk300Modem loses them too, and the deployed afsk300-il2pc bank's escape was TXDELAY luck - swept at TXDELAY 150 it loses all six; the affected modes were swept at TXDELAY 0 and 150 as well, failing pre-fix and clean post-fix. Everything else is clean pre-fix for stated structural reasons: bpsk1200 (graze chatter survives its 10-sample symbols), the qpsk family (V.26A always rotates; the differential path took #329's decision DCD), classic fsk9600 and the AFSK HDLC/FX.25 arms (bit stuffing bounds runs at six), c4fsk (bits gated on energy), freedv and ms110d (own sync machinery). Post-fix: 0/2048 everywhere in both families. The full mask ladder was A/B'd pre and post on the same seeds: identical point for point (95 rows green both sides, 81 per-point readings matching exactly); suite green. Fix and evidence in PR #341.

### 2026-08-22 (later) - the C4FSK TXDELAY penalty is gone on the data port and under a decibel on AWGN; the cause was the envelope tracker, not the equalizer

Issue #336. The named suspect, the 5-tap equalizer training on the rank-deficient 0x77 alternation, was measured first and cleared: a new bench seam (`C4fskModem.DecisionObserver`, with `CopyEqualizerTaps`) and the probe that reads it (`C4fskTxDelayProbe`) show the taps within 0.03 of identity and frozen at the sync word after every long run-in. What the probe found instead was the envelope tracker: a per-point peak follower is a max-hold on a noisy waveform, so over a 150 ms run-in it climbed to the largest noise excursion of the whole preamble (1.2 to 1.4 times the clean envelope at the sync, against 1.0 to 1.07 after the 20 ms floor), the sync word's own outer symbols read 0.70 to 0.75 normalised against a 2/3 slice, nearly every front-of-frame error was an outer symbol demoted to inner, and at 18 dB the clock instant's stream never carried the sync word after 150 ms. The fix keeps the follower for the first 32 symbols of a burst, where it is what acquires a cold signal inside the run-in floor, and hands over to a decision-directed envelope: one reading per symbol at the clock instant, outer decisions only, each peak moving 0.05 of the way toward its reading up or down alike, inner decisions holding it.

Knees, 50 %, N=200 seed 1, before -> after: c4fsk9600 AWGN 16.9 -> 16.5 dB at TXDELAY 0, 22.3 -> 17.2 at 150 ms, 22.4 -> 17.5 at 300 ms; c4fsk19200 AWGN 19.8 -> 19.4, 24.6 -> 19.7, 24.7 -> 19.8; c4fsk9600 fm-data 14.8 -> 13.2 dB CNR, 16.7 -> 12.8, 17.1 -> 13.0; c4fsk19200 fm-data 14.4 -> 12.6, 16.2 -> 12.4, 16.0 -> 12.4. On the data port the long run-ins now sit at or below the 0 ms knee; on AWGN they finish 0.3 to 0.7 dB above it, and that is the rig, not the receiver: the sim sets its noise against the whole burst's mean power and our shaped 0x77 alternation is a tone 2.15 dB hotter than the 4-PAM frame, so 150 ms of it costs the frame 1.0 dB of SNR by definition (1.25 dB at 300 ms); the FM axis is carrier-to-noise and has no such term. Two refinements were measured and left out: a running-mean envelope that restarts when the alternation ends (the preamble tone reads 14 to 16 % above a symbol's level through the shaping) cost 138 frames over the six AWGN knee rows, and reading the decision back to the clock's fractional instant, which does fix the +15 % phase's clean-burst sync misread, was a wash on the ladder. Two fm-data mask rows are added a rung below the existing ones (18 dB CNR, floor 36 of 40 against 40/40 measured); `C4fskTxDelayTests` pins the mechanism. The NinoTNC corpus probes could not be run here (the corpus is not on this box) and are the one thing left to check. Full numbers and the honest negatives in docs/mode-validation.md (2026-08-22).
### 2026-08-22 - QPSK packet DCD is scored on the symbol decisions; it asserts on every decodable burst and the gear and hold it drives now engage

Issue #329. The differential QPSK path fed Dire Wolf's transition-timing `PacketDcd` with the quadrant changes of the conjugate product, and that product's angle is the phase CHANGE: it moves only when consecutive dibits differ, flickers in noise at every phase-change null, and on a clean all-reversal preamble never moves at all, so 40-60 % of its transitions scored and DCD asserted only on frequency with noise present. The envelope-null timing source the issue suggested was built and measured first and is rejected on the numbers: at qpsk600's 0.20 roll-off the neighbouring symbols move each null by up to a third of a symbol, a clean +60 dB burst scatters its nulls over half a symbol, and no per-transition scorer can reach 30 of 32 from that. `QpskDecisionDcd` scores the decisions instead: the fourth-power coherence of the symbol-instant sample seen from the detector's reference (the de-rotated product on qpsk3600), averaged over 32 symbols, asserting at 0.55 and releasing at 0.10; on noise the average is 0.09 wide and ten minutes of idle at 300 and 1800 Bd never crossed 0.37. `PacketDcd`, `BitDpll`, the coherent path and BPSK are untouched. On the public-API gate every qpsk600 row now asserts (clean, +-7.5/+-15 Hz, the 16-symbol preamble, 0 dB) where before only the on-frequency noisy row did; qpsk2400 asserts 46 symbols into its preamble instead of 171. Release is 60-130 symbols after a burst (200-430 ms at qpsk600), the honest cost. The gear DCD engages is worth 40-50 frames at the knee (qpsk600 -1 dB 149 against 101 with it disabled), the hold's inertia is immaterial on the sim and it now gates on DCD alone, which cures a lone modem at +40 dB and 11.25 Hz that #330's window-gated hold had frozen mid-acquisition. One regression was found and fixed on the way: `QpskModem` reset its deframers on every DCD falling edge, and a DCD that dips mid-burst at the knee cost qpsk3600 43 of 200 frames; the release level went to 0.10 and the differential path resets only when DCD and in-band energy are both down. Ladder (N=200): every row holds or gains (qpsk600 -1/0 dB 146/187 -> 152/188 and 157/198 -> 163/199; qpsk2400 150 ms 195/199 -> 198/199; qpsk3600 fm-mic 150 ms 163 -> 175, AWGN 180 -> 189) except qpsk2400 +7 dB at zero TXDELAY, 195 -> 194, one seed; fading rows within two frames either way; no mask moves. The off-air qpsk600 fixture still copies with 6 corrected of 8; `QpskMultiModemTests` regains its two DCD assertions. Full numbers in docs/mode-validation.md (2026-08-22).

### 2026-08-21 (later6) - the direct-FSK modes take the timing phases, 1.5 to 2 dB at every knee; the clock hold that came with them is measured and rejected

Issue #331's second item (rx-roadmap workstream 10), and it splits cleanly into a large yes and an honest no. **Yes:** `FskModem` now decides every symbol at seven timing phases, interpolated from a short ring of the slicer's own input, each with its own deframer and, on the classic G3RUH leg, its own descrambler and NRZI state; a frame any phase reads is delivered once behind a symbol-clocked content dedupe. Phase 0 is the clock's own instant and reads exactly the level the DPLL sliced, so the shipping behaviour survives as the first branch. On deterministic seeds (N=200) fsk9600-il2p goes 45/128/177 to 135/183/197 of 200 at AWGN +9/+10/+11 dB, fsk4800-il2p 16/74/133 to 51/147/180 at +6/+7/+8, and classic fsk9600, whose only FEC is "some phase's FCS checks", 63/119/159 to 167/195/199 at +14/+15/+16; the fm-data rows move about as far. The step is 10 % of a symbol, not the PSK set's 2.5 %, and that was measured rather than inherited: at ten decision points per symbol the union of the phases saturates by 10 % (128 with none, then 172, 178, 183, 183 at 2.5, 5, 10 and 15 %). **No:** the DCD-gated clock hold at 0.995 is neutral on every sim row here, and it costs the modes an order of magnitude of sample-clock tolerance - `The_Clock_Tracks_A_Mistuned_Transmitter` passes +-2000 ppm as the modes stand and fails from +-500 ppm with the hold in - because this chain deliberately does not interpolate its zero crossings, so the loop's whole correction is (1 - inertia) times a phase quantised to a tenth of a symbol. A stiff clock and an unresolved crossing do not go together; the hold is not shipped for FSK, the measurement lives on the inertia constant, and the ppm test is the guard. Six mask rows added a rung below the old knees, none moved. Full numbers in docs/mode-validation.md (2026-08-21, later7).

### 2026-08-21 (later5) - the AFSK family gets the timing phases and the clock hold; about a third of a decibel at every AFSK knee, and an honest account of what did not move

Issue #331's first item, ported straight from PR #330. `AfskDemodulator` keeps its slicer's input in a short ring and decides every bit at the seven `TimingDiversity` phases, interpolated, with the front end, the clock, the envelope trackers and DCD shared and phase 0 byte-identical to the bit the plain sink always got; `Afsk300Modem` and `Afsk1200Il2pModem` run an `Il2pReceiver` per phase, `Afsk1200Modem` an `HdlcDeframer` (and its `Fx25Deframer`) per phase, `Afsk1200MultiModem` the same per branch, each behind a content dedupe clocked in bits. The clock acquires at 0.74 and holds at 0.995 once `PacketDcd` asserts, gated as BPSK gates it. On deterministic seeds (N=200): `afsk300-il2pc` awgn -2/-1 dB 61/177 -> 92/187 and 80/174 -> 97/187 at 150 ms TXDELAY, its CFO rows 183/183 -> 191/190; `afsk300` 0/+1 dB 70/161 -> 86/175; `afsk1200` +5/+6/+7 dB 27/107/152 -> 49/129/173; `afsk1200-il2p` fm-data +7/+8 dB CNR 50/143 -> 60/151.

Two results worth keeping for their honesty. The clock hold is neutral on every sim row (measured on its own: `afsk300-il2pc` reproduces the before numbers to the frame), exactly as it was on BPSK, and it is kept for what it does to a real capture rather than for a ladder number; the timing phases are the whole of the sim gain. And the fading and FM channels barely move: a fade is not a timing problem, and an FM link fails in bursts of carrier noise rather than at a mistimed sample. The eye sweep behind that (`AfskEyeSweepProbe`, new) is the general instrument: 300 baud has a nine-sample plateau at 40 samples per bit, so the phases mostly agree and only the edges differ, while 1200 baud's window is about two samples wide and one sample late nearly doubles its bit errors. Full numbers in docs/mode-validation.md (2026-08-21, later6).
### 2026-08-21 (later4) - the C4FSK modes get the timing phases and the clock hold: 1 to 2 dB at every knee, and the coarse-resolution question answered

Issue #331's second item. `C4fskModem` now decides every symbol at seven timing phases - the recovered clock instant and 5, 10 and 15 % of a symbol either side, interpolated from a ring of the slicer's normalised input, decisions emitted a third of a symbol late so the late phases have their samples - each phase carrying its own 5-tap equalizer, its own preamble-freeze hysteresis and its own `Il2pReceiver`, with the modem delivering whichever phase reads a frame once behind a 32-symbol content dedupe; and the 4-PAM clock, which acquires at Dire Wolf's 0.74, holds at 0.995 from the moment DCD asserts. The open question the issue raised was whether interpolated phases are worth anything at 10 decision points per symbol, and the answer is yes but at twice the PSK step: summed over eight knee rows of 200, no diversity 681, the PSK set's 2.5 % 1223, 5 % 1339, 7.5 % 1341, 10 % x 2 pairs 1292, and a ninth phase 1366 - so 5 % x 3 pairs, reach 15 %, seven phases. The phases carry nearly all of it (1311 of the 1339 on their own); the hold is neutral on AWGN and worth 4 to 8 frames of 200 on each fm-data row, which is the channel these modes are actually deployed on. Knees, before -> after: c4fsk9600 fm-data 16.6 -> 14.8 dB CNR at TXDELAY 0 and 18.9 -> 16.8 at 150 ms, c4fsk19200 fm-data 16.2 -> 14.4 and 17.9 -> 16.3, with the AWGN rows moving 1.0 to 1.6 dB. No mask moved (both C4FSK FM rows sit at 24 dB CNR and measured 25/25 either way) and the full suite is green. Two negatives worth carrying forward: `fm-mic` is 0/25 at any level for both modes before and after, a mode limit rather than a receiver one; and TXDELAY costs these modes 2 dB on fm-data and 5 dB on AWGN, before this change and after it, which is backwards and is the biggest single thing left in them. Full numbers in docs/mode-validation.md (2026-08-21, later5).

### 2026-08-21 (later3) - the #326 fixture copies: seven timing phases per symbol and a clock that holds, ~1 dB at every PSK knee

Tom asked why the receiver could not do what the bench had done to the fixture, and then, when the first answer called the burst a knife edge, why that was being waved away. It was the receiver's own clock. `TimingDiversity` gives both PSK demodulators seven decision phases per symbol (the recovered instant and 2.5, 5 and 7.5 % of a symbol either side, interpolated so every samples-per-symbol ratio works), each with its own reference state and `Il2pReceiver`, the modem delivering whichever copy passes once behind a symbol-clocked dedupe shorter than any frame; and the DPLL, having acquired at its differential inertia, holds at 0.995 from the moment the offset window seeds or DCD asserts, because the clock that acquired two samples from the copyable phase and then wandered through the payload was what the phases could not follow. `pdn-decode` now reads the N2IRZ-2 beacon out of `packet-24738.wav` with 6 bytes corrected of a limit of 8, and `OffAirQpskTests` pins it. On deterministic seeds (N=200) the knees move: qpsk600 -1/0 dB 100/166 -> 146/187, qpsk2400 +6/+7 dB 155/180 -> 190/195, bpsk300 -5/-4 dB 133/184 -> 174/197, bpsk1200 +1/+2 dB 131/186 -> 173/198; CFO rows hold or improve; qpsk3600 AWGN +8 dB 81 -> 95 % with its detector unchanged. Two faults found by the measurement and fixed: the first cut's phases were [0, -0, 0] through a static-initialiser ordering slip (caught by per-phase fixture scoring, not by the ladder, which read a null), and the QPSK rotation tracker was walking on noise between bursts to its clamp and losing short-preamble bursts that started there (a latent #328 weakness; it now integrates only while a signal is present, while BPSK's stays free-running because gating it measurably cost the selection diversity #236 recorded). Full numbers in docs/mode-validation.md (2026-08-21, later4).

### 2026-08-21 (later2) - the QPSK family gets the bank and the rest of the #236 chain; the fixture gets its answer and keeps its place

Issue #326 asked for three things and one proof: port PR #236's receive chain to `QpskModem`, give the family a `QpskMultiModem` like BPSK's, wire it into the catalogue, and copy the real off-air `qpsk600` burst in `samples/offair/2026-08-21/`. The first three landed; the proof did not, and the reason is now measured rather than suspected. Full numbers in docs/mode-validation.md (2026-08-21, later3); the shape of it:

- **What the 2026-08-07 campaign had already done, and what it had not.** The matched filter, the decode-path band-pass removal and the 0.92 inertia were in; the decision-feedback reference was in but gated on the fourth-power offset window, which on a real QPSK payload at the knee is incoherent throughout, so the reference never decided the payload and the campaign's "AWGN null" was the gate, not the reference. Deciding from cold, with the rotation tracker running from the first symbol and its gain dropped to 0.01 once DCD holds (0.05 was the jitter that made the reference no better than the product), the reference is worth its root-two on the decision noise.
- **The knee was timing-bound.** With a synthetic burst's truth known, a fixed clock at the right phase decoded it with zero wrong bytes where the DPLL left six. Sub-sample crossing interpolation (BPSK's DPLL always had it; QPSK fed zero) is worth ~1.5 dB on qpsk2400 at 10 samples per symbol, and inertia 0.94 a little more; 0.96 breaks the 16-symbol zero-TXDELAY acquisition exactly as #236 found.
- **Carrier offset is closed for the family in simulation.** The product the DPLL sees is de-rotated by the burst's estimated rotation, the seed re-arms on the window's rising edge and releases on the DCD falling edge (a mid-payload release had been collapsing whichever bank branch's window emptied first), and `QpskMultiModem` runs nine branches at baud/40 for qpsk600/qpsk2400 (one for the FM qpsk3600). qpsk600 at +30 Hz: 3 % -> 100 %; qpsk2400 at 120 Hz: 40/40. `ModemOptions.SecondDetector` accepts the qpsk banks (coherent is the only other detector), QPSK soft bits reach the IL2P erasure ladder, and nine mask rows are added upward.
- **The fixture.** A fixed-clock bench receiver swept over every clock phase recovered the frame (a TARPN ID beacon from N2IRZ-2; hex in the folder's README) with exactly 8 corrected bytes at one phase, the Reed-Solomon limit. The deployed chain leaves 10 wrong bytes, six of them consecutive in the first 70 ms of payload and wrong at every clock phase, decided with high margins; no erasure ranking reaches them. The burst is damaged, not merely weak, and the SNR figure was an average over it. It stays committed as a burst scorable in wrong bytes against an 8-byte floor.
- **Found on the way, not fixed here:** QPSK's `PacketDcd` asserts only on frequency with noise present and never with the minimum preamble (pre-existing; the bank's tests pin counters and measurement, not DCD); `sm-ota sim` aborted with an internal CLR error in about 5 of 100 runs on the dev box (minidump kept, not reproduced in isolation); #11 and #116 narrowed but not closed (the first needs a live QtSM retest, the second still covers afsk300).

### 2026-08-21 (later) - pdn-decode: sweep every modem over a recording nobody labelled

Tom asked for a tool he could point at a pile of WAVs off an FM radio that another modem had struggled with, not knowing the mode, and get back the frames as hex and printable ASCII. `sm-decode` answers "decode this as qpsk3600"; this answers "what is in this". `tools/Packet.SoundModem.MultiDecode` (**`pdn-decode`**) sweeps every catalogue mode over each file, groups identical frames across the modes that read them, names the most confident reader (verified CRC beats Reed-Solomon alone beats a frame the receiver would not have delivered), and prints a canonical hex dump, an AX.25 header line and the information field. It carries its own polyphase resampler, deliberately: the library refuses a capture rate that is not an integer multiple of the DSP rate and is right to, but a forensic tool is handed whatever the person's soundcard was set to, and 44100 is an ordinary answer. Everything is driven off `IModem.FrameDecoded` rather than the frame sink, because the event is the superset - it carries the `MonitorOnly` frames a real IL2P+CRC link reads and does not deliver, which is exactly what somebody comparing against another TNC wants to see, badged as what it is.

**The corpus corrected the design, which is the part worth recording.** The first cut defaulted to the FM-native mode set, derived from `FmModeProfiles.IsFmMode` so it could never drift - elegant, and wrong. That table answers "which modes reach the air as frequency modulation", a fact about modulators and deviation targets; the question this tool is asked is what can arrive through an FM receiver, and the shaped-PSK modes answer yes to the second and no to the first (Nino's switch map: "Shaped PSK - SSB radios, **or FM radios**"). Tom's five captures turned out to be one `bpsk1200` IL2P+CRC QSO - N2IRZ-2 to WA2M-2, a BPQChat session - and the FM-native sweep read **none** of it while the wide sweep recovered seven frames from three of the five files. Default is now the whole catalogue, with `--packet` and `--fm` as narrowings; `SweepTests` pins the lesson so a tidy-up cannot re-narrow it. Corpus committed with its analysis at `samples/offair/2026-08-21/`, ledger entry in `mode-validation.md`.

Also landed: `WavFile.ReadChannel`, which reports the channel count `ReadMono` already computed, so the tool can pick the loudest channel of a stereo capture instead of silently reading a silent side; and round-trip tests over every packet mode's own modulator at the 48 kHz card rate, which is the only rendered-capture coverage `c4fsk9600` and `c4fsk19200` have had (`sm-samples` does not carry them). One thing those tests taught, in the doc so nobody re-learns it: build a test frame as a proper AX.25 **command** frame, because IL2P's Type 1 header carries one command/response bit for the pair and a hand-built frame with both bits clear comes back from the plain-IL2P reading normalised to a response. Full suite green. Tool doc: [pdn-decode.md](pdn-decode.md).

### 2026-08-20 (later3) - MS110D WN8 Poor hard-gated: every MS110D Poor point now passes

Tom asked how to get WN8 from almost passing to passing and said "yes, try" to two ideas: keep working past a suspect fixed point, and take a second opinion. Measured on the four blocks that still settled on nearly-right decodes (62 / 15 / 12 / 19 errors), the first idea found that every one of them was a soft phase running to its cap without settling and then a period-2 oscillation accepted as "converged" at the first hard rung - the #316 hole I had filed earlier, now shown to be the exact mechanism. Four schedule changes in the QAM16 branch, each measured on the same ten corpses and five pins: a soft-phase floor; a cycle accepted only between hard-phase decodes; the hard-first schedule also run after a cycle-accept, with an exact fixed point winning and two cycles priced through one probe-anchor model (residuals through different re-fit models are not comparable - the second-opinion idea's banked negative); and a plateau handover, the soft phase ending when its residual stops falling rather than at the cap. One step was tried and rejected on the record (no cycle acceptance at all: two blocks reverted to coin-flip). **Battery: Poor WN8 12 / 4,325,120 canonical and 18 / 4,325,120 disjoint (bounds 4.85E-6 and 6.58E-6), from 31 / 77 this afternoon and 1,254 / 75,713 at the WN8 program's close; AWGN 0; 113 censuses byte-identical; WN7 untouched.** WN8 is hard-gated, `MeasuredOnlyBank` is empty, and **all ten MS110D Poor points pass the hardest channel in the spec in simulation.** Still sim-only by rig physics; H1/H2 with Tom. Also today, at Tom's request, a standing instruction: lead every report with the plain version.

### 2026-08-20 (later2) - MS110D WN8 Poor forty-fold and thousand-fold closer, the shifted tenant path proven, and the Watterson rig corrected

Tom said "go ahead with more G". **G2** opened on WN8's residual with a premise that turned out wrong - the three failing disjoint bursts were not failing on their first block but on a single mid-burst block each - and an instrument that turned out decisive: the matched-filter bound with nothing but label-free probe anchors decoded every one of those blocks to zero, so the MFB's model was never the deficiency. G2b measured the shipped soft cap cutting off a live descent (a longer soft phase converts one of the three); G2c found the real lever, the cold rung: the MFB started every block with an ISI-inclusive matched projection at coin-flip, and a per-symbol linear MMSE start on the anchored trajectory (known probes subtracted, neighbours as Es-power unknowns, the anchor-fit residual as the noise) starts every block at 4-13k errors and lets the soft cancellation finish in five or six rungs - two of the three stragglers and the control burst's own straggler to zero. G2d shipped it in `Ms110dMfbBlockDecoder`, QAM16-scoped so WN7's ensemble stays byte-identical: **Poor WN8 31 / 4,325,120 canonical (7.17E-6, at the mask) and 77 / 4,325,120 disjoint (1.78E-5), from 1,254 / 75,713; AWGN 0; 113 censuses byte-identical.** Exit (ii), re-banked at 31 / 77; the two low-tens fixed points that remain, and the levers for them, are in the G2 evidence. **G3** put every waveform through `FrequencyShiftedModem` at 3750 (the GB7RDG tenant centre) and 5000 Hz - all ten within the shift gate's criterion - and its first Poor arm caught the Watterson rig rather than the shifter: the rig formed its envelope about 1800 Hz with a 2.2 kHz low-pass and had never been pointed at a moved centre (4/12 at 3750, 0/12 at 5000, SNR-independent). It now takes `CentreHz`, scales its taps with the sample rate, and is byte-identical at 9600 Hz; corrected, the shifted WN6 is at parity with native on Poor. The daemon now prints WN7's and WN8's Poor standing at start-up and on SETHW. Full suite 1794/0. Program status: G0, G1, G1d, G2, G3 closed in one day; what remains is H1/H2 with Tom. Evidence `docs/ms110d/evidence/2026-08-20-poorgate-g2/` and `-g3/`.

### 2026-08-20 (later) - MS110D WN7 Poor hard-gated: the 8PSK per-block ensemble

The successor program's G1 asked whether the MFB-form receiver that beat WN8's walls would clear WN7, and the answer was no on its own (238 errors against the chain's 131 over the residual bursts) but yes in combination: the two receivers are never wrong on the same block. G1d shipped that as an ensemble - `Ms110dMfbBlockDecoder` made modulation-generic and given a `Price()` for any block decode, run beside the DFE-chain path on every 8PSK block, the decode with the larger evidence kept. The selector is constant-free: the MFB's evidence is its Gaussian log-likelihood gain over the block (rows x ln of the reconstruction-residual ratio), the chain's is the sum of its |LLR| at the wire bits where the two decodes disagree. The first battery, with a residual-only selector, took Poor WN7 to 0/0 but AWGN WN7 from 0 to 8 on one block where the MFB had converged to a nearest-neighbour codeword - the residual-minimising decode under its own model, invisible to a residual comparison - and that stop condition is what produced the two-sided rule; the chain's |LLR| on those five bits was 4.5 against 1-3 wherever the chain is wrong. Final battery: **Poor WN7 0 / 3,243,776 on both families, AWGN WN7 0, the other 114 censuses byte-identical to G0, WN8 untouched**. WN7 is hard-gated by default; nine of the ten Poor points now are, WN8 the one measured-only. Sim-only by rig physics, so the ledger row stays partial and H2 remains the route to a hardware confirmation. Also found and fixed on the way: the MFB's block-0 probe geometry for one-frame interleavers (the preamble's closing probe is unshifted), caught by the hermetic WN7 UltraShort loopback; and #316 records a latent cycle-accept hole in the shipped MFB for its own registration. Evidence `docs/ms110d/evidence/2026-08-20-poorgate-g1/` and `-g1d/`.

### 2026-08-20 - MS110D: the Poor-gate successor program registered, the record reconciled, and the measured-only counts banked in the suite

Tom asked where MS110D stood and for a work plan. The survey found the area in better shape than its documents admitted: the WN8 redesign program had closed on 2026-07-31 as exit (ii) (WN8 Poor from coin-flip to 2.90E-4 canonical / 1.75E-2 disjoint through the MFB-form receiver), §E2 had run live eighteen times on 2026-07-27 and §E4 from a real antenna on 2026-08-03, yet `docs/ms110d/README.md`, `roadmap.md`, `phase-b-closeout.md` §4/§5 and all five OTA planning documents still described the state before those things happened. The one substantive gap was in the suite, not the prose: the W6 closeout's obligation that `MS110D_POOR_GATED`'s expected-red values re-bank to the measured figures had never reached code, so nothing asserted WN7's 2.56E-5 / 1.48E-5 or WN8's 2.90E-4 / 1.75E-2 and a regression from either was invisible.

**Registered: [docs/ms110d/poor-gate-successor-plan.md](ms110d/poor-gate-successor-plan.md), issue #312**, in the WN8 program's form (registration before running, pre-committed kill rules, corpse before battery, byte-identity). Its charter is the two measured-only Poor points plus the debt. The leg that matters most is **G1, WN7 under the MFB-form receiver**: the Phase B B3.9 verdict ("the waveform's own floor, needs added information") was reached under the DFE-chain class, the WN8 program then measured that class's ceiling was its time model rather than the waveform, and W1's calibration lane decoded WN7's oracle-residual corpse to zero on all 11 blocks under per-symbol truth - the "new mechanism evidence" closeout §5 requires before a banked negative is retried. WN7 sits at 2.6x the mask, not 290x. **G2** takes WN8's residual, which W6 localised to three first-block non-convergences on the disjoint family (w1/b0, w2/b0, w3/b0, ~24.5k errors each, the entire 1.75E-2), pricing the preamble-as-anchor and the banked W3 moment observables against the W1 truth seam before building anything. **G3** covers the production path: the GB7RDG tenant puts `ms110d-wn6` through `FrequencyShiftedModem`, which no Poor battery covers and WN7/WN8 never traverse in test. **H1/H2** are Tom's: a 40 m radio evening (the §E3 IQ-vs-DAX A/B, the hour-long Phase 1 ladder, m9psy) and an hour at the pad chain measuring whether the RSP1 rig's ~13.5 dB-at-3.7 W ceiling is gain-stage or thermal, because a pad swap is the only route by which the +19/+23 dB points ever run through a real transmitter. Both points stay sim-only until then, and the plan says so.

**G0 ran the same day**: `Ms110dMaskTests.MeasuredOnlyBank` pins WN7's and WN8's closing counts (both seed families, exact, in the battery configuration; `MS110D_POOR_GATED=1` still asserts the mask and is how a chase leg gets past the bank), the five closeout §6 guard pins reproduced their digits on the Release exe, and the three-lane battery re-baselined current main with the bank armed (evidence under `docs/ms110d/evidence/2026-08-20-poorgate-g0/`, verdict in its README). The documentation reconciliation is this docs-only PR: Phase B closeout amended with dated notes rather than rewritten; README and roadmap carry the WN8 result and the successor; the ledger rows drop caveats retired on 2026-07-28; the OTA documents say what the 2026-07-27 and 2026-08-03 evidence closed and list the software gaps that contradict "everything offline is built".

### 2026-08-15 - the daemon can identify itself: Morse ID as a per-modem setting

Tom asked for a FreeDV campaign transmitting from the GB7RDG node into the 7053-7060 segment, identifying as M0LTE in CW every ten minutes ([docs/freedv-ota-plan.md](freedv-ota-plan.md)). The access audit found everything ready except the one thing the daemon could not do at all: `grep -rln Morse src/` returned nothing. `MorseGenerator` and the ten-minute interval logic existed only inside the OTA harness, wired to the Flex transmitters there, so the only station that could identify itself was a measurement run. `idBeacons` is not that feature and never was - it is a receive-only ghost listening for *other* stations' idents, and its own docs said "it never transmits".

**So `MorseGenerator` moved from `tools/Packet.SoundModem.Ota/` into `src/Packet.SoundModem/Ident/`** (the rendering is pure DSP; the harness now uses it from its new home) and gained `StationIdentifier`, the policy layer over it, plus an `identify` block on a modem entry. Tom's call that it be **per modem rather than per station** is what makes the design work: the modems on one channel can sit kilohertz apart, so a station-wide ident would land on one audio frequency, say nothing about the others, and usually sit on top of one of them. Hung off a modem it defaults to **that modem's own centre**, which is both the useful answer and the one the band planner keeps current for free. That default earns its keep immediately - on the campaign's own layout, a conventional 700 Hz ident tone on the planner-chosen 7.049750 dial would have transmitted at 7050.45, directly on the node's `afsk300` slot.

**An ident is owed only after transmitting**, which is the other half of the rule and the half that is easy to get wrong. The clock starts at the first transmission and one falls due when the interval has elapsed *and* the modem has transmitted since it last identified - so an idle modem never keys up to announce a callsign nobody has heard transmit, and a busy one does not identify after every frame. This is what a NinoTNC does ("while the station is transmitting"), and matching the established behaviour on this network beat inventing a rule. It goes through the same `EnqueueTransmit` delegate path paging uses, so it contends politely instead of transmitting over somebody, spends its TXDELAY budget on silence (an SSB transmitter radiates nothing without audio, which is what PTT settling wants), and stamps the clock **only on success** - an identification the transmitter refused was not made, and clearing the debt for it would mean the station quietly stopped identifying.

Six things are refused at start-up rather than ignored, because a silent no-op on a licence condition is the worst available outcome: a missing callsign, both `toneHz` and `rfFrequency`, `rfFrequency` with no band plan to resolve it against, a callsign Morse has no code for, a tone above Nyquist, `identify` on a receive-only station, and `identify` on `ardop` (its ARQ bursts do not go out as addressed frames, so there is nothing to count transmissions against). A tone outside the planned passband warns and starts. Proven end to end on `flex:mock`: a frame to the FreeDV modem's KISS port produces `tx[3] freedv-datac1 M0LTE>TEST` followed by `id[3] M0LTE in CW`, no repeat while idle, and nothing at all on the three modems that did not ask for it. `scripts/kiss-send.py` is the injector that made that testable without a node attached.

### 2026-08-14 (later still) - the filter moved, the warning did not: a read-back that never asked the radio

The fix above went out as 0.34.1, Tom installed it, and the station said the same thing again - while the waterfall showed the filter had visibly narrowed. Both were right. `filt 0 450 2550` had moved the DSP; the read-back was reporting a value it had cached at `slice create` and never refreshed.

**The radio does not push this change back to the session that made it.** Measured on GB7RDG's 6500 (fw 4.2.20) with a read-only second client while the station ran: the slice reported `filter_lo=450 filter_hi=2550`, the new values, at a moment when the daemon was still insisting on 0-3000. No slice status reached the commanding session in the whole 5 s window, so the poll re-read its own stale copy until it timed out. The station had been *right about the radio* and wrong about itself.

**The confirmation is a re-subscription, not a wait.** A repeated `sub slice all` re-dumps every slice in ~40 ms, and the dump lands before that command's own reply on the connection, which one reader processes in order - so awaiting the reply means the state is already in hand. `M0LTE.Flex` 0.14.1 refreshes that way inside the read-back loop (pin moved here), and `MockFlexRadio` now models both halves: no echo after `filt`, and a re-dump on subscribe, dump before reply. The old echo is precisely what let 0.14.0 pass its tests, so the mock has now been wrong in the same place twice, in opposite directions.

Worth keeping: the transmit object *does* push its changes back - the 10 kHz transmit-filter clamp was measured through exactly that read-back - so this is a difference between two objects on one radio, not a rule about the radio. Guessing either way from the other is what produced both bugs.

### 2026-08-14 (later) - the slice receive filter has never taken, and the warning that said so blamed the radio

Tom read a line off GB7RDG's journal that did not add up: `asked the slice for a receive filter of low 450 Hz, high 2550 Hz and it reports 0-3000 Hz - the radio would not go that wide`. 0-3000 *contains* 450-2550, so nothing was deaf and the radio had not narrowed anything; it had ignored the request and stayed on the stock DIGU passband. Two separate faults, both in `M0LTE.Flex`, both fixed in **0.14.0** (pin moved here).

**The command was wrong.** A slice *reports* its passband as `filter_lo`/`filter_hi` and is *moved* by `filt <n> <lo> <hi>`. 0.11.0 wrote the reported names back with `slice set`, which does not move it - the same report-one-way/write-another asymmetry the transmit filter has, and one this repo's own `FlexIqNoise` tool had documented and been using correctly against the 6500 since it was written. So the receive half of the wide-DAX path has never worked on hardware: this daemon's `receiveLow`/`receiveHigh` (the clearing margins around the outermost modem, `Program.cs`) have been computed, announced and discarded since the day they landed. Harmless for the 40 m three-modem plan, whose 650-2350 Hz of audio sits inside the stock window either way; **not** harmless for anything that needs DAX to carry more than ~3 kHz, which was the entire point of the feature (`ms110d-*` reaching past 3.1 kHz, and the widened `Passband.Fit` window with it). Those were deaf above the slice's default and only this misleading warning said anything.

**The offline tests could not have caught it.** `MockFlexRadio` was written in the same commit as the bug, from the same assumption, and honoured `slice set ... filter_lo=`; seven tests proved the client against a model of the radio that agreed with it. The mock now honours only `filt` and discards the `slice set` form, so the suite fails the way the radio does. The instrument-audit lesson again, in a new place: a mock authored alongside the code it checks tests the author's belief, not the hardware.

**And the warning is now per edge and directional.** "The radio would not go that wide" was emitted for any disagreement, so the case above - filter *wider* than asked, nothing lost, request simply gone - read as a bandwidth limit that does not exist. It now says which cut landed where, whether that cuts into what was asked for, and separates cause from cost; the write is no longer best-effort, so an error code distinguishes "the radio refused it" from "the radio took it and did nothing". Also every printable string in that library is ASCII again (the em dash in the line Tom quoted was landing in a station's journal), pinned there by a `PrintableTextTests` ported from this repo's `SourceTextTests`.

Worth knowing at the next restart of a Flex station: the narrowing now actually happens, so a slice that has been sitting on 0-3000 will close to the planned window.

### 2026-08-14 - the waterfall answers two questions it was silent on: what the last burst did, and whether the node is still attached

Both of Tom's asks are about state a station has and never showed. **The transmit readout is now held.** Forward power and SWR were pushed to the page as a formatted string that existed only while the meters read above 0.1 W, so on packet - bursts of a fraction of a second, minutes apart - the one moment the figures were on screen was the one moment nobody was looking. `SetTransmitStatus(string)` became `SetTransmitReading(double? watts, double? swr)`: keyed samples accumulate in the server, key-up averages them and that average stays on the page, stamped with the time, until the next transmission replaces it. The page shows live figures in a red readout and held ones in a neutral one labelled `Last TX 09:15:23` - colour is never the only difference, because a held 29 W read as a transmitter still up is exactly the misreading this feature could introduce. The message is structured rather than prose (`{keyed, watts, swr, at}`), which also retired the page's regex over its own status text for the high-SWR alarm.

**And every modem label says whether the consuming node is connected.** `KissTcpServer` already raised connect/disconnect events and the journal already logged them - one line, at the moment it happens, hours before the operator looks. `SetHostPorts` takes a snapshot of every KISS port and its client count (the daemon republishes the whole set on each change, so a missed event cannot leave the page wrong), and each chip resolves it per modem: the dedicated port and the multiplexed one both reach it, so the badge answers "can anything get to this modem" rather than "which socket".

Both are sent to a browser on connect as well as broadcast, which turned up the interesting bug: read the retained message *around* the client's registration and a change racing the handshake arrives twice or not at all - the duplicate is what the dedup test caught. The snapshot is now taken under the same lock the setters hold while they broadcast, with `_clientsLock` nested inside it. The node:vm page probe gained a real `classList` (state the page carries in a class was invisible to every assertion before) and a `replaceChildren` that keeps its arguments (which is why the modem chips could not be read back at all).

### 2026-08-06 (later) - the receive-performance roadmap and the 40 m capture campaign

PR #236 rebuilt the differential BPSK receive chain against measurement (~1 dB AWGN, ledger entry in mode-validation.md); [docs/rx-roadmap.md](rx-roadmap.md) is now the durable plan for what remains - ranked workstreams (soft/erasure RS as the big cross-repo lever, ensemble decode-any, retransmission soft combining, two-pass burst processing, a Poor-channel MLSE, impulse-noise instrumentation, per-station priors, and waveform escalation as the strategic answer to Poor's outage floor). Supporting it, a **live 40 m capture campaign**: `pdn-capture-40m.service` on the dev box drives the Flex 6500 receive-only and keeps continuous 12 kHz raw audio plus frame log plus survey under `/home/tf/capture-40m/`, via the daemon's new `rawCapture` config section, because the 37-frame miss corpus is nearly exhausted as a discriminator (32 copy). Details, discipline and harvest methodology in the roadmap doc.

### 2026-08-06 - MS110D became a first-class tenant: ARDOP bridged to 48 kHz, the spec-fixed centres movable, SETHW waveform control, and shared-PA arbitration

The programme Tom asked for on 2026-08-05 (plan approved as an artifact, five PRs) landed across #220, #221, #223, M0LTE.Flex #17/0.12.0 and this PR. The goal was one sentence: the GB7RDG production config gains `{ "subChannel": 3, "mode": "ms110d-wn6", "rfFrequency": 7053500, "port": 8103 }` and everything plans, starts and runs, with the TX waveform switchable at runtime over KISS - and that sentence now runs end to end on `flex:mock`, all four modems placed (dial 7.049750, MS110D at 3750 Hz audio ABOVE the packet pair, window widened to 5350 Hz), SETHW wn6->wn2 echoed and journalled, the ACKMODE ack arriving after the burst airs. Four obstacles fell, one per PR: `ArdopChannelBridge` decimates/upsamples the fixed-12 kHz ARDOP engine onto a 48 kHz channel (#220); `FrequencyShiftedModem` moves ms110d-*/freedv-* off their spec-pinned audio centres so they stop dictating the dial - with the merge-gate BER ladder catching a real 3 dB noise-folding penalty in the first cut, fixed by bandpassing receive to the on-air band before the downshift (#221); `Ms110dModem.SetTxWaveform` plus KISS SETHW make the transmit waveform a runtime setting, with three ACKMODE/server defects fixed on the way (#223); and the concurrent-TX half: `FlexPtt` re-asserts `tx=1` per keyup (the silent TX-slice steal), `FlexArbitratedPtt` keys only into a quiet radio behind a default-off `"arbitration"` flag, the transmitter loop survives a throwing PTT instead of dying silently (a latent all-devices bug), `systemctl stop` now runs the graceful path via a SIGTERM handler instead of leaking the headless slice, and both our Flex clients register named stations. The nine shared-PA hardware probes (P1-P9, docs/flex-integration.md §11) are the remaining Tom-gated leg; the arbitration default flips only after P1/P2/P3.

### 2026-08-05 (later³) - a CRC-less neighbour is always heard, and shown; the option now only decides whether the host is given it

The split above was the wrong one, and the operator said so as soon as he read it: IL2P+CRC exists precisely because Reed-Solomon alone was letting too much corrupt traffic through, so he does not want RS-only frames reaching his node on that port. He does want to *see* them. "Off by default" was answering both questions with one switch, and the two questions are not the same size - a station that cannot read a neighbour cannot tell you the neighbour is there, and "nothing decoded" and "a CRC-less node you are structurally deaf to" look identical from the outside.

So an IL2P+CRC modem now **always** runs the second, plain reading, and `acceptPlainIl2p` decides only where such a frame goes. Off (still the default) it is monitor-only: waterfall, frame log, journal line and signal survey, and it stops there. On, it also reaches the KISS host. Nothing about an ordinary IL2P+CRC frame changes in either direction, and nothing about transmit changes at all.

**The routing seam already existed and did not need inventing.** `SoundModemChannel.FrameReceived` comes from each modem's constructor frame sink and is the host path; `FrameReceivedWithQuality` comes from `IModem.FrameDecoded` and is the monitor path, with the waterfall, the frame log, the journal line and the survey all hanging off it in `Program.cs`. A monitor-only frame therefore raises `FrameDecoded` and simply never reaches the sink. That breaks an invariant `IModem.FrameDecoded` documented ("fires in addition to the constructor's frame sink"), so its XML doc now says so plainly, including the trap: do not infer delivery from the order the two fire in, because they still fire from the same synchronous decode when both fire, which makes ordering look like a usable signal right up to the frame where one of them does not come.

**Two members on `FrameQuality`, each with one job.** `PlainIl2p` is the fact about the decode - no trailing CRC stood behind this frame - and is what the UI badge keys off, true whether or not the option is on, and true on a `-nocrc` mode's own frames as well, because the guarantee behind them is identical. `MonitorOnly` is the fact about what happened to the frame, and is what anything relaying frames onward must test. They are separate because `CrcValid: null` cannot answer either: it is also null on HDLC, on FX.25, on our own transmissions and on ARDOP, so "no CRC was checked" and "no CRC existed" would be the same flag.

**The KISS sidecar was the trap worth naming.** `KissTcpServer` subscribes to both events; `OnFrameQuality` sends a JSON quality frame whose whole contract is "sent after the data frame it describes". Gating only the data path would have sent a host a quality report for a frame it never received, which a host with the extension on has no way to detect. It tests `MonitorOnly` and skips.

**A consequence the tests found rather than the design.** The diversity banks' content dedupe is a few seconds wide, and a monitor-only copy passes through it like any other frame - so a burst whose trailer will not verify could eat the window and dedupe away a retransmission of the same frame a second later, this time verifying. Losing a real delivery is a far worse trade than the duplicate row the window exists to prevent, so `FrameDeduper` now records whether a copy went to the host and lets the first delivered copy through over a withheld one.

**And a defect in the first cut of the matching rule one level up, worth recording because the wrong version looked right.** Where several branches copy one transmission they do not all establish the same thing about it, so the bank has to choose between their copies. The first attempt ranked them on `MonitorOnly` - prefer the copy that was not withheld - which is the *routing* fact, and identical across every branch of one bank because it is the operator's setting rather than anything about the signal. With `acceptPlainIl2p` off it happens to correlate with the decode and the rule works; with it **on** no copy is withheld, the test says nothing, and the best-centred branch wins even when its reading never verified anything. On the committed GB7RDG capture that is exactly what happens: the five branches from −30 to 0 Hz verify the trailing CRC and the four nearest the carrier (which sits at ~+8.4 Hz) manage only the plain reading, so branch +7.5 Hz won on a 0.87 Hz residual and a **verified** frame came out reported as RS-only - `crc_valid` null in the station's own frame log, and an `RS ONLY` badge on every ordinary frame of any port that had asked to see CRC-less traffic, which is the one place the badge most needed to mean something. Ranking is now on what each reading actually proved (`DecodeEvidence.RankOf`: a verified trailer, then the link's own reading with a trailer that failed, then a plain reading with no trailer behind it), ahead of centring and independent of both the option and branch order. `OffAirBpskTests.A_Verified_Frame_Is_Reported_As_Verified_Whatever_The_Option_Says` pins it over the real capture with the option both ways; a synthetic frame decodes on-centre, never splits the bank, and could not have caught it.

**Dev-box note, because the failure mode is a build that silently does not rebuild.** With the box near its 16 GB (this session: 15.3 GB used), the Roslyn compiler server is OOM-killed and `dotnet build` exits **0** having printed only the dependency lines - no "Build succeeded", no error, and the test project's own assembly quietly not rebuilt. Test runs then execute the previous binary, so edits appear to have no effect and, worse, a deliberately broken assertion appears to pass - which is how a mutation check on the waterfall probe briefly "proved" the wrong thing here. `dotnet build-server shutdown` clears it. If a change seems to make no difference, check the assembly's timestamp before you check your reasoning.

**Evidence.** The committed GB7RDG off-air capture turns out to hold its connected-mode frame **twice**, 12.5 s apart, and only the first one's CRC verifies - every branch demodulates the second perfectly, zero FEC corrections, and no branch can make its trailer check out. That frame has been in the repository since issue #40 and was silently discarded on every run; it is now a badged row and a withheld delivery (`OffAirBpskTests.The_Second_Burst_In_The_Capture_Is_Reported_As_Plain_And_Withheld`). End to end on the operator's own 12 kHz survey capture at 2150 Hz, `bpsk300`: with the option off the host path gets nothing and the monitor path gets `46 B GB7BPQ>BEACON: =5828.54N/00612.69W- {BPQ32}` marked plain and monitor-only; with it on the host gets it once, still marked plain.

**The CPU it costs is nothing worth reporting to an operator, measured rather than assumed.** The added work is one more `Il2pDeframer` hunting a 24-bit sync word per received bit: **5.0-5.8 ns per bit** in isolation, which at 300 baud is 1.5 us per second of audio per branch, so the nine-branch BPSK bank spends **0.0014 % of one core** on it, and a 19200 bps C4FSK modem 0.01 %. Against the whole receive path over 120 s of real off-air audio, five passes each, best of: the nine-branch `bpsk300` bank 4805 ms with it and 4828 ms without, `afsk300-il2pc` 5895 against 5811, `qpsk2400` 556 against 550 - inside the run-to-run spread in both directions. The DSP is per *sample* and behind a FIR; the deframer is per *bit* and behind a shift register, and at 40 samples per bit that ratio is the whole answer.

### 2026-08-05 (later²) - an IL2P+CRC modem can be told to hear a CRC-less neighbour

A station running `bpsk300` at 2150 Hz on 7.0516 MHz had a survey full of `missed` captures. One of them, replayed offline through every 12 kHz mode, came out as `bpsk300-nocrc @ 2116 Hz -> 46 B GB7BPQ>BEACON: =5828.54N/00612.69W- {BPQ32}`, carrier measured at ~2123 Hz: 27 Hz off the station's own centre and comfortably inside its own diversity bank. Same bank, same centre, same audio, and `bpsk300` decoded nothing while `bpsk300-nocrc` decoded it cleanly. The bits were arriving perfectly and the frame was being thrown away at the IL2P+CRC check, because that BPQ32 node sends plain IL2P. Right frequency, right modulation, right baud, wrong IL2P variant - the same population the overnight corpus found on the AFSK slot (GB7BWR-2 and PD4R-11, 2026-08-03), where the answer offered was a second modem entry on the same frequency.

`"acceptPlainIl2p": true` on a modem entry is the one-modem answer. Off by default and per modem, because IL2P+CRC is the interop ground truth and a station may want tolerance on the BPSK slot and strictness on the AFSK one. It changes nothing about transmit.

**The hazard was in what a CRC-less deframer does with a CRC frame, and it had to be measured before anything was designed.** `Il2pDeframer` comes from `M0LTE.Il2p` and can only be instantiated, so the obvious implementation is a second one at `crcMode: false` on the same bit stream. Measured against 0.1.2: it emits the **same AX.25 frame, byte for byte, exactly `TrailingCrcWireLength * 8` = 32 bits before** the `crcMode: true` one does, at every payload size tried. It does not fail and it does not hand up a frame two bytes longer - it sizes the payload from the header, decodes it, and goes back to hunting, leaving the four trailer bytes to be hunted through as though they were noise. Run naively, that delivers every ordinary frame on the channel twice, plain copy first, which would have been far worse than the bug being fixed. `Il2pReceiverTests` pins the answer so a future package version cannot change it quietly.

So the plain reading is **held** for those 32 bits: if the link's own reading emits the same bytes inside them it wins and the held copy is dropped, and otherwise the held copy is released and that is the plain frame. A reset - the DCD or burst falling edge every IL2P modem already does - releases a held frame rather than discarding it, which is the usual path for the case this is for: a plain frame ends where the transmission ends, and at 300 baud DCD drops about 24 bit times later, inside the hold.

One seam rather than eight edits: `Il2pReceiver` wraps the deframer with the same `PushBit`/`Reset` surface, and `Afsk300Modem`, `Afsk1200Il2pModem`, `BpskModem`, `QpskModem`, `FskModem`, `C4fskModem`, `FreeDvDatacModem` and `Ms110dModem` all construct one instead. The knob rides `ModemOptions.AcceptPlainIl2p` through `ModemCatalog.Create`, which refuses it on a mode with no CRC check to relax (including `bpsk300-nocrc`, which already reads plain IL2P) on the same reasoning as the fixed-centre frequency refusal: an ignored setting leaves an operator believing their modem got more tolerant when it did not. The diversity banks need nothing new - every branch reads both ways and their existing content dedupe collapses a transmission two branches read differently.

**And it is honest about the price, in `CONFIG.md` where the option is documented.** A plain frame is checked by Reed-Solomon and nothing else, and RS invents corrections rather than merely detecting errors, so noise will occasionally arrive as a structurally valid frame; that is the entire reason the +CRC variant exists. Those frames log `crc_valid` null rather than true. And an IL2P+CRC frame whose trailer fails is now indistinguishable from a plain one - it is a valid frame with four bytes after it that do not check out, either way - so it is delivered instead of counted as a CRC failure. Same house rule as `heard_at`: document the ugly consequence rather than hide it.

### 2026-08-04 (later⁷) - the transmit waterfall's dead half was an ordering bug, not a pacing one

Tom described the symptom precisely enough to solve it: *"the radio keys up and the waterfall starts showing black rows, then there is a pause of about 50% of the time the waterfall is not showing RX rows, then the preamble, then the frame."* That halving is the whole diagnosis, and it ruled out the theory filed an hour earlier in **#213** - which had the dead time *after* key-up, as the queue drained. It is before, and it is silence.

`SoundModemChannel` raised `TransmittedAudio` **after** `output.Write(samples)`. A real sound card's write blocks until its buffer has room, so a burst longer than the buffer does not return from that call until most of the burst has already played. The display was therefore told nothing for that whole stretch - and `PaceTransmitLines`, keyed with an empty queue, has exactly one thing to draw: silence. Then the write returned, the entire burst landed at once, and the pacer painted it all over again at real time. **Twice the duration, the first half black**, which is what he was looking at.

Every audio-output double in the test suite accepts instantly, which is precisely why nothing ever saw this: `InstantDrainOutput` and `FakeAudioOutput` both return immediately from `Write`, so the ordering never mattered in a test. A new `RealTimeOutput` models the device - a fixed buffer, drained at the sample rate, blocking when full - and reproduced it first time at **92 black lines ahead of 97 lines of signal, 48.7 %**, against "about 50%" off the air.

The fix is the swap: tell the display before the write. What it costs the transmitter is one scale-and-copy of the burst before the audio goes out - bounded, allocation-only, waiting on nothing - so the standing rule that the transmitter must never wait on a picture still holds. `A_Keyup_Is_Painted_As_It_Goes_Out_Not_After_The_Device_Has_Swallowed_It` guards it at a quarter, which leaves room for the genuine lead-in (keying, CSMA, modulating) and for a loaded box while a return of the defect could not fit under it.

**Judder is not addressed** and #213 stays open for it: the 33 ms pacer timer is coarse under load and each tick turns a variable sample budget into a variable clump of lines. Worth re-measuring now the gross error is gone, because some of what looked like judder may simply have been the seam where the black gave way to the burst.

Instrument lesson, third of the day and the same shape as the other two: **a double that never fails the way the real thing fails cannot test the thing that matters.** The clipboard shim always returned success; the `--wav-loop` fixture always repeated; these outputs always accepted instantly. In each case the test was green and the behaviour was broken.

### 2026-08-04 (later⁶) - right-click copy withdrawn; the test asserted its own stub

Shipped in 0.22.1, reported not working, withdrawn in 0.22.2. Two failures worth keeping, and the second is the instrument one.

**The diagnosis in that entry was incomplete.** `navigator.clipboard` being secure-context only is true and is why the `execCommand` path was reached - but `execCommand("copy")` needs **transient user activation**, and per the HTML spec the activation-triggering input events are keydown, mousedown, pointerdown, pointerup and touchend. `contextmenu` is not one, and Chrome does not grant activation for a secondary-button press. So on the machine this was built for, *neither* route can copy: the modern API is unavailable and the older one is unauthorised. Right-click was the wrong gesture, not merely the wrong API - and the entry above confidently explained one of those and missed the other.

**The test passed because it asserted the shim.** The `node:vm` DOM shim's `execCommand` was written for this feature and returned `true` unconditionally, so `Right_Clicking_The_Waterfall_Copies_The_Frequency_Under_The_Pointer` verified that a stub returns what it was written to return. Every part of the mechanism that could actually fail - user activation, the secure-context gate, whether an off-screen textarea can be selected - lives in the browser and was stubbed out of existence. The harness improvement made in the same commit (recording listeners so a real `contextmenu` could be dispatched) was real and made the test *look* more convincing while testing nothing new. Same lesson as 2026-08-03's cross-decode and the `--wav-loop` duplicate: **a fixture that always agrees is not evidence**, and a shim written alongside the feature it tests will agree by construction.

What would work, if it is wanted: a plain left-click, which does grant activation, with the older route (no secure context needed). Not shipped - the operator asked for it backed out, and the next attempt should be verified in the browser it is for rather than in a shim.

### 2026-08-05 (later) - the ARDOP slot was never asked whether it had read anything

ARDOP demodulates inside the virtual TNC, so its frames never reach the channel event the survey learns decodes from. That was already known and already written down: the waterfall and the frame log were wired to `ardopTnc.FrameDecoded` when it was noticed, with a comment saying why. The survey was not. So every ARDOP transmission the station successfully copied was still filed as a burst inside a configured band that nothing decoded - **15 of the 33 misses on the live 40 m station**, the whole slot reading as a modem that does not work.

`NoteDecode` takes `ax25: false` for a waveform that does not carry AX.25. Running an ARDOP payload past the address parser asks a question about a different protocol, and its "byte 0 of the destination callsign is not a shifted callsign character" would file a perfectly good decode under *unattributed* and capture it - noise in the directory, and a diagnosis of the wrong thing. Only frames the demodulator reports `Ok` count: a failed ARDOP decode is exactly what the survey exists to catch, and telling it otherwise would hide the one thing worth keeping in that band.

**Off-air, `sm-decode` gains an `ardop` mode with `--centre`**, which mixes a capture down from where the signal actually sits to the 1500 Hz ARDOP is pinned to, so "was that burst in the ARDOP slot ARDOP?" can be asked of a capture rather than guessed at. Run over the 15 ARDOP-band captures it recovers nothing, and the reason is worth writing down because it is not "no ARDOP on the channel": **14 of the 15 are under a second long**, against 5.46 s for a 4PSK.2000.100 frame with its leader. They could not have contained an ARDOP frame. That is the floor latch of the entry below showing up as fragments rather than as inflated SNRs, and the question has to be asked again of a corpus collected with a floor that tracks.

The tool was proved before the zero was believed: a modulated control frame shifted to 1713 Hz decodes with `--centre` and does not without it, so the harness is doing work. `ArdopChannelShiftTests` now round-trips a real frame through a shifted centre and back and checks the payload comes out, which the two-tone energy check could not have caught - a shift that left the constellation smeared would pass the old test and fail this one.

### 2026-08-05 - the survey's floor could not climb back

Five hours of real collection on the live 40 m station said the instrument was wrong before the findings were. Of 95 captures, **29 contain anything that stands out from the noise** when each clip is measured against its own lead-in second at its own frequency, with a control taken from bins outside the reported band so the statistic is checked against what noise alone produces. The sidecar's `peakSnrDb` barely tracks whether there is a signal there at all (r = +0.27): 40.6 dB recorded against a clip whose in-band bin sits 22 dB *below* its own control, 21.3 dB against one of the strongest real signals of the day. And **28 % of the captures fall in the first minute after one of the four daemon restarts**, which is 1.3 % of the collection time - the window where the floor is still new.

**The floor was a rolling minimum with a carry-forward, and the two latch.** A dip deeper than the 6 dB threshold entered the ring and took the floor with it; ordinary noise then stood over the lowered floor, so every line in those bins read as hot; hot lines are held out of the average that feeds the floor, so a block with no unhot line had nothing to offer and `CloseBlock` carried the previous ring entry forward - the dip. It recirculated, and the floor could not climb back, because climbing back needed the noise to fall below a floor already beneath it. The inflation in the SNR is the depth of whatever fade latched the bin, which is why the numbers look like measurements and are not. The second failure mode is worse than the false positives: a latched band is hot on every line, so it holds one burst open indefinitely and a real signal arriving in it is absorbed rather than reported.

`SpectralBurstDetector` claimed to be `EnergyBusyDetector`'s min-tracking idea generalised per bin. It was not: the proven detector uses an **asymmetric one-pole - down fast, up slowly, but always up** - and cannot latch, because every block moves it and the only question is how far. The fix is to go back to that, per bin, with three rates rather than two: down quickly (0.25), up over about ten seconds when the block had quiet lines in it (0.05), and up very slowly (0.004) when every line was hot, which may be a transmission sitting on the bin and may be a floor that is wrong. A 25 s SSB over moves the floor under a dB, which is what the existing over-runner test demands; a bin held hot by nothing but a wrong floor climbs out within the minute. Seeding follows `EnergyBusyDetector` too - four blocks, taking the loudest, because a cold start that seeds high costs two seconds of deafness where one that seeds low costs every bin reading as busy until the floor has climbed all the way back.

Four tests. Two construct the latch directly and were committed red before the fix existed. A third is five minutes of noise that behaves like noise - exponentially distributed power, a spread of about 5.6 dB - and asks for silence; it was written expecting *not* to discriminate, and it does, which is only known because the assumption was checked rather than asserted. The fourth guards the other side of the bargain, since tracking up to the noise rather than down to its minimum raises the bar every signal has to clear: 8 dB over the noise, two dB over the threshold, is still found and still reported at the SNR it actually has.

What the survey finds once the floor tracks is unanswerable from this corpus and is deferred honestly. Tier 4's shortlist still wants a week of captures, and the week has to start again.

### 2026-08-04 (later⁴) - the survey comes up onto the page

Tom asked what I thought about bringing the survey and its diagnostics onto the web UI. Three things, worth different amounts, and the smallest was the one I had missed: **the panel is where he saw the word "unattributed" in the first place**, and I had fixed the journal and the capture sidecar and left that surface saying it and stopping - the same complaint he had made, one layer up. It now carries the IL2P header type, the reason, and the frame's bytes laid out to be selected and pasted, which is the next thing anybody does with one. `Ax25AttributionNote` moved from `Survey` to `Waterfall`, beside the parser whose verdict it explains: the panel's use of it has nothing to do with surveying. The note quotes a character taken straight off the air, so the panel's `innerHTML` rows gained escaping and a test that a payload containing `<` arrives as text.

**Captures are drawn where they happened.** A capture has a frequency, a width and a time, which is exactly what the waterfall's two axes are - "something we could not read went past *there*" is a statement that display can make and a list of filenames cannot, which is why this is a bracket on the scroll and not a captures browser. Placed by **age, not line index**: the survey runs its own spectrum feed so it keeps working on a station with nobody watching, and its line clock is therefore not the display's; seconds-ago is the quantity both agree on. Each is listed with links to its audio and sidecar, served by the one waterfall route that reads from disk - only the exact filename shape the writer produces, only out of the survey directory, refusals tested including percent-encoded traversal.

**And the blind spot closed.** `SignalSurvey` counted bursts a rate limit, cooldown or missing audio refused, and nothing reported the number: a station left collecting for a week silently becomes a sample rather than the set, and the alternative was counting files per hour and noticing it equalled the cap. The panel header now carries `survey N · M skipped · X MB`, pushed on change rather than polled, and sent to a browser arriving mid-session. It is state, not an event, which is why it belongs on a display and not in a journal.

Verified live end to end: the daemon on the m9psy fixture pushed `survey` on connect and again on each refusal, emitted a `capture` for a real burst, and served its 259 KB WAV and JSON over HTTP while refusing `../frames.db`, its percent-encoded form, and a bare `frames.db`. **No authentication on the waterfall** - operator-accepted, and now stated plainly in CONFIG.md rather than left as tidy-mindedness, because recorded audio is reachable over it.

Instrument note: the page probe's row assertions were addressed by index, so adding two probe steps broke two unrelated tests. Rows are found by content now, with order asserted only where it means something.

### 2026-08-04 (later³) - an unattributed frame explains itself

Tom, on being told to run a SQL query against his own frame log to find out why a frame had no callsigns: *"You should be capturing those details yourself."* Correct, and the tool had the information all along - a frame reaching the panel as unattributed has already passed Reed-Solomon and the IL2P trailing CRC, so the bits are right and the *reading* of them is not, and that distinction is the whole diagnosis. It was being thrown away at the point it was cheapest to keep.

`FrameQuality` now carries `Il2pHeaderType`, plumbed from `Il2pDecodeInfo` through all six IL2P modems: Type 1 translated and Type 0 transparent put the AX.25 address field in different places, so which one carried a frame decides whether the payload is unusual or the decode is, and it is the first question worth asking. `Ax25AttributionNote` says in a line what would not read - a frame too short for an address field and control byte, or the exact byte, field and character that is not a shifted callsign character. Both land in an `unattributed` survey capture's sidecar beside the payload hex, and both land on the journal's `rx` line as well, because the survey is optional, budgeted, and may drop that particular burst. The live case - 118 bytes on `bpsk300-il2pc-multi9`, CRC-valid, zero corrections - now reads `il2p Type1 [byte 0 of the destination callsign is 0x00 → 0x00, not a shifted callsign character]` instead of `(no ax25 header)` and nothing more.

Deliberately a diagnostic and not a parser: `Ax25AddressParser` still decides whether a frame is attributable and this only explains its verdict. Ordinary lines are unchanged and pinned by an equality assertion, these being text that ends up in other people's grep pipelines.

### 2026-08-04 (later²) - the station starts keeping the signals it cannot read

Tom watched a packet-shaped burst slide past at 7.050594 on the live 40 m waterfall - in the 225 Hz hole between the afsk300 bank's top edge (7.050475) and ARDOP's bottom one (7.050700) - and asked the obvious question: we are not listening there, so how can we ever tell what it was? He ruled out the obvious answer himself ("seems a bit indulgent to run many many modems over the whole passband"), and he is right for a better reason than CPU: the *mode* is unknown too, so a comb is centres × modes and it is still silent when it guesses wrong. Energy, meanwhile, is already being computed thirty times a second for the display.

Landed as **issue #206** tiers 1-3, with tier 4 (classification) deliberately deferred until a week of real captures says what the shortlist needs to contain. `SpectralBurstDetector` is the whole-band generalisation of `BandActivityTracker`: per-bin min-tracking floor, runs of adjacent bins 6 dB over it, runs overlapping in frequency on consecutive lines treated as one burst, and a burst reported only once it *ends* - "started and stopped" being most of what separates a transmission from a carrier. `SignalSurvey` judges a closed burst against the modems actually running: **unclaimed** (outside every band), **missed** (inside one, nothing decoded - the most valuable of the three, since it is the residual-miss problem `NinoTncMissCorpusAspirationTests` tracks, invisible today unless somebody happens to be recording), and **unattributed** (a frame that decoded with no readable AX.25 addresses - Tom's other sighting, `2·bpsk300-il2pc-multi9 · 118 B`, whose payload now travels in the sidecar beside the audio). `AudioRingBuffer` is what makes any of it possible: a burst is only reported once it has ended, by which point the audio that carried it is past. Its own spectrum feed rather than the waterfall's - the same transform at the same rate is cheap, and the alternative is a survey that only works when somebody has also asked for a browser page.

**Three defects the tests and the fixture found, all of which would have mattered on air.** `AudioRingBuffer.Write` placed an over-long block's surviving tail at the position the *whole block* started from, silently shifting every later read - real audio from the wrong moment, worse than none because nothing about the file looks wrong. A signal outlasting the floor's ~15 s memory raised its own floor and stopped looking like a burst: a 25-second SSB over came out as a pair of ~13-second "bursts", each short enough to pass the duration gate and be captured as a packet - bins carrying signal are now excluded from the floor, a floor being a measurement of noise. And the reported duration was derived from the WAV's length less its margins, which read −0.2 s whenever the trailing margin had not been recorded yet; it comes from the burst's own line count now.

**One misdiagnosis, banked.** The same fixture run appeared to show one signal reported as four overlapping bursts (1500, 1324, 507 and 288 Hz wide, all 10.13 s), and a merge pass was written for it. The fixture is exactly 10.0 s and `--wav-loop` replays it: the four were four loop iterations of the same clip, let through by a 5-second cooldown in the test config rather than the shipped 120. The merge was reverted. Instrument lesson, same family as 2026-08-03's: **a repeating fixture manufactures repeating findings** - check the clip length against the interval before believing a duplicate.

**End to end on real off-air audio**, which is what says the thing works. Against the committed m9psy 40 m QRM fixture with only `bpsk300` at 1500 Hz configured, the survey wrote seven captures, and the one at 862 Hz audio - **7.050312 MHz, the m9psy afsk300 slot the station was not listening to** - decodes through `Afsk300MultiModem` as `M0LTE-0>GB7IOW-1`. A signal nobody was configured to hear, found by its energy, kept, and read back afterwards. Tier 4 by hand, in one line.

Off unless configured, and budgeted when it is: 512 MB pruning oldest-first, 30 captures an hour, a 120 s cooldown per 250 Hz bucket. Pruning rather than stopping is deliberate and operator-approved - a station left collecting for a week would otherwise quietly stop on day one and leave an empty tail.

### 2026-08-04 (later) - the frame log records what the station sends, and the backlog says which frames were ours

The display learned to list our own transmissions this morning; the *log* still did not write them down, so the record on disk stayed what it had always been - every frame received, none sent, which Tom's view (and the right one) is half a journal. `FrameLog` now takes a `direction` column (`rx`/`tx`) and a `RecordTransmitted` path off the same `SoundModemChannel.FrameTransmitted` event the console line and the waterfall already use - raised after the audio has gone to the device, so a logged row is a frame that actually went on air. A transmitted row carries who to who, mode, length, payload and the modem's configured `audio_hz`/`rf_hz`; it carries **null** for `corrected`, `crc_valid` and `offset_hz`, because those are receive measurements and inventing them for our own transmission would be inventing a measurement of ourselves - the same decision the UI made, kept consistent on disk. The write stays queued and drop-on-backlog like the receive path: the transmit thread does not wait on a disk either.

**The column is still called `heard_at`, and on a transmitted row it means when it went out.** Renaming it would be more honest about one row and would silently break every query, dashboard and documented example written against this log, including `CONFIG.md`'s own - so the wart is documented in three places (the class, the record, the operator docs) rather than deviated from. Same house rule as the `deb` packaging precedent: document the ugly consequence instead of taking the tidier route that costs someone else a breakage.

**Deployed stations get migrated, not broken.** `CREATE TABLE IF NOT EXISTS` leaves an existing table exactly as it was, so a station that has been logging since the `frameLog` work would have kept a table with no `direction` column and failed *every* insert from here on - a modem that silently stops logging, which is the worst shape this failure could take. `Open` checks `pragma_table_info` and adds the column when it is absent; the existing rows are all receives, so `DEFAULT 'rx'` is the truth about them rather than a guess, and no backfill is needed. Two tests build a pre-migration database by hand from the old schema and prove the old rows survive intact, read back as `rx` through both `SELECT *` and the panel's own `Recent` query.

**And the waterfall's opening backlog carries the direction through**, so a browser that reloads sees its own beacons badged TX rather than listed as stations heard. One CSS trap on the way: a historic transmission gets `class="fr tx hist"`, and `.fr.hist`'s grey `border-left` is declared after `.fr.tx`'s cyan one - at equal specificity the grey wins, and the row would have arrived dimmed *and* stripped of the only thing saying it was ours. `.fr.hist.tx` restores it. The node:vm page probe now resolves `border-left` out of the shipping stylesheet the way a browser resolves it (matching class chains, by specificity then source order), because the class list alone cannot say which rule won - removing the new rule turns that assertion red, which is the check the defect deserved.

The `mode` column on a transmitted row takes the modem's own report of itself (`bpsk300-il2pc-multi9`), not the configured name the console line prints (`bpsk300`), so one modem's traffic stays under one spelling and "everything on this mode today" is a single query that includes our own frames. Proved end to end as well as in the suite: the daemon on `--wav-loop` with a `frameLog` and a `waterfall`, a KISS frame pushed in at 18105, and the row lands `direction=tx`, `M0LTE-9>GB7RDG-2`, 38 bytes, 1500 Hz / 7.0516 MHz, nulls in all three receive-measurement columns - and a browser connecting afterwards is sent that row with `tx:true, hist:true` alongside the GB7RDG decode with `tx:null`. Suite 1161/0 (125 skipped) in Debug and Release.

### 2026-08-04 - the BPSK bank's carrier offset becomes a measurement, and the frame panel stops being half a record and half a session

Two things Tom asked for in one leg. **Issue #202**: the `offset_hz` the frame log has been accumulating for every BPSK-family mode was not a frequency. `BpskMultiModem` reported the nominal comb position of whichever branch happened to emit a frame, branches run in array order, and the dedupe is first-wins - so on 26 hours of the GB7RDG 40 m channel, 431 frames from a GPSDO-locked station took nine values and nothing else, 82 % of them the most negative branch. Same defect as the 300 AFSK bank's (fixed 2026-08-02), same fix in shape and now in code: the differential detector was already forming the product whose discarded imaginary part *is* the carrier rotation, so `BpskDemodulator.CarrierOffsetHz` inlines `BpskCarrierOffsetEstimator`'s squaring trick over a decaying window rather than a peak hold (a peak hold would freeze on the first strong burst of a long session), the coherent path reads its Costas NCO instead, and the bank holds every branch's copy to the end of its dedupe chunk and emits the best-centred one as `branch + residual`. Where nothing could be measured it reports **null**, not a comb position. Clean loopback tracks to 0.09 Hz across ±33 Hz and 0.16 Hz across ±100 Hz at 1200 Bd; the real GB7RDG off-air capture comes out at +8.56 Hz against the standalone estimator's independent ~8. Detail and the AWGN numbers in the [mode-validation ledger](mode-validation.md); the offsets that entry corrects were quoted in this repo's own docs, so those are annotated rather than quietly edited.

**And the panel opens on the station's own frame log.** A panel that starts empty says nothing about a channel that has been busy all morning, and on a quiet band it is indistinguishable from a modem that is not working - the station had been writing every decode down since the `frameLog` work and the display was the one place that never read it back. `WaterfallOptions.FrameHistory` is the seam (a delegate, because the log lives in the daemon and the server lives in the library), `FrameLog.Recent` the implementation: its own short-lived read-only connection rather than the writer's, since `SqliteConnection` is not thread-safe and this is called from whichever connection thread a browser turned up on while the writer is mid-INSERT. WAL, which the log has been since it was built, is what makes that free. The last 50 rows - half the panel's own cap - go out oldest-first in one message straight after the config and before the send loop starts, so a frame decoded during the handshake is queued behind them and lands above rather than being interleaved. Rows are dimmed and carry the time they were *heard*, with a date when that was not today; they are listed and never tagged, having been heard before the scroll on screen began. A reconnect rebuilds the panel from the log rather than stacking a second copy. Receive-only, because the log is: what this station sends shows live and is not written down.

**And the waterfall's decoded-frames panel now lists what this station sends**, marked TX and styled apart. It was a record of half the channel - everything heard, nothing sent - so an operator watching their own beacon go out had only the burst to go on. Listed once the audio has left, so a listed frame is one that went on air; no SNR, offset, FEC count or CRC verdict, because those are receive measurements of somebody else; and not tagged onto the waterfall, because the burst is repainted from a queue in real time while the event fires as soon as the device took the audio, so the tag would land up the burst rather than on it. The page's frame dispatch became a named function so the node:vm probe can drive it, which is how "listed but not tagged" is asserted rather than assumed.

### 2026-08-03 (later) - one wide slice instead of two daemons: the passband is worked out, not configured

Tom asked how two pdn-soundmodem instances would share one Flex - ordinary modems on one, MS110D on the other, different places in the same band - and whether a per-modem slice number would be nicer than managing two processes. The answer to the first is that receive would be fine and transmit would not: a Flex has one PA and one transmit slice, two daemons have no shared carrier sense, and (as of this morning's work) each would set the global transmit filter at bring-up with the last one up winning. The answer to the second is that a slice per modem means a channel per slice plus a transmit arbiter, because the radio still has one transmitter - real work, and worth it only for genuinely different dials. Which left a third option that needs neither: **this repo's own §10.2 measurement says DAX is not a ~3 kHz path**. One slice can carry the packet modems at 300-2700 Hz and MS110D 3 kHz higher up, on one dial, in one process, with one CSMA view. Tom picked that route, said to skip the receive-filter measurement and assume, and asked for the passband to be automatic rather than configured.

Three pieces landed. **`M0LTE.Flex` 0.11.0** adds the receive half of the bandwidth question: `ReceiveFilterLowHz`/`ReceiveFilterHighHz` apply `slice set <n> filter_lo= filter_hi=` during headless bring-up, because the transmit filter governs what leaves the radio while the slice's own filter governs what reaches DAX-RX - widen only the transmit side and you get a wide signal out and an ordinary 3 kHz window back in. Its ceiling is *not* measured, unlike the transmit filter's 10 kHz clamp, so nothing pretends otherwise: the filter is read back, and `ReceiveFilterWarning` reports a radio that would not go as wide as asked rather than the modem going quietly deaf. `MockFlexRadio` models the slice passband and takes an optional ceiling so that path is testable offline.

**The passband became a derived value.** `RfPlan`'s 300/2700 constants are now a `Passband` threaded through the solver, and `Passband.Fit` picks one: the ordinary SSB window when the modems fit it - so every existing plan lands on exactly the same dial - and, on a headless Flex whose filters the daemon sets itself, a window widened just far enough to fit them, capped at the radio's 10 kHz. The daemon then sets both filters to match (the transmit high cut from this morning's `TransmitFilterPlan`, the slice receive filter from the same measured bands) and reports the window when it is not the ordinary one. Nothing to configure: the ceiling follows the device, and the width follows the modems.

**Spec-fixed modes turned out to be the blocker, and were a pre-existing bug.** The planner assigned every modem an audio centre, including modes that reject one - so `rfFrequency` on any `ms110d-*`, `freedv-*`, `fsk9600` or `c4fsk*` modem had always died at start-up with "mode has a fixed centre frequency - drop the frequency override", about an override the operator never wrote. The fix inverts the relationship: a mode whose centre its standard pins (`ModemCatalog.DefaultCentreFrequencyFor`, declared and held to the modem's own measured spectrum by `ModemCentreFrequencyTests`) cannot move to suit a dial, so it *is* the dial, and the movable modems are placed around it. Two such modems that want different dials are refused by name; a modem left below the dial a fixed one forces gets told that in those words rather than being shown a negative audio frequency; and the baseband families, which have no centre at all, are refused a band plan with the reason instead of a confusing override message. Found along the way: `ModemBandProbe` could not meter any mode whose probe frame was over in under 2048 samples, so `qpsk3600` - a NinoTNC mode - was being planned at the 500 Hz nominal fallback rather than its real width. The window now shrinks to fit the burst.

End to end against `flex:mock`: `ms110d-wn4` at 7.0516 MHz and `bpsk300` at 7.0540 MHz plan onto one dial of 7.049800 (1800 and 4200 Hz audio), passband 300-4470 Hz, transmit filter 4600, slice receive filter 200-4600. The 40 m three-modem plan is unchanged to the Hz. Suite 1200/0 in Release. Still assumed rather than measured: that a slice will accept a receive filter as wide as the transmit one - the warning is what will tell us otherwise, and Tom's 6500 is where it gets settled.
### 2026-08-03 - overnight validation closes the afsk300 program: bank at 2×, the slot speaks three framings, and one negative banked

The two-vantage overnight capture promised by the 0.14.0 release ran its course (Wessex all night; m9psy quota-blocked throughout - its "daily" limit is a rolling ~24 h window, which the capture loop's backoff rode out with 55 polite 10-minute probes; 0.14.1's in-daemon patience was verified separately against the same refusing instance). Verdict on 45 unique IL2P-family frames from 7 stations across both receivers: **old wide single 19, released single 24, released bank 38 (84 %)**, shipped class frame-identical to the prototype. Full detail in the [mode-validation ledger](mode-validation.md) addendum, including the two population discoveries - UT1HZM's 22 classic-AX.25 frames and the GB7BWR/PD4R plain-IL2P signature, which make the slot a *three-framing* neighbourhood a single-framing config structurally half-ignores (operator fix: three modem entries on one slot frequency) - and one banked negative: the DCD-falling-edge deframer reset cost the old wide single 4 frames but the shipped narrow paths nothing, a 16-bit hysteresis prototype recovered none of them, so no reset-policy change ships and the measurement is on file for whoever looks next. No 0.14.2: nothing earned it.

### 2026-08-03 - the transmit filter is worked out from the modems, not inherited

Tom asked whether the Flex driver could be configured for 48 kHz. It already is - `DaxStreamFormat.ForDspRate` picks the full-bandwidth 48 kHz float32 DAX stream whenever any configured mode puts the channel at 48 kHz, and the reduced-bandwidth 24 kHz s16 one otherwise, so putting `ms110d-wn4` in the modems list is the whole configuration (verified against `flex:mock`: `DAX 48000 Hz → 48000 Hz` versus `DAX 24000 Hz → 12000 Hz` for `bpsk300`). What was *not* configured was the thing that would actually have degraded MS110D on that radio: the transmit filter. It is a global, persistent radio setting - whatever last touched the rig, surviving the daemon - and the factory 3000 Hz cuts the top off a waveform that occupies ~410-3199 Hz measured. The band-planned path had stated the high cut since the RF-terms work, but a station placed by audio centre inherited whatever was there, and MS110D cannot use a band plan at all (its occupied width exceeds the planner's 2400 Hz single-passband budget, so `rfFrequency` on an `ms110d-*` modem fails at start-up).

So a headless Flex now derives it: `TransmitFilterPlan` measures each modem at the centre it is configured for - through `ModemBandProbe`, the same probe the band planner fits with and the waterfall draws, so those three cannot disagree - and sets the high cut to clear the highest, on the same 200 Hz margin and 50 Hz rounding the plan uses (`BandPlanner.HighCutClearing`, now shared). `ms110d-wn4` alone asks for 3400 Hz; `bpsk300` at 1500 Hz asks for 1900, which narrows the filter and keeps transmitted noise off the neighbours. ARDOP keeps its special case (nothing to probe - the width is a per-session negotiation, so the configured cap or the widest it can reach stands in). `"flex": { "transmitFilterHighHz": N }` pins it and a band plan no longer overrides that; `0` restores the old leave-it-alone behaviour, and a value outside 500-10000 is rejected at start-up because the units invite a frequency where a cut-off belongs. The 2026-07-27 entry's "we deliberately never set it" no longer holds.

The clipping check that used to run only for band-planned stations now runs for every Flex station off the same measured bands, which is the whole of what attach mode gets (SmartSDR owns the slice there, so the daemon still sets nothing) - and it no longer tells you to move a modem whose centre is pinned by its spec. Nine tests on the derivation, three on the config key; verified end to end against `flex:mock` in all five shapes (derived, pinned, `0`, attach, band-planned).

### 2026-08-02 (later³) - a station behind a spent quota waits; it does not hammer

The public UberSDR instances meter listening per address per day (3 h on `m9psy-1`), and restarting Tom's daemon put it behind that limit - where the `ubersdr:` device's failure handling turned one polite refusal into an all-evening pelting: a 429 at start-up crashed the daemon (systemd `RestartSec=5` re-asked every five seconds forever), and mid-run the reconnect loop's fixed one-second "breath" plus a give-up-and-restart path did the same in different clothes. None of that can mint quota; all of it burdens somebody else's receiver.

Fixed by classifying failures by what fixes them (`UberSdrReconnectPolicy`, unit-tested apart from the sockets). **Refused-for-now** - HTTP 429 on the preflight or the stream upgrade (`CollectHttpResponseDetails`), or a reply whose `daily_time_remaining_secs` is 0 - waits on a long ladder (60 s doubling to a 15 min cap), never trips the give-up restart, and at start-up brings the station up anyway (KISS, waterfall, a clear log line) with the stream joining when the receiver relents: the same behaviour as if the quota had run out mid-afternoon. **Transient** transport failures keep the quick ladder and the 5-minute give-up (a restart can genuinely help there). **Sessions that die before delivering 10 s of audio** - an instance that accepts and instantly drops - now escalate 5 s → 5 min instead of breathing one second forever. One healthy session resets the ladder. `ConnectionResponse` learns the daily-metering fields; the preflight's refusals come back as data for the caller to classify rather than as exceptions. Verified live against the actually-refusing instance: the daemon prints the reason twice, stays up, and waits. Released as 0.14.1.

### 2026-08-02 (later²) - afsk300's real problem was its receive filter: the narrow-branch diversity bank

Tom pointed the session at the live 7.0503 MHz slot through the `ubersdr:` device - pdn-soundmodem has never decoded afsk300-il2pc well there, which no bench result predicted. The investigation ran on evidence, not code reading: a segmented IQ capture of the slot from `m9psy-1` (corpus/ubersdr/, kept locally), decoded offline through the daemon's exact receive path with an instrumented branch bank, plus synthetic CFO/interference sweeps against the recorded channel.

Three findings. **CFO was exonerated** - the single demod copies its own TX to ±60-70 Hz static offset even at 10 dB SNR, and the real stations measured −3/+35/−35 Hz; the ledger's "CFO-fragile" note came from the RSP1's *drifting* LO, a different failure. **The mechanism is discriminator capture**: the shipped ±400 Hz receive filters reach ~200 Hz past the slot in each direction, and the slot's everyday neighbourhood (an SSB QSO parked at ~7.0500, occasionally a wideband OFDM burst) lands inside them at comparable power; a quadrature discriminator follows the strongest thing it sees. Injecting a clean burst into the *recorded* interference bed: ±400 filters need the packet +6 dB above the neighbour, ±250 manages −3 dB, a ±175 Hz passband −12 dB. **Tight filters cost offset range**, so the deployable shape is the pattern this repo already trusts twice over: a stepped-centre bank of narrow branches.

Landed: `Afsk300Modem` receive filters 400 → 300 (the bench plateau its own comment always described; ctor params added for other widths), and **`Afsk300MultiModem`** - 2·pairs+1 branches of ±250 Hz-filtered `Afsk300Modem` at 35 Hz steps (default ±5 pairs = ±175 Hz), `FrameDeduper` across the bank, TX on the centre branch, no emphasis variants (nothing to twist across a 200 Hz tone spacing). The catalog's `afsk300`/`afsk300-il2p`/`afsk300-il2pc` now build the bank (`offsetPairs`/`offsetStepHz` knobs as for bpsk; 0 = single tight modem). Scored against the first 30 minutes of captured traffic - 13 unique CRC-valid frames from M0LTE, GB7BEX-15 and GB7NOT at three different offsets - **the old wide single decoded 3/13, the bank 13/13**. A 10 s off-air clip (the M0LTE SABM the wide filters lose to the neighbouring QSO) is committed as `samples/offair/m9psy-40m-afsk300-il2pc-qrm.wav` and guarded three ways by `OffAirAfsk300Tests`; bank behaviour (off-tune decode, dedupe, offset-side reporting, framings, pairs:0 collapse) by `Afsk300MultiModemTests`. Suite 995/0. Overnight capture continues for a wider-corpus validation before the ledger row moves; `sm-iqcapture` was also hardened to finalise its WAV on an abrupt server close instead of crashing (found the hard way when the receiver dropped the session and the retry loop met a rate limiter).

### 2026-08-02 (later) - the SSB demodulators move to `M0LTE.Dsp` 0.2.0

The two IQ→audio converters were never modem code. They are textbook SSB demodulation from complex baseband - NCO to put the suppressed carrier at DC, a complex bandpass keeping one sideband, the real part, decimate - and they only lived here because that is where the MS110D capture scorer needed them first. Yesterday's `ubersdr:` work made that awkward visible: the same filter was now serving an offline scorer *and* a live receiver, and the next SDR front end would have grown a third copy.

Lifted into **`M0LTE.Dsp` 0.2.0** as `SsbDemodulator` / `StreamingSsbDemodulator` / `SsbDemodulatorOptions` / `Sideband`, next to `Decimator` and `FrequencyShifter` (which is the real-signal counterpart of the same idea). Renamed on the way: `IqToAudioConverter` says what goes in and out, `SsbDemodulator` says what it *does*, and in a general-purpose package that is the difference that matters. No new dependency - they only ever used `FilterDesign` and `FirFilter`, both already there. Additive to that package's public API, so a MINOR bump under its own versioning policy, with `PublicApi.approved.txt` moved in the same commit.

The tests split along the same line. Everything that is a property of the filter - streaming-versus-reference equivalence, block-boundary independence, sideband selection asserted at absolute frequencies, decimation by one - went with the code, because none of it needs a modem to state. What stays here is `SsbDemodulatorMs110dLoopbackTests`: the question only this repo can ask, which is whether a real MS110D burst survives modulate → synthesise IQ → demodulate → decode and still comes back bit-exact.

Not extracted, and deliberately: the UberSDR client itself. It has one consumer, it is co-developed against a live third-party service whose behaviour is the only authority, and ka9q_ubersdr is young enough that its framing may still move - all of which argue for keeping it where a protocol fix and a modem fix can land in the same PR. The coupling is already nil (nothing in `UberSdr/` reaches past `Iq/`, `M0LTE.Dsp` and `M0LTE.Radio.Audio`), so extraction stays cheap for whenever a second consumer turns up. Revisit then, or when upstream settles.

### 2026-08-02 - a station with no antenna: `ubersdr:` as a receive-only device

Tom asked for UberSDR as a source for receive-only instances, IQ preferred, driven by an ordinary band-plan config with nothing but the device string changed. Landed as `--device ubersdr:<instance>`: the instance may be a host, a host:port, or the https:// URL out of a browser's address bar. The daemon takes the receiver's **iq48** stream (48 kHz complex, ±24 kHz), demodulates SSB from it in-process at the channel's DSP rate, and hands the modems real audio - so every mode, the waterfall, the frame log and KISS work unchanged, and the band plan's computed dial *tunes the receiver* the way it tunes a headless Flex rather than being printed for an operator to dial in. IQ rather than the instance's own demodulated audio for the reason the OTA capture plan gives: holding the complex baseband keeps the receiver's filter, AGC and resampler out of the path, which is what makes an SNR figure off this route mean the same thing as one off a sound card.

Most of the machinery already existed for the MS110D OTA campaign and only had to move: `PcmBinaryDecoder` and `ConnectionResponse` from `tools/Packet.SoundModem.UberSdr` into the core (which gains a `ZstdSharp.Port` dependency - MIT), and `IqToAudioConverter`/`StreamingIqToAudioConverter` into `Iq/`. Two gaps had to be filled to make an offline scorer's converter serve a live receiver: **decimation by 1**, for the 48 kHz mode families whose DSP rate *is* the IQ rate (the anti-alias low-pass is dropped there, in both converters together so their sample-for-sample equivalence test still holds), and **LSB**, which is the same kernel applied to the conjugated baseband. New is `UberSdrAudioInput` - a reconnecting receive loop behind `IAudioInput`, with the C0-measured one-second startup guard applied per connection, because public instances cap a session at three hours and a modem is expected to run for months.

The honest half of the feature is that there is no transmitter at the far end of a WebSocket. `SoundModemChannel.ReceiveOnlyReason` says so once, so KISS, paging and ARDOP all get the same refusal immediately rather than each discovering it differently - deliberately *not* `TransmitInhibit`, which would turn "cannot" into a 30-second wait ending in the wrong explanation. `ptt` alongside the device is rejected at start-up; `ardop` still loads, still hears the channel, and is warned about at start-up since no ARQ session can ever complete (its transmitter delegate is now awaited and caught, because the TNC's transmit worker does not survive an exception out of it).

**It works on air.** Against `m9psy-1.instance.ubersdr.org` on Tom's example 40 m plan (afsk300-il2pc 7.050300 / ardop 7.050950 / bpsk300 7.051600, dial computed at 7.049450 USB), the station decoded real off-air traffic on both packet modems within minutes - including **`afsk300-il2pc`, which had no on-air validation at all before this**. See the [mode-validation ledger](mode-validation.md). Measured demodulated level off that instance is ≈ −26 dBFS RMS, so the default `gain` of 1.0 needs no help.

### 2026-08-01 - the daemon grows its own browser waterfall (Phase 2's display, without waiting for packet.net)

Tom asked for a web waterfall in the daemon itself: 30 fps, selectable 2-4 kHz-nominal span, operator-set dial frequency, modems drawn over the passband with AF + absolute-RF centres and shaded bandwidth, a spectrum view above, and per-burst callsign/SNR/offset attribution readable straight off the scroll. Landed as PR #157, all in the library so the PDN node can reuse it: `WaterfallSource` (overlapping-FFT display-rate lines; the existing `SpectrumSource` stays as the low-rate telemetry feed), `WaterfallWebServer` (HttpListener + WebSocket, single embedded page, per-client bounded queues that drop history rather than stall the receive thread), display-grade `Ax25AddressParser`, and `BandActivityTracker` (burst SNR/extent from the display's own lines - the EnergyBusyDetector min-tracking idea on spectral power, so the numbers always agree with what the screen shows). Two measurement-over-tables decisions: modem overlay bands are measured at start-up from each modem's own modulated audio (SM.443 99 % OBW - new modes draw correctly for free), and burst extent/SNR are measured rather than derived from mode bitrate tables. The daemon gains `--waterfall`/`--dial`/config, and `--wav-loop` (a recording replayed at wall-clock pace as the capture device) for hardware-free demos - which is also how the page was verified on this GUI-less box: the real page driven by a byte-exact stubbed socket under headless Chrome, the real socket path proven by a ClientWebSocket integration test (this box's Chrome cannot create sockets at all, an environment quirk worth remembering - headless canvas *compositing* also silently fails here while the canvas pixels are provably correct via toDataURL). Marker palette validated with the dataviz checker (OKLCH dark band + CVD ΔE); README carries the screenshot.

### 2026-07-31 - WN8 redesign program closed (exit ii): WN8 decodes on Poor; the walls fell to measurement

The program registered this morning ran to its closing verdict in one day, every leg registered before running: W0 re-baselined byte-identity on current main; W1's truth-injection instrument showed the "immovable" Phase B ceiling was the estimator's segment time model (496/136 → 100/36); W1b's matched-filter bound decoded every specimen block to zero on the exact channel - the waveform was never the floor; W2 pinned the residual on the FF+sparse-chain detection sandwich and pivoted the candidate ladder; W3 measured label-free probe-anchor trajectory estimation 11 dB inside the MFB-form receiver's requirement, killing B3.4's "only labels provide" verdict without even needing the moment observables; W5 shipped `Ms110dMfbBlockDecoder` (per-burst delay-profile window, composite-FIR probe anchors, matched projection, SISO-soft/hard cancellation with convergence-gated decision-directed re-fit, fixed-point/cycle-accept/revert termination) behind the QAM16 gate with the full §6 ladder green - corpses 269,237→112 / 269,154→32, all 108 non-WN8 battery censuses byte-identical, AWGN WN8 0 at full budget; and W6's decision battery closed the program per its pre-committed rule at **Poor WN8 2.90E-4 canonical / 1.75E-2 disjoint** - a 1,711×/28× improvement over the coin-flip at entry, measured-only vs the 1E-5 mask, sim-only by rig physics. Exit (iii) is permanently refuted; the successor levers (three burst-start block failures, the slow-fade edge, deeper schedule diversity, the unused moment observables) are recorded with instruments waiting. PRs #129-#139; program record [ms110d/wn8-program-plan.md](ms110d/wn8-program-plan.md) (now historical); ledger updated in [mode-validation.md](mode-validation.md).

### 2026-07-31 - WN8 redesign program registered: the receiver-only attack on the last MS110D Poor point

Tom directed the next Fable program at the WN8 (16QAM r3/4) Poor verdict - chosen over AFC for the CFO-fragile modes (#116) and a WN7 outer-coding/ARQ program. Registered before any DSP work in [ms110d/wn8-program-plan.md](ms110d/wn8-program-plan.md): legs W0-W6, receiver-only scope (the wire stays byte-exact App D), Phase B's full discipline carried (registration-first, ceilings before implementations, corpse before battery, banked-negative clearances, guard pins), and three acceptable exits - hard-gated, improved-measured-only, or measured-infeasible closed like WN7's verdict. Two founding observations from the program design: the b34 "genie indistinguishable" read conflated perfect channel *observation* with perfect channel *knowledge* - no instrument has ever injected the true per-symbol tap trajectory into the detector, and `Ms110dChainBcjr` already accepts per-position h1/h2/noise spans, so the decisive feasibility bound (W1, with a pre-committed kill rule that can close the whole program as infeasible before any architecture is built) is a cheap instrument; and the Table D-VII outer ring is an exact 12PSK - x¹² = 1 for all 12 outer points, scramble-invariant - giving a label-free mid-frame channel observable (windowed y¹²) that Phase B never tried, precisely the information the B3.4 bootstrap verdict said "only labels provide". W0 first re-baselines the byte-identity battery on current main (which has moved past b38: the #103 AGC, the OTA-era work).

### 2026-07-27 (night) - MS110D full on-air campaign: every masked waveform meets its mask over real RF

With the OTA rig re-founded (M0LTE.Flex 0.8.0, the DAX transmit route, RSP1 capture), the first off-air lab campaign found and fixed the two defects only real RF could show: WN2's DFE dead-init at low absolute input level (#101, closed by the input signal-level AGC in PR #103) and the WN6/WN13 Poor collapse traced to the rig's reference phase noise (#102, closed by GPSDO-disciplining the Flex - a rig fix, not a modem change). The full 18-run AWGN+Poor sweep then landed clean (PR #109): **every waveform with a defined mask decodes at or below it on the real rig**, AWGN thresholds monotonic WN0 −3.5 dB → WN7 +8.8 dB; WN7/WN8 Poor were not run - their masks (+19/+23 dB regions) sit above the rig's ~15-16 dB ceiling at 3.7 W, so those two Poor points remain sim-only by physical necessity. This forwarded WN0/WN1/WN3/WN5 from not-yet-on-air to working in [mode-validation.md](mode-validation.md); evidence in [ms110d/evidence/2026-07-27-110d-full-campaign/](ms110d/evidence/2026-07-27-110d-full-campaign/) with the lab-campaign and poor-validation dirs beside it.

### 2026-07-27 - M0LTE.Flex 0.3.0 → 0.8.0: the DAX transmit source becomes load-bearing

The Flex client dependency moves 0.3.0 → 0.8.0, picking up docs/flex-integration.md §10's findings as library behaviour: `FlexStation.SetUpHeadlessAsync` now sends `transmit set dax=1` and reads it back - on 0.3.0 the DAX transmit path never modulated anything (the transmitter's audio source defaulted to the mic, and every DAX enable step returned err=0 regardless). No source changes were needed for the API breaks (0.4.0's `VitaPacket` doesn't touch our `byte[]` mock wiring; 0.8.0's waveform-options split doesn't touch the DAX path we use). Consumer side: `FlexDevice.OpenAsync` treats a non-null `TransmitSourceWarning` after headless bring-up as a failure and throws - a modem that keys and transmits mic silence is dead, not degraded - and the daemon reports the radio's global transmit filter read-back at startup (it, not the slice, limits transmitted audio bandwidth; we deliberately never set it). M0LTE.Radio.Audio stays pinned at 0.1.0 (0.8.0 pins the same). `MockFlexRadio` now models the transmitter source at its real default of dax=0, so the Flex mock loop tests are the offline proof the selection works.

### 2026-07-27 - MS110D Phase B formally closed: 8 of 10 Poor points hard-gated; WN7/WN8 close measured-only

PR #98 lands [ms110d/phase-b-closeout.md](ms110d/phase-b-closeout.md) as the summary of record for the B3/B4 program (PRs #70-#95, #97). WN0-6+13 hold design §6's at-mask bar as **default-armed hard gates** - canonical and disjoint seed families at full §5.3 budgets, Phase A regressions green throughout, 6M-bit default budgets for WN2/WN5/WN6 per the B4 false-red criterion. WN7 closes **measured-only at 2.56E-5/1.48E-5** against the 1E-5 mask: B3.9's anatomy of all 131 residual errors prices the residual as the waveform's own fade-lottery floor - the shipped iterated decoder beats its own oracle bounding instrument, every error is an honest-erasure co-location lottery over the interleaver - so reducing it requires added information (diversity, retransmission, outer coding) outside this demodulator. WN8 closes **measured-only at coin-flip** behind two walls: a 9.2E-4/2.5E-4 true-label model ceiling (92×/25× over mask - the equalizer-plus-chain class fails even with perfect labels) and a bootstrap basin with no label-free crossing; the lever class named for any successor is waveform-processing redesign (FD equalization, pilots-in-data, per-symbol tracking) plus a bootstrap story. The closeout's §5 banked negatives (do-not-retry) and §6 guard-pin registry are binding on any future demod change. Hermetic suite 697/0 (105 env-gated skips).

### 2026-07-24 (evening) - MS110D Phases B1+B2 closed: all four broken-tier mechanisms confirmed, then the science core lands WN4 (and WN13) Poor at mask

B1 (PR #72): every broken-tier point got a confirmed mechanism from ≥3 independent instruments before any fix - WN7/WN8 intra-frame rotation collapsing DD tracking past the ±22.5°/±16° decision half-angles with the anchored probe solve as the persistence mechanism, WN6 a rate-3/4 code cliff on shared physics (its uncoded SER BEATS passing WN13's), WN0 coherent Walsh detection wasting the channel's 2-path diversity (Poor 8× worse than flat Rayleigh; echo refuted by a zero-error static 2-path run). Autopsies in [ms110d/phase-b-autopsies.md](ms110d/phase-b-autopsies.md).

B2 (branch `ms110d-phase-b2`, evidence [ms110d/evidence/2026-07-24-phase-b2/](ms110d/evidence/2026-07-24-phase-b2/)): the science core from those mechanisms. First pass: fading frames equalize on a per-symbol tap trajectory interpolated between the bracketing probe solves (rotation as a phase ramp - the block-buffered architecture's free non-causality), a per-probe gain-1 phase re-anchor kills the anchored solve's steady-state lag, RLS is decision-gated and tracks only the residual, collapse recovery restarts tracking once per unhealthy episode. Turbo: the 2^delay BCJR replaced by a **chain-decomposed exact BCJR** (d independent memory-1 chains, M states each - exactness pinned by brute-force marginalization; echo ceiling and BPSK restriction both gone) running for every PSK mode with a **scrambler-exact echo model** (the legacy lag correlation was phase-scrambled toward zero - a latent model defect) and per-position h1 from re-encoded mid-frame references. **The B2 exit gate holds: WN4 Poor 7.83E-6 canonical / 8.14E-6 disjoint at 6.39M bits each under the full §5.3 rule; WN13 Poor 5.70E-6 canonical banked early.** Poor movement vs B0: WN2 430× (the U=48 turbo exclusion WAS the bottleneck), WN5 2.2E-2 → 5.6E-6, WN13 6.2E-4 → 5.5E-6, WN6 3× down the cliff; WN7/WN8 await B3. §B2.4 delivered: [ms110d/rls-vs-nlms-report.md](ms110d/rls-vs-nlms-report.md) - frame-tied λ stands, NLMS confirmed retired from the signal path. Carried to B3: the WN13/WN6 catastrophic-burst tail (rotation-invisible to correlation magnitude - B3.2's entry autopsy), the K=48 genie instrument defect, WN2/WN5 full-budget closure, the whole-table re-measure.

### 2026-07-24 (later) - MS110D Phase B0 closed: instruments live, and the RLS freeze fix puts WN3 Poor at mask

Stage B0 of [phase-b-plan](ms110d/phase-b-plan.md) executed and closed in one session (branch `ms110d-phase-b0`; evidence [ms110d/evidence/2026-07-24-phase-b0/](ms110d/evidence/2026-07-24-phase-b0/)). The instruments: a **channel-truth genie** - the demodulator takes a noise-free copy of the same Watterson realization (same seed at SNR=∞; the rig draws gains before noise) and runs all channel estimation on truth while detection stays noisy, yielding the perfect-channel-observation bound per point - proven inert by a bit-identical seam test and calibrated the hard way (the first fading run came out 26× WORSE than baseline: noiseless rows give the zero-forcing solve, so the demodulator now measures σ² = mean |noisy−clean|² between its rings and restores the σ²·Σweight Gram term - the true-channel MMSE bound, validated on the static rig and WN4); **evidence-line telemetry** (uncoded channel-bit SER vs the re-encoded TX stream, deep-fade error concentration against the rig's recorded tap trajectory, turbo outcome counters); an **off-rig discipline harness** ({1 ms, 0.5 Hz} / {3 ms, 2 Hz}, measured-never-gated). Honesty remainders from #64/#65/#67 closed, chief among them the **weighted-RLS weight/P asymmetry**: advisory (0.1-weight) rows shrank P at full confidence while barely moving taps, progressively freezing adaptation on every static/AWGN span - fixed to consistent weighted RLS (scale-invariant in the weight), regression-pinned by new DfeTests that fail on the old rule.

The fix moved the table: **WN3 Poor 8.7E-3 → ZERO errors in 3.19M bits, confirmed at seed+10000 - the first Poor point at mask**, under the full §5.3 rule on two disjoint seed sets. WN4 straddles (1.91E-5 canonical / 3.23E-6 disjoint); WN1/2/13 improved ~1.5-2×; the broken tier (WN6/7/8) unmoved as expected (fading frames ran at weight 1, untouched). Phase A hard gates all re-pass at full budget on the B0 code (AWGN 10/10 zero errors, static clean, Doppler clean). First genie science: WN4 Poor's genie bound is only 2× better than measured - its residual gap is **detection**-dominated, not tracking. The telemetry hands B1 its autopsy leads: WN7's errors are fade-UNcorrelated with turbo never converging (0c/88r) and coded BER worse than uncoded - an LLR-chain defect signature; WN8 likewise fade-uncorrelated; WN0 barely codes (1.42E-1 uncoded → 1.12E-1 coded). Also flagged off-rig: WN4 collapses at {3 ms, 2 Hz} (BER 3.0E-1) while clean at {1 ms, 0.5 Hz}. B1 order revised on evidence: WN7 → WN8 → WN6 → WN0.

### 2026-07-24 - MS110D Phase B planned: the program to make the Poor column true

Phase B (design §6 gate: **D-LXIV at mask, no allowance, AWGN + Poor, WN0-8+13, Phase A regressions green**) now has a program plan - [docs/ms110d/phase-b-plan.md](ms110d/phase-b-plan.md), distilled from #69/#64/#65/#67 on top of the closeout baseline. The organizing observation: the 10-point Poor baseline sorts into three regimes - **near** (WN4 at 2.4× mask, WN13 at 62×), **structural** (the BPSK ladder + WN0, 870-8 100×, known physics deficits: stale-across-the-frame channel snapshots, BCJR excluded for U=48 and all QPSK+, WN0's coherent-only Walsh path), and **broken** (WN6/7/8 at 0.13-0.50 BER - random output is a defect, not a weak equalizer) - and the stages follow from it. B0: instruments first - a channel-truth genie exported from the Watterson rig (splits every deficit into tracking vs detection vs infeasible-as-architected), fade-correlated error telemetry, an off-rig discipline harness, and the #64/#65/#67 honesty remainders. B1: time-boxed autopsies of the broken tier - no fix without a written mechanism. B2: the science core - a time-varying channel representation (per-position h1/h2, probe-anchored phase/gain ramps; retrospective interpolation is free in the block-buffered architecture) and the **chain-decomposed exact BCJR**: for the sparse model h1 + h2·z⁻ᵈ the symbol graph splits into d independent memory-1 chains of M states each, which kills the 2^delay state ceiling (#64's 3.3 ms echo limit) and the M^L cost that made QPSK/8PSK BCJR look unaffordable (#69) in one move. B3: family closure - BPSK ladder (WN4→3→5→1/2) → QPSK (13→6) → 8PSK → 16QAM, with WN0's detector mini-program in parallel. B4: the `MS110D_POOR_GATED=1` flip with full evidence. Hard gates along the way: B2 exits on WN4 Poor at mask under the full §5.3 rule; every family closes full-budget + disjoint-seed before the next starts; stalling at mask+2 dB triggers a written stop-and-reassess, not more tuning. Phase A audit discipline carried forward: no constant may encode the rig, genie numbers always labelled, the Phase A evidence set re-runs before any demod-path merge.

### 2026-07-23 - MS110D: the equalizer campaign audited and repaired; Phase A formally closed

The unrecorded 2026-07-19→23 span, then the closeout. Landed in that span: PR #61 (IL2P deframer reset on DCD falling edge), #59 (the 110D PDF vendored beside the transcriptions), M0LTE.Ardop 0.2.0, PR #60 (**Phase B modulations pulled forward** - 8PSK WN7 / 16QAM WN8), and a four-day equalizer performance campaign (~45 commits, run by an experimental agent): RLS tracking, 3-pass bidirectional equalization, turbo re-equalization, a BCJR MAP equalizer for BPSK, several generations of flat/fading gating - plus the xUnit v3 + MTP migration (#62) and a parallel mask runner. The campaign's headline claims: AWGN 10/10, Poor WN4 7.04E-6.

**The closeout audit (Tom: formally close Phase A + review the campaign) retired those numbers and repaired the instruments.** An adversarial dual review (modem source + test side) found: a **blocker** - `TurboReequalize` re-read every Long-interleaver block from a 6.83 s sample ring the blocks outrun (WN1/2 10.24 s, WN5-8/13 7.68 s), silently re-equalizing the head frames against overwritten samples, with the outer code bridging the resulting erasures (WN5's marginal 7.69E-5 era explained); the mask harness's **evidence chain broken** by the migration (per-WN processes each re-running the 3M-bit static gate; vacuous per-point passes possible; the Poor smoke mathematically unpassable at its default budget; the Poor gate silently re-hardened against §6; the §5.3 600 s fading floor plumbed but never enforced); **CI red since the migration with zero tests executed** (MTP apphosts couldn't resolve the runner's .NET; VSTest filter syntax silently ignored; the aspiration scoreboard permanently vacuous); and a set of **rig-fitted heuristics** - the BCJR echo delay hard-coded to the D.6.1 Poor rig's 2 ms path spacing, a "fading" detector that was actually a residual-CFO detector (0.005 rad on the probe-to-probe tap rotation), IsFlatChannel measuring a noise-floor FF tap and structurally unable to return true for UltraShort interleavers, bidirectional passes 2/3 equalizing the frame head through feedback taps filled with its own tail, turbo with no divergence protection, and steady-state allocation throughout the per-frame hot path.

**Tom's direction: make it right, not document-and-defer.** Fixed on `ms110d-phase-a-closeout`: ring sized past the longest Long block + staleness backstop (839be92); harness evidence chain rebuilt - method filters, per-point MS110D_MASK_LOG evidence, Poor measured-by-default with MS110D_POOR_GATED=1 as the Phase B switch, SMOKE labelling, 600 s floor enforced (cfd9fd0); CI repaired (dbeb73e); burst-state leaks cleared (aed1f03); the equalizer de-rigged (05c92b4) - CFO-immune fading detection classified by recurring excursions over a min-tracking floor (the EnergyBusyDetector pattern; validated 0/1664 false-positive frames on AWGN, 0/4096 on static incl. the convergence transient, 152/256 detecting on Poor), searched BCJR echo delay with a significance floor (capped at lag 8 = 256 trellis states - the first cut searched to lag 24 and OOM'd the box at 2^24 states, the constraint the old constant was silently load-bearing for), bidirectional decision-history re-seeding, turbo fixed-point revert, per-dimension noiseVar, dead QAM16 paths made explicit throws; DD training rows preserved across turbo via Dfe.Snapshot/RestoreTraining (ff1d832); hot-path allocations removed with bit-identical numerics proven (c4b83a5/a4b72e3/9f20175, closes #66); design §5.1/§5.3 restated in place to match the shipped instruments (8c0f924). Deferred, with issues: **#64** (what remains of the rig-fitting: RLS λ deviation, weight/P asymmetry, the 2-tap BCJR model's ≤3.3 ms echo ceiling), **#65** (per-position h1[] time-invariance), **#67** (coverage gaps). Test additions: a clock-skew rig (windowed-sinc resample) measuring **±50 ppm met with ~14× margin** (breaking points ±700 ppm on ~4 s bursts, ±300-400 ppm on ~11 s - the design figure the implementation had disclaimed turns out to hold), hermetic ±75 Hz CFO green across all four modulation families, 23 new WN×interleaver×K matrix rows covering every distinct (size, increment) cell of D-XXXVII/D-LI, and WN7/8 joining the interleaver permutation check - Ms110d namespace 161→198 tests. Mask sweeps gained intra-point parallelism (MS110D_MASK_WORKERS - disjoint-seed workers per point, counts summed; the low-rate tail points drop ~N×) and a disjoint-seed verification knob (MS110D_MASK_SEED_OFFSET).

**Fresh full-budget evidence on the final code (§5.3 as restated; fleet OOM-hardened after the box killed two sessions mid-sweep):** **D-LXIV AWGN 10/10, every point ≥3M bits with ZERO errors** (97.5 % Poisson upper bounds ~1.2E-6, an 8× margin under the mask) - including the first-ever full-budget WN1/WN2 runs (previously banked at 500k bits). Static WID2 (0/3/9 ms @ 9 dB): **PASS, 0 errors in 3,018,912 bits**. Doppler: **3/3 clean**. Disjoint-seed cross-checks (AWGN WN4/5, Poor WN4 at seed+10000): **AWGN both 0 errors at full budget; Poor WN4 1.33E-5 vs canonical 2.36E-5 - statistically consistent; nothing is a seed artifact**. Poor (measured-not-gated, the Phase B baseline): the **first complete 10-point Poor baseline ever banked** - WN0 8.1E-2, WN1 2.85E-2, WN2 3.67E-2, WN3 8.7E-3, WN4 2.36E-5, WN5 2.2E-2, WN13 6.2E-4, WN6/7/8 catastrophic as documented (Phase B: QPSK/8PSK BCJR + 16QAM carrier recovery). Pre-fix baseline banked for comparison (scratchpad + closeout doc): AWGN WN0/4/5/6/7/8/13 all 0-error at 3M pre-fix too; Poor WN4 was 1.88E-5 pre-fix - the WN4 delta (claimed 7.04E-6 → de-rigged 2.36E-5) is the measured price of removing the rig-fitted heuristics, chiefly the channel-matched BCJR delay. **The evidence run also caught a receiver-killing acquisition bug** - at −1 dB a noise-corrupted WID can pass its checksum yet name (WN 0, UltraShort), which Table D-XXXVII does not define, and `TryReadPreamble` let `Get3k`'s exception escape the receive path (the actual cause of every historically "stuck" Poor WN0 run; on air, a daemon crash from unlucky noise). Fixed with `Has3k` pre-validation (ae3998c) and proven against the deterministic seed-500 reproducer, which now completes at the historic 7.7E-2. **Phase A is closed** - docs/ms110d/phase-a-closeout.md is the record; completion-roadmap.md superseded; README claims made exact. Full hermetic suite 541 pass / 0 fail / 42 env-gated skips; landed as PR #68.

### 2026-07-18 (later⁴) - differential + frequency-diversity bank is the BPSK default (reverses #5, per #40/#42)

Benchmarked our BPSK decode against GB7RDG's NinoTNC on the live 40 m channel (same off-air
audio; the NinoTNC's frames off MQTT as ground truth; `tools/Packet.SoundModem.NinoCompare`).
Over a busy 2-hour, 3-node window: a single differential modem copied **116/117** NinoTNC
frames; the **differential frequency-diversity bank (`BpskMultiModem`, pairs=4) copied 117/117
and decoded 2 more the NinoTNC missed** - 100 %, matching and slightly beating the reference.
Root finding (correcting #42): coherent's narrow Costas loop can't acquire the tens-of-Hz
offset real carriers arrive with inside a short (~150 ms) preamble; the bank sidesteps that,
and the diversity helps differential too in multi-signal conditions (a deep-dived beacon the
single modem missed decoded on an offset branch). So the library BPSK default flips
**coherent → differential** (`BpskModem`/`BpskDemodulator`/`BpskMultiModem`), and the daemon's
`bpsk300`/`bpsk1200` become the differential bank (offsetPairs/offsetStepHz tuneable;
offsetPairs:0 = single modem). Coherent stays a `--psk-detector` option; **QPSK keeps its
coherent default** (V.26A interop validated coherent, #5/#6). QtSM + loopback suites stay green
under the new default; a guard test locks it. The frequency shift for the ARDOP slot-2 bench
lives in **M0LTE.Dsp 0.1.1** (`FrequencyShifter`). Released as pdn-soundmodem 0.5.0.

### 2026-07-18 (later³) - the general convolutional codec folds into M0LTE.Fec 0.2.0

`Ms110d/Fec` was a mix: a general rate-1/2 tail-biting convolutional codec
(`ConvolutionalCode` / `TailBitingEncoder` / `TailBitingViterbiDecoder`) and the
MIL-STD-188-110D-specific puncture/interleaver tables. The general codec moved into
**M0LTE.Fec 0.2.0** (it belongs next to the block codes there); the 110D tables
(`Ms110dPuncture`, `Ms110dInterleaver`) stay in `Ms110d/Fec`. This repo bumps M0LTE.Fec to
0.2.0 and the Ms110d modulator/demodulator/framing/puncture (+ its test) now `using M0LTE.Fec;`
for the codec. Build clean, 397 pass / 31 skip.

### 2026-07-18 (later²) - settable audio centre for the narrow modes (issue #39)

The narrow modes' audio centre is now **variable per modem**, QtSoundModem-style, on both TX and
RX - `--modem N:MODE:FREQ` (already the CLI shape) and config `"frequency"` now reach every
variable-centre mode. Covers the AFSK tone-pair modes (afsk*, centre = mark/space midpoint,
default 1700) and the BPSK/QPSK carrier modes (bpsk*/qpsk*, default 1500; 1650 for qpsk3600). The
GB7RDG signal that sat ~41 Hz off our fixed 1500 (the finding behind #39/#40) is now correctable in
the field.

- **Real bug found completing the plumbing:** all three AFSK1200 modems (`Afsk1200Modem`,
  `Afsk1200Il2pModem`, `Afsk1200MultiModem`) constructed their `AfskModulator` with the hardcoded
  Bell-202 1200/2200 tones, so **TX ignored `centerFrequency`** (only the demodulator honoured it) -
  a mistuned own-transmission at any non-default centre. Fixed to `centre ± 500` so both sides move
  together (`Afsk300Modem` was already correct - it was the reference). Identical output at the
  1700 default; only the previously-broken off-centre path changes.
- The PSK factories `BpskModem.Bpsk300/Bpsk1200` and `QpskModem.Qpsk600/2400/3600` gained an
  appended `carrierFrequency` param (append-only - the private ctor always took the carrier; the
  positional NinoBench callers are undisturbed). `Program.cs` passes `frequency ?? default` through.
- **Fixed-centre modes now reject a `:FREQ` at start-up** rather than silently ignoring it: the
  baseband FSK families (fsk*/c4fsk*, DC-to-Nyquist, no audio centre) and the spec-fixed waveforms
  (freedv-*/ms110d-*, POCSAG, ARDOP). Guard covers both the CLI and config paths.
- `Modems/CentreFrequencyTests.cs` (14 cases): every variable-centre mode round-trips a frame at a
  shifted centre; the PSK carrier modes additionally must NOT decode at the default centre (proving
  the override genuinely moves the signal - the AFSK tone modes are deliberately offset-tolerant, so
  that stricter check is PSK-only). Verified end-to-end on the real NinoTNC bpsk300 recording:
  `--modem 0:bpsk300:1500` → 4 frames, `:1200` → 0. README / soundmodem.example.json / DaemonConfig
  document the coverage. roadmap #39 marked RESOLVED.

### 2026-07-18 (later) - POCSAG codec lifted into M0LTE.Pocsag; consume it

The POCSAG paging **codec** (`PocsagCodeword/Encoder/Decoder/Message/Page`, + a bundled copy
of the own-code `BitDpll`) was extracted into the standalone **M0LTE.Pocsag** package (AGPL,
spec-first CCIR RPC No.1; depends on M0LTE.Dsp). This repo now consumes it and keeps only the
daemon glue: `Pocsag/PagingTcpServer.cs` (bound to `SoundModemChannel`) and its integration
test, both switched to `using M0LTE.Pocsag;` - as did `Program.cs` and the `sm-pocsag` tool.
The moved codec source + unit tests were deleted; the multimon-ng runner the paging test
needs was split out into `tests/…/Pocsag/MultimonNg.cs`. `Modems/BitDpll.cs` stays (the
modems use it). Build clean, 371 pass / 31 skip.

### 2026-07-18 - consume M0LTE.Dsp / M0LTE.FecLdpc / M0LTE.Ofdm; drop the duplicated source

Third extraction flip (after Flex, then Fec/Il2p/Ardop): the **DSP primitives**, the **LDPC
codec** and the whole **OFDM modem** were each lifted into their own repos/packages -
**M0LTE.Dsp**, **M0LTE.FecLdpc**, **M0LTE.Ofdm** (all published **0.1.0** to nuget.org). This
repo now **consumes all three** instead of carrying the code:

- Deleted `Dsp/{Fft,FirFilter,FilterDesign,Decimator,Upsampler,SpectrumSource}` (**kept
  `Dsp/ConstellationSource.cs`** - it depends on `Modems.IConstellationSource`, so it stayed
  out of the package; it now takes `using M0LTE.Dsp;` for `Fft`), all of `Fec/Ldpc/` (7 files -
  this supersedes the previous entry's "kept `Fec/Ldpc` in-repo": it is now the
  `M0LTE.FecLdpc` package), and all of `Ofdm/` (12 files, including `Cf.cs` - `Cf` now lives in
  `M0LTE.Ofdm`). Added `PackageReference`s to M0LTE.Dsp/FecLdpc/Ofdm (all 0.1.0).
- Swapped `using Packet.SoundModem.{Dsp,Ofdm,Fec.Ldpc}` and `Packet.SoundModem.Tests.Dsp`
  (where the `OccupiedBandwidth` helper lived) to the `M0LTE.{Dsp,Ofdm,FecLdpc}` equivalents
  across src/tests/tools; `Ms110d/*` + `Modems/FreeDvDatacModem` (the `Cf` consumers) and the
  `Modems`/`Channel`/`Pocsag` FFT/filter users came along. `SoundModemChannel` keeps both
  `using M0LTE.Dsp;` (`SpectrumSource`) and `using Packet.SoundModem.Dsp;` (`ConstellationSource`).
- Deleted the moved unit tests (`Dsp/{DecimatorTests,SpectrumTests}` + the `OccupiedBandwidth`
  helper, all of `Fec/Ldpc/`, all of `Ofdm/` - pure tests of the moved types, now living and
  passing in the package repos). **Kept + reused** the tests that exercise types that stayed:
  `Dsp/{UpsamplerTests,OccupiedBandwidthTests}` (modem + NinoTNC-fixture + `WavFile` cases),
  the OBW tests (`Pocsag`/`Ms110d`/`Ardop`, which use the package's `Fft`/`OccupiedBandwidth`),
  the Watterson-channel helper and `ConstellationTests`.
- Licences unchanged (this repo stays GPL-3.0). Out-of-solution generators `tools/oracle` and
  `tools/gen-ldpc-tables` were left as-is (not in `pdn-soundmodem.slnx`; the LDPC-table
  generator is now the `M0LTE.FecLdpc` repo's concern).

Suite 407 pass / 31 skip. On branch `dsp-fecldpc-ofdm-to-nuget`.

### 2026-07-17 (later still²) - consume the M0LTE.* packages; drop the duplicated source

Extended the extraction: **Fec** (core RS/CRC/Hamming/interleaver), **Il2p** and **Ardop**
were each lifted into their own repos/packages alongside Flex, plus a shared
**M0LTE.Radio.Audio** package holding the `IAudioInput`/`IAudioOutput`/`IPttControl`/`NullPtt`
seam. This repo now **consumes all of them** instead of carrying the code:

- Deleted `Fec/{Crc16X25,FreedvCrc16,GpInterleaver,Hamming74,ReedSolomon}` (kept `Fec/Ldpc`,
  which is codec2/LGPL and stays), all of `Il2p/` and all of `Ardop/`, and the moved unit
  tests. Added `PackageReference`s to M0LTE.Fec/Il2p/Ardop/Radio.Audio (all 0.1.0) and bumped
  M0LTE.Flex to 0.2.0 (it now consumes Radio.Audio too).
- The `Packet.SoundModem.Channel` audio/PTT **interfaces moved to M0LTE.Radio.Audio**; the
  ALSA/serial/CM108 impls and `SoundModemChannel` now implement the package's interfaces
  (`Channel/IAudioInput.cs` etc. → `AlsaAudioInput.cs`/`AlsaAudioOutput.cs`/`SerialPtt.cs`).
  With Flex 0.2.0's types implementing the same seam, the Flex adapters were deleted and
  `FlexDevice` uses the Flex audio directly.
- The daemon's `ArdopHostServer.ForChannel` (removed from the package as soundmodem-specific)
  is now inline glue over the package's public `ArdopHostTnc`/`ForAudio` seam.
- Kept in-repo: `Fec/Ldpc`, the `Ofdm`/`Ms110d` modems (LGPL/spec, not extracted), and the
  Ardop OBW + ardopcf-live tests (they use this repo's `Fft`/`OccupiedBandwidth` + external
  ardopcf). Licences unchanged (this repo stays GPL-3.0; see the earlier entry re AGPL §13).

Suite 550 pass / 31 skip (the ~360 moved unit tests now live and pass in the package repos).
On branch `flex-to-nuget-package`.

### 2026-07-17 (later still) - FlexRadio client lifted out into the M0LTE.Flex NuGet package

The whole FlexRadio client (session/discovery/VITA-49/DAX/station/PTT + mock) was extracted
from this repo into its own standalone repo and package - **`M0LTE.Flex`** (AGPL-3.0-or-later,
github.com/M0LTE/M0LTE.Flex), published **0.1.0** to nuget.org via Trusted Publishing, with a
build-time public-API lock and a SemVer policy. It was clean to lift: the code was MIT-derived
(KC2G nDAX/nCAT/flexclient, HB9FXQ flexlib-go) with near-zero coupling - only the tiny
audio/PTT seams. This repo now **consumes the package** instead of carrying the code:
`src/Packet.SoundModem/FlexRadio/` keeps only the daemon glue (`FlexDevice` - the `flex:`
device-string parse + bring-up) plus three thin adapters (`FlexAudioAdapters.cs`) that
re-present the package's `M0LTE.Flex.IAudioInput/IAudioOutput/IPttControl` through this modem's
`Packet.SoundModem.Channel` seams. The 9 client files + 7 client tests were deleted; the 3
glue/loop tests stay. **Licence note:** M0LTE.Flex is AGPL-3.0; adding it to the GPL-3.0 core
is permitted by GPLv3 §13, which carries AGPL §13 (network-source) onto the combined work -
Tom signed off. Full suite green (913 pass / 97 skip). Behaviour unchanged; `flex:mock` and the
byte-exact modem-loop-through-mock both still pass through the package.

### 2026-07-17 (later) - FlexRadio client: offline Phases 0-2 land (session/DAX/PTT + mock)

PR #37: the pure-managed FlexRadio 6000-series client (design PR #32, Route A) - `--device
flex:<radio>[:slice]` makes a Flex the daemon's sound-card + PTT over the LAN, all modes via
the shared channel path, no PulseAudio/FlexLib (MIT Go refs, provenance headers). Phase 0
session/discovery/VITA-49 with byte-exact vectors; Phase 1 the `IAudioInput` refactor +
Flex RX/TX/PTT + `--device flex:` + a mock radio (`flex:mock` runs the whole daemon
hardware-free); Phase 2 the decisive byte-exact modem-loop-through-mock (afsk1200 reduced-bw +
freedv-datac3 full-bw). A datac3 loop flaky under real UDP loopback loss was caught by
independent re-verification and fixed with a lossless in-process mock transport (real
audio/reorder/rate-bridge code + byte-exact assertion untouched; reorder-ring tests added);
independent 10× isolation 10/10 green. Suite 878→925. Real-DAX UDP loss is a Phase-3 hardware
measurement. **Remaining: Phase 3 hardware smoke on Tom's 6500 into a dummy load** (discover →
DAX stream → PTT/interlock → latency/txdelay floor), then the HF-loop Flex variant.

### 2026-07-17 (later) - MIL-STD-188-110D App D Phase A lands: 3 kHz waveform, mask-gated

PR #34: the App D 3 kHz serial-tone waveform (Walsh-75/BPSK/QPSK) - pure-managed C#, built on
the dual-verified tables and critique-folded design. No open App D implementation exists, so a
from-scratch Watterson/CCIR channel simulator gated against the spec's D-LXIV SNR masks is the
acceptance instrument. **Independently re-verified at full budget (3M bits/point): all 12 gated
points 0 errors** - AWGN WN0-6+13 at their mask SNRs, Doppler ±75 Hz, Static WID2 0/3/9 ms @
+9 dB. Two late failures were root-caused (off-cursor DFE taps fitting noise in the K=48 class;
MMSE-ridge fix scoped so the eight green modes stay byte-identical) not fudged - and an earlier
CFO-trim sign error was caught by the masks after passing hermetically (the discipline earning
its keep). The Static WID2 5 dB figure was a house bar (spec's static SNR untranscribed, D.6.3
"Not yet standardized"), honestly restated to +9 dB with the remaining margin assigned to Phase
B RLS. OBW ~2.89 kHz about 1800 Hz. Built across a Fable→Opus handoff (Fable's spend limit hit
mid-build; Opus picked up the checkpointed branch with no loss). Suite 733→878; the one flaky
failure is the pre-existing ARDOP TCP race (issue #33). Phase A = a 110D waveform that
previously ran only on RapidM/Rockwell hardware now decoding on a soundcard. Next: Phase B
(8PSK/16QAM + RLS DFE) or another roadmap thread per Tom.

### 2026-07-17 (later) - 110D ledger cleared: every constant dual-verified; Phase A unblocked

PR #31: the design's 13-row transcription-debt ledger is cleared - all remaining Appendix D
figures/tables (K7/K9 encoder figures, PSK transcoding, U/K geometry, preamble tables, both
256-digit PN arrays, probe bases, the 3 kHz interleaver set, the worked example) transcribed
twice independently and value-diffed with **zero conflicts**, including full agreement on all
512 PN digits (which have no structural oracle - the dual read IS their verification). Ledger
errata applied (D-XXV numbering, Walsh-prose location, D-XIV settled 10→0044/11→0440, L8
correction). Operational note: the first transcriber-A run was killed after an hour of
in-context triple-reading with nothing written (32 MB transcript, zero files - a digit-fidelity
risk under compaction); the fresh run under write-immediately/checkpoint discipline finished in
18 minutes - the discipline is now part of the standing sub-agent policy. **No 110D constant
remains provisional; the Phase A build starts now** (3 kHz framing + Walsh-75/BPSK/QPSK + LMS
DFE per design §6, Tom's §10 decisions folded).

### 2026-07-17 - ARDOP Phase D: host interface + Pat - the ARDOP stack is software-complete

PR #30: the ardopcf-compatible virtual-TNC host interface (command/reply/notification formats
byte-for-byte, quirks preserved), command+data sockets, RXO monitor mode, and daemon
integration (`--ardop`, dedicated-channel policy). Validation: a 107-command conformance
script **byte-identical** vs live ardopcf (VERSION excluded by design); **real Pat v1.0.0
delivered a full B2F message through our daemon** to ardopcf; scripted full-stack sessions
both roles byte-exact (sequences pinned from wl2k-go's transport source); live RXO monitored a
third-party ardopcf↔ardopcf session (25 frames, all data + ACKs). Hermetic suite 723→741
(verifier 733/0); five live legs green in one run. With Phases A-D merged the ARDOP stack is
**software-complete at ardopcf parity** (waveforms 0 dB knee delta, ARQ live both roles, host
interface Pat-proven); the only remaining item is the on-air acceptance from GB7RDG's HF port
on the 40m UK packet channel (task #6). Next build thread: MIL-STD-188-110D App D Phase A on
the landed design (ledger figures to dual-transcribe first).

### 2026-07-16 (later still¹⁴) - ARDOP Phase C: PSK/16QAM at ardopcf parity, 0 dB knee delta

PR #29: differential 4PSK/8PSK + 16QAM TX+RX on 1/2/4/8 parallel carriers; FSK-only ARQ guard
removed - full gearshift ladders. Tom's all-in bar met and exceeded: **noise knees
trial-identical to ardopcf at every swept point (0 dB delta; bar was ~1 dB)**; 59/59
payload-exact both directions across every PSK/QAM type/bandwidth (+offset+noise rows); TX
within 2 LSB of ardopcf's own --writetxwav; **live mixed-mode ARQ both roles, 4 KB byte-exact
each way, 0 NAKs**, ladders climbing to 4PSK/8PSK.2000; OBW never wider (17/17 rows). Honest
corners recorded: the 2000 Hz quality-85 top-out is ardopcf-parity (verified in their decoder);
16QAM.2000 proven by fixtures/knees/OBW, not live sessions (same on both implementations);
knees AWGN-only per the design. Hermetic suite 610→723 (715 verifier-env), env-enabled 795/0.
Remaining: Phase D - the 8515/8516 host protocol + daemon integration + Pat, then the
GB7RDG/40m on-air acceptance.

### 2026-07-16 (later still¹³) - 110D App D design doc: implementation-ready, critique-folded

PR #28: docs/ms110d/design.md (52 k chars) - the App D 3 kHz waveform design on the
dual-verified tables, produced by a 3-section → adversarial-critique → assemble workflow run
in parallel with ARDOP Phase C (Tom: "the box isn't that busy"). All 12 critique findings
folded, none deferred; the provenance BLOCKER resolved with real forensics (everyspec stamps
each download's PDF trailer /ID - the doc pins the permanent PDF ID + a stamp-invariant
SHA-256; README corrected). K=9 polys corroborated against the published (561,753)
max-free-distance code; the interleaver direction pinned by a wire-side worked-example test;
a 13-row transcription-debt ledger gates encoder code on formally-transcribed figures; the
no-oracle validation ladder gets a loopback-blind checklist (L1-L12) + a statistical budget
vs the transcribed D-LXIV/LXV masks. Native rate 9600 Hz; phasing A (Walsh/BPSK/QPSK + LMS
DFE) / B (8PSK/16QAM + RLS) / C (high QAM, groundwave-gated). §10 = three open questions for
Tom. Build remains sequenced after ARDOP.

### 2026-07-16 (later still¹²) - ARDOP Phase B: the ARQ engine, live sessions vs ardopcf

PR #27: the ISS/IRS ARQ session engine (the design's named riskiest block), ported
behaviourally from ardopcf with a virtual-clock architecture (no wall time in the engine →
hermetic sessions ~100× real time; the live path is the same code on the audio clock).
**Live ARDOP sessions ours↔ardopcf over snd-aloop, both roles, green twice** - byte-exact
transfers, orderly teardown (ardopcf's END-session-ID quirk live-confirmed and ported as-is).
Hermetic: exactly-once data with **measured ≥775 ms ACK margin** in the 1500 ms window;
NAK/repeat; Memory-ARQ recovering from two individually-undecodable copies; gearshift;
AUTOBREAK; timeouts - real counts throughout, + 42 pure-logic tests. Suite 557→610 hermetic
(618 with the env-gated oracle/aloop legs), 0 failures. Remaining: Phase C (PSK/16QAM
RX-first, Tom's all-in 16QAM bar), Phase D (8515/8516 host protocol + Pat + the GB7RDG/40m
on-air acceptance per task #6 notes).

### 2026-07-16 (later still¹¹) - ARDOP Phase A: 4FSK codec + FEC mode, 33/33 vs ardopcf

PR #26: the ARDOP 4FSK layer lands (design §6 Phase A, ported from ardopcf with provenance;
600 Bd FM modes folded in per Tom). Cross-validated both directions against ardopcf itself:
**ardopcf→us 33/33** fixtures (payload-exact incl. ±40/±80 Hz and noise variants), **us→ardopcf
33/33** via --decodewav (hex-exact data). CRC-16/CRC-8/RS byte-exact against vectors from
ardopcf's compiled sources; **OBW equal to ardopcf's to the FFT bin** per bandwidth class
(never-wider rule). One design-doc correction found by implementation: ARDOP's RS wire layout
is byte-reversed vs FX.25's (same field/generator) - mapped and proven, documented. Memory-ARQ
averaging included. Suite 419→557. Also landed this cycle: the ARDOP spec as self-contained
Markdown (PR #25, docs/refs/ardop-spec-rev2.md - 15 internal spec inconsistencies flagged).
Next: Phase B, the ARQ engine (both-ends-FSKONLY), the design's named riskiest block.

### 2026-07-16 (later still¹⁰) - MIL-STD-188-110D App D tables: dual-transcribed, zero conflicts

PR #24: the image-only interop-critical tables of 110D Appendix D (the public counterpart of
the RESTRICTED STANAG 5069 - task #7) land in docs/ms110d/, transcribed **twice independently**
(branches ms110d-tables-a/-b, agents forbidden from cross-consulting, per the verified
scoping's demand) and diffed: six of ten files byte-identical (incl. all four constellation
tables), four differing only in formatting - **zero value conflicts**, plus machine self-checks
(constellation symmetry/lattice, puncture ones-counts reproducing code rates, the scrambler
regenerating the printed sequence exactly). Source PDF SHA-256 + method in the README; the -a
branch retained as the independent record. Structural findings: D-VII…D-X are the
16/32/64/256-QAM coordinate tables (PSK uses transcoding tables D-III…D-VI); puncture patterns
are the separate Table D-L. Spec oddities recorded (length-68 mini-probe; a 40 kHz interleaver
table with no 40 kHz bandwidth; "Not yet standardized." acquisition section). Next for task #7:
the App D design doc on these verified values; build sequenced after ARDOP.

### 2026-07-16 (later still⁹) - ARDOP design/scoping lands

PR #23: [ardop-design.md](ardop-design.md) - the FreeDV-style de-risking pass for the open
Winlink path, grounded in ardopcf (@a7c92289, v1.0.4.1.3, MIT - verified; port-from-ardopcf
recommended over clean-room, the spec lacking implementation detail e.g. the nonstandard CRC).
Headlines: exactly one interoperable ARDOP (spec Rev 2.0, 2017; G8BPQ "ARDOP 2" is
OTA-incompatible, out of scope); 18 data modes + ~15 control frames over 200/500/1000/2000 Hz -
4FSK / differential 4-8PSK / 16QAM on 1-8 parallel 100 Bd tone carriers (NOT IFFT OFDM); FEC =
RS + repeats + Memory-ARQ (our ReedSolomon is a direct hit - FX.25's GF config); ≈6-8 k lines
C#. Riskiest: the ARQ timing machine (ACK on-air inside the ISS's 1.5-2.1 s window) and
PSK/16QAM demod robustness. Host interface: byte-compatible ardopcf TCP (8515/8516) so Pat
works unmodified. ardopcf proven as a fully-offline oracle on this box (--decodewav + null-dev
TX vectors, both measured). Phasing: A) 4FSK codec + FEC mode → B) ARQ (both-ends-FSKONLY) →
C) PSK/16QAM RX-first → D) host interface + Pat + bench. OBW: never-wider-than-ardopcf per
bandwidth class. §10 holds the open questions for Tom (bench/gateway logistics, 16QAM bar,
600 Bd FM / RXO scope).

### 2026-07-16 (later still⁸) - POCSAG lands: spec-first paging + the daemon paging endpoint

PR #22: POCSAG (roadmap easy win) implemented spec-first from CCIR RPC No.1 - layout proven by
reproducing the published sync/idle constants from their own data bits; BCH(31,21) exhaustively
verified (all 1/2-bit patterns corrected, all 4960 3-bit patterns rejected). Cross-validated
against multimon-ng: 9/9 pages exact across RIC edge cases, functions, charsets and all three
bauds; polarity pinned to the spec convention. **Interface (Tom's call): no KISS** (pages are
not AX.25 frames; one-way medium) - instead a daemon `--paging <port>` TCP line protocol
(`PAGE <ric> <func> ALPHA|NUMERIC|TONE [text]` → CSMA/PTT TX) with heard pages broadcast as
`HEARD …` on the same socket; a DAPNET-core transmitter client is an explicit non-goal for now.
Internal plumbing added two clean channel seams (a generalised audio TX queue entry +
an RX tap) rather than abusing IModem/KISS. Found + recorded an upstream bug: DAPNET
UniPager's crc() off-by-one cannot reproduce the sync word (PROVENANCE.md). OBW pinned by
absolute bounds (~691 Hz baseband at 1200 bd; no reference recording exists). Suite → 419.
Follow-ups noted: first off-air 439.9875 MHz capture when taken; DAPNET client if ever wanted.

### 2026-07-16 (later still⁷) - Phase 2: all six datac modes complete

PR #21: RX for the narrow modes (datac4/13/14) - the RX band-pass filter
(`find_carrier_centre` float-summation centres 1468.75/1500/1472.22 Hz; the existing
`quisk_ccfFilter` port applied per nin-batch at the rxbuf tail, state persistent across burst
resets) plus mode wiring; the LDPC shortening path already existed. Measured byte-exact both
directions vs stock codec2 tooling (codec2→ours 2/2, 2/2, 5/5 clean and through +22 Hz /
~5.5 dB; ours→codec2 12/12 interop tests), round-trips and IModem green, TX oracle extended to
all six modes (xcorr 1.0). **The OBW rule now covers all six modes** - datac4 (300.8 Hz) and
datac13 (265.6 Hz) measure exactly equal to FreeDV's own vectors. Suite → 378. The FreeDV datac
family is code-complete: six modes, TX+RX, KISS-integrated (`freedv-datac0/1/3/4/13/14`),
CI-guarded OBW, stock-tooling interop in both directions. Remaining: the HF radio loop
(task #4's proven-reliable gate - needs the bench), streaming-mode acquisition for the narrow
modes (unneeded for the burst-mode deployments), low-SNR/multipath characterisation.

### 2026-07-16 (later still⁶) - FreeDV datac as KISS modes: IL2P+CRC on the FreeDV waveform

PR #20: `freedv-datac0/1/3` land as daemon KISS modes on the 48 kHz DSP path (integer ÷6/×6
bridge to the modem's native 8 kHz). **Framing (Tom's call, two iterations): the datac payloads
carry the family-standard IL2P+CRC bit stream** - no invented wrapper (a 2-byte length prefix
and HDLC-in-payload were both considered and rejected; AX.25 itself has no length field, and
the family already solves variable-length-in-fixed-container with IL2P's sync word + byte
count). Frames span packet boundaries, so datac0's 14-byte packets carry full AX.25 frames and
datac1 has no hard cap; `FrameQuality` is the family's real RS/CRC one; the RS layer is largely
dormant today (the datac transport delivers clean-or-missing) but enables a future
salvage-from-CRC-failed-packets path. Waveform untouched - the OBW rule (never wider than
FreeDV's own, PR #18) stays CI-green. Measured: exact IModem round-trips datac0 30/60 B,
datac3 60/124 B, **datac1 60/508/1000 B (first datac1 end-to-end validation; 13.1 s of audio
demodulated in 877 ms)**; back-to-back bursts 2/2; daemon `--wav` smoke green. One opt-in
(default-off) pdn extension for variable-length KISS bursts (`EndOfBurstUwDrop` + CRC
backstop). Suite → 355. Remaining for task #4: real HF-loop validation (burst DCD is ~1 frame
late - EnergyBusyDetector is the CSMA source; datac1's short UW leaves ~10 %/burst odds of a
~4 s phantom-DCD tail; CSMA interaction unmeasured on air - coexistence with regular
FreeDV voice is a NON-goal per Tom: data and voice never share a channel in practice).
Phase 2 (datac4/13/14 RX BPF)
unchanged.

### 2026-07-16 (later still⁵) - burst acquisition: the real-world FreeDV interop loop closes

Burst-mode acquisition lands (PR #19), on top of the Phase-1 modem (PR #17) and the OBW rule
(PR #18 - **our datac TX must never exceed FreeDV's own OBW**, CI-enforced like-for-like against
codec2's checked-in transmissions; standing directive). The standard FreeDV CLI tools and
FreeDATA force burst mode, so this is the path real deployments use: the known-sequence
preamble/postamble correlator (`est_timing_and_freq`), `ofdm_sync_search_burst` with postamble
packet-rewind, and the data-burst state machine - the validated demod core reused untouched.
Measured: codec2 CLI TX → our RX datac0 **5/5 clean and 5/5 at +22 Hz / ~4.6 dB SNR**; **our TX →
codec2's own `freedv_data_raw_rx` 5/5** (the full CLI loop, kept as a Category=Interop test);
round-trips 10/10; the noise knee matches codec2 (19/20 = 19/20 on identical audio); the one
found corner (fully-blanked preamble, single-packet burst) is unrecoverable in codec2 too (0/49
on their own RX) - parity, not a defect. Suite 329→338. The pure-managed datac0/datac3 modem now
interoperates with stock FreeDV tooling in both directions. Remaining: datac1 end-to-end burst,
Phase 2 modes (RX BPF), IModem/KISS (task #4).

### 2026-07-16 (later still⁴) - FreeDV OFDM Phase-1: FEC + engine ported, validated vs codec2

The FreeDV datac OFDM modem built on branch `freedv-ofdm-phase1` as a pure-managed C# port of
codec2 1.2.0 (git 310777b, LGPL-2.1), validated against libcodec2 as a **test-only oracle** (no
runtime native dependency; reference vectors checked into `samples/freedv/`). Design:
[ofdm-design.md](ofdm-design.md); provenance: PROVENANCE.md `Fec/Ldpc` + `Ofdm` rows; R-1
(licence review) is a roadmap task.

- **FEC layer - bit-exact.** LDPC matrices transliterated (`tools/gen-ldpc-tables/gen.py`); the
  phi0 table, RA encoder and sum-product decoder reproduce codec2's **own built-in decode
  vectors bit-for-bit** (all 4 codes that ship one); the frame codec (shortening) round-trips
  all six datac modes. Golden-prime interleaver + CRC-16 (pinned to 0x29B1) alongside.
- **Modulator** (parallel sub-agent, `freedv-ofdm-modulator`) - direct IDFT + CP (not an FFT -
  datac14's M=144 forbids it), symbol assembly, pilots/UW, the Hilbert-clipper/BPF chain, LCG
  preamble. Vs codec2: **xcorr = 1.0 (8 d.p.) all six modes**, ≤1.5 LSB, datac14 preamble
  bit-for-bit; the residual is codec2's own float→int16 truncation.
- **Demodulator + streaming sync** (parallel sub-agent, `freedv-ofdm-demod`) - timing/frequency
  acquisition, pilot channel estimation, LLR demap, sync state machine. Decodes codec2's own
  datac0 TX: **10/10 clean, 10/10 at +45 Hz offset, 10/10 at ±600 ppm sample-clock, 19/20 AWGN**
  (matching codec2); datac3 4/4 (mode-generic).
- Both halves **merged** (reconciled the shared `Cf` complex type); build clean, **suite 218→319
  green**. The two big DSP halves were built by parallel background sub-agents in isolated
  worktrees, each validated against codec2 independently (context-preservation + parallelism).

**datac0 first-light ACHIEVED.** `DatacTransmitter` ports the full TX chain
(`freedv_rawdatacomptx`→`ofdm_ldpc_interleave_tx`: payload → CRC → LDPC encode → QPSK-map →
interleave → assemble UW → modulate); the our-TX→our-RX round-trip decodes datac0 **10/10 clean,
10/10 at +25 Hz offset, 10/10 at ±600 ppm sample-clock**, datac3 3/3 (mode-generic) - and the
transmitter output matches codec2's own datac0 TX to **0.75 LSB / xcorr = 1.0**, the 16-byte
frame **byte-identical**. No TX↔RX boundary fix was needed (the two independently-built halves
agreed first pass). Suite **319→325**. So the pure-managed datac0 modem is proven equivalent to
codec2 on both TX and RX and interoperates end-to-end.

Remaining for Phase 1: the burst/preamble acquisition path (needed for the standard FreeDV CLI
tools, which force burst mode) and datac1 end-to-end. Phase 2: datac4/13/14 (RX BPF + LDPC
puncturing). Phase 3 (task #4): IModem/KISS + the 12k/48k→8k rate bridge.

### 2026-07-16 (later still³) - next-wave modem roadmap + FreeDV OFDM Phase-1 design

Two planning docs land ahead of the next build wave. [waveform-roadmap.md](waveform-roadmap.md)
ranks the candidate modems after two research sweeps (FreeDV/Codec2 OFDM internals + a full
landscape survey) and a verified scoping of MIL-STD-188-110D App D: build order **FreeDV OFDM
datac → POCSAG → ARDOP → MIL-STD-188-110D App D (3 kHz) → own FM OFDM → own HF OFDM**, with the
cannot-implement (VARA/PACTOR/P25/…) and label-only (APRS, CubeSat 9k6) sets, M17 parked, and the
compatibility-labelling rule up front. [ofdm-design.md](ofdm-design.md) is the implementation-ready
Phase-1 design for the lead item: a **pure-managed C# port of the FreeDV datac OFDM modes validated
bit-for-bit against `libcodec2` as a test-only oracle** (not a P/Invoke wrap - the port builds the
shared OFDM sync engine our own FM/HF modes reuse). Six QPSK modes @ 8 kHz/1500 Hz; phasing
datac0→datac1→datac3; OBW CI-enforced per mode; the sync/channel-estimation state machine + the
sample-clock and datac4/13-shortening bit-exactness are the flagged risks. The MIL-STD-188-110D App
D redirect (of the RESTRICTED STANAG 5069 that G4KLX advocated) is public/verified-downloadable but
gated on its no-oracle validation risk and sequenced after FreeDV. Design docs produced by a
research/design workflow; the final synthesis was assembled by hand (multi-agent synthesis failed on
prompt size - the six component designs are preserved verbatim). No code yet - next is the Phase-1
build.

### 2026-07-16 (later still²) - QtSM matrix re-measured under coherent; #6/#10/#11 resolved on evidence

The coherent detector default (#5) invalidated the **qtsm→ours** half of the QtSoundModem
matrix (docs/qtsm-loop.md), which PR #8 had measured under differential detection. Re-stood
the snd-aloop rig (QtSM 0.0.0.76) and re-measured, capturing QtSM's per-mode TX off the cable
into `samples/qtsm/*.wav` and decoding with our modems on the coherent default. **Every
qtsm→ours PSK leg is now 10/10** (afsk1200, bpsk300, bpsk1200, qpsk600, qpsk2400, qpsk3600).
ours→QtSM is receiver-in-QtSM, so coherent-independent; re-confirmed the changed cells + a
control on the artifact-free continuous-WAV method. **Nine of ten pairings interoperate both
ways**; the lone marginal leg is qpsk600 ours→QtSM.

The three open interop issues, resolved on the fresh data (evidence-based comments posted; not
closed unilaterally, per Tom):

- **#11 (qpsk600 marginal)** - *half-resolved.* The qtsm→ours leg is **10/10 under coherent**
  (the differential-era 9/10 was live-path variance - a clean deterministic WAV decode reads
  10/10 on both detectors). The residual is **ours→QtSM 8/10**: QtSM's narrow V26A-600 receiver
  loses a frame or two of our TX. That is receiver-side in QtSM - our `qpsk600` TX is
  NinoTNC-proven (mode 9, 10/10) and stays exactly as-is (widening it to suit QtSM would trade
  away NinoTNC OBW compliance). Characterised, not our defect.
- **#10 (fsk4800-il2p one-way)** - *resolved: the 0/10 did not reproduce.* Under current code
  QtSM's Dire-Wolf RUH-4800 receiver decodes our 4800 GFSK TX **10/10** (reproduced twice -
  committed `samples/pdn` mode-04 and a fresh WAV - with QtSM's own RUH-4800 TX decoding in the
  same setup as a control; QtSM headless-RUH `using48000` patch applied). Timeline rules out a
  stale sample (the FskModem tail-flush acquisition fix landed ~4 h *before* the original 0/10
  measurement). **No change to our 4800 modem** - it is NinoTNC-derived and stays so; it simply
  also cross-validates against QtSM's RUH-4800 now. So 4800 GFSK is bidirectional both with the
  NinoTNC and with Dire-Wolf/QtSM RUH.
- **#6 (qpsk2400 vs QtSM's 2400 maps)** - *confirmed characterisation under coherent.* Our
  V.26A `qpsk2400` decodes QtSM's V26A/DW2400 (type 12) **8/8** and its legacy "QPSK AX.25
  2400bd" (type 10) **0/8** - different phase maps, not a defect. Coherent does not change it.

Per Tom's mid-task directive, every mode is now **compatibility-labelled** (which peer it
interoperates with - universal / NinoTNC+QtSM-V26A / NinoTNC+DW-RUH / NinoTNC+MMDVM) in
docs/qtsm-loop.md § Results, README.md and samples/README.md; NinoTNC interop is never traded
for QtSM interop.

Landed alongside: **`QtsmInteropTests` (`Category=Interop`, 7 cases)** - decodes the checked-in
`samples/qtsm/` WAVs with our modems and asserts the frames (mirrors the NinoTNC/Dire-Wolf
reference-WAV tests); the live headless QtSM rig stays manual. Five new QtSM reference WAVs
checked in (trimmed). Tool reproducibility helpers (no wire/behaviour change, no modem touched,
so no PROVENANCE update, no ax25-ts leg): `sm-decode` gains `bpsk1200`/`qpsk600`/`fsk4800`/
`fsk4800-il2p` (and qpsk3600's loop bandwidth now matches its factory); `sm-samples` gains
`--only <mode>` and `--native-rate` (12 kHz TX for the QtSM rig). Suite 218 → 218 + 7 Interop.

### 2026-07-16 (later still) - coherent (Costas) detection is the PSK default (#5)

Flipped the BPSK/QPSK default from differential to **coherent** detection, matching the
NinoTNC - a `CostasLoop` recovers the carrier's absolute phase and the recovered absolute
symbols are differentially decoded (the wire format is differential and untouched: only the
receiver changes). The differential detector stays as a named opt-in (`PskDetector`
enum on both modems + factories; `--psk-detector coherent|differential` on the daemon).

Done under #5's explicit discipline - **measure, don't merge on theory.** Built coherent as
a selectable path with differential still default, then ran before/after noise + acquisition
sweeps; only after the numbers confirmed the gate did the default flip. Measured (our-TX to
our-RX, 40 trials/point): coherent beats differential on noise for **every** mode - decode
counts, e.g. qpsk2400 σ0.25 8→18, qpsk3600 σ0.15 11→25, bpsk1200 σ0.35 20→35, qpsk600 σ0.40
22→34, bpsk300 σ0.60 27→35 - the ~1-2 dB the theory predicts. Acquisition: coherent pulls in
within ~50-80 ms after idle (qpsk2400 50, qpsk3600 80, the rest 0), well inside the NinoTNC's
~100 ms; on a clean cold channel it acquires at 0 ms. The accepted trade (per #5): the
differential detector's 0 ms-after-idle acquisition and its wider frequency-offset tolerance.

Two measurement-driven tuning findings. (1) **Loop bandwidth is per-mode.** A single fraction
does not fit: bpsk300's carrier-offset pull-in needs ≥0.06×baud (its noise is flat, being
heavily oversampled), while qpsk3600 at 0.06×baud (108 Hz, 6⅔ samples/symbol, 0.25 roll-off)
tracks noise and loses even at low SNR (25/40 at σ0.08 where 0.03×baud scores 40/40). Default
is 0.06×baud; qpsk3600 overrides to 0.03×baud. (2) **The QPSK Costas detector nulls at the
diagonals**, so the recovered constellation locks to 45/135/225/315° - the quadrant decision
must index by 90° sector (floor), not nearest multiple, or the symbols sit on a decision
boundary and nothing decodes; the constant 45° lock offset washes out of the differential
decode. (First-light bug: caught and fixed by measurement, not reasoning.)

Tests migrated per #5's stated consequence - the acquisition parity tests changed meaning:
`Acquires_At_Txdelay_Zero_Like_A_NinoTNC` now covers only the non-PSK modes;
`Differential_Psk_Acquires_At_Txdelay_Zero` guards the opt-in's 0 ms property;
`Coherent_Psk_Acquires_After_Idle_Within_Ninotnc_Preamble` (100 ms) is the coherent "match
the NinoTNC" criterion; the idle-noise test moved to the differential opt-in. New
`CoherentDetectionTests` bake the noise-margin gate as a deterministic regression test. The
#9 constellation test now covers both detectors (differential product clusters tightest,
coherent absolute a little looser under loop jitter - both far above phase noise). Suite
201→218. Diagnostic/receiver-only change: no wire format, no named parse flag → no ax25-ts
leg. PROVENANCE updated (`CostasLoop` is a textbook loop implemented fresh; margins measured
in-project). Issue #5 closed on the evidence.

### 2026-07-16 (later) - constellation side channel: the per-symbol PSK diagnostic (#9)

Landed issue #9 - the per-symbol constellation / eye feed for the PSK modes, sequenced (by
Tom) immediately before #5 because it is #5's debugging surface. The PSK demodulators already
compute, at each symbol instant, the differential product they reduce to a decision and
discard (`re = i·delayedI + q·delayedQ`, `im = q·delayedI − i·delayedQ` for QPSK; the 1-D
`decision` for BPSK). That product **is** a constellation of phase-*changes* - exactly the
right artifact for a differential detector, clustering at the four dibit phases (QPSK) or the
two rails (BPSK). Exposed via a small `IConstellationSource { SymbolPlotted }` on `QpskModem`
and `BpskModem`; `ConstellationSource` mirrors `SpectrumSource` - batches points into
fixed-size scope frames (default 256 points, two signed bytes each, auto-ranged to the
frame's peak so cluster geometry reads independent of level; silent frames emit zeros). Wired
on `SoundModemChannel` via a new optional `constellationSink` (sub-channel, frame), attached
only to modems implementing the interface - the node-side seam, mirroring `spectrumSink`; no
daemon flag (spectrum has none either - the node consumes both over SSE).

Diagnostic-only: no wire format, no interop surface, no named parse flag, so no ax25-ts leg.
Seven tests (all green, suite 194→201): offset-invariant 4-fold phase coherence >0.9 on clean
qpsk2400/qpsk3600 loopbacks (measured 0.94/0.98 gating symbols within 60 % of burst peak -
the low-amplitude symbols carry real per-symbol phase noise and belong to the smear the
diagnostic reveals, so the "is the core tight?" assertion looks at the strong symbols),
BPSK's 1-D/bimodal geometry, the frame batching/auto-range/silence-floor encoding, and that
the channel wires PSK modems but leaves AFSK unwired. PROVENANCE updated (`ConstellationSource`
is original; the tap reuses existing demod arithmetic). Next: #5 (coherent detection).

### 2026-07-16 (night) - QtSoundModem matrix extended: 10 mode/pairings, 9 interoperate

Extended the QtSM cross-validation (docs/qtsm-loop.md) to five more shared modes, both
directions, reusing `qtsm-bench` + the rig recipe. New: **bpsk1200** (QtSM type 4, 10/10 both
ways), **qpsk600** (QtSM type 16 QPSK V26A 600bps - the V26A map again, 9/10 & 6/10),
**fsk9600** (QtSM type 19 RUH 9600(DW), 10/10 both ways), **fsk9600-il2p** (type 19 + IL2P,
10/10 both ways). **Nine of ten pairings interoperate cleanly both ways** across both rate
classes (12 kHz audio-band + 48 kHz RUH).

Two findings. (1) **`fsk4800-il2p` is one-way**: qtsm→ours 10/10 but ours→QtSM **0/10** - QtSM's
Dire-Wolf RUH-4800 receiver rejects our 4800 GFSK TX (which a NinoTNC decodes), even from the
clean 300 ms-preamble sample; our 4800 descends from the NinoTNC and, unlike our 9600, was never
Dire-Wolf-cross-validated. Evidence `samples/qtsm/qtsm-ruh4800.wav` + `samples/pdn` mode 04.
Raised as an issue; no change to our modem. (2) **QtSM's RUH modes don't run headless** without
a patch - its `using48000` flag (which opens the card at 48 kHz for RUH) is set only in the GUI
init path, so `nogui` RUH opened at 12 kHz and fed its 48 kHz demod garbage. A three-line patch
to QtSM's nogui worker (set `using48000` from the configured speeds before `InitSound`) fixes
it; applied to the local build, documented in docs/qtsm-loop.md § Rates. The RUH `ours→QtSM`
figures come from playing our pre-generated `samples/pdn` TX WAVs into QtSM, because the 48 kHz
aloop record-then-replay path is too lossy (documented).

### 2026-07-16 (later still) - QtSoundModem interop: cross-validated against the ancestor

Built **QtSoundModem** (G8BPQ, UZ7HO lineage - the modem ours descends from) from source and
cross-validated the two over an **snd-aloop** virtual cable - no sound card, no radios. QtSM
runs headless via its genuine `nogui` switch (`QCoreApplication`, `main.cpp:49`). Full recipe,
device strings and results in **docs/qtsm-loop.md**; committed driver
`tools/Packet.SoundModem.QtsmBench` (`qtsm-bench`, a pure KISS-TCP client that frames-in /
counts-out on both modems); QtSM's QPSK transmissions checked in under `samples/qtsm/`.

**Every mode tested interoperates both ways** (qtsm→ours live + ours→QtSM continuous-WAV, both
artifact-free): afsk1200, afsk1200-il2p, bpsk300, qpsk2400, qpsk3600 all 9-10/10 each way.

The headline finding - the QpskModulator doc-comment's "pairwise-negotiated phase map" caveat
made concrete: **our `qpsk2400` pairs with QtSM's V26A/DW2400 (ModemType 12), NOT its legacy
"QPSK AX.25 2400bd" (type 10) or V26B (type 14)** - ours is the V.26A map (as NinoTNC and Dire
Wolf use). `qpsk3600` matches QtSM's legacy type-9 (QtSM has no V26 at 3600). Proven offline:
`sm-decode` reads QtSM's type-12 QPSK 8/8 and its type-10 0/8 (samples/qtsm/). Raised as a
tracking issue.

Two rig lessons worth keeping (both in docs/qtsm-loop.md): QtSM's `soundChannel[ch]=0` means
**channel disabled** (it then neither TX nor RX while looking alive - the bring-up time-sink);
and every audio process here must run under **`sg audio`** (this login shell isn't in the
audio process-group despite `/etc/group`). A real daemon defect surfaced and was **fixed**:
`--capture-rate 12000` (DSP-rate == capture-rate) crashed on a factor-1 `Decimator`; the RX
loop now feeds captured samples straight through when the rates match (Program.cs). This is
what lets the daemon run at the aloop's native 12 kHz. Filed as an issue for the record.

### 2026-07-16 (later) - issue tracker cleared: #1-#4 closed on evidence

All four open issues resolved and closed. #2's fix is the structural one: the
never-wider-than-a-NinoTNC test now measures its reference **from the checked-in
recordings at test time** - whole burst, identical frame content, explicit sample rates
(a first attempt inferred rate from burst length and mis-measured 48 k as 12 k; the same
error class the test polices). All 9 modes pass including qpsk3600, whose "9 % wider"
reading died with the window mismatch (fairly: ours 1808 Hz vs its 1887 Hz). #1 closed -
shaping fixed + enforced, idle-gap behaviour characterised as the TNC's, mode-5 matched
RX filter demoted to optimisation-without-a-driver. #3 closed: modem floors measured and
parity-enforced; the daemon's 300 ms documented as a radio PTT-to-RF allowance with a
guidance table in ninotnc-loop.md. #4 closed: root causes fixed earlier; the one-word
flag-fill residual priced as an explicit trade (I/Q LPF 750 Hz → 10/10 on that case but
WA8LMF 472 → 410; default stays 650, ctor parameter for ports that know their peer).

### 2026-07-16 (night) - C4FSK lands: 15 of 15 NinoTNC modes

The last coverage gap closed. `C4fskModem` implements NinoTNC modes 1 (19200) and 3
(9600) - which turn out to be **MMDVM-TNC "Mode 2"** (G4KLX; Tom's pointer), inherited
wholesale: 0x77 preamble, outer-only 4-byte sync 0x5D57DF7F (deframer sync now
parameterised), then standard IL2P bytes on shaped 4-PAM (dibits 01/00/10/11 →
+3/+1/−1/−3). The format was cracked against ground-truth recordings captured on the rig
(known frames sent via serial, transmitted by the TNC, one symbol error in 316 at fixed
phase) before any implementation - and MMDVM-TNC's Mode2Defines.h then confirmed every
constant. Three 4-level lessons are recorded in docs/ninotnc-loop.md (the 0.55× RX filter
kills the eye; clock only from sign crossings; gate bits on energy or a 1-heavy sync
false-locks ~12k times per recording of silence). Live: us→NinoTNC 8/8 both modes at
first attempt, NinoTNC→us 6-7/8 (headroom tracked via parity tests). The C4FSK
aspiration criteria graduated to the parity suite the same day they became meetable -
the scoreboard is empty. Daemon + bench wired; packet.net transport follows with the
0.4.0 pin bump.

Same day, other threads: #635 delivered by subagent (FrameQuality → node metrics/API/log,
PR #636); hardware validation of the acquisition work (us→NinoTNC 20 ms everywhere, new
training preamble confirmed; nino→us at ITS 20 ms flag fill remains marginal on bare-HDLC
modes - on #4); Opus-period audit clean (five stale worktrees from the July 8-12 arc
removed, one already-merged branch confirmed landed via PR 588).

### 2026-07-16 (later still) - per-frame receive quality: FrameQuality surfaced end to end

Tom asked whether we get BER from the modems. Answer: the deframers have always computed
the honest version of it and every modem discarded it - `Il2pDecodeInfo` (RS corrected
symbols + CRC state) and the FX.25 corrected-byte count died in `(frame, _) =>` lambdas at
seven call sites. Now surfaced as `FrameQuality` (mode/branch, frame length,
CorrectedBytes, CrcValid, winning multi-decoder offset + emphasis), deliberately NOT named
"BER": true bit-error rate is unobservable from a receiver (errors inside a corrected byte
are invisible; frames beyond the correction budget never report). CorrectedBytes over
frame length is a floor on channel byte-error rate - zero on a clean link, persistently
non-zero = a link consuming its error budget before it starts dropping frames.

Plumbing: `IModem.FrameDecoded` event (all seven modems), `SoundModemChannel.
FrameReceivedWithQuality` (with sub-channel), and - for the standalone daemon - an
**opt-in** KISS extension: `--quality-frames` emits command **0x07 RxQuality** after each
data frame, same port nibble, compact JSON payload. A distinct command rather than a
synthetic data frame, deliberately: the NinoTNC's own habit of sending diagnostics as fake
`TNC>USB` data frames means every host needs a special case to avoid parsing phantom
traffic, and we're not exporting that problem. Off by default so unaware hosts never see
it. HDLC framings report CorrectedBytes = null - an FCS pass proves zero residual errors,
not an error count.

Found while testing: on a clean signal the multi-decoder bank's "winning branch" is
first-past-the-post among many successful branches, so its offset/emphasis is only
directionally meaningful for marginal signals - documented in the test.

PDN-side leg (attach FrameQuality to the node's per-frame metadata via
SoundModemFrameTransport, UI surfacing) needs the next package release; tracked in
packet.net.

### 2026-07-16 (later) - performance criteria as tests: parity floors + aspiration scoreboard

Tom proposed expressing the performance criteria as failing unit tests. Implemented as two
tiers rather than a permanently-red suite (red that never goes green trains people to
ignore red):

- **`NinoTncParityTests`** - criteria already met, asserted forever: every mode acquires
  at TXDELAY 0 from a cold receiver (10/10), fsk9600 classic at 10 ms (the NinoTNC's own
  floor for that mode), and qpsk2400 short-preamble acquisition after 4 s idle with 20 dB
  SNR noise. Red here = regression below reference hardware. The reference numbers are
  from the 2026-07-16 TNC↔TNC survey and cited in the test docs.
- **`NinoTncAspirationTests`** (`Category=Aspiration`) - criteria not yet met, expected
  red: currently the two C4FSK modes (1/3) lacking modems. CI runs the category in a
  separate `continue-on-error` step, so it is a visible scoreboard, not a broken build.
  Discipline in the class doc: a passing aspiration graduates to the parity suite; a
  stale one gets deleted with its reasoning recorded.

The discipline proved itself immediately: the idle-noise qpsk2400 criterion was written
as an aspiration and passed on first run - graduated to parity the same hour, and is now
a floor. Blocking suite: 186 green. Aspiration scoreboard: 2 red (C4FSK), by design.

### 2026-07-16 - RX acquisition: NinoTNC-floor parity (goal: match or better NinoTNC)

Tom set the goal after the NinoTNC↔NinoTNC TXDELAY sweep showed the reference hardware
acquiring from ONE 16-bit word of preamble in 13 of 15 modes, while our receiver needed
100-300 ms in several. Three root causes, found by instrumenting rather than theorising
(a diagnostic tap on the real demodulator; every claim below was observed, and two
plausible fixes that did nothing were removed again):

1. **TX truncated the pulse-shaping filter's tail** (FskModem): output stopped at
   bits×samplesPerBit, chopping the final ~5 bits - the IL2P CRC trailer - off the air.
   Whether the Hamming-coded trailer survived depended on payload, so it presented as the
   receiver deterministically dropping *specific contents* (4/10 at any TXDELAY) while a
   NinoTNC decoded the same audio 10/10. Same bug class as the Afsk300 BandLimit flush.
2. **The discriminator's power-normalisation floor (1e-12) manufactured full-scale garbage
   during the filter-fill transient** (~19 bits of near-zero power at every burst start),
   and the envelope trackers trained on it - slice midpoint measured at 0.65 against a
   real eye of [0.2, 0.65]. Floor raised to 1e-5 (-50 dB below nominal in-band power):
   sub-signal input now yields sub-eye output. This also fixed real off-air decoding:
   WA8LMF Track 2 single 426 → 472, multi-bank 983 → 986 (direwolf: 970).
3. **An all-flags TXDELAY fill trains a cold receiver poorly** (87.5 % one tone; the
   opposite tone appears as 1-bit excursions that barely emerge from the receive LPF -
   observed as periodic errors on every flag boundary for the first ~40 bits). Classic
   HDLC AFSK modes now precede the two opening flags with an NRZI-zeros training run
   (level change every bit), which is what the IL2P framer already did and why those
   modes never suffered. Pre-flag zeros cannot alias to a flag; NinoTNC interop with our
   flag preamble was already proven, re-verification of the new fill is pending hardware.

Negative result recorded in code: a cold-start envelope "warm-up" (both legs at attack
rate) converts the min/max tracker into a mean-follower and loses all discrimination
during flag runs - tried, measured harmful, removed.

Offline sweep after (10×40-byte frames, 1 s gaps, cold): **all 13 modes 10/10 at
TXDELAY 0** except fsk9600 classic at 10 ms - identical to the NinoTNC's own floor
(both bounded by the x^17 scrambler needing >16 bits), and **better than it on
qpsk2400** (ours acquires at 0 where its demodulator needs ~100 ms). samples/pdn
regenerated (the committed set embodied bug 1). Hardware re-validation against a real
NinoTNC pending - the bench TNCs are currently paired for the TXDELAY survey.

### 2026-07-15 (night) - TXDELAY: 20 ms is enough (and the 500 ms claim was wrong)

Tom challenged the "QPSK needs ≥500 ms TXDELAY" note - suspecting it conflated *preamble
length* with *the modem settling after a mode change*, and flagging that the NinoTNC may
send the frame after a TXDELAY change at the old setting. Both suspicions were right, and
the rig can now prove it: GETALL register 0B (`PreamblCnt`) is a readback of the applied
preamble in 16-bit words, and the bench reports per-burst air duration.

- **TXDELAY applies one frame late.** The readback updates immediately; the air does not.
  Moved 300 → 50 ms, burst #00 measured 571 ms and #01+ 330 ms - a 241 ms excess, exactly
  the old setting. Never measure a TXDELAY change on the frame after it.
- **20 ms is enough** for afsk1200, fsk9600 and bpsk300 in both directions (6/6), and
  **our demodulator locks on ~13-20 ms preambles in every mode tested**. Only the
  NinoTNC's QPSK demodulator wants more: QPSK-2400 goes 6/6 at 100 ms and 0/6 at 50 ms.
- **The 500 ms claim is retracted.** It was the QPSK modulator bug (since fixed) plus
  unreliable first frames after a mode change, misread as a preamble requirement. The
  bench now settles 1500 ms after SETHW (`--settle-ms`) rather than papering over it with
  a long TXDELAY.

Tables in docs/ninotnc-loop.md § How short can TXDELAY be?. Bench gained `--our-txdelay-ms`
so the two directions can be swept independently - conflating them is what hid this.

### 2026-07-15 (evening) - v44 firmware, 13/15 mode coverage, and the silence bug

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
Modes 12/13/14 (300 AFSK, 1600/1800 Hz - measured off-air to confirm) needed a new
`Afsk300Modem` over a generalised `AfskDemodulator`/`AfskModulator`. **Coverage is now 13
of 15 DIP positions; the gap is C4FSK (modes 1/3).**

The 300 baud bring-up then paid for itself several times over. It stuck at 3-6 of 8 frames
while the FEC modes on the same audio did better - the tell that the *bits* were marginal,
not the signal. Recording the link and decoding it offline showed each burst was actually
perfect when a **fresh** demodulator saw it and lossy when a **long-running** one did;
logging the envelope trackers found the cause. With no signal, the discriminator's power
normalisation divides noise by ~zero power and emits full-scale garbage, and the trackers
learn it - so every burst opened with its peaks pinned and its slice point up to a third
of the eye off centre. **The clamp meant to bound that garbage was a fixed ±1: ~2x the
legitimate ±0.5 at Bell 202's ±500 Hz shift, but 10x the ±0.105 of the ±100 Hz HF modes.**
It now tracks each mode's own full deviation. Result: 300 AFSK 8/8, **and the WA8LMF
benchmark improved at every rate measured - Track 2 @12 kHz single decoder 269 → 426 and
multi-bank 972 → 983 (atest 970); @44.1 kHz multi-bank 955 → 987 (atest 983), taking us
ahead of the reference at both rates for the first time.** A constant that was merely
generous for one mode had been costing real off-air frames for the whole project - and
note what it cost us to have stopped earlier at "the residual 44.1 kHz gap is direwolf's
multi-slicer margin, not timing": that conclusion was wrong, and comfortable enough to
stop the search.

That in turn exposed a latent `PacketDcd` bug: transition scoring can only drop DCD when
it *sees* badly-timed transitions, so it relied on receiver noise to notice a signal had
stopped - on a genuinely quiet channel (squelched radio, wired loop, or our own now-silent
demodulator) **DCD latched on for ever**. It now also drops after 24 transition-free
symbols, which tightened release from a ragged 60-300 ms to a consistent 60-91 ms. Exactly
the end-of-DCD accuracy the CSMA seam depends on.

Negative results, banked in code comments so they are not re-attempted: a **silence
squelch** (zero the discriminator below an absolute power floor) is intuitive and
measurably worthless once the clamp is right - Track 2 scored 269 unclamped / 426 clamped
/ 270 squelched-only / 427 both, so it was dropped rather than kept on plausibility. An
earlier *relative* version of that gate was far worse than useless (Track 2 972 → **65**):
one loud frame parks the tracker and squelches every quieter frame after it, which is
precisely what that track exists to test. And a 7×7 filter-cutoff sweep produced an
erratic, non-monotonic surface I nearly tuned constants against - it was noise thrown off
by the real bug, not a filter optimum. Every fix here is attributed by toggling it alone
against a corpus, because three of them went in together and the tempting story ("the
squelch fixed it") turned out to be the wrong one.

### 2026-07-15 (later still) - NinoTNC loop: all six pairs bidirectional, sustained

The wired CM108↔NinoTNC rig (docs/ninotnc-loop.md) ran its first full campaign against
firmware 3.41 via the new `nino-bench` tool, which reads NinoTNC-side truth from the
GETALL diagnostic registers. Every supported pair (afsk1200:6, bpsk300:8, qpsk2400:11,
qpsk3600:5, fsk9600:0, fsk9600-il2p:2) now passes 100% both directions in sustained
runs, with DCD assert/release lag measured against the audio envelope (assert ≤ tens of
ms, release always late - CSMA-safe). Three defects found and fixed, none of which any
loopback/WAV test caught: AlsaPcm needed an explicit capture `snd_pcm_start` (CM108B
EIO) and a `snd_pcm_prepare` after drain (second TX EBADFD); QpskModulator's
integer-boundary synthesis jittered 1800-baud symbol edges by ±½ sample and collapsed
the phase ramp to a hard step (56-88% NinoTNC decode → 100% after continuous-time
rewrite; `TxRampFraction` default 0.25 - 0.5 drops to ~7%, the NinoTNC wants sharp
transitions); Fsk9600Modem RX now interpolates ×2 before the DPLL à la direwolf
(classic-HDLC 88% → 100%, DCD assert lag → ≤2 ms). Also learned: QPSK-from-cold wants
≥500 ms TXDELAY (NinoTNC demod lock); the bench initially mis-blamed audio for what was
a `SerialPort.ReadTimeout` TimeoutException silently killing its serial pump - GETALL
before/after each direction now makes that class of error self-diagnosing. Level
verdict for the rig as wired: RX peak 0.17-0.28 FS across modes, no pot changes needed.

### 2026-07-15 (later) - FX.25 + multi-decoder + daemon config + .deb; publish staged

Multi-decoder AFSK bank lands at exact atest parity (38/38-reference on the direwolf noise
battery, from 34 single-decoder). FX.25 codec + deframer cross-validated bidirectionally
with direwolf and wired into the AFSK modem/daemon with transparent-dedupe. Daemon gains a
JSON config file and .deb packaging (amd64 smoke-tested, arm64 built). NuGet publish
workflow added and v0.1.0 tagged with Tom's authorization - pack+tests green on the org
runner; push skipped pending the NUGET_API_KEY secret being granted to this repo (see
Blocked on Tom). 147 tests green.

### 2026-07-15 - QPSK + 9600 modems; the legacy-max-FEC interop discovery

QPSK 2400/3600 (spec symbol map, fractional-delay differential detection) and the 9600
baseband modem (classic G3RUH + IL2P framings) land with loopback suites; sm-decode grows
all modes; the daemon registers them (48 kHz auto-selected for 9600). Bidirectional Dire
Wolf cross-validation added for 9600 both framings (fixtures committed). Two wire-truth
finds: IL2P baseband polarity differs between implementations → the deframer now hunts the
sync word in both polarities (spec-recommended); and the v0.6 RESERVED header bit is still
a live max-FEC selector in Dire Wolf's decoder - clear = legacy variable-parity plan →
16-parity frames rejected. Encode now defaults the bit ON (`legacyMaxFecBit`), spec-exact
mode retained for the vector tests. 131 tests green.

### 2026-07-14 (later) - Phase 1 complete in software; DCD, spectrum, ALSA land

Same-day continuation: HDLC bit layer + IL2P streaming deframer; WAV harness; AFSK 1200 and
BPSK 300 modulator/demodulator pairs with loopback suites (noise, offset, quiet, multi-block,
back-to-back); cross-validation vs Dire Wolf built from source - 4/4 parity with atest on
clean AFSK and IL2P-over-AFSK fixtures (committed as regression tests), 34-vs-38 on the
100-frame noise battery (single decoder vs multi-slicer; multi-decoder bank is the Phase 4
answer). Two real-world demod fixes came out of direwolf audio: discriminator clamping
(silence noise over near-zero power deafened the envelope slicer) and flush-tail handling.
Then Phase 2 groundwork: native DCD (PacketDcd + EnergyBusyDetector on both demods),
radix-2 FFT + SpectrumSource waterfall feed, AlsaPcm P/Invoke + anti-aliased ÷4 Decimator.
`tools/Packet.SoundModem.Decode` (sm-decode) added as our atest equivalent. 101 tests
(99 pass + 2 ALSA smoke tests that need the audio group). Remaining Phase 1 exit gate -
hardware corpus ≥ QtSM/NinoTNC - needs bench-rig time (Phase 0).

### 2026-07-14 - repo founded; IL2P codec lands
Repo created from the packet.net research + decisions. Scaffold (net10.0, CPM, xunit +
AwesomeAssertions, self-hosted CI) plus the first functional layer: complete IL2P frame codec
written from spec draft v0.6, validated byte-exact against the spec's S/UI/I example packets,
with RS error-correction tests (1-byte header repair, 8-byte payload-block repair, fuzz)
and encode/decode roundtrip fuzz across frame types, Type 0 fallbacks and multi-block
payloads. Wire nuance recorded: spec vectors leave the RESERVED header bit clear (Dire Wolf
sets it) - we encode clear, ignore on RX. CRC variant pinned as CRC-16/X-25 by the S-frame
vector (0xF0DB).
