# An internal USB accessory board for the Tait TM8100

Design notes for a board fitting inside a TM8100 radio body, presenting **one USB cable to the host**
that enumerates as a serial device and an audio device. The serial side reaches the radio's CCDI
port; the audio side reaches its audio tap points. Intended for headless packet operation with
`pdn-soundmodem`.

## Sources, and two corrections to an earlier note

Nearly every figure here comes from the **TM8100/TM8200 3DK Hardware Developer's Kit Application
Manual** (Tait Electronics, March 2006, 156 pages), which is the document Tait wrote for exactly this
purpose. Citations below are page numbers in it unless another document is named.

**That manual superseded two claims in [tm8100-cm108-interface.md](tm8100-cm108-interface.md),**
which was written from the service set before the 3DK manual was in it. Both are corrected there
now, and they are worth repeating because they were load-bearing:

- It said no input-level-to-deviation figure is published anywhere. **It is published**, per tap
  point, in the 3DK manual's Table 2.7. The Bessel-null calibration is still worth doing, but as a
  cross-check rather than the only route to a number.
- It said T13 appears nowhere in the documentation. **T13 is a fully documented tap-in point** (p.93),
  with its own level, group delay and frequency response specifications. The service manual's CCTM
  tap list is a test command's subset in an older numbering, not the feature set.

The two notes now share the same figures and differ only in where the board lives. This one is the
better read if you are designing copper; the CM108 note has since split into
[wiring instructions](tm8100-cm108-interface.md) for the deployed widget and
[extended notes](tm8100-cm108-interface-notes.md) carrying the dongle-side practicalities and the
bench procedures.

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

Tait sell the parts as a kit: **TMAA30-06** as an orderable product code, which is the same bag the
service manual's parts list calls 600-00010-00, "Pkg Kit Opt Int parts". The 3DK manual prints its
bill of material and both connector footprints (p.101, Table 4.1, Figures 4.4 and 4.5):

| Tait IPN | Qty | Description |
|---|---|---|
| 240-10000-11 | 1 | Conn SMD 18w Skt M/Match, the board-side socket |
| 219-00329-00 | 1 | Loom TMA Int Opt, the 18-way ribbon between the two sockets |
| 240-00011-67 | 1 | Skt 15w Drng Ra Slim Dsub, if you take a 15-way HD D-range out of the case |
| 240-00010-80 | 1 | Plg 15w Drng Hi-D, its mating plug |
| 240-06010-29 | 1 | Conn 9w Hood/Cvr Lets |
| 354-01043-00 | 2 | Fsnr Scrw Lok 1pr 4-40, the D-range hexlocks |
| 347-00011-00 | 2 | Scrw 4-40x3/16 |
| 349-02062-00 | 9 | Scrw M3x8 self-tapping, for the nine lid screw points |
| 362-01108-00 | 1 | Seal Drng Cvr 9way |
| 362-01111-00 | 1 | Seal Drng 9way |

The socket footprint is 26.2 x 4.6 mm with 21.6 mm between the outer pins, and **8.2 mm tall
including the mating connector**, which is most of the height budget in the next section. Tighten
the hexlocks before screwing the board to the lid, to 0.9 N.m. The screw length is the one place the
manual argues with itself: the BOM says M3x8, the installation drawing on p.100 calls out M3x10.
Buy the 8.

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
design leaves both unconnected (both **DATASHEET**, Table 2.16, p.28). **RX_BEEP_IN** (pin 5) injects
sidetone: 1 kohm input impedance, 0.76 Vp-p for 6.2 Vp-p at the speaker, full scale 2.5 Vp-p, and
a 0.3 to 3 kHz response, so it is a beep injector and nothing more. **RX_AUD** (pin 7) is receive
audio taken after the volume control: 1.0 Vp-p at 60 % deviation and full volume, 2.0 Vp-p full
scale, 100 ohm out. **"At full volume" is the whole problem with it**, and exactly why it is the
wrong tap for a modem: a knob nobody told you about is in the gain path. All three Tait options
boards leave it unconnected too.

