# Bandwidth, bit loading and frame length: sizing a preset to this radio

**Status: measured 2026-09-18.** Prompted by the passband measurement coming back much better than the waveform assumed.

## The opportunity

The profiles the modem was carrying at the time were sized for a 300 to 3000 Hz voice path. Measured on this TM8110 and CM108 combination, in both directions, the actual audio path is flat within 0.1 dB from 200 Hz to 4.5 kHz, -1.7 dB at 5 kHz, -2.8 dB at 6 kHz and -5.6 dB at 7 kHz. We are using about half the channel we have.

## Three constraints, and they fight

**1. The audio path rolls off.** Gently, and only past 4.5 kHz. On its own this would put the useful top edge somewhere around 6 to 7 kHz.

**2. FM occupied bandwidth grows with the top audio frequency.** Roughly 2 x (deviation + highest audio frequency). At a voice-width profile and tonight's 2.84 kHz peak deviation that is about 11.5 kHz, comfortably inside a 25 kHz channel. At a 6 kHz top edge it is about 17.7 kHz, which is close enough to a 25 kHz radio's IF filter to start being truncated by it. This is almost certainly what the campaign's "IF-truncation distortion floor" prediction was about, and the reason it never appeared on air is that every dense cell so far has run on a voice-width profile, where it cannot.

Note the shape of this constraint: it can be bought off with LOWER deviation, which costs FM signal to noise. So bandwidth and deviation are a two-dimensional surface with a peak somewhere in it, and both axes are settable.

**3. FM noise is triangular, and this tap does nothing about it.** After a discriminator the noise power density rises with the square of the audio frequency, 6 dB per octave. Broadcast FM cancels this with pre-emphasis and de-emphasis; the T12 transmit tap and the receive tap deliberately bypass both, which is why the measured passband is FLAT rather than sloping, and is also why the noise is not.

The codec already knows this: the per-carrier weighting comment records that the deployed un-de-emphasised tap "tilts 18 dB across the band", which is 6 dB per octave across a voice-width span's three octaves, and per-carrier noise weighting was built, measured worse and removed.

**The consequence for a wide preset is the important part.** From 211 Hz to 6 kHz is 4.8 octaves, so about 29 dB of signal-to-noise tilt from the bottom carrier to the top. A uniform constellation across that span is the wrong shape: the bottom carriers are being asked to carry far less than they could, and the top carriers are the ones that fail first. This is what `BitLoading` is for, and on a wide preset it stops being a refinement and becomes the main design decision.

## A prediction about bit loading, made before measuring it

[receiver-findings.md](receiver-findings.md) records that per-carrier noise weighting, which it calls "the receive-side twin of bit loading", was built against exactly this tilt and measured WORSE: 117 frames over twelve cells became 97. The decisive arm of that experiment handed the receiver the link model's ground-truth per-carrier noise, and **the genie lost too**, which says the weighting model is wrong for this channel rather than the estimate feeding it. The hypothesis on record is that near a working point an FM discriminator's failures are not stationary Gaussian noise per carrier: they are clicks, time-bunched and broadband.

Broadband clicks are not something a per-carrier bit allocation can dodge, so that result is a real warning against assuming bit loading will pay.

**The prediction, so it is on record before the measurement rather than after.** These are not quite the same problem. Bit loading addresses the STATIONARY tilt, which is measured, large and not in dispute. The clicks are an additional, non-stationary impairment that dominates close to a cliff and matters much less well above one. So bit loading should help at moderate margin, and should disappoint near the cliff, where whatever the top carriers were given will be lost to the same click that takes the bottom ones. If it instead helps most near the cliff, the click hypothesis is wrong and that is worth more than the throughput.

## What tonight can and cannot settle

At the current bench margin, about 58 dB above the idle floor, a 29 dB tilt costs nothing: even the worst carrier has plenty in hand. So a bandwidth sweep tonight measures the CEILING, which is a real and useful number, and it will make uniform bit loading look fine when it is not.

The shape of the right preset at a working margin cannot be found without a signal axis, because the whole question is which carriers run out first and at what level. That is stage F, and it is waiting on the power attenuators.

Both are worth having, in that order: the ceiling says what the channel could carry, the ladder says what to ask of it.

