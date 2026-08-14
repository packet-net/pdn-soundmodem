# Wiring the CM108 radio widget to a Tait TM8100

Build instructions for connecting the single-sided CM108 interface board at
[tomwardill/cm108radiowidget](https://github.com/tomwardill/cm108radiowidget) to a TM8100/TM8200
auxiliary connector, for headless packet operation with `pdn-soundmodem`. Values are for 12.5 kHz
channels, where 100% of class deviation is 2.5 kHz.

This note is only what to build. Every figure is justified, cited and derived in the
[extended notes](tm8100-cm108-interface-notes.md). The radio side comes from the 3DK Hardware
Developer's Kit Application Manual (`MMA-00011-01` in
[M0LTE/tait-tm8100-tm8200-docs](https://github.com/M0LTE/tait-tm8100-tm8200-docs)), whose section
5.1.1 is Tait's own worked example of exactly this job; the widget side comes from reading its
KiCad schematic.

## What the widget provides

No connector: a row of five labelled solder pads. The audio names are from the widget's point of
view, not the radio's:

| Pad | What it is on the board |
|---|---|
| OUT | CM108AH line out (AUDIO_OUT in the schematic): transmit audio, through 1 uF on board |
| IN | CM108AH microphone in (AUDIO_IN): receive audio, through 1 uF on board; no bias network is fitted |
| PTT | open-drain BSS138; pulls to ground while CM108 GPIO3 is high |
| SQL | active-low input; pulling it low presses the CM108's volume-down key through a Schottky |
| GND | board ground |

## The build: pads to a DE-9 socket

Hang a 9-way D-sub socket off the back of the board on a short tail, with the discrete
components in that tail, wired to the standard Kantronics/NinoTNC radio-port convention: TXA on
1, TX inhibit on 2, PTT on 3, RXA on 5, ground on 6 (verified in
[ninotnc-loop.md](../ninotnc-loop.md) from TARPN's cable documentation, and proven by the bench
loop that ran on it). Use a female socket, which is what a NinoTNC presents; the assembly then
takes the same radio cables a NinoTNC uses.

```
  widget pads                                                  DE-9 socket, rear

  OUT o-----[ Rt 3k0 ]-----+------------| |--------------------o 1  TXA
                           |           C4 1u
                          [ ] Rb 1k
                           |
  GND o--------------------+--------+-----------+--------------o 6  GND
                                    |           |
                                   [ ] Rp 1k   === C3 4n7
                                    |           |
  IN  o-----------------------------+-----------+--[ Rs 6k8 ]--| |--o 5  RXA
                                                              C1 4u7
  PTT o--------------------------------------------------------o 3  PTT

  SQL o--x  leave unconnected              pins 2, 4, 7, 8, 9: empty
```

Pin by pin, from the iron's point of view:

- **Pin 6 (GND): plain wire to the GND pad.** This wire is also the return for every shunt
  component: Rb, Rp and C3 all land on it.
- **Pin 3 (PTT): plain wire to the PTT pad.** No components: the board already has the
  open-collector stage, and the radio end of the cable has the pull-up.
- **Pin 1 (TXA): three components.** From the OUT pad, Rt in series. From Rt's far end, Rb down
  to the ground wire. From that same junction, C4 to pin 1. **C4 must be the last element before
  the pin**: AUD_TAP_IN at the far end of the cable is internally biased to 1.5 V behind roughly
  100k with a valid range of 0.5 to 2.5 V, and a shunt resistor DC-coupled to it drags that bias
  to millivolts and clips every negative half cycle. Built as drawn, Rb returns to the widget's
  ground and never sees the radio's bias.
- **Pin 5 (RXA): four components.** From pin 5, C1 first, which blocks the tap's +2.3 V offset.
  Then Rs in series. From Rs's far end, Rp and C3 down to the ground wire. That junction wires
  to the IN pad.
- **The SQL pad and DE-9 pins 2, 4, 7, 8 and 9: nothing.** The convention has no carrier-detect
  pin, so SQL stays empty; pin 2 is TX inhibit on a real NinoTNC and this assembly does not
  implement it.

Physically, all seven components fit on the DE-9's solder cups, free-standing with heatshrink
over each leg and the lot, or on a fingernail of stripboard in the tail. Keep the tail short.

**Mind the direction.** This is the opposite way round from the bench loop in
[ninotnc-loop.md](../ninotnc-loop.md). There the widget played the radio, recording a NinoTNC's
output, so its OUT fed RXA on pin 5 and its IN listened to TXA on pin 1. Here the widget is the
TNC: OUT drives TXA on pin 1 and IN listens on RXA on pin 5. Copying the loop table into this
build swaps transmit and receive.

### DE-9 plug to the radio

Four wires, straight through, to a 15-way standard-density D-sub plug in the auxiliary
connector. The space for that plug is limited to 41 mm wide by 18 mm high (`MMA-00028-05` p.22),
so check the backshell. Keep the total run screened and short (a metre or two), screen to the
radio backshell and pin 15.

| DE-9 pin | Radio pin | Signal |
|---|---|---|
| 1 (TXA) | 7 | AUD_TAP_IN |
| 3 (PTT) | 12 | AUX_GPI1: internal 33k pull-up, external PTT is active low |
| 5 (RXA) | 13 | AUD_TAP_OUT |
| 6 (GND) | 15 | AGND, the only ground on the connector |

Mechanical interchangeability is not calibration: the deviation ceiling belongs to the pairing of
this assembly with this radio, so if a different device ever hangs on the DE-9, or this assembly
moves to a different radio, redo the Bessel check before trusting the levels.

### Choosing Rt (transmit)

- **Rb = 1k, Rt = 3k0**, 1% metal film, for the usual 1.0 Vrms CM108 full scale. Measure yours
  (measurement 1) and take Rt from this table, each value the nearest E24 that lands under the
  ceiling rather than over:

  | CM108 output full scale (measured) | Rt |
  |---|---|
  | 0.5 Vrms | 1k0 |
  | 0.7 Vrms | 1k8 |
  | 1.0 Vrms | 3k0 |
  | 1.4 Vrms | 4k7 |
  | 2.0 Vrms | 6k8 |

  A full-scale sine then lands about 0.70 Vp-p on the pin, just under the 0.725 Vp-p that is
  2.5 kHz at T13's published 0.87 Vp-p per 3 kHz: **full-scale digital is the deviation ceiling**,
  and normal drive sits below it in software. Confirm with the Bessel null below. If you will
  never measure the null, fit the next Rt step up (3k3 on the 1 Vrms row): the worst-case radio is
  then exactly legal and a typical one gives up 0.9 dB. The first assembly measured about
  1.0 Vrms and took 3k3 on exactly that reasoning; see Measured below.
- **C4 = 1u film or bipolar** (Tait's own suggestion of 10 uF also works). It sits across the
  tap's 1.5 V bias, so nothing leaky.
- The divider's 750R source impedance puts the pole against the connector's internal 10 nF at
  21 kHz, well out of band.

### Choosing Rs (receive)

- **Rp = 1k.** The widget has no microphone bias network (the CM108AH's VBIAS pin is
  unconnected), so Rp is the whole shunt; do not copy values from notes written for stock dongles
  with a 2k2 bias resistor.
- **Rs from the table**, against measurement 1 below. Build with **6k8** if you have not measured
  yet. The first assembly measured 455 mVrms full scale at +8 dB capture and took **1k8 at +13 dB
  capture gain**; Rs and the capture gain are a single choice, so fix the gain too. See Measured
  below.
- **C1 = 4u7 non-polarised** (blocks the tap's +2.3 V offset), **C3 = 4n7** (RF bypass, pole near
  40 kHz).

The target is -12 dBFS at 60% of class deviation, computed from R1's published 0.60 Vp-p per
3 kHz of deviation into 600R (1.2 Vp-p open-circuit):

| CM108 input full scale (measured) | Rs |
|---|---|
| 50 mVrms | 15k |
| 100 mVrms | 6k8 |
| 150 mVrms | 3k9 |
| 200 mVrms | 2k7 |
| 250 mVrms | 1k8 |

## Parts

Values are those measured for the first assembly. Rt comes from the transmit table against your
own measurement, and **Rs is paired with a capture gain**, so neither is a free choice: see
Measured above.

| Ref | Value | Type | Tolerance | Rating | Notes |
|---|---|---|---|---|---|
| Rt | **3k3** | metal film | **1%** | 0.125 W | transmit series; 3k0 instead if trimming on the Bessel null |
| Rb | **1k** | metal film | **1%** | 0.125 W | transmit shunt |
| Rs | **1k8** | metal film | 1% | 0.125 W | receive series; paired with +13.00 dB capture gain |
| Rp | **1k** | metal film | 1% | 0.125 W | receive shunt; the whole shunt, there is no bias network |
| C4 | 1u | film (PET or PP), or bipolar electrolytic | 10% or 20% | 50 V | radio side of the transmit divider; Tait's own 10u also works |
| C1 | 4u7 | non-polarised: bipolar electrolytic or film | 10% or 20% | >= 16 V | receive DC block, sits across the tap's +2.3 V |
| C3 | 4n7 | ceramic, C0G/NP0 preferred, X7R acceptable | 10% or 20% | 50 V | RF bypass at the receive shunt |
| socket | 9-way D-sub, female | | | | the assembly's radio port, wired as a NinoTNC's |
| plug | 15-way standard-density D-sub, male | | | | radio end of the cable; backshell within 41 x 18 mm |
| cable | screened, 2 pairs plus PTT | | | | DE-9 plug to 15-way plug; screen to the radio backshell and pin 15 |
| R(gate) | 100k | any | 5% | any | **optional, fitted to the widget not this assembly**: BSS138 gate to source, see Widget modifications |

### Why those tolerances

**1% on Rt and Rb is required, and it is the only place tolerance is tight.** The transmit
divider's ratio moves 0.767% per 1% of error in either resistor, so 1% parts give at worst
1.5%, or 0.13 dB. The margin between a full-scale sine and the deviation ceiling is only about
5.9%, i.e. 0.50 dB, so 1% parts leave 0.37 dB in hand while 5% parts (0.64 dB worst case) can
put the assembly **over the ceiling on their own**. Metal film rather than carbon also keeps the
temperature coefficient near 50 ppm/K, so a hot vehicle does not move what the Bessel null set.

**1% on Rs and Rp is convenience, not necessity.** The receive divider moves about 1.2% worst
case with 1% parts and 6.2% with 5% parts, which is 0.52 dB against 12.26 dB of headroom to
clipping. 5% would work; 1% is specified only so the whole build uses one type of resistor.

**Capacitor tolerance does not matter here; the dielectric does.** All three set poles far
outside the passband, and even 20% parts move them by 20%:

| | pole | against |
|---|---|---|
| C4 (1u) | about 15 Hz | Rt \|\| Rb = 767R plus the tap input |
| C1 (4u7) | about 12 Hz | Rs + Rp = 2k8 |
| C3 (4n7) | about 48 kHz | (600R + Rs) \|\| Rp = 706R |

What does matter is **not using a high-K ceramic for C4 or C1**. A 1u or 4u7 in X7R has a strong
voltage coefficient and would add distortion to the transmit audio; Y5V additionally loses much
of its capacitance with bias and temperature. Film or bipolar electrolytic has neither problem.
C3 is small enough and far enough out of band that ceramic is right for it, C0G by preference.

**Power rating is a formality.** The transmit divider dissipates about 0.2 mW at full scale and
the receive divider about 20 uW at full class deviation, so the smallest part you can handle is
adequate; 0.125 W is simply what is easy to buy.

## Program the radio

From 3DK section 5.1.1 (p.112), changed only where this widget differs (its COS input is active
low; the 3DK example modem wanted active high):

Programmable I/O form, Digital tab:

| Pin | Direction | Action | Active | Debounce |
|---|---|---|---|---|
| AUX_GPI1 | Input | External PTT1 | Low | 0 |
| AUX_GPIO4 | Output | Busy Status | Low | none |

The AUX_GPIO4 row only does anything if a dedicated carrier-detect wire is ever fitted outside
the standard DE-9 cable; it is harmless to program regardless.

PTT / External PTT (1) form, Advanced EPTT1 group: PTT Transmission Type **Data**, PTT State Is
Reflected cleared, PTT Priority Highest, Audio Source **Audio Tap In**.

Networks / Basic Settings: Squelch Detect type **Signal Strength**.

Programmable I/O form, Audio tab:

| Rx / PTT Type | Tap In | Tap In Type | Tap In Unmute | Tap Out | Tap Out Type | Tap Out Unmute |
|---|---|---|---|---|---|---|
| Rx | None | | | R1 | D - Split | **Except on PTT** |
| EPTT1 | T13 | A - Bypass In | On PTT | None | | |

Tap Out Unmute must be Except on PTT: any busy-detect option gates the receive audio and the
modem misses the start of every burst, which looks exactly like an acquisition fault and is not
one.

Put all data channels in one network and voice channels in another, so the settings stay
independent (3DK p.112 step 5).

## Software settings

- PTT in the daemon config: `"ptt": { "type": "cm108", "device": "/dev/hidrawN" }`. GPIO defaults
  to 3, which is what the widget uses. Find the node via `/sys/class/hidraw/*/device/uevent`
  (vendor 0d8c).
- **TXDELAY at least 20 ms.** The radio takes 14.8 +/- 0.5 ms from PTT to full carrier with valid
  modulation via T13 (3DK Table 5.2, p.113).
- Capture: microphone **boost off**, and note the capture gain you calibrated at.
- Playback: **calibrate at maximum playback volume and leave it there.** The divider is sized so
  that the loudest thing the host can produce is exactly 100% deviation; that guarantee only
  holds at the volume it was calibrated at.
- SQL stays unconnected in the DE-9 arrangement, since the convention has no carrier-detect
  pin, and `pdn-soundmodem` has its own DCD so nothing is lost. If a dedicated wire is ever run
  for it, the signal arrives as the CM108's volume-down HID key: readable over hidraw, and
  hazardous on a host that maps consumer keys to a mixer.

## The two bench measurements

**1. CM108 full scale, both directions.** Feed a 1 kHz sine into the IN pad from a generator,
boost off, capture gain at the setting you will run. Raise the level until the samples just clip;
that voltage picks the Rs row. Then play a 0 dBFS sine at maximum playback volume and measure the
OUT pad open circuit with a true-RMS meter; that voltage picks the Rt row. Record the mixer
settings alongside both.

**2. The deviation ceiling, by Bessel null.** Key the radio into a dummy load, play a 1040 Hz
sine, and raise its digital level while watching the carrier on an SDR. The carrier vanishes at
2.5 kHz deviation exactly (modulation index 2.405). The null should land at 0 dBFS: if the null
arrives early, raise Rt one E24 step (each step is about 0.8 dB); if it never arrives, lower Rt
one step. While you are there, confirm deviation keeps following level right up to the null; if
it stops following, the radio is limiting and the tap is not programmed to T13.

### Measured: first assembly, 2026-08-14

Transmit half of measurement 1, on the widget from `tomwardill/cm108radiowidget` (C-Media
0d8c:0012, netlist in [cm108-widget-netlist.md](cm108-widget-netlist.md)), host `radio2`.
Measured on a Hantek DSO2D15 driven over USBTMC, capturing the waveform and fitting a sine
rather than reading the screen.

| | |
|---|---|
| CM108 full scale, OUT pad open circuit | **1.00 Vrms, +/- 3%** (see below) |
| Linearity, 0 to -26 dBFS | +/- 0.06 dB, no compression at full scale |
| Rt to fit | **3k3** with Rb = 1k, or 3k0 if you will trim on the Bessel null |
| Peak deviation at 0 dBFS with 3k3 | about 2360 Hz |

Mixer state it was calibrated at, which the levels only hold for: card `hw:3,0` addressed
directly at 48 kHz S16_LE with no plug layer and no software volume; `Speaker` 37/37 = 0.00 dB;
`Mic` playback (sidetone) muted; `Auto Gain Control` off; `Mic` capture 20/35 = +8.00 dB.

**Why 3k3 rather than the 1.0 Vrms row's 3k0.** Rt = 3k0 stays under the 0.725 Vp-p ceiling
only while full scale is at or below 1.0253 Vrms, and the measurement sits right on that
boundary. Three sound readings gave 0.997, 1.030 and 1.041 Vrms; two of the three are over it.
The spread is instrumental, not the widget: repeated captures hold to 0.01 dB within a session,
but the scope's own vertical ranges disagree by 0.18 dB and an 8-bit scope's gain accuracy is
about 3% anyway. A voltage measurement therefore cannot resolve which side of this boundary the
board is on. 3k3 is under the ceiling for every reading taken and costs about 0.6 dB.

The Bessel null (measurement 2) is the way to settle it, because it measures deviation directly
and does not care about the scope's calibration. Fit 3k0 and check the null if you want the last
0.6 dB; fit 3k3 and skip it otherwise.

The linearity figure is worth more than the absolute one and should be repeated on any new
assembly: being a ratio, it is immune to gain calibration, and it is what confirms nothing
compresses at 0 dBFS, which is the premise that lets full-scale digital stand in for the
deviation ceiling.

#### Receive half

Signal generator on the IN pad with the scope probe on the same node, so the applied voltage
and the resulting digital level are read together and the generator's own calibration never
enters the answer. Both ends measure the 1 kHz component only: a least-squares sine fit on the
scope, a Goertzel on the capture. Broadband RMS is the wrong statistic here, and using it gave
a first reading with a 38 dB crest factor from a single transient.

| | |
|---|---|
| Input full scale at +8.00 dB capture | **455 mVrms** |
| Cross-check, by clipping onset | +10.0 dB of gain above that reference, against +9.7 predicted |
| Capture control accuracy | tracks its claimed dB to 0.32 dB worst case over 25 dB |
| **Capture gain to run** | **+13.00 dB (`Mic` capture step 25 of 35)**, AGC off |
| **Rs at that gain** | **1k8** with Rp = 1k |
| Level at 60% of class deviation | -12.26 dBFS, against the -12 target |
| Clips at | about 6.15 kHz deviation |

Full scale scales with the capture control, so **Rs and the capture gain are one choice, not
two**: 270R at +8 dB, 1k8 at +13, 3k3 at +16, 8k2 at +23 all hit the same -12 dBFS. Set the gain
explicitly at start-up, because moving it silently invalidates Rs.

+13 dB is chosen for robustness rather than noise. Low gain does put a larger signal into the
codec, but the tap already carries the radio's own audio noise about 40 dB below full deviation,
which swamps the CM108's preamp noise by roughly 35 dB at any of these settings, so that
advantage is not real. What is real: at +8 dB the required Rs falls to 270R, the divider barely
divides, and one more step down leaves no positive Rs at all.

Note when reworking the table: the tap's **600R source impedance is in series with Rs**, so

    Rs = 844.3/FS - 1600      (FS in volts)

which reproduces the published rows closely (100 mV -> 6843 against 6k8, 250 mV -> 1777 against
1k8). Omitting the 600R inflates Rs by about 600R and quietly picks the wrong row.

Three cautions earned the hard way, all of which produced confident wrong numbers rather than
obvious errors:

- Levels taken through a crocodile clip on a board edge were low and drifting. The tell was the
  crest factor: Vp-p sat at 2.97 to 3.16 times Vrms instead of 2.828. Check that ratio before
  believing any reading, and solder the joint.
- A tone looped from a WAV file leaves a gap at each restart that pulls an RMS reading low. Play
  one continuous stream.
- One session read 1.4 dB low, stably and with a ladder that sloped, which looked exactly like a
  bad joint but was not: nothing was touched and it returned to normal by itself. It has not
  recurred across a 12 minute watch. Treat a single session's absolute figure as provisional
  until a second session agrees; the ladder's linearity is the check that tells you which
  readings to trust.

## Widget modifications

**None required; the board is right for this job as-is.** In particular do not add a microphone
bias network (its absence is what makes the receive divider clean) and do not add an external PTT
transistor (the BSS138 on board is that transistor; doubling it would invert the logic).

One cheap improvement worth making: **100k from Q1's gate to ground** (tack it across the BSS138
gate and source legs). GPIO3 floats briefly while USB enumerates, and the pull-down removes any
chance of a keying glitch at plug-in.

## Grounding

In a fixed station with one supply and a short USB lead, the direct connection above is fine. In
a vehicle, isolate: see the extended notes, which cover transformer and opto isolation and the
low-frequency cost.
