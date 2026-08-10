# An internal USB accessory board for the Tait TM8100

Design notes for a board fitting inside a TM8100 radio body, presenting **one USB cable to the host**
that enumerates as a serial device and an audio device. The serial side reaches the radio's CCDI
port; the audio side reaches its audio tap points. Intended for headless packet operation with
`pdn-soundmodem`.

## Sources, and one correction to an earlier note

Nearly every figure here comes from the **TM8100/TM8200 3DK Hardware Developer's Kit Application
Manual** (Tait Electronics, March 2006, 156 pages), which is the document Tait wrote for exactly this
purpose. Citations below are page numbers in it unless another document is named.

**That manual supersedes two claims in `tm8100-cm108-interface.md`,** which was written from the
20-manual service set:

- It said no input-level-to-deviation figure is published anywhere. **It is published**, per tap
  point, in the 3DK manual's Table 2.7. The Bessel-null calibration is still worth doing, but as a
  cross-check rather than the only route to a number.
- It said T13 appears nowhere in the documentation. **T13 is a fully documented tap-in point** (p.93),
  with its own level, group delay and frequency response specifications. The service manual's CCTM
  tap list is a test command's subset, not the feature set.

Marking as before: **DATASHEET** stated and cited, **DERIVED** computed with the arithmetic shown,
**ABSENT** not in the documents.

## Architecture

```
                  radio body lid
   +-----------------------------------------------------+
   |  [USB socket] -- through the external options hole   |
   |        |                                            |
   |     common-mode choke, VBUS pi, shield to casting    |
   |        |                                            |
   |     +--+--------------+  USB hub (bus powered)       |
   |     |                 |                             |
   |  USB-serial       USB audio codec (CM108 class)      |
   |  bridge            |         |          |            |
   |   TXD RXD       line in   line out   GPIO (PTT)      |
   +-----|---|----------|---------|----------|------------+
         |   |          |         |          |
        18  17          2         6      one of 9..15      18-pin Micro-MaTch
      IOP_  IOP_   AUD_TAP_   AUD_TAP_    IOP_GPIOn
      TXD   RXD      OUT        IN
```

Host sees: one USB device tree, a `/dev/ttyUSB*`-class serial port, and an ALSA card. No drivers
beyond USB CDC and USB Audio Class 1.0.

## The radio interface

**18-pin 0.1 inch pitch Micro-MaTch** (p.27). On the main board it is **SK102**, which the silkscreen
may label PL103 (MMA-00005-05 p.410). Tait's part is **240-10000-11, "Conn SMD 18w Skt M/Match"**
(MMAB12-B1-00-814 p.4), and every Tait options board in the set fits **the same part** rather than a
plug: SK1 on the TMAA01-02 RS-232 board, SK2 on the TMAA01-01 line interface, SK1 on the TMAA01-05
extender. The two sockets are bridged by a loom, Tait IPN **219-00329-00 "Loom TMA Int Opt"**,
supplied in kit 600-00010-00.