## Preset family under test

Same low edge throughout, at carrier 9 (211 Hz), where the path is within a quarter of a decibel and which buys half an octave of the BEST part of the band, since low carriers see the least noise. Only the top edge changes.

| candidate | carriers | span | data carriers |
|---|---|---|---|
| x5 | 9 to 213 | 211 Hz to 4992 Hz | 197 |
| x6 | 9 to 256 | 211 Hz to 6000 Hz | 240 |
| x7 | 9 to 299 | 211 Hz to 7008 Hz | 283 |

All at QAM-64 rate 2/3 for the first pass, so bandwidth is the only variable, measured against a narrow voice-width profile at the same rate. **That reference is not one of the shipped profiles**, and nothing of our own has been measured in its place: what the rows below buy is the difference between the candidates, and the narrow row is there to say where the day started. Goodput is measured with a bulk sender that queues frames back to back and times the delivery, rather than counting single frames separated by a gap a test script chose.

## Result: bandwidth, measured 2026-09-18

Four cells, same rate throughout (QAM-64, K=7 rate 2/3), 24 frames of 1024 bytes queued back to back. Every frame arrived in every cell.

| candidate | audio span | data carriers | goodput |
|---|---|---|---|
| narrow reference (not shipped) | voice width | | 7.42 kbit/s |
| x5 | 211 Hz to 4992 Hz | 197 | 11.10 kbit/s |
| x6 | 211 Hz to 6000 Hz | 240 | 12.86 kbit/s |
| x7 | 211 Hz to 7008 Hz | 283 | 13.41 kbit/s |

**No IF truncation, up to 7 kHz of audio.** The x7 candidate needs roughly 19.7 kHz of RF by Carson's rule at tonight's deviation, and it delivered 24 of 24. So the constraint that was expected to bite first does not bite at all on a 25 kHz channel at this margin, and the campaign's IF-truncation prediction stays unconfirmed for a second time tonight.

**The returns diminish sharply, and per-burst overhead explains all of it.** x6 to x7 is 18 % more carriers for 4 % more throughput. Each burst carries about four symbols of fixed cost before any payload: sync, preamble, header and lead-in. Predicting the elapsed time from symbol counts alone gives 25.0, 16.3, 14.1 and 13.1 s against 26.5, 17.7, 15.3 and 14.7 measured, which is the same shape throughout and no room for an unexplained loss.

The consequence is a change of direction. Once the payload is only eight symbols long, adding carriers mostly adds rounding waste. What is left is what rides on each symbol, and how many payload symbols there are to amortise the fixed four: **a denser constellation, and longer frames.**

## Result: the tilt is what stops a dense wide preset, and it corrects the prediction above

Pushing the 6 kHz candidate to QAM-256 at rate 3/4 does not work: **10 frames of 24**. The same constellation on a narrow voice-width profile managed 29 of 30 an hour earlier, at the same margin, on the same radios.

That difference is the whole argument. A narrow voice-width span covers about three octaves and so about 18 dB of tilt; the 6 kHz span covers 4.8 octaves and about 29 dB. Uniform QAM-256 asks the same thing of every carrier, and the top of a wide band is roughly 11 dB worse off than the top of a narrow one. It is not a margin problem and it will not come back at a higher signal level, because the tilt is 6 dB per octave of the path itself.

**This corrects what is written above.** The prediction was that uniform dense loading would beat bit loading at high margin, on the arithmetic that a tilt-matched allocation averages fewer bits per carrier than a uniform dense one. The arithmetic was right and the premise was wrong: uniform QAM-256 is not available across a wide band at ANY margin, so the comparison was never between 8 bits everywhere and a tilted average. It is between 6 bits everywhere, which works, and a tilted allocation that puts 8 on the carriers that can take them.

| candidate | loading | delivered | goodput |
|---|---|---|---|
| x6 | uniform QAM-64, rate 2/3 | 24/24 | 12.86 kbit/s |
| x6h | uniform QAM-256, rate 3/4 | 10/24 | not usable |
| x6bl | 8 bits below 3 kHz, 6 above, rate 2/3 | see below | |

Taking the same constellation across three spans puts a number on where it stops:

