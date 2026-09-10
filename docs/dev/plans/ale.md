# 2G ALE - implementation plan

**Status: plan only. No code exists.** Written 2026-07-26.

**Charter.** Implement MIL-STD-188-141A Appendix A second-generation Automatic Link Establishment in software, so an MS110D station can find a working channel and a listening correspondent without an operator and without a vendor option board.

**Why now, and why software.** The obvious hardware route - a Kenwood TK-90 with its KPE-2 ALE unit - is closed: the board is [hard or impossible to obtain](../ms110d/radio-tk-90-evaluation.md). Doing it ourselves is not merely a workaround. It removes the dependency on any particular radio, it makes the link-quality metric *our* measured SNR rather than a vendor's opaque score, and it is a smaller piece of work than the modem this repo has already built.

**Non-goals.** 3G ALE (MIL-STD-188-141B Appendix C / STANAG 4538) - it is synchronous, needs time-of-day at both ends, and drags the ARQ data-link protocols in with it. Wideband ALE (141D). Any traffic-carrying protocol: ALE hands over a channel and stops.

---

## 0. Provenance discipline - read this before writing any code

**Every constant in §2 below is unverified against the primary source.** They are cross-checked across three independent secondary references and they agree, which is worth something and is not worth building on. This repo's MS110D work sets the standard: `docs/ms110d/design.md` records a permanent PDF ID *and* a stamp-invariant SHA-256, because everyspec rewrites part of the trailer on every download and a naive hash is therefore not reproducible. 2G ALE gets the same treatment or it does not get built.

**Primary source.** MIL-STD-188-141A Appendix A. The same 2G waveform is carried forward in 141B and 141C Appendix A, so a later revision is an acceptable primary as long as the revision is recorded. A public copy of **141C** is hosted at `hflink.com/standards/MIL_STD_188-141C.pdf`; everyspec carries the family. FED-STD-1045 is the federal equivalent and a useful cross-check on anything ambiguous.

**Phase A0 is the transcription**, and nothing else starts until it is done. Tables go in `docs/ale/tables/` as CSV with a `README` recording page numbers, exactly as `docs/ms110d/tables/` does.

**Licence care.** Consulting a reference implementation is allowed and encouraged, but this repo is GPL-3.0-or-later: anything consulted must be GPL-compatible, and anything derived from it gets a comment naming the source file and function - the same rule that governs the QtSoundModem and Dire Wolf lineage in this codebase.

---

## 1. Scope: what "implementing ALE" actually means

Four layers, and it is worth being explicit because only the first is DSP:

1. **Waveform** - 8-ary FSK modulator and demodulator.
2. **Word layer** - Golay coding, interleaving, redundant transmission, majority voting. Turns a noisy tone stream into reliable 24-bit words.
3. **Protocol** - addressing, the call/response/acknowledge handshake, scanning, sounding, link maintenance and teardown.
4. **Application** - LQA scoring, channel selection, AMD short messages, and the handover to the modem.

Layers 1 and 2 are a weekend's DSP by this codebase's standards. **Layer 3 is where the real work is**, and it is where a naive implementation will fail interop.

---

## 2. The waveform - unverified summary

