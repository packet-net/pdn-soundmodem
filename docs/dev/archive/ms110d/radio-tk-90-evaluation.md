# Kenwood TK-90 for MS110D - evaluation

Written 2026-07-26. Sources are the manufacturer's own documents linked from [wiki.oarc.uk/radios:tk-90](https://wiki.oarc.uk/radios:tk-90) - service manual, function reference (v2.00), instruction manual and ALE supplement - plus a measurement made with this repo's own harness. Every number below is either quoted from those documents or measured here; where it is neither, it says so.

## Verdict

> **Revised 2026-07-26, after Tom noted that both option boards - KIF-2 and KPE-2 - are hard or impossible to obtain.** That removes the wider filter *and* ALE entirely, which are the two things the first draft leaned on. The revision is below; the original reasoning is kept intact further down because it is still the right analysis, just of a radio nobody can buy the parts for.

**As actually obtainable - bare radio, no options - it is a decent but not compelling choice for MS110D.**

What survives without the option boards is genuinely good: **±0.5 ppm** stability, a proper **DATA mode with fixed DI/DEO levels**, full **CAT**, 100 W with commercial build. Those are real advantages over most amateur radios and they matter for unattended operation.

What is lost is most of the case for choosing *this* radio:

| | With options | As obtainable |
|---|---|---|
| Passband | 2.7 kHz (KIF-2) | **2.2 kHz built-in - the only option** |
| Measured MS110D penalty | ~0.3 dB | **~2-2.5 dB** (1.3 dB energy + ~1 dB distortion) |
| ALE | 2G, MIL-STD-188/141A | **None. Requires KPE-2** |

So the honest position is:

- **The 2-2.5 dB is now the actual figure, not the avoidable one.** It cannot be tuned or configured away: MS110D Appendix D occupies ~3 kHz (roughly 180-3420 Hz about the 1800 Hz sub-carrier) and no shift of the dial makes a 3.2 kHz signal fit a 2.2 kHz filter. The radio also filters on transmit, so it happens twice. What can be said for it is that the penalty is **fixed, known and measurable** - for a research campaign a constant 2 dB offset that we have characterised is workable, provided it is stated every time a result is quoted.
- **Without KPE-2 there is no ALE at all**, and ALE was the feature that made this radio interesting rather than merely adequate. See the next section - the answer is probably to build it in software, at which point the radio choice stops depending on an unobtainable board.

**Recommendation.** If a TK-90 is already to hand, it is a perfectly usable MS110D radio and its stability and data port are better than most - budget the 2 dB and get on with it. If one is being bought *for this purpose*, the bandwidth is a poor trade: any modern transceiver offering a 3 kHz or wider SSB filter with a data port removes the penalty entirely, and without KPE-2 the TK-90 no longer brings ALE to offset it.

## What MS110D actually needs, and how the TK-90 answers

| Requirement | TK-90 | Source |
|---|---|---|
| ~3 kHz linear SSB path | **2.2 kHz built-in, 2.7 kHz with KIF-2** - the weak point | Service manual, Specifications |
| Frequency stability inside the ±75 Hz acquisition grid | **±0.5 ppm** (−10…+50 °C), ±1.0 ppm (−20…+60 °C) → **±9 Hz at 18.1 MHz** | Service manual, Specifications |
| Fixed-level audio in/out that bypasses mic and AF gain | **DATA mode** with DI and DEO on the KCT-39 cable, levels 0-9 set in dealer menu or KPG-102D | Function reference §14 |
| Sideband choice for the data path | DATA Sideband, USB or LSB | Function reference §14.1 |
| No ALC pumping or compression on data | DI level is an input-sensitivity setting; the TX equaliser (`TXEQ`, "Mic High Boost") is a microphone-path function | Service manual; function reference |
| High duty cycle | 100/50/25/5 W settings; 20.5 A on transmit; thermal protection present. **No duty-cycle rating appears in any manual I read** | see caveat below |
| Remote control | Full Kenwood-style CAT: `FA` frequency, `MD` mode, `PC` power, `RX`/`TX` keying, `IF` status, `SM` meters, `PS` power, plus `A0`-`B0` for ALE | Function reference, Appendix 1 |
| Link establishment | **2G ALE to MIL-STD-188/141A** - soundings, LQA scoring, AMD messages, all-calls | ALE supplement |

**Caveat on duty cycle.** The wiki describes "100% duty cycle at 25 W". That figure does not appear in the service manual, the instruction manual or the function reference. It is plausible for a commercial radio at quarter power and it matches how the radio is marketed, but treat it as community knowledge until someone finds it in a Kenwood document - a §E2 ladder is a high-duty load and the answer matters.

