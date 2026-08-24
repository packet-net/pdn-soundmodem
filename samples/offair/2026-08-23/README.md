# 2026-08-23 - a survey capture of nothing at all

`20260823-131719-2921hz-unclaimed.wav` is the signal survey's own output from M0LTE's 40 m
station (Flex 6500, dial 7.049450 MHz USB), and its sidecar states, in good faith:

```
"audioCentreHz": 2920.9,  "widthHz": 431.6,  "durationSeconds": 0.167,  "peakSnrDb": 37.1
```

A 431 Hz signal at 37 dB over the noise. **There is no signal.** Every number in that sentence
is arithmetic on an absence, and this file is here because **3,433 of the station's 8,874
unclaimed captures were the same thing** - more than a third of everything it had kept.

## What is actually in it

The station's slice receive filter passes 450 to 2550 Hz. Above that cut the audio is not quiet,
it is *empty*: the quiet lines of this file sit at **-110 to -117 dBFS**, and the waterfall's own
byte encoding bottoms out at -100 dBFS, so those bins read as byte zero and the burst detector's
tracked floor sits at exactly the bottom of its scale.

Then, at **t = 1.02 s**, every band in the file rises at once - 200 Hz to 6 kHz, to about
**-47 dBFS** - for a single 85 ms transform window, while the 50 ms RMS envelope does not move at
all (-25.4 to -26.0 dBFS throughout). That is not a signal arriving. A level change would show in
the envelope; this does not. It is a **break in the waveform** - a splice, a repeated block, a
phase step where two buffers were joined - which is broadband by construction and lasts exactly
as long as one window.

Inside the passband the break is invisible, buried under real signal and real noise. Above the
cut there is nothing to bury it, so 53 dB over a floor that is measuring nothing reads as a
magnificent burst. **That is why they all cluster above the filter cut**: not because the radio
hears more up there, but because it is the only part of the spectrum quiet enough for a
discontinuity to show.

The capture window is `[burst start - 1 s, burst end + 1 s]`, which is why the break sits at
t = 1.02 s in every one of these files.

## What it fixed

`EmptyBandTests`, and two changes in `SpectralBurstDetector`:

- **A bin whose floor has reached the encoding's own floor cannot support a detection.** It is
  not measuring a quiet channel, it is measuring nothing, and an SNR against it is meaningless.
  The threshold is 3 dB over `WaterfallSource.FloorDb`; the station's quietest *real* in-passband
  bins sit 16 dB up and its ordinary ones 25 to 55.
- **`minSeconds` now means what it says.** `Math.Round(0.15 * 30)` is 4, not 5 - 4.5 goes to even
  - so "at least 0.15 s" was really "at least 0.133 s", and the two thirtieths of a second in the
  gap are precisely where a one-window event lives.

## What it did not fix

The filter's **roll-off** is a continuum, not a cliff. This station's 2600-2900 Hz bins sit about
16 dB over the byte floor: attenuated hard, but genuinely measuring something, so the dead-bin
rule does not reach them and a break there can still open a burst. Captures in that transition
region remain, at a lower rate. Judging a burst against the *channel's* noise floor rather than
only its own bin's is the obvious next move and is not done here.

## A note on replaying these

Seeding a floor by tiling a capture's quiet lead-in **with random sign flips is a white-noise
generator**: it fills every empty bin at the file's own RMS and hides exactly the hole this file
is about. An early version of the harness for this investigation did that and reproduced nothing.
Crossfaded tiling keeps the file's spectrum, holes included, and puts no discontinuity at the
joins. `EmptyBandTests.Seed` is the version to copy.
