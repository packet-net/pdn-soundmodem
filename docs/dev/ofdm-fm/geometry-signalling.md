# Signalling the carrier layout in the burst header

**Status: implemented and proved on air, 2026-09-19. No shipped preset uses it.**

Sections 0 to 12 are the proposal as it was written, with section 6 rewritten to what this repository actually ships. Section 13 says what was built and where it differs from the proposal, section 14 is the on-air proof, and section 15 is the rig re-characterised the same day, which overturns section 5.1 and is what [preset-design.md](preset-design.md) starts from.

## Read this first: the table ships, with one caveat

A geometry table lets one station transmit on a carrier layout the receiving station was not configured for. It works, it was proved on air, and **every shipped preset is in one**. There are three layouts: entry 0 is the narrow span that `ofdm-fm-narrow` runs, entry 1 the 6 kHz span, entry 2 the 8 kHz span. Rate variants of a span share its id because they share its layout. A burst names its payload layout by id in its header, so a receiver on any preset in the table decodes a burst sent on any other, which is what lets a link change bandwidth without a handshake.

The caveat is entry 0. It is where every burst's sync symbol, preamble and header go out, whatever the payload's geometry, which makes it the one layout every station in a table must be able to transmit and receive, and the least forgiving thing in the waveform to get wrong. **`ofdm-fm-narrow` was keyed on 2026-09-20** and delivered every frame in both directions at 2.2 to 2.3 kbit/s across a multi-hour soak, so entry 0 is measured rather than extrapolated. Cross-geometry decoding itself was already proved in section 14 below. Section 5 of [preset-design.md](preset-design.md) is the one measurement there is of a *wide* layout used for acquisition, and it came out worse, not better, which is why entry 0 is the narrow one.

So the mechanism is measured and this particular entry 0 is an extrapolation. Keying `ofdm-fm-narrow` between two stations is the run that closes it, and it should happen before anybody leans on a long link.

## 0. The goal this serves

**Get the most out of these particular radios, on this particular channel, rather than out of the voice path the waveform was designed against.**

The profiles the modem was carrying occupied a classic FM voice passband. Injecting audio at the TM8110's T12 tap bypasses the limiter, the 3 kHz filter and pre-emphasis, and the path that remains measures flat within 0.1 dB from 200 Hz to 4.5 kHz and is usable to 6. Roughly half the channel these radios can actually pass was going unused, and the waveform had no way to know.

The objective is therefore a preset tuned to THIS radio, interface and channel combination, sized from the measurement rather than from the assumption, and pushed until something real stops it. Section 4 is what that produced and section 5 is what stopped it.

**Geometry signalling is the enabler, not the goal.** A station can only use a wider preset today if both ends were configured for it in advance, which is fine for a two station bench and useless on a shared channel where a station cannot know what will call it. Signalling the layout is what turns a bench result into something a network can use, and it is also what lets the rate controller reach for bandwidth, which section 4 shows is worth more on this path than constellation or code.

### What the bandwidth is worth, measured

On the 6 kHz preset, per burst, payload over burst duration. Every burst spends 4 symbols on lead-in, sync, preamble and header before any payload, so the rate climbs with frame length toward the 21.18 kbit/s the carriers sustain during payload symbols.

| payload | burst | rate while transmitting |
|---|---|---|
| 64 B | 0.227 s | 2.26 kbit/s |
| 256 B | 0.317 s | 6.45 kbit/s |
| 1024 B | 0.589 s | 13.90 kbit/s |
| 1900 B | 0.907 s | 16.76 kbit/s |

Raw modulation on the carriers is 31.76 kbit/s, and rate 2/3 coding leaves 21.18 of it. Measured end to end including gaps and keying, a 1024 byte frame gives 12.36 kbit/s of goodput, against 7.42 for a narrow voice-width reference at the same rate. **Every figure in this subsection was taken at the 128-sample cyclic prefix**, which the shipped presets have since halved (see [preset-design.md](preset-design.md) section 4.6), so the same preset is about 3 % faster today.

## 1. What a burst header signals today, and what it does not

A header names its payload's **constellation** and its **coding**, and a receiver adapts to both per burst. Confirmed on air 2026-09-18: eight cells at eight different rates were decoded by a receiver that stayed configured on one profile throughout and was never told what was coming. Had the header been wrong or ignored, a receiver expecting uncoded QPSK would have failed every coded frame instead of decoding 30 of 30.

