# Answering a station on its own frequency

Most stations on an HF packet channel are not exactly on it. This is about measuring by how
much, and transmitting to suit.

## The measurement

The offset-diversity decoder banks (`bpsk300`, `afsk300-il2pc` and the other `-multi` modes)
already report which branch won a frame, and `FrameQuality.FrequencyOffsetHz` has always
carried it: "a persistent non-zero value means the far station is off-frequency by about that
much". `StationFrequencyOffsets` keeps a short per-callsign window of those numbers.

Measured on GB7RDG's 40 m port, 2026-08-13, over the whole frame log:

| Station | frames | mean | spread (sd) | min | max |
|---|---|---|---|---|---|
| GB7WEM-7 | 467 | -3.7 | **0.6** | -6.7 | +3.6 |
| GB7BEX-15 | 159 | -4.8 | 8.4 | -65.5 | +25.4 |
| EI0RSI-1 | 133 | +4.5 | 2.1 | -4.5 | +8.6 |
| GB7BPQ | 67 | -17.9 | 6.9 | -52.9 | +0.1 |
| GB7OXF-2 | 39 | -2.8 | **0.7** | -4.4 | -1.0 |
| PD4R-12 | 28 | -16.0 | 3.6 | -20.9 | -10.2 |
| GB7NOT | 24 | +8.3 | **12.4** | -13.7 | +40.6 |

The offset is a property of the rig, not of the path: GB7WEM-7 holds 0.6 Hz of spread across
467 frames, and different SSIDs of one station agree (GB7WEM -3.8 against GB7WEM-7 -3.7;
EI0RSI-1 +4.5 against EI0RSI-7 +4.9; GB7OXF -3.2 against GB7OXF-2 -2.8). GB7NOT does not sit
still, and is the shape the stability gate exists to exclude.

## Why transmitting to suit works

A rig's transmit and receive conversions run off one master oscillator. A station whose
reference is high by `d` emits `d` high **and listens `d` high**.

Writing each station's reference error as an offset from truth, station `i` transmitting on
nominal channel `f` with audio centre `c` emits at `f + e_i + c`. Station `j` receiving that
converts it to audio `c + e_i - e_j`, so what `j` measures is `e_i - e_j`.

For our signal to land on the audio centre `j`'s modem expects, we need
`c + e_us - e_them + adj = c`, so `adj = e_them - e_us` - which is exactly the offset we
measured on their frames.

**Our own reference error cancels.** It appears in the measurement and in the transmission with
the same sign, through the same oscillator, and drops out. A calibrated or GPS-locked reference
is not required for this to be correct; only one that does not move appreciably between hearing
them and answering them, which a TCXO does not.

## Why the benefit is theirs

This station finds them regardless: the 300 baud modes run offset-diversity banks, and the BPSK
carrier-offset estimator pulls in bursts from further still (a burst deliberately transmitted
300 Hz off still decoded on the nominal centre in `TransmitTrimTests`). The station that cannot
hear **us** is the one running a fixed-centre modem with no such bank, which is the common case
on the other end of an HF packet link. So the correction is transmit-only, and worth making
precisely because the capability is asymmetric.

It follows that the stations who would benefit most are the ones we can measure least: a station
far enough off to be undecodable never produces a frame to measure. This helps the moderately
off, not the lost.

## Why it stops when the other end does the same

If both ends correct, it does not run away, but it does not converge on the right answer either.
For a true difference `D` with each end applying a fraction `k` of what it measures, the pair
settles at `kD/(1+k)` and `-kD/(1+k)`: at `k = 0.5` and `D = 5 Hz`, both stations end up
transmitting 1.7 Hz off and each still hears the other 3.3 Hz out - worse than if only one had
corrected. Undamped (`k = 1`) it does not settle at all, oscillating with a period of two
exchanges as each end applies a correction, sees the offset vanish, and withdraws it.

Damping alone therefore is not enough, so the chase is detected:

