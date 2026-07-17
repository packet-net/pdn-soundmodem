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
- **In flight:** branch `flex-headless-tune` — the band-persistence tune fix (a headless slice
  otherwise stays on the wrong band) + `--flex-daxch` DAX-channel coexistence + mock modeling +
  docs. Verify + merge.
- **Then:** shipped-daemon final hardware confirmation (a FreeDV-datac / ARDOP frame into the
  dummy load), and a **Flex variant of the HF-loop** (folds into #4/#6). OBW self-capture is NOT
  viable on the 6500's public API (panadapter TX trace is leakage) — bench/second-RX stays the
  OBW path.
- **Later possibility (Tom's prompt):** DAX-IQ + the SmartSDR Waveform API — running our modems
  from raw IQ (no SSB filter/AGC) or as a Flex-native "mode". Noted for own-FM/HF (#8/#9).

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
- **#39** — make the modem tone/carrier centre variable per-mode (QtSoundModem-style); the GB7RDG
  signal sat ~41 Hz off our fixed 1500 Hz. Partially plumbed (`--modem N:MODE:FREQ`); complete +
  document coverage; spec-fixed waveforms (FreeDV/ARDOP/110D/POCSAG) stay fixed.
- **#33** — flaky ARDOP host TCP test under full-suite load (races on port bind); harden the test.

---

## Hardware Tom can supply (validation gates)

- **HF radio loop** — closes #4 (FreeDV), #6 (ARDOP on-air at GB7RDG/40m), and eventually the
  110D real-channel check. A **Flex 6500** path now exists (#11).
- **FM radio loop** — required for #8 (own FM OFDM).
- **GB7RDG RF access** — the on-air acceptance venue for #6; also the live off-air RX source that
  produced #39/#40/#42.

## Parked / non-goals

- **M17** — parked (Tom): kept in the survey, not on the build path.
- **VARA HF/FM, PACTOR II–IV, P25, NXDN, System Fusion/C4FM, FLEX** — proprietary, cannot implement.
- **FreeDV voice coexistence** — a non-goal (data and voice never share a channel).
