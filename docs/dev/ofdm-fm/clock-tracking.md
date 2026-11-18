# The clock-tracked read

Status: current as of 2026-11-18. Describes why OFDM-FM bursts were being lost on a real pair of sound cards, what was measured, and the receive path that came out of it. The code is `OfdmFmBurstCodec.ClockTracking.cs`; the instrument is `tools/Packet.SoundModem.OfdmFmLab` (`sm-ofdmlab`).

Measured 17 and 18 November 2026 on two Tait TM8110s with tait-cm108 boards (CM108B sound cards), one Raspberry Pi 4 each, 25 kHz channel, about -80 dBm at each receiver. The capture harness, every recording and the analysis are in `M0LTE/ofdm-fm-lab`; its `docs/findings.md` is the long form of this page.

## What was wrong

Of 1360 bursts recorded off air with the played samples kept, the receiver as it stood lost 431. Each loss was put in the first class whose remedy recovers it:

| Cause | Bursts | How it was established |
|---|---|---|
| The burst's sample rate was off | 153 | decodes once the burst's average rate offset is removed |
| The clock bent inside the burst | 101 | decodes only once the whole measured delay track is removed |
| Carriers of very different quality were weighted alike | 175 | still lost with an ideal clock; decodes with each carrier weighted by its measured noise |
| Out of margin | 2 | lost by everything; rate 7/8 at 9 dB reduced drive |

No burst was lost to a missed sync or an unread header.

**The clock.** The delay between the played samples and the capture was tracked through every burst, so the clock is known without the modem. Between two CM108B cards the rate inside one burst ranged from -278 to +312 ppm (mean +46, spread 57), a different figure for every burst, although the two hosts' USB clocks are within 5 ppm of each other. Inside a 10 s transmission the timing zig-zags by 20 to 60 microseconds, the rate stepping by 200 to 500 ppm for tens of milliseconds at a time, with occasional jumps of about a sample. Longer bursts hold more bends: median wander 10 microseconds under 0.7 s, 46 microseconds at 1.8 s.

A rate error tilts the phase across the band, which the pilot fits partly undo, and it also slides the signal through the transform window inside each symbol, which smears carriers into their neighbours and which nothing after the transform can undo. At 100 ppm that smear is about -24 dB at the top of the 8 kHz span, level with the path's own noise there.

**The weighting.** With an ideal clock the path measures 37 dB at 1.5 kHz and 23 dB at 7.5 kHz: 13 dB of slope where the channel's gain, which is what the soft bits were weighted by, falls 8 dB. Above 1.5 kHz at full drive the limit is receiver distortion, not noise (it repeats exactly between copies of a burst and falls when the drive falls). One of the two radios also has a spur at 3.66 kHz in its receive audio, 15 dB over its noise, on one or two carriers.

## What the read does

Only for a full burst on a profile that does not send follow-on frames. Nothing on the air changes.

1. **Each symbol's delay is measured from all of its carriers.** A delay of d samples turns carrier k by 2 pi k d / N through the origin (an FM audio path has no carrier offset), so there is one unknown per symbol. The header's carriers are known once its CRC has passed and give the starting rate. A payload carrier is known once it has been decided. The delay is searched for over two samples either side of the prediction before it is fitted: a clock that has just changed rate leaves the top carriers outside their decision regions, the wrong decisions read as small turns about the wrong points, and a fit from the prediction alone followed a third of the change.
2. **The audio is resampled along the delay curve**, from the estimate symbol to the end of the payload, and the channel estimate and every symbol are read again from the resampled audio.
3. **The curve is measured again at a quarter of a symbol.** Every carrier is decided by now, so the symbol's waveform is known, and over a short stretch a small delay is the part of the difference between received and expected that lies along the expected waveform's slope.
4. **Each carrier's soft bits are weighted by the noise it showed**: the scatter of its equalised points about the points they were decided to be, as a median over neighbouring carriers, with a carrier that stands well clear of its neighbours taken at its own figure.
5. **The channel estimate is refined** from the payload's own symbols.
6. **A read that fails its CRC is read once more against the decoder's own output**, re-encoded: fewer wrong points than slicing each carrier alone. Steps 3 to 5 are repeated from those points.
7. **If that fails too, the tracked symbols are decoded once more on channel-power weights.**

Each stage, added in turn, on a session recorded after the work was done (240 bursts a preset, drive from full to -9 dB):

