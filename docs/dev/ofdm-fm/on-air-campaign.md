# OFDM-FM on air: the campaign

The hardware arrived. [on-air-plan.md](on-air-plan.md) is the pre-flight, written 2026-08-20 before anyone had a radio on a bench; this is the campaign it asked for, rewritten against what the bench turned out to be. Read the pre-flight first. Everything it says about the gate, about capture, and about what a null looks like still stands and is not repeated here.

What follows is only the parts that changed, plus the running order.

## The bench, as measured on 2026-09-18

Two Raspberry Pi 4Bs, `radio1` (10.45.0.241) and `radio2` (10.45.0.73), each with a Tait TM8110 on a CM108 dongle, joined by coax through 100 dB of attenuation, both radios on channel 0 in the 136 to 174 MHz band at their lowest power setting. Callsigns M0LTE and M9YYY, both Tom's, and the path is coax in any case.

| | radio1 | radio2 |
|---|---|---|
| Radio | TM8110, serial 19925369 | TM8110, serial 19925328 |
| Firmware / FPGA | QMA1F_std_02.18.00.00 / QMA1G_std_2.04.00.0001 | identical |
| CCDI | 03.02, Mic connector, 28800 baud, command mode at power-up | identical |
| `pdn-soundmodem` | 0.71.1 (apt) | 0.71.1 (apt) |
| `axcall` / `tait-codeplug` | 0.11.1 / 0.14.0 | identical |
| Audio | C-Media CM108, `plughw:CARD=Device,DEV=0` | identical |
| PTT | CM108 GPIO 3 on `/dev/hidraw0` | identical |
| KISS | `0.0.0.0:8105`, reachable from the dev box | identical |

A matched pair to within a decibel everywhere it was measured, which is what makes a difference between them worth believing later.

## What the bench changed about the plan

Five things, and three of them are better than the pre-flight assumed.

### The audio path is flat to about 5.8 kHz, not 300 to 3000 Hz

The pre-flight expected the TM8110's voice passband and warned that the profiles then in use sat "edge to edge with essentially no margin at either end". That is wrong on this interface, and it is wrong in our favour.

**The formal measurement.** A stepped single tone at 31 frequencies from 100 Hz to 8 kHz, keyed from radio1 by `POST /api/txtest` at a fixed drive, recorded at radio2 straight off the CM108, and measured inside the quieted window at each step. Using the quieted window matters: a carrier drops the receiver's noise from about -16 dBFS to -62 dBFS, so the tone is only measurable once you window on the keyed stretch rather than the file. Every recovered tone was checked to be at the frequency that was actually sent.

| Hz | rel. dB | | Hz | rel. dB | | Hz | rel. dB |
|---|---|---|---|---|---|---|---|
| 100 | -1.07 | | 1000 | -0.07 | | 4000 | -0.63 |
| 200 | -0.29 | | 1500 | -0.17 | | 4500 | -1.32 |
| 300 | -0.10 | | 2000 | -0.13 | | 5000 | -2.18 |
| 400 | 0.00 | | 2500 | -0.21 | | 5500 | -3.01 |
| 500 | -0.01 | | 2900 | -0.20 | | 6000 | -3.77 |
| 600 | -0.03 | | 3000 | -0.12 | | 7000 | -6.41 |
| 800 | -0.02 | | 3500 | -0.30 | | 8000 | -8.52 |

**Flat within 0.2 dB from 200 Hz to 3000 Hz**, -1.1 dB at 100 Hz, and a gentle roll-off above 3 kHz reaching -3 dB at about 5.8 kHz.

**Repeated in both directions** at the corrected drive, which is the better dataset: all 31 tones captured each way, much higher tone-to-noise, and it differences the two interfaces against each other.

| Hz | 1 to 2 | 2 to 1 | | Hz | 1 to 2 | 2 to 1 |
|---|---|---|---|---|---|---|
| 100 | -0.96 | -1.02 | | 3000 | -0.50 | -0.31 |
| 200 | -0.23 | -0.23 | | 3500 | -0.40 | -0.41 |
| 300 | -0.05 | -0.04 | | 4000 | -0.66 | -0.62 |
| 500 | -0.01 | 0.00 | | 4500 | -0.86 | -0.96 |
| 1000 | -0.04 | -0.05 | | 5000 | -1.16 | -2.14 |
| 1500 | -0.05 | -0.10 | | 6000 | -2.65 | -2.84 |
| 2000 | -0.14 | -0.17 | | 7000 | -6.11 | -5.15 |
| 2500 | -0.24 | -0.22 | | 8000 | -9.38 | -8.88 |

**The two interfaces are the same interface, to within a tenth of a decibel.** Across 100 Hz to 4500 Hz the two directions agree within 0.1 dB at every point; the mean difference over all 31 tones is -0.02 dB, and the only excursions approaching a decibel are at 5 to 7 kHz where the roll-off is steep and a small frequency error becomes a large level one. Their absolute references match too: -21.60 dBFS one way and -21.45 dBFS the other.

That is worth more than it looks. The pre-flight's argument for building two interface variants was that "anything that behaves the same on both is a property of the waveform, and anything that does not is a property of the interface". These two are the same variant, built by hand, twice, and they have just demonstrated that the build is repeatable. So a difference between the stations from here on is not the boards.

Two things follow from the *flatness* rather than the width, and they are the more useful half:

- **The response is flat, not tilted.** An FM discriminator's own noise rises at 6 dB per octave, so a flat end-to-end response means transmit pre-emphasis and receive de-emphasis are both in the path and cancel. Had only one been present, a voice-width profile would have sat under a 19 dB tilt across its occupied band. It does not.
- **So the per-carrier SNR is flat**, which by the pre-flight's own rule puts this bench in the case where bit loading measurably hurts. Bit loading stays off, and that is now a measurement rather than an assumption.

