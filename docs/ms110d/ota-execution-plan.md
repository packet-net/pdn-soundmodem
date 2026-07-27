# MS110D OTA execution plan — TX client, campaign runner, scorer

Status: **plan for the half of the OTA test that does not exist yet.** [ota-test-plan.md](ota-test-plan.md) says *why* and *what to measure*; [ota-capture-client-plan.md](ota-capture-client-plan.md) built the RX capture (C0–C2 done). This document covers the **transmit path, the orchestration that ties TX to capture, the offline scorer, and the bench sequence that actually runs it** — i.e. everything between "we can record IQ" and "we have a scored corpus". No code written yet.

The first campaign is **Flex 6500 → dummy load → M0LTE's own UberSDR at LAN host `ubersdr`**, transmitting **wideband complex IQ via the Waveform API** (`M0LTE.Flex` `FlexWaveform`/`FlexWaveformIqOutput`, `underlying_mode=RAW` — proven on this radio, [flex-integration.md §9.2/§9.5](../flex-integration.md)) rather than through the SSB modulator. The modem is driven **in-process** (`Ms110dModulator`/`Ms110dDemodulator` directly, as `Ms110dMaskTests` does) — not through the KISS daemon — so the harness keeps the full B0 telemetry surface and exact bit references.

## The geometry, stated exactly (because everything downstream depends on it)

**One transmitter, one receiver, one path, one direction — and the path is local leakage.** The Flex feeds a dummy load; the UberSDR's active receive loop is metres away at the same QTH; the only reason anything is heard at all is near-field coupling between the two. That was already proven on this exact pair during the §9.5 wideband-comb measurement, but it is a property of the *room*, not of propagation, and three consequences follow that no part of this plan may quietly violate:

- **`m9psy` cannot hear the dummy load.** It is 400 miles away with no path to a dummy load. It has exactly two roles here: a **GPSDO frequency reference** for §I2 (which works by comparing a *third-party off-air* carrier heard at both sites — it never needs to hear us), and a **second receive site in §E4**, once we move to a real antenna. It is never a listener during E1–E3.
- **The transmitter is deaf.** No dummy-load return path, and the Flex's own receiver is blanked while transmitting anyway (§9.5: DAX-IQ self-capture during TX sees only the muted noise floor). There is **no self-check of any kind on the TX side** — the UberSDR is the sole observation instrument for everything we radiate. That is the ordering argument for §I1: audit the receiver *before* trusting anything it says about the transmitter, or a TX fault and an RX fault are indistinguishable.
- **The coupling factor is uncalibrated and physical.** Received level is not a function of TX power alone; move a cable and it changes. So absolute received level is never evidence — only *ratios* measured within one session are (SNR, IMD, image rejection, linearity slope). Re-measure the path at the start of every session (§E1), never carry a number across sessions.

## What the first-instance chain changes vs the parked assumptions

The capture-client plan deliberately parked IQ TX as an *optional Phase-0 refinement*, on the grounds that exercising the real SSB TX chain is what Phase 0 exists to measure. Leading with IQ TX inverts that, and it is the better order:

- **IQ TX is the reference leg, not a substitute for the SSB leg.** With `RAW` we transmit our own complex baseband: no SSB filter, no ALC on the envelope, no uncharacterised TX DSP. That makes the first bring-up a pure *plumbing and instrument* exercise — every residual impairment is either ours or the RX's, and there is no third unknown to hide in. Once the loop is trusted end-to-end, the same seeded bursts go out **again through DAX SSB audio** (`FlexDevice`/`FlexStation`, already shipped) and the difference **is** the TX-SSB-filter-plus-ALC contribution — measured directly instead of inferred. That is a strictly stronger Phase 0 than the original plan's, and it is why the A/B leg (§E3) is scheduled, not optional.
- **The RX has no GPSDO.** `ubersdr` reports `frequency_reference.enabled = false` and `receiver.gps.gps_enabled = false` (probed 2026-07-25). So the two free instruments the capture plan credited to the m9psy instance — ≈0 RX dial error and sub-ms GPS sample-0 — **do not apply here**. Both are recoverable, cheaply, and §I2/§I4 say how. Neither is on the critical path: burst extraction is designed not to depend on absolute time at all (§S2).

### Measured facts about `ubersdr` (probed 2026-07-25, 15 s capture at 7.074 MHz)

| Item | Value | Consequence |
|---|---|---|
| `allowed_iq_modes` / `public_iq_modes` | `["iq48"]` only (m9psy also offers `iq96`) | ±24 kHz span — ample for a 3 kHz waveform. `iq96` would need a bypassed/whitelisted IP; Tom owns this instance if we ever want it. |
| `bypassed` | `false` (we arrive over Tailscale as `100.77.12.98`) | Public-tier access is sufficient. |
| `max_session_time` | **3600 s** (m9psy: 10800) | A 1-hour ladder sits exactly on the cap → **chunk per pass with reconnect**, don't plan one long session. |
| `max_clients` / `available_clients` | 20 / 20 | No contention; concurrent captures (e.g. the §I2 calibration pair) cost nothing. |
| `frequency_reference` | `enabled: false` | No GPSDO self-report. RX reference error unknown → §I2. |
| `receiver.gps` | `gps_enabled: false`, `tdoa_enabled: false` | Packet timestamps are host-clock (`server_time_sync: true` → NTP), not GPS. ~ms, not sub-ms. Fine for coarse alignment. |
| Capture behaviour | 720000 frames in 15.000 s, sample0 stamped, sidecar written, startup guard applied | `sm-iqcapture` works unmodified against this instance. |
| Levels | peak −14.7 dBFS, rms ≈ −27 dBFS, per-second rms stable within 1.8 dB | Sane headroom; no evidence of AGC in 15 s, but **not yet audited** → §I1. |
| Data rate | 192,003 B/s measured = **691 MB/hr per receiver** | Corrects the capture plan's "~1.3 GB/hr" (2× high). 10-minute passes ≈ 115 MB. |

