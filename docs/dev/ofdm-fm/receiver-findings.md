# Receiver findings

What has been tried on the receive path, what it measured, and what therefore is and is not in the code. Negative results are here on purpose: three of the four things below looked obviously right, were built, measured no better or worse, and were reverted. Without this file the next person rebuilds them.

All numbers are frames recovered out of the seeds tried, on a narrow profile, QPSK, 64-byte payload, FM link at 2500 Hz peak deviation (100 % of class on a 12.5 kHz channel; measurements before 2026-08-10 used 3000 Hz, which is 120 % of that and flattered them by about 1.6 dB), carrier-to-noise stated in the receiver's IF bandwidth. Anything measured before M0LTE.FmChannel 0.3.0 is not comparable with anything after it, because up to 0.2.0 the model's filters had fixed tap counts and so got sloppier as the sample rate rose.

## In the code, and what it bought

### Coding the header (in), and spreading it over two symbols (out)

The header was 40 uncoded bits, BPSK, repeated about 2.75 times across the data carriers of ONE symbol, combined by |H|^2 and hard sliced. It is now coded: tail-biting convolutional K=9 rate 1/2, 40 bits in and 80 out, soft-decision Viterbi, with the coded bits scattered across the data carriers by a coprime stride. Eighty bits fit inside one symbol on every profile in use, so **the coding costs no air time whatsoever**.

Measuring the header on its own said it was fade limited, not noise limited: with the flutter off it read 64 of 64 at every carrier-to-noise ratio where the burst could be acquired at all, and 18 of 40 at +20 dB with 20 Hz of Doppler. A flat fade takes every carrier down together, so a header inside one symbol has no diversity to exploit, and the obvious answer is a second copy a symbol later, which usually misses the same dropout. That was built and it is NOT in the code, because it does not pay.

Three arrangements, 64 seeds a cell, fifteen cells from no flutter to 20 Hz, 960 bursts each:

| header | frames recovered | lost to header | lost to payload |
|---|---|---|---|
| uncoded, one symbol (as it was) | 772/960 | 53 | 82 |
| **coded, one symbol (now)** | **806/960** | 32 | 79 |
| coded, two symbols | 808/960 | 25 | 87 |

Uncoded to coded is +34 frames, p = 0.049, for no air time. Coded one symbol to coded two symbols is **+2 frames, p = 0.95**, for 8 % more air time. The second copy does what it was meant to - it takes header losses from 32 to 25 - and it gains nothing overall, because a burst 8 % longer is 8 % more exposed to the next dropout and loses as many frames in the payload as the header saves. It trades a header failure for a payload failure, roughly one for one.

**Two things I got wrong, recorded because the reasoning looked sound at every step.** The first write-up of this attributed the gain to the time diversity and led with a single dramatic cell (2 of 16 becoming 8 of 16, which did not clear significance at all at that sample size). Every bit of the gain was the coding, which was the free part, and none of it was the diversity, which was the part the whole design argument was built on. The second was claiming that the payload bucket improved as a knock-on; it did not, and at 64 seeds the two-symbol arrangement makes it measurably worse, which is the actual mechanism that kills the idea.

The lesson is not that the diversity reasoning was wrong - a fade really does take one symbol and not usually two. It is that a mechanism being real does not make it worth its cost, and the cost here (air time on a burst that must survive the same channel) attacks the same quantity the benefit does. Nothing but an end-to-end measurement across the whole burst was ever going to show that.

`OfdmFmParameters.HeaderRepeats` keeps the two-symbol arrangement available and defaults to one. It is kept rather than deleted only because the trade depends on payload length: at 64 bytes the extra symbol is 8 % of the burst, at 256 bytes it would be nearer 2 %, and nobody has measured whether the header saving survives at that ratio.

### Scattering and repeating the header (in)

The header was laid on a contiguous run of carriers, which on this waveform is the bottom of the band, which on a voice path is the worst of it. Spreading it by a coprime stride and repeating it across every data carrier took the +16 dB rung from dying outright to 8/8. This also corrected an earlier and wrong conclusion that +16 was simply the FM threshold for this waveform.

### The channel estimate denoiser (in)

Transform the estimate to a delay profile over the occupied band only, shrink each tap by how much of it is signal rather than noise, transform back. Measured, 8 seeds:

| coding | +40 | +34 | +28 | +24 | +20 | +16 |
|---|---|---|---|---|---|---|
| none, before | 8 | 8 | 7 | 2 | 0 | 0 |
| none, after | 8 | 8 | 8 | 5 | 0 | 0 |
| conv 2/3, before | 8 | 8 | 8 | 8 | 8 | 3 |
| conv 2/3, after | 8 | 8 | 8 | 8 | 8 | 7 |
| conv 3/4, before | 8 | 8 | 8 | 8 | 3 | 0 |
| conv 3/4, after | 8 | 8 | 8 | 8 | 7 | 0 |

Two details are load-bearing and both were got wrong first: it must transform the occupied bins only (inventing values for the unoccupied ones rings, and rang hard enough to break QAM-256 on a noiseless loopback), and the bulk delay must come out before the transform and go back after (a non-integer phase ramp does not transform to a spike, it transforms to a sinc smeared over every tap, and then the shrinkage eats the channel).

### Padding the last symbol with noise instead of zeros (in)

The largest single gain measured in this repository, and it is one line.

The last payload symbol is rarely full, and the tail was padded with zero bits. Zero is a constellation point like any other, so a run of padded carriers all transmitted the SAME point, and a run of identical carrier values is close to an impulse in the time domain. Measured on a narrow profile, that symbol peaked at **2.238 while every other symbol in the burst peaked between 0.70 and 0.89**, and it set the burst's peak on every burst at every payload length tried.

That is expensive in a way that is easy to miss, because it costs nothing where it happens. An FM transmitter is peak deviation limited, so the loudest instant in a burst sets the drive for all of it: one peaky symbol held every other symbol about 8 dB below the deviation the radio was set up for. Burst crest factor was 18.5 dB.

Padding with pseudo-random bits instead costs nothing at all - the receiver reads exactly as many soft bits as the code produced and never looks at the rest, so their value was always free. The padded symbol now peaks at 0.813, the burst peak is set by the sync symbol at 0.891, and the crest factor is 11.9 dB.

Measured, acquisition oracle, fixed station, 48 seeds, a narrow profile, frames recovered by the real receiver:

| CNR | before | after |
|---|---|---|
| +12 dB | 40/48 | 48/48 |
| +10 dB | 12/48 | 44/48 |
| +8 dB | 1/48 | 17/48 |

**About 3 dB of sensitivity**, which is more than every coding change in this file put together.

**That figure depends on where the modem injects, and for this deployment it holds.** It was measured with the transmitter's drive scaled so each burst's peak lands on the deviation limit, which is what an operator must do at a tap PAST the radio's limiter: nothing there protects the modulator, so the level is set once against the waveform's own peak and a spike costs level across the whole burst. A Tait TM8100's T13, which is the tap in use, sits after the limiter, the 3 kHz low pass, the peak-system-deviation scaler, the 300 Hz high pass and pre-emphasis, so it is exactly that case. Re-measured with a limiter in circuit instead - the microphone path, or a tap before the limiter such as T5 - the same fix is worth nothing measurable, because the spike is clipped rather than dragging the burst down and one symbol of eight is distorted while the rest are untouched. Same waveform, same fix, two honest answers to two different questions. The 50 % point moves from roughly +10.7 dB to +7.7 dB. `CrestFactorTests` pins it: no symbol may peak more than twice the median symbol of its own burst.

The lesson worth carrying: on a peak-limited transmitter, a defect in ONE symbol is paid for by every symbol, so "it only affects the padding" is not a reason to leave it. Look for structure in anything transmitted that is not data.

### The search band limit (in)

The largest single gain still on the table, and it only appears on the hardware this is actually for.

The deployment target is a Tait TM8100 tapped at **R1 and T13**: R1 is the discriminator output, ahead of the PSD normaliser, the 3 kHz low pass, the 300 Hz high pass and de-emphasis; T13 goes straight into the modulator, past compression, the 300 Hz high pass, pre-emphasis, the limiter and 3 kHz low pass, and the PSD scaler. So the audio path is flat, un-emphasised and band-limited only by the IF filter - roughly 4 kHz on a 12.5 kHz channel. That is the `DataPort` profile, not `MicAndSpeaker`, and every measurement in this file above this section used the wrong one.

Measured on the right one, acquisition oracle, 48 seeds, a narrow profile, frames recovered:

| received audio band | +12 dB | +10 dB | +8 dB |
|---|---|---|---|
| full 4 kHz, as R1 delivers it | 39/48 | 7/48 | 0/48 |
| low passed to 3 kHz | 48/48 | 44/48 | 18/48 |

**About 3 dB, from a low pass filter on the receiver's own input.** Nothing transmitted changes. Built, and measured again after building: the real search goes 39/7/0 to **48/47/17** at +12/+10/+8 dB, which puts it level with the genie column at +12 and +10 (48 and 47). Acquisition has stopped being the limit at those rungs entirely.

The mechanism is worth understanding because it explains why nothing caught it. The waveform occupied only the lower part of that band, so everything above it up to 4 kHz was noise and no signal, and FM discriminator noise power rises with the SQUARE of audio frequency, so that top slice is the noisiest part of the band. The demodulator never noticed: each carrier is a transform bin and out-of-band noise lands outside all of them. `FindSync` correlates the raw time-domain waveform, so it swallows every bit of that noise, and acquisition is what collapses. It is invisible on `MicAndSpeaker` because the radio's own 3 kHz low pass was doing the job for us.

