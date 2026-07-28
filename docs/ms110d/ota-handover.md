# MS110D OTA — handover and remaining roadmap

Written 2026-07-25 at the end of the bring-up session, for whoever picks this up next. Companion documents: [`ota-execution-plan.md`](ota-execution-plan.md) is the plan and the rationale; [`ota-air-backlog.md`](ota-air-backlog.md) is everything blocked on hardware, which is where to look the moment a radio is free; [`evidence/`](evidence/) is what happened and what it measured. This file is the forward-looking one: state, environment, the work left in order, and the traps that cost a session each.

## Read this first

**Work in `/home/tf/pdn-ota`, branch `ms110d-ota-harness`.** It is a git worktree of `pdn-soundmodem`. The main checkout at `/home/tf/pdn-soundmodem` is used concurrently by another agent doing MS110D demodulator phases — do not `git checkout` there, and expect its build to be broken intermittently while they work. Sharing that tree cost real time before the split.

Build against a local `M0LTE.Flex` checkout with `-p:FlexSourcePath=/home/tf/M0LTE.Flex/src/M0LTE.Flex/M0LTE.Flex.csproj`. Without it the published package (0.5.0) is used. The Flex work is co-developed with that library and the radio is the only authority on its behaviour, so expect to edit both.

```
dotnet test tests/Packet.SoundModem.Tests/Packet.SoundModem.Tests.csproj -c Release \
    -p:FlexSourcePath=/home/tf/M0LTE.Flex/src/M0LTE.Flex/M0LTE.Flex.csproj
```
795 tests (690 pass, 105 skipped — mask suite, corpus and hardware gates) green at handover.

**Run them with `./.test.sh` (gitignored; recreate from the handover if missing).** It pins workstation GC and a 3 GB heap ceiling, because this box has 16 GB and hosts another agent's long mask runs. And **never `pkill` by pattern** — `pkill -f "dotnet test"` matches their runs as readily as ours, and killed one.

## State

| Phase | Status |
|---|---|
| §E0.5 tone bring-up | **done** — operating point, path SNR, spectral audit, library shake-out |
| §I1 receiver audit | partial — levels and PSD done; AGC/clipping audit not formally repeated |
| §I2 frequency calibration | **done**, and it was a blocker |
| §I3 TX characterisation | linearity and levels **done**; **IMD not measured** (see below) |
| §E1b first modem burst | **done** — WN2, WN6, WN13 all bit-exact |
| §S3 SNR estimator + audit | **done** |
| §S1 streaming converter | **done** — an hour converts in 13 s / 26 MB |
| §S2 burst scorer / uncoded BER | **done** — `sm-ota score` |
| §E2 hardware-in-the-loop | **built and rehearsed offline** — needs the radio |
| §E3 IQ vs SSB A/B, §E4 on air | not started |

`sm-ota` subcommands: `tone`, `sweep`, `tune`, `burst`, `synth`, `meters`, `measure` (`--survey`, `--purity`), `radio`, `rawmeters`, `score`, `ladder`, `monitor`.

## Environment — constants and quirks

| Thing | Value |
|---|---|
| Radio | FlexRadio 6500 at `10.45.0.76`, dummy load on ANT1 |
| Receiver | `ubersdr` (M0LTE, Reading), iq48 only, `max_session_time` 3600 s, **no GPSDO, no GPS** |
| Receiver (alt) | **RSP1 on `studybox`** — `--capture rsp`, `rx_sdr` CF32 streamed over ssh. On Flex **ANT2** (ubersdr/dummy-load path is ANT1). Single-client device; no GPS. See the RSP1 section below |
| Frequency | 17m: waveform centre **18.106500 MHz**, modem/tone at **+2000 Hz** → 18.1085 MHz |
| Flex reference | corrected: `radio set freq_error_ppb=-1497` (persists) |
| Receiver reference | ≈ **−6.3 ppm**, measured against **RWM 9.996 MHz** |
| `--dial-correction` | **per-session, re-measure every time.** Was 115, then 129 after a supply change (0.9 ppm shift) |
| Power ceiling | 30 W default in the tool — protects the *receiver's* ADC, not the PA |
| Capture rate | 48 kHz IQ ≈ **691 MB/hr** per receiver |