**And more of the band is reachable than expected.** Against a path flat to 6 kHz, a profile occupying a voice passband was using about half the channel. The profiles the stations were carrying that morning are not the ones this repository ships, and what this measurement licensed was the wider layouts of our own that [bandwidth-and-frame-length.md](bandwidth-and-frame-length.md) then built and measured: 211 Hz to 5.0, 6.0 and 7.0 kHz, and later 8.0. Read against the table above, everything to about 6 kHz sits within 4 dB, which the channel-estimate denoiser was built for, so read the per-carrier SNR before blaming anything else; 8 kHz is at -8.5 dB and climbing, and that is an interface fact rather than a waveform one.

### Both receivers were clipping on band noise alone

The CM108 capture gain was at +8 dB, where band noise with no signal present read -7.8 dBFS RMS with its peaks pinned at full scale. An OFDM waveform is the worst possible thing to feed a clipped input. A gain ladder found the operating point:

| Mic capture | peak | RMS | clipped samples |
|---|---|---|---|
| +23 dB | 0.0 dBFS | -1.3 dBFS | 32.91 % |
| +8 dB (as found) | -0.0 dBFS | -7.8 dBFS | 0.09 % |
| **0 dB (set)** | **-6.3 dBFS** | **-15.8 dBFS** | **0.00 %** |
| -6 dB | -6.6 dBFS | -15.8 dBFS | 0.00 % |
| -12 dB | -6.9 dBFS | -15.7 dBFS | 0.00 % |

Both stations are now at 0 dB and the daemon persists it. Note the control saturates: the mixer advertises -12 dB but nothing below 0 dB does anything, so **0 dB is the floor and there is no headroom left in software**. Band noise alone now peaks at -6.3 dBFS. If a real signal comes in hotter than the noise it does, the next move is a resistive pad in the interface, not a mixer setting. Stage B measures that before anything else is believed.

### Stage 1 is no longer blocked

The pre-flight blocked its clock-skew stage on buying a second USB audio dongle. That was written when the work was on one box. There are now two boxes with two CM108s and therefore two independent crystals, so the largest unexercised risk in the modem can be measured on the bench that exists, with nothing bought.

### Stage 3 has its calibrated axis, and it needs no new software

The pre-flight left "who writes the CCDI poller" as the only thing between stage 3 and a real signal axis. Nobody needs to: `tait-cli` is packaged, installs from the packet-net apt repository, and reads the radio directly. Both radios answer on the quiet bench:

```
$ tait-cli /dev/ttyUSB0 rssi
Signal: -124.8 dBm (S4), radio's running average -125.5 dBm (S4)
```

That is the absolutely calibrated dBm figure stage 3 wanted, per burst, from the radio under test. `info`, `channel`, `temp` and `watch` are there too. Two cautions from the pre-flight still apply and are still right: averaged and instantaneous are different instruments, and the reading is channel power that cannot tell our signal from anyone's carrier. On a sealed coax bench the second one stops mattering, which is one more reason to pin the axis here rather than on an antenna.

### The plugin had to be rebuilt, and the failure it was hiding is worth knowing

OFDM-FM ran as a modem plugin at the time, and that plugin was pinned to `pdn-soundmodem` 0.31.0 while both stations ran 0.71.1. `FrameQuality` grew from 11 constructor parameters to 19 over that range and appending an optional parameter is binary-breaking, so the plugin loaded, listed all five modes, logged a clean start-up and then killed the daemon the first time it decoded a frame. On the bench that presents as "transmits, never decodes", which is indistinguishable by eye from a level fault or a dead audio tap.

It was caught by running the two-station pipe **on the station**, against that station's own daemon binary, which is now a standing pre-flight step rather than a thing to do once. The fix was a recompile with no source change.

## Bench state after validation

Both stations ran, and both were left this way. The modem was a plugin then; it is a built-in mode now, so a station today needs no `modemPlugins` entry and names one of the catalogue's `ofdm-fm-*` modes directly.

```json
{
  "bind": "*",
  "device": "plughw:CARD=Device,DEV=0",
  "captureRate": 48000,
  "modems": [
    { "subChannel": 0, "mode": "ofdm-fm-narrow" },
    { "subChannel": 1, "mode": "afsk1200" }
  ],
  "ptt": { "type": "cm108", "device": "/dev/hidraw0" },
  "waterfall": { "port": 8107 }
}
```

The previous afsk1200-only config is kept beside it as `soundmodem.json.afsk-backup`.

**`afsk1200` is deliberately still configured, on sub-channel 1.** It costs nothing, it runs on the same audio at the same DSP rate, and it means every null has a control available in the same minute over the same coax: if afsk1200 crosses the path and OFDM-FM does not, the fault is ours, and if neither crosses it is the rig. The pre-flight's whole "what a null looks like" section gets cheaper with a known-good mode one sub-channel away. (It also, a day later, turned out to be the thing silencing a station in connected mode. See [carrier-sense.md](../carrier-sense.md).)

The channel runs at 48 kHz, which is worth understanding because there is **no config key for it**: `StationFactory` derives the DSP rate from the configured modes, taking 48000 if any configured mode declares it and 12000 otherwise. OFDM-FM declares 48000, so configuring one of its modes is what moves the channel. `captureRate` is the card rate and is a different number.

## Running order

Each stage exits on a number, and no stage starts until the one above it has produced its number. Air time is cheap; a stage skipped to save it is what produces an afternoon of theories.

**Every stage captures audio at both ends, transmit and receive, timestamped.** That rule is from the pre-flight and it is the one that decides whether a session was worth having.

### Stage 0b: false acquisition against real band noise. PARTLY ANSWERED 2026-09-18

