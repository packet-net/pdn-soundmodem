# The shipped preset set, from the floor to the fastest

**Recorded 2026-09-20**, from the station table the radio1/radio2 bench was running that morning and from the on-air sweeps of the evening of 2026-09-19.

This is a deployable set, not a proposal: presets ordered from the most robust to the fastest, sharing three carrier layouts, so that one catalogue covers the range the waveform can do on a 25 kHz FM channel.

**All of them share one geometry table.** Each preset names its own layout by id in every burst header and acquires on entry 0, so a receiver configured for any preset in the set decodes a burst sent on any other, and a link can change bandwidth without a handshake. Ids are 0 for the narrow layout, 1 for the 6 kHz span and 2 for the 8 kHz span; rate variants of a span share its id because they share its layout. [geometry-signalling.md](geometry-signalling.md) has the wire format, the on-air proof, and the one caveat worth reading before you rely on it.

## The set

Coded payload rate is arithmetic: 22.727 symbols/s (44.0 ms, the 64-sample cyclic prefix) times the data carriers, times the bits per symbol, times the code rate. Goodput is what a KISS application moved end to end, scored from both stations' frame logs, at 1024 / 1900 / 3000 byte frames. The gap between the two is the four fixed symbols per burst, the rounding of a frame up to whole symbols, the AX.25 header and the keying time.

| mode | what it is | coded | goodput at 1024 / 1900 / 3000 B | delivered |
|---|---|---|---|---|
| `ofdm-fm-narrow` | 211 Hz to 2.9 kHz, QPSK, K=7 rate 1/2: the floor | 2.5 | 2.2 / 2.3 | every frame both directions, 2026-09-20; the only preset a voice-bandwidth path can carry |
| `ofdm-fm-6k` | 6 kHz, QPSK, K=7 rate 1/2: the robust data-port fallback | 5.5 | not measured | simulation only |
| `ofdm-fm-6k-fast` | 6 kHz, QAM-64 rate 2/3, contiguous bursts | 21.8 | 13.3 / 16.6 / 17.8 (see note) | every frame |
| `ofdm-fm-8k` | 8 kHz, QAM-64 rate 2/3, full bursts: the default | 29.4 | 17.5 / 22.3 / 24.7 | every frame at 1024 and 1900, 19 of 20 at 3000, both directions |
| `ofdm-fm-8k-r56` | the default at rate 5/6: the robust bulk choice | 36.7 | 19.2 / 25.5 / 29.8 | every frame or all but one in every cell, both directions |
| `ofdm-fm-8k-follow` | 8 kHz rate 2/3 with follow-on frames | 29.4 | 23.9 / 23.2 / 28.3 | every frame at 1024; 17 and 16 of 20 at 1900; 20 and 18 of 20 at 3000 |
| `ofdm-fm-8k-adaptive` | follow-on 2/3 with adaptive rate: the link chooses, capped at QAM-64 | up to 29.4 | whatever the link supports | see below |
| `ofdm-fm-8k-r78` | rate 7/8, for a better link than this one | 38.5 | 21.1 / 24.1 / 29.8 | every frame at 1024; 18 and 17 of 20 at 1900; 20 and 13 of 20 at 3000 |

**The 6 kHz fast figures are a floor, not a like-for-like.** They were measured on the bench's own 6 kHz profile, which did not set `contiguousBursts`, and at the 128-sample prefix. The shipped one turns contiguous bursts on, which bought the 8 kHz preset about 12 %, and runs at the shorter prefix, worth about 3 % more. Nobody has measured the two together on the 6 kHz span.

**One measured configuration is deliberately not shipped:** follow-on bursts at rate 5/6 reached 27.0 / 32.4 / 36.1 kbit/s, the fastest anything on this rig has gone, and it did it in one direction only. Every frame at 1024 and 1900 bytes and 13 of 20 at 3000 one way; 14, 10 and 3 of 20 the other way, in the same half hour. It is the asymmetry that [preset-design.md](preset-design.md) section 4.5 is about, and it is not a mode to hand somebody.

Which to run. `ofdm-fm-8k` is what both stations were left on as their persistent mode and is the honest default. `ofdm-fm-8k-r56` is the one to reach for when the link is good and the traffic is bulk: it delivered every frame or all but one in every cell in both directions, which none of the follow-on profiles managed. `ofdm-fm-8k-r78` is on the list to mark where the margin runs out on this rig rather than as a recommendation.

**The adaptive preset** has no figure of its own because it has no fixed rate: the controller steps it on what the link is spending. In the 2026-09-19 exercise, four rounds of ten 1900-byte frames alternating direction, it opened at BPSK 3/4 (5.7 kbit/s), walked to QPSK 3/4 (11.0) and then to QAM-64 2/3 on follow-on bursts (27.0) with every frame delivered, and the round that tried QAM-256 lost one frame one way and all ten the other. That exercise ran before the cap existed; `adaptiveTopConstellation` is QAM-64 by default now, measured against what a Raspberry Pi can decode in time (see [receiver-findings.md](receiver-findings.md), "Decode time against air time"), which is why that last round would not happen today.