| span | octaves | tilt | QAM-256 delivered |
|---|---|---|---|
| a narrow voice-width span | about 3 | about 19 dB | 29/30 |
| 211 Hz to 6000 Hz | 4.8 | about 29 dB | 10/24 |
| 211 Hz to 7008 Hz | 5.0 | about 30 dB | 1/24 |

The last two are barely a decibel of tilt apart and fall from 42 % to 4 %, so the threshold sits around 29 to 30 dB of spread. That is where it should be: QAM-256 wants roughly 28 dB of signal-to-noise, so at that tilt the top carriers are dropping through their own floor while the bottom ones are still comfortable. The band tilt is not a nuisance term in this design, it is the binding constraint on it.

The bit-loaded candidate puts 115 data carriers at 8 bits and 125 at 6, averaging 6.96 against 6.00, which is 16 % more payload per symbol if the split is in the right place. The split was chosen from the tilt rather than by search: each extra pair of bits wants about 6 dB, the band gives up 6 dB per octave, so the carriers that can take 8 bits are the ones at least an octave below the top.

## Result: bit loading, measured 2026-09-18

| candidate | loading | delivered | goodput |
|---|---|---|---|
| x6 | uniform QAM-64, rate 2/3 | 24/24 | 12.86 kbit/s, and 12.90 on a repeat |
| x6bl | 8 bits below 3 kHz, 6 bits above, rate 2/3 | 24/24 | 13.34 kbit/s |
| x6h | uniform QAM-256, rate 3/4 | 10/24 | not usable |

**It works, and it is the only thing that works at 8 bits on this span.** The bit-loaded candidate puts QAM-256 on the carriers that can take it and keeps every frame, where asking the same of all of them loses more than half. The uniform control was run twice, 0.3 % apart, so the instrument resolves the difference comfortably.

**The gain is 3.7 %, not the 16 % the bit budget predicts, and the shortfall is whole-symbol rounding.** A payload occupies a whole number of symbols. At 1024 bytes the uniform preset needs 9 and the bit-loaded one needs 8, so a 16 % improvement in bits per symbol buys one symbol out of 13 once the fixed four are counted. Most of the budget is spent on the part of the last symbol that carries nothing.

That is the same fixed-cost story as the bandwidth sweep, in a different disguise, and it points the same way: **frame length is the multiplier on everything else here.** The longer the payload, the less of each improvement is lost to the rounding, and the smaller the fixed four symbols look beside it.

## Result: frame length, and where the sweet spot actually is

