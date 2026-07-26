# Kenwood TK-90 for MS110D — evaluation

Written 2026-07-26. Sources are the manufacturer's own documents linked from [wiki.oarc.uk/radios:tk-90](https://wiki.oarc.uk/radios:tk-90) — service manual, function reference (v2.00), instruction manual and ALE supplement — plus a measurement made with this repo's own harness. Every number below is either quoted from those documents or measured here; where it is neither, it says so.

## Verdict

**Well suited, and unusually so — with the optional KIF-2 filter fitted.** Almost everything MS110D needs from a radio, this one has by design rather than by accident: a proper data port with fixed levels, ±0.5 ppm stability, 2G ALE, and a full CAT command set. It is a commercial HF radio built for unattended data, and it shows.

**The one real compromise is bandwidth**, and it is worth being precise about rather than hand-waving:

| Receive path | −6 dB | −60 dB | Measured cost to MS110D |
|---|---|---|---|
| Built-in filter | **2.2 kHz** | 4.8 kHz | ~1.3 dB of energy **plus** ~1 dB of distortion |
| With KIF-2 | **2.7 kHz** | 6.2 kHz | ~0.3 dB, distortion negligible |
| Reference (our Flex IQ path) | 3.3 kHz | — | 0 by definition |

MS110D Appendix D occupies about 3 kHz — roughly 180–3420 Hz about the 1800 Hz sub-carrier. **Neither filter is wide enough**, and the radio filters on transmit as well as receive, so the truncation happens twice.

With KIF-2 that costs a few tenths of a dB and is of no practical consequence. Without it, budget **2–2.5 dB**, which is a meaningful fraction of the margin between adjacent waveforms. Fit the KIF-2.

## What MS110D actually needs, and how the TK-90 answers

| Requirement | TK-90 | Source |
|---|---|---|
| ~3 kHz linear SSB path | **2.2 kHz built-in, 2.7 kHz with KIF-2** — the weak point | Service manual, Specifications |
| Frequency stability inside the ±75 Hz acquisition grid | **±0.5 ppm** (−10…+50 °C), ±1.0 ppm (−20…+60 °C) → **±9 Hz at 18.1 MHz** | Service manual, Specifications |
| Fixed-level audio in/out that bypasses mic and AF gain | **DATA mode** with DI and DEO on the KCT-39 cable, levels 0–9 set in dealer menu or KPG-102D | Function reference §14 |
| Sideband choice for the data path | DATA Sideband, USB or LSB | Function reference §14.1 |
| No ALC pumping or compression on data | DI level is an input-sensitivity setting; the TX equaliser (`TXEQ`, "Mic High Boost") is a microphone-path function | Service manual; function reference |
| High duty cycle | 100/50/25/5 W settings; 20.5 A on transmit; thermal protection present. **No duty-cycle rating appears in any manual I read** | see caveat below |
| Remote control | Full Kenwood-style CAT: `FA` frequency, `MD` mode, `PC` power, `RX`/`TX` keying, `IF` status, `SM` meters, `PS` power, plus `A0`–`B0` for ALE | Function reference, Appendix 1 |
| Link establishment | **2G ALE to MIL-STD-188/141A** — soundings, LQA scoring, AMD messages, all-calls | ALE supplement |

**Caveat on duty cycle.** The wiki describes "100% duty cycle at 25 W". That figure does not appear in the service manual, the instruction manual or the function reference. It is plausible for a commercial radio at quarter power and it matches how the radio is marketed, but treat it as community knowledge until someone finds it in a Kenwood document — a §E2 ladder is a high-duty load and the answer matters.

## The bandwidth measurement

This is the part worth having, because "2.2 kHz sounds too narrow" is an opinion and this is a number. The harness already emulates a narrower SSB filter on receive; I added the same on transmit, since a conventional transceiver truncates at both ends and modelling only the receive side understates the cost.

Method: render a WN2/WN6/WN13 AWGN ladder through the emulated passband, score it through the real receive converter and the real scorer. Passbands 300–2500 Hz (2.2 kHz) and 300–3000 Hz (2.7 kHz) against the 150–3450 Hz reference.

**Energy lost** — the same at every rung and every waveform, because it is simply the part of the spectrum thrown away:

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

Half again as many channel bit errors at the *same* delivered SNR — worth roughly another dB against the reference curve. So the 2.2 kHz path costs about **1.3 dB of energy plus about 1 dB of distortion**, and the 2.7 kHz path costs about 0.3 dB and nothing measurable beyond it.

**This is a lower bound.** The emulation is a linear-phase FIR with sharp edges. A real crystal filter has passband ripple (2 dB specified), and group-delay distortion that rises steeply at the band edges — which is exactly where a serial-tone waveform keeps the sidebands it needs. Expect the real radio to be somewhat worse than these figures, not better.

## What is genuinely good about it for this application

