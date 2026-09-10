# G4 - the cold rung and the schedule rework on the 8PSK branch (MS110D Poor-gate successor program)

Registered 2026-08-21, on Tom's instruction, before any code.

## Registration

**Question.** With the MMSE cold rung (G2d) and the schedule rework (G2f: soft floor, hard-phase cycle members only, hard-first after a cycle-accept with common-model pricing, plateau handover) applied to the 8PSK branch of `Ms110dMfbBlockDecoder` - the second member of WN7's per-block ensemble (G1d) - does WN7 Poor stay at 0 / 0 on both families, does the ensemble still never select a wrong decode, and what does the MFB's per-block wall-clock do?

**Decision inputs (banked).** G1d: WN7 Poor 0 / 3,243,776 both families, AWGN WN7 0, the G1d battery's WN7 censuses (the WN7 byte-identity baseline), the nineteen contested blocks and their evidence margins, the 8PSK MFB's rung counts (10-40). G2d/G2f: on QAM16 the cold rung took every block to 4-7 rungs and the schedule rework closed the stragglers, with #316 shown to be the mechanism behind accepted soft oscillations. The WN7 w0/b0 ensemble burst shows one `r30c2` block under the old 8PSK schedule - a soft oscillation accepted at the handover, the #316 hole live on 8PSK too, harmless there only because the evidence rule did not select it.

**Mechanism claim.** None of the four changes is modulation-specific: the MMSE cold rung is a better start under any constellation, and the schedule changes decide only when to stop. The 8PSK MFB should converge in fewer rungs and offer the same or better candidates; the ensemble's evidence rule keeps wrong ones out as before.

**Instrument + method.** Remove the QAM16 scope gates (the code paths are already generic); corpses: the eight G1 WN7 specimens and the AWGN WN7 burst (107 w0/b0), plus the two WN8 specimens (must be byte-identical: the QAM16 branch is untouched); the five pins; isolated wall-clock of the two WN7 pins against G1d's 66.1 / 84.5 s; the full battery in the G0 form.

**Kill/proceed rule (pre-committed).** WN7 Poor 0 / 0 both families, AWGN WN7 0, every non-WN7 census byte-identical (WN8 at 12 / 18), pins at their digits, no ensemble selection producing an error -> ship; the WN7 censuses re-bank as the new baseline if they moved (a moved census with zero errors is a different path to the same decode, recorded, not drift). Any WN7 error, any wrong selection, or any non-WN7 census moved -> stop and diagnose; the 8PSK branch keeps the W5b2 schedule.

## Measurements (2026-08-21; [corpses/](corpses/), [pins/](pins/), [battery/](battery/))

- **Corpses:** the eight WN7 Poor specimens and the AWGN WN7 burst all **0 coded**, with the ensemble's selection counts exactly as at G1d (2, 2, 1, 1, 0, 2, 3, 2; AWGN 0) - the 8PSK MFB offers the same winners; every MFB block now terminates at `r11c1` (the soft floor's ten rungs plus one hard confirm) where the W5b2 schedule took 10-40 and left one `r30c2` cycle-accept on the w0/b0 burst. The two WN8 specimens 0 / 0 (the QAM16 branch is untouched).
- **Pins:** all five at their digits (WN7 w0/b0 0 / 11c/0r/4v / oracle b5:15 / selected 0; WN7 w1/b0 0 / selected 2; WN6, WN13sp, WN0 exact).
- **Battery** (33 legs, 06:39 to 07:24): Poor WN7 **0 / 3,243,776 both families**, AWGN WN7 0; Poor WN8 12 / 18, AWGN WN8 0; every other point at its digits; **120 of 120 censuses byte-identical** to their baselines (G1d for WN7, G2f for WN8, G0 for the rest) - the 8PSK branch's new start and schedule change no decode the ensemble emits. All lanes rc=0 (WN7 and WN8 both under their armed gates).
- **Wall-clock, isolated:** WN7 w0/b0 with the oracle pass 67.3 s against G1d's 66.1; w1/b0 87.7 against 84.5 - flat: the MFB's fewer rungs and the MMSE start's per-block cost cancel on 8PSK.
- Full hermetic suite 1794 / 0; `SourceTextTests` green.

## Verdict

**Ship.** The 8PSK branch carries the same receiver as the QAM16 branch; WN7 is byte-identical in outcome with a simpler, bounded schedule, and #316 is closed on both branches. No row moves; the WN7 baseline stays G1d's (the censuses are identical to it).
