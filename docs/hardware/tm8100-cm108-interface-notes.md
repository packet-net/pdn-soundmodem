# TM8100 to CM108: extended notes

The reasoning, provenance and arithmetic behind the audio interface between a TM8100/TM8200 mobile
and a CM108-class USB sound card, for headless packet operation with `pdn-soundmodem`. The build
instructions for the specific hardware pair actually deployed are in
[tm8100-cm108-interface.md](tm8100-cm108-interface.md); this note is the generic CM108-class
treatment behind them, and where the bench procedures live.

**No presets.** The two resistors are computed from published Tait figures and confirmed once on the
bench. Fine level setting is done in software, where it belongs: the CM108 has capture and
playback gain controls that can be set remotely, logged, and put in a config file, none of which is
true of a trimmer.

**What changed, and it changed a lot.** An earlier version of this note was written before
`MMA-00011-01`, the **3DK Hardware Developer's Kit Application Manual**, was in the set. It said
that no input-level-to-deviation figure was published at any tap in either direction, and that T13
appeared in no manual. Both are wrong now: the 3DK manual gives the level for every tap point in
both directions (Table 2.7, p.22), documents T13 with its own level, group delay and frequency
response, and prints Tait's own recommended configuration for an external modem on the auxiliary
connector, which turns out to be **R1 and T13**, the same two taps this note had already argued for.
The dividers below are computed rather than measured, the bench work has become confirmation rather
than discovery, and everything Tait now says about PTT, carrier detect and turnaround timing has
been folded in. The note has since split: the wiring instructions for the board actually deployed,
with values recomputed against that board's KiCad schematic, are in
[tm8100-cm108-interface.md](tm8100-cm108-interface.md), and this document keeps the reasoning.

## Provenance

