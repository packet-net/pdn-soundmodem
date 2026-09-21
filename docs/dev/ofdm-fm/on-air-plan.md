# OFDM-FM on air: the pre-flight plan

Opened 2026-08-20, when Tom said on-air testing happens as soon as hardware arrives. Everything this modem knew about itself had been measured in simulation, against a channel model written alongside it. This document exists so the first session produces evidence rather than a debugging log.

It is a plan, not a result. Results go in [receiver-findings.md](receiver-findings.md) and [on-air-campaign.md](on-air-campaign.md), and a dated entry goes in [docs/dev/mode-validation.md](../mode-validation.md).

## The gate, stated before we start

It is worth being precise about what counts as proof rather than declaring victory on the first frame that lands.

- **First contact** is a milestone and not the gate: one frame, any rate, any path, at whatever margin. Worth celebrating and worth a line in the ledger.
- **Proven** means a stated rate carrying a stated number of frames over a stated path at a stated margin, reproducible on a second day, with the failure budget by stage recorded for the cells that did not pass. Plus a mode-validation entry saying exactly that.
- **Characterised**, which is the level the bench work is already at and the level the ladder claims live at, means the chosen cells measured across a signal axis with the sim numbers beside them and the disagreements explained. That is where "the code is worth 5 to 6 dB" either survives contact with a real radio or does not.

Nothing below is worth doing if the answer to "what would this session have shown if it failed" is "we would not know".

## The one rule that decides whether a session was worth having

**Capture the audio at both ends, every session, always, timestamped.**

Every campaign this project has learned anything from lived off a corpus: MS110D's cuts, the 40m archive, ARDOP's bit-identical replays. A session without capture produces a pass or fail and nothing else; a session with capture produces a corpus that every future receiver change can be re-run against, offline, for free, forever. It converts an unexplained null from a wasted afternoon into the most valuable thing we could have brought home.

Capture on the receive side is the obvious half. Capture the **transmit** side too, from the same sound card that fed the radio, because half the failure modes are ours and the transmitted audio is the only way to tell "the modem sent something wrong" from "the channel broke something right".

## The station arrangement, fixed by Tom 2026-08-20

**Squelch is never in the path.** These modems always hear open-squelch band noise, on every station, permanently. That is a standing decision and not a per-session setting, and it removes what would otherwise have been the most likely cause of a bad first session: a squelch opening on the burst and eating the front of the preamble.

It is not purely a simplification, and the consequences run the other way too.

- **TXDELAY gets cheaper.** It now has to cover PTT to RF and the far end settling, and nothing else. The TM8100 series' real floor is 14.8 ms rather than the 1.8 ms the documentation implies, and one OFDM-FM symbol is about 44 ms, so the sim's 150 ms is about three symbols and is probably generous rather than tight. TXDELAY is dead air on every single burst, so it is worth measuring down rather than leaving where the bench left it.
- **The receiver is permanently in acquisition against noise.** That is now the normal operating condition rather than an edge case, which retrospectively justifies the streaming adapter's bounded window and its commit budget: the measurement that a self-similar signal cost 253 MB of allocation in two seconds against 1272 bytes for silence was describing the everyday case, not a pathological one.
- **False acquisition becomes a real metric**, and one the bench cannot measure. Every commit the correlator makes against noise costs a header read, and real band noise is nothing like the Gaussian noise the model adds: it has impulsive content, carriers drifting through, adjacent channel splatter and someone's switching supply. A correlator that commits on an impulse is a problem we would not find in simulation.
- **DCD is ours.** With squelch permanently open there is no radio-supplied carrier detect, so whatever this modem tells the host about channel occupancy comes from the correlator and nothing else.

## Hardware, and what it gates

**Radios: a pair of Tait TM8110s.** Confirmed against MMA-00072-03 as a TM8100-series model, the 1-digit-display conventional mobile, so the ladder's R1/T13 calibration and MMA-00011-01 as the interface authority both carry across unchanged. Two facts from its specification bear directly on this waveform:

- **Audio bandwidth is 300 to 3000 Hz**, and the profiles the modem was carrying at the time were sized edge to edge with exactly that, with essentially no margin at either end, so expect the outermost carriers to sit down the skirts of the radio's own audio filter. This is not a surprise and it is not a fault: the channel-estimate denoiser was built precisely because this waveform always has carriers 20 dB down on the edges of a voice passband. It does mean the outer carriers are where a marginal link will fail first, and the per-carrier SNR measurement is the thing to look at before blaming anything else.
- **De-emphasis is selectable**, flat or a 6 dB per octave curve over that same 300 to 3000 Hz. Which is chosen decides whether the per-carrier SNR is flat or carries the discriminator's 17 dB rising-noise gradient, and therefore decides whether bit loading helps or actively hurts. It must be pinned and written down for every session, and it is itself one of the cheapest and most informative A/Bs available.

