# MS110D one-way OTA test plan (40m NVIS)

Status: **planned instrument, not a gate**. OTA remains Rung 3 in the roadmap and a Phase B non-goal; this document exists so the test is designed before it is wanted, and because half of it (Phase 0) is independent of demodulator progress.

> **Where it got to (2026-08-20).** Phase 0 ran on 2026-07-27 over the RSP1 pad-chain rig (the deterministic replacement for the dummy-load idea): [evidence/2026-07-27-110d-full-campaign/](evidence/2026-07-27-110d-full-campaign/). Phase 1's first contact ran on 2026-08-03 from a real antenna at 48 W on 40 m ([evidence/2026-08-03-e4-first-on-air/](evidence/2026-08-03-e4-first-on-air/)): single bursts per waveform, WN0-7 and WN13 bit-exact, WN8 the ceiling of the path. **Phase 1 as specified below - the hour-long repeated ladder with power varied and a second receiver - has not been run**; it is leg H1 of [poor-gate-successor-plan.md](poor-gate-successor-plan.md). Two lessons that amend the protocol: the dial correction is per *receiver*, not per session (a -65 Hz receiver produced no acquisitions at all and looked like a broken modem), and mid-morning 40 m loses ~14 dB to D-layer absorption in an hour - run late afternoon or evening. Everything here is one-way TX → remote SDR: all current work is a receive/decode problem and no ARQ layer exists yet, so a return path adds nothing.

## Why (and why it stays off the gate table)

The Watterson rig is a model, and the Phase A audit's central lesson is that constants quietly fit the rig. A 40m NVIS path is approximately the ITU-R poor profile the D.6.1 rig imitates - multipath plus slow fading - so it is the ultimate off-rig direction check, plus it exercises what the simulator omits entirely: real SSB TX filtering against the ~300-3300 Hz occupied band, ALC action on the 8PSK/16QAM envelope, genuine dial-error CFO, and receiver sample-clock skew (what the ±75 Hz acquisition search and clock-skew tolerance were built for). The durable payoff is the capture corpus: off-air bursts with known payloads are regression fixtures forever, re-scoreable offline against every future demodulator change the way the mask suite is today. Results are direction checks - divergence from the rig's prediction at matched SNR is the measurement - never §5.3 evidence.

## When

- **Phase 0 (no ionosphere) - any time, including during Phase B.** It characterizes the TX chain and the capture tooling, not the demodulator, and its findings (filter truncation, ALC compression) feed back into the simulator as new impairment models.
- **Phase 1 (NVIS ladder) - after B3.2**, i.e. once the BPSK ladder (WN1-5) and QPSK (WN13/6) close on the Poor rig. At that point WN0-6/13 have a right to work over real NVIS and failures are informative rather than expected. Do not wait for all of Phase B: 8PSK/16QAM (B3.3/3.4) closure is not a prerequisite - their bursts ride along in the ladder as stretch data.

## Hardware chain

- **TX: FlexRadio 6500 via `M0LTE.Flex`** - already a supported audio path in this stack, so the modulator drives the transmitter through the existing integration rather than an acoustic/soundcard lash-up. Watch ALC (drive so ALC is barely active; PSK/QAM bursts are not constant-envelope), and record the Flex's TX monitor audio locally as a free pre-channel reference for every burst.
- **RX: a ka9q_ubersdr instance, captured as IQ48 complex baseband** via the in-repo `sm-iqcapture` client (`tools/Packet.SoundModem.UberSdr`, cross-checked bit-identical to the upstream `iq-recorder`) and converted offline to 9600 Hz MS110D audio, which bypasses the receiver's SSB filter/AGC/audio path entirely and gives sample-0 GPS UTC and the GPSDO `frequency_reference` as two free instruments for scoring ([ota-capture-client-plan.md](ota-capture-client-plan.md) C0-C2, 2026-07-24; the original "investigate with the operator" items (a) IQ access and (b) USB passband are both resolved by this). The receiver is a campaign variable: `wessex.zapto.org` (disciplined, ~0 Hz) decoded everything on 2026-08-03; M0EYT heard us at -65 Hz reference error and produced nothing; M0XDK-1 could not hear 50 W; `m9psy` (GPSDO, the original choice) is quota-limited. A second, independent SDR at a different distance is desirable when available - it separates ionospheric effects from site-local QRM.

## Protocol

1. **Phase 0:** dummy load (or minimal-power groundwave to a local capture), full burst ladder, captured at the Flex monitor point and off-air locally. Deliverables: measured TX spectral truncation vs the ideal RRC spectrum, ALC/compression behaviour per modulation, verified end-to-end timing of the capture tooling.
2. **Phase 1 ladder:** scripted schedule, robust → fragile - WN0, 1, 2, 3, 4, 13, 5, 6, 7, 8 - Long interleaver throughout so results compare against the gate table, payload per burst from a seeded PRNG (seed recorded in the manifest) so offline BER is exact, CW/voice ID bookends per licence conditions, GPS/NTP timestamps on every burst. Repeat the whole ladder for at least an hour: NVIS decorrelates over minutes and single passes sample a single channel state. Vary power over repeats (e.g. 100 W / 25 W / 5 W) to walk the ladder through its SNR range rather than trusting one operating point.
3. **Scoring (offline, repeatable):** run the demodulator over the captures - the B0 telemetry (uncoded SER, deep-fade attribution, turbo counters, acquisition/WID outcomes) works on any audio. Estimate per-burst SNR from the adjacent noise floor; compare each outcome against the Watterson rig's prediction at that SNR. Track misacquisitions and WID errors explicitly - corrupt-WID misacquisition at low SNR is already a flagged concern from the B0 sweeps.

## Corpus discipline

Audio does not go in git. Captures (WAV, plus IQ if obtainable) live in object storage (OARC static hosting is the candidate home); the repo gets `docs/ms110d/evidence/ota-<date>/` containing a manifest - per-burst WN, seed, timestamp, TX power, capture URLs, SHA-256 of every file - and the scoring outputs, so any future checkout can re-fetch and re-score the corpus bit-exactly. TX monitor recordings are part of the corpus, not just the off-air audio.

## Licensing/ops

Transmissions are made and supervised by Tom (M0LTE) under their licence; ID bookends around each ladder pass; data-mode frequencies per the 40m band plan; the schedule is a script Tom runs, not an automated transmitter.
