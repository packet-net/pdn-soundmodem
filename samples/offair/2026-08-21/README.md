# 2026-08-21 - noisy FM off-air capture, mode not recorded

Five captures Tom made off an FM radio, handed over with the mode unknown ("two modes I think,
quite noisy") as the first real corpus for [`pdn-decode`](../../../docs/pdn-decode.md). They are
here because they are the material that corrected the tool's default sweep set, and because a
weak-signal FM recording of a shaped-PSK mode is not otherwise represented in `samples/`.

## What they turned out to be

**All five are one QSO in `bpsk1200` (IL2P+CRC, NinoTNC switch 1010), N2IRZ-2 to WA2M-2, carrying
a BPQChatServer-6.0.21.40 session.** Not two modes; every frame recovered from any of the five is
`bpsk1200`, and the spectra agree - the one file with real quiet gaps shows the mode's main lobe
at 844-2133 Hz, which is its 1500 Hz carrier plus and minus its 600 Hz half-baud.

`pdn-decode` over the five, default sweep: **7 distinct frames from 3 of the 5 files**.

| File | Frames | Notes |
|---|---|---|
| `packet-24140.wav` | 1 | RR nr=7 P/F, CRC clean, -4 Hz off centre |
| `packet-24141.wav` | 0 | noise-swamped |
| `packet-24444.wav` | 1 | RR nr=1 P/F, 1 byte FEC-corrected |
| `packet-24501.wav` | 5 | the clean one: three I frames and two RRs, 1-8 bytes corrected |
| `packet-24738.wav` | 0 | noise-swamped |

## Why four of them are hard

`packet-24501.wav` is the only file with a squelched noise floor: bursts at 63-65 dB over a
-28 dB floor, with real silence between them. The other four are **continuously full-scale noise**
- an open-squelch FM receiver with a weak signal, in-band and out-of-band energy about equal
(above 4 kHz within 1 dB of the 200-3500 Hz band). Nothing is clipped and the levels are fine
(-7 dBFS peak, -18 dBFS RMS on all five); the signal is simply down in the noise. That two of them
yield a frame at all is the diversity bank earning its keep.

## What they corrected

`pdn-decode` originally defaulted to the FM-native mode set, derived from
`FmModeProfiles.IsFmMode`. That set answers "which modes reach the air as frequency modulation",
which is a question about modulators, and it **read exactly none of this corpus**. The question
the tool is actually asked is what can arrive through an FM receiver, and Nino's own switch map
groups the shaped-PSK modes "Shaped PSK - SSB radios, **or FM radios**"
([mode-modulation-reference.md](../../../docs/mode-modulation-reference.md)). The default is now
the whole catalogue, and `SweepTests` pins the lesson.

## Reproducing

```
pdn-decode samples/offair/2026-08-21/*.wav
```

Provenance: Tom's own receiver, 48 kHz mono 16-bit, 2026-08-21. Amateur transmissions received
off air; callsigns are as transmitted.
