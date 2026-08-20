# Waveform / modem roadmap

What pdn-soundmodem might implement next, and in what order. This is the strategic map;
the deep design for the lead item (the OFDM family) lives in [ofdm-design.md](ofdm-design.md).

Approved 2026-07-16 after two research sweeps (FreeDV/Codec2 OFDM data-mode internals + a
full landscape survey) and a verified scoping of MIL-STD-188-110D Appendix D. The numbered
tasks referenced here are tracked in the session task list; the durable capture is the
`pdn-soundmodem-waveform-roadmap` memory.

## The rule that ranks everything

Every mode is **labelled with its compatibility** - which real modem/TNC it interoperates
with - and **we never trade NinoTNC compatibility for another peer's**. New modes are
*additive*, never a re-shaping of an existing NinoTNC-compatible mode to suit a different
implementation. Preference order for candidates: **open spec we can implement from scratch**,
**guaranteed real-world interop**, **packet-DATA transport** over keyboard-chat/beacon/voice.
See [PROVENANCE.md](../PROVENANCE.md) and the `pdn-soundmodem-ninotnc-compat-sacrosanct` memory.

## Where we are today

The NinoTNC family, all IL2P/AX.25/FX.25-framed, KISS-interfaced, each labelled by interop
target (see [qtsm-loop.md](qtsm-loop.md) § Results):

| family | modes | compatibility |
|---|---|---|
| AFSK | 1200 (Bell 202), 300 (HF) | universal / NinoTNC / QtSM |
| GFSK | 9600 (G3RUH), 4800 | universal / NinoTNC + Dire-Wolf RUH |
| BPSK | 300, 1200 | NinoTNC / QtSM V26A |
| QPSK | 600, 2400, 3600 | NinoTNC / QtSM V26A (2400 = DW2400) |
| C4FSK | 9600, 19200 | NinoTNC / MMDVM-TNC (G4KLX Mode 2) |

Detection: **differential by default for the PSK modes**, with coherent (Costas) as the opt-in.
That is the reverse of what this line said until 2026-08-20, and the reversal is old news: issue
#5 made coherent the default in July, real off-air NinoTNC BPSK could not be decoded by it
(issues #40/#42 - the NinoTNC uses coherent demod *with* differential encoding to beat the 180
degree ambiguity), and the default went back to differential plus a frequency-diversity bank on
2026-07-17/18. The QPSK family followed on 2026-07-31. See `mode-validation.md` for the ledger.

## Build order

| # | candidate | RF niche | openness | interop | difficulty | task |
|---|---|---|---|---|---|---|
| 1 | **FreeDV OFDM datac** | HF SSB | open (LGPL, port) | FreeDATA / FreeDV | moderate (sync engine) | #2-#4 |
| 2 | **POCSAG** | VHF/UHF paging | open | DAPNET | low (easy win) | #5 |
| 3 | **ARDOP** | HF SSB ARQ | open (MIT ref) | Winlink | moderate | #6 |
| 4 | **MIL-STD-188-110D App D (3 kHz)** | HF SSB | open (public spec) | 5069-class | high (equalizer, no oracle) | #7 |
| 5 | **own FM OFDM** | VHF/UHF FM voice | greenfield | pdn↔pdn | high | #8 |
| 6 | **own HF OFDM** | HF SSB | greenfield | pdn↔pdn | high | #9 |

**Status, 2026-08-20:** 1-4 are built (see [`roadmap.md`](roadmap.md) for what remains of each).
Item 5 was built and then deliberately moved out of this repository to the private
`packet-net/pdn-ofdm-fm`, reached through the runtime modem plugin seam - a buildability
requirement, not a licence one. Item 6 is the only unstarted entry, and §4a below records why no
MIL-STD appendix is a substitute for it.

### 1. FreeDV OFDM datac - the lead build
Interop with the FreeDATA/FreeDV ecosystem, and the way we build the shared OFDM engine every
later OFDM item reuses. **Approach: a managed C# port (not a P/Invoke wrap) validated
bit-for-bit against `libcodec2` as a test-only oracle** - the LDPC is the easy part (~300 lines
of self-contained sum-product with the H-matrices checked into `drowe67/codec2` as
transliterable arrays); the hard part is the OFDM sync/timing/channel-estimation engine, which
is exactly what our own FM/HF modes need, so porting makes FreeDV the training ground. Keeps
the library pure-managed (no native dependency ships). Six QPSK modes @ 8 kHz / 1500 Hz:
datac1 (workhorse, 510 payload bytes/frame), datac3/4 (low SNR), datac0/13/14 (ACK/signalling).
Interop is exact-or-nothing (no spec doc - the source *is* the spec). Full design in
[ofdm-design.md](ofdm-design.md). *Licence note:* `libcodec2` is LGPL-2.1; the port carries
LGPL-2.1 lineage/attribution, workable via the relicensing clause - flag for a real FOSS-licence
review.