## Receive path: AUD_TAP_OUT at tap R1

All **DATASHEET**, p.21-23 and p.91.

| Property | Value |
|---|---|
| Output impedance | 590 / **600** / 650 ohm, DC to 10 kHz, constant across frequency |
| DC offset | 2.1 / **2.3** / 2.5 V, no load, zero Rx frequency error |
| Level at tap R1 | 0.54 / **0.60** / 0.66 Vp-p for **3 kHz deviation** at 1 kHz, into 600 ohm |
| Full scale output | **2.0 Vp-p** into 600 ohm, **4 Vp-p** with no load |
| Safe limits | -0.5 to +17 V, short-circuit safe, input current under +/-20 mA |
| Group delay at R1 | **1.8 ms** |
| Signal chain | DAC at 48 kSa/s, 12 kHz low pass, buffer amplifier |

**DERIVED, and confirmed by Tait's own prose.** The quoted levels are into a matched 600 ohm load,
so a light load sees twice the voltage: **1.2 Vp-p for 3 kHz deviation** open circuit. The p.91 text
states the full-scale case outright, "full scale output level is nominally 4Vp-p with no load", so
the doubling is not an inference on the part that matters most. At the 1.5 kHz deviation this modem
is expected to run (60 % of narrowband's 2.5 kHz, which is what Tait's own 1200 baud modem uses)
that is **0.6 Vp-p, or 212 mVrms** open circuit.

**R1 clips at 10 kHz of deviation** (2.0 Vp-p into 600 ohm at 0.20 Vp-p per kHz), and the narrowband
IF is 7.8 kHz wide, so nothing that reaches this tap can overload it. **DERIVED.**

**Two things about R1 specifically.**

Its DC offset moves with receive carrier frequency error (p.91) - true of R1, R2 and R4 only, and
unsurprising, since raw discriminator output IS frequency. **AC-couple, and do not treat the DC as a
constant.** A 2 kHz frequency error would shift it by 2/3 of a volt.

Its frequency response is plotted per bandwidth class (Table 2.10, p.24) and it is the widest tap
available, being ahead of the deviation normaliser, the 3 kHz low pass, the 300 Hz high pass and
de-emphasis. Keep that plot to hand: it is the reference a receive-path sweep gets compared against,
and it is the fastest way to prove a radio is tapped where its programming claims.

**Use tap type D - Split, not C - Bypass Out.** Split copies the audio out and leaves the radio's own
receive path running; Bypass Out diverts it. Tait are explicit that Bypass Out on R1, R2 or R4 "may
prevent correct operation of the signalling decoder" with subaudible or inband signalling (p.94), and
their own external-modem configuration uses R1-D. Split also keeps the speaker and busy detect alive,
which is worth having on a radio you are sharing with a human.

**Being ahead of the normaliser has one consequence to design around.** R1's volts per kilohertz does
not follow the channel spacing, which is why Tait specify it in kHz of deviation while every other
receive tap is specified as a percentage of the channel's rating. If this board only ever works
12.5 kHz channels that is a feature, and the divider below is fixed. If it has to work 12.5 and
25 kHz channels with one set of resistors, use **R2 and T12** instead: they sit on the far side of
the normaliser and scaler, are otherwise identical to R1 and T13 including the 1.8 ms group delay,
and their levels "are automatically scaled to match the channel spacing" (p.112). The cost is that
the radio, not this board, then decides the drive.

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

Know where that puts the signal: since R1's full scale is 10 kHz of deviation, aligning the two
full scales lands **1.5 kHz of deviation at about -17 dBFS** and 2.5 kHz at -12.5 dBFS **DERIVED**.
That is a quiet-looking waveform on a level meter and it is the correct answer on a 16-bit path;
the alternative, gaining it up to sit nearer full scale, buys nothing but a codec that clips before
the radio does.

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