**Across an evening of listening on a live 2 m band with antennas up, neither station ever delivered a frame it was not sent.** 499 OFDM frames arrived between the two, and every one carries the callsign of the station that actually transmitted it. Nothing spurious reached the host.

**That is a weaker statement than it looks and the difference matters.** The modem delivers a frame only when its payload CRC passes, and writes `CrcValid: true` when it does, so a committed burst that failed its CRC cannot appear in the frame log at all. "Zero CRC failures" in that table is a property of the code path, not a measurement of the air. What has been shown is that nothing false got THROUGH; what has not been shown is how often the correlator commits to noise and is then caught by a CRC, which is the number that decides how much time a busy channel wastes on ghosts.

That still wants the offline analyser: run the correlator over the raw-capture corpus already on the stations and count sync commits, header reads and header-CRC passes separately. It is the one measurement the channel model structurally cannot produce, and the corpus is sitting there.

### Stage A: bench validation, no transmitter. DONE 2026-09-18

Both daemons load the modem and offer all five modes; all five cross a virtual audio pipe on the station's own binary; both radios answer CCDI; both receive chains pass band noise without clipping; both KISS ports answer from the dev box. Recorded above.

### Stage B: first key, one tone, no modem. DONE 2026-09-18

A 1000 Hz tone from radio1 read **-68.9 dBm** at radio2, rock steady for the whole keying, against a quiet-bench floor of about **-125.5 dBm**. The receiver quiets fully: with a carrier and no modulation the recovered audio drops from -16 dBFS to -62 dBFS. So the RF path is excellent and nothing about it is marginal.

**That is the axis anchor, and it says the bench starts about 50 dB above the radio's sensitivity.** A TM8110 makes 12 dB SINAD at around -119 dBm, so stage F needs roughly 50 dB more attenuation than the bench has to walk a cell down to threshold. See the open question below; it is now a measured requirement rather than a guess.

A real signal does **not** clip the receive chain, and the reason is the one that matters: FM quiets, so the loudest thing the receiver ever produces is band noise with no signal present, which is what the capture gain was already set against. The clipping risk is entirely in the no-signal case.

*Exit met. Still outstanding from this stage: forward and reverse power over CCDI 318/319 as a VSWR check on the coax, which was not read.*

### Stage C: the level chain. DONE 2026-09-18, fixed, and verified on air

**The fix is fitted and measured.** The divider was a 12.5 kHz design running on a 25 kHz channel; the tap-in has moved from T13 to T12, which the radio scales with the channel spacing, and 2k7 is fitted across the 3k3 on each board. Every step landed within 0.11 dB of prediction:

| | sine peak deviation | % of 5 kHz class |
|---|---|---|
| Rt 3k3, T13 (as found) | 1.84 kHz | 37 % |
| Rt 3k3 with 2k7 across it, T13 | 3.23 kHz | 65 % |
| **Rt 3k3 with 2k7 across it, T12** | **4.12 kHz** | **82 %** |

The tap change measured **+2.12 dB against +2.01 dB predicted**, which confirms both that the radio accepts the T12 encoding and that Tait's 0.23 Vp-p per kHz figure for T12 on a 25 kHz channel is right. THD 0.11 % at the new level. `M0LTE/tait-codeplug` moved its profiles to T12 on the strength of this, and pdn-soundmodem's hardware page now documents two variants.

**The number that matters is the burst, not the sine.** A real narrow-profile burst at this setting measures **1.09 kHz rms and 2.84 kHz peak, 57 % of class, at 8.3 dB crest factor**. The waveform backs itself off to leave room for its own peaks, which is what `PeakToAverageLimitDb` is for and is correct. So where a sine sits at 82 % of class, OFDM sits at 57 %, and there is still about 4 to 5 dB available. **That remainder is a software question**, the 0.8 amplitude the modulators send and the crest-factor limiter, and not a resistor one. Do not size the divider to recover it, or the sine case goes over class.

**What it bought, measured by accident.** While the pad was failing underneath us the link lost 13 dB of signal but only 5.5 dB of frame-log SNR, which puts the deviation work at about **7 dB**. That is the only quantitative figure we have for it, and it exists only because two independent things went wrong at once in opposite directions.

### Stage C, as originally written: the fault and how it was found

Transmit drive stepped over the card's whole range, -36 dB to 0 dB on the CM108 `Speaker` control, 1000 Hz tone, measured at the far end:

| Speaker | recovered | 2nd harm | 3rd harm | THD |
|---|---|---|---|---|
| -36 dB | -57.74 dBFS | -33.5 dB | -36.2 dB | 3.28 % |
| -28 dB | -50.47 dBFS | -44.4 dB | -45.2 dB | 0.86 % |
| -20 dB | -42.11 dBFS | -46.0 dB | -48.4 dB | 0.65 % |
| -12 dB | -33.76 dBFS | -52.6 dB | -53.2 dB | 0.33 % |
| -8 dB | -29.75 dBFS | -52.8 dB | -50.5 dB | 0.38 % |
| -4 dB | -25.65 dBFS | -48.9 dB | -47.4 dB | 0.56 % |
| **0 dB** | **-21.69 dBFS** | -44.4 dB | -41.1 dB | 1.07 % |

The falling THD as level rises is the noise floor leaving the harmonic bins, not the chain improving.

**The chain is linear across the entire 36 dB, to within a decibel, and there is no limiting knee anywhere.** That is the finding. A correctly driven FM transmitter meets its IF filter somewhere and the recovered audio stops growing; this one never does. At maximum digital drive the recovered audio is -21.7 dBFS while the receiver's own band noise is -15.8 dBFS, so the modulation never approaches what the discriminator can deliver.

**So the station is substantially under-deviated and it cannot be fixed in software.** The `Speaker` control is at its ceiling, `txTest.amplitude` is already 0.8, and the capture side has no bearing on it. What is left is the resistive network between the CM108 output and the radio's T13 input, which is attenuating too hard.