**Interfaces, 2 to 3 weeks out, and there will be two of them.** A second CM108 dongle to build up with the agreed circuitry, and in parallel an internal variant somebody else has designed and built. Two different audio paths means two different level chains, two different noise floors and possibly two different passbands, so **a measurement on one does not transfer to the other** and each needs its own deviation calibration. Two variants is a gift for exactly one reason: anything that behaves the same on both is a property of the waveform, and anything that does not is a property of the interface.

## Stage ladder: one new variable at a time

The bench runs everything in one process over a virtual pipe. Going from that to two radios on a real path changes several things at once, and a null then means nothing. Each stage adds one.

**Stage 0. One box, one real sound card, loopback cable.** Output to input through a pad. Same crystal at both ends, so this isolates the audio stack: ALSA, the 48 kHz host rate, the power-of-two rescale on real hardware, block sizes, and the streaming receiver's bounded window against a device that does not deliver samples in tidy chunks. *Exit: frames at the workhorse rate, failure budget clean.*

**Stage 0b. The band-noise soak, and it can happen today.** One radio, the CM108 that already exists, receive only, no transmitter and no second box. Sit a receiver on an open-squelch channel for an hour and count how often the correlator commits, how many of those commits survive the header CRC, and what it costs in allocation. This measures the false-acquisition rate against real band noise, which is the one thing the channel model structurally cannot produce, and it is free. If a burst of impulse noise reliably commits the correlator, that is worth knowing before the radios are keyed rather than after. *Exit: commits per hour and header-CRC rejections per hour on a quiet channel and on a busy one, and a capture of any noise that did commit.*

**Stage 1. Two independent crystals, an audio cable, still no radios. Blocked on a five pound purchase.** The largest unexercised risk in this modem is the sample-clock difference: measured, mitigated by the tilt fit, and never once run against two real crystals rather than an injected offset. That test needs two independent sound cards and a cable. It does not need the agreed circuitry, it does not need a radio, and it does not even need two boxes, because two USB audio devices on one machine already have two separate crystals.

**Blocked as of 2026-08-20: there is no second audio device to hand.** The unblock is any cheap USB audio dongle, not anything on the critical path, and the reason to get one now rather than wait for the CM108 that arrives with everything else is that otherwise the clock question and the radio path land on the same day and get debugged together. Measure the offset first from a long capture of a known tone at each end, then run the workhorse cell with the tilt fit on and off. *Exit: the measured ppm for that specific pair of cards, and a number for what the tilt fit bought on real clocks.*

**Stage 2. Two TM8110s, pads and dummy loads.** Now the audio path is real: deviation, emphasis as the radio actually implements it, the IF filter, the limiter. Fixed high margin, one cell, one interface variant at a time. **Measure deviation with the scope before the first frame** rather than assuming it; the per-mode targets are in [docs/dev/mode-modulation-reference.md](../mode-modulation-reference.md) and the instrument is `sm-ota fm-deviation`. Under-deviation costs output SNR, over-deviation meets the IF filter, and both look like "the modem does not work". *Exit: frames over RF at high margin, with deviation, both audio levels and the de-emphasis setting written down.*

**Stage 3. The attenuated ladder.** Sweep the pad for a signal axis and run the chosen cells. This is where the sim comparison happens, and **the axis is the hard part**: our numbers are CNR in the receiver IF bandwidth, deliberately not SSB's SNR3k, and the two are not interconvertible. Anchor on the radio's 12 dB SINAD sensitivity figure and work from there, or read the radio's own RSSI if it can be got at. If the axis cannot be pinned honestly, say so and quote pad attenuation as an arbitrary but repeatable axis rather than inventing a CNR. *Exit: the cells across five points, with the sim rows beside them.*

**Stage 4. Antennas, real path, fixed stations.** The actual claim. Fixed stations first: the flutter work established that the 0 Hz Doppler row is exactly where this modem was already fine, and Tom's judgement is that it will not be used mobile. *Exit: the gate above.*

## RSSI: the instrument stage 3 was missing

Tom, 2026-08-20: DCD can come from polling the radio for RSSI or from the modem's own detector, either works, and PDN will be polling the modem for RSSI so there will need to be some kind of contract.

**On the contract, the honest answer is that no action is required yet, and an earlier draft of this section overstated it.** Three reasons, recorded so nobody re-derives the wrong one.

- **Radio RSSI has no business on `IModem`.** A modem is handed a span of floats. It has no serial link and no knowledge that a radio exists. A radio-reported RSSI is a radio-control concern that belongs beside PTT at the daemon level, not on the demodulator interface.
- **PDN never touches `IModem` anyway.** That is the internal and plugin-facing interface; PDN talks to the daemon over KISS and the API, where a per-frame `snrDb` already rides the quality frame and already lands in the frame log.
- **The continuous number already exists internally.** `EnergyBusyDetector` computes block power against a rolling noise floor on every block for every modem, and feeds `ChannelBusy` from it. Whatever a host ends up wanting is an exposure decision on the daemon's own surface, not a new modem capability.