The 100 kohm input impedance **is** that bias network: a 220k from 3V3 and a 180k to ground
(Figure 3.12, p.91), which is 99 kohm and 1.49 V. Anything you put across the pin divides it. There
is a DC-coupled option if you want one, and for a data application Tait prefer it: drive the pin at
**1.5 +/- 0.2 V** and skip the capacitor (p.111). The 3.7 Hz high pass is downstream and digital, so
a bias error cannot pull the transmit carrier frequency (Table 2.7 note 4); all it costs is symmetry
of headroom. This board AC-couples anyway, because a codec that boots into an undefined output state
should not be able to sit the tap at a rail.

**What T13 bypasses** (p.93, Figure 3.14): everything. The signal flows ALC mic audio, T3, a future
processing block, T4, a second future processing block, T5, the 300 Hz high pass, pre-emphasis, T8,
the limiter, T9, the 3 kHz low pass, T12, the deviation scaler, **T13**, modulator. Injecting at T13
as a bypass-in therefore meets **no high pass, no pre-emphasis, no limiter and no 3 kHz low pass**.

The group delays measure the same statement: **T13 and T12 are 1.8 ms from the antenna, T9 is 6.6,
T8 is 9.6, T5 is 11.6, T4 and T3 are 11.7** (Table 2.7). Ten milliseconds of processing sits between
the microphone's tap and the modulator, and none of it is between T13 and the modulator.

**Tait attach a regulatory caveat to exactly this, and it is fair.** Their external-modem
specification notes that the listed modulation formats "may not comply with transmit spectral
emission mask regulations in some countries. It is the integrator's responsibility to ensure that
the system complies" (p.111), and their commissioning steps ask for a spectrum check that is "not
necessary if tap in point T8 is used" (p.113). T8 is behind the limiter and the 3 kHz low pass,
which is why it needs no check and why we are not using it. At T13 the emitted spectrum is a
property of the waveform, so being clean is the modem's job.

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
  codec                          Rt 3k0                     pin 6
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
that is a ratio of 0.256, **-11.8 dB**, so Rt = 2k9 exactly with Rb = 1k. **Fit 3k0**, the nearest
E24 value on the safe side: it gives 2.44 kHz, where 2k7 would give 2.64 kHz and put the ceiling 5%
over the legal one. **MEASURE** the codec's real output swing and adjust; the ratio is what matters,
not the values.

**Then confirm it against the radio, because the spread is wider than the divider.** T13's level is
0.78 / **0.87** / 0.96 Vp-p, which is **+/-0.9 dB** of radio-to-radio variation on the one figure the
ceiling depends on: a divider computed from the typical number over-deviates by 9 % on a radio at the
sensitive end. 1 % resistors are not the uncertainty that matters here. Either measure each radio and
move Rt by one E24 step, or size Rt on the 0.78 Vp-p figure so that the worst case is exactly legal
and the typical radio gives up 0.9 dB.

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

**Thevenin impedance is 3k0 || 1k = 750 ohm DERIVED.** Keep it under about 1 kohm. Into the tap's
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
+/-10 mA. The pull-up is **33k to 3.3 V**, and the IOP lines carry **1 kohm in series** and, unlike
the auxiliary connector's, are "only protected against minor over-voltage conditions" (p.27, p.41).
Inside a sealed radio body that is acceptable; it is one more reason not to bring these pins out of
the case.

Three settings, not one, and each of them has a failure mode attached:

- **Active state: Low.** Not a preference. "Because of the pullups, setting the active state to High
  will cause the action to commence if the connector is removed or dislodged while the radio is on.
  To prevent this happening, set the active state to Low" (p.42). On an internal board the loom is
  the thing that can work loose, and an active-high PTT means a jarred loom keys the transmitter.
- **Debounce: 0.** The field exists for mechanical switches, where Tait recommend 50 to 100 ms.
  Every millisecond is added to keying latency and a logic signal has no bounce to remove.
- **PTT Priority: Highest.** "All PTT lines may be active at any time, and the PTT line with the
  highest priority controls the audio path" (p.51). Two external PTTs are available across the two
  connectors, and the microphone is always there as a third.