Cross-checked against [sigidwiki](https://www.sigidwiki.com/wiki/Automatic_Link_Establishment_(2G_ALE)), [Wavecom's decoder documentation](http://www.wavecom.ch/content/ext/DecoderOnlineHelp/worddocuments/mil188141a.htm) and [I-56578's signal analysis](http://i56578-swl.blogspot.com/2014/05/mil-188-141ab-mfsk-8.html), which agree. **To be replaced by transcription from the standard in A0.**

| Parameter | Value |
|---|---|
| Modulation | 8-ary FSK, incoherent |
| Tones | 750 - 2500 Hz, **250 Hz** spacing (8 tones) |
| Symbol period | **8 ms** → 125 Bd |
| Bits per symbol | 3 → **375 bit/s** raw |
| Word | **24 bits** = 3-bit preamble (word type) + 21 bits (three 7-bit ASCII characters) |
| FEC | **Golay (24,12)** on each half, then interleaved |
| On-air word | 48 bits + 1 stuff bit = **49 symbols = 392 ms** |
| Redundancy | Words sent repeatedly; receiver majority-votes across receptions |

Three observations that shape the design:

**It is orthogonal, so detection is easy.** Noncoherent FSK tones are orthogonal at 1/T spacing; T = 8 ms gives 125 Hz, and the tones are spaced 250 Hz. A bank of eight correlators (or Goertzel bins) per symbol is sufficient - no equaliser, no carrier recovery, no phase tracking. Compared with MS110D's probe-trained DFE and turbo equaliser this is trivial, and that comparison is the whole argument for doing it.

**It fits a narrow filter.** 750-2500 Hz sits comfortably inside even the TK-90's 2.2 kHz built-in filter. Whatever radio ends up carrying this, ALE will not be the part that suffers.

**The sample rate wants care.** MS110D runs at 9600 Hz natively throughout this repo, and 8 ms at 9600 Hz is **76.8 samples** - not an integer, which makes symbol timing needlessly awkward. At **8000 Hz** a symbol is exactly 64 samples, and 750-2500 Hz sits well inside Nyquist. 9600 → 8000 is a clean 5/6 rational resample, and 48000 → 8000 is a simple ÷6 straight from a capture. **Recommend 8 kHz internally**, with resampling at the boundary rather than fractional symbol timing throughout.

---

## 3. Architecture

```
src/Packet.SoundModem/Ale/          the library
  AleTone.cs                        8-FSK modulator / noncoherent detector
  Golay24.cs                        Golay(24,12) encode + decode (3-error correcting)
  AleWord.cs                        word types, ASCII-38 alphabet, interleave, stuff bit
  AleReceiver.cs                    symbol sync, word framing, redundant-word voting
  AleProtocol.cs                    call / response / acknowledge state machine
  AleStation.cs                     addresses, nets, scanning, sounding, LQA store
tests/Packet.SoundModem.Tests/Ale/
docs/ale/tables/                    transcribed from the standard (A0)
```

**Reuse rather than rebuild.** `M0LTE.Dsp` supplies `FilterDesign`, `FirFilter`, `Fft`, `Decimator` and the SSB demodulators. The OTA harness already has everything needed to test it on air: `LadderPass` renders, `StreamingSsbDemodulator` converts, `sm-ota monitor` watches live. The Watterson rig is already compiled into the OTA tool and gates MS110D - it should gate ALE too.

**Golay(24,12) is new.** The repo's `Fec/` has CRC-16/X-25, Hamming(7,4) and Reed-Solomon GF(2⁸), but no Golay. It is a small, self-contained, exhaustively-testable piece of work: a 12×12 generator matrix, syndrome decoding to 3 errors, and a test that enumerates every correctable error pattern - which for this code is cheap enough to do exhaustively rather than statistically.

---

## 4. Phases and gates

Each phase closes on a stated, measurable gate. No phase starts before the previous one closes - the MS110D programme's central lesson is that plausible work built on unverified foundations produces numbers that die under audit.

### A0 - the standard, transcribed *(blocking everything)*

Obtain the primary source, record its identity the way `ms110d/design.md` records 110D's, and transcribe the tables that matter: word types and their preambles, the ASCII-38 alphabet, the Golay generator, the interleaver, the tone assignments, and the protocol timing constants.

**Gate:** every constant in §2 either confirmed or corrected, with a document page reference. Any figure the standard leaves in a figure rather than text is flagged, as `d20-widpn` was for MS110D.

### A1 - waveform

8-FSK modulator; noncoherent demodulator with symbol timing recovery.

**Gate:** modulate → demodulate recovers symbols bit-exact on a clean channel. **And an absolute-frequency assertion**: a tone generated for symbol *n* must be measured at the frequency the standard specifies, not merely at "whatever the demodulator expects". This repo has already paid once for tests that synthesised their input with the same convention they decoded it with - both IQ converters carried the same sideband inversion, cancelled exactly, and recovered payloads bit-exact while both were wrong. Do not repeat it.

### A2 - word layer

Golay, interleaving, stuff bit, redundant transmission, majority voting.

**Gate:** exhaustive Golay test (every 1-, 2- and 3-error pattern corrected, every 4-error pattern detected-or-miscorrected as the code actually behaves - measured, not assumed). Word recovery through AWGN at a stated SNR, with a curve rather than a single point.

### A3 - protocol

Addressing, the three-way handshake, link state, teardown.

**Gate:** two instances link to each other through the Watterson rig over a simulated channel, and correctly fail to link when the channel is too poor. A link that establishes when it should not is worse than one that fails.

### A4 - scanning, sounding, LQA

Multi-channel scan, call duration covering a scan cycle, sounding, LQA scoring and storage.

**Gate:** a scanning station is reliably called on any channel of its scan set. **LQA is calibrated against measured SNR** - see §5.

### A5 - AMD, and the handover to the modem

Short messages; the state machine that leaves scan, runs an MS110D exchange, and resumes.

**Gate:** end-to-end in simulation - link, hand over, transfer a scored MS110D burst, terminate, resume scanning.

### A6 - interop

**Gate:** decode real off-air ALE (see §5), and exchange links with a real ALE station.

---

## 5. Test strategy

Mirror MS110D's, because it worked: offline loops, absolute assertions, statistical gates over AWGN and Watterson Poor, and instruments audited as hard as the code.

**The standout opportunity: decode real off-air 2G ALE, immediately, for free.** HF carries a great deal of live 2G ALE traffic, and this project already owns everything needed to capture it - a wideband receiver, a streaming IQ→audio converter, and a live monitor. There is no transmit licence, no partner station and no hardware purchase involved.

That makes the usual order of things invertible, and much better:

- **Build the demodulator first and prove it against real signals before writing a single line of modulator.** Real off-air ALE is ground truth of a kind the MS110D work never had at this stage - it had to be generated before it could be received. Here the world is already transmitting a reference signal, continuously, from many different implementations.
- It tests **interop first rather than last**, which is where interop failures are cheap to fix. Kenwood's own manual admits 2G framing varies between manufacturers ("the AMD frame must be changed if the transceiver cannot link with other manufacturers transceivers"); hearing that variety early is worth a great deal.
- Decoded callsigns are self-validating. An address that decodes to a plausible callsign, repeatedly, from a station that keeps sounding on a schedule, is a decode that is almost certainly correct.

**Do this in A1/A2, not in A6.** The phase list above puts interop last by convention; for the receive side it should come first. Concretely: `sm-ota monitor` already captures and converts; adding an ALE decoder to that path is the natural first deliverable, and it needs nothing this project does not already have.

**LQA must be calibrated, not invented.** The whole point of doing ALE ourselves is that our link-quality metric can be the SNR and uncoded BER the scorer already measures. But a score is only useful if it predicts which MS110D waveform will work, and the gate table spans −6 dB (WN0) to +16 dB (WN8) - 22 dB. So A4's gate is not "LQA produces a number" but **"LQA predicts the highest waveform that will decode, and the prediction is measured against `BurstScore.UncodedBer`"**. That is a genuinely novel and useful thing this implementation could have and a vendor's cannot.

---

## 6. Risks and open questions

| Risk | Handling |
|---|---|
| Constants in §2 wrong | A0 gates everything. They are secondary-source figures until then. |
| Protocol timing under-specified in the standard | Cross-check FED-STD-1045; measure real off-air traffic, which is the arbiter anyway. |
| Interop framing variation between vendors | Decode off-air early and widely (§5). Kenwood documents that this variation exists. |
| Scanning and traffic are mutually exclusive radio states | Inherent to ALE, not to our implementation - see the TK-90 evaluation. Design the handover explicitly rather than discovering it. |
| Effort underestimated because the DSP looks easy | The DSP *is* easy. Layer 3 is the work. Budget accordingly. |
| Distraction from MS110D | **This is the main risk.** MS110D Phase B is not closed and §E2 has never run on hardware. See below. |

**Sequencing against MS110D.** This plan is deliberately not scheduled. MS110D's Poor-channel gate is open, and the OTA campaign has not yet run a single ladder over the air. ALE is the right thing to build *after* those close, not instead of them - with one exception: **the off-air receive experiment in §5 is cheap, needs no hardware, and could be done in an afternoon** whenever a break from the modem is wanted. It would also settle whether the constants in §2 are right, which is A0's job done empirically.

---

## 7. What it unlocks

- **Unattended operation.** A station that finds its own channel and its own correspondent is the difference between a demonstration and a service.
- **Radio independence.** No vendor option board, no particular manufacturer.
- **A calibrated link-quality metric**, tied to the same SNR and BER measurements the OTA harness already produces - which is strictly more useful than any vendor's LQA, because it predicts waveform choice rather than merely link existence.
- **A path to the rest of the family.** If this project ever climbs Appendix D's bandwidth column beyond 3 kHz, 2G ALE cannot follow - its channel concept assumes voice bandwidth. A software link layer is the one that could be taken forward; a hardware one never could.
