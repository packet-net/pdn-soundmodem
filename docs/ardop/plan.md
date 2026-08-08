# ARDOP off-air validation campaign

Opened 2026-08-08 on Tom's direction, after the receive-sim campaigns (bpsk300, QPSK) reached
their measured conclusions and the capture harvest characterized slot 2 of the monitored 40 m
band as carrying daily wild third-party ARDOP sessions. The repo's ARDOP implementation
(M0LTE.Ardop plus the daemon's `ArdopChannelBridge` and `Ardop/Host/`) is validated only
against ardopcf on `snd-aloop` bench rigs - the acceptance ladder's Rungs 0-4 (see
docs/ardop-design.md §6.2, §7). This campaign opens a new leg of that ladder: not a controlled
peer on a loopback cable, but the wild band, where sessions arrive at any strength, from any
implementation in the ARDOP family, heard from one side. It underpins workstream 8 (waveform
escalation needs a trustworthy ARDOP) and closes the rx-roadmap workstream 0 debt that ARDOP
has no maskable sim seam - the off-air monitor is that seam's first form.

## Charter and ground truth

- **What is being validated:** our ARDOP RECEIVE chain (`ArdopDemodulator` in RXO mode) against
  real, uncontrolled ARDOP traffic. Not TX (nothing is transmitted; this is receive-only
  monitoring, like the capture campaign), not the ARQ engine's timing (no session of ours), not
  the host interface. Just: can we hear, classify, and attribute what the band actually carries.
- **Ground truth is weaker than the bench rig's and honestly so.** The bench rig knew both
  stations, both configs, every byte sent. The wild band gives us none of that: we hear one
  side well and the other poorly or not at all, we do not know the sending implementation
  (ARDOP_Win / ardopc / ardopcf / a G8BPQ variant that is deliberately non-compatible), and
  there is no referee. "What should have decoded" comes from session-level structure (a ConReq
  answered by a ConAck implies a real session whose data frames we then either read or miss),
  never from a second receiver. This is the same step down the capture campaign took from the
  GB7RDG NinoTNC referee, recorded the same way.
- **Interop family:** only ARDOP 1 (spec Rev 2.0) is OTA-compatible; the G8BPQ "ARDOP 2" /
  ardopofdm variants are not and are out of scope (docs/ardop-design.md §1.2). A wild burst that
  is structurally ARDOP-shaped but never type-decodes may be one of those, or a badly faded
  ARDOP 1 frame, or not ARDOP at all - the instrument records the residue's shape and does not
  guess.

## The A0 instrument