One power-up behaviour to handle in the driver: if the PTT line is already active when the radio
powers up, "it must be re-applied for the action to be carried out" (p.51). A host that comes up
with its GPIO asserted will not transmit until it deasserts and asserts again.

**Use a hardware PTT line, not CCDI.** A serial command has queueing and parsing latency; a GPIO
does not, and transmit timing is one of the things this modem cares about.

**Consider a hardware transmit timeout** on the board - a monostable or a watchdog that releases PTT
after a few seconds regardless of what USB is doing. A wedged host is the one failure mode that
inconveniences everybody else on the channel, and it is cheap to make impossible.

A second GPIO configured as a radio output gives channel-busy status, readable on another codec GPIO.
Note the radio's outputs have **1 kohm series resistance** and are specified at 100 uA, so present a
high-impedance load.

**Set Squelch Detect Type to Signal Strength, and the busy line becomes useful.** Busy status can be
derived from RSSI or from noise level, and the response times differ by a factor of four:
**under 5 ms for signal strength, under 20 ms for noise level** (p.77). Tait's own modem
configuration specifies signal strength for that reason. From valid RF at the antenna to the line
going active is 3 ms typical (Table 5.2).

It is not a replacement for the modem's own data-carrier detect, which knows about modulation where
this knows only about energy. Reading both is how you tell a busy channel from a channel busy with
something decodable, which is worth logging when a link goes quiet.

**Ignore every output for the first two seconds after the radio powers up.** I/O lines configured as
outputs are high-impedance until the radio takes control, "up to 1 to 2 seconds after power is first
applied", and the pull-ups dominate, so everything reads high (p.73). With busy status active high
that reads as a busy channel, which at least fails towards silence.

## Programming

Tait publish the radio configuration for an external modem (p.112). It is written for the auxiliary
connector, but AUD_TAP_IN, AUD_TAP_OUT and the tap machinery are literally the same signals on the
internal connector, so only the pin names change. Follow it: every field in it is one you would
otherwise get wrong once.

| Form | Setting | Value |
|---|---|---|
| Programmable I/O, Digital | PTT line | External PTT1, **Active Low**, **Debounce 0**, signal state None |
| Programmable I/O, Digital | busy line | Busy Status, Active High, Momentary |
| Programmable I/O, Audio | Rx | tap out **R1**, type **D - Split**, unmute **Except on PTT** |
| Programmable I/O, Audio | EPTT1 | tap in **T13**, type **A - Bypass In**, unmute On PTT |
| PTT / External PTT (1) | Advanced EPTT1 | Transmission Type **Data**, State Is Reflected cleared, Priority **Highest**, Audio Source **Audio Tap In** |
| Networks / Basic Settings | Squelch Detect type | **Signal Strength** |

**Except on PTT** is the receive gate a modem wants; the full set for the Rx path is Busy Detect,
Busy Detect and Subaudible, Rx Mute Open, and Except on PTT (p.95). Gate a modem on busy-detect and
it misses the start of every burst, which looks exactly like an acquisition fault in the modem and
is not one.

Tait add one system-level note worth honouring: put every channel the modem uses in one network and
every voice channel in another, so the two sets of channel settings cannot drag each other around.

## Power

**Bus powered.** A hub, a codec and a bridge is on the order of 100 to 150 mA, well inside a single
upstream port's budget, and it avoids putting any switching regulator inside the radio.

13V8_SW on pin 1 is available and generous - **1 A continuous, 2 A peak for under a second**
(**DATASHEET** p.28), derated by whatever the control head and auxiliary interfaces draw. It is
there if bus power proves marginal.

**Prefer bus power for thermal reasons as much as electrical ones.** Tait ask for industrial-grade
parts rated to 85 C and say that "heat dissipation added by an internal options board can reduce the
radio's operating temperature range or duty cycle. Keep heat dissipation to a minimum" (p.102). A
linear regulator dropping 13.8 V to 5 V at 150 mA is **1.3 W DERIVED** inside a sealed lid that is
already carrying a PA, which is exactly the dissipation they are asking you not to add. If the board
must run from 13V8_SW, that 1.3 W is the reason to reach for a well-filtered switcher after all, and
to measure the receiver afterwards.