One further receiver-side hazard, from the QTH rather than the API: there is an **on-site packet station on 7.051 MHz** whose signal the loop hears very loudly. The RX888 front end is wideband — total ADC load is set by everything on HF at once, not by the tuned channel — so that station eats headroom on 17m just as surely as on 40m. It is a reason to (a) stay off 40m entirely for this work, and (b) measure §I1's headroom *while* it is active, so the audit reflects the worst case rather than a quiet moment.

### Operating frequency

**17m data segment, 18095–18109 kHz** (Tom's call; WARC band, no contests, usually quiet, and clear of the on-site 40m packet station). Concrete default:

| Parameter | Value | Why |
|---|---|---|
| Flex slice (waveform centre) | **18.098 000 MHz** | Any LO/carrier leakage in `RAW` lands here — deliberately placed *outside* the occupied band. |
| `--tx-offset-hz` | **+2000** | Effective SSB dial = 18.100 000 MHz. |
| Occupied band | 18.100 3 – 18.103 3 MHz | The modem's ≈300–3300 Hz above the effective dial. Inside the segment with margin at both ends. |
| RX tune | **18.100 000 MHz** | `sm-iqcapture --frequency 18100000`; converter runs `--dial-hz 0`. Leakage sits at −2 kHz in the complex baseband and the SSB bandpass (+150…+3450 Hz) rejects it outright. |

Everything else in this plan is frequency-agnostic; this is a default, not a constraint.

---

## The instruments are all new code we own — plan for finding bugs in them

Every layer of this chain was written in the last ten days and none of it has been used in anger: `M0LTE.Flex`'s waveform IQ TX (one hardware session, a tone and a comb), `sm-iqcapture` (one 30 s audit against a *different* instance), `IqToAudioConverter` (loopback only, never real bursts), and an MS110D demodulator under daily change. A failure anywhere in that stack presents as "the modem didn't decode". Two rules follow.

**Rule 1 — never let the modem be the first thing under test.** Each layer gets an oracle that is independent of the modem and that fails *loudly and diagnosably*:

| Layer | Oracle | A fault looks like |
|---|---|---|
| `FlexWaveformIqOutput` reflection loop | A **continuous tone** (§E0.5) | Any dropped/starved/duplicated packet is a phase discontinuity → visible spectral splatter around a pure carrier. A modem burst would only show "bad decode". |
| Waveform `RAW` IQ→RF fidelity | Tone frequency, image, spurs vs commanded | Wrong frequency, an image at −offset, a carrier at the slice centre. |
| TX level path | Commanded amplitude/power vs received dBFS | Non-monotonic or compressed linearity curve. |
| `sm-iqcapture` on this instance | §I1 audit (AGC, clipping, PSD, timestamp continuity) | Floor that tracks signal; clipped peaks; sample-rate drift. |
| `IqToAudioConverter` / streaming rewrite | Whole-file vs streaming equivalence, plus §S1's synthetic gate | Divergence between the two paths. |
| `Ms110dDemodulator` | Everything above already green | Only *now* is a decode failure attributable to the modem. |

**Rule 2 — fixes land upstream, not as local workarounds.** `M0LTE/M0LTE.Flex` is a public repo we own; bugs found there get fixed and republished (SWR support below is already one such change), not patched around in `sm-ota`. Record the exact package version in every manifest alongside the modem commit, for the same reason.

---

## §T — the transmit path

### T1 — `Ms110dIqUpconverter` (9600 Hz real audio → 24 kHz complex baseband)

The mirror of C2's `IqToAudioConverter`, and the only genuinely new TX DSP.

- **Rate bridge.** `Ms110dModulator` is native 9600 Hz; `FlexWaveformIqOutput.SampleRate` is **24 kHz complex** (the 6000-series waveform rate). 24000/9600 = 2.5, so go via the existing integer stages: `Upsampler` ×5 → 48 kHz, `Decimator` ÷2 → 24 kHz. (24000/2400 = 10 samples/symbol, so nothing awkward lands at the symbol rate.)
- **Real → analytic.** Apply the complex SSB bandpass over +[low, high] Hz and keep the complex result (C2 keeps the real part; TX keeps the complex one). This synthesises an ideal USB transmitter: RF = dial + audio frequency, exactly what C2's `--dial-hz 0` assumes at the other end.
- **Placement NCO.** `--tx-offset-hz` shifts the occupied band away from the slice centre so any LO/DC artefact at the dial sits outside the modem passband, and so we can inject a **known** dial error to exercise the ±75 Hz acquisition search on demand.
- **Amplitude.** Explicit `--tx-amplitude`, peak reported, hard clip check. The waveform sink takes floats; whether the radio applies any limiting in `RAW` is a §I3 measurement, not an assumption.
- **Guard.** Lead-in/lead-out zeros around each burst (the `Ms110dModem.Modulate(…, txDelayMs)` idea, applied at the IQ layer).

**Extract `ComplexSsbFilter` as a shared kernel** used by both T1 and `IqToAudioConverter`. That is deliberate — and it creates a de-rigging hazard the campaign-audit lesson warns about: a bug in a *shared* filter cancels in a TX→RX loopback and the payload still recovers. So the T1 gate is **not** "loopback recovers the payload". It is:

1. loopback payload recovery through `IqToAudioConverter` across WN0/2/6/13 and several dial offsets (the C2 test pattern), **and**
2. an **independent spectral audit** of the upconverter output computed from first principles — image (negative-frequency) rejection ≥ 50 dB, occupied bandwidth matching the `Ms110dObwTests` expectation, passband ripple, no DC term — asserted against analytic values, not against the reverse chain, **and**
3. a round trip through `MockFlexRadio`: write to a `FlexWaveformIqOutput`, read `MockFlexRadio.CapturedWaveformIq`, assert sample fidelity and `SamplesStarved == 0`. This exercises the real reflection-driven TX plumbing with no radio.

### T2 — `FlexIqTransmitter`

Thin orchestration over `M0LTE.Flex` 0.3.0, which already has every piece:

`FlexClient` → `FlexWaveform.SetUpHeadlessAsync(client, FlexWaveformOptions{ UnderlyingMode="RAW", Frequency, Antenna, TxFilterLowHz/HighHz, RfPower })` → `CreateIqOutput()` + `CreatePtt()`.

`TransmitBurstAsync(float[] iq24k, …)`: key → wait for `interlock=TRANSMITTING` (the measured settle is **139 ms**, so a fixed pre-roll of zeros is not optional) → `Write(burst)` → `Drain(timeout)` → unkey. Records, per burst: key/first-sample/unkey UTC + monotonic ticks, `PacketsReflected`, `SamplesStarved`, `TuneWarning`.

**Safety rules, enforced in code:** `RfPower` must be passed explicitly (no default), a `--max-burst-seconds` ceiling, and a startup assertion that the slice verified on-frequency. The receiver is an active loop metres from the dummy load and there is **no per-user gain control on the IQ channel** — an over-driven TX clips the shared ADC for every user of that SDR. Bring-up starts at minimum power and steps up while watching captured peak dBFS (§E1).

### T4 — transmitter health: SWR/power metering and a TX safety interlock — **BUILT AND WORKING (2026-07-25)**

Live on M0LTE's 6500 into the dummy load: commanded `rfpower=10` → **forward 39.9 dBm (9.7 W), SWR 1.32**, pre-flight passing on its own merits. The two independent SWR paths agree (the radio's own meter 1.27 vs the FWDPWR/REFPWR computation 1.32), which cross-validates the ÷128 scaling *and* the derived formula at once.