**Stability.** ±0.5 ppm is better than the Flex 6500 in this shack managed uncorrected — that measured −1497 ppb, i.e. −1.5 ppm, and needed a correction applied before anything would acquire. At ±0.5 ppm the TK-90 sits inside the demodulator's ±75 Hz acquisition grid at 18 MHz with 8× margin, and would very likely need no correction at all. For an unattended station that is the difference between working and not.

**A real data port.** DI and DEO with independently configurable levels, set once in the dealer menu and thereafter immune to somebody turning the AF knob. Most amateur radios make you fight for this.

**2G ALE built in.** MS110D is the data waveform that belongs behind 188-141 ALE, and having the link-establishment layer in the radio — with sounding and LQA already implemented and interoperable with other brands — is a substantial thing not to have to build. This is the feature that makes the TK-90 interesting rather than merely adequate.

**CAT that covers what a campaign runner needs**: set frequency, set mode, set power, key and unkey, read status and meters. `sm-ota` currently drives a Flex through `M0LTE.Flex`; the equivalent here is the standard Kenwood ASCII protocol.

## What it cannot do, and why that is interesting

**No IQ transmit.** The Flex path this campaign has been built around synthesises an ideal single-sideband signal digitally — no SSB filter, no ALC, no uncharacterised TX DSP — precisely so the first measurements were of our own work rather than a radio's. The TK-90 is a conventional superhet: audio in, RF out, through real filters.

That makes it **the ideal subject for §E3**, which exists to measure exactly this difference. §E3 was scoped as "our IQ path versus our own DAX audio path"; a TK-90 would extend it to "versus a real commercial radio", which is a far more useful comparison, because a real deployment will be running a radio like this and not a Flex in waveform mode.

## How 2G ALE and MS110D would work together in practice

ALE requires the optional **KPE-2** unit; it is not in the base radio.

### What ALE is, and what it is not

2G ALE (MIL-STD-188-141A) is a **rendezvous and channel-selection layer**. It answers "which of my channels can reach that station right now, and is he listening?" — and nothing else. It carries no traffic. Once it has established a link, it hands over a channel and gets out of the way, and the data waveform runs on that channel as a separate activity.

So the pairing is not "ALE carries MS110D". It is: ALE picks the channel and proves the path exists, then MS110D uses it.

### The sequence, concretely

1. Both stations idle in **Net mode**, scanning their net's channel list at the configured Scan Rate.
2. The caller issues an individual call. The radio first listens for a clear channel (**Listen-before-transmit Time**), then transmits an ALE call long enough for the callee to hear it whichever channel its scan is currently on.
3. The callee responds; the caller acknowledges. `<< LINKED >>`, link tone, and both radios are now parked on one agreed channel. LQA picks that channel automatically, or the operator overrides it.
4. **Switch out of Net mode** — to VFO or Channel mode — because Net mode will not transmit anything but ALE (see below).
5. Set DATA mode, and run MS110D through DI/DEO with PTT on the KCT-39.
6. Terminate the link explicitly (`TERMINATION` in the ALE Call menu), or let the Wait for Activity Timer drop it.

### The interlock that shapes everything

The three ALE operating modes differ in exactly the way that matters to a data modem:

| Mode | Scans? | Can transmit arbitrary data? | ALE calls? |
|---|---|---|---|
| **Net** | Yes, always — *"scan cannot be manually stopped"* | **No, not unless a link is established** | Yes |
| **Channel** | No | Yes | Yes |
| **VFO** | No | Yes | **No** (except emergency) |

This is the crux of the integration. **A station sitting in Net mode cannot be made to transmit data on demand** — it is scanning, and the radio refuses. Data can only flow after a link exists, or after the controller has moved the radio to Channel or VFO mode, at which point it is no longer scanning and no longer reachable by anyone else's ALE call.

That is a genuine either/or: *listening for callers* and *being available for data* are different radio states, and something has to arbitrate between them. The manual explicitly allows the transition that makes the flow work — "when a link is established, it is possible to change from Net mode to VFO mode" — so the intended pattern is: scan, link, leave scan, pass traffic, terminate, resume scanning.

### Where the time goes

An ALE call has to be long enough to cover the callee's entire scan cycle, since the caller cannot know where in the scan the callee is. So **call duration scales with the number of channels in the net**, and the handshake costs seconds before a single data bit moves. For a short MS110D burst — ours are a second or two — the link setup can easily cost more airtime than the traffic.

That argues for one of two patterns, and it is a design decision worth making deliberately:

- **Link once, transfer a lot.** Amortise the handshake over a long exchange. Good for bulk transfer, poor for latency.
- **Skip ALE for scheduled contacts.** If both ends know the time and channel, Channel mode with no link at all is faster and simpler. ALE earns its keep when the channel is *unknown* — which is exactly the case ionospheric conditions create, and exactly the case a fixed schedule does not.

### The gap ALE does not close

**LQA tells you the link exists. It does not tell you which waveform to use.**

