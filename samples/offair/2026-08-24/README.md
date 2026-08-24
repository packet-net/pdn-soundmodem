# 2026-08-24 - a signal survey capture that cut off mid-transmission

`20260824-133727-1149hz-unclaimed.wav` is the survey's own output, not a hand-made recording:
M0LTE's 40 m station wrote it unattended at 13:37:27 on 2026-08-24, verdict **unclaimed** -
packet-shaped energy outside every modem the station was configured to listen to. **16-bit mono
PCM, 12 kHz, 4.17 s.**

It is here because it is the evidence for the burst detector's floor tracker climbing into a
sustained signal (`OffAirSurveyTests`), and because nothing else in `samples/` shows what a real
modulated signal's spectrum does line to line. Every other test of the detector paints flat
rectangles.

## What the file holds

| | |
|---|---|
| 0.00 - 0.58 s | channel noise, about -33 dBFS RMS |
| 0.58 s - end | signal, about -26 dBFS RMS, still at full strength in the last sample |

Measured over the signal:

- **Occupied width 533 Hz** at -20 dB (847-1380 Hz), a single flat-topped lobe from ~975 to
  ~1290 Hz with steep skirts - one shaped carrier, not an FSK pair.
- **Envelope rate 300.0 Hz**, so **300 baud**. Consistent with a shaped single-carrier 300 Bd
  mode (BPSK300-like); the station could not read it and it is not identified here.
- **Peak SNR 21.5 dB** against the channel noise, and it does not vary across the file.

## The defect it caught (#353)

The signal is at full strength in the last sample, so the transmission outlasted the capture.
That is not the writer truncating a file - the WAV is internally complete and its RIFF sizes
agree. It is the burst detector deciding the transmission had ended while it was still going:

- The capture window is `[burst start - MarginSeconds, burst end + MarginSeconds]`. This
  detector replayed over the file opens its burst 1.03 s in, so at the 1.0 s default the burst
  the station recorded was 2.17 s and the last second of the file is trailing margin full of
  signal the detector was no longer seeing. (The sidecar JSON beside the WAV records
  `DurationSeconds` directly; it was not kept with this copy.)
- Replaying this capture's own lines for 34.7 s reproduced it: the floor in the signal's centre
  bin climbed **13.6 dB** (-59.1 to -45.5 dBFS), the detector reported five fragments of 3.17,
  2.40, 0.90, 0.20 and 0.30 s and then nothing at all for the last 22 seconds, and the survey
  wrote one capture and refused four more on the frequency cooldown.

The cause was the test for whether a bin is measuring noise. It was "is this line under the 6 dB
detection threshold", and a modulated signal spends much of every bin's time between 0 and 6 dB
over the noise - the gaps between symbols, the shoulders of the shaped spectrum. Those lines
were averaged into the floor as if the channel were quiet, and the floor walked up until the
signal no longer cleared it. Bins under an open burst are now held out of the floor entirely.