This is worth real decibels rather than being a tidiness point: the waveform is injected past the radio's limiter, so post-detection SNR goes as **deviation squared**, and [receiver-findings.md](receiver-findings.md) measures 1500 Hz costing 3 to 4 dB against 2500 Hz on this very waveform. Both stations are at `playbackDb: 0` pinned in the config meanwhile, which is the best available and is well inside the law rather than outside it.

**How much is missing is not yet measured, and should not be guessed.** A discriminator-output level is not an absolute deviation figure. Two ways to pin it, in order of preference:

1. **An SDR on a tap off the coax.** `sm-ota fm-deviation --in <iq.wav>` already does exactly this job: it FM-discriminates a two-channel I/Q capture and prints peak, RMS and mean deviation in kHz. It needs I/Q and refuses a mono file, so a CM108 cannot feed it, but any RTL-SDR or the RSP1 the daemon already supports can. This is the cheap, correct answer and it needs no new software.
2. **The Bessel null**, which the transmitter test already computes for you: it reports the null deviation for whatever tone you ask for (500 / 999 / 1248 / 2079 Hz give 1.2 / 2.4 / 3.0 / 5.0 kHz). Raise drive until the RF carrier vanishes. It needs something that can see the carrier component, which again means a receiver that is not an FM set, because a limiting FM receiver cannot see it.

**One decision needs Tom either way.** The radios are on **Wide**, a 25 kHz channel, where 100 % of class is 5 kHz peak deviation. Everything in [receiver-findings.md](receiver-findings.md) was calibrated at 2500 Hz, which is 100 % of class on a **12.5 kHz** channel. Running the wider figure is legal here and worth about 6 dB of post-detection SNR, but it is not the number the ladder was measured at, so the two cannot be compared without saying which was used. Pick one and write it down before stage F.

### Stage C addendum: what stage B and C between them did not do

The passband **was** since repeated in both directions and the two interfaces difference to within 0.1 dB across the band, which is recorded above. Still owed, and cheap:

- **Forward and reverse power**, CCDI 318 and 319 on the transmitting radio, as a VSWR sanity check on the coax and the 100 dB pad before either is trusted as part of a measurement axis.
- **The level ladder in the reverse direction.** The passband agreeing does not prove the drive ladders do, because the passband is normalised and the ladder is not. The absolute references match to 0.15 dB, which is suggestive but is one point rather than a curve.

### Stage D: first contact. DONE 2026-09-18

**OFDM-FM has been on the air.** AX.25 UI frames, 64-byte payload, the narrow profile at its default QPSK rate 1/2, M0LTE and M9YYY, over the coax path at the stage B margin:

| | frames | |
|---|---|---|
| radio1 to radio2, first attempt | 9 / 10 | 90 % |
| radio1 to radio2 | 29 / 30 | 97 % |
| radio2 to radio1 | 26 / 30 | 87 % |
| radio1 to radio2, with the frame log on | 19 / 20 | 95 % |

Per-frame quality from radio2's frame log over that last run: every frame CRC-valid, reported SNR 13.5 to 15.8 dB. Treat that figure carefully: `DecodeStanding` documents `snr_db` as a band measurement, mean in-band power over a rolling minimum floor, and not the 3 kHz-referenced SNR the ladders use. It is a useful relative number and not a ladder axis.

**Do not read the direction difference.** 29/30 against 26/30 does not clear significance at that N, and the two interfaces have not been differenced properly yet. It is worth re-running at the N stage E uses before anyone theorises about interface asymmetry.

A milestone, not the gate: this is one rate at one margin with no signal axis under it.

*Exit met, except the failure budget by stage, which needs the decoder's pre-FEC bit error rate and was not collected.*

### Stage E: the cells, at high margin. DONE 2026-09-18

**Every rung of the rate ladder carried, on the first attempt, including the densest one.** Eight cells, 30 frames of 64 bytes each, radio1 to radio2 at a fixed -36 dBm with about 58 dB of margin over the idle floor, nothing moved between cells but the transmitter's profile.

| cell | constellation and code | decoded | mean SNR |
|---|---|---|---|
| control | QPSK, uncoded | 30/30 | 7.9 dB |
| workhorse | QPSK, K=7 rate 1/2 | 30/30 | 8.6 dB |
| | QPSK, K=7 rate 2/3 | 30/30 | 8.4 dB |
| | QPSK, K=7 rate 3/4 | 30/30 | 8.8 dB |
| dense | QAM-16, K=7 rate 2/3 | 30/30 | 7.4 dB |
| dense | QAM-64, K=7 rate 2/3 | 30/30 | 7.7 dB |
| dense | QAM-256, K=7 rate 2/3 | 29/30 | 7.5 dB |
| code family | QPSK, LDPC rate 1/2 | 30/30 | 8.0 dB |

**The dense-rung prediction was wrong.** This stage was written expecting QAM-16 or QAM-64 to meet an IF-truncation distortion floor under even a noiseless link, and called that "the prediction most likely to be wrong". It was: QAM-64 took 30 of 30 and QAM-256, which nothing had ever asked of a real FM path, took 29 of 30. At this margin the audio path is simply not the limit.

**Nothing here separates the rungs.** Every cell is at or within one frame of the ceiling, which is what a high-margin cell is for: it says each rung works, and it says nothing at all about which is better. That separation is stage F's job and stage F still wants the power attenuators.

**Two cells from the original list were not run: the two wider profiles at QPSK rate 1/2.** Unlike a change of constellation or code, a change of GEOMETRY is not signalled in the header, it is what the header is read against, so those cells need the receiver reconfigured as well, and a receiver that has been moved is no longer the fixed reference the other six cells were measured against. They are a separate sitting, and the profiles for them were already deployed.

