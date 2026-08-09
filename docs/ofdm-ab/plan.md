# OFDM-AB: research and implementation plan

Opened 2026-08-08 on Tom's direction, superseding the receive campaign's remaining legs as the
active priority. This document is the research record and the plan; it will carry the campaign's
measured results as it runs, in the same shape as [docs/ardop/plan.md](../ardop/plan.md) and
[docs/qpsk/plan.md](../qpsk/plan.md).

## What OFDM-AB is, and why this repo cares

**OFDM-AB** ("audio band") is an OFDM waveform confined to the audio passband of an ordinary FM
transceiver, so the radio emits standard FM and regulatory compliance rides on that. It comes
from the **IP400 project** of the Alberta Digital Radio Communications Society (ADRCS), by
**Martin C. Alcock VE6VH**, and it is the project's pivot away from its original 100 kbps 4FSK
plan at 420-450 MHz toward something that works with radios amateurs already own, on any band
from 10 m to 1296 MHz.

Three facts make it this repo's business rather than a curiosity:

1. **It is a soundcard modem.** Audio in, audio out, mic-and-speaker or data port. That is
   exactly what `pdn-soundmodem` is: our FM family (`afsk1200`, `fsk9600`, `c4fsk9600`) already
   runs at 12 kHz DSP on the same audio path OFDM-AB targets.
2. **The speeds are a step change for FM packet.** In a 12.5 kHz channel: 19,360 bps through
   mic/speaker, 42,353 bps through a data port, against the 9600 bps our fastest FM mode manages
   today. A four-fold improvement on the same radios and the same channel is not an increment.
3. **The lineage runs straight into our interop ground truth.** OFDM-AB is co-developed with
   **Nino Carrillo KK4HEJ** - the NinoTNC and IL2P author - and ADRCS is collaborating with
   **TARPN** to put an audio-band version into TARPN's TNC. This repo's stated ground truth is
   "NinoTNC behaviour"; if OFDM-AB lands in a NinoTNC, it stops being an optional mode and
   becomes something we must speak.

## What is published, as of 2026-08-08

The only technical publication is a 14-slide introduction deck (preserved; see Provenance). It
gives the mode's outline and its numbers, and no waveform specification.

### Bandwidth profiles, on 12.5 kHz channel spacing

| Mode | Audio bandwidth | Application | Radio connection |
|---|---|---|---|
| Narrowband (NB) | 2,625 Hz | most FM radios | mic and speaker |
| Enhanced NB (ENB) | 3,656 Hz | radios without a sharp high-end cut-off | mic and speaker |
| Wideband (WB) | 5,766 Hz | radios with direct modulator connections | data port |
| Enhanced WB (EWB) | 7,781 Hz | high-end radios with extended passbands | data port |

Bandwidth adaptation is automatic and needs no handshake, which implies the preamble announces
the profile (or the receiver measures it) - an important unknown, since it is the first thing a
receiver must get right.

### Data rates (bps, raw - before FEC, which is not in the product yet)

| Constellation | bits/carrier | NB | ENB | WB | EWB |
|---|---|---|---|---|---|
| BPSK | 1 | 2,426 | 3,353 | 5,294 | 7,147 |
| QPSK | 2 | 4,853 | 6,706 | 10,588 | 14,294 |
| QPSK-8 (8PSK) | 3 | 7,260 | 10,059 | 15,882 | 21,441 |
| QAM-16 | 4 | 9,680 | 13,412 | 13,412 (*) | 28,588 |
| QAM-32 | 5 | 12,132 | 16,765 | 16,765 (*) | 35,735 |
| QAM-64 | 6 | 14,520 | 20,118 | 31,765 | 42,882 |
| QAM-128 | 7 | 16,940 | 23,471 | 37,059 | 50,029 |
| QAM-256 | 8 | 19,360 | 26,824 | 42,353 | 57,176 |

(*) The WB column repeats ENB's values on those two rows, inconsistent with every other row;
scaling WB's own BPSK rate gives ~21,176 and ~26,470. A transcription slip in the deck.

### The geometry, inferred (this is inference, not specification)

Every published bandwidth divides by **46.875 Hz** to within rounding:

| profile | bandwidth | / 46.875 |
|---|---|---|
| NB | 2,625 Hz | 56.0 |
| ENB | 3,656 Hz | 78.0 |
| WB | 5,766 Hz | 123.0 |
| EWB | 7,781 Hz | 166.0 |

**Treat this inference as unreliable.** It is arithmetic on ADRCS's published bandwidth figures and
nothing more; later information indicates it is wrong, and no number derived from it should be
built on. It is kept here as the record of what could be worked out from the public material, not
as a specification. The mode's own spec is unpublished and unfinished, and remains the only
authority.

Four for four on integers is not coincidence, and 46.875 Hz is exactly 48000/1024, 24000/512 and
**12000/256** - the spacing an implementer picks when the FFT is a power of two at a standard
audio rate. So: subcarrier spacing 46.875 Hz, 56 / 78 / 123 / 166 subcarriers, useful symbol
21.33 ms. The quoted rates then imply a per-carrier symbol rate near 43 baud, i.e. a cyclic
prefix around 8-9 % (about 2 ms). Long symbols, which is the right choice against FM flutter and
multipath, and at our 12 kHz FM DSP rate a 256-point FFT lands exactly on the spacing.

Everything else a receiver needs is unpublished: pilot count and placement, preamble and
synchronisation, how the bandwidth profile and constellation are signalled, framing and CRC
placement, bit-to-subcarrier mapping and interleaving, scrambling, and the Trellis code that
arrives later.

### Availability and licence, stated plainly

- **No OFDM-AB source is public.** The published roadmap puts OFDM-AB in firmware v2.1 (Q4 2026,
  up to QAM-64, all bandwidths, **CRC only**), QAM-128/256 and **Trellis FEC** in v2.2 (Q2 2027).
  The reference implementation runs OFDM subcarrier processing on an Efinix Trion FPGA in the
  Advanced Mesh Network Controller, production Q4 2026.
- **The upstream repository carries no licence** (`license: null`), and on 2026-08-08 it deleted
  its Documentation, Node Firmware, SDCard and Wireshark directories in a "reboot". The project
  calls itself open source and invites contributions, but until a licence exists, nothing there
  may be copied into this GPL-3.0-or-later repo. A clean-room implementation from a published
  specification is unaffected, and is the only route worth planning around.

## What we would have to build, honestly

We have a great deal of the surrounding machinery and almost none of the waveform.

**We have**: the mode-generic sim harness (`SimModem`, `SimBench`, `sm-ota sim`) that would drive
an OFDM-AB modem the day it exists; the Watterson rig and the two-tier mask discipline; an
`IModem` seam and KISS/daemon plumbing that a new mode drops into; QAM experience from the ARDOP
work (8PSK/16QAM demodulation, and the measured lesson that dense constellations die on fading
channels); an FFT in `M0LTE.Dsp`; and the FM audio path at 12 kHz.

**We lack**:

- **A native OFDM chain.** The FreeDV datac family is OFDM, but it is codec2's engine behind a
  wrapper - we have never written OFDM ourselves. Needed: framing with cyclic prefix, coarse and
  fine timing sync, fractional and integer frequency offset correction, per-subcarrier channel
  estimation and equalisation, pilot tracking, QAM slicing to soft bits. This is the bulk of the
  work and it is reusable well beyond OFDM-AB.
- **An FM channel model.** rx-roadmap workstream 6 item 6 has this recorded as the gate on masks
  for every FM mode: deviation error, pre-emphasis/de-emphasis mismatch, discriminator noise,
  flutter. OFDM-AB is *the* mode that needs it, because the whole point of the design is that it
  survives an ordinary radio's mic-and-speaker path - which is a pre-emphasised, band-limited,
  AGC'd, possibly companded channel that our sim models not at all. Masking OFDM-AB against AWGN
  alone would be measuring the wrong thing.
- **The specification.** Without it there is no interop, and interop is the entire point.

## Plan

Phases in the MS110D discipline: instrument first, measure before building, honest negatives with
mechanism, and a ledger entry when a mode's status changes.

