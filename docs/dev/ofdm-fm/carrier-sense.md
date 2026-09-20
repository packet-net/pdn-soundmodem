# Carrier sense on an FM path

**Status: solved on the bench by asking the radio instead of the audio, and verified on air 2026-09-19. What this repository ships is the seam, not the radio driver; see "What ships here" below.**

The short version, for anyone who does not need the history: audio cannot answer this question on an FM path, for reasons measured below. The radio can, over its control serial link, and does it well: carrier present reads 60 dB above the noise floor, it releases the instant the carrier stops, and a station does not see its own transmission. The rest of this page is why the obvious approaches do not work, which is worth keeping because both of them looked right.

Connected-mode sessions on this bench did not converge, and then they did: 12 of 12 lines each way, three runs out of three, clean connect and disconnect. It took TWO fixes, and the second one is the more surprising. This is the whole investigation, including the part where the fix made it worse.

## What carrier sense is made of here

`OfdmFmModem.ChannelBusy` is what the host's CSMA will not transmit over. It has two sources:

* a **sync detect**, true from the moment a burst's sync correlation crosses its threshold until the burst is decoded or abandoned;
* an **energy detect** from the host package, which asserts when audio rises above a slowly adapting noise floor.

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

## The replacement, and why it is not wired to anything

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

This costs a hard dependency: a radio with its data port programmed for command mode, and a cable to it. For a station that wants carrier sense that works, that is a reasonable thing to require.

## What ships here

`IChannelBusySource` is the seam: one property, `bool? Busy`, where **null means "no opinion"** and never means busy. `ChannelBusySources.Host` is where a host registers one, before the first modem is built, and `OfdmFmModem` reads it once at construction.

**Only the seam is in this repository.** The radio-derived source that the bench ran, and the standalone opener that read a station file and talked to the radio itself, depend on a radio-control library this repository does not carry, so they are not here. Anything that can answer "is the channel occupied" implements the interface, and the modem only ever asks for a `bool?`, so nothing downstream cares what is behind it.

The in-process case is the one the seam exists for. Run inside a host that already owns the serial link, opening it a second time from in here would at best fail and at worst fight the owner for the port. The host registers its own already-open radio and the same deciding runs over it.

With a source registered, `ChannelBusy` is `CarrierDetect || (Busy ?? false)` and **the audio energy detect is dropped entirely**, because on this path it is not merely redundant but actively harmful. Without one, nothing changes for anybody.

## It fails open, deliberately

Every path that does not know answers null, and null is treated as clear. A port that will not open, a radio in the wrong mode, a pulled USB cable, RSSI reads that start failing mid-session: all of them cost carrier sense and nothing else, leaving the station exactly where it was before this feature existed.

This is a judgement, and it is the right way round. A carrier sense that can *silence* a station by losing a serial cable is worse than no carrier sense at all, and this bench has already met that failure once: the quieting detector stopped radio2 transmitting entirely. In the station build the catch around opening the radio was `catch (Exception e)` with no filter, on purpose, and it was the most important line in the feature. A modem is constructed during daemon start-up, so anything thrown there does not cost carrier sense, it costs the station its whole service. A `DllNotFoundException` out of a native serial library is a live possibility whenever dependencies are resolved out of a directory somebody else assembled.

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

About ten seconds from the end of a burst, which is the energy detector's hangover exactly (9.6 s after a 0.4 s burst, 11.0 s after a 1 s one). But this modem stops consulting the energy detector the moment a busy source is registered, so it was not ours. The host's own documentation says where it came from:

> `SoundModemChannel.ChannelBusy`: True while **any modem** sees packet or energy busy, or we are transmitting.

Both stations were configured with a second sub-channel, `afsk1200`, alongside the OFDM-FM mode. An AFSK modem's channel-busy is "packet DCD or any significant in-band energy", and an OFDM burst is certainly that. So the AFSK modem heard every OFDM burst, latched busy for ten seconds on the FM noise-floor sag described above, and **the channel's transmit gate is the OR across every modem on it**. A modem nobody was using held the transmitter shut.

That also explains the asymmetry that made it look like a dead receive path. radio2 heard radio1 constantly, so radio2 was locked out permanently. radio1 heard nothing back, so radio1's AFSK modem never asserted and radio1 transmitted freely. One station talking, one station mute, and neither of them faulty.

Removing the unused `afsk1200` sub-channel, on this bench, 2026-09-19:

| | before | after |
|---|---|---|
| quiet channel | 0.5 s | 0.7 s |
| 2 s after hearing one burst | 8.1 s | 0.7 s |

and the session connects, carries 12 of 12 lines in each direction, and disconnects cleanly, three runs out of three.

**The general lesson is worth more than the fix.** Carrier sense is only as good as the WORST modem sharing the channel, and adding a modem to listen with can silence the one you are transmitting with. On an FM path every audio-energy modem carries this defect, so a station using OFDM-FM in connected mode should not run one alongside unless it needs it.

## State of the tree

Both bench stations ran one OFDM-FM mode alone; the `afsk1200` sub-channel was removed and stayed out.

`FmQuietingBusyDetector` and its ten tests stay, unconsulted. The levels it was built from are measured and correct, and a station with no control cable to its radio is still the case it was written for. It is not wired to anything.

The contract change proposed earlier, telling the modem it is transmitting rather than making it infer that from a level, is still worth having and is still packet-net/pdn-soundmodem#502. It is no longer on the critical path.

A silence-detecting busy detector is deliberately deferred.