**How the cells were run, which matters for reading them.** Only the TRANSMITTER changed. The receiver stayed configured on the plain profile for all eight cells and read each burst's constellation and code out of its header, which is the arrangement that actually tests the signalling: had the header been wrong or ignored, a receiver expecting uncoded QPSK would have failed every coded frame rather than decoding 30 of 30. Rate switching needs no agreement between the two ends beyond the geometry.

#### The coding had never been switched on

The stations' profile file carried geometry only. `Coding` is optional in a profile and falls back to none, so every frame this modem had put on air before tonight (the first contact, stage D, the whole of the passband and deviation work, and the baseline measured two hours earlier) was uncoded QPSK. The FEC existed, was tested, was measured in simulation, and had never once been keyed.

Nothing was wrong with the code. The profile file simply says less than a profile can say, and the one thing it did not say was the thing the README's headline claim rests on. Worth a look at any config whose defaults are load-bearing.

#### At 64-byte frames the rate ladder is nearly pointless

Measured from rendered bursts at this geometry, 160-byte payload, and quoted both as bare air time and with the 300 ms TXDELAY a station actually prepends:

| rung | burst | payload rate | with TXDELAY |
|---|---|---|---|
| QPSK rate 1/2 | 0.771 s | 1.66 kbit/s | 1.20 kbit/s |
| QPSK LDPC rate 1/2 | 0.771 s | 1.66 kbit/s | 1.20 kbit/s |
| QPSK rate 3/4 | 0.589 s | 2.17 kbit/s | 1.44 kbit/s |
| QPSK uncoded | 0.499 s | 2.57 kbit/s | 1.60 kbit/s |
| QAM-16 rate 2/3 | 0.453 s | 2.82 kbit/s | 1.70 kbit/s |
| QAM-64 rate 2/3 | 0.363 s | 3.53 kbit/s | 1.93 kbit/s |
| QAM-256 rate 2/3 | 0.363 s | 3.53 kbit/s | 1.93 kbit/s |

**QAM-256 and QAM-64 take exactly the same air time here**, because at this payload both round to the same whole number of symbols and the remainder is spent carrying nothing. The entire ladder, from the most robust rung to the densest the waveform has, spans 1.2 to 1.9 kbit/s once TXDELAY is counted. Fixed cost dominates: four symbols of preamble, sync, header and lead-in before any payload symbol, plus whatever dead air TXDELAY adds in front of that.

So the dense rungs are not a throughput feature at short frames. They pay off on long ones, where the payload symbols outnumber the fixed cost: at 1024 bytes the same arithmetic puts QAM-256 at about 2.3 times uncoded QPSK rather than 1.4 times. **Any claim about this modem's throughput has to state the frame length it was measured at**, and a rate controller choosing a rung without knowing how much it has to send is choosing on incomplete information.

#### Peak deviation is a property of the constellation, not of the drive

The transmit drive is normalised once, from the preamble, and each constellation is then allowed its own crest factor: 5 dB at QPSK, 8 dB at QAM-16, 10 dB at QAM-64 and denser. Average power barely moves across the ladder; the peaks do. Measured on rendered bursts at this geometry:

| constellation | peak, relative to QPSK | peak deviation at tonight's drive |
|---|---|---|
| QPSK | reference | 2.84 kHz, 57 % of class |
| 8PSK | +2.2 dB | 3.65 kHz, 73 % |
| QAM-16 | +3.1 dB | 4.06 kHz, 81 % |
| QAM-64 | +4.6 dB | 4.81 kHz, 96 % |
| QAM-256 | +4.6 dB | 4.81 kHz, 96 % |

So a drive set against a QPSK burst puts a QAM-64 burst within 4 % of the 5 kHz class limit. It is inside it, and there is no headroom left. **Set the drive against the densest constellation the station will ever transmit, not the one it happens to be sending**, and treat the deviation figure measured at QPSK as 4.6 dB optimistic for anything denser.

*Exit: met. Every rung carries at high margin; which rung is worth using is stage F's question.*

### TXDELAY: 300 ms of dead air in front of every burst, and 40 ms will do. MEASURED 2026-09-18

The daemon's default TXDELAY is 300 ms. The modem turns it into whole SILENT symbols, rounded up and never fewer than one, so only the settings that land on a different symbol count are different waveforms. Walked down 25 frames of 64 bytes a rung, radio1 to radio2:

| TXDELAY | symbols of lead-in | decoded |
|---|---|---|
| 300 ms | 7 | 25/25 |
| 200 ms | 5 | 25/25 |
| 150 ms | 4 | 25/25 |
| 100 ms | 3 | 25/25 |
| 60 ms | 2 | 24/25 |
| 40 ms | 1 | 25/25 |

**There is no cliff.** The bottom rung is the floor the modem can express, and the one miss at 60 ms is a single frame with a rung either side of it at full marks. The radio's own floor is far below both: a TM8110 at this tap reaches valid modulation 14.8 ms after EPTT, and one silent symbol is about three times that.

Worth 260 ms off every burst, which at 64-byte frames is about half as much again in throughput for the sake of one number. **Two reasons not to just change the default yet.** This was measured at 58 dB of margin with a receiver that was already listening and already had its AGC settled, and lead-in is precisely what acquisition spends itself on, so the rung that holds here may not hold near a cliff; and it is one direction on one pair of radios. Re-run it as part of stage F, where there is a signal axis to run it against, before it becomes a default.

### Stage F: the ladder

Sweep the signal axis, five points, 50 frames per cell per point, RSSI logged per burst from the receiving radio. This is where the simulation rows get their real-hardware column and where the disagreements get explained rather than averaged away.

**This stage needs a way to move the axis** and 100 dB of fixed pad does not provide one. See the open question below.

