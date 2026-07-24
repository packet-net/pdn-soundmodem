# OTA C0 — RX capture instrument audit (2026-07-24)

De-risking spike for the [OTA capture client plan](../../ota-capture-client-plan.md) Phase C0. Pure receive — no transmit. Verifies that the `ka9q_ubersdr` IQ path off `m9psy.tunnel.ubersdr.org` is a sound instrument before we commit to it, and answers the open questions from real behaviour rather than correspondence.

## What was run

- Built the upstream `clients/iq-recorder` (Go, GPL-3.0) unchanged.
- Probed the HTTP endpoints: `GET /api/description` (→ [`api-description.json`](api-description.json)) and `POST /connection` (→ [`connection.json`](connection.json)).
- Captured 30 s of `iq48` at 7074 kHz (40 m FT8 — intermittent strong signals, ideal for a dynamic-range and AGC probe): `m9psy…_7074000_2026-07-24T15:39:56.300Z.wav`, 16-bit stereo PCM @ 48 kHz, 5.49 MB. The WAV stays out of git (object storage per corpus discipline); its metadata sidecar is [`capture-sidecar.json`](capture-sidecar.json).
- Audited it with `scratchpad/audit_iq.py` → [`audit-7074-30s.png`](audit-7074-30s.png).

## Findings

**Access — no permission gate for the mode.** `/connection` returns `allowed: true`, `allowed_iq_modes: ["iq48","iq96"]`, `bypassed: false`; `/api/description` lists `public_iq_modes: ["iq48","iq96"]`. `iq48` is open to guests. Whatever the operator ask covers, capture is not blocked on it.

**Session budget is generous.** `max_session_time: 10800` (3 h), `session_timeout: 10800`, `daily_time_remaining_secs: -1` (unlimited daily), `max_clients: 20` with 19 free. A 1-hour ladder fits one session — chunk-per-pass reconnect becomes hygiene, not a necessity.

**GPSDO is real, locked, and reads zero.** `gpsdo`: `gps_lock: true`, `pll_lock: true`, 3D fix, 9 sats, PLL mode. `frequency_reference`: expected = detected = 25 MHz, **offset 0 Hz**, SNR 54 dB. RX dial error ≈ 0 and self-reported — the disciplined CFO anchor the test wanted; residual carrier offset in a capture is attributable to the Flex TX dial. (Note `receiver.gps.tdoa_enabled: false` — per-instance *position* is off, but the GPSDO time/frequency discipline is separate and on, so packet timestamps are disciplined.)

**Timing is clean.** First packet 15:39:56.300 UTC, last 15:40:26.281, span 29.981 s over 30 s wall — contiguous GPS timestamps, no dropped packets. Exactly 48000.0 samples/s (1,439,400 samples / 29.988 s).

**16-bit is comfortably adequate.** No clipping (`clip_frac = 0`); peaks −11.7 dBFS (12 dB headroom); RMS −27.8 dBFS; the quietest 100 ms block's noise sits **8.4 bits above the LSB**. Weak bursts will retain plenty of bit depth; strong bursts won't clip at this gain.

**No AGC on the IQ channel — the key result.** Tracking a known-quiet 4 kHz noise band (+18…+22 kHz, clear of all signals) over time: its floor is flat (std 1.6 dB) and, crucially, does **not** drop when total wideband power steps up ~4 dB at t≈20 s as a strong FT8 station arrives (visible in red on the spectrogram). An AGC riding the aggregate would have pulled that quiet floor down; it doesn't. The channel is linear. (The reported +0.30 quiet-vs-total correlation is an artifact of a shared startup transient, below — post-settling the floor is decoupled from signal.)

**Clean front end.** PSD flat across the full ±24 kHz, sharp channel-edge roll-off, no DC spur (DC bin ≈ −33 dB, level with neighbours), no IQ-imbalance ridge.

**Startup transient — actionable.** The first ~0.7–1.0 s after connect ramps up from a low level (both the spectrogram's leading edge and the AGC-probe traces starting at −15 dB). **The capture client / converter must discard the first ~1 s of every session and every reconnect.** This is the real cost of chunk-per-pass reconnect (≈1 s lost per pass) and argues for fewer, longer sessions given the 3 h cap, or a deliberate guard interval before the ladder starts.

## Verdict

The `ka9q_ubersdr` IQ48 path off `m9psy` is a sound, linear, GPSDO-disciplined capture instrument with adequate dynamic range and accurate absolute timing. Proceed to C1 (in-repo C# client) and C2 (offline IQ→audio converter). Carry forward two rules: **drop the first ~1 s** after any connect, and **prefer one long session** over many short passes within the 3 h budget.