**Scalings now empirically anchored, not assumed:** `SWR` raw 128 → 1.00 into a dummy load and raw 3294 → 12.87 V on the `+13.8A` rail settle both disputed divisors (÷128 and ÷256 respectively); PATEMP raw 2239 → 34.98 °C confirms ÷64. Note the wiki's "all others used directly" rule is **wrong** for reading the radio's own SWR meter — it is ÷128, per the MIT `kc2g-flex-tools/flexclient` reference. That rule applies to meters a *client creates and sends*, not to reading.

**The bug that cost a session, recorded so it is not repeated.** `FlexClient.DispatchVitaPacket` hands handlers `packet.Slice(PayloadOffset, PayloadLength)` — **already payload-only**. Re-applying `PayloadOffset` inside the handler dropped short meter packets entirely (offset ≥ length) and, on long ones, silently discarded the first seven id/value pairs. A real 6500 sends every meter on one stream in one packet shape (`type=3 C=1 T=0 tsi=1 tsf=1 stream=0x00000700`, 28-byte preamble), so the casualties were exactly the low ids: MICPEAK, MIC, HWALC, +13.8A/B, FWDPWR, REFPWR, **SWR**. What made it expensive to find is that *nothing looked wrong* — every surviving value was a genuine meter with a genuine id, no unknown ids, no decode errors, plausible readings throughout. Only an absence.

The diagnostic that cracked it is kept as `sm-ota rawmeters`: it speaks the TCP/UDP protocol directly using **none** of `M0LTE.Flex`, and dumps every datagram whole. That independence is the point — every earlier diagnostic ran downstream of the same parse, so none of them could see what was being discarded. Two regression tests now build a real 6500-shaped meter datagram, slice it exactly as the library does, and require the first meter to survive.

**Watch-item for `M0LTE.Flex`:** `FlexDaxIqSource.OnVita` makes the identical double-offset mistake. On the DAX-IQ path that would skip 28 bytes (3.5 complex samples) off the head of every packet — which also breaks I/Q pairing, mirroring the spectrum. It would not have shown up in the existing validation, which was run on noise (a mirrored noise spectrum looks the same) with the unit tests feeding `DaxIqStreamBuffer.Ingest` directly and bypassing `OnVita`. Worth checking before multi-channel RX is trusted.

**The library still needs the meter API.** `M0LTE.Flex` knows the meter *packet class* (`Vita49.MeterClass`) but has no meter surface — no subscription, no descriptor parse, no value decode. Everything required is reachable from the **existing public `FlexClient` surface**, so:

1. **Prototype in `sm-ota`** over `FlexClient.SendCommandAsync` (`sub meter all`, `meter list`), `FlexClient.StatusUpdated` (the `S…|meter <n>.nam=SWR .unit=… .low=… .hi=…` descriptors → a name→id map) and `FlexClient.VitaPacketReceived` (class `0x8002`, payload = repeated meter-id/value pairs). Also subscribe `FlexClient.MessageReceived` — the `M…` channel is where the radio reports faults, and it is free.
2. **Then upstream it** as `FlexMeters` in `M0LTE/M0LTE.Flex` 0.4.0 (`Subscribe`, `TryGet(name)`, an `Action<string,double>` update event), since it belongs next to `FlexPtt`/`FlexStation` and every other consumer wants it.