| Receiver | `ofdm-fm-8k` 1024 B | `ofdm-fm-8k-r78` 1900 B |
|---|---|---|
| As it stood | 193 | 67 |
| + delay per symbol, resampled | 220 | 139 |
| + delay per quarter symbol | 219 | 149 |
| + measured-noise weights | 235 | 163 |
| + a carrier standing clear weighted on its own figure | 240 | 213 |
| + channel refined from the payload | 239 | 223 |
| + second pass and plain-weight retry | 240 | 238 |

## Two things that were measured out before, and are in now

**Per-carrier noise weighting** is recorded in [receiver-findings.md](receiver-findings.md) as built and rejected, a genie included. Both results stand, and the difference is what is measured. That attempt weighted by noise measured in the sync symbol's empty bins, before the payload: it sees neither the distortion that follows the signal nor what the clock leaves behind, and on the real path those two are most of what a carrier suffers. This one measures the payload's own scatter.

It has a blind spot, and the simulated link is what found it. A decision hides its own error: scatter against the nearest point cannot exceed the size of a decision cell, so a carrier 20 dB worse than its neighbours measures as only a little worse. On the bench the worst carrier has 23 dB and nothing saturates. On the simulated data-port link near the cliff the band edges do, and the first version decoded 12 of 24 bursts where channel-power weights decode 22. Two guards answer it: a carrier whose scatter is within sight of the cell's limit is held to what the channel-power model says of it, and step 7 above. `MeasuredWeightTests` pins the behaviour.

**A decision-directed second pass** is recorded there as winning 0 of 30, with the advice that a rebuild should re-encode the decoder's output and not slice raw. Step 6 is that. It is worth about 1 dB at each preset's cliff. A second and third repeat add nothing (60, 63 and 63 of 160 at the cliff).

## What it measured

Decoded, as it stood against the new read, same recordings:

| | As it stood | New |
|---|---|---|
| `ofdm-fm-8k` 1024 B, full drive and -3 dB | 422 of 440 | 440 of 440 |
| `ofdm-fm-8k-r78` 1900 B, full drive and -3 dB | 220 of 440 | 440 of 440 |
| `ofdm-fm-8k` 1024 B, -6 and -9 dB drive | 83 of 120 | 120 of 120 |
| `ofdm-fm-8k-r78` 1900 B, -6 and -9 dB drive | 14 of 120 | 118 of 120 |
| `ofdm-fm-8k` 3000 B | 26 of 40 | 40 of 40 |
| `ofdm-fm-8k-r56` 1900 B | 27 of 40 | 40 of 40 |
| `ofdm-fm-8k-r78` 3000 B | 18 of 40 | 40 of 40 |
| `ofdm-fm-6k-fast` 1024 B | 40 of 40 | 40 of 40 |
| `ofdm-fm-6k` 1024 B | 39 of 40 | 40 of 40 |
| `ofdm-fm-narrow` 512 B | 40 of 40 | 40 of 40 |
| Frames sent by the real modem, 20 to a transmission, `ofdm-fm-8k` | 119 of 120 | 120 of 120 |
| The same, `ofdm-fm-8k-r78` | 63 of 80 | 80 of 80 |

End to end through two running daemons, frames in one KISS port and counted byte-identical at the other, the two builds interleaved cell by cell (20 frames each way a cell, six cells a preset, each station given a short transmission to warm up first):

| | main | this branch |
|---|---|---|
| `ofdm-fm-8k` 1024 B, radio1 to radio2 | 91 of 120 | 120 of 120 |
| `ofdm-fm-8k` 1024 B, radio2 to radio1 | 116 of 120 | 120 of 120 |
| `ofdm-fm-8k-r78` 1900 B, radio1 to radio2 | 55 of 120 | 120 of 120 |
| `ofdm-fm-8k-r78` 1900 B, radio2 to radio1 | 108 of 120 | 120 of 120 |

Four of the six cells were run on the branch before its last three changes (the saturation guard, the plain-weight retry and the two-attempt limit) and two after; it delivered every frame in both. No capture overruns in any counted cell.

**Margin**, by adding each receiver's own measured noise to 160 recorded bursts a preset as more path loss would add it. Decoded of 160:

| Extra path loss | 0 | +3 | +6 | +8 | +10 | +12 | +14 | +16 dB |
|---|---|---|---|---|---|---|---|---|
| `ofdm-fm-8k` 1024 B, as it stood | 155 | 156 | 150 | 151 | 128 | 73 | 30 | 0 |
| `ofdm-fm-8k` 1024 B, new | 160 | 160 | 160 | 160 | 160 | 160 | 150 | 48 |
| `ofdm-fm-8k-r78` 1900 B, as it stood | 92 | 77 | 37 | 17 | 0 | 0 | 0 | 0 |
| `ofdm-fm-8k-r78` 1900 B, new | 160 | 160 | 159 | 153 | 68 | 0 | 0 | 0 |

About 5 dB more margin on the default preset (nine in ten still decoding at about +9 dB as it stood, +14 dB now), and about 8 dB on rate 7/8 where there was none. The payload gave out before the header in every case.

**The simulated data-port link** (4.5 kHz peak deviation, 15 kHz IF, 48 bursts a cell), which reaches the weak-signal regime the bench does not:

| CNR, dB | `ofdm-fm-8k` as it stood | new | `ofdm-fm-6k-fast` as it stood | new | `ofdm-fm-6k` as it stood | new |
|---|---|---|---|---|---|---|
| 32 | 46 | 48 | | | | |
| 30 | 40 | 47 | | | | |
| 28 | 17 | 45 | | | | |
| 26 | 0 | 7 | 48 | 48 | | |
| 24 | | | 45 | 48 | | |
| 22 | | | 5 | 45 | | |
| 16 | | | | | 48 | 48 |
| 14 | | | | | 47 | 48 |
| 12 | | | | | 2 | 17 |

**Clock tolerance** on synthetic bursts: the new read decodes at a constant 1000 ppm either way and through a rate that flips 300 ppm either side of 100 ppm every 60 ms. The reads as they stood are lost by 300 ppm. 520 ppm is the fixed offset of a CM108B whose "MIC PLL Adjust" EEPROM bit is clear, which until now stopped OFDM-FM outright; that case is covered by `ClockSkewTests` but has not been tried on such a board.

## What it costs, and two things it replaces

On a Raspberry Pi 4, per burst, the receiver as shipped against this one:

| | Air time | As shipped | This read |
|---|---|---|---|
| `ofdm-fm-8k` 1024 B, bursts as recorded | 0.48 s | 0.13 s | 0.15 s |
| `ofdm-fm-8k-r78` 1900 B, bursts as recorded | 0.57 s | 0.28 s | 0.18 s |
| `ofdm-fm-8k` 1024 B, noise added until nearly all fail | 0.48 s | 0.23 s | 0.28 s |
| `ofdm-fm-8k-r78` 1900 B, noise added until all fail | 0.57 s | 0.31 s | 0.39 s |

(The shipped receiver lost three quarters of the recorded rate 7/8 bursts and read each of those four times over, which is why it is the slower one on that row.)

Two changes keep a failing burst inside its own air time, because a receive thread that takes longer over a burst than the burst took to arrive falls behind its sound card and loses the bursts after it:

- **The pilot-fitted reads are not tried after a tracked read that failed.** Over the 1360 recorded bursts, over the same bursts with noise added until one in eight decoded, and over the simulated link, they decoded one burst the tracked read had lost. They remain the whole receiver wherever the tracked read is not taken.
- **A burst whose tracked read failed is committed twice, not four times.** The retry a sample further on exists because the pilot-fitted reads take their timing from where the sync committed; the tracked read measures its own. Each attempt is still one more draw at the cliff (42, 40, 35 and 33 of 320 decoded with four, three, two and one), and two is what fits.

## Not done

- **Follow-on frames.** `ofdm-fm-8k-follow` and `ofdm-fm-8k-adaptive` read a follow-on frame against a channel estimate inherited from the frame before, and the tracked read as built wants the estimate symbol in the audio it resamples. Those two presets are read as before.
- **Which card wanders.** Both do. An SDR listening off air shows the sending card's playback clock moving by tens of microseconds on its own, but the split was not pinned down, and it no longer decides anything here.
- **Weak signals on air.** Margin was measured by adding noise to recordings and through the simulated link, not by adding attenuation.
- **The coded FM ladder** (`OfdmFmFmLadderTests.The_Coded_Ladder_Still_Holds_Through_The_Fm_Link` with `OFDMFM_LADDER=1`) decodes nothing at any level on main before this change: it puts the 8 kHz layout through the microphone-and-speaker link profile, whose audio passband is a fraction of that span. It was no use as a cross-check and is left as found.