*Exit: the cells across five points with the sim rows beside them.*

### Stage G: the coding A/B

Cell 1 against cell 2 across the whole ladder. The 5 to 6 dB coding claim is this modem's headline number, it came from 8 seeds a point against a channel model written alongside the modem, and this is the single most valuable measurement of the campaign.

*Exit: the measured coding gain on real radios, with N and the sim number beside it.*

### Stage H: the clocks. MEASURED 2026-09-18

**These two stations' sample clocks differ by about 5 ppm.** Measured on air, both directions: a 30 s tone from one station's test generator, captured by the other's raw capture, and its frequency read off by fitting the phase ramp rather than by looking at a spectrum. The tone generator is a numerically controlled oscillator, so the tone leaves the transmitter at exactly the frequency asked for in ITS OWN clock domain; an FM path carries audio frequency untouched, a carrier offset landing as a DC step out of the discriminator rather than as a frequency shift. So the ratio the receiver measures is the ratio of the two soundcards' crystals and nothing else.

| direction | measured at 1500 Hz | difference |
|---|---|---|
| radio1 -> radio2 | 1499.9919 Hz | -5.41 ppm |
| radio2 -> radio1 | 1500.0072 Hz | +4.77 ppm |

The two directions must sum to zero and they sum to -0.64 ppm, so the figure is good to about 0.3 ppm however small the fit's own error bars say it is; the residual is most likely the crystals wandering over the minute between the two runs, which is itself worth knowing.

**What it means for the tilt correction.** `RemoveSampleClockTilt` was measured against 100 ppm and 200 ppm injected in simulation, where QAM-256 rate 2/3 spent 77 % of its correcting power on the tilt alone and 200 ppm failed outright. Real hardware here is twenty times better than the level that hurts. That is not a reason to remove the correction, since it is CRC-arbitrated and can only help, but it does move the tilt down the list of things likely to bite on air, and it means any future failure at a dense rate should be looked for somewhere else first.

**One pair of crystals is not a population.** 5 ppm is this pair, tonight, at this temperature. The simulation's 100 ppm is not a silly number for two arbitrary consumer dongles, and the measurement costs one 30 s tone, so it is worth repeating on any new pair before blaming the waveform for something.

#### Burst length against the clocks

The tilt a sample-clock difference paints GROWS along a burst, so if 5 ppm were going to cost anything it would cost it on the longest burst at the densest constellation. Twenty frames a cell, same margin, nothing changing but the payload length. The wait per frame was scaled with the frame, because a 1024-byte burst at rate 1/2 is close to four seconds of air time and a fixed wait would have scored every one of them a loss and called it a cliff.

| payload | QPSK rate 1/2 | QAM-64 rate 2/3 |
|---|---|---|
| 64 B | 20/20 | 20/20 |
| 256 B | 20/20 | 20/20 |
| 512 B | 20/20 | 20/20 |
| 1024 B | 20/20 | 20/20 |

Nothing. The longest burst here is 75 payload symbols and it arrived as reliably as the shortest. Taken with the 5 ppm measurement, the clock question is answered for this pair of cards at this margin, and the remaining half of stage H, the tilt fit on against off, wants running where there is a cliff to run it against.

**One instrument note.** `snr_db` comes back null for the long-gap cells. It is a rolling band measurement rather than a frame measurement, and at an 11 second gap its window has expired before the next burst arrives. The decode count is the measurement in those cells; the missing SNR is the instrument behaving as documented, not a loss.

*Exit: done for this pair. The tilt-fit A/B is no longer the urgent half of this stage, because at 5 ppm there is almost nothing for the fit to find; it wants running at the point where a signal ladder can put the link near a cliff.*

### Stage I: a connected-mode session. RUN 2026-09-18, and it found the best defect of the campaign

**A link was established and data crossed it.** M0LTE called M9YYY over the narrow profile, the link came up, and four lines of text from the far end arrived and printed. As a demonstration that the thing is a modem and not a frame counter, that is met.

**It then failed to converge, and the reason is worth more than the session was.** Both ends had four I frames queued and both transmitted them at the same moment:

```
radio2 tx  21:36:20.730  21.126  21.522  21.918
radio1 tx  21:36:21.080  21.481  21.877  22.272
```

Neither decoded any of the other's. The session then polled with RR for 66 seconds and gave up. One-way testing over the same path had delivered 100 % of frames all evening at every rate and every payload length, so nothing here is about the waveform.

**Carrier sense is blind for exactly as long as TXDELAY, and on FM nothing covers the gap.**

A sync-based carrier detect reports "a burst is on the air" only once there is modulation to correlate against. TXDELAY is SILENCE ahead of the preamble, so for its whole duration a transmitting station occupies the channel and no receiver can tell. At the shipped 300 ms default that is a 300 ms window at the start of every transmission in which the channel is busy and reads clear. radio1 keyed about 50 ms into radio2's lead-in.

The energy detector that should cover that window is the wrong way round for FM. It asserts when audio energy rises above an adapting noise floor. On a receiver with the squelch open, which is what a data station runs, an arriving carrier QUIETS the receiver: measured here, the noise drops from about -16 dBFS to -62 dBFS the moment a carrier appears. The arrival of a signal makes the energy FALL. A detector that asserts on a rise cannot fire on it, and the adapting floor makes it worse, not better, because open-squelch band noise becomes the floor and a real signal sits far below it.

So on this path carrier sense rests entirely on the sync detector, which is blind for TXDELAY.

**It also gives the TXDELAY result a second and better reason.** Walking TXDELAY from 300 ms to 40 ms cost nothing in delivery, which is worth having for its own sake; it also removes 260 ms of collision exposure from every transmission on a channel with more than one station on it. The first argument is about throughput and the second is about whether a shared channel works at all.