It does not name the **geometry**: which carriers are occupied and how many are pilots. Geometry is what the header is READ AGAINST, so both ends must agree in advance. That is why a rate ladder can be swept from one end while a bandwidth change has to be made at both.

## 2. The constraint

The header cannot be fully self-describing. Demodulating it needs the transform size, the cyclic prefix and the carrier layout, so something must be fixed by prior agreement. The only question is how little.

The answer is: very little. Every profile in a table shares a sample rate, a transform size and a cyclic prefix, and differs only in the carrier span. Every span contains the narrowest one.

## 3. The proposal

1. **Acquisition moves to a fixed narrow span.** Sync, preamble and header always occupy one carrier layout that every profile contains, whatever the payload uses. Acquisition then behaves identically for every burst on the band.
2. **The header gains a 4-bit geometry id**, indexing an ordered table, exactly the pattern `_codingById` in `OfdmFmBurstCodec` already establishes: appending is safe, reordering is not, and the table never appears on the wire. The wire says "geometry 2" and nothing about what geometry 2 is, which also keeps the carrier layout out of anything transmitted.
3. **One wide channel-estimate symbol follows the header**, on the announced geometry, because a narrow preamble cannot estimate carriers it never occupied. Skipped when the announced geometry is the acquisition geometry, which is the common case on a settled link.

### Why 4 bits and not more

The header is 40 bits, which its own K=9 rate 1/2 code turns into 96 coded bits, one coded bit per data carrier on a BPSK header. Four more bits gives 104 coded bits, which still fits one symbol on any layout with at least 104 data carriers, and a narrow acquisition layout has that and not much more. Eight more would not fit. So 4 bits is close to exactly the room that exists, which is both the argument for 4 and the warning against anything more ambitious.

## 4. What the channel actually carries, measured

All figures from this bench: two Tait TM8110s on a 25 kHz channel, CM108 interfaces, audio injected at the T12 tap so the limiter, the 3 kHz filter and pre-emphasis are all bypassed. Evidence is in [on-air-campaign.md](on-air-campaign.md) and [bandwidth-and-frame-length.md](bandwidth-and-frame-length.md).

**The audio path is much wider than the waveform assumed.** Stepped tone, 31 points, both directions:

| | |
|---|---|
| 200 Hz to 4.5 kHz | flat within 0.1 dB |
| 5 kHz | -1.7 dB |
| 6 kHz | -2.8 dB |
| 7 kHz | -5.6 dB |
| 8 kHz | -9.1 dB |

Roughly half the usable channel was going unused, which is what started this.

**Three candidate geometries were built and measured**, all with the same low edge at carrier 9 (211 Hz, where the path is within a quarter of a dB) and differing only at the top:

| name | carriers | span | data + pilot | best measured goodput |
|---|---|---|---|---|
| x5 | 9 to 213 | 211 Hz to 4992 Hz | 197 + 8 | 12.56 kbit/s |
| **x6** | 9 to 256 | 211 Hz to 6000 Hz | 240 + 8 | **15.36 kbit/s** |
| x7 | 9 to 299 | 211 Hz to 7008 Hz | 283 + 8 | 16.01 kbit/s but unreliable |

Against a narrow voice-width reference at the same rate, which gives 7.42 kbit/s. That reference is not one of the shipped presets and nothing of our own has been measured in its place, so read it as where the day started rather than as a baseline anyone can reproduce.

**x6 never dropped a frame in any cell run at any payload length. x7 did**, 24 of 24 at 1024 bytes and 9 of 16 at 1900. Its top carriers are the marginal ones and a longer burst gives them more chances to fail. x6 is therefore the recommended default and is what both bench stations ran from here on; it ships as `ofdm-fm-6k-fast`.

## 5. Four constraints that shape any geometry table

These are the reasons the table is not simply "as wide as the audio path allows".

### 5.1 The band tilts about 6 dB per octave, and that is the binding limit

*Section 15 overturns this for this rig. It is kept because the reasoning is right in general and because the measurement that corrected it is the more interesting result.*

An FM discriminator's noise power rises with the square of audio frequency. Both taps in use bypass pre-emphasis and de-emphasis, which is why the measured passband is FLAT and the noise is not. The codec records 18 dB of tilt across a voice-width span; a 211 Hz to 6 kHz span is 4.8 octaves and about 29 dB.

Measured consequence, the same constellation across three spans:

| span | tilt | QAM-256 delivered |
|---|---|---|
| a narrow voice-width span | about 19 dB | 29/30 |
| 211 Hz to 6000 Hz | about 29 dB | 10/24 |
| 211 Hz to 7008 Hz | about 30 dB | 1/24 |

