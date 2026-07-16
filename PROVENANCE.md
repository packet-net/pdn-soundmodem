# Provenance

What each part of this codebase is truly based on. Nothing here is a line-by-line
translation of another program, but several components are genuinely derivative —
exact formulas, constants and bit-level behaviours taken from GPL sources — which is
why this repository is GPL-3.0-or-later and must stay that way (see COPYING and
CLAUDE.md § Licence rules).

## The four kinds of source

1. **Published specifications, read directly**: the
   [IL2P specification draft v0.6](https://tarpn.net/t/il2p/il2p-specification_draft_v0-6.pdf)
   (Nino Carrillo KK4HEJ) including its worked example packets (provided by Jon Naylor
   G4KLX); the AX.25 v2.2 specification (HDLC rules, CSMA); the KISS specification; the
   FX.25 draft (Stensat, 2006, via archive.org).
2. **Dire Wolf source, read directly during development** (GPL-2.0-or-later,
   © John Langner WB2OSZ): `il2p_scramble.c`, `il2p_header.c`, `il2p_init.c`,
   `fx25_init.c`, `fx25_send.c`, `fx25_rec.c`, `gen_tone.c`, `hdlc_rec.c`,
   `hdlc_send.c`, `demod_9600.c` (in part).
3. **QtSoundModem** (GPLv3+, © Andrei Kopanchuk UZ7HO, ported by John Wiseman G8BPQ),
   read in full by the research phase that founded this project (see
   packet.net `docs/research/headless-soundmodem.md`): its findings supplied algorithms,
   formulas and constants used here. No QtSoundModem C was transcribed.
4. **Empirical determination**: several wire-format facts were established by
   experiment, not by any document — encoding against the IL2P spec's example packets
   byte-for-byte, decoding Dire Wolf-generated audio and having Dire Wolf's `atest`
   decode ours (bidirectional cross-validation, fixtures committed in
   `tests/.../Fixtures/`), and benchmarking against the WA8LMF TNC Test CD. The
   CRC-16/X-25 variant, the legacy max-FEC header bit behaviour, the 9600 IL2P baseband
   polarity, and the NRZI/scramble ordering are all in this category.

## Per component

| Component | Basis |
|---|---|
| `Il2p/*` (codec, header, blocks, scrambler) | IL2P spec v0.6 first: Hamming encode/decode tables **verbatim from the spec**, block-size computations, sync word, RS parameters. The scrambler's exact bit expressions and initial states (0x00F/0x1F0, 5-bit Galois delay, flush) and the header's bit-field placement are **from Dire Wolf's source** — the spec presents those only as figures. Byte-exact against the spec's three example packets. |
| `Fec/ReedSolomon` | Independent implementation of the textbook algorithms (systematic LFSR encoder; Berlekamp-Massey, Chien search, Forney — the Forney exponent derived from first principles during development). Field/root parameters from the specs and Dire Wolf's init tables. The least derivative DSP component. |
| `Fec/Crc16X25`, `Fec/Hamming74` | CRC: the standard X-25 algorithm, variant pinned by the spec's S-frame example (0xF0DB). Hamming: both tables verbatim from the spec. |
| `Fec/Ldpc/*` (FreeDV datac codes) | **codec2 lineage (David Rowe / Bill Cowley VK5DSP / Valenti CML), LGPL-2.1.** The five sparse parity-check matrices (`H_128_256_5`, `H_256_512_4`, `H_1024_2048_4f`, `H_4096_8192_3d`, `HRA_56_56`) and the built-in decode-oracle vectors are **transliterated verbatim** from codec2 1.2.0 (git 310777b) by `tools/gen-ldpc-tables/gen.py` (a port of the data, not a re-derivation). The sum-product decoder, `phi0` table and RA encoder are ports of codec2's `mpdecode_core.c`/`phi0.c`. Being a derivative of LGPL-2.1 code relicensed into GPL-3.0-or-later via LGPL-2.1 §3 — **pending a FOSS-licence review (docs/ofdm-design.md open item R-1)** before release. `.g.cs` tables are generated output committed like the WAV fixtures. |
| `Hdlc/*` (framer, deframer, NRZI) | AX.25/HDLC specification; textbook. Unavoidably similar to every implementation because the protocol dictates the machine. |
| `Modems/Afsk1200Demodulator` | **UZ7HO lineage via QtSoundModem**: chain architecture (BPF → complex mix → I/Q LPF → discriminator), the discriminator formula `(I₀−I₂)Q₁−(Q₀−Q₂)I₁`, and the 1400/650 Hz filter plan are QtSM's `Mux3`/`MODEM_1200`. The power normalisation, silence clamp and envelope min/max slicer are original to this project, developed against the WA8LMF corpus (QtSM solves those differently). |
| `Modems/Afsk1200MultiModem` (offset bank, emphasis variants) | UZ7HO's concepts (`RCVR` offset pairs, `emph_all`); implementations and the content-dedup scheme are original. |
| `Modems/BitDpll` | Dire Wolf's DPLL design and inertia constant (sample at wrap, transitions nudged toward zero, 0.74). The **sub-sample crossing interpolation is original to this project** (neither source has it). A searching/locked inertia switch was tried and regressed — see the comment. |
| `Modems/PacketDcd` | Dire Wolf 1.6's DCD algorithm and constants (±0.125 phase window, 32-transition history, assert ≥30/32, drop ≤6/32) from `fsk_demod_state.h`/`hdlc_rec.c`; fresh code. |
| `Modems/EnergyBusyDetector` | Concept prompted by QtSM/ARDOP's spectral busy detector; the actual design (block power vs min-tracking floor, 6/3 dB hysteresis, hold, warm-up seeding) is original. |
| `Modems/Bpsk300*`, `Modems/Qpsk*`, `Modems/CostasLoop` | Symbol maps directly from the IL2P spec; per-mode filter numbers follow QtSM's tables; the fractional one-symbol delay is original. Two detectors: the default coherent path is a textbook decision-directed 2nd-order Costas loop (standard PLL gain equations, decision-directed M-PSK phase detectors) recovering the carrier and differentially decoding the recovered absolute symbols — the NinoTNC's method, implemented fresh; the differential detector is generic DSP. Per-mode loop bandwidths and the coherent-over-differential margin were tuned/verified by measurement in-project (issue #5), not copied. |
| `Modems/Fsk9600Modem`, `G3ruhScrambler` | G3RUH scrambler expressions bit-exact from Dire Wolf (`gen_tone.c`/`hdlc_rec.c`), NRZI→scramble ordering confirmed from their source and by bidirectional audio validation. Receive chain outline is `demod_9600` lineage with this project's envelope slicer; the TX pulse shaping deliberately differs from Dire Wolf's phase-pinning synthesis (compatibility proven empirically). |
| `Fx25/*` | Correlation-tag table, block formats and the 8-bit match tolerance verbatim from Dire Wolf's `fx25_init.c` (protocol constants originating in the Stensat draft); wire behaviour (flag fill, LSB-first, NRZI placement, accumulator direction) from reading their send/receive paths; fresh implementation with an added miscorrection guard. |
| `Modems/C4fskModem` | Wire format from **MMDVM-TNC** (Jonathan Naylor G4KLX, GPL-2.0-or-later): the 0x77 preamble byte, the 4-byte outer-only sync 0x5D57DF7F, the dibit→level map and the "Mode 2 = IL2P bytes on 4-PAM" structure are its `Mode2Defines.h`/`Mode2TX.cpp`/`IL2PRX.cpp` constants and layout, which the NinoTNC C4FSK modes inherit. Established empirically first (symbol analysis of NinoTNC recordings, one error in 316 symbols against a known frame), then confirmed against the MMDVM-TNC source. The receive chain (adaptive outer-envelope 4-level slicer, sign-crossing-only symbol clock, energy-gated bit stream) is this project's own — MMDVM's matched-filter/fixed-clock receiver was read but not ported. TX pulse shaping is a windowed-sinc low-pass approximating their Gaussian BT 0.6 pulse, validated on hardware rather than copied. |
| `Kiss/*`, `Channel/SoundModemChannel` (CSMA) | KISS specification (framing, commands, the BPQ ACKMODE extension) and AX.25 §6.4 classic p-persistence. Implemented from the specs — deliberately no dependency on packet.net's AGPL `Packet.Kiss`. |
| `Audio/*`, `Dsp/*` (ALSA, WAV, FIR/design, decimator, upsampler, FFT, spectrum, constellation) | Standard APIs and textbook DSP (windowed-sinc design, polyphase-style rate change, radix-2 FFT). The runtime filter-design *choice* mirrors QtSM's approach. `SpectrumSource` and `ConstellationSource` (per-symbol PSK decision points, batched into auto-ranged scope frames) are original diagnostic side channels of this project, structurally alike; the constellation feed taps the differential product the PSK demodulators already compute (`IConstellationSource`). |
| `Channel/Cm108Ptt` | The de-facto CM108 HID report convention (5-byte output report, GPIO3) as documented and used by Dire Wolf and QtSoundModem. |
| Daemon, KISS TCP server, config | Original. |

## What was deliberately not taken

QtSoundModem's program structure (global state, threading, buffer handling), its
Memory-ARQ and dual-threshold recovery machinery, and Dire Wolf's multi-slicer are not
ported. Where this project needed equivalents it either engineered its own (envelope
slicer, dedup, busy detector) or recorded the lever as future work in `docs/plan.md`.

## Licence consequence

Components above carry material derived from Dire Wolf (GPL-2.0-or-later, upgradeable),
UZ7HO/QtSoundModem (GPLv3+) and MMDVM-TNC (GPL-2.0-or-later, upgradeable). Beyond that, everything here was written with both GPL
trees having been read, so the conservative and honest licence for the whole is
**GPL-3.0-or-later** — chosen deliberately at the project's founding (packet.net
`docs/research/headless-soundmodem.md` §4 and §Decisions, 2026-07-14) rather than
claiming a clean-room permissive implementation that could not be substantiated.
