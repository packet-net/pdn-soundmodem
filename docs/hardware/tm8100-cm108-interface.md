# Wiring the CM108 radio widget to a Tait TM8100

Build instructions for connecting the single-sided CM108 interface board at
[tomwardill/cm108radiowidget](https://github.com/tomwardill/cm108radiowidget) to a TM8100/TM8200
auxiliary connector, for headless packet operation with `pdn-soundmodem`. Values are for 12.5 kHz
channels, where 100% of class deviation is 2.5 kHz.

This note is only what to build. The reasoning, the arithmetic, the bench procedures and the
measured evidence behind every figure are in the
[extended notes](tm8100-cm108-interface-notes.md). The radio side comes from the 3DK Hardware
Developer's Kit Application Manual (`MMA-00011-01` in
[M0LTE/tait-tm8100-tm8200-docs](https://github.com/M0LTE/tait-tm8100-tm8200-docs)), whose section
5.1.1 is Tait's own worked example of exactly this job; the widget side from its
[netlist](cm108-widget-netlist.md).

The component values below were measured for this widget and this radio on 2026-08-14. **They are
not generic.** A different widget, or this one on a different radio, needs measurement 1 redoing:
see the extended notes.

## What the widget provides

No connector: a row of five labelled solder pads. The audio names are from the widget's point of
view, not the radio's:

| Pad | What it is on the board |
|---|---|
| OUT | CM108AH line out, through 1 uF on board: transmit audio |
| IN | CM108AH microphone in, through 1 uF on board: receive audio. No bias network is fitted |
| PTT | open-drain BSS138; pulls to ground while CM108 GPIO3 is high |
| SQL | active-low input; pulling it low presses the CM108's volume-down key through a Schottky |
| GND | board ground |

## The build: pads to a DE-9 socket

Hang a 9-way D-sub socket off the back of the board on a short tail, with the discrete components
in that tail, wired to the standard Kantronics/NinoTNC radio-port convention. Use a female
socket, which is what a NinoTNC presents; the assembly then takes the same radio cables.

```
  widget pads                                                  DE-9 socket, rear

  OUT o-----[ Rt 3k3 ]-----+------------| |--------------------o 1  TXA
                           |           C4 1u
                          [ ] Rb 1k
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

- **Pin 6 (GND): plain wire to the GND pad.** Also the return for every shunt component: Rb, Rp
  and C3 all land on it.
- **Pin 3 (PTT): plain wire to the PTT pad.** No components: the board already has the open-drain
  stage and the radio end has the pull-up.
- **Pin 1 (TXA): three components.** From OUT, Rt in series. From Rt's far end, Rb down to the
  ground wire. From that same junction, C4 to pin 1. **C4 must be the last element before the
  pin**: AUD_TAP_IN is internally biased to 1.5 V behind roughly 100k, and a shunt resistor
  DC-coupled to it drags that bias to millivolts and clips every negative half cycle.
- **Pin 5 (RXA): four components.** From pin 5, C1 first, blocking the tap's +2.3 V offset. Then
  Rs in series. From Rs's far end, Rp and C3 down to the ground wire. That junction wires to IN.
- **SQL and DE-9 pins 2, 4, 7, 8, 9: nothing.**

All seven components fit on the DE-9's solder cups, free-standing with heatshrink over each leg
and the lot, or on a fingernail of stripboard. Keep the tail short.

**Mind the direction.** This is the opposite way round from the bench loop in
[ninotnc-loop.md](../ninotnc-loop.md), where the widget played the radio. Here the widget is the
TNC: OUT drives TXA on pin 1, IN listens on RXA on pin 5. Copying the loop table into this build
swaps transmit and receive.

### DE-9 plug to the radio

Four wires, straight through, to a 15-way standard-density D-sub plug. The space for that plug is
41 mm wide by 18 mm high (`MMA-00028-05` p.22), so check the backshell. Keep the run screened and
short, screen to the radio backshell and pin 15.

| DE-9 pin | Radio pin | Signal |
|---|---|---|
| 1 (TXA) | 7 | AUD_TAP_IN |
| 3 (PTT) | 12 | AUX_GPI1: internal 33k pull-up, external PTT is active low |
| 5 (RXA) | 13 | AUD_TAP_OUT |
| 6 (GND) | 15 | AGND, the only ground on the connector |

## Parts

| Ref | Value | Type | Tolerance | Rating |
|---|---|---|---|---|
| Rt | **3k3** | metal film | **1%** | 0.125 W |
| Rb | **1k** | metal film | **1%** | 0.125 W |
| Rs | **1k8** | metal film | 1% | 0.125 W |
| Rp | **1k** | metal film | 1% | 0.125 W |
| C4 | 1u | film (PET or PP), or bipolar electrolytic; X7R only at 25 V rating or more | 20% | 50 V |
| C1 | 4u7 | non-polarised: bipolar electrolytic or film; X7R only at 25 V rating or more | 20% | >= 16 V |
| C3 | 4n7 | ceramic, C0G preferred, X7R acceptable | 20% | 50 V |
| socket | 9-way D-sub, female | | | |
| plug | 15-way standard-density D-sub, male | | | |
| cable | screened, 2 pairs plus PTT, DE-9 to 15-way | | | |

Two things in that table are not free choices:

- **1% on Rt and Rb is required**, and it is the only place tolerance is tight. 5% parts can put
  the transmit level over the deviation ceiling on their own, at nominal values, with nothing on
  the bench to show for it. 1% on Rs and Rp is only so you stock one type.
- **C1 and C4: film or bipolar electrolytic, or an X7R rated 25 V or more.** Both have corners far
  below the band (1.6 Hz for C4, 12 Hz for C1), so at 300 Hz they drop under a percent of the
  signal (C4) and about 3% (C1), and a ceramic would be harmless on that score; the PCB designs, the
  [internal board](tm8100-internal-usb-board.md) and the
  [packethacking/tait-cm108](https://github.com/packethacking/tait-cm108) reference board it
  follows, fit 25 V and 50 V X7R in the same positions. What rules out the leaded ceramics a
  hand-wired build would reach for is bias: C1 carries the tap's full 2.3 V and C4 1.5 V, a class 2
  ceramic sheds capacitance in proportion to volts over rated volts, and radial ceramics at 1u and
  4u7 are typically Y5V/Z5U and low-voltage, so they lose a large slice of their value at that bias
  and the corner walks up toward the band. Tolerance does not matter for any of the three
  capacitors.

## Program the radio

From 3DK section 5.1.1 (p.112), changed only where this widget differs (its COS input is active
low; the 3DK example modem wanted active high):

Programmable I/O form, Digital tab:

| Pin | Direction | Action | Active | Debounce |
|---|---|---|---|---|
| AUX_GPI1 | Input | External PTT1 | Low | 0 |
| AUX_GPIO4 | Output | Busy Status | Low | none |

The AUX_GPIO4 row only does anything if a dedicated carrier-detect wire is ever fitted; it is
harmless to program regardless.

PTT / External PTT (1) form, Advanced EPTT1 group: PTT Transmission Type **Data**, PTT State Is
Reflected cleared, PTT Priority Highest, Audio Source **Audio Tap In**.

Networks / Basic Settings: Squelch Detect type **Signal Strength**.

Programmable I/O form, Audio tab:

| Rx / PTT Type | Tap In | Tap In Type | Tap In Unmute | Tap Out | Tap Out Type | Tap Out Unmute |
|---|---|---|---|---|---|---|
| Rx | None | | | R1 | D - Split | **Except on PTT** |
| EPTT1 | T13 | A - Bypass In | On PTT | None | | |

Tap Out Unmute must be Except on PTT: any busy-detect option gates the receive audio and the
modem misses the start of every burst, which looks exactly like an acquisition fault and is not.

Put all data channels in one network and voice channels in another (3DK p.112 step 5).

## Software settings

The resistors are only half the calibration. **Set these explicitly at start-up**, because the
divider values were chosen against them and moving either silently invalidates the build.

| Setting | Value |
|---|---|
| Playback (`Speaker`) | **maximum**, 0.00 dB |
| Capture (`Mic`) | **+13.00 dB**, step 25 of 35 |
| `Auto Gain Control` | **off** |
| `Mic` playback (sidetone) | muted |
| Device | address the card directly, 48 kHz S16_LE, no plug layer and no software volume |

Rs and the capture gain are one choice, not two: 270R at +8 dB, 1k8 at +13, 3k3 at +16 and 8k2 at
+23 all hit the same target. Changing the gain means changing Rs.

Also:

- PTT: `"ptt": { "type": "cm108", "device": "/dev/hidrawN" }`. GPIO defaults to 3, which is what
  the widget uses. Find the node via `/sys/class/hidraw/*/device/uevent` (vendor 0d8c).
- **TXDELAY at least 20 ms.** The radio takes 14.8 +/- 0.5 ms from PTT to full carrier with valid
  modulation via T13 (3DK Table 5.2, p.113).
- SQL stays unconnected: the convention has no carrier-detect pin and `pdn-soundmodem` has its own
  DCD, so nothing is lost.

## Before you trust it on air

**Check the deviation ceiling by Bessel null.** Key into a dummy load, play a 1040 Hz sine, raise
its digital level while watching the carrier on an SDR. The carrier vanishes at 2.5 kHz deviation
exactly. The null should land at or just above 0 dBFS: if it arrives early, raise Rt one E24 step;
if it never arrives, lower Rt one step.

While you are there, confirm deviation keeps following level right up to the null. If it stops
following, the radio is limiting and the tap is not programmed to T13.

This is the check that settles Rt. Fitted at 3k3 the assembly is under the ceiling on every
measurement taken, giving up about 0.6 dB; 3k0 recovers that if the null confirms it.

Mechanical interchangeability is not calibration: the ceiling belongs to the pairing of this
assembly with this radio. If a different device ever hangs on the DE-9, or this assembly moves to
another radio, redo the null before trusting the levels.

## Widget modifications

**None required; the board is right for this job as-is.** In particular do not add a microphone
bias network (its absence is what makes the receive divider clean) and do not add an external PTT
transistor (the BSS138 on board is that transistor; doubling it would invert the logic).

**Do fit 100k from Q1's gate to ground**, tacked across the BSS138 gate and source legs. Any type,
5%, anything from 47k to 220k. The gate net holds only the gate and the CM108's GPIO3, so until
the driver configures that pin the gate floats, and a floating BSS138 gate can sit above
threshold. The failure mode is the transmitter keying itself when the widget is plugged in.

## Grounding

In a fixed station with one supply and a short USB lead, the direct connection above is fine. In a
vehicle, isolate: see the extended notes, which cover transformer and opto isolation and the
low-frequency cost.