One behavioural consequence worth choosing deliberately: bus power means the board is alive whenever
the host is, regardless of the radio. Powering from 13V8_SW instead makes the USB devices appear and
disappear with the radio, which some hosts handle more gracefully than others.

## Mechanical

**DATASHEET**, p.97-99. The radio body provides space for an internal options board with a maximum
envelope of **139 x 99 mm** and **nine dia 3.5 mm screw points in the inside of the lid**. Boards may be
any size and shape within that, using any combination of the fixings.

**Height is the constraint that actually binds, not area.** Figure 4.2 (p.99) divides the envelope
into zones with different ceilings: **10.7 mm** over the largest region, then **9.5, 8.2, 7.7 and
6.7 mm** over the rest, with a no-components zone, twelve keep-outs where no routes may go, five
dia 10 mm and twelve dia 7.8 mm clearance circles, and two rules that decide the stack-up:

- **No components on the bottom side at all**, and maximise ground plane over it.
- **Through-hole parts are allowed on the top side** in the marked areas, with **no more than 2 mm**
  of leg protruding from the bottom.

Against 10.7 mm, the Micro-MaTch socket alone is **8.2 mm including its mating connector**. A
standard USB-A socket is about 6.5 mm and fits; a USB-B is around 10.5 mm and does not, in any
sensible zone. **Design for a low-profile socket or a pigtail**, and settle that before laying
anything out, because it is not recoverable at the artwork stage.

**The hole provided for the external options connector is the obvious USB exit** (p.97: an internal
options board "can also use the hole provided for the external options connector"), sized for a
9-way standard-density or 15-way high-density D-range and closed by a bung when unused (p.30). Two
things come with it. **It costs the radio its IP54 rating**: "the IP54 protection class no longer
applies when the external options connector or an additional connector are used", and sealing is
"the integrator's sole responsibility" (p.102). And there is a second, smaller opening beside it,
**a round hole up to 7.5 mm for an SMA or a cable grommet** (p.30), which for a USB pigtail is the
better of the two: a grommet seals more readily than a D-range cut-out, and 7.5 mm is ample for a
USB cable.

[marrold/Tait_TM8100-8200_Options_Board_Dimensions](https://github.com/marrold/Tait_TM8100-8200_Options_Board_Dimensions)
has the 3DK drawing traced to scale as an SVG, which is the quickest way to a correct board outline.
Its author notes the fit has not been verified against a manufactured board, so check it before
committing to a panel, and note that a traced outline carries the plan view and not the height
zones.

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
   corner against the 600 ohm tap source, 212 kHz against the 750 ohm transmit divider, both
   **DERIVED**), 10 nF on PTT, and **470 pF on serial** - at 28800 baud a 10 nF part would round the
   edges badly.
5. Solid ground plane under the digital section, guarded and stitched crystal, and no audio routed
   under the hub.

**Fit the footprints even if the parts are not populated.** Screen-can pads, the choke, the
feed-through positions, zero-ohm links where filters would go. If the sensitivity test finds a
problem it is then a fitting exercise rather than a respin, and a problem you cannot predict is
exactly the kind worth leaving room to fix.

### What Tait ask for, which is not quite the same list

Their EMC chapter (p.103 to 106) is written for exactly this board, and it puts the effort in a
different place. Worth reading in full; the parts that change a layout:

**Filter the outside, not the loom.** "For the I/O lines to or from the radio, filtering is usually
not necessary. The exception is when the internal options board contains high-speed digital
circuits. In this case, the outputs to the radio should be RC-filtered on the internal options board
as close as possible to the connector to minimise noise on the loom." A USB hub counts as
high-speed, so the exception is us, and item 4 above is doing that job. Everything crossing to the
**external** connector, on the other hand, needs filtering and ESD protection as a matter of course.