The last two are barely a decibel of tilt apart and fall from 42 % to 4 %, so the threshold sits around 29 to 30 dB of spread, which is where QAM-256's own 28 dB signal-to-noise requirement puts it. **A wide geometry and a dense constellation are alternatives, not companions.** A table offering both should expect them to be used one at a time.

### 5.2 Peak deviation is a property of the constellation, not of the drive

Transmit drive is normalised once from the preamble, and each constellation is then allowed its own crest factor. Average power barely moves across the ladder; the peaks do. Measured on rendered bursts:

| constellation | peak vs QPSK | peak deviation at this bench's drive |
|---|---|---|
| QPSK | reference | 2.84 kHz, 57 % of class |
| QAM-16 | +3.1 dB | 4.06 kHz, 81 % |
| QAM-64 | +4.6 dB | 4.81 kHz, 96 % |
| QAM-256 | +4.6 dB | 4.81 kHz, 96 % |

So a drive set against QPSK puts QAM-64 within 4 % of a 5 kHz class limit. **Set the drive against the densest constellation a station will ever transmit**, and treat any deviation figure taken at QPSK as 4.6 dB optimistic for anything denser. This matters for a geometry table because adding wider entries invites denser ones alongside.

### 5.3 The IF filter does not bite, which was not expected

FM occupied bandwidth is roughly 2 x (deviation + top audio frequency), so x7 needs about 19.7 kHz at this deviation and was expected to meet a 25 kHz radio's IF filter. It delivered 24 of 24 at 1024 bytes. **No IF truncation was observed up to 7 kHz of audio.** The campaign's prediction of a distortion floor on the dense wide rungs is unconfirmed twice over, and the tilt in 5.1 explains the failures that were seen without needing it.

### 5.4 Sample clock skew is not a reason to stay narrow

These two soundcards measure 5.1 ppm apart, measured on air both directions with a tone and a phase ramp fit, the two readings summing to -0.64 ppm. The tilt correction in the codec was designed against 100 to 200 ppm injected in simulation. Burst length was swept 64 to 1024 bytes at two rates and delivered 160 of 160. **Nothing here argues against wider or longer bursts.**

## 6. The layouts this suggests, and the entry that is missing

The proposal's table was ordered and append-only, with the layouts kept in a station's own file rather than in the source, so that the wire only ever carries an index. That part is built and works (section 13).

These are the layouts of our own that were built and measured on this rig. They are not a shipped table; they are what a table would be assembled from:

| name | carriers | span | data + pilot |
|---|---|---|---|
| x5 | 9 to 213 | 211 Hz to 4992 Hz | 197 + 8 |
| x6 | 9 to 256 | 211 Hz to 6000 Hz | 240 + 8 |
| x7 | 9 to 299 | 211 Hz to 7008 Hz | 283 + 8 |
| x6bl | 9 to 256 | 211 Hz to 6000 Hz | 240 + 8, bit loaded |
| x6bl2 | 9 to 256 | 211 Hz to 6000 Hz | 240 + 8, bit loaded, a second split |
| w8 | 9 to 341 | 211 Hz to 7992 Hz | 323 + 10 |
| w85 | 9 to 363 | 211 Hz to 8508 Hz | 345 + 10 |
| w85bl | 9 to 363 | 211 Hz to 8508 Hz | 345 + 10, bit loaded |
| w9 | 9 to 384 | 211 Hz to 9000 Hz | 364 + 12 |
| w9bl | 9 to 384 | 211 Hz to 9000 Hz | 364 + 12, bit loaded |
| w10 | 9 to 427 | 211 Hz to 10008 Hz | 407 + 12 |

**A bit-loaded span is its own entry even when its carriers are another entry's**, because the tiers are what a receiver reads the payload against, exactly as the carrier count is. The profile check refused a bit-loaded span sharing a uniform span's id for that reason, and the rule is now in the code (`OfdmFmGeometry.SameLayoutAs`).

**What the table has not got is an entry 0.** Every burst's sync symbol, preamble and header go out on entry 0, whatever the payload's layout, so it is the one entry that has to work for every station in the table and the one entry a station falls back to when it knows nothing. The bench's entry 0 was a narrow voice-width layout this repository does not carry. Nothing on this list has ever been used as an acquisition layout except the 8 kHz span, once, in [preset-design.md](preset-design.md) section 5, where it acquired WORSE than the narrow layout did and lost frames at 1900 bytes.

