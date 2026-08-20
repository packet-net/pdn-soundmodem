# pdn-soundmodem roadmap

Living roadmap of open work. Founded 2026-07-17; **fully reconciled 2026-08-20** against
`mode-validation.md`, `plan.md` §17 and `rx-roadmap.md`. Complements
[`docs/plan.md`](plan.md) (the phase plan + §17 amendment log) and
[`docs/waveform-roadmap.md`](waveform-roadmap.md) (the approved modem build order and the
standing quality/OBW directives). Where those disagree with this file, the amendment log in
`plan.md` §17 is authoritative - update all three together.

Standing directives that gate everything here: **proven reliable, not "only just working"**
(bit-exact vs oracle → channel models → real radio loop before "done"); **OBW never exceeds the
reference** (CI-enforced); **NinoTNC compatibility is never traded away**. Substantive phases run
as background sub-agents in worktrees; the orchestrator verifies on a fresh checkout (build +
suite + re-run any flaky test in isolation) before merging.

---

## Done (context)

- **FreeDV datac OFDM** - all six modes (datac0/1/3/4/13/14), TX+RX, pure-managed C# port
  validated bit-exact-equivalent to codec2; interoperates with stock `freedv_data_raw_tx/rx`
  both directions; burst acquisition; KISS modes `freedv-datac*` carrying IL2P+CRC; OBW
  never-wider-than-FreeDV CI-enforced. **All six on air 2026-07-28** (closes #4).
- **POCSAG** - spec-first, multimon-ng-validated; daemon `--paging` endpoint + `HEARD` feed.
- **ARDOP** - software-complete at ardopcf parity, plus a maskable sim seam (2026-08-08).
  Still not on air: see #6.
- **MIL-STD-188-110D App D** - Phases A and B closed, mask-gated, on-air campaign clean, and the
  **WN8 redesign program closed 2026-07-31**. Phase C is what remains: see #7.
- **The NinoTNC packet family on air** - `bpsk300` and `afsk300-il2pc` are the live 40 m modes,
  validated off air from three independent receive chains, with the receive chain rebuilt against
  measurement (PRs #236/#238) and floors pinned by `WattersonMaskTests`.
- **FlexRadio 6500** - client, hardware bring-up and **production deployment**: GB7RDG's 40 m port
  has run headless on the 6500 since 2026-08-07 (see #11 for what that surfaced).
- **The daemon became a station rather than a library demo** - its own browser waterfall
  (2026-08-01), signal survey and frame log, `ubersdr:` receive-only device, runtime KISS SETHW
  waveform control, per-modem Morse ident (2026-08-15), and a **runtime modem plugin seam**
  (2026-08-09) that lets a station run a modem this repository does not contain.
- **The measurement discipline itself** - the Watterson mask ladder and its accept rule
  (CLAUDE.md), the `sm-ota sim` bench, the FM channel model, and the live 40 m capture campaign
  under `/home/tf/capture-40m/`.
- Roadmap/design docs, the dual-verified 110D tables, the ARDOP spec-as-Markdown, HF-loop and
  Flex-integration procedures, and one real off-air regression fixture
  (`samples/offair/gb7rdg-ninotnc-bpsk300-il2pc.wav`).

---

## Open work

Numbering is by issue, so items that have closed are kept here with their closing evidence rather
than deleted - a reader arriving at "#4" should find out what happened to it, not find a gap.

### #4 - FreeDV datac: HF-loop validation *(CLOSED 2026-07-28)*
All six `freedv-datac*` modes acquire and decode **over the air**, proven by the 12-run AWGN+Poor
sweep over the Flex to DIGU to RSP1 rig via the DAX route
(`docs/ms110d/evidence/2026-07-28-freedv-ofdm-oncar/`, harness PR #108). AWGN matched the sim
baseline (datac1 +1.8 dB exact, datac3 within 0.6 dB); the narrow modes read below sim for
understood reasons (known sim-Poor pessimism plus the 3 kHz SNR reference against their 250-500 Hz
occupied bandwidth). That forwarded all six from not-yet-on-air to working in
[`mode-validation.md`](mode-validation.md). [`docs/freedv-hf-loop.md`](freedv-hf-loop.md) remains
the written procedure.

**Successor, not a reopening:** [`docs/freedv-ota-plan.md`](freedv-ota-plan.md) is Tom's campaign
transmitting FreeDV from the GB7RDG node into 7053-7060 identifying as M0LTE in CW. Its one
blocking gap - the daemon could not identify itself at all - closed 2026-08-15 with
`StationIdentifier` and the per-modem `identify` block.

### #6 - ARDOP: on-air acceptance *(in progress - still the open item)*
Phases A-D complete at ardopcf parity - 4FSK/PSK/16QAM (0 dB noise-knee delta), the ARQ engine
live both roles, byte-identical host interface with a **real Pat B2F message** proven, RXO
monitor. Since the July snapshot ARDOP has also gained a **shared-channel shifted-centre session**
(pdn to pdn, byte-exact both directions including the BREAK turnover, 2026-08-02) and a
**maskable sim seam** with 8 blocking plus 23 full-tier mask rows (2026-08-08).

**Still not proven on air: nothing, at any centre.** That is unchanged and is the whole of #6.
Two measured truths now bound what to expect: the top rungs (8PSK/16QAM.500.100) decode
essentially nothing under CCIR Moderate or Poor at any plausible NVIS SNR, and the single-shot
ceiling is ~98 % rather than 100 % because certain noise realisations false-trigger the leader
detector. The campaign is written up in [`docs/ardop/plan.md`](ardop/plan.md) (opened 2026-08-08),
which starts from receive-only monitoring of the wild third-party ARDOP sessions the capture
campaign found in slot 2. Rung 5 of the acceptance ladder remains outstanding.

### #7 - MIL-STD-188-110D App D *(Phases A+B closed, WN8 program closed; only Phase C remains)*
Phase A (Walsh-75/BPSK/QPSK + DFE) closed 2026-07-23 - all mask points 0 errors at full
statistical budget, KISS-integrated (`ms110d-wn*`); `docs/ms110d/phase-a-closeout.md`.
- **Phase B closed 2026-07-27** (`docs/ms110d/phase-b-closeout.md`): 8PSK (WN7) / 16QAM (WN8)
  landed and the Poor-channel gating went binding - WN0-6+13 hard-gated at mask. WN7 closed
  measured-only at the waveform's own fade-lottery floor (needs added information - diversity,
  ARQ, outer coding - from outside the demodulator).
- **WN8 redesign program closed 2026-07-31** (exit ii), the day it was registered: WN8 decodes on
  Poor. W1's truth-injection instrument showed the "immovable" Phase B ceiling was the estimator's
  segment time model, W1b's matched-filter bound proved the waveform was never the floor, and W5
  shipped `Ms110dMfbBlockDecoder`. Closing verdict **Poor WN8 2.90E-4 canonical / 1.75E-2
  disjoint**, a 1711x / 28x improvement on the coin-flip at entry - measured-only against the 1E-5
  mask, sim-only by rig physics. Exit (iii) permanently refuted. PRs #129-#139; program record
  `docs/ms110d/wn8-program-plan.md`, now historical.
- On-air (2026-07-27/28): every masked waveform meets its mask over the real rig
  (`docs/ms110d/evidence/2026-07-27-110d-full-campaign/`); WN7/WN8 Poor masks sit above the
  rig's ~15-16 dB ceiling, so those Poor points are sim-only by physical necessity.
- **A first-class tenant of the production config since 2026-08-06**: MS110D shares the 40 m plan
  with the packet modems (ARDOP bridged to 48 kHz, spec-fixed centres made movable, TX waveform
  switchable at runtime over KISS SETHW).
- **Phase C** - 32/64/256-QAM, WN9-12 (groundwave-gated, high-SNR) - **not started, and the only
  open leg of #7**.
- Validation is pdn to pdn plus on-air self-consistency (no external App D oracle exists). App C /
  STANAG 4539 was evaluated as a possible oracle and **rejected** - see
  [`waveform-roadmap.md`](waveform-roadmap.md) §4a.

### #8 - Own FM OFDM *(delivered, and it lives outside this repository)*
Built, and then deliberately moved out. **The waveform itself is now the private
`packet-net/pdn-ofdm-fm`**, loaded through the runtime modem plugin seam that landed with it
(2026-08-09, `docs/modem-binding.md`): `IModemPlugin` + `ModemDescriptor` + a `ModemPluginLoader`
that loads an explicitly named assembly into its own collectible `AssemblyLoadContext`, with modes
namespaced `pluginId:mode` so `ofdm-fm:nb` can never shadow a built-in. **Not a licence problem, a
buildability one**: a public GPL work has to stay buildable by anyone who clones it, and OFDM-FM's
profiles were sized against numbers supplied unofficially by IP400's author. A fresh clone with no
plugin present builds and passes the suite, which was the point.

**Deliberately, `mode-validation.md`'s matrix has no OFDM-FM row and should not grow one** - the
matrix tracks `KnownModes`, and a mode whose waveform is not in this repository is not one this
repository can vouch for.

What stayed here and serves everything: the **FM channel model and the FM mask ladder** (five
blocking masks, 2026-08-08; the model's rate-dependent filter defect found and every mask
re-measured through the fix, 2026-08-09), and the **deviation sweep** across every FM mode
(2026-08-10) which found two modes leaving 3-4 dB on the table and dropped qpsk3600 to 2500 Hz.
OFDM-FM's own later work - header coding, rate negotiation - is recorded in the ledger because it
was measured with this repo's instruments.

### #9 - Own HF OFDM *(pending; no longer blocked)*
Narrower HF-SSB OFDM aiming to beat FreeDV datac on throughput and robustness (adaptive
bit-loading, pilot/CP tuning), benchmarked head-to-head against our datac implementation. The #8
dependency is discharged - the shared engine, the plugin seam and the FM channel model all exist -
so this is now simply unstarted. It is also **the standing answer to "more capability in narrow
channels"**: `waveform-roadmap.md` §4a records why no MIL-STD appendix supplies one.

### #10 - R-1: codec2 LGPL→GPL port licence analysis *(pending)*
Critical analysis of the legal basis for the managed codec2 port (LGPL-2.1 → GPL-3.0-or-later
via §3 relicensing) + attribution (Rowe/Valenti/Cowley). Scheduled by Tom as a roadmap task;
not a blocker, not ours to bless - needs a real FOSS-licence sanity check.

### #12 - 2G ALE in software *(planned, not started; the reason it was held back has expired)*
MIL-STD-188-141A Appendix A link establishment, so an MS110D station can find a working channel
and a listening correspondent unattended. Plan: [`docs/ale/plan.md`](ale/plan.md). Prompted by the
Kenwood TK-90 assessment - its ALE needs a KPE-2 board that cannot be obtained, and doing it in
software removes the dependency on any particular radio, makes LQA our own measured SNR rather
than a vendor's opaque score, and is a smaller job than the modem already built (8-FSK, no
equaliser, no turbo decoder).

**Still unscheduled, but the stated reason has expired.** The July entry held it back because
"MS110D's Poor gate is open and §E2 has never run on hardware". The Poor gate closed for WN0-6+13
in Phase B and the WN8 program closed on 2026-07-31, leaving only WN7's fade-lottery floor and
WN8's measured-only standing - so this is now unscheduled **by priority, not by blocker**, and
should be re-ranked on its merits rather than left sitting behind a gate that has gone.

The one exception is cheaper than ever: decoding real off-air 2G ALE needs no transmit licence, no
partner and no hardware this project does not already own, the `ubersdr:` device supplies an
antenna for free, and it would verify the waveform constants empirically.

### #11 - FlexRadio 6500 integration *(in production; the remaining work is operational)*
Pure-managed client: discovery + TCP session + VITA-49 DAX RX/TX → `IAudioInput`/`IAudioOutput`/
`IPttControl`, `--device flex:<radio>`. Offline Phases 0-2 merged (mock radio, byte-exact loop).
**Hardware proven on M0LTE's 6500**: discovery, session, headless GUI-client + slice, DAX RX
(0 loss) / TX, PTT (139 ms settle). **Live off-air RX proven** - decoded GB7RDG's NinoTNC BPSK300
through the Flex's DAX audio, no sound card in path.
- **Software-complete (PR #44 merged):** headless setup + the band-persistence tune fix
  (`EnsureTunedAsync` - a headless slice otherwise stays on the wrong band) + `--flex-daxch`
  DAX-channel coexistence, mock-validated (949/0). The tune fix is HW-proven on the 6500.
- **The "shipped-daemon hardware confirmation" is closed by deployment**, not by a bench run:
  GB7RDG's 40 m port has run headless on the 6500 since 2026-08-07, and the Flex variant of the
  HF-loop is what carried the FreeDV (#4) and MS110D (#7) on-air campaigns. OBW self-capture is
  NOT viable on the 6500's public API (panadapter TX trace is leakage) - bench/second-RX stays the
  OBW path.
- **Three defects only production could show**, all since fixed: a second headless client
  **silently stole the slice** and left the production modem six days deaf and mute (2026-08-07 to
  08-13; the DAX collision was *not* the mechanism, which `flex-integration.md` asserted until it
  was actually tested against the live radio);
  the slice **receive filter had never taken on hardware** at all, because the client wrote the
  report-side names with `slice set` instead of `filt` (M0LTE.Flex 0.14.0), so `receiveLow`/
  `receiveHigh` had been computed, announced and discarded since the day they landed; and the
  read-back that reported it was **re-reading its own cache** rather than the radio (0.14.1, fixed
  by re-subscription rather than by waiting). Each was a mock that agreed with the code instead of
  with the radio.
- **Remaining:** daemon/CLI wiring for multi-channel DAX-IQ (select channels, place their
  offsets), and the nine shared-PA hardware probes P1-P9 (`docs/flex-integration.md` §11) - the
  `arbitration` default only flips after P1/P2/P3.
- **IQ interfaces (Tom's prompt) - researched + TX proven, 2026-07-17** (see
  [flex-integration.md §9](flex-integration.md)): **RX** = DAX-IQ, wideband complex baseband, but
  **receive-only** (K3TZR: no IQ-TX via DAX). Good for multi-channel monitoring + wide own-mode RX.
  **TX** = the SmartSDR **Waveform API** - the *only* IQ-TX door on a Flex, and it is **GPL-3.0**
  (port, don't depend), **runs off-radio on a network host** (headless-friendly), and is **proven
  end-to-end on the 6500**: a from-scratch client registered a custom waveform over TCP, owned a
  headless slice in that mode, keyed, and the radio pulled 224 TX-IQ packets from us
  (`interlock=TRANSMITTING`, dummy load, 24 kHz/128-complex). Open gate for wideband own-modes
  (#8/#9): achievable on-air TX **bandwidth** (24 kHz-rate but USB-routed; `underlying_mode=RAW/IQ`
  and wide `tx_filter` accepted). Bandwidth **MEASURED on air 2026-07-18** (via M0LTE's UberSDR
  hearing the dummy-load leakage): **`underlying_mode=RAW` gives true wideband complex IQ→RF**
  (both sidebands, ~14-20 kHz, capped by the 24 kHz waveform rate); USB/IQ are SSB-limited. So the
  Waveform API is a genuine wideband-TX path for own-modes (#8/#9), not an SSB dead-end. (Second-
  slice DAX-IQ self-capture was confirmed non-viable - RX blanked during TX.) Multi-channel RX
  (DAX-IQ + DDC) is the low-risk near-term win - **front-end built** (`src/Packet.SoundModem/Iq/`,
  concurrent 2-channel AFSK decode) **and the real DAX-IQ transport now landed + hardware-validated**
  (`FlexRadio/FlexDaxIqSource.cs` over the M0LTE.Flex `VitaPacketReceived` event; 238k IQ samples/2s,
  0 loss on the 6500). Remaining: daemon/CLI wiring to select channels and place their offsets.

### Receive performance: the rx-roadmap workstreams *(live programme, opened 2026-08-06)*
Not an issue but the largest body of open work, and the one this roadmap had no entry for at all.
[`docs/rx-roadmap.md`](rx-roadmap.md) is the durable plan; the standing assessment (2026-08-15) is
that **the implementation is in good shape and what is left is structural rather than defective** -
the bpsk300 demodulator now sits within ~0.5-1 dB of the matched-filter bound on AWGN, and the
remaining losses are above the bit decision, which is hard-decision, single-look and causal.

Ranked workstreams: 0 Watterson masks and the accept discipline (**landed**, and now binding via
CLAUDE.md), 1 soft-decision and erasure Reed-Solomon (the big cross-repo lever), 2 trailer
corroboration (**landed 2026-08-06**), 3 retransmission soft combining (the sleeper), 4 two-pass
non-causal burst processing, 5 Poor-channel MLSE (**built, measured, and its own 50-60 % claim
retired** - most of Poor's loss is flat outage across 60-150 symbol times, a ceiling rather than a
backlog item), 6 channel-model extensions starting with impulse noise, 7 per-station acquisition
priors, 8 waveform escalation between consenting stations (**now assessed as the biggest real
lever, and it is not receive-side**), 9 per-frame SNR in the record (**landed**).

Feeding it: the **live 40 m capture campaign**, `pdn-capture-40m.service` driving the Flex
receive-only and keeping continuous 12 kHz raw audio plus frame log plus survey under
`/home/tf/capture-40m/`. It exists because the 37-frame miss corpus is nearly exhausted as a
discriminator.

---

## Cross-cutting follow-ups (issues from live-RF validation, 2026-07-17)

- **#42 - RESOLVED** (2026-07-17/18): NinoTNC BPSK is DEBPSK and the Coherent default could not
  decode it. The NinoTNC uses coherent Costas demod **with** differential encoding to beat the
  180° ambiguity, and our coherent path omitted the differential-decode step. Fixed by reverting
  the default to **differential detection plus a 4-pair frequency-diversity bank** (PR #52,
  PR #56), with the off-air GB7RDG capture checked in as `OffAirBpskTests.cs` (PR #41). Coherent
  remains an opt-in detector and QPSK keeps its coherent default; `CoherentDetectionTests` pins
  the deliberate inversion (BPSK rows assert differential >= coherent). This landed the day after
  this file's snapshot, which is why it read as open for so long.
- **#40 - RESOLVED** with #42 (same fix): the general coherent-vs-differential off-air finding was
  the same defect.

**`bpsk300` since then** - now the network's flagship mode and the most heavily validated one in
the repo. Do not re-derive its state from this file; [`docs/mode-validation.md`](mode-validation.md)
is the authority and carries the dated ledger. Summary as of 2026-08-16:
- **On air continuously.** GB7RDG's 40 m port runs pdn-soundmodem with a `bpsk300` slot. The
  continuous replay of the 166.9 h 40 m capture archive reads 4394 decoded / 3914 deliverable,
  and the live slot's traffic fingerprints into four known stations by carrier offset alone
  (GB7BPQ and PD4R-12 at -18 Hz, GB7BEX-15 at -8.4 Hz, EI0RSI-7 at +1.6 Hz).
- **Instrumented.** On-air BER-vs-SNR waterfall over the Flex→RSP1 rig (2026-07-28, AWGN
  threshold ~ -3 dB); independently confirmed through a public web receiver (2026-08-02).
- **Receive chain rebuilt against measurement** (PR #236): ~1 dB more AWGN margin, decision-feedback
  differential detection, RRC matched filtering, retuned DPLL inertia.
- **Trailer corroboration** (PR #238) moved host-delivered frames from 3/37 to 22/37 on the 24 h
  miss corpus; chase decoding (M0LTE.Il2p 0.3.0) added +78 frames over the archive.
- **Floors are pinned by tests, not prose** - `WattersonMaskTests`, per the mask discipline in
  CLAUDE.md.
- **Still open:** the residual-miss aspiration scoreboard (`NinoTncMissCorpusAspirationTests`), and
  Poor remains equaliser-bound - a known mode limit, not a regression.
- **#39 - RESOLVED** (2026-07-18): the narrow modem tone/carrier centre is now variable per-mode
  (QtSoundModem-style), on both TX and RX, via `--modem N:MODE:FREQ` / config `"frequency"`. Covers
  the AFSK tone-pair modes (afsk*, default 1700) and the BPSK/QPSK carrier modes (bpsk*/qpsk*,
  default 1500; 1650 for qpsk3600). Completing the plumbing exposed + fixed a real bug: all three
  AFSK1200 modems' modulators were hardcoded to the Bell-202 1200/2200 tones, so their TX ignored
  the centre (the demod already honoured it) - now both sides shift together. The PSK factories
  (Bpsk1200/Qpsk600/2400/3600) gained a `carrierFrequency` param. Baseband FSK (fsk*/c4fsk*, no
  audio centre) and the spec-fixed waveforms (freedv-*/ms110d-*/POCSAG/ARDOP) stay fixed - a
  `:FREQ` on any of those is now rejected at start-up, not silently ignored. `CentreFrequencyTests`
  locks in the round-trip-at-a-shifted-centre behaviour; README/config/DaemonConfig document the
  coverage. The GB7RDG-was-~41-Hz-off case (#40) is now correctable in the field.
- **#33** - flaky ARDOP host TCP test under full-suite load (races on port bind); harden the test.
  **Status unverified at the 2026-08-20 reconciliation**: the 2026-08-06 whole-codebase review
  campaign (PRs #227-#234) lists "TCP server robustness" among its fixes, but nothing names #33,
  so it is carried forward as open rather than assumed closed.

---

## Needs Tom + a radio (the specific actions)

These are the concrete steps that can't be done in software alone. None is blocking; each is
self-contained and can be picked up standalone. Operate as **M0LTE**.

1. ~~**Flex daemon live confirmation** (#11)~~ - **DONE**, and then some: the daemon has been the
   production station on GB7RDG's 40 m port since 2026-08-07.
2. ~~**FreeDV datac HF-loop validation** (#4)~~ - **DONE 2026-07-28** via the Flex path; all six
   modes on air.
3. **ARDOP on-air acceptance** (#6) - *needs GB7RDG's HF port on 40m.* **The live one.** The bench
   doc now exists ([`docs/ardop/plan.md`](ardop/plan.md)), and its receive-only first leg needs no
   transmit licence and no partner - the band already carries daily wild third-party ARDOP
   sessions in slot 2. Peer-to-peer and the optional Winlink/Pat gateway session come after.
   Closes #6.
4. **Own FM OFDM - an FM radio loop** (#8) - *for `packet-net/pdn-ofdm-fm`, not this repo.*
   Required to validate PAPR / pre-emphasis / deviation, which simulation can't fully capture.
   This repo's FM channel model and mask ladder are the simulation half of it.
5. **Shared-PA hardware probes P1-P9** (#11) - *needs the 6500 and a dummy load.*
   `docs/flex-integration.md` §11. The `arbitration` default only flips after P1/P2/P3.
6. **GB7RDG traffic on request** - *optional, opportunistic.* The #42 fix landed and has since
   been confirmed on real signals many times over (GB7RDG's own 40 m port now runs the daemon), so
   this is no longer about #42. It stays on the list as the standing way to get a **calibration
   transmission** on demand: a long carrier tone plus a frame, which is how #39/#40/#42 were found
   in the first place.

**Hardware available:** a Flex 6500 (10.45.0.76, on the bench with an ANT1 dummy load; GB7RDG's
transceiver couples into it), an HF rig / GB7RDG's HF port, and an FM radio loop.

## Parked / non-goals

- **M17** - parked (Tom): kept in the survey, not on the build path.
- **VARA HF/FM, PACTOR II-IV, P25, NXDN, System Fusion/C4FM, FLEX** - proprietary, cannot implement.
- **FreeDV voice coexistence** - a non-goal (data and voice never share a channel).