- **O0: ask, and preserve.** Two actions, both cheap and both high-value. **(a)** Write to ADRCS
  (info@adrcs.org, ve6vh@adrcs.org) asking for the OFDM-AB waveform specification and a licence
  on the published sources, introducing this repo as a prospective independent implementation.
  The project explicitly invites contributions and says it wants OFDM-AB to be an open standard
  with interoperable implementations, so this is a request it is set up to welcome, and Nino
  Carrillo's involvement means our NinoTNC interop record is a credential. **(b)** Preservation:
  done 2026-08-08 (see Provenance). *Exit: a reply, or a recorded non-reply after a fair
  interval.*
- **O1: the FM channel model - DONE 2026-08-08** (rx-roadmap workstream 6 item 6, which carries
  the measured ladders). `FmChannel` models the link physically - modulate, noise on the carrier,
  limit, discriminate - so the threshold effect, the discriminator's rising noise spectrum, IF
  truncation, emphasis and deviation error all emerge rather than being asserted. Two results
  bear directly on OFDM-AB. The **rising noise spectrum** means its top subcarriers are
  measurably noisier than its bottom ones, so a per-subcarrier SNR tilt is a design fact and not
  a detail; masking OFDM-AB against flat AWGN would have measured the wrong thing. And **IF
  truncation puts a distortion floor under a noiseless link** (third harmonic ~-25 dB on an 8 kHz
  filter at full deviation), which is the ceiling QAM-256 meets before it meets noise. The
  microphone path is now modelled explicitly, and the model reproduces unprompted the reason
  9600 baud packet needs a data port - which is the same wall OFDM-AB's narrowband profiles are
  drawn to avoid. Original rationale, kept because it still holds: it unblocks masks for `afsk1200`, `fsk9600`, `c4fsk9600` and `qpsk3600` as well, and
  it is a prerequisite for any honest OFDM-AB number. Calibrate against real radios where we can
  (the FM deviation instrument `sm-ota fm-deviation` exists, and the mode-modulation reference
  records per-mode peak-deviation targets). *Exit: an FM channel axis on the Watterson rig, its
  parameters calibrated against a real radio path rather than invented, and the existing FM modes
  measured through it.*
