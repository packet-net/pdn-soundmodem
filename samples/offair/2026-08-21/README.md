# 2026-08-21 - noisy FM off-air capture, mode not recorded

Five captures Tom made off an FM radio, handed over with the mode unknown ("two modes I think,
quite noisy") as the first real corpus for [`pdn-decode`](../../../docs/pdn-decode.md). They are
here because they are the material that corrected the tool's default sweep set, because a
weak-signal FM recording of a shaped-PSK mode is not otherwise represented in `samples/`, and
because one of them is a real-signal fixture for a receive-path defect we had only ever seen in
simulation.

## What they turned out to be

**Tom was right that there are two modes.** Four files carry `bpsk1200`; `packet-24738.wav`
carries a slower one, and that file is the interesting one - see
[the qpsk600 burst](#packet-24738wav-a-qpsk600-burst-we-cannot-copy) below.

The `bpsk1200` traffic (IL2P+CRC, NinoTNC switch 1010) is one QSO, N2IRZ-2 to WA2M-2, carrying a
BPQChatServer-6.0.21.40 session. The one file with real quiet gaps shows the mode's main lobe at
844-2133 Hz, which is its 1500 Hz carrier plus and minus its 600 Hz half-baud.

`pdn-decode` over the five, default sweep: **7 real frames from 3 of the 5 files**.

| File | Frames | Notes |
|---|---|---|
| `packet-24140.wav` | 1 | RR nr=7 P/F, CRC clean, -4 Hz off centre |
| `packet-24141.wav` | 0 | noise-swamped |
| `packet-24444.wav` | 1 | RR nr=1 P/F, 1 byte FEC-corrected |
| `packet-24501.wav` | 5 | the clean one: three I frames and two RRs, 1-8 bytes corrected |
| `packet-24738.wav` | 0 | **a qpsk600 burst, below the receiver's floor - see below** |

`packet-24140.wav` also yields a 16-byte plain-IL2P reading from the coherent BPSK branch that is
not a frame anyone sent. It is left in the output on purpose: it carries `MONITOR ONLY` and no
callsign line, and it is the worked example of the tool's own honesty machinery on real data.

## packet-24738.wav: a qpsk600 burst we cannot copy

This one is not merely noisy. It contains a clean, well-formed burst that the shipping receiver
cannot read, and it is the best evidence we have for the QPSK receive chain being the next thing
to fix.

**Finding the burst needs the polarity the other files do not.** The file is loud almost
throughout and goes *quiet* from 3.06 to 5.31 s. That is the wrong way round for a squelched
recording and is the giveaway: this is an open-squelch FM receiver, so the hiss **drops** when a
carrier arrives (FM quieting). The transmission is in the quiet window, not the loud one.

**What is in it, measured:**

| Property | Measured | Means |
|---|---|---|
| Preamble, 3.03-3.63 s | two tones, **1350.6** and **1649.4 Hz**, 30 dB over the local floor | centre **1500.0 Hz**, shift **298.8 Hz** |
| Symbol rate | **300.00 Hz** (cyclostationary line of `abs(z)^2`) | 300 sym/s |
| Constellation order | `z^4` line at **0.00 Hz**, 20 dB above `z^2`, which has **no** DC line | 4th order, i.e. **QPSK** |
| Carrier offset | ~0 Hz | nothing for a diversity bank to find |
| SNR, 3 kHz-referenced | **+4.1 dB** preamble, **+1.7 dB** data | above the published bpsk300 AWGN threshold |
| Noise character | kurtosis 3.0, no samples beyond 4 sigma | ordinary Gaussian, **not** FM click noise |
| Levels | -7 dBFS peak, nothing clipped | not a recording fault |

300 sym/s on a 1500 Hz carrier with a 4th-order constellation is **`qpsk600`** (NinoTNC switch
1001, "600 QPSK IL2Pc, 300 sym/sec"). A `bpsk300` burst would be 300 sym/s on the same carrier
with the same preamble sidebands, which is why the preamble alone cannot separate them - the
power-law test can, and against both references degraded to the same SNR this burst looks *more*
4th-order than the `qpsk600` reference does.

**What was tried, and failed:** the full 46-entry sweep over the whole file; the burst isolated to
2.9-5.5 s so the surrounding full-scale hiss could not disturb the AGC or the deframer; a manual
frequency-diversity sweep at 17 offsets from -30 to +30 Hz; and the coherent detector on every PSK
mode. None of them copies it.

**Why, structurally.** `bpsk300` and `bpsk1200` run through `BpskMultiModem`, a nine-branch
frequency and detector diversity bank, and got the rebuilt differential receive chain of PR #236
(matched root-raised-cosine, corrected DPLL inertia, decision-feedback differential detection)
worth about 1.5 dB. `afsk300` runs an eleven-branch bank. **The entire QPSK family is a single
`QpskModem` with no bank and the old chain** - which the ledger entry for #236 says outright, in
its own list of what was left undone: "the obvious next legs are an on-air re-run of the
Flex-to-RSP1 AWGN waterfall and *the QPSK family, which still runs the old-style chain*."

Degrading the clean NinoTNC references to matched noise and counting frames shows the gap. The
absolute calibration here is rough and the numbers are only meaningful against each other:

```
  SNR(3k)  +8 dB : qpsk600 3/6    bpsk300 3/6
  SNR(3k)  +2 dB : qpsk600 3/6    bpsk300 3/6
  SNR(3k)   0 dB : qpsk600 2/6    bpsk300 3/6
  SNR(3k)  -2 dB : qpsk600 0/6    bpsk300 3/6
```

`qpsk600` falls off a cliff roughly 4 dB before `bpsk300` does. This burst sits in that gap: a
receiver with BPSK's sensitivity would very likely have copied it.

**So this file is a regression fixture with a known answer**: it contains a `qpsk600` frame,
nothing in the tree can read it today, and a QPSK chain that copies it is a chain that has earned
its keep. Tracked as **issue #326**.

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
