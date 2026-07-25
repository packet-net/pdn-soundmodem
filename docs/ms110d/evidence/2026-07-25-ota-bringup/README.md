# OTA bring-up — tone to first bit-exact modem burst (2026-07-25)

Session log for the MS110D over-the-air harness: what was built, what was measured, and what broke. Plan: [`../../ota-execution-plan.md`](../../ota-execution-plan.md). Chain throughout: FlexRadio 6500 → **dummy load** → near-field leakage → M0LTE's UberSDR (`ubersdr`, Reading) → IQ48 capture → offline demodulation. 17m, 18.1065 MHz waveform centre, modem at +2 kHz.

Nothing here is a §5.3 gate result. The channel is a few metres of room, not a path.

## Headline

**First MS110D bursts on air, decoded bit-exact.** 15 W into the dummy load, first attempt each:

| Waveform | Rate | CFO | Bits | BER | End |
|---|---|---|---|---|---|
| WN2 (BPSK) | 300 bps | −0.1 Hz | 160/160 | **0.00E+00** | EOM |
| WN6 (QPSK) | 3200 bps | −0.1 Hz | 1504/1504 | **0.00E+00** | EOM |
| WN13 (QPSK) | 2400 bps | −0.1 Hz | 1120/1120 | **0.00E+00** | EOM |

WID decoded correctly in every case. On a clean channel this is the expected result rather than an interesting one — its value is that the harness is now trustworthy enough for the result to mean something.

## What was built

`tools/Packet.SoundModem.Ota` (`sm-ota`): `tone`, `sweep`, `tune`, `burst`, `synth`, `meters`, `measure` (with `--survey`), `radio`, `rawmeters`. Safety is by construction — `--rf-power` has no default, bursts are refused if they would clip or overrun a duration ceiling, SWR is checked before every session, and a power ceiling guards the *receiver's* ADC rather than the PA.

Upstreamed into `M0LTE.Flex` (0.4.0 and 0.5.0, both published): the `VitaPacket` API, `FlexMeters`, the waveform transmit gating, and mock behaviour matching the real radio.

## Measured

| Quantity | Value |
|---|---|
| Power control | `rfpower` is percent of 100 W, delivered within 5% at every step |
| Linearity 1→15 W | forward +11.6 dB, received +11.2 dB — tracking within 0.4 dB, no compression |
| SWR into the dummy load | 1.30–1.33, flat with power |
| TX image rejection | −43.7 dBc |
| LO/carrier leakage at waveform centre | −27.1 dBc |
| Receiver (`ubersdr`) reference error | **−6.28 ppm** (RWM 9.996 MHz; 20 s and 60 s captures agree to 0.13 Hz) |
| Flex 6500 reference error, before correction | **−1497 ppb**, stable to ±20 ppb over three rounds |
| Path SNR at 15 W | ~24 dB in 3 kHz |
| Waveform reflection loop | 0 starved samples, once gated |

**Frequency calibration was a blocker, not a nicety.** The combined TX+RX error at 18.1 MHz is +115 Hz, **outside the demodulator's ±75 Hz acquisition grid** — nothing would have acquired, and it would have presented as a demodulator fault. `radio set freq_error_ppb=-1497` (sign confirmed by measurement: the observed error moved +87.7 → +115.1 Hz against +113.7 predicted) plus tuning the capture by the residual brings the CFO the demodulator sees to −0.1 Hz. Note the correction makes the *observed* error larger, because the two references had been partially cancelling.

**Distortion is not characterised, and the path is why.** Dropping drive 6.1 dB should improve a genuine third-order product by 12.2 dB; measured, IMD3 moved 1.1 dB and held at ≈ −28 dBc at both 15 W and 4 W. That is a floor set by the measurement, so the transmitter is *at least* that linear. The leakage path reaches only ~19 dB SNR with strong band signals in the capture span, and more signal is capped by the receiver's ADC. Deferred to the on-air phase — with the caveat that a fading path corrupts a two-tone measurement much as envelope modulation corrupts SWR, so `m9psy` fixes the coupling problem but not necessarily this one.

## Open: a 100 Hz comb on everything this radio transmits

**Found by ear, not by instrument.** Tom listened to a 30 s carrier and said it sounded raspy. The spur figure being reported at the time was −6.4 dBc, and the image −45 dBc, both of which looked fine — because the spur search masks ±200 Hz either side of the carrier so that its own skirt is not counted, and that is exactly where the problem lives. The measurement was blind to it by construction.

Looking properly (`sm-ota measure --purity`, masking only ±25 Hz):

| Source | +100 Hz | −100 Hz | Notes |
|---|---|---|---|
| Our generated IQ, never near a radio | — | — | clean to **−103 dBc** |
| Our waveform tone, 15 W | −10.8 | −13.7 | plus ±200, ±300 Hz |
| Our waveform tone, 1 W | −9.7 | −13.1 | **unchanged in dBc across 12 dB of power** |
| The radio's own tune carrier | −10.4 | −13.7 | no IQ from us, no waveform path |
| RWM 9.996 MHz (distant, via the antenna) | ≤ −27 | ≤ −28 | at its own noise floor |