**Leaving the decode path unfiltered is not a preference, it is required.** A filter sharp enough to be worth having has an impulse response longer than the cyclic prefix - 166 taps against a 64-sample prefix at this geometry - and anything longer than the prefix puts inter-symbol interference into every symbol, which is precisely what the prefix exists to prevent. Filtering everything was tried first and broke six tests for that reason. The search does not care: it looks for self-similarity, not orthogonality.

So `SearchBandLimit` filters a parallel copy for the correlator alone. The modem holds it in lockstep with the raw window, compacting both together, and the codec's whole-buffer search filters a fresh copy from a zeroed state so the two see identical audio and agree on where a burst starts - which `The_Incremental_Metric_Agrees_With_A_Direct_One_At_Every_Position` exists to enforce.

**One trap, recorded because it hung the test suite rather than failing it.** The filter is linear phase and delays by exactly half its length, so a position in the filtered buffer is that much later than the same instant in the raw one. Committing a peak converts one way; resuming the search after a failed header converts the other. Getting the second conversion wrong put the search BEHIND the peak it had just rejected, so it found the same peak again, for ever. `SearchPositionOf` now names the conversion instead of leaving it to arithmetic at three call sites.

Two configuration notes for the R1 tap, from Tait's own documentation rather than from us. If the receive tap out type is bypass at R1, the squelch must be set to **signal strength, not noise level (SINAD)**, because the noise-level detector samples at R1 itself and will unmute unpredictably. And subaudible signalling decodes ahead of the 300 Hz high pass, so a channel with CTCSS programmed and a bypass tap at R1, R2 or R4 will open and close its mute unexpectedly.

### Peak reduction, and the deviation it buys (in, opt in)

**Deviation is the biggest lever on this link and it was being wasted.** This waveform is injected at T13, past the radio's limiter, so the operator sets the drive once against the burst's loudest instant. An FM discriminator's output noise is fixed by the IF while the recovered signal is proportional to deviation, so post-detection signal to noise goes as deviation SQUARED. Every decibel taken off the crest factor is a decibel of link, for no bandwidth and no air time.

Measured, 32 seeds, a narrow profile, QPSK at rate 1/2, carrier to noise fixed in a fixed 7.8 kHz IF, so every row is the same received power into the same radio and differs only in how hard it is driven:

| peak deviation | +14 | +12 | +10 | +8 | +6 |
|---|---|---|---|---|---|
| 1500 Hz (60 % of class) | 16/32 | 1 | 0 | 0 | 0 |
| 2500 Hz (100 %, legal) | 32 | 31 | 20 | 1 | 0 |
| 4000 Hz | 32 | 32 | 32 | 30 | 4 |
| 5000 Hz | 32 | 32 | 32 | 32 | 11 |
| 6900 Hz (full scale at T13) | 32 | 32 | 32 | 28 | 7 |

Two things fall out. **Running at 60 % of class deviation, which is what Tait's own 1200 baud modem defaults to, costs 3 to 4 dB here** - that default is sized for FFSK at about 3 dB crest factor, and this waveform is at 12. And the 6900 Hz row going backwards at +8 dB is the signal outgrowing the 7.8 kHz IF, so the optimum on a narrow channel is near 5 kHz, which is not legal on one.

**So take it out of the crest factor instead, where it costs no spectrum.** `PeakToAverageLimitDb` clips each symbol in the time domain, then transforms, zeros every bin the waveform does not own, and transforms back - three passes. The restoration is what keeps the energy out of the adjacent channel, and it undoes part of the clipping, which is why it iterates.

Measured on the same ladder, at legal 2500 Hz deviation throughout:

| limit | achieved crest | +14 | +12 | +10 | +8 | +6 | noiseless loopback |
|---|---|---|---|---|---|---|---|
| none | 12.2 dB | 32 | 31 | 20 | 1 | 0 | all constellations ok |
| 9 dB | 9.7 | 32 | 32 | 32 | 11 | 0 | all ok |
| **7 dB** | **8.2** | 32 | 32 | 32 | **31** | 0 | all ok |
| 6 dB | 7.6 | 32 | 32 | 32 | 31 | 1 | all ok |
| 5 dB | 6.9 | 32 | 32 | 32 | 31 | 3 | **QAM-128 and QAM-256 lost** |

**About 2.5 dB**, and the returns flatten below 7 dB while the distortion does not.

**It is ON by default, and the limit follows the constellation.** An earlier version defaulted it off because a fixed 7 dB limit broke a noiseless UNCODED QAM-256 loopback - which is a configuration nobody will ever transmit, since an FM link cannot carry uncoded QAM-256 at any realistic signal level. Turning off a measured gain on the deployed configuration to protect a test case that will never exist was the wrong call. The right one was the thing that write-up called "the obvious improvement and is not done here", so it is done.

The floor was measured per constellation: the lowest limit at which a noiseless loopback still round trips, uncoded, that being the case with no code to absorb the distortion. BPSK and QPSK 4.0 dB, 8PSK 6.0, QAM-16 7.0, QAM-32 7.5, QAM-64 and denser 9.0; coded, every one tolerates 1 to 4 dB more. The table adds a decibel of margin to each and stays monotonic in density.

**The sync and preamble symbols are clipped too, and leaving them out cost 2 dB.** They were excluded at first on the reasoning that they are the references acquisition and the channel estimate are built on. But with them unclipped the sync symbol becomes the burst's loudest instant, so it alone sets the drive and clipping the payload buys almost nothing: measured, 26 frames of 32 at +10 dB and 2 at +8, against 32 and 31 with them clipped.

Neither reference is harmed, for a reason worth stating. The sync symbol's two halves are identical and clipped identically, so the self-correlation is exactly as strong. And the channel estimate divides the received preamble by the KNOWN one, so clipping is absorbed into the estimate as though it were channel and equalisation applies its inverse to the payload; it very largely cancels. What DOES matter is that the reference limit must follow the burst's own constellation rather than BPSK's floor - the preamble's distortion reaches every payload carrier, and clipping it at 5 dB while the payload carried QAM-32 or denser broke a noiseless loopback outright. So the references are rendered per burst rather than cached.

**Where this leaves the deployed configuration**, acquisition oracle, 48 seeds, a narrow profile, legal 2500 Hz deviation, frames recovered by the real receiver:

| CNR | +14 | +12 | +10 | +8 |
|---|---|---|---|---|
| before peak reduction | 48 | 48 | 26 | 3 |
| after | 48 | 48 | **48** | **47** |

Between 2 and 3 dB, for no bandwidth and no air time.

### Tracking the sample-clock tilt (in)

The one impairment no ladder in this file ever exercised, because the FM link model moves everything except the clocks - so it would have arrived on air first, and it would have arrived looking like a bad link. The two ends keep time with two soundcards, consumer USB audio is routinely tens of ppm off nominal, and two cards 100 ppm apart is an ordinary thing to own. A clock difference slides the receiver's transform window slowly through the burst, which paints a phase ramp ACROSS the carriers; the per-symbol pilot correction takes out only its average, and what remains grows symbol by symbol, worst at the band edges, densest constellations first. The channel estimate absorbs the tilt as it stood at the preamble, so what shows is what accrues over the burst - long bursts and dense rungs pay most.

Measured by resampling the transmitted burst, noiseless, nothing else in the path: a narrow profile, 256 bytes, pre-FEC error rate (context: rate 3/4 gives out at 0.032, 2/3 at 0.053):

| rate | 50 ppm | 100 ppm | 200 ppm |
|---|---|---|---|
| QAM-16 3/4, before | 0.0000 | 0.0011 | 0.0352, past its cliff |
| QAM-64 2/3, before | 0.0000 | 0.0113 | 0.0627, past its cliff |
| QAM-256 2/3, before | 0.0048 | 0.0407 | failed outright |
| all three, after | 0.0000 | 0.0000 | worst 0.0003 |

At 100 ppm and no noise at all, QAM-256 2/3 was spending 77 % of its code on nothing but the tilt - and the spending reads as link margin, so the rate controller would settle low with nothing anywhere naming the reason.

What is in (`SampleClockCorrected`): every pilot of every payload symbol feeds one pooled weighted regression for a tilt that grows linearly per symbol, which is the only shape a constant clock difference can produce, and the fitted drift is rotated out. Three guards, each paid for by a measurement on a paired ladder - each practical rate at its cliff and one dB either side through the data-port model, 32 seeds, frames recovered over the twelve cells:

| receiver | frames |
|---|---|
| no correction at all | 113 |
| drift fitted alone | 84 |
| static tilt fitted as a nuisance beside it, drift shrunk by its own standard error | 94 |
| and the payload CRC arbitrating corrected against plain | 117 |

The drift-only fit loses because the channel estimate's own noise at a pilot is a STATIC angle offset, and through the regression a static tilt projects onto the drift term, so the fit over-rotated the late symbols of perfectly healthy bursts. The static nuisance term and the denoiser-style shrinkage recover most of that and not all of it: a short burst leaves the variance estimate almost no degrees of freedom, and it is sometimes badly wrong. So the corrected read has to PROVE itself: the receiver decodes the corrected symbols, and if their CRC fails, decodes the plain ones. A burst the old receiver copied can therefore never be lost to this, and the four frames over the old baseline are bursts where the correction captured something real. `ClockSkewTests` pins the whole arrangement at 100 and 200 ppm and fails against the uncorrected receiver.

