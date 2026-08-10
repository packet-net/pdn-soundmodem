# Connecting a Tait TM8100 to a CM108 sound card

A build note for the audio interface between a TM8100/TM8200 mobile and a CM108-class USB sound
card, for headless packet operation with `pdn-soundmodem`.

**No presets.** Two quantities are measured once on the bench and two resistors are then chosen from
a table. Fine level setting is done in software, where it belongs: the CM108 has capture and
playback gain controls that can be set remotely, logged, and put in a config file, none of which is
true of a trimmer.

## Provenance

Every Tait figure carries its document and page, from the set at
[M0LTE/tait-tm8100-tm8200-docs](https://github.com/M0LTE/tait-tm8100-tm8200-docs). Marks used
throughout:

| Mark | Meaning |
|---|---|
| **TAIT** | stated in the Tait documentation, cited |
| **CM108** | C-Media datasheet or ordinary dongle practice, NOT Tait |
| **DERIVED** | computed from cited figures, arithmetic shown |
| **MEASURE** | not published anywhere; you measure it once |
| **CHOICE** | an engineering decision made here, not anyone's datasheet |

Component values quoted from a schematic are from `MMAB12-B1-00-814`, the **TM8100 B1 main board**
(136 to 174 MHz). No board pack for the other bands, and none for the TM8200, is in the set.

## Which tap points

**R1 on receive and T13 on transmit.** That is the deployment, and the rest of this note assumes it.

**Receive: R1.** `MMA-00005-05` p.124 lists the CCTM audio tap-out command as accepting
`r1 r2 r3 r4 r5 t1 t2 t3 t7`, so the raw-demodulator tap is documented and reachable. At R1 the audio
has had no bandwidth-dependent scaling, no decimation to 8 kHz, no 0.3 to 3 kHz bandpass and no
de-emphasis (chain order, `MMA-00005-05` p.56). It is limited only by the IF filter, 7.8 kHz total
3 dB on a 12.5 kHz channel (`MMA-00005-05` p.73, Table 3.1), so roughly 3.9 kHz of audio.

**Transmit: T13, which is the last tap before the modulator.** The programming application's tap
diagram places it after compression, encryption, the 300 Hz high pass, pre-emphasis, the limiter, the
3 kHz low pass and the peak-system-deviation scaler. **An injected signal there meets none of them.**

A documentation note, because it will confuse anyone who goes looking. **T13 appears nowhere in
these 20 manuals**: searching all of them for "t13" returns only occurrences inside "MPT1327". The
service manual's own tap-in list is `r2 r5 t1 t5` (p.124), and the only transmit tap-in ever
configured in a programming form is T5, in six tables across five documents. That manual is from
2007 and uses a different tap numbering from the programming application; the two describe the same
radio at different times. **Where they disagree, the programming application is what the radio in
front of you actually does**, and this note follows it.

The distinction matters because the two taps are on opposite sides of the limiter. Tait qualify
their own transmitter response as "below limiting" for a tap-injected test signal (p.480), a caveat
only needed if the limiter is in circuit:

> Bandwidth Response: 300Hz to 3kHz, +1, -3dB relative to -6dB/octave
> *relative to 1kHz, 20% deviation, below limiting*
> Test Signal: 0dBm line input, audio tap T1

So a signal injected at T5 would still meet the 300 Hz high pass, pre-emphasis, the hard limiter and
the 3 kHz low pass. **At T13 it meets none of that**, which is the whole reason for choosing it.

### What T13 means for the modem, and it is not only wiring

**The transmit path is flat.** No 300 Hz high pass, so a waveform with carriers near 305 Hz is not
sitting on a filter corner. No pre-emphasis, so no tilt to undo. No 3 kHz low pass, so the audio
bandwidth is bounded by the modulator and the channel rather than by a voice filter.

**Nothing protects the modulator, and nothing scales the drive.** The limiter and the
peak-system-deviation scaler are both upstream. Your audio level sets deviation directly, and there
is no ceiling anywhere in the radio. **Over-deviation is therefore entirely yours to prevent**, which
is why the transmit attenuator below is sized so that a full-scale digital sample cannot exceed 90 %
of class deviation. At T5 that would be belt and braces; at T13 it is the only belt there is.

**Peak deviation is set by your waveform's peak, so peak-to-average ratio costs you level.** With no
limiter, staying legal means setting the drive against the loudest instant in the burst, so a peaky
waveform forces the whole burst down. That is worth real decibels: on an audio-band OFDM mode,
removing one unusually peaky symbol was worth about 3 dB of sensitivity measured this way, and
nothing measurable at all when re-measured with a limiter in circuit. Both are honest answers; T13
is the first question. `M0LTE.FmChannel`'s default drive mode models this, and its
`LimitAtDeviationHz` models the other, for anyone who ends up at T5 or on the microphone.

## The radio side

Auxiliary connector, 15-way standard-density D-range socket. `MMA-00005-05` p.42, Table 2.3.

| Pin | Signal | Notes |
|---|---|---|
| 7 | AUD_TAP_IN | programmable tap into the Rx or Tx audio chain, DC-coupled, analogue |
| 13 | AUD_TAP_OUT | programmable tap out of the Rx or Tx audio chain, DC-coupled, analogue |
| 15 | AGND | analogue ground, and the only ground pin on this connector |
| 12 | AUX_GPI1 | general purpose digital input, 3V3 CMOS, programmable function |
| 8 | +13V8_SW | switched 13.8 V, off when the radio body is off |
| 6 | RSSI | analogue RSSI output, useful for calibration and for a receive-level check |

Mechanical: the space for a mating plug is limited to 41 mm wide by 18 mm high (`MMA-00028-05`
p.22). Check the backshell before making a loom.

### AUD_TAP_OUT as a source

| Property | Value | Mark |
|---|---|---|
| Source impedance | 600 ohm resistive | **DERIVED** |
| DC pedestal | approximately +2.4 V when operational | **TAIT** |
| Anti-alias filtering | 3rd-order Butterworth, approximately 12 kHz | **TAIT** |
| Level | 0.7 Vpp at the DAC node at 60% deviation, x2 buffer to the pin | **TAIT**, with a caveat below |

The 600 ohms: "The output of the low-pass filter is amplified by 6dB by a buffer amplifier, IC201
(pins 5 to 7), and fed via R207 and R208 to drive the CDC AUD TAP OUT interface line"
(`MMA-00005-05` p.84), with R207 and R208 both 1k2 (`MMAB12-B1-00-814` p.3) in parallel, so 600 ohm.

**The level has a factor-of-two ambiguity in Tait's own documents.** p.412 gives "0.7Vpp with 2.4V
DC offset" at the junction of R224 and IC204, while p.84 puts the bias at that node at 1.2 V. Either
the 2.4 V is a typo and the pin sees 1.4 Vpp after the x2 buffer, or the line quotes connector
figures and the pin sees 0.7 Vpp. **This is one of the two things you measure.** The design below
covers both.

Note also that the documented level is for CCTM tap `r5`, a late receive tap. **R1 is ahead of the
bandwidth-dependent normaliser**, so its volts per kHz is different and is not published anywhere.

### AUD_TAP_IN as a load

| Property | Value | Mark |
|---|---|---|
| DC bias | approximately +1.5 V, generated inside IC205 | **TAIT** |
| Series element | R241 = 4k7, plus IC205's internal clamp diodes | **TAIT** |
| Connector network | ferrite L709 and 10 nF C720 to AGND | **TAIT** |
| Input level for a given deviation | **not published, at any tap, in either direction** | **MEASURE** |

The 10 nF at the connector is the reason the transmit interface below is deliberately low impedance:
into a 909 ohm source it is a pole at 17.5 kHz and irrelevant, but into a 10 k source it would be a
pole at 1.6 kHz and would eat the top of the audio band.

## The CM108 side

All **CM108**, and all worth confirming on your actual dongle, which is the second thing you measure.

- USB Audio Class 1.0, 48 kHz maximum, 16-bit.
- Stereo headphone/line output, capable of driving 32 ohm, AC-coupled on the dongle by its own
  output electrolytics. Open-circuit full scale is on the order of 1 Vrms and varies by unit and by
  the mixer setting.
- Mono microphone input, biased through roughly 2.2 kohm, with a software-selectable +20 dB boost
  and a capture gain control. **Turn the boost off.**
- Four GPIO pins, 3.3 V CMOS, weak drive. The de-facto convention for PTT is GPIO3.

**Use a line input if your board has one.** On the flat R1 tap the microphone path's low-frequency
behaviour is the weak link: many dongles use a 1 uF or smaller coupling capacitor, which puts the
corner at 80 Hz or above and causes visible baseline wander on a wideband tap. A board with a real
line input avoids that, has more headroom and is quieter.

## Receive path

```
  AUX pin 13                      Rs                          to CM108
  AUD_TAP_OUT                  (table)         Rp             mic or line in
  600R src, +2.4V DC   C1      _______         1k       C2
      o------------||---------|_______|----+---||-------------o
                   2u2                     |    10u
                                          [ ]  Rp 1k
                                           |
                                          === C3 4n7
                                           |
  AUX pin 15  o----------------------------+--------------------o  dongle GND
  AGND                                                             (see Grounding)
```

**C1 blocks the +2.4 V pedestal absolutely**, so the interface presents no DC path and does not
disturb the radio's bias. Tait's own crossband cable does load the tap with 600 ohm to ground and
accepts the resulting 6 dB loss; there is no reason for us to.

**Rp is fixed at 1k. Rs is chosen from the table**, from your two measurements. The shunt leg sits
in parallel with the dongle's microphone bias resistor, 1k || 2k2 = 688 ohm **DERIVED**.

Target: **-12 dBFS at 60% of class deviation** **CHOICE**. That leaves 4.4 dB of headroom to 100%
deviation and about 7 dB above that before clipping, which covers over-deviating stations.

| CM108 full scale | tap 0.7 Vpp | tap 1.4 Vpp |
|---|---|---|
| 50 mVrms | Rs = 12k, load 13.5k, -0.4 dB | Rs = 27k, load 27k, -0.2 dB |
| 100 mVrms | Rs = 6k2, load 6.8k, -0.7 dB | Rs = 13k, load 13.5k, -0.4 dB |
| 150 mVrms | Rs = 3k9, load 4.5k, -1.1 dB | Rs = 8k2, load 9.0k, -0.6 dB |
| 250 mVrms | Rs = 2k0, load 2.7k, -1.7 dB | Rs = 4k7, load 5.4k, -0.9 dB |

E24 values, rounded from the exact result. The insertion loss column is what the 600 ohm source
loses into that load; all are negligible and all are far lighter than the 600 ohm Tait themselves
demonstrate is a working load.

**C3 is RF hygiene, not anti-aliasing.** 4n7 against roughly 625 ohm is a pole at 54 kHz; the
CM108's own decimation filter does the anti-aliasing. If aliased noise appears on the wideband R1
tap, raise C3 to 8n2, which moves the pole to 31 kHz and costs about 0.4 dB at 9.6 kHz.

**Low-frequency corners.** C1 2u2 into the load is about 10 Hz. C2 10u into the dongle's bias
network is about 8 Hz. Both are comfortably below anything the modem uses, provided the dongle's own
input capacitor is not the limit, which is the reason for the line-input recommendation above.

## Transmit path

```
  from CM108                  Rt                    AUX pin 7
  line out                   2k2                    AUD_TAP_IN
  AC-coupled       C4                               4k7 series, +1.5V bias
      o-----------||--------|_______|-----+------------o
                  10u                     |
                                         [ ] Rb 220R
                                          |
      o-----------------------------------+------------o  AUX pin 15 AGND
        dongle GND
```

**Rt/Rb = 2k2/220R gives -20.8 dB and a 200 ohm source impedance** **CHOICE**, which puts the pole
formed with the radio's 10 nF at 80 kHz, well clear of the audio band. Scale both together if you
need a different attenuation; keep the Thevenin impedance under about 1 kohm.

**C4 blocks DC in both directions.** The dongle's output is already AC-coupled by its own
electrolytics, and AUD_TAP_IN is internally biased to about 1.5 V; neither wants the other's DC.

**Size the attenuator so full-scale digital cannot over-deviate.** Choose Rt/Rb such that a 0 dBFS
sine at the CM108 output produces **90% of class deviation** **CHOICE**, that is 2.25 kHz on a
12.5 kHz channel. Then no software fault, mixer setting or runaway process can splatter: the
hardware ceiling is below the legal one. The modem then runs at whatever level below that the mode
wants, set in software.

This is the property that replaces the preset, and it is strictly better than one: a trimmer set to
90% today can be knocked to 200% tomorrow, and a fixed divider cannot.

## PTT

AUX_GPI1 on pin 12 is a 3.3 V CMOS input **TAIT**. Drive it from a CM108 GPIO through a small-signal
MOSFET or an open-collector transistor rather than directly: the GPIO's drive is weak **CM108**, and
a transistor also gives you the option of an isolator later.

Program the PTT source and the audio source together. A tap-in only becomes the modulation source
when the PTT form agrees: `MMA-00005-05` p.573 shows "PTT Transmission Type: Voice, Audio Source:
Audio Tap In".

**Check the tap-out gating.** The Tap Out Unmute field takes On PTT, Busy Detect, or Busy Detect
plus Subaud (`MMA-00005-05` p.472). A modem that wants to hear the channel continuously must not be
behind a gate that only opens on busy-detect, or it will miss the start of every burst, which looks
exactly like an acquisition problem in the modem and is not one.

## Grounding

Pin 15 AGND is the only ground on the auxiliary connector, and there is no separate digital ground
**TAIT**. In a vehicle the radio's negative is bonded to the chassis and the sound card's ground
arrives through the USB host, so a direct wire completes a loop through the vehicle's electrical
system, which on a mobile is a large and noisy one.

**Recommendation: isolate.** Two 600:600 ohm audio transformers and an opto-isolated PTT. The
receive transformer goes after C1 and before Rs; the transmit transformer goes after C4. This costs
about a decibel and some low-frequency response, and it removes an entire class of fault that is
tedious to diagnose after the fact. In a fixed station with a single mains supply and a short USB
lead, a direct connection is usually fine, and you can decide by trying it.

If you do isolate, check the transformer's low-frequency response against the modem's lowest
carrier. A 600:600 telecoms transformer is typically flat from a few hundred hertz, which is fine
for a 300 Hz to 3 kHz mode and marginal for anything using the flat R1 tap down low.

## Parts

| Ref | Value | Purpose | Mark |
|---|---|---|---|
| C1 | 2u2 film or bipolar electrolytic | blocks the +2.4 V tap-out pedestal | **CHOICE** |
| Rs | from the table | receive attenuator, series leg | **DERIVED** from measurement |
| Rp | 1k | receive attenuator, shunt leg | **CHOICE** |
| C3 | 4n7 | RF hygiene at the receive input | **CHOICE** |
| C2 | 10u bipolar | couples into the sound card input | **CHOICE** |
| C4 | 10u bipolar | blocks DC into AUD_TAP_IN | **CHOICE** |
| Rt | 2k2 | transmit attenuator, series leg | **CHOICE**, size from measurement |
| Rb | 220R | transmit attenuator, shunt leg | **CHOICE**, size from measurement |
| Q1 | 2N7000 or similar | PTT switch from a CM108 GPIO | **CHOICE** |
| T1, T2 | 600:600 audio transformers | optional isolation | **CHOICE** |

## The two measurements that replace the preset

Do these once, per radio model and per dongle model. They are not per unit unless you find the
spread is large, which is worth checking on two or three of each.

### Measurement 1: what the CM108 input full scale actually is

Feed a 1 kHz sine into the sound card input from a signal generator, boost off, capture gain at a
known setting. Raise the level until the captured samples just clip, and note the input voltage.
That is the full-scale figure for the table. Do it at the capture gain you intend to run at, and
record that setting alongside it.

### Measurement 2: what the tap levels actually are

**Receive.** Feed the radio a signal generator on channel, modulated 1 kHz at 60% of class deviation
(1.5 kHz on a 12.5 kHz channel), at about -80 dBm so the receiver is well above threshold and no AGC
is acting. Measure the AC level at AUD_TAP_OUT with a scope or a true-RMS meter. That resolves the
0.7 versus 1.4 Vpp ambiguity, and it is the figure the table wants. Do it at the tap you will
actually use: R1 and R5 have different gains and only R5's is documented.

**Transmit.** This is the one with no published figure at all, so it has to be measured, and it can
be done without a deviation meter. It matters more at T13 than it would anywhere else, because the
peak-system-deviation scaler is upstream and therefore bypassed: nothing in the radio is normalising
your level, so what you inject is what deviates.

Use a **Bessel null**. A carrier frequency-modulated by a single tone has its carrier component
vanish at a modulation index of 2.405, so if you feed a tone of frequency `f` and watch the carrier
on any SDR or spectrum analyser, the deviation at the null is exactly `2.405 x f`. Choose the tone
so the null lands on the deviation you want:

| Target deviation | Tone for first carrier null |
|---|---|
| 2.5 kHz, 100% narrowband | 1040 Hz |
| 1.5 kHz, 60% narrowband, Tait's own data default | 624 Hz |
| 4.0 kHz, 100% mid | 1663 Hz |
| 5.0 kHz, 100% wide | 2079 Hz |
| 3.0 kHz, 60% wide | 1247 Hz |

Procedure: key the radio into a dummy load, inject the tone at the sound card output, and raise the
level until the carrier nulls. Note the sound card output voltage at the null. That gives you volts
per kHz at your tap, and hence the Rt/Rb ratio that puts 0 dBFS at 90% deviation.

The 60% row is worth knowing: Tait's own internal 1200 baud modem defaults to 60% of the class
deviation ceiling (calibration manual p.15), which is 1.5 kHz on a 12.5 kHz channel. That is a
sensible operating point for a data mode and a good sanity check that your interface is in the right
territory.

**Watch for the limiter while you do this.** If deviation stops following the input level before the
null appears, you are into the limiter, which would mean the tap is upstream of it and not the T13
this note assumes. Worth watching for, free, during a calibration you were doing anyway - the
service manual does not describe T13 at all, so its position comes from the programming application
rather than from a document that can be cited, and this is the cheapest confirmation available.

### While you are there: two more useful sweeps

- **Transmit response.** Sweep the tone from 100 Hz to 4 kHz at a level well below limiting and
  watch deviation. A roll-off below 300 Hz says the 300 Hz high-pass is in circuit, so you are
  upstream of it. A rise with frequency says pre-emphasis is enabled.
- **Receive response.** Sweep the signal generator's modulation frequency and watch the tap output.
  At R1 it should be flat well past 3 kHz; if it rolls off at 3 kHz you are not where you think you
  are.

Both take minutes and both settle questions the documentation cannot.

## What goes wrong

- **Tap gated to busy-detect or PTT**, so the modem hears nothing or hears the channel late. Looks
  like an acquisition fault. Check the Tap Out Unmute setting first when a receiver seems deaf.
- **Microphone still live**, so the modem's audio and the microphone are summed. Mute the
  microphone in the programming, or use a tap type that bypasses rather than combines.
- **+20 dB mic boost left on**, which puts the receive path 20 dB into clipping and makes every
  strong signal decode worse than a weak one.
- **The dongle's own input capacitor**, typically 1 uF, cutting the low end on a flat R1 tap and
  causing baseline wander.
- **Over-driving the modulator.** At T13 nothing downstream protects the transmitter: no limiter, no
  peak-system-deviation scaler. The hardware ceiling described above is not optional, and a software
  fault that would merely sound bad through the microphone will splatter here.
- **Ground loop through the vehicle**, heard as alternator whine and as a noise floor that rises
  with engine speed.
- **Assuming R1 and R5 have the same gain.** They do not, and only R5's is documented.