### 2. POCSAG - the easy win
2-FSK + BCH(31,21) paging, interoperating with the live **DAPNET** ham paging network
(439.9875 MHz). Reuses the existing FSK demod; sits alongside the packet modes as a distinct
labelled feature. TX-to-DAPNET is the higher-value direction (RX decoders already abound).

### 3. ARDOP - the open HF ARQ answer
Open ([ardopcf](https://github.com/pflarue/ardop), MIT), interoperates with the live **Winlink**
ARDOP gateway network - the open substitute for exactly the modes we *cannot* touch (VARA HF,
PACTOR). Carries its own ARQ, not raw AX.25, so it runs as its own TNC-style mode with a framing
bridge (scope that in its design).

### 4. MIL-STD-188-110D Appendix D (3 kHz) - the HF stretch (redirect of STANAG 5069)
STANAG 5069 (NATO wideband HF, G4KLX-advocated) is **RESTRICTED**, but its content mirrors the
**public** MIL-STD-188-110D Appendix D (Distribution Statement A - download verified). The
implementable target is App D's **3 kHz** serial-tone waveform (2400 Bd @ 1800 Hz, SRRC 35 %,
tail-biting CL7/9 convolutional FEC, autobaud, 4 interleavers), which fits a ~2.7 kHz SSB
channel - the same footprint as the STANAG-4285 signals hams already decode.
- **Phase A (feasible):** 3 kHz framing + Walsh-75/BPSK/QPSK low-order rates + a basic DFE - a
  "5069-class" narrowband capability.
- **Phase B (stretch, gated):** 16-QAM (RLS DFE); 64/256-QAM deferred (groundwave-only).
- **Two real risks** the scoping surfaced: the interop-critical tables are **images** in the PDF
  (must be transcribed twice and diffed - a first-class deliverable), and there is **no open
  App-D implementation and no public reference vectors → no oracle** (unlike FreeDV's
  `libcodec2`); validation is the top risk. The hard DSP block is the adaptive DFE/MLSE/turbo
  equalizer for high-order QAM over HF multipath.
- **Sequenced after FreeDV** - it reuses the shared HF engine (SRRC, timing/carrier recovery,
  Viterbi, Watterson channel-sim) and the no-oracle risk is far safer once a working HF modem +
  sim harness exist. `mars-suite` (110A only, AGPL) is the equalizer reference to study
  clean-room. (Earlier "Nino KK4HEJ is building a 110 modem" was a Discord mention only, no
  public code - not usable prior art; Nino ships FSK/IL2P.)

### 4a. MIL-STD-188-110D Appendix C / STANAG 4539 - evaluated and rejected (2026-08-20)
Tom asked whether App C (= STANAG 4539, the narrowband MDR waveform) was worth building beside
the App D work, on the theory that it might hold more sensitive modes for narrow channels. It
does not. **Rejected: it is a strict subset of what App D already gives us, and the overlapping
masks are identical.**

The evidence, read off the copy of the standard already vendored at
`docs/ms110d/spec/MIL-STD-188-110D.pdf` (App C is doc pages 106-135, PDF pages 111-140):

- **Identical performance requirements where they overlap.** Table C-XVII against the
  transcribed D-LXIV masks (`docs/ms110d/tables/d6x-ber-masks.csv`), AWGN / Poor SNR in dB for
  BER <= 1.0E-5, 3 kHz: 3200 bps 9/14 both, 4800 13/19 both, 6400 16/23 both, 8000 19/27 both,
  9600 21/31 both. App D carried App C's numbers forward unchanged for those rates, which
  follows from it using the same modulations at the same 3/4 code rate. At the top App D is
  better: WN11 does 12000 bps at 24 dB against App C's 12800 uncoded at 27 dB.
- **No low-rate modes at all.** App C's title is "for data rates *above* 2400 bps"; its floor is
  3200 bps at 9 dB AWGN. App D's 3 kHz row runs down to WN0 at 75 bps, **-6 dB AWGN / -1 dB
  Poor**. That is a 15 dB sensitivity gap in App D's favour, on both channels.
- **One code rate for everything.** C.4: "A single coding option, a constraint length 7, rate 1/2
  convolutional code, punctured to rate 3/4, is used for all data rates." App D's 3 kHz column
  (`docs/ms110d/tables/d49-code-rates.csv`) spans 1/8 to 8/9. App C was built to go fast in
  3 kHz, not to go weak.
- **It is not narrower either.** 3 kHz is the floor of the whole document: App A is a LAN
  interface, App B is the obsolete 39-tone parallel mode, App C is 3 kHz, and App D's bandwidth
  column runs 3, 6, 9 ... 48 kHz. There is no sub-3-kHz member of this family to reach for; we
  implemented the narrowest thing 110D contains.

**Recorded because it would otherwise be cheap to build and easy to re-propose.** Unlike App D,
App C's tables are real text in the PDF rather than page images, so the dual-transcription
deliverable that dominated App D's early cost largely evaporates; its FEC is K=7 rate 1/2 full
tail-biting punctured 3/4, which `TailBitingSisoDecoder` + `Ms110dPuncture` already implement;
and it shares App D's 2400 Bd / 1800 Hz / SRRC front end, DFE and Watterson harness outright.
Cheap is not the same as worth doing: it would add no rate, no sensitivity and no bandwidth we
do not already have.

**The one argument that survives, and where it actually points.** App C has third-party decoders
(commercial, plus closed hobbyist ones such as MultiPSK and Sigmira; no open implementation was
found), where App D has none - so it could serve as the external oracle `PROVENANCE.md` records
this project as lacking for the whole serial-tone stack. That is real but does not justify a
full waveform build for validation alone. **STANAG 4285 is the cheaper way to buy the same
thing**: same 2400 Bd on 1800 Hz, same known-symbol-block structure, much simpler, and common
enough on air to be receivable on demand. If the external-oracle gap is ever worth closing,
start there, not here. For narrow weak-signal capability the lever is bandwidth and code rate,
not a MIL-STD appendix - `freedv-datac4` (87 bps in 250 Hz) and its siblings already beat
everything in 110D on a 3 kHz-referenced threshold, and item 6 below is the slot for improving
on them.

### 5-6. Our own FM / HF OFDM - greenfield
Reusing the shared OFDM engine from item 1. **FM OFDM** is the speed play through an FM voice
channel (VARA FM is the only incumbent and it is closed - genuinely greenfield); new work is
PAPR management, pre/de-emphasis EQ, deviation-limit behaviour, sample-clock sync, and it
**must be validated on a real FM radio loop** (PAPR/emphasis can't be simulated). **HF OFDM**
aims to outperform FreeDV datac (adaptive bit-loading, pilot/CP tuning), benchmarked head-to-head
against our datac implementation. Both clearly labelled `pdn-OFDM` (pdn↔pdn only).

## Cannot implement - proprietary / blocked

- **VARA HF / VARA FM** - closed shareware; the licence forbids reverse engineering. (VARA FM,
  ~25 kbps over an FM voice channel, is the benchmark our *own* FM OFDM aims at - but there is
  nothing open to adopt.)
- **PACTOR II/III/IV** - closed; PACTOR-4 is SCS P4dragon hardware only.
- **P25 / NXDN / dPMR** - AMBE-family proprietary vocoder; gated specs; ~zero ham relevance.
- **System Fusion / C4FM (Yaesu)** - no public spec at all (only MMDVM reverse-engineering) -
  fails the "open spec, don't take an implementation as the spec" rule.
- **FLEX (paging)** - reverse-engineered rather than openly specified, GPL-incompatible encoder,
  no live ham network. Prefer POCSAG/DAPNET.

*Nuance:* for **DMR / D-STAR** the modulation and framing layers are open - only the *voice*
payload is AMBE-blocked - so a data-only variant is not IP-blocked, just low ham value. Noted,
not planned.

## Already covered - label, don't build

- **APRS** = the existing AFSK 1200 (Bell 202) and 9k6 GFSK.
- **CubeSat 9k6 G3RUH telemetry** = the existing GFSK 9600 (same scrambler/AX.25/KISS); only
  Doppler tolerance is an open operational question.

Mark these compatible; no new waveform work.

## Parked

- **M17** (open 4FSK + Codec2 voice, native KISS packet mode) surfaced strongly in the survey -
  a genuinely good fit next to our C4FSK experience - but is **parked** (Tom, 2026-07-16, "leave
  it out for now"). Kept here as a deferred candidate, not on the build path.
- **The WSJT-X family** (FT8/FT4/JS8/MSK144/Q65) is open but a poor packet-DATA fit (tiny
  fixed-format payloads); **MT63 / Olivia / PSK31 / RTTY** are fldigi-lineage keyboard modes -
  cheap but chat-oriented, needing the separate flmsg/flamp layer to carry data. Note-only.