The ladder figures elsewhere in this file were measured before the correction existed; the paired ladder above says a skewless link is unchanged within noise (113 to 117, all of it the CRC arbitration), so they stand.

**And then the noisy measurement, which took some shine off.** M0LTE.FmChannel grew the missing clock-offset knob (`ReceiverClockOffsetPpm`, its 9b94e4e - move the pin when it is released); `ClockSkewLadderProbe` measures the same composition meanwhile by resampling the model's output, which is where the receiving soundcard sits. Frames recovered, 32 seeds, one and three dB above each cliff:

| rate | CNR | 0 ppm | 100 ppm | 200 ppm |
|---|---|---|---|---|
| QPSK 3/4 | +8.4 | 23/32 | 12/32 | 7/32 |
| QPSK 3/4 | +10.4 | 32/32 | 30/32 | 29/32 |
| QAM-16 3/4 | +14.6 | 17/32 | 12/32 | 13/32 |
| QAM-16 3/4 | +16.6 | 32/32 | 32/32 | 30/32 |
| QAM-64 2/3 | +19.6 | 27/32 | 13/32 | 7/32 |
| QAM-64 2/3 | +21.6 | 32/32 | 31/32 | 21/32 |
| QAM-256 2/3 | +23.9 | 17/32 | 3/32 | 5/32 |
| QAM-256 2/3 | +25.9 | 28/32 | 16/32 | 15/32 |

Noiseless, the tracker recovers everything; under noise at one dB above a cliff, an ordinary 100 ppm still costs about half the marginal frames. The mechanism is the fit's own arithmetic: the drift is read from pilot angles, at the cliff pilot angles are mostly noise, and the shrinkage rightly refuses to act on a fit it cannot trust - the CRC arbitration makes that refusal free, but the tilt it refused to correct still lands. Two to three dB up the cost fades, except on QAM-256, whose four payload symbols give the fit almost nothing to pool. The worst regime is a tilt big enough to hurt and too small to fit confidently. Until that changes, on a link whose ends do not share a clock, read the ladder's top rungs as sitting one to two dB above their table cliffs.

**The lever, built.** The header's carriers become KNOWN values the moment its CRC passes: the decoded bits are re-encoded - the decoded BITS, not the parsed fields, because a recommendation that failed to parse re-encodes differently from what the wire carried - and every header carrier becomes a reference as dense as the preamble, one and more symbol periods after it. The fit was generalised to observation groups with each symbol's mean absorbed against its own weights, and a header carrier whose sign disagrees with what was sent is skipped rather than read: its angle is half a turn of pure noise, an outlier rather than a tilt. Same probe, same seeds, with the header feeding the fit:

| rate | CNR | 0 ppm | 100 ppm | 200 ppm |
|---|---|---|---|---|
| QPSK 3/4 | +8.4 | 23/32 | 14/32 | 10/32 |
| QPSK 3/4 | +10.4 | 32/32 | 30/32 | 28/32 |
| QAM-16 3/4 | +14.6 | 18/32 | 10/32 | 10/32 |
| QAM-16 3/4 | +16.6 | 32/32 | 32/32 | 30/32 |
| QAM-64 2/3 | +19.6 | 27/32 | 15/32 | 12/32 |
| QAM-64 2/3 | +21.6 | 32/32 | 31/32 | 29/32 |
| QAM-256 2/3 | +23.9 | 17/32 | 9/32 | 6/32 |
| QAM-256 2/3 | +25.9 | 28/32 | 21/32 | 26/32 |

Against the table above: thirteen more frames over the eight skew cells at 100 ppm and twenty-four more at 200, biggest exactly where the fit was starving - QAM-256 one dB over its cliff goes 3 of 32 to 9, and three dB over from 15 to 26. A skewless link is unchanged within noise (the paired cliff ladder reads 115 against 117, both above the 113 the plain read recovers alone), and the noiseless loopback stays clean to 200 ppm.

With M0LTE.FmChannel 0.7.0 pinned, the probe now takes its skew from the model's own `ReceiverClockOffsetPpm` rather than resampling the output itself - the identical composition, differing only in sub-sample alignment, which re-rolls each seed's noise phasing. Re-measured through the knob the after state reads 184 and 156 over the same eight cells at 100 and 200 ppm, the same shape and a few frames kinder, about two sigma of that re-roll; the knob run is the record future re-runs compare against. The paired before-and-after above was taken with one instrument throughout, which is why it is the one carrying the comparison. What remains is an honest residual: at one dB of margin an ordinary 100 ppm still costs a third to a half of the marginal frames, so two ends on separate clocks should still price the top rungs about a decibel dearer - down from one to two. The next lever, recorded rather than built: once a payload decodes, its carriers are known too, and a second pass could fit the drift from everything - though under the CRC arbitration it could only ever rescue bursts that failed both of today's reads.

## Tried, measured, not in the code

### MMSE equalisation (out)

Divide by `|H|^2 + sigma^2` instead of `|H|^2`. On a waveform that always has carriers 20 dB down this looks like exactly the right medicine. It measured worse: 93 to 92 at the profile's own rate, 86 to 80 rescaled, 24 seeds. The noise estimate was not at fault; it was checked against the model's ground truth and is good to a tenth of a decibel. The MMSE output is biased, by `|H|^2/(|H|^2+sigma^2)`, and the demapper was measuring distance to full-sized reference points. Remove the bias properly and the algebra reduces exactly to zero forcing: for a per-subcarrier OFDM equaliser, unbiased MMSE IS zero forcing. What MMSE really offers a coded system is knowing which carriers to trust, and that is already taken, by weighting each carrier's soft bits by `|H|^2`.

### Per-carrier noise weighting (out)

The receive-side twin of bit loading, and the theory could not look stronger. The soft-bit weight is `|H|^2` with ONE scalar noise figure, and on the deployed R1 tap the scalar is the wrong shape: there is no de-emphasis there, discriminator noise power rises with the square of audio frequency, and measured through the link model at +20 dB the noise under the top occupied carrier sits **18.4 dB above** the noise under the bottom one - per-carrier SNR spreads 19.4 dB across the band. Textbook combining says the weight is `|H|^2` over the noise AT that carrier, and the sync symbol's odd bins hand over the per-carrier measurement for free. It was built - weights `|H|^2 / sigma_c^2`, the estimate smoothed along the band, payload and header both - and on the paired cliff ladder it measured WORSE: 117 frames over the twelve cells became 97, with QAM-64 2/3 a decibel above its cliff falling from 27 of 32 to 17.

The decisive experiment is why this entry is worth having: a three-arm run with an override handing the receiver a flat profile, its own sync-symbol estimate, or the link model's ground-truth per-carrier noise for that very seed - a genie. 32 seeds, one dB above each cliff:

| rate | CNR | flat (shipped) | own estimate | genie |
|---|---|---|---|---|
| QPSK 3/4 | +8.4 | 23/32 | 15/32 | 17/32 |
| QAM-16 3/4 | +14.6 | 17/32 | 17/32 | 18/32 |
| QAM-64 2/3 | +19.6 | 27/32 | 17/32 | 18/32 |
| QAM-256 2/3 | +23.9 | 17/32 | 14/32 | 10/32 |

**The genie loses too.** Perfect knowledge of the per-carrier noise still costs frames, so the weighting MODEL is wrong for this channel, not the estimate feeding it. The plausible mechanism, flagged as hypothesis rather than measurement: near a working point an FM discriminator's failures are not stationary Gaussian noise per carrier - they are clicks and signal-times-noise products, time-bunched and broadband - and a weight that says "this carrier is quiet, trust it hard" turns one click on a trusted carrier into a confidently wrong metric that steers the Viterbi decoder off the path. Under heavy-tailed noise the robust weight is far flatter than `1/sigma^2`, and `|H|^2` alone is evidently close enough. A tempered exponent between the two was not tried.

It is the MMSE lesson one layer up, and the two entries should be read together: knowing which carriers to trust is already taken by the channel-power weight, and sharpening that knowledge past what the noise's tails support buys a negative number. The 18.4 dB tilt is real and stays measured; what failed is the assumption that it maps onto per-carrier confidence.

### Decision-directed second pass (out)

When a payload fails its CRC, slice every equalised carrier of every symbol to its nearest constellation point, average the residual over all of them, fold that into the estimate and decode again. The pilots come free, being known exactly. In principle this beats the single-symbol preamble estimate by roughly the square root of the number of symbols.

It won **0 of about 30 attempts**, across the plain ladder and across a flutter ladder at 2, 5, 10 and 20 Hz Doppler. Not "a little"; none.

The reason is visible in the failure budget below. By the time a payload fails, it is not marginally wrong, and slicing then gives a reference too corrupt to improve anything. The cases where a better estimate would have helped are already being caught by the denoiser, which is cheap, runs on every burst, and needs no second decode.

Do not rebuild this without first checking the failure budget still looks like the table below. If payload failures ever come to dominate, it is worth another look, ideally re-encoding the decoder's output rather than slicing raw, so the coding gain is in the reference.

### Lowering the sync threshold (out)

The sync search accepts a normalised correlation of 0.8 or better. At +16 dB the worst score over 8 seeds was 0.695 at rate 3/4 against 0.847 at 2/3, and the whole 3/4 rung was failing acquisition, which made the threshold look like the binding constraint. It is not. Sweeping it to 0.6, 0.45 and 0.3 changes the ladder in no cell at all: acquisition then succeeds and the payload fails instead. The threshold was masking the failure, not causing it, and a lower one only buys more false acquisitions to pay for. Left at 0.8.

