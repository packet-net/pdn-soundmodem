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
- ✅ 300 BPSK modulator + demodulator (IL2P symbol map; QtSM P300 filter plan) —
  clean/noisy/offset/multi-block loopbacks green. (2026-07-14; coherent default 2026-07-16
  per #5, **reverted to differential default 2026-07-18 per #40/#42** — on real off-air HF
  benchmarked against a NinoTNC, differential + the frequency-diversity bank matches/beats
  coherent because real carriers arrive off-frequency with short preambles. Coherent stays a
  detector option; QPSK keeps its coherent default.)
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
- ✅ Constellation side channel (2026-07-16, issue #9): `ConstellationSource` — the PSK
  demodulators' per-symbol decision point (the differential product they already compute)
  tapped via `IConstellationSource`, batched into auto-ranged scope frames (256 pts, 2
  signed bytes/pt ≈5/s at qpsk2400). Wired per-modem on `SoundModemChannel`, for the PSK
  modes only. Diagnostic-only (no wire/interop impact); the debugging surface #5 builds on.
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
- ✅ Daemon-side browser waterfall (2026-08-01, PR #157): `WaterfallWebServer` — an
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
- ⬜ packet.net side: `kind: soundmodem` transport + `transport is ICarrierSense` probe at
  PortSupervisor (seam mapped in the research doc §5), spectrum + constellation SSE
  endpoints + waterfall/constellation UI (PdnPortTuningApi is the template; add to the SSE
  token allowlist; node-api.yaml). The `constellationSink` on `SoundModemChannel` is the
  node-side seam, mirroring `spectrumSink`.
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
- ✅ QPSK 2400/3600 modem pair (spec QPSK symbol map, coherent Costas detection default +
  differential opt-in, fractional one-symbol delay for 1800 Bd at 12 kHz); loopbacks incl.
  noise/offset/multi-block.
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
- ✅ BPSK frequency-diversity bank (2026-07-18, #40/#42): `BpskMultiModem` — the same
  stepped-centre model for the coherent PSK modes (daemon `bpsk300-multi`/`bpsk1200-multi`).
  Coherent's narrow tracking loop can't pull a tens-of-Hz offset carrier onto frequency
  within a ~150 ms preamble without forfeiting its noise margin / QtSM interop, so a bank of
  ordinary branches (step ≈ baud/40) covers the offset range instead — a single centred
  coherent modem misses ±12–24 Hz, the bank decodes it. Corrected the #42 diagnosis: the
  coherent path already differential-decodes (it was never the missing step); the real gap is
  short-preamble acquisition of an offset carrier. The committed GB7RDG off-air frame (~8 Hz
  offset, 16 dB, but a preamble too short for the narrow loop even on-frequency) decodes via
  `PskDetector.Differential` — guarded by `OffAirBpskTests`. Bank step/span are tuneable
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

### 2026-08-04 (later⁴) — the survey comes up onto the page

Tom asked what I thought about bringing the survey and its diagnostics onto the web UI. Three things, worth different amounts, and the smallest was the one I had missed: **the panel is where he saw the word "unattributed" in the first place**, and I had fixed the journal and the capture sidecar and left that surface saying it and stopping — the same complaint he had made, one layer up. It now carries the IL2P header type, the reason, and the frame's bytes laid out to be selected and pasted, which is the next thing anybody does with one. `Ax25AttributionNote` moved from `Survey` to `Waterfall`, beside the parser whose verdict it explains: the panel's use of it has nothing to do with surveying. The note quotes a character taken straight off the air, so the panel's `innerHTML` rows gained escaping and a test that a payload containing `<` arrives as text.

**Captures are drawn where they happened.** A capture has a frequency, a width and a time, which is exactly what the waterfall's two axes are — "something we could not read went past *there*" is a statement that display can make and a list of filenames cannot, which is why this is a bracket on the scroll and not a captures browser. Placed by **age, not line index**: the survey runs its own spectrum feed so it keeps working on a station with nobody watching, and its line clock is therefore not the display's; seconds-ago is the quantity both agree on. Each is listed with links to its audio and sidecar, served by the one waterfall route that reads from disk — only the exact filename shape the writer produces, only out of the survey directory, refusals tested including percent-encoded traversal.

**And the blind spot closed.** `SignalSurvey` counted bursts a rate limit, cooldown or missing audio refused, and nothing reported the number: a station left collecting for a week silently becomes a sample rather than the set, and the alternative was counting files per hour and noticing it equalled the cap. The panel header now carries `survey N · M skipped · X MB`, pushed on change rather than polled, and sent to a browser arriving mid-session. It is state, not an event, which is why it belongs on a display and not in a journal.

Verified live end to end: the daemon on the m9psy fixture pushed `survey` on connect and again on each refusal, emitted a `capture` for a real burst, and served its 259 KB WAV and JSON over HTTP while refusing `../frames.db`, its percent-encoded form, and a bare `frames.db`. **No authentication on the waterfall** — operator-accepted, and now stated plainly in CONFIG.md rather than left as tidy-mindedness, because recorded audio is reachable over it.

Instrument note: the page probe's row assertions were addressed by index, so adding two probe steps broke two unrelated tests. Rows are found by content now, with order asserted only where it means something.

### 2026-08-04 (later³) — an unattributed frame explains itself

Tom, on being told to run a SQL query against his own frame log to find out why a frame had no callsigns: *"You should be capturing those details yourself."* Correct, and the tool had the information all along — a frame reaching the panel as unattributed has already passed Reed-Solomon and the IL2P trailing CRC, so the bits are right and the *reading* of them is not, and that distinction is the whole diagnosis. It was being thrown away at the point it was cheapest to keep.

`FrameQuality` now carries `Il2pHeaderType`, plumbed from `Il2pDecodeInfo` through all six IL2P modems: Type 1 translated and Type 0 transparent put the AX.25 address field in different places, so which one carried a frame decides whether the payload is unusual or the decode is, and it is the first question worth asking. `Ax25AttributionNote` says in a line what would not read — a frame too short for an address field and control byte, or the exact byte, field and character that is not a shifted callsign character. Both land in an `unattributed` survey capture's sidecar beside the payload hex, and both land on the journal's `rx` line as well, because the survey is optional, budgeted, and may drop that particular burst. The live case — 118 bytes on `bpsk300-il2pc-multi9`, CRC-valid, zero corrections — now reads `il2p Type1 [byte 0 of the destination callsign is 0x00 → 0x00, not a shifted callsign character]` instead of `(no ax25 header)` and nothing more.

Deliberately a diagnostic and not a parser: `Ax25AddressParser` still decides whether a frame is attributable and this only explains its verdict. Ordinary lines are unchanged and pinned by an equality assertion, these being text that ends up in other people's grep pipelines.

### 2026-08-04 (later²) — the station starts keeping the signals it cannot read

Tom watched a packet-shaped burst slide past at 7.050594 on the live 40 m waterfall — in the 225 Hz hole between the afsk300 bank's top edge (7.050475) and ARDOP's bottom one (7.050700) — and asked the obvious question: we are not listening there, so how can we ever tell what it was? He ruled out the obvious answer himself ("seems a bit indulgent to run many many modems over the whole passband"), and he is right for a better reason than CPU: the *mode* is unknown too, so a comb is centres × modes and it is still silent when it guesses wrong. Energy, meanwhile, is already being computed thirty times a second for the display.

Landed as **issue #206** tiers 1–3, with tier 4 (classification) deliberately deferred until a week of real captures says what the shortlist needs to contain. `SpectralBurstDetector` is the whole-band generalisation of `BandActivityTracker`: per-bin min-tracking floor, runs of adjacent bins 6 dB over it, runs overlapping in frequency on consecutive lines treated as one burst, and a burst reported only once it *ends* — "started and stopped" being most of what separates a transmission from a carrier. `SignalSurvey` judges a closed burst against the modems actually running: **unclaimed** (outside every band), **missed** (inside one, nothing decoded — the most valuable of the three, since it is the residual-miss problem `NinoTncMissCorpusAspirationTests` tracks, invisible today unless somebody happens to be recording), and **unattributed** (a frame that decoded with no readable AX.25 addresses — Tom's other sighting, `2·bpsk300-il2pc-multi9 · 118 B`, whose payload now travels in the sidecar beside the audio). `AudioRingBuffer` is what makes any of it possible: a burst is only reported once it has ended, by which point the audio that carried it is past. Its own spectrum feed rather than the waterfall's — the same transform at the same rate is cheap, and the alternative is a survey that only works when somebody has also asked for a browser page.

**Three defects the tests and the fixture found, all of which would have mattered on air.** `AudioRingBuffer.Write` placed an over-long block's surviving tail at the position the *whole block* started from, silently shifting every later read — real audio from the wrong moment, worse than none because nothing about the file looks wrong. A signal outlasting the floor's ~15 s memory raised its own floor and stopped looking like a burst: a 25-second SSB over came out as a pair of ~13-second "bursts", each short enough to pass the duration gate and be captured as a packet — bins carrying signal are now excluded from the floor, a floor being a measurement of noise. And the reported duration was derived from the WAV's length less its margins, which read −0.2 s whenever the trailing margin had not been recorded yet; it comes from the burst's own line count now.

**One misdiagnosis, banked.** The same fixture run appeared to show one signal reported as four overlapping bursts (1500, 1324, 507 and 288 Hz wide, all 10.13 s), and a merge pass was written for it. The fixture is exactly 10.0 s and `--wav-loop` replays it: the four were four loop iterations of the same clip, let through by a 5-second cooldown in the test config rather than the shipped 120. The merge was reverted. Instrument lesson, same family as 2026-08-03's: **a repeating fixture manufactures repeating findings** — check the clip length against the interval before believing a duplicate.

**End to end on real off-air audio**, which is what says the thing works. Against the committed m9psy 40 m QRM fixture with only `bpsk300` at 1500 Hz configured, the survey wrote seven captures, and the one at 862 Hz audio — **7.050312 MHz, the m9psy afsk300 slot the station was not listening to** — decodes through `Afsk300MultiModem` as `M0LTE-0>GB7IOW-1`. A signal nobody was configured to hear, found by its energy, kept, and read back afterwards. Tier 4 by hand, in one line.

Off unless configured, and budgeted when it is: 512 MB pruning oldest-first, 30 captures an hour, a 120 s cooldown per 250 Hz bucket. Pruning rather than stopping is deliberate and operator-approved — a station left collecting for a week would otherwise quietly stop on day one and leave an empty tail.

### 2026-08-04 (later) — the frame log records what the station sends, and the backlog says which frames were ours

The display learned to list our own transmissions this morning; the *log* still did not write them down, so the record on disk stayed what it had always been — every frame received, none sent, which Tom's view (and the right one) is half a journal. `FrameLog` now takes a `direction` column (`rx`/`tx`) and a `RecordTransmitted` path off the same `SoundModemChannel.FrameTransmitted` event the console line and the waterfall already use — raised after the audio has gone to the device, so a logged row is a frame that actually went on air. A transmitted row carries who to who, mode, length, payload and the modem's configured `audio_hz`/`rf_hz`; it carries **null** for `corrected`, `crc_valid` and `offset_hz`, because those are receive measurements and inventing them for our own transmission would be inventing a measurement of ourselves — the same decision the UI made, kept consistent on disk. The write stays queued and drop-on-backlog like the receive path: the transmit thread does not wait on a disk either.

**The column is still called `heard_at`, and on a transmitted row it means when it went out.** Renaming it would be more honest about one row and would silently break every query, dashboard and documented example written against this log, including `CONFIG.md`'s own — so the wart is documented in three places (the class, the record, the operator docs) rather than deviated from. Same house rule as the `deb` packaging precedent: document the ugly consequence instead of taking the tidier route that costs someone else a breakage.

**Deployed stations get migrated, not broken.** `CREATE TABLE IF NOT EXISTS` leaves an existing table exactly as it was, so a station that has been logging since the `frameLog` work would have kept a table with no `direction` column and failed *every* insert from here on — a modem that silently stops logging, which is the worst shape this failure could take. `Open` checks `pragma_table_info` and adds the column when it is absent; the existing rows are all receives, so `DEFAULT 'rx'` is the truth about them rather than a guess, and no backfill is needed. Two tests build a pre-migration database by hand from the old schema and prove the old rows survive intact, read back as `rx` through both `SELECT *` and the panel's own `Recent` query.

**And the waterfall's opening backlog carries the direction through**, so a browser that reloads sees its own beacons badged TX rather than listed as stations heard. One CSS trap on the way: a historic transmission gets `class="fr tx hist"`, and `.fr.hist`'s grey `border-left` is declared after `.fr.tx`'s cyan one — at equal specificity the grey wins, and the row would have arrived dimmed *and* stripped of the only thing saying it was ours. `.fr.hist.tx` restores it. The node:vm page probe now resolves `border-left` out of the shipping stylesheet the way a browser resolves it (matching class chains, by specificity then source order), because the class list alone cannot say which rule won — removing the new rule turns that assertion red, which is the check the defect deserved.

The `mode` column on a transmitted row takes the modem's own report of itself (`bpsk300-il2pc-multi9`), not the configured name the console line prints (`bpsk300`), so one modem's traffic stays under one spelling and "everything on this mode today" is a single query that includes our own frames. Proved end to end as well as in the suite: the daemon on `--wav-loop` with a `frameLog` and a `waterfall`, a KISS frame pushed in at 18105, and the row lands `direction=tx`, `M0LTE-9>GB7RDG-2`, 38 bytes, 1500 Hz / 7.0516 MHz, nulls in all three receive-measurement columns — and a browser connecting afterwards is sent that row with `tx:true, hist:true` alongside the GB7RDG decode with `tx:null`. Suite 1161/0 (125 skipped) in Debug and Release.

### 2026-08-04 — the BPSK bank's carrier offset becomes a measurement, and the frame panel stops being half a record and half a session

Two things Tom asked for in one leg. **Issue #202**: the `offset_hz` the frame log has been accumulating for every BPSK-family mode was not a frequency. `BpskMultiModem` reported the nominal comb position of whichever branch happened to emit a frame, branches run in array order, and the dedupe is first-wins — so on 26 hours of the GB7RDG 40 m channel, 431 frames from a GPSDO-locked station took nine values and nothing else, 82 % of them the most negative branch. Same defect as the 300 AFSK bank's (fixed 2026-08-02), same fix in shape and now in code: the differential detector was already forming the product whose discarded imaginary part *is* the carrier rotation, so `BpskDemodulator.CarrierOffsetHz` inlines `BpskCarrierOffsetEstimator`'s squaring trick over a decaying window rather than a peak hold (a peak hold would freeze on the first strong burst of a long session), the coherent path reads its Costas NCO instead, and the bank holds every branch's copy to the end of its dedupe chunk and emits the best-centred one as `branch + residual`. Where nothing could be measured it reports **null**, not a comb position. Clean loopback tracks to 0.09 Hz across ±33 Hz and 0.16 Hz across ±100 Hz at 1200 Bd; the real GB7RDG off-air capture comes out at +8.56 Hz against the standalone estimator's independent ~8. Detail and the AWGN numbers in the [mode-validation ledger](mode-validation.md); the offsets that entry corrects were quoted in this repo's own docs, so those are annotated rather than quietly edited.

**And the panel opens on the station's own frame log.** A panel that starts empty says nothing about a channel that has been busy all morning, and on a quiet band it is indistinguishable from a modem that is not working — the station had been writing every decode down since the `frameLog` work and the display was the one place that never read it back. `WaterfallOptions.FrameHistory` is the seam (a delegate, because the log lives in the daemon and the server lives in the library), `FrameLog.Recent` the implementation: its own short-lived read-only connection rather than the writer's, since `SqliteConnection` is not thread-safe and this is called from whichever connection thread a browser turned up on while the writer is mid-INSERT. WAL, which the log has been since it was built, is what makes that free. The last 50 rows — half the panel's own cap — go out oldest-first in one message straight after the config and before the send loop starts, so a frame decoded during the handshake is queued behind them and lands above rather than being interleaved. Rows are dimmed and carry the time they were *heard*, with a date when that was not today; they are listed and never tagged, having been heard before the scroll on screen began. A reconnect rebuilds the panel from the log rather than stacking a second copy. Receive-only, because the log is: what this station sends shows live and is not written down.

**And the waterfall's decoded-frames panel now lists what this station sends**, marked TX and styled apart. It was a record of half the channel — everything heard, nothing sent — so an operator watching their own beacon go out had only the burst to go on. Listed once the audio has left, so a listed frame is one that went on air; no SNR, offset, FEC count or CRC verdict, because those are receive measurements of somebody else; and not tagged onto the waterfall, because the burst is repainted from a queue in real time while the event fires as soon as the device took the audio, so the tag would land up the burst rather than on it. The page's frame dispatch became a named function so the node:vm probe can drive it, which is how "listed but not tagged" is asserted rather than assumed.

### 2026-08-03 (later) — one wide slice instead of two daemons: the passband is worked out, not configured

Tom asked how two pdn-soundmodem instances would share one Flex — ordinary modems on one, MS110D on the other, different places in the same band — and whether a per-modem slice number would be nicer than managing two processes. The answer to the first is that receive would be fine and transmit would not: a Flex has one PA and one transmit slice, two daemons have no shared carrier sense, and (as of this morning's work) each would set the global transmit filter at bring-up with the last one up winning. The answer to the second is that a slice per modem means a channel per slice plus a transmit arbiter, because the radio still has one transmitter — real work, and worth it only for genuinely different dials. Which left a third option that needs neither: **this repo's own §10.2 measurement says DAX is not a ~3 kHz path**. One slice can carry the packet modems at 300–2700 Hz and MS110D 3 kHz higher up, on one dial, in one process, with one CSMA view. Tom picked that route, said to skip the receive-filter measurement and assume, and asked for the passband to be automatic rather than configured.

Three pieces landed. **`M0LTE.Flex` 0.11.0** adds the receive half of the bandwidth question: `ReceiveFilterLowHz`/`ReceiveFilterHighHz` apply `slice set <n> filter_lo= filter_hi=` during headless bring-up, because the transmit filter governs what leaves the radio while the slice's own filter governs what reaches DAX-RX — widen only the transmit side and you get a wide signal out and an ordinary 3 kHz window back in. Its ceiling is *not* measured, unlike the transmit filter's 10 kHz clamp, so nothing pretends otherwise: the filter is read back, and `ReceiveFilterWarning` reports a radio that would not go as wide as asked rather than the modem going quietly deaf. `MockFlexRadio` models the slice passband and takes an optional ceiling so that path is testable offline.

**The passband became a derived value.** `RfPlan`'s 300/2700 constants are now a `Passband` threaded through the solver, and `Passband.Fit` picks one: the ordinary SSB window when the modems fit it — so every existing plan lands on exactly the same dial — and, on a headless Flex whose filters the daemon sets itself, a window widened just far enough to fit them, capped at the radio's 10 kHz. The daemon then sets both filters to match (the transmit high cut from this morning's `TransmitFilterPlan`, the slice receive filter from the same measured bands) and reports the window when it is not the ordinary one. Nothing to configure: the ceiling follows the device, and the width follows the modems.

**Spec-fixed modes turned out to be the blocker, and were a pre-existing bug.** The planner assigned every modem an audio centre, including modes that reject one — so `rfFrequency` on any `ms110d-*`, `freedv-*`, `fsk9600` or `c4fsk*` modem had always died at start-up with "mode has a fixed centre frequency — drop the frequency override", about an override the operator never wrote. The fix inverts the relationship: a mode whose centre its standard pins (`ModemCatalog.DefaultCentreFrequencyFor`, declared and held to the modem's own measured spectrum by `ModemCentreFrequencyTests`) cannot move to suit a dial, so it *is* the dial, and the movable modems are placed around it. Two such modems that want different dials are refused by name; a modem left below the dial a fixed one forces gets told that in those words rather than being shown a negative audio frequency; and the baseband families, which have no centre at all, are refused a band plan with the reason instead of a confusing override message. Found along the way: `ModemBandProbe` could not meter any mode whose probe frame was over in under 2048 samples, so `qpsk3600` — a NinoTNC mode — was being planned at the 500 Hz nominal fallback rather than its real width. The window now shrinks to fit the burst.

End to end against `flex:mock`: `ms110d-wn4` at 7.0516 MHz and `bpsk300` at 7.0540 MHz plan onto one dial of 7.049800 (1800 and 4200 Hz audio), passband 300–4470 Hz, transmit filter 4600, slice receive filter 200–4600. The 40 m three-modem plan is unchanged to the Hz. Suite 1200/0 in Release. Still assumed rather than measured: that a slice will accept a receive filter as wide as the transmit one — the warning is what will tell us otherwise, and Tom's 6500 is where it gets settled.
### 2026-08-03 — overnight validation closes the afsk300 program: bank at 2×, the slot speaks three framings, and one negative banked

The two-vantage overnight capture promised by the 0.14.0 release ran its course (Wessex all night; m9psy quota-blocked throughout — its "daily" limit is a rolling ~24 h window, which the capture loop's backoff rode out with 55 polite 10-minute probes; 0.14.1's in-daemon patience was verified separately against the same refusing instance). Verdict on 45 unique IL2P-family frames from 7 stations across both receivers: **old wide single 19, released single 24, released bank 38 (84 %)**, shipped class frame-identical to the prototype. Full detail in the [mode-validation ledger](mode-validation.md) addendum, including the two population discoveries — UT1HZM's 22 classic-AX.25 frames and the GB7BWR/PD4R plain-IL2P signature, which make the slot a *three-framing* neighbourhood a single-framing config structurally half-ignores (operator fix: three modem entries on one slot frequency) — and one banked negative: the DCD-falling-edge deframer reset cost the old wide single 4 frames but the shipped narrow paths nothing, a 16-bit hysteresis prototype recovered none of them, so no reset-policy change ships and the measurement is on file for whoever looks next. No 0.14.2: nothing earned it.

### 2026-08-03 — the transmit filter is worked out from the modems, not inherited

Tom asked whether the Flex driver could be configured for 48 kHz. It already is — `DaxStreamFormat.ForDspRate` picks the full-bandwidth 48 kHz float32 DAX stream whenever any configured mode puts the channel at 48 kHz, and the reduced-bandwidth 24 kHz s16 one otherwise, so putting `ms110d-wn4` in the modems list is the whole configuration (verified against `flex:mock`: `DAX 48000 Hz → 48000 Hz` versus `DAX 24000 Hz → 12000 Hz` for `bpsk300`). What was *not* configured was the thing that would actually have degraded MS110D on that radio: the transmit filter. It is a global, persistent radio setting — whatever last touched the rig, surviving the daemon — and the factory 3000 Hz cuts the top off a waveform that occupies ~410–3199 Hz measured. The band-planned path had stated the high cut since the RF-terms work, but a station placed by audio centre inherited whatever was there, and MS110D cannot use a band plan at all (its occupied width exceeds the planner's 2400 Hz single-passband budget, so `rfFrequency` on an `ms110d-*` modem fails at start-up).

So a headless Flex now derives it: `TransmitFilterPlan` measures each modem at the centre it is configured for — through `ModemBandProbe`, the same probe the band planner fits with and the waterfall draws, so those three cannot disagree — and sets the high cut to clear the highest, on the same 200 Hz margin and 50 Hz rounding the plan uses (`BandPlanner.HighCutClearing`, now shared). `ms110d-wn4` alone asks for 3400 Hz; `bpsk300` at 1500 Hz asks for 1900, which narrows the filter and keeps transmitted noise off the neighbours. ARDOP keeps its special case (nothing to probe — the width is a per-session negotiation, so the configured cap or the widest it can reach stands in). `"flex": { "transmitFilterHighHz": N }` pins it and a band plan no longer overrides that; `0` restores the old leave-it-alone behaviour, and a value outside 500–10000 is rejected at start-up because the units invite a frequency where a cut-off belongs. The 2026-07-27 entry's "we deliberately never set it" no longer holds.

The clipping check that used to run only for band-planned stations now runs for every Flex station off the same measured bands, which is the whole of what attach mode gets (SmartSDR owns the slice there, so the daemon still sets nothing) — and it no longer tells you to move a modem whose centre is pinned by its spec. Nine tests on the derivation, three on the config key; verified end to end against `flex:mock` in all five shapes (derived, pinned, `0`, attach, band-planned).

### 2026-08-02 (later³) — a station behind a spent quota waits; it does not hammer

The public UberSDR instances meter listening per address per day (3 h on `m9psy-1`), and restarting Tom's daemon put it behind that limit — where the `ubersdr:` device's failure handling turned one polite refusal into an all-evening pelting: a 429 at start-up crashed the daemon (systemd `RestartSec=5` re-asked every five seconds forever), and mid-run the reconnect loop's fixed one-second "breath" plus a give-up-and-restart path did the same in different clothes. None of that can mint quota; all of it burdens somebody else's receiver.

Fixed by classifying failures by what fixes them (`UberSdrReconnectPolicy`, unit-tested apart from the sockets). **Refused-for-now** — HTTP 429 on the preflight or the stream upgrade (`CollectHttpResponseDetails`), or a reply whose `daily_time_remaining_secs` is 0 — waits on a long ladder (60 s doubling to a 15 min cap), never trips the give-up restart, and at start-up brings the station up anyway (KISS, waterfall, a clear log line) with the stream joining when the receiver relents: the same behaviour as if the quota had run out mid-afternoon. **Transient** transport failures keep the quick ladder and the 5-minute give-up (a restart can genuinely help there). **Sessions that die before delivering 10 s of audio** — an instance that accepts and instantly drops — now escalate 5 s → 5 min instead of breathing one second forever. One healthy session resets the ladder. `ConnectionResponse` learns the daily-metering fields; the preflight's refusals come back as data for the caller to classify rather than as exceptions. Verified live against the actually-refusing instance: the daemon prints the reason twice, stays up, and waits. Released as 0.14.1.

### 2026-08-02 (later²) — afsk300's real problem was its receive filter: the narrow-branch diversity bank

Tom pointed the session at the live 7.0503 MHz slot through the `ubersdr:` device — pdn-soundmodem has never decoded afsk300-il2pc well there, which no bench result predicted. The investigation ran on evidence, not code reading: a segmented IQ capture of the slot from `m9psy-1` (corpus/ubersdr/, kept locally), decoded offline through the daemon's exact receive path with an instrumented branch bank, plus synthetic CFO/interference sweeps against the recorded channel.

Three findings. **CFO was exonerated** — the single demod copies its own TX to ±60–70 Hz static offset even at 10 dB SNR, and the real stations measured −3/+35/−35 Hz; the ledger's "CFO-fragile" note came from the RSP1's *drifting* LO, a different failure. **The mechanism is discriminator capture**: the shipped ±400 Hz receive filters reach ~200 Hz past the slot in each direction, and the slot's everyday neighbourhood (an SSB QSO parked at ~7.0500, occasionally a wideband OFDM burst) lands inside them at comparable power; a quadrature discriminator follows the strongest thing it sees. Injecting a clean burst into the *recorded* interference bed: ±400 filters need the packet +6 dB above the neighbour, ±250 manages −3 dB, a ±175 Hz passband −12 dB. **Tight filters cost offset range**, so the deployable shape is the pattern this repo already trusts twice over: a stepped-centre bank of narrow branches.

Landed: `Afsk300Modem` receive filters 400 → 300 (the bench plateau its own comment always described; ctor params added for other widths), and **`Afsk300MultiModem`** — 2·pairs+1 branches of ±250 Hz-filtered `Afsk300Modem` at 35 Hz steps (default ±5 pairs = ±175 Hz), `FrameDeduper` across the bank, TX on the centre branch, no emphasis variants (nothing to twist across a 200 Hz tone spacing). The catalog's `afsk300`/`afsk300-il2p`/`afsk300-il2pc` now build the bank (`offsetPairs`/`offsetStepHz` knobs as for bpsk; 0 = single tight modem). Scored against the first 30 minutes of captured traffic — 13 unique CRC-valid frames from M0LTE, GB7BEX-15 and GB7NOT at three different offsets — **the old wide single decoded 3/13, the bank 13/13**. A 10 s off-air clip (the M0LTE SABM the wide filters lose to the neighbouring QSO) is committed as `samples/offair/m9psy-40m-afsk300-il2pc-qrm.wav` and guarded three ways by `OffAirAfsk300Tests`; bank behaviour (off-tune decode, dedupe, offset-side reporting, framings, pairs:0 collapse) by `Afsk300MultiModemTests`. Suite 995/0. Overnight capture continues for a wider-corpus validation before the ledger row moves; `sm-iqcapture` was also hardened to finalise its WAV on an abrupt server close instead of crashing (found the hard way when the receiver dropped the session and the retry loop met a rate limiter).

### 2026-08-02 (later) — the SSB demodulators move to `M0LTE.Dsp` 0.2.0

The two IQ→audio converters were never modem code. They are textbook SSB demodulation from complex baseband — NCO to put the suppressed carrier at DC, a complex bandpass keeping one sideband, the real part, decimate — and they only lived here because that is where the MS110D capture scorer needed them first. Yesterday's `ubersdr:` work made that awkward visible: the same filter was now serving an offline scorer *and* a live receiver, and the next SDR front end would have grown a third copy.

Lifted into **`M0LTE.Dsp` 0.2.0** as `SsbDemodulator` / `StreamingSsbDemodulator` / `SsbDemodulatorOptions` / `Sideband`, next to `Decimator` and `FrequencyShifter` (which is the real-signal counterpart of the same idea). Renamed on the way: `IqToAudioConverter` says what goes in and out, `SsbDemodulator` says what it *does*, and in a general-purpose package that is the difference that matters. No new dependency — they only ever used `FilterDesign` and `FirFilter`, both already there. Additive to that package's public API, so a MINOR bump under its own versioning policy, with `PublicApi.approved.txt` moved in the same commit.

The tests split along the same line. Everything that is a property of the filter — streaming-versus-reference equivalence, block-boundary independence, sideband selection asserted at absolute frequencies, decimation by one — went with the code, because none of it needs a modem to state. What stays here is `SsbDemodulatorMs110dLoopbackTests`: the question only this repo can ask, which is whether a real MS110D burst survives modulate → synthesise IQ → demodulate → decode and still comes back bit-exact.

Not extracted, and deliberately: the UberSDR client itself. It has one consumer, it is co-developed against a live third-party service whose behaviour is the only authority, and ka9q_ubersdr is young enough that its framing may still move — all of which argue for keeping it where a protocol fix and a modem fix can land in the same PR. The coupling is already nil (nothing in `UberSdr/` reaches past `Iq/`, `M0LTE.Dsp` and `M0LTE.Radio.Audio`), so extraction stays cheap for whenever a second consumer turns up. Revisit then, or when upstream settles.

### 2026-08-02 — a station with no antenna: `ubersdr:` as a receive-only device

Tom asked for UberSDR as a source for receive-only instances, IQ preferred, driven by an ordinary band-plan config with nothing but the device string changed. Landed as `--device ubersdr:<instance>`: the instance may be a host, a host:port, or the https:// URL out of a browser's address bar. The daemon takes the receiver's **iq48** stream (48 kHz complex, ±24 kHz), demodulates SSB from it in-process at the channel's DSP rate, and hands the modems real audio — so every mode, the waterfall, the frame log and KISS work unchanged, and the band plan's computed dial *tunes the receiver* the way it tunes a headless Flex rather than being printed for an operator to dial in. IQ rather than the instance's own demodulated audio for the reason the OTA capture plan gives: holding the complex baseband keeps the receiver's filter, AGC and resampler out of the path, which is what makes an SNR figure off this route mean the same thing as one off a sound card.

Most of the machinery already existed for the MS110D OTA campaign and only had to move: `PcmBinaryDecoder` and `ConnectionResponse` from `tools/Packet.SoundModem.UberSdr` into the core (which gains a `ZstdSharp.Port` dependency — MIT), and `IqToAudioConverter`/`StreamingIqToAudioConverter` into `Iq/`. Two gaps had to be filled to make an offline scorer's converter serve a live receiver: **decimation by 1**, for the 48 kHz mode families whose DSP rate *is* the IQ rate (the anti-alias low-pass is dropped there, in both converters together so their sample-for-sample equivalence test still holds), and **LSB**, which is the same kernel applied to the conjugated baseband. New is `UberSdrAudioInput` — a reconnecting receive loop behind `IAudioInput`, with the C0-measured one-second startup guard applied per connection, because public instances cap a session at three hours and a modem is expected to run for months.

The honest half of the feature is that there is no transmitter at the far end of a WebSocket. `SoundModemChannel.ReceiveOnlyReason` says so once, so KISS, paging and ARDOP all get the same refusal immediately rather than each discovering it differently — deliberately *not* `TransmitInhibit`, which would turn "cannot" into a 30-second wait ending in the wrong explanation. `ptt` alongside the device is rejected at start-up; `ardop` still loads, still hears the channel, and is warned about at start-up since no ARQ session can ever complete (its transmitter delegate is now awaited and caught, because the TNC's transmit worker does not survive an exception out of it).

**It works on air.** Against `m9psy-1.instance.ubersdr.org` on Tom's example 40 m plan (afsk300-il2pc 7.050300 / ardop 7.050950 / bpsk300 7.051600, dial computed at 7.049450 USB), the station decoded real off-air traffic on both packet modems within minutes — including **`afsk300-il2pc`, which had no on-air validation at all before this**. See the [mode-validation ledger](mode-validation.md). Measured demodulated level off that instance is ≈ −26 dBFS RMS, so the default `gain` of 1.0 needs no help.

### 2026-08-01 — the daemon grows its own browser waterfall (Phase 2's display, without waiting for packet.net)

Tom asked for a web waterfall in the daemon itself: 30 fps, selectable 2–4 kHz-nominal span, operator-set dial frequency, modems drawn over the passband with AF + absolute-RF centres and shaded bandwidth, a spectrum view above, and per-burst callsign/SNR/offset attribution readable straight off the scroll. Landed as PR #157, all in the library so the PDN node can reuse it: `WaterfallSource` (overlapping-FFT display-rate lines; the existing `SpectrumSource` stays as the low-rate telemetry feed), `WaterfallWebServer` (HttpListener + WebSocket, single embedded page, per-client bounded queues that drop history rather than stall the receive thread), display-grade `Ax25AddressParser`, and `BandActivityTracker` (burst SNR/extent from the display's own lines — the EnergyBusyDetector min-tracking idea on spectral power, so the numbers always agree with what the screen shows). Two measurement-over-tables decisions: modem overlay bands are measured at start-up from each modem's own modulated audio (SM.443 99 % OBW — new modes draw correctly for free), and burst extent/SNR are measured rather than derived from mode bitrate tables. The daemon gains `--waterfall`/`--dial`/config, and `--wav-loop` (a recording replayed at wall-clock pace as the capture device) for hardware-free demos — which is also how the page was verified on this GUI-less box: the real page driven by a byte-exact stubbed socket under headless Chrome, the real socket path proven by a ClientWebSocket integration test (this box's Chrome cannot create sockets at all, an environment quirk worth remembering — headless canvas *compositing* also silently fails here while the canvas pixels are provably correct via toDataURL). Marker palette validated with the dataviz checker (OKLCH dark band + CVD ΔE); README carries the screenshot.

### 2026-07-31 — WN8 redesign program closed (exit ii): WN8 decodes on Poor; the walls fell to measurement

The program registered this morning ran to its closing verdict in one day, every leg registered before running: W0 re-baselined byte-identity on current main; W1's truth-injection instrument showed the "immovable" Phase B ceiling was the estimator's segment time model (496/136 → 100/36); W1b's matched-filter bound decoded every specimen block to zero on the exact channel — the waveform was never the floor; W2 pinned the residual on the FF+sparse-chain detection sandwich and pivoted the candidate ladder; W3 measured label-free probe-anchor trajectory estimation 11 dB inside the MFB-form receiver's requirement, killing B3.4's "only labels provide" verdict without even needing the moment observables; W5 shipped `Ms110dMfbBlockDecoder` (per-burst delay-profile window, composite-FIR probe anchors, matched projection, SISO-soft/hard cancellation with convergence-gated decision-directed re-fit, fixed-point/cycle-accept/revert termination) behind the QAM16 gate with the full §6 ladder green — corpses 269,237→112 / 269,154→32, all 108 non-WN8 battery censuses byte-identical, AWGN WN8 0 at full budget; and W6's decision battery closed the program per its pre-committed rule at **Poor WN8 2.90E-4 canonical / 1.75E-2 disjoint** — a 1,711×/28× improvement over the coin-flip at entry, measured-only vs the 1E-5 mask, sim-only by rig physics. Exit (iii) is permanently refuted; the successor levers (three burst-start block failures, the slow-fade edge, deeper schedule diversity, the unused moment observables) are recorded with instruments waiting. PRs #129–#139; program record [ms110d/wn8-program-plan.md](ms110d/wn8-program-plan.md) (now historical); ledger updated in [mode-validation.md](mode-validation.md).

### 2026-07-31 — WN8 redesign program registered: the receiver-only attack on the last MS110D Poor point

Tom directed the next Fable program at the WN8 (16QAM r3/4) Poor verdict — chosen over AFC for the CFO-fragile modes (#116) and a WN7 outer-coding/ARQ program. Registered before any DSP work in [ms110d/wn8-program-plan.md](ms110d/wn8-program-plan.md): legs W0–W6, receiver-only scope (the wire stays byte-exact App D), Phase B's full discipline carried (registration-first, ceilings before implementations, corpse before battery, banked-negative clearances, guard pins), and three acceptable exits — hard-gated, improved-measured-only, or measured-infeasible closed like WN7's verdict. Two founding observations from the program design: the b34 "genie indistinguishable" read conflated perfect channel *observation* with perfect channel *knowledge* — no instrument has ever injected the true per-symbol tap trajectory into the detector, and `Ms110dChainBcjr` already accepts per-position h1/h2/noise spans, so the decisive feasibility bound (W1, with a pre-committed kill rule that can close the whole program as infeasible before any architecture is built) is a cheap instrument; and the Table D-VII outer ring is an exact 12PSK — x¹² = 1 for all 12 outer points, scramble-invariant — giving a label-free mid-frame channel observable (windowed y¹²) that Phase B never tried, precisely the information the B3.4 bootstrap verdict said "only labels provide". W0 first re-baselines the byte-identity battery on current main (which has moved past b38: the #103 AGC, the OTA-era work).

### 2026-07-27 (night) — MS110D full on-air campaign: every masked waveform meets its mask over real RF

With the OTA rig re-founded (M0LTE.Flex 0.8.0, the DAX transmit route, RSP1 capture), the first off-air lab campaign found and fixed the two defects only real RF could show: WN2's DFE dead-init at low absolute input level (#101, closed by the input signal-level AGC in PR #103) and the WN6/WN13 Poor collapse traced to the rig's reference phase noise (#102, closed by GPSDO-disciplining the Flex — a rig fix, not a modem change). The full 18-run AWGN+Poor sweep then landed clean (PR #109): **every waveform with a defined mask decodes at or below it on the real rig**, AWGN thresholds monotonic WN0 −3.5 dB → WN7 +8.8 dB; WN7/WN8 Poor were not run — their masks (+19/+23 dB regions) sit above the rig's ~15–16 dB ceiling at 3.7 W, so those two Poor points remain sim-only by physical necessity. This forwarded WN0/WN1/WN3/WN5 from not-yet-on-air to working in [mode-validation.md](mode-validation.md); evidence in [ms110d/evidence/2026-07-27-110d-full-campaign/](ms110d/evidence/2026-07-27-110d-full-campaign/) with the lab-campaign and poor-validation dirs beside it.

### 2026-07-27 — M0LTE.Flex 0.3.0 → 0.8.0: the DAX transmit source becomes load-bearing

The Flex client dependency moves 0.3.0 → 0.8.0, picking up docs/flex-integration.md §10's findings as library behaviour: `FlexStation.SetUpHeadlessAsync` now sends `transmit set dax=1` and reads it back — on 0.3.0 the DAX transmit path never modulated anything (the transmitter's audio source defaulted to the mic, and every DAX enable step returned err=0 regardless). No source changes were needed for the API breaks (0.4.0's `VitaPacket` doesn't touch our `byte[]` mock wiring; 0.8.0's waveform-options split doesn't touch the DAX path we use). Consumer side: `FlexDevice.OpenAsync` treats a non-null `TransmitSourceWarning` after headless bring-up as a failure and throws — a modem that keys and transmits mic silence is dead, not degraded — and the daemon reports the radio's global transmit filter read-back at startup (it, not the slice, limits transmitted audio bandwidth; we deliberately never set it). M0LTE.Radio.Audio stays pinned at 0.1.0 (0.8.0 pins the same). `MockFlexRadio` now models the transmitter source at its real default of dax=0, so the Flex mock loop tests are the offline proof the selection works.

### 2026-07-27 — MS110D Phase B formally closed: 8 of 10 Poor points hard-gated; WN7/WN8 close measured-only

PR #98 lands [ms110d/phase-b-closeout.md](ms110d/phase-b-closeout.md) as the summary of record for the B3/B4 program (PRs #70–#95, #97). WN0–6+13 hold design §6's at-mask bar as **default-armed hard gates** — canonical and disjoint seed families at full §5.3 budgets, Phase A regressions green throughout, 6M-bit default budgets for WN2/WN5/WN6 per the B4 false-red criterion. WN7 closes **measured-only at 2.56E-5/1.48E-5** against the 1E-5 mask: B3.9's anatomy of all 131 residual errors prices the residual as the waveform's own fade-lottery floor — the shipped iterated decoder beats its own oracle bounding instrument, every error is an honest-erasure co-location lottery over the interleaver — so reducing it requires added information (diversity, retransmission, outer coding) outside this demodulator. WN8 closes **measured-only at coin-flip** behind two walls: a 9.2E-4/2.5E-4 true-label model ceiling (92×/25× over mask — the equalizer-plus-chain class fails even with perfect labels) and a bootstrap basin with no label-free crossing; the lever class named for any successor is waveform-processing redesign (FD equalization, pilots-in-data, per-symbol tracking) plus a bootstrap story. The closeout's §5 banked negatives (do-not-retry) and §6 guard-pin registry are binding on any future demod change. Hermetic suite 697/0 (105 env-gated skips).

### 2026-07-24 (evening) — MS110D Phases B1+B2 closed: all four broken-tier mechanisms confirmed, then the science core lands WN4 (and WN13) Poor at mask

B1 (PR #72): every broken-tier point got a confirmed mechanism from ≥3 independent instruments before any fix — WN7/WN8 intra-frame rotation collapsing DD tracking past the ±22.5°/±16° decision half-angles with the anchored probe solve as the persistence mechanism, WN6 a rate-3/4 code cliff on shared physics (its uncoded SER BEATS passing WN13's), WN0 coherent Walsh detection wasting the channel's 2-path diversity (Poor 8× worse than flat Rayleigh; echo refuted by a zero-error static 2-path run). Autopsies in [ms110d/phase-b-autopsies.md](ms110d/phase-b-autopsies.md).

B2 (branch `ms110d-phase-b2`, evidence [ms110d/evidence/2026-07-24-phase-b2/](ms110d/evidence/2026-07-24-phase-b2/)): the science core from those mechanisms. First pass: fading frames equalize on a per-symbol tap trajectory interpolated between the bracketing probe solves (rotation as a phase ramp — the block-buffered architecture's free non-causality), a per-probe gain-1 phase re-anchor kills the anchored solve's steady-state lag, RLS is decision-gated and tracks only the residual, collapse recovery restarts tracking once per unhealthy episode. Turbo: the 2^delay BCJR replaced by a **chain-decomposed exact BCJR** (d independent memory-1 chains, M states each — exactness pinned by brute-force marginalization; echo ceiling and BPSK restriction both gone) running for every PSK mode with a **scrambler-exact echo model** (the legacy lag correlation was phase-scrambled toward zero — a latent model defect) and per-position h1 from re-encoded mid-frame references. **The B2 exit gate holds: WN4 Poor 7.83E-6 canonical / 8.14E-6 disjoint at 6.39M bits each under the full §5.3 rule; WN13 Poor 5.70E-6 canonical banked early.** Poor movement vs B0: WN2 430× (the U=48 turbo exclusion WAS the bottleneck), WN5 2.2E-2 → 5.6E-6, WN13 6.2E-4 → 5.5E-6, WN6 3× down the cliff; WN7/WN8 await B3. §B2.4 delivered: [ms110d/rls-vs-nlms-report.md](ms110d/rls-vs-nlms-report.md) — frame-tied λ stands, NLMS confirmed retired from the signal path. Carried to B3: the WN13/WN6 catastrophic-burst tail (rotation-invisible to correlation magnitude — B3.2's entry autopsy), the K=48 genie instrument defect, WN2/WN5 full-budget closure, the whole-table re-measure.

### 2026-07-24 (later) — MS110D Phase B0 closed: instruments live, and the RLS freeze fix puts WN3 Poor at mask

Stage B0 of [phase-b-plan](ms110d/phase-b-plan.md) executed and closed in one session (branch `ms110d-phase-b0`; evidence [ms110d/evidence/2026-07-24-phase-b0/](ms110d/evidence/2026-07-24-phase-b0/)). The instruments: a **channel-truth genie** — the demodulator takes a noise-free copy of the same Watterson realization (same seed at SNR=∞; the rig draws gains before noise) and runs all channel estimation on truth while detection stays noisy, yielding the perfect-channel-observation bound per point — proven inert by a bit-identical seam test and calibrated the hard way (the first fading run came out 26× WORSE than baseline: noiseless rows give the zero-forcing solve, so the demodulator now measures σ² = mean |noisy−clean|² between its rings and restores the σ²·Σweight Gram term — the true-channel MMSE bound, validated on the static rig and WN4); **evidence-line telemetry** (uncoded channel-bit SER vs the re-encoded TX stream, deep-fade error concentration against the rig's recorded tap trajectory, turbo outcome counters); an **off-rig discipline harness** ({1 ms, 0.5 Hz} / {3 ms, 2 Hz}, measured-never-gated). Honesty remainders from #64/#65/#67 closed, chief among them the **weighted-RLS weight/P asymmetry**: advisory (0.1-weight) rows shrank P at full confidence while barely moving taps, progressively freezing adaptation on every static/AWGN span — fixed to consistent weighted RLS (scale-invariant in the weight), regression-pinned by new DfeTests that fail on the old rule.

The fix moved the table: **WN3 Poor 8.7E-3 → ZERO errors in 3.19M bits, confirmed at seed+10000 — the first Poor point at mask**, under the full §5.3 rule on two disjoint seed sets. WN4 straddles (1.91E-5 canonical / 3.23E-6 disjoint); WN1/2/13 improved ~1.5–2×; the broken tier (WN6/7/8) unmoved as expected (fading frames ran at weight 1, untouched). Phase A hard gates all re-pass at full budget on the B0 code (AWGN 10/10 zero errors, static clean, Doppler clean). First genie science: WN4 Poor's genie bound is only 2× better than measured — its residual gap is **detection**-dominated, not tracking. The telemetry hands B1 its autopsy leads: WN7's errors are fade-UNcorrelated with turbo never converging (0c/88r) and coded BER worse than uncoded — an LLR-chain defect signature; WN8 likewise fade-uncorrelated; WN0 barely codes (1.42E-1 uncoded → 1.12E-1 coded). Also flagged off-rig: WN4 collapses at {3 ms, 2 Hz} (BER 3.0E-1) while clean at {1 ms, 0.5 Hz}. B1 order revised on evidence: WN7 → WN8 → WN6 → WN0.

### 2026-07-24 — MS110D Phase B planned: the program to make the Poor column true

Phase B (design §6 gate: **D-LXIV at mask, no allowance, AWGN + Poor, WN0–8+13, Phase A regressions green**) now has a program plan — [docs/ms110d/phase-b-plan.md](ms110d/phase-b-plan.md), distilled from #69/#64/#65/#67 on top of the closeout baseline. The organizing observation: the 10-point Poor baseline sorts into three regimes — **near** (WN4 at 2.4× mask, WN13 at 62×), **structural** (the BPSK ladder + WN0, 870–8 100×, known physics deficits: stale-across-the-frame channel snapshots, BCJR excluded for U=48 and all QPSK+, WN0's coherent-only Walsh path), and **broken** (WN6/7/8 at 0.13–0.50 BER — random output is a defect, not a weak equalizer) — and the stages follow from it. B0: instruments first — a channel-truth genie exported from the Watterson rig (splits every deficit into tracking vs detection vs infeasible-as-architected), fade-correlated error telemetry, an off-rig discipline harness, and the #64/#65/#67 honesty remainders. B1: time-boxed autopsies of the broken tier — no fix without a written mechanism. B2: the science core — a time-varying channel representation (per-position h1/h2, probe-anchored phase/gain ramps; retrospective interpolation is free in the block-buffered architecture) and the **chain-decomposed exact BCJR**: for the sparse model h1 + h2·z⁻ᵈ the symbol graph splits into d independent memory-1 chains of M states each, which kills the 2^delay state ceiling (#64's 3.3 ms echo limit) and the M^L cost that made QPSK/8PSK BCJR look unaffordable (#69) in one move. B3: family closure — BPSK ladder (WN4→3→5→1/2) → QPSK (13→6) → 8PSK → 16QAM, with WN0's detector mini-program in parallel. B4: the `MS110D_POOR_GATED=1` flip with full evidence. Hard gates along the way: B2 exits on WN4 Poor at mask under the full §5.3 rule; every family closes full-budget + disjoint-seed before the next starts; stalling at mask+2 dB triggers a written stop-and-reassess, not more tuning. Phase A audit discipline carried forward: no constant may encode the rig, genie numbers always labelled, the Phase A evidence set re-runs before any demod-path merge.

### 2026-07-23 — MS110D: the equalizer campaign audited and repaired; Phase A formally closed

The unrecorded 2026-07-19→23 span, then the closeout. Landed in that span: PR #61 (IL2P deframer reset on DCD falling edge), #59 (the 110D PDF vendored beside the transcriptions), M0LTE.Ardop 0.2.0, PR #60 (**Phase B modulations pulled forward** — 8PSK WN7 / 16QAM WN8), and a four-day equalizer performance campaign (~45 commits, run by an experimental agent): RLS tracking, 3-pass bidirectional equalization, turbo re-equalization, a BCJR MAP equalizer for BPSK, several generations of flat/fading gating — plus the xUnit v3 + MTP migration (#62) and a parallel mask runner. The campaign's headline claims: AWGN 10/10, Poor WN4 7.04E-6.

**The closeout audit (Tom: formally close Phase A + review the campaign) retired those numbers and repaired the instruments.** An adversarial dual review (modem source + test side) found: a **blocker** — `TurboReequalize` re-read every Long-interleaver block from a 6.83 s sample ring the blocks outrun (WN1/2 10.24 s, WN5-8/13 7.68 s), silently re-equalizing the head frames against overwritten samples, with the outer code bridging the resulting erasures (WN5's marginal 7.69E-5 era explained); the mask harness's **evidence chain broken** by the migration (per-WN processes each re-running the 3M-bit static gate; vacuous per-point passes possible; the Poor smoke mathematically unpassable at its default budget; the Poor gate silently re-hardened against §6; the §5.3 600 s fading floor plumbed but never enforced); **CI red since the migration with zero tests executed** (MTP apphosts couldn't resolve the runner's .NET; VSTest filter syntax silently ignored; the aspiration scoreboard permanently vacuous); and a set of **rig-fitted heuristics** — the BCJR echo delay hard-coded to the D.6.1 Poor rig's 2 ms path spacing, a "fading" detector that was actually a residual-CFO detector (0.005 rad on the probe-to-probe tap rotation), IsFlatChannel measuring a noise-floor FF tap and structurally unable to return true for UltraShort interleavers, bidirectional passes 2/3 equalizing the frame head through feedback taps filled with its own tail, turbo with no divergence protection, and steady-state allocation throughout the per-frame hot path.

**Tom's direction: make it right, not document-and-defer.** Fixed on `ms110d-phase-a-closeout`: ring sized past the longest Long block + staleness backstop (839be92); harness evidence chain rebuilt — method filters, per-point MS110D_MASK_LOG evidence, Poor measured-by-default with MS110D_POOR_GATED=1 as the Phase B switch, SMOKE labelling, 600 s floor enforced (cfd9fd0); CI repaired (dbeb73e); burst-state leaks cleared (aed1f03); the equalizer de-rigged (05c92b4) — CFO-immune fading detection classified by recurring excursions over a min-tracking floor (the EnergyBusyDetector pattern; validated 0/1664 false-positive frames on AWGN, 0/4096 on static incl. the convergence transient, 152/256 detecting on Poor), searched BCJR echo delay with a significance floor (capped at lag 8 = 256 trellis states — the first cut searched to lag 24 and OOM'd the box at 2^24 states, the constraint the old constant was silently load-bearing for), bidirectional decision-history re-seeding, turbo fixed-point revert, per-dimension noiseVar, dead QAM16 paths made explicit throws; DD training rows preserved across turbo via Dfe.Snapshot/RestoreTraining (ff1d832); hot-path allocations removed with bit-identical numerics proven (c4b83a5/a4b72e3/9f20175, closes #66); design §5.1/§5.3 restated in place to match the shipped instruments (8c0f924). Deferred, with issues: **#64** (what remains of the rig-fitting: RLS λ deviation, weight/P asymmetry, the 2-tap BCJR model's ≤3.3 ms echo ceiling), **#65** (per-position h1[] time-invariance), **#67** (coverage gaps). Test additions: a clock-skew rig (windowed-sinc resample) measuring **±50 ppm met with ~14× margin** (breaking points ±700 ppm on ~4 s bursts, ±300–400 ppm on ~11 s — the design figure the implementation had disclaimed turns out to hold), hermetic ±75 Hz CFO green across all four modulation families, 23 new WN×interleaver×K matrix rows covering every distinct (size, increment) cell of D-XXXVII/D-LI, and WN7/8 joining the interleaver permutation check — Ms110d namespace 161→198 tests. Mask sweeps gained intra-point parallelism (MS110D_MASK_WORKERS — disjoint-seed workers per point, counts summed; the low-rate tail points drop ~N×) and a disjoint-seed verification knob (MS110D_MASK_SEED_OFFSET).

**Fresh full-budget evidence on the final code (§5.3 as restated; fleet OOM-hardened after the box killed two sessions mid-sweep):** **D-LXIV AWGN 10/10, every point ≥3M bits with ZERO errors** (97.5 % Poisson upper bounds ~1.2E-6, an 8× margin under the mask) — including the first-ever full-budget WN1/WN2 runs (previously banked at 500k bits). Static WID2 (0/3/9 ms @ 9 dB): **PASS, 0 errors in 3,018,912 bits**. Doppler: **3/3 clean**. Disjoint-seed cross-checks (AWGN WN4/5, Poor WN4 at seed+10000): **AWGN both 0 errors at full budget; Poor WN4 1.33E-5 vs canonical 2.36E-5 — statistically consistent; nothing is a seed artifact**. Poor (measured-not-gated, the Phase B baseline): the **first complete 10-point Poor baseline ever banked** — WN0 8.1E-2, WN1 2.85E-2, WN2 3.67E-2, WN3 8.7E-3, WN4 2.36E-5, WN5 2.2E-2, WN13 6.2E-4, WN6/7/8 catastrophic as documented (Phase B: QPSK/8PSK BCJR + 16QAM carrier recovery). Pre-fix baseline banked for comparison (scratchpad + closeout doc): AWGN WN0/4/5/6/7/8/13 all 0-error at 3M pre-fix too; Poor WN4 was 1.88E-5 pre-fix — the WN4 delta (claimed 7.04E-6 → de-rigged 2.36E-5) is the measured price of removing the rig-fitted heuristics, chiefly the channel-matched BCJR delay. **The evidence run also caught a receiver-killing acquisition bug** — at −1 dB a noise-corrupted WID can pass its checksum yet name (WN 0, UltraShort), which Table D-XXXVII does not define, and `TryReadPreamble` let `Get3k`'s exception escape the receive path (the actual cause of every historically "stuck" Poor WN0 run; on air, a daemon crash from unlucky noise). Fixed with `Has3k` pre-validation (ae3998c) and proven against the deterministic seed-500 reproducer, which now completes at the historic 7.7E-2. **Phase A is closed** — docs/ms110d/phase-a-closeout.md is the record; completion-roadmap.md superseded; README claims made exact. Full hermetic suite 541 pass / 0 fail / 42 env-gated skips; landed as PR #68.

### 2026-07-18 (later⁴) — differential + frequency-diversity bank is the BPSK default (reverses #5, per #40/#42)

Benchmarked our BPSK decode against GB7RDG's NinoTNC on the live 40 m channel (same off-air
audio; the NinoTNC's frames off MQTT as ground truth; `tools/Packet.SoundModem.NinoCompare`).
Over a busy 2-hour, 3-node window: a single differential modem copied **116/117** NinoTNC
frames; the **differential frequency-diversity bank (`BpskMultiModem`, pairs=4) copied 117/117
and decoded 2 more the NinoTNC missed** — 100 %, matching and slightly beating the reference.
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

### 2026-07-18 (later³) — the general convolutional codec folds into M0LTE.Fec 0.2.0

`Ms110d/Fec` was a mix: a general rate-1/2 tail-biting convolutional codec
(`ConvolutionalCode` / `TailBitingEncoder` / `TailBitingViterbiDecoder`) and the
MIL-STD-188-110D-specific puncture/interleaver tables. The general codec moved into
**M0LTE.Fec 0.2.0** (it belongs next to the block codes there); the 110D tables
(`Ms110dPuncture`, `Ms110dInterleaver`) stay in `Ms110d/Fec`. This repo bumps M0LTE.Fec to
0.2.0 and the Ms110d modulator/demodulator/framing/puncture (+ its test) now `using M0LTE.Fec;`
for the codec. Build clean, 397 pass / 31 skip.

### 2026-07-18 (later²) — settable audio centre for the narrow modes (issue #39)

The narrow modes' audio centre is now **variable per modem**, QtSoundModem-style, on both TX and
RX — `--modem N:MODE:FREQ` (already the CLI shape) and config `"frequency"` now reach every
variable-centre mode. Covers the AFSK tone-pair modes (afsk*, centre = mark/space midpoint,
default 1700) and the BPSK/QPSK carrier modes (bpsk*/qpsk*, default 1500; 1650 for qpsk3600). The
GB7RDG signal that sat ~41 Hz off our fixed 1500 (the finding behind #39/#40) is now correctable in
the field.

- **Real bug found completing the plumbing:** all three AFSK1200 modems (`Afsk1200Modem`,
  `Afsk1200Il2pModem`, `Afsk1200MultiModem`) constructed their `AfskModulator` with the hardcoded
  Bell-202 1200/2200 tones, so **TX ignored `centerFrequency`** (only the demodulator honoured it) —
  a mistuned own-transmission at any non-default centre. Fixed to `centre ± 500` so both sides move
  together (`Afsk300Modem` was already correct — it was the reference). Identical output at the
  1700 default; only the previously-broken off-centre path changes.
- The PSK factories `BpskModem.Bpsk300/Bpsk1200` and `QpskModem.Qpsk600/2400/3600` gained an
  appended `carrierFrequency` param (append-only — the private ctor always took the carrier; the
  positional NinoBench callers are undisturbed). `Program.cs` passes `frequency ?? default` through.
- **Fixed-centre modes now reject a `:FREQ` at start-up** rather than silently ignoring it: the
  baseband FSK families (fsk*/c4fsk*, DC-to-Nyquist, no audio centre) and the spec-fixed waveforms
  (freedv-*/ms110d-*, POCSAG, ARDOP). Guard covers both the CLI and config paths.
- `Modems/CentreFrequencyTests.cs` (14 cases): every variable-centre mode round-trips a frame at a
  shifted centre; the PSK carrier modes additionally must NOT decode at the default centre (proving
  the override genuinely moves the signal — the AFSK tone modes are deliberately offset-tolerant, so
  that stricter check is PSK-only). Verified end-to-end on the real NinoTNC bpsk300 recording:
  `--modem 0:bpsk300:1500` → 4 frames, `:1200` → 0. README / soundmodem.example.json / DaemonConfig
  document the coverage. roadmap #39 marked RESOLVED.

### 2026-07-18 (later) — POCSAG codec lifted into M0LTE.Pocsag; consume it

The POCSAG paging **codec** (`PocsagCodeword/Encoder/Decoder/Message/Page`, + a bundled copy
of the own-code `BitDpll`) was extracted into the standalone **M0LTE.Pocsag** package (AGPL,
spec-first CCIR RPC No.1; depends on M0LTE.Dsp). This repo now consumes it and keeps only the
daemon glue: `Pocsag/PagingTcpServer.cs` (bound to `SoundModemChannel`) and its integration
test, both switched to `using M0LTE.Pocsag;` — as did `Program.cs` and the `sm-pocsag` tool.
The moved codec source + unit tests were deleted; the multimon-ng runner the paging test
needs was split out into `tests/…/Pocsag/MultimonNg.cs`. `Modems/BitDpll.cs` stays (the
modems use it). Build clean, 371 pass / 31 skip.

### 2026-07-18 — consume M0LTE.Dsp / M0LTE.FecLdpc / M0LTE.Ofdm; drop the duplicated source

Third extraction flip (after Flex, then Fec/Il2p/Ardop): the **DSP primitives**, the **LDPC
codec** and the whole **OFDM modem** were each lifted into their own repos/packages —
**M0LTE.Dsp**, **M0LTE.FecLdpc**, **M0LTE.Ofdm** (all published **0.1.0** to nuget.org). This
repo now **consumes all three** instead of carrying the code:

- Deleted `Dsp/{Fft,FirFilter,FilterDesign,Decimator,Upsampler,SpectrumSource}` (**kept
  `Dsp/ConstellationSource.cs`** — it depends on `Modems.IConstellationSource`, so it stayed
  out of the package; it now takes `using M0LTE.Dsp;` for `Fft`), all of `Fec/Ldpc/` (7 files
  — this supersedes the previous entry's "kept `Fec/Ldpc` in-repo": it is now the
  `M0LTE.FecLdpc` package), and all of `Ofdm/` (12 files, including `Cf.cs` — `Cf` now lives in
  `M0LTE.Ofdm`). Added `PackageReference`s to M0LTE.Dsp/FecLdpc/Ofdm (all 0.1.0).
- Swapped `using Packet.SoundModem.{Dsp,Ofdm,Fec.Ldpc}` and `Packet.SoundModem.Tests.Dsp`
  (where the `OccupiedBandwidth` helper lived) to the `M0LTE.{Dsp,Ofdm,FecLdpc}` equivalents
  across src/tests/tools; `Ms110d/*` + `Modems/FreeDvDatacModem` (the `Cf` consumers) and the
  `Modems`/`Channel`/`Pocsag` FFT/filter users came along. `SoundModemChannel` keeps both
  `using M0LTE.Dsp;` (`SpectrumSource`) and `using Packet.SoundModem.Dsp;` (`ConstellationSource`).
- Deleted the moved unit tests (`Dsp/{DecimatorTests,SpectrumTests}` + the `OccupiedBandwidth`
  helper, all of `Fec/Ldpc/`, all of `Ofdm/` — pure tests of the moved types, now living and
  passing in the package repos). **Kept + reused** the tests that exercise types that stayed:
  `Dsp/{UpsamplerTests,OccupiedBandwidthTests}` (modem + NinoTNC-fixture + `WavFile` cases),
  the OBW tests (`Pocsag`/`Ms110d`/`Ardop`, which use the package's `Fft`/`OccupiedBandwidth`),
  the Watterson-channel helper and `ConstellationTests`.
- Licences unchanged (this repo stays GPL-3.0). Out-of-solution generators `tools/oracle` and
  `tools/gen-ldpc-tables` were left as-is (not in `pdn-soundmodem.slnx`; the LDPC-table
  generator is now the `M0LTE.FecLdpc` repo's concern).

Suite 407 pass / 31 skip. On branch `dsp-fecldpc-ofdm-to-nuget`.

### 2026-07-17 (later still²) — consume the M0LTE.* packages; drop the duplicated source

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

### 2026-07-17 (later still) — FlexRadio client lifted out into the M0LTE.Flex NuGet package

The whole FlexRadio client (session/discovery/VITA-49/DAX/station/PTT + mock) was extracted
from this repo into its own standalone repo and package — **`M0LTE.Flex`** (AGPL-3.0-or-later,
github.com/M0LTE/M0LTE.Flex), published **0.1.0** to nuget.org via Trusted Publishing, with a
build-time public-API lock and a SemVer policy. It was clean to lift: the code was MIT-derived
(KC2G nDAX/nCAT/flexclient, HB9FXQ flexlib-go) with near-zero coupling — only the tiny
audio/PTT seams. This repo now **consumes the package** instead of carrying the code:
`src/Packet.SoundModem/FlexRadio/` keeps only the daemon glue (`FlexDevice` — the `flex:`
device-string parse + bring-up) plus three thin adapters (`FlexAudioAdapters.cs`) that
re-present the package's `M0LTE.Flex.IAudioInput/IAudioOutput/IPttControl` through this modem's
`Packet.SoundModem.Channel` seams. The 9 client files + 7 client tests were deleted; the 3
glue/loop tests stay. **Licence note:** M0LTE.Flex is AGPL-3.0; adding it to the GPL-3.0 core
is permitted by GPLv3 §13, which carries AGPL §13 (network-source) onto the combined work —
Tom signed off. Full suite green (913 pass / 97 skip). Behaviour unchanged; `flex:mock` and the
byte-exact modem-loop-through-mock both still pass through the package.

### 2026-07-17 (later) — FlexRadio client: offline Phases 0–2 land (session/DAX/PTT + mock)

PR #37: the pure-managed FlexRadio 6000-series client (design PR #32, Route A) — `--device
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

### 2026-07-17 (later) — MIL-STD-188-110D App D Phase A lands: 3 kHz waveform, mask-gated

PR #34: the App D 3 kHz serial-tone waveform (Walsh-75/BPSK/QPSK) — pure-managed C#, built on
the dual-verified tables and critique-folded design. No open App D implementation exists, so a
from-scratch Watterson/CCIR channel simulator gated against the spec's D-LXIV SNR masks is the
acceptance instrument. **Independently re-verified at full budget (3M bits/point): all 12 gated
points 0 errors** — AWGN WN0–6+13 at their mask SNRs, Doppler ±75 Hz, Static WID2 0/3/9 ms @
+9 dB. Two late failures were root-caused (off-cursor DFE taps fitting noise in the K=48 class;
MMSE-ridge fix scoped so the eight green modes stay byte-identical) not fudged — and an earlier
CFO-trim sign error was caught by the masks after passing hermetically (the discipline earning
its keep). The Static WID2 5 dB figure was a house bar (spec's static SNR untranscribed, D.6.3
"Not yet standardized"), honestly restated to +9 dB with the remaining margin assigned to Phase
B RLS. OBW ~2.89 kHz about 1800 Hz. Built across a Fable→Opus handoff (Fable's spend limit hit
mid-build; Opus picked up the checkpointed branch with no loss). Suite 733→878; the one flaky
failure is the pre-existing ARDOP TCP race (issue #33). Phase A = a 110D waveform that
previously ran only on RapidM/Rockwell hardware now decoding on a soundcard. Next: Phase B
(8PSK/16QAM + RLS DFE) or another roadmap thread per Tom.

### 2026-07-17 (later) — 110D ledger cleared: every constant dual-verified; Phase A unblocked

PR #31: the design's 13-row transcription-debt ledger is cleared — all remaining Appendix D
figures/tables (K7/K9 encoder figures, PSK transcoding, U/K geometry, preamble tables, both
256-digit PN arrays, probe bases, the 3 kHz interleaver set, the worked example) transcribed
twice independently and value-diffed with **zero conflicts**, including full agreement on all
512 PN digits (which have no structural oracle — the dual read IS their verification). Ledger
errata applied (D-XXV numbering, Walsh-prose location, D-XIV settled 10→0044/11→0440, L8
correction). Operational note: the first transcriber-A run was killed after an hour of
in-context triple-reading with nothing written (32 MB transcript, zero files — a digit-fidelity
risk under compaction); the fresh run under write-immediately/checkpoint discipline finished in
18 minutes — the discipline is now part of the standing sub-agent policy. **No 110D constant
remains provisional; the Phase A build starts now** (3 kHz framing + Walsh-75/BPSK/QPSK + LMS
DFE per design §6, Tom's §10 decisions folded).

### 2026-07-17 — ARDOP Phase D: host interface + Pat — the ARDOP stack is software-complete

PR #30: the ardopcf-compatible virtual-TNC host interface (command/reply/notification formats
byte-for-byte, quirks preserved), command+data sockets, RXO monitor mode, and daemon
integration (`--ardop`, dedicated-channel policy). Validation: a 107-command conformance
script **byte-identical** vs live ardopcf (VERSION excluded by design); **real Pat v1.0.0
delivered a full B2F message through our daemon** to ardopcf; scripted full-stack sessions
both roles byte-exact (sequences pinned from wl2k-go's transport source); live RXO monitored a
third-party ardopcf↔ardopcf session (25 frames, all data + ACKs). Hermetic suite 723→741
(verifier 733/0); five live legs green in one run. With Phases A–D merged the ARDOP stack is
**software-complete at ardopcf parity** (waveforms 0 dB knee delta, ARQ live both roles, host
interface Pat-proven); the only remaining item is the on-air acceptance from GB7RDG's HF port
on the 40m UK packet channel (task #6). Next build thread: MIL-STD-188-110D App D Phase A on
the landed design (ledger figures to dual-transcribe first).

### 2026-07-16 (later still¹⁴) — ARDOP Phase C: PSK/16QAM at ardopcf parity, 0 dB knee delta

PR #29: differential 4PSK/8PSK + 16QAM TX+RX on 1/2/4/8 parallel carriers; FSK-only ARQ guard
removed — full gearshift ladders. Tom's all-in bar met and exceeded: **noise knees
trial-identical to ardopcf at every swept point (0 dB delta; bar was ~1 dB)**; 59/59
payload-exact both directions across every PSK/QAM type/bandwidth (+offset+noise rows); TX
within 2 LSB of ardopcf's own --writetxwav; **live mixed-mode ARQ both roles, 4 KB byte-exact
each way, 0 NAKs**, ladders climbing to 4PSK/8PSK.2000; OBW never wider (17/17 rows). Honest
corners recorded: the 2000 Hz quality-85 top-out is ardopcf-parity (verified in their decoder);
16QAM.2000 proven by fixtures/knees/OBW, not live sessions (same on both implementations);
knees AWGN-only per the design. Hermetic suite 610→723 (715 verifier-env), env-enabled 795/0.
Remaining: Phase D — the 8515/8516 host protocol + daemon integration + Pat, then the
GB7RDG/40m on-air acceptance.

### 2026-07-16 (later still¹³) — 110D App D design doc: implementation-ready, critique-folded

PR #28: docs/ms110d/design.md (52 k chars) — the App D 3 kHz waveform design on the
dual-verified tables, produced by a 3-section → adversarial-critique → assemble workflow run
in parallel with ARDOP Phase C (Tom: "the box isn't that busy"). All 12 critique findings
folded, none deferred; the provenance BLOCKER resolved with real forensics (everyspec stamps
each download's PDF trailer /ID — the doc pins the permanent PDF ID + a stamp-invariant
SHA-256; README corrected). K=9 polys corroborated against the published (561,753)
max-free-distance code; the interleaver direction pinned by a wire-side worked-example test;
a 13-row transcription-debt ledger gates encoder code on formally-transcribed figures; the
no-oracle validation ladder gets a loopback-blind checklist (L1–L12) + a statistical budget
vs the transcribed D-LXIV/LXV masks. Native rate 9600 Hz; phasing A (Walsh/BPSK/QPSK + LMS
DFE) / B (8PSK/16QAM + RLS) / C (high QAM, groundwave-gated). §10 = three open questions for
Tom. Build remains sequenced after ARDOP.

### 2026-07-16 (later still¹²) — ARDOP Phase B: the ARQ engine, live sessions vs ardopcf

PR #27: the ISS/IRS ARQ session engine (the design's named riskiest block), ported
behaviourally from ardopcf with a virtual-clock architecture (no wall time in the engine →
hermetic sessions ~100× real time; the live path is the same code on the audio clock).
**Live ARDOP sessions ours↔ardopcf over snd-aloop, both roles, green twice** — byte-exact
transfers, orderly teardown (ardopcf's END-session-ID quirk live-confirmed and ported as-is).
Hermetic: exactly-once data with **measured ≥775 ms ACK margin** in the 1500 ms window;
NAK/repeat; Memory-ARQ recovering from two individually-undecodable copies; gearshift;
AUTOBREAK; timeouts — real counts throughout, + 42 pure-logic tests. Suite 557→610 hermetic
(618 with the env-gated oracle/aloop legs), 0 failures. Remaining: Phase C (PSK/16QAM
RX-first, Tom's all-in 16QAM bar), Phase D (8515/8516 host protocol + Pat + the GB7RDG/40m
on-air acceptance per task #6 notes).

### 2026-07-16 (later still¹¹) — ARDOP Phase A: 4FSK codec + FEC mode, 33/33 vs ardopcf

PR #26: the ARDOP 4FSK layer lands (design §6 Phase A, ported from ardopcf with provenance;
600 Bd FM modes folded in per Tom). Cross-validated both directions against ardopcf itself:
**ardopcf→us 33/33** fixtures (payload-exact incl. ±40/±80 Hz and noise variants), **us→ardopcf
33/33** via --decodewav (hex-exact data). CRC-16/CRC-8/RS byte-exact against vectors from
ardopcf's compiled sources; **OBW equal to ardopcf's to the FFT bin** per bandwidth class
(never-wider rule). One design-doc correction found by implementation: ARDOP's RS wire layout
is byte-reversed vs FX.25's (same field/generator) — mapped and proven, documented. Memory-ARQ
averaging included. Suite 419→557. Also landed this cycle: the ARDOP spec as self-contained
Markdown (PR #25, docs/refs/ardop-spec-rev2.md — 15 internal spec inconsistencies flagged).
Next: Phase B, the ARQ engine (both-ends-FSKONLY), the design's named riskiest block.

### 2026-07-16 (later still¹⁰) — MIL-STD-188-110D App D tables: dual-transcribed, zero conflicts

PR #24: the image-only interop-critical tables of 110D Appendix D (the public counterpart of
the RESTRICTED STANAG 5069 — task #7) land in docs/ms110d/, transcribed **twice independently**
(branches ms110d-tables-a/-b, agents forbidden from cross-consulting, per the verified
scoping's demand) and diffed: six of ten files byte-identical (incl. all four constellation
tables), four differing only in formatting — **zero value conflicts**, plus machine self-checks
(constellation symmetry/lattice, puncture ones-counts reproducing code rates, the scrambler
regenerating the printed sequence exactly). Source PDF SHA-256 + method in the README; the -a
branch retained as the independent record. Structural findings: D-VII…D-X are the
16/32/64/256-QAM coordinate tables (PSK uses transcoding tables D-III…D-VI); puncture patterns
are the separate Table D-L. Spec oddities recorded (length-68 mini-probe; a 40 kHz interleaver
table with no 40 kHz bandwidth; "Not yet standardized." acquisition section). Next for task #7:
the App D design doc on these verified values; build sequenced after ARDOP.

### 2026-07-16 (later still⁹) — ARDOP design/scoping lands

PR #23: [ardop-design.md](ardop-design.md) — the FreeDV-style de-risking pass for the open
Winlink path, grounded in ardopcf (@a7c92289, v1.0.4.1.3, MIT — verified; port-from-ardopcf
recommended over clean-room, the spec lacking implementation detail e.g. the nonstandard CRC).
Headlines: exactly one interoperable ARDOP (spec Rev 2.0, 2017; G8BPQ "ARDOP 2" is
OTA-incompatible, out of scope); 18 data modes + ~15 control frames over 200/500/1000/2000 Hz —
4FSK / differential 4-8PSK / 16QAM on 1-8 parallel 100 Bd tone carriers (NOT IFFT OFDM); FEC =
RS + repeats + Memory-ARQ (our ReedSolomon is a direct hit — FX.25's GF config); ≈6-8 k lines
C#. Riskiest: the ARQ timing machine (ACK on-air inside the ISS's 1.5-2.1 s window) and
PSK/16QAM demod robustness. Host interface: byte-compatible ardopcf TCP (8515/8516) so Pat
works unmodified. ardopcf proven as a fully-offline oracle on this box (--decodewav + null-dev
TX vectors, both measured). Phasing: A) 4FSK codec + FEC mode → B) ARQ (both-ends-FSKONLY) →
C) PSK/16QAM RX-first → D) host interface + Pat + bench. OBW: never-wider-than-ardopcf per
bandwidth class. §10 holds the open questions for Tom (bench/gateway logistics, 16QAM bar,
600 Bd FM / RXO scope).

### 2026-07-16 (later still⁸) — POCSAG lands: spec-first paging + the daemon paging endpoint

PR #22: POCSAG (roadmap easy win) implemented spec-first from CCIR RPC No.1 — layout proven by
reproducing the published sync/idle constants from their own data bits; BCH(31,21) exhaustively
verified (all 1/2-bit patterns corrected, all 4960 3-bit patterns rejected). Cross-validated
against multimon-ng: 9/9 pages exact across RIC edge cases, functions, charsets and all three
bauds; polarity pinned to the spec convention. **Interface (Tom's call): no KISS** (pages are
not AX.25 frames; one-way medium) — instead a daemon `--paging <port>` TCP line protocol
(`PAGE <ric> <func> ALPHA|NUMERIC|TONE [text]` → CSMA/PTT TX) with heard pages broadcast as
`HEARD …` on the same socket; a DAPNET-core transmitter client is an explicit non-goal for now.
Internal plumbing added two clean channel seams (a generalised audio TX queue entry +
an RX tap) rather than abusing IModem/KISS. Found + recorded an upstream bug: DAPNET
UniPager's crc() off-by-one cannot reproduce the sync word (PROVENANCE.md). OBW pinned by
absolute bounds (~691 Hz baseband at 1200 bd; no reference recording exists). Suite → 419.
Follow-ups noted: first off-air 439.9875 MHz capture when taken; DAPNET client if ever wanted.

### 2026-07-16 (later still⁷) — Phase 2: all six datac modes complete

PR #21: RX for the narrow modes (datac4/13/14) — the RX band-pass filter
(`find_carrier_centre` float-summation centres 1468.75/1500/1472.22 Hz; the existing
`quisk_ccfFilter` port applied per nin-batch at the rxbuf tail, state persistent across burst
resets) plus mode wiring; the LDPC shortening path already existed. Measured byte-exact both
directions vs stock codec2 tooling (codec2→ours 2/2, 2/2, 5/5 clean and through +22 Hz /
~5.5 dB; ours→codec2 12/12 interop tests), round-trips and IModem green, TX oracle extended to
all six modes (xcorr 1.0). **The OBW rule now covers all six modes** — datac4 (300.8 Hz) and
datac13 (265.6 Hz) measure exactly equal to FreeDV's own vectors. Suite → 378. The FreeDV datac
family is code-complete: six modes, TX+RX, KISS-integrated (`freedv-datac0/1/3/4/13/14`),
CI-guarded OBW, stock-tooling interop in both directions. Remaining: the HF radio loop
(task #4's proven-reliable gate — needs the bench), streaming-mode acquisition for the narrow
modes (unneeded for the burst-mode deployments), low-SNR/multipath characterisation.

### 2026-07-16 (later still⁶) — FreeDV datac as KISS modes: IL2P+CRC on the FreeDV waveform

PR #20: `freedv-datac0/1/3` land as daemon KISS modes on the 48 kHz DSP path (integer ÷6/×6
bridge to the modem's native 8 kHz). **Framing (Tom's call, two iterations): the datac payloads
carry the family-standard IL2P+CRC bit stream** — no invented wrapper (a 2-byte length prefix
and HDLC-in-payload were both considered and rejected; AX.25 itself has no length field, and
the family already solves variable-length-in-fixed-container with IL2P's sync word + byte
count). Frames span packet boundaries, so datac0's 14-byte packets carry full AX.25 frames and
datac1 has no hard cap; `FrameQuality` is the family's real RS/CRC one; the RS layer is largely
dormant today (the datac transport delivers clean-or-missing) but enables a future
salvage-from-CRC-failed-packets path. Waveform untouched — the OBW rule (never wider than
FreeDV's own, PR #18) stays CI-green. Measured: exact IModem round-trips datac0 30/60 B,
datac3 60/124 B, **datac1 60/508/1000 B (first datac1 end-to-end validation; 13.1 s of audio
demodulated in 877 ms)**; back-to-back bursts 2/2; daemon `--wav` smoke green. One opt-in
(default-off) pdn extension for variable-length KISS bursts (`EndOfBurstUwDrop` + CRC
backstop). Suite → 355. Remaining for task #4: real HF-loop validation (burst DCD is ~1 frame
late — EnergyBusyDetector is the CSMA source; datac1's short UW leaves ~10 %/burst odds of a
~4 s phantom-DCD tail; CSMA interaction unmeasured on air — coexistence with regular
FreeDV voice is a NON-goal per Tom: data and voice never share a channel in practice).
Phase 2 (datac4/13/14 RX BPF)
unchanged.

### 2026-07-16 (later still⁵) — burst acquisition: the real-world FreeDV interop loop closes

Burst-mode acquisition lands (PR #19), on top of the Phase-1 modem (PR #17) and the OBW rule
(PR #18 — **our datac TX must never exceed FreeDV's own OBW**, CI-enforced like-for-like against
codec2's checked-in transmissions; standing directive). The standard FreeDV CLI tools and
FreeDATA force burst mode, so this is the path real deployments use: the known-sequence
preamble/postamble correlator (`est_timing_and_freq`), `ofdm_sync_search_burst` with postamble
packet-rewind, and the data-burst state machine — the validated demod core reused untouched.
Measured: codec2 CLI TX → our RX datac0 **5/5 clean and 5/5 at +22 Hz / ~4.6 dB SNR**; **our TX →
codec2's own `freedv_data_raw_rx` 5/5** (the full CLI loop, kept as a Category=Interop test);
round-trips 10/10; the noise knee matches codec2 (19/20 = 19/20 on identical audio); the one
found corner (fully-blanked preamble, single-packet burst) is unrecoverable in codec2 too (0/49
on their own RX) — parity, not a defect. Suite 329→338. The pure-managed datac0/datac3 modem now
interoperates with stock FreeDV tooling in both directions. Remaining: datac1 end-to-end burst,
Phase 2 modes (RX BPF), IModem/KISS (task #4).

### 2026-07-16 (later still⁴) — FreeDV OFDM Phase-1: FEC + engine ported, validated vs codec2

The FreeDV datac OFDM modem built on branch `freedv-ofdm-phase1` as a pure-managed C# port of
codec2 1.2.0 (git 310777b, LGPL-2.1), validated against libcodec2 as a **test-only oracle** (no
runtime native dependency; reference vectors checked into `samples/freedv/`). Design:
[ofdm-design.md](ofdm-design.md); provenance: PROVENANCE.md `Fec/Ldpc` + `Ofdm` rows; R-1
(licence review) is a roadmap task.

- **FEC layer — bit-exact.** LDPC matrices transliterated (`tools/gen-ldpc-tables/gen.py`); the
  phi0 table, RA encoder and sum-product decoder reproduce codec2's **own built-in decode
  vectors bit-for-bit** (all 4 codes that ship one); the frame codec (shortening) round-trips
  all six datac modes. Golden-prime interleaver + CRC-16 (pinned to 0x29B1) alongside.
- **Modulator** (parallel sub-agent, `freedv-ofdm-modulator`) — direct IDFT + CP (not an FFT —
  datac14's M=144 forbids it), symbol assembly, pilots/UW, the Hilbert-clipper/BPF chain, LCG
  preamble. Vs codec2: **xcorr = 1.0 (8 d.p.) all six modes**, ≤1.5 LSB, datac14 preamble
  bit-for-bit; the residual is codec2's own float→int16 truncation.
- **Demodulator + streaming sync** (parallel sub-agent, `freedv-ofdm-demod`) — timing/frequency
  acquisition, pilot channel estimation, LLR demap, sync state machine. Decodes codec2's own
  datac0 TX: **10/10 clean, 10/10 at +45 Hz offset, 10/10 at ±600 ppm sample-clock, 19/20 AWGN**
  (matching codec2); datac3 4/4 (mode-generic).
- Both halves **merged** (reconciled the shared `Cf` complex type); build clean, **suite 218→319
  green**. The two big DSP halves were built by parallel background sub-agents in isolated
  worktrees, each validated against codec2 independently (context-preservation + parallelism).

**datac0 first-light ACHIEVED.** `DatacTransmitter` ports the full TX chain
(`freedv_rawdatacomptx`→`ofdm_ldpc_interleave_tx`: payload → CRC → LDPC encode → QPSK-map →
interleave → assemble UW → modulate); the our-TX→our-RX round-trip decodes datac0 **10/10 clean,
10/10 at +25 Hz offset, 10/10 at ±600 ppm sample-clock**, datac3 3/3 (mode-generic) — and the
transmitter output matches codec2's own datac0 TX to **0.75 LSB / xcorr = 1.0**, the 16-byte
frame **byte-identical**. No TX↔RX boundary fix was needed (the two independently-built halves
agreed first pass). Suite **319→325**. So the pure-managed datac0 modem is proven equivalent to
codec2 on both TX and RX and interoperates end-to-end.

Remaining for Phase 1: the burst/preamble acquisition path (needed for the standard FreeDV CLI
tools, which force burst mode) and datac1 end-to-end. Phase 2: datac4/13/14 (RX BPF + LDPC
puncturing). Phase 3 (task #4): IModem/KISS + the 12k/48k→8k rate bridge.

### 2026-07-16 (later still³) — next-wave modem roadmap + FreeDV OFDM Phase-1 design

Two planning docs land ahead of the next build wave. [waveform-roadmap.md](waveform-roadmap.md)
ranks the candidate modems after two research sweeps (FreeDV/Codec2 OFDM internals + a full
landscape survey) and a verified scoping of MIL-STD-188-110D App D: build order **FreeDV OFDM
datac → POCSAG → ARDOP → MIL-STD-188-110D App D (3 kHz) → own FM OFDM → own HF OFDM**, with the
cannot-implement (VARA/PACTOR/P25/…) and label-only (APRS, CubeSat 9k6) sets, M17 parked, and the
compatibility-labelling rule up front. [ofdm-design.md](ofdm-design.md) is the implementation-ready
Phase-1 design for the lead item: a **pure-managed C# port of the FreeDV datac OFDM modes validated
bit-for-bit against `libcodec2` as a test-only oracle** (not a P/Invoke wrap — the port builds the
shared OFDM sync engine our own FM/HF modes reuse). Six QPSK modes @ 8 kHz/1500 Hz; phasing
datac0→datac1→datac3; OBW CI-enforced per mode; the sync/channel-estimation state machine + the
sample-clock and datac4/13-shortening bit-exactness are the flagged risks. The MIL-STD-188-110D App
D redirect (of the RESTRICTED STANAG 5069 that G4KLX advocated) is public/verified-downloadable but
gated on its no-oracle validation risk and sequenced after FreeDV. Design docs produced by a
research/design workflow; the final synthesis was assembled by hand (multi-agent synthesis failed on
prompt size — the six component designs are preserved verbatim). No code yet — next is the Phase-1
build.

### 2026-07-16 (later still²) — QtSM matrix re-measured under coherent; #6/#10/#11 resolved on evidence

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

- **#11 (qpsk600 marginal)** — *half-resolved.* The qtsm→ours leg is **10/10 under coherent**
  (the differential-era 9/10 was live-path variance — a clean deterministic WAV decode reads
  10/10 on both detectors). The residual is **ours→QtSM 8/10**: QtSM's narrow V26A-600 receiver
  loses a frame or two of our TX. That is receiver-side in QtSM — our `qpsk600` TX is
  NinoTNC-proven (mode 9, 10/10) and stays exactly as-is (widening it to suit QtSM would trade
  away NinoTNC OBW compliance). Characterised, not our defect.
- **#10 (fsk4800-il2p one-way)** — *resolved: the 0/10 did not reproduce.* Under current code
  QtSM's Dire-Wolf RUH-4800 receiver decodes our 4800 GFSK TX **10/10** (reproduced twice —
  committed `samples/pdn` mode-04 and a fresh WAV — with QtSM's own RUH-4800 TX decoding in the
  same setup as a control; QtSM headless-RUH `using48000` patch applied). Timeline rules out a
  stale sample (the FskModem tail-flush acquisition fix landed ~4 h *before* the original 0/10
  measurement). **No change to our 4800 modem** — it is NinoTNC-derived and stays so; it simply
  also cross-validates against QtSM's RUH-4800 now. So 4800 GFSK is bidirectional both with the
  NinoTNC and with Dire-Wolf/QtSM RUH.
- **#6 (qpsk2400 vs QtSM's 2400 maps)** — *confirmed characterisation under coherent.* Our
  V.26A `qpsk2400` decodes QtSM's V26A/DW2400 (type 12) **8/8** and its legacy "QPSK AX.25
  2400bd" (type 10) **0/8** — different phase maps, not a defect. Coherent does not change it.

Per Tom's mid-task directive, every mode is now **compatibility-labelled** (which peer it
interoperates with — universal / NinoTNC+QtSM-V26A / NinoTNC+DW-RUH / NinoTNC+MMDVM) in
docs/qtsm-loop.md § Results, README.md and samples/README.md; NinoTNC interop is never traded
for QtSM interop.

Landed alongside: **`QtsmInteropTests` (`Category=Interop`, 7 cases)** — decodes the checked-in
`samples/qtsm/` WAVs with our modems and asserts the frames (mirrors the NinoTNC/Dire-Wolf
reference-WAV tests); the live headless QtSM rig stays manual. Five new QtSM reference WAVs
checked in (trimmed). Tool reproducibility helpers (no wire/behaviour change, no modem touched,
so no PROVENANCE update, no ax25-ts leg): `sm-decode` gains `bpsk1200`/`qpsk600`/`fsk4800`/
`fsk4800-il2p` (and qpsk3600's loop bandwidth now matches its factory); `sm-samples` gains
`--only <mode>` and `--native-rate` (12 kHz TX for the QtSM rig). Suite 218 → 218 + 7 Interop.

### 2026-07-16 (later still) — coherent (Costas) detection is the PSK default (#5)

Flipped the BPSK/QPSK default from differential to **coherent** detection, matching the
NinoTNC — a `CostasLoop` recovers the carrier's absolute phase and the recovered absolute
symbols are differentially decoded (the wire format is differential and untouched: only the
receiver changes). The differential detector stays as a named opt-in (`PskDetector`
enum on both modems + factories; `--psk-detector coherent|differential` on the daemon).

Done under #5's explicit discipline — **measure, don't merge on theory.** Built coherent as
a selectable path with differential still default, then ran before/after noise + acquisition
sweeps; only after the numbers confirmed the gate did the default flip. Measured (our-TX to
our-RX, 40 trials/point): coherent beats differential on noise for **every** mode — decode
counts, e.g. qpsk2400 σ0.25 8→18, qpsk3600 σ0.15 11→25, bpsk1200 σ0.35 20→35, qpsk600 σ0.40
22→34, bpsk300 σ0.60 27→35 — the ~1–2 dB the theory predicts. Acquisition: coherent pulls in
within ~50–80 ms after idle (qpsk2400 50, qpsk3600 80, the rest 0), well inside the NinoTNC's
~100 ms; on a clean cold channel it acquires at 0 ms. The accepted trade (per #5): the
differential detector's 0 ms-after-idle acquisition and its wider frequency-offset tolerance.

Two measurement-driven tuning findings. (1) **Loop bandwidth is per-mode.** A single fraction
does not fit: bpsk300's carrier-offset pull-in needs ≥0.06×baud (its noise is flat, being
heavily oversampled), while qpsk3600 at 0.06×baud (108 Hz, 6⅔ samples/symbol, 0.25 roll-off)
tracks noise and loses even at low SNR (25/40 at σ0.08 where 0.03×baud scores 40/40). Default
is 0.06×baud; qpsk3600 overrides to 0.03×baud. (2) **The QPSK Costas detector nulls at the
diagonals**, so the recovered constellation locks to 45/135/225/315° — the quadrant decision
must index by 90° sector (floor), not nearest multiple, or the symbols sit on a decision
boundary and nothing decodes; the constant 45° lock offset washes out of the differential
decode. (First-light bug: caught and fixed by measurement, not reasoning.)

Tests migrated per #5's stated consequence — the acquisition parity tests changed meaning:
`Acquires_At_Txdelay_Zero_Like_A_NinoTNC` now covers only the non-PSK modes;
`Differential_Psk_Acquires_At_Txdelay_Zero` guards the opt-in's 0 ms property;
`Coherent_Psk_Acquires_After_Idle_Within_Ninotnc_Preamble` (100 ms) is the coherent "match
the NinoTNC" criterion; the idle-noise test moved to the differential opt-in. New
`CoherentDetectionTests` bake the noise-margin gate as a deterministic regression test. The
#9 constellation test now covers both detectors (differential product clusters tightest,
coherent absolute a little looser under loop jitter — both far above phase noise). Suite
201→218. Diagnostic/receiver-only change: no wire format, no named parse flag → no ax25-ts
leg. PROVENANCE updated (`CostasLoop` is a textbook loop implemented fresh; margins measured
in-project). Issue #5 closed on the evidence.

### 2026-07-16 (later) — constellation side channel: the per-symbol PSK diagnostic (#9)

Landed issue #9 — the per-symbol constellation / eye feed for the PSK modes, sequenced (by
Tom) immediately before #5 because it is #5's debugging surface. The PSK demodulators already
compute, at each symbol instant, the differential product they reduce to a decision and
discard (`re = i·delayedI + q·delayedQ`, `im = q·delayedI − i·delayedQ` for QPSK; the 1-D
`decision` for BPSK). That product **is** a constellation of phase-*changes* — exactly the
right artifact for a differential detector, clustering at the four dibit phases (QPSK) or the
two rails (BPSK). Exposed via a small `IConstellationSource { SymbolPlotted }` on `QpskModem`
and `BpskModem`; `ConstellationSource` mirrors `SpectrumSource` — batches points into
fixed-size scope frames (default 256 points, two signed bytes each, auto-ranged to the
frame's peak so cluster geometry reads independent of level; silent frames emit zeros). Wired
on `SoundModemChannel` via a new optional `constellationSink` (sub-channel, frame), attached
only to modems implementing the interface — the node-side seam, mirroring `spectrumSink`; no
daemon flag (spectrum has none either — the node consumes both over SSE).

Diagnostic-only: no wire format, no interop surface, no named parse flag, so no ax25-ts leg.
Seven tests (all green, suite 194→201): offset-invariant 4-fold phase coherence >0.9 on clean
qpsk2400/qpsk3600 loopbacks (measured 0.94/0.98 gating symbols within 60 % of burst peak —
the low-amplitude symbols carry real per-symbol phase noise and belong to the smear the
diagnostic reveals, so the "is the core tight?" assertion looks at the strong symbols),
BPSK's 1-D/bimodal geometry, the frame batching/auto-range/silence-floor encoding, and that
the channel wires PSK modems but leaves AFSK unwired. PROVENANCE updated (`ConstellationSource`
is original; the tap reuses existing demod arithmetic). Next: #5 (coherent detection).

### 2026-07-16 (night) — QtSoundModem matrix extended: 10 mode/pairings, 9 interoperate

Extended the QtSM cross-validation (docs/qtsm-loop.md) to five more shared modes, both
directions, reusing `qtsm-bench` + the rig recipe. New: **bpsk1200** (QtSM type 4, 10/10 both
ways), **qpsk600** (QtSM type 16 QPSK V26A 600bps — the V26A map again, 9/10 & 6/10),
**fsk9600** (QtSM type 19 RUH 9600(DW), 10/10 both ways), **fsk9600-il2p** (type 19 + IL2P,
10/10 both ways). **Nine of ten pairings interoperate cleanly both ways** across both rate
classes (12 kHz audio-band + 48 kHz RUH).

Two findings. (1) **`fsk4800-il2p` is one-way**: qtsm→ours 10/10 but ours→QtSM **0/10** — QtSM's
Dire-Wolf RUH-4800 receiver rejects our 4800 GFSK TX (which a NinoTNC decodes), even from the
clean 300 ms-preamble sample; our 4800 descends from the NinoTNC and, unlike our 9600, was never
Dire-Wolf-cross-validated. Evidence `samples/qtsm/qtsm-ruh4800.wav` + `samples/pdn` mode 04.
Raised as an issue; no change to our modem. (2) **QtSM's RUH modes don't run headless** without
a patch — its `using48000` flag (which opens the card at 48 kHz for RUH) is set only in the GUI
init path, so `nogui` RUH opened at 12 kHz and fed its 48 kHz demod garbage. A three-line patch
to QtSM's nogui worker (set `using48000` from the configured speeds before `InitSound`) fixes
it; applied to the local build, documented in docs/qtsm-loop.md § Rates. The RUH `ours→QtSM`
figures come from playing our pre-generated `samples/pdn` TX WAVs into QtSM, because the 48 kHz
aloop record-then-replay path is too lossy (documented).

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
