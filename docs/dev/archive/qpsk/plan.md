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

  **Q1-1, band-pass bypass alone (2026-08-07): a recorded near-null with a lesson about
  experiment order.** Feeding the differential path raw audio instead of the band-passed
  sample moved qpsk600 not at all (6/80/99 -> 7/77/98 across 0/+2/+4) and qpsk2400 +5
  only at its bottom rung. The bpsk300 prior (~1.5 dB) was measured against a MATCHED
  filter; against QPSK's unmatched 0.75-baud low-pass the band-pass ripple is lost in the
  larger mismatch. The autopsy order is therefore matched-filter-first.

  **Q1-2, RRC matched filter (2026-08-07): the dominant chain fix, ~+1-1.5 dB on both
  modes.** Replacing the differential path's 0.75-baud low-pass with an RRC matched to
  the modulator's 0.35 roll-off, the 2x2 with the band-pass measured (N=100, seed 1,
  AWGN):

  | Configuration | qpsk600 0/+2/+4 dB | qpsk2400 +7/+9/+11 dB |
  |---|---|---|
  | LPF + BPF (Q0 baseline) | 6 / 80 / 99 | 15 / 80 / 99 |
  | LPF, BPF bypassed | 7 / 77 / 98 | 20 / 80 / 98 |
  | RRC, BPF bypassed | **40 / 90 / 100** | 41 / 88 / 98 |
  | RRC + BPF | 33 / 88 / 100 | **42 / 89 / 99** |

  With matching in place the band-pass cost becomes visible exactly where the bpsk300
  story predicts, and only there: qpsk600's deepest rung (40 vs 33 - its +-300 Hz passband
  ripples across the 300 Bd signal), while qpsk2400's +-1200 Hz band-pass is benign. Q2
  landing shape: the bpsk300 arrangement - matched filter on the decode path, band-pass
  kept for EnergyBusy gating only - on the differential path, coherent untouched.

  **Q1-3, DPLL inertia 0.92 (2026-08-07): another ~1 dB on both modes, measured on top of
  Q1-2.** The Dire Wolf 0.74 inertia costs the same timing jitter here as it did on
  bpsk300. RRC+BPF+0.92, AWGN, N=100 seed 1: qpsk600 0/+2/+4: **66 / 99 / 100** (from
  33/88/100); qpsk2400 +7/+9/+11: **72 / 97 / 100** (from 42/89/99). Cumulative from the
  Q0 baseline: qpsk600's 0 dB rung 6 -> 66, qpsk2400's +7 rung 15 -> 72 - roughly
  2-2.5 dB banked from two chain fixes, the #236 playbook transferring nearly
  number-for-number. Caveat before landing: bpsk300 measured 0.96+ failing its 24-bit
  minimum preamble - the Q2 gate re-runs acquisition (short TXDELAY), the CFO sweep, the
  fading rows and the interop suites (NinoTNC corpus 9/9, QtSM, parity) before any of
  this ships. Still unprobed from the priors table: the DF-DD reference (the remaining
  ~1.5-2 dB detector gap Q0 measured against coherent) and offset seeding for the CFO
  wall.