Raised as packet-net/pdn-soundmodem#502 with the timestamps and both fixes. Solved the following day; see [carrier-sense.md](../carrier-sense.md).

*Exit: a clean connect and data one way, so partly met. Data BOTH ways, and a clean disconnect, want the carrier-sense fix first, because what failed is not something a retry will get past.*

## The one open question the bench cannot answer by itself

**How do we move the signal axis?** Stage F needs five points across a signal range and the bench has a fixed 100 dB pad. The radios are already at their lowest power setting. Options, in the order they are worth trying:

1. **A step attenuator or a set of fixed pads** between the radios. Cleanest by far: it moves the channel and nothing else, and the RSSI reading measures the result directly rather than being trusted.
2. **Programming a lower transmit power** into the codeplug per channel. Worth maybe 10 to 20 dB and it needs the radio latched into programming mode, which needs a power cycle at the bench.
3. **Not varying it at all** and quoting a single operating point, which turns the campaign from characterisation back into "it works", and is the outcome worth avoiding.

Anything up to about 50 dB of extra attenuation is probably what the ladder wants, but stage B's RSSI reading is what says so, and it is one keying away.

## Instruments, as they stood on the stations

Everything below was enabled and used. The daemon-side instruments are part of this repository; the campaign harness was a scratch Python rig on the dev box and is not.

| What | Where | Note |
|---|---|---|
| Per-frame decode quality | `frameLog` -> `/var/lib/pdn-soundmodem/frames.db` | SQLite, WAL, readable while the modem runs. One row per frame heard AND sent, including one per transmitter test. Carries `crc_valid`, `corrected`, `snr_db`, `peak_dbfs`, `clipped`, `level`, `offset_hz`. |
| Signal level in dBm | `tait-cli /dev/ttyUSB0 rssi` or `watch` | Calibrated, from the radio. `info`, `channel`, `temp` too. |
| Transmit test | `POST /api/txtest` | `{"twoTone":false,"toneHz":N,"seconds":S}`. **`twoTone` defaults to TRUE**, so always state it. `toneHz` is refused rather than clamped outside 50 Hz to Nyquist. Reports the Bessel-null deviation for the tone it sent. |
| Mixer | `POST /api/mixer` | `{"playbackDb":N}` / `{"captureGainDb":N}`, `?persist=false` for one run. |
| Metrics | `GET /metrics/frames` | Influx line protocol, one point per frame, 300 s window, no auth. |
| Frame counts over the path | campaign harness | Sends N UI frames into one station's KISS port, counts byte-identical arrivals at the other. |
| One cell, with an error bar | campaign harness | Keys a tone and records the far end's RSSI, marks the receiver's frame log, sends N UI frames, counts byte-identical arrivals, and writes a JSON record with a Wilson 95 % interval. The attenuator figure it records is an operator-set dial position, not a calibrated level, and it says so in every file. |
| Sample-clock difference | campaign harness | A 30 s tone one way, its frequency read off the far end's raw capture by fitting the phase ramp. Answers in ppm. Run it both ways: the two must sum to zero, and whatever they do not cancel is the honest error bar. |
| Transmit rate, per cell | campaign harness | Puts one station's sub-channel on a named profile for ONE run, so a restart returns it to its config file by itself. Builds the request from the config FILE, never from `GET /api/config`, which redacts `api.key` to a literal string that a round trip would then install as the key. |
| Dead air per burst | campaign harness | Walks TXDELAY down over KISS, which applies live with no restart. Only the settings that land on a different whole symbol count are worth testing. |
| A profile set, before it is deployed | campaign harness | Validates every profile in a station geometry file and prints what each one carries. A profile that fails validation takes the modem's constructor down and the daemon's start-up with it, so this runs on a dev box first, never on air first. |

The `api` section carries a key, which is what turns all of the HTTP routes on: with no key configured they are not 404-because-forbidden, they are simply not routed, which is confusing to debug. The key is in each station's `soundmodem.json`, mode 600, owned by the daemon user.

**Two cautions on the numbers, both of which have bitten this codebase before.**

`snr_db` in the frame log and on the WebSocket is documented by `DecodeStanding` as mean in-band power over a rolling **minimum** floor. It is a band measurement, not a frame measurement, it is floored near the 6 dB the burst gate demands, and it repeats the previous burst's figure for two seconds after a run ends. It is not the 3 kHz-referenced SNR the ladders use and the two must never be put in the same column.

The radio's RSSI is a third scale again: absolute dBm across the whole channel, silent on whether the signal is ours. On this sealed bench that is fine, which is exactly why the axis should be pinned here rather than on an antenna.

## What is still missing before stage F can run

- **A way to move the signal axis.** See the open question above. Measured requirement: about 50 dB.
- **An absolute deviation figure**, which needs an I/Q capture and `sm-ota fm-deviation`. Note that `sm-ota` is **not** in the `.deb`; it is a dev-box tool in `tools/Packet.SoundModem.Ota` and cross-publishes to arm64 cleanly if it is wanted on a Pi.
- **Transmit audio capture.** The daemon has `rawCapture` for receive and there is **no transmit audio writer anywhere in it**: the only tap hangs off the receive path, which returns early while transmitting. The campaign's standing rule is to capture both ends in both directions, so this needs either a second stereo-capture dongle wired across both audio pairs, or an ALSA `type asym` file tee, or the rule relaxed to "the far end's receive capture IS the record of our transmission", which is defensible for an over-the-air campaign and costs nothing.
- **The reverse-direction passband and level ladder**, per the stage C addendum.

## What the rig broke, and what each fault taught

Four faults in one evening, none of them the waveform. Recorded because each one wasted time in a way the next person need not, and because two of them produced measurements we could not otherwise have made.

