# Streaming conversion and burst scoring (§S1, §S2) — 2026-07-25

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

## Then §S2 — the scorer, and what auditing it turned up

`Ms110dReferenceBits` regenerates what went on the air (payload from its seed, channel bits per block through the production framing/puncture/interleave), and `BurstScorer` streams a capture through one demodulator and grades every burst it finds. `sm-ota score` ties it together. On a synthetic 60 s pass with three WN6 bursts and a fourth scheduled but never transmitted:

```
  #   start s   WN   CFO Hz   SNR dB   coded BER  uncoded BER  blocks  end
  0     10.10    6     -0.0     30.5           0            0       1  Eom
  1     30.10    6     -0.0     30.4           0            0       1  Eom
  2     50.10    6     -0.0     30.4           0            0       1  Eom
scored 3 burst(s); 0 unscheduled; 1 MISSED
MISSED: WN6 seed 4 expected at 55.0 s
```

**Burst starts come from carrier detect, not from the first block event.** The first attempt used the block event, and it reported `StartSeconds == EndSeconds` for every burst with no SNR at all — because a block event fires only once the whole block has arrived, so it marks the burst's *end*. The symptom was a null SNR; the cause was a start marker that was really an end marker.

### The reference bits are right, and here is why we believe it

Comparing the re-encoded bits against the demodulator's first-pass hard decisions on a **noiseless** channel gives 0 errors for WN0, WN6 and WN13 — and **3 errors in 768 for WN2**, at wire positions 24, 40 and 46.

That is the sort of small discrepancy it would be easy to shrug at or to paper over with a tolerance. The LLR magnitudes settle it:

| | value |
|---|---|
| LLR magnitude at the three errors | 0.032, 0.061, 0.414 |
| block median LLR magnitude | 1.507 |
| block minimum LLR magnitude | 0.032 |

**The three errors are the three least-confident decisions in the block.** A misaligned reference — wrong wire order, wrong puncture, wrong interleaver increment — compares against bits that are unrelated, so about half its errors would land *above* the median confidence and there would be hundreds of them. Ambiguity puts them all in the low tail. A 20-super-frame preamble instead of 3 changes nothing (identical positions, identical magnitudes), so it is not acquisition settling either.

So the test asserts the invariant rather than a count: **on a noiseless channel the demodulator may be unsure, but it must never be confidently wrong about a bit that was actually transmitted.** A tolerance of "≤ 3 errors" would have passed a genuinely misaligned re-encoder on a shorter block, and would have needed loosening every time a waveform was added.

The WN2 behaviour itself is an observation for whoever owns the demodulator — recorded here, not acted on.

## Also

- `PcmWavReader` — chunked reading, and a capture cut off mid-write (every interrupted session leaves one) is read as far as it goes rather than refused.
- `--gain auto|<factor>` replaces the silent whole-file peak normalisation. `auto` makes a second pass; either way the peak, the gain and any clipping are reported. A per-file scale is not comparable between files, so the help says so.
- Steady-state allocation is asserted directly (≤4 KB over 16 blocks of 65536 frames), not inferred from the design.
- `sm-ota` now reports a lost radio connection as an error rather than a `SocketException` stack trace, and `measure --purity` refuses a window with no spectrum near the stated carrier instead of referencing its answer to bin −1.
