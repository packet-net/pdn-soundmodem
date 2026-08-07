# QPSK receive campaign

Opened 2026-08-07 on Tom's direction, after the bpsk300 receive campaign measured its own
stopping rule (docs/rx-roadmap.md): the station reads ~94 % of its slot's real activity and
the residue is structurally out of reach, while the QPSK family - qpsk2400/qpsk3600, the
live NinoTNC network's primary modes - still runs the pre-#236 receive chain at known-poor,
uncampaigned mask levels. This campaign ports the measured bpsk300 lessons to the QPSK
family under the MS110D programme's discipline, which is the point of this document:
phased, instruments-first, one experiment per loss, masks from measured reality, every
claim reproducible from (mode, channel, SNR, seed, count).

## Ground truth and scope

- Wire format: NinoTNC IL2P+CRC, differentially-encoded QPSK (V.26A constellation).
  **Spec + NinoTNC behaviour is ground truth**; the 2026-07-31 studybox corpus
  (`docs/bench/ninotnc-corpus-2026-07-31.md`, 9/9 QPSK files) and the QtSM interop suite
  are the regression gates. Nothing here changes a transmitted bit.
- In scope: `qpsk600` (300 Bd) and `qpsk2400` (1200 Bd), the SSB pair the Watterson rig
  can drive. `qpsk3600` is FM; it inherits any symbol-chain improvements mechanically but
  its channel-model work stays with rx-roadmap workstream 6's FM item.
- Detector: differential is the deliberate default (2026-07-31 reversal, catalogue doc) -
  V.26A is differentially encoded by construction and coherent measured 0-2 of 3 real
  frames against differential's 9/9. Coherent stays as the cross-check variant; the
  campaign baselines BOTH.

## The inherited priors (what #236 measured on bpsk300)

Each is a candidate loss here, and each gets its own autopsy before its fix:

| Chain stage | bpsk300 measured cost | QPSK chain today |
|---|---|---|
| Band-pass fronting the decode path | ~1.5 dB (breaks matched-filter condition) | present (`QpskDemodulator` BPF ~2x baud) |
| Low-pass not matched to TX shaping | ~0.5 dB | present (0.75x baud LPF, QtSM-style) |
| DPLL inertia 0.74 | ~1 dB timing jitter at the RS threshold | present (0.74) |
| Single-sample differential decision | ~1 dB (the classic differential give-away) | present (per-sample conjugate product) |
| No offset seeding / reference rotation | mid-branch CFO nulls | offset window exists for REPORTING only |

The priors are priors, not conclusions: QPSK's 2 bits/symbol halve the phase margins, so
each number must be re-measured here - the MS110D Phase B lesson (equalizer heuristics
fitted to one rig do not transfer) applies to receive chains too.

## Phases

- **Q0 - instruments and baselines (open).** Deterministic full-grid baselines for both
  modes, both detectors: AWGN ladder about each mode's knee, CFO sweep at the knee, CCIR
  Good/Moderate/Poor. N=100 pins at seed 1 recorded here; the existing smoke-mask rows
  (qpsk600 awgn +4 floor 30, qpsk2400 awgn +11 floor 32) stay the CI floor. Exit: the
  baseline table below is full and the loss surface is named (which channels are
  detector-bound vs timing-bound vs equaliser-bound).
- **Q1 - autopsies.** One isolation experiment per suspected loss (the table above), each
  a measured A/B on the sim ladder with the change reverted afterwards. Exit: each loss
  has a number or a recorded null.
- **Q2 - science core.** Fixes land one at a time, each with its full-ladder A/B quoted in
  the PR and its interop gates green (NinoTNC corpus 9/9, QtSM suite, parity tests). No
  fix lands on aspiration.
- **Q3 - family closure.** qpsk600 and qpsk2400 both at their new measured floors;
  qpsk3600 re-validated on the FM loop for chain regressions; the 40 m capture replay
  cross-checks any mode the live station carries.
- **Q4 - gate flip.** Mask rows move up to the new measured reality with their
  mode-validation.md ledger entry; closeout note appended here, MS110D-style.

## Discipline (the MS110D rules, restated for this campaign)

- Masks are measured reality, never aspiration; a mask moves only with a ledger entry.
- Every point is deterministic in (mode, channel, SNR, CFO, seed, bursts) - a failure
  that does not reproduce is not a result.
- Audit the instruments as hard as the code: a gate that can pass vacuously is a defect
  (the MS110D B0 lesson), and the first use of any new instrument includes a positive
  control.
- Honest negatives are results and get recorded with their mechanism, not deleted.
- The accept discipline from CLAUDE.md applies to every receive-path PR.

## Q0 baseline table

(filled by measurement; empty cells are unmeasured, never assumed)

| Mode | Detector | Channel | Points (SNR dB: ok/N) |
|---|---|---|---|