### The first 50 dB pad burnt out, progressively, over about two hours

**A 50 dB pad dissipates essentially the whole transmit power.** Only 10 uW of a watt comes out the far side, so the first pad in the chain absorbs the lot. "Very low" on a TM8110 is **1 W** (the 25 W B1 variant, specifications manual p.18), and across two 31-tone passband sweeps, a 10-point drive ladder, a 5-point deviation ladder and several hundred frames, that pad cooked.

**How it presented, which is the part worth knowing.** Not as a step but as a *drift*: the far-end signal fell from -68.9 dBm to -81.7 dBm to -98.5 dBm over two hours, symmetric in both directions, while both transmitters provably held their output (forward power 382 to 403 mV throughout, measured an hour apart). A burnt-open resistive element still passes RF through stray capacitance across the gap, so the DC path opens while the RF path degrades gradually. Multimeter confirmed it afterwards: the dead pad read **open** centre-to-shell at both ends and open through, where a healthy 50 dB pad reads about **50 ohm** centre-to-shell and **99 ohm** through (both pi and tee topologies give the same figures). The second pad, which only ever saw 10 uW, was pristine.

**The rule this gives the campaign.** The attenuator chain is bidirectional, so it needs power-rated attenuation at **both** ends: whatever is first from one radio is last from the other. A single power pad at one end leaves the other radio's watt landing on whatever is at the far end, which, for a step attenuator rated 1 W, destroys it. The topology is a power-rated pad at each end with everything precious inside them:

```
radio1 -> [20 dB, 10 W] -> [50 dB] -> [step 0-80 dB] -> [20 dB, 10 W] -> radio2
```

At 1 W in from either direction the end pads see the full watt, the 50 dB pad sees 10 mW and the step attenuator sees 100 nW. That is 90 dB fixed plus 0 to 80 dB variable, putting the far end between -60 and -140 dBm, which brackets the range the ladder needs.

**And it cost us a day's frame counts.** Every decode percentage taken before the fault was found was measured across a degrading path, so none of them is a baseline. The deviation measurements survive, because they are phase measurements and indifferent to level.

### RF from the transmitter knocked the interface off the USB bus

On the station moved to a different room, every single keying was followed within one to ten seconds by a USB reset, and the transmit audio write failed with `snd_pcm_writei: No such device`. Five keyings, five resets, deterministic.

**The control that made it a diagnosis rather than a guess**, and the step worth copying: exercise playback and capture hard for several minutes with the transmitter unable to transmit. Three and a half minutes of continuous `aplay` and `arecord` produced **zero** resets, which exonerates the dongle, the cable's mechanical integrity, the USB port and the 5 V supply in one stroke, since that is the heaviest current draw the device will ever see. Then, with the radio switched off so the daemon did the identical HID PTT write and the identical audio write with no RF anywhere: **zero resets again**. Radio on: one reset per keying, every time.

**The cure was a screened USB cable**, not ferrite and not antenna position. Two mitigations failed first, a different antenna and a lot of clip-on ferrite, because the coupling was into the cable itself rather than common-mode current the ferrite could choke. Worth stating plainly in the hardware notes: **this interface wants a properly screened USB cable when a transmitter is anywhere near it**, and the failure mode is not subtle once you know to look for `usb ... reset full-speed` in the kernel log alongside the audio error.

**A software consequence worth fixing upstream.** When the device vanished mid-transmission the daemon tried to release PTT and could not, because PTT keys through the same dongle:

```
tx test: refused, snd_pcm_writei: No such device
ptt: Broken pipe : '/dev/hidraw0'
   at Packet.SoundModem.Channel.Cm108Ptt.Dispose()
```

It is reported as an exception during `Dispose()` and the daemon carries on. Here the USB reset happened to drop the GPIO and unkey the radio, but that is luck rather than design, and a PTT path that can silently fail to de-assert is a stuck-transmitter risk on an unattended station.

### Two instruments that lie, and how

**The radio's RSSI does not depend on our deviation, and does not report what an SWR alarm reports.** Swept across the card's whole 36 dB drive range, from 4.12 kHz peak deviation down to an effectively unmodulated carrier, RSSI moved by 0.2 dB. So it is a clean measure of received power. But CCTM 319, the reverse-power reading, read **164 mV into a known-good 50 ohm pad chain** and **0 mV into an antenna the radio was actively alarming about**. Whatever it is reporting, it is not a mismatch indicator that can be read naively, and an alarm should not be attributed to VSWR on the strength of it.

**`sm-ota fm-deviation` needs its analysis bandwidth chosen, and its peak figure ignored.** It takes the instantaneous frequency sample by sample, so every hertz of bandwidth kept beyond the signal adds noise to the answer. Measured on real captures: a 25 kHz window read the rms **4 % high at full drive and 61 % high at 16 dB down**, which looks exactly like transmitter compression and is not; at 4 kHz the same ladder is linear to 0.21 dB over 16 dB. Filter to a little beyond Carson bandwidth and find the plateau. And its peak figure is a per-sample maximum: on one capture it printed **8.476 kHz where the true peak was 1.80 kHz**. Use the rms, and multiply by root two for a sine.

**One trap of our own making, for anyone writing a capture analyser**: a receiver's envelope drops when a carrier arrives, because FM quiets. An SDR watching the same transmitter sees the envelope *rise*. Burst-detection logic does not transfer between the two.

## Evidence, per session

One directory per session, named by date, containing: both stations' configs and daemon versions, the build the stations are running, the mixer settings at both ends, the transmit level and the deviation setting, the RSSI log, the audio captures from both ends in both directions, the frame counts with N, and a JSON summary. A session that produces a pass or a fail and no corpus has thrown away the most valuable thing it could have brought home. Those directories are campaign evidence and live outside this repository; what is here is what they were used to conclude.
