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

## Phases

- **A0 (this leg): the monitor instrument and the baseline.** Exit: the instrument decodes the
  positive controls, the baseline scoreboard is measured over the capture, and the residue is
  characterized. No fixes.
- **A1: autopsy the residue.** One experiment per class of miss - weak-leader acquisition on the
  far side, a bandwidth class that never decodes, a frame type that acquires but fails RS. Each
  measured against the raw audio, each a recorded number or null. Where a wild session's own
  retries give a second copy, use it as the per-frame referee the band otherwise lacks.
- **A2: fixes, if any pay.** Receive-side only, each with its A/B against the wild corpus and
  the bench rig's Rungs 0-2 kept green (interop is still ground truth). A monitor improvement
  that decodes more of the wild band without regressing the bench oracle.
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
