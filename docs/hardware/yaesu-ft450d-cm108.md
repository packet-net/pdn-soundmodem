# Wiring a CM108 interface to a Yaesu FT-450D

What to build to connect the single-sided CM108 interface board at [tomwardill/cm108radiowidget](https://github.com/tomwardill/cm108radiowidget) to an FT-450D's rear-panel DATA jack, for headless HF packet with pdn-soundmodem. When you finish you will have a lead from the board to the radio's 6-pin mini-DIN socket, the radio in its data mode, and the card's levels set.

**Status: computed, not built.** Every radio-side figure here is Yaesu's own, from the FT-450D operation manual; every interface-side figure is the CM108 board measured on 2026-08-14 for the [TM8100 build](tait-tm8100-cm108.md). Nothing on this page has been on a bench or on the air. The arithmetic and the one evening of measurement that would close it are in the [extended notes](../dev/hardware/ft450d-cm108-interface-notes.md). Treat the resistors as a starting point good to about a decibel, and set the transmit level against the radio's ALC meter rather than against the table.

**There is one variant, and that is the main way this job is easier than the Tait one.** Yaesu publish a single level in each direction at the DATA jack. There is no tap to choose, no channel spacing to scale by, and no codeplug. The two resistor pairs below are the whole design.

## What the radio provides

A 6-pin mini-DIN on the rear panel. Pin functions and both levels are from the FT-450D operation manual, p.75.

| Pin | Signal | What it is |
|---|---|---|
| 1 | DATA IN | transmit audio in. **60 mVp-p** for full modulation, 600 ohm input impedance |
| 2 | GND | the only ground on the jack |
| 3 | DATA PTT | ground to transmit |
| 4 | FSK IN | direct RTTY keying. Not used here |
| 5 | DATA OUT | receive audio out. **Fixed 500 mVp-p**, 600 ohm output impedance |
| 6 | SQL OUT | squelch status. Not used here |

Three sentences from that page decide everything else.

**"There is no adjustment of the DATA input level and DATA output level of the DATA jack on the rear panel; please make any needed level adjustments at the TU side."** Both levels are ours to set, in this cable and in the card's mixer. No front-panel control and no menu item will help, and MIC GAIN does not reach the DATA jack.

**DATA OUT is "Fixed level, does not respond to setting of [AF GAIN] or [SQL/RF GAIN] knob."** So once the receive level is right it stays right, and a knob nudged on the front panel cannot undo it. That is the opposite of a microphone-and-speaker interface and it is worth the jack on its own.

**"During Packet operation via the rear panel's DATA jack, the front panel MIC jack is cut off."** The live-microphone fault that bites on an FM mobile cannot happen here.

Pin 4 is FSK IN rather than a no-connect, whatever a pinout card may say. The FT-450D's RTTY mode is a true FSK mode keyed on that pin, so audio sent to pin 1 never reaches it. Leave it unwired.

## What the interface provides

The same board as the Tait build, so the same five pads. There is no connector, only a row of labelled solder pads, and the audio names are from the board's point of view rather than the radio's.

| Pad | What it is on the board |
|---|---|
| OUT | CM108AH line out, through 1 uF on board: transmit audio |
| IN | CM108AH microphone in, through 1 uF on board: receive audio. No bias network is fitted |
| PTT | open-drain BSS138; pulls to ground while CM108 GPIO3 is high |
| SQL | active-low input; pulling it low presses the CM108's volume-down key through a Schottky |
| GND | board ground |

## The build: pads to a 6-pin mini-DIN plug

Seven components in a short tail, the same count as the Tait assembly.

```
  board pads                                                  DATA jack, 6-pin mini-DIN

  OUT o-----[  Rt 4k7  ]----+------------| |---------------------o 1  DATA IN
                            |          C4 10u
                           [ ] Rb 100R
                            |
  GND o---------------------+--------+------------+-------------o 2  GND
                                     |            |
                                    [ ] Rp 1k    === C3 10n
                                     |            |
  IN  o------------------------------+------------+--[ Rs 220R ]--| |--o 5  DATA OUT
                                                                  C1 4u7

  PTT o------------------------------------------------------------o 3  DATA PTT

  SQL o--x  leave unconnected                    pins 4 and 6: empty
```

Pin by pin, from the iron's point of view:

- Pin 2 (GND) takes a plain wire to the GND pad. It is also the return for every shunt component: Rb, Rp and C3 all land on it.
- Pin 3 (DATA PTT) takes a plain wire to the PTT pad, with no components. The board already has the open-drain stage and the radio end has the pull-up.
- Pin 1 (DATA IN) takes three components. From OUT, Rt in series. From Rt's far end, Rb down to the ground wire. From that same junction, C4 to pin 1. **C4 must be the last element before the pin**, which is an ordering rule and not a position: with the components at the board end, C4 sits between the Rt/Rb junction and the cable core, and the core runs on to pin 1 with no DC reference, which is what is wanted. It matters more here than it did on the Tait build: Rb is 100 ohm, so if DATA IN carries any DC bias at all, a DC-coupled Rb is a short across it. Yaesu do not say whether it does, which is exactly why the capacitor goes there and not somewhere more convenient.
- Pin 5 (DATA OUT) takes four components. From pin 5, C1 first. Then Rs in series. From Rs's far end, Rp and C3 down to the ground wire. That junction wires to IN.
- Pins 4 and 6 and the SQL pad get nothing.

### How it goes together

Three segments, with the seven components on a scrap of protoboard between the second and the third.

```
  PC  --USB lead--  CM108 dongle  --5 cm wires--  protoboard  --0.3 m pigtail--  radio
                    PCB                           7 parts     screened, moulded
                                                              6-pin mini-DIN,
                                                              bare ends this end
```

That arrangement is better than it looks, and it is worth knowing why before rearranging it. **The unscreened segment is the one carrying the high level, and the screened segment is the one carrying the low level**, which is the right way round in both directions.

- **Transmit.** The 5 cm carries the CM108's full 2.828 Vp-p. Anything induced into it is attenuated by the divider, about 34 dB, along with the wanted signal, so 5 cm of unscreened wire there costs about 34 dB less than 5 cm anywhere downstream. The pigtail then carries about 51 mVp-p, which is small, and it is screened.
- **Receive.** The pigtail carries the radio's full 500 mVp-p from 600 ohm. The 5 cm carries 97 mV from a 451 ohm source into MICIN. The receive divider is shallow, about 5 dB, so there is no large asymmetry to exploit here; the argument is only that 5 cm of unscreened wire beats 30 cm of it.
- **And the components in the middle are what keeps the radio's cable away from the codec.** RF arriving up the pigtail meets Rb, 100 ohm to ground, and then 4k7 in series before it can reach the line output. On the other path it meets C3 and then Rs. A divider at either end gives up one of those two properties; in the middle you get both.

So build it as drawn, and keep the 5 cm at 5 cm. **No component value changes for this form factor.** The pigtail's 30-odd picofarads put the transmit pole at 63 MHz and the receive pole at 13 MHz, and the 5 cm wires are a few picofarads.

Twist each 5 cm signal wire with a ground wire back to the dongle's GND pad, or twist all three loosely around one. At 5 cm it barely matters and it costs nothing.

Mind the direction. The board is the TNC here: OUT drives DATA IN on pin 1, IN listens on DATA OUT on pin 5. This is the opposite way round from the bench loop in [ninotnc-loop.md](../dev/bench/ninotnc-loop.md), where the board played the radio, and copying that table into this build swaps transmit and receive.

### About the connector

A moulded pigtail is the right way to buy this: six-pin mini-DIN is unpleasant to solder and easy to mis-number. Identify the bare ends with a meter against the DATA JACK diagram in the radio's own manual rather than by colour, because cable colour codes are not standard between makers. Pin 2 is the only ground on the jack, so it is the one to find first and the one every shunt leg returns to.

**Buzz two more things out of the pigtail before you trust it**, because both vary between makers and neither is visible:

- **Does pin 2 have a core of its own, or is the braid doing that job?** A lead with six cores and a separate drain is what you want. A lead that saves a core by using the braid as pin 2 leaves the screen carrying signal return, which is the one arrangement [Grounding and the screen](#grounding-and-the-screen) is written to avoid. Over 0.3 m with everything else short it is a tolerable compromise, but know which one you have, because it is the first suspect if hum appears.
- **Is the braid bonded to the mini-DIN shell inside the moulding?** Often it is, sometimes it is not, and the radio has already tied that shell to pin 2 and to its own chassis. If it is not bonded, the screen is landed at one end only whatever you do at the protoboard.

### Grounding and the screen

Buzz the radio out before you build and you will find the DATA jack's pin 2, the mini-DIN's metal shell and the rig's chassis are all one node. The board is the same story at its end: the [netlist](../dev/hardware/cm108-widget-netlist.md) puts the GND pad and the micro-USB connector's **GND pin** on the same net, and leaves the micro-USB **shell** on a net of its own, connected to nothing. So there are two decisions and only one of them is open.

**There is one ground node in this assembly, and it lives on the protoboard.** Five things meet at a single point there, and nothing daisy-chains:

1. the pigtail's pin 2 core, out to the radio;
2. the pigtail's braid;
3. a wire back to the dongle's GND pad, 5 cm;
4. Rb, the transmit shunt;
5. Rp and C3, the receive shunts.

**The wire to the GND pad is not a choice.** It is the return for both audio paths and for PTT, and there is no circuit without it. The moment it is fitted the rig's chassis and the host's ground are bonded, because the GND pad is the USB ground pin is the host.

**The screen is a choice, and the answer here is both ends.** At the radio end the moulding has usually done it for you, on to a shell the radio has already tied to pin 2 and to its chassis; buzz it, as above. At the other end, land the braid on the protoboard's star point. The usual audio-interconnect rule is the opposite, one end only, and it does not apply here:

- **The loop it avoids already exists.** Chassis, pin 2, the GND wire, the GND pad, the USB ground pin, the host, its mains earth, back round to the rig's supply. Floating one end of the screen does not break that loop, so it buys nothing at mains frequencies.
- **Above a few hundred kilohertz a screen earthed at one end is not a screen**, it is a wire with a free end. At an HF station that is the failure it was fitted to prevent. Bonded at both ends it carries common-mode current around the conductors instead of letting that current develop a voltage across them.

**The screen is never the return.** The pigtail wants a core for pin 2 and a braid that carries no signal current. A screen doing double duty as the return puts every millivolt of common-mode noise on it directly in series with the audio, and that, rather than the mains loop, is the real argument against bonding both ends. Giving the return its own conductor is what disposes of it.

**Bond the rig to the station earth with something heavier than this cable.** A moulded mini-DIN lead's ground conductor is a thin wire, and once fitted it is one of the bonds between the rig's chassis and the host's. It should never be the main one. A short braid strap from the rig's GND terminal to the station earth leaves the data cable's ground carrying only the signal return it was sized for.

**One point, not three.** PTT's switching current shares the ground wire with both audio returns, so the five legs above meet at a point and go their separate ways from there. Tapping the ground wire in three places along the protoboard puts that current in series with the audio instead. It is a few millivolts either way and it costs nothing to get right.

**Leave the micro-USB shell as it is.** It is isolated from board ground by design, and on a bare board there is nothing better to connect it to, because the board has no chassis and its only ground is the codec's.

**The upgrade, if RF bites: put the dongle and the protoboard in one small metal box.** That is the real answer to a bare board and 5 cm of unscreened wire sitting a few feet from an HF antenna, and it tidies up three loose ends at once. The box becomes the screen the 5 cm run does not have. The pigtail's braid lands on the box wall where it enters, which is a better termination than a pad. And the USB lead's braid lands on the box wall at the other end, which bonds the micro-USB shell's job to ground properly rather than through the 10 nF bodge that would otherwise be the thing to try. Bond the box to the star point at one place.

**If you fit the isolation transformers**, all of this is replaced by the simple case. The CM108's ground is then not connected to the rig at all, so land the screen at the radio end only and leave the board end floating: at that point there is finally a loop worth not closing.

## Parts

| Ref | Value | Type | Tolerance | Rating |
|---|---|---|---|---|
| Rt | **4k7** | metal film | 1% | 0.125 W |
| Rb | **100R** | metal film | 1% | 0.125 W |
| Rs | **220R** | metal film | 1% | 0.125 W |
| Rp | **1k** | metal film | 1% | 0.125 W |
| C4 | **10u** | non-polarised: bipolar electrolytic or film; X7R only at 25 V rating or more | 20% | >= 16 V |
| C1 | **4u7** | non-polarised, as C4 | 20% | >= 16 V |
| C3 | **10n** | ceramic, C0G preferred, X7R acceptable | 20% | 50 V |
| pigtail | 0.3 m moulded 6-way mini-DIN, male, screened, bare ends; a core for pin 2 and a braid that is not it | | | |
| protoboard | a scrap, and 5 cm of wire to the dongle's pads | | | |
| box | small metal enclosure for the dongle and the protoboard together. Optional, and the answer if RF bites | | | |

Tolerance is looser here than on the Tait build, and for a reason worth knowing: there the transmit divider set a hard deviation ceiling with only half a decibel of margin, so 5% parts could breach it unaided. Here the transmit level is set on the ALC meter against the radio's own indication, so a resistor that is half a decibel off is absorbed by the mixer in the first five minutes. 1% metal film is specified because it costs nothing and holds its value in a warm shack, not because the design needs it.

C1 and C4 must not be low-voltage class 2 ceramics. The reasoning from the [Tait parts note](../dev/hardware/tm8100-cm108-interface-notes.md#parts) applies unchanged: a radial Y5V or Z5U at these values sheds much of its capacitance under bias and walks its corner up towards the passband.

## Program the radio

FT-450D menu items are chosen by name, not by number: hold [F] for a second, turn [DSP/SEL] to the item, press [DSP/SEL], turn to the value, press again, then hold [F] to leave.

| Menu item | Set to | Default |
|---|---|---|
| `D TYPE` (DATA MODE) | **USER-U** | RTTY |
| `D DISP` (DATA DISP) | **0** | 0 |
| `DIG VOX` | **OFF** | OFF |
| `RFPOWER` (RF PWR SET) | **30** to start | 100 |

Then select the data mode on the front panel with the [MODE] buttons. **The rear DATA jack only carries audio in the data mode**, so a radio left in plain USB is a radio that decodes nothing and transmits nothing, with no other symptom.

`D TYPE` is USER-U because HF data work is upper sideband nearly everywhere, and [08-hf.md](../08-hf.md) assumes it. USER-L exists for the few places it is not.

`D DISP` is the one that will silently ruin a band plan. It offsets the *displayed* frequency during data mode by up to 3 kHz either way, and pdn-soundmodem prints a dial to set on the assumption that the display means what it says:

```
dial: 7.049450 MHz USB - set your radio to this
  modem 0 afsk300-il2pc at 7.050300 MHz = 850 Hz audio
```

Leave `D DISP` at 0 and let the modem do the offset arithmetic, which it shows you. A non-zero value moves every modem in the plan together and nothing in the journal can see it.

`DIG VOX` off, because this interface keys on pin 3. VOX keying on top of a hardware PTT line gives two things a claim on the transmitter, and VOX keys on whatever the card emits between frames.

`RFPOWER` because Yaesu's own advice is to reduce to 1/2 or 1/3 of maximum for data transmissions longer than a few minutes, which is 30 to 50 W here. A packet node transmits far more than a few minutes a day. Start at 30 W and raise it only if a link needs it.

One operating setting that is not about the cable but decides whether the modem decodes: leave AGC on and slow or medium rather than fast. A fast AGC pumps on every strong signal in the passband and amplitude-modulates the one you are trying to decode, which reads as a marginal path.

## Software settings

| Setting | Value |
|---|---|
| Capture (`Mic`) | **+9.00 dB** |
| Playback (`Speaker`) | **start low**, then set it on the two-tone test |
| `Auto Gain Control` | **off** |
| `Mic` playback (sidetone) | muted |
| Device | `plughw:CARD=Device,DEV=0`, not `default` |

```json
{ "alsa": { "mixer": { "captureGainDb": 9, "playbackDb": -20 } } }
```

+9.00 dB puts Yaesu's nominal 500 mVp-p at -12.4 dBFS, which is this modem's design target for a receive path and leaves 12 dB for an AGC transient. Confirm the journal agrees:

```
alsa: mixer: Mic capture 9.00 dB of -12.00 to 23.00 dB (set 9.00 dB, config), Auto Gain Control off (forced), Speaker playback -20.00 dB of -36.00 to 0.00 dB (set -20.00 dB, config)
```

Rs and the capture gain are one choice, not two. Changing one means changing the other:

| Rs | Capture gain | Lands at | Load on the radio |
|---|---|---|---|
| 0R (a link) | +8 dB | -12.3 dBFS | 1.0k |
| **220R** | **+9 dB** | **-12.4 dBFS** | **1.2k** |
| 1k0 | +13 dB | -11.5 dBFS | 2.0k |
| 1k8 | +15 dB | -11.8 dBFS | 2.8k |
| 3k3 | +18 dB | -12.0 dBFS | 4.3k |

**A warning about this particular card**, carried over from the Tait page because it still applies: the CM108's `Mic` control advertises -12.00 to +23.00 dB and **nothing below 0.00 dB does anything at all**. 0 dB is the floor. If band noise still clips there, the fix is a larger Rs, not a mixer setting. The playback control is the other way about: its -36.00 to 0.00 dB range works, which is what makes the transmit level a software adjustment.

The rest of the configuration:

- PTT: `"ptt": { "type": "cm108", "device": "/dev/hidrawN" }`, described under [`ptt`](../reference/config.md#ptt). GPIO defaults to 3, which is what the board uses. Find the node the way [03-radios-and-interfaces.md](../03-radios-and-interfaces.md#the-gpio-pin-on-a-cm108-interface) shows, and add the udev rule from [02-first-station.md](../02-first-station.md#choose-the-ptt-line), or the service cannot open it.
- TXDELAY: **start at 50 ms**. Yaesu publish no PTT-to-modulation figure for the FT-450D, so unlike the Tait build there is no number to size it from, and a solid-state HF radio's transmit-receive changeover is tens of milliseconds. Bring it down once you have measured it on a monitor receiver. Your node or APRS software sets TXDELAY over KISS ([commands](../reference/ports-and-endpoints.md#commands)); with nothing attached, `--txdelay` sets it for a bench run.
- SQL stays unconnected at both ends. The DATA jack's pin 6 is a squelch status line for an FM receiver's benefit and means nothing on SSB, and pdn-soundmodem has its own DCD.

## Which modes this serves

Everything in the [HF SSB families](../05-modes.md): the `afsk300` group, the BPSK and QPSK modes, `freedv-datac*`, the `ms110d-*` serial-tone modes and `ardop`. `fsk9600` and the `c4fsk*` family cannot use it, and not because of this cable: they are baseband modes carrying data down to DC, and an SSB transmitter has no way to send that. `qpsk3600` is an FM mode. Use the [Tait build](tait-tm8100-cm108.md) for those.

## Before you trust it on air

There is no Bessel null here. A null is an absolute deviation reference and deviation is an FM quantity; on SSB the calibration is the two-tone test, and the radio's own ALC meter is the instrument.

**Set the meter to ALC first.** [METER/DIM] cycles the transmit meter PO, ALC, SWR. On PO you will see power and learn nothing about whether the audio is too hot.

Then, into a dummy load:

1. **Receive first, and check band noise before trusting the capture figure.** Open the squelch, point the radio at a quiet part of the band and record a few seconds with `arecord`, then look at the peak. The number to beat is clipping: if band noise clips, the modem is being fed a clipped signal the whole time it is listening, which is what an OFDM waveform least tolerates. On HF the AGC decides this, so check it again on a busy evening as well as a quiet afternoon.
2. **Then transmit.** Send two tones from the station page or with `--two-tone 5`, with playback well down, and bring the playback gain up until the ALC meter just begins to move. Back off until it does not. That point is the operating level; [04-levels.md](../04-levels.md#send-a-transmit-test) is the procedure in full.
3. **Look at the pair on a monitor receiver or an SDR.** The third-order products land either side of the two tones where nothing else is, so they are easy to see. Turn the level down until they stop falling; below that point you are giving away signal for nothing.

**If the two-tone test runs out of level at `playbackDb` 0**, the divider is too deep and the fix is a smaller Rt: 3k9 buys 1.6 dB and 3k3 buys 3.0 dB. That should not happen, because at `playbackDb` 0 the modem's default 0.8 amplitude already delivers 68 to 79% of Yaesu's full-modulation figure, but it is the direction the arithmetic is least sure of and it is one resistor.

**Expect to end up well down the playback range**, somewhere below -10 dB, and do not read that as a fault. A 100 W SSB radio driven to no-ALC on a high crest factor waveform wants a small fraction of its full-modulation input, and the card's playback control is where that belongs.

## Board modifications

None are required; the board is right for this job as it stands. Do not add a microphone bias network, whose absence is what keeps the receive divider clean. Do not add an external PTT transistor: the BSS138 on board is that transistor, and doubling it would invert the logic.

Do fit 100k from Q1's gate to ground, tacked across the BSS138 gate and source legs. Any type, 5%, anything from 47k to 220k. The gate net holds only the gate and the CM108's GPIO3, so until the driver configures that pin the gate floats, and a floating BSS138 gate can sit above threshold. The failure mode is the transmitter keying itself when the board is plugged in, and on 100 W of HF that is worse than it is on a mobile.

## RF on the cable

This section has no counterpart on the Tait page, because a 25 W mobile on VHF does not do this and a 100 W HF station does. A USB lead, a sound card and a wire to the radio, a few feet from an antenna carrying 100 W, is the classic arrangement for common-mode RF to find its way into the interface. The symptoms read as software faults: PTT that latches on and will not release, the card falling off the USB bus mid-transmission, the modem hearing its own transmission, the host locking up on one band and not another.

Four defences, in the order they are worth fitting.

- **The screen bonded at both ends and a proper earth strap to the rig**, as [Grounding and the screen](#grounding-and-the-screen) sets out. Free, and the one a station is likeliest to have got wrong.
- **Ferrites and short leads.** A clip-on mix 31 sleeve at each end of the USB lead and one on the DATA tail, several turns through each where the lead will take it. This is cheap and fixes most of it.
- **A common-mode choke on the feedline**, which is an antenna-system problem rather than an interface one, but it is the root cause more often than the cable is.
- **Transformer isolation, which is nearly free here.** SSB data lives between 300 and 2700 Hz, and a 600:600 telecoms transformer is flat across exactly that window. On the Tait build isolation cost low-frequency response that the 9600 baud modes needed; here there is nothing below 300 Hz to lose. One transformer after C1 on receive and one after C4 on transmit, with the PTT taken to an opto-isolator, breaks every galvanic path between the radio and the host. If you are building this from scratch rather than retrofitting, plan for it.

Whatever you fit, test it at full power on every band you intend to use, keyed into a dummy load first and then into the antenna. RF ingress is frequency-dependent and a station that is clean on 40 m can be unusable on 10 m.

## Related

- [03-radios-and-interfaces.md](../03-radios-and-interfaces.md) for the `device` string and the PTT block this assembly needs.
- [04-levels.md](../04-levels.md) for setting receive and transmit levels once it is built.
- [08-hf.md](../08-hf.md) for placing modems in the passband and reading the dial line.
- [Extended notes](../dev/hardware/ft450d-cm108-interface-notes.md) for the arithmetic, what is assumed, and the measurements that would confirm it.
- [tait-tm8100-cm108.md](tait-tm8100-cm108.md) for the FM side of the same board, and the [netlist](../dev/hardware/cm108-widget-netlist.md) for what the board does.