## Where the frames actually go

Rate 1/2, 16 seeds, with flat Rayleigh flutter on the RF carrier. Every failure is counted once, in the stage that lost it.

| Doppler | CNR | recovered | no sync | bad header | bad payload |
|---|---|---|---|---|---|
| 2 Hz | +20 | 10/16 | 3 | 1 | 2 |
| 5 Hz | +20 | 11/16 | 2 | 1 | 2 |
| 10 Hz | +20 | 7/16 | 3 | 2 | 4 |
| 20 Hz | +24 | 8/16 | 2 | 2 | 4 |
| 20 Hz | +20 | 2/16 | 4 | 7 | 3 |

**This table is the state BEFORE the header was coded** and is kept because it is what identified the problem: the header was 7 of 14 lost frames in the worst row, the largest single bucket, and it was the one part of the burst with no coding on it at all. It has since been fixed (see "Coding the header, across two symbols" above), and the same measurement now reads 3 of 8 lost frames in that row with 8 of 16 recovered against 2 of 16.

**Sync is now the largest bucket** and is the next thing worth attention: 15 lost frames against the header's 6 over the twelve cells. The current detector correlates the two halves of one sync symbol and takes the best position above a fixed threshold. There is a whole preamble symbol behind it carrying known values, and nothing uses it for timing at all; and the threshold sweep recorded above showed that acquisition failures at the bottom rung were masking payload failures rather than causing them, so any work here should re-check that first.

## The rate ladder, and which rates are never worth choosing

Signalling the rate raises the question of which to pick, and until this measurement nobody could answer it. There are 24 rates a header can name if you count the eight constellations against the three code rates. Filling that grid says that **eleven of them are never the right answer on any link**, which matters: an adaptation scheme that can select a dominated rate will eventually select one, and it will be slower AND less robust than something else it could have sent instead.

Measured with `RateLadderGridProbe`: a narrow profile, R1/T13 narrow at 2500 Hz peak deviation, a 256-byte payload, K=9, 64 seeds at the fine step. "Threshold" is the carrier-to-noise ratio at which half the frames land, interpolated across a 1 dB step. "Goodput" is payload bits per second of transmission with every frame landing, which is what a shared channel pays.

| rate | burst | goodput | threshold | step from the one below |
|---|---|---|---|---|
| BPSK 1/2 | 1904 ms | 1076 bit/s | +5.8 dB | |
| BPSK 2/3 | 1496 ms | 1369 bit/s | +5.9 dB | +0.1 dB for +27 % |
| BPSK 3/4 | 1360 ms | 1506 bit/s | +6.0 dB | +0.1 dB for +10 % |
| QPSK 1/2 | 1043 ms | 1964 bit/s | +6.3 dB | +0.3 dB for +30 % |
| QPSK 2/3 | 861 ms | 2378 bit/s | +6.9 dB | +0.6 dB for +21 % |
| QPSK 3/4 | 771 ms | 2657 bit/s | +7.4 dB | +0.5 dB for +12 % |
| QAM-16 1/2 | 635 ms | 3227 bit/s | +10.3 dB | +2.9 dB for +21 % |
| 8PSK 3/4 | 589 ms | 3475 bit/s | +12.1 dB | +1.8 dB for +8 % |
| QAM-16 2/3 | 544 ms | 3765 bit/s | +12.7 dB | +0.6 dB for +8 % |
| QAM-16 3/4 | 499 ms | 4107 bit/s | +13.6 dB | +0.9 dB for +9 % |
| QAM-32 2/3 | 453 ms | 4518 bit/s | +16.1 dB | +2.5 dB for +10 % |
| QAM-64 2/3 | 408 ms | 5020 bit/s | +18.6 dB | +2.5 dB for +11 % |
| QAM-256 2/3 | 363 ms | 5647 bit/s | +22.9 dB | +4.3 dB for +12 % |

Seventeen decibels of link for 5.2 times the throughput. The eleven that did not make it, each beaten by something on the list that is at least as fast and needs less signal:

| rate | | beaten by |
|---|---|---|
| 8PSK 1/2 | 2657 bit/s at +9.0 | QPSK 3/4, same speed at +7.4 |
| 8PSK 2/3 | 3227 at +10.9 | QAM-16 1/2, same speed at +10.3 |
| QAM-32 1/2 | 3765 at +13.6 | QAM-16 2/3, same speed at +12.7 |
| QAM-64 1/2 | 4107 at +15.5 | QAM-16 3/4, same speed at +13.6 |
| QAM-32 3/4 | 4518 at +18.0 | QAM-32 2/3, same speed at +16.1 |
| QAM-256 1/2 | 5020 at +19.0 | QAM-64 2/3, same speed at +18.6 |
| QAM-128 1/2 | 4518 at +19.1 | QAM-32 2/3, same speed at +16.1 |
| QAM-64 3/4 | 5020 at +20.0 | QAM-64 2/3, same speed at +18.6 |
| QAM-128 2/3 | 5020 at +22.1 | QAM-64 2/3, same speed at +18.6 |
| QAM-128 3/4 | 5647 at +23.3 | QAM-256 2/3, same speed at +22.9 |
| QAM-256 3/4 | 5647 at +25.0 | QAM-256 2/3, same speed at +22.9 |

What it says, beyond the ordering:

- **QAM-128 is dead on this waveform, at all three code rates.** A burst is a whole number of symbols, so seven bits per carrier usually rounds up to the same symbol count as eight; it pays QAM-256's noise penalty and gets QAM-64's air time.
- **Rate 2/3 owns the top of the ladder.** Every rung above QAM-16 3/4 is a 2/3 code. Rate 3/4 appears only at the bottom, where the code is not what is limiting things, and rate 1/2 appears only at the bottom and at QAM-16 1/2. A ladder built on one code rate with the constellation varying, which is the obvious design, would miss most of this.
- **Loosen the code before densifying the constellation, while you still can.** From QPSK 1/2, going 35 % faster costs 1.1 dB by the code (QPSK 3/4) and 2.7 dB by the constellation (8PSK 1/2). That is why 8PSK barely survives: by the time it is worth reaching for, QPSK 3/4 has already taken the slot.
- **The BPSK rungs are fade-limited, not noise-limited, and only the fastest is worth keeping.** All three sit within 0.2 dB of each other while differing by 40 % in speed, which is what it looks like when the code rate has stopped mattering and a dropout somewhere in a 1.4 to 1.9 second transmission is what loses the frame. BPSK 1/2 reaches 0.5 dB below QPSK 1/2 for 45 % less throughput; it is not dominated, but it is poor value and belongs at the bottom of the ladder as a last resort rather than as a step.
- **There is a real gear gap at +7 to +10 dB.** QPSK 3/4 to QAM-16 1/2 is 2.9 dB for 21 %, the worst trade on the list. Nothing fills it, because that is where the constellation has to change.

A workable seven-rung ladder, roughly 2 to 4 dB apart, which is the spacing an adaptation scheme wants if it is not going to oscillate: BPSK 3/4, QPSK 2/3, QPSK 3/4, QAM-16 1/2, QAM-16 3/4, QAM-64 2/3, QAM-256 2/3.

**Caveats, all of them load-bearing.** One geometry, one deviation, one tap, one payload size, in simulation. Payload size in particular moves the answer: at 64 bytes the symbol rounding merges whole groups of rungs and the ordering above does not hold. Nothing here has been on air.

## A margin measure: how much of the code a burst spent

A rate decision needs to know how close to the edge the link is, and "the CRC passed" does not answer that. It is one bit; it reads the same at 3 dB of margin as at 0.2; and by the time it changes, the decision that should have been taken is already late.

`OfdmFmBurst.PreFecBitErrorRate` is what replaces it. Re-encode what the Viterbi decoder decided, compare against the demodulator's own hard decisions, count the differences: the fraction of coded bits that were wrong before the code fixed them, which is a direct read of how much of the code's correcting power the burst spent. It costs one encode, a shift register over a few thousand bits against a decode that has just walked the same length with 256 states, so nothing measurable. Null on an uncoded burst, where re-encoding just reproduces the demodulator and the answer would be a constant zero rather than a measurement.

Measured on the practical ladder, R1/T13 narrow at 2500 Hz, K=9, 256 bytes, 64 seeds, quoting the median over the bursts that decoded (mean would track the tail of nearly-failed bursts rather than the typical one), against carrier-to-noise relative to each rate's own cliff:

| rate | +8 | +6 | +4 | +3 | +2 | +1 | 0 | -1 dB |
|---|---|---|---|---|---|---|---|---|
| BPSK 3/4 | 0.0000 | 0.0000 | 0.0004 | 0.0015 | 0.0033 | 0.0080 | 0.0229 | 0.0422 |
| QPSK 2/3 | 0.0006 | 0.0019 | 0.0061 | 0.0110 | 0.0171 | 0.0294 | 0.0439 | 0.0749 |
| QPSK 3/4 | 0.0004 | 0.0018 | 0.0058 | 0.0102 | 0.0167 | 0.0258 | 0.0389 | - |
| QAM-16 1/2 | 0.0075 | 0.0191 | 0.0371 | 0.0501 | 0.0652 | 0.0821 | 0.0947 | 0.1027 |
| QAM-16 3/4 | 0.0007 | 0.0022 | 0.0076 | 0.0124 | 0.0185 | 0.0265 | 0.0360 | 0.0443 |
| QAM-64 2/3 | 0.0023 | 0.0065 | 0.0161 | 0.0229 | 0.0313 | 0.0407 | 0.0510 | 0.0581 |
| QAM-256 2/3 | 0.0058 | 0.0129 | 0.0255 | 0.0320 | 0.0413 | 0.0504 | 0.0594 | 0.0669 |

