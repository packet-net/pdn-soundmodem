# FT-450D to CM108: extended notes

Status: design as of 2026-09-22. **Computed, not built and not measured.** Describes the levels, dividers and assumptions behind wiring a Yaesu FT-450D's DATA jack to the CM108-class board at [tomwardill/cm108radiowidget](https://github.com/tomwardill/cm108radiowidget), for headless HF packet with pdn-soundmodem. The build instructions a builder follows are the user page at [docs/hardware/yaesu-ft450d-cm108.md](../../hardware/yaesu-ft450d-cm108.md); this note is the reasoning behind them and the list of what still has to be put on a bench.

The companion note for the FM half of the same board is [tm8100-cm108-interface-notes.md](tm8100-cm108-interface-notes.md), and much of the CM108 side is taken from it rather than restated. Where a figure here is marked **MEASURED** it comes from that note's 2026-08-14 session on this board type; **YAESU** means the FT-450D operation manual; **DERIVED** means arithmetic in this file; **CHOICE** means a decision with its reason beside it.

## How this job differs from the Tait one

Worth stating up front, because the two pages look alike and the engineering is not.

- **There is one variant.** Yaesu publish one input level and one output level at the DATA jack. There is no tap to select, no channel spacing to scale by, no codeplug and no `MMA-00011-01` to read. The design is two resistor pairs.
- **The levels are ours by Yaesu's own instruction**, not by inference: "There is no adjustment of the DATA input level and DATA output level of the DATA jack on the rear panel; please make any needed level adjustments at the TU side" (**YAESU**, p.75).
- **The transmit side is a load, not a bias node.** AUD_TAP_IN on a TM8100 is a 100 kohm input sitting at 1.5 V. DATA IN on an FT-450D is specified as 600 ohm, which is low enough that the divider's own output impedance is part of the answer. That, and not the ratio, is what sets the values below.
- **There is no hard ceiling to defend.** On FM the resistors set a deviation ceiling with half a decibel of margin, so 1% parts were mandatory. On SSB the ceiling is the ALC, the operator sets the level against the radio's own meter, and a resistor half a decibel out is absorbed in the first five minutes.
- **Nothing below 300 Hz matters.** An SSB transmitter cannot send it. Every coupling-capacitor argument in the Tait note, and the whole [end-to-end low-frequency corner](tm8100-cm108-interface-notes.md#measured-the-end-to-end-low-frequency-corner-and-what-it-costs-the-9600-baud-modes) problem that costs the 9600 baud modes their long frames, is moot here. It also makes transformer isolation nearly free, where on the Tait build it was a compromise.
- **RF ingress is a first-order concern.** 100 W a few feet from a USB lead is not the same problem as a 25 W mobile.

## The radio side

All **YAESU**, from the FT-450D operation manual, pp.8 and 75.

| Pin | Signal | Specification |
|---|---|---|
| 1 | DATA IN | 60 mVp-p for full modulation, 600 ohm input impedance |
| 2 | GND | the only ground on the jack |
| 3 | DATA PTT | ground to transmit |
| 4 | FSK IN | direct RTTY keying; no level adjustment in the radio |
| 5 | DATA OUT | fixed 500 mVp-p, 600 ohm output impedance, independent of [AF GAIN] and [SQL/RF GAIN] |
| 6 | SQL OUT | squelch status |

Three properties of that specification are load-bearing.

**DATA OUT is post-AGC and fixed.** It does not follow the volume control, which is the single best thing about this jack: a calibration made once stays made, and no front-panel knob can undo it. The price is that the AGC, not the signal, decides the level, so the level is roughly constant above the AGC threshold and falls away below it. What that means for the receive design is that the nominal figure behaves as a ceiling rather than as an operating point, and headroom above it is spent on AGC transients rather than on signal dynamics.

**"600 Ohms" is a nominal, and we should not trust it as a resistance.** Manufacturers quote 600 ohm at data jacks as a design impedance far more often than they present one, and Yaesu publish no measurement. Everything below is therefore built to be insensitive to it: the transmit divider's source impedance is low enough that the level moves only 1.3 dB between a 600 ohm load and an open circuit, and the receive path treats the 600 ohm source impedance as real because there the conservative reading is to assume the loss is there.

**The 500 mVp-p figure is ambiguous about loading**, in exactly the way Tait's tap level was until it was pinned. Open circuit and into a matched 600 ohm differ by 6 dB. This note assumes **open circuit**, which is the hotter reading and therefore the one that cannot leave the codec clipping; if the truth is the other one, the receive path is 6 dB light and 6 dB of capture gain fixes it, which is a config file edit. That asymmetry is why the assumption is made in this direction and not the other. **Check 2** below settles it in five minutes with a meter.

**The PTT line's electrical specification is not published.** A Yaesu data-jack PTT is a pull-up to a low-voltage rail that the external device grounds, and the BSS138 on the widget is an open drain rated 50 V and 200 mA, which covers anything such a jack can present by a wide margin. It is still worth a meter on pin 3 before connecting anything: open-circuit voltage to pin 2, and short-circuit current. Both should be small.

## The CM108 side

All **MEASURED** on this board type on 2026-08-14; see [Measurement 1](tm8100-cm108-interface-notes.md#measurement-1-what-the-cm108-input-and-output-full-scale-actually-are) for the method and the three ways the reading lies.

- **Output full scale at the OUT pad, open circuit: 1.00 Vrms to about +/-3%**, which is **2.828 Vp-p**, linear to +/-0.06 dB from 0 to -26 dBFS with no compression at full scale.
- **Input full scale: 455 mVrms at +8.00 dB capture gain**, with the capture control tracking its claimed dB to 0.32 dB worst case over a 25 dB span. Full scale scales with that control.
- **The capture control has no attenuation below 0.00 dB**, whatever it advertises. 0 dB is the floor and the only way further down is a larger Rs.
- **The playback control does work** across its -36.00 to 0.00 dB range, which is what makes the SSB transmit level a software adjustment rather than a resistor.
- From the [netlist](cm108-widget-netlist.md): transmit is LOL through **C8, 1 uF**, receive is MICIN through C9 1 uF with **no bias network** (VBIAS unconnected), and PTT is an open-drain BSS138 on GPIO3 **with no gate pull-down**.

C8 is the constraint that shapes the transmit divider, and it is easy to miss. It is on the board, we cannot change it, and it works against whatever input impedance our divider presents.

## Transmit path

```
  OUT pad                Rt                                          DATA jack pin 1
  1.00 Vrms FS        4k7                        C4                  60 mVp-p full mod
  behind C8 1u      _______                     10u                  600 ohm nominal
      o------------|_______|----+---------------||------------------o
                                |
                               [ ] Rb 100R
                                |
  GND o-------------------------+-----------------------------------o  pin 2
```

The attenuation wanted is **60 mVp-p from 2.828 Vp-p, which is -33.47 dB** **DERIVED**. That is 14 dB deeper than the Tait transmit divider, and deep dividers are where the source and load impedances stop being negligible.

### Why Rt = 4k7 and Rb = 100R

Two constraints pull in opposite directions, and the pair is where they cross.

**Pulling the impedance down: load insensitivity and noise.** The divider's Thevenin resistance is `Rt || Rb`. If that is small against the load, the delivered level barely depends on what the load actually is, which matters because "600 ohm" is a nominal we do not trust. It also means the cable carrying 60 mV of wanted signal is a low-impedance line, which is much harder for hum and RF to get into than a high-impedance one.

**Pulling it up: C8.** The board's 1 uF output capacitor works against the divider's input impedance, so a low-impedance divider walks a high-pass corner up towards the passband.

| Rt | Rb | Zth | Zin | C8 corner | at 300 Hz | 600R vs open |
|---|---|---|---|---|---|---|
| 1k0 | 22R | 21 ohm | 1021 | 156 Hz | **-1.04 dB** | 0.32 dB |
| 2k0 | 47R | 46 ohm | 2044 | 78 Hz | -0.32 dB | 0.64 dB |
| 2k7 | 68R | 66 ohm | 2761 | 58 Hz | -0.17 dB | 0.91 dB |
| 3k9 | 100R | 98 ohm | 3986 | 40 Hz | -0.08 dB | 1.31 dB |
| **4k7** | **100R** | **98 ohm** | **4786** | **33 Hz** | **-0.05 dB** | **1.31 dB** |
| 5k6 | 120R | 118 ohm | 5700 | 28 Hz | -0.04 dB | 1.55 dB |

All **DERIVED**. Below about 2k of input impedance the passband tilt becomes visible; above about 5k the Thevenin impedance stops being small against 600 ohm and the answer starts depending on a number we do not have. 4k7 with 100R sits in the flat part of both curves.

### What it delivers

| Load on pin 1 | Level at 0 dBFS | As a fraction of full modulation |
|---|---|---|
| 600 ohm as specified | 50.7 mVp-p | 84% |
| open circuit | 58.9 mVp-p | 98% |

**DERIVED**, and the useful thing about the pair is the spread: 1.3 dB across the entire range of what DATA IN might turn out to be, and **neither end exceeds Yaesu's figure**. Sizing so that the worst case is at or under the published ceiling is the safe direction on SSB, where the cost of too much drive is splatter on somebody else's frequency.

At the modem's default 0.8 amplitude (`txTest.amplitude`, and the same figure in the modulators) that becomes 40.5 to 47.1 mVp-p, or **68 to 79% of full modulation, at `playbackDb` 0**. So the whole useful adjustment range lives inside the card's playback control, with 2.1 dB of "up" unavailable on the open-circuit reading and 3.4 dB on the 600 ohm one. If the two-tone test ever runs out of level at 0 dB the fix is a smaller Rt: 3k9 buys 1.58 dB, 3k3 buys 3.00 dB.

### C4

C4 works against the divider's Thevenin impedance in series with the load, about 698 ohm on the 600 ohm reading.

| C4 | Corner | at 300 Hz |
|---|---|---|
| 1u | 228 Hz | -1.98 dB |
| 2u2 | 104 Hz | -0.49 dB |
| 4u7 | 48 Hz | -0.11 dB |
| **10u** | **23 Hz** | **-0.03 dB** |

**DERIVED**. This is the one place the low impedance costs a part: 1 uF, which is what the Tait build fits in the same position, would put a 228 Hz corner right at the bottom of the SSB passband. 10u, non-polarised.

**C4 is the last element before the pin** and that is not negotiable. Rb is 100 ohm. If DATA IN carries any DC bias, a DC-coupled Rb is a short across it, and Yaesu do not publish whether it does. The same rule on the Tait build protected a 1.5 V bias behind 100 kohm through a 1k5 shunt; here the shunt is fifteen times lower and the consequence correspondingly worse.

### Tolerance

The ratio moves 0.979% per 1% of error in either resistor, so 1% parts give 0.09 dB each way and 5% parts give 0.43 dB **DERIVED**. Neither matters, because the level is set on the ALC meter. 1% metal film is specified for its temperature coefficient and because it is what the Tait build already stocks, not because the arithmetic needs it.

## Receive path

```
  DATA jack pin 5           C1        Rs                            to IN pad
  500 mVp-p fixed          4u7      220R                            (MICIN, high Z,
  600 ohm source            ||     _______                           no bias network)
      o-------------------- || ---|_______|----+--------+-----------o
                                                |        |
                                               [ ] Rp   === C3
                                                |  1k    |  10n
  pin 2  o--------------------------------------+--------+-----------o  board GND
```

Rp and C3 are parallel shunt legs from the same node.

**Target: Yaesu's nominal 500 mVp-p at -12 dBFS** **CHOICE**, which is this repository's design target for a receive path (`InputLevelMeter.cs` and `FrameLevelLimits.cs` cite the Tait note for it). 500 mVp-p is 176.8 mVrms.

The arithmetic runs the other way from the Tait build, and that is the surprise in this design. There the discriminator tap was hot and the divider's job was to throw signal away. Here **the radio's output is slightly too small for the card**: a straight wire from pin 5 to the IN pad lands at -16.2 dBFS with the capture control at 0.00 dB, so the path needs gain rather than attenuation, and Rp is doing more work as a defined termination than as an attenuator.

| Rs | At the card | Ideal gain | Nearest step | Lands at | Load on the radio |
|---|---|---|---|---|---|
| 0R | 110.5 mV | +8.29 dB | +8 dB | -12.29 dBFS | 1.0k |
| **220R** | **97.1 mV** | **+9.41 dB** | **+9 dB** | **-12.41 dBFS** | **1.2k** |
| 470R | 85.4 mV | +10.53 dB | +11 dB | -11.53 dBFS | 1.5k |
| 1k0 | 68.0 mV | +12.51 dB | +13 dB | -11.51 dBFS | 2.0k |
| 1k8 | 52.0 mV | +14.84 dB | +15 dB | -11.84 dBFS | 2.8k |
| 3k3 | 36.1 mV | +18.02 dB | +18 dB | -12.02 dBFS | 4.3k |
| 6k8 | 21.0 mV | +22.70 dB | +23 dB | -11.70 dBFS | 7.8k |

**DERIVED** throughout, with Rp = 1k and the 600 ohm source impedance in series with Rs. For a different dongle, with FS the measured input full scale in volts rms at the gain you intend to run at:

    Rs = 704/FS - 1600

The 1600 is Rp plus the source's 600 ohm, which is in series with Rs. Omitting it inflates Rs by about 600 ohm and quietly picks the wrong value, which is the same trap the Tait note documents.

**Rs = 220R at +9 dB is the build** **CHOICE**, for three reasons rather than one. It gives a real series element, so C3 has something to filter against and the tail has somewhere to put an RF choke. It loads the radio at 1.2k, which is twice the nominal source impedance and light enough to be harmless while still being a defined, low-impedance line. And it lands within a decibel of the calibrated +8.00 dB measurement point, so almost nothing is being extrapolated from the capture control's tracking.

Rs = 0 at +8 dB is equally defensible and hits the reference point exactly. It was not chosen only because a divider table with a wire link in it invites the wrong substitution later.

**Headroom.** -12.4 dBFS nominal means the codec clips at 12.4 dB above Yaesu's figure, which is 2.08 Vp-p out of the radio. Because DATA OUT is AGC-held rather than signal-following, the nominal behaves as a ceiling and that margin is generous. It is spent on two things: the AGC's attack transient on a sudden strong signal, and the fact that the 500 mVp-p figure is a nominal that has a unit-to-unit and AGC-setting spread nobody publishes.

**C1** works against the whole series path, 1820 ohm, so 4u7 puts the corner at 19 Hz **DERIVED**, which is 0.02 dB at 300 Hz. It is there to guarantee no DC path into the radio whatever DATA OUT turns out to be, and to keep Rs and Rp from loading a bias we have not been told about.

**C3 is RF hygiene**, and it is 10n here where the Tait build fits 4n7. 10n against the node's 451 ohm is a pole at 35 kHz, 0.03 dB at 3 kHz **DERIVED**. The Tait build had 9600 baud modes with content out to 9.6 kHz to protect and could not go lower; an SSB path stops at 2.7 kHz, so the extra filtering is free and HF is where it is wanted.

## What is not known, and the evening that would settle it

Nothing on this page has been measured. Five things, in the order they are worth doing, and the whole list is one session with a meter, a dummy load and an SDR.

**Check 1: which reading of 500 mVp-p is right.** Put a signal into the radio on a quiet frequency, or just use band noise, and measure the AC level at pin 5 with a true-RMS meter or a scope, first open circuit and then across a 600 ohm resistor. If the two differ by 6 dB, the 600 ohm source impedance is real and the open-circuit figure is the one the table above assumes. If they barely differ, the output impedance is much lower than specified and the table is 6 dB light: add 6 dB of capture gain, or drop Rs a row.

**Check 2: what DATA IN actually presents.** The cleanest way is indirect and needs no access to the pin: key the radio with a steady tone through the assembly and measure the audio at pin 1 with a scope, first with the plug in the radio and then with it out. The ratio between the two readings is the divider's Thevenin resistance against the true input impedance, and it should be 1.3 dB if the input is 600 ohm and less if it is higher. Anything much more than 1.3 dB means the input impedance is *lower* than specified and the transmit level is short.

**Check 3: the two-tone calibration.** The real one, on the ALC meter, into a dummy load, with the products watched on an SDR. This is the number that ends up in `playbackDb` and it is the one figure on the build page that is genuinely expected to be set rather than predicted. Record where it lands, because a second assembly then has a starting point.

**Check 4: band noise against the capture setting.** With the radio on a real band, record with `arecord` and look at the peak, on a quiet afternoon and on a busy evening. The failure this catches is a receive path that is clipping on band noise the whole time it is listening, which the Tait build hit on FM and which HF reaches by a different route, through the AGC.

**Check 5: PTT-to-modulation, and hence TXDELAY.** Yaesu publish nothing. Key through the interface with a tone and watch the RF envelope and the modulation on an SDR; the delay that matters is to the first *valid modulation*, not to the first carrier. The build page starts at 50 ms for want of a number.

While the assembly is on the bench, an audio sweep of the whole chain is worth the ten minutes it costs. The Tait equivalent is in the [low-frequency corner section](tm8100-cm108-interface-notes.md#measured-the-end-to-end-low-frequency-corner-and-what-it-costs-the-9600-baud-modes), and the same method applies: step a tone through `POST /api/txtest` and read each tone's level coherently out of the receiving station's `rawCapture`. On HF the interesting part is the top end rather than the bottom, because the SSB filter is there and the modem's band plan arithmetic assumes the passband is where it says.

## RF ingress

The Tait note's grounding section says that in a fixed station with one supply and a short USB lead, a direct connection is usually fine. **That advice does not carry over.** It was written about a 25 W VHF mobile, and the failure mode being weighed was a ground loop through a vehicle's electrical system. Here the transmitter is 100 W into an HF antenna a few feet from a USB lead, and the mechanism is common-mode current on every conductor leaving the shack, not a loop.

What makes it worth designing for rather than debugging later is that the symptoms do not look electrical. A latched PTT, a card that drops off the USB bus mid-transmission, a modem that hears its own transmission, a host that locks up on one band and not another: every one of those reads as a software fault first, and the band-dependence is usually the only clue.

The defences, in the order they pay:

1. **Ferrites and short leads.** Mix 31 sleeves at both ends of the USB lead and on the DATA tail, with as many turns through each as will fit.
2. **A common-mode choke at the feedpoint.** More often the root cause than the interface is, and not an interface problem at all.
3. **Transformer isolation**, which is close to free on this build and was not on the Tait one. Two 600:600 telecoms transformers, one after C1 on receive and one after C4 on transmit, plus an opto-isolator in place of the direct PTT wire, breaks every galvanic path between radio and host. Such a transformer is flat from a few hundred hertz, which is marginal on a flat FM discriminator tap and exactly right for a 300 to 2700 Hz SSB passband. If the assembly is being built from scratch, leave room for them.

Test at full power on every band the station will use, into a dummy load first. RF ingress is frequency-dependent and a station clean on 40 m can be unusable on 10 m.

## What goes wrong

Predicted rather than observed, since nothing has been built, but each is either carried over from the Tait build or specific to this jack.

- **The radio left in plain USB rather than the data mode.** The rear DATA jack only carries audio in the data mode, so the station transmits nothing and decodes nothing with no other symptom. First thing to check.
- **`D DISP` left non-zero.** It offsets the displayed frequency in data mode by up to 3 kHz, and the modem's dial line assumes the display means what it says, so every modem in a band plan lands somewhere other than where it was asked to. Nothing in the journal can see it.
- **`DIG VOX` left on** alongside the hardware PTT, giving two claims on the transmitter and keying on whatever the card emits between frames.
- **The shunt leg of the transmit divider left on the radio side of C4**, which, if DATA IN carries any bias at all, is a 100 ohm short across it.
- **C4 fitted at 1 uF**, copied from the Tait build, putting a 228 Hz corner at the bottom of the passband. The low impedance is what makes this position need 10u.
- **Transmit level set on the PO meter rather than the ALC meter.** [METER/DIM] cycles PO, ALC, SWR, and PO tells you nothing about whether the audio is too hot.
- **Full power for a data duty cycle.** Yaesu's own advice is 1/2 to 1/3 of maximum for anything longer than a few minutes; a packet node is longer than a few minutes.
- **+20 dB mic boost left on**, which puts the receive path deep into clipping. pdn-soundmodem forces it off at every start-up, so this only bites a hand-run bench test.
- **A fast AGC**, which pumps on every strong signal in the passband and amplitude-modulates the wanted one. Reads as a marginal path and is not.
- **RF ingress**, which reads as a software fault. See above.
- **The 100k gate pull-down not fitted.** The BSS138's gate floats until the driver configures GPIO3, and a floating gate can sit above threshold. On this radio that is 100 W keyed by a plugged-in USB lead.