So the work, when it happens, is small and on the host-facing API, and it should wait until PDN's side exists and can say what it wants. Designing a contract before its consumer is how you get the wrong one.

**One thing to remember when a field does appear: say what scale it is on.** This codebase has been bitten twice. The FM masks are CNR in the receiver IF bandwidth and deliberately not SNR3k, with the failure messages saying so. `BurstSnrMonitor`'s own summary warns that its `snr_db` is mean in-band power against a rolling minimum floor, is not the 3 kHz-referenced SNR the ladders use, and that comparing them without converting "is exactly the mistake this remark exists to prevent". A radio's RSSI is a third scale again: absolute dBm across the whole channel, silent on whether the signal is ours or decodable. A value carrying its unit and its source costs nothing and closes that.

The one substantive opinion worth holding: **`CarrierDetect` should stay modem-derived whatever happens**, because it gates transmission and a radio's RSSI can neither tell our signal from anybody's carrier nor survive a serial round trip in time. `ChannelBusy` is the one where a radio reading genuinely adds something, since it sees interferers outside our occupied bins that an in-band detector never will. (That opinion was tested on air and half of it did not survive: see [carrier-sense.md](../carrier-sense.md).)

### What the radios can report, which is the part that matters here

From the CCDI protocol manual (MMA-00038-06), `QUERY` with `[QUERY_TYPE]` = 5:

| CCTM command | Returns |
|---|---|
| 063 | averaged RSSI, int in 0.1 dBm |
| 064 | raw instantaneous RSSI, int16 in 0.1 dBm |
| 318 / 319 | forward / reverse power, 0 to 1200 mV |
| 047 | PA temperature |

Worked example from the manual: `q045063 5D` returns `j07063-488 C5`, meaning -48.8 dBm. **That is an absolutely calibrated dBm figure**, subject to the radio having had its RSSI calibrated per the calibration manual, which is per-radio and worth confirming rather than assuming.

**This is what fixes stage 3.** That stage admits it cannot pin the CNR-in-IF axis honestly and falls back to quoting pad attenuation as an arbitrary but repeatable axis. A calibrated dBm reading from the radio under test, at the moment of the burst, is a real axis instead. Tom already has USB serial to the radios, so this is available now and needs no new hardware.

Two practical notes. **Averaged and raw are different instruments**: 063 is smoothed telemetry and will not track a one-second burst, 064 might if the poll can be timed against it. And **318/319 give a free drive and VSWR sanity check** for stage 2, where getting the level chain right is the whole job.

### The serial link is a measurement dependency, so treat it as one

The 3DK manual's caution that a Tait radio should not drive a line longer than 3.0 m is not load-bearing for a short USB-serial lead, and Tom's already works. It is worth understanding anyway, because of how this class of link fails.

The radio's TXD swings 0 V to +3V3 only. Tait calls the lines negative logic: 0 V is a logic high and 3V3 a logic low, which is RS-232's polarity but with the positive rail alone standing in for a bipolar swing. A conforming RS-232 receiver is specified to read above +3 V as one state and below -3 V as the other, with everything between undefined, so this signal just clears one threshold and never approaches the other. It works because real receivers switch near +1.4 V with hysteresis rather than at the specification limits. Two things eat what margin is left as a cable grows: capacitance, since RS-232's own limit is 2500 pF and ordinary cable runs 50 to 100 pF per metre, and a weak driver into capacitance rounds the edges, which moves the crossing point against a threshold that is not centred in the swing and shows up as duty-cycle distortion; and ground offset, since the signal is single-ended with one logic state sitting at 0 V, so any ground difference between radio and host comes straight off the noise margin. The TMAA01-05 Options-Extender Board fits real RS-232 line drivers and restores a bipolar swing, which is why it is the answer for long runs.

**The part that matters for the campaign**: a marginal link of this kind does not fail cleanly. It degrades into occasional framing and checksum errors at high baud or long cable, which for RSSI means silently missing or corrupt readings rather than a dead link, and **a measurement axis with silent dropouts is worse than no axis**. Two cheap mitigations, both worth doing regardless of cable length:

- **Validate the CCDI checksum on every response**, count the rejects, and log the reject rate beside the readings. Anything above zero means the axis is suspect and should be said out loud.
- **Do not run the CCDI link at 115200 just because it is offered.** An RSSI query is about eight characters and its response about ten, so a poll is roughly twenty bytes. At 9600 that is about 20 ms, ample for per-burst polling, with twelve times the timing margin.

## The cells worth the air time, and the ones that are not