## The bandwidth measurement

This is the part worth having, because "2.2 kHz sounds too narrow" is an opinion and this is a number. The harness already emulates a narrower SSB filter on receive; I added the same on transmit, since a conventional transceiver truncates at both ends and modelling only the receive side understates the cost.

Method: render a WN2/WN6/WN13 AWGN ladder through the emulated passband, score it through the real receive converter and the real scorer. Passbands 300-2500 Hz (2.2 kHz) and 300-3000 Hz (2.7 kHz) against the 150-3450 Hz reference.

**Energy lost** - the same at every rung and every waveform, because it is simply the part of the spectrum thrown away:

| Path | Delivered SNR against a nominal 18 dB |
|---|---|
| Reference 3.3 kHz | 18.3 dB |
| 2.7 kHz both ends | 18.0 dB |
| 2.2 kHz both ends | 17.0 dB |

**Distortion, separated from energy.** Asking for 7.3 dB through the 2.2 kHz path delivers the same SNR as asking for 6.0 dB through the reference, so any remaining difference is inter-symbol interference rather than level. Ten bursts each, WN6, 20 480 channel bits per arm:

| Path | Delivered SNR | Uncoded BER |
|---|---|---|
| Reference 3.3 kHz | 6.15 dB | 2.11 × 10⁻² |
| 2.2 kHz both ends | 6.17 dB | **3.16 × 10⁻²** |

Half again as many channel bit errors at the *same* delivered SNR - worth roughly another dB against the reference curve. So the 2.2 kHz path costs about **1.3 dB of energy plus about 1 dB of distortion**, and the 2.7 kHz path costs about 0.3 dB and nothing measurable beyond it. With KIF-2 unobtainable, the 2.2 kHz row is the one that applies.

**This is a lower bound.** The emulation is a linear-phase FIR with sharp edges. A real crystal filter has passband ripple (2 dB specified), and group-delay distortion that rises steeply at the band edges - which is exactly where a serial-tone waveform keeps the sidebands it needs. Expect the real radio to be somewhat worse than these figures, not better.

## What is genuinely good about it for this application

**Stability.** ±0.5 ppm is better than the Flex 6500 in this shack managed uncorrected - that measured −1497 ppb, i.e. −1.5 ppm, and needed a correction applied before anything would acquire. At ±0.5 ppm the TK-90 sits inside the demodulator's ±75 Hz acquisition grid at 18 MHz with 8× margin, and would very likely need no correction at all. For an unattended station that is the difference between working and not.

**A real data port.** DI and DEO with independently configurable levels, set once in the dealer menu and thereafter immune to somebody turning the AF knob. Most amateur radios make you fight for this.

**2G ALE built in.** MS110D is the data waveform that belongs behind 188-141 ALE, and having the link-establishment layer in the radio - with sounding and LQA already implemented and interoperable with other brands - is a substantial thing not to have to build. This is the feature that makes the TK-90 interesting rather than merely adequate.

**CAT that covers what a campaign runner needs**: set frequency, set mode, set power, key and unkey, read status and meters. `sm-ota` currently drives a Flex through `M0LTE.Flex`; the equivalent here is the standard Kenwood ASCII protocol.

## What it cannot do, and why that is interesting

**No IQ transmit.** The Flex path this campaign has been built around synthesises an ideal single-sideband signal digitally - no SSB filter, no ALC, no uncharacterised TX DSP - precisely so the first measurements were of our own work rather than a radio's. The TK-90 is a conventional superhet: audio in, RF out, through real filters.

That makes it **the ideal subject for §E3**, which exists to measure exactly this difference. §E3 was scoped as "our IQ path versus our own DAX audio path"; a TK-90 would extend it to "versus a real commercial radio", which is a far more useful comparison, because a real deployment will be running a radio like this and not a Flex in waveform mode.

## Neither option board is obtainable - what follows

Tom reports KIF-2 and KPE-2 are hard or impossible to source. Both conclusions above are conditional on them, so both change.

### The filter: accept it, and measure it

There is no configuration workaround. The waveform is ~3.2 kHz wide about its own sub-carrier, so no amount of shifting the dial fits it through a 2.2 kHz filter - shifting only changes *which* part is cut. Substituting a wider crystal filter is a hardware modification of the IF strip (the built-in part is an MCF, L71-0604-15 / XF252) and is well outside what this campaign should be doing.