**So the board takes a socket, not a header, and a ribbon loom joins the two.** That is worth knowing
before laying out a footprint, and it is why the connector is specified as 18-way in two rows of nine
with the pinout drawings labelled "top view". The
[Tait_TM8XXX_TNC_Adapter](https://github.com/marrold/Tait_TM8XXX_TNC_Adapter) project has a working
SMD footprint and a photograph of a loom fitted in a real radio.

The specifications manual summarises what the connector carries as "1 serial, 7 I/O, 1 audio tap in,
1 audio tap out" (MMA-00072-03 p.26), against the auxiliary connector's "1 serial, 3 input, 4 I/O,
1 audio tap in, 1 audio tap out" - so the internal connector trades the auxiliary's dedicated inputs
for three more bidirectional lines.

| Pin | Signal | Description | Type |
|---|---|---|---|
| 1 | 13V8_SW | Switched 13.8 V, off with the radio body | Power |
| 2 | AUD_TAP_OUT | Programmable tap out of the Rx or Tx chain, **DC-coupled** | Analog |
| 3 | AGND | Analogue ground | Ground |
| 4 | AUX_MIC_AUD | Auxiliary microphone input, electret bias provided | Analog |
| 5 | RX_BEEP_IN | Receive sidetone input, AC-coupled | Analog |
| 6 | AUD_TAP_IN | Programmable tap into the Rx or Tx chain, **DC-coupled** | Analog |
| 7 | RX_AUD | Receive audio, post volume control, AC-coupled | Analog |
| 8 | RSSI | Analogue RSSI output | Analog |
| 9 to 15 | IOP_GPIO1..7 | Programmable function and direction; pin 9 is GPIO1 through pin 15 GPIO7. With LK4 fitted GPIO7 becomes a power sense input | 3V3 CMOS |
| 16 | DGND | Digital ground | Ground |
| 17 | IOP_RXD | Serial receive data | 3V3 CMOS |
| 18 | IOP_TXD | Serial transmit data | 3V3 CMOS |

All **DATASHEET**, p.27.

**One sharing constraint that matters** (p.27): the digital signals and the serial port are
independent of the auxiliary connector, but **AUD_TAP_IN, AUD_TAP_OUT, AUX_MIC_AUD and RSSI are
shared with it**. So this board's audio cannot coexist with anything using the auxiliary connector's
audio, while its serial port can coexist with a second serial user on the auxiliary side.

RSSI is on pin 8 but is not used here: it is read digitally over CCDI instead, which is more accurate
and costs no analogue input.

Two pins have no counterpart on the auxiliary connector and are worth knowing about even though this
design leaves both unconnected. **RX_BEEP_IN** (pin 5) injects sidetone. **RX_AUD** (pin 7) is
receive audio taken after the volume control, which is exactly why it is the wrong tap for a modem;
all three Tait options boards leave it unconnected too.

## Receive path: AUD_TAP_OUT at tap R1

All **DATASHEET**, p.21-22 and p.91.

| Property | Value |
|---|---|
| Output impedance | 590 / **600** / 650 ohm, DC to 10 kHz, constant across frequency |
| DC offset | 2.1 / **2.3** / 2.5 V, no load, zero Rx frequency error |
| Level at tap R1 | 0.54 / **0.60** / 0.66 Vp-p for **3 kHz deviation** at 1 kHz, into 600 ohm |
| Full scale output | **2.0 Vp-p** into 600 ohm |
| Safe limits | -0.5 to +17 V, short-circuit safe, input current under +/-20 mA |
| Group delay at R1 | **1.8 ms** |
| Signal chain | DAC at 48 kSa/s, 12 kHz low pass, buffer amplifier |

**DERIVED.** The quoted levels are into a matched 600 ohm load, so a light load sees twice the
voltage: **1.2 Vp-p for 3 kHz deviation, 4.0 Vp-p at full scale, open circuit.** At the 1.5 kHz
deviation this modem is expected to run (60 % of narrowband's 2.5 kHz, which is what Tait's own
1200 baud modem uses) that is **0.6 Vp-p, or 212 mVrms** open circuit.

**Two things about R1 specifically.**

Its DC offset moves with receive carrier frequency error (p.91) - true of R1, R2 and R4 only, and
unsurprising, since raw discriminator output IS frequency. **AC-couple, and do not treat the DC as a
constant.** A 2 kHz frequency error would shift it by 2/3 of a volt.

Its frequency response is plotted per bandwidth class (Table 2.10) and it is the widest tap
available, being ahead of the deviation normaliser, the 3 kHz low pass, the 300 Hz high pass and
de-emphasis.

### Design

```
  pin 2                    Rs                        codec
  AUD_TAP_OUT     C1     (below)                    line in
  600R, +2.3V DC   ||     ____                          |
      o-----------||-----|____|-----+------------------o
                  1u                |
                                   [ ] Rp
                                    |
                                   === C2 1n   (RF, to the screen ground)
                                    |
  pin 3 AGND o----------------------+------------------o codec ground
```

**Use a line input, not a microphone input.** More headroom, flatter, quieter, no bias resistor
fighting the divider, and no software boost waiting to be left on. This is the single biggest
advantage of a custom board over a dongle.

**Size Rs/Rp so the radio's full scale lands on the codec's full scale**, so both clip in the same
place and no headroom is wasted. **MEASURE** the codec's actual full-scale input first; for a
1 Vrms (2.83 Vp-p) line input the required attenuation is 4.0 to 2.83 Vp-p, **-3.0 dB DERIVED**,
which is Rs = 10k with Rp = 22k. The load on a 600 ohm source is then 32k, so insertion loss is
negligible and the tap is barely loaded.

C1 at 1 uF into 32k is a **5 Hz** corner **DERIVED**, comfortably below anything the modem uses and
below the tap's own behaviour. Do not economise here: a 100 nF part would put the corner at 50 Hz,
which is fine for a 300 Hz to 3 kHz mode and not fine for a wideband one off R1.

## Transmit path: AUD_TAP_IN at tap T13

All **DATASHEET**, p.21-22 and p.91-93.

| Property | Value |
|---|---|
| Input impedance | 50 / **100** / 150 kohm, DC to 10 kHz |
| Bias voltage | 1.4 / **1.5** / 1.6 V, internally generated |
| Valid input range | **0.5 to 2.5 V**, regardless of bias error |
| Level at tap T13 | 0.78 / **0.87** / 0.96 Vp-p for **3 kHz deviation** at 1 kHz |
| Full scale input | **2.0 Vp-p** |
| Input filtering | 3.7 Hz switched-capacitor high pass, then 22 kHz low pass |
| Safe limits | -17 to +17 V |
| Group delay at T13 | **1.8 ms** |

Tait are explicit (p.92) that the bias must be preserved: *"to avoid asymmetrical clipping and
reduced dynamic range, it is important that the input bias voltage is preserved when driving the
input. This can be achieved by simply AC-coupling the drive signal."*

**What T13 bypasses** (p.93, transmit chain): everything. The signal flows ALC mic audio, T3, two
future processing blocks, T4, T5, the 300 Hz high pass, pre-emphasis, T8, the limiter, T9, the 3 kHz
low pass, T12, the deviation scaler, **T13**, modulator. Injecting at T13 as a bypass-in therefore
meets **no high pass, no pre-emphasis, no limiter and no 3 kHz low pass**.

**DERIVED, and this is the number the design hangs on.** 0.87 Vp-p gives 3 kHz deviation, so the
scaling is **0.29 Vp-p per kHz**, and the 2.0 Vp-p full-scale input corresponds to **6.9 kHz
deviation**. On a 12.5 kHz channel, 100 % modulation is 2.5 kHz (MMA-00072-03 p.6), which is
**0.725 Vp-p**.

**Run at 100 % of class, not at Tait's 60 %.** Their internal 1200 baud modem defaults to 60 % of
the ceiling, which is sensible for FFSK at about 3 dB crest factor and wrong for an OFDM waveform at
12 dB, where the peak that sets the drive carries almost no energy. Measured at fixed received power
in a fixed IF, 32 seeds: 1500 Hz peak recovers 16 frames of 32 at +14 dB carrier to noise and none
below it; 2500 Hz recovers 32, 31 and 20 at +14, +12 and +10. **Three to four decibels, for a
setting.** The same sweep shows why the extra headroom at T13 is not free spectrum: 6900 Hz peak
starts going backwards as the signal outgrows a 7.8 kHz IF, so the optimum on a narrow channel is
near 5 kHz, which will not fit inside one.

**So the radio will happily accept nearly three times legal deviation, and nothing downstream stops
it.** The limiter and the deviation scaler are both upstream of T13. Over-deviation is entirely this
board's responsibility.

### Design

```
  codec                          Rt 3k3                     pin 6
  line out          C3            ____          C4        AUD_TAP_IN
      o------------||------------|____|----+----||-----------o
                  10u                      |    1u        100k, +1.5V bias
                                          [ ] Rb 1k
                                           |
                                          === C5 1n
                                           |
  codec gnd o------------------------------+--------------------o pin 3 AGND
```

**Set Rt/Rb so that a full-scale digital sample produces exactly 100 % of class deviation.** For
narrowband that is 2.5 kHz, which is 0.725 Vp-p **DERIVED**. From a 1 Vrms (2.83 Vp-p) codec output
that is a ratio of 0.256, **-11.8 dB**, giving Rt = 2k9 (2k7 or 3k0 in E24) with Rb = 1k.
**MEASURE** the codec's real output swing and adjust; the ratio is what matters, not the values.

**Not 90 %, which is what an earlier draft of this note said.** A margin below the legal ceiling
looks prudent and costs 0.9 dB of the one thing measurement says matters most here. Deviation is the
dominant lever on this link: at fixed received power in a fixed IF, dropping from 2500 Hz peak to
1500 Hz costs 3 to 4 dB of sensitivity, because post-detection signal to noise goes as deviation
squared. Setting the ceiling AT the legal limit means a software fault produces exactly 100 %
modulation, which is legal, while normal operation gets the full budget. A margin buys nothing
except a quieter signal.

This is the board's only protection against splattering the channel, and it belongs in copper rather
than in a config file. A number in software can be changed by a bug, a mixer, or a well-meaning
operator; a divider cannot.

**Thevenin impedance is 3k3 || 1k = 767 ohm DERIVED.** Keep it under about 1 kohm. Into the tap's
100 kohm input the loading is under 1 %, and a low source impedance keeps any connector-side
capacitance from eating the top of the audio band.

**C4 preserves the tap's 1.5 V bias**, as Tait require. Note the divider's shunt leg must sit on the
codec side of C4, not the radio side, or it would drag the bias to ground. 1 uF into 100 kohm is a
1.6 Hz corner **DERIVED**, well below the tap's own 3.7 Hz high pass, so the radio remains the
limiting element rather than this board.

## Serial

**DATASHEET**, p.28. IOP_TXD on pin 18, IOP_RXD on pin 17, both **3V3 CMOS**, so a 3.3 V bridge
connects directly with no level shifter.

| Parameter | Value |
|---|---|
| Baud rates | 1200, 2400, 4800, 9600, 14400, 19200, **28800** |
| Framing | 8 data bits, 1 start, 1 stop, no parity |
| Protocol | CCDI3 |
| Flow control | XON/XOFF, software only |

**28800 is the ceiling.** There is no 57600 or 115200, so do not design a UI or a driver that assumes
one. Software flow control means the bridge must not be configured for RTS/CTS, and it means XON and
XOFF bytes are reserved in the data stream.

**Pick a bridge with a unique USB serial number** - FT230X or CP2102N rather than a CH340, which
ships without one. With several radios on one host, stable `udev` naming is the difference between a
deterministic install and guessing which `/dev/ttyUSB*` is which. Do the same for the audio side if
the codec variant supports a configuration EEPROM; if it does not, the USB topology path is the
fallback, which is stable as long as nobody reorganises the cabling.

## PTT and channel status

Configure one of IOP_GPIO1..7 as a PTT input and drive it from a codec GPIO through an open-drain
FET. **DATASHEET**, p.27-28: inputs are 3V3 CMOS with input low at or below 0.7 V, input high at or
above 1.7 V, and an input low current of 100 to 120 uA, so there is an internal pull-up and a small
FET pulling low is all that is needed. Safe input range is -0.5 to +5.5 V with current under
+/-10 mA.

**Use a hardware PTT line, not CCDI.** A serial command has queueing and parsing latency; a GPIO
does not, and transmit timing is one of the things this modem cares about.

**Consider a hardware transmit timeout** on the board - a monostable or a watchdog that releases PTT
after a few seconds regardless of what USB is doing. A wedged host is the one failure mode that
inconveniences everybody else on the channel, and it is cheap to make impossible.

A second GPIO configured as a radio output gives channel-busy status, readable on another codec GPIO.
Note the radio's outputs have **1 kohm series resistance** and are specified at 100 uA, so present a
high-impedance load.

## Power

**Bus powered.** A hub, a codec and a bridge is on the order of 100 to 150 mA, well inside a single
upstream port's budget, and it avoids putting any switching regulator inside the radio.

13V8_SW on pin 1 is available and generous - **1 A continuous, 2 A peak for under a second**
(**DATASHEET** p.28), derated by whatever the control head and auxiliary interfaces draw. It is
there if bus power proves marginal, but prefer a linear regulator to a switcher if it is used, and
accept the dissipation: 13.8 V to 5 V at 150 mA is 1.3 W **DERIVED** in a sealed lid.

One behavioural consequence worth choosing deliberately: bus power means the board is alive whenever
the host is, regardless of the radio. Powering from 13V8_SW instead makes the USB devices appear and
disappear with the radio, which some hosts handle more gracefully than others.

## Mechanical

**DATASHEET**, p.97-98. The radio body provides space for an internal options board with a maximum
envelope of **139 x 99 mm** and **nine dia 3.5 mm screw points in the inside of the lid**. Boards may be
any size and shape within that, using any combination of the fixings.

**The hole provided for the external options connector is the USB exit** (p.97: an internal options
board "can also use the hole provided for the external options connector"). That is the intended
route for a cable or socket through the case, so no drilling is required.

[marrold/Tait_TM8100-8200_Options_Board_Dimensions](https://github.com/marrold/Tait_TM8100-8200_Options_Board_Dimensions)
has the 3DK drawing traced to scale as an SVG, which is the quickest way to a correct board outline.
Its author notes the fit has not been verified against a manufactured board, so check it before
committing to a panel.

139 x 99 mm is a lot of room for three small devices. Spend it on separation and screening rather
than on shrinking the board.

## Interference

The receiver makes 12 dB SINAD at -121 dBm (MMA-00072-03 p.14) and this board sits inside the same
casting. USB clocks are on 12 MHz multiples, so 12, 24 and 48 MHz all have a harmonic at exactly
144.000 MHz, and crystal tolerance moves it by only a few kHz. **At an operating frequency of
144.950 MHz that spur is 950 kHz outside a 7.8 kHz channel filter and can be discounted.** What
cannot be predicted from a desk is broadband noise from switching edges and data transitions raising
the floor.

Treatment, in order of effect:

1. **Bond the USB shield to the casting 360 degrees at the point of entry**, not through a pigtail.
   The cable is the largest antenna in the system and common-mode current on it radiates far more
   than anything on the board.
2. **Common-mode choke on D+/D-** (about 90 ohm at 100 MHz) with low-capacitance ESD protection
   (under 3 pF). **Do not add shunt capacitance to the data pair** beyond that; full-speed USB
   tolerates a few pF and a "filter" there only costs eye margin.
3. **Pi filter on VBUS at the entry**: 1 uF and 100 nF, a ferrite bead of about 600 ohm at 100 MHz
   rated for the current, then 100 nF. Every filter ground goes to the same bond point as the shield.
4. **Screen the digital section** and keep the codec's analogue pins, both audio pairs and the control
   lines on the quiet side. Filter each line where it crosses: 1 nF on the audio pairs (265 kHz
   corner against the 600 ohm tap source, 800 kHz against the 767 ohm transmit divider, both
   **DERIVED**), 10 nF on PTT, and **470 pF on serial** - at 28800 baud a 10 nF part would round the
   edges badly.
5. Solid ground plane under the digital section, guarded and stitched crystal, and no audio routed
   under the hub.

**Fit the footprints even if the parts are not populated.** Screen-can pads, the choke, the
feed-through positions, zero-ohm links where filters would go. If the sensitivity test finds a
problem it is then a fitting exercise rather than a respin, and a problem you cannot predict is
exactly the kind worth leaving room to fix.

## Timing, for the modem's benefit

Group delay is **1.8 ms at R1 and 1.8 ms at T13** (**DATASHEET**, p.21-22), so **3.6 ms DERIVED**
round trip through the radio's audio processing, before USB latency and buffering. Relevant to
TXDELAY, to any turnaround timing, and to a receiver that reports where it thinks a burst started.

## Verification

**Sensitivity, first and last.** 12 dB SINAD on the operating channel with the board powered and
actively streaming audio, against the same measurement with USB unplugged. An idle hub is far
quieter than one moving isochronous packets every millisecond, so measure it working, not resting. A
degradation under a decibel means the screening can stay unpopulated.

**Deviation, two ways that should agree.** Inject a 1 kHz tone at a known codec output level and
confirm 0.87 Vp-p gives 3 kHz deviation, per Table 2.7. Then check it independently with a Bessel
null, which needs only an SDR: a carrier frequency modulated by a single tone loses its carrier
component entirely at a modulation index of 2.405, so deviation at the null is 2.405 times the tone.
A **624 Hz** tone nulls at 1.5 kHz deviation and **1040 Hz** at 2.5 kHz. If the two methods disagree,
believe neither until you know why.

**Receive level and response.** Feed the radio a signal generator at 1 kHz, 3 kHz deviation, around
-80 dBm so no AGC is acting, and confirm 1.2 Vp-p open circuit at pin 2. Then sweep the modulation
frequency and compare against the R1 plot in Table 2.10 - if it rolls off at 3 kHz, the tap is not
R1 and something is programmed differently from what you think.

**Full-scale ceiling.** Play 0 dBFS and confirm deviation lands on the class ceiling and does not
exceed it.
This is the one test that protects everyone else on the channel, so do it before the first transmit
on a real antenna.

## Open items

- **The codec's actual full-scale input and output swing**, which sets both dividers. Two
  measurements, and they are the only ones the design waits on.
- **Whether the chosen codec supports a configuration EEPROM** for a USB serial number. Decides
  whether stable device naming comes free or has to come from USB topology.
- **Whether a bus-powered hub's per-port current advertisement** is acceptable to both downstream
  devices. If not, 13V8_SW is the fallback.
- **Fit of the traced board outline** against a real radio, which its author has not verified.
