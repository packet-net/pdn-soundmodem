# Designing the maximum-performance preset for the radio1/radio2 rig

**Status: measured and decided 2026-09-19. The preset is section 6, and it ships as `ofdm-fm-8k`.**

The objective set for the day: put the carrier layout in the header (done, [geometry-signalling.md](geometry-signalling.md) sections 13 and 14), re-characterise the audio path through this particular pair of TM8110s, CM108s and taps (done, section 15 there), then design the waveform preset that gets the most out of them. This document is the third part. It is written for somebody who has not seen the rest of the day, and it says where every number came from.

**One thing to hold while reading the sweeps.** Sections 1 to 4.5 were measured with a 128-sample cyclic prefix; section 4.6 halves it to 64 and everything from there on, including every shipped preset, runs at 64. The shorter prefix is 3 % faster for the same arithmetic, so a figure from the earlier sections is about 3 % below what the same configuration does today. The symbol is 44.0 ms at the shipped prefix, 22.727 symbols a second.

## 1. What "maximum" is limited by on this rig

Three things were expected to limit a wide, dense preset, and the measurements put a number on each. They are not what the previous campaign assumed.

**Not the discriminator noise tilt.** The 18th's design reasoning was that FM discriminator noise rises 6 dB per octave, so the top of a wide span is the noisiest and a dense constellation cannot live there. At this rig's signal level (60 to 90 dB over the receivers' RSSI floors) the floor under a carrier FALLS with frequency, at the same rate the passband rolls off, and the per-carrier signal-to-noise shape from the tone sweep is within 8 dB of the best carrier from 500 Hz to 9 kHz. The per-bin noise floor and per-carrier shape measurements are in [geometry-signalling.md](geometry-signalling.md) section 15; the raw CSVs are campaign evidence and are not in this repository.

**The transmitter's own peak reducer, at the constellation's default limit.** Per-carrier SNR on a noiseless loopback, which is the floor no channel can beat:

| crest-factor limit | which constellation defaults to it | per-carrier SNR, noiseless |
|---|---|---|
| 5 dB | BPSK, QPSK | 12.7 dB |
| 8 dB | QAM-16 | 23 to 24 dB |
| 10 dB | QAM-64, QAM-256 | 33 to 40 dB |
| 12 dB | none | 50 dB and up, in effect unclipped |

So a QPSK burst's per-carrier reading is the clipper and says nothing about the channel, and a QAM-16 burst at its default limit is within a decibel or two of the channel on this rig. Every sounding below therefore sets the limit explicitly to 10 dB, which is what a QAM-64 preset uses. Peak deviation follows the limit: 4.8 kHz, 96 % of the 5 kHz class, at 10 dB and full drive (measured on the 18th at QAM-64), so 12 dB would need the drive 2 dB lower.

**Burst length, through the sample clocks.** The two CM108s are 5.6 ppm apart and wander by 1 to 2 ppm between runs. The codec fits one linear tilt per burst and corrects it, and on an 8 kHz span at 1024-byte QPSK rate-1/2 bursts (26 payload symbols) that correction is worth 3 dB (the same capture read 20.1 dB with the tilt fit and 16.9 without). What it leaves behind still costs: the same 1024 bytes on the 6 kHz span read 28.1 dB at QAM-64 rate 2/3 (9 payload symbols) and 23.8 dB at QPSK rate 1/2 (34 symbols), same span, same clip limit, same hour. **A robust rate on a wide span pays 4 dB for being four times longer**, and the first sounding, taken at QPSK, was misleading for exactly that reason. The presets below are dense, so their bursts are short, and the loss does not apply to them; it is recorded here because the rate ladder's lower rungs on a wide span will meet it.

## 2. What a wide span actually delivers at QAM-64

Second sounding, 12 bursts of 1024 bytes a cell, QAM-64 rate 2/3, clip limit 10 dB, radio1 transmitting and radio2 receiving, both stations settled, the 6 kHz preset measured in the same session as the control. Per-carrier SNR from the error vectors of decoded bursts, mean over the layout:

| candidate | span | delivered | per-carrier mean |
|---|---|---|---|
| 6 kHz (control) | 211 Hz to 6.0 kHz, 248 carriers | 12 of 12 | 28.1 dB |
| w8h64 | 211 Hz to 8.0 kHz, 333 carriers | 12 of 12 | 24.5 dB |
| w9h64 | 211 Hz to 9.0 kHz, 376 carriers | 12 of 12 | 24.1 dB |
| w9h64, drive -3 dB | | 12 of 12 | 23.4 dB |
| w9h64, drive -6 dB | | 12 of 12 | 23.1 dB |
| w10h64 | 211 Hz to 10.0 kHz, 419 carriers | 10 of 12 | 22.7 dB |

Two things to read off that. **Six decibels less drive costs one decibel of SNR**, so the wide span's impairment is proportional to the signal, not to the noise floor: it is distortion, the receiver-side kind the tone sweep's harmonics pointed at, and it is what the 3 to 4 dB gap between the 6 and 9 kHz spans is made of once the 1.8 dB of power split over more carriers is taken out. It also means the drive is not a lever here, and can be backed off for adjacent-channel margin at almost no cost. And **10 kHz is past the edge**: the top carriers are 13 dB down in the passband and QAM-64 fails there.

Where the loss sits across the band, the 9 kHz span against the 6 kHz one, 500 Hz bands, radio2 receiving:

| band, kHz | 0 | 0.5 | 1 | 1.5 | 2 | 2.5 | 3 | 3.5 | 4 | 4.5 | 5 | 5.5 | 6 | 6.5 | 7 | 7.5 | 8 | 8.5 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 6 kHz | 24.8 | 29.0 | 29.6 | 31.2 | 30.8 | 30.7 | 28.7 | 29.0 | 26.6 | 26.7 | 25.2 | 24.3 | | | | | | |
| w9h64 | 20.7 | 24.9 | 25.1 | 26.2 | 26.0 | 26.7 | 26.2 | 26.7 | 25.6 | 25.9 | 25.6 | 24.5 | 24.0 | 22.9 | 22.5 | 21.5 | 20.3 | 18.4 |

The interior of the wide span is flat at 25 to 27 dB, which is QAM-64 with 3 to 5 dB in hand against the 22 dB it needs at rate 2/3. From 6 kHz the figure declines about 1 dB per 500 Hz to 18 dB at 8.5 kHz, which is QAM-16 territory, and the bottom 300 Hz is the same. The single carrier at 9.0 kHz reads 6 dB and carries nothing useful.

**The other direction is 2 dB worse everywhere.** Same cells, radio2 transmitting, radio1 receiving (radio1 is the station on the antenna, and its idle floor is 31 dB above radio2's):

| candidate | delivered | per-carrier mean, radio1 receiving | radio2 receiving |
|---|---|---|---|
| 6 kHz | 12 of 12 | 26.5 dB | 28.1 dB |
| w8h64 | 12 of 12 | 23.1 dB | 24.5 dB |
| w9h64 | 12 of 12, with sync retries | 21.5 dB | 24.1 dB |
| w9h64, drive -6 dB | 9 of 12 | 21.7 dB | 23.1 dB |
| w10h64 | 12 of 12 | 21.1 dB | 22.7 dB |

At radio1 the interior of the 9 kHz span sits at 22.6 to 24.3 dB, on the QAM-64 line rather than above it, and uniform QAM-64 there decoded every frame only after retries. The drive ladder also reads differently at this end: -6 dB cost 9 of 12 frames, because radio1's floor is high enough that additive noise does matter there. A preset has to work in both directions, so the design is taken from the worse receiver, carrier by carrier.

## 3. The design

A bit-loading designer takes the two directions' per-carrier means, keeps the lower, smooths over five carriers so a single spur bin does not make a tier of its own, quantises against approximate needs (28 dB for QAM-256, 22 for QAM-64, 16 for QAM-16, 10 for QPSK) plus a 1 dB margin, and run-length encodes the result into contiguous tiers, absorbing runs shorter than four carriers into the lower neighbour.

On the 9 kHz span that gives, from 211 Hz upward: 21 carriers at 4 bits, 77 at 6, 7 at 4, 10 at 6, 11 at 4, 64 at 6, 157 at 4, 17 at 2. The two short 4-bit runs inside the interior are real: a dip at 2.5 to 3 kHz seen by radio1 and the 4055 Hz spur seen by radio2. The 157-carrier run at 4 bits is everything from about 5 kHz up, and the 17 at 2 bits are the top 400 Hz.

What that is worth, per payload symbol, against the alternatives it competes with:

| candidate | layout | constellation, code | payload bits per symbol | against the 6 kHz preset |
|---|---|---|---|---|
| 6 kHz | 6 kHz | QAM-64 2/3 | 960 | control |
| x6r34 | 6 kHz | QAM-64 3/4 | 1080 | +12 % |
| x6bl | 6 kHz bit loaded | QAM-256 on 115 carriers, QAM-64 on 125, 2/3 | 1113 | +16 % |
| w8h64 | 8 kHz | QAM-64 2/3 | 1292 | +35 % |
| w8h64r34 | 8 kHz | QAM-64 3/4 | 1454 | +51 % |
| w9h64 | 9 kHz | QAM-64 2/3 | 1456 | +52 % |
| w9bl | 9 kHz bit loaded | the tiers above, 2/3 | 1149 | +20 % |
| w9bl34 | 9 kHz bit loaded | the tiers above, 3/4 | 1293 | +35 % |

Two things temper those percentages. **Whole-symbol rounding**: a 1024-byte frame is 8208 coded bits after framing, and every candidate here needs between 6 and 9 payload symbols for it, so a 20 % gain in bits per symbol can be a 0 % gain in symbols per frame. **The estimate symbol**: on the bench's table every payload off the acquisition layout paid one, the 6 kHz preset included, because that table acquired on a narrower layout than any of these. Both are why the comparison is done as measured goodput on air and not as arithmetic.

The bit-loaded design is the conservative candidate: it puts QAM-64 only where the worse receiver has a decibel in hand. The uniform QAM-64 spans are the aggressive ones: w8h64 decoded cleanly both ways in the sounding, w9h64 needed retries at radio1. Rate 3/4 on any of them spends about 1.5 dB of whatever margin is left.

## 4. On air: goodput against the 6 kHz preset

### 4.1 The first run measured two defects instead of the presets

Twenty frames back to back per cell, at 1024 and 1900 bytes, both directions, the receiver on its persistent 6 kHz preset and the transmitter set per cell. The client reported the 6 kHz preset delivering 20 of 20 at 1024 bytes and 4 of 20 at 1900, and every 9 kHz candidate delivering 2 to 6 of 20 at either length. Neither figure was the modem's.

**The client's KISS parser dropped every frame too big for one TCP segment.** The bulk sender parsed its stream up to the last delimiter and kept the tail, which discarded the "inside a frame" state with the parsed chunk, so a frame whose opening delimiter ended one read was skipped as noise on the next. The receiver's own frame log showed 19 of 20 and 20 of 20 of those 1900-byte frames decoded. A stateful parser replaced it, and the run is scored from the two stations' frame logs below, which see what the modems did.

**The receiver retried a failed payload once per sample of its sync plateau.** The streaming receiver resumes a sample on after a payload fails, up to `MaxSyncAttemptsPerRun` per plateau, and the budget was reset by the code path that commits, so the cap counted to one. Read offline from the receiver's capture, the 6 kHz 1900-byte cell had 301 decode attempts for 18 decoded bursts; one burst that would not decode was decoded 140 times over. On a Pi 4 that is about a second of the receive thread, and in a back-to-back run the bursts behind it go by unheard. That is why the 9 kHz candidates, which are marginal at radio1 and so fail a first attempt often, delivered 12 of 12 one frame at a time in the sounding and 2 of 20 back to back here, and it is very probably the mechanism behind the "losses under load" of [missing-frames-under-load.md](missing-frames-under-load.md). The budget now resets only when the correlation breaks while nothing is being tracked; the same capture reads 25 attempts for 17 decoded.

Scored from the frame logs, before either fix was deployed, delivered frames of 20 and the rate from the transmitter's first frame to the receiver's last:

| candidate | 1024 B, 1 to 2 | 1024 B, 2 to 1 | 1900 B, 1 to 2 | 1900 B, 2 to 1 |
|---|---|---|---|---|
| 6 kHz | 20, 12.7 kbit/s | 20, 12.6 | 19, 14.4 | 5 |
| x6r34 | 20, 13.9 | 20, 13.8 | 20, 16.6 | 18, 16.6 |
| x6bl | 20, 13.9 | 7 | 5 | 9 |
| w8h64 | 20, 14.6 | 20, 14.6 | 17, 19.8 | 1 |
| w8h64r34 | 5 | 20, 16.4 | 3 | 12, 21.3 |
| w9h64 | 2 | 2 | 3 | 3 |
| w9bl | 5 | 2 | 4 | 6 |
| w9bl34 | 6 | 6 | 2 | 1 |

Two things survive even this run: the 6 kHz span at rate 3/4 beat it at rate 2/3 in every cell, and the 8 kHz span at rate 2/3 delivered every 1024-byte frame both ways at 15 % over the 6 kHz preset.

### 4.2 The run repeated with both fixes deployed

Same protocol, the retry cap in the receiver and the stateful parser in the client, scored from the frame logs. Frames delivered of 20, and the rate from the transmitter's first frame to the receiver's last in kbit/s:

| candidate | 1024 B, 1 to 2 | 1024 B, 2 to 1 | 1900 B, 1 to 2 | 1900 B, 2 to 1 |
|---|---|---|---|---|
| 6 kHz (control) | 20, 12.65 | 20, 12.64 | 20, 15.94 | 18, 14.49 |
| x6r34 | 20, 13.87 | 20, 13.86 | 20, 16.57 | 18, 14.91 |
| x6bl | 20, 13.88 | 20, 13.79 | 17, 15.81 | 17, 14.92 |
| **w8h64** | **20, 14.68** | **20, 14.68** | **18, 17.74** | **19, 18.69** |
| w8h64r34 | 18, 14.76 | 20, 16.41 | 14, 14.35 | 20, 20.59 |
| w9h64 | 16, 13.91 | 19, 15.56 | 10, 10.84 | 15, 15.46 |
| w9bl | 17, 11.76 | 20, 13.96 | 14, 12.35 | 15, 13.21 |
| w9bl34 | 16, 11.76 | 18, 13.17 | 10, 9.85 | 7, 7.71 |

The 6 kHz figures repeat the first run's to a tenth, which is the measurement's own reproducibility across an hour and a daemon restart.

**The 8 kHz span at uniform QAM-64 rate 2/3 is the preset.** Every 1024-byte frame delivered both ways at 14.7 kbit/s, 16 % over the 6 kHz preset, and 18 to 19 of 20 at 1900 bytes at 17.7 to 18.7 kbit/s, 15 to 25 % over it at the same length. Rate 3/4 on the same span reaches 20.6 kbit/s at 1900 bytes in the direction that can take it and drops a third of the frames in the other; it is the choice for a link that has measured itself and found the margin, which is what the rate controller is for, and not the default. Everything at 9 kHz loses frames at either length however it is loaded: past 8 kHz the top carriers cost more in retries and lost bursts than they pay in bits, and the bit-loaded design's 20 % more bits per symbol never shows through the estimate symbol and the whole-symbol rounding. The 6 kHz span at rate 3/4 is the safe 10 %.

So: the 8 kHz layout, 211 Hz to 7992 Hz, 323 data and 10 pilot carriers, QAM-64, K=7 rate 2/3, crest factor limit 10 dB, full drive. Peak deviation at that setting is the 4.8 kHz measured on the 18th for QAM-64, 96 % of the class.

### 4.3 The fixed symbols: no lead-in between frames of one keyup

Of the twelve symbols a 1024-byte burst on the 8 kHz preset takes, five carry no payload: the lead-in, the sync symbol, the preamble, the header and the estimate symbol. The lead-in was there on every frame because the host asks for 30 ms before each frame after the first in a keyup and the modem rounded that up to a whole silent symbol. The receiver does not need it: it resumes its search at the end of the burst it just read, which is exactly where the next sync symbol starts. `ContiguousBursts` on a profile renders no lead-in inside a keyup and leaves the first frame's alone, since that one is the radio's keying time. Measured in the same session against the 6 kHz preset and the 8 kHz preset as it stood, with two 8.5 kHz spans for the width question at the same time:

| candidate | 1024 B, 1 to 2 | 1024 B, 2 to 1 | 1900 B, 1 to 2 | 1900 B, 2 to 1 |
|---|---|---|---|---|
| 6 kHz | 20, 12.63 | 20, 12.73 | 20, 15.89 | 20, 16.05 |
| w8h64 | 20, 14.63 | 20, 14.64 | 18, 17.61 | 17, 16.81 |
| **w8h64z**, contiguous | **20, 16.33** | **20, 16.29** | **19, 19.29** | **17, 17.28** |
| w85h64z, 8.5 kHz uniform, contiguous | 20, 16.45 | 19, 15.52 | 16, 16.45 | 12, 12.34 |
| w85tz, 8.5 kHz with its top 500 Hz at QAM-16, contiguous | 20, 16.36 | 20, 16.31 | 15, 16.25 | 14, 14.42 |

**Twelve per cent at 1024 bytes for nothing**, both directions, every frame delivered; at 1900 bytes 19.3 and 17.3 kbit/s. The 6 kHz control repeats the earlier runs to a tenth once more. The 8.5 kHz spans are the width question answered again: the same rate as the 8 kHz preset at 1024 bytes and fewer frames delivered at 1900, whether the extra carriers are loaded down or not. Eight kilohertz is the top of this rig.

What is left in the fixed symbols is four: sync, preamble, header, estimate. The estimate is the price of a narrow acquisition layout (section 5) and the other three are the waveform.

### 4.4 Long frames, and the clock through a long burst

Frame length is the other lever on the fixed cost, and it was capped at 2048 bytes by the daemon's KISS layer, silently (packet-net/pdn-soundmodem#503, fixed in 0.72.0, which passes 8192 and says so in the journal when it drops one) and at 2048 by this modem, a number from the days of a 16-bit length field. Both raised, 3000 and 4000-byte frames reached the modem and about half of them failed, back to back, on a span where every 1024-byte frame decoded. Read offline from the captures, the survivors sat 3 to 5 dB below the short bursts with pre-FEC error rates of 1.5 to 4.6 per cent, and switching the burst-wide clock-tilt correction off made them worse: the residual was a tilt that one straight line per burst cannot follow over thirty symbols.

The receiver now reads each payload symbol's own tilt from its pilots, on top of the burst-wide fit: the same regression, over one symbol's ten to twelve pilots, smoothed over five symbols and shrunk by its own variance, tried first with the CRC arbitrating as before. On the captured 4000-byte cell that took 7 decoded of 20 to 17, and pre-FEC rates to mostly under 0.6 per cent. On air, frames of 20 and kbit/s from the frame logs, before and after:

| frame | into radio2, before | after | into radio1, before | after |
|---|---|---|---|---|
| 1024 B | 18, 14.62 | 18, 14.71 | 20, 16.20 | 20, 16.25 |
| 1900 B | 15, 15.43 | 17, 17.26 | 20, 20.44 | 20, 20.49 |
| 3000 B | 11, 12.66 | 16, 18.35 | 11, 12.64 | **19, 21.83** |
| 4000 B | 10, 12.27 | 12, 14.65 | 7, 10.85 | 16, 19.55 |
| 6 kHz 1024 B, control | 20, 12.72 | 20, 12.63 | 20, 12.67 | 20, 12.63 |

Into radio1, 3000-byte frames deliver 19 of 20 at 21.8 kbit/s, which is the best figure this rig has produced so far, and 4000-byte ones 16 of 20 at 19.6. Into radio2 the whole evening was weaker than the morning (18 of 20 even at 1024 bytes, where the afternoon gave 20), so the two directions should not be read as the same channel; the gain from the tracker shows in both.

**So the frame to use on this preset is 1900 to 3000 bytes**: 20 of 20 at 20.5 kbit/s and 19 of 20 at 21.8 on the better direction. 4000 bytes buys nothing more and loses frames. And the arithmetic that promised 24 kbit/s at 4000 bytes was right about the burst and wrong about the link: a longer burst is more of the channel's time in one basket, and on this rig a basket of thirty symbols is where the clocks start to cost.

### 4.5 Follow-on frames, and what the sound card does between frames

The four fixed symbols left after 4.3 are sync, preamble, header and estimate, and every frame of a keyup paid them to re-acquire what the receiver had a frame earlier. `FollowOnFrames` on a profile sends a frame that follows another in the same keyup, on the same geometry, as its header and payload alone: eight symbols instead of eleven at 1024 bytes, 38 % more on the wire, 23 % at 1900 bytes and 15 % at 3000. The receiver, having decoded a burst, expects the next header where that burst ended and reads it against the acquisition channel it refreshes from every header once the CRC has made the bits known; the payload is read against a channel refreshed from the previous frame's last four decoded symbols, every carrier of which is a known point. A follow-on header that does not read ends the keyup for the receiver, because there is no sync symbol to find the next frame by; that is the trade, and why it is a choice per profile. Receivers read follow-on bursts whether or not they send them, and the first frame of a keyup, or the first after a change of geometry, is always a full burst.

**The first run on air delivered one frame in twenty, in every cell, both directions**, from a build that passed every synthetic test. The receiver's own capture said why: between every burst of a keyup and the next there were 30 to 35 ms of silence, carrier up, and the follow-on bursts were there, eight symbols long with the louder header symbol at the front, each one starting a period after the position the receiver was reading it at. pdn-soundmodem drains the sound card after every frame, which stops and re-arms the PCM, pads the last partial period with silence and only then modulates the next frame; on synthetic audio the bursts abut to the sample (packet-net/pdn-soundmodem#507 makes the daemon drain once per keyup, after the tail).

Two fixes, because a receiver that needs sample-exact continuity from a sound card is fragile whatever the daemon does. The receiver now looks for a follow-on burst that is not where it is expected within two symbols after, by the self-similarity of its symbols' cyclic prefixes, coarsely on band-limited audio and sharpened on the raw audio, and offers the strongest few starts to the header's CRC best first. A found header is read on the grid the keyup's estimates were made on, not at the boundary the prefixes place, because the sync search commits up to a prefix before the true boundary (about 40 samples on this profile) and the payload estimate is only good to the sample; the keyup state carries that alignment from every decoded burst's own prefixes. A header found with a sync symbol two symbols before it is a full burst's and is left to the sync hunt. Replayed against the capture, that took the receiver from 3 of the 60 frames to 27.

**The other 33 were the sound card's clock.** The follow-on bursts that did decode read 6 dB under the first burst of their keyup, and the diagnostics said why: within every one of them the pilots' timing curved over the burst by about 0.15 samples and the burst-wide drift fit came out at two to three times the steady 51 ppm the first burst showed, from 83 to 150 ppm. A USB sound card's clock takes a few hundred milliseconds to settle after its stream is restarted, and the daemon restarted it before every frame; the first frame of a keyup escaped because 300 ms of lead-in played before its sync symbol. **Every full burst after the first in a contiguous keyup showed the same thing**, drift from 28 to 100 ppm from burst to burst where the first burst read 51, and the same curvature, absorbed by the estimate symbol one symbol before the payload and by the per-symbol tracker. That is the most likely cause of the back-to-back losses in 4.3, of the long-frame losses in 4.4, whose thirty symbols see far more of the settling than seven do, and of the weaker radio1 to radio2 direction.

With the daemon draining once per keyup, the first sweep found two more things before the numbers could be believed. **The preset itself lost exactly four frames in twenty in every cell.** The sync search allows a run of correlated samples four commits and then refuses more until the correlation breaks, so that an unmodulated carrier cannot put a header read on the receive thread at every sample; with the frames now truly back to back the hunt resumes exactly at the next sync symbol, the correlation never breaks, and the fifth burst of every five was refused. The 30 ms gaps had been resetting that budget all along. A burst that copied now resets it. And the follow-on bursts into radio2 alternated between 25 dB and 18 dB from one burst to the next: the channel estimate a follow-on burst inherits was the raw bins of the previous burst's last four symbols averaged, and on the radio1 to radio2 path the relative clock wanders by up to 0.4 samples inside a burst, which smears that average. It is now averaged coherently, each symbol rotated by the correction that decoded it, and referenced to the last symbol's timing.

Measured in the same way with both fixes, the frame logs scoring, frames of 20 and kbit/s, with the preset at rate 3/4 in both forms alongside because a link this rich should be asked:

| profile | 1024 B, 1 to 2 | 1900 B, 1 to 2 | 3000 B, 1 to 2 | 1024 B, 2 to 1 | 1900 B, 2 to 1 | 3000 B, 2 to 1 |
|---|---|---|---|---|---|---|
| 6 kHz, control | 20, 13.32 | 20, 16.59 | 20, 17.81 | 20, 13.44 | 20, 16.62 | 20, 17.81 |
| w8h64z, full bursts | 20, 16.90 | 20, 21.70 | 20, 23.87 | 20, 16.99 | 20, 21.65 | 17, 20.32 |
| **w8h64f**, follow-on | **20, 23.16** | **20, 26.57** | **20, 27.48** | **20, 23.12** | 16, 25.22 | 10, 19.99 |
| w8h64z34, full bursts, rate 3/4 | 20, 18.48 | 19, 21.96 | 20, 26.16 | 20, 18.61 | 20, 23.22 | 20, 26.14 |
| w8h64f34, follow-on, rate 3/4 | 20, 26.33 | 20, 28.88 | **20, 30.41** | 13, 26.49 | 17, 24.55 | 10, 28.77 |

Three things to read off it. **Follow-on frames deliver what the arithmetic promised**: at 1024 bytes 23.2 kbit/s both ways with every frame, 43 % over the full-burst preset's 16.2 of the afternoon and 37 % over its 16.9 tonight, and into radio2 26.6 and 27.5 kbit/s at 1900 and 3000 bytes with every frame. **Rate 3/4 is now a rate this rig delivers**: as full bursts 20 of 20 in five cells of six, 26.2 kbit/s at 3000 bytes both ways, where in the afternoon it delivered in one direction only, on the settling clock; and as follow-on bursts into radio2, 30.4 kbit/s at 3000 bytes with every frame, the best figure this rig has produced, about 70 % of the raw modulation rate. **And the 6 kHz control moved for the first time all day**, 12.6 to 13.3 kbit/s at 1024 bytes, which is the 30 ms of silence per frame the daemon no longer inserts, and nothing else.

The fourth thing is the edge. Into radio1, this time, the long follow-on keyups lost frames, 16 and 10 of 20 at 1900 and 3000 bytes at rate 2/3 and 13, 17 and 10 at rate 3/4, where an hour earlier that direction delivered every follow-on frame at every length and the other one lost them. The full-burst forms on the same link at the same time lost at most three. A follow-on burst is read against an estimate inherited from the burst before, and on a link whose clock is wobbling within a burst that inheritance is worth less than a fresh estimate symbol; which direction wobbles moves with time. So the follow-on preset is the fastest thing here and the least forgiving, and the choice between it and full bursts at rate 3/4 is one the rate controller should make per link from what it hears, not one to fix in a file.

What remains on the radio1 to radio2 path at 1900 and 3000 bytes is a loss the pilots do not see: in a bad follow-on burst the first symbols after the frame boundary carry several per cent of errors across the whole band while the pilots' timing is flat to a tenth of a sample, and the next burst is clean. That is consistent with the transmitting sound card's adaptive clock wobbling at each write, within a symbol, which smears a transform rather than tilting it, and the dense constellation pays where BPSK pilots do not. It does not appear on the radio2 to radio1 path, where the follow-on preset delivered every frame at every length. Recorded rather than chased tonight.

### 4.6 The cyclic prefix

Every symbol carried a 128-sample prefix, 2.7 ms, against a channel whose dispersion is a radio's audio filters, a few tens of microseconds. Halved to 64, the symbol is 3 % shorter and everything else is unchanged, so the question was whether anything on this rig needs the other 64 samples. Measured at 22:11 UTC with every profile in the station table at the halved prefix (the prefix is a table property, so all profiles change together), against the same cells at 128 three hours earlier, both frame logs scoring, frames of 20 and kbit/s:

| profile | 1024 B, 1 to 2 | 1900 B, 1 to 2 | 3000 B, 1 to 2 | 1024 B, 2 to 1 | 1900 B, 2 to 1 | 3000 B, 2 to 1 |
|---|---|---|---|---|---|---|
| w8h64z, prefix 128 | 20, 16.90 | 20, 21.70 | 20, 23.87 | 20, 16.99 | 20, 21.65 | 17, 20.32 |
| w8h64z, prefix 64 | 20, 17.34 | 20, 22.23 | 19, 23.37 | 20, 17.51 | 20, 22.26 | 19, 24.73 |
| w8h64f, prefix 128 | 20, 23.16 | 20, 26.57 | 20, 27.48 | 20, 23.12 | 16, 25.22 | 10, 19.99 |
| w8h64f, prefix 64 | 20, 23.84 | 17, 23.17 | 20, 28.34 | 20, 23.94 | 16, 21.84 | 18, 26.78 |

Where every frame delivers, the prefix pays its 3 % to the tenth of a kilobit; where frames are lost, they are the long follow-on keyups that lose frames at either prefix, on whichever direction is wobbling. The check that matters is whether the shorter prefix lets one symbol's tail into the next, and the full bursts' own error vectors say it does not: replayed through the same decoder, the 1024-byte full-burst cells read a pre-FEC error rate of 0.0020 at both prefixes into radio2 and 0.0040 against 0.0034 into radio1, a mean per-carrier SNR of 25.5 against 25.0 dB and 23.9 against 24.1, and the same worst carrier. **Every shipped preset carries the 64-sample prefix**, and everything from here on is measured at it.

### 4.7 Rates 5/6 and 7/8

Rate 2/3 spends a third of the raw rate on the code, on a link that measures 24 to 28 dB per carrier. Punctured rates 5/6 and 7/8 on the same K=7 code (two coding ids, appended) were measured at 22:35 UTC on the 64-sample prefix, in both burst forms, with the follow-on rate-3/4 preset as the reference in the same session, both frame logs scoring, frames of 20 and kbit/s:

| profile | 1024 B, 1 to 2 | 1900 B, 1 to 2 | 3000 B, 1 to 2 | 1024 B, 2 to 1 | 1900 B, 2 to 1 | 3000 B, 2 to 1 |
|---|---|---|---|---|---|---|
| w8h64f34, follow-on, rate 3/4 | 20, 26.94 | 20, 29.54 | 17, 26.77 | 20, 27.00 | 20, 29.65 | 8, 32.74 |
| **w8h64z56**, full bursts, rate 5/6 | 20, 19.06 | 20, 25.45 | 19, 28.23 | 20, 19.22 | 19, 24.18 | **20, 29.80** |
| w8h64f56, follow-on, rate 5/6 | 20, 26.98 | **20, 32.38** | 13, 36.05 | 14, 18.81 | 10, 25.44 | 3, 23.45 |
| w8h64z78, full bursts, rate 7/8 | 20, 21.07 | 18, 24.10 | 20, 29.76 | 20, 21.16 | 17, 21.70 | 13, 19.39 |
| w8h64f78, follow-on, rate 7/8 | 17, 26.56 | 18, 29.02 | 3, 18.96 | 7, 31.87 | 7, 11.86 | 0, 0 |

**Rate 5/6 as full bursts is the robust choice for bulk**: every frame, or all but one, in every cell in both directions, 29.8 kbit/s at 3000 bytes into radio1 and 28.2 into radio2, against 26.2 at rate 3/4 three hours earlier. It ships as `ofdm-fm-8k-r56`. On the steady direction, follow-on bursts at 5/6 deliver every frame at 1900 bytes at 32.4 kbit/s, and at 3000 bytes 36 kbit/s on the 13 frames of 20 that arrive, against a coded rate of 36.7 kbit/s on this preset. **Rate 7/8 is past this link's margin at QAM-64**: full bursts hold at 1024 bytes only, and follow-on bursts at 7/8 lose most of every keyup longer than that. Its rung stays on the ladder for a link that has the margin, and ships as `ofdm-fm-8k-r78` to mark where the edge is; on this link the controller has no reason to reach it.

**The direction that wobbles had moved again.** In this hour radio1 was the weaker receiver: the follow-on rate-3/4 preset that had delivered every frame into radio1 at 22:05 lost 12 of 20 at 3000 bytes at 22:49, and follow-on bursts at 5/6 lost frames into radio1 at every length while full bursts at 5/6 lost none. Three hours earlier it was radio2. That is the case for the controller's choice of burst form per link (4.5) being made continuously, from what it hears.

## 5. If the preset is the acquisition layout

*This section is the only measurement there is of a wide layout used as a table's acquisition layout, and it is why no shipped preset joins a geometry table. See [geometry-signalling.md](geometry-signalling.md).*

The estimate symbol is a property of the table, not of the waveform: a payload on the acquisition layout does not carry one. For a two-station bench that only ever runs one wide preset, making that preset's layout entry 0 removes a symbol from every burst, which at 1024 bytes is 7 to 10 % of the burst. The cost is that every station in such a table must be able to transmit the wide acquisition symbols, so a narrow-path radio cannot join it. Measured with the 8 kHz layout as entry 0 and the bench's narrow layout moved down the table, same protocol as section 4.2, scored from the frame logs:

| candidate, 8 kHz layout as acquisition | 1024 B, 1 to 2 | 1024 B, 2 to 1 | 1900 B, 1 to 2 | 1900 B, 2 to 1 |
|---|---|---|---|---|
| w8h64 | 19, 15.52 | 20, 16.38 | 15, 15.34 | 16, 16.33 |
| w8h64r34 | 19, 16.47 | 20, 17.37 | 14, 15.63 | 11, 12.29 |

**Not worth it.** The rate per delivered frame rises 6 to 12 % at 1024 bytes, as the missing symbol predicts, and delivery falls: one frame lost at 1024 bytes in one direction, and 15 and 16 of 20 at 1900 bytes against 18 and 19 with the narrow acquisition. A sync symbol and a header spread over 323 carriers to 8 kHz acquire worse than the same on a far narrower layout, on this rig and at this signal level, because the search's band-limit now admits everything to 8 kHz and the sync symbol's self-correlation is what it costs. A narrow acquisition layout, which the proposal chose for shared-channel reasons, turns out to be the right one for throughput too, and the estimate symbol is the price of it, paid gladly.

## 6. The preset, and what remains

**`ofdm-fm-8k`**: carriers 9 to 341 (211 Hz to 7992 Hz), 323 data and 10 pilots, QAM-64, K=7 rate 2/3, crest-factor limit 10 dB, full drive, contiguous bursts, a 64-sample cyclic prefix, and no geometry id, so it acquires on its own layout and two stations must both be on it. Measured against the 6 kHz preset in the same session and scored from the frame logs: 20 of 20 both ways at 1024 bytes at 16.3 kbit/s (6 kHz: 12.6 and 12.7), and 19 and 17 of 20 at 1900 bytes at 19.3 and 17.3 kbit/s (6 kHz: 15.9 and 16.1, 20 of 20). Both stations were left running it as their persistent mode at the end of the day.

In NinoTNC terms, which name a mode by its raw channel bit rate, the waveform is about 44,000 bit/s (22.727 symbols/s times 323 carriers times 6 bits), 29.4 kbit/s after the code, and 17.5 kbit/s as a KISS application doing a one-way bulk transfer of 1024-byte frames sees it. The difference is the four fixed symbols per burst, the rounding of a frame up to whole symbols, the AX.25 header and the first frame's keying time, in that order.

With 1900 to 3000-byte frames (section 4.4) it delivers 20.5 to 21.8 kbit/s into radio1, and with the daemon keeping the sound card running for the whole keyup (4.5) 21.7 and 23.9 kbit/s with every frame.

**`ofdm-fm-8k-follow`** is the same preset with follow-on frames (4.5): 23.2 kbit/s at 1024 bytes both ways with every frame, 26.6 and 27.5 at 1900 and 3000 bytes when the link is steady, and at rate 3/4 30.4 kbit/s at 3000 bytes, about 70 % of the raw rate. It is what to run for bulk transfer on a good link and the least forgiving thing here on a wobbling one, where `ofdm-fm-8k-r56` (4.7) delivers 29.8 kbit/s at 3000 bytes with every frame in both directions, and is the robust choice.

What stops it going further, in the order it would pay to remove:

1. **The controller chooses the burst form** now: a receiver that sees two follow-on bursts fail among the last eight asks its correspondent for full bursts, in the header beside the rate recommendation, and lets follow-on bursts be tried again once eight full bursts have decoded, holding twice as long each time they fail again soon after. A follow-on failure no longer steps the rate down, because on the same link at the same time full bursts at that rate were decoding. What the controller still does not choose is the rate from the per-carrier SNR it now measures, nor the geometry; it steps the rate on the pre-FEC spending of decoded bursts as it always did, and rate 3/4 is a rung it can step to.
2. **Long follow-on keyups on a wobbling clock.** A follow-on burst is read against an estimate inherited from the burst before, and when the transmitting sound card's clock wobbles within a symbol that inheritance costs several per cent of errors in the first symbols after the boundary, on data carriers the pilots cannot see. Which direction does it moves with time. Either the tracker learns to see it or the transmitter's card is made to hold its clock.
3. **Rates above 5/6**: 5/6 and 7/8 exist on the K=7 code now and are rungs on the ladder (4.7); 5/6 delivers, 7/8 does not on this link. What would raise the rate again while keeping the margin is a long-block code sized to the frame at 2/3 or 3/4, which the rate-1/2 LDPC family measured 0.5 to 1.6 dB better than K=9 at bulk sizes says is worth building (see [receiver-findings.md](receiver-findings.md)).
4. **The robust rungs on wide spans** pay 4 dB for being four times longer at 1024 bytes; the per-symbol tracker was measured at QAM-64 and should reach them too, and has not been measured there.

Closed tonight: the width question stays closed at 8 kHz; the back-to-back losses of 4.3 and the long-frame losses of 4.4 were the settling clock, and with the daemon fixed the full-burst preset delivers 20 of 20 at every length in five cells of six; and the cyclic prefix is 64 samples (4.6), 3 % more for nothing measurable.

## 7. What the pipe could carry: voice and video, a thought experiment

Asked on 2026-09-19, with the numbers above as the inputs. Nothing here has been built.

**The pipe.** During payload symbols the preset carries 29.4 kbit/s of coded payload (1292 bits a symbol). Every burst pays four fixed symbols first (sync, preamble, header, estimate), and the first burst of a keyup pays the radio's TXDELAY on top. A stream that holds the key and sends bursts of N payload symbols back to back therefore gets N/(N+4) of 29.4 kbit/s, and has a burst's length of latency:

| payload symbols per burst | burst length | rate | mouth-to-ear, about |
|---|---|---|---|
| 2 | 264 ms | 9.8 kbit/s | 350 ms |
| 4 | 352 ms | 14.7 kbit/s | 440 ms |
| 8 | 528 ms | 19.6 kbit/s | 630 ms |
| 16 | 880 ms | 23.5 kbit/s | 1 s |

The two long rows are where today's long bursts started losing frames to the sample clocks, so for a stream the honest choice is four to eight symbols a burst.

**Voice.** Opus at 12 to 16 kbit/s is wideband speech (50 Hz to 8 kHz) that most people cannot tell from a good telephone line, and 20 to 24 kbit/s is fullband and carries music. Four symbols a burst gives 14.7 kbit/s and about 440 ms mouth-to-ear, which is the shape of a good push-to-talk link: Opus at 12 kbit/s wideband, 20 ms frames packed eighteen to a burst, with the rest for framing and a parity burst every few. That is about four times the bit rate the digital voice modes people run over FM today (Codec2 at 3.2 kbit/s), on the same 25 kHz channel, with 8 kHz of audio bandwidth against analogue FM's 3. Loss: a lost burst is a lost 350 ms, which Opus's concealment does not hide, so the rate stays at 2/3 and the stream wants either a parity burst per group or Opus's in-band FEC, and with either a few per cent of burst loss is a link a person would use. Rate 3/4 buys 12 % and, by the evening's evidence (4.5), delivers it once the sound card is kept running for the keyup; the afternoon's lost bursts were the settling clock.

**What a continuous mode would buy.** The four fixed symbols are the burst structure, not the waveform. A mode that keys once and sends payload symbols continuously with a pilot symbol every so often for the clock and the channel, resynchronising only when it loses lock, would carry close to the full 29.4 kbit/s: Opus at 24 kbit/s fullband, stereo if wanted, at about 150 ms latency. That is a real change to the receiver (tracking rather than acquiring), and it is the change that turns this from a packet modem into a broadcast-grade voice channel. Follow-on frames (4.5) are most of that change, and measured: 23.2 kbit/s at 1024-byte frames, 27.5 at 3000, and 30.4 at rate 3/4, so Opus at 24 kbit/s fullband is inside the pipe as it stands, at a frame's latency rather than a keyup's, on a steady link.

**Video.** At 14 to 20 kbit/s the current codecs (AV1, H.265) manage a talking head at about 160 by 120 at 5 to 8 frames a second, or 320 by 240 at one frame a second; it is a moving postage stamp, recognisable and lip-readable, not television. A continuous mode at 29 kbit/s reaches 176 by 144 at 10 frames a second on a good encoder, which is the shape of an early video call. Still pictures are the better use of the pipe: a 640 by 480 photograph at ordinary quality is about 40 kB, which is 20 seconds on the preset, and a 320 by 240 one is five.

**The ceiling, for reference.** The audio path passes about 8 kHz and the channel gives about 26 dB per carrier, so 6 bits per carrier over 8 kHz is about 44 kbit/s raw and that is the waveform's top on these radios. The 25 kHz RF channel itself, at the signal-to-noise this rig has, would carry an order of magnitude more with the modulator driven directly rather than through the audio path; that is a different radio.