**The Flex's voltage ADC under-reads by ~0.6 V.** It showed 12.0 V where a meter showed 12.6 V. Use it for *changes*, not absolutes.

**`m9psy` cannot hear a dummy load.** Its roles are the GPSDO cross-check for §I2 (comparing a third-party carrier heard at both sites — it never needs to hear us) and the second site in §E4.

## Traps — each of these cost a session or came close

1. **An instrument built on the suspect component cannot exonerate it.** Meter telemetry appeared absent for hours; six hypotheses were disproved because every diagnostic ran downstream of the same parse. A raw-socket probe using none of the library (`sm-ota rawmeters`) settled it in one run. Keep that tool.
2. **Consistency is not correctness.** Both IQ converters had the same sideband inversion and cancelled exactly — payloads decoded bit-exact while both were wrong. Only spectral assertions anchored to *absolute frequencies* caught it. Never accept round-trip recovery as proof of a converter.
3. **Measurements that mask a region are blind to it.** The spur search masks ±200 Hz around the carrier; a −8 dBc hum comb lived there and was reported as a clean −45 dBc signal for hours. A human listening found it. `sm-ota measure --purity` exists for this.
4. **SWR is meaningless unless the envelope is constant.** Forward and reflected are sampled at different instants. A dummy load read 1.31 on a carrier and 1.93 on a two-tone. The transmitter now refuses to evaluate SWR on a modulated burst — leave that in.
5. **The radio pulls waveform TX buffers continuously**, keyed or not, and never announces its return to RECEIVE. `FlexWaveformIqOutput` gates on the interlock because of this; before the fix, samples queued before keying were silently discarded and bursts came out truncated with a starve count of zero.
6. **The demodulator clears `Lock` when a burst ends.** Read it inside `BlockDecoded`, not after `Process`, or a perfect decode reports "NO ACQUISITION".
7. **Re-measure `--dial-correction` each session.** At 18 MHz the combined reference error exceeds the demodulator's ±75 Hz acquisition grid, so getting this wrong means nothing acquires and it looks like a demodulator fault.
8. **On a fake clock, a delay nothing advances never completes.** `FlexIqTransmitter` takes a `TimeProvider` so the ten-minute identification rule and the ladder's pacing can be proved by advancing a clock. The first attempt at that test hung on a settle delay inside `EnsureIdentifiedAsync`. The fix was to separate the policy from the mechanism — `IdentificationDue` is the decision alone — not to pump the clock from the test.
9. **Where an error lives is the diagnosis.** When two implementations of the same maths disagree, find the first and last differing sample before theorising: confined to the head is a startup convention, spread through the file is a wrong kernel, at the tail is the flush. The streaming converter's equivalence test prints all three because RMS alone cannot tell them apart.

## Remaining roadmap

### 1. ~~Streaming IQ→audio conversion (§S1)~~ — done

`StreamingIqToAudioConverter` + `PcmWavReader`. An hour of iq48 (691 MB, 172.8 M frames) converts in 13 s at 26.6 MB peak RSS, and the burst at 3590 s comes out bit-identical to the one at 10 s. Both acceptance criteria met: it agrees with the whole-file reference to ~1e-8 RMS, and the memory is flat.

`IqToAudioConverter` stays as the readable reference the streaming form is tested against — do not use it on a real capture. `--gain auto|<factor>` replaced the silent whole-file peak normalisation; pass a fixed number when levels must be comparable between files.

The sideband-inversion test gap is closed too — see [`evidence/2026-07-25-streaming-converter/`](evidence/2026-07-25-streaming-converter/README.md), which also records why the reference's low-pass now starts primed rather than cold.

### 2. ~~Reference bits and uncoded BER (§S2)~~ — done

