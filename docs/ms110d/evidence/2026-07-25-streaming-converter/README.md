# Streaming IQ→audio conversion (§S1) — 2026-07-25

The offline half of the OTA harness, built after the [bring-up session](../2026-07-25-ota-bringup/README.md). Nothing here involves a radio: it is the piece that lets a campaign pass be scored at all, because a pass is an hour long by construction (the receiver's `max_session_time` is 3600 s) and the existing converter could not read one.

## Headline

**An hour of IQ48 converts in 13 s using 26 MB.**

| | before (`IqToAudioConverter`) | now (`StreamingIqToAudioConverter`) |
|---|---|---|
| Memory for an hour | ~4 GB of `double[]`, plus the 691 MB file read twice | **26.6 MB peak RSS**, independent of length |
| Wall clock for an hour | did not complete | **13.2 s** (274× real time), or 26 s with `--gain auto` |
| Arithmetic | 301 complex taps at 48 kHz, plus a 41-tap low-pass | 341 complex taps at 9.6 kHz |

Measured on a 691,200,044-byte synthetic capture (172.8 M frames) with WN6 bursts spliced in at 10 s, 1800 s and 3590 s. All three came out at −9.6 dBFS RMS against a −41.7 dBFS floor.

**The burst at 3590 s converts bit-identically to the one at 10 s** — normalised correlation 1.000000000, RMS difference 0.000000. After 172.8 M samples the NCO phasor and every filter's memory are still exactly where they should be, which is the property that would quietly rot a long pass if it were not true.

## How it is cheaper

The reference chain is NCO → complex SSB bandpass (`h`, 301 taps) → take the real part → real anti-alias low-pass (`g`, 41 taps) → keep 1 in 5. Because `g` is real, `g * Re{y} = Re{g * y}`, so the two filters collapse into a single complex kernel `e = h * reverse(g)` whose real part is taken at the end. That kernel is then evaluated **only at output instants** — a decimating filter costs taps-per-output, not taps-per-input.

Nothing is approximated. It is the same linear filter, evaluated where its result is wanted, and a test requires the two implementations to agree to ≤1e-6 RMS. They currently agree to ~1e-8, which is float-versus-double rounding in the reference's low-pass.

## What the equivalence test found

The first attempt disagreed by 2e-4 RMS — small, but forty times the rounding budget. The error was **confined to the first seven output samples**, which is what made it diagnosable: a wrong kernel spreads error through the whole file, a startup convention does not.

The cause: the reference started its low-pass cold at *k* = 0, discarding the sideband filter's output at negative instants. Those are not zero — at *k* = −1 the 301-tap window still overlaps the start of the capture — so the reference was truncating its own cascade. The composite form has no way to reproduce that, because it does not have two filters to start separately.

The reference was changed to prime its low-pass over the preceding 40 samples, which makes both implementations compute the same object: the zero-padded convolution of the two filters. That is the better definition on its own merits, and it is what makes the collapsed form exactly equivalent rather than approximately so. It moves the first ~4 ms of a converted capture and nothing else.

Worth keeping: **where an error lives is the diagnosis.** The test now reports the first and last differing sample index in its failure message, because "first=0 last=6" and "first=0 last=9626" are different bugs and the RMS alone cannot tell them apart.

## The gap that was closed

The handover recorded that **the receive converter's own tests could not detect a sideband inversion** — they synthesised their input with the same convention they decoded it with, so a reversed kernel cancelled itself and payloads still came back bit-exact. That is how the inversion survived in both converters until the transmit side, whose band placement is asserted against absolute frequencies, exposed it.

There is now a test with nothing to share a convention with: a bare complex exponential at +2000 Hz must appear in the audio at 2000 Hz, and one at −2000 Hz must not appear at all. It runs against **both** converters. It is the assertion that would have caught the original fault on day one.

## Also

- `PcmWavReader` — chunked reading, and a capture cut off mid-write (every interrupted session leaves one) is read as far as it goes rather than refused.
- `--gain auto|<factor>` replaces the silent whole-file peak normalisation. `auto` makes a second pass; either way the peak, the gain and any clipping are reported. A per-file scale is not comparable between files, so the help says so.
- Steady-state allocation is asserted directly (≤4 KB over 16 blocks of 65536 frames), not inferred from the design.
- `sm-ota` now reports a lost radio connection as an error rather than a `SocketException` stack trace, and `measure --purity` refuses a window with no spectrum near the stated carrier instead of referencing its answer to bin −1.
