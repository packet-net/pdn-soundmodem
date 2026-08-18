# The TM8100 internal USB board, and why it is the way it is

The reasoning behind a board that fits inside a TM8100 radio body and presents **one USB cable to
the host**, enumerating as a serial device on the radio's CCDI port and an audio device on its tap
points, for headless packet operation with `pdn-soundmodem`.

**The board itself is [packethacking/tait-cm108](https://github.com/packethacking/tait-cm108)**
(Tom Wardill): a CM108B, an FE1.1s hub and a CP2102N on a four-hole board with a USB-C entry. Its
schematic, layout and bill of materials live there and are not repeated here. This note keeps what
a schematic cannot carry: which tap points and why, how the two dividers are sized and what
protects the channel if software gets it wrong, what the radio has to be programmed to, what the
timing means for the modem, and what to check before the first transmission. An earlier version
was a plan for laying out such a board; the parts of it that a finished board answers (connector
kits, mechanical zones, socket heights, filter component lists) have been cut.

## Sources

Nearly every figure comes from the **TM8100/TM8200 3DK Hardware Developer's Kit Application
Manual** (Tait Electronics, March 2006, 156 pages, `MMA-00011-01` in
[M0LTE/tait-tm8100-tm8200-docs](https://github.com/M0LTE/tait-tm8100-tm8200-docs)), which is the
document Tait wrote for exactly this purpose; its section 5.1.1 is their own worked example of an
external modem on these taps. Page numbers below are in it unless another document is named. The
codec figures are from the CM108B datasheet (rev 1.11), cross-checked against the CM108AH measured
on the deployed widget. Marking: **DATASHEET** stated and cited, **DERIVED** computed with the
arithmetic shown, **ABSENT** not in the documents.

The same figures, applied to a stock CM108 widget on the auxiliary connector, are the deployed
build in [tm8100-cm108-interface.md](tm8100-cm108-interface.md) with the reasoning in
[tm8100-cm108-interface-notes.md](tm8100-cm108-interface-notes.md). The three notes agree on
every level; they differ only in where the board lives and which connector it reaches.

**Where this note has been corrected**, so the record is not lost: the 3DK manual established that
tap levels are published (Table 2.7) and that T13 is a documented tap, both of which an earlier
note denied; comparing this note against the built board on 2026-08-18 added that the serial lines
are negative logic and need inverters (the board had them, this note did not), that the receive
divider's shunt must sit behind a coupling capacitor because the codec pin is biased (this note
drew it on the pin, and the board inherited that), and replaced the "measure the codec"
placeholders with the CM108B's figures. Only the audio wiring of the board was verified here, from
its schematic netlist.

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
   |    |   |           |         |          |            |
   |   inv inv          |         |          |            |
   +-----|---|----------|---------|----------|------------+
         |   |          |         |          |
        17  18          2         6      one of 9..15      18-pin Micro-MaTch
      IOP_  IOP_   AUD_TAP_   AUD_TAP_    IOP_GPIOn
      RXD   TXD      OUT        IN
```

The serial pair crosses over, as serial pairs do: the pin names are the radio's own perspective,
with IOP_TXD in the manual's digital *output* line list and IOP_RXD in its *input* list (Tables 3.7
and 3.2, p.71 and p.40). So the bridge's TXD lands on pin 17, IOP_RXD, and its RXD on pin 18. An
earlier version of this diagram wired TXD to TXD, which is a dead serial port both ways. **The pair
is also inverted**: Tait serial is negative logic, RS-232 polarity at 3V3 levels, so there is an
inverter in each direction between the bridge and the connector. See Serial below.

Host sees: one USB device tree, a `/dev/ttyUSB*`-class serial port, and an ALSA card. No drivers
beyond USB CDC and USB Audio Class 1.0.

## The radio interface

**18-pin Micro-MaTch** (p.27), **SK102** on the main board (silkscreen may say PL103,
MMA-00005-05 p.410). Every Tait options board fits **the same socket** rather than a plug, and a
ribbon loom (Tait 219-00329-00, in kit TMAA30-06 / 600-00010-00) joins the two; the reference
board does the same. Tait's part is 240-10000-11, and its socket is 8.2 mm tall including the
mating connector, which is most of the height budget inside the lid.

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
| 17 | IOP_RXD | Serial receive data, **negative logic** (see Serial) | 3V3 levels |
| 18 | IOP_TXD | Serial transmit data, **negative logic** (see Serial) | 3V3 levels |

All **DATASHEET**, p.27. Named from the radio's point of view: AUD_TAP_OUT is what the codec
listens to and AUD_TAP_IN is what it drives, which is the opposite of the codec-side names a
schematic will use.

**One sharing constraint that matters** (p.27): the digital signals and the serial port are
independent of the auxiliary connector, but **AUD_TAP_IN, AUD_TAP_OUT, AUX_MIC_AUD and RSSI are
shared with it**. So this board's audio cannot coexist with anything using the auxiliary connector's
audio, while its serial port can coexist with a second serial user on the auxiliary side.

RSSI is on pin 8 but is not used here: it is read digitally over CCDI instead, which is more
accurate and costs no analogue input.

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
the doubling is not an inference on the part that matters most. On receive the level is the far
station's deviation, not ours: a peer at 100 % of narrowband class (2.5 kHz, which is what the
transmit section below calibrates this board to) delivers **1.0 Vp-p, or 354 mVrms** open circuit,
and one at the 60 % that Tait's own 1200 baud modem defaults to (1.5 kHz) delivers 0.6 Vp-p,
212 mVrms.

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
  pin 2                    Rs                                   codec
  AUD_TAP_OUT             1k0                       C1         MICIN
  600R, +2.3V DC          ____                      ||
      o----------------|____|-----+-----------+----||-----------o  10k to VREF (1.75 V)
                                  |           |    10u             inside the CM108B
                                 [ ] Rp      === C2 1n
                                  |  3k9      |    (RF, to the screen ground)
  pin 3 AGND o--------------------+-----------+---------------------o codec ground
```

Rp and C2 are parallel shunt legs from one node; stacked in series they read as a divider that
stops dividing below radio frequencies, and the codec sees the tap barely attenuated.

**The coupling capacitor goes at the codec pin, because the pin is biased.** MICIN is 10 kohm to
VREF at 1.75 V (**DATASHEET**, pin table and block diagram). A shunt to ground on the pin drags it
to about half a volt, and whether that saturates the booster or only costs it headroom, C-Media's
reference feeds the pin through a capacitor and so does the widget's own board, which is why the
deployed assembly could put Rp on the pad and get away with it. Drawn as above the tap's 600 ohm
buffer sees a 5.5 kohm DC load and 0.4 mA (it is short-circuit safe, +/-20 mA), and the node sits
near 1.6 V so C1 has almost no DC across it. A second 10 uF at pin 2 keeps the tap AC-isolated too,
if you would rather.

**A line-level input, which on the CM108B means MICIN with its boost cleared.** There is no
separate line input, but the 12 dB microphone booster is EEPROM register 0x2B bit 3, and with it
cleared MICIN is a **2.88 Vp-p** full-scale input at 0 dB gain (**DATASHEET**). Leave the boost on
and a peer at 60 % deviation arrives at +2 dBFS through this divider and clips; clear it and the same
peer is at -17.5 dBFS. Set boost off and the ADC's initial volume at 0 dB (default +8) in the
EEPROM, so the codec boots calibrated rather than relying on the host.

**Size Rs/Rp so the radio's full scale lands on the codec's**, so both clip together and no headroom
is wasted: 4.0 to 2.88 Vp-p is **-2.9 dB DERIVED**, and against the tap's 600 ohm source that is
**Rs = 1k0 with Rp = 3k9**. Count the pin's 10 k across Rp: 3k9 in parallel with 10k is 2.8k, so
the pad is really **-3.9 dB DERIVED**, in the safe direction. R1's 4 Vp-p full scale lands at
2.55 Vp-p against the codec's 2.88, and the codec clips at 11 kHz of deviation against R1's own 10,
which is as aligned as E24 values get. Keep the divider low impedance for exactly this reason: an
earlier 10k/22k version handed that same 10 k nearly 5 dB of error. The CM108AH on the widget
measured 455 mVrms full scale at +8 dB gain, the same input to within a decibel, so the family is
consistent.

**Where that puts the signal:** a peer at 100 % of class deviation (2.5 kHz) is at **-13 dBFS**,
one at Tait's 1200 baud default of 60 % at -17.5 dBFS **DERIVED**. Quiet on a meter and correct on
a 16-bit path; gaining it up buys nothing but a codec that clips before the radio does. The widget
assembly targets -12 dBFS at 60 % instead, 4.4 dB hotter, from the same reasoning applied to a
smaller full scale. Both are fine, and on either board the difference is one capture-gain step, so
pick one and write it down.

**C1** at 10 uF into the pin's 10 k plus the divider's 1.3 k is a **1.4 Hz** corner **DERIVED**. Do
not economise: 1 uF is 14 Hz, fine for a 300 Hz to 3 kHz mode and marginal for a wideband one off
R1. It has under 0.2 V across it, so X7R is fine; a second capacitor at pin 2 sits across the tap's
+2.3 V and wants non-polarised or film. **C2** at 1 nF against the node's 1.1 kohm is
**141 kHz DERIVED**, RF hygiene only.

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

**So the radio will accept nearly three times legal deviation and nothing downstream stops it**:
the limiter and the deviation scaler are both upstream of T13. Over-deviation is entirely this
board's responsibility.

### Design

```
  codec                          Rt 3k3                          pin 6
  line out          C3            ____                C4       AUD_TAP_IN
      o------------||------------|____|----+-----+----||----------o
                  10u                      |     |    1u       100k, +1.5V bias
                                          [ ] Rb === C5
                                           |  1k  |   1n
  codec gnd o------------------------------+-----+----------------o pin 3 AGND
```

Rb and C5 are parallel shunt legs on the codec side of C4, so neither ever loads the tap's 1.5 V
bias; put the shunt on the radio side and it drags the bias to ground.

**Rt/Rb: a full-scale digital sample produces exactly 100 % of class deviation**, 2.5 kHz on a
12.5 kHz channel, **0.725 Vp-p DERIVED**. The CM108B's line output is **0.995 Vrms, 2.81 Vp-p** full
scale (**DATASHEET**, into 10 k; its source is about 40 ohm, so the divider's 4 k costs nothing), and
the CM108AH on the widget measured 1.00 Vrms, so 1 Vrms is the family figure. The ratio is 0.258,
**-11.8 dB**, Rt = 2k9 exactly with Rb = 1k; 3k0 is the nearest E24 and gives **2.40 kHz on a
typical radio**.

**But fit 3k3, unless you will null that radio.** T13 is 0.78 / **0.87** / 0.96 Vp-p, **+/-0.9 dB**
radio to radio on the one figure the ceiling depends on, and 3k0 gives **2.68 kHz, 7 % over, on a
radio at the sensitive end**. 3k3 gives 2.24 kHz on a typical radio and 2.49 kHz on the worst one:
legal everywhere, for 0.6 dB on a typical radio, and the Bessel null in Verification takes that
back by stepping down to 3k0 where it says so. Same footprint, so a fitting decision. The widget
build reached the same conclusion from a measured 1.00 Vrms and is fitted with 3k3. 1 % resistors
are not the uncertainty that matters here; the radio is.

**Playback volume is part of the ceiling.** The DAC volume defaults to **-10 dB** at power-up
(**DATASHEET**) and the divider is sized for 0 dB. Set the initial DAC volume to 0 dB in the EEPROM
(register 0x29) so the board boots at the ceiling and the host cannot leave it 10 dB short by doing
nothing; and, as on the widget, calibrate at maximum playback volume and leave it there.

**Not 90 %.** A margin below the legal ceiling looks prudent and costs the 3 to 4 dB measured
above. Setting the ceiling AT the limit means a software fault produces exactly 100 % modulation,
which is legal, while normal operation gets the full budget. (3k3 is not that margin: it is the
worst-case radio's 100 %, and the null takes it back on a typical one.)

**This is the board's only protection against splattering the channel, and it belongs in copper.**
A number in software can be changed by a bug, a mixer or a well-meaning operator; a divider cannot.

**Thevenin impedance 3k3 || 1k = 770 ohm DERIVED.** Keep it under about 1 kohm: under 1 % loading
into the tap's 100 kohm, and a low source impedance keeps connector capacitance from eating the top
of the band. **C4** preserves the tap's 1.5 V bias as Tait require; 1 uF into 100 kohm is
**1.6 Hz DERIVED**, below the tap's own 3.7 Hz high pass, so the radio stays the limiting element.
**C3** at 10 uF into the divider's 4 k is 4 Hz. **C5** at 1 nF against 770 ohm is 207 kHz.

## Serial

**DATASHEET**, p.28. IOP_TXD on pin 18, IOP_RXD on pin 17, both at 3V3 levels, so no level
shifter. **But the polarity is inverted, and this note previously got that wrong.** Tait put it on
its own page as a notice to all integrators (section 1.2, p.11): "The serial lines in all Tait
radios are negative logic. This means that a logic high is 0V and a logic low is 3V3, which is the
same polarity as RS232, and is opposite to TTL/CMOS. No negative voltage is provided on these
lines." A USB-UART bridge's TTL-level TXD and RXD therefore need **an inverter in each direction**
between the bridge and pins 17 and 18. Wired straight through, both directions are dead and every
byte is framing errors, which is easy to misdiagnose as a baud-rate or wiring fault.

Two ways to invert. FTDI's FT230X and FT232R can invert TXD and RXD in their configuration EEPROM,
so no gates are needed; a CP2102N cannot, and wants a dual XOR (74LVC2G86) or two inverters
(74LVC2G04), which is what the reference board fits, XORed against a strap so the same board can
talk to a plain TTL device on the bench. Idle for negative logic is **0 V**, so an absent radio's
lines are held with a pull-down, not a pull-up. The same notice says IOP_TXD's 0 to 3V3 swing
drives most RS-232 receivers and IOP_RXD accepts full RS-232 levels, so an RS-232-level bridge also
works with no inverter over a short cable, and caps these lines at **3.0 m**.

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

**A hardware transmit timeout** (a monostable or watchdog that drops PTT after a few seconds
regardless of USB) is cheap and would make a wedged host harmless to everyone else on the channel;
the reference board does not have one. See Open items.

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

**Bus powered**, and for thermal reasons as much as electrical ones. A hub, a codec and a bridge is
on the order of 100 to 150 mA, inside one upstream port's budget. 13V8_SW on pin 1 is generous
(**1 A continuous, 2 A peak for under a second**, p.28) but a linear regulator dropping it to 5 V
at 150 mA is **1.3 W DERIVED** inside a sealed lid already carrying a PA, and Tait ask that an
options board "keep heat dissipation to a minimum" (p.102). Bus power also means the board is alive
whenever the host is, regardless of the radio; powering from 13V8_SW would make the USB devices
appear and disappear with the radio, which some hosts handle less gracefully.

## Mechanical

The lid gives an internal board a **139 x 99 mm** envelope with nine 3.5 mm screw points, and
**height binds before area**: zones from 10.7 mm down to 6.7 mm (Figure 4.2, p.99), no parts on the
bottom side, through-hole legs no more than 2 mm proud. The reference board takes four of the nine
holes on a 57 x 50 mm outline and a USB-C receptacle a few millimetres tall, so neither constraint
bites; the Micro-MaTch socket at 8.2 mm is the tallest thing on it. Using the external options hole
or the 7.5 mm round hole beside it for the cable **costs the radio its IP54 rating** (p.102), and
sealing is the integrator's problem. Bond the board to the lid at the mounting hole nearest the
cable entry: Tait ask for a **3.5 mm plated hole with a 7 mm pad on both sides**, resist cleared,
on the ground plane.

## Interference

The receiver makes 12 dB SINAD at -121 dBm (MMA-00072-03 p.14) and this board sits inside the same
casting. USB clocks are on 12 MHz multiples, so 12, 24 and 48 MHz all have a harmonic at exactly
**144.000 MHz**; at 144.950 that is 950 kHz outside a 7.8 kHz channel filter and can be discounted.
What cannot be predicted from a desk is broadband noise from switching edges raising the floor,
which is why the sensitivity check in Verification is done with the board **streaming**, not idle.

Two things Tait ask for that are easy to get wrong (p.103 to 106). **Filter the lines to the radio
on the board, at the connector**, because a hub counts as the "high-speed digital circuits" that
are their stated exception to "filtering is usually not necessary": 1 nF on the audio pairs, 10 nF
on PTT, and 470 pF on serial (a 10 nF part rounds 28800 baud badly), all **DERIVED** to sit far
above the audio band against the dividers' 770 ohm and 1.1 kohm. And **use both ground pins**, AGND
on 3 and DGND on 16, joined inside the radio; two returns halve the impedance of the earth at the
board end, and on a high-speed board they end up common on the ground plane anyway. The rest of
their EMC chapter (common-mode choke and pi filter at the USB entry, ESD arrays, four layers with an
unbroken ground plane) is what the reference board implements and is not repeated here.

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
-80 dBm so no AGC is acting, and confirm 1.2 Vp-p at pin 2 with the divider disconnected; through
the fitted 4.9k divider the same signal reads about 1 dB less. Then sweep the modulation
frequency and compare against the R1 plot in Table 2.10 - if it rolls off at 3 kHz, the tap is not
R1 and something is programmed differently from what you think.

**Full-scale ceiling.** Play 0 dBFS and confirm deviation lands on the class ceiling and does not
exceed it. This is the one test that protects everyone else on the channel, so do it before the
first transmit on a real antenna, and it is the test that decides between 3k3 and 3k0.

**Turnaround.** Key the radio from the PTT line with a tone playing and watch the RF envelope and
the demodulated tone on an SDR. Full power carrying valid modulation should arrive 14.8 ms after the
edge. A figure much longer than that usually means debounce was left programmed on the PTT input,
which is a one-field fix and otherwise silently costs every burst.

**Loop-back before the radio.** Tait's own commissioning sequence starts by joining baseband out to
baseband in and passing traffic with the radio disconnected (p.113). Doing the same with a jumper
between the codec's input and output divider proves the host, the driver and the modem before any
of it is entangled with a transmitter.

## Codec settings that belong in the EEPROM

The CM108B takes a 93C46 (the reference board fits one, with a Tag-Connect header to program it),
and four things in it turn "the host must set this" into "the board boots this way":

| Register | Setting | Why |
|---|---|---|
| 0x2B bit 3 | MIC BOOST **off** | the receive divider is sized for the unboosted 2.88 Vp-p input; boosted, a 60 % peer clips |
| 0x29 bits 8:3 | ADC initial volume **0 dB** | default is +8 dB; the divider is sized for 0 |
| 0x29 bits 15:9 | DAC initial volume **0 dB** | default is -10 dB; the transmit ceiling is set at 0 |
| descriptors | USB serial number and product string | stable `/dev` and ALSA naming across several radios |

The host should still set the same values at start-up, as the widget note requires, because a
mixer can be moved; the EEPROM only guarantees where it starts.

## Open items

Most of the original list closed when the reference board and the CM108B datasheet arrived:

- **Codec full scale and input impedance: closed.** Line out 0.995 Vrms into 10 k, MICIN 10 kohm
  and 2.88 Vp-p full scale at 0 dB with boost off, both **DATASHEET**; the CM108AH on the widget
  measured within a decibel of both. What remains is the radio's own +/-0.9 dB at T13, which is
  what the Bessel null is for.
- **USB socket: closed.** The reference board takes a USB-C receptacle, a few millimetres tall.
- **Configuration EEPROM: closed.** The CM108B supports a 93C46 and the reference board fits one.
- **Bus-powered hub per-port advertisement.** The reference board runs bus-powered with the CM108B
  strapped for 100 mA; not independently checked here.
- **Fit against a real radio.** The reference board uses a 57 x 50 mm outline on four of the nine
  screw points rather than the full 139 x 99 mm envelope; still to be proven in a radio body.
- **A hardware transmit timeout** is still only a suggestion, and the reference board does not
  have one.