What is left is to treat it as a characterised loss rather than a defect. That is not a consolation prize: a **known** 2-2.5 dB offset is scientifically fine, because every result can be quoted against it. And the harness measures it directly - `sm-ota ladder --tx-ssb-low/--tx-ssb-high` and `sm-ota score --ssb-low/--ssb-high` already model it, and a bench sweep of the real radio would replace the modelled figure with a measured one.

The one thing that must not happen is a campaign that quotes TK-90 results against simulation without stating the offset. That is exactly how a 2 dB instrument error becomes a 2 dB demodulator deficiency in somebody's write-up.

### ALE: build it in software instead

Losing KPE-2 does not have to mean losing ALE, and the alternative is arguably better.

**2G ALE is a much simpler waveform than the one this repo already demodulates.** It is 8-ary FSK in the region of 750-2500 Hz at a low symbol rate, with Golay-coded, interleaved, redundantly-transmitted 24-bit words - no equaliser, no turbo decoder, no serial-tone acquisition problem. Against MS110D Appendix D, which this project has already built, it is a modest piece of work, and every DSP primitive it needs is in hand.

The advantages of doing it ourselves are substantial:

- **It works with any radio.** The radio choice stops depending on a discontinued option board - and stops depending on Kenwood at all.
- **It fits the narrow filter.** The 2G tone set spans roughly 750-2500 Hz, comfortably inside 2.2 kHz. The one part of the system the TK-90's filter does *not* hurt is the part we would be building.
- **LQA becomes ours.** A vendor's LQA score is an opaque number; ours could be the SNR and uncoded BER the scorer already produces, which is a far better predictor of which waveform will work (see the gap discussed below).
- **It is testable the way everything else here is.** The same offline loop - render, convert, score - applies directly, so an ALE implementation could be verified without a radio, exactly as the E2 ladder was.