Per this repo's provenance rule, the per-unit value scaling (`dBm`, `SWR`, `degC`, `Volts` all use different fixed-point divisors) is **pinned from the FlexRadio wiki plus the MIT Go references**, cited in the source — not guessed. It is then verified empirically on the radio, which is unusually easy here: **into a dummy load SWR must read ≈1.0–1.2, and `FWDPWR` must track commanded `rfpower` monotonically**. Any scaling error shows up immediately against those two anchors.

**The safety interlock** (the reason Tom asked, and it earns its keep before the first real burst):

- **Pre-flight, every session:** key at minimum power with a tone, read `SWR`/`FWDPWR`/`REFPWR`, and **abort the run** unless SWR is under threshold and forward power is sane. This is what catches *"the dummy load isn't actually connected"* — the failure that would otherwise put full power into an open ANT1 a few metres from the receive loop.
- **Per burst, during a campaign:** sample the meters around each transmission, log them into the manifest, and **abort on** SWR over threshold, `PATEMP` climbing, or any fault `M…` message. An hour-long unattended ladder with no transmitter-health telemetry is not something to run.
- Meters also give an **independent TX-power record per burst**, which is exactly what the §I3 linearity sweep and the §E2 power-varied repeats need on the transmit side — the coupling factor being uncalibrated (see *The geometry*) means received level alone can never supply it.

### T3 — `FlexDaxTransmitter` (the deployment route, and the A/B leg) — **delivered**