- **Q2 - science core.** Fixes land one at a time, each with its full-ladder A/B quoted in
  the PR and its interop gates green (NinoTNC corpus 9/9, QtSM suite, parity tests). No
  fix lands on aspiration.

  **Fix-set 1 (2026-08-07): matched filter + decode-path band-pass removal + DPLL
  inertia, landed together as one chain change.** The differential path gets an RRC
  matched to each mode's own TX roll-off (0.20/0.35/0.25 - the Q1 probe had hardcoded
  0.35, and matching qpsk600 properly bought a further ~3 points at its deepest rung),
  raw audio into the decode chain with the band-pass gating EnergyBusy only, and
  inertia 0.92. Full ladder, N=100 seed 1, differential (Q0 baseline -> fix-set 1):

  | Channel | qpsk600 | qpsk2400 |
  |---|---|---|
  | AWGN | 0 dB: 6->69, +2: 80->99, +4: 99->100, +6: 100->100 | +5: 0->3, +7: 15->72, +9: 80->97, +11: 99->99 |
  | CFO @knee | 3.75 Hz: 95->100, 7.5: 92->99, 15: 30->71, 30: 0->0 | 3.75: 72->95, 7.5: 76->96, 15: 71->94, 30: 58->84 |
  | Good | +4: 52->57, +8: 68->74 | +9: 20->24, +13: 35->34 |
  | Moderate | +4: 26->26, +8: 41->41 | +9: 2->8, +13: 10->13 |
  | Poor | +8: 4->7, +12: 5->10 | +13: 2->2, +17: 3->2 |

  ~2.5 dB at qpsk600's AWGN knee and ~2 dB at qpsk2400's; qpsk2400's CFO dip eliminated;
  fading rows barely move, exactly as the Q0 loss surface predicted (equaliser territory).
  Three pinned expectations moved WITH this landing, each an improvement outrunning its
  fixture: the coherent-beats-differential gate's QPSK rows joined the BPSK rows in the
  inverted matches-coherent gate; the SSB scorer's sub-cliff rung moved 0 -> -6 dB
  (qpsk600 now decodes 69/100 at 0 dB); and the qpsk600/qpsk2400 AWGN smoke masks rose
  30 -> 36 and 32 -> 36 of 40. Ledger entry: mode-validation.md 2026-08-07.

  **Fix-set 2 (2026-08-07, later): the four-phase decision-feedback reference with seeded
  rotation - a CFO-robustness landing, an AWGN null, and two findings the first cut paid
  for.** The BPSK DF-DD port (absolute-quadrant decisions against a ~20-symbol
  decision-remodulated reference, per-burst rotation seed from the fourth-power offset
  window, decision-directed tracker) measured, on its final shape: qpsk600 CFO at +8 dB
  with a realistic 150 ms TXDELAY **15 Hz: 71 -> 97 %, 30 Hz: 0 -> 38 %** (the first
  configuration ever to decode this mode at 30 Hz); AWGN, fading and knee-SNR CFO rows
  all identical to fix-set 1 within confidence intervals - the reference's noise-margin
  contribution on AWGN is a measured NULL for QPSK, recorded as such. The two findings:
  (1) **the seed gates the handover, not just the rotation** - a first cut that let the
  reference decide from cold scored 60 % at a mere 8 Hz at +8 dB (0 % at 15 Hz), because
  an unseeded reference must drag its phase through the offset by decision-remodulation
  alone and QPSK gives it half the preamble and half the margin BPSK had; until the seed
  fires the plain product decides and the reference only warms up, so a burst too weak to
  seed keeps exactly the fix-set-1 detector. (2) **qpsk3600 is scoped out after a
  measured regression** (22/30 vs the product's 30/30 on a clean signal - the FM chain at
  6 2/3 samples per symbol was never in the SSB campaign's scope); `decisionFeedback:
  false` pins it to the detector it was validated with, and the demodulator parameter
  documents why. Full suite green (1574); coherent untouched; wire unchanged.
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

## Closeout (2026-08-07)

The campaign ran Q0 through Q2 in one day and merged as PR #245; this section closes Q3
and Q4 in the MS110D closeout style.

- **Q3, family closure.** qpsk600 and qpsk2400 sit at their new measured floors (the Q2
  tables above), pinned by the raised smoke masks and the inverted
  differential-matches-coherent gate. qpsk3600 was re-validated at its ORIGINAL
  configuration: fix-set 1's matched filter reaches it and its gates stayed green;
  fix-set 2's decision-feedback reference measurably regressed its FM chain and is scoped
  out with `decisionFeedback: false` at the factory - re-opening that is the first task of
  any future qpsk3600 campaign, which also owns the FM channel model (rx-roadmap
  workstream 6 item 6). No live cross-check exists for any QPSK mode: the 40 m capture
  carries afsk300/bpsk300/ARDOP only, so on-air confirmation waits for a QPSK slot to be
  monitored or worked; the NinoTNC corpus (9/9) and QtSM interop suites stand as the
  interop evidence meanwhile.
- **Q4, the gate flip.** Both mask moves and both fixture inversions shipped inside the
  landing commits with their mode-validation.md entries (2026-08-07, two entries) - there
  was no separate flip to perform, which is the discipline working as intended.
- **What the campaign leaves open, honestly ranked:** the qpsk fading rows barely moved
  and remain equaliser territory (the workstream 5 causal-limit findings apply doubly at
  1200 Bd, where CCIR echoes span 1.2-2.4 symbols); the CFO tail beyond ~20 Hz on qpsk600
  is seed-timing-bound at short TXDELAY (38 % at 30 Hz with a realistic preamble, 0 %
  without); and a diversity bank for the QPSK family - BPSK's remaining structural
  advantage - was neither needed for these gains nor measured, and is the obvious next
  lever if the live network ever shows QPSK stations arriving further off-frequency than
  the seed covers.

Net: ~2.5 dB (qpsk600) and ~2 dB (qpsk2400) at the AWGN knees, CFO robustness roughly
doubled with the dip eliminated, one measured null (DF-DD noise margin on QPSK) and one
measured scope-out (qpsk3600's FM chain) on the record, at zero transmitted-bit change.

## Postscript, 2026-08-08: qpsk3600's clean-signal weakness has a cause

The campaign scoped `qpsk3600` out of the decision-feedback reference after measuring a
clean-signal regression peculiar to it "at 6 2/3 samples per symbol" (Q2). That ratio was the
cause, not a coincidence, and the FM channel model made it visible: the mode decodes *worse* as
the signal gets stronger - 30/30 at +18 dB CNR, then ~70-77 % at +24 to +40 - while every other
PSK mode, all of which have integer samples-per-symbol, is flat. Re-running qpsk3600 at rates that
make the ratio integer removes it (30/30 at 20 samples/symbol on both AWGN and the FM path).

QtSoundModem avoids the whole problem by construction: `make_core_INTR` interpolates each PSK mode
by `n_INTR = baud/300` (6 for Q3600) and its sampler hardcodes 300 baud, so every mode decides at
exactly 40 samples per symbol. The fix here is internal interpolation in `QpskDemodulator`, because
no divisor of 48 kHz gives an integer ratio at 1800 baud. See docs/mode-modulation-reference.md and
the mode-validation.md entry of 2026-08-08 (later6).

## Postscript, 2026-08-21: the bank, the always-deciding reference, and the timing fixes (issue #326)

The "obvious next lever" of the closeout arrived as issue #326, with a real off-air `qpsk600`
burst at +1.7 dB that the shipping receiver could not copy (`samples/offair/2026-08-21/`). The
work is recorded in docs/mode-validation.md (2026-08-21, later3) and docs/plan.md's amendment
log; the findings that revise this document's record are:

- **Fix-set 2's "AWGN null" had a mechanism.** The handover was gated on the fourth-power offset
  window turning coherent, and on a real QPSK payload at the knee that window is coherent through
  the all-reversal preamble and incoherent through the whole payload (the fourth power of a noisy
  phasor is far noisier than the square BPSK gets away with), so the reference never decided the
  part of the burst that matters. Decided from cold, as BPSK's does, the reference is worth its
  theoretical root-two on the decision noise once the tracker's gain is dropped from 0.05 to 0.01
  after DCD asserts (at 0.05 the tracker's own jitter gave it all back).
- **The knee was timing-bound, not detector-bound.** With the truth known, a fixed clock at the
  right phase decodes the 0 dB synthetic burst with zero wrong bytes where the DPLL left six; the
  sub-sample crossing interpolation BPSK's DPLL always had (QPSK passed zero) and inertia 0.94
  are worth ~1.5 dB on qpsk2400 and a few points on qpsk600; 0.96 loses the 16-symbol
  zero-TXDELAY acquisition exactly as #236 found at its 24-bit minimum.
- **Finding (1) of fix-set 2 stands, with its cause named:** the reference failed at 8 Hz from
  cold because the rotation tracker was dormant until the seed. Running it from the first symbol,
  clamped to one bank step, and de-rotating the product it feeds the DPLL with, a single branch
  now copies +-30 Hz at +4 dB where the campaign measured 3 %, and the bank takes the family's
  carrier-offset tolerance to its comb (+-30 Hz at 300 Bd, +-120 Hz at 1200 Bd) plus the seed.
- **Finding (2), qpsk3600's scope-out, is re-measured on the FM ladder in the #326 PR** (the
  interpolated-instant reference is now applied to the sample the reference decides on, not only
  to the product).
- **The fixture itself does not copy**, and its README now records why in wrong bytes: a 70 ms
  damaged stretch at the start of the payload leaves 8 bad bytes at the best possible clock phase
  (the RS limit) and 10 at the recovered clock.