The costs are honest ones: it is new work, 2G ALE has real interop variation in the field (Kenwood's own manual admits as much, noting the AMD frame "must be changed if the transceiver cannot link with other manufacturers transceivers"), and interoperating with commercial stations would need testing against one.

**This is not a recommendation to start now** - MS110D's Poor gate is still 8 of 10 with a successor program registered (2026-08-20: [poor-gate-successor-plan.md](poor-gate-successor-plan.md)); §E2 has run on hardware since 2026-07-27, so that half of the original reason has lapsed. But it is worth recording that the ALE capability is not gated on hardware we cannot buy. There is now a plan for it: [`docs/ale/plan.md`](../ale/plan.md), which also identifies one experiment worth doing early regardless - decoding real off-air 2G ALE, which needs no transmit licence, no partner station and no hardware this project does not already own.

The section that follows describes how ALE and MS110D fit together in practice. It was written for the TK-90's built-in implementation, but the sequence, the timing arguments and the LQA-versus-waveform gap apply equally to a software one - the mode interlock is the only part that is specific to the radio.

## How 2G ALE and MS110D would work together in practice

ALE requires the optional **KPE-2** unit; it is not in the base radio, and it is reportedly hard or impossible to obtain - see the section above.

### "2G" means generation, and the generation has to match the channel

ALE generations are eras of the technology, not channels or bands:

| | Standard | Character | Where it stands |
|---|---|---|---|
| **1G** | none - vendor schemes | pre-standard selective calling, early 1980s | historical |
| **2G** | MIL-STD-188-141A App. A, FED-STD-1045 | asynchronous, 8-FSK, robust, slow to link | **what "ALE" means in practice** - near-universal, interoperable |
| **3G** | MIL-STD-188-141B App. C; NATO STANAG 4538 | **synchronous** - needs time-of-day at both ends - much faster linking, and carries ARQ data-link protocols with it | capable, far less common, needs a clock |
| **4G** (wideband ALE) | MIL-STD-188-141D, 2017 | linking for HF channels **wider than 3 kHz** | pairs with the wideband members of 110D |

**3G is technically better and a poor fit for us.** It needs synchronised time at both ends - our local UberSDR has no GPS at all (M9PSY's does) - and 3G is not merely a faster handshake: STANAG 4538 brings the ARQ data-link protocols with it, so adopting it means implementing a great deal more than link setup. 2G is the pragmatic choice: simpler than the modem we have already built, needs no clock, and is what every other station on the air actually speaks.

**4G is the one that matters to this project's future**, and for a reason that is easy to miss. Appendix D is not a 3 kHz waveform - it is a *family* parameterised by bandwidth, and we have implemented its narrowest member:

| Bandwidth | Symbol rate | Sub-carrier |
|---|---|---|
| **3 kHz** | **2400 Bd** | **1800 Hz** ← what `Get3k` returns |
| 6 kHz | 4800 Bd | 3300 Hz |
| 12 kHz | 9600 Bd | 6300 Hz |
| 24 kHz | 19200 Bd | 12300 Hz |
| 48 kHz | 38400 Bd | 24300 Hz |

(`tables/d01-symbol-rates.csv`; the sub-carrier is 300 + BW/2 throughout.)

**2G ALE cannot describe a channel wider than 3 kHz.** Its whole design - the tone set, the channel concept, the LQA - assumes a voice-bandwidth channel. So if this project ever climbs the bandwidth column, the link layer has to move generation at the same time. MIL-STD-188-110D and MIL-STD-188-141D are both dated 2017 and are meant to be used together; the generations pair up, and mixing them does not work.

That is not a near-term concern - going wideband would need a transmitter and a receiver that can pass it, which is a bigger step than any radio discussed here - but it is the reason not to over-invest in 2G if wideband is ever the destination.

### What ALE is, and what it is not

2G ALE (MIL-STD-188-141A) is a **rendezvous and channel-selection layer**. It answers "which of my channels can reach that station right now, and is he listening?" - and nothing else. It carries no traffic. Once it has established a link, it hands over a channel and gets out of the way, and the data waveform runs on that channel as a separate activity.

So the pairing is not "ALE carries MS110D". It is: ALE picks the channel and proves the path exists, then MS110D uses it.

### The sequence, concretely

1. Both stations idle in **Net mode**, scanning their net's channel list at the configured Scan Rate.
2. The caller issues an individual call. The radio first listens for a clear channel (**Listen-before-transmit Time**), then transmits an ALE call long enough for the callee to hear it whichever channel its scan is currently on.
3. The callee responds; the caller acknowledges. `<< LINKED >>`, link tone, and both radios are now parked on one agreed channel. LQA picks that channel automatically, or the operator overrides it.
4. **Switch out of Net mode** - to VFO or Channel mode - because Net mode will not transmit anything but ALE (see below).
5. Set DATA mode, and run MS110D through DI/DEO with PTT on the KCT-39.
6. Terminate the link explicitly (`TERMINATION` in the ALE Call menu), or let the Wait for Activity Timer drop it.

### The interlock that shapes everything

The three ALE operating modes differ in exactly the way that matters to a data modem:

| Mode | Scans? | Can transmit arbitrary data? | ALE calls? |
|---|---|---|---|
| **Net** | Yes, always - *"scan cannot be manually stopped"* | **No, not unless a link is established** | Yes |
| **Channel** | No | Yes | Yes |
| **VFO** | No | Yes | **No** (except emergency) |

This is the crux of the integration. **A station sitting in Net mode cannot be made to transmit data on demand** - it is scanning, and the radio refuses. Data can only flow after a link exists, or after the controller has moved the radio to Channel or VFO mode, at which point it is no longer scanning and no longer reachable by anyone else's ALE call.

That is a genuine either/or: *listening for callers* and *being available for data* are different radio states, and something has to arbitrate between them. The manual explicitly allows the transition that makes the flow work - "when a link is established, it is possible to change from Net mode to VFO mode" - so the intended pattern is: scan, link, leave scan, pass traffic, terminate, resume scanning.

### Where the time goes

An ALE call has to be long enough to cover the callee's entire scan cycle, since the caller cannot know where in the scan the callee is. So **call duration scales with the number of channels in the net**, and the handshake costs seconds before a single data bit moves. For a short MS110D burst - ours are a second or two - the link setup can easily cost more airtime than the traffic.

That argues for one of two patterns, and it is a design decision worth making deliberately:

- **Link once, transfer a lot.** Amortise the handshake over a long exchange. Good for bulk transfer, poor for latency.
- **Skip ALE for scheduled contacts.** If both ends know the time and channel, Channel mode with no link at all is faster and simpler. ALE earns its keep when the channel is *unknown* - which is exactly the case ionospheric conditions create, and exactly the case a fixed schedule does not.

### The gap ALE does not close

**LQA tells you the link exists. It does not tell you which waveform to use.**

ALE's own signalling is 8-ary FSK at a few hundred bits per second - far more robust than most of the MS110D family. A channel that links cleanly may still be nowhere near good enough for the higher waveforms. Our own gate table spans **−6 dB (WN0) to 16 dB (WN8)**, a 22 dB range: "linked" leaves essentially the whole waveform ladder open.

So an ALE-fronted MS110D station still needs a rate-selection policy of its own. LQA score is a reasonable *prior* - it is a real measurement of that channel to that station - but it cannot be the decision. The options are to probe (send a known burst and score it, which is precisely what `sm-ota score` does), to adapt (start conservative and climb), or to carry the previous exchange's measured SNR forward. `BurstScore.Snr` and `UncodedBer` are the right inputs for that, and the harness already produces both.

There is a neat opportunity here: the LQA score and our own measured SNR could be logged together over a campaign, and the relationship between them measured. That would turn LQA from a vendor-defined number into a calibrated predictor of which waveform will work - and it needs no extra hardware beyond the radio itself.

### One thing that works out nicely

The 2G ALE tone set spans roughly 750-2500 Hz. That fits inside the TK-90's 2.2 kHz built-in filter with room to spare, so **the narrow filter costs ALE nothing** - the link layer is unaffected, and only the traffic waveform pays the 1-2 dB measured above. Fitting the KIF-2 improves the data path and leaves ALE exactly as it was.

### Link maintenance, and a question to settle on the bench

The **Wait for Activity Timer** drops the link automatically if nothing happens - and the manual defines "something happening" as *"no PTT (microphone) switch has been pressed"*. Whether keying via the **data-port PTT** on the KCT-39 also resets that timer is not stated, and it matters: if it does not, a long data exchange would have its link terminated underneath it while the modem is mid-transfer.

Other per-net parameters worth knowing about, all set in KPG-102D: **Scan Rate**, **Listen-before-transmit Time**, **LQA Request** (asks the far end to report its score back, giving both directions), **LQA Score Threshold Level** (below which the radio refuses to link at all), **LQA Timeout** (how long scores stay valid), and **AMD Frame**, which the manual notes "must be changed if the transceiver cannot link with other manufacturers transceivers" - a plain admission that 2G ALE interop has framing variations in the field.

### What we would have to build

Modest, and none of it novel:

1. **A Kenwood CAT driver** - `FA`, `MD`, `PC`, `RX`/`TX`, `IF`, `SM`, plus the ALE set `A0`-`B0` (`A5` makes calls, `A6` reports received calls, `A7` reads link status). Structurally the same job `M0LTE.Flex` does for the Flex.
2. **A link state machine**: request link → poll `A7` → on link, read the chosen channel → switch mode → hand the channel to the modem → terminate.
3. **PTT arbitration**, so the ALE unit and our modem never both believe they own the transmitter.
4. **A rate-selection policy** informed by LQA *and* by our own measured SNR from the previous exchange.

The interesting engineering is item 4. Items 1-3 are plumbing.

## If one is obtained, measure these

In order, and all of them cheap once the radio is on the bench:

1. **The actual passband, both directions.** Sweep audio in through DI and measure the RF; sweep RF in and measure DEO. The specification gives a width, not edges, and my emulation had to guess them - the guess is doing real work in the numbers above.
2. **Whether KIF-2 is in the transmit path.** The manual implies the IF filter is used on transmit (it says the built-in filter is forced on transmit when the CW filter is installed) but never states it for KIF-2. If KIF-2 is receive-only, the transmit side is stuck at 2.2 kHz and the total penalty is worse than the table above.
3. **Group delay across the passband.** The measurement above models amplitude truncation only, and group delay at the band edges is what actually hurts a serial-tone waveform.
4. **ALC behaviour on DATA.** Sweep DI level and check the RF envelope tracks linearly. Our two-tone rig (`sm-ota tone --tone2-hz`) does this directly.
5. **Duty cycle**, since no manual states it and a ladder pass is a high-duty load.
6. **Actual frequency error**, against the same RWM reference used for the Flex and the UberSDR.
7. **Whether the data-port PTT resets the ALE Wait for Activity Timer.** If it does not, a long data exchange will have its link terminated underneath it.
8. **ALE handshake duration** against net size, so the link-setup overhead can be budgeted against burst length.

Items 1, 2 and 4 would take an afternoon and would turn every estimate in this document into a measurement.

## Provenance

Read in full for this evaluation: `kenwood_tk-90_service.pdf` (Specifications p.116, IF filter tables pp.21-22, block diagrams), `tk-90_function_reference.pdf` v2.00 (§14 Data Communication, §19 IF Filter, Appendix 1 PC Commands), `kenwood_tk-90_ale.pdf`, `kenwood_tk-90_instructions.pdf`. The bandwidth figures are from Kenwood's own specification table and MCF component tables, not from a review.

The measurement was made with `sm-ota ladder --tx-ssb-low/--tx-ssb-high` and `sm-ota score --ssb-low/--ssb-high` at the revision recorded in this repo's history for 2026-07-26. It is reproducible: the schedule and manifest for each arm carry the seeds.
