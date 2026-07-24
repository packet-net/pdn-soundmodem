# sm-iqcapture — ka9q_ubersdr IQ capture

Records an IQ stream from a [ka9q_ubersdr](https://github.com/madpsy/ka9q_ubersdr) / UberSDR instance to a 16-bit stereo WAV (I = left, Q = right) plus a JSON sidecar. Built for the MS110D one-way OTA test — see [`docs/ms110d/ota-capture-client-plan.md`](../../docs/ms110d/ota-capture-client-plan.md) for the design and the documented wire protocol, and [`docs/ms110d/evidence/2026-07-24-ota-c0/`](../../docs/ms110d/evidence/2026-07-24-ota-c0) for the instrument audit.

One session per invocation (one WAV). Drive per-pass reconnect from a script — a fresh process per ladder pass — rather than reconnecting mid-file, so each file is one contiguous GPS-timestamped session.

## Usage

```
sm-iqcapture --host m9psy.tunnel.ubersdr.org --frequency 7074000 --duration 30 --out-dir captures
```

| Flag | Default | Notes |
|---|---|---|
| `--host` | (required) | UberSDR host |
| `--frequency` | (required) | tune frequency in Hz (IQ is centred here; ±24 kHz for iq48) |
| `--port` | 443 | |
| `--no-ssl` | (SSL on) | use plain ws/http |
| `--duration` | 0 | seconds; 0 = until Ctrl+C or the server closes |
| `--mode` | iq48 | iq48 (48 kHz) or iq96 (96 kHz) where the server allows it |
| `--name` | (host) | label used in the output filename |
| `--password` | — | for password-protected instances |
| `--out-dir` | . | |
| `--startup-guard-ms` | 1000 | audio discarded after connect to clear the stream-start transient (C0 finding) |

Output: `<name|host>_<frequency>_<sample0UTC>.wav` and a matching `.json`. The filename timestamp is the GPS UTC of the **first written sample** (after the startup guard), so absolute sample-0 time is exact for offline scoring. The sidecar carries the capture manifest (frequency, mode, sample0 UTC, frame count, SHA-256 of the WAV, session id) plus the receiver's `/api/description` (GPSDO frequency reference, location, antenna).

## Converting a capture to MS110D audio

`sm-iqcapture convert` runs the offline SSB demodulator (C2): an IQ48 capture WAV → the 9600 Hz real audio the MS110D demodulator consumes (modem on the 1800 Hz sub-carrier). Because we hold the complex baseband, *we* choose the passband, so RX-side filter truncation is never a confound.

```
sm-iqcapture convert --in capture.wav [--out audio9600.wav] \
             [--dial-hz 0] [--ssb-low 150] [--ssb-high 3450] [--out-rate 9600]
```

`--dial-hz` is the IQ-baseband frequency of the SSB suppressed carrier — set it to (our TX dial − the RX tune frequency); 0 when the RX is tuned exactly to our dial. Narrow `--ssb-low/--ssb-high` to emulate a tighter RX filter for an A/B against the RRC-clearing default. Chain: shift by −dial → complex bandpass (upper sideband only) → take real part → decimate to the output rate. `IqToAudioConverter` is validated by a modulate→synthesise-IQ→convert→demodulate loopback (`tests/Packet.SoundModem.Tests/UberSdr/`) recovering exact payloads across waveforms and dial offsets.

## How it works

1. `POST /connection` — check access and the allowed IQ modes.
2. `GET /api/description` — receiver + GPSDO metadata for the sidecar.
3. `wss://…/ws?frequency=…&mode=iq48&format=pcm-zstd` — each binary frame is a zstd-compressed PCM packet with a hybrid `PC`/`PM` header and a GPS-nanosecond timestamp; `PcmBinaryDecoder` decompresses, parses, and byte-swaps big-endian int16 → little-endian.
4. Discard the first `--startup-guard-ms`, then stream to the WAV, trimming the final packet to hit `--duration` exactly.

`PcmBinaryDecoder` is a direct port of `clients/iq-recorder/pcm_decoder.go` (GPL-3.0, compatible with this repo's GPL-3.0-or-later); the capture flow re-implements `main.go`. Cross-checked bit-identical against that reference client on a live simultaneous capture (see the plan's C1 section). Unit tests: `tests/Packet.SoundModem.Tests/UberSdr/`.