Air time is cheaper than it feels. A 256-byte QPSK burst on a narrow profile is about 21 symbols, so roughly a second on air; 50 frames is under a minute a cell. Four cells at five signal points is about twenty minutes of keying. That is affordable, which is the argument for doing it properly rather than sampling thinly.

**Four cells, all on the narrow profile unless stated:**

1. **QPSK, convolutional rate 1/2.** The workhorse, and the cell with the most simulation history behind it.
2. **QPSK, uncoded.** The control. The 5 to 6 dB coding claim is this modem's headline number and it came from 8 seeds a point on a model; cell 1 against cell 2 on real hardware is the single most valuable measurement of the campaign.
3. **One dense rung, QAM-16 or QAM-64 at rate 2/3.** The model says IF truncation puts a distortion floor under even a noiseless link, third harmonic around -25 dB on an 8 kHz filter at full deviation, and that is the ceiling a dense constellation meets before it ever meets noise. A real radio either shows that or does not, and it is the prediction most likely to be wrong.
4. **One data-port cell on a wide profile, QPSK rate 1/2.** The mic and data-port paths are different channels, not the same channel louder: the measured per-carrier SNR is flat on one (de-emphasis having undone the discriminator's rising noise) and has a 17 dB gradient on the other. Every wide profile assumes the data port, and none of that has met a radio.

**Explicitly not in the first campaign**: the full thirteen-rung ladder, adaptive rate, LDPC, bit loading, and every profile at once. Each of those is a second-order question and each one needs the first-order answers to be trustworthy first. Bit loading in particular is recorded as helpful only on flat data-port paths and actively harmful on emphasised mic paths, so it is a stage-4 question at the earliest.

## What a null looks like, decided in advance

Deciding this now is what stops a bad session turning into an afternoon of theories.

- **Nothing acquires, nothing decodes.** In order: levels and deviation (measure, do not reason), then the radio's actual audio passband against the profile's occupancy, then the clock. **Not the sync threshold**: it was swept to 0.6, 0.45 and 0.3 in simulation and changed no cell at all, acquisition succeeding and the payload failing instead. That path is closed and re-opening it wastes a session.
- **Acquires, header fails.** With no flutter the coded header read 64 of 64 at every level where the burst could be acquired at all. So a header failure on a fixed link is the channel doing something the model does not contain: companding, an AF AGC pumping, or clipping.
- **Acquires, header good, payload fails.** Read the pre-FEC bit error rate the decoder reports. It is a smooth margin measure and it says how far off in dB, where the CRC says one bit too late.
- **Intermittent and correlated with keying.** TXDELAY, and only TXDELAY, since squelch is never in the path. If shortening the burst helps and lengthening TXDELAY does not, suspect the level chain settling rather than the timing.
- **Frames appear that were never sent.** The correlator committing on band noise and a header passing its CRC by chance. Stage 0b measures how often that happens before it can be mistaken for something else.

## Instrument audit before trusting any number

The lesson this project has paid for more than once: audit the rig as hard as the code.

- **Confirm the daemon logged the profile you meant.** A station whose geometry file is missing falls back to a small synthetic layout that resembles nothing. On a bench that is a helpful fallback; on air, with one end misconfigured, it looks exactly like total failure. The start-up line naming the loaded modes is the check.
- **Confirm both ends are the build you think.** A silently re-used binary has produced a whole table of plausible numbers in this project before.
- **Confirm the capture actually captured**, at the start of the session and not at the end.
- **Change one thing between runs.** Two changes and a moved number tells you nothing.
- **State N every time**, and no single-cell headline. That rule was written after a headline cell turned out not to clear significance at all.

## Ledger obligations

- A dated entry in [docs/dev/mode-validation.md](../mode-validation.md), per the standing rule.
- **Which modes the validation matrix tracks is a separate question** from whether a mode has been on the air, and on-air success does not change it. Say what was measured, on what, and leave the matrix rule where it is.

## Open, and needing Tom

Answered 2026-08-20: squelch is never in the path; the radios are TM8110s, which are TM8100 series so the calibration carries; interfaces are 2 to 3 weeks out in two variants.

Still open:

- **A second USB audio dongle.** Answered 2026-08-20: none to hand, so stage 1 is blocked on a purchase rather than on anything hard. Worth making, because otherwise the clock question and the radio path land on the same day. Stage 0b still needs nothing that does not already exist.
- **Nothing on the interfaces.** Answered 2026-08-20: USB serial to the radios already exists and the internal board design is fine, so the RSSI path needs no new hardware and stage 3 can have a calibrated axis whenever someone writes the polling.
- **Who writes the CCDI poller**, and whether it lives in `sm-ota` beside the other instruments or in the daemon. It is the only thing between stage 3 and a real signal axis.
- **Which interface variant gets characterised first**, and whether both get the full stage-2 treatment or one is taken as reference and the other differenced against it.