So the honest position is: the machinery is built and proved, the layouts above are measured, and the one entry a table cannot do without is the one nobody has measured. Until somebody keys a narrow layout of our own and shows a wide payload decoding against it, the shipped presets run standalone.

## 7. What it costs

* One extra symbol per burst, and only when the payload geometry differs from the acquisition geometry.
* The header no longer spreads across every carrier on a wide profile, so it loses coding margin exactly where the band is widest. `HeaderRepeats` exists and can buy that back.
* A receiver must build a codec for any geometry in the table on demand. The codec is already cached per coding for the adaptive rate controller, so the shape of that is not new.

## 8. What it buys

* **A station hears everything.** Today a receiver must be configured for the bandwidth it will be sent, which is a real limit on a shared channel where a station cannot know what will call it.
* **The rate controller gains its most valuable axis.** It adapts constellation and code today. Section 4 shows bandwidth is worth more than either on this path, and it is the one thing adaptation cannot currently reach.
* **It removes a silent misconfiguration.** Two ends disagreeing about layout produces a link that simply does not work, with nothing in any log saying why.

## 9. State of the bench, for whoever picks this up

*As of the evening of 2026-09-19 the bench ran a geometry file whose profiles all carried ids, radio carrier sense on, and the `afsk1200` sub-channel gone. The bullets below are how the proposal found it that morning and are kept as the record.*

* Both stations ran the 6 kHz preset as their persistent configured mode, with `afsk1200` kept on sub-channel 1 as an on-air control over the same path.
* The geometry file was a station-local JSON beside the plugin, untracked by design, carrying the profiles the modem shipped with plus the measured x5, x6, x7 and several rate variants.
* A profile-set validator ran before anything was deployed. A profile that fails validation throws in the plugin's constructor and takes the daemon's start-up with it, so run the check on a dev box first, never on air first.
* An offline replay tool ran the streaming receiver over a station's own raw capture, with a self test. It is the only way to ask whether the decoder or something around it is at fault.

The validator and the replay tool were campaign tools on the station build and are not part of this repository.

## 10. Three traps that cost real time overnight

Anyone measuring this will meet these.

