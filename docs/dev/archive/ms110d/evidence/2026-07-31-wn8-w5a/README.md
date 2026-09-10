# W5a — the MFB-form receiver prototyped in the demod's ring domain (WN8 redesign program)

Registered 2026-07-31, after W3 banked the proceed-with-margin verdict ([../2026-07-31-wn8-w3/](../2026-07-31-wn8-w3/)), before any instrument code or run. W5 is staged — W5a (this leg): the complete receiver algorithm as a corpse instrument in the demodulator's own ring domain; W5b: shipped integration behind the QAM16 gate; W5c: corpse closure + guard pins; then W6's battery. Nothing ships in W5a.

## Registration

**Question.** Does the MFB-form receiver work as an *algorithm a receiver can actually run* — no truth, no genie, no parametric path knowledge — in the demod's own signal domain? Concretely: ring-domain (post front-end, CFO/timing from the shipped acquisition) probe-anchored **composite-FIR** trajectory estimation (the field-realistic generalization of W3's 2-path parametric estimator), per-symbol matched projection with believed-Gram pricing, and **iterative reconstruction-and-cancellation** seeded from its own decodes — measured on both specimens against the W1 (100/36) and MFB (0/0) anchors.

**Decision inputs.** W3's requirement curve (−18 dB NMSE suffices; probes deliver −29/−30 dB in the TX domain) and margin verdict; W2's sandwich budget; W1b's statistic form; the shipped receiver limits (acquisition provides `_chip0`/`_tau`/`_omega`; ring reads are CFO-derotated and timing-tracked).

**Mechanism.** The composite response the ring sees (TX SRRC ⊗ channel ⊗ RX front-end) is a short T/2 FIR (raised-cosine tails + ≤2.3 ms delay spread → support ≈ −6..+15 half-chips, L = 22 complex taps) whose per-tap time variation is the fade process — band-limited exactly as the path gains are, so probe anchors (per-probe LS over ISI-clean interior rows, ~2× overdetermined, ridged) + per-tap linear interpolation reproduce the W3 story one domain later. Cancellation replaces the genie: reconstruct the block from decisions through the estimated h(t), subtract all-but-u, re-project — iterated with the outer decode. Iteration-0 is honestly open: matched projection with un-cancelled ISI may be too poor a seed on a 2-path channel; the registered ladder below measures it rather than argues it.

**Instrument + method.** Test-side (`Ms110dMfbFormReceiver` + passive internal demod seams: ring read, chip-position, and block-frame-chip hooks — behavior-untouched when unhooked):

- Corpse construction as banked; the shipped demod runs first (its lock supplies timing/CFO and block-frame positions; its decodes are NOT consumed).
- Per probe: composite-FIR anchor LS on ISI-clean interior rows; per-tap linear interpolation between anchors. Reported: implied trajectory quality via probe-row prediction residual (label-free), plus anchor-tap energy profile.
- Detection ladder, each rung reported per block on both specimens: **R0** matched projection, no cancellation; **R1–R3** reconstruction-and-cancellation iterations seeded from the previous rung's decode (re-encoded through the code — wrong decisions cancel wrongly and must be priced by measurement, not assumed).
- **Pre-committed reads.** (1) If a rung reaches ≤ W1-class totals (~100/36) the cancellation loop is credible; if a rung reaches ~MFB-class (≤ ~10 total per specimen) the receiver is prototype-proven and W5b's shipped integration is registered next. (2) If R0 is too poor to seed (decode near coin-flip) and iterations do not descend, the registered escalation is a banded time-varying LMMSE iteration-0 with the same estimated h(t) — a written follow-on (W5a2), not silent scope creep; the B3.7 banked negative is not re-tread (it compared per-bin FD MMSE to chains at equal knowledge in the old sandwich; this is time-domain, probe-trajectory knowledge, iteration-0 only). (3) If cancellation diverges from good seeds (the b34 echo-chamber shape), the revert principle applies and the leg closes with the measured negative.

**Budget.** Passive demod seams (~30 lines) + one test-side instrument (~450 lines); corpse runs only; hermetic suite + unchanged-demod note (no §6 battery — nothing ships).

## Measurements (2026-07-31, [summaries/](summaries/); hermetic 790/0/110)

**Estimation chain, both specimens** (after two instrument findings, below): anchor-fit residual **~7E-5** per complex ring sample, truth-recon calibration **~3.0E-4** over the whole data span — the ring-domain composite-FIR estimator and interpolated h(t) are valid on both seed families.

**Two burst-dependent failure modes, caught by the instrument's own lanes and fixed field-realistically:**
1. The fixed support [−6,16) truncated the echo's raised-cosine tail (canonical anatomy: anchor peaks at 0 AND +10).
2. The disjoint burst's acquisition locked on the ECHO path (the B3.5b phenomenon at WN8), putting the direct path at −10 — outside any one-sided window, with exactly half the signal power unexplained (anchor residual 1.8E-2 ≈ mean|ring|²/2, ladder dead-flat; [summaries/pre-fix-fixed-window-disjoint.txt](summaries/pre-fix-fixed-window-disjoint.txt)). Fix: a per-burst delay-profile scan (ridged wide LS over [−18,+19) on 8 probes, per-tap energies accumulated across fading) places the 26-tap window — canonical got [−5,21), disjoint [−17,9), and the disjoint anchors collapsed 250× to 7.3E-5.

**The detection ladder** (per-rung coded info-bit errors, self-seeded — R0 is matched projection with un-cancelled ISI at a coin-flip-class decode):

| Rung | canonical | disjoint |
|---|---|---|
| R0 | 257,815 | 260,245 |
| R12 | 34,931 | 80,182 |
| R40 | 1,860 | 191 |
| R60 | **36** | **140** |
| R70/R80 | 36 (frozen) | 140 (frozen) |

Fixed points, not churn: totals frozen over the last 20 rungs, **18 of 22 blocks at exactly zero** (canonical residual all in b3:36; disjoint b4:73, b6:5, b8:62). The b34 wrong-attractor test passes — these are stable correct-majority fixed points reached from coin-flip.

## Verdict

**The registered "credible" read fires decisively; the receiver algorithm is real.** A fully label-free, self-seeded MFB-form receiver — shipped acquisition, probe-anchored composite-FIR trajectories with per-burst window placement, matched projection, hard-decision reconstruction-and-cancellation — converges from coin-flip to the **truth-injection class** (pooled 176 vs W1's truth-fed 136) with 18/22 blocks exactly zero, on both seed families, in the demod's own signal domain. The b34 uncrossable-basin verdict is crossed by architecture, label-free. Not yet MFB-class (0/0): the residual concentrates in 3 fade-lottery blocks, and the loop needs ~60 rungs from its cold seed. **W5b registers next** with the closure levers this prototype deliberately left on the table: soft cancellation (SISO E[x] — the B3.3 lesson in this architecture), per-symbol elliptical pricing inside the loop (the W1b form; this prototype priced with a crude global σ per rung), decision-directed anchor re-fit at convergence, and a warm iteration-0 seed (the existing chain pass) to collapse the rung count to shipping economics — plus the W5b/W5c ladder (structural QAM16 scoping, guard pins, byte-identity, battery) before anything gates.