`Ms110dReferenceBits` + `BurstScorer` + `sm-ota score`. A capture streams through the converter into **one** demodulator, and every burst comes back with acquisition, WID, CFO, SNR, coded BER, uncoded BER, turbo counters and end reason; a table on stdout and a CSV row per burst, missed bursts included as rows so a summary built by counting cannot lose them.

```
sm-ota score --in pass.wav --wn 6 --count 12 --seed 1 --at 10,30,50,… --csv pass.csv
```

Three things about it that are load-bearing and easy to undo by accident:

- **Bursts are found by acquisition, never by slicing at the scheduled times.** Slicing hides the result that matters most — a burst the receiver never heard — by handing the demodulator a window already known to contain a signal. Time only matches what was found to what was sent. A missed burst exits 0, because at the bottom of an E2 ladder it is the expected outcome and not a tool failure.
- **The burst's start comes from `CarrierDetect`, polled per chunk — not from the first block event.** A block event fires only once the whole block has arrived, so using it puts the start at the end; measured, that made `StartSeconds` equal `EndSeconds` and left no burst audio to estimate SNR from. Time resolution is `ChunkSeconds` (0.1 s), and chunking is on absolute sample positions so a pass scores identically however the reader divided it up.
- **Uncoded BER grades opinions, not erasures.** An exactly-zero LLR is the demodulator expressing no opinion — WN0 erases its first channel symbol of every burst by design, because the RAKE's decision-directed finger gains start cold — so those positions are neither bits compared nor errors. The hard-decision tie-break reads an erasure as a bit 1, so grading it would charge up to 2/80 = 2.5 % on a clean WN0 Short block regardless of SNR, and every clean-channel point would sit above the curve it is compared with.

Still owed: the schedule is homogeneous on the command line (one WN, seeds incrementing). A mixed ladder wants §3's JSON.

**An observation for whoever owns the demodulator, recorded not acted on:** on a *noiseless* channel WN2's first-pass output has 3 wrong hard decisions in 768, at wire positions 24/40/46, with |LLR| 0.032/0.061/0.414 against a block median of 1.507 — the three least-confident decisions in the block. WN0, WN6 and WN13 are exactly 0, and a 20-super-frame preamble changes nothing, so it is not acquisition settling. The scorer's test asserts the invariant that actually matters — no error may be *confidently* wrong — rather than a count.

### 3. ~~Schedule and manifest types, and `sm-ota monitor`~~ — done

`CampaignSchedule` (the request) and `CampaignManifest` (the record). `sm-ota ladder` writes both for every pass, rehearsals included, and `sm-ota score --schedule <file>` takes either — reading burst positions from a manifest so a pass is scored where the transmissions actually were. The score table then shows **asked** beside **got**, which is the comparison a ladder exists to make.

The manifest records the repository revision, stamped into the binary at build time by a target in the OTA csproj and marked `-dirty` when the tree had uncommitted changes. Also the capture's SHA-256, the dial correction, the pass gain, the RF power and a free-text `--supply` note — the supply has already moved both the 100 Hz floor and the frequency reference, and no machine on this bench can see which one is connected.

Two traps found building it, both of the silent-wrong-answer kind:

- **`System.Text.Json` does not object to missing members.** Deserialising a bare schedule into a `CampaignManifest` *succeeds* and yields one whose `Schedule` is null, so a try/catch fallback never fires. `LoadScheduleOrManifest` decides by inspecting the JSON, and a document with no bursts is refused outright — an empty pass and a pass where everything was missed must not look alike.
- **`Assembly.GetEntryAssembly()` is the test host under a test runner.** The first version of the revision helper returned a 40-character hash belonging to the runner, and would have written that into manifests anywhere the harness was hosted rather than run directly. Read `typeof(CampaignFiles).Assembly`.

`sm-ota monitor` watches a receiver live — capture → convert → demodulate, printing each burst as it lands, receive-only, with the capture still written to disk so a monitored session can be scored afterwards. It runs the same chain a scored pass uses, driven from the socket instead of a file, via a new `OnBlock` hook on the capture client. Proved against `ubersdr`: 20 s real-time, kept up comfortably, zero false acquisitions on band noise.