- **O2: the OFDM core, which is OFDM-FM and not this mode - BUILT 2026-08-08.** Researching OFDM-AB
  produced a working audio-band OFDM modem, and the honest thing to call it is **OFDM-FM**: OFDM
  over an FM audio path, our own waveform, not aiming at compatibility with anything. It is named
  `OfdmFm*` throughout and lives at `Packet.SoundModem.Modems.OfdmFm`: real-FFT symbols
  with a cyclic prefix, a self-correlating sync symbol (two identical halves, so timing survives a
  channel that would defeat matching against a clean reference), a channel estimate taken from a
  known preamble symbol, pilot-tracked residual phase, Gray-coded constellations from BPSK to
  QAM-256, and a CRC-checked frame whose header announces its own constellation and length. 17
  tests, all on **synthetic geometry** - see below. Not yet wired to `IModem`, the sim harness or
  the catalogue; that waits until there is a waveform worth registering.

  Everything below this point is a measurement of OFDM-FM. It is recorded in this document because
  this is the campaign that produced it, not because it says anything about OFDM-AB's performance,
  which nobody outside the project has measured.

  **The geometry is not in this repository, deliberately.** The profiles OFDM-FM actually runs were
  sized against what we know of OFDM-AB's parameters, which came unofficially from the mode's
  author, who is staying quiet publicly so the organisation funding the project can be its
  information source. `OfdmFmParameters.Synthetic` is a small
  invented profile that exercises every code path and resembles nothing; the working profiles live in
  an untracked `ofdm-fm.local.json` (gitignored) and are loaded at run time. That has a second
  benefit worth having on its own: every part of the implementation had to be geometry-generic,
  which is exactly what you want while a geometry is still moving.

  Two bugs found by pointing the code at a real geometry, both of the kind that produce a signal
  which looks healthy and decodes to nothing. The first cut normalised every symbol to its own
  peak, which rescales each symbol differently and destroys the amplitude a QAM constellation
  carries information in. And the sync symbol modulated every second *occupied carrier* rather
  than every even *absolute bin* - a bin repeats over half a symbol only if its index is even, so
  an odd first carrier gave two sign-flipped halves and a correlation peaking at -1 where the
  search looked for +1. A synthetic profile with an even first carrier cannot catch that, so a
  parity theory now guards it.

  **Coding and bit loading, added 2026-08-08, both as config** (`OfdmFmCoding`,
  `OfdmFmBitLoadingTier` on the profile, so the scheme is a parameter exactly like the geometry -
  OFDM-AB's own FEC is "to be determined", so ours stays a knob). Rate-1/2 tail-biting convolutional, K=7 (the
  classic 0o133/0o171 pair 802.11a carries), soft-decision Viterbi with max-log LLRs from the
  equalised constellation, puncturable to 2/3 and 3/4, and a frequency interleave.

  **Measured through the FM link, narrowband profile, QPSK, 8 seeds a point.** Microphone and
  speaker path, frames recovered of 8:

  | coding | +40 | +34 | +28 | +24 | +20 | +16 |
  |---|---|---|---|---|---|---|
  | none | 8 | 8 | 8 | 2 | 0 | 0 |
  | conv 1/2 | 8 | 8 | 8 | 8 | 6 | 0 |
  | conv 2/3 | 8 | 8 | 8 | 8 | 4 | 0 |
  | conv 3/4 | 8 | 8 | 8 | 8 | 2 | 0 |

  So **the code is worth about 5-6 dB** at rate 1/2, and the puncturing ladder falls in the
  expected order. The cliff at +16 dB is not a coding limit at all - it is the FM threshold, below
  which the discriminator hands up click noise and no code helps. There is no point spending rate
  below where the link itself collapses.

  **Bit loading: a prediction of ours that measurement corrected.** Uniform QPSK against a
  bit-loaded profile carrying the identical 220 bits per symbol, just distributed toward the quiet
  end of the band. On the microphone path loading made things consistently WORSE (uncoded 8/8/8/2
  became 8/8/0/0). The reason is one this repo already documented: **de-emphasis flattens the
  discriminator's rising noise**, which is what an emphasis pair is for. Measured per-carrier SNR
  on that path is flat - 11.4 to 13.6 dB from 305 Hz to 2742 Hz - so there is no gradient for
  loading to exploit, and spending three bits on a carrier no better than its neighbours simply
  breaks it.

  On the **data-port path, with no de-emphasis, the gradient is real and large**: 22.0 dB at
  305 Hz falling to 4.7 dB at 2742 Hz, a 17 dB slope, exactly the f-squared triangular noise. There
  loading helps in every pairing measured (uncoded +1 of 8 at +24 dB, conv 1/2 7/8 -> 8/8, conv 2/3
  6/8 -> 7/8). The gain is modest because the tiers are a hand-picked three-step guess rather than
  water-filled to the measured curve; deriving them from the per-carrier SNR is the obvious next
  step.

  **So the recommendation sharpens: bit loading is for the flat-audio data-port profiles, not the
  emphasised microphone ones.** On a mic path the emphasis network has already done the
  equalisation, and loading is not just useless there but harmful.

  Two things found on the way. A round trip through `M0LTE.Fec.GpInterleaver` was returning about
  a third of a block wrong, and this was first written up as a library defect. **That was wrong and
  is corrected here**: the third parameter is the ELEMENT COUNT, not the stride, and passing
  `ChooseB(n)` there permutes only a prefix of the block with the wrong stride. The library is
  correct, the call was not, and the chain now uses the shared interleaver as intended. The
  round-trip test stays, because the failure mode is silent - a mispaired interleaver decodes every
  coded burst to noise and nothing else says why. And at burst level on the synthetic profile the payload code cannot show its value, because the
  **header is uncoded BPSK and fails first**: whatever codes the payload should cover the header
  too, or the header is the burst's floor.

  Original rationale, kept: Build the reusable chain - CP framing, sync,
  per-carrier equalisation, QAM soft-slicing - and validate it against our own sim at the
  inferred geometry (46.875 Hz spacing, 56/78/123/166 carriers, ~2 ms CP; marked unreliable in
  `d0e5b7f`). This is not OFDM-AB and must not be called it - hence OFDM-FM; it is the machinery
  that makes implementing OFDM-AB a matter of
  filling in a spec's constants, plus a working answer to "can we even do OFDM here". *Exit: a
  loopback modem carrying frames at each profile, an AWGN and FM-channel ladder for each
  constellation, and the honest gap against the published rate table.*