The KISS input drops anything over 2048 bytes silently (raised as packet-net/pdn-soundmodem#503), so 1900 bytes is the longest frame currently testable. The three widths at that length, against the same widths at 1024:

| candidate | span | 1024 B | 1900 B |
|---|---|---|---|
| x5 | 211 Hz to 4992 Hz | 11.10 kbit/s, 24/24 | 12.56 kbit/s, 16/16 |
| x6 | 211 Hz to 6000 Hz | 12.86 kbit/s, 24/24 | **15.36 kbit/s, 16/16** |
| x7 | 211 Hz to 7008 Hz | 13.41 kbit/s, 24/24 | 16.01 kbit/s, 9/16 |

**x6 at 1900-byte frames is the best configuration that keeps every frame: 15.36 kbit/s**, against 7.42 for the narrow reference at the same rate. Length alone bought x6 19 %.

**Width and length interact, and that moves the answer.** x7 was 24 of 24 at 1024 bytes and 9 of 16 at 1900. Its top carriers are the marginal ones, and a longer burst gives them more chances to put an error somewhere the code cannot reach. The aggressive bit-loaded candidate went the same way, 24 of 24 short and 11 of 16 long.

So width that survives a short frame is not width that survives a long one. Since long frames are where the throughput is, the widest preset is not the fastest usable one, and neither is the cleverest loading.

## Result: the best usable setting is at a cliff, which is why nothing cleverer helps

Three ways of spending the margin at 1900 bytes on the 6 kHz layout, with the unmodified preset re-run last as a control:

| setting | bits per carrier | delivered | goodput |
|---|---|---|---|
| uniform QAM-64, rate 2/3 | 6.00 | 16/16 | 15.36 kbit/s |
| uniform QAM-64, rate 3/4 | 6.00, weaker code | 3/16 | not usable |
| 8 bits below 3 kHz, rate 2/3 | 6.96 | 11/16 | not usable |
| 8 bits below 2 kHz, rate 2/3 | 6.62 | 1/16 | not usable |
| uniform QAM-64, rate 2/3, CONTROL | 6.00 | 16/16 | 15.28 kbit/s |

**The control is the important row.** Re-run after the failures, on the same evening and the same path, it returned 16 of 16 at 15.28 against its earlier 15.36. The link had not moved; the failures are real.

**So the working preset is sitting just under a cliff.** Every one of the three alternatives asks for between 10 % and 16 % more, and all three fall off. That also explains the one ordering here that is not physical: the more conservative bit loading did WORSE than the aggressive one, 1 of 16 against 11. Both are past the edge, and past an edge that steep the delivered count is not measuring what it was set up to measure.

**Bit loading cannot help where there is no margin to redistribute.** The idea is sound and it was demonstrated working at 1024 bytes, where the same aggressive candidate kept every frame and beat uniform loading. At 1900 bytes there is nothing spare to move around.

Which puts a shape on the remaining work. The throughput question and the signal-ladder question are the same question: this path's best setting is defined by where its cliff is, and the cliff has been located only in the one direction that costs nothing to explore, which is asking for more. Finding how much margin there is to give back needs the attenuators.

## Result: the frame length curve, and a correction to the sweet spot above

Measured on the 6 kHz candidate at QAM-64 rate 2/3, frames queued back to back, with a corrected reader (the first one re-parsed its whole accumulated buffer per read, which is quadratic, and a second attempt at it dropped the opening delimiter of every frame that straddled a read; both are fixed and the KISS reader now has a test that feeds it frames in seven-byte chunks).

| frame | delivered | goodput |
|---|---|---|
| 64 B | 40/40 | 1.97 kbit/s |
| 256 B | 40/40 | 5.72 kbit/s |
| 512 B | 40/40 | 9.05 kbit/s |
| 1024 B | 39/39 | 12.36 kbit/s |
| 1900 B | 16/21 | 15.17 kbit/s |

**Frame length is worth a factor of six**, from 1.97 kbit/s to 12.36 with every frame still arriving. That is the whole of the fixed per-burst cost being amortised and nothing else changing.

**And it corrects the claim above that 1900 bytes is the sweet spot.** That came from two runs of sixteen frames which both happened to come back clean. At twenty-one frames the same setting drops five. So the honest statement is that **1024 bytes is the longest frame this path delivers in full**, and 1900 buys about 23 % more throughput in exchange for retransmissions that a connected-mode link would have to pay for. Which of those is faster end to end is not something a one-way instrument can answer, and it is a good argument for finishing the connected-mode work before tuning any further.

## What the transmit-log defect does to everything above

[missing-frames-under-load.md](missing-frames-under-load.md) establishes that under load about a third of the frames a station logs as transmitted are never heard. That confounds one specific thing here and leaves the rest standing.

**The goodput figures stand.** They are delivered bytes over wall time, and both halves are measured at the ends rather than inferred from the log. A frame that was never sent shortens the elapsed time as well as the delivered total.

**The delivery FRACTIONS in the longer runs do not.** "16 of 21" and "26 of 100" were counted against frames the transmitter claimed to have sent, and a third of those claims are unaccounted for. Those runs were measuring the transmit defect, not the link.

**So the correction above is itself unsafe.** The claim that 1024 bytes is the longest frame this path delivers in full rests on 1900-byte runs dropping frames, and those drops are now the prime suspect for having never reached the air. The frame-length sweet spot is not established, and cannot be until the frame log counts what was played. The honest position is that longer frames are worth a factor of six in goodput, which is measured, and that where reliability falls off is not yet known.

The day after, [preset-design.md](preset-design.md) section 4.5 found the mechanism: the transmitting sound card was drained and re-armed between frames, so every frame after the first in a keyup went out on an unsettled clock. With that fixed the preset delivers 20 of 20 at every length in five cells of six, and 3000-byte frames became the fastest thing on the rig.

A carrier layout is not signalled to a receiver that has not been configured for it, so both ends move together for these. See [geometry-signalling.md](geometry-signalling.md), which this work is the input to.
