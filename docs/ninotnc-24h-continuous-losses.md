# NinoTNC 24 h benchmark — continuous-decode losses

_Recorded 2026-07-19 from the GB7RDG 40 m off-air benchmark (`~/gb7rdg-capture`)._

## Summary

Over the 22.9 h ring buffer our differential frequency-diversity bank copied **96.5 %** of the
NinoTNC's 2 120 frames (matched 2 046, **missed 74**, plus **83** the NinoTNC itself missed). Of
those 74 misses, **37 decode cleanly from their own extracted audio** — run
`nino-compare decode --wav <snippet> --detector differential --pairs 4` on the snippet and the frame
copies, yet the whole-chunk `analyse` pass dropped it. They were **not** lost because the audio is
hard; they were lost in the *continuous* 15-minute per-chunk decode.

The remaining 37 misses fail even in isolation and are the honest expected-fail corpus in
`NinoTncMissCorpusAspirationTests` / `samples/offair/misses-24h/` (24 decode nothing standalone,
13 decode a neighbour in the window but not the target).

## What it is not

It is **not** the offset step. `BpskMultiModem` uses a fixed step of `baud / 40 = 7.5 Hz` when no
`offsetHz` is given (`--step` absent), identical in the per-chunk `analyse` pass and in an isolated
`decode`. So the branch grid is the same; only the *history* feeding the demod differs.

## Likely mechanism (to investigate)

A frame arriving mid-stream meets a receiver whose state was shaped by everything before it — prior
transmissions, inter-frame noise, and collisions during the busy morning peak (42 of the 74 misses
fall in 07–09 UTC). A fresh snippet start lets acquisition (timing/phase, AGC, DCD gating) settle
cleanly. Candidates, roughly in order:

1. **DCD / squelch carry-over** — carrier-detect state from a preceding signal masking the new preamble.
2. **AGC / level state** — gain wound to a prior (louder or quieter) signal.
3. **Timing- or phase-recovery carry-over** between back-to-back transmissions.
4. **`FrameDeduper` (3 s window)** collapsing a genuine frame against a near-simultaneous branch decode.

## Impact

Roughly **half** our misses are recoverable without touching the hard-frame problem: the honest copy
ceiling on this capture is nearer **98.3 %** (2 083 / 2 120) if continuous-decode robustness is
fixed. Worth a focused look at per-frame decode state isolation over a busy channel.

## The 37 frames (all decode standalone, lost in-stream)