**Their values for external lines are 10 nF on audio and 470 pF plus a zener clamp on digital**, all
returned to the chassis rather than to signal ground, so that a discharge has a low-impedance path
that does not run through the board. The zener wants a small capacitor in parallel to slow the pulse
edge so it clamps without overshoot, and they suggest a zener on the digital supply too, about 0.5 V
above the rail. Note their 10 nF is chosen for ESD energy and assumes voice audio: against a 600 ohm
tap it is a 26.5 kHz corner, which costs about half a decibel at 9.6 kHz on a wideband R1 tap. On a
line that stays inside the casting, 1 nF is the better trade.

**Use both ground pins, and use them separately.** AGND on pin 3 and DGND on pin 16 are joined
inside the radio through its ground plane, and the loom's wire impedance is high enough that digital
return current develops real noise along its length. Two returns also halve the impedance of the
earth connection at the board end. Keep them separate on a low-speed board; on a high-speed one the
ground-plane requirement usually wins and they end up common.

**Bond the board to the lid next to the external connector.** A plated through hole **dia 3.5 mm
with a 7 mm pad on both sides**, resist cleared, connected to analogue earth or the ground plane,
using the mounting screws nearest the connector. Other screws may also be bonded but need not be.
This is the same instinct as item 1 above, expressed in the geometry Tait's own screw points allow.

**Four layers or more, with one reserved as an unbroken ground plane**, is their standing
recommendation for anything with a DSP or a 16/32-bit part in it, which includes a USB hub. Decouple
with multiple 100 nF ceramics plus a low-ESR tantalum, and analyse it out to **500 MHz**. They
finish with a warning worth quoting to anyone who thinks this board is trivial: high-speed digital
design "should not be undertaken without" experience, the right tools and high-bandwidth test
equipment.

## Timing, for the modem's benefit

Group delay is **1.8 ms at R1 and 1.8 ms at T13** (**DATASHEET**, p.21-23), so **3.6 ms DERIVED**
round trip through the radio's audio processing, before USB latency and buffering. That is the
figure for a receiver reporting where it thinks a burst started.

**It is not the figure for TXDELAY, and the difference is large.** Tait measure the delays end to
end in Table 5.2 (p.113), all with zero debounce programmed:

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

**14.8 ms is the floor for TXDELAY.** Audio crosses the transmit chain in 1.8 ms, but the radio needs
nearly fifteen from the PTT edge before what leaves the antenna is at full power and carrying your
modulation; anything sent inside that window is thrown away. The PA is not the slow part, since
Tait's external PTT reaches 90 % of full power in under 8 ms (p.51), so the balance is the modulation
path settling.

**12.3 ms is the floor on the way back.** After PTT releases, the receive path needs that long before
the audio out of R1 is valid, so a station replying inside about 13 ms of your carrier dropping is
talking to a deaf radio. Both numbers are per radio, so a round trip carries each twice, and USB
buffering is on top of all of it.

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

**Turnaround.** Key the radio from the PTT line with a tone playing and watch the RF envelope and
the demodulated tone on an SDR. Full power carrying valid modulation should arrive 14.8 ms after the
edge. A figure much longer than that usually means debounce was left programmed on the PTT input,
which is a one-field fix and otherwise silently costs every burst.

**Loop-back before the radio.** Tait's own commissioning sequence starts by joining baseband out to
baseband in and passing traffic with the radio disconnected (p.113). Doing the same with a jumper
between the codec's input and output divider proves the host, the driver and the modem before any
of it is entangled with a transmitter.

## Open items

- **The codec's actual full-scale input and output swing**, which sets both dividers. Two
  measurements, and they are the only ones the design waits on.
- **Which USB socket, if any, fits the height zones.** 10.7 mm at best and 8.2 mm of that already
  spent on the Micro-MaTch connector; a pigtail through the 7.5 mm round hole may be the answer.
- **Whether the chosen codec supports a configuration EEPROM** for a USB serial number. Decides
  whether stable device naming comes free or has to come from USB topology.
- **Whether a bus-powered hub's per-port current advertisement** is acceptable to both downstream
  devices. If not, 13V8_SW is the fallback.
- **Fit of the traced board outline** against a real radio, which its author has not verified.