Every Tait figure carries its document and page, from the set at
[M0LTE/tait-tm8100-tm8200-docs](https://github.com/M0LTE/tait-tm8100-tm8200-docs). Marks used
throughout:

| Mark | Meaning |
|---|---|
| **TAIT** | stated in the Tait documentation, cited |
| **CM108** | C-Media datasheet or ordinary dongle practice, NOT Tait |
| **DERIVED** | computed from cited figures, arithmetic shown |
| **MEASURE** | not published anywhere; you measure it once. Since the 3DK manual joined the set this applies only to the dongle |
| **CHOICE** | an engineering decision made here, not anyone's datasheet |

Component values quoted from a schematic are from `MMAB12-B1-00-814`, the **TM8100 B1 main board**
(136 to 174 MHz). No board pack for the other bands, and none for the TM8200, is in the set.

Interface figures come from `MMA-00011-01`, the **3DK Hardware Developer's Kit Application Manual**
(issue 1, March 2006, 156 pages), which is the document Tait wrote for people building hardware that
plugs into this radio. It is the authority for every level, impedance, bias and delay quoted below.
Where it and the service manual disagree, the 3DK manual is the one describing the interface as a
specification rather than as a repair aid. Its companion note in this repo,
[tm8100-internal-usb-board.md](tm8100-internal-usb-board.md), takes the same figures inside the
radio body.

## Which tap points

**R1 on receive and T13 on transmit.** That is the deployment, the rest of this note assumes it, and
it is also what Tait themselves specify for an external modem on this connector (`MMA-00011-01`
Table 5.1, p.111, reproduced under [Programming](#programming) below). Agreeing with the
manufacturer was not the plan; the note reached R1 and T13 from the block diagram before the 3DK
manual was in the set, and finding Tait had written the same answer down is a good sign about the
reasoning rather than a reason to stop reading it.

The full chain, from `MMA-00011-01` Figure 3.14, p.93:

```
  receive   demodulator -> R1 -> deviation normaliser -> R2 -> 3 kHz LPF -> R4 -> 300 Hz HPF
                        -> R5 -> de-emphasis -> R7 -> future processing -> R10 -> volume, speaker

  transmit  ALC mic audio -> T3 -> future processing -> T4 -> future processing -> T5
                          -> 300 Hz HPF -> pre-emphasis -> T8 -> limiter -> T9 -> 3 kHz LPF
                          -> T12 -> deviation scaler -> T13 -> modulator
```

**Receive: R1, the tap closest to the demodulator.** At R1 the audio has had no bandwidth-dependent
scaling, no 0.3 to 3 kHz bandpass and no de-emphasis. It is limited only by the IF filter, 7.8 kHz
total 3 dB on a 12.5 kHz channel (`MMA-00005-05` p.73, Table 3.1), so roughly 3.9 kHz of audio.

**Transmit: T13, the last tap before the modulator.** Everything the transmit chain does to audio is
upstream of it: the 300 Hz high pass, pre-emphasis, the limiter, the 3 kHz low pass and the
peak-system-deviation scaler. **An injected signal there meets none of them.**

A documentation note, because it will confuse anyone who goes looking. **The two Tait numbering
schemes are different**, and only one of them is the radio's. `MMA-00005-05` p.124 lists the CCTM
audio tap test command as accepting `r1 r2 r3 r4 r5 t1 t2 t3 t7` out and `r2 r5 t1 t5` in. The 3DK
manual and the programming application both give the real feature set (p.93): tap out at
**R1, R2, R4, R5, R7, R10, T3, T4**, tap in at **T3, T4, T5, T8, T9, T12, T13, R7, R10**. There is
no R3 or T7 in that list and no T13 in the CCTM one, so the service manual's list is a test
command's own subset in its own scheme rather than a smaller radio. **Where they disagree, follow
the 3DK manual and the programming application**, which agree with each other and with the hardware.

The distinction matters because T5, the tap the older documents configure, is on the far side of the
limiter from T13. Tait qualify their own transmitter response as "below limiting" for a tap-injected
test signal (`MMA-00005-05` p.480), a caveat only needed if the limiter is in circuit:

> Bandwidth Response: 300Hz to 3kHz, +1, -3dB relative to -6dB/octave
> *relative to 1kHz, 20% deviation, below limiting*
> Test Signal: 0dBm line input, audio tap T1

So a signal injected at T5 would still meet the 300 Hz high pass, pre-emphasis, the hard limiter and
the 3 kHz low pass. **At T13 it meets none of that**, which is the whole reason for choosing it.

The group delays put a number on how much processing that is. `MMA-00011-01` Table 2.7 gives the
absolute delay from each tap to the antenna: **T13 and T12 are 1.8 ms, T9 is 6.6 ms, T8 is 9.6 ms,
T5 is 11.6 ms, T4 and T3 are 11.7 ms.** Nearly ten milliseconds of filtering and limiting sits
between T5 and the modulator, and none of it is between T13 and the modulator.

### What T13 means for the modem, and it is not only wiring

**The transmit path is flat.** No 300 Hz high pass, so a waveform with carriers near 305 Hz is not
sitting on a filter corner. No pre-emphasis, so no tilt to undo. No 3 kHz low pass, so the audio
bandwidth is bounded by the modulator and the channel rather than by a voice filter.

**Nothing protects the modulator, and nothing scales the drive.** The limiter and the
peak-system-deviation scaler are both upstream. Your audio level sets deviation directly, and there
is no ceiling anywhere in the radio. Tait put a number on how far past legal the tap will happily
go: full scale at AUD_TAP_IN is 2.0 Vp-p and T13 wants 0.87 Vp-p for 3 kHz deviation, so the input
accepts **6.9 kHz of deviation** before it clips, nearly three times the narrowband ceiling.
**Over-deviation is therefore entirely yours to prevent**, which is why the transmit attenuator below
is sized so that a full-scale digital sample lands exactly on class deviation. At T5 that would be
belt and braces; at T13 it is the only belt there is.

**Tait say the same thing about the spectrum mask, and they say it about their own modem.** The
external-modem specification carries the note that "the modulation formats listed may not comply
with transmit spectral emission mask regulations in some countries. It is the integrator's
responsibility to ensure that the system complies" (`MMA-00011-01` p.111), and their commissioning
steps say to check the transmit spectrum against local requirements, "not necessary if tap in point
T8 is used" (p.113). T8 is behind the limiter and the 3 kHz low pass, which is exactly why it needs
no check and exactly why we are not using it. Injecting at T13 means the emitted spectrum is a
property of the waveform, so it is the modem's job to be clean, not the radio's.

**Peak deviation is set by your waveform's peak, so peak-to-average ratio costs you level.** With no
limiter, staying legal means setting the drive against the loudest instant in the burst, so a peaky
waveform forces the whole burst down. That is worth real decibels: on an audio-band OFDM mode,
removing one unusually peaky symbol was worth about 3 dB of sensitivity measured this way, and
nothing measurable at all when re-measured with a limiter in circuit. Both are honest answers; T13
is the first question. `M0LTE.FmChannel`'s default drive mode models this, and its
`LimitAtDeviationHz` models the other, for anyone who ends up at T5 or on the microphone.

## The radio side

Auxiliary connector, 15-way standard-density D-range socket. `MMA-00005-05` p.42 Table 2.3, and
`MMA-00011-01` p.20 Table 2.5, which agree.

| Pin | Signal | Notes |
|---|---|---|
| 7 | AUD_TAP_IN | programmable tap into the Rx or Tx audio chain, DC-coupled, analogue |
| 13 | AUD_TAP_OUT | programmable tap out of the Rx or Tx audio chain, DC-coupled, analogue |
| 15 | AGND | analogue ground, and the only ground pin on this connector |
| 12 | AUX_GPI1 | general purpose digital input, 3V3 CMOS, programmable function. **The PTT pin** |
| 10 | AUX_GPIO4 | programmable I/O, 3V3 CMOS, open collector with pullup as an output. **The carrier-detect pin** |
| 8 | +13V8_SW | switched 13.8 V, off when the radio body is off. 1 A continuous, 2 A peak for under a second |
| 6 | RSSI | analogue RSSI output, 0 to 3 V into a 1 kohm source impedance |

The 13V8_SW and RSSI figures are `MMA-00011-01` Table 2.6, p.21. The 13.8 V rating is shared with
the control head and internal options interfaces, so derate it by whatever they draw.

Mechanical: the space for a mating plug is limited to 41 mm wide by 18 mm high (`MMA-00028-05` p.22,
repeated at `MMA-00011-01` p.19). Check the backshell before making a loom. Tait's own plug for it
is IPN **240-00020-55**, supplied in the internal options kit.

**Shield the cable if it is longer than a metre.** Tait recommend a shielded cable and a metal
backshell, with the braid and the foil earthed **at the radio end only** (`MMA-00011-01` p.19,
Figure 2.7). Earthing both ends is the wrong instinct here: it makes the screen a second ground path
between the radio and the sound card, which is the loop the [Grounding](#grounding) section is
trying to avoid.

### AUD_TAP_OUT as a source

All **TAIT** now, from `MMA-00011-01` Table 2.6 (p.21), Table 2.7 (p.22) and p.91.

| Property | min / **typ** / max | Notes |
|---|---|---|
| Source impedance | 590 / **600** / 650 ohm | DC to 10 kHz, constant across frequency |
| DC pedestal | 2.1 / **2.3** / 2.5 V | no load, zero Rx frequency error |
| Signal chain | DAC at 48 kSa/s, 12 kHz low pass, buffer | |
| Level at R1 | 0.54 / **0.60** / 0.66 Vp-p | for **3 kHz deviation** at 1 kHz, **into 600 ohm** |
| Level at every other Rx tap | 0.62 / **0.69** / 0.76 Vp-p | at 60% of rated system deviation, into 600 ohm |
| Full scale | **2.0 Vp-p** into 600 ohm, **4 Vp-p** unloaded | |
| Safe limits | -0.5 to +17 V | short-circuit safe, input current under +/-20 mA |
| Group delay at R1 | **1.8 ms** | antenna to the pin |

The 600 ohms is also visible in the schematic, which is a satisfying cross-check on the mark: "the
output of the low-pass filter is amplified by 6dB by a buffer amplifier, IC201 (pins 5 to 7), and
fed via R207 and R208 to drive the CDC AUD TAP OUT interface line" (`MMA-00005-05` p.84), with R207
and R208 both 1k2 (`MMAB12-B1-00-814` p.3) in parallel, so 600 ohm.

**The factor-of-two ambiguity is resolved, and both readings were right.** An earlier version of
this note could not decide between `MMA-00005-05` p.412's "0.7Vpp with 2.4V DC offset" and p.84's
1.2 V bias at the same node, and made you measure it. The 3DK manual settles it with two words in
the test conditions column: **Rload=600 ohm**. The 0.69 Vp-p figure is into a matched load, so the
open-circuit voltage is twice that. A light load, which is what the divider below presents, sees
close to **1.4 Vp-p**. Nothing was wrong; the two documents were quoting the same signal at
different loads.

**R1 is quieter than the other taps, and now by a known amount.** It sits ahead of the
bandwidth-dependent normaliser, and Tait specify it accordingly: in volts per kilohertz of actual
deviation, where every other receive tap is specified as a percentage of the channel's rating. That
difference in how the specification is written is itself the statement that R1's volts per kilohertz
does not move with channel spacing, and the other taps' does. **R1 gives 0.20 Vp-p per kHz into
600 ohm, 0.40 Vp-p per kHz unloaded.** Rated system deviation is 2.5 kHz narrowband, 4.0 kHz mid,
5.0 kHz wide (Table 2.7), so R5's 0.69 Vp-p at 60% of rating works out per kilohertz as:

| Channel | 60% of rating | R5 | R1 | R1 relative to R5 |
|---|---|---|---|---|
| 12.5 kHz | 1.5 kHz | 0.46 Vp-p/kHz | 0.20 Vp-p/kHz | **-7.2 dB** |
| 20 kHz | 2.4 kHz | 0.29 Vp-p/kHz | 0.20 Vp-p/kHz | -3.1 dB |
| 25 kHz | 3.0 kHz | 0.23 Vp-p/kHz | 0.20 Vp-p/kHz | -1.2 dB |

All **DERIVED** from the two published levels. Anything that assumed one gain for both taps was out
by up to 7 dB, in the direction of an under-driven sound card.

Two consequences worth having in mind:

- **R1 cannot clip on anything the IF passes.** Full scale is 2.0 Vp-p into 600 ohm, which at
  0.20 Vp-p per kHz is **10 kHz of deviation**. The 12.5 kHz channel filter is 7.8 kHz wide, so a
  signal deviating hard enough to clip this tap was never going to reach it. **DERIVED.**
- **R1's DC pedestal moves with receive frequency error**, which is true of R1, R2 and R4 only
  (p.91) and is unsurprising, since the raw discriminator output is frequency. C1 below blocks it
  absolutely, so this costs nothing here, but do not treat the pedestal as a constant if you ever
  DC-couple: a 2 kHz frequency error shifts it by about two thirds of a volt.

### AUD_TAP_IN as a load

All **TAIT**, from `MMA-00011-01` Table 2.6 (p.21), Table 2.7 (p.22) and p.91-92.

| Property | min / **typ** / max | Notes |
|---|---|---|
| Input impedance | 50 / **100** / 150 kohm | DC to 10 kHz. It is a bias divider, 220k from 3V3 and 180k to ground |
| DC bias | 1.4 / **1.5** / 1.6 V | internally generated |
| Valid input range | **0.5 to 2.5 V** | regardless of bias error |
| Level at T13 | 0.78 / **0.87** / 0.96 Vp-p | for **3 kHz deviation** at 1 kHz |
| Level at T12, T9, T8, T5, T4, T3 | 0.62 / **0.69** / 0.76 Vp-p | at 60% of rated system deviation |
| Full scale | **2.0 Vp-p** | |
| Input filtering | 3.7 Hz switched-capacitor high pass, 22 kHz low pass, 48 kHz ADC | |
| Safe limits | -17 to +17 V | |
| Group delay at T13 | **1.8 ms** | the pin to the antenna |

**DERIVED, and it is the number the transmit divider hangs on.** 0.87 Vp-p for 3 kHz is
**0.29 Vp-p per kHz**, so 100% of narrowband class deviation, 2.5 kHz, wants **0.725 Vp-p** at the
pin, and the 2.0 Vp-p full scale corresponds to 6.9 kHz.

**Preserve the bias.** Tait are explicit (p.92): "to avoid asymmetrical clipping and reduced dynamic
range, it is important that the input bias voltage is preserved when driving the input. This can be
achieved by simply AC-coupling the drive signal." The 3.7 Hz high pass immediately after the pin is
digital and removes the bias internally, so a bias error cannot pull the transmit carrier frequency
(Table 2.7 note 4); what it can do is cost you headroom on one half of the waveform. If you would
rather DC-couple, their external-modem specification asks for **1.5 +/- 0.2 V** from the driver
(p.111), and where a modem cannot supply that they suggest "a large coupling capacitor, typically
10uF".

Board-level detail from the schematic, which the 3DK manual does not cover: the connector pin
reaches the codec through R241 = 4k7 and IC205's clamp diodes, with ferrite L709 and 10 nF C720 to
AGND at the connector itself. **That 10 nF is the reason the transmit interface below is
deliberately low impedance**: into a 750 ohm source it is a pole at 21 kHz and irrelevant, but into
a 10k source it would be a pole at 1.6 kHz and would eat the top of the audio band.

### If you work more than one channel spacing

R1 and T13 are the two taps whose levels are specified in kilohertz of deviation rather than as a
percentage of the channel's rating, which is the same thing as saying **their gain does not follow
the channel spacing**. Tait state the consequence directly: if the modem's channels are not all the
same spacing, "tap in T12 and tap out R2 should be used. The signal levels on these taps are
automatically scaled to match the channel spacing, i.e. 3kHz deviation on a 25kHz channel and
1.5kHz deviation on a 12.5kHz channel will result in the same tap in and tap out signal levels"
(`MMA-00011-01` p.112).

T12 and R2 sit on the far side of the deviation scaler and normaliser from T13 and R1 and are
otherwise identical, same 1.8 ms group delay, same absence of filtering. **So the choice is: R1/T13
for one fixed spacing and a level you set yourself, or R2/T12 for a level that follows the channel
at the cost of the radio deciding your drive.** For a fixed 12.5 kHz packet channel, R1 and T13 are
right. For a radio that roams between 12.5 and 25 kHz channels, R2 and T12 will save you a divider
per bandwidth.

## If the board is yours

The rest of this note is written for a stock dongle, where several values are somebody else's choice
and the design works around them. **On a custom board most of those workarounds are unnecessary and
some of the advice inverts.** In rough order of value:

**Use a line input, not the microphone input.** The mic path is only recommended above because it is
the only input a standard dongle brings out. A line input has more headroom, a flatter response,
lower noise, no bias resistor fighting the divider, and no +20 dB boost waiting to be left on by
accident. It also removes the single worst trap in the stock design: dongle microphone coupling
capacitors are commonly 1 uF or less, which puts the corner at 80 Hz or above and causes visible
baseline wander on the flat, wide R1 tap. Choose the capacitor yourself and that problem does not
exist.

**Put the attenuators on the board, at computed values, confirmed once.** The dividers exist to get
a known tap level into an arbitrary dongle. With the board in your hands both ends are yours, so fit
the divider as designed-in resistors. Keep the transmit ratio such that a full-scale sample gives
**100% of class deviation and not a decibel more**: at T13 nothing in the radio limits, so that
ceiling is the only protection there is, and it is worth more when it is a property of the PCB than
of a config file.

**Bring RSSI in on a second input channel.** Auxiliary pin 6 is an analogue RSSI output, **0 to 3 V
from a 1 kohm source** (`MMA-00011-01` Table 2.6, p.21), with the voltage-to-signal-strength curve in
their Table 2.9. If the codec variant has a stereo line input, the right channel costs nothing and
gives the modem a per-burst received-signal-strength reading sampled alongside the audio it decoded.
That is the measurement that turns "it decoded" into "it decoded at -113 dBm", which is exactly what
is needed to check a simulator against a radio, and no off-the-shelf dongle can do it. It is a slow
DC-ish signal, so it wants a DC-coupled input or a codec that will report it: an AC-coupled line
input will show you the edges of a burst and not its level. Check the input arrangement of the exact
part before designing it in; the CM108AH's is mono, and the stereo-line-input variants are different
devices.

**Design the isolation in rather than deciding later.** Two 600:600 transformers and an opto-isolated
PTT, on the board, remove a whole class of vehicle ground-loop fault that is tedious to diagnose
after the fact. Watch the transformer's low-frequency response against the lowest carrier in use,
since R1 is flat well below where a telecoms transformer is.

**Drive PTT properly.** AUX_GPI1 needs almost nothing from a driver: it has a 33k pullup to 3.3 V
behind a 47k series resistor and clamp diodes, and asks only that you pull it below 0.7 V while
sinking its 120 uA (`MMA-00011-01` Table 2.6, p.21, and Figure 3.1, p.41). On your own board use an
open-drain stage or an opto-isolator anyway, for the isolation rather than for the current, and
consider whether a hardware timeout belongs there too: a stuck PTT is the one failure that annoys
everybody else on the channel.

**Keep the deviation check regardless, as a check.** The tap levels are published now, so the
Bessel-null procedure below is no longer the only route to a number; it is the independent second
opinion on a number you already have. Do it anyway. It costs an evening and it is the only thing
standing between a wrong assumption and a splattered channel.

## The CM108 side

All **CM108**, and all worth confirming on your actual dongle, which is the one thing you measure.

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

## The board actually deployed

The deployment uses the single-sided CM108AH board at
[tomwardill/cm108radiowidget](https://github.com/tomwardill/cm108radiowidget), and its KiCad
netlist changes four of this note's stock-dongle assumptions. The build values in
[tm8100-cm108-interface.md](tm8100-cm108-interface.md) already account for all of this.

- **No microphone bias network.** MICIN is reached through 1 uF with the CM108AH's VBIAS pin
  unconnected, so there is no 2k2 bias resistor: the receive shunt is Rp alone rather than
  1k || 2k2 = 688 ohm, and the Rs values in the receive table below land about 3 dB hot on this
  board. The build note's table is computed for Rp = 1k alone. The stock-dongle trap of a 1 uF
  input capacitor against the bias network, an 80 Hz corner on a flat tap, does not exist here
  either: the same 1 uF sees MICIN's high input impedance instead.
- **The line out is already AC-coupled**, 1 uF on board, left channel only. Against the transmit
  divider's 4k load that is a 40 Hz corner, 0.13 dB at a 305 Hz carrier.
- **The PTT transistor is already on the board**: an open-drain BSS138 on GPIO3, which is exactly
  the stage the PTT section below asks for. Wire it straight to pin 12 and add nothing; a second
  open-collector stage would invert the logic. The gate has no pull-down, so it floats briefly
  while USB enumerates; 100k across gate and source closes that window.
- **A SQUELCH input exists**: a small Schottky into the CM108's volume-down pin, active low. It
  maps onto AUX_GPIO4 programmed Busy Status active Low (the generic table below says High; the
  diode wants Low, and the radio's floating-then-pulled-up power-up state then reads idle rather
  than busy, the right failure for a cross-check). It arrives at the host as a HID volume-down
  key: readable over hidraw, but anything that maps consumer keys to a mixer will wind the
  calibrated gain down while the channel is busy. Leave it unwired unless something is actually
  reading it.

## Receive path

```
  AUX pin 13                      Rs                             to CM108
  AUD_TAP_OUT                  (table)                          mic or line in
  600R src, +2.3V DC   C1      _______                    C2
      o------------||---------|_______|----+--------+-----||------o
                   2u2                     |        |     10u
                                          [ ] Rp   === C3
                                           |  1k    |  4n7
  AUX pin 15  o----------------------------+--------+-------------o  dongle GND
  AGND                                                               (see Grounding)
```

Rp and C3 are parallel shunt legs from the same node; an earlier rendering of this diagram stacked
them into one string, which reads as a series leg that stops shunting below radio frequencies.

**C1 blocks the +2.3 V pedestal absolutely**, so the interface presents no DC path and does not
disturb the radio's bias. Tait's own crossband cable does load the tap with 600 ohm to ground and
accepts the resulting 6 dB loss; there is no reason for us to.

**Rp is fixed at 1k. Rs comes from the table below.** The shunt leg sits in parallel with the
dongle's microphone bias resistor, 1k || 2k2 = 688 ohm **DERIVED**.

The tap's side of the arithmetic is now Tait's rather than yours. R1 delivers **0.40 Vp-p per kHz
of deviation open circuit**, behind 600 ohm, so at 60% of a 12.5 kHz channel's rating (1.5 kHz) the
source is 0.60 Vp-p, **212 mVrms**, and on a 25 kHz channel at 60% (3.0 kHz) it is exactly twice
that.

Target: **-12 dBFS at 60% of class deviation** **CHOICE**. The consequences of that choice can be
stated exactly now: 100% of class lands at -7.6 dBFS, the sound card clips at 6.0 kHz of deviation
(240% of narrowband class), and the tap itself clips at 10 kHz, which no signal that got through a
7.8 kHz IF filter can reach. The first thing to clip is the codec, on a station over-deviating by
more than a factor of four.

| CM108 full scale **MEASURE** | 12.5 kHz channel | 25 kHz channel |
|---|---|---|
| 50 mVrms | Rs = 10k, load 10.7k | Rs = 22k, load 22.7k |
| 100 mVrms | Rs = 4k7, load 5.4k | Rs = 10k, load 10.7k |
| 150 mVrms | Rs = 2k7, load 3.4k | Rs = 6k2, load 6.9k |
| 250 mVrms | Rs = 1k0, load 1.7k | Rs = 3k3, load 4.0k |

E24 values, rounded from the exact result, all within 0.4 dB of the target. **DERIVED** throughout,
from Tait's R1 level and your dongle's full scale. The load column is what the tap sees; every one
of them is far lighter than the 600 ohm Tait themselves demonstrate is a working load, and the
resulting insertion loss is already inside the Rs figure.

**C3 is RF hygiene, not anti-aliasing.** 4n7 against roughly 625 ohm is a pole at 54 kHz; the
CM108's own decimation filter does the anti-aliasing. If aliased noise appears on the wideband R1
tap, raise C3 to 8n2, which moves the pole to 31 kHz and costs about 0.4 dB at 9.6 kHz.

**Low-frequency corners.** C1 2u2 works against the whole series path, so its corner moves with Rs:
6 Hz at the top of the table, 32 Hz at the bottom. C2 10u into the dongle's bias network is about
8 Hz. **If your dongle's full scale puts you in the bottom row, raise C1 to 10u**, which returns the
corner to 7 Hz. All of this is moot if the dongle's own input capacitor is the limit, which is the
reason for the line-input recommendation above.

## Transmit path

```
  from CM108                  Rt                  C4        AUX pin 7
  line out                  (table)                         AUD_TAP_IN
  AC-coupled                 _______                        100k, +1.5V bias
      o---------------------|_______|-----+-------||--------o
                                          |       1u
                                         [ ] Rb 1k
                                          |
      o-----------------------------------+-----------------o  AUX pin 15 AGND
        dongle GND
```

**Size the attenuator so full-scale digital cannot over-deviate.** Choose Rt/Rb such that a 0 dBFS
sine at the CM108 output produces **exactly 100% of class deviation** **CHOICE**, that is 2.5 kHz on
a 12.5 kHz channel. An earlier version of this note said 90%, and that margin costs 0.9 dB of the
single most valuable quantity on this link: post-detection signal to noise goes as deviation
squared, and dropping from 2500 to 1500 Hz peak measures 3 to 4 dB of sensitivity. A ceiling at the
legal limit means a software fault produces exactly legal modulation, and normal operation gets the
whole budget. Then no software fault, mixer setting or runaway process can splatter: the hardware
ceiling is the legal one. The modem then runs at whatever level below that the mode wants, set in
software.

This is the property that replaces the preset, and it is strictly better than one: a trimmer set to
90% today can be knocked to 200% tomorrow, and a fixed divider cannot.

**T13 wants 0.725 Vp-p for 2.5 kHz**, from Tait's 0.87 Vp-p per 3 kHz. **Rb is fixed at 1k and Rt
comes from your dongle's output full scale**, the one quantity here nobody publishes:

| CM108 output full scale **MEASURE** | Rt | Divider | Deviation at 0 dBFS |
|---|---|---|---|
| 0.5 Vrms | 1k0 | -6.0 dB | 2.44 kHz |
| 0.7 Vrms | 1k8 | -8.9 dB | 2.44 kHz |
| 1.0 Vrms | 3k0 | -12.0 dB | 2.44 kHz |
| 1.4 Vrms | 4k7 | -15.1 dB | 2.40 kHz |
| 2.0 Vrms | 6k8 | -17.8 dB | 2.50 kHz |

E24 values, each the nearest that lands **under** the ceiling rather than over it. **DERIVED.** The
old 2k2/220R in this diagram was chosen for its impedance before any level figure existed, and at
-20.8 dB it would have driven a 1 Vrms dongle to about 0.9 kHz, a third of what the channel allows.

**Then check it on the radio in front of you, because the spread is wider than the divider.** Tait's
T13 level is 0.78 / 0.87 / 0.96 Vp-p, which is **+/-0.9 dB** of radio-to-radio spread on the one
number the ceiling depends on. A divider computed from the typical figure over-deviates by 9% on a
radio at the sensitive end and under-drives by 11% at the other. 1% resistors are not the
uncertainty that matters here. **Measure the null once per radio** and move Rt by one E24 step if
that radio sits at an end of the spread. If you are never going to measure, size Rt on the 0.78 Vp-p
figure instead, which makes the worst case exactly legal and costs the typical radio 0.9 dB.

**Thevenin impedance is 3k0 || 1k = 750 ohm DERIVED** at the 1 Vrms row, which puts the pole formed
with the radio's connector-side 10 nF at 21 kHz, well clear of the audio band. Keep it under about
1 kohm on any row. The tap's own 100 kohm input loads the shunt leg by under 1%, which is inside the
rounding above.

**C4 goes between the divider and the pin, and this is a correction.** Earlier versions of this
diagram put the coupling capacitor between the dongle and Rt, which leaves the shunt leg Rb sitting
across AUD_TAP_IN with a DC path to ground. The manual makes the consequence explicit: the tap's
1.5 V bias comes from a 220k/180k divider off 3V3, a 100 kohm source (p.91, Figure 3.12), so a 1k
shunt on the radio side of the block would pull the bias to about 15 mV, far outside the valid
0.5 to 2.5 V window, and half of every waveform would clip against the bottom of the ADC's range.
**Put the DC block last, so the divider's shunt leg returns to the dongle's ground and never sees
the tap's bias.** 1u against 100 kohm is a 1.6 Hz corner, below the radio's own 3.7 Hz high pass, so
the radio stays the limiting element rather than this interface. Use film or a bipolar type, not a
leaky electrolytic: leakage into a 100 kohm bias node is a bias error. Tait suggest a "large
coupling capacitor, typically 10uF" for a modem that cannot supply the bias itself (p.111), which is
the same instruction with more margin; either works, and 1u is easier to find as film.

## PTT

AUX_GPI1 on pin 12 is a 3.3 V CMOS input **TAIT**, and the 3DK manual gives what it takes to drive
it (Table 2.6 p.21, Figure 3.1 p.41): a **33k pullup to 3.3 V** behind a **47k series resistor and
clamp diodes**, recognising a low at or below **0.7 V** and a high at or above **1.7 V**, drawing
**100 to 120 uA** when held low. The inputs are 3.3 V CMOS, 5 V CMOS and 5 V TTL compatible but are
**not RS-232 tolerant** without 3k3 in series at the radio end (Table 3.2, p.40).

**So drive strength is not the reason for the transistor.** Anything can sink 120 uA. Use a
small-signal MOSFET or an open collector anyway: it keeps the two grounds one component apart, it
turns into an opto-isolator later without redesigning anything, and it means a dongle that boots
with its GPIOs in an undefined state cannot key the radio.

**Set the active state to Low, and this is not a preference.** Tait: "because of the pullups,
setting the active state to High will cause the action to commence if the connector is removed or
dislodged while the radio is on. To prevent this happening, set the active state to Low"
(`MMA-00011-01` p.42). An active-high PTT on a 15-way D-range in a vehicle is a transmitter that
keys itself when the plug works loose.

**Set the debounce to zero.** The field exists for mechanical switches, where Tait recommend 50 to
100 ms; every millisecond of it is added to your keying latency, and a logic signal has no bounce to
remove. Their own external-modem configuration sets it to 0.

**One power-up behaviour worth knowing.** If the PTT line is already active when the radio powers
up, "it must be re-applied for the action to be carried out" (`MMA-00011-01` p.51). A host that
comes up with its GPIO asserted will not transmit until it deasserts and asserts again, which looks
like a dead PTT and is not one.

## Programming

Tait publish the configuration for an external modem on this connector, and it is worth following
exactly, because every field in it is one you would otherwise get wrong once
(`MMA-00011-01` p.112).

Programmable I/O form, Digital tab:

| Pin | Direction | Action | Active | Debounce | Signal state |
|---|---|---|---|---|---|
| AUX_GPI1 | Input | External PTT1 | Low | 0 | None |
| AUX_GPIO4 | Output | Busy Status | High | None | Momentary |

Programmable I/O form, Audio tab:

| Rx / PTT type | Tap in | Tap in type | Tap in unmute | Tap out | Tap out type | Tap out unmute |
|---|---|---|---|---|---|---|
| Rx | None | | | **R1** | **D - Split** | **Except on PTT** |
| EPTT1 | **T13** | **A - Bypass In** | On PTT | None | | |

PTT / External PTT (1) form, Advanced EPTT1 group: **PTT Transmission Type: Data**, PTT State Is
Reflected cleared, **PTT Priority: Highest**, **Audio Source: Audio Tap In**. Networks / Basic
Settings: **Squelch Detect type: Signal Strength**.

Four of those deserve a sentence each.

**Data, not Voice.** An earlier version of this note quoted `MMA-00005-05` p.573's "PTT Transmission
Type: Voice, Audio Source: Audio Tap In", which is the right pairing of PTT and audio source with
the wrong transmission type for a modem.

**D - Split, not C - Bypass Out.** Split copies the audio out and lets the radio's own receive path
carry on; Bypass Out diverts it. Tait flag the consequence: "do not use 'Bypass Out' on R1, R2 and
R4 with subaudible or inband signalling schemes, as this may prevent correct operation of the
signalling decoder" (p.94). Split costs nothing and keeps CTCSS, busy detect and the speaker alive.

**Except on PTT** is the tap-out gate a modem wants, and it was missing from the earlier list. The
full set for the receive path is Busy Detect, Busy Detect and Subaudible, Rx Mute Open, and Except
on PTT (p.95). A modem gated on busy-detect misses the start of every burst, which looks exactly
like an acquisition fault in the modem and is not one. Except on PTT hands you the channel
continuously and mutes only while you are transmitting.

**Highest priority** matters because all PTT sources are live at once: "all PTT lines may be active
at any time, and the PTT line with the highest priority controls the audio path" (p.51). Without it,
a microphone left on the desk outranks the modem.

Tait add one system note: put every channel the modem uses in one network and every voice channel in
another, so the two sets of channel settings cannot drag each other around (p.112).

## Carrier detect, free, on a pin

AUX_GPIO4 on pin 10 configured as **Busy Status** gives the modem a hardware channel-busy line, and
nothing in the earlier version of this note used it. It reflects whether the receiver sees a
carrier, and the detection method is chosen in Squelch Detect Type: **signal strength (RSSI)
responds in under 5 ms, noise level in under 20 ms** (`MMA-00011-01` p.77). That is the whole reason
Tait's modem recipe specifies signal strength.

Electrically it is an open collector with a pullup, and the pullup rail is selectable on the main
board: R769 to 3.3 V is the factory default, R778 to 5 V and R782 to 13.8 V are the alternatives,
exactly one of which must be fitted (p.72). Leave it at 3.3 V for a CM108 GPIO input. Current
through the pullup must not exceed 5 mA when the output is low.

**It is not a DCD replacement, it is a cross-check.** The modem's own data-carrier detect knows
about modulation; this line knows about energy. Reading both tells you the difference between a
channel that is busy and a channel that is busy with something you can decode, which is worth having
in a log when a link goes quiet.

**During the first second or two after the radio powers up, believe none of it.** Outputs are
high-impedance until the radio takes control, "up to 1 to 2 seconds after power is first applied",
and the pullups dominate, so every output reads high (p.73). With Busy Status active high that
reads as a busy channel, which at least fails in the safe direction.

## Turnaround timing

`MMA-00011-01` Table 5.2, p.113, measures the delays a packet modem actually cares about. All
**TAIT**, all with zero debounce programmed:

| Path | Delay |
|---|---|
| PTT asserted to full carrier power **with valid modulation**, via T13 or T12 | **14.8 +/- 0.5 ms** |
| the same, via T9 | 14.3 +/- 0.5 ms |
| the same, via T8 | 17.8 +/- 0.5 ms |
| PTT released to valid baseband modulation out of R1 or R2 | **12.3 +/- 0.5 ms** |
| the same, via R4 | 16.9 +/- 0.5 ms |
| antenna to AUD_TAP_OUT, via R1 or R2 | 1.8 ms typical |
| AUD_TAP_IN to antenna, via T13 or T12 | 1.8 ms typical |
| valid RF at the antenna to carrier detect active | 3 ms typical |

**This sets the floor for TXDELAY.** The group delay through the transmit chain is only 1.8 ms, but
the radio needs **14.8 ms** from the PTT edge before what leaves the antenna is both at full power
and carrying your modulation. Anything the modem sends inside that window is thrown away. The
transmitter also reaches 90% of full power in under 8 ms (p.51), so the extra 7 ms is the modulation
path settling, not the PA.

**And it sets a floor on turnaround the other way.** After releasing PTT the receive path needs
**12.3 ms** before the audio coming out of R1 is valid again, so a station that replies to you
inside about 13 ms of your carrier dropping is talking to a deaf radio. Add the 3 ms carrier-detect
delay if you are gating on the busy line.

Both figures are per radio, not per link, so a two-radio round trip carries them twice.

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
| C1 | 2u2 film or bipolar electrolytic, 10u on the bottom table row | blocks the +2.3 V tap-out pedestal | **CHOICE** |
| Rs | from the receive table | receive attenuator, series leg | **DERIVED** |
| Rp | 1k | receive attenuator, shunt leg | **CHOICE** |
| C3 | 4n7 | RF hygiene at the receive input | **CHOICE** |
| C2 | 10u bipolar | couples into the sound card input | **CHOICE** |
| C4 | 1u film or bipolar, **after** the divider | blocks DC into AUD_TAP_IN, preserves its 1.5 V bias | **CHOICE** |
| Rt | from the transmit table | transmit attenuator, series leg | **DERIVED** |
| Rb | 1k | transmit attenuator, shunt leg | **CHOICE** |
| Q1 | 2N7000 or similar | PTT switch from a CM108 GPIO | **CHOICE** |
| T1, T2 | 600:600 audio transformers | optional isolation | **CHOICE** |

## The measurement that replaces the preset, and the check that confirms it

The dongle is the unknown half of this interface now. Tait publish both tap levels; nobody publishes
what a nameless USB dongle does at full scale, and it varies by unit and by mixer setting. So there
is **one measurement you have to make** and **one check you should make anyway**.

Do them once, per radio model and per dongle model. They are not per unit unless you find the spread
is large, which is worth checking on two or three of each, and Tait's own +/-10% on the tap levels
says the radios will not all agree.

### Measurement 1: what the CM108 input and output full scale actually are

Feed a 1 kHz sine into the sound card input from a signal generator, boost off, capture gain at a
known setting. Raise the level until the captured samples just clip, and note the input voltage.
That is the full-scale figure for the receive table. Do it at the capture gain you intend to run at,
and record that setting alongside it.

Then the other direction, for the transmit table: play a 0 dBFS sine at the playback gain you intend
to run at and measure the output with a true-RMS meter, into nothing. Both numbers are properties of
that dongle at those mixer settings, so write the mixer settings down beside them; the modem should
be setting them explicitly at start-up rather than inheriting whatever ALSA remembered.

### Check 2: that the published tap levels are the levels you have

**Receive.** Feed the radio a signal generator on channel, modulated 1 kHz at 3 kHz deviation, at
about -80 dBm so the receiver is well above threshold and no AGC is acting. Measure the AC level at
AUD_TAP_OUT with a scope or a true-RMS meter. Expect **1.2 Vp-p** open circuit, which is Tait's
0.60 Vp-p into 600 ohm with the load taken off. If you see 0.6 Vp-p you have loaded it; if you see
1.4 Vp-p at 60% of narrowband class you are on R5 or another late tap, not R1.

**Transmit.** Tait publish 0.87 Vp-p at T13 for 3 kHz, but with a +/-10% spread, and this is the
number that decides whether a software fault splatters. It can be confirmed without a deviation
meter. It matters more at T13 than it would anywhere else, because the peak-system-deviation scaler
is upstream and therefore bypassed: nothing in the radio is normalising your level, so what you
inject is what deviates.

Use a **Bessel null**. A carrier frequency-modulated by a single tone has its carrier component
vanish at a modulation index of 2.405, so if you feed a tone of frequency `f` and watch the carrier
on any SDR or spectrum analyser, the deviation at the null is exactly `2.405 x f`. Choose the tone
so the null lands on the deviation you want:

| Target deviation | Tone for first carrier null |
|---|---|
| 2.5 kHz, 100% narrowband | 1040 Hz |
| 1.5 kHz, 60% narrowband, Tait's own data default (too low for OFDM, see below) | 624 Hz |
| 4.0 kHz, 100% mid | 1663 Hz |
| 5.0 kHz, 100% wide | 2079 Hz |
| 3.0 kHz, 60% wide | 1247 Hz |

Procedure: key the radio into a dummy load, inject the tone at the sound card output, and raise the
level until the carrier nulls. Note the sound card output voltage at the null. That gives you volts
per kHz at your tap, and hence the Rt/Rb ratio that puts 0 dBFS exactly on class deviation. Compare
it with the 0.29 Vp-p per kHz the manual predicts at the pin: agreement inside 10% means your radio
is an ordinary one and the table's Rt stands.

The 60% row is worth knowing as Tait's own default (calibration manual p.15) and worth NOT copying.
It is sized for FFSK, whose crest factor is about 3 dB, so 60% of peak still puts plenty of energy on
the carrier. An OFDM waveform runs at about 12 dB crest factor, so the peak that sets the drive
carries almost none of its energy, and 60% throws away 3 to 4 dB of sensitivity - measured, 32 seeds,
at fixed received power in a fixed IF. **Calibrate to 100% of class deviation and let the modem's own
peak reduction bring the crest factor down**, which is worth another 2.5 dB and costs no spectrum.

**Watch for the limiter while you do this.** If deviation stops following the input level before the
null appears, you are into the limiter, which means the radio is not tapping in where you think it
is. T13's position is documented now, so this is a check on the programming rather than on the
documentation, but it is free during a calibration you were doing anyway and it is the fastest way
to catch a radio that was programmed for T5.

### While you are there: two more useful sweeps

- **Transmit response.** Sweep the tone from 100 Hz to 4 kHz at a level well below limiting and
  watch deviation. A roll-off below 300 Hz says the 300 Hz high-pass is in circuit, so you are
  upstream of it. A rise with frequency says pre-emphasis is enabled.
- **Receive response.** Sweep the signal generator's modulation frequency and watch the tap output.
  At R1 it should be flat well past 3 kHz; if it rolls off at 3 kHz you are not where you think you
  are. Tait plot the expected shape per bandwidth class in `MMA-00011-01` Table 2.10, p.24, so this
  one now has an answer to compare against rather than just an expectation.

Both take minutes and both settle questions faster than reading does.

## What goes wrong

- **Tap gated to busy-detect or PTT**, so the modem hears nothing or hears the channel late. Looks
  like an acquisition fault. Check the Tap Out Unmute setting first when a receiver seems deaf, and
  set it to **Except on PTT**.
- **PTT programmed active high**, so the radio keys itself the moment the D-range works loose. Set
  the active state Low.
- **Debounce left at the default** on the PTT line, quietly adding tens of milliseconds to every
  turnaround. Set it to 0.
- **TXDELAY set from the group delay.** 1.8 ms is how long audio takes to cross the radio; **14.8 ms**
  is how long the radio takes to be transmitting your audio at full power. Sizing on the first loses
  the head of every burst.
- **Microphone still live**, so the modem's audio and the microphone are summed. Mute the
  microphone in the programming, or use a tap type that bypasses rather than combines.
- **+20 dB mic boost left on**, which puts the receive path 20 dB into clipping and makes every
  strong signal decode worse than a weak one.
- **The dongle's own input capacitor**, typically 1 uF, cutting the low end on a flat R1 tap and
  causing baseline wander.
- **Over-driving the modulator.** At T13 nothing downstream protects the transmitter: no limiter, no
  peak-system-deviation scaler, and the tap will accept 6.9 kHz of deviation without complaint. The
  hardware ceiling described above is not optional, and a software fault that would merely sound bad
  through the microphone will splatter here.
- **The shunt leg of the transmit divider left on the radio side of the DC block**, which collapses
  the tap's 1.5 V bias into a 100 kohm source and clips half of every waveform.
- **Ground loop through the vehicle**, heard as alternator whine and as a noise floor that rises
  with engine speed.
- **Assuming R1 and R5 have the same gain.** They do not: R1 is 7.2 dB below R5 on a 12.5 kHz
  channel, and unlike R5 its gain does not follow the channel spacing.