| time (UTC) | from → to | bytes | frame (hex) |
|---|---|---|---|
| 2026-07-18T16:27:11Z | `EI0RSI-7` → `EI7KX` | 78 | `8A926E96B040E08A9260A4A6…` |
| 2026-07-18T18:36:12Z | `GB7WEM-7` → `GB7RDG-2` | 86 | `8E846EA4888EE48E846EAE8A…` |
| 2026-07-18T19:17:04Z | `GB7BWR-2` → `MAIL` | 31 | `9A8292984040E08E846E84AE…` |
| 2026-07-18T21:16:11Z | `GB7WEM-7` → `GB7RDG-2` | 106 | `8E846EA4888EE48E846EAE8A…` |
| 2026-07-19T00:20:34Z | `GB7BPQ` → `BEACON` | 46 | `848A82869E9CE08E846E84A0…` |
| 2026-07-19T00:50:28Z | `GB7BPQ` → `BEACON` | 46 | `848A82869E9CE08E846E84A0…` |
| 2026-07-19T05:21:54Z | `PD4R-12` → `IW2OHX-8` | 15 | `92AE649E90B0F0A08868A440…` |
| 2026-07-19T05:22:01Z | `PD4R-12` → `IW2OHX-8` | 15 | `92AE649E90B0F0A08868A440…` |
| 2026-07-19T05:43:33Z | `GB7BPQ` → `GB7RDG-2` | 19 | `8E846EA4888EE48E846E84A0…` |
| 2026-07-19T07:09:20Z | `EI0RSI-1` → `GB7RDG-2` | 15 | `8E846EA4888E648A9260A4A6…` |
| 2026-07-19T07:27:52Z | `EI0RSI-1` → `GB7RDG-2` | 15 | `8E846EA4888E648A9260A4A6…` |
| 2026-07-19T07:27:57Z | `EI0RSI-1` → `GB7RDG-2` | 15 | `8E846EA4888E648A9260A4A6…` |
| 2026-07-19T07:29:14Z | `EI0RSI-1` → `GB7RDG-2` | 15 | `8E846EA4888E648A9260A4A6…` |
| 2026-07-19T08:33:05Z | `EI0RSI-1` → `GB7RDG-2` | 15 | `8E846EA4888E648A9260A4A6…` |
| 2026-07-19T08:36:00Z | `GB7WEM-7` → `GB7RDG-2` | 15 | `8E846EA4888EE48E846EAE8A…` |
| 2026-07-19T08:36:11Z | `GB7WEM-7` → `GB7RDG-2` | 15 | `8E846EA4888E648E846EAE8A…` |
| 2026-07-19T08:39:47Z | `G8BPQ` → `APBPQ1` | 65 | `82A084A0A262E08E7084A0A2…` |
| 2026-07-19T08:47:55Z | `GB7BWR-2` → `MAIL` | 31 | `9A8292984040E08E846E84AE…` |
| 2026-07-19T08:48:45Z | `GB7WEM-7` → `GB7RDG-2` | 106 | `8E846EA4888EE48E846EAE8A…` |
| 2026-07-19T08:54:48Z | `GB7WEM-7` → `GB7RDG-2` | 15 | `8E846EA4888EE48E846EAE8A…` |
| 2026-07-19T08:55:18Z | `GB7WEM-7` → `GB7RDG-2` | 15 | `8E846EA4888EE48E846EAE8A…` |
| 2026-07-19T08:55:32Z | `GB7WEM-7` → `GB7RDG-2` | 15 | `8E846EA4888EE48E846EAE8A…` |
| 2026-07-19T08:58:47Z | `G8BPQ` → `APBPQ1` | 65 | `82A084A0A262E08E7084A0A2…` |
| 2026-07-19T09:04:10Z | `G8BPQ` → `APBPQ1` | 65 | `82A084A0A262E08E7084A0A2…` |
| 2026-07-19T09:17:36Z | `G8BPQ` → `APBPQ1` | 65 | `82A084A0A262E08E7084A0A2…` |
| 2026-07-19T09:26:41Z | `GB7WEM-7` → `GB7RDG-2` | 41 | `8E846EA4888EE48E846EAE8A…` |
| 2026-07-19T09:32:57Z | `GB7WEM-7` → `GB7RDG-2` | 15 | `8E846EA4888EE48E846EAE8A…` |
| 2026-07-19T09:34:19Z | `GB7OXF-2` → `GB7RDG-2` | 35 | `8E846EA4888EE48E846E9EB0…` |
| 2026-07-19T09:34:36Z | `GB7OXF-2` → `GB7RDG-2` | 15 | `8E846EA4888EE48E846E9EB0…` |
| 2026-07-19T09:34:47Z | `GB7WEM-7` → `GB7RDG-2` | 15 | `8E846EA4888EE48E846EAE8A…` |
| 2026-07-19T09:38:40Z | `GB7WEM-7` → `GB7RDG-2` | 106 | `8E846EA4888EE48E846EAE8A…` |
| 2026-07-19T09:40:12Z | `GB7WEM-7` → `GB7RDG-2` | 106 | `8E846EA4888EE48E846EAE8A…` |
| 2026-07-19T09:41:44Z | `GB7WEM-7` → `GB7RDG-2` | 15 | `8E846EA4888EE48E846EAE8A…` |
| 2026-07-19T09:42:01Z | `GB7WEM-7` → `GB7RDG-2` | 15 | `8E846EA4888EE48E846EAE8A…` |
| 2026-07-19T09:42:27Z | `GB7WEM-7` → `GB7RDG-2` | 15 | `8E846EA4888EE48E846EAE8A…` |
| 2026-07-19T09:42:58Z | `GB7WEM-7` → `GB7RDG-2` | 15 | `8E846EA4888EE48E846EAE8A…` |
| 2026-07-19T10:18:00Z | `GB7BWR-2` → `MAIL` | 31 | `9A8292984040E08E846E84AE…` |

## Reproduce

```sh
# lost in the full-chunk pass, but this copies it:
nino-compare decode --wav /home/tf/gb7rdg-capture/analysis/misses-24h/miss-<stamp>-<from>-to-<to>.wav \
  --detector differential --pairs 4
```
