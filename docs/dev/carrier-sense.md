# Carrier sense on an FM path

**Status: solved by asking the radio instead of the audio, verified on air 2026-09-19 for `ofdm-fm` and made station-wide for every mode on 2026-09-21 (packet-net/pdn-soundmodem#522), with an audio-only fallback for a station that has no control cable.**

The short version, for anyone who does not need the history: audio cannot answer "is this channel busy" on an FM path, for reasons measured below. The radio can, over its control serial link, and does it well: carrier present reads 60 dB above the noise floor, it releases the instant the carrier stops, and a station does not see its own transmission. The rest of this page is why the obvious approaches do not work, which is worth keeping because both of them looked right.

This started as an `ofdm-fm` problem and is not one. The defect is in the model of what a busy channel looks like, so it belongs to every mode a station runs on an FM radio.

## What carrier sense is made of here

`SoundModemChannel.ChannelBusy` is what the station's CSMA will not transmit over. Three things can make it true:

* **we are transmitting** - not a measurement of the channel and nothing is asked about it, because a station's receiver is muted while it keys;
* **the station's `IChannelBusySource`**, if it has one: the radio's own squelch and RSSI read over its control cable, or failing that the shape of the received audio (`FmShapeBusyDetector`);
* **the modems' own answers** - each modem's packet carrier detect, and each modem's in-band energy detector.

The rule that combines them is `CarrierSenseRule.Occupied`, in one place:

* with a source that has an opinion, that opinion decides, ored with any modem's **carrier detect**. Every modem's **energy detect** is dropped entirely.
* with no source, or a source that answers null, it is the OR across every modem's own answer, which is what this has always been.

**Null is a real answer and means "I do not know".** It is never treated as busy.

## Defect one: the sync detect cannot see a lead-in, so stations collide

TXDELAY is SILENCE in front of the preamble. For its whole length a station is holding the channel and producing nothing for a correlator to find. At the shipped 300 ms default that is a 300 ms window per transmission in which the channel is busy and reads clear.

Measured, from both stations' frame logs during a connected-mode session:

```
radio2 tx  21:36:20.730  21.126  21.522  21.918
radio1 tx  21:36:21.080  21.481  21.877  22.272
```

Both ends had four I frames queued and both transmitted them at once. radio1 keyed about 50 ms into radio2's lead-in. Neither decoded any of the other's frames.

## Defect two: the energy detect is backwards on FM, and worse than useless

An FM receiver with the squelch open is LOUD when idle and QUIET when a carrier arrives, because the carrier captures the discriminator and replaces the noise with the modulation. Measured on radio1, 5 ms rms blocks:

| state | level |
|---|---|
| idle, squelch open | -15.9 dBFS |
| far end modulating | -25 dBFS |
| far end's carrier, unmodulated | -55 dBFS |

A detector waiting for a RISE cannot fire on any of that. Worse, its floor sags to the quieted level during a burst, so when the noise returns about 9 dB louder at the end of the burst it reads the return of the noise as a signal. Simulated against the host's own source with these levels: busy for 9.6 s after a 0.4 s burst, 11.0 s after a 1 s one. Ored into `ChannelBusy` that is a station which cannot answer anybody for ten seconds after being spoken to, which is exactly the shape of the failure.

**And it is not a level that wants tuning.** Measured again in 2026-09 through the C4FSK modems' own receive filters, in the energy detector's own 20 ms blocks:

| filter | idle | burst | burst minus idle |
|---|---|---|---|
| `c4fsk19200` (1.5 x 9600) | -16.0 dB | -18.9 dB | **-2.9 dB** |
| `c4fsk9600` (1.5 x 4800) | -17.0 dB | -18.9 dB | **-1.9 dB** |

Over a 100 s recording holding 29 s of transmissions the detector asserted **once**, at the instant a transmission ended and the hiss came back. The idle channel's block-to-block scatter straddles the burst completely: the quietest idle block sits below the loudest burst block. No in-band level threshold separates them in either direction, at any value that does not also fire on idle noise.

## The first replacement, and why it is wired to nothing

`FmQuietingBusyDetector` asserts busy on the QUIETING rather than on a rise, which sees a carrier whether or not it is modulating and therefore closes the lead-in window. It has ten tests, including one for the failure that matters most: a noise floor that falls and stays down latched the first draft permanently busy, so there is now a backstop that discards the floor and relearns.

**On air it stopped one of the two stations transmitting at all.** Deployed to both, a connected-mode session produced 22 transmissions from radio1 and ZERO from radio2, where the same build passed a one-way smoke test at 8/8 and 10/10 minutes earlier.

The cause is a design error that a bench of one station cannot expose. **The thresholds are absolute dBFS, and the two stations are not the same.** Their own transmit gating, which the detector must not mistake for a carrier, reads:

| station | audio while transmitting |
|---|---|
| radio1 | below -86 dBFS |
| radio2 | -63.5 dBFS |

22 dB apart. The guard that separates "dead or gated input" from "a carrier" was set at -72 dBFS from radio1's figure, which puts radio2's own gating on the wrong side of it.

## What the right design actually was

Not a better threshold, and not the contract change first proposed here. **The radio already knows.** It has a squelch, a hardware DCD line and a calibrated RSSI meter, and it will tell you over the serial port it is already cabled to for programming. Everything absolute and station-dependent in the audio detectors existed to reconstruct, badly, from audio, a fact a chip in the radio had all along.

This costs a hard dependency: a radio with its data port programmed for command mode, and a cable to it. For a station that wants carrier sense that works, that is a reasonable thing to require. What it must not cost is the ability to transmit at all when that cable is not there, which is the next section.

## What ships here

`IChannelBusySource` is the seam: one property, `bool? Busy`, where **null means "no opinion"** and never means busy. Everything else lives beside it in `Packet.SoundModem.CarrierSense`:

| | |
|---|---|
| `IChannelBusySource` | the seam |
| `CarrierSenseRule` | the one rule that combines a radio's opinion with the modems' own |
| `RadioBusySource` | the deciding, over any `IRadioControl` |
| `TaitCarrierSense` | the standalone opener: reads the station's settings and talks to its own Tait |
| `ChannelBusySources` | `Host`, where an in-process host registers its already-open radio |
| `StationRadio` | the legacy untracked station file |
| `FmShapeBusyDetector` | the audio-only fallback, for a station with no cable to its radio |
| `FmQuietingBusyDetector` | the first attempt at that, kept as a record and wired to nothing |

A station switches it on with a `carrierSense` section in `soundmodem.json`; see [the configuration reference](../reference/config.md#carriersense).

The in-process case is what the `Host` static exists for. Run inside a host that already owns the serial link, opening it a second time from in here would at best fail and at worst fight the owner for the port. The host registers its own already-open radio before it builds the channel and the same deciding runs over it.

## It fails open, deliberately

Every path that does not know answers null, and null is treated as clear. A port that will not open, a radio in the wrong mode, a pulled USB cable, RSSI reads that start failing mid-session: all of them cost carrier sense and nothing else, leaving the station exactly where it was before this feature existed.

This is a judgement, and it is the right way round. A carrier sense that can *silence* a station by losing a serial cable is worse than no carrier sense at all, and this bench has already met that failure once: the quieting detector stopped radio2 transmitting entirely. The catch around opening the radio is `catch (Exception e)` with no filter, on purpose, and it is the most important line in the feature. A modem is constructed during daemon start-up, so anything thrown there does not cost carrier sense, it costs the station its whole service. A `DllNotFoundException` out of a native serial library is a live possibility whenever dependencies are resolved out of a directory somebody else assembled.

## What it measured, on air

A standalone tool watched what the modem would see, against the real radio, without involving the daemon. Both stations, 2026-09-19:

| | radio1 | radio2 |
|---|---|---|
| idle noise floor (median) | -94.3 dBm | -125.6 dBm |
| far end transmitting | -34.4 dBm | -35.7 dBm |
| margin | 60 dB | 90 dB |
| threshold chosen | -75 dBm | -110 dBm |

**The two floors are 31 dB apart on two nominally identical stations.** That is the same class of problem that sank the quieting detector, and it is why the threshold has to be configured per station and why the tool measured the floor and suggested a number rather than leaving it to be copied.

Keying the far end, radio1 saw DCD assert and RSSI jump to -34.4 dBm together, and **both released within 0.4 s of the carrier stopping**, against ten seconds or more of false busy from the energy detect.

Watching a station during its OWN transmission, proven to be keying by the far end seeing it at -35.7 dBm:

```
     time  dcd    rssi       busy
      0.1    ?     -94.9 dBm   clear
      5.1    ?     -93.6 dBm   clear     <- its own 6 s carrier is in here
     10.1    ?     -93.6 dBm   clear
```

**A station does not see its own transmission**, because its receiver is muted while it transmits. The failure that killed the quieting detector is not reachable this way.

One real property to know: **DCD reads null until the first squelch edge after the radio's unsolicited progress messages are enabled.** On a quiet channel no edge ever comes, so DCD alone reports nothing at all until someone transmits. An RSSI threshold covers that cold start, which is most of why it is worth having on a data station whose squelch is held open anyway.

## Verified on air, both stations

* Daemon starts, the port opens, the native serial library resolves: `carrier sense from the radio on /dev/ttyUSB0 at 28800 baud, DCD plus RSSI above -75 dBm every 100 ms`.
* Both stations still transmit: 14/15 and 13/15 UI frames delivered, both directions. **This is the regression that matters**, and it is what the quieting detector failed.
* Deferral works: frames offered 2 s into a far-end transmission were held and went out after it stopped, on 4 of 4 valid trials. Release was 0.6 to 5 s after the channel cleared, varying between runs, which is the host's CSMA backoff rather than anything latching here.
* Control: with the channel clear, the same frames go out 0.8 s after being offered.

## Known operational conflict

With carrier sense on, **the daemon holds the serial port**, and a serial port cannot be opened twice. That breaks any separate signal-level probe against the same port, including the campaign harness's own. To measure signal levels, take the station's radio config away and restart.

## The second blocker: another modem on the same channel

Radio carrier sense went in and the session STILL failed, in a way that looked like the reverse path being dead: the caller transmitted 21 frames and received nothing. The frame logs said otherwise, and this is why both stations' logs are worth pulling rather than believing one end's:

```
radio1: 21 transmitted,  0 received
radio2:  0 transmitted, 21 received (21 with good CRC, SNR 10 to 13 dB)
```

**radio2 never transmitted at all.** It heard every frame perfectly and its `axcall` generated every reply; nothing reached the air. The reverse path was never the problem.

Not this feature, either: watching radio2's radio through a session-like burst pattern, its carrier sense asserted for each burst and went CLEAR 0.6 s later, leaving about 2.4 s of clear channel in every 3. The gate was open and the station still would not transmit.

Measuring the hold directly, by handing frames straight to the KISS port and timing them to air:

| | radio2 to air |
|---|---|
| quiet channel | 0.5 s |
| 2 s after hearing ONE burst | 8.1 s |

About ten seconds from the end of a burst, which is the energy detector's hangover exactly. Both stations were configured with a second sub-channel, `afsk1200`, alongside the OFDM-FM mode. An AFSK modem's channel-busy is "packet DCD or any significant in-band energy", and an OFDM burst is certainly that. So the AFSK modem heard every OFDM burst, latched busy for ten seconds on the FM noise-floor sag described above, and **the transmit gate was the OR across every modem on the channel**. A modem nobody was using held the transmitter shut.

That also explains the asymmetry that made it look like a dead receive path. radio2 heard radio1 constantly, so radio2 was locked out permanently. radio1 heard nothing back, so radio1's AFSK modem never asserted and radio1 transmitted freely. One station talking, one station mute, and neither of them faulty.

Removing the unused `afsk1200` sub-channel, on this bench, 2026-09-19:

| | before | after |
|---|---|---|
| quiet channel | 0.5 s | 0.7 s |
| 2 s after hearing one burst | 8.1 s | 0.7 s |

and the session connects, carries 12 of 12 lines in each direction, and disconnects cleanly, three runs out of three.

**That workaround is no longer needed on a station with a radio to ask**, and the fix is structural rather than a matter of which modes an operator runs: the OR is taken over carrier DETECTS and not over energy detects the moment a source has an opinion, so an unused sub-channel cannot reach the decision. `OpenSquelchFmCarrierSenseTests` holds it.

### Proved on air, 2026-09-21

The same measurement, on the same bench, with the `afsk1200` sub-channel deliberately put back on radio2. Time from handing a frame to the KISS port until the station keyed, measured on the station's own clock:

| radio2 | quiet channel | 2 s after hearing one burst |
|---|---|---|
| radio consulted (`"radio": "tait"`) | 0.50, 1.57 s | **0.50, 1.19, 1.31 s** |
| audio only (`"radio": "none"`) | 0.60, 0.88 s | **9.73, 9.92, 10.23 s** |

Both configurations answer in well under a second on a quiet channel, so the difference is not the CSMA and not the link: it is ten seconds of hangover from an energy detector reading the return of the open-squelch noise as a signal. The scatter inside each row is the p-persistence roll at 100 ms slots.

**The general lesson survives the fix.** Carrier sense is only as good as the worst thing feeding it, and a station without a control cable to its radio was still in the position radio2 was in, which is what the next section is about.

## What #522 changed

Everything above was built for `ofdm-fm` and scoped to it: the whole stack lived in the `OfdmFm` namespace and `OfdmFmModem` was the only modem that consulted it. Meanwhile every other modem on the same FM station was still running the energy detector that points the wrong way. So:

1. The stack moved to `Packet.SoundModem.CarrierSense` and stopped belonging to a mode family.
2. The decision moved from the modem to `SoundModemChannel`, which is where "the station" exists. One source is opened per station rather than one per modem, and one rule decides.
3. `carrierSense` became a section of `soundmodem.json`, beside `ptt`, which is its nearest relative. The untracked `ofdm-fm.station.json` still works and says at start-up that it should be moved.
4. `FmModeProfiles.IsFmMode` stopped meaning "does Nino publish a deviation figure for this mode". It did, so every `ofdm-fm-*` preset reported that it was not an FM mode: the one family that proved this whole argument, excluded by the predicate anything would key the argument off.
5. `OpenSquelchFmReceiver` gave the suite a channel model in which a transmission makes the receiver quieter. **Every other audio fixture here puts the noise below the signal** - the loopback has digital silence between bursts, the AWGN and Watterson ladders inject their lead-in noise at the burst's own signal-to-noise ratio. That is why an inverted assumption survived the whole suite and was only found on air. The energy detector's behaviour on it is pinned rather than fixed, with a control run proving the harness fires on an additive path.

## The station with no control cable

A station with no serial link to its radio, or a radio that is not a Tait, reads carrier sense off the SHAPE of the received spectrum: `FmShapeBusyDetector`. It is on by default (`carrierSense.audioFallback`), because off is not a neutral choice - off is the ten-second hangover measured above.

**The rule it had to obey is that no absolute level appears anywhere in the decision**, since an absolute level is exactly what silenced radio2. Every threshold in it is a ratio against something the same station measured itself a few seconds earlier.

### What it measures

Quieting is not uniform across the band, and that is the whole opportunity. A carrier removes noise hardest well above the signal's own occupancy, where the modulation puts nothing back. Measured per kilohertz on the reference recording, 1985 idle blocks against 94 whole-burst blocks:

| band | idle | keyed | quieting |
|---|---|---|---|
| 0 to 1 kHz | -24.55 dB | -23.47 dB | **-1.08 dB** |
| 4 to 5 kHz | -25.79 | -31.53 | 5.74 |
| 7 to 8 kHz | -27.96 | -47.90 | 19.94 |
| 9 to 10 kHz | -30.37 | -58.27 | **27.90** |
| 16 to 17 kHz | -44.66 | -64.41 | 19.75 |
| 23 to 24 kHz | -61.66 | -76.38 | 14.73 |

So the statistic is the ratio of power below a sixth of the sample rate to power above it: 7.55 dB idle, 30.57 dB keyed, a 23.0 dB rise. The split has to sit just above the widest signal the channel carries, because a modulated signal only quiets the band its own modulation does not fill; swept from 7 to 18 kHz the margin peaks at 8 kHz and falls monotonically either side. An UNMODULATED carrier, which is what a far end's silent TXDELAY puts on the air and which nothing else here can see at all, quiets by 32 to 54 dB at every frequency.

Idle hiss on this path FALLS with frequency, by 37 dB across the band, where textbook post-detection FM noise rises. That is one receive path's own response, and it is the clearest possible argument against a fixed threshold on any band ratio.

### What it scores

| | reference capture, radio1, 15 NinoTNC transmissions | 660 s chunk, radio1, 11 of ours | 60 s, **radio2**, 15 of ours |
|---|---|---|---|
| found | 15 of 15 | 11 of 11 | 15 of 15 |
| assert latency after carrier up | median 26 ms | median 21 ms | 170 to 220 ms before the decode |
| **false busy** | **0 blocks** | **0 blocks in 583 s of idle** | **none in 44 s of idle** |

Zero false busy in 620 s of idle channel altogether, and the relative threshold gives 0 missed and 0 false at every value from 4 to 20 dB, which is what buying the margin was for.

### What it refuses to answer

The level gate is not a quality check, it is the detector asking whether this is a path it understands: one where a signal does NOT change the level much. Digital silence, a muted or dead card, a squelched receiver, receive audio gated by the station's own transmission at either bench station's level, and an additive path where a signal ADDS 40 dB all fail it, and all return null rather than busy. On an additive path `EnergyBusyDetector` is the right instrument and keeps the job.

Put the same channel through seven simulated station audio paths, from a card rolling off at 6 kHz to one at 15 kHz and 20 dB of gain either way: the raw statistic moves 9.6 dB and **the decision quantity does not move at all**. A fixed 15 dB threshold, which is inside the range that looked reasonable on one station, reads the 6 kHz station as permanently busy on an empty channel. That is the radio2 incident reproduced in simulation, and it is why the reference is the station's own.

### What it cannot do

**It does not work on a 12 kHz channel.** The split would land at 2 kHz, inside a wideband mode's own occupancy: measured on the reference recording decimated, and cross-checked against an ideal brickwall to take the decimator out of the question, the best margin over every split from 1 to 4 kHz is 0.6 dB against 19.3 dB at 48 kHz. It is not used below 48 kHz and the journal says so at start-up. A station that wants carrier sense on a 12 kHz channel needs the control cable.

**Its evidence is thin on stations and on waveforms.** All 15 transmissions on the reference recording are the same NinoTNC frame sample for sample, so a statistic tuned on it has seen one waveform fifteen times, and the 660 s chunk adds our own transmissions at six drive levels off the same receiver. One generalisation check exists and it passed: 60 s captured off **radio2**, a second sound card and a second Tait, carrying 15 `ofdm-fm-8k` transmissions from radio1 rather than NinoTNC C4FSK, scored against radio2's own frame log. Every one of the 15 fell inside a detected episode, no episode corresponded to anything else, and the 17 s of idle before the first and 27 s after the last produced no busy at all. Three of the transmissions arrived within a quarter of a second of each other and were reported as one episode, which is the 100 ms hold doing its job. That is two stations and two waveforms, not a survey.

`#502`, telling the modem it is transmitting rather than making it infer that from a level, would make the dead-input gate belt and braces rather than load-bearing. At the channel it is nearly free already: `SoundModemChannel` does not feed the detector while it transmits.

## The fixtures

Durable copies on the dev box at `/home/tf/fm-carrier-sense-evidence/`, with a README giving the numbers:

* `ninorx.wav` - 45 s, 15 NinoTNC C4FSK 19k2 bursts of about 131 ms against real idle hiss. The reference fixture.
* `radio1-chunk-with-idle-and-bursts.wav` - 660 s, our own transmissions at 0 to -18 dB with long idle stretches, for idle statistics.
* `fresh0db.wav` - 8.2 s, one of our own transmissions at full level.
* `radio2-ofdm-bursts.wav` - 60 s off the OTHER station, 15 `ofdm-fm-8k` transmissions against its own idle channel. The generalisation fixture.

## State of the tree

`FmQuietingBusyDetector` and its ten tests stay, unconsulted. The levels it was built from are measured and correct, and a station with no control cable to its radio is still the case it was written for. It is not wired to anything.

`EnergyBusyDetector` stays and is still right for an additive path: SSB, a wired loop, a squelched receiver. What changed is which source a station consults, not whether the class exists.

The contract change proposed earlier, telling the modem it is transmitting rather than making it infer it from a level, is still worth having and is still packet-net/pdn-soundmodem#502.

C4FSK hard-gates its receive bit path on carrier sense, which is a separate layering error: carrier sense is a transmit-side decision and should never have reached a receive path. That is packet-net/pdn-soundmodem#518.