**It rises smoothly and monotonically on every rate**, which is the first thing it had to do.

**The value at the cliff is a property of the code rate, not of the constellation.** Interpolating each row to where half its frames land:

| code rate | cliff pre-FEC error rate | measured on |
|---|---|---|
| 1/2 | 0.091 | QAM-16 |
| 2/3 | 0.053 | QPSK 0.046, QAM-64 0.054, QAM-256 0.060 |
| 3/4 | 0.032 | QPSK 0.033, QAM-16 0.039, BPSK 0.022 |

That is the useful result. It means a station needs **three numbers, not thirteen**, and it is what you would expect physically: how many raw errors a burst can absorb is set by the code's correcting power, and that belongs to the code. The one visible outlier is BPSK 3/4 at 0.022 against the other 3/4 rates near 0.036, and it fits the other finding about that rung: a 1.4 second burst is fade-limited, so its errors arrive bunched in a dropout rather than scattered, and a burst of errors is harder for a convolutional code than the same number spread out.

**What is NOT recoverable from the error rate alone is the distance to the cliff in decibels.** The slope differs by constellation: at +4 dB, QPSK 2/3 reads 0.0061 while QAM-256 2/3 reads 0.0255, four times as much at the same margin on the same code. So "am I near the edge" is one number per code rate, and "how far above the edge am I, in dB" needs the rate's own row.

**And there is a measurement floor that shapes the policy.** A 256-byte burst carries 2752 to 4128 coded bits depending on rate, so an error rate is a count of a few thousand trials:

| pre-FEC error rate | errors in one burst | precision of a single-burst estimate |
|---|---|---|
| 0.05 (at the cliff) | 140 to 210 | about +/-8 % |
| 0.02 | 55 to 85 | about +/-13 % |
| 0.005 | 14 to 21 | about +/-25 % |
| 0.0015 (about 3 dB spare) | 4 to 6 | about +/-45 % |

So the measure is sharp exactly where a station is in trouble and vague exactly where it has room. That is convenient rather than awkward, because it says the same thing the risk argument does, for an independent reason: **step down on one burst, step up only on an average of several.** Guessing down wrongly costs throughput; guessing up wrongly costs a retransmission; and only the downward decision is one a single burst can actually support.

## Closing the loop: what adaptation is actually worth

The ladder, the margin measure and the step policy were each measured on their own. None of that says the loop works: a controller can be right about every burst and still settle on the wrong rung, or find the right one and spend so long probing past it that it delivers less than sitting still would have.

`NegotiationLoopProbe` runs one direction of a link at a fixed carrier-to-noise ratio. The transmitter sends at whatever the receiver last asked for, the receiver measures what arrived and asks for something else, and the probe counts what got through. The comparison is against an **oracle**: every fixed rate over the same link and the same seeds, best taken afterwards. That is a station that knew the answer in advance and never had to find it, so adaptation cannot beat it, and the gap is the price of not knowing. Air time counts for failed bursts too, because a probe that overreaches costs the frame and the time it spent losing it.

R1/T13 narrow at 2500 Hz, 256-byte payload, 200 bursts a cell:

| CNR | adapting | best fixed | reached | settles on |
|---|---|---|---|---|
| +8 | 1968 bit/s | 2211 (QPSK 2/3) | 89 % | QPSK 2/3 |
| +12 | 2746 | 3130 (QAM-16 1/2) | 88 % | QPSK 3/4 |
| +16 | 3777 | 4066 (QAM-16 3/4) | 93 % | QAM-16 3/4 |
| +20 | 3960 | 4568 (QAM-64 2/3) | 87 % | QAM-16 3/4 |
| +24 | 4554 | 5020 (QAM-64 2/3) | 91 % | QAM-64 2/3 |

87 to 93 % of a station that already knew the answer, settling on the right rung at three of the five and one below at the other two. The residual is estimator variance: a median over a handful of bursts sometimes reads a working rung as too dear, the controller drops, and it costs a few bursts to climb back.

**Three things were tried on the way and two of them were wrong.** Recorded because each looked obviously right beforehand.

*Requiring a run of consecutive clean bursts to step up.* Wrong, and worth about nothing when fixed in isolation: a run rule needs every burst in the run to clear the bar, so on a link whose typical burst sits at the bar it almost never fires. Changed to the median of a window, which is what "several bursts" was always meant to say. That alone moved 78 % to 81 %.

*One slope for how fast the pre-FEC error rate falls with signal.* Wrong. A single fitted 0.7 per dB reads anything from 2.4 dB to 11.3 dB where the truth is 4.0, because the real slope runs from 0.36 on BPSK to 0.81 on QAM-256: a dense constellation's decision regions shrink slowly against extra signal, so its error rate falls slowly, while BPSK falls off a cliff. Now measured per rate. Also worth only a point or two on its own.

*Stepping down whenever a single successful burst looked expensive.* **This was the real fault, and neither of the other two mattered much until it was fixed.** Instrumented at +16 dB it retreated 14 times over 200 bursts, every single one on a burst that had decoded perfectly and not one on an actual failure. A burst that decoded is proof the rate works, whatever it spent getting there.

What replaced it is the piece worth keeping: **choose the rung with the best expected throughput, not the best margin.** From the median spending on the current rung, work out the margin; translate that to every other rung through the difference in their cliffs and each rate's own slope; turn each one's implied spending into an expected frame success and multiply by its goodput. A rate losing one frame in seven at 4107 bit/s is well ahead of one losing none at 3227, and only an arithmetic that puts throughput and reliability in the same units can see that. Deciding on margin alone always picks the safest rate, which is exactly the failure the first version had.

The remaining knobs, and what they cost when moved: judging a rate on 4 bursts rather than 2 drops 89 % to 78 % at the bottom of the ladder, because a rate that has genuinely stopped paying costs throughput for every burst it is not left. Stepping down late is not free just because stepping down wrongly is expensive.

**Caveats.** One geometry, one deviation, one tap, one payload size, a static link, simulation only. A real path changes while the controller is deciding, and nothing here has been on air.

## A transmitter identifier in the header: built, measured, backed out

The rate controller is one per channel, which is right for a point-to-point link and wrong for a shared one: with several stations on frequency their reports average into a single recommendation that suits none of them. That gap is real and this was the attempted fix. **It is not in the code and should not be rebuilt in this form.**

What was built: two 12-bit identifiers in the header, arbitrary and generated, saying who sent the burst and who its rate recommendation was for. A receiver kept a controller per correspondent, dropped one that went quiet, refused its own bursts, and ignored advice addressed to somebody else. It worked. It was backed out anyway, for three reasons.

**It is a second namespace for something that already has a name.** An operator reading a log wants "GB7RDG-1 is asking for QPSK 2/3", not "peer 2317". The number cannot be looked up, collides by birthday, and has to be explained to everyone who ever meets it.

**It costs a header symbol on the narrowest profile, permanently.** A header at rate 1/2 fits half a profile's data carriers as bits in one symbol, and against a header of 48 bits the narrowest profile deployed at the time had a little room and the wider ones a great deal. Twelve bits each takes the header to 72: the wider profiles still fit one symbol and the narrowest needs two, which is a whole extra symbol a burst, 3 % of a slow one and 12 % of the fastest. Measured against the oracle, the closed loop went from 89, 88, 93, 87, 91 % at +8 to +24 dB to **78, 86, 91, 87, 92 %**. The loss is all at the weak end, where bursts are already over a second long and fade-limited, so one more symbol is one more chance of a dropout catching it. The cost is also binary rather than graded: on the narrowest profile, *any* identity at all needs the second symbol.

**And it fixes half the problem.** It gives exact receive binning and does nothing for the transmit side, because the modem still cannot choose a rate per destination: the payload is an opaque AX.25 frame and this layer never reads an address. That residue is the tell that the design sits at the wrong layer. It invents an identity at a layer with no business knowing about correspondents, in order to serve a decision that needs the destination address the same layer has just refused to read.

