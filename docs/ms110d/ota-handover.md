# MS110D OTA — handover and remaining roadmap

Written 2026-07-25 at the end of the bring-up session, for whoever picks this up next. Companion documents: [`ota-execution-plan.md`](ota-execution-plan.md) is the plan and the rationale; [`evidence/2026-07-25-ota-bringup/`](evidence/2026-07-25-ota-bringup/) is what happened and what it measured. This file is the forward-looking one: state, environment, the work left in order, and the traps that cost a session each.

## Read this first

**Work in `/home/tf/pdn-ota`, branch `ms110d-ota-harness`.** It is a git worktree of `pdn-soundmodem`. The main checkout at `/home/tf/pdn-soundmodem` is used concurrently by another agent doing MS110D demodulator phases — do not `git checkout` there, and expect its build to be broken intermittently while they work. Sharing that tree cost real time before the split.

Build against a local `M0LTE.Flex` checkout with `-p:FlexSourcePath=/home/tf/M0LTE.Flex/src/M0LTE.Flex/M0LTE.Flex.csproj`. Without it the published package (0.5.0) is used. The Flex work is co-developed with that library and the radio is the only authority on its behaviour, so expect to edit both.

```
dotnet test tests/Packet.SoundModem.Tests/Packet.SoundModem.Tests.csproj -c Release \
    -p:FlexSourcePath=/home/tf/M0LTE.Flex/src/M0LTE.Flex/M0LTE.Flex.csproj
```
742 tests green at handover.

## State

| Phase | Status |
|---|---|
| §E0.5 tone bring-up | **done** — operating point, path SNR, spectral audit, library shake-out |
| §I1 receiver audit | partial — levels and PSD done; AGC/clipping audit not formally repeated |
| §I2 frequency calibration | **done**, and it was a blocker |
| §I3 TX characterisation | linearity and levels **done**; **IMD not measured** (see below) |
| §E1b first modem burst | **done** — WN2, WN6, WN13 all bit-exact |
| §S3 SNR estimator + audit | **done** |
| §S1 streaming converter | **not started** |
| §S2 burst scorer / uncoded BER | **not started** |
| §E2 hardware-in-the-loop | **not started** — the point of the exercise |
| §E3 IQ vs SSB A/B, §E4 on air | not started |

`sm-ota` subcommands: `tone`, `sweep`, `tune`, `burst`, `synth`, `meters`, `measure` (`--survey`, `--purity`), `radio`, `rawmeters`.

## Environment — constants and quirks

| Thing | Value |
|---|---|
| Radio | FlexRadio 6500 at `10.45.0.76`, dummy load on ANT1 |
| Receiver | `ubersdr` (M0LTE, Reading), iq48 only, `max_session_time` 3600 s, **no GPSDO, no GPS** |
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

## Remaining roadmap

### 1. Streaming IQ→audio conversion (§S1) — blocking for anything long

`IqToAudioConverter` is whole-file and O(n·taps) at the input rate. An hour of iq48 is 172.8 M complex samples and several 1.4 GB `double[]` allocations — it will OOM this box before it is slow, and `WavFile.ReadMono` is whole-file too.

Build: a chunked stereo-int16 WAV reader; a streaming NCO → **polyphase decimating** complex bandpass computing only every 5th output (48 k → 9600 in one stage); and drop the whole-file `NormalisePeak`, which currently scales an entire capture to its loudest burst.

Acceptance: output matches the existing whole-file path to ≤1e-6 RMS on a short capture, and an hour-long capture converts within memory.

While there: **the receive converter's own tests cannot detect a sideband inversion** — they synthesise their input with the same convention they decode it with. Add an absolute-frequency assertion.

### 2. Reference bits and uncoded BER (§S2)

Regenerate the transmitted bit stream from the manifest seed and compare against `FirstPassBlockLlrs` in wire order, exactly as `Ms110dMaskTests` does — `Ms110dFraming.BuildTxBits` + `EncodeBlock`, `Ms110dPuncture.Get`, `Ms110dInterleaver`. `Ms110dFraming` is `internal`, so add one `InternalsVisibleTo` line to `src/Packet.SoundModem/Packet.SoundModem.csproj` for the OTA tool (precedented — the test assembly already has one).

Then a `BurstScorer` that streams a whole capture through **one** `Ms110dDemodulator` (verified: it returns to `Searching` after each burst and re-acquires), matching detected bursts to schedule entries by order plus a time window, and reporting per burst: acquisition, WID correctness, CFO, coded BER, uncoded BER/SER, turbo counters, end reason, and SNR from `SnrEstimator`.

Do **not** use absolute time for burst extraction — acquisition finds them; time is a cross-check and the way a *missed* burst is identified, which is itself a headline metric.

### 3. Schedule and manifest types, and `sm-ota monitor`

A JSON schedule (WN, interleaver, seed, power, offset, repeats, gaps) and a per-pass manifest recording actuals, capture SHA-256s, receiver `/api/description`, **and the modem's git commit** — the demodulator changes daily and a score without a revision is uninterpretable.

`sm-ota monitor` — live capture → streaming convert → demodulate, printing decodes as they happen. Cheap once §S1 exists, and worth a lot during a session.

### 4. §E2 — the actual point

Inject the Watterson rig and calibrated AWGN **at the transmitter**, using the mask suite's own seeds, and score the same points through real hardware. The differential against pure simulation at matched SNR is what this whole exercise exists to produce.

`WattersonChannel` lives in the test project; link the source file into the tool rather than reimplementing it (`<Compile Include="…/WattersonChannel.cs" Link="…"/>`). Reimplementing a rig is the de-rigging failure this project has already paid for.

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

### 6. §E4 — on air

Real antenna, `m9psy` joins as second site, ID bookends, power varied across repeats. Gated on B3.2 per the original plan; not gated on B3.3/B3.4. Morse ID is built and on by default (`MorseGenerator`, callsign taken from the radio).

Two-tone IMD is deferred here (Tom's call). Carry the caveat: **a fading path corrupts a two-tone measurement much as envelope modulation corrupts SWR**. `m9psy` fixes the coupling and reference problems but not that one; a stable attenuated tap is what would.

## Outstanding measurements

- **Two-tone IMD** — never obtained. The earlier ≈ −28 dBc was a floor set by path SNR, proved by dropping drive 6.1 dB and seeing IMD3 move 1.1 dB where a genuine third-order product must move 12.2 dB. Needs a stable supply and better path SNR.
- **Image rejection and carrier leakage** were re-measured after the waveform gating fix (−43.7 and −27.1 dBc); the pre-fix figures in older text are superseded.

## Open questions for Tom

1. Mains hum: filter the supply, or run campaigns on a good battery? E2 needs one or the other settled.
2. Receiver front-end gain — reducing it would allow more transmit power and more path SNR margin, at the cost of changing a shared instrument's configuration.
3. Whether to merge `ms110d-ota-harness` to `main` now or keep it running until E2 produces results.
