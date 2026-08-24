# 2026-08-24 - one station, captured twice: before and after the burst detector's floor fix

Both files are the signal survey's own output rather than hand-made recordings: M0LTE's 40 m
station (Flex 6500, dial 7.049450 MHz USB) wrote them unattended on 2026-08-24, an hour apart,
and they hold the same station's beacon. `133727` was written by the burst detector that cut it
off (#353) and `152242` by v0.44.0, which does not, and that is why the pair is worth keeping
together: the first will not decode and the second does.

Both are **16-bit mono PCM at 12 kHz**, verdict **unclaimed** - packet-shaped energy outside
every modem the station was configured to listen to.

## What the station is

`PD4R-12`, beaconing in **300 baud AFSK**: 2-FSK, tones **1023 / 1227 Hz** (204 Hz shift), centre
~1120 Hz audio = **7.050570 MHz**, about 21 dB out of the noise. Framing is **plain AX.25** -
HDLC and an FCS, no IL2P. The instantaneous-frequency histogram of either file is cleanly
bimodal, which is what settles it; the averaged spectrum alone is not enough to tell this from a
single shaped carrier, because a 204 Hz shift at 300 baud merges into one flat-topped lobe.

From `20260824-152242`:

```
PD4R-12>ALL  UI  pid=F0   (116 bytes, FCS clean)
:>>>>> PD4R-12 <<<<< qrv on 144.925 (fm 1k2) 144.775 (ssb) 14.105 (ssb) 7.049 (ssb) 438.175 (fm 9k6)
```

A single `afsk300` receiver reads it at any centre from 1010 to 1210 Hz.

## `20260824-133727-1149hz-unclaimed.wav` - the truncated one

Written at 13:37:27, **4.17 s**: 0.58 s of channel noise at about -33 dBFS RMS, then signal at
about -26 dBFS which is still at full strength in the last sample of the file. **It does not
decode**, at any centre - the frame is cut in half.

That is not the writer truncating a file. The WAV is internally complete and its RIFF sizes
agree. The burst detector decided the transmission had ended while it was still going, and the
capture window is `[burst start - MarginSeconds, burst end + MarginSeconds]`, so the last second
of the file is trailing margin full of a signal it had stopped seeing. The sidecar the station
wrote says `durationSeconds: 2.167` for a transmission that ran at least twice that.

The cause was the floor tracker's test for whether a bin is measuring noise: "was this line under
the 6 dB detection threshold". A modulated signal spends much of every bin's time between 0 and
6 dB over the noise - here, the gap between an FSK pair's two tones - and every one of those
lines was averaged into the floor as if the channel were quiet, so the floor walked up into the
signal until it no longer stood out. Replaying this file's own lines for 34.7 s: the floor in the
centre bin climbs **13.6 dB** (-59.1 to -45.5 dBFS), the detector reports fragments of 3.17,
2.40, 0.90, 0.20 and 0.30 s and then nothing at all for the last 22 seconds, and the survey
writes one capture and refuses four more on the frequency cooldown.

It is here because it is the evidence for that, guarded by `OffAirSurveyTests`, and because
nothing else in `samples/` shows the detector what a real modulated signal's spectrum does line
to line. Every other test of it paints flat rectangles, which is why a 25 s test passed
throughout.

## `20260824-152242-1134hz-unclaimed.wav` - the same beacon, whole

Written at 15:22:42 by v0.44.0, ten minutes after that fix reached the station. **6.43 s**: 0.5 s
of noise, 4.9 s of signal, 1 s of noise - a complete transmission with both ends inside the file.
Its JSON sidecar is kept beside it, because what the sidecar says is half of what this file
tests.

Two separate reasons nothing read it at the time, and the second is the one worth remembering:

- **`pdn-decode` swept all 46 modes over it and reported silence**, because every mode listens at
  its catalogue centre and `afsk300`'s is 1700 Hz. That is what `--centre`, the sidecar reading
  and `--sweep` exist for (#355) - see
  [docs/pdn-decode.md](../../../docs/pdn-decode.md#where-it-listens). This file is the
  regression: `CentreSweepTests` asserts the default sweep finds nothing in it and the sidecar's
  measured centre finds the beacon.
- **The station's own `afsk300-il2pc` bank would have copied it.** Fed this audio at its
  configured 850 Hz centre it decodes the frame - but only when told to read plain AX.25. It runs
  IL2P+CRC, and PD4R-12 does not send IL2P. Same population as the GB7BWR-2 / PD4R-11 finding of
  2026-08-03, and the same answer: a second modem entry. The station gained
  `{ "subChannel": 3, "mode": "afsk300", "rfFrequency": 7050570 }` on 2026-08-24 and copied the
  next beacon live off the air eight minutes later, at +1 Hz off that centre.

824 of that station's 8,836 unclaimed captures sit between 1080 and 1180 Hz, 35 to 90 a day,
every day of the month.

## `20260824-165052-2350hz-unclaimed.wav` - a real signal nothing in the tree can name

Written at 16:50:52, verdict **unclaimed**, sidecar `audioCentreHz: 2349.6`, `widthHz: 193.7`,
`durationSeconds: 1.533`, `peakSnrDb: 21.9`. **12 kHz mono, 3.54 s.**

This one is neither of the artefacts the other captures in this folder document. It is committed
because it is a real transmission this station could not read, which is what the survey is for,
and because working out what it is remains open.

### Measured

| | |
|---|---|
| Occupied width | **270 Hz** at -10 dB, **2206 to 2476 Hz** |
| Shape | flat-topped with steep skirts; no discrete tones anywhere in it |
| Centre | ~2341 Hz = **7.051791 MHz** |
| Duration | 2.1 s of signal in the file; the burst detector measured 1.533 s of it |
| Level | about 20 dB over the channel noise |

### What it is not

- **Not the station's own transmission.** The longest run of exactly-zero samples in the whole
  file is one sample. Compare the 2976 Hz capture of the same afternoon, which holds 2,051 of
  them. (The journal does show `tx[2] bpsk300` at 16:50:44 and 16:50:54, either side of it, and
  an `rx[2]` of `EI0RSI-1` at 16:50:55 - so the channel was busy, but this burst is in neither.)
- **Not a clean FSK pair.** The instantaneous frequency is a single smeared distribution centred
  on 2341 Hz, not two peaks. An early reading of "two tones 92 Hz apart" was an artifact of
  band-limiting the analysis to 2230-2520 Hz, which clipped one side of the signal; over a wide
  band it does not hold.
- **Not a clean BPSK.** Squaring the band-limited signal produces no strong line at twice the
  centre - the largest component near 2x carries 2% of the band's power.
- **Not anything in the catalogue.** `pdn-decode --packet --sweep` over it is **307 mode-and-centre
  combinations, all silent**.

### Where it sits

2350 Hz is exactly where modem 2's NinoTNC id-beacon ghost listens (`bpsk300` at 2150 Hz plus the
200 Hz ident offset). That is why this capture found the ghost bug - a ghost is a receive tap, so
neither its band nor its decodes were reaching the survey - but the ghost being deaf to *this*
signal is a separate question from the survey mislabelling its frequency, and it is not answered
here. A NinoTNC ident is 300 baud AFSK with a 200 Hz shift; this is 270 Hz of continuous spectrum
with no tone pair in it.