## Writing a profile

A station can add profiles of its own in a geometry file beside the daemon, under whatever names it likes. Below are the parameters the shipped presets carry, written the way a profile in that file is written, so that a station wanting a variant has something to start from. A profile a station defines is a mode of its own; it does not redefine a built-in.

```json
{
  "6k-fast": {
    "sampleRate": 48000, "fftSize": 2048, "cyclicPrefix": 64,
    "firstCarrier": 9, "dataCarriers": 240, "pilotCarriers": 8,
    "coding": {"scheme": 1, "constraintLength": 7, "rateNumerator": 2, "rateDenominator": 3, "interleave": true},
    "constellation": 6,
    "contiguousBursts": true
  },
  "8k": {
    "sampleRate": 48000, "fftSize": 2048, "cyclicPrefix": 64,
    "firstCarrier": 9, "dataCarriers": 323, "pilotCarriers": 10,
    "coding": {"scheme": 1, "constraintLength": 7, "rateNumerator": 2, "rateDenominator": 3, "interleave": true},
    "constellation": 6,
    "peakToAverageLimitDb": 10,
    "contiguousBursts": true
  },
  "8k-r56": {
    "sampleRate": 48000, "fftSize": 2048, "cyclicPrefix": 64,
    "firstCarrier": 9, "dataCarriers": 323, "pilotCarriers": 10,
    "coding": {"scheme": 1, "constraintLength": 7, "rateNumerator": 5, "rateDenominator": 6, "interleave": true},
    "constellation": 6,
    "peakToAverageLimitDb": 10,
    "contiguousBursts": true
  },
  "8k-follow": {
    "sampleRate": 48000, "fftSize": 2048, "cyclicPrefix": 64,
    "firstCarrier": 9, "dataCarriers": 323, "pilotCarriers": 10,
    "coding": {"scheme": 1, "constraintLength": 7, "rateNumerator": 2, "rateDenominator": 3, "interleave": true},
    "constellation": 6,
    "peakToAverageLimitDb": 10,
    "contiguousBursts": true,
    "followOnFrames": true
  },
  "8k-adaptive": {
    "sampleRate": 48000, "fftSize": 2048, "cyclicPrefix": 64,
    "firstCarrier": 9, "dataCarriers": 323, "pilotCarriers": 10,
    "coding": {"scheme": 1, "constraintLength": 7, "rateNumerator": 2, "rateDenominator": 3, "interleave": true},
    "constellation": 6,
    "peakToAverageLimitDb": 10,
    "contiguousBursts": true,
    "followOnFrames": true,
    "adaptiveRate": true,
    "adaptiveTopConstellation": 6
  },
  "8k-r78": {
    "sampleRate": 48000, "fftSize": 2048, "cyclicPrefix": 64,
    "firstCarrier": 9, "dataCarriers": 323, "pilotCarriers": 10,
    "coding": {"scheme": 1, "constraintLength": 7, "rateNumerator": 7, "rateDenominator": 8, "interleave": true},
    "constellation": 6,
    "peakToAverageLimitDb": 10,
    "contiguousBursts": true
  }
}
```

## Four things to know when reading it

**Profiles are written natively at the rate the channel runs.** A pdn-soundmodem channel carrying an OFDM-FM mode runs at 48 kHz, so a profile is written at 48 kHz: a 2048-point transform and a 64-sample cyclic prefix, which is the 44.0 ms symbol the rate arithmetic above uses. A profile written at half that rate describes the same waveform on the wire and the plugin will rescale it, but two profiles mixing the conventions are the same layout written two ways and it is not worth the confusion.

**Carrier frequencies come from the layout, not from a number in the file.** The 6 kHz layout runs 211 Hz to 6.0 kHz and the 8 kHz one 211 Hz to 8.0 kHz, both starting at carrier 9, which is where this rig's audio path is within a quarter of a decibel and which buys half an octave of the best part of the band.

**`"peakToAverageLimitDb": 10` is stated on the wide QAM-64 profiles although it is that constellation's default**, so that a copy edited to another constellation keeps the measured setting rather than quietly inheriting a different default. The limit is per subcarrier clipping error, not a crest-factor target.

**`"coding"` and `"constellation"` are transmit choices only.** Every burst names both in its header and a receiver uses what the burst says, not what its own profile says. What has to be agreed in advance is the carrier layout, which is why both ends must be on the same mode.

## Sources

* The evening sweeps of 2026-09-19, sweeps 4 and 5 (22:11 to 23:04 UTC, the 64-sample prefix) for everything at 8 kHz, and sweep 3 for the 6 kHz preset, which was measured at the 128-sample prefix and would be about 3 % faster today. The raw sweep records are campaign evidence and are not in this repository.
* [preset-design.md](preset-design.md) for how the 8 kHz preset was arrived at and what still limits it.
* [geometry-signalling.md](geometry-signalling.md) for the geometry table machinery and why no shipped preset uses it.
