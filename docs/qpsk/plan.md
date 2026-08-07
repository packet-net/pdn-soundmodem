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

Measured 2026-08-07, N=100/point, seed 1, `sm-ota sim` (the same rig the masks run).
CFO rows are at the mode's knee SNR (+4 for qpsk600, +9 for qpsk2400).

| Mode | Detector | Channel | Points (SNR dB: ok/100) |
|---|---|---|---|
| qpsk600 | differential | AWGN | 0: 6, +2: 80, +4: 99, +6: 100 |
| qpsk600 | differential | CFO @+4 | 3.75 Hz: 95, 7.5: 92, 15: 30, 30: 0 |
| qpsk600 | differential | Good | +4: 52, +8: 68 |
| qpsk600 | differential | Moderate | +4: 26, +8: 41 |
| qpsk600 | differential | Poor | +8: 4, +12: 5 |
| qpsk600 | coherent | AWGN | 0: 52, +2: 81, +4: 89, +6: 98 |
| qpsk600 | coherent | CFO @+4 | 3.75 Hz: 0, 7.5: 0, 15: 0, 30: 0 |
| qpsk600 | coherent | Good | +4: 58, +8: 66 |
| qpsk600 | coherent | Moderate | +4: 30, +8: 41 |
| qpsk600 | coherent | Poor | +8: 7, +12: 7 |
| qpsk2400 | differential | AWGN | +5: 0, +7: 15, +9: 80, +11: 99 |
| qpsk2400 | differential | CFO @+9 | 3.75 Hz: 72, 7.5: 76, 15: 71, 30: 58 |
| qpsk2400 | differential | Good | +9: 20, +13: 35 |
| qpsk2400 | differential | Moderate | +9: 2, +13: 10 |
| qpsk2400 | differential | Poor | +13: 2, +17: 3 |
| qpsk2400 | coherent | AWGN | +5: 3, +7: 36, +9: 58, +11: 74 |
| qpsk2400 | coherent | CFO @+9 | 3.75 Hz: 33, 7.5: 4, 15: 0, 30: 0 |
| qpsk2400 | coherent | Good | +9: 19, +13: 26 |
| qpsk2400 | coherent | Moderate | +9: 3, +13: 9 |
| qpsk2400 | coherent | Poor | +13: 1, +17: 3 |

**The loss surface, named (Q0 exit).**

1. **qpsk600's low-SNR knee is detector-bound.** Coherent beats differential by ~1.5-2 dB
   at the bottom (52 vs 6 at 0 dB) - the classic single-sample differential give-away,
   exactly the gap the bpsk300 DF-DD reference closed. The DF-DD port (a 4-phase
   decision-remodulated reference) is the headline Q2 candidate.
2. **qpsk600's CFO wall sits at ~+-10-15 Hz** (95 % at 7.5 Hz, 30 % at 15, 0 at 30) - no
   diversity bank, no seeding, and a differential product whose phase margin is half
   BPSK's. Coherent's wall is at 0 Hz for both modes, re-confirming the 2026-07-31
   default reversal. The offset window already computed for reporting (4th-power) is the
   seeding source, as it was on BPSK.
3. **qpsk2400 on real-channel fading is near-floor** (Moderate 2-10 %, Poor 2-3 % at
   +13-17 dB): at 1200 Bd the CCIR echoes span 1.2-2.4 symbols - genuine equaliser
   territory, where the WS5 machinery's lessons (and its measured causal limits,
   docs/rx-roadmap.md workstream 5) both apply. Recorded as the hard end of the campaign,
   not the first target.
4. **The whole family's knees carry several dB of implementation loss** against the
   matched-filter expectation (qpsk600 differential reaches 90 % near +3 dB SNR3k where
   ideal DQPSK at these rates sits several dB lower; the #236-class chain suspects -
   decode-path band-pass, unmatched low-pass, DPLL inertia - are all present and
   unmeasured here). Q1's autopsies put a number on each.
