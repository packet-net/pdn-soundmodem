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
- Pin 1 (DATA IN) takes three components. From OUT, Rt in series. From Rt's far end, Rb down to the ground wire. From that same junction, C4 to pin 1. **C4 must be the last element before the pin**, and that matters more here than it did on the Tait build: Rb is 100 ohm, so if DATA IN carries any DC bias at all, a DC-coupled Rb is a short across it. Yaesu do not say whether it does, which is exactly why the capacitor goes there and not somewhere more convenient.
- Pin 5 (DATA OUT) takes four components. From pin 5, C1 first. Then Rs in series. From Rs's far end, Rp and C3 down to the ground wire. That junction wires to IN.
- Pins 4 and 6 and the SQL pad get nothing.

All seven fit free-standing in the tail with heatshrink over each leg and the lot, or on a fingernail of stripboard in the plug's backshell.

Mind the direction. The board is the TNC here: OUT drives DATA IN on pin 1, IN listens on DATA OUT on pin 5. This is the opposite way round from the bench loop in [ninotnc-loop.md](../dev/bench/ninotnc-loop.md), where the board played the radio, and copying that table into this build swaps transmit and receive.

### About the connector

Six-pin mini-DIN is unpleasant to solder and easy to mis-number. Buy a moulded lead and cut it rather than soldering a bare plug, and identify the conductors with a meter against the DATA JACK diagram in the radio's own manual rather than by colour, because cable colour codes are not standard between makers. Pin 2 is the only ground on the jack, so it is the one to find first and the one every shunt leg returns to.

Keep the tail short and screened, and land the screen on pin 2 at the radio end only.

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
| plug | 6-way mini-DIN, male, screened, on a moulded lead | | | |

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

Three defences, in the order they are worth fitting.

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