### 4. §E2 — built and rehearsed; needs the radio

`sm-ota ladder` injects the rig at the transmitter and scores the pass. `--dry-run` renders the whole thing to an IQ file with no radio involved, which is how it was proved: rendered, converted, scored, and the measured SNR tracked the requested SNR to 0.3 dB at every rung from 18 dB down to 3 dB, with coded BER holding at zero to 6 dB (WN6's gate is 9 dB). The only untested link is the hardware.

**`--route` picks the live transmit path, and `dax` is the default.** The DAX route (`FlexDaxTransmitter`) is the modem's real deployment path: it hands the radio real audio and the radio's own DIGU SSB modulator places it — audio frequency *f* lands at dial + *f*, carrier suppressed at the dial — so the MS110D waveform (native 9600 Hz, 1800 Hz sub-carrier, occupied ≈180–3420 Hz) goes out with **no software offset, NCO or SSB synthesis**, just a 9600→24000 Hz resample into a reduced-bandwidth DAX stream. The IQ route (`FlexIqTransmitter`, `--route iq`) is the bench instrument: it synthesises single-sideband IQ in software through a headless `RAW` waveform, bypassing the radio's SSB modulator, ALC and TX DSP. The route selector changes *only* the live transmit path — `--dry-run` and `sm-ota score` are receiver-side and model the captured IQ regardless of how it was radiated, so they are identical either way.

```
sm-ota ladder --wn 6 --snr 18,12,9,6,3 --repeats 4 --dry-run --out pass.wav   # rehearse (receiver-side, route-independent)
sm-ota ladder --wn 6 --snr 18,12,9,6,3 --repeats 4 --rf-power 15 \
              --capture-host ubersdr --dial-correction <re-measure!>          # for real, DAX (the deployment default)
sm-ota ladder --wn 6 --snr 18,12,9,6,3 --repeats 4 --rf-power 15 --route iq \
              --capture-host ubersdr --dial-correction <re-measure!>          # the bench-instrument IQ leg
```

The one gain across the whole pass is applied on both routes (a separate audio-gain constant for DAX, since the audio and the up-converted IQ have different natural scales), and the manifest records whichever level actually reached the radio.

`WattersonChannel` is compiled into the tool from where it lives in the test project (see both csprojs) — one definition, still the mask suite's to edit. Three things about the design are load-bearing:

- **Each point transmits its own noise lead-in**, so the receiver measures the SNR actually delivered instead of trusting the nominal one. This is what makes a rung's position on the comparison curve a measurement rather than a claim. `LeadInSeconds` must exceed the scorer's `NoiseLeadSeconds + NoiseWindowSeconds` with margin for the key-up ramp.
- **One gain across the whole pass**, taken from the worst point. Peak-normalising per point would quieten the *signal* at low SNR, and since the leakage path adds noise at a fixed absolute level, that is a second uncalibrated dose of noise at the bottom of the ladder.
- **Rungs are interleaved, not grouped**, so a pass cut short still covers the ladder.

**How high the ladder can usefully go.** Two constraints bind, and both bind at the *top*: the path margin (~24–26 dB at 15 W) and the −27 dBc close-in artefact. At 5 dB the artefact is 22 dB below the injected noise and contributes nothing; at 23 dB it is 4 dB below and costs ~1.5 dB. Both put the ceiling near **15–16 dB**, which covers the whole AWGN mask except WN8's 16 dB point, and the Poor mask through WN6/WN13. WN7 (19 dB) and WN8 (23 dB) Poor need the real-antenna phase. *This refines the older note below, which warned about low-SNR behaviour — the arithmetic says the artefact is a high-SNR problem.*

**Prerequisites before the numbers mean anything:**

- **The 100 Hz hum: reduced, and the evidence now points at the RECEIVER as the remaining floor.**

  | Source of the transmission | ±100 Hz |
  |---|---|
  | original mains PSU | −8.0 dBc |
  | switched-mode mains PSU | −20 dBc (three repeats within 2 dB) |
  | large battery | −26.9 dBc |
  | the radio's own tune carrier, current supply | −27.5 dBc |
  | RWM 9.996 MHz — a distant caesium carrier, through the same receiver | ≈ −27 dBc |

  The supply plainly matters: changing it moved the figure by 12–19 dB, and the original PSU was by far the worst. But **three independent sources now converge on ≈ −27 dBc**, one of which is a distant frequency standard that cannot have 100 Hz sidebands of its own. Three unrelated signals landing on the same number is far more likely to be the measuring instrument than a coincidence, so the working conclusion is that **≈ −27 dBc is the UberSDR's own floor**, and on a good supply the Flex may already be clean — we cannot see past the receiver to tell.

  **The next test should target the receiver, not the transmitter**: a common-mode choke on its antenna feed, or its supply. If that floor moves, every figure above needs re-reading.

  Ruled out along the way: **the network cable is not the source.** Pulling the Ethernet mid-transmission made the sidebands ~1.7 dB *worse* and raised the carrier 2.2 dB — the behaviour of a cable acting as part of the RF environment, not one injecting hum. (The radio also unkeys a couple of seconds after the TCP session drops, so that test can only be done on the radio's own tune carrier, never on the waveform path, which needs the network for every packet.)

  Until the receiver floor is understood, do not attribute low-SNR E2 behaviour to the demodulator: a −27 dBc close-in artefact is on every burst we measure, wherever it originates.

- **Check path SNR margin.** The injected noise must dominate the path, so the leakage path needs ~25–30 dB of headroom above the highest test SNR. Measured ~24–26 dB at 15 W. That is tight; it may cap how high the ladder can go.

### 5. §E3 — IQ vs SSB A/B

Same seeded bursts through `RAW` IQ and through DAX audio (`FlexStation`, `mode=DIGU`). The difference **is** the TX SSB filter and ALC contribution, measured rather than inferred. Claim a DAX channel other than 1 if SmartSDR is running.

**Both legs are now built into `sm-ota ladder`** — flip `--route` between the two (`dax` the default deployment path, `iq` the bench instrument) on otherwise-identical invocations, and the manifests differ only in how the same seeded bursts were radiated. `FlexDaxTransmitter` shares the whole safety envelope with the IQ route (the same `FlexTransmitterOptions`, `SwrInterlock`, Morse identification and inter-burst settle) via the `IOtaTransmitter` seam; it points the transmitter at DAX (`transmit set dax=1`) and reads the selection back, throwing at bring-up rather than key a mic-sourced transmitter into silence, and it opens the global transmit filter to `DaxTransmitFilterHighHz` (default 3450) so the top of the MS110D band is not truncated by a stale 3 kHz SSB passband.

### 6. §E4 — on air

Real antenna, `m9psy` joins as second site, ID bookends, power varied across repeats. Gated on B3.2 per the original plan; not gated on B3.3/B3.4. Morse ID is built and on by default (`MorseGenerator`, callsign taken from the radio).

Two-tone IMD is deferred here (Tom's call). Carry the caveat: **a fading path corrupts a two-tone measurement much as envelope modulation corrupts SWR**. `m9psy` fixes the coupling and reference problems but not that one; a stable attenuated tap is what would.

## RSP1 / studybox capture backend

A second capture backend, parallel to the UberSDR one and behind `--capture rsp`. It mirrors `UberSdrIqClient`: same `CaptureResult`, same 16-bit stereo (interleaved I/Q) WAV via `PcmWavWriter` + JSON sidecar, so a capture drops straight into `sm-ota score` with no scorer change. Where the UberSDR client reads a websocket, `RspIqClient` runs `rx_sdr -d driver=sdrplay … -F CF32 -` on `studybox` over ssh and reads its stdout live, converting CF32 (interleaved float32 I/Q) to int16 on the fly (×32767, clamp — never wrap). Same streaming shape: a startup guard (default 1 s of settling discarded), `Sample0Utc` taken as the wall clock at the first sample *kept* after the guard — a real timestamp, which is the whole reason for streaming rather than capture-then-copy — and a trim to the target duration.

```
sm-ota ladder --wn 6 --snr 12,9,6,3 --repeats 4 --rf-power 15 \
              --capture rsp --dial-correction <re-measure!>          # RSP1 on ANT2, DAX route
```

Selecting `--capture rsp` **defaults `--antenna` to ANT2** (the RSP1 rig's port; ANT1 is the dummy-load/UberSDR path). An explicit `--antenna` still wins, and if it is anything but ANT2 the ladder logs a warning — keying a different port while capturing on ANT2 records nothing. Options: `--rsp-host` (default `studybox`), `--rsp-freq` (default `--freq` centre + `--dial-correction`), `--rsp-rate` (default 96000 — any low rate covers the 3 kHz waveform), `--rsp-gain` (default below), `--rsp-ssh-key` (default `~/.ssh/id_ed25519`).

Two traps found and handled here, both the single-client-device kind:

1. **This `rx_sdr` build cannot disable the RSP1's AGC.** It links no `setGainMode` and the SoapySDRPlay3 module exposes no AGC write-setting (only `agc_setpoint`), so a `-g` gain string cannot turn AGC off — `AGC=false` is parsed as a phantom gain element, IFGR is silently ignored, and `rx_sdr` logs `Not updating IFGR gain because AGC is enabled`. The client watches stderr for that line and warns loudly. The default gain `AGC=false,IFGR=40,RFGR=0` still requests AGC off (forward-compatible with an rx_tools build that honours it) and, via RFGR=0, was validated on the real rig to put the noise floor at ≈ −51 dBFS RMS, peak ≈ −30 dBFS, no clipping, repeatable run to run. At the campaign's ≈ −88 dBm input the AGC sits pinned at maximum gain, so the level is effectively fixed — **but a strong burst can still pump it**, which is the real cost of AGC-on and the reason to fix it (a patched `rx_sdr` with `setGainMode`, or a small SoapySDR helper) before quoting absolute levels across a ladder.
2. **`rx_sdr` survives a closed SSH channel and holds the RSP1.** Killing the local ssh client does *not* stop the remote `rx_sdr`; it keeps running, the single-client device stays locked, and every later capture fails with `SoapySDRDevice_make failed`. So the remote command is `echo RXPID:$$ 1>&2; exec rx_sdr …` — `exec` makes `rx_sdr` inherit the shell's PID, which is echoed first — and on stop the client kills the local ssh *and* opens a second ssh to `kill` that PID. A fixed `--duration` also adds an `-n` sample-count backstop so even a missed kill self-terminates. Validated end to end against the real RSP1: 4 s at 18.100 MHz → a well-formed WAV that `PcmWavReader` opens at 96000 Hz / 2 channels, remote process reaped, device released. Gated hardware test: `SM_OTA_RSP_HW=1 … -class …RspIqClientTests`.

## Outstanding measurements

- **Two-tone IMD** — never obtained. The earlier ≈ −28 dBc was a floor set by path SNR, proved by dropping drive 6.1 dB and seeing IMD3 move 1.1 dB where a genuine third-order product must move 12.2 dB. Needs a stable supply and better path SNR.
- **The receiver's own 100 Hz floor** — the test that would settle §E2's prerequisite. A common-mode choke on the UberSDR's antenna feed, or its supply; measure a quiet stretch of band with nothing transmitting and look for the comb. See §E2 below for why three sources converging on −27 dBc points at the instrument.
- **Image rejection and carrier leakage** were re-measured after the waveform gating fix (−43.7 and −27.1 dBc); the pre-fix figures in older text are superseded.

## Open questions for Tom

1. Mains hum: filter the supply, or run campaigns on a good battery? E2 needs one or the other settled.
2. Receiver front-end gain — reducing it would allow more transmit power and more path SNR margin, at the cost of changing a shared instrument's configuration.
3. Whether to merge `ms110d-ota-harness` to `main` now or keep it running until E2 produces results.
