# Receive-performance roadmap

The durable plan for squeezing the remaining performance out of the IL2P receive path,
written 2026-08-06 after the PR #236 campaign rebuilt the differential BPSK chain. This is a
living document in the plan.md style: workstreams get dated status notes as they move, and a
workstream that lands gets its ledger entry in [mode-validation.md](mode-validation.md) - the
ledger stays the record of what was proven, this stays the record of what is worth doing next
and why.

## Where we are (the PR #236 baseline)

Measured on the sim ladder (`sm-ota sim`, default 9-branch bank, 60-byte frames, 300
bursts/point, 3 kHz SNR convention) and the real-audio corpora:

| Instrument | Reading |
|---|---|
| AWGN ladder | 49 % @ −5 dB, 82 % @ −4, 94 % @ −3 (was 4/46/81 before #236) |
| Carrier-offset sweep 0-33 Hz @ −3 dB | 92-97 % flat |
| CCIR Good (0.5 ms / 0.1 Hz) | 33 % @ −4 dB, outage-bound above |
| CCIR Poor (2 ms / 1 Hz) | ~29 % ceiling - equaliser-bound |
| GB7RDG 24 h miss corpus, demodulated byte-exact | 32 of 37 (`NINOTNC_ASPIRATION=1`) |
| GB7RDG 24 h miss corpus, **host-delivered** | 22 of 37 (3 CRC-verified + 19 trailer-corroborated) |
| bpsk1200 AWGN | 86 % @ +2 dB (was 20 %) |

The demodulated/delivered split matters and was initially conflated: the aspiration test counts
`FrameDecoded`, which includes withheld RS-only readings. Before trailer corroboration landed
(2026-08-06, same day as this doc), only 3 of the 32 byte-exact frames actually verified their
CRC and reached a host. The other 29 failed on 2-10 grazed trailer bits, clustered in the last
positions - the end-of-burst pulse-truncation cliff landing on the wire format's only
unprotected bytes. Corroboration (`Il2pReceiver`: recompute the trailer the payload implies,
deliver within 4 wire bits, ~1.1e-5 false-accept - CRC-order evidence) recovered 19 of them.

The demodulator now sits within ~0.5-1 dB of the matched-filter bound for this waveform on
AWGN. The remaining structural losses are **above** the demodulator: everything from the bit
decision upward is hard-decision, single-look, and causal. The wire format is NinoTNC IL2P+CRC
and is not negotiable; every workstream below is receive-side only unless marked otherwise.

### Assessment, 2026-08-15 (Tom asked: how well is it behaving, and how much room is left?)

**The implementation is in good shape; what is left is structural rather than defective.** The
evidence for the first half is the FreeDV real-path run of 2026-08-15, which is the cleanest
receiver behaviour this project has measured in the wild: 23 bursts, decoded ones at +17.2 dB
mean in-band excess against the missed ones' +5.7 dB, **11.5 dB of separation with not one
inversion**, and `fec 0` on every decode across the day - not a single Reed-Solomon correction
spent. A misbehaving demodulator shows up precisely as scatter through that boundary, and there
was none. Add no false positives and +2 Hz frequency tracking held all day, and there is nothing
here indicting the DSP.

The room that remains, ranked honestly and by family:

- **The `freedv-datac*` OFDM engine: nearly nothing left.** It trails codec2's own modem by ~4
  points on the *same* `ch` channel, on the heavily-coded long modes only, and its AWGN knees
  match published figures. Characterised, small, not worth effort.
- **The IL2P/BPSK chain (the modes that carry live traffic): the demodulator is close to done,
  and everything above it is not.** Hard-decision, single-look, causal - the textbook ~2 dB
  give-away that workstream 1 exists to collect, worth much more on the fading channels 40 m
  actually serves than on AWGN.
- **CCIR Poor is largely not liftable here, and that is now measured rather than assumed.**
  Workstream 5 built the MLSE and *retired* its own 50-60 % claim: most of Poor's loss is flat
  outage across 60-150 symbol times, which no receiver fixes at a wire format with no
  interleaving. That is a ceiling, not a backlog item.
- **So the biggest real lever is not receive-side at all.** It is workstream 8, and the 2026-08-15
  run is the first hard evidence for it (see its status note).
- **And the cheapest item on the whole list is workstream 9**, which improves no decode at all
  and makes every other workstream measurable in the wild.

## The 40 m capture campaign (live)

Tom's concern, 2026-08-06: the 37-frame miss corpus is small, and improvements are starting to
overfit its tail. The fix is a bigger, fresher corpus, and it is being collected now:

- **`pdn-capture-40m.service`** on this box (systemd, enabled at boot, `Restart=always`, runs
  as `tf`) drives the FlexRadio 6500 at `10.45.0.76` - **receive only**: headless slice A /
  DAX channel 2, ANT1, dial 7.049450 USB, station name `pdn-rx-capture`, KISS bound to
  localhost with no hosts attached, `transmitFilterHighHz: 0` so nothing TX-side on the radio
  is written. Started 2026-08-06 13:54 UTC.
- Modems live on the standard 40 m slots: `afsk300-il2pc` at 7.050300, `bpsk300` at 7.051600,
  with the ID-beacon ghost listening at +200 Hz.
- Three records accumulate under `/home/tf/capture-40m/`:
  - `raw/` - **continuous 12 kHz chunked WAVs** via the daemon's new `rawCapture` feature
    (15-minute chunks, 120 GB budget ≈ 60 days, oldest pruned). This is the re-scorable
    ground truth: every future receiver change can be replayed against every hour of it.
  - `framelog/frames.sqlite` - every frame the live receiver decoded, with quality fields.
  - `survey/` - curated bursts the station could not read (2 GB budget).
- The container's disk was grown 100 → 200 GB (`pct resize` on `root@10.45.0.10`) to fund it.
- **Methodology when harvesting**: re-run the raw chunks through the current receiver and diff
  against the frame log to find in-stream losses; extract bursts that fail isolated decode
  into a `misses-v2` corpus with expected bytes from context (retries, digipeats) or operator
  logs where available. The GB7RDG benchmark had a NinoTNC as referee; this capture has none,
  so "what should have decoded" comes from burst-level evidence (a clean burst that RS-fails)
  rather than a second receiver - weaker per-frame ground truth, far more frames.

There is no automatic end date: 24 h was the ask, the budget carries two months, and the value
grows with band variety (a weekend contest, a geomagnetic upset, summer QRN). Check on it with
`systemctl status pdn-capture-40m` and `ls -lh /home/tf/capture-40m/raw | tail`.

**Status 2026-08-06 (night): the harvest instrument exists and has flown.** `sm-ota replay`
re-decodes the raw chunks through catalogue receivers (one continuous modem instance per mode
across chunk boundaries; per-mode passes in parallel), stamps frames from the chunk filename
UTC, writes a payload-hex CSV, and diffs against the frame log by exact payload in a +-45 s
nearest-first window. Validation over the opening 1.3 h: 144 replayed / 148 logged /
144 matched / 0 phantoms. **Know the referee's generation** (Tom's catch): the deployed
daemon is 0.25.1, built against M0LTE.Fec 0.2.0 / M0LTE.Il2p 0.1.2 (pinned from the deployed
`deps.json`) - the pre-erasure receiver, and its frame log lacks the trailer_near_bits /
monitor_only / erased_bytes columns - so log-vs-replay deltas mix receiver generations with
instrument effects (16-bit raw quantisation against the live float path, window edges) and
are a cross-version reading, not an instrument floor. Measured across the full evening, the
generations barely separate on real traffic: 4 exclusive frames each way (two of the live
station's were window-edge), none of the replay's exclusives erasure-driven. Erasure decoding
fired on 2 of 708 frames all evening and both matched frames the pre-erasure live station
also copied - zero exclusive real-traffic contribution tonight, consistent with the corpus
finding that the real frames do not die at the RS knee - while trailer corroboration fired
on 20. The first real-audio detector A/B (the whole opening evening,
7.2 h, ~708 bpsk300 frames) then ran on it: differential 708 decoded / 659 deliverable /
639 CRC-verified against mlse 707 / 658 / 644 - totals at parity, mlse +5 CRC-verified, and a
roughly symmetric ~1.5 % exchange (11 frames only differential caught, 10 only mlse caught,
the mlse-exclusive list skewing to the genuinely weak distant traffic: three GB7WEM-7 frames,
a GB7WEM IDENT, an EI0RSI-7 ID). Two of mlse's catches are the exact frames the live station
heard but the differential replay missed, so the exchange is real signal, not instrument
noise. Consequences: no default flip on one evening's evidence - the A/B repeats as the
corpus grows - and the symmetric exchange strengthens the demoted ensemble-decode-any idea
(workstream 2): running both detectors would have banked ~+1.5 % union gain for CPU alone.

**The opening evening's misses, autopsied (2026-08-07 early).** The survey banked 62
missed-verdict bursts in the bpsk300 slot (peak SNR p50 19 dB, to 36 dB) - and **none of
them decodes in isolation under either detector**, against a positive control (a
survey-length window cut around a frame-log-proven frame) that decodes cleanly through the
identical per-burst pipeline. That inverts the GB7RDG-era expectation, where half the
in-stream losses decoded isolated because collection-state masking ate them: the
DCD-falling reset has closed that class, and what the station misses now is genuinely
undecodable. What the misses are: **43 of 62 are shorter than 1 s** - physically too short
to hold a complete frame; fragments (collided tails, partial acquisitions), not losses -
and the ~19 full-length residue died to damage no current detector reads. What they are
NOT: static crashes - coincidence with >25 dB broadband impulses is 16 observed against
~32 expected by chance, so **a noise blanker would not rescue this failure class** (it may
still buy general SNR margin; that question stays with the workstream-6 instrument). The
station read ~94 % of its slot's activity on the opening evening; misses-v2 as a corpus
needs either richer pickings (contest weekends, deeper QRN) or expected-bytes context
(retry correlation) to say more than "hard".

**First full-day harvest (2026-08-07, covering 2026-08-06 13:53Z to 2026-08-07 14:19Z).**
The honest extent first: the replay window spans 100 chunks / 24.4 h of wall clock, but only
**19.6 h of it is audio**. The DAX feed died silently at 09:26:47Z (pinned by the last
non-zero sample, 447.8 s into `raw-20260807T091919Z.wav`); the daemon stayed up, logged no
error, and wrote zero-filled chunks until a manual restart at 16:17:53Z - 6 h 51 m of dead
air that looked alive from file timestamps. The feed watchdog (#247) now bounds that class
at ~30 s. Separately the chunk timeline carries 116 s of inter-chunk gaps: 57 s of startup
churn at 13:54Z, 7 s at the 14:19Z binary deploy, two silent process deaths at 23:16:58Z
and 23:18:43Z (26 s + 22 s; `Restart=always` masked them, the kill mechanism lives in the
system journal user `tf` cannot read, config unchanged across both), and two 2 s chunk-roll
jitters. Different failure families: process death restarts and gaps the record; feed death
gaps nothing and records silence. Instruments, for reproduction: `sm-ota replay --raw
/home/tf/capture-40m/raw --mode bpsk300@2150,afsk300-il2pc@850 --detector differential
--framelog /home/tf/capture-40m/framelog/frames.sqlite --from 20260806T135000Z --to
20260807T141900Z --workers 10 --csv ...` (second run `--mode bpsk300@2150 --detector mlse`),
the CSVs diffed with the scratch `ab-compare.py` (payload-hex match within +-45 s), impulses
via `docs/bench/impulse-stats-2026-08-06.py` over the 81 audio-bearing chunks.

- **Replay vs the live log.** Differential: bpsk300 **1122 decoded / 1062 deliverable /
  1036 CRC-verified**, afsk300-il2pc **174 / 158 / 149**. Frame-log diff over the window:
  1304 logged / 1296 replayed / **1292 matched / 12 log-only / 4 replay-only** - the same
  near-parity cross-version reading as the opening evening, now at 24 h scale. Traffic
  shape: evening peak 16:00-21:00Z (bpsk 111-189 frames/h), pre-dawn floor 01:00-04:00Z
  (4, 15, 0, 0), dawn recovery from 05:00Z (07:00Z hits 69). 14 distinct bpsk source
  callsign-SSIDs across 9 base calls (EI0RSI-1 317, GB7WEM-7 314, GB7BPQ 145, GB7OXF-2 69,
  GB7BEX-15 67, PD4R-12 40, EI0RSI-7 38, the rest under 21; GB7NOT and G8BPQ heard rs-only),
  4 on afsk (GB7BEX-15 84, GB7BPQ 45, GB7BWR-2 19, GB7NOT 10). New face: PD4R-12, an
  empty-destination 118-byte beacon every ~13 min that the live daemon logs as source NULL.
- **Detector A/B at 24 h: the opening evening's verdict holds.** mlse: **1126 / 1060 /
  1043**. Totals at parity (4 frames apart on 1122), mlse again ahead only on CRC-verified
  (+7 now, +5 then), and the exchange stays roughly symmetric: 12 differential-only against
  16 mlse-only, union **+12 decoded (+1.07 %) / +13 deliverable (+1.22 %)** over the best
  single detector - the same ~+1 % the opening evening promised for ensemble-decode-any.
  mlse's exclusives again skew to the weak distant traffic (three GB7WEM-7, a GB7WEM IDENT,
  an EI0RSI-7 ID, and all three PD4R-12 frames the differential replay dropped). Sharpest
  new number: of the 1127 bpsk frames the live station logged in-window, differential's
  replay misses 9 and mlse's misses 13, but **only 2 frames are missed by both** (18:46:52Z
  GB7BWR len 56, 02:47:51Z len 15) - the live-vs-replay gap is almost entirely detector
  exchange, not instrument loss. Still no default flip; the union number keeps funding
  workstream 2.
