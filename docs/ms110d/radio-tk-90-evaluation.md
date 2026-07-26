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

## If one is obtained, measure these

In order, and all of them cheap once the radio is on the bench:

1. **The actual passband, both directions.** Sweep audio in through DI and measure the RF; sweep RF in and measure DEO. The specification gives a width, not edges, and my emulation had to guess them — the guess is doing real work in the numbers above.
2. **Whether KIF-2 is in the transmit path.** The manual implies the IF filter is used on transmit (it says the built-in filter is forced on transmit when the CW filter is installed) but never states it for KIF-2. If KIF-2 is receive-only, the transmit side is stuck at 2.2 kHz and the total penalty is worse than the table above.
3. **Group delay across the passband.** The measurement above models amplitude truncation only, and group delay at the band edges is what actually hurts a serial-tone waveform.
4. **ALC behaviour on DATA.** Sweep DI level and check the RF envelope tracks linearly. Our two-tone rig (`sm-ota tone --tone2-hz`) does this directly.
5. **Duty cycle**, since no manual states it and a ladder pass is a high-duty load.
6. **Actual frequency error**, against the same RWM reference used for the Flex and the UberSDR.

Items 1, 2 and 4 would take an afternoon and would turn every estimate in this document into a measurement.

## Provenance

Read in full for this evaluation: `kenwood_tk-90_service.pdf` (Specifications p.116, IF filter tables pp.21–22, block diagrams), `tk-90_function_reference.pdf` v2.00 (§14 Data Communication, §19 IF Filter, Appendix 1 PC Commands), `kenwood_tk-90_ale.pdf`, `kenwood_tk-90_instructions.pdf`. The bandwidth figures are from Kenwood's own specification table and MCF component tables, not from a review.

The measurement was made with `sm-ota ladder --tx-ssb-low/--tx-ssb-high` and `sm-ota score --ssb-low/--ssb-high` at the revision recorded in this repo's history for 2026-07-26. It is reproducible: the schedule and manifest for each arm carry the seeds.
