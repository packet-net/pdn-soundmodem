# MS110D one-way OTA test plan (40m NVIS)

Status: **planned instrument, not a gate**. OTA remains Rung 3 in the roadmap and a Phase B non-goal; this document exists so the test is designed before it is wanted, and because half of it (Phase 0) is independent of demodulator progress. Everything here is one-way TX → remote SDR: all current work is a receive/decode problem and no ARQ layer exists yet, so a return path adds nothing.

## Why (and why it stays off the gate table)

The Watterson rig is a model, and the Phase A audit's central lesson is that constants quietly fit the rig. A 40m NVIS path is approximately the ITU-R poor profile the D.6.1 rig imitates — multipath plus slow fading — so it is the ultimate off-rig direction check, plus it exercises what the simulator omits entirely: real SSB TX filtering against the ~300–3300 Hz occupied band, ALC action on the 8PSK/16QAM envelope, genuine dial-error CFO, and receiver sample-clock skew (what the ±75 Hz acquisition search and clock-skew tolerance were built for). The durable payoff is the capture corpus: off-air bursts with known payloads are regression fixtures forever, re-scoreable offline against every future demodulator change the way the mask suite is today. Results are direction checks — divergence from the rig's prediction at matched SNR is the measurement — never §5.3 evidence.

## When

- **Phase 0 (no ionosphere) — any time, including during Phase B.** It characterizes the TX chain and the capture tooling, not the demodulator, and its findings (filter truncation, ALC compression) feed back into the simulator as new impairment models.
- **Phase 1 (NVIS ladder) — after B3.2**, i.e. once the BPSK ladder (WN1–5) and QPSK (WN13/6) close on the Poor rig. At that point WN0–6/13 have a right to work over real NVIS and failures are informative rather than expected. Do not wait for all of Phase B: 8PSK/16QAM (B3.3/3.4) closure is not a prerequisite — their bursts ride along in the ladder as stretch data.

## Hardware chain

- **TX: FlexRadio 6500 via `M0LTE.Flex`** — already a supported audio path in this stack, so the modulator drives the transmitter through the existing integration rather than an acoustic/soundcard lash-up. Watch ALC (drive so ALC is barely active; PSK/QAM bursts are not constant-envelope), and record the Flex's TX monitor audio locally as a free pre-channel reference for every burst.
- **RX: the GPSDO-disciplined ka9q_ubersdr instance at `m9psy.tunnel.ubersdr.org`** (RX888 MkII + ka9q-radio backend, full-HF). The web UI offers USB with a passband up to ~2700–3000 Hz and native recording (WAV/PCM or Opus — use WAV; Opus is lossy and adds an uncharacterized codec). Two items to investigate with the operator before the session, in descending value: (a) **IQ or wide-passband access** — ka9q-radio's radiod is inherently multichannel and can serve linear/IQ channels at configurable rates, so a server-side capture (or native-client stream) may bypass the browser audio path entirely; (b) confirm the USB passband can be set to at least 3000 Hz high-cut and note the exact filter, since the serial tone's RRC skirt extends past it and the truncation must be attributed to the RX, not the TX. A second, independent SDR at a different distance is desirable when available — it separates ionospheric effects from site-local QRM.

## Protocol

1. **Phase 0:** dummy load (or minimal-power groundwave to a local capture), full burst ladder, captured at the Flex monitor point and off-air locally. Deliverables: measured TX spectral truncation vs the ideal RRC spectrum, ALC/compression behaviour per modulation, verified end-to-end timing of the capture tooling.
2. **Phase 1 ladder:** scripted schedule, robust → fragile — WN0, 1, 2, 3, 4, 13, 5, 6, 7, 8 — Long interleaver throughout so results compare against the gate table, payload per burst from a seeded PRNG (seed recorded in the manifest) so offline BER is exact, CW/voice ID bookends per licence conditions, GPS/NTP timestamps on every burst. Repeat the whole ladder for at least an hour: NVIS decorrelates over minutes and single passes sample a single channel state. Vary power over repeats (e.g. 100 W / 25 W / 5 W) to walk the ladder through its SNR range rather than trusting one operating point.
3. **Scoring (offline, repeatable):** run the demodulator over the captures — the B0 telemetry (uncoded SER, deep-fade attribution, turbo counters, acquisition/WID outcomes) works on any audio. Estimate per-burst SNR from the adjacent noise floor; compare each outcome against the Watterson rig's prediction at that SNR. Track misacquisitions and WID errors explicitly — corrupt-WID misacquisition at low SNR is already a flagged concern from the B0 sweeps.

## Corpus discipline

Audio does not go in git. Captures (WAV, plus IQ if obtainable) live in object storage (OARC static hosting is the candidate home); the repo gets `docs/ms110d/evidence/ota-<date>/` containing a manifest — per-burst WN, seed, timestamp, TX power, capture URLs, SHA-256 of every file — and the scoring outputs, so any future checkout can re-fetch and re-score the corpus bit-exactly. TX monitor recordings are part of the corpus, not just the off-air audio.

## Licensing/ops

Transmissions are made and supervised by Tom (M0LTE) under their licence; ID bookends around each ladder pass; data-mode frequencies per the 40m band plan; the schedule is a script Tom runs, not an automated transmitter.