**What to do instead, when this is picked up again.** Push attribution up rather than inventing identity down: report the per-burst measurement to the host and take a rate from it per frame. The host has a callsign to name things with, and - importantly - it does not have to key on the frame's source address either, since a node knows from its own link state which neighbour a frame arrived over. That is exactly the knowledge AX.25's address field fails to carry (packethacking/ax25spec#80), and the host has it without needing the spec to change. It costs no air time, fixes both halves, and the diagnostics are readable.

If a physical key is ever wanted as a cross-check, the channel estimate and the residual frequency offset are a fingerprint of the transmitter and its path, and they cost nothing on the wire.

Two things the exercise did leave behind, worth keeping in mind. A station has to refuse bursts that appear to come from itself, because a repeater loop or a monitoring path can make it its own correspondent. And several tests were modelling a link by feeding a modem its own modulated audio, which was harmless while a burst had no idea who sent it and became meaningless the moment it did; if a future design gives bursts an identity, those tests have to model two stations.

**The header line this settles.** It carries what a receiver needs to demodulate the burst in front of it, and the rate recommendation, which fits the spare bits of a symbol it already sends. Identity and addressing are facts about a link rather than about a burst, and belong wherever links are understood.

## The code family, measured at last

Every code in this repository was convolutional because 802.11a's was, and the README carried that as the one sizeable decision taken without a table. The table exists now. M0LTE.FecLdpc holds the FreeDV datac family's rate-1/2 repeat-accumulate LDPC codes with soft sum-product decoding, and the payloads were sized to make the comparison exact: a framed 126-byte payload is precisely H_1024_2048_4f's 1024 data bits and 510 bytes precisely H_4096_8192_3d's 4096, so both arms produce identical coded-bit counts, identical symbol counts and identical air time, and whatever separates them is the code and nothing else.

One instrument note, because it would have poisoned the comparison silently. Sum-product is the one decoder in this chain that reads the LLRs' ABSOLUTE scale as meaning something - max-log Viterbi is scale invariant and the demapper leans on that - so the LDPC path normalises each block to a mean magnitude near what a burst at its cliff truly carries, and the probe swept the target before trusting anything: at 1.0 the decoder converges on nothing at all, 2.5 recovers 26 of 32, 6.0 and 15.0 fall away as overconfidence sets in. A fixed 2.5 was shipped first, and an A/B run against a mis-scaled decoder would have measured the scale, not the code. The rung calibration below then showed why a constant cannot serve the family, and the scale now derives from each burst's own noise measurement.

`LdpcAbProbe`: a narrow profile, DataPort at 2500 Hz, K=9 rate 1/2 against LDPC rate 1/2, 32 seeds, frames recovered:

| 126 B QPSK | +4.3 | +5.3 | +6.3 | +7.3 | +8.3 |
|---|---|---|---|---|---|
| conv | 0/32 | 1/32 | 17/32 | 30/32 | 32/32 |
| ldpc | 0/32 | 0/32 | 18/32 | 31/32 | 32/32 |

| 126 B QAM-16 | +8.3 | +9.3 | +10.3 | +11.3 | +12.3 |
|---|---|---|---|---|---|
| conv | 0/32 | 2/32 | 13/32 | 24/32 | 31/32 |
| ldpc | 1/32 | 11/32 | 26/32 | 31/32 | 32/32 |

| 510 B QAM-16 | +8.3 | +9.3 | +10.3 | +11.3 | +12.3 |
|---|---|---|---|---|---|
| conv | 0/32 | 0/32 | 0/32 | 19/32 | 28/32 |
| ldpc | 0/32 | 1/32 | 16/32 | 30/32 | 32/32 |

**Above the FM threshold, the literature's promise holds.** At QAM-16 with a 1024-bit block the LDPC 50 % point sits about 0.7 dB below the convolutional one, and with the 4096-bit workhorse block 1.0 to 1.3 dB - at +10.3, where the convolutional arm copies nothing, LDPC copies half. The gain grows with block length, which is LDPC behaving exactly as advertised.

**At the click-dominated bottom it washes out.** QPSK 1/2's cliff sits below the FM threshold, where the discriminator hands up impulses rather than noise, and the two families tie to within a frame or two. That is the same lesson the per-carrier noise weighting taught: what decides things down there is not Gaussian, and refinements calibrated for Gaussian noise stop paying. The two entries deserve reading together before anyone extrapolates either.

**So LDPC is signallable and stays** - coding id 7, one id for the family, since which mother codes a payload lands on follows from its length identically at both ends. A sum-product decode costs more CPU than a Viterbi walk, bounded at 100 iterations, which a receive thread should know.

### The rung calibration, and what it caught first

Measuring what an LDPC rung would carry on the ladder's own 256-byte grid caught a defect the A/B could not see. With the fixed LLR normalisation, QAM-64 recovered nothing until 4 dB past the convolutional cliff and QAM-256 nothing in the swept range at all - while both round tripped a noiseless loopback cleanly, which said calibration rather than bug. Swept directly at QAM-64 one dB over its eventual cliff: a mean-magnitude target of 2.5 recovers 0 of 16, and 5.0 recovers 16 of 16. The fixed target that had been swept to its optimum on QPSK and QAM-16 was four decibels of underconfidence at QAM-64, because a dense constellation's mean max-log distance shrinks while its true likelihoods do not shrink with it.

The scale is now derived per burst instead of fixed: mean channel power over the sync symbol's free noise measurement, with the demapper's own scaling folded out, which is just "distance over noise" - what a likelihood is. That lands every constellation where its own sweep would have put it (QAM-64 reads 15 of 16 at the cell the fixed target read 0). The trade, measured by re-running the A/B under the derived scale: the QAM-16 and 510-byte results hold, and QPSK gives a little back (its cliff cell reads 11 of 32 against 18 before, now slightly behind convolutional's 17) - below the FM threshold the noise is clicks, true-likelihood scaling is overconfident about them, and the fixed target had been accidentally robust there. The family's scale cannot be a constant, so the derived scale is what ships, and its cost lives only where LDPC was not winning anyway.

`LdpcLadderProbe`, derived scale, 48 seeds, 256 bytes:

| LDPC 1/2 at | cliff | conv 1/2 cliff | cliff spending | spending slope |
|---|---|---|---|---|
| QPSK | +6.7 | +6.3 | 0.081 | 0.65/dB |
| QAM-16 | +10.7 | +10.3 | 0.102 | 0.80/dB |
| QAM-64 | +16.0 | +15.5 | 0.112 | 0.83/dB |
| QAM-256 | +19.3 | +19.0 | 0.121 | 0.87/dB |

**No rung changes, and the reason is the payload, not the code.** At 256 bytes a burst is two full 1024-bit codewords and a 16-bit tail, each decoded independently, and a burst only copies if ALL of them do - which prices the joint cliff 0.3 to 0.5 dB above the single-codeword one. The convolutional code spans the whole burst as one word and pays no such tax. So at the grid the ladder is calibrated for, every LDPC 1/2 sits behind its convolutional sibling, while at payloads that fill one mother code (126 bytes exactly fills K=1024, 510 exactly K=4096) LDPC wins by 0.6 to 1.3 dB. The grid's own caveat - payload size moves the answer - now has teeth. `CliffPreFecErrorRate` carries 0.10 for the family, the measured figure, for the margin readout only. What would earn LDPC rungs, recorded rather than built: a mother code sized so a 256-byte burst is ONE codeword (codec2's wider family has candidates this package does not carry), a ladder calibrated at the payload sizes long transfers actually use, or joint decoding across a burst's frames. One geometry, a static link, simulation only.

### Bulk payload sizes: the codeword tax does not keep compounding

The ladder calibration above split a 256-byte burst into two codewords; at 1024, 1900 and 3000 bytes - the range "How fast is it" below calls out as what bulk transfers actually use - a burst is 3, 10 and 12 codewords. `LdpcBulkSizeProbe` measured LDPC 1/2 against K=9 1/2 at QAM-16 and QAM-64 across that range, with K=7 2/3 (the 8 kHz preset's code) alongside as a reference point, same link model and pass criterion as the two probes above, synthetic profile only - no local overrides, no geometry numbers. 16 seeds per point rather than 32: the full 18-row sweep already ran 17.5 minutes at that count, each point found by a cheap coarse search before the real sweep rather than a fixed window, and doubling the seed count was not worth the session it would have cost.

`LdpcBulkSizeProbe`, synthetic profile, DataPort 2500 Hz, 16 seeds per point, 50% point interpolated across the 1 dB step that crosses it:

| payload | constellation | LDPC codewords | LDPC 1/2 | K=9 1/2 | K=7 2/3 | LDPC gain over K=9 1/2 |
|---|---|---|---|---|---|---|
| 1024 B | QAM-16 | 3  | +10.0 | +11.6 | +13.6 | 1.6 dB |
| 1024 B | QAM-64 | 3  | +15.6 | +16.2 | +20.6 | 0.6 dB |
| 1900 B | QAM-16 | 10 | +10.9 | +11.4 | +14.3 | 0.5 dB |
| 1900 B | QAM-64 | 10 | +16.0 | +17.0 | +20.8 | 1.0 dB |
| 3000 B | QAM-16 | 12 | +10.8 | +11.5 | +14.6 | 0.7 dB |
| 3000 B | QAM-64 | 12 | +16.0 | +17.6 | +21.3 | 1.6 dB |

**The per-codeword gain survives at bulk sizes.** LDPC 1/2 beats K=9 1/2 at every row measured, by 0.5 to 1.6 dB, even where a burst is ten or twelve codewords that must all decode together. The tax that flipped the sign at 256 bytes does not keep compounding as codewords pile up: going from 3 codewords at 1024 bytes to 12 at 3000 bytes moved the margin around within that same 0.5 to 1.6 dB band rather than eroding it further, probably because those extra codewords are mostly full 4096-bit frames with their own strong per-codeword gain, not the weak short mother codes a 256-byte burst is built from. Both LDPC and K=9 1/2 stay well ahead of K=7 2/3 at every size and constellation measured, by 2.0 to 5.3 dB. So a rate-1/2 LDPC rung would be a genuine win at the frame sizes bulk transfers use, not only at the two sizes that fill one mother code exactly - and a station running the 8 kHz preset's K=7 2/3 for speed is leaving several decibels on the table at any of these sizes, LDPC or not. 16 seeds and a coarse-then-fine sweep make this a rougher read than the 32-seed A/B and ladder probes above; it settles the shape of the answer, not its last decimal.

## Punctured rates 5/6 and 7/8, and the K=7 rungs at QAM-64

The measured 8 kHz preset spends a third of its raw rate on a 2/3 code over a link that reads 24 to 28 dB per carrier, which is what prompted the two standard punctured rates above 3/4 on the K=7 code. They are a table entry and two coding ids. 5/6 is 802.11n's period-5 pattern and 7/8 is DVB-S's period-7 pattern (EN 300 421 table 3), both on the (133,171) mother code with A the 0o133 output and B the 0o171 output as 802.11 labels them; DVB-S lists the 0o171 output first, so its 7/8 rows are exchanged here to keep the pattern on the polynomial it was published for. Coding ids 8 and 9 name them, for K=7 only: ids 0 to 7 are untouched and 10 to 15 stay reserved for the LDPC families. Rates 1/2, 2/3 and 3/4 code exactly what they did before, which `PuncturedRateTests` proves against output captured from the encoder at 39b43ac before the table was touched.

**Which way round each pattern's rows go was measured, not assumed.** Exchanging the rows gives another valid code, and the two are not always equal. On the mother code alone, 700-bit tail-biting blocks, antipodal metrics on Gaussian noise at a coded-bit signal-to-noise ratio of 4.0 dB, 600 blocks: 802.11n's 5/6 lost 5 blocks and its mirror 24; DVB-S's 7/8 as published lost 49 and its mirror 55, and at 5.0 dB 5 against 9. The published orientations ship.

**The calibration, re-run whole on one instrument (2026-09-20).** `PuncturedRungProbe` is `RateLadderGridProbe`'s cliff method and `MarginMeasureProbe`'s spending method in one instrument, the way `LdpcLadderProbe` combined them: R1/T13 narrow at 2500 Hz, a 256-byte payload fresh per seed, 16 seeds on a 2 dB grid walking down to bracket the cliff, then 64 seeds at 1 dB from 3 dB under the bracket to 6 over; the cliff interpolated across the step where half the frames are lost, the spending at the cliff interpolated the same way, the slope fitted log-linearly over the 4 dB above the cliff. Burst length is the transmitter's, goodput is 2048 payload bits over it, spending is the median pre-FEC error rate of the bursts that decoded.

The first run, on 2026-09-19, put the four K=7 rungs through this instrument but left the seven K=9 rungs on the figures an earlier narrow TM8100 profile gave them (a layout this repository does not carry), because re-measuring those was out of scope that day. That run read the ladder's own QAM-64 K=9 2/3 rung 1.2 dB under its stored cliff, so the K=7 figures went onto the ladder tied to that rung by an offset rather than used raw, and the subsection said the next step was to re-run the whole ladder on one instrument. This is that run: all eleven rungs, plus the same two reference cells as before, on the synthetic profile, all raw:

| cell | burst | goodput | cliff | spending at cliff | slope | on the ladder |
|---|---|---|---|---|---|---|
| BPSK K9 3/4, rung 0 | 1666 ms | 1229 bit/s | +6.3 | 0.013 | 0.390/dB | yes |
| QPSK K9 2/3, rung 1 | 986 | 2077 | +6.7 | 0.038 | 0.519 | yes |
| QPSK K9 3/4, rung 2 | 884 | 2317 | +7.1 | 0.030 | 0.563 | yes |
| QAM-16 K9 1/2, rung 3 | 691 | 2962 | +9.7 | 0.090 | 0.803 | yes |
| QAM-16 K9 3/4, rung 4 | 499 | 4107 | +13.0 | 0.035 | 0.651 | yes |
| QAM-64 K9 2/3, rung 5 | 397 | 5163 | +17.4 | 0.057 | 0.768 | yes |
| QAM-256 K9 2/3, rung 6 | 329 | 6231 | +22.1 | 0.061 | 0.800 | yes |
| QAM-16 K7 5/6 | 453 | 4518 | +14.8 | 0.018 | 0.647 | not a rung |
| QAM-64 K9 3/4 | 363 | 5647 | +19.2 | 0.038 | 0.715 | dominated on the narrow grid |
| QAM-64 K7 2/3 | 397 | 5163 | +18.3 | 0.049 | 0.744 | yes |
| QAM-64 K7 3/4 | 363 | 5647 | +19.7 | 0.032 | 0.712 | yes |
| QAM-64 K7 5/6 | 340 | 6024 | +21.6 | 0.017 | 0.643 | yes |
| QAM-64 K7 7/8 | 329 | 6231 | +22.2 | 0.014 | 0.545 | yes |

**The offset transfer is gone.** `OfdmFmRateLadder` now stores this table's cliff, goodput and slope for every rung, unscaled and untied to a neighbour: the K=9 rungs as much as the K=7 ones, all read straight off one run on one instrument. No rung changed place - the eleven stay in the cliff order they were already in, K=9 up to rung 5, then K7 2/3, K7 3/4, K7 5/6, QAM-256 K9 2/3, K7 7/8 - because that order already matched this run's raw cliffs before the offset was removed.

**The top of the ladder is a near-tie, not a margin.** QAM-256 K9 2/3 reads +22.1 dB for 6231 bit/s; QAM-64 K7 7/8 reads +22.2 dB for the same 6231 bit/s, because at 256 bytes the two round to the same 329 ms burst on this instrument. A tenth of a decibel is inside the resolution 64 seeds gives a cliff, so K7 7/8 is kept where it is rather than read as dominated, but it is not read as a real gain over QAM-256 2/3 at this payload size either. A run at a payload size that does not tie the two bursts to the same length, or a link that has been on air, is what would settle it.

**Reproducibility, checked rather than assumed.** Re-running the cells the first pass also measured, a day apart on the same build, mostly reproduced them exactly: the four K=7 cliffs (+18.3, +19.7, +21.6, +22.2) came back bit-for-bit the same, and so did most of the K=9 ones. Two cliffs moved, both within the decibel the probe claims for itself at 64 seeds: QPSK K9 3/4 from +7.2 to +7.1, and QAM-256 K9 2/3 from +21.9 to +22.1. Slopes moved more in a few places - QAM-256 K9 2/3 from 0.829 to 0.800, and QAM-64 K7 7/8 from 0.586 to 0.545 - because the log-linear fit runs over only the 4 dB above a cliff, and a cliff sitting a tenth of a decibel from its neighbour (see above) has few points in that window before it either runs into the next rung or the seed noise dominates the fit.

**What the run says beyond the figures.**

- K=7 costs 0.9 dB at 2/3 and 0.5 dB at 3/4 against K=9 at QAM-64, for the same burst, and decodes with a quarter of the states.
- Spending at the cliff is the rate's, as the first run also found: 5/6 reads 0.018 at QAM-16 and 0.017 at QAM-64, 7/8 0.014; K7 3/4 reads 0.032 and K7 2/3 0.049 beside the ladder's separately-measured 0.032 and 0.053 for those rates (today's K=9 cells read 0.035 and 0.038 at 3/4 and 0.057 at 2/3), so the constraint length still does not move it. `CliffPreFecErrorRate` keeps 0.018 for 5/6 and 0.014 for 7/8, cross-checked again rather than changed.
- The slope keeps falling as the code loosens at QAM-64: 0.744, 0.712, 0.643 and 0.545 from 2/3 to 7/8, so a margin read on 7/8 is the least sharp of any rung, more so than the first run's 0.586 suggested.
- QAM-16 K7 5/6 was measured again for the record and still not added: +14.8 dB against QAM-16 3/4's +13.0 and QAM-64 K9 2/3's +17.4. The ladder has no QAM-16 2/3 rung, which was the condition set for it, and a K=7 rung at QAM-16 would need its own anchor.

**A decoder finding that a burst cannot reach.** At 7/8 the tail-biting decoder fails with no noise at all on about half of all payloads at block lengths 9 to 13, 16, 19, 23 and 34 bits, and on none from 35 to 400 (8 seeds a length), nor on any whole-byte length from 24 to 520 bits at 3/4, 5/6 or 7/8 (4000 seeds a length to 96 bits, 500 beyond). The coding is injective at 19 bits, every one of the 524288 payloads checked, so it is the decoder and not the pattern: M0LTE.Fec's `TailBitingViterbiDecoder` warms up and cools down over min(6K, N) trellis steps around the block and traces back from the best end state without requiring a tail-biting path, and on a block shorter than 42 bits with three sevenths of the lattice erased the survivors have not merged by the time the trace-back enters it. Every frame this modem sends is whole bytes and at least a payload byte plus two of CRC, 24 bits, so nothing on air reaches it; a zero-byte payload would, as a 16-bit frame, and nothing sends one. The fix is the decoder's, not this repository's.

**Caveats.** One profile, and the synthetic one; one payload size; a static simulated link; 64 seeds, so a cliff is good to a decibel, confirmed rather than assumed by re-running the whole ladder a day later and seeing most cells reproduce exactly and a couple move within that decibel. Nothing here has been on air. [preset-design.md](preset-design.md) records QAM-64 3/4 delivering on the 8 kHz preset, and 5/6 and 7/8 since sent; and the cliffs above say what to expect: about 2 and 2.5 dB more than 3/4 wants.

## How fast is it, and the answer nobody wants

Three different numbers get called "the data rate" and quoting the wrong one flatters this waveform badly. Raw is data carriers times bits per carrier per symbol, with no code and no overhead - the figure a specification sheet prints and nothing ever achieves. Coded applies the code rate. Delivered is payload bits over the whole transmission, sync symbol, preamble, header, lead-in and all.

`ThroughputProbe` measures all three over real modulated bursts. It is arithmetic over the waveform rather than a channel measurement: what a link will bear is `OfdmFmRateLadder`'s business, and the ladder has only ever been calibrated on one narrow profile. The delivered figures the shipped presets reach on air are in [profile-set.md](profile-set.md); what the probe adds is the shape of the curve, and one hard ceiling.

**At 64 bytes there is a ceiling of about 2.3 kbit/s, and every layout's top rung sits exactly on it.** A burst is a whole number of symbols and four of them are fixed (lead-in, sync, preamble, header), so once the payload fits ONE symbol, five symbols is the floor. Every profile that shares a symbol duration therefore delivers the same 512 payload bits over the same five symbols, which at the shipped 44.0 ms symbol is about 2.3 kbit/s, on the narrowest layout and the widest alike. An earlier version of this paragraph claimed every constellation delivered that figure identically, and the probe's own rows contradict it: a rate too sparse to fit 64 bytes in one symbol falls below the ceiling rather than onto it, needing four payload symbols where a dense one needs one. The conclusion survives the correction: **at short frames the top of the ladder is overhead-limited, not rate-limited**, and making it faster there means shortening the fixed part or batching frames into one burst, and nothing else.

One concrete way to shorten the fixed part, unbuilt and unmeasured: the sync symbol already carries a known value on every EVEN occupied bin, so it could double as the channel estimate - even bins measured, odd bins interpolated - and the separate preamble symbol goes away. That is one of the four fixed symbols, which at 64 bytes is 25 % more delivered on the rungs sitting on the ceiling. The noise measurement is untouched (it lives in the sync symbol's odd bins already); the open question is what a half-density estimate costs the payload through the denoiser, and nothing but the ladder can answer it.

### Against what pdn-soundmodem already has

Delivered payload bits per second, same measurement, TXDELAY excluded from both because that is a station setting rather than a property of a waveform:

| mode | 64 B | 256 B | 1024 B |
|---|---|---|---|
| fsk9600 | 8629 | 9333 | 9533 |
| fsk9600-il2p | 5840 | 7847 | - |
| c4fsk9600 | 4793 | 7310 | - |
| qpsk3600 | 2173 | 2935 | - |

The OFDM-FM row of that table was measured on a profile this repository does not ship, so it is not reproduced here. What carries over is the ceiling above, which belongs to the burst structure rather than to any layout: **at 64 bytes every OFDM-FM rung sits on about 2.3 kbit/s, so plain 9600 GFSK beats this waveform there by nearly four times.** A narrow profile only draws level at about a kilobyte, and only on its top rung, which wants +23 dB of carrier-to-noise before it holds half its frames. The wide presets do far better and are measured on air in [profile-set.md](profile-set.md), but they need a data port and a radio with an extended passband.

That is not an argument against the waveform. It is an argument about where the work is: this thing was built to be fast and it is spending its speed on burst overhead at exactly the frame sizes a packet network uses. Until that changes, "OFDM-FM is faster than 9600" is only true for bulk transfer.

## Decode time against air time: what a receive thread can afford

On air on 2026-09-19 the adaptive rate controller stepped a link up to QAM-256 rate 2/3. One station decoded 9 of 10 bursts at 31.5 kbit/s; the other decoded nothing and logged an audio capture overrun. Nobody had ever measured how long the receive thread takes to walk a burst against how long that burst spends on air, on the machine that actually has to do it - a Raspberry Pi. Everything else in this document is about what a burst costs to SEND; this section is about what it costs to RECEIVE, which turned out to be the thing that broke.

A bench harness, written for the campaign and not part of this repository, renders a 1900-byte burst on every rung of `OfdmFmRateLadder`, on a wide carrier layout built in code for the tool alone: a few hundred carriers on a 2048-point transform at 48 kHz, 64-sample cyclic prefix, representative of a deployed wide preset without being one, since a station's layouts live in its own file rather than in the source. Each burst is fed through the streaming `OfdmFmModem`'s `Process`, timed with a `Stopwatch` around the whole call, 40 repetitions on one modem instance after a warm-up so the window buffer's one-time growth is not counted. The same burst with its payload zeroed after the header is measured the same way, standing in for a burst that will not decode, since a receiver has to walk the whole thing before it can find that out from the CRC.

### Raspberry Pi 4 Model B Rev 1.5

`pdn-soundmodem` was `active`, and had been idle for about half an hour (its last journal lines were start-up logging, nothing had been decoded or transmitted since) - so this ran uncontended with the daemon it will eventually run inside.

| rung | air ms | decode ms | ratio | wrecked ms | ratio |
|---|---|---|---|---|---|
| BPSK K=9 3/4 | 3432.0 | 542.99 | 6.3x | 656.95 | 5.2x |
| QPSK K=9 2/3 | 2156.0 | 371.89 | 5.8x | 518.59 | 4.2x |
| QPSK K=9 3/4 | 1936.0 | 354.77 | 5.5x | 500.60 | 3.9x |
| QAM-16 K=9 1/2 | 1584.0 | 323.14 | 4.9x | 529.39 | 3.0x |
| QAM-16 K=9 3/4 | 1188.0 | 285.88 | 4.2x | 463.23 | 2.6x |
| QAM-64 K=9 2/3 | 1012.0 | 273.37 | 3.7x | 514.79 | 2.0x |
| QAM-64 K=7 2/3 | 1012.0 | 231.94 | 4.4x | 285.52 | 3.5x |
| QAM-64 K=7 3/4 | 968.0 | 225.03 | 4.3x | 275.72 | 3.5x |
| QAM-64 K=7 5/6 | 924.0 | 220.34 | 4.2x | 265.43 | 3.5x |
| QAM-256 K=9 2/3 | 880.0 | 276.15 | 3.2x | 688.99 | 1.3x |
| QAM-64 K=7 7/8 | 880.0 | 214.88 | 4.1x | 253.38 | 3.5x |

### The dev box, for comparison

An AMD Ryzen 7 8745H (16 threads, x86_64), which is not what this waveform will ever actually run on, but shows how much of the Pi's cost is the algorithm rather than the hardware: the same code, the same bursts, the same 40 repetitions.

| rung | air ms | decode ms | ratio | wrecked ms | ratio |
|---|---|---|---|---|---|
| BPSK K=9 3/4 | 3432.0 | 113.25 | 30.3x | 134.96 | 25.4x |
| QPSK K=9 2/3 | 2156.0 | 83.41 | 25.8x | 103.26 | 20.9x |
| QPSK K=9 3/4 | 1936.0 | 83.34 | 23.2x | 98.55 | 19.6x |
| QAM-16 K=9 1/2 | 1584.0 | 72.06 | 22.0x | 107.98 | 14.7x |
| QAM-16 K=9 3/4 | 1188.0 | 65.71 | 18.1x | 93.63 | 12.7x |
| QAM-64 K=9 2/3 | 1012.0 | 60.87 | 16.6x | 101.96 | 9.9x |
| QAM-64 K=7 2/3 | 1012.0 | 47.90 | 21.1x | 59.17 | 17.1x |
| QAM-64 K=7 3/4 | 968.0 | 47.19 | 20.5x | 56.31 | 17.2x |
| QAM-64 K=7 5/6 | 924.0 | 45.32 | 20.4x | 52.68 | 17.5x |
| QAM-256 K=9 2/3 | 880.0 | 60.24 | 14.6x | 139.80 | 6.3x |
| QAM-64 K=7 7/8 | 880.0 | 45.82 | 19.2x | 46.67 | 18.9x |

The Pi is five to eight times slower than the dev box on a clean burst and worse than that on a wrecked one, and it is the Pi that ships, so the Pi's numbers are what the bound below is taken from.

### The reading

QAM-256 rate 2/3 is where the fault reproduces: 3.2x on a burst that decodes, and 1.3x on one that does not - a wrecked QAM-256 burst COSTS MORE THAN A SECOND BURST WOULD HAVE TAKEN TO ARRIVE, which is exactly the shape of an overrun. Every QAM-64 rung held at least 3.7x clean. Wrecked, the four K=7-coded QAM-64 rungs held 3.5x with almost no spread between them; the one K=9-coded rung, 2/3, held 1.97x - under the two-times bar this bound is set to, and consistently so across three separate runs at 5, 20 and 40 repetitions (1.997x, 1.997x, 1.966x), not a one-off dip. QAM-16 and below held 2.6x or better wrecked, with more margin the sparser the constellation.

`AdaptiveTopConstellation` (see `OfdmFmParameters`) bounds what `OfdmFmRateController` will RECOMMEND by constellation density, not by rung or code rate, because that is the shape of thing a profile can name in one field. It therefore cannot separate the K=9 2/3 QAM-64 rung, which sits right at the line, from the four K=7 QAM-64 rungs sharing its constellation, which sit clear of it with room to spare. QAM-64 is the default bound: it is the densest constellation whose rungs held at least twice as much air time as decode time on the Pi, including the wrecked case, for four out of five of its own rungs, and the fifth missed the line by under two per cent, well inside the run-to-run noise this measurement showed. QAM-256 is excluded outright, at less than a third of the bar. A station that wants QAM-256 anyway sets the field; what a correspondent asks THIS station's transmitter for is never bounded by it, because a station cannot measure a correspondent's receive thread, only its own.