> Our transmit trim cannot change what we measure of them. We measure their emissions, and our
> transmitter is not in that path. So a station's measured offset should sit still while we
> correct for it, however much we correct.

If it moves by more than `chaseThresholdHz` after we begin correcting, the movement is theirs.

**Backing off is not giving up.** A station that moves once has most likely just moved: a
knocked dial, a rig warming up, a new radio. It will sit perfectly happily at its new offset,
and writing it off forever would mean never correcting for it again because of something it did
one Tuesday. So a move costs a cooldown (`chaseCooldownSeconds`, 30 minutes by default), after
which its new offset is measured and corrected for like anybody else's.

What separates that from a real chase is repetition. A rig that was moved stays put afterwards;
a peer running this same algorithm moves again *every time we correct*. Only after `maxChases`
of those does the correction stop for good, and the log says which of the two it concluded.

**None of that is what bounds the damage.** Every trim is clamped to `maxTrimHz`, so even two
stations chasing each other with the detector disabled entirely could not walk further than 50 Hz
off the channel centre - less than several of the stations in the table above are off it already.
That cap, not the detector, is why this is safe to have on by default.

## Configuration

```json
"frequencyMatching": {
  "enabled": true,
  "minSamples": 3,
  "maxSpreadHz": 20,
  "maxTrimHz": 50,
  "damping": 0.5,
  "chaseThresholdHz": 10,
  "chaseCooldownSeconds": 1800,
  "maxChases": 3
}
```

These are the defaults, and the section may be omitted entirely for them. Measurement runs
always; `enabled` governs only whether the transmitter moves.

`minSamples` is deliberately low. The correction only has to hold for the exchange it is used
in, so a handful of recent frames is the right evidence; a long run would average across drift
and describe neither end of it. `maxSpreadHz` is what separates a rig that is merely off
frequency from one that is wandering: on the table above it admits GB7WEM-7 and GB7OXF-2 and
excludes GB7NOT.

Beacons and IDs are never trimmed. Aiming at one correspondent's oscillator aims away from every
other listener, and a broadcast has no one correspondent. In practice those destinations exclude
themselves - no frames are ever received *from* `BEACON`, so no estimate for it can exist - but
the exclusion is stated rather than left incidental.

## Where it is implemented

The shift is applied to the rendered burst in `SoundModemChannel`, not inside a modem. The AFSK
and PSK families carry a settable centre natively and generate their carrier at it, so they are
never wrapped in `FrequencyShiftedModem` and there is no shift stage there to lean on - and
those are exactly the modes talking to the stations this is for. Translating the finished burst
costs one Hilbert pass per transmission and works for every mode on one code path.

## Seeing it happen

A shifted transmission is marked wherever frames are listed, because a signal that is not where
the band plan says it should be is otherwise indistinguishable from a fault - and because the
only way to find out whether this helps is to be able to point at the frames it applied to.

- **Waterfall panel**: a `SHIFTED` badge beside the callsigns, and `shifted +4.5 Hz` in the
  detail line. Outlined rather than filled, so it reads as an annotation on our own frame rather
  than another station's badge. The tooltip explains the reasoning.
- **Journal**: `tx[2] bpsk300 GB7RDG-2>EI0RSI-1 37 bytes  shifted +2.3 Hz to suit them`.
- **Frame log**: a `tx_trim_hz` column, added by the same migration path as the others, so
  existing logs pick it up without losing their history.

It is deliberately **not** written into `offset_hz`. That column holds a measurement of somebody
else's transmitter; this is a command to our own, known exactly rather than estimated. Averaging
the two together would mix what a station did with what we did about it, and the question this
feature will eventually be judged on - did correcting for them improve their decode rate - is
exactly the query that mixing would ruin.

## Not proven on air

All of this is measured against the frame log and tested offline. Nobody has yet confirmed that
a correspondent's decode rate improves when we correct for it, which is the only claim that
matters and the only one that needs a cooperative station at the other end.