- **Survey misses: 0-for-71 again.** The harvest window banked 600 survey bursts - a capped
  sample, not an inventory (120 s per-250 Hz-bucket cooldown, 30/h cap): 134 Missed
  (51 afsk-slot, 72 bpsk-slot, 9 at the afsk watcher's 1002-1017 Hz edge, 2 at 683 Hz),
  463 Unclaimed, 3 Unattributed. The 71 missed bursts not in the opening evening's autopsy
  (56 afsk-claimed, 15 bpsk) were staged per-burst and re-decoded isolated, bpsk under both
  detectors: **0 of 71 decode**. Lifetime isolated-decode score for missed bursts is now
  0/133; the opening evening's conclusion (fragments and hard damage, not collection-state
  masking) survives its first scale-up. Cross-check: the burst-verdict instrument (#250,
  `/home/tf/capture-40m/replays/bursts-bpsk300-20260806-07.csv`, window extending past the
  feed restart to 18:19Z) sees 873 DCD bursts, 743 with a decode, 130 zero-decode of which
  57 are sub-second - the same shape from the receiver's own DCD.
- **Impulse noise, full day.** 371,967 events >6x background in 2500-2950 Hz over 19.6 h
  (315.7/min), 35 % broadband-coincident (opening evening: 38 %). Broadband rate by half
  hour: evening peak 211-218/min at 18:00-18:30Z, overnight sustained 62-135/min (no
  collapse; floor 58.7/min at 21:30Z), and a new observation the opening evening could not
  see: a **sunrise spike, 187-230/min at 06:30-07:00Z with the day's strongest medians
  (21-22 dB, 32-41 ms)**. Distribution stable against the opening evening: amplitude p50
  18.0 / p90 23.7 / p99 34.0 / max 73.0 dB over background; duration p50 17.8 ms (5.3
  symbols at 300 Bd) / p90 92 ms / p99 1.5 s. The 09:30Z half-hour row is a boundary
  artefact (one event at the audio-to-zeros transition) and is excluded from the claims.
