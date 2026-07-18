# pdn-soundmodem roadmap

Living roadmap of open work. Snapshot committed 2026-07-17. Complements
[`docs/plan.md`](plan.md) (the phase plan + §17 amendment log) and
[`docs/waveform-roadmap.md`](waveform-roadmap.md) (the approved modem build order and the
standing quality/OBW directives). Where those disagree with this file, the amendment log in
`plan.md` §17 is authoritative — update all three together.

Standing directives that gate everything here: **proven reliable, not "only just working"**
(bit-exact vs oracle → channel models → real radio loop before "done"); **OBW never exceeds the
reference** (CI-enforced); **NinoTNC compatibility is never traded away**. Substantive phases run
as background sub-agents in worktrees; the orchestrator verifies on a fresh checkout (build +
suite + re-run any flaky test in isolation) before merging.

---

## Done (context)

- **FreeDV datac OFDM** — all six modes (datac0/1/3/4/13/14), TX+RX, pure-managed C# port
  validated bit-exact-equivalent to codec2; interoperates with stock `freedv_data_raw_tx/rx`
  both directions; burst acquisition; KISS modes `freedv-datac*` carrying IL2P+CRC; OBW
  never-wider-than-FreeDV CI-enforced.
- **POCSAG** — spec-first, multimon-ng-validated; daemon `--paging` endpoint + `HEARD` feed.
- **ARDOP** — software-complete at ardopcf parity (see #6 below for what's left).
- **MIL-STD-188-110D App D** — Phase A landed, mask-gated (see #7).
- **FlexRadio 6500** — offline client + hardware bring-up proven; live off-air RX (see #11).
- Roadmap/design docs, the dual-verified 110D tables, the ARDOP spec-as-Markdown, HF-loop and
  Flex-integration procedures, and one real off-air regression fixture
  (`samples/offair/gb7rdg-ninotnc-bpsk300-il2pc.wav`).

---

## Open work

### #4 — FreeDV datac: HF-loop validation *(in progress)*
The datac modes are KISS-integrated and tested through the IModem surface; **only real-radio
validation remains**. Procedure written: [`docs/freedv-hf-loop.md`](freedv-hf-loop.md) — the 8
radio-only unknowns, rig recipe, drive commands, pass criteria. Needs an HF radio loop (Tom can
supply; a **Flex variant** of the procedure is now viable — see #11). Feeds results back into the
doc + `plan.md` §17 to close.

### #6 — ARDOP: on-air acceptance *(in progress)*
Phases A–D complete at ardopcf parity — 4FSK/PSK/16QAM (0 dB noise-knee delta), the ARQ engine
live both roles, byte-identical host interface with a **real Pat B2F message** proven, RXO
monitor. **Remaining: the on-air acceptance** — peer-to-peer ARDOP on the 40m UK packet channel
from **GB7RDG's HF port** (operate as M0LTE), where ARDOP stations already run. Winlink gateway
session is optional gravy (Pat works via the host interface for free). Write the on-air bench doc
before the session; add the busy-detector port if channel-sharing needs it on air.

### #7 — MIL-STD-188-110D App D: Phases B/C *(in progress)*
Phase A (Walsh-75/BPSK/QPSK + LMS DFE) landed, **all 12 mask points 0 errors at full statistical
budget**, KISS-integrated (`ms110d-wn*`). Constants dual-verified; design in `docs/ms110d/`.
- **Phase B** — 8PSK/16QAM (WN7–9) + an **RLS equaliser** for fading; Poor-channel mask gating
  becomes binding. **Parked for Fable** (Tom, 2026-07-17): the hardest-reasoning DSP left, no
  deadline. All remaining 110D work is ordinary DSP (no more vision/transcription).
- **Phase C** — 32/64/256-QAM (groundwave-gated, high-SNR).
- Validation is pdn↔pdn only (no external oracle exists) — the spec's D-LXIV/LXV masks in the
  Watterson/CCIR sim + self-consistency; the off-air/hardware rung is parked (Tom).

### #8 — Own FM OFDM *(pending)*
Greenfield speed play through an FM voice channel (VARA FM is the only incumbent, and closed).
Reuses the FreeDV OFDM engine. **Needs Tom's FM radio loop** (PAPR / pre-emphasis / deviation
can't be fully simulated). OBW-critical by design.

### #9 — Own HF OFDM *(pending, blocked by #8)*
Narrower HF-SSB OFDM aiming to beat FreeDV datac on throughput/robustness. Wants the shared
engine + the FM work first.

### #10 — R-1: codec2 LGPL→GPL port licence analysis *(pending)*
Critical analysis of the legal basis for the managed codec2 port (LGPL-2.1 → GPL-3.0-or-later
via §3 relicensing) + attribution (Rowe/Valenti/Cowley). Scheduled by Tom as a roadmap task;
not a blocker, not ours to bless — needs a real FOSS-licence sanity check.

### #11 — FlexRadio 6500 integration *(in progress — nearly done)*
Pure-managed client: discovery + TCP session + VITA-49 DAX RX/TX → `IAudioInput`/`IAudioOutput`/
`IPttControl`, `--device flex:<radio>`. Offline Phases 0–2 merged (mock radio, byte-exact loop).
**Hardware proven on M0LTE's 6500**: discovery, session, headless GUI-client + slice, DAX RX
(0 loss) / TX, PTT (139 ms settle). **Live off-air RX proven** — decoded GB7RDG's NinoTNC BPSK300
through the Flex's DAX audio, no sound card in path.
- **Software-complete (PR #44 merged):** headless setup + the band-persistence tune fix
  (`EnsureTunedAsync` — a headless slice otherwise stays on the wrong band) + `--flex-daxch`
  DAX-channel coexistence, mock-validated (949/0). The tune fix is HW-proven on the 6500.
- **Remaining:** shipped-daemon final hardware confirmation (a FreeDV-datac / ARDOP frame into the
  dummy load), and a **Flex variant of the HF-loop** (folds into #4/#6). OBW self-capture is NOT
  viable on the 6500's public API (panadapter TX trace is leakage) — bench/second-RX stays the
  OBW path.
- **IQ interfaces (Tom's prompt) — researched + TX proven, 2026-07-17** (see
  [flex-integration.md §9](flex-integration.md)): **RX** = DAX-IQ, wideband complex baseband, but
  **receive-only** (K3TZR: no IQ-TX via DAX). Good for multi-channel monitoring + wide own-mode RX.
  **TX** = the SmartSDR **Waveform API** — the *only* IQ-TX door on a Flex, and it is **GPL-3.0**
  (port, don't depend), **runs off-radio on a network host** (headless-friendly), and is **proven
  end-to-end on the 6500**: a from-scratch client registered a custom waveform over TCP, owned a
  headless slice in that mode, keyed, and the radio pulled 224 TX-IQ packets from us
  (`interlock=TRANSMITTING`, dummy load, 24 kHz/128-complex). Open gate for wideband own-modes
  (#8/#9): achievable on-air TX **bandwidth** (24 kHz-rate but USB-routed; `underlying_mode=RAW/IQ`
  and wide `tx_filter` accepted). Bandwidth **MEASURED on air 2026-07-18** (via M0LTE's UberSDR
  hearing the dummy-load leakage): **`underlying_mode=RAW` gives true wideband complex IQ→RF**
  (both sidebands, ~14–20 kHz, capped by the 24 kHz waveform rate); USB/IQ are SSB-limited. So the
  Waveform API is a genuine wideband-TX path for own-modes (#8/#9), not an SSB dead-end. (Second-
  slice DAX-IQ self-capture was confirmed non-viable — RX blanked during TX.) Multi-channel RX
  (DAX-IQ + DDC) is the low-risk near-term win — **front-end built** (`src/Packet.SoundModem/Iq/`,
  concurrent 2-channel AFSK decode) **and the real DAX-IQ transport now landed + hardware-validated**
  (`FlexRadio/FlexDaxIqSource.cs` over the M0LTE.Flex `VitaPacketReceived` event; 238k IQ samples/2s,
  0 loss on the 6500). Remaining: daemon/CLI wiring to select channels and place their offsets.

---

## Cross-cutting follow-ups (issues from live-RF validation, 2026-07-17)

- **#42 — NinoTNC BPSK is DEBPSK; our Coherent detector can't decode it.** Real off-air GB7RDG
  decodes with `Differential` only; `Coherent` (the default from #5) fails even strong + centred,
  because the NinoTNC uses coherent Costas demod **with** differential encoding to beat the 180°
  ambiguity, and our coherent path omits the differential-decode step. **Fix:** add a
  differential-decode step after coherent carrier recovery (match the NinoTNC's modified Costas
  loop), or default HF BPSK to Differential; re-examine the #5 bench result. Highest-value modem
  fix in the queue — it's a real NinoTNC-interop gap. Fixture committed.
- **#40** — the general coherent-vs-differential off-air finding (now explained by #42).
- **#39 — RESOLVED** (2026-07-18): the narrow modem tone/carrier centre is now variable per-mode
  (QtSoundModem-style), on both TX and RX, via `--modem N:MODE:FREQ` / config `"frequency"`. Covers
  the AFSK tone-pair modes (afsk*, default 1700) and the BPSK/QPSK carrier modes (bpsk*/qpsk*,
  default 1500; 1650 for qpsk3600). Completing the plumbing exposed + fixed a real bug: all three
  AFSK1200 modems' modulators were hardcoded to the Bell-202 1200/2200 tones, so their TX ignored
  the centre (the demod already honoured it) — now both sides shift together. The PSK factories
  (Bpsk1200/Qpsk600/2400/3600) gained a `carrierFrequency` param. Baseband FSK (fsk*/c4fsk*, no
  audio centre) and the spec-fixed waveforms (freedv-*/ms110d-*/POCSAG/ARDOP) stay fixed — a
  `:FREQ` on any of those is now rejected at start-up, not silently ignored. `CentreFrequencyTests`
  locks in the round-trip-at-a-shifted-centre behaviour; README/config/DaemonConfig document the
  coverage. The GB7RDG-was-~41-Hz-off case (#40) is now correctable in the field.
- **#33** — flaky ARDOP host TCP test under full-suite load (races on port bind); harden the test.

---

## Needs Tom + a radio (the specific actions)

These are the concrete steps that can't be done in software alone. None is blocking; each is
self-contained and can be picked up standalone. Operate as **M0LTE**.

1. **Flex daemon live confirmation** (#11) — *~2 min, radio already on the bench.* Run the
   shipped daemon against the 6500 and push one real frame into the ANT1 dummy load:
   `pdn-soundmodem --device flex:10.45.0.76 --flex-freq <MHz> --flex-ant ANT1 --modem 0:freedv-datac3 --kiss 8105`
   (or `--ardop 8515` for ARDOP). Success = `interlock=TRANSMITTING`, RF on the dummy load, no
   setup errors. Closes the last Flex item.
2. **FreeDV datac HF-loop validation** (#4) — *needs an HF rig, or the Flex path.* Follow
   [`docs/freedv-hf-loop.md`](freedv-hf-loop.md) (rig recipe, the 8 radio-only unknowns, drive
   commands, pass criteria). Record results back into that doc + `plan.md` §17. Closes #4.
3. **ARDOP on-air acceptance** (#6) — *needs GB7RDG's HF port on 40m.* Peer-to-peer ARDOP with
   the stations already on the UK 40m packet channel; Winlink/Pat gateway session optional. Write
   the on-air bench doc first (à la the FreeDV one). Closes #6.
4. **Own FM OFDM — an FM radio loop** (#8) — *later, when the #8 build starts.* Required to
   validate PAPR / pre-emphasis / deviation, which simulation can't fully capture.
5. **GB7RDG traffic on request** — *optional, opportunistic.* Once the #42 coherent+differential
   fix lands, capture more live NinoTNC BPSK300 off-air through the Flex to confirm the fix on
   real signals (this is how #39/#40/#42 were found). A long carrier tone + a frame is the ideal
   calibration transmission.

**Hardware available:** a Flex 6500 (10.45.0.76, on the bench with an ANT1 dummy load; GB7RDG's
transceiver couples into it), an HF rig / GB7RDG's HF port, and an FM radio loop.

## Parked / non-goals

- **M17** — parked (Tom): kept in the survey, not on the build path.
- **VARA HF/FM, PACTOR II–IV, P25, NXDN, System Fusion/C4FM, FLEX** — proprietary, cannot implement.
- **FreeDV voice coexistence** — a non-goal (data and voice never share a channel).
