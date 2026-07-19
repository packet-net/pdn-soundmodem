# Off-air miss corpus — GB7RDG 40 m, 2026-07-18/19

Real BPSK300 IL2P+CRC frames the **GB7RDG slot-3 NinoTNC decoded off-air** that our differential
frequency-diversity bank did **not** copy, during the 22.9 h benchmark on the `gb7rdg-capture` rig
(7050.10 kHz USB, 1500 Hz audio centre). These drive
`Modems/NinoTncMissCorpusAspirationTests.cs` — an expected-fail scoreboard of frames we should copy
but don't yet.

## Files

- `*.wav` — one snippet per frame, **12 kHz mono 16-bit** (the channel DSP rate), ~9 s, with the
  target frame ~6 s in. Named `miss-<UTCstamp>-<from>-to-<to>.wav`. Downsampled from the 48 kHz
  capture with `sox`; each was re-verified to still fail at 12 kHz (none flipped).
- `manifest.json` — `{wav, hex, from, to, iso, cls}` per frame. `hex` is the expected AX.25 frame
  bytes; the test asserts our bank decodes exactly that from the snippet.

## Why these 37 and not the day's full 74 misses

The benchmark's continuous 15-min-per-chunk decode missed 74 of the NinoTNC's 2 120 frames
(96.5 % copy). We split them by whether the frame decodes from its **own isolated audio**:

| bucket | n | here? |
|---|---|---|
| `FAILS_ISOLATED` — decodes nothing standalone (genuinely marginal: weak/short) | 24 | ✅ |
| `DECODES_OTHER` — a neighbour in the window decodes, but not the target | 13 | ✅ |
| decodes the target fine standalone — lost only in the continuous stream | 37 | ❌ → see below |

Only the 37 in the first two buckets are honest per-frame aspirations: their **own** audio does not
copy, independent of stream context, so a snippet test reproduces the failure deterministically. The
other 37 decode cleanly in isolation and were dropped only mid-stream (a continuous-decode
robustness gap, **not** the offset step) — they are tracked in
`docs/ninotnc-24h-continuous-losses.md`, not as unit tests, because faithfully reproducing them
needs committed multi-frame continuous audio (which we deliberately don't commit).

## Discipline

Category `Aspiration`: non-blocking. When one starts copying, move it to `NinoTncParityTests` and
delete its `manifest.json` row — do not loosen the assertion to make it pass. Misses skew to
weak/distant paths (GB7BPQ = Isle of Lewis, GB7BWR, G8BPQ) and short supervisory frames.
