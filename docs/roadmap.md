# pdn-soundmodem roadmap

Living roadmap of open work. Snapshot committed 2026-07-17. Complements
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
  never-wider-than-FreeDV CI-enforced.
- **POCSAG** - spec-first, multimon-ng-validated; daemon `--paging` endpoint + `HEARD` feed.
- **ARDOP** - software-complete at ardopcf parity (see #6 below for what's left).
- **MIL-STD-188-110D App D** - Phase A landed, mask-gated (see #7).
- **FlexRadio 6500** - offline client + hardware bring-up proven; live off-air RX (see #11).
- Roadmap/design docs, the dual-verified 110D tables, the ARDOP spec-as-Markdown, HF-loop and
  Flex-integration procedures, and one real off-air regression fixture
  (`samples/offair/gb7rdg-ninotnc-bpsk300-il2pc.wav`).

---

## Open work

### #4 - FreeDV datac: HF-loop validation *(in progress)*
The datac modes are KISS-integrated and tested through the IModem surface; **only real-radio
validation remains**. Procedure written: [`docs/freedv-hf-loop.md`](freedv-hf-loop.md) - the 8
radio-only unknowns, rig recipe, drive commands, pass criteria. Needs an HF radio loop (Tom can
supply; a **Flex variant** of the procedure is now viable - see #11). Feeds results back into the
doc + `plan.md` §17 to close.

### #6 - ARDOP: on-air acceptance *(in progress)*
Phases A-D complete at ardopcf parity - 4FSK/PSK/16QAM (0 dB noise-knee delta), the ARQ engine
live both roles, byte-identical host interface with a **real Pat B2F message** proven, RXO
monitor. **Remaining: the on-air acceptance** - peer-to-peer ARDOP on the 40m UK packet channel
from **GB7RDG's HF port** (operate as M0LTE), where ARDOP stations already run. Winlink gateway
session is optional gravy (Pat works via the host interface for free). Write the on-air bench doc
before the session; add the busy-detector port if channel-sharing needs it on air.

### #7 - MIL-STD-188-110D App D *(Phases A+B complete; WN8 program closed; Poor-gate successor program live)*
Phase A (Walsh-75/BPSK/QPSK + DFE) closed 2026-07-23 - all mask points 0 errors at full
statistical budget, KISS-integrated (`ms110d-wn*`); `docs/ms110d/phase-a-closeout.md`.
- **Phase B closed 2026-07-27** (`docs/ms110d/phase-b-closeout.md`): 8PSK (WN7) / 16QAM (WN8)
  landed and the Poor-channel gating went binding - WN0-6+13 hard-gated at mask. WN7/WN8 close
  measured-only: WN7 at the waveform's own fade-lottery floor (needs added information -
  diversity/ARQ/outer coding - outside the demodulator); WN8 at coin-flip behind a true-label
  model ceiling and a bootstrap basin (needs waveform-processing redesign).
- **WN8 redesign program closed 2026-07-31, exit (ii)** (`docs/ms110d/wn8-program-plan.md`):
  both walls measured down (the ceiling was the estimator's time model; the matched-filter
  bound is 0/0; the basin is crossed label-free), the MFB-form receiver ships, WN8 Poor at
  2.90E-4 canonical / 1.75E-2 disjoint - decoding, still measured-only against 1E-5.
- **Poor-gate successor program registered 2026-08-20**
  (`docs/ms110d/poor-gate-successor-plan.md`, issue #312): G0 bank + re-baseline done; **G1d
  hard-gated WN7 Poor the same day (0/0 both families)** through the 8PSK per-block ensemble
  (DFE-chain beside the MFB-form decoder, evidence weighed in log-likelihood units) - nine of
  ten Poor points hard-gated. **G2** (same day) took WN8 Poor from 2.90E-4 / 1.75E-2 to
  12 / 18 per 4.3M bits with an MMSE cold rung and a reworked schedule in the MFB decoder -
  **all ten Poor points hard-gated**; **G3** (same day) proved every waveform through
  `FrequencyShiftedModem` at 3750/5000 Hz and fixed the Watterson rig for moved centres.
  Remaining: H1/H2, Tom's radio evening and the pad-chain ceiling.
- On-air (2026-07-27/28): every masked waveform meets its mask over the pad-chain rig
  (`docs/ms110d/evidence/2026-07-27-110d-full-campaign/`). **§E4 2026-08-03**: first
  transmissions from a real antenna at 48 W, WN0-7 and WN13 bit-exact into wessex, WN8 the
  ceiling of that path (`docs/ms110d/evidence/2026-08-03-e4-first-on-air/`). WN7/WN8 Poor
  masks sit above both rigs' ceilings, so those Poor points are sim-only until H2.
- **Phase C** - 32/64/256-QAM, WN9-12 (groundwave-gated, high-SNR) - not started.
- Validation is pdn↔pdn + on-air self-consistency (no external App D oracle exists).

### #8 - Own FM OFDM *(pending)*
Greenfield speed play through an FM voice channel (VARA FM is the only incumbent, and closed).
Reuses the FreeDV OFDM engine. **Needs Tom's FM radio loop** (PAPR / pre-emphasis / deviation
can't be fully simulated). OBW-critical by design.

### #9 - Own HF OFDM *(pending, blocked by #8)*
Narrower HF-SSB OFDM aiming to beat FreeDV datac on throughput/robustness. Wants the shared
engine + the FM work first.

### #10 - R-1: codec2 LGPL→GPL port licence analysis *(pending)*
Critical analysis of the legal basis for the managed codec2 port (LGPL-2.1 → GPL-3.0-or-later
via §3 relicensing) + attribution (Rowe/Valenti/Cowley). Scheduled by Tom as a roadmap task;
not a blocker, not ours to bless - needs a real FOSS-licence sanity check.

### #12 - 2G ALE in software *(planned, not started)*
MIL-STD-188-141A Appendix A link establishment, so an MS110D station can find a working channel
and a listening correspondent unattended. Plan: [`docs/ale/plan.md`](ale/plan.md). Prompted by the
Kenwood TK-90 assessment - its ALE needs a KPE-2 board that cannot be obtained, and doing it in
software removes the dependency on any particular radio, makes LQA our own measured SNR rather
than a vendor's opaque score, and is a smaller job than the modem already built (8-FSK, no
equaliser, no turbo decoder). **Deliberately unscheduled**: MS110D's Poor gate is 8 of 10 with a
successor program live (#7 above); §E2 has run on hardware since 2026-07-27, so that half of the
original reason has lapsed, and ALE is a legitimate candidate again once #7's G-legs close. One
exception worth taking early - decoding real off-air 2G ALE needs no
transmit licence, no partner and no hardware this project does not already own, and it would
verify the waveform constants empirically.

### #11 - FlexRadio 6500 integration *(in progress - nearly done)*
Pure-managed client: discovery + TCP session + VITA-49 DAX RX/TX → `IAudioInput`/`IAudioOutput`/
`IPttControl`, `--device flex:<radio>`. Offline Phases 0-2 merged (mock radio, byte-exact loop).
**Hardware proven on M0LTE's 6500**: discovery, session, headless GUI-client + slice, DAX RX
(0 loss) / TX, PTT (139 ms settle). **Live off-air RX proven** - decoded GB7RDG's NinoTNC BPSK300
through the Flex's DAX audio, no sound card in path.
- **Software-complete (PR #44 merged):** headless setup + the band-persistence tune fix
  (`EnsureTunedAsync` - a headless slice otherwise stays on the wrong band) + `--flex-daxch`
  DAX-channel coexistence, mock-validated (949/0). The tune fix is HW-proven on the 6500.
- **Remaining:** shipped-daemon final hardware confirmation (a FreeDV-datac / ARDOP frame into the
  dummy load), and a **Flex variant of the HF-loop** (folds into #4/#6). OBW self-capture is NOT
  viable on the 6500's public API (panadapter TX trace is leakage) - bench/second-RX stays the
  OBW path.
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

### #13 - A second Flex slice, receive only, on another band *(parked, Tom's request 2026-09-04)*
Tom: "I'd like the ability for pdn-soundmodem's flex support to be able to create a second slice, so that for example it could listen to 20m packet as well as being a transceiver on 40m packet." Not for now. The shape when it comes: the daemon opens a second headless slice on its own DAX channel (the 6500 has four of each), receive only, and runs a second `Station` over it with its own modems, frame log and waterfall page, which the `Station` extraction of the monitor work (PR #387, [monitor-plan.md](monitor-plan.md)) makes a wiring job rather than a rewrite. The 40 m slice keeps PTT and the KISS host; the second slice never transmits. Config shape to decide then: a second `device: flex:<radio>:<slice>` entry with its own `modems`, or a `listen` block beside the main one. Ties in with [uplink-plan.md](uplink-plan.md): a listening slice is a station the public monitor could show. Known constraint from the IQ work above: a second slice cannot self-capture during TX (RX blanks), so the two bands are independent receivers only while the first is not keyed.

### #14 - Audio record button with lookback, from a ring buffer *(parked, Tom's request 2026-09-04)*
Tom: "an audio record button, working from a ring buffer so when I press record, it immediately snapshots the ring buffer's contents, starts recording, and prompts me how many seconds back it wants to keep. Then, when I press stop, the whole of the chosen lookback + all of the audio to the point of the stop button being pressed is handed to the user." Not for now. Decisions taken with the request (Tom, 2026-09-04): the ring must not add a persistent stream to any UberSDR receiver, so no viewers connected means no session and no ring being gathered; it fills only while the station is live for a viewer, and the lookback on offer is however much has accumulated since the session opened, up to the ring length. It is public: the button is on the public page. Single take: one recording open per user at a time (each viewer may have their own take running on the same station; a viewer may not have two). Our own transmitted audio is included when the station is a pdn-soundmodem transceiver (the uplink stations of [uplink-plan.md](uplink-plan.md)), filling the gaps the receiver blanks during TX, so the take is what the station was working, not just what it heard. The shape when it comes: the ring lives in the daemon, not the page, so the lookback covers time before this viewer pressed Listen; it holds the same receive feed the Listen button gets (mono s16 at the station's sample rate, so five minutes at 12 kHz is about 7 MB and at 48 kHz about 29 MB), one ring per `Station`, allocated when the session opens and dropped when it closes, length from config (`record.ringSeconds`, default to decide). Pressing Record snapshots the whole ring at once and starts appending live audio, and only then does the page ask how many seconds back to keep, so nothing is lost while the prompt is open; the answer is capped at what the ring holds and trims the snapshot's front. Stop closes the take and the page fetches it as a WAV (`/api/recording/<id>.wav` or similar, header written from the sample rate, served once and then dropped), named with the station, dial and UTC start time. A cap on take length is needed on a public page (a take that nobody stops must end itself), value to decide then; since takes are per viewer, the cap also bounds what a page full of viewers can hold open on the daemon.
### #15 - Full duplex using UberSDR as remote receiver(s) *(parked, Tom's request 2026-09-04)*
Tom: "Full duplex using UberSDR as remote receiver(s)." Not for now. The idea: the station transmits on its own radio and receives through one or more public UberSDR receivers at the same time, over the `ubersdr:` input that already exists (on demand or always on). A remote receiver is not blanked by the local transmitter, so the station keeps hearing the channel while it is keyed: it can hear its own signal as others hear it, hear a collision as it happens rather than afterwards, and with several receivers pick the best copy of the far station from several sites at once. The shape when it comes: `device` stays the transmit radio (Flex or sound card, with PTT), and a `receivers` list of `ubersdr:<host>` entries each becomes a receive-only `Station` (PR #387) sharing the node's modems and frame log, with one vote per frame across the local receiver and the remote ones (the diversity bank's ranking on what each reading proved, docs/plan.md 2026-08-05, already does this within one receiver). Known constraints to measure first: the round trip of a remote receiver's IQ path (network plus the receiver's buffering) against AX.25 turnaround timing, since the node's own reply timing and the far end's T1 both assume it heard promptly; the receivers' per-address daily allowance (docs/monitor-plan.md 4.4), which a node keyed all day would use up on a busy receiver; and which receiver's dial the modems are planned against when they disagree by a few Hz. Relation to the rest: [uplink-plan.md](uplink-plan.md) decision 7 says a station whose receiver is an UberSDR may not publish to the public site, and this feature is the case that decision was protecting, a station that transmits on its own radio and listens elsewhere; #13 is the same idea with a second local slice instead of a remote receiver.

### #16 - Flex ATU on and off from the daemon *(parked, Tom's request 2026-09-05)*
Tom: "ability to turn flex ATU on/off". Not for now. The shape when it comes: an operator control, never a public one, that puts the 6500's internal tuner into bypass or back into use, and on request starts a tune, over the command session the Flex client already holds ([flex-integration.md](flex-integration.md) 2.2). The SmartSDR commands are `atu bypass`, `atu start` and `atu clear`, with `atu set memories_enabled=0|1` for the tuner memories, and the outcome arrives on the `atu` status object (`status=TUNE_BYPASS|TUNE_IN_PROGRESS|TUNE_SUCCESSFUL|TUNE_FAIL|...`, `atu_enabled`, `memories_enabled`, `using_mem`); confirm the exact strings against the flexclient source as the rest of the Flex work did. Three places it could be driven from, to pick then: a `flex.atu` config setting applied at bring-up (bypass, in use, or leave the radio as found, the last being today's behaviour and the safe default), the config API for a change while running, and a control beside the radio status on the operator page, which the public page never shows. A tune keys the transmitter with a carrier on the working frequency, so starting one is always the operator's act, never something the daemon does by itself at bring-up or after a retune; bypass and un-bypass do not transmit. The `radio` status line should show whether the tuner is in or out, and the journal should say what was asked and what the radio answered, in ASCII. The tuner is per radio, not per slice, so with #13 a tune on one slice's band affects both. Needs the radio on the bench and a load or the antenna to try.

### #17 - Mixer, AGC and mic boost controls for CM108 sound cards *(parked, Tom's request 2026-09-05)*
Tom: "mixer / AGC / mic boost controls for CM108". Not for now. Today the daemon opens the CM108's PCM through ALSA (`AlsaPcm.cs`) and drives its GPIO for PTT (`ptt.type: cm108`, CONFIG.md), but the card's mixer is left however `alsamixer` last set it: capture volume, the Auto Gain Control switch and the +20 dB Mic Boost, which between them decide whether the receive audio is clean, clipped or buried, and which a reboot or a re-plug can silently reset. The shape when it comes: an `alsa.mixer` block in the config (capture gain in dB or percent, `agc` on or off, `micBoost` on or off, playback level for the transmit side) applied at start-up through the ALSA mixer API (`snd_mixer_*`, the same alsa-lib the PCM already uses; no shell-out to `amixer`), journalled as set and as read back so the start-up log records the card's actual state, and an operator-page control group beside the level meter so the operator can trim it while watching the waterfall, going through the config API so a change persists. AGC should default off for a data modem, since it fights the modem's own level tracking and turns the noise floor into a moving target; Mic Boost off unless the radio's output is genuinely low. Control names differ by card revision ("Mic", "Mic Capture", "Auto Gain Control"), so find them by name with a fallback and say in the journal which were found. Operator page only; the public page never shows it. Needs a CM108 on the bench.

### #18 - TX test button: a two-tone test transmission *(parked, Tom's request 2026-09-05)*
Tom: "TX test button - transmit two-tone test". Not for now. The classic SSB linearity check: two equal tones (700 and 1900 Hz is the usual pair; the ARDOP work already has a `TwoToneTest()` generator in `ArdopModulator`, `ArdopChannelBridge.cs:21`) keyed through the normal transmit path so the operator can read ALC, power and IMD on the radio or a monitor receiver, and see the result on the public monitor if the station publishes there. The shape when it comes: an operator-page button, never on the public page, that keys PTT, sends the two tones at the modem's configured transmit level for a bounded time (a few seconds, configurable, hard cap so a stuck button cannot hold the PA), and unkeys, with the frame log and journal recording it as a test transmission; a companion `--two-tone <seconds>` CLI switch for a bench without a browser; single-tone as a second option for a carrier-level check. It goes through the same PTT and level path as a real frame (ALSA plus serial or CM108 PTT, or the Flex DAX path) so what is measured is what a frame gets. It is a licensed transmission, so it is always the operator's act, it must respect the same channel-busy and inhibit rules a frame does (or say plainly that it does not), and it must be refused when no PTT is configured. Ties in with #16 (a tune and a two-tone test are the two operator-initiated carriers the operator page will have) and with #17 (setting the CM108 level while watching the two-tone result).

Also under this item, for FM radios, a Bessel null deviation check (Tom, 2026-09-05, "wonder about adding bessel null testing for FM actually, in that same roadmap item ... a 999hz tone will drive a bessel null at 2.4khz deviation. a 500hz tone will drive a bessel null at 1.2khz deviation. a 2079hz tone will drive a bessel null at 5khz deviation. a 1248hz tone will drive a bessel null at 3khz deviation. This would be useful. Don't do it now."). The rule behind those four pairs: the carrier of an FM signal vanishes when the modulation index is 2.405 (the first zero of the Bessel function J0), so with a single tone at f the carrier nulls at a deviation of 2.405 x f; 999 Hz gives 2.40 kHz, 500 Hz gives 1.20 kHz, 2079 Hz gives 5.00 kHz, 1248 Hz gives 3.00 kHz. The method: key the radio with the chosen tone, raise the transmit audio level until the carrier disappears on a spectrum display (the Flex panadapter, a public UberSDR receiver, or the station's own monitor page on a second receiver), and the transmit level at that point is calibrated for that deviation; back it off in proportion for the deviation the mode wants (an AFSK1200 station on a 12.5 kHz channel wants about 2.5 to 3 kHz). The shape when it comes: the single-tone option of the TX test button gains a deviation preset menu with those four pairs and the target deviation shown, the same bounded keying and the same rules as the two-tone test; the daemon cannot see the carrier null itself unless it also has a receiver on the channel (#15 or a second Flex slice, #13), in which case it could read the null off its own waterfall and say the level. Needs the FM radio kind of #413 so the page knows the radio is FM.

---

## Cross-cutting follow-ups (issues from live-RF validation, 2026-07-17)

- **#42 - NinoTNC BPSK is DEBPSK; our Coherent detector can't decode it.** Real off-air GB7RDG
  decodes with `Differential` only; `Coherent` (the default from #5) fails even strong + centred,
  because the NinoTNC uses coherent Costas demod **with** differential encoding to beat the 180°
  ambiguity, and our coherent path omits the differential-decode step. **Fix:** add a
  differential-decode step after coherent carrier recovery (match the NinoTNC's modified Costas
  loop), or default HF BPSK to Differential; re-examine the #5 bench result. Highest-value modem
  fix in the queue - it's a real NinoTNC-interop gap. Fixture committed.
- **#40** - the general coherent-vs-differential off-air finding (now explained by #42).
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

---

## Needs Tom + a radio (the specific actions)

These are the concrete steps that can't be done in software alone. None is blocking; each is
self-contained and can be picked up standalone. Operate as **M0LTE**.

1. **Flex daemon live confirmation** (#11) - *~2 min, radio already on the bench.* Run the
   shipped daemon against the 6500 and push one real frame into the ANT1 dummy load:
   `pdn-soundmodem --device flex:10.45.0.76 --flex-freq <MHz> --flex-ant ANT1 --modem 0:freedv-datac3 --kiss 8105`
   (or `--ardop 8515` for ARDOP). Success = `interlock=TRANSMITTING`, RF on the dummy load, no
   setup errors. Closes the last Flex item.
2. **FreeDV datac HF-loop validation** (#4) - *needs an HF rig, or the Flex path.* Follow
   [`docs/freedv-hf-loop.md`](freedv-hf-loop.md) (rig recipe, the 8 radio-only unknowns, drive
   commands, pass criteria). Record results back into that doc + `plan.md` §17. Closes #4.
3. **ARDOP on-air acceptance** (#6) - *needs GB7RDG's HF port on 40m.* Peer-to-peer ARDOP with
   the stations already on the UK 40m packet channel; Winlink/Pat gateway session optional. Write
   the on-air bench doc first (à la the FreeDV one). Closes #6.
4. **Own FM OFDM - an FM radio loop** (#8) - *later, when the #8 build starts.* Required to
   validate PAPR / pre-emphasis / deviation, which simulation can't fully capture.
5. **GB7RDG traffic on request** - *optional, opportunistic.* Once the #42 coherent+differential
   fix lands, capture more live NinoTNC BPSK300 off-air through the Flex to confirm the fix on
   real signals (this is how #39/#40/#42 were found). A long carrier tone + a frame is the ideal
   calibration transmission.

**Hardware available:** a Flex 6500 (10.45.0.76, on the bench with an ANT1 dummy load; GB7RDG's
transceiver couples into it), an HF rig / GB7RDG's HF port, and an FM radio loop.

## Parked / non-goals

- **M17** - parked (Tom): kept in the survey, not on the build path.
- **VARA HF/FM, PACTOR II-IV, P25, NXDN, System Fusion/C4FM, FLEX** - proprietary, cannot implement.
- **FreeDV voice coexistence** - a non-goal (data and voice never share a channel).
