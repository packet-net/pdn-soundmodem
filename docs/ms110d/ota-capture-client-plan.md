# MS110D OTA capture client — plan

Status: **plan for the RX-side capture tooling of the one-way OTA test** ([ota-test-plan.md](ota-test-plan.md)). Resolves the two "investigate with the operator" items that doc parks under *Hardware chain* → RX, and upgrades the capture from SSB audio to GPS-disciplined IQ. No code is written yet; Phase C0 below is a de-risking spike that can run any time (it is independent of demodulator progress, like the test's Phase 0).

## Bottom line

The receiver end of the OTA test is a solved problem with an existing, GPL-3.0, headless client. The [`ka9q_ubersdr`](https://github.com/madpsy/ka9q_ubersdr) repo ships `clients/iq-recorder` — a small Go tool that records **IQ48** (48 kHz complex baseband, ±24 kHz around the tuned frequency) from an UberSDR instance to a standard 16-bit stereo WAV plus a JSON metadata sidecar, over `wss://<host>:443/ws?frequency=<Hz>&mode=iq48&format=pcm-zstd`. Its own README examples record from `m9psy.tunnel.ubersdr.org:443 -ssl` — the exact instance the test plan names. It stamps each recording's filename with the first packet's GPS timestamp and writes the receiver's GPSDO frequency reference into the sidecar.

Recommendation, in order:

1. **Do not reinvent the wire protocol.** It is fully documented below (reverse-read from the upstream Go source, which is factual framing, not expressive code).
2. **Phase C0 — de-risk with the upstream binary as-is.** `go build` it, capture 60 s off `m9psy`, and answer the open operator questions from real behaviour rather than correspondence.
3. **Phase C1 — port a minimal C# `UberSdrIqCapture` into `tools/`** for the campaign, so capture integrates with our manifest/evidence pipeline and the `Decode` scorer, with the Go client retained as a byte-for-byte cross-check (mirrors this repo's "spec is ground truth, QtSoundModem is a cross-check" discipline, and the instrument-audit lesson).
4. **Phase C2 — build the offline IQ→audio converter.** This is the one genuinely new piece of DSP and is needed whichever capture client we use.

## Why IQ instead of SSB audio (what this changes for the test)

The current [ota-test-plan.md](ota-test-plan.md) RX line assumes USB audio through the web UI's ~2.7–3.0 kHz filter and flags two things to investigate with the operator. IQ capture settles both:

- **(a) "IQ or wide-passband access"** — confirmed available to non-web clients. `mode=iq48` delivers 48 kHz complex baseband; the RX's SSB demod/AGC/audio path is bypassed entirely. IQ96/IQ192 exist too if we ever want more span.
- **(b) "confirm the USB passband ≥3000 Hz and note the exact filter, so RRC-skirt truncation is attributed to the RX not the TX"** — **mooted for the RX.** With IQ we apply *our own* characterized SSB filter offline, so there is no uncharacterized RX brick wall to attribute around. The only passband truncation left in the chain is the Flex's TX SSB filter — which is exactly what Phase 0 of the test exists to measure.

Two capabilities come free with the IQ path and become new instruments in the test:

- **Disciplined CFO reference.** `GET /api/description` returns a `frequency_reference` block — expected vs detected carrier, offset in Hz, signal strength, SNR — measured against the GPSDO. The RX dial error is therefore ≈0 and self-reported, so residual carrier offset in the capture is cleanly attributable to the **Flex 6500 TX dial** (the "genuine dial-error CFO" the ±75 Hz acquisition search was built for).
- **Sample-0 absolute UTC to sub-millisecond.** Every packet carries a GPS timestamp (nanoseconds since epoch); the recorder writes the first one into the filename. That is the anchor for correlating the scripted TX schedule against RX samples (coarse), before per-burst preamble correlation refines it (fine).

### Aside: IQ TX is also available (out of scope here, one refinement it unlocks)

The Flex radio supports IQ transmit and it is exposed in `M0LTE.Flex`, so complex baseband could be injected directly rather than through the Flex's SSB modulator. This is deliberately **not** part of the core OTA path: exercising the real SSB TX chain — its passband filter, its ALC action on the non-constant-envelope PSK/QAM envelope — is precisely what the test's Phase 0 exists to measure, so replacing it with ideal IQ injection would delete the impairment under study. Where it *does* earn its keep is as an **A/B reference**: transmit the same burst twice, once through the SSB modulator and once as direct IQ, capture both on the disciplined RX, and the difference isolates the TX SSB-filter-plus-ALC contribution that Phase 0 otherwise infers only indirectly. Park it as an optional Phase-0 refinement; it changes nothing in the capture-client plan below.

## The client landscape (`clients/` in ka9q_ubersdr)

| Client | Lang | Fit |
|---|---|---|
| **`iq-recorder`** | Go | **Chosen.** Headless IQ48→WAV, GPS-timestamp filenames + `/api/description` sidecar, multi-instance GPS alignment, single-file, already exampled against `m9psy:443`. |
| `python_iq_recorder` | Python | Same job, but GUI-first (tkinter) and depends on `../python/radio_client.py`; a headless `--config` CLI mode exists. Heavier; useful as a second reference for the protocol. |
| `python` (`radio_client.py`, `TCI_IQ_STREAMING.md`) | Python | The full SDR client + a TCI IQ-streaming bridge (float32 IQ to CW Skimmer/HDSDR). Documents the same IQ modes; overkill for capture. |
| `ubersdr-audio` | Go | Demodulated **audio**, not IQ — the thing we are moving away from. |
| `go` | Go | Full-featured native client (spectrum, TCI, flrig). Not capture-focused. |
| `CW_Skimmer`, `rtl_sdr`, `soapy_driver`, `hpsdr`, `benchmark`, `tdoa-processor`, `chrome/firefox-bridge`, `multi_instance` | — | Not relevant to one-way IQ capture. |

`iq-recorder` wins on every axis that matters here: headless, single-purpose, GPS-aligned, metadata sidecar, and field-proven against the target instance.

## The wire protocol (documented, so we can reimplement cleanly)

Reconstructed from `clients/iq-recorder/main.go` + `pcm_decoder.go`. The whole thing is ~130 lines of framing.

**Pre-flight (HTTPS):**
- `POST /connection` with `{"user_session_id": "<uuid>", "password": "<optional>"}` → `{allowed, bypassed, session_timeout, max_session_time, allowed_iq_modes[]}`. If `allowed_iq_modes` is non-empty and `bypassed` is false, `"iq48"` must be in the list or the server will refuse the mode.
- `GET /api/description` → receiver JSON (callsign, lat/lon/asl, `gps.tdoa_enabled`, and the `frequency_reference` block). Save it verbatim as the sidecar.

**Stream (WSS):** `wss://<host>:443/ws` with query `frequency=<Hz>`, `mode=iq48`, `user_session_id=<uuid>`, `format=pcm-zstd`, optional `password=<url-escaped>`; headers `User-Agent`, optional `X-Password`.

**Binary frames** are zstd-compressed. Decompress, then read a hybrid header:
- **Full — magic `"PC"` (0x5043), 29 bytes:** `[2] magic · [1] version · [1] format(0=PCM,2=PCM-zstd) · [8] RTP timestamp u64-LE · [8] wallclock u64-LE (deprecated) · [4] sample-rate u32-LE · [1] channels u8 · [4] reserved`, then PCM. **GPS timestamp = the u64-LE at offset 4** (nanoseconds since Unix epoch).
- **Minimal — magic `"PM"` (0x504D), 13 bytes:** `[2] magic · [1] version · [8] GPS ts u64-LE (offset 3) · [2] reserved`, then PCM. Reuses sample-rate/channels from the last full header.
- **PCM payload** is **big-endian int16**, interleaved `I,Q,I,Q…`; byte-swap to little-endian for a WAV. For iq48 the rate/channels are fixed at 48000/2 (the client pre-seeds these because the server sends no initial text status for binary formats).

Text frames, when present, carry `{sampleRate, channels}` — only relevant if we ever use a mode other than iq48.

A C# port therefore needs only `System.Net.WebSockets.ClientWebSocket`, a zstd decoder, and this header parse. **Upstream is GPL-3.0** (`LICENSE.TXT` + the repo's SPDX are GPLv3) — compatible with this repo's GPL-3.0-or-later, so we may either run the binary at arm's length or port with a provenance comment; never let a port pull this stack into anything MIT.

## Plan of work

### C0 — de-risk with the upstream binary — **DONE (2026-07-24)**

Ran; full write-up and plots in [`evidence/2026-07-24-ota-c0/`](evidence/2026-07-24-ota-c0/). Reproduction:

```
git clone https://github.com/madpsy/ka9q_ubersdr && cd ka9q_ubersdr/clients/iq-recorder
go build -o build/iq-recorder .
./build/iq-recorder -host m9psy.tunnel.ubersdr.org -port 443 -ssl \
    -frequency 7074000 -duration 30 -output-dir .
```

Instrument audit results (a hidden AGC or clipped ADC would quietly fit our numbers, per the campaign-audit lesson — so each was checked):
- **`iq48` is open to guests** — `/connection` → `allowed: true`, `allowed_iq_modes: [iq48, iq96]`, `bypassed: false`; `public_iq_modes: [iq48, iq96]`. The mode is not gated; capture is not blocked on any operator permission.
- **Session budget generous** — `max_session_time: 10800` (3 h), `daily_time_remaining_secs: -1` (unlimited), 19/20 client slots free. A 1-hour ladder fits one session; chunk-per-pass reconnect is hygiene, not a necessity.
- **No AGC on the IQ channel** ✓ — a known-quiet noise band's floor stays flat (std 1.6 dB) and does *not* drop when total power steps up as a strong signal arrives. The channel is linear; SNR/fade measurement is trustworthy.
- **16-bit adequate** ✓ — no clipping, −11.7 dBFS peaks (12 dB headroom), noise floor 8.4 bits above the LSB.
- **GPSDO locked, offset 0 Hz** ✓ — `frequency_reference` reads expected = detected (25 MHz), offset 0, SNR 54 dB; `gps_lock`/`pll_lock` true, 3D/9 sats. RX dial error ≈ 0 and self-reported.
- **Timing clean** ✓ — contiguous GPS timestamps, no drops, exactly 48000.0 samples/s, sample-0 UTC in the filename.
- **Clean front end** ✓ — flat ±24 kHz PSD, sharp edge roll-off, no DC spur, no IQ-imbalance ridge.
- **New rule — startup transient**: the first ~0.7–1.0 s after connect ramps from a low level. **The client/converter must discard the first ~1 s of every session and reconnect** — the real cost of chunk-per-pass (≈1 s/pass lost), which argues for one long session over many short passes within the 3 h cap.

**Operator status:** the `m9psy` operator is happy for us to proceed; any access permission is grant-on-ask. In the event, C0 needed none — `iq48` and a 3 h session are already available to public users. A bypassed/authenticated session is still worth requesting for the real campaign (removes any shared-slot contention during the ladder), but it is not a prerequisite.

### C1 — in-repo C# capture client — **DONE (2026-07-24)**

Built as `tools/Packet.SoundModem.UberSdr` (`sm-iqcapture`): `PcmBinaryDecoder` + streaming `StereoPcmWavWriter` + `UberSdrIqClient` (preflight → `/api/description` → WSS receive with the 1 s startup guard → WAV + manifest sidecar, filename stamped with sample-0 GPS UTC, final packet trimmed to hit `--duration` exactly). `ZstdSharp.Port` added to CPM. **Validation:** 8 unit tests (decoder header/offset/byte-swap/zstd cases + WAV round-trip) green, and a live *simultaneous* capture off `m9psy` cross-checked against the reference Go client is **bit-identical in steady state** (100.0% of samples equal once both channels warm up; overall correlation 0.99999866 over 27.75 s, integer lag 0 — the two clients' GPS-timestamp→sample mapping agrees to the sample). As-built notes below.

- **Home:** `tools/Packet.SoundModem.UberSdr` (a tool/application, **not** the published core library). `PcmBinaryDecoder` carries a provenance comment citing `clients/iq-recorder/{main.go,pcm_decoder.go}` per the repo's porting rule.
  - **Superseded 2026-08-02**: the wire decoder (`PcmBinaryDecoder`, `ConnectionResponse`) moved to `src/Packet.SoundModem/UberSdr/` and both IQ→audio converters to `src/Packet.SoundModem/Iq/`, when the daemon gained the `ubersdr:` receive-only device and needed the same protocol and the same SSB demodulator live. The shipped package therefore now carries a `ZstdSharp.Port` dependency (MIT). `sm-iqcapture` is unchanged in behaviour and now consumes them from the library; the provenance comment moved with the file.
- **Deps:** add a permissive zstd decoder (e.g. `ZstdSharp.Port`) to `Directory.Packages.props` (CPM — no `Version=` on the `PackageReference`); `System.Net.WebSockets` is in-box. Alternatively request `format=pcm` if the server serves uncompressed and drop the dependency.
- **Behaviour:** connect → decode → write int16 stereo WAV + JSON sidecar, filename stamped with sample-0 GPS UTC. **Chunk one file per ladder pass with reconnect between passes** — this is the natural fit for the test's "repeat the ladder for ≥1 hour" and it survives any `max_session_time` cap without a bespoke keepalive; GPS timestamps make the passes trivially re-concatenable offline.
- **Manifest hook:** on close, emit per-file SHA-256, the receiver metadata, and a GPSDO `frequency_reference` snapshot straight into the evidence pipeline (see C3).
- **Test:** unit-test the packet decoder against golden frames captured in C0, asserting byte-for-byte parity with the Go client's WAV output. That is the cross-check that keeps the reimplemented instrument honest.

### C2 — offline IQ→audio converter — **core DONE (2026-07-24)**

Built as `IqToAudioConverter` in the same tool (`sm-iqcapture convert`). Validated by a loopback unit test: MS110D modulate → synth 48 kHz USB IQ (modem at a chosen dial offset, int16-quantised, AWGN, with a deliberate LSB image) → convert → the MS110D demodulator recovers the **exact payload** across WN0/2/6 and dial offsets of 0/±(0.75–1.2 kHz) — the NCO correction and the image-rejecting complex SSB bandpass both proven. Also run on the real C0 FT8 capture: 48 kHz IQ → 9600 Hz mono audio with the energy correctly in the SSB band. Still **to do (needs real OTA bursts)**: per-burst GPS/preamble alignment, CFO-vs-GPSDO logging, and wiring the output into the MS110D scorer/B0 telemetry. As-built pipeline per burst:

Turns an IQ48 capture into the real 9600 Hz audio the demodulator and the B0 telemetry consume (the modem is native **9600 Hz, 1800 Hz carrier** — `Ms110dModem`/`Ms110dDemodulator`). Pipeline per burst:

1. Read stereo-int16 WAV → complex samples at 48 kHz.
2. NCO frequency-shift so the modem's occupied band lands where the harness expects it (**1800 Hz** centre, App D convention). Tuning the ka9q channel to the SSB **suppressed-carrier (dial)** frequency puts the USB signal at +0.3…+3.3 kHz in the complex baseband.
3. Apply **our** SSB filter as a complex bandpass over that band — run two variants: an ideal RRC-matched brick wall (clean, RX-truncation-free — the reference) and a filter that emulates the ka9q USB passband (the "as-heard" case, for comparison). Rejecting the negative-frequency side here is what makes step 4 clean.
4. Take the real part → real audio with the modem at 1800 Hz.
5. Resample 48 kHz → 9600 Hz; hand to the `Decode` scorer / B0 telemetry.

Alignment and CFO: coarse-align each burst from GPS UTC (TX schedule ↔ sample-0), fine-align by preamble correlation. **Do not pre-correct CFO** — let the demod's ±75 Hz search absorb the Flex dial error, log the estimate, and compare it to the GPSDO-implied RX accuracy for attribution.

### C3 — corpus & manifest (extends ota-test-plan.md *Corpus discipline*)

Audio and IQ do not go in git. Per-pass IQ WAV (~1.3 GB/hr at iq48) + JSON sidecar go to object storage (OARC static hosting is the candidate); the repo's `docs/ms110d/evidence/ota-<date>/` gets the manifest — per-burst WN, seed, sample-0 UTC, TX power, capture URLs, SHA-256 of every file — plus the derived audio-scoring outputs. The **IQ is the durable regression fixture**: re-scoreable forever against any future demodulator *and* any future SSB-filter choice, which the SSB-audio path could never offer. Small, git-committable exemplars (like the existing 20 s `samples/offair/gb7rdg-…wav`) can live under `samples/offair/` for guarded tests.

## Risks / watch-items

- **Session-time cap** → resolved in C0: 3 h cap, so a 1-hour ladder is one session; reconnect is hygiene. Prefer one long session over many passes.
- **AGC on the IQ channel** → audited clean in C0 (linear channel, floor decoupled from signal). Re-check if the operator changes the channel config.
- **16-bit scaling / clipping** → verified fine in C0 (12 dB headroom, floor 8.4 bits up). IQ96/192 are the same bit width, so any future remedy is RX gain management, not a wider mode.
- **Startup transient** → first ~1 s after connect is not usable; the client must drop it, and the ladder should lead with a >1 s guard before the first burst.
- **zstd requirement** → add `ZstdSharp.Port` (permissive) or request `format=pcm` if supported.
- **Single RX for now** → a second instance is a drop-in (`-host … -host …` triggers GPS-aligned dual capture) to separate ionospheric effects from site-local QRM; deferred, and the test plan already lists it as "desirable when available".
- **Licence** → upstream is GPL-3.0 (compatible); keep any port provenance-commented and never let it entangle an MIT package.

## Edits to fold back into ota-test-plan.md (once C0 confirms)

Swap the RX line from "USB … native recording (WAV)" to "IQ48 complex WAV via the `ka9q_ubersdr` `iq-recorder`/`UberSdrIqCapture` client"; mark investigate-items (a) and (b) resolved; and add sample-0 GPS UTC and the GPSDO `frequency_reference` as two new free instruments in the *Scoring* section.