`sm-ota ardop-monitor` (tools/Packet.SoundModem.Ota/ArdopMonitorCommand.cs): runs
`ArdopDemodulator` with `RxoMode = true` over recorded audio and emits one verdict row per
acquired frame - type, RS/CRC ok, quality 0-100, the RXO-decoded third-party session ID, the
station fields where the frame carries them, and the measured leader length - then groups
consecutive same-session-ID frames into sessions. Two feed modes: `--raw` (continuous over the
capture chunks with absolute timestamps, one demodulator carrying RXO/Memory-ARQ state as a
live monitor would); `--wav` (a fresh demodulator per file, for the survey's isolated bursts).

**Centre handling** replicates `ArdopChannelBridge`'s receive discipline: ARDOP is pinned to a
1500 Hz centre, the wild slot sits near 1650 Hz audio, so the instrument bandpasses to the
on-air band FIRST and unshifts SECOND - the noise-folding order the bridge audit made
load-bearing (docs/mode-validation.md 2026-08-06: unshifting a centre above 1500 folds
sub-delta noise onto the band unless the bandpass has already zeroed it, measured at +2.9 dB).
Tuning range defaults to +-200 Hz (spec §4.1 capture requirement; the wild centres spread
1500-1800 Hz), wider than ardopcf's +-100 because a monitor does not choose its stations.

**Limits, stated up front.** RXO decodes what type-acquires; a frame whose leader is too weak
to acquire never appears (the instrument cannot report what it did not hear, and on the wild
band that is most of the far side of every session). Session grouping is by RXO session ID
within 120 s, which merges two genuinely separate sessions that happen to hash to the same
CRC-8 ID if they abut - rare, and flagged where the callsign fields disagree. The instrument
is a MONITOR, not a decoder of record: it says what it read and what it saw but could not
classify, exactly as the burst-verdict instrument does for bpsk.

**Positive controls** (`ArdopMonitorTests`): known library-modulated frames (an IDFrame and a
4FSK.500.100 data frame) return through the monitor at the native centre and through the
shifted 1650 Hz path - the instrument's zero is distinguishable from a broken chain, the
burst-verdict campaign's day-one lesson applied here.

## A0 baseline scoreboard

(filled by measurement over the capture to date; the raw-chunk continuous scan and the
survey-burst isolated scan reconciled)

Measured 2026-08-08 over the capture to date (2026-08-06 13:53Z to 2026-08-08 07:33Z,
176 chunks; the 6.85 h dead-feed silence contributes nothing and needs no exclusion). One
continuous RXO pass at centre 1650 Hz, +-200 Hz tuning
(`sm-ota ardop-monitor --raw /home/tf/capture-40m/raw --centre 1650 --csv ...`), decoding
~45 h of audio in ~75 min single threaded - the demodulator is sample-fed with no wall-clock
pacing, so the corpus rescans at ~36x realtime.

| Verdict | Count |
|---|---|
| frames acquired (type decoded) | 636 |
| frames fully ok (RS/CRC where carried) | 551 (87 %) |
| session groups (>=2 frames, same RXO id in 120 s) | 88 |
| distinct stations identified | GB7BPQ, GB7BWR-2, DC7DE (+ unattributed sessions) |

Frame families, acquired -> ok: control healthy (DataACK 117/117, IDLE 58/58, BREAK 53/53,
DataNAK 12/12, DISC 12/12, END 10/10); connection frames good (ConReq500M 63->52,
ConAck500 30->25, IDFrame 65->60); 200 Hz class data strong (4FSK.200.50S 31->31,
4PSK.200.100S 28->26, 4PSK.200.100 24->19); **4FSK.500.100 the workhorse and the gap:
84 acquired -> 50 ok (60 %)**; 4PSK.500.100 38->26; **the top rungs acquire and never
body-decode: 16QAM.500.100 7->0, 8PSK.500.100 3->0, 16QAM.2000.100 1->0**. GB7BPQ runs a
half-hourly two-frame ID sequence (the [FF] groups at :00:57/:30:57), a free recurring
calibration signal.

Survey-vs-raw reconciliation: the isolated survey pass (258 unclaimed 1200-2100 Hz bursts)
decoded 11 frames from the same events the continuous pass read - the survey's
cooldown-capped index holds ~2 % of the slot's decodable traffic, so the raw chunks are the
corpus and the survey is only a pointer, exactly as the bpsk harvest found.

## Loss surface for A1

(named from the baseline residue: which frame classes acquire but fail body decode, which
bandwidth classes appear, what the unrecognized bursts look like)

1. **The top-rung body decode: 0 of 11 wild 16QAM/8PSK bodies.** Types acquire (leader,
   sync and type tones are fine) and every body fails RS/CRC - the design doc's predicted
   thin-margin risk (docs/ardop-design.md §3.6) measured on air. A1 asks: is it SNR (the
   wild copies were simply weak - check their quality scores and S:N against the 4FSK frames
   of the same sessions), our amplitude/phase tracking against real drift, or the far-side
   problem (top rungs only fly when the link is good, and the good direction is the one we
   hear poorly)?
2. **4FSK.500.100 at 60 % body-ok.** The workhorse rung with 34 failed bodies - enough wild
   copies for a real autopsy, and sessions with retries provide same-content pairs (the
   band's own referee).
3. **ConReq500M 11 failures of 63** - connection frames carry callsigns (RS 4), so each
   failure is an attribution loss; same autopsy shape.
4. **The 200 Hz class is nearly clean** (76 of 83 bodies ok) - whatever A1 changes must not
   disturb it; it is the guard rail.
5. **Acquisition itself is unmeasurable from this side alone**: frames whose leader never
   acquired do not appear. The A1 instrument extension worth having: a leader-candidate
   counter (acquisitions that never reached type decode), so the residue above type-decode
   stops being invisible.

## A1: the reference-decoder autopsy (2026-08-08)

The loss surface above was put to the reference implementation frame by frame. Two new
instruments carry the experiment, both in `sm-ota`:

- **`ardop-cut`**: cuts one WAV per frame group (frames within 120 s of each other) out of
  the raw capture around every baseline failure, whole-session so Memory-ARQ context is
  preserved, with a manifest aligning every baseline verdict to its offset in the cut.
  Capture gaps are silence-filled so offsets stay truthful.
- **`ardop-monitor --emit`**: writes the exact post-chain stream (bandpassed, unshifted to
  1500 Hz) the demodulator consumed, so an external decoder can be handed bit-identical
  audio. The external referee is ardopcf 1.0.4.1.3 (`--decodewav`, the design doc's pinned
  provenance commit a7c9228, built locally; GCC 14 needs `-std=gnu17
  -Wno-error=int-conversion` for its rawhid/ws_server libs, no source changes).

ardopcf has no wide tuning search, so each cut was emitted at trial centres 1500-1800 in
20 Hz steps and the referee scored at each; the plateau locates the session's true centre.
Reproduction: `tools/.../sm-ota ardop-cut --csv ardop-baseline.csv --raw
/home/tf/capture-40m/raw --out <dir>`, then the sweep + per-frame join scripts (session
scratch: `a1-sweep/sweep.sh`, `a1-sweep/perframe.py`).

### Autopsy table (52 cuts, 344 aligned frames, referee at each cut's best centre)

| family          | ours ok / oracle ok | ours FAIL / oracle FAIL | ours FAIL / oracle ok | ours ok / oracle FAIL |
|-----------------|--------------------:|------------------------:|----------------------:|----------------------:|
| 16QAM.500.100   |                   0 |            4 (+3 unseen) |                     0 |                     0 |
| 16QAM.2000.100  |                   0 |                       1 |                     0 |                     0 |
| 8PSK.500.100    |                   0 |            2 (+1 unseen) |                     0 |                     0 |
| 4FSK.500.100    |                   9 |                      31 |                     3 |                     1 |
| 4PSK.500.100    |                  13 |                      12 |                     0 |                     4 |
| 4PSK.200.100(S) |                  39 |                       7 |                     0 |                     2 |
| 4FSK.200.50S    |                  29 |                       0 |                     0 |                     2 |
| ConReq500M      |                  21 |                      10 |                     1 |                     0 |
| ConAck500       |                  15 |                       0 |                     5 |                     0 |
| IDFrame         |                  20 |                       5 |                     0 |                     0 |
| control (ACK/NAK/IDLE/BREAK/DISC/END) | 189 |               0 |                     0 |                     0 |

### Findings

1. **The top-rung null is confirmed by reference parity.** All 11 wild 16QAM/8PSK bodies
   fail in ardopcf too, at every trial centre; 4 of the 11 it never even acquires. These
   are weak one-sided copies (we hear the poor direction), not a decoder gap. The design
   doc's thin-margin prediction stands, but our implementation is not behind the
   reference. Honest null; no decoder work indicated by this corpus.
2. **The 4FSK.500.100 60 % body rate is dominated by one emitter.** The failed pairs
   recur at half-hour marks +37 s: GB7BPQ's FEC beacon (BPQ node position + text, sent
   20 s before its known ID pair), and both decoders fail it 31 of 34 times - at the
   decode threshold, not a defect. Of the 3 oracle-only decodes, one passed at the full
   8-of-8 RS correction budget (a coin-toss margin), and 2 of our failures decode for us
   too once the cut is monitored at the session's true centre.
3. **The ConAck500 5-0 to the oracle is a policy artifact, not demodulation.** ardopcf's
   `Decode4FSKConACK` (SoundInput.c:3059) initialises Timing to 0 and tests `Timing >= 0`,
   so a body with no 2-of-3 majority still PASSes, reporting "timing 0 ms" - it cannot
   fail a ConAck. All five disputed frames show exactly that. Our decoder implements the
   strict majority and calls them FAIL. Interop question for A2: live ISS peers proceed on
   such ConAcks, so matching reference behaviour (frame ok on type decode; timing null
   without majority) is the interop-correct choice. Behaviour change belongs in
   M0LTE.Ardop, gated on its ardopcf loop tests.
4. **The A0 baseline ran ~150 Hz off-centre.** 48 of 52 cuts lock at trial centres
   1500-1580 (outliers 1680, 1760): wild session centres cluster at 1500-1560 audio, not
   the ~1650 the survey estimator suggested and A0 assumed. Our ±200 Hz tuning absorbed
   the offset for acquisition, but body decode degrades with residual offset (finding 2's
   recoveries). The corrected-centre rescan (addendum below) settled the number; the
   monitor default is now 1500.
5. **Net of the ConAck artifact, genuine per-frame disputes go 9-4 in our favour**
   (ours-ok/oracle-FAIL vs ours-FAIL/oracle-ok). On this corpus our receive chain is at
   parity with or slightly ahead of the reference implementation everywhere except the
   marginal-RS coin-toss region.

### Addendum: the corrected-centre rescan (2026-08-08)

The full corpus rescanned at `--centre 1500` (package 0.3.0, same instrument):

| | @1650 (A0) | @1500 (corrected) |
|---|---:|---:|
| acquired / ok | 636 / 551 | **749 / 605** |
| IDFrame | 65 / 60 | 84 / 74 |
| DataACK | 117 / 117 | 129 / 129 |
| 4PSK.500.100 | 38 / 26 | 69 / 26 |
| 16QAM.500.100 | 7 / 0 | 17 / 0 |
| 4FSK.500.100 | 84 / 50 | 90 / 47 |
| 8PSK.500.100 | 3 / 0 | 2 / 0 |

The 150 Hz miss was costing **acquisitions** - the loss A0 item 5 called invisible,
now measured at +113 frames (+18 %) and +54 bodies (+10 %). 4PSK.500.100 acquires 31
more frames at the same ok count (the weak direction of sessions previously only
half-heard). The top-rung sample more than doubles (17 16QAM.500 bodies) and still
decodes zero - the confirmed null, on a bigger corpus. Two honest negatives: the
**4FSK.500.100 body rate does not improve at the corrected centre** (50 -> 47 ok on
84 -> 90 acquired; the cut-level recoveries are threshold churn that moves both ways,
and the GB7BPQ beacon stays at the decode threshold regardless), and the outlier
sessions at 1680-1760 audio cut the other way (8PSK 3 -> 2, 16QAM.2000 1 -> 0: a
1760 Hz session is outside ±200 of 1500). Centre 1500 wins as the single default and
the monitor now defaults to it; the outliers are the reason a future multi-centre or
wider-tuning pass could still pay.

## A2: the ConAck reference-policy alignment (2026-08-08)

The one fix the autopsy indicated, approved on the recommended shape: accept a ConAck on
successful type decode; report its timing as null when the 2-of-3 byte majority is absent
(reference-compatible with deployed peers, which proceed on majority-less ConAcks, without
fabricating ardopcf's 0 ms). Shipped in **M0LTE.Ardop 0.4.0** (M0LTE.Ardop#3, tag v0.4.0
on merge 7616ce9, nuget-published via trusted publishing), with a loopback test pinning
the behaviour and the package's ardopcf oracle legs green (368/368 with `ARDOPCF` set).
This repo: `ArdopSourcePath` joined the local-checkout override pattern (the A/B iterated
without a package round trip) and the pin moved to 0.4.0.

Measured, bounded to the capture chunks both scans processed (the capture appends
continuously, so unbounded row counts differ by corpus growth, not behaviour):

- @1650: 634 aligned frames, **exactly 5 verdict flips, all ConAck500 fail -> ok** (551 ->
  556 bodies). The autopsy's five disputed frames, no other movement.
- @1500: 748 aligned frames, **exactly 8 flips, all ConAck500 fail -> ok** (604 -> 612).
  The corrected centre acquires more ConAcks, so more majority-less ones.

ConAck500 on the wild corpus is now 35 of 35 at the corrected centre. Nothing else moved
at either centre; the bench Rungs 0-2 and the full local suite stayed green throughout.

## Phases

- **A0 (this leg): the monitor instrument and the baseline.** Exit: the instrument decodes the
  positive controls, the baseline scoreboard is measured over the capture, and the residue is
  characterized. No fixes.
- **A1: autopsy the residue.** Done (2026-08-08, table above). The per-frame referee turned
  out stronger than wild retries: the reference implementation itself, run over bit-identical
  audio via the --emit seam. Result: top-rung null confirmed at parity, the 4FSK loss is one
  weak beacon emitter, the one 5-0 deficit is an ardopcf accept-anything policy, and the
  baseline's centre assumption was 150 Hz off.
- **A2: fixes, if any pay.** Done (2026-08-08, section above). Both autopsy candidates
  shipped: the ConAck reference-policy alignment (M0LTE.Ardop 0.4.0; 5 flips @1650, 8
  @1500, nothing else moved) and the corrected monitor default centre (1650 -> 1500,
  +18 % acquisitions). Ledger entry in docs/mode-validation.md (decode behaviour shipped).
- **A3: the sim seam.** Turn the validated wild corpus into a maskable ARDOP sim baseline
  (the rx-roadmap workstream 0 debt), so ARDOP joins the mask discipline the other modes have.

## Discipline (restated for this campaign)

- The bench oracle (ardopcf, Rungs 0-4) stays the interop ground truth; nothing here changes a
  transmitted bit or the wire protocol.
- Every measured claim reproduces from its command; the instrument's positive control ships
  with it and runs in CI.
- Honest negatives recorded with mechanism; the weaker wild ground truth stated wherever it
  bears on a conclusion.
- No aspiration in the baseline table - measured reality only.