- **The ARDOP slot, characterized without decoding it.** Between the IL2P slots the survey
  logged 206 Unclaimed bursts in 1000-2000 Hz across the 19.6 h: 403 s of air time, 0.57 %
  duty, arriving ~10/h around the clock with no diurnal collapse (range 5-19 per hour).
  Burst widths cluster at ~200 Hz (median 203, p90 264); durations split into a ping/ACK
  population (p50 0.53 s) and a data tail (p75 1.79 s, p90 6.15 s, max 16.9 s).
  Duration-weighted centre concentrates 63 % of air time in 1500-1800 Hz, session centre
  ~1650 Hz audio = RF ~7.051100 - consistent with ARDOP 200/500 sessions parked mid-slot.
  A time-averaged PSD of active chunks is noise-dominated and flat across the passband, so
  the survey bursts stay the instrument. This corpus is banked as the future cross-check
  for the repo's own ARDOP implementation: real on-air ARDOP bursts with timestamps,
  centres and SNRs, none of which the frame-layer rig can claim.

**Full-archive harvest and the misses-v2 corpus (2026-08-15).** The whole raw archive -
675 chunks, 2026-08-06 13:53Z to 2026-08-13 12:34Z, 166.9 h (the capture service stood down
on 2026-08-13 when the Flex went to the FreeDV campaign) - replayed through current main:
bpsk300 differential **4318 decoded / 3872 deliverable**, burst verdicts **4442 DCD bursts,
3456 decoded, 986 undecoded (310 synced-then-RS-failed)**. Frame-log diff at 7-day scale
stays near parity, and is now **generation-controlled** (the deployed daemon changed at
frame id 1328, 2026-08-07 16:45Z, from pre-erasure 0.25.1 to the erasure receiver): gen1
1146 logged / 9 log-only, gen2 3200 logged / 32 log-only, 13 replay-only - the same
detector-exchange-not-instrument-loss shape as the 24 h reading. The corpus itself:
**misses-v2 is built** at `/home/tf/capture-40m/misses-v2/` - the 573 full-length (>= 1 s
DCD) undecoded bursts cut to per-burst WAVs with 2 s margins, 413 sub-second fragments
excluded per the opening-evening autopsy, manifest.json carrying burst metadata and, for
**77 cuts, expected bytes attached by unique retry-sibling** (a decoded frame within ten
minutes whose wire duration matches the burst's DCD hold - context evidence, weaker than
the GB7RDG NinoTNC referee, stated per case; 252 more had multiple candidate payloads and
carry none). Instruments committed under `docs/bench/`: the corpus builder
(generation-tagged log diff included; its matching reproduces `sm-ota replay`'s 4305
matched exactly) and the isolated re-score harness (one fresh deployed-configuration bank
per cut - the aspiration-test pipeline pointed at the new corpus). Baseline isolated-decode
score: **14 of 573** (1 of the 77 expected-attached byte-exact) - the "what the station
misses is genuinely undecodable" conclusion holding at 7-day scale, and the yardstick the
chase landing then moved to 32 (workstream 1's 2026-08-15 status).

**Corpus caveat, found by ear (2026-08-16, mode-validation.md entry of the same date):** a
share of the full-length "misses" are fade-split leading fragments of transmissions the
station DECODED seconds later - the burst detector's 0.2 s grace splits a QSB-hit
transmission, and the decode lands at the transmission's end, outside the survey's old
one-second attribution window. On the rollout morning's sample, 9 of 11 in-slot bursts were
this class, station-fingerprinted by carrier offset. The survey now holds Missed captures
for a `decodeClaimSeconds` window so a decode claims its fragments (PR #311); misses-v2
entries from before that fix over-count true misses accordingly, and the fragment class is
one more reason the isolated re-decode score reads low - a fragment is not a frame.

## Workstreams, ranked

Ranked by expected real-world return per unit of effort, with the reasoning pinned so a future
session can re-rank honestly as facts change.

### 0. Watterson masks and the accept discipline (cross-cutting; Tom, 2026-08-06)

Extend the MS110D programme's two instruments to the audio modems: **per-mode Watterson
performance masks** frozen as tests, and the **accept discipline** that goes with them. Most
of the machinery exists - `WattersonChannel` is itself validated, `SimChannel` pins the
Good/Poor geometries so packet and MS110D masks stay one rig, and `SimModem` makes the
generate-channel-score loop mode-generic. What is missing is thin and is this workstream:

- **Two tiers.** A small always-blocking smoke per mode - one anchor rung, 30-50 bursts,
  threshold far enough under the measured value that binomial wobble cannot flake it, sized
  to catch a regression of a dB or more. Plus an env-gated full ladder (the A/B instrument)
  over the whole SNR x channel x CFO grid. Deterministic seeds throughout: a mask failure
  reproduces exactly, or it is not a mask failure.
- **Masks from measured reality, never aspiration.** bpsk300/bpsk1200 masks come from the
  2026-08-06 campaign numbers; the QPSK family's come from their current, known-poor levels,
  which documents the truth and catches further slippage - when those modes get their own
  receive campaign the masks move up with the ledger entry that justifies it. Aspirations
  stay in the aspiration suites, where the house discipline already keeps them non-blocking.
- **The discipline.** A PR touching a modem's receive path runs that mode's full ladder A/B
  and quotes it; a mask moves only with a mode-validation.md entry. This is what the #236 and
  erasure campaigns did by hand; the workstream makes it the floor, not the habit.
- **The honest limit, stated up front.** Masks pin the receiver against the model. The
  Watterson sim carries no static crashes, no SSB filter tilt, no AGC and our own TX shaping
  rather than a NinoTNC's - so a green mask means "no regression against the model", and the
  capture campaign stays the truth about the band. Two instruments, two jobs.

Why it ranks where it does: three near-misses in two days were caught only by ad-hoc sweeps
or one-off pins (the mid-branch CFO hole, the coherent-margin inversion, the erasure
interpolation hazard), the ~1.4 dB banked this week exists only as ledger prose until a mask
holds it, and workstreams 5-7 below each need exactly this instrument to develop against.

**Status 2026-08-06 (evening): landed** - `WattersonMaskTests`, both tiers green. Coverage is
every non-FM mode the mode-generic rig can drive: the NinoTNC SSB lineage AND the FreeDV
datac OFDM family (Tom's clarification: all non-FM PDN modems, not just NinoTNC modes).
Deliberately absent, on the record: **ms110d-\*** keeps its own richer mask suite;
the FM modes wait for an FM-appropriate channel model (the impulse-noise
workstream is the natural place both arrive together).

**Status 2026-08-08: the ARDOP gap is closed** - the ARDOP campaign's phase A3
(docs/ardop/plan.md) landed `ArdopFrameProbe` (the `DatacPacketProbe` pattern applied to the
session TNC's engine: library modulator -> shared Watterson rig -> fresh demodulator, scored
payload byte-exact), driven as `sm-ota sim --mode ardop:<FrameName>` through the same
`SimBench.RunPoint` the mask rows use. The wild-relevant frame ladder (4FSK.200.50S,
4PSK.200.100S, 4FSK.500.100, 4PSK.500.100, 8PSK.500.100, 16QAM.500.100) is measured and
mask-pinned in `WattersonMaskTests` alongside every other mode.

### 1. Soft-decision and erasure Reed-Solomon decoding (the big lever)

Everything above the demodulator is hard-decision; that is the textbook ~2 dB give-away, and
it is the largest remaining item at fixed wire format. Receive-side legal, three steps of
increasing depth:

- **Erasure marking.** RS corrects twice as many erasures as errors. The DF-DD detector's
  projection magnitude is a free per-symbol confidence; a fade is visible in the envelope.
  Byte-level erasure flags roughly double the correctable span through a fade - worth little
  on AWGN, a lot on the fading channels 40 m actually serves.
- **CRC-arbitrated chase decoding.** Flip the m least-confident bits in combinations, re-run
  RS + CRC per candidate. The CRC arbitrates, so it cannot deliver garbage. Of the order of
  1 dB at the knee, where the real frames die.
- **Full soft RS (GMD/KV)** if the first two pay: diminishing returns beyond chase for these
  block sizes.

**Dependency**: the RS decoder lives in **M0LTE.Il2p** (NuGet, separate repo). The deframer
needs a soft-bit or erasure-hint input surface. This is the one workstream that cannot land
from this repo alone - it needs the Il2p repo opened alongside. Estimated 1-2 dB AWGN
equivalent, more under fading.

**Status 2026-08-06 (evening): the erasure leg is built and measured.** M0LTE.Fec 0.3.0
gained errors-and-erasures decoding with a caller-set cap on located errors (an attempt that
spends the whole parity budget is pure interpolation and always "succeeds" - found the hard
way, pinned by test); M0LTE.Il2p 0.2.0 gained `PushBit(bit, confidence)` and a failed-block
retry ladder of (erasures, cap) rungs that each keep two parity symbols in reserve; the BPSK
demodulator emits per-symbol confidence from the DF-DD decision magnitude. Measured: **AWGN
−5 dB 49 % → 55 %, −4 dB 82 % → 88 %, −3 dB 94 % → 96 %** (~0.3-0.4 dB); Good/Poor unchanged
within noise; the miss corpus unmoved at 22/37 delivered - its residue is not
payload-RS-limited (5 cases never demodulate, 10 fail only on obliterated trailers). The
header deliberately gets no erasure rescue (its 2-parity code cannot afford speculative
erasures without hallucinating collections). What remains of this workstream, in value order:
**CRC-arbitrated chase** (bit-flip retries on the least-confident bits, which handles the
scattered-error pattern erasures cannot and is the header's only rescue), and the DCD-falling
reset question, now **answered in the negative** (2026-08-06 evening): disabling the reset
entirely changed nothing on Good (identical to the burst) and 0-4 points on Moderate, inside
the confidence intervals - fading losses are acquisition or RS-budget failures, not
collection aborts, so the reset stays exactly as it is and the suspicion is retired.

**Status 2026-08-15: the chase leg is landed, and the 2026-08-06 archived null it reverses
is explained - the null was the per-bit metric, not the idea.** Chase had been built once
already (M0LTE.Il2p branch `chase-decoding`, archived unmerged 2026-08-06: "re-open only
with a demonstrably better per-bit soft metric") and its diagnosis was right: with the DF-DD
magnitude as the per-bit confidence, the population where chase beats erasures is empty. The
missing mechanism: a differential bit depends on TWO symbols, so one hit symbol flips two
adjacent bits, and only the first ranked weak - measured 0 chase hits in 50 header attempts
at the -4 dB knee, 40/70 after `BpskDemodulator` emits the pair-min, with the accepted flip
sets coming back as adjacent pairs. Each half alone is a null; together (M0LTE.Il2p 0.3.0 +
the pair-min, PR #307): AWGN -5/-4 dB 55->67 %/88->93 %, the CFO span at -3 dB 92-97->98-99 %,
15-byte frames -5 dB 62->76 %, fading +1..+5 points, Poor at its ceiling; misses-v2 isolated
decode 14->32 of 573 with zero lost and 11 of the 18 new reads CRC-verified. Numbers and
budgets in the mode-validation.md entry of the same date; the overnight continuous replay
of the whole 166.9 h archive then read +78 frames (41 CRC-verified, zero content lost, 102
decodes leaning on chase) and out-gained the old receiver's MLSE detector swap on the
deployed default - the 2026-08-16 ledger addendum. What remains of this workstream
now: **full soft RS (GMD/KV), still judged diminishing beyond chase for these block sizes**,
and one cheap idea the pair signature suggests - a chase enumerator that flips adjacent
PAIRS as single candidates would cover two symbol errors where today's 3-flip budget covers
one and a half, at the same candidate count. Sized only, not built: the header (the knee's
population) already chases all 4095 subsets of its 12 weakest, so the pair enumerator only
helps payload blocks, whose failures the erasure ladder already dominates.

### 2. Trailer corroboration - LANDED 2026-08-06

Measurement redirected this slot on the day the roadmap was written: probing why 29 corpus
frames demodulated byte-exact without delivering exposed the trailer-cliff mechanism above, and
the fix (corroboration in `Il2pReceiver`) took the corpus from 3 to 22 host-delivered - worth
more than any planned demodulator change, for two hundred lines. Left behind it, two follow-ups:

- **Tail handling in the demodulator.** The cliff itself is partly recoverable: the matched
  filter loses the final symbols because both ends truncate the last pulse. Our own modulator
  does too - extending TX rendering by the pulse span (wire-compatible, it is just carrier
  tail) would make our transmissions decode better at every receiver, and an RX-side
  asymmetric-tail treatment could shave the remaining grazed bits. Also worth probing: whether
  bpsk300's DF-DD reference decays into the trailer when the tail fades.
- **Ensemble decode-any**, demoted from this slot: the measured union gain was one corpus case
  (coherent copied `GB7OXF-180252` where DF-DD did not) plus general detector diversity under
  fading. Still cheap and worth having, but corroboration ate most of its lunch. Re-evaluate
  against the capture campaign's `misses-v2`.

  **Status 2026-08-07: built as an opt-in knob, and the fading gain is real.** The capture
  campaign's opening-evening A/B revived this (a roughly symmetric ~1.5 % exchange of
  exclusive frames between the differential and MLSE detectors, +1.1 % deliverable union on
  real 40 m traffic), so the knob now exists: `ModemOptions.SecondDetector` doubles a bpsk
  bank with one branch per detector per diversity position and the existing content dedupe
  delivers the union (`sm-ota sim --detector2`, bpsk banks only - other modes throw rather
  than silently hand a measurement the single bank). Sim ladder A/B, N=100 seed 1, single
  differential bank -> differential+mlse ensemble: AWGN -5/-4/-3 dB 56/88/95 -> 58/88/95
  (the detectors agree on AWGN); Moderate 0/+2/+4 dB 50/55/63 -> 50/58/67; **Poor +6/+9 dB
  21/24 -> 28/30** - the ensemble banks workstream 5's Poor-rung catches without giving up
  a single differential frame, never measuring worse anywhere, at twice the bank CPU
  (~19 % of a core at 12 kHz). Not the default: the CPU doubling is an operator's call,
  and the masks continue to pin the single-detector default.

### 3. Retransmission soft combining (the sleeper)

AX.25 retries are byte-identical frames seconds apart - the #236 guard bug proved the stream
is full of them. Hold soft symbols of bursts that failed to decode; when a later burst
correlates as the same transmission, combine (MRC on the aligned soft symbols) and decode the
sum. +3 dB on paper for two copies, more than that against fading because the two fades are
independent. Novel for this ecosystem, receive-only, wire-legal. Needs: burst soft-buffer
(bounded), alignment via the sync word + correlation, and honest accounting so a combined
decode is badged as such in the frame log.

**Status 2026-08-07: sized before building, and the measured ceiling says do not build -
for now.** The sizing itself took three attempts, each failure instructive enough to record:
pairing the survey's missed bursts found zero retry twins, but the survey's own sampling
policy (120 s per-250 Hz-bucket cooldown, 30/h cap) structurally discards the second copy of
any retry pair, so that zero measured the cooldown (Tom's catch); raw band-energy detection
swung to the opposite artefact, counting the slot's abundant foreign traffic as misses; and a
squaring classifier failed to separate. The honest instrument is the receiver itself:
`sm-ota replay --bursts` (PR #250) uses the bank's own DCD as the per-burst bpsk verdict,
with sync-without-decode surfaced through new RsFailures/CrcFailures counters and the
calibration cross-checked against the frame log (93-98 % containment; the classifier errs
conservative). On the first full day: 873 DCD bursts of ~2600 energy events (72 % of the
slot's energy is foreign), 743 decoded, 130 damaged - of which 72 had their content recovered
by a decoded retry sibling within ten minutes, and only **2-5 all-copies-lost retry groups
existed in 28.6 h**: roughly 2-4 combinable frames per day against ~1030 decoded, a
throughput ceiling of 0.2-0.4 %. The link layer's own retransmission diversity (43.5 % of
decoded traffic is retry copies, chains to 14 deep) is already doing this workstream's job at
these SNRs. Re-run the sizing (the instrument is one command now) when the corpus contains
storm or contest days - the twin class is exactly what deep synchronised QSB would populate -
and build only if it appears.

### 4. Two-pass, non-causal burst processing

The receiver is strictly causal; a burst is short and cheap to buffer. First pass: decode as
today while estimating the burst's timing/phase/amplitude trajectory end-to-end. Second pass
(only when the first fails): re-decode with the smoothed, non-causal estimates. Claims the
remaining ~0.5 dB of DPLL timing loss, and is the natural place to compute fade-envelope
erasure flags for workstream 1. Fits inside `BpskDemodulator`/`BpskMultiModem` without touching
the streaming surface, since branches already hold per-burst state.

**Status 2026-08-08: sized before building, and the timing half is a measured null - perfect
symbol timing is worth nothing.** The instrument is `sm-ota oracle` over a new bench seam
(`BpskDemodulator.SymbolInstantObserver` / `SymbolInstantSchedule`): the Watterson rig draws its
fades from the burst seed *before* its noise, so the same seed at infinite SNR replays the
identical channel with the noise left off. A pass over that noise-free copy records the sample
index of every symbol instant, and the measured arms decode the *noisy* copy reading their
symbols at those instants. The DPLL keeps running throughout - transitions, `PacketDcd`, and
therefore the deframer's gating are held constant - so the arms differ in one variable only:
when each symbol is read. Four arms per burst: **causal** (deployed), **oracle** (the noise-free
instants), **grid** (a perfect constant-rate clock at the noise-free phase - the transmitted
symbol grid itself, since the sim shares one clock end to end, and therefore the best a
perfectly-smoothed non-causal estimate could ever be), and a **control** whose schedule is
slipped half a symbol. The control decoded 0 of 200 at every rung of 5600 bursts, which is what
licenses reading a null off the rest.

Measured, single-branch bpsk300, N=200/rung, seed 1 (`docs/bench/timing-oracle-2026-08-08.txt`):
pooled over 5600 bursts **causal 2774, grid 2763 (-0.2 points), oracle 2591 (-3.3 points)**. At
the AWGN knees the perfect clock buys 73 -> 76 at -5 dB and 147 -> 153 at -4 dB against a local
slope of ~70 frames/dB: **under 0.1 dB, against the ~0.5 dB the workstream claimed**. Good is
parity across -5..+12 dB (+7/+3/+1/-1/+1/+3/+2). On Moderate the perfect *fixed* clock is
consistently WORSE (-3/-2/-10/-9/-7/-5/-1), and the perfect *instantaneous* clock is worse
everywhere it can be (Good -11 to -21, Moderate -3 to -17).

Two mechanisms, both worth keeping. At 40 samples per symbol behind an RRC matched filter the
timing-error slope is gentle enough that the DPLL's residual jitter costs a fraction of a dB, and
the measurement puts that fraction under a tenth. And on a fading channel there are *two*
candidate true clocks - the transmitted symbol grid, and the composite channel's instantaneous
group delay as the paths swap dominance - so sampling perfectly at either one loses to the DPLL's
inertia-limited compromise between them. **The inertia is not a limitation waiting to be smoothed
away; it is the smoothing**, and it already sits where a second pass would be estimating toward.
This also corrects a suspicion this document recorded under workstream 5: DPLL timing wander
during dominance swaps is a *symptom* of the fade, not an independent cause of Poor's ceiling - a
perfect clock rescues none of those frames (Poor rows at parity).

What survives of the workstream: converged equaliser taps from symbol 0 (real, but bounded by
what MLSE is worth, which measured at parity except ~+4 points on Poor) and fade-envelope erasure
flags for workstream 1 (whose conversion shell the crash-erasure leg already measured at ~1
point at the joint-budget knee). Neither pays for the buffering architecture on its own, so the
workstream drops below 6 and 7. The instrument stays: the question deserves re-asking on a mode
with few samples per symbol (fsk9600 runs 1.25 at 12 kHz, where timing is a different animal),
and `sm-ota oracle` is the way to ask it.

**The lead the null left behind: timing diversity, not timing estimation.** The same probe's
hindsight arm (`--phase-sweep`: decode at all 40 phases, count the burst if any delivers) reads
70/100 at AWGN -5 dB against a single branch's 34, and 95 against 69 at -4 dB. The winning phase
is picked knowing which one decoded, so this is no bound on estimation - but a receiver can
select on exactly that criterion, because a frame passes IL2P RS+CRC or it does not. It is the
frequency bank's own trick along the other axis, and the phase branches could share the whole
front end (band-pass, mixer, matched filter are phase-independent; only the symbol decisions,
the DF-DD reference and the deframer duplicate). Before anyone builds it, the honest comparison
is against the **deployed bank**, not one branch: on the same seeds the 9-branch bank already
scores 56/88/95 at -5/-4/-3 dB, so it banks most of this diversity already and leaves at most
~14 and ~7 points at the two knees before overlap is subtracted - against a proportional
multiplication of the bank's false-accept exposure. Sized, recorded, not built.

The instrument had a fault worth recording, found by a result that looked like physics: the first
displacement profile read exactly 0/100 across its whole negative half, which was a schedule
stalling on entries that fell before the first sample rather than a receiver that could not
decode. Every headline number here was re-measured after the fix.

### 5. Poor-channel MLSE (the known mode limit, half-liftable)

CCIR Poor's 2 ms echo at 300 Bd spans ~0.6 symbol: the composite channel fits a **2-4 state
MLSE/BCJR** per branch - nearly free at this rate, and optimal for the ISI half of Poor's
losses. Expected: the ~29 % ceiling moves to 50-60 %. The other half is flat outage (both
Rayleigh paths fading together for 60-150 symbol times) which **no receiver fixes at fixed
wire format** - there is no interleaving to bridge it. Beyond MLSE, Poor is workstream 8's
problem.

**Status 2026-08-06 (night): built, measured, and the 50-60 % claim is retired.** The
equaliser exists (`MlseEqualiser`, `PskDetector.Mlse`, `sm-ota sim --detector mlse`): a
4-state Viterbi over the absolute polarities behind the unchanged differential front end,
3 complex taps under decision-directed LMS, SOVA margins feeding the erasure ladder,
16-symbol traceback inside the DCD window. Getting it to *parity* took four measured
design corrections, each worth recording: mid-stream flushes swallow a pipeline of bits (a
frame-killing slip - the trellis must free-run); a 3-tap LMS is **unidentifiable on the
all-reversal preamble** (only g0-g1+g2 is observable), so unconstrained adaptation walks
into the sync word with the main energy split equally across all three taps - the fix is
skipping outer-tap updates exactly on alternating decision triples; outer taps may enter
the metrics only on sustained, smoothed evidence during a seeded burst (unconditional
exposure cost ~5 dB on AWGN from metric self-noise; between bursts the tap-power ratio is
meaningless and engagement at burst start cost 0/50 at -5 dB); and the main tap plus
rotation tracker form a per-symbol feedback loop that must stay undelayed, while the outer
taps want traceback-matured (depth-6) decisions.

Measured endpoint (N=100/point, seed 1; Poor 6/9 dB re-confirmed on seeds 101-200):
AWGN and the CFO sweep at parity within two points (a hair under at -5..-3, even at -2);
Good/Moderate at parity with +3 at the top rungs; **Poor +6/+9 dB pooled over both seed
spans: 51->58 and 56->64 of 200, roughly +4 points**; miss corpus unchanged at 32/37
demodulated. Why the ceiling never moved: the frames Poor takes die of flat outage, DPLL
timing wander during dominance swaps (**withdrawn 2026-08-08**: workstream 4's timing oracle
rescues none of those frames with a perfect clock, so the wander is a symptom of the fade rather
than an independent cause), and near-antiphase composite nulls (a static-echo
probe found **no** two-path setting that separates the detectors through the full chain -
DF-DD+RS digests any static echo the trellis can equalise, and what kills DF-DD kills the
trellis too, because a symbol-rate equaliser cannot restore timing); and the preamble's
unidentifiability means the outer taps cannot converge before the sync word, so
header-limited frames are structurally out of reach - engagement matures mid-header at
best. **The default detector stays differential.** Two successors inherit the machinery:
the capture campaign's misses-v2 should A/B `mlse` on real audio (the sim says the gain
lives exactly where real QSOs operate on 40 m), and workstream 4's two-pass receiver is
the honest answer to "converged taps from symbol 0", which is most of what this
architecture cannot reach causally.

### 6. Channel-model extensions: impulse noise first, then the rest of reality

(Broadened from "impulse-noise instrumentation" on 2026-08-06, Tom's prompt: the masks pin
the model, so extending the model extends what the masks can hold.) Ranked by value per
effort:

1. **CCIR Moderate - DONE 2026-08-06.** The middle of the standard triple (1 ms / 0.5 Hz)
   was the biggest gap for the least work: Good is outage-bound and Poor equaliser-bound, so
   Moderate is the channel where receiver improvements actually show. Measured on landing:
   bpsk300 climbs 23/42/53/60 % across -2..+4 dB (a real slope at last); freedv-datac3
   rides it at 92-100 % - the OFDM contrast, on the record. Profile pinned in
   `SimBenchTests`, mask rows in both tiers.
2. **Impulse noise, calibrated from the capture.** Real 40 m evenings are static crashes and
   QRN; the receiver has no blanker and no heavy-tail-aware metric, and nothing measures the
   cost. The capture campaign changes the job: derive arrival rate, amplitude distribution
   and burst shape from the raw chunks' real summer-evening QRN rather than inventing a
   Middleton model, then the blanker or clipped-metric fix, sized by what the instrument
   shows. Possibly the most real-world dB per line of code on this list.

   **First measurement, 2026-08-06 (the campaign's opening evening, 9.4 h).** Methodology
   that survived its own first contact: a naive full-band envelope detector counted packet
   transmissions as impulses (near-full-scale "events" with durations chopped by its own
   adaptive background); the honest detector runs on the *out-of-slot* audio - the slice
   passband measures 0-3 kHz with the slots at 850/2150 Hz, so 2500-2950 Hz is in-passband
   but signal-free - with a 20th-percentile background the sparse events cannot pollute,
   and a 300-700 Hz coincidence gate separating broadband atmospherics from band-limited
   interference (38 % of quiet-band events were broadband). Measured: **60-220 broadband
   crashes/min** across the evening, peaking 18:00-20:00 UTC; median crash **15-30 ms
   (~5-7 symbols at 300 Bd)** at median **~18 dB over the quiet-band floor**; heavy tail
   p90 24 dB / p99 35 dB / max 73 dB, with second-long crash trains at p99 duration. Known
   limits, recorded: slice AGC compresses absolute amplitudes (rates and durations robust,
   the amplitude tail a lower bound), and the rate counts every sferic tick above 6x the
   quiet floor - the modem-relevant subset is the strong tail. Next: correlate crash times
   against frame-log misses to size what a blanker would actually buy.

   **The model exists and the cost is measured (2026-08-07 late).** The injector
   (`ImpulseNoiseProfile`, an axis on the Watterson rig like CFO; `sm-ota sim --impulse
   <rate/min>`; anchors Evening=120, SunrisePeak=220) is calibrated through a closed loop:
   its own output re-analysed by the campaign's measuring instrument reproduces the
   measured percentile table (docs/bench/impulse-model-validation-2026-08-07.txt - rate
   +13 %, amplitudes within 1.9 dB, durations within 28 %, with the instrument couplings
   that cost three loop iterations recorded so nobody re-fights them). The cost table the
   workstream never had: an ordinary evening's 120/min collapses bpsk300 AWGN -4 dB from
   88 % to 23 % and Moderate +2 from 55 % to 29 %; the 220/min peak takes them to 6 % and
   19 %; qpsk600 0 dB falls 69 -> 28 -> 13 %. QRN is, by these numbers, the largest
   un-addressed receive impairment in the model set. A naive clipping blanker was probed
   and is NOT landed: +11 points at the evening rate, nil at the peak, ~1.5 clean-channel
   points of cost - because at 300 Bd a 5-7 symbol crash is a short jam, not an impulse;
   clipping bounds its energy but cannot restore the symbols under it. The follow-up the
   probe points at: the blanker knows exactly which spans it clipped, and that knowledge
   belongs in the per-symbol confidence stream feeding the erasure ladder (crash-hit
   bytes become erasures, which is what the RS budget can actually spend on them).

   **Leg 2, crash-marked erasures (2026-08-08): that follow-up ran, and it is a measured
   null - with the mechanism that finally explains the whole impulse cost.** The crash
   detector (raw-envelope ratio, broadband on purpose: in-band detection measured blind at
   under 2x prominence - the impulse instrument's own lesson recreated in the receive
   path) was wired into the bpsk confidence stream with marks consumed at the matched
   filter's group delay (undelayed marks erased the fine bytes AHEAD of the damage - an
   exact zero until aligned). Correct, tested, zero clean-channel cost - and N=300 at the
   -4 dB knee under 120/min: 97/300 vs 93/300. Why there is nothing to convert: **crash
   TRAINS carry the impulse cost** (true baselines at 0/+2 dB under 120/min are only
   53/61 %, and those failures are train hits - hundreds of bytes, beyond any erasure
   budget), short crashes at high SNR already survive errors-only decoding, and at the
   joint-budget knee the ladder's erasures+2*cap=14 leaves a conversion shell worth
   ~1 point. Implementation archived unmerged on branch `crash-erasures` (the
   chase-decoding precedent). The honest residual: the impulse cost is a TRAIN problem -
   an outage class like Poor's flat fades that no in-frame machinery fixes at this wire
   format; what would pay is avoiding or riding trains (retry timing, workstream 8's
   escalation), not decoding through them. Instrument hazard found en route, worth a
   separate fix: `sm-ota sim` accepts unknown flags silently (a one-merge-stale binary
   swallowed `--impulse` and measured a clean channel while claiming an impulse run -
   caught only because the numbers matched the no-injection table).
3. **Slow CFO drift + phase noise.** The exact impairment that walled qpsk2400 (#116) and
   the RSP1 coherent modes (#102): a Hz-per-minute ramp plus a 1/f phase process. Cheap (a
   time-varying rotation in the channel), and it is what the undisciplined-radio aspiration
   needs to develop against.
4. **TX/RX sample-clock skew.** Real soundcards sit +/-50-100 ppm apart; the sim shares one
   clock, which quietly flatters every timing loop. A resample-by-(1+epsilon) axis protects
   the DPLL work.
5. **SSB passband tilt** (a configurable radio filter post-channel) - placement-dependent
   losses for multi-modem plans; **QRM injection** and **AGC dynamics** - both waiting on
   real interferer material from the capture.
6. **An FM channel model** - deviation error, emphasis mismatch, discriminator noise,
   flutter. Its own track, and the gate on masks for the FM modes.

   **Status 2026-08-08: built, validated and masking.** `FmChannel` is a physical link rather
   than a curve fit: the modem's audio really is frequency-modulated onto a carrier, noise is
   added *there*, and a limiter and discriminator bring it back. Every FM impairment then
   emerges instead of being asserted - the threshold effect, the discriminator's rising noise
   spectrum, IF truncation, emphasis, deviation error and flutter. `FmChannelTests` pins them as
   physics (below the knee the output falls away faster per dB than above it; noise power rises
   with the square of audio frequency and de-emphasis flattens it; under-deviation costs output
   SNR; a microphone path will not pass 4 kHz). The axis is **carrier-to-noise ratio in the
   receiver IF bandwidth**, deliberately not SNR3k: the two are different quantities and moving
   a number between them would be wrong. Driven as `sm-ota sim --channel fm-mic|fm-data`, each
   mode on the channel spacing it is deployed on.

   First measured FM ladders (N=50, seed 1, TXDELAY 150 ms, 50 % knees):

   | mode | channel | path | knee |
   |---|---|---|---|
   | `afsk1200-il2p` | 12.5 kHz | mic + speaker | ~+8 dB |
   | `afsk1200-il2p` | 12.5 kHz | data port | ~+7.5 dB |
   | `fsk9600-il2p` | 25 kHz | data port | ~+10.5 dB |
   | `fsk9600-il2p` | 25 kHz | mic + speaker | **never** - 0/50 at +15, +21 and +27 |
   | `c4fsk9600` | 12.5 kHz | data port | ~+19.5 dB |
   | `c4fsk9600` | 25 kHz | data port | ~+17.7 dB |
   | `c4fsk19200` | 25 kHz | data port | ~+16.5 dB |

   Three results worth keeping. **The model reproduces the reason 9600 packet needs a data
   port** without being told: 9600 GFSK through a 300-3000 Hz emphasised microphone path decodes
   nothing at any signal level, a well-known operational fact falling straight out of a
   passband. **The channel spacing is worth 4.5 dB**: `fsk9600` first measured a ~15 dB knee
   because it was run on a narrow channel, and moved to ~10.5 dB on the wide channel it is
   actually deployed on - so an FM number without its spacing is meaningless, and
   mode-modulation-reference.md now records the spacing per mode. **A noiseless FM link is not
   arbitrarily clean**: full deviation on a low tone spreads sidebands past a narrow IF filter,
   and symmetric truncation comes back as odd-order distortion (third harmonic ~-25 dB on an
   8 kHz filter) - a ceiling any dense constellation meets before it meets noise, and the first
   thing OFDM-AB's QAM-256 rung will run into.

   Masks: five blocking FM rows plus the microphone-path negative in `WattersonMaskTests`, smoke
   tier still ~40 s. What this does not yet have is calibration against a real radio: the
   numbers are the model's, and the `sm-ota ladder` FM route over the Flex/RSP1 rig is the
   instrument that would tie them to hardware. Until that runs, an FM mask means "no regression
   against the model", exactly as workstream 0 says of the Watterson masks.

### 7. Per-station acquisition priors

The daemon hears the same handful of stations all day. Cache each station's measured carrier
offset, timing skew and level (keyed by heard callsign) and warm-start acquisition from the
cache. Targets short supervisory frames, which die in acquisition, and the corpus says the
real misses skew exactly there. Modest scope; measurable directly against the capture
campaign's frame log (same station, decode rate before/after).

### 8. Waveform escalation between consenting stations (strategic)

The honest answer to Poor, and to every ceiling above: the repo already carries ARDOP, MS110D
and the FreeDV datac OFDM family, which have the equalisers and interleaving this waveform
lacks. Blue sky: per-neighbour capability discovery - speak IL2P BPSK as the lingua franca,
recognise a pdn-soundmodem peer, escalate that link to a Poor-capable waveform automatically.
Protocol and node work, not receiver work; parked here so the receive plan says out loud where
its own ceiling is.

**Status 2026-08-15: the first real-path evidence for this, and it is a strong argument.** The
FreeDV campaign transmitted `freedv-datac1` and `freedv-datac3` from the production GB7RDG node
over a real ionospheric hop within the same half hour, same power, same dial (ledger entry of
2026-08-15). `datac3` delivered **6 of 6**; `datac1` **11 of 14**, and on a later, worse pass
`datac1` managed 10 of 23 where the burst-level analysis put the threshold squarely between the
two modes' requirements. That gap is roughly 5 dB of link margin, bought purely by choosing a
different waveform on a link that had the option, and it is the escalation ladder in miniature:
the same choice a capability-discovery mechanism would be making automatically. It also sharpens
the case, because the alternative reading of workstream 5's retirement - "Poor is just hard" - is
true but incomplete. Poor is hard *for this waveform*. The escalation answer is the one that
moves, and the modes it escalates to are already in the repo and already validated on air.

### 9. Per-frame SNR in the record (cross-cutting; the cheapest item here)

**The daemon already measures this and then throws it away.** `BandActivityTracker.TryMeasureBurst`
computes a per-burst SNR, and the waterfall draws it - but the `frameLog` schema has no SNR column
and the `rx[...]` journal line does not print it. So the station's own record of what it heard
cannot answer "how strong was it", which is the first question of nearly every receive
investigation.

That cost real time on 2026-08-15. Answering "why did those three `freedv-datac1` frames not
decode" required standing up a parallel `sm-iqcapture` session, a Welch analysis and a
burst-to-decode time correlation, purely to recover a number the daemon had already computed and
discarded. With SNR in the frame log the whole investigation is a SQL query, and it is one that
can be run retrospectively against captures already sitting on disk.

Concretely: an `snr_db` column on `frames`, the figure on the `rx[...]` line, and the same number
in the KISS quality frame where one is emitted. Worth ranking **above every DSP workstream above
it**, not because it improves a single decode, but because it is what makes the others
measurable in the wild rather than only on the sim ladder. The masks tell us what the receiver
does against a model; this tells us what the band did to us, per frame, for free, forever.

Care needed on one point, learned the same day: an SNR figure must say what it is measured
against. The waterfall's is a band-tracker ratio against a rolling noise floor, which is not the
3 kHz-referenced convention the sim ladder and the masks use. Record which one it is, in the
column name or a documented convention, or the two will be compared and the comparison will be
wrong.

**Status 2026-08-15 (later): landed.** The measurement moved into the channel itself
(`BurstSnrMonitor`: a waterfall source feeding one `BandActivityTracker` per modem band,
bands from the same `ModemBandProbe` the waterfall overlay and the RF planner use), so it
exists on every station - the capture rig never had a waterfall configured and therefore
never had the number at all. Enrichment happens at the one point every modem's quality
passes through (`SoundModemChannel.AddModem`'s FrameDecoded wrapper), so the frame log's
new `snr_db` column, the `rx[...]` journal line's `snr x.x dB` figure, the KISS quality
frame's `snrDb` and the waterfall panel all carry the IDENTICAL figure - one measurement,
per the branch-index-offset lesson. The convention warning above is answered in
documentation rather than the column name: `FrameQuality.SnrDb`'s doc, the frameLog table
in CONFIG.md and the ledger entry all state it is the band-tracker ratio, NOT SNR3k. A
quiet band at decode time yields null, never zero.

### 10. Timing diversity and the clock hold for the remaining single-carrier modes (opened 2026-08-21, issue #331)

PR #330 gave both PSK demodulators seven decision phases per symbol (the recovered clock
instant and 2.5, 5 and 7.5 % of a symbol either side, each with its own detector state and
its own `Il2pReceiver`, the modem delivering whichever copy passes once behind a
symbol-clocked dedupe) and a DPLL that holds nearly rigid once a burst is established. On
deterministic seeds it is worth about 1 dB at the knee of qpsk600, qpsk2400, bpsk300 and
bpsk1200, and it is what copies the 2026-08-21 off-air qpsk600 fixture (ledger, 2026-08-21
later4). The technique needs only a symbol clock and a deframer that can say yes or no, so
it reaches every single-carrier mode; the open items, ranked by likely payoff, live in
**issue #331**: afsk300-il2pc first (same DPLL, same RS edge, bank already there), then the
FSK/C4FSK IL2P modes at 48 kHz (interpolated sub-sample phases), then afsk1200's classic
HDLC (any phase whose FCS checks, a few per cent of frames rather than a decibel). FreeDV
datac, MS110D and ARDOP are not candidates. Two rules carried over from #330: verify the
phases actually differ by scoring a known frame per phase before trusting any ladder (the
first cut's phases were [0, -0, 0] and the ladder read a clean null), and gate the clock hold
on a DCD that really means the clock has converged (BPSK's seed fires too early; QPSK's DCD is
marginal, #329).

**C4FSK landed 2026-08-21 (PR for issue #331's third item, ledger 2026-08-21 later5).** Both
C4FSK modes now decide at seven phases and hold the clock on DCD, worth 1.0 to 2.1 dB at every
knee measured (c4fsk9600 fm-data 16.6 -> 14.8 dB CNR at TXDELAY 0, 18.9 -> 16.8 at 150 ms;
c4fsk19200 fm-data 16.2 -> 14.4 and 17.9 -> 16.3; the AWGN rows 1.0 to 1.6 dB). The
resolution question the item raised has an answer: at 10 decision points per symbol the PSK
set's 2.5 % step measures materially worse than 5 %, 7.5 % measures the same as 5 %, and a
ninth phase buys nothing, so the mode runs 5 % x 3 pairs. The phases carry nearly all of it;
the hold is neutral on AWGN and worth a few frames in 200 on fm-data. Two things the
measurement turned up for later: TXDELAY costs these modes 2 dB on fm-data and 5 dB on AWGN
(a longer run-in making a receiver worse is backwards, and it is bigger than what timing
diversity just bought), and the 4-PAM clock's wrap can only land on a sample, so its decision
instant is up to a tenth of a symbol late and the phase set is not centred on the eye -
reading the decision back to the instant the clock asked for, as `QpskDemodulator` does, is
the next cheap thing to measure.
**The AFSK family is done (PR #334, ledger 2026-08-21 later6).** `AfskDemodulator` decides
every bit at the seven phases and holds its clock once DCD asserts; every wrapper runs a
deframer per phase behind a bit-clocked content dedupe. Worth about a third of a decibel at
every AFSK knee on AWGN (`afsk300-il2pc` -2 dB 61 -> 92 of 200, `afsk1200` +6 dB 107 -> 129,
`afsk300` 0 dB 70 -> 86), a little on the FM channels, and nothing at all on the fading ones.
Measured apart, the phases are the entire sim gain and the hold is neutral there, kept for
what it does to a real capture. Two findings for the modes still open: the eye sweep
(`AfskEyeSweepProbe`) is the cheap way to know in advance how much a mode's timing can be
worth - 300 baud's plateau is nine samples wide at 40 samples per bit, which is why it gains
a third of what the PSK modes did, while 1200 baud's window is two samples wide - and at
40 samples per bit the phases one and two samples early are often the same decision, which is
real rather than a bug and is what the per-phase scoring test exists to tell apart.

## Discipline

- Every workstream that changes decode behaviour lands with its sim-ladder A/B and a corpus
  re-score, and gets its dated entry in [mode-validation.md](mode-validation.md).
- The 37-frame corpus is close to exhausted as a discriminator (32 demodulate, 22 deliver); do
  not tune against its tail. New tuning decisions wait for the capture campaign's `misses-v2`.
- Nothing here changes a single transmitted bit: NinoTNC interop is ground truth, and the
  parity/QtSM/off-air suites stay the regression gate.