1. **A station needs time to settle after a restart.** Switching a profile restarts the daemon, and it is natural to start measuring as soon as the config API answers. Sending immediately after a restart delivered 14 of 20; the same test after 45 seconds of quiet delivered 17 of 20, and the losses were all in the first few seconds. Several of the campaign's "losses under load" were this.
2. **Delivery fractions and goodput are not equally trustworthy.** Goodput is measured at both ends and survives; delivery fractions counted against a transmitter's own frame log do not, because that log and the far end disagree by a large margin under load. See [missing-frames-under-load.md](missing-frames-under-load.md), which is also a record of me getting the explanation wrong twice.
3. **KISS silently drops frames over 2048 bytes** (packet-net/pdn-soundmodem#503; fixed in pdn-soundmodem 0.72.0, which passes 8192 by default, says so in the journal when it drops one, and takes `kissMaxFrameBytes` in the config; this modem's own cap rose to the header's 4095 with it). Frame length is worth a factor of six in goodput on this path, from 1.97 kbit/s at 64 bytes to 12.36 at 1024, so this cap sits directly on the biggest lever there is.

## 11. What this depends on that was still broken

Geometry signalling is a shared-channel feature, and this bench could not run a shared channel properly while the proposal was written. **Carrier sense was broken on FM in two ways** (packet-net/pdn-soundmodem#502): nothing saw a far end's silent TXDELAY, so stations keyed into each other's lead-in, and the energy detector asserted busy for ten seconds or more after every burst it heard because an FM receiver gets QUIETER under a carrier and the return of the noise reads as a signal. Both are measured in [carrier-sense.md](carrier-sense.md), which also records how it was solved: by asking the radio rather than the audio.

That never blocked implementing geometry signalling, but it did mean the benefit in section 8, which is mostly about shared channels, could not be demonstrated until carrier sense worked.

## 12. Order of work

The measurement this was waiting for is done and section 6 is the answer. What remains is implementation, and the sequence that makes sense is:

1. Move acquisition to the fixed narrow span and prove nothing regresses, with both ends on the same geometry throughout. That change is invisible on the wire and can be validated alone.
2. Add the geometry id and the wide channel-estimate symbol, and prove a receiver configured for entry 0 decodes a burst sent on a wider entry.
3. Only then let the rate controller reach for it, which is where the value is and also where a wrong table becomes expensive to change.

## 13. What was built, and where it differs from the proposal

Steps 1 and 2 above were built together, since the wire format changes once. Step 3 is half done: the wire carries a geometry recommendation and a station follows one, but nothing yet decides what to recommend from measurement.

**The header is 52 bits**, not the proposal's 48 plus 4:

| field | bits | note |
|---|---|---|
| geometry | 4 | table index of the payload's layout |
| constellation | 4 | as before |
| coding | 4 | as before |
| payload length | 12 | **was 16**; 4095 bytes at most |
| recommended geometry | 4 | new, read only beside a rate recommendation |
| recommended constellation | 4 | 1 to 8 the constellation; 9 to 15 the constellation plus eight, asking for full bursts |
| recommended coding | 4 | as before |
| CRC-16 | 16 | over the 36 bits above, taken as five bytes with the last nibble zero |

**The burst form is asked for in the recommended constellation's nibble.** A receiver that sees follow-on bursts (`FollowOnFrames`, [preset-design.md](preset-design.md) section 4.5) fail where full ones decode asks for full bursts by sending its recommended constellation plus eight, values 9 to 15, which no constellation uses; the rate controller decides when (`WantFullBursts`). A reader from before the flag existed sees a value it does not know and drops the recommendation, which is the safe failure. QAM-256, the one constellation whose value plus eight does not fit, is asked for without the flag.

That is 104 coded bits, so a layout needs 104 data carriers to carry a header in one symbol: at 103 it takes two, which a test pins. A narrow acquisition layout has the room and no more. The proposal said four more bits was all that fitted, and it was right; the recommended geometry came out of the length field instead. Sixteen bits of length described 65535 bytes for a receiver that assembles at most 2048 and a KISS link that dropped anything over 2048, so twelve bits cost nothing anybody can send, and the recommendation is what lets a receiver ask for bandwidth, which section 4 says is worth more than constellation or code. The cap is now `OfdmFmBurstCodec.MaxPayloadBytes`, 4095.

**The table is built from the geometry file**, not declared in code. Each profile gains a `geometryId`, and every profile that has one joins one table shared by the whole file, so the carrier layouts stay in the station's own file where they have always lived. Rate variants of one span share an id. Two profiles with the same id and different layouts, or different transform sizes or prefixes, cannot share a table: the second is dropped from the modes with its reason printed at start-up, which is the silent misconfiguration section 8 promised to remove, made loud instead. **A profile with no id runs alone**, as this waveform always did, with its own layout as its own acquisition layout, and that is what every shipped preset does.

**One drive per layout.** A symbol's scale is set from its own layout's reference pattern, so a narrow header and a wide payload come out at the same RMS and spend the deviation budget the same way. Setting one drive from the narrow preamble, as the single-layout codec did, would have put a 240-carrier payload 3 dB louder than its own header.

**The noise the wide estimate is denoised with is measured, not borrowed.** The sync symbol occupies only the even bins of the acquisition span, so on a wider payload span every other bin is empty during it, and those bins measure the noise the payload will meet, at the top of the band where the sync symbol never goes. A narrow noise figure would have said nothing about 6 kHz.

**The clock-tilt fit works in absolute bins.** It reads the header on the acquisition layout and the pilots on the payload layout, and a tilt is so many radians per bin per symbol whatever layout a carrier belongs to. Indices into each layout would have fitted at one offset and corrected at another; a test resamples a wide payload by 100 and 200 ppm to hold that.

**Every decoded burst now reports its per-carrier signal to noise** (`OfdmFmBurst.CarrierSnrDb`), from the error vector between each equalised point and the point the decoded bits say was sent, alongside the channel's per-carrier gain. It is the measurement a bit-loading decision needs, and it costs one re-encode the pre-FEC figure already paid for. Two things it exposed on a clean loopback: the uncoded scheme still interleaves, so a reference that skipped the encoder read every data carrier at -3 dB; and the peak reducer's own distortion at QAM-16's default limit reads 23 to 27 dB, which on a rig whose noise floor sits 30 dB down is a real share of the budget.

**Costs, as built.** One estimate symbol per burst whose payload is off the acquisition layout; none otherwise. The header does not lose coding margin on a wide profile, because the acquisition layout is narrow for every profile and the header's margin is whatever that layout gives it. A receiver builds a layout for any table entry the first time a burst names it, and keeps it.

## 14. The on-air proof

Twenty frames a cell, 1024 bytes each (256 on the narrow layout), callsigns M0LTE-1 (radio1) and M0LTE-2 (radio2), over the air at 1 W with radio2 on a dummy load, both stations 45 seconds settled after every restart. The receiver's configured profile is what its `soundmodem.json` names; it was never told what the other end would send.

**The acquisition layout used for this proof is the bench's narrow voice-width layout, which this repository does not carry**, so the cells below cannot be reproduced as written. What they prove is the mechanism, and the mechanism is what shipped.

| step | sender on | receiver configured for | delivered |
|---|---|---|---|
| 1, same geometry | 6 kHz span, radio1 | 6 kHz span, radio2 | 20 of 20 |
| 1, same geometry | 6 kHz span, radio2 | 6 kHz span, radio1 | 20 of 20 |
| 2, the proof | **6 kHz span**, radio1 | **narrow acquisition layout**, radio2 | **20 of 20** |
| 2, the reverse | narrow acquisition layout, radio2 | 6 kHz span, radio1 | 20 of 20 |
| 2, wider than the receiver | 7 kHz span, radio2 | 6 kHz span, radio1 | 20 of 20 |

The third row is the one the proposal asked for: a station configured for the narrowest profile in the table decoded every burst sent on a profile twice its width, on carriers its own preamble never occupied. The last row is the case a shared channel will meet, a station hearing something wider than it would send itself. Nothing regressed with both ends on one geometry, which is step 1.

The frame log's `snr_db` for these cells, 9 to 11 dB, is a rolling-minimum band figure and not a measurement of the link; the per-carrier figures in section 13 are.

## 15. The rig re-characterised, 2026-09-19

The rig is no longer coax through 100 dB of pads: radio1 is on an antenna, radio2 on a dummy load, and they hear each other by leakage at about -34 dBm, which is 60 dB over radio1's idle floor and 91 dB over radio2's.

**Deviation is unchanged**: a 1 kHz sine at the default drive peaks at 4.12 kHz on radio1 and 4.04 kHz on radio2, 82 % of the 5 kHz class, measured on the RSP1 with a +/-8 kHz analysis window. The +/-4 kHz window section 5.2 was taken with truncates a 4 kHz deviation and reads 3.83 kHz with a false third harmonic at -30 dB; the window ladder plateaus from +/-8 kHz.

**The passband** is flat within a few tenths of a decibel of the 18th's, -3 dB at 6.3 kHz, -6 dB at 7.4 kHz, -8 dB at 8 kHz and -18.7 dB at 10 kHz, the same both ways to 0.8 dB, with the 1 kHz reference at the start and end of each sweep agreeing to 0.02 dB.

**Section 5.1 is wrong for this rig, and it changes the design.** The noise floor under a carrier was measured per 23 Hz bin at both receivers, and it FALLS with frequency, about 1.4 dB per octave, at the same rate as the passband rolls off:

| under carrier, dBFS per bin | radio1 | radio2 |
|---|---|---|
| 211 Hz | -76.9 | -78.1 |
| 1 kHz | -80.3 | -80.7 |
| 6 kHz | -85.0 | -85.6 |
| 8 kHz | -90.4 | -90.7 |
| 10 kHz | -95.7 | -95.0 |

Both receivers are in full quieting, 60 to 90 dB above their RSSI floors, so the discriminator's f-squared noise is far under the audio chain's own floor, and what the top of the band loses in signal it loses in noise too. The rising tilt exists near threshold, which this rig cannot reach without an attenuator in a leakage path. The per-carrier signal-to-noise SHAPE that follows, relative to the best carrier, is within 8 dB from 500 Hz to 9 kHz on both receivers, 6 to 9 dB down below 500 Hz, and only past about 9.8 kHz on radio2 more than 10 dB down. Three spur bins on radio2 at 4031 to 4078 Hz sit 13 to 19 dB under the shape, and a 1406 Hz line on both sits 6 to 8 dB under; their source is not known.

So the reason x7 dropped frames in section 4 was not the tilt, and the binding limits are now elsewhere: the peak reducer's own distortion (23 to 27 dB at QAM-16's limit on a clean loopback), receiver-side distortion that scales with deviation (a third harmonic at -24 to -30 dB under a full-deviation sine, 35 to 45 dB down at the burst's deviation, worsening with modulating frequency, which points at the IF filter against the sideband extent), and the additive floor above, which for a 248-carrier burst at this drive works out around 28 to 32 dB per carrier. Those three are comparable, and which one wins at a given span and drive is what the sounding in [preset-design.md](preset-design.md) measures rather than reasons about.

**Clock skew** is 5.6 ppm, radio2's card slow, wandering by 1 to 2 ppm between runs.
