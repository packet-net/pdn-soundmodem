# G1d - the WN7 ensemble ships: DFE-chain + MFB per 8PSK block, residual selection (MS110D Poor-gate successor program)

Registered 2026-08-20 after G1c's PASS ([../2026-08-20-poorgate-g1/](../2026-08-20-poorgate-g1/)), per [poor-gate-successor-plan.md](../../poor-gate-successor-plan.md) §3/§4, before any shipped-path code.

## Registration

**Question.** Does the shipped receiver, running the MFB-form block decoder beside the DFE-chain path on every 8PSK block and keeping the decode with the lower label-free reconstruction residual, bring WN7 Poor to the D-LXIV mask on both seed families at full §5.3 budgets, with every non-8PSK point byte-identical?

**Decision inputs (banked).** G1: the two receivers are never wrong on the same block (0 of 88), and the MFB reconstruction residual selects a zero-error decode on all 88 (18 contested, margins +0.17 % to +22 %). G0: WN7 83/3,243,776 canonical and 48/3,243,776 disjoint; the eight bursts that carry them; the 120-census byte-identity baseline; the five §6 guard pins (WN7 w0/b0 0 coded, WN7 w1/b0 20 coded - both expected to move under this change, the other three exact by structural scope). W5b2: the shipped `Ms110dMfbBlockDecoder` (caps SoftCap 30 / TotalCap 72, refit gate 5 %, cycle-accept) and its measured wall-clock class.

**Mechanism claim.** The chain path's WN7 residual is deep-fade decoder events (1-32 info bits, honest low-|LLR| wire bits); the MFB's is basin traps (confident wrong fixed points) on different blocks. Each re-encodes to wire symbols the other receiver's trajectory does not explain, so the block's reconstruction residual through the MFB's probe-anchored trajectory prices a wrong decode above a right one by the energy of its wire-symbol difference, with a cross-term noise far below that energy at every contested block measured.

**Instrument + method.**
1. `Ms110dMfbBlockDecoder` takes the modulation-generic symbol map the prototype proved (calibration lane (i): its QAM16 arithmetic must stay operation-identical; the WN8 corpses 509/10509 and the WN8 battery points must reproduce 112/32 and 1,254/75,713 byte-identically) and gains `Price(info)`: the mean reconstruction residual of a given block decode through the block's final anchor set.
2. `Ms110dDemodulator`: structurally scoped to `Psk8` blocks that satisfy the existing turbo gate, after the chain path's decode is final (converged, salvaged or reverted): run the MFB on a copy, price both decodes, keep the lower residual. Ties keep the chain's. A new report-only counter (`MfbSelected`) surfaces in the autopsy summary as its own line; the census line format and the `turbo` field are unchanged so byte-identity stays meaningful.
3. Order: hermetic suite; the eight G1 specimens as corpses (expected 0 on every block, matching the instrument); the five §6 guard pins (three exact, the two WN7 pins re-recorded); per-block wall-clock of the MFB at WN7 on this box (report-only, against the 7.68 s a WN7 block takes on air); then the full three-lane battery in the G0 form.

**Kill/proceed rule (pre-committed).**
- Battery: WN7 Poor both families clear the §5.3 rule (<= 32 errors direct on 3,243,776 bits, or the 97.5 % Poisson bound <= 1E-5 at fewer), AWGN WN7 0, all 112 non-WN7 censuses byte-identical to G0, WN8 censuses byte-identical to G0 (the generic map may not move QAM16), WN6/WN13/WN0 pins exact -> **exit (i) for WN7**: `Poor_Channel_Mask_Gate` arms WN7 by default, `MeasuredOnlyBank` drops its WN7 rows, the ledger row forwards to sim hard-gated (on-air column unforwarded per plan §6), guard pins re-recorded, the B4 false-red check at the registered budget.
- WN7 improved but not §5.3-green on both families -> **exit (ii)**: the ensemble ships if every corpse and the battery show it never selects a wrong decode that the chain path had right (a selector that loses blocks is worse than no selector); the new counts re-bank in `MeasuredOnlyBank`; the closeout states the new ceiling.
- Any wrong selection on a block the chain path had right, or any non-WN7 census moved -> stop, diagnose in writing, no merge.

## Measurements

*(appended as the leg runs)*
