# G4 - the cold rung and the schedule rework on the 8PSK branch (MS110D Poor-gate successor program)

Registered 2026-08-21, on Tom's instruction, before any code.

## Registration

**Question.** With the MMSE cold rung (G2d) and the schedule rework (G2f: soft floor, hard-phase cycle members only, hard-first after a cycle-accept with common-model pricing, plateau handover) applied to the 8PSK branch of `Ms110dMfbBlockDecoder` - the second member of WN7's per-block ensemble (G1d) - does WN7 Poor stay at 0 / 0 on both families, does the ensemble still never select a wrong decode, and what does the MFB's per-block wall-clock do?

**Decision inputs (banked).** G1d: WN7 Poor 0 / 3,243,776 both families, AWGN WN7 0, the G1d battery's WN7 censuses (the WN7 byte-identity baseline), the nineteen contested blocks and their evidence margins, the 8PSK MFB's rung counts (10-40). G2d/G2f: on QAM16 the cold rung took every block to 4-7 rungs and the schedule rework closed the stragglers, with #316 shown to be the mechanism behind accepted soft oscillations. The WN7 w0/b0 ensemble burst shows one `r30c2` block under the old 8PSK schedule - a soft oscillation accepted at the handover, the #316 hole live on 8PSK too, harmless there only because the evidence rule did not select it.

**Mechanism claim.** None of the four changes is modulation-specific: the MMSE cold rung is a better start under any constellation, and the schedule changes decide only when to stop. The 8PSK MFB should converge in fewer rungs and offer the same or better candidates; the ensemble's evidence rule keeps wrong ones out as before.

**Instrument + method.** Remove the QAM16 scope gates (the code paths are already generic); corpses: the eight G1 WN7 specimens and the AWGN WN7 burst (107 w0/b0), plus the two WN8 specimens (must be byte-identical: the QAM16 branch is untouched); the five pins; isolated wall-clock of the two WN7 pins against G1d's 66.1 / 84.5 s; the full battery in the G0 form.

**Kill/proceed rule (pre-committed).** WN7 Poor 0 / 0 both families, AWGN WN7 0, every non-WN7 census byte-identical (WN8 at 12 / 18), pins at their digits, no ensemble selection producing an error -> ship; the WN7 censuses re-bank as the new baseline if they moved (a moved census with zero errors is a different path to the same decode, recorded, not drift). Any WN7 error, any wrong selection, or any non-WN7 census moved -> stop and diagnose; the 8PSK branch keeps the W5b2 schedule.

## Measurements

*(appended as the leg runs)*