At 0.183 Hz resolution the spacing is **99.6–100.0 Hz**, smeared across ~0.4 Hz over a 30 s capture — consistent with twice mains and normal grid wander, not with a fixed oscillator.

What this rules out: our software (−103 dBc), the waveform transmit path, the reflection loop, and the burst machinery — because the radio's own internally-generated tune carrier carries the identical signature. What it does not yet distinguish is **the transmitter itself versus the near-field coupling path** between dummy load and receive loop. Tom's ground-loop hypothesis fits the RWM control well: a mains-modulated common-mode current on shared cabling would modulate locally-coupled signals while leaving a distant one arriving through the antenna untouched — which is exactly the pattern.

If it is the coupling path it is a bench artefact that disappears on a real antenna; if it is the transmitter it is a real impairment. That distinction matters before E2, because hardware-in-the-loop scoring at low SNR would otherwise be measuring mains hum rather than the modem. Note it did **not** prevent WN2/WN6/WN13 decoding bit-exact at ~25 dB SNR.

Discriminating tests needing physical access: whether the sidebands appear on SmartSDR's own TX trace (before the antenna socket); whether a choke on the dummy-load feedline or moving it relative to the loop changes their level (geometry-dependent ⇒ coupling); and running the radio from a different supply.

## Bugs found, and what found them

Seven, of which six were in the instruments rather than the modem. Recorded because the pattern is the lesson: **every one of them produced plausible-looking output**, and none would have been caught by "did the payload arrive".

| Bug | Presented as | Caught by |
|---|---|---|
| Noise estimator applied the single-periodogram ln2 median correction to a Welch-averaged spectrum | a flat 1.59 dB bias on every SNR ever reported | offline test against an injected noise variance |
| Bursts not padded to whole waveform buffers | a starve on a healthy transmission | offline test, before any RF |
| **Meter payload offset applied twice** — `DispatchVitaPacket` already slices to the payload | SWR/FWDPWR/REFPWR "not sent by the radio"; six hypotheses disproved first | a raw-socket probe owing nothing to the library under suspicion |
| **Waveform sink answered the radio unconditionally** | a burst truncated to 148 of 639 buffers, no error, starve count zero | watching whether the radio still pulled after unkey; confirmed against FlexRadio's own SDK |
| **Sideband inversion in both IQ converters** (reversed convolution kernel) | nothing — the two cancelled and payloads decoded perfectly | spectral assertions against absolute frequencies, *not* round-trip recovery |
| SWR taken across key ramps, and on a modulated envelope | a dummy load reading 1.56, then 1.93 | disbelieving a number that had been 1.31 all day |
| `Lock` read after the burst ends | "NO ACQUISITION" printed beside a perfect decode | the contradiction in the same output |
| Spur search masks ±200 Hz around the carrier | a −11 dBc sideband comb reported as a clean −45 dBc signal | **a human listening to it** |

Two general lessons worth keeping:

- **An instrument built on the suspect component cannot exonerate it.** Every diagnostic in the meter investigation ran downstream of the same parse, so none could distinguish "the radio never sent it" from "we discarded it". A probe using none of the library settled it in one run.
- **Consistency is not correctness.** The sideband inversion existed in both converters and cancelled exactly, so the loopback recovered payloads bit-exact while both ends were wrong. Only assertions anchored to absolute frequencies could see it. This is the failure the plan predicted for a shared kernel and it happened anyway, independently.

## Instruments still owed

- Streaming IQ→audio conversion — the whole-file path will OOM on an hour-long capture (~691 MB/hr per receiver).
- Reference-bit re-encoding for uncoded BER/SER, matching `Ms110dMaskTests`.
- SNR estimator, with its audit against `WattersonChannel` at known SNRs. **Blocking**: an unaudited estimator would re-fit every OTA number to itself.
- The receive converter's own tests cannot see a sideband inversion — they synthesise their input with the same convention they decode it with. Worth tightening when it is made streaming.

## Provenance

Reference material read in full for this work: FlexRadio's API wiki (`smartsdr-api-docs.wiki`, pages *Metering-Protocol*, *TCPIP-meter*, *TCPIP-transmit*, *TCPIP-sub*), the MIT `kc2g-flex-tools/flexclient` (meter scaling), and FlexRadio's own GPL-3.0 waveform SDK `n5ac/smartsdr-dsp` (`sched_waveform.c`, `status_processor.c` — the transmit lifecycle). Where the documentation contradicted itself on meter scaling, the radio settled it: SWR raw 128 → 1.00 into a dummy load, `+13.8A` raw 3294 → 12.87 V, PATEMP raw 2239 → 34.98 °C.
