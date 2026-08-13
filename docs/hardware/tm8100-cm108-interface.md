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

Five single-pin sockets. The audio names are from the widget's point of view, not the radio's:

| Socket | What it is on the board |
|---|---|
| AUDIO_OUT | CM108AH line out: transmit audio to the radio, through 1 uF on board |
| AUDIO_IN | CM108AH microphone in: receive audio from the radio, through 1 uF on board; no bias network is fitted |
| PTT | open-drain BSS138; pulls to ground while CM108 GPIO3 is high |
| SQUELCH | active-low input; pulling it low presses the CM108's volume-down key through a Schottky |
| GND | board ground |

## The five wires

Radio side is a 15-way standard-density D-sub plug into the auxiliary connector. The space for the
plug is limited to 41 mm wide by 18 mm high (`MMA-00028-05` p.22), so check the backshell. All the
passive parts below fit inside the backshell; keep the cable screened and short (a metre or two).

| Widget | Via | Radio pin | Signal |
|---|---|---|---|
| GND | wire | 15 | AGND, the only ground on the connector |
| PTT | wire, nothing else | 12 | AUX_GPI1: internal 33k pull-up, external PTT is active low |
| AUDIO_OUT | Rt, Rb, C4 below | 7 | AUD_TAP_IN |
| AUDIO_IN | C1, Rs, Rp, C3 below | 13 | AUD_TAP_OUT |
| SQUELCH | wire (optional, see below) | 10 | AUX_GPIO4, programmed Busy Status, active low |

PTT needs no components because the board already has the open-collector stage and the radio
already has the pull-up: Tait's own external-PTT example is a bare footswitch from pin 12 to
pin 15.

### Transmit: AUDIO_OUT to pin 7

```
  widget                    Rt                        C4         radio pin 7
  AUDIO_OUT                3k0           +---------o | | o------o AUD_TAP_IN
      o-------------------|____|---------+          1u film       (+1.5 V bias, 100k)
                                        [ ] Rb
                                         |  1k
  widget GND o---------------------------+----------------------o pin 15 AGND
```

**C4 sits between the divider and the radio, not on the widget side.** AUD_TAP_IN is internally
biased to 1.5 V behind roughly 100k, and its valid input range is 0.5 to 2.5 V; a shunt resistor
DC-coupled to the pin drags that bias to millivolts and the ADC clips on every negative half
cycle. The widget's own 1 uF blocks DC on the other side.

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
  then exactly legal and a typical one gives up 0.9 dB.
- **C4 = 1u film or bipolar** (Tait's own suggestion of 10 uF also works). It sits across the
  tap's 1.5 V bias, so nothing leaky.
- The divider's 750R source impedance puts the pole against the connector's internal 10 nF at
  21 kHz, well out of band.

### Receive: pin 13 to AUDIO_IN

```
  radio pin 13             C1             Rs                     widget
  AUD_TAP_OUT          +-o | | o-------|______|-------+---------o AUDIO_IN
      o----------------+  4u7 NP       (table)        |
  (600R source,                                      [ ] Rp    === C3
   +2.3 V offset)                                     |  1k     |  4n7
  pin 15 AGND o---------------------------------------+---------+--o widget GND
```

- **Rp = 1k.** The widget has no microphone bias network (the CM108AH's VBIAS pin is
  unconnected), so Rp is the whole shunt; do not copy values from notes written for stock dongles
  with a 2k2 bias resistor.
- **Rs from the table**, against measurement 1 below. Build with **6k8** if you have not measured
  yet.
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

| Ref | Value | Notes |
|---|---|---|
| plug | 15-way standard-density D-sub, male | backshell within 41 x 18 mm |
| Rt | 3k0, 1% (or from the transmit table) | transmit series; trim from the Bessel null |
| Rb | 1k, 1% | transmit shunt |
| C4 | 1u film or bipolar | radio side of the transmit divider |
| C1 | 4u7 non-polarised, >= 16 V | receive DC block |
| Rs | 6k8, 1% (or from the table) | receive series |
| Rp | 1k, 1% | receive shunt |
| C3 | 4n7 | RF bypass at the receive shunt |
| cable | screened, 2 pairs plus PTT | screen to the backshell and pin 15 |

## Program the radio

From 3DK section 5.1.1 (p.112), changed only where this widget differs (its COS input is active
low; the 3DK example modem wanted active high):

Programmable I/O form, Digital tab:

| Pin | Direction | Action | Active | Debounce |
|---|---|---|---|---|
| AUX_GPI1 | Input | External PTT1 | Low | 0 |
| AUX_GPIO4 | Output | Busy Status | Low | none |

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
- SQUELCH is optional: `pdn-soundmodem` has its own DCD, and the wire arrives as the CM108's
  volume-down HID key, so only connect it if something reads it (hidraw) and nothing on the host
  maps consumer keys to a mixer.

## The two bench measurements

**1. CM108 full scale, both directions.** Feed a 1 kHz sine into AUDIO_IN from a generator, boost
off, capture gain at the setting you will run. Raise the level until the samples just clip; that
voltage picks the Rs row. Then play a 0 dBFS sine at maximum playback volume and measure AUDIO_OUT
open circuit with a true-RMS meter; that voltage picks the Rt row. Record the mixer settings
alongside both.

**2. The deviation ceiling, by Bessel null.** Key the radio into a dummy load, play a 1040 Hz
sine, and raise its digital level while watching the carrier on an SDR. The carrier vanishes at
2.5 kHz deviation exactly (modulation index 2.405). The null should land at 0 dBFS: if the null
arrives early, raise Rt one E24 step (each step is about 0.8 dB); if it never arrives, lower Rt
one step. While you are there, confirm deviation keeps following level right up to the null; if
it stops following, the radio is limiting and the tap is not programmed to T13.

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
