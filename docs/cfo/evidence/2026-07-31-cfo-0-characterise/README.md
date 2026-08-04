# CFO-0 - carrier-offset tolerance characterisation (issue #116 program)

Registered 2026-07-31, the #116 program's first leg (authorised by Tom this session), before any modem code changes. Program conventions follow the WN8 program's: registration before running, dated evidence dirs under `docs/cfo/evidence/`, one leg per decidable question, pre-committed reads, and the standing bar that acquisition changes to the NinoTNC-lineage modes must preserve NinoTNC interop (the bench rig lane, once the offered NinoTNC+CM108 host is live).

## Registration

**Question.** What is each audio-carrier mode's actual acquisition-vs-CFO curve, and what is the per-mode failure mechanism? The banked evidence (`docs/ms110d/evidence/2026-07-28-ssb-coverage/`, issue #116) established the wall qualitatively - afsk300/qpsk600 0/12 on-air, qpsk2400 0/6 with the offset nulled, bpsk1200 rides ±120 Hz - but no systematic curve exists, and the design choice per mode (offset-search acquisition, a bpsk300-style diversity bank, or tracking AFC) hangs on where and how each mode dies.

**Instrument.** A `--cfo <list>` sweep axis on `sm-ota sim`: the offset applied by `WattersonChannel.Apply`'s existing `frequencyOffsetHz` path (the D.6.4 rig - exact SSB spectrum shift via the analytic envelope; the ±2.2 kHz envelope passband covers every audio mode's occupancy), threaded through `SimChannel.Apply` → `SimBench.RunPoint` → the report/CSV. Deterministic per (mode, channel, SNR, cfo, seed). A permanent harness instrument, not throwaway.

**Method.** Per mode - `afsk300`, `qpsk600`, `qpsk2400` (the #116 trio), plus `bpsk1200` and `bpsk300` as the known-tolerant references - AWGN at a comfortably-above-threshold SNR (banked 90% knees + ~4-6 dB: afsk300 +12, qpsk600 +8, qpsk2400 +14, bpsk1200 +8, bpsk300 +2), CFO grid 0, ±5, ±10, ±20, ±40, ±80, ±120 Hz, 25 bursts/point, frame layer, TXDELAY 150 ms (the realistic run-in). Banked output: the per-mode success-vs-CFO curve (CSV + report).

**Pre-committed reads.** (1) The trio's 50% CFO half-widths - the quantitative wall the design legs must beat (target from #116's physics: ride the RSP1's tens-to->100 Hz drift → design bar ±100 Hz-class, set precisely in the design leg's registration from these curves). (2) The references must reproduce their banked tolerance (bpsk1200 ~±120 Hz; bpsk300's bank likewise) - validating the instrument against known behaviour before the trio's curves are believed. (3) Failure-mechanism notes per mode from the curve shapes (cliff vs slope; symmetric vs skewed - AFSK discriminator centring skews, correlation-acquisition cliffs).

**Budget.** ~40 lines of harness threading + one sweep session (~5 modes × 13 points × 25 bursts, minutes each on this box).

## Measurements (2026-07-31, [data/](data/); AWGN, 25 bursts/point, TXDELAY 150 ms, ±120 Hz grid with ±60/±100 refinement)

| Mode (SNR) | Clean through | Death | Shape / mechanism read |
|---|---|---|---|
| `bpsk1200` (+8) | **±120 Hz, flat 25/25 everywhere** | none in grid | the differential + 4-pair diversity bank - the reference reproduces its banked ride; the design existence proof lives in this repo |
| `bpsk300` (+2) | ±60 | **0/25 at ±80 and +100**, then 15-17/25 at ±120 | **a comb gap in the diversity bank**: branch spacing covers ~0-60 and ~±120 with a dead hole between - a discovered defect in a ✅-working mode |
| `afsk300` (+12) | ±40 | sloping 60→120 (16→0/25), roughly symmetric | discriminator-margin rolloff, not a cliff; tolerance is SNR-dependent (the on-air failures were at threshold SNR) |
| `qpsk600` (+8) | **0 Hz only - 0/25 at ±5** | ±5 | a brick wall: coherent acquisition with no offset search at all; also the obvious mechanism behind #11's marginal QtSoundModem interop |
| `qpsk2400` (+14) | ±5 | ±10 partial (6-7/25), 0 by ±20 | half-width ≈ ±8 Hz - why on-air failed even offset-nulled (intra-session drift of a few Hz exceeds it) |

Instrument validated by read (2): both references reproduce their banked behaviour (bpsk1200 flat; bpsk300's bank rides where its branches sit). The `--cfo` axis is now a permanent `sm-ota sim` instrument with report and CSV columns.

## Verdict

The wall is quantified and per-mode mechanisms are read. Design targets for the next leg (registered there, from these curves + #116's physics): **qpsk600 needs ≥×20 widening** (±5 → ±100-class), **qpsk2400 ≥×12** (±8 → ±100-class), **afsk300 ~×2-3** (±40 → ±100-class, plus the SNR-dependence characterised), and **bpsk300's comb gap closes** (branch placement - cheap). The in-repo existence proof (bpsk1200's bank) says ±120-class is achievable at these symbol rates; the design choices per mode - bank vs offset-search vs AFC - are the next leg's registered question, with the NinoTNC bench as the interop guard once live.
