# ARDOP in pdn-soundmodem — design & scoping (task #6 groundwork)

Status: design, pre-implementation. Target: `.NET 10`, pure-managed, **GPL-3.0-or-later**, matching pdn-soundmodem idioms. This is the ARDOP counterpart of [ofdm-design.md](ofdm-design.md): source-grounded, honest about risks, written to be implementable without re-deriving anything.

**Source provenance.** Every ardopcf citation below is to the shallow reference clone used during design — **ardopcf v1.0.4.1.3+, git `a7c92289b569afbe4259dc556d749405ebc008f5` (2025-05-27)**, `github.com/pflarue/ardop` — cited as `file:line` relative to the repo root (`src/common/` for the C, `docs/` for its documentation). The protocol spec is the in-repo **ARDOP Specification Rev 2.0, 2017-11-27** (`docs/refs/ARDOP_Specification_20171127.pdf`), cited as *spec §n* / *spec App. X*. pdn-soundmodem citations are relative to `src/Packet.SoundModem/`. Facts marked **[measured]** were verified on this box during scoping (2026-07-16); facts I could not ground are explicitly marked **[unverified]**.

---

## 1. Scope, versions, and what "interoperating with Winlink" actually requires

### 1.1 What ARDOP is

Amateur Radio Digital Open Protocol (Rick Muething KN6KB): an HF/VHF sound-card ARQ + FEC data protocol in four bandwidth classes (200/500/1000/2000 Hz at the −26 dB points, spec §2.2), designed as a virtual TNC that host programs drive over TCP (spec §8). It is the open substitute for the closed VARA HF / PACTOR modes on the live Winlink network (`docs/waveform-roadmap.md` §3, ardopcf `docs/Motivation.md:9`).

### 1.2 Protocol version: there is exactly one interoperable ARDOP

- The deployed, interoperable protocol is **ARDOP "1"** as defined by the spec Rev 2.0 (2017-11-27). Three implementations form the compatible family (`docs/About_Ardop.md`): **ARDOP_Win** (Muething, ships with Winlink Express), **ardopc** (John Wiseman G8BPQ, C translation of ARDOP_Win), and **ardopcf** (Peter LaRue AI7YN, maintained fork of ardopc, MIT).
- G8BPQ's later variants ("ARDOP 2", ardopofdm etc.) are explicitly **not** OTA-compatible and were deliberately **not adopted** by ardopcf because they "would break conformance to the Ardop specification and over the air compatibility with ARDOP_Win and ardopc" (`docs/About_Ardop.md`, final section). They are out of scope.
- ardopcf pins itself to the 2017 spec: "The files in this directory … define the Ardop protocol that ardopcf intends to conform to" (`docs/refs/refs.md`).

**Interop target: ardopcf itself** (v1.0.4.1.3, commit above) — the maintained, buildable, scriptable member of the family, exactly the role codec2 played for FreeDV. Where ardopcf and the spec disagree, ardopcf's behaviour is the operative truth (it is what stations run); the disagreement gets recorded, not silently resolved (§9.3).

### 1.3 What Winlink interop concretely requires

Layering, bottom-up:

1. **The waveforms** (§3) — every frame type a peer may legally send within the negotiated bandwidth. Note the gearshift means the remote ISS can pick *any* mode in the session-bandwidth ladder (`ARQ.c:571-607`), so RX coverage of a bandwidth class must be complete before any live session at that class (this shapes the phasing, §8).
2. **The ARQ session engine** (§4) — connect/negotiate/data/turnaround/disconnect with real-time timing.
3. **The TCP host interface** (§6) — what Winlink client programs (Pat, Winlink Express) actually speak to the TNC. The B2F message-forwarding protocol above it is the *host program's* job, not the TNC's — Pat carries B2F; we terminate at the host-interface boundary, same place ardopcf does.
4. **A live gateway** — Winlink publishes ARDOP-mode gateways (winlink.org/RMSChannels has an ARDOP filter; Pat's `Show RMS list` filters by ARDOP mode — `docs/Pat_windows.md:194,198`). Which software the gateway side runs (ARDOP_Win under RMS Trimode vs ardopc/ardopcf under LinBPQ) is **[unverified]** from the reference repo and doesn't matter: all deployed gateways speak ARDOP 1.

So "Winlink interop" = §3 + §4 + §6 validated against ardopcf, then one real gateway session from Tom's bench (§7.5).

### 1.4 Goals / non-goals

**In scope:** the full ARDOP 1 SSB-mode inventory (200–2000 Hz, 4FSK/4PSK/8PSK/16QAM); FEC (connectionless) mode; the ARQ engine; PING/PINGACK; ID frames + optional CW ID; the ardopcf-compatible TCP host interface; ardopcf-oracle validation.
**Deferred (flagged, not designed here):** the 600-baud FM-only modes (`4FSK.2000.600/600S`, gated behind `USE600MODES`, off by default — `docs/Host_Interface_Commands.md` §USE600MODES); serial/WA8DED host mode (ardopcf itself is TCP-only); the webgui (ardopcf's `Webgui.c` — our daemon has its own UI story); RXO receive-only monitor mode (`RXO.c`, 332 lines — cheap, nice-to-have, not needed for interop).
**Out of scope:** B2F/CMS (Pat's job); "improved" non-spec waveforms (that's the greenfield HF OFDM item, roadmap #6/#9).

---

## 2. Architecture — where ARDOP sits in the codebase

### 2.1 ARDOP is a session TNC, not an `IModem`

Our existing modes are stateless framers behind `IModem` (`Modems/IModem.cs`): audio in → AX.25 frames out, multiplexed on one channel by KISS port. ARDOP is different in kind: it owns a **connection state machine with hard real-time TX obligations** (an IRS must ACK inside the ISS's repeat window, §4.4), carries **its own framing** (not AX.25), and exposes **its own host protocol** (not KISS). It is a peer of the KISS and paging services, not another `IModem`:

```
Packet.SoundModem.Daemon
 ├─ KissTcpServer        (8105) ── IModem multiplex ──┐
 ├─ PagingTcpServer      (8106) ── POCSAG encoder ────┼── SoundModemChannel ── audio + PTT
 └─ ArdopHostServer  (8515/8516) ── ArdopSession ─────┘        (per channel)
                                        │
                             ArdopModem (frame codec + demod state machine)
```

Namespaces, mirroring the `Ofdm/` pattern:

- `Packet.SoundModem.Ardop` — frame codec: leader/sync, frame-type coding, 4FSK/PSK/QAM modulators + demodulators, RS+CRC framing, busy detector glue.
- `Packet.SoundModem.Ardop.Arq` — the protocol engine: states, gearshift, timers, session IDs, FEC mode.
- `Packet.SoundModem.Ardop.Host` — the TCP host interface (command + data sockets).

### 2.2 Channel policy (v1): ARDOP gets a dedicated channel

ardopcf assumes it owns the sound card and the PTT. Sharing a channel between ARDOP and the packet modems raises TX-arbitration and DCD questions that buy nothing for the Winlink use-case (an HF SSB rig dedicated to Winlink sessions). **v1: an ARDOP-enabled channel runs only ARDOP; co-channel KISS+ARDOP multiplexing is deferred.** The daemon config grows an `Ardop` section per channel (`{ "Port": 8515 }`) exclusive with `Modems`; the existing `SoundModemChannel`, ALSA/WAV audio, PTT (`Cm108Ptt` etc.) and 12 kHz rate support are reused as-is (ARDOP's native rate is 12000 Hz — `ALSASound.c` opens capture/playback at 12000 **[measured]**; same rate our audio-band modes already use, `docs/qtsm-loop.md` table).

### 2.3 Sizing — "how big is it really?"

ardopcf `src/common/` totals **23,068 lines** of C (headers included). The parts we port/reimplement:

| block | ardopcf source | lines | our estimate |
|---|---|---|---|
| demod + acquisition | `SoundInput.c` | 5,282 | the big one; ~2.5–3.5 k C# (§3.6) |
| ARQ engine | `ARQ.c` | 2,672 | ~1.5–2 k C# (§4.6) |
| frame tables + encode + RS/CRC glue | `ARDOPC.c` | 2,318 | ~800 (tables become records) |
| host interface (logic + TCP) | `HostInterface.c` + `TCPHostInterface.c` | 1,608 + 1,050 | ~800 (Kestrel-less sockets like `KissTcpServer`) |
| modulator | `Modulate.c` | 1,052 | ~600 |
| FEC mode | `FEC.c` | 393 | ~250 |
| busy detector | `BusyDetect.c` | 233 | ~200 |
| SDFT 4FSK decoder (optional alt) | `sdft.c` | 455 | defer (§3.4) |
| callsign/grid/6-bit packing | `StationId.c`, `Packed6.c`, `Locator.c` | 799 | ~300 |
| *not ported* | webgui, logging, ALSA, wav, rig glue | ~7 k | existing infra |

Roughly a **6–8 k line C# port** — larger than POCSAG, smaller than the FreeDV OFDM+LDPC effort, with the novelty concentrated in the demodulator and the timing engine rather than in any single hard DSP algorithm.

---

## 3. The waveform inventory

### 3.1 Common frame skeleton (every frame)

All frames share (spec §7.1, App. B; verified against the code):

1. **Two-tone leader**: 50 Bd (20 ms) symbols of 1475+1525 Hz with alternating phase, length 100–1000 ms negotiated (spec App. B; default `LeaderLength = 240` ms, `ARDOPC.c:98`); generated from a 240-sample template (`Modulate.c:39-63`, `intFSK50bdCarTemplate`).
2. **Sync symbol**: one final leader symbol with *non*-inverted phase (spec App. B "LeaderSync"; `GetTwoToneLeaderWithSync`, `Modulate.c:52-58`).
3. **Frame type**: 10 × 50 Bd 4FSK symbols on one carrier = 2 bytes + 2 parity symbols: byte 0 = frame type, byte 1 = type XOR **SessionID**; after each byte comes one 2-bit parity symbol — and *both* parity symbols are computed from byte 0, the plain frame type (`SendLeaderAndSYNC`, `Modulate.c:90-110`; `ComputeTypeParity` = XOR-fold of the four dibits seeded with 1, `ARDOPC.c:1640-1656`). Decoded by minimal-distance soft decision over the candidate set (`MinimalDistanceFrameType`, `SoundInput.c:2137`).
4. **Frame data** (data-bearing types): per carrier, `[1 byte count][data][CRC16]` + RS parity. CRC covers count+data only (not RS), and its **low byte is XORed with the frame type** (`GenCRC16FrameType`, `ARDOPC.c:1722-1730`; assembly at `ARDOPC.c:1179`). Unused data bytes zero-filled (spec App. B).

**SessionID** = CRC-8 over the concatenated caller+target callsign strings, poly constant 0xC6, init 0xFF, result 0xFF remapped to 0 (0xFF is reserved for unconnected/FEC frames) — `GenCRC8` `ARQ.c:200-246`, `GenerateSessionID` `ARQ.c:507-527`.

**CRC-16**: spec says "x^16+x^12+x^5+1" (spec App. B), but the implementation is a non-standard shift formulation — data bit shifted into the LSB *before* the poly XOR, poly constant **0x8810**, init 0xFFFF (`GenCRC16`, `ARDOPC.c:1673-1718`). This is *not* table-standard CCITT-FALSE; port it verbatim and pin it with vectors from ardopcf. Neither `Fec/Crc16X25.cs` nor `Fec/FreedvCrc16.cs` matches — new ~40-line class.

**Sample rate 12000 Hz** throughout; TX amplitude reference `intAmp = 26000` (`ARDOPC.c:287`); TX symbols come from precomputed integer templates (`ardopSampleArrays.c`, generated by `CalcTemplates.c`); trailer default 20 ms (`ARDOPC.c:99`).

### 3.2 Tone and carrier plan (all exact, from `CalcTemplates.c`)

| use | frequencies (Hz) | source |
|---|---|---|
| leader | 1475, 1525 | `Modulate.c:42` |
| 4FSK 50 Bd (200 Hz class + all control) | 1425, 1475, 1525, 1575 | `CalcTemplates.c:111` |
| 4FSK 100 Bd (500 Hz class) | 1350, 1450, 1550, 1650 | `CalcTemplates.c:192-195` |
| 4FSK 600 Bd (FM only) | 600, 1200, 1800, 2400 | `CalcTemplates.c:274-277` |
| PSK/QAM 100 Bd carriers | from {800,1000,1200,1400,**1500**,1600,1800,2000,2200}: 1 car → 1500; 2 car → 1400,1600; 4 car → 1200–1800; 8 car → all except 1500 | `CalcTemplates.c:360-370`; `PlayPSKSymbols` `Modulate.c:413-467` |

PSK/QAM carriers are **independent 100 Bd single-carrier streams summed in the time domain** with per-carrier-count empirical scaling + soft clipping (`Modulate.c:497-530`) — *parallel tones*, not IFFT OFDM; there is no cyclic prefix (the ~1 ms guard comes from Hanning-windowed symbol shaping, spec App. C note 6). PSK is **differential** (phase increment per symbol, `SymSet`=2 for 4PSK / 1 for 8PSK, one reference symbol per carrier at frame start, `Calc1CarPSKSymbols` `Modulate.c:361-408`); 16QAM = the 8 differential phases × 2 absolute amplitude levels (bit 3 halves the template amplitude, `Modulate.c:453-458`; payload packing incl. count/CRC/RS in `EncodePSKData`, `ARDOPC.c:1106`).

### 3.3 The frame-type table (complete)

From `FrameInfo` (`ARDOPC.c:770-1077`), matching spec App. B:

**Control (all 1-carrier 4FSK 50 Bd, no payload unless noted):**

| code(s) | frame | payload | notes |
|---|---|---|---|
| 0x00–0x1F | DataNAK | — | 5 LSBs = quality, Q = 38 + 2×bits (`Modulate.c:165`) |
| 0x23 / 0x24 / 0x29 / 0x2C | BREAK / IDLE / DISC / END | — | 360 ms total (spec App. C) |
| 0x2D / 0x2E | ConRejBusy / ConRejBW | — | |
| 0x30 | IDFRAME | 12 B + RS 4 | callsign + gridsquare, two Packed6 fields (8 chars→6 B, `Packed6.h`) |
| 0x31–0x38 | ConReq{200,500,1000,2000}{M,F} | 12 B + RS 4 | caller+target callsigns; SessionID always 0xFF; M=max/negotiable, F=forced |
| 0x39–0x3C | ConAck{200,500,1000,2000} | 3 B, no RS | received-leader length in 10 ms units ×3 redundancy |
| 0x3D / 0x3E | PingAck / Ping | 3 B / 12 B + RS 4 | PingAck carries S:N (−10…+21 dB) + quality (`Modulate.c:170-176`) |
| 0xE0–0xFF | DataACK | — | 5 LSBs = quality, as NAK |

**Data (all even/odd paired — the ARQ duplicate-detection toggle, spec §D-2.3):**

| codes | mode | cars | Bd | payload/car (B) | RS/car (B) | net/frame (B) | class |
|---|---|---|---|---|---|---|---|
| 0x48/49 | 4FSK.200.50S | 1 | 50 | 16 | 4 | 16 | 200 Hz |
| 0x42/43 | 4PSK.200.100S | 1 | 100 | 16 | 8 | 16 | 200 Hz |
| 0x40/41 | 4PSK.200.100 | 1 | 100 | 64 | 32 | 64 | 200 Hz |
| 0x44/45 | 8PSK.200.100 | 1 | 100 | 108 | 36 | 108 | 200 Hz |
| 0x46/47 | 16QAM.200.100 | 1 | 100 | 128 | 64 | 128 | 200 Hz |
| 0x4C/4D | 4FSK.500.100S | 1 | 100 | 32 | 8 | 32 | 500 Hz |
| 0x4A/4B | 4FSK.500.100 | 1 | 100 | 64 | 16 | 64 | 500 Hz |
| 0x50/51 | 4PSK.500.100 | 2 | 100 | 64 | 32 | 128 | 500 Hz |
| 0x52/53 | 8PSK.500.100 | 2 | 100 | 108 | 36 | 216 | 500 Hz |
| 0x54/55 | 16QAM.500.100 | 2 | 100 | 128 | 64 | 256 | 500 Hz |
| 0x60/61 | 4PSK.1000.100 | 4 | 100 | 64 | 32 | 256 | 1000 Hz |
| 0x62/63 | 8PSK.1000.100 | 4 | 100 | 108 | 36 | 432 | 1000 Hz |
| 0x64/65 | 16QAM.1000.100 | 4 | 100 | 128 | 64 | 512 | 1000 Hz |
| 0x70/71 | 4PSK.2000.100 | 8 | 100 | 64 | 32 | 512 | 2000 Hz |
| 0x72/73 | 8PSK.2000.100 | 8 | 100 | 108 | 36 | 864 | 2000 Hz |
| 0x74/75 | 16QAM.2000.100 | 8 | 100 | 128 | 64 | 1024 | 2000 Hz |
| 0x7A/7B | 4FSK.2000.600 | 1 | 600 | 600 | 150 | 600 | FM only |
| 0x7C/7D | 4FSK.2000.600S | 1 | 600 | 200 | 50 | 200 | FM only |

18 named data modes (`strAllDataModes`, `ARDOPC.c:289-298`), 16 in the SSB path. Net throughput spans ~310 bytes/min (4FSK.200.50S) to ~12,072 bytes/min (16QAM.2000.100) per the spec App. C worksheets — the spec's "38:1" agility claim (spec §2.3). Notes: the max US-symbol-rate rule caps carriers at 100 Bd for HF (spec §2.5); each carrier of a multi-carrier frame has its own count+CRC+RS and is accepted/retained independently (spec §D-2.1, `CarrierOk[8]` `SoundInput.c:214`).

**ARQ gearshift ladders** (per session bandwidth, most-robust first, `ARQ.c:571-607`): 200 Hz `{48,42,40,44,46}`; 500 Hz `{48,42,40,50,52,54}`; 1000 Hz `{4C,4A,50,60,62,64}`; 2000 Hz `{4C,4A,50,60,70,72,74}`. FSKONLY variants exist (e.g. 500 Hz → `{48}` alone), which matters for phasing (§8). Shift-up quality thresholds per rung at `ARQ.c:678-684`; the shifting algorithm is `Gearshift_9` (`ARQ.c:717`) driven by the 5-bit quality echoed in every ACK/NAK.

### 3.4 The receive side (the real work)

RX pipeline (`ProcessNewSamples`, `SoundInput.c:810`, states `_ReceiveState` `ARDOPC.h:241-252`):

1. **Leader search** — `SearchFor2ToneLeader3` (`SoundInput.c:1604`): windowed Goertzel scan (`GoertzelRealImag*`, `SoundInput.c:1440-1580`) + 3-bin `SpectralPeakLocator` interpolation to find the two-tone pair, estimate the frequency offset (spec requires ±200 Hz capture, §4.1, honed to ~1 Hz, §7.1.1) and S:N.
2. **Mix + filter** — NCO mix by the measured offset to a fixed passband and 2 kHz filter (`MixNCOFilter`/`FSMixFilter2000Hz`, `SoundInput.c:423-650`).
3. **Symbol framing** — envelope correlation of the two-tone waveform (`Acquire2ToneLeaderSymbolFraming`/`EnvelopeCorrelator`, `SoundInput.c:1862-1970`).
4. **Frame sync** — the phase-reversal sync symbol (`AcquireFrameSyncRSB`, `SoundInput.c:1971`).
5. **Frame type** — 10-symbol 4FSK soft decode with minimal-distance search over valid types ⊕ expected SessionIDs (`Acquire4FSKFrameType`/`MinimalDistanceFrameType`, `SoundInput.c:2360,2137`).
6. **Per-mode demod** — dispatched by `DemodulateFrame` (`SoundInput.c:3192`):
   - **4FSK**: per-symbol Goertzel magnitudes on the 4 tones, hard decision + quality from tone-mag ratios (`Demod1Car4FSK`, `SoundInput.c:2431`; 600 Bd variant `:2741`). ardopcf also has an alternative sliding-DFT decoder with fine symbol-timing tracking (`sdft.c`, opt-in via `--sdft`) — we start with the Goertzel decoder (the default) and treat SDFT as a later robustness experiment.
   - **PSK**: per-carrier, per-symbol Goertzel phase measurement, differential decision, with a phase-ramp correction for residual tuning offset (`DemodPSK` `SoundInput.c:4577`, `Demod1CarPSKChar :4786`, `Track1CarPSK :4228`, `CorrectPhaseForTuningOffset :4336`). No equalizer anywhere — multipath tolerance comes from 100 Bd + windowing (spec §2.6).
   - **16QAM**: as PSK plus absolute amplitude discrimination against a tracked reference magnitude (`DemodQAM` `SoundInput.c:5034`, `Demod1CarQAMChar :5227`).
7. **RS correct + CRC check** per carrier (`CorrectRawDataWithRS`, `SoundInput.c:692`), then frame accept/NAK.
8. **Memory ARQ** — failed carriers' tone magnitudes (FSK) or phase/magnitude vectors (PSK/QAM) are accumulated across repeats and re-decoded from the average (`Save{FSK,PSK,QAM}Samples` + `intToneMagsAvg`/`intCarPhaseAvg`, `SoundInput.c:147-150,4910-5030`; fully implemented only since v1.0.4.1.3, `changelog.md` "Improve decoding of repeated data frames").
9. **Busy detection** (channel-clear gating, BUSYBLOCK): FFT-bin spectral S:N estimator, ~11.72 Hz bins (`BusyDetect3`, `BusyDetect.c:52`).

### 3.5 Reuse map — what we already have vs what is new

| block | existing pdn asset | verdict |
|---|---|---|
| **Reed-Solomon** | `Fec/ReedSolomon.cs` — GF(2^8) poly **0x11D**, configurable fcr | **Direct reuse.** ardopcf's `lib/rockliff/rrs.c` uses the same field (pp = x^8+x^4+x^3+x^2+1 = 0x11D, `rrs.c:130`; the "0x171" comment there is the bit-reversed rendering) and generator roots α^1…α^rslen (`rrs.c:196-207`) → `new ReedSolomon(rsLen, firstConsecutiveRoot: 1)`, the FX.25 configuration. Shortened systematic, parity appended — same shape. Verify with byte vectors from ardopcf before trusting (§7.2). |
| audio/channel/PTT infra | `Audio/`, `Channel/` (ALSA, WAV, `SoundModemChannel`, `Cm108Ptt`, upsampler) | direct reuse; 12 kHz native |
| FFT | `Dsp/Fft.cs` | reuse for busy detector spectrum |
| FIR/design | `Dsp/FirFilter.cs`, `FilterDesign.cs` | partial — ARDOP's specific filters (`FSMixFilter2000Hz`, `Filter75Hz`, TX `initFilter` sections) get ported as constants, run through existing structures where they fit |
| busy/DCD | `Modems/EnergyBusyDetector.cs` | keep as fallback; port `BusyDetect3` for behaviour parity (it feeds protocol decisions — ConRejBusy) |
| CRC | `Fec/Crc16X25.cs`, `FreedvCrc16.cs` | **not** compatible — new `ArdopCrc16` (verbatim `GenCRC16` + frame-type XOR) + `ArdopCrc8` for SessionID |
| Goertzel | — | new, small (3 windowed variants, `SoundInput.c:1440-1580`) |
| FSK demod chain | `Modems/AfskDemodulator`, `FskModem`, `BitDpll` | **not reusable** — those are continuous-stream FSK with clock recovery; ARDOP 4FSK is burst MFSK with externally-established symbol timing. New, but simple. |
| PSK demod | `Modems/QpskModem`, `CostasLoop` | **not reusable** — ARDOP PSK is differential, per-symbol tone-probe demod, no Costas. New. |
| OFDM engine | `Ofdm/*` | **not applicable** — ARDOP multi-carrier is summed independent tones, not IFFT OFDM; no CP, no pilots. (The *experience* transfers; the code does not.) |
| LDPC / Viterbi / interleaver | `Fec/Ldpc`, `GpInterleaver` | **not needed** — ARDOP has no convolutional code, no LDPC and no bit interleaver. FEC = RS + repeats + Memory-ARQ averaging. A genuine simplification vs FreeDV/MIL-STD. |
| 6-bit packing / grid | — | new, tiny (`Packed6`, `Locator` ports) |

### 3.6 Honest difficulty map

| waveform / block | difficulty | why |
|---|---|---|
| leader detect + symbol/frame sync | **high** | the acquisition front-end everything else depends on; ±200 Hz capture to ~1 Hz; envelope-correlation timing with no further tracking for 4+ s frames (sound-card ppm spec §4.2 is absorbed here) |
| frame-type decode (soft 4FSK + SessionID set) | low–moderate | well-specified, closed candidate set |
| 4FSK 50/100 Bd data | **low–moderate** | non-coherent Goertzel magnitudes at fixed timing |
| 4FSK 600 Bd (FM) | low (deferred) | 20 samples/symbol, same structure |
| 4PSK/8PSK 1–8 carriers | **moderate–high** | differential helps, but phase must stay coherent-enough per carrier across ~4 s with only a start-reference + offset ramp; matching ardopcf's *robustness* (not just correctness) is the bar |
| 16QAM | **high** | adds absolute amplitude decisions (AGC/reference tracking); ardopcf itself only recently fixed its 16QAM.2000 modulator (`changelog.md` v1.0.4.1.3) — thin margins by design |
| Memory ARQ averaging | moderate | cross-cutting state, easy to get subtly wrong; validate with repeated-frame WAVs + `INPUTNOISE`-degraded vectors |
| TX (all modes) | low | table-driven template playback + scaling/soft-clip; templates transcribable from `ardopSampleArrays.c` or regenerated per `CalcTemplates.c` |

---

## 4. The ARQ protocol layer

### 4.1 States and roles

Two stations; the **ISS** (sending) / **IRS** (receiving) roles swap during a session (spec §5.1). Top-level states (`_ARDOPState`, `ARDOPC.h:271-283`): `OFFLINE, DISC, ISS, IRS, IDLE, IRStoISS, FECSend, FECRcv`, with ARQ sub-states (`_ARQSubStates`, `:290-304`): `ISSConReq, ISSConAck, ISSData, ISSId, IRSConAck, IRSData, IRSBreak, IRSfromISS, DISCArqEnd`. Protocol modes (`_ProtocolMode`): `FEC, ARQ, RXO`.

### 4.2 Connect + bandwidth negotiation (spec App. D rules 1.1–1.5)

Caller repeats `ConReq{bw}{M|F}` (SessionID 0xFF) until answered or timeout; callee checks callsign match (incl. `MYAUX` aliases, `IsCallToMe` `ARQ.c:541`), negotiates bandwidth (`IRSNegotiateBW`, `ARQ.c:2318` — forced vs max per the M/F suffix; incompatible → `ConRejBW`; channel busy + BUSYBLOCK → `ConRejBusy`), then answers `ConAck{bw}` carrying the **measured received leader length** (10 ms units ×3). Caller replies with its own ConAck (leader measurement back), IRS confirms with a DataACK — session up, both ends now using tuned leader lengths (spec Fig. D-1; the auto-timing feature, spec §2.10). All subsequent frames use the CRC-8 SessionID.

### 4.3 Data exchange (spec App. D rules 2.1–2.5)

ISS sends a data frame (mode chosen by gearshift from the ladder, §3.3); IRS replies DataACK (all carriers OK, incl. via Memory-ARQ recovery) or DataNAK, both carrying 5-bit quality. Even/odd frame-type toggle only advances on ACK — repeats are recognised and re-ACKed without re-delivering data. Data delivery to the host is in-order and exactly-once by construction of the toggle.

### 4.4 Timing budgets (the hard real-time part)

- ISS repeat interval: `ComputeInterFrameInterval(1500…2100 ms)` scaled by carrier count, i.e. the IRS must detect, demodulate, decide and get its 360 ms ACK/NAK on the air inside ~1.5–2.1 s of frame end (`ARQ.c:872-895`).
- IDLE chirps every ~2 s when the ISS runs dry (`ARQ.c:947`); IRS answers each with ACK, or with BREAK to take the link (AUTOBREAK, rule 3.3); BREAK is the only frame the IRS ever repeats (2–4 s cadence).
- Pending-connect timeout 10 s (`ARQ.c:1241`); ARQTIMEOUT (idle-session) default 120 s, host-settable 30–240 (`docs/Host_Interface_Commands.md` §ARQTIMEOUT); final-ID at teardown ~3 s (`ARQ.c:1156`).
- `EXTRADELAY` host command pads RX→TX turnaround for long paths / slow rigs — our daemon analogue must map onto its existing PTT lead-time handling.

Disconnect: DISC → END (or timeout + ID), with the DISC-after-DISC END-repeat rule for a lost END (spec rules 1.6–1.8). Role swap: BREAK/ACK handshake, buffer purge + `RESTOREBUFFER` semantics on the deposed ISS (rule 3.6).

### 4.5 FEC mode + PING + ID

- **FEC (connectionless)**: any data frame type, 0–5 repeats (`FECREPEATS`), even/odd toggled per new frame; receiver passes first-good copy, or the errored copy flagged `ERR` after the sender moves on (spec App. D FEC rules; `FEC.c`, 393 lines; multi-station dedup semantics per `changelog.md` v1.0.4.1.3). Drive via `FECMODE/FECSEND/FECID`.
- **PING/PINGACK**: DISC-state-only channel probe; PingAck returns measured S:N and quality (spec §2.11, §5.4; `ProcessPingFrame`).
- **IDFRAME** every ≤10 min while transmitting + at session end; optional CW ID after (spec §7.2.3.13; UK legal-ID relevant).

### 4.6 Engine design in C#

A single-threaded event-driven engine (like the SDL AX.25 machine in packet.net, but far smaller): inputs = {decoded frame, decode-failure notification, host command, timer tick, buffer state}; outputs = {frame to modulate, host events, state changes}. All ardopcf timing constants become a named `ArdopTimings` record (testable, and adjustable-with-provenance if bench work shows we need margin). The gearshift is a pure function over (ladder, quality history, `ModeHasWorked/ModeNAKS` stats — `ARQ.c:711-713`) → next frame type; port `Gearshift_9` behaviourally with its thresholds rather than inventing our own (interop lives in both stations converging).

---

## 5. Host interface

### 5.1 The wire protocol (what Pat speaks)

Two TCP sockets (`Host_Interface_Spec_for_WL2K…pdf` App. C; ardopcf `TCPHostInterface.c:582`):

- **Command port** (default 8515): `<CR>`-terminated ASCII commands, case-insensitive; echo/`now` responses; `FAULT <desc>` on error; async events (`NEWSTATE`, `CONNECTED <call> <bw>`, `DISCONNECTED`, `BUFFER n`, `PTT TRUE/FALSE`, `BUSY TRUE/FALSE`, `STATUS …`, `PENDING/CANCELPENDING`, `TARGET`, `REJECTEDBW/REJECTEDBUSY`, `PING/PINGACK/PINGREPLY`) pushed at any time (`docs/Host_Interface_Commands.md` §Command Socket Messages).
- **Data port** (always command port + 1): binary, `[2-byte big-endian length][payload]`; TNC→host payloads are tagged with a 3-byte prefix `ARQ`/`FEC`/`ERR`/`IDF` inside the length (`HostInterface.c:222-260`).

### 5.2 Decision: implement the ardopcf host protocol, byte-compatible

**Recommendation: same protocol, same defaults (8515/8516), no pdn-native alternative in v1.**

Rationale:
- **Interop-maximal**: Pat (and Winlink Express, gARIM, ARIM, hamChat — `docs/Host_Interface_Commands.md` §Modes) work unmodified; our modem becomes a drop-in ardopcf replacement, which is also exactly how we A/B test it (§7).
- **It is a written spec**: the WL2K host-interface PDF defines the framing; ardopcf's `Host_Interface_Commands.md` documents every command (self-describedly *descriptive*, so pin behaviour against ardopcf responses, not just the doc).
- **Cheap**: line-oriented command parsing + a length-prefixed data socket — the same pattern as `Pocsag/PagingTcpServer` and `Kiss/KissTcpServer`.
- A pdn-native surface adds a second protocol to maintain with zero additional reachable peers. If the node ever wants richer control, that's a later additive, not a v1 fork.

Command subset for v1 (from the full list, `docs/Host_Interface_Commands.md`): `INITIALIZE, VERSION, MYCALL, MYAUX, GRIDSQUARE, PROTOCOLMODE, ARQBW, CALLBW, ARQTIMEOUT, ARQCALL, LISTEN, ENABLEPINGACK, PING, BUSYDET, BUSYBLOCK, DISCONNECT, ABORT, BUFFER, PURGEBUFFER, STATE, SENDID, CWID, DRIVELEVEL, LEADER, TRAILER, EXTRADELAY, FECMODE, FECSEND, FECREPEATS, FECID, MONITOR, AUTOBREAK, BREAK, FSKONLY, USE600MODES, TWOTONETEST, CONSOLELOG/LOGLEVEL` (audio-device and RADIOHEX/rig commands are daemon-config concerns; return benign responses). **Implementation task: pin the exact subset Pat's ardop transport actually sends by reading `la5nta/pat`'s ardop driver source — [unverified] here, network source not in the reference clone.**

### 5.3 Coexistence with the daemon

`ArdopHostServer` is a per-channel service beside `KissTcpServer`/`PagingTcpServer` (§2.1–2.2), started from `DaemonConfig` the same way (`Daemon/Program.cs:275-291` pattern). Because v1 gives ARDOP a dedicated channel, there is no TX arbitration question; the daemon's shared bits (audio device, PTT, logging, config) are unchanged. The host-socket failsafe (drop of host connection ⇒ abort TX, revert to receive — spec §8.1.2.1.4) is mandatory.

---

## 6. Validation strategy — ardopcf as the test oracle

### 6.1 Why this is a good oracle (verified)

- **Builds trivially**: `make` with gcc + libasound on this box **[measured]** (721 KB binary, no other deps).
- **Fully offline RX path**: `--decodewav <file>` (up to 5 files) decodes WAVs *instead of* opening any audio device — the main loop short-circuits before ALSA setup (`ALSASound.c:415-419`) **[measured via source]**.
- **Fully offline TX path**: `ardopcf 8515 null null -T` (ALSA `null` PCM + `--writetxwav`) writes exact TX audio as 12 kHz WAV without touching hardware — **[measured]**: a `TWOTONETEST` via `--hostcommands` produced `ARDOP_txfaudio_*.wav` (120,644 B ≈ 5.02 s), no sound card involved.
- **Arbitrary frame generator**: the `TXFRAME` host command (debug-only, `txframe.c:1-8`) transmits any frame type with chosen payload/session ID — per-frame-type golden vectors on demand.
- **Channel degradation built in**: `INPUTNOISE <stddev>` adds Gaussian noise to all input audio including `--decodewav` (`changelog.md` v1.0.4.1.3) — SNR sweeps without a channel simulator.
- **RXO mode + logs**: `PROTOCOLMODE RXO` decodes everything heard with debug output — a protocol sniffer for session debugging.

Caveat vs codec2: ardopcf's decoder is *not* bit-deterministic in the codec2 sense across timing/noise (acquisition is threshold-driven), so the exactness contract is **decoded-payload equivalence and protocol equivalence**, not sample-exact DSP parity. TX direction *is* deterministic (integer templates) — our TX can and should be validated sample-comparable per mode after scaling.

### 6.2 The acceptance ladder

Mirrors the FreeDV three-leg pattern (`ofdm-design.md` §7.6, `freedv-hf-loop.md`):

- **Rung 0 — component vectors**: CRC-16/CRC-8/Packed6/RS byte vectors extracted from ardopcf (small C harnesses or TXFRAME dissection); frame-type parity; SessionID cases incl. the 0xFF remap.
- **Rung 1 — frame-level WAV cross-decode (CI, offline)**: for every frame type in scope: (a) ardopcf `TXFRAME`+`-T` WAV → our demod decodes payload byte-exact; (b) our TX WAV → `ardopcf --decodewav` reports the frame decoded OK (parse the debug log, `STATUS … frame received OK` format `Host_Interface_Commands.md:786`). Clean + `INPUTNOISE`-degraded + frequency-offset (±200 Hz) variants. Checked-in fixtures with `PROVENANCE.md` rows, exactly like `samples/freedv/`.
- **Rung 2 — FEC-mode exchange (offline)**: multi-frame FEC transmissions incl. repeats → Memory-ARQ recovery paths; both directions via WAV.
- **Rung 3 — full ARQ sessions ours↔ardopcf, both roles (loopback rig)**: needs live full-duplex audio between two processes — the `snd-aloop` rig from [qtsm-loop.md](qtsm-loop.md) is the proven pattern (ardopcf on one side of the loop pair, our daemon on the other; drive both host interfaces from the test). Assert: connect at each bandwidth class, data both directions, BREAK role reversal, gearshift up/down under `INPUTNOISE`, DISC/END teardown, session log comparison. Also run **ardopcf↔ardopcf first** on the same rig to capture baseline timing/quality ground truth before ours enters (removes "is it the rig or us?" ambiguity). Sequential, one heavy job at a time (box constraint).
- **Rung 4 — Pat end-to-end (loopback rig)**: Pat → our host interface ↔ ardopcf peer; then Pat ↔ ours on both sides. A real B2F mail exchange is the pass criterion.
- **Rung 5 — Tom's bench: real HF + live Winlink gateway**: real radio, real path, connect to a published ARDOP gateway, send/receive a test message via Pat. Also P2P ours↔ardopcf over RF. (Logistics = open question §10.)

### 6.3 OBW — never-wider-than-reference, per bandwidth class

ARDOP publishes its bandwidth classes (200/500/1000/2000 Hz at −26 dB, spec §2.2). Extend the existing `OccupiedBandwidthTests` pattern (`ofdm-design.md` §8.5): generate ardopcf reference TX per class via the null-device rig, measure its −26 dB OBW as the golden number, and assert ours ≤ ardopcf's per class (not ≤ the nominal figure — the reference's own skirts are the honest bar).

---

## 7. Phasing — validated increments

**Phase A — 4FSK frame codec + FEC mode (first light).**
Leader/sync/frame-type codec; 4FSK 50/100 Bd modulator + demodulator; RS/CRC framing; control frames; IDFRAME/Ping codecs; FEC-mode engine (`FECSEND`/RX). Scope: 200+500 Hz 4FSK data modes (0x48/49, 0x4A/4B, 0x4C/4D) + all control frames (they're all 4FSK 50 Bd — Phase A necessarily builds every control frame ARQ will need). Exit: Rung 0–2 green in CI.

**Phase B — the ARQ engine, FSK-only.**
Full state machine, session IDs, leader-timing negotiation, gearshift (trivial ladder), timers, BREAK/IDLE/DISC. Peer ardopcf configured `FSKONLY TRUE` so both TX ladders are pure 4FSK (`ARQ.c:572,579` — legitimate, host-commandable configuration, not a fork). Exit: Rung 3 green with FSKONLY at 200/500 Hz, both roles, including gearshift between the S and long 4FSK rungs at 1000 Hz FSK ladder (`{4C,4A}`).

**Phase C — PSK + 16QAM inventory (RX first, then TX) + real gearshift.**
All 100 Bd PSK/QAM modes, 1→2→4→8 carriers; Memory-ARQ for PSK/QAM; full ladders. RX-before-TX matters: RX coverage of a bandwidth class is what unlocks unrestricted sessions at that class (§1.3 — the remote ISS picks the mode, so we must decode everything in-ladder even if our TX still prefers robust rungs). Exit: Rung 3 green *without* FSKONLY at all four bandwidths; OBW gate green (§6.3).

**Phase D — host interface + Pat + bench.**
`ArdopHostServer` (command subset pinned against Pat's source), daemon config integration, failsafe; Rung 4 (Pat loopback, both topologies); then Rung 5 on Tom's bench (real HF, live gateway). 600 Bd FM modes and RXO mode ride along here or later, gated by interest.

Phase A/B can interleave development-wise (the ARQ engine is pure logic, testable against scripted frame sequences before the codec is done), but each phase's exit is a hard gate before the next's exit is attempted.

### 7.1 The riskiest block

**The ARQ timing machine driven by real-time audio is the top integration risk.** Not because the state machine is unclear (it's well documented and only ~2.7 k lines of C), but because correctness is *temporal*: an IRS that demodulates perfectly but gets its ACK on the air 300 ms late fails every session (§4.4 budgets), and failures present as flaky sessions rather than clean assertion failures. De-risked by: the ardopcf↔ardopcf baseline on the identical rig (Rung 3), per-event timestamped session logs on both sides, and the daemon's existing PTT-lead-time discipline (which the FreeDV bench work already exercised).

**The PSK/16QAM demodulator robustness is the top DSP risk** — matching ardopcf's decode thresholds with no equalizer and only start-of-frame phase references, especially 16QAM amplitude tracking (§3.6). De-risked by: `INPUTNOISE` SNR sweeps against ardopcf's own decode rate on identical WAVs (relative bar, not absolute), and by the fact that ARQ sessions degrade gracefully — a modem that gears down earlier than ardopcf still interoperates, just slower (documented, measured, improved iteratively).

The leader acquisition front-end is the third watch item; it is fully exercised by Rung 1 offset/noise variants before anything depends on it.

---

## 8. Licence & provenance

### 8.1 ardopcf licence — verified text

`LICENSE` (26 lines): **MIT**, "Copyright (c) 2014-2024 Rick Muething, John Wiseman, Peter LaRue", with a trailing clarification that the MIT grant covers the ardop software by those authors and **not** the `lib/` directory, where "each subdirectory there identifies the author of that library and the license under which it may be used". Relevant `lib/` pieces: `rockliff/rrs.c` (Simon Rockliff's RS codec, custom permissive notice — freely modifiable/distributable with author attribution, `rrs.txt:38-47`); the rest (hid/rawhid/ws_server/zf_log/txt2c) we don't touch. The spec itself: "The specification and the protocol are released to the public domain" (spec §1.1). The README adds a *request* (not a licence term) that incompatible derivatives not use the "ardop" name — we comply by building a compatible implementation and naming ours by capability anyway.

**Verdict: MIT → GPL-3.0-or-later is the easy direction.** MIT code/algorithms may be incorporated into a GPL work provided the MIT copyright/permission notice is preserved for the ported material. No relicensing question like FreeDV's LGPL-2.1 item (ofdm-design §1.4/R-1). We don't port `rrs.c` at all (our own `ReedSolomon.cs` already covers it, §3.5), so the Rockliff notice is moot — but cite it in PROVENANCE.md anyway since we validated parameters against it.

### 8.2 Port vs clean-room: **port from ardopcf, cite the spec**

The spec is protocol-grade, not implementation-grade: it has no leader-detection algorithm, no demod math, no scaling factors, and its Appendix B/C tables are partly page images; meanwhile the operative CRC is a non-standard formulation only visible in code (§3.1), and interop details like the frame-type parity quirk (§3.1 item 3) exist only in code. A clean-room build from the spec alone would re-derive ardopcf empirically anyway — with worse fidelity. MIT permits porting; so:

- **Policy**: per-file provenance headers on every ported file citing ardopcf file + commit `a7c9228` + MIT attribution (Muething/Wiseman/LaRue), same discipline as the codec2 ports; spec section citations alongside where the spec does specify the behaviour.
- `PROVENANCE.md` gains an ardopcf section (kind-2 "source read directly during development" + kind-4 rows for anything established empirically, e.g. CRC vectors).
- Checked-in WAV/byte fixtures are generated output, committed with provenance rows (same as the codec2/Dire Wolf fixtures).

---

## 9. Deliberate deviations & recorded unknowns

### 9.1 Deviations (none silent)

- v1 dedicated-channel policy (§2.2) — a pdn deployment decision, not a protocol deviation.
- Goertzel-first (default) rather than SDFT 4FSK decoder (§3.4) — matches ardopcf's default; SDFT later.
- Host-command subset (§5.2) — unsupported commands answer with plausible inert responses, logged; widened as hosts demand.

### 9.2 Not determinable from this scoping read (flagged, do not confabulate)

- **Pat's exact command usage** — needs its source (§5.2). Until pinned, the subset is an educated list.
- **Gateway-side software mix** (§1.3) — irrelevant to protocol correctness, relevant to bench expectations.
- **ARDOP_Win behavioural quirks vs ardopcf** — we validate against ardopcf only; if a live gateway runs ARDOP_Win and behaves differently, that's a Rung-5 finding to record (ardopcf's own compatibility with ARDOP_Win is our shield here).
- **Exact `Gearshift_9` internals** — function located (`ARQ.c:717`), thresholds and stats arrays read, full 76-line body not traced in this pass; port it verbatim at implementation time, don't re-derive.
- **`CorrectPhaseForTuningOffset` / `Track1CarPSK` numerics** — located and understood in role, not yet transcribed; they're the heart of Phase C.
- **Leader-timing negotiation edge cases** (`CalculateOptimumLeader` is commented out in ardopcf, `ARQ.c:531-536` — the received-leader byte is still exchanged; what ardopcf currently *does* with it needs a focused read during Phase B).

### 9.3 Spec-vs-code conflicts noticed so far

- CRC-16 described as CCITT 0x1021 (spec App. B) vs the 0x8810-constant LSB-injection formulation actually shipped (`ARDOPC.c:1673`) — code wins; record in PROVENANCE.
- Spec App. B table's "RS FEC+CRC" column vs `FrameInfo`'s `intRSLen` (e.g. 4FSK.500.100: table "16+2", code RS=16 + CRC=2 separately) — consistent once decoded, but transcribe frame geometry from `FrameInfo`, not from the PDF table (which is an image in places).

---

## 10. Open questions for Tom

1. **Host protocol**: recommendation is byte-compatible ardopcf host interface, default ports 8515/8516, no pdn-native surface in v1 (§5.2). OK?
2. **Dedicated-channel policy** (§2.2): acceptable for v1 that an ARDOP channel can't simultaneously run KISS packet modes?
3. **Bench/gateway logistics for Rung 5**: which HF rig + antenna from the bench pool; your callsign for over-air ARQ (and a Winlink account for the Pat leg — password lives in Pat's config); preferred UK ARDOP gateways/bands to target; any RSGB/Ofcom constraints you want observed for unattended testing (the protocol's ID frame + optional CW ID covers legal ID, §4.5).
4. **16QAM acceptance bar** (§7.1): is "decodes ardopcf's clean+moderate-SNR 16QAM, gears down earlier than ardopcf under stress" acceptable for first release, with parity tracked as an improvement item?
5. **600 Bd FM modes** (`USE600MODES`): in scope at all? (VHF/UHF FM niche; cheap after Phase A but zero Winlink relevance.)
6. **RXO monitor mode**: worth exposing (e.g. to the collector) as a cheap ARDOP-band sniffer?