- **O3: interop, when the spec or firmware lands.** Whichever arrives first - specification,
  source with a licence, or hardware we can capture off the air - becomes ground truth, and the
  campaign switches to the pattern that worked for ARDOP: capture real traffic, replay it, and
  referee frame by frame against the reference. *Exit: bit-exact interop with a reference
  implementation, or a documented list of what differs.*
- **O4: deployment.** Mode registered in `ModemCatalog`, masks pinned in both tiers, ledger entry,
  daemon config, KISS. *Exit: a station can run it.*

O1 and O2 can proceed in parallel with O0's wait and are useful whatever the answer. O3 is
blocked on someone else's timeline: firmware Q4 2026 at the earliest, FEC Q2 2027.

## Risks and honest limits

- **The spec may never be detailed enough.** The reference is FPGA firmware; there may be no
  document that pins pilot placement and sync to the bit. If so, interop needs captured signals
  and reverse engineering, which is slower and needs hardware on the air near us.
- **No soft reference decoder.** ARDOP's campaign leaned entirely on running `ardopcf` over
  bit-identical audio. An FPGA reference gives us no such referee, so our A/B discipline loses
  its strongest instrument and captures become the only truth.
- **The mic/speaker path is the crux and the least modelled thing we own.** OFDM through
  pre-emphasis and a speaker output is where this mode either works or does not, and our sim
  currently says nothing about it. O1 exists because of this.
- **Timelines are not ours.** Everything interop-facing waits on Q4 2026 firmware at the
  earliest. Plan the work that pays regardless, and do not let the schedule drive scope.
- **Licence contamination.** Until ADRCS publishes a licence, no IP400 code may be read into this
  repo's sources. The archive is private and reference-only, and the provenance rule in
  [CLAUDE.md](../../CLAUDE.md) applies with extra force here.

## Provenance and preservation (2026-08-08)

Upstream `github.com/adrcs/ip400` deleted its `Documentation/`, `Node Firmware/`, `SDCard/` and
`Wireshark/` directories between 00:49 and 00:59 UTC on 2026-08-08, replacing the README with a
note about a forthcoming release. Because that content is the only public record of the project's
frame format and node software, it was preserved the same day at Tom's direction:

- **`M0LTE/ip400-archive`** (private): a full `git clone --mirror` pushed into a newly created
  repository - **not a GitHub fork**, so it is independent of upstream's continued existence
  (`fork: false`, no parent, no source). Branch `pre-reboot-2025-10-29` and tag
  `pre-reboot-snapshot` point at the last commit before the deletions, whose working tree holds
  all 148 files. An `archive-notes` branch carries the provenance record and this technical
  extract; `main` is left as a faithful mirror.
- **`/home/tf/ip400-archive.git`**: a bare local mirror, verified complete (754 objects,
  192.69 MiB pack).

The recovered `Wireshark/ip400.fdesc` is worth naming here because it is the one hard
specification the project has published: it defines the IP400 frame precisely - eye, status,
offset, length, packed from/to callsigns and ports, a 16-value message type (text, audio, video,
data, beacon, encapsulated IP, AX.25, DTMF, DMR, D-STAR, P25, NXDN, M17, command), then flags with
hop count, repeat/connectionless/command bits, extended-callsign bits, a 2-bit compression field
and the hop table. That is the network layer, independent of the PHY, and implementable from the
dissector alone. Note that reading it for interop is legitimate; copying the project's C sources
is not, absent a licence.

## Sources

- IP400 project site: <https://ip400.adrcs.org/>, introduction deck
  `IP400-OFDB-AB-Introduction.pdf` (2026-08)
- ADRCS: <https://adrcs.org/adrcs/ip400-network-project/>
- Upstream sources: <https://github.com/adrcs/ip400>
- Zero Retries 0259, Steve Stroh N8GNJ: <https://www.zeroretries.org/p/zero-retries-0259>
- ADRCS/TARPN collaboration announcement (2026-02), via Amateur Radio Daily and the ICQ Podcast
