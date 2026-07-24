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