Same seeded bursts, same runner, selected with `--route dax` (**now the ladder's default**): 9600 Hz audio → ×5 → 48 kHz → ÷2 → 24 kHz into a reduced-bandwidth DAX audio stream (`FlexStation` headless path, `mode=DIGU`) with `FlexPtt`. No new DSP and no offset/NCO/SSB synthesis — DIGU places audio *f* at dial + *f*, so the radio does what the IQ route's up-converter does in software. This is the modem's real deployment path, which is why it is the default and the IQ route (`--route iq`) is the reference instrument; the pair make §E3's differential a matter of flipping one flag. `FlexDaxTransmitter` shares `FlexTransmitterOptions`, `SwrInterlock`, the Morse identification and the inter-burst settle with the IQ route through the `IOtaTransmitter` seam, points the transmitter at DAX and reads the selection back (throwing rather than keying a mic-sourced transmitter into silence — mirrors `FlexDevice.OpenAsync`), and opens the global transmit filter to `DaxTransmitFilterHighHz` (default 3450 Hz). SWR on this route is meaningful only on the constant-envelope pre-flight tone, not the modulated bursts — decided by construction rather than measured, since real audio has no analytic envelope to inspect.

---

## §H — the campaign runner (`tools/Packet.SoundModem.Ota`, `sm-ota`)

One process owns TX, capture, and the manifest, so TX timestamps and capture sample-0 share a clock and the correlation is instrumented rather than reconstructed. `UberSdrIqClient.CaptureAsync` is already a library-shaped class — the runner drives one instance per receiver as a background task.

Subcommands:

| Command | Purpose |
|---|---|
| `sm-ota tx-check` | TX plumbing smoke: bring up the waveform, key, send a tone and one burst, report reflected/starved counters. No capture, no scoring. |
| `sm-ota caltone` | Transmit a known single complex tone for the frequency/level/linearity instruments (§I2/§I3). |
| `sm-ota run --schedule <f>` | The campaign: start capture(s), walk the ladder, write the manifest. |
| `sm-ota monitor` | Live capture → streaming convert → demodulate, printing decodes as they happen. Bring-up feedback before committing an hour. |
| `sm-ota score` | Offline scoring of a capture set against its manifest (§S2). |
| `sm-ota measure` | Instrument audits over a capture: PSD, levels, AGC check, tone frequency/ppm, OBW, IMD (§I). |

**Schedule file** (JSON, committed to the evidence dir): ladder-level `{ dialHz, txOffsetHz, passes, gapSeconds, idCall, receivers[] }` plus per-step `{ wn, interleaver, constraintLength, payloadBits, seed, txAmplitude, rfPower, injectSnrDb, channelProfile, repeat }`.

**Manifest** (JSON, per pass): the schedule echoed, plus per-burst actuals (key/first-sample/unkey UTC + monotonic, counters, reference-bitstream SHA-256), the capture filenames + SHA-256s, each receiver's `/api/description` snapshot, and — because another agent is changing the demodulator daily — **the modem's git commit, branch, and build configuration**. A capture that cannot be attributed to a modem revision is not a regression fixture.

### The two channel-emulation knobs (`injectSnrDb`, `channelProfile`)

A dummy-load path has no interesting SNR: the leakage is whatever it is. To walk the mask suite's SNR ladder through **real hardware**, inject the impairment at the transmitter — apply `WattersonChannel` and/or calibrated AWGN to the 9600 Hz burst *before* upconversion, at the same seeds the mask suite uses.

This is the single most informative thing the dummy-load phase can do. It turns §E2 from "the plumbing works" into a **hardware-in-the-loop replication of the mask suite**: identical seed, identical channel realisation, identical SNR convention — scored once in pure simulation and once through Flex TX → RF → SDR → capture → convert → demod. The difference is the hardware's contribution, isolated. That is exactly the "divergence from the rig's prediction at matched SNR is the measurement" discipline the test plan asks for, available before any ionosphere is involved.

Prerequisite: the leakage path's own SNR must sit well above the highest injected noise floor (measure once in §E1; want ≥ 25–30 dB of margin) so the injected noise, not the path, sets the operating point.

`WattersonChannel` lives in the **test** project (`tests/Packet.SoundModem.Tests/Channel/`). Do not reimplement it — that is precisely the de-rigging failure mode. Link the existing source file into the tool project with a single `<Compile Include="…/WattersonChannel.cs" Link="Channel/WattersonChannel.cs" />`, which touches nothing the other agent owns. Promoting it to a shared project is the tidier end state and can wait.

---

## §S — scoring

### S1 — make the IQ→audio converter streaming (blocking defect, must land first)

`IqToAudioConverter` as built is whole-file and O(n·taps) at the input rate. For a one-hour capture that is 172.8 M complex samples through a 301-tap naive convolution (~5×10¹⁰ MACs) after allocating several `double[172_800_000]` arrays (1.4 GB each) — it will OOM this box before it is slow. `WavFile.ReadMono` is whole-file too. Three fixes:

1. **`StreamingIqWavReader`** — chunked stereo-int16 WAV reads.
2. **`StreamingIqToAudio`** — NCO → **polyphase decimating** complex bandpass computing only every 5th output (48 k → 9600 in one stage) → real part. Filter runs at the decimated cost; ~5× saving on top of not materialising the file.
3. **Kill the whole-file `NormalisePeak`.** Normalising an hour-long capture to its single loudest burst attenuates everything else. Default it off for scoring (the demodulator equalises level anyway); keep per-burst normalisation as an explicit option.

Gate: byte-comparable output against the existing whole-file converter on a short capture (≤ 1e-6 RMS difference), so the fast path is provably the same transform.

### S2 — `BurstScorer`

Stream the converted audio through **one** `Ms110dDemodulator` for the whole pass. Verified safe: `EndBurst()` returns the state machine to `Searching`, so a single instance re-acquires burst after burst — no per-burst reconstruction, and continuous operation is itself under test.

**Burst extraction does not use absolute time.** The demodulator finds bursts by acquisition; the harness feeds audio in small chunks and records the sample index at each `Searching → ReadingPreamble → Tracking` transition. Detected bursts are matched to schedule entries by order plus a generous time window. Absolute UTC is a cross-check and, crucially, the way a **scheduled burst with no acquisition** is identified — an acquisition failure, which is a headline metric, not a gap in the data.

Per burst, from the existing telemetry surface:

- **Acquisition** — acquired?, sample index, derived UTC, `PeakSearchMetric`.
- **WID correctness** — `Lock.WaveformNumber` / `Interleaver` / `ConstraintLength` vs the schedule. A mismatch is a **WID error**, flagged separately from a decode failure; corrupt-WID misacquisition at low SNR is already a flagged concern from the B0 sweeps and this is the first time it can be observed off-air.
- **CFO** — `Lock.CfoHz`, logged, never pre-corrected (per the capture plan). With §I2's calibration this decomposes into Flex TX dial error + RX reference error.
- **Coded BER** — decoded `BlockDecoded` bits vs the reference payload regenerated from the manifest seed.
- **Uncoded BER/SER** — `FirstPassBlockLlrs` vs the re-encoded wire-order reference, exactly as `Ms110dMaskTests` does it (`Ms110dFraming.BuildTxBits` + `EncodeBlock`, `Ms110dPuncture.Get`, `Ms110dInterleaver`).
- **Turbo counters** — `TurboConverged/Reverted/Aborted/Skipped`, `CollapseResolves`.
- **End reason** — `Ms110dBurst.Reason`, block count.
- **Per-burst SNR** (§S3).
- **Fade attribution** — the rig classifies each uncoded error against the channel's *recorded* gain trajectory. Off-air there is no channel truth, so substitute the **measured short-time received-power envelope** of the burst and classify errors against measured fade depth. Honest, different, and worth having: it is the OTA analogue of the deep-fade split and it also works on the injected-Watterson runs, where the rig's truth *is* available for direct comparison — which validates the substitute instrument.

`Ms110dFraming` is `internal` with `InternalsVisibleTo` only for the test assembly. Add one line to `src/Packet.SoundModem/Packet.SoundModem.csproj` for the OTA tool. It is additive and precedented; flag it to whoever holds the branch.

Output: one CSV row per burst plus a JSON summary, and a comparison table against the Watterson rig's prediction at the matched SNR.

### S3 — the SNR estimator (and its own audit)

The mask suite's SNR is **mean signal power over the burst / noise power in a 3 kHz bandwidth** (`WattersonChannel.AddNoise`). The OTA estimator must match that convention or the numbers are not comparable to the gate table:

1. Noise **density** from the scheduled silent gap immediately before the burst — median PSD across the passband, so a carrier in the gap does not poison it.
2. Signal power = mean power during the burst minus noise power in the same measured band.
3. Scale noise to a 3000 Hz reference bandwidth.

**Audit:** feed the estimator bursts produced by `WattersonChannel.Apply` at known SNRs and require it to recover them to a fraction of a dB, across AWGN and Poor, across WNs. Entirely offline. An unaudited SNR estimator would quietly re-fit every OTA number to itself — the exact instrument failure the campaign-audit lesson is about.

---

## §I — instruments and calibration

- **I1 — audit the local receiver** (the C0 audit, repeated for `ubersdr`, which has never had one): AGC present? (does the noise floor drop when a strong signal arrives — the C0 method), clipping headroom, PSD flatness across ±24 kHz, DC spur, IQ-imbalance ridge, timestamp continuity and exact 48000 sps, and host-clock offset vs ours. Blocking for any quantitative result.
### I2 measured (2026-07-25) — and it is a blocker at 17m, not a curiosity

A **frequency-standard station gives an absolute reference from a single receiver**, which is simpler than the two-site comparison below and needs no second SDR at all. **RWM on 9.996 MHz** (Russian caesium-referenced, continuous carrier) is well received here; WWV on 5/10 MHz and the 4.996 RWM harmonic were all noise at the time of test.

| Quantity | Value | How |
|---|---|---|
| Local RX (`ubersdr`) reference error | **−6.28 ppm** | RWM carrier measured at +62.80 Hz (20 s and 60 s captures agree to 0.13 Hz) |
| Combined TX+RX error at 18.1 MHz | **+4.77 … +4.97 ppm** (+86…+90 Hz) | our own tone, across five runs |
| Flex 6500 TX reference error | **≈ −1.5 ppm** | by subtraction |

Both oscillators are free-running, and the combined figure moved ~0.2 ppm over half an hour — so this is re-measured per session, never carried across.

**The consequence:** the demodulator's acquisition search is a **±75 Hz coarse CFO grid in 25 Hz steps** (design §2.6). At 18.1 MHz the combined error is **+86…+90 Hz — outside that window**, so the modem would simply fail to acquire, and would do so in a way that looks like a demodulator problem rather than a dial problem. Three remedies, in preference order:

1. **Tune the capture to the frequency actually received** — we own `--capture-freq`, so offsetting it by the measured error drives the residual to ≈0. Costs nothing, needs no change to the radio, and turns the demod's CFO estimate into the residual measurement §S2 wants anyway.
2. **Correct the Flex's PPM in SmartSDR** — removes only the transmit term (≈27 Hz at 18.1 MHz), leaving ~59 Hz. Inside the window, but without much margin, and it does nothing about the receiver, which is the dominant error and is not correctable there.
3. **Operate lower** — the same ppm is only ~33 Hz at 7 MHz. Ruled out here by the on-site 7.051 MHz packet station.

Do (1) always; (2) is worth doing anyway to make the Flex a cleaner reference transmitter.

**Remedy (1) verified the same day:** with the capture tuned 86 Hz high (`--capture-freq 18106586`), the same tone measured **+0.66 Hz residual** — down from +86.3 Hz, and now two orders of magnitude inside the ±75 Hz window. The whole correction is one calibration capture against RWM plus one number on the capture command.

- **I2 (original plan) — frequency calibration without a GPSDO, in two independent steps.** Retained as the cross-check now that an absolute reference is in hand. Neither step requires `m9psy` to hear our transmitter — it cannot, and nothing here asks it to.
  1. **Local RX reference error.** Capture the *same* strong, stable, third-party off-air carrier **simultaneously** on `ubersdr` and on the GPSDO-disciplined `m9psy` — a commercial shortwave broadcast carrier is ideal (those transmitters are typically held well inside 0.1 ppm) and both sites hear the same one. The apparent-frequency difference is the local receiver's reference error in ppm. **Needs no transmission at all and can be done today.** Verify by repeating on a second carrier: a genuine reference error is the same ppm on both, a mis-chosen carrier is not.
  2. **Flex TX dial error**, by subtraction: a `caltone` into the dummy load, heard on the now-calibrated `ubersdr`, yields the combined error; remove the RX term and the remainder is the transmitter's. So §S2's CFO figures decompose cleanly even though neither end of the first-instance chain is disciplined.

  An SDR's dial and sample-clock errors share one reference oscillator, so the same ppm also predicts the sample-clock skew the demodulator's timing tracker must absorb; confirm it directly from the phase ramp of a long tone capture rather than assuming the relationship holds.
- **I3 — characterise the TX chain** (this *is* ota-test-plan Phase 0's deliverable list): amplitude-linearity sweep, two-tone IMD (the honest test for hidden limiting in `RAW`), our own image rejection as radiated, occupied bandwidth vs the ideal RRC spectrum, and spectral regrowth vs drive. Repeat the OBW and IMD legs on the SSB path in §E3 — the delta is the ALC/filter contribution.

  **Measured 2026-07-25** (18.1065 MHz, dummy load, `sm-ota sweep` / `sm-ota tone --tone2-hz`):

  | Quantity | Result |
  |---|---|
  | Power control | `rfpower` is percent of 100 W, delivered within 5% at every step (1→1.03 W … 15→14.91 W) |
  | Linearity 1→15 W | forward power +11.6 dB, received level +11.2 dB — tracking within **0.4 dB, no compression** |
  | SWR vs power | 1.31–1.33, flat across the whole range |
  | Two-tone IMD3 | ≈ **−28 dBc** at both 15 W and 4 W — see below, this is a floor, not a measurement |
  | TX image rejection | **−43.7 dBc** |
  | LO/carrier leakage at the waveform centre | **−27.1 dBc** |
  | Waveform reflection loop | **0 starved samples**, once the sink was gated on the interlock |

  **The image and leakage figures above supersede earlier ones** (−35.8 and −20.4 dBc). Those were taken while the waveform sink was draining its own transmit ring continuously, so the transmitted signal was being cut about; they were measuring our bug, not the radio. Both leakage figures still vindicate placing the modem band away from the waveform centre.

  **IMD is not yet characterised, and the reason is the path.** Dropping drive by 6.1 dB should improve a genuine third-order product by 12.2 dB. Measured, it moved **1.1 dB** — so ≈ −28 dBc is a floor set by the measurement, not by the transmitter, which is therefore at least that linear and possibly far better. The limit is the leakage path: SNR reaches only ~19 dB at 15 W, with strong band signals inside the capture span (one measured level with our own tone, 15 kHz away). Characterising TX IMD needs a cleaner path or more signal, and more signal is capped by the receiver's ADC — so this is a real Phase-0 conclusion rather than a number to quote: **the dummy-load leakage path is adequate for frequency, linearity and level work, and inadequate for distortion work.**

  **Deferred (Tom, 2026-07-25) to the on-air phase with a real antenna and `m9psy` listening.** Distortion blocks nothing: the modem cares about level and linearity, both of which measure cleanly here. One caveat to carry into that session — **a skywave path fades, and fading corrupts a two-tone measurement the same way envelope modulation corrupts SWR**, by making the forward and product samples describe different moments. The 400-mile path fixes the coupling and reference problems but not necessarily this one. The instrument that would is a stable attenuated tap into a receiver, where level is controlled and nothing moves.

  **The receiver, not the transmitter, sets the power ceiling.** At 10 W the capture peaks at −11.4 dBFS, so ~30 W would reach −6 dBFS and 100 W would clip the shared ADC — degrading the SDR for every user of it. That caps usable SNR at roughly **27 dB at ~30 W** against the 25–30 dB the injected-noise ladder wants: achievable, with nothing spare. More headroom means reducing the receiver's front-end gain, which is a decision about a shared instrument, not a knob to reach for.
- **I4 — timing.** TX key UTC vs RX acquisition UTC across many bursts → constant offset plus jitter. Establishes the coarse-alignment window §S2 needs and quantifies what NTP-grade timestamps actually buy us here.

---

## §E — execution sequence

**The modem is the last thing to go on the air, not the first.** Tom's call, and it is right: a tone answers "can we fundamentally hear this, at what level, with what error" using none of the modem, none of the scorer, and almost none of the harness — and it is a *better* detector of bugs in the new TX stack than a burst would be (Rule 1 above).

### E0.5 — tone-first bring-up (the first RF milestone)

Deliberately small: `sm-ota tone` (waveform bring-up + meters + key + write a complex tone + drain + unkey) and `sm-ota measure tone` (find the tone in a capture, report frequency/level/SNR/PSD). No upconverter, no scorer, no manifest, no modem. It answers, in one session:

1. **Can we hear it at all** — the fundamental question, and the one everything else assumes.
2. **The power operating point** — sweep `rfpower` × IQ amplitude against received dBFS, and pick the setting that keeps the receiver clear of clipping with the most SNR. Do this with the on-site 7.051 MHz packet station active, since it sets the worst-case ADC headroom.
3. **The path SNR budget** — how far above the noise floor the leakage path actually puts us, which is what decides whether §H's TX-injected-noise ladder is viable (wants ≥ 25–30 dB of margin).
4. **Combined frequency error and its stability** — one number, and its drift over minutes; the §I2 pair then splits it into TX and RX terms.
5. **Our first spectral audit of the TX chain** — image at −offset, carrier leakage at the slice centre, spurs, all with no modem in the way.
6. **A hard shake-out of the new libraries** — a *continuous* tone turns any starved, dropped, or duplicated waveform packet into a phase discontinuity and therefore visible splatter around an otherwise pure carrier. Cross-check against `PacketsReflected`/`SamplesStarved`. A modem burst would hide the same fault as "bad decode".

Add a two-tone leg in the same session for IMD, and an on/off tone train for the §I4 timing offset. Success: a clean carrier at the commanded frequency and level, `SamplesStarved == 0`, SWR nominal, and a documented operating point.

### Full sequence

| Phase | RF? | What | Success |
|---|---|---|---|
| **E0** | No | Offline dry run: `flex:mock` TX + a **synthesised capture** (upconvert → resample to 48 k complex → add dial offset, noise, int16 quantisation → write a real capture-format WAV + manifest) → the real scorer. | A complete, correct result set with zero hardware. **Gate before any modem RF** (not before E0.5). |
| **E1a** | RX only | **§I1 receiver audit** and **§I2 step 1** (dual-site carrier calibration). No transmission. | The receiver is characterised *before* it is used to judge a transmitter. |
| **E0.5** | Dummy load | **Tone bring-up**, above. Pre-flight SWR check first. | Clean carrier, operating point chosen, no starves, libraries shaken out. |
| **E1b** | Dummy load | First modem RF: one WN2 burst, `sm-ota monitor` live. Re-measure path SNR. | **DONE 2026-07-25.** WN2, WN6 and WN13 each transmitted as IQ, captured on `ubersdr`, and decoded **bit-exact** — BER 0.00E+00, WID correct, EOM found, CFO −0.1 Hz. `sm-ota burst` does the whole loop in one command. |
| **E2** | Dummy load | ota-test-plan **Phase 0**: §I3 measurements, then the hardware-in-the-loop mask replication — matched seeds, matched SNRs, simulation vs through-the-radio. | A rig-vs-hardware differential per WN/SNR point, with the divergence attributed. |
| **E3** | Dummy load | The **IQ vs SSB A/B**: identical seeded bursts through `RAW` IQ and through DAX SSB. | The TX SSB filter + ALC contribution measured directly — the question the capture plan could only park. |
| **E4** | On air | ota-test-plan **Phase 1**: NVIS ladder, real antenna, `m9psy` joins as the second site (the first time it hears us at all), ID bookends, power varied across repeats. Gated on B3.2; **not** gated on B3.3/B3.4. | The capture corpus + scored ladder. |

Everything through E3 is independent of demodulator progress and can run during Phase B, exactly as the test plan says of its Phase 0.

---

## Work order

Reordered so the **tone path comes first** — it is the shortest route to real RF and it gates the rest.

| # | Deliverable | Depends on | Hardware? |
|---|---|---|---|
| **Tone-first track** | | | |
| 1 | `sm-ota` skeleton + `FlexIqTransmitter` (tone only) + `MockFlexRadio` round trip (§T2) | — | No |
| 2 | Meter/SWR subscription + decode + the pre-flight interlock, prototyped over `FlexClient` (§T4) | 1 | No |
| 3 | `sm-ota measure` — PSD, tone frequency/level/SNR/ppm, spur & image, IMD (§I) | — | No |
| 4 | **§I1 receiver audit + §I2 step 1 calibration** (`ubersdr` + `m9psy`, receive only) | 3 | RX only |
| 5 | **E0.5 tone bring-up** — operating point, path SNR, spectral audit, library shake-out | 1–4 | **Yes** |
| **Modem track** (buildable in parallel; gated on 5 for RF) | | | |
| 6 | `ComplexSsbFilter` extraction + `StreamingIqWavReader` + `StreamingIqToAudio` (§S1) | — | No |
| 7 | `Ms110dIqUpconverter` + the three-part T1 gate (§T1) | 6 | No |
| 8 | Schedule/manifest types, `InternalsVisibleTo`, linked `WattersonChannel` (§H) | 1 | No |
| 9 | `ReferenceBits` + `BurstScorer` + `SnrEstimator` + the S3 audit (§S2/§S3) | 6, 8 | No |
| 10 | Synthetic-capture generator + the **E0** offline end-to-end gate | 7, 9 | No |
| 11 | `sm-ota monitor` (live capture → convert → demod) | 6, 9 | No |
| 12 | **E1b** first modem RF | 5, 10, 11 | **Yes** |
| 13 | **E2** characterisation + hardware-in-the-loop mask replication | 12 | **Yes** |
| 14 | `FlexSsbTransmitter` + **E3** A/B (§T3/§E3) | 13 | **Yes** |
| 15 | Upstream `FlexMeters` into `M0LTE/M0LTE.Flex` 0.4.0, plus any bugs found in 5/12 | 5, 12 | No |
| 16 | **E4** on-air ladder | 14, B3.2 | **Yes** |

Items 1–4 are a couple of days and put us on the air with a tone. Everything offline (1–4, 6–11) touches only `tools/` — nothing in `src/Packet.SoundModem/Ms110d/` — so it runs concurrently with active demodulator work; the sole edit outside `tools/` is the one-line `InternalsVisibleTo`.

## Corpus discipline (extends ota-test-plan *Corpus discipline* and capture-plan C3)

Unchanged in principle: IQ and audio to object storage (OARC static hosting), `docs/ms110d/evidence/ota-<date>-<phase>/` gets the schedule, the manifest, the scoring CSV/JSON, the instrument-audit outputs, and a README. Two additions this plan forces:

- **The manifest records the modem's git commit.** With the demodulator under daily change, a score without a revision is uninterpretable and a fixture without one is unusable.
- **Pass files, not session files** — the 3600 s cap on `ubersdr` makes ~10-minute passes (≈115 MB) the natural unit, with the ~1 s post-connect startup transient discarded per pass (the C0 rule).

## Risks / watch-items

- **Shared TX/RX filter kernel hides its own bugs in loopback** → the T1 gate is spectral and analytic, not just round-trip payload recovery.
- **Over-driving a shared receiver.** No per-user gain on the IQ channel; clipping degrades the SDR for everyone. Minimum power first, step up on measured peak dBFS, and keep the ceiling in the schedule file.
- **Transmitting into a disconnected or faulty load.** The transmitter is deaf and the receive loop is metres away; the only guard is §T4's pre-flight SWR check, so it is a hard gate on every session rather than a nicety.
- **New, unproven libraries throughout** → Rule 1: every layer gets a modem-independent oracle, and the tone (E0.5) precedes the modem on air. Record the `M0LTE.Flex` package version in every manifest.
- **The coupling factor is physical and uncalibrated** → only within-session ratios are evidence; re-measure path SNR every session, and take per-burst TX power from the meters (§T4), never from received level.
- **Unaudited SNR estimator re-fits every number to itself** → §S3's audit against the rig at known SNRs is blocking, not optional.
- **Whole-file DSP** → §S1 is first in the work order for a reason; an hour-long capture will OOM the current converter, and this box's LXC OOM behaviour is unforgiving.
- **Demodulator churn.** Telemetry hooks (`FirstPassBlockLlrs`, turbo counters, `Lock`) are the scorer's contract. Keep the use of internals minimal, pin the commit, and expect to re-score.
- **Injected-noise SNR only holds while the leakage path is quiet** — re-measure the path SNR at the start of every session, not once.
- **Dummy-load leakage still radiates** (§9.5 proved it is receivable off-air). Frequency choice, power, and ID remain operator decisions under Tom's licence.

## Operator decisions

1. ~~Frequency~~ — **settled: 17m data segment**, defaults tabulated above.
2. **Power ceiling and SWR abort threshold** for the interlock — proposed: start at the radio's minimum, and abort above SWR 1.5 into the dummy load. Tom to confirm the numbers he is happy running unattended.
3. **Whether to whitelist our IP on `ubersdr`** for `iq96` — not needed for a 3 kHz waveform, but it would widen §I3's spectral-regrowth and IMD measurements beyond ±24 kHz. Tom owns the instance.
4. **Which off-air carrier to use for the §I2 calibration pair** — wants to be strong at both Reading and `m9psy`, and reference-locked. A commercial shortwave broadcast carrier is the default suggestion; open to a better one.
