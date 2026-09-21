# Wiring a CM108 interface to a Tait TM8100

What to build to connect the single-sided CM108 interface board at [tomwardill/cm108radiowidget](https://github.com/tomwardill/cm108radiowidget) to a TM8100/TM8200 auxiliary connector, for headless packet operation with pdn-soundmodem. When you finish you will have a lead from the board to the radio's auxiliary socket, the radio programmed for it, and the card's levels set.

**There are two variants, and you have to choose before you cut a resistor.** The difference is two components and one codeplug field, and it decides what channel spacings the assembly is correct on.

| | **Variant A: any spacing** | **Variant B: 12.5 kHz only** |
|---|---|---|
| Tap in | **T12** | T13 |
| Rt | **2k2** | 3k3 |
| Full-scale deviation | 98 % of class on 12.5 **and** 25 kHz | 2.44 kHz, i.e. 98 % of class on 12.5 kHz and **49 % on 25 kHz** |
| Choose it when | almost always | the radio is narrowband-only and you are matching an existing build |

**Variant A is the one to build.** T12's signal level is scaled by the radio to match the channel spacing and T13's is not, so a T12 assembly is correct on both and a T13 assembly is correct on exactly one. That is Tait's own advice: 3DK `MMA-00011-01` p.112 says to use T12 and R2 "if not all the channels that the modem will be communicating on have the same channel spacing or bandwidth". Variant B is documented because it is what the original version of this page told people to build, and because two of them exist.

The levels below were measured for this board and this radio on 2026-08-14, and the deviation confirmed on air on 2026-09-18. They are not generic: a different board, or this one on a different radio, needs [measurement 1](../dev/hardware/tm8100-cm108-interface-notes.md#measurement-1-what-the-cm108-input-and-output-full-scale-actually-are) redoing.

This page is only what to build. The reasoning, the arithmetic, the bench procedures and the measured evidence behind every figure are in the [extended notes](../dev/hardware/tm8100-cm108-interface-notes.md). The radio side comes from the 3DK Hardware Developer's Kit Application Manual (`MMA-00011-01` in [M0LTE/tait-tm8100-tm8200-docs](https://github.com/M0LTE/tait-tm8100-tm8200-docs)), whose section 5.1.1 is Tait's own worked example of this job. The interface side comes from its [netlist](../dev/hardware/cm108-widget-netlist.md).

## What the interface provides

There is no connector, only a row of five labelled solder pads. The audio names are from the board's point of view, not the radio's.

| Pad | What it is on the board |
|---|---|
| OUT | CM108AH line out, through 1 uF on board: transmit audio |
| IN | CM108AH microphone in, through 1 uF on board: receive audio. No bias network is fitted |
| PTT | open-drain BSS138; pulls to ground while CM108 GPIO3 is high |
| SQL | active-low input; pulling it low presses the CM108's volume-down key through a Schottky |
| GND | board ground |

## The build: pads to a DE-9 socket

Hang a 9-way D-sub socket off the back of the board on a short tail, with the discrete components in that tail, wired to the standard Kantronics/NinoTNC radio-port convention. Use a female socket, which is what a NinoTNC presents. The assembly then takes the same radio cables.

```
  board pads                                                   DE-9 socket, rear

  OUT o-----[   Rt    ]-----+------------| |--------------------o 1  TXA
                           |           C4 1u
                          [ ] Rb
                           |
  GND o--------------------+--------+-----------+--------------o 6  GND
                                    |           |
                                   [ ] Rp 1k   === C3 4n7
                                    |           |
  IN  o-----------------------------+-----------+--[ Rs 1k8 ]--| |--o 5  RXA
                                                              C1 4u7
  PTT o--------------------------------------------------------o 3  PTT

  SQL o--x  leave unconnected              pins 2, 4, 7, 8, 9: empty
```

Pin by pin, from the iron's point of view:

- Pin 6 (GND) takes a plain wire to the GND pad. It is also the return for every shunt component: Rb, Rp and C3 all land on it.
- Pin 3 (PTT) takes a plain wire to the PTT pad, with no components. The board already has the open-drain stage and the radio end has the pull-up.
- Pin 1 (TXA) takes three components. From OUT, Rt in series. From Rt's far end, Rb down to the ground wire. From that same junction, C4 to pin 1. C4 must be the last element before the pin. AUD_TAP_IN is internally biased to 1.5 V behind roughly 100k, and a shunt resistor DC-coupled to it drags that bias to millivolts and clips every negative half cycle.
- Pin 5 (RXA) takes four components. From pin 5, C1 first, blocking the tap's +2.3 V offset. Then Rs in series. From Rs's far end, Rp and C3 down to the ground wire. That junction wires to IN.
- SQL and DE-9 pins 2, 4, 7, 8, 9 get nothing.

All seven components fit on the DE-9's solder cups, free-standing with heatshrink over each leg and the lot, or on a fingernail of stripboard. Keep the tail short.

Mind the direction. This is the opposite way round from the bench loop in [ninotnc-loop.md](../dev/bench/ninotnc-loop.md), where the board played the radio. Here the board is the TNC: OUT drives TXA on pin 1, IN listens on RXA on pin 5. Copying the loop table into this build swaps transmit and receive.

### DE-9 plug to the radio

Four wires, straight through, to a 15-way standard-density D-sub plug. The space for that plug is 41 mm wide by 18 mm high (`MMA-00028-05` p.22), so check the backshell. Keep the run screened and short, screen to the radio backshell and pin 15.

| DE-9 pin | Radio pin | Signal |
|---|---|---|
| 1 (TXA) | 7 | AUD_TAP_IN |
| 3 (PTT) | 12 | AUX_GPI1: internal 33k pull-up, external PTT is active low |
| 5 (RXA) | 13 | AUD_TAP_OUT |
| 6 (GND) | 15 | AGND, the only ground on the connector |

## Parts

| Ref | Variant A (T12) | Variant B (T13) | Type | Tolerance | Rating |
|---|---|---|---|---|---|
| Rt | **2k2** | **3k3** | metal film | **1%** | 0.125 W |
| Rb | **1k5** | **1k** | metal film | **1%** | 0.125 W |
| Rs | **1k8** | **1k8** | metal film | 1% | 0.125 W |
| Rp | **1k** | **1k** | metal film | 1% | 0.125 W |
| C4 | 1u | 1u | film (PET or PP), or bipolar electrolytic; X7R only at 25 V rating or more | 20% | 50 V |
| C1 | 4u7 | 4u7 | non-polarised: bipolar electrolytic or film; X7R only at 25 V rating or more | 20% | >= 16 V |
| C3 | 4n7 | 4n7 | ceramic, C0G preferred, X7R acceptable | 20% | 50 V |
| socket | 9-way D-sub, female | | | | |
| plug | 15-way standard-density D-sub, male | | | | |
| cable | screened, 2 pairs plus PTT, DE-9 to 15-way | | | | |

Rt and Rb have to be 1%, and that is the only place tolerance is tight. 5% parts can put the transmit level over the deviation ceiling on their own, at nominal values, with nothing on the bench to show for it. 1% on Rs and Rp is only so you stock one type.

C1 and C4 have to be film or bipolar electrolytic, or an X7R rated 25 V or more. C1 carries the tap's full 2.3 V and C4 1.5 V, and the low-voltage leaded ceramics a hand-wired build would reach for lose much of their value under that bias. Tolerance does not matter for any of the three capacitors. The corner frequencies, and what the [internal board](../dev/hardware/tm8100-internal-usb-board.md) and the [packethacking/tait-cm108](https://github.com/packethacking/tait-cm108) board it follows fit in those positions, are in the [extended notes](../dev/hardware/tm8100-cm108-interface-notes.md#parts).

## Program the radio

From 3DK section 5.1.1 (p.112), changed only where this board differs: its COS input is active low, and the 3DK example modem wanted active high.

Programmable I/O form, Digital tab:

| Pin | Direction | Action | Active | Debounce |
|---|---|---|---|---|
| AUX_GPI1 | Input | External PTT1 | Low | 0 |
| AUX_GPIO4 | Output | Busy Status | Low | none |

The AUX_GPIO4 row only does anything if a dedicated carrier-detect wire is ever fitted. It is harmless to program regardless.

PTT / External PTT (1) form, Advanced EPTT1 group: PTT Transmission Type **Data**, PTT State Is Reflected cleared, PTT Priority Highest, Audio Source **Audio Tap In**. On the Networks / Basic Settings form, set Squelch Detect type to **Signal Strength**.

Programmable I/O form, Audio tab:

| Rx / PTT Type | Tap In | Tap In Type | Tap In Unmute | Tap Out | Tap Out Type | Tap Out Unmute |
|---|---|---|---|---|---|---|
| Rx | None | | | R1 | D - Split | **Except on PTT** |
| EPTT1 | **T12** (variant A) or T13 (variant B) | A - Bypass In | On PTT | None | | |

**Tap In is the field that picks the variant**, and it has to match the resistors. T12 and T13 are
otherwise the same tap: same 1.8 ms group delay, same 14.8 ms from PTT to valid modulation, both
past the limiter, neither pre-emphasised. The only difference is that the radio scales T12's level
with the channel spacing and leaves T13's alone. T12 wants 0.23 Vp-p per kHz of deviation on a
25 kHz channel and 0.46 on a 12.5 kHz one; T13 wants 0.29 either way (3DK Table 2.7, p.22).

Tap Out Unmute must be Except on PTT. Any busy-detect option gates the receive audio, and the modem then misses the start of every burst, which looks like an acquisition fault.

Put all data channels in one network and voice channels in another (3DK p.112 step 5).

## Software settings

The resistors are only half the calibration. Set these explicitly at start-up, because the divider values were chosen against them and moving either invalidates the build.

| Setting | Value |
|---|---|
| Playback (`Speaker`) | **maximum**, 0.00 dB |
| Capture (`Mic`) | **+13.00 dB** on a 12.5 kHz channel, **+7.00 dB** on 25 kHz - see below |
| `Auto Gain Control` | **off** |
| `Mic` playback (sidetone) | muted |
| Device | `plughw:CARD=Device,DEV=0`, not `default` |

Set the two gains in the config file, under [`alsa`](../reference/config.md#alsa). The journal then says what the card did with them.

```json
{ "alsa": { "mixer": { "captureGainDb": 13, "playbackDb": 0 } } }
```

```
alsa: mixer: Mic capture 13.00 dB of -12.00 to 23.00 dB (set 13.00 dB, config), Auto Gain Control off (forced), Speaker playback 0.00 dB of -36.00 to 0.00 dB (set 0.00 dB, config)
```

Confirm the capture reads 13.00 dB, which is step 25 of 35. pdn-soundmodem switches AGC and mic boost off at every start-up, so that row is already done for you. It never touches the sidetone, so mute the playback `Mic` control once in `alsamixer` (F6 picks the card, M mutes) and keep it with `sudo alsactl store`. [04-levels.md](../04-levels.md) covers levels from the station page, and [03-radios-and-interfaces.md](../03-radios-and-interfaces.md) the `device` string.

Rs and the capture gain are one choice, not two: 270R at +8 dB, 1k8 at +13, 3k3 at +16 and 8k2 at +23 all hit the same target. Changing the gain means changing Rs.

**Those figures are for a 12.5 kHz channel and every one of them is 6 dB hot on 25 kHz.** R1's volts per kilohertz does not follow the channel spacing any more than T13's does, so where the target is 60 % of class, 60 % of 25 kHz class is 3.0 kHz rather than 1.5 kHz and produces twice the audio. On a 25 kHz channel take 6 dB off the row you would otherwise use: 1k8 at **+7 dB**, not +13.

**Check it against band noise before trusting either figure.** With the squelch open and no signal, the receiver's own noise is the loudest thing the card ever sees, because FM quiets on a carrier. Measured on a TM8110 on a 25 kHz channel with this assembly, that noise peaks at -6.3 dBFS with `Mic` at 0.00 dB, so +7 dB would clip it continuously between bursts and +13 dB would clip it by nearly 7 dB. Record a few seconds with `arecord` and look at the peak before settling on a number; if band noise clips, the modem is being fed a clipped signal the whole time it is listening, which is exactly what an OFDM waveform least tolerates.

**A warning about this particular card.** The CM108's `Mic` control advertises a -12.00 to +23.00 dB range and **nothing below 0.00 dB does anything at all** - -12, -6 and 0 give identical levels, measured. So 0 dB is the floor, there is no attenuation available in software below it, and a `captureGainDb` of -12 (which is what the example in [config.md](../reference/config.md#alsa) shows) silently becomes 0. If band noise still clips at 0 dB, the fix is a larger Rs, not a mixer setting.

- PTT: `"ptt": { "type": "cm108", "device": "/dev/hidrawN" }`, described under [`ptt`](../reference/config.md#ptt). GPIO defaults to 3, which is what the board uses. Find the node the way [03-radios-and-interfaces.md](../03-radios-and-interfaces.md#the-gpio-pin-on-a-cm108-interface) shows, and add the udev rule from [02-first-station.md](../02-first-station.md#choose-the-ptt-line), or the service cannot open it.
- TXDELAY at least 20 ms. The radio takes 14.8 +/- 0.5 ms from PTT to full carrier with valid modulation via T13 (3DK Table 5.2, p.113). Your node or APRS software sets TXDELAY over KISS ([commands](../reference/ports-and-endpoints.md#commands)); with nothing attached, `--txdelay` sets it for a bench run.
- SQL stays unconnected. The convention has no carrier-detect pin and pdn-soundmodem has its own DCD, so nothing is lost.

## Before you trust it on air

Check the deviation ceiling. Key into a dummy load and measure, on the channel spacing the radio will actually run on. The ceiling belongs to this assembly on this radio at that spacing, so redo it if a different device hangs on the DE-9, if the assembly moves to another radio, or if the channel spacing changes.

**The tone to use depends on what deviation you are aiming at**, because a Bessel null lands at 2.405 times the tone frequency and nowhere else:

| Channel | 100 % of class | Bessel-null tone | Station-page preset? |
|---|---|---|---|
| 12.5 kHz | 2.5 kHz | **1040 Hz** | no, type it in |
| 25 kHz | 5.0 kHz | **2079 Hz** | yes |

Two ways to do it, and the second is better if you have the hardware.

**By Bessel null**, which needs nothing calibrated: play the tone and raise its digital level while watching the carrier on an SDR. The carrier disappears at exactly 2.405 times the tone and nowhere else, so the null is an absolute deviation reference that no gain anywhere in the chain can move. The null should land at or slightly above 0 dBFS. If it arrives early, raise Rt one E24 step; if it never arrives, lower Rt one step.

**By direct measurement**, which is quicker and gives a number rather than a yes or no: capture I/Q of the keyed tone and run [`sm-ota fm-deviation`](../dev/mode-modulation-reference.md). Three things decide whether that number is worth anything, all of them checkable in the capture:

- **The capture must not clip.** Clipping corrupts the phase, which is the thing being measured. Deviation is otherwise completely indifferent to receive gain, AGC included, because it is a frequency measurement and not an amplitude one.
- **Analyse a narrow band.** `sm-ota` takes the instantaneous frequency sample by sample, so every hertz of analysis bandwidth you keep beyond the signal adds noise to the answer. Filter to a little more than the Carson bandwidth, about 4 kHz either side for these deviations. Measured on a real capture, a 25 kHz window read the rms 4 % high at full drive and 61 % high at 16 dB down, which reads convincingly as compression and is not.
- **Use the rms, not the peak.** For a sine, peak deviation is the rms times the square root of two. `sm-ota`'s own peak figure is a sample-by-sample maximum and is badly noise-inflated: on the same capture it printed 8.476 kHz where the true peak was 1.80 kHz. The rms it prints alongside is sound.

While you are there, **confirm deviation keeps following level**, right up to the null or across a 16 dB ladder. If it stops following, the radio is limiting and the tap is not programmed to T12 or T13. Measured on a correct build, the ladder is linear to within 0.21 dB over 16 dB.

**What it should read.** With the CM108 at 1.00 Vrms full scale, which is what was measured on this board type:

| Variant | Tap | Rt/Rb | 0 dBFS on 12.5 kHz | 0 dBFS on 25 kHz |
|---|---|---|---|---|
| A | T12 | 2k2/1k5 | 2.47 kHz (99 %) | 4.94 kHz (99 %) |
| B | T13 | 3k3/1k | 2.25 kHz (90 %) | 2.25 kHz (**45 %**) |

Confirmed on air 2026-09-18: a variant B assembly on a TM8110 on a 25 kHz channel measured **1.800 kHz peak** at the 0.8 amplitude the software actually sends, against a forward prediction of 1.80 kHz. The arithmetic on this page is good to better than 0.1 dB; what it was not, until now, was applied to the right channel spacing.

**The software does not send full scale.** `txTest.amplitude` defaults to 0.8 and the modulators use the same figure, so a sine goes out 1.94 dB under whatever ceiling the resistors set. That is deliberate headroom and not a fault, but it means the numbers above are the ceiling rather than the operating point, and an assembly sized so that 0 dBFS is exactly 100 % of class actually runs at about 80 %.

### Retrofitting an assembly already built to variant B

Rb cannot be raised by paralleling, so the exact variant A pair needs Rb replaced. If that is awkward - these components live on the DE-9 solder cups under heatshrink - the divider ratio is what matters and it can be reached by paralleling Rt alone and leaving Rb at 1k:

| Across Rt (3k3) | Rt effective | 0 dBFS on T12, either spacing | Thevenin | C8 corner |
|---|---|---|---|---|
| 3k3 | 1650 | 92 % of class | 623 ohm | 60 Hz |
| 3k0 | 1571 | 95 % of class | 611 ohm | 62 Hz |
| **2k7** | **1485** | **98 % of class** | **598 ohm** | **64 Hz** |
| 2k4 | 1389 | 102 % of class, over | 581 ohm | 67 Hz |

**2k7 across Rt, plus the codeplug tap-in changed to T12**, is the one-resistor retrofit, and 3k0 is the same job with more margin. It costs a slightly higher coupling corner than a proper variant A build, 64 Hz against 43 Hz, which is 0.19 dB at 300 Hz and nothing above it. Do not fit 2k4.

Measure the null afterwards rather than trusting the table. The T12 sensitivity figure it rests on is from Tait's specification and has not been confirmed on a bench here, and `tait-codeplug`'s tap-in encoding for node 12 follows the documented scheme but has not been byte-checked against a CPS save.

## Board modifications

None are required; the board is right for this job as it stands. Do not add a microphone bias network, whose absence is what keeps the receive divider clean. Do not add an external PTT transistor: the BSS138 on board is that transistor, and doubling it would invert the logic.

Do fit 100k from Q1's gate to ground, tacked across the BSS138 gate and source legs. Any type, 5%, anything from 47k to 220k. The gate net holds only the gate and the CM108's GPIO3, so until the driver configures that pin the gate floats, and a floating BSS138 gate can sit above threshold. The failure mode is the transmitter keying itself when the board is plugged in.

## Grounding

In a fixed station with one supply and a short USB lead, the direct connection above is fine. In a vehicle, isolate: see the extended notes, which cover transformer and opto isolation and the low-frequency cost.

## Related

- [03-radios-and-interfaces.md](../03-radios-and-interfaces.md) for the `device` string and the PTT block this assembly needs.
- [04-levels.md](../04-levels.md) for setting receive and transmit levels once it is built.
- [Extended notes](../dev/hardware/tm8100-cm108-interface-notes.md) for the arithmetic, the bench procedures and the evidence, and the [netlist](../dev/hardware/cm108-widget-netlist.md) for what the board does.