ALE's own signalling is 8-ary FSK at a few hundred bits per second — far more robust than most of the MS110D family. A channel that links cleanly may still be nowhere near good enough for the higher waveforms. Our own gate table spans **−6 dB (WN0) to 16 dB (WN8)**, a 22 dB range: "linked" leaves essentially the whole waveform ladder open.

So an ALE-fronted MS110D station still needs a rate-selection policy of its own. LQA score is a reasonable *prior* — it is a real measurement of that channel to that station — but it cannot be the decision. The options are to probe (send a known burst and score it, which is precisely what `sm-ota score` does), to adapt (start conservative and climb), or to carry the previous exchange's measured SNR forward. `BurstScore.Snr` and `UncodedBer` are the right inputs for that, and the harness already produces both.

There is a neat opportunity here: the LQA score and our own measured SNR could be logged together over a campaign, and the relationship between them measured. That would turn LQA from a vendor-defined number into a calibrated predictor of which waveform will work — and it needs no extra hardware beyond the radio itself.

### One thing that works out nicely

The 2G ALE tone set spans roughly 750–2500 Hz. That fits inside the TK-90's 2.2 kHz built-in filter with room to spare, so **the narrow filter costs ALE nothing** — the link layer is unaffected, and only the traffic waveform pays the 1–2 dB measured above. Fitting the KIF-2 improves the data path and leaves ALE exactly as it was.

### Link maintenance, and a question to settle on the bench

The **Wait for Activity Timer** drops the link automatically if nothing happens — and the manual defines "something happening" as *"no PTT (microphone) switch has been pressed"*. Whether keying via the **data-port PTT** on the KCT-39 also resets that timer is not stated, and it matters: if it does not, a long data exchange would have its link terminated underneath it while the modem is mid-transfer.

Other per-net parameters worth knowing about, all set in KPG-102D: **Scan Rate**, **Listen-before-transmit Time**, **LQA Request** (asks the far end to report its score back, giving both directions), **LQA Score Threshold Level** (below which the radio refuses to link at all), **LQA Timeout** (how long scores stay valid), and **AMD Frame**, which the manual notes "must be changed if the transceiver cannot link with other manufacturers transceivers" — a plain admission that 2G ALE interop has framing variations in the field.

### What we would have to build

Modest, and none of it novel:

1. **A Kenwood CAT driver** — `FA`, `MD`, `PC`, `RX`/`TX`, `IF`, `SM`, plus the ALE set `A0`–`B0` (`A5` makes calls, `A6` reports received calls, `A7` reads link status). Structurally the same job `M0LTE.Flex` does for the Flex.
2. **A link state machine**: request link → poll `A7` → on link, read the chosen channel → switch mode → hand the channel to the modem → terminate.
3. **PTT arbitration**, so the ALE unit and our modem never both believe they own the transmitter.
4. **A rate-selection policy** informed by LQA *and* by our own measured SNR from the previous exchange.

The interesting engineering is item 4. Items 1–3 are plumbing.

## If one is obtained, measure these

In order, and all of them cheap once the radio is on the bench:

1. **The actual passband, both directions.** Sweep audio in through DI and measure the RF; sweep RF in and measure DEO. The specification gives a width, not edges, and my emulation had to guess them — the guess is doing real work in the numbers above.
2. **Whether KIF-2 is in the transmit path.** The manual implies the IF filter is used on transmit (it says the built-in filter is forced on transmit when the CW filter is installed) but never states it for KIF-2. If KIF-2 is receive-only, the transmit side is stuck at 2.2 kHz and the total penalty is worse than the table above.
3. **Group delay across the passband.** The measurement above models amplitude truncation only, and group delay at the band edges is what actually hurts a serial-tone waveform.
4. **ALC behaviour on DATA.** Sweep DI level and check the RF envelope tracks linearly. Our two-tone rig (`sm-ota tone --tone2-hz`) does this directly.
5. **Duty cycle**, since no manual states it and a ladder pass is a high-duty load.
6. **Actual frequency error**, against the same RWM reference used for the Flex and the UberSDR.
7. **Whether the data-port PTT resets the ALE Wait for Activity Timer.** If it does not, a long data exchange will have its link terminated underneath it.
8. **ALE handshake duration** against net size, so the link-setup overhead can be budgeted against burst length.

Items 1, 2 and 4 would take an afternoon and would turn every estimate in this document into a measurement.

## Provenance

Read in full for this evaluation: `kenwood_tk-90_service.pdf` (Specifications p.116, IF filter tables pp.21–22, block diagrams), `tk-90_function_reference.pdf` v2.00 (§14 Data Communication, §19 IF Filter, Appendix 1 PC Commands), `kenwood_tk-90_ale.pdf`, `kenwood_tk-90_instructions.pdf`. The bandwidth figures are from Kenwood's own specification table and MCF component tables, not from a review.

The measurement was made with `sm-ota ladder --tx-ssb-low/--tx-ssb-high` and `sm-ota score --ssb-low/--ssb-high` at the revision recorded in this repo's history for 2026-07-26. It is reproducible: the schedule and manifest for each arm carry the seeds.
