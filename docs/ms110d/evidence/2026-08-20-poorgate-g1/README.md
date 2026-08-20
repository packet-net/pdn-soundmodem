# G1 - WN7 under the MFB-form receiver (MS110D Poor-gate successor program)

Registered 2026-08-20 after G0's PASS, per [poor-gate-successor-plan.md](../../poor-gate-successor-plan.md) §3/§4, before any code.

## Registration

**Question.** Does the label-free MFB-form receiver, generalised from the Table D-VII QAM16 map to the Table D-VI 8PSK map, decode WN7's residual bursts at the mask class where the shipped DFE-chain receiver leaves its 131 errors?

**Decision inputs (banked).** WN7 Poor 83/3,243,776 canonical and 48/3,243,776 disjoint at the W0/G0 batteries, every error on exactly seven of the sixteen bursts (W0 census, reproduced byte-identically at G0): canonical w1/b0 (channelSeed 1000508) 20, w1/b1 (1001508) 25, w2/b1 (2001508) 38; disjoint w0/b0 (10508) 16, w1/b0 (1010508) 12, w2/b1 (2011508) 6, w3/b0 (3010508) 14. The other nine bursts are clean. The B3.9 anatomy of these errors (single contiguous 14-24-info-bit decoder events, honest LLRs, every block converged). The W1 truth lane: WN7 w0/b0 (508) decodes to 0 on all 11 blocks under per-symbol truth, including b5 where the oracle-labels pass leaves 15 (the guard pin). The W5b2 prototype state on WN8: corpses 112 canonical / 32 disjoint under the shipped schedule (soft rungs, one gated refit, hard rungs to a fixed point).

**Mechanism claim.** WN7's residual is carried by the same term W2 located at WN8 - the FF + sparse-chain + segment-pricing sandwich's representation of the deep-fade spans - not by the waveform (W1 truth lane) and not by labels (B3.9). The MFB-form receiver replaces that sandwich with per-symbol matched projection on a probe-anchored composite-FIR trajectory and whitened pricing, which at WN8 took the same class of block from coin-flip to zero. At 8PSK the projection has no envelope information to use (every point on the unit circle) and 45 degree spacing instead of 30, so the gain, if any, comes from the trajectory and the pricing, not the constellation.

**Instrument + method.**
1. A modulation-generic symbol map in the test-side prototype `Ms110dMfbFormReceiver` only: bits per symbol (3|4), point count (8|16), and the wire map `fetched number -> scrambled point` (QAM16: number XOR the frame-position nibble from `NextQam(0,4)`; 8PSK: `Transcode8Psk[tribit]` plus the frame-position tribit from `NextPsk(0)`, modulo 8), applied at the five sites that currently read `Ms110dTables.Qam16` (truth wire, nearest-point noise estimate, LLR metric, soft E[x], re-encode). The shipped `Ms110dMfbBlockDecoder` is not touched in G1.
2. Calibration lane (i), the mapper may not move QAM16: the generic prototype re-run on the WN8 corpses w0/b0 canonical (509) and disjoint (10509) under the W5b2 schedule must reproduce the banked rung lines byte-identically.
3. Calibration lane (ii), 8PSK templates and alignment: the truth-reconstruction residual per block on WN7 w0/b0 must sit at the same floor the QAM16 lane measures (a wrong map or misaligned tribit shows up here as a residual orders of magnitude above it before any honest rung runs).
4. The measurement: the prototype on the seven residual bursts plus the clean guard-pin burst w0/b0, `MS110D_MFBRX_WN=7 MS110D_MFBRX_SNR=19`, the W5b2 schedule (`MS110D_MFBRX_SOFT=1 MS110D_MFBRX_REFIT=1`, caps as shipped), reading coded errors per block at the honest fixed point against ship's per-burst counts. Budget ~2 days including the mapper.

**Kill/proceed rule (pre-committed).**
- Pooled prototype residual over the seven bursts <= 65 (half of ship's 131), with the guard-pin burst still 0 and both calibration lanes green -> **G1b**: a shipped port behind structural 8PSK scope (`Ms110dMfbBlockDecoder` generalised, armed for `Psk8` blocks), corpse -> the five guard pins -> the full three-lane battery, gate decision by the §5.3 rule on both families (<= 32 errors direct on 3,243,776 bits, or the 97.5 % Poisson bound clearing 1E-5 at fewer), censuses byte-identical on every non-WN7 point.
- Pooled residual > 65, or no better than ship on the named blocks -> the B3.9 verdict is **confirmed under the MFB class** and banked as "WN7 needs added information, measured under both the DFE-chain class and the MFB class"; no shipped change; the program moves to G2.
- Either calibration lane red -> the instrument is wrong, not the receiver; fix and re-run the lane before reading any honest number.

## Measurements

**Calibration lane (i) - the mapper does not move QAM16 (2026-08-20, commit af621da).** The prototype before and after the modulation-generic symbol map, on WN8 w0/b0 canonical (channelSeed 509) and disjoint (10509), `MS110D_MFBRX_ITERS=8 MS110D_MFBRX_SOFT=1 MS110D_MFBRX_SOFTUNTIL=4 MS110D_MFBRX_REFIT=1`: summaries **byte-identical** on both specimens ([calibration/](calibration/), SHA-256 in `sha256.txt` - 45f615dc... for 509, 6454af37... for 10509, before = after). Every rung line, anchor-fit residual and truth-reconstruction residual reproduces. The generic code path is the QAM16 code path, operation for operation.

**Calibration lane (ii) - 8PSK templates and alignment.** On every WN7 specimen the truth-reconstruction residual sits at 9.0E-4 to 9.9E-4 per row against an anchor-fit floor of 1.8E-4 to 2.3E-4 (both about 3x the WN8 figures of 3.0E-4 and 7.3E-5, as the 4 dB lower operating point predicts); a wrong map or a misaligned tribit would show here as orders of magnitude, not a factor of three consistent across eight bursts. The lane is green; R0 on every burst sits at 44-46 % coded errors, the coin-flip of an honest unequalised first pass.

**The measurement (2026-08-20, commit af621da; summaries in [wn7-prototype/](wn7-prototype/)).** The generic prototype on the seven residual bursts and the guard-pin burst, `MS110D_MFBRX_WN=7 MS110D_MFBRX_SNR=19 MS110D_MFBRX_ITERS=60 MS110D_MFBRX_SOFT=1 MS110D_MFBRX_SOFTUNTIL=30 MS110D_MFBRX_REFIT=1` (W5b1's best honest schedule: soft30 + refit -> hard), read at the R60 fixed point. Ship's per-block counts come from the same bursts' autopsy corpses (`Mask_Burst_Corpse_Dump`, the decoded-bits file against the tx bits, block = position / 36,864); every autopsy total reproduces its census digit.

| burst (channelSeed) | ship | ship per block | prototype | prototype per block | per-block min |
|---|---|---|---|---|---|
| 507 w0/b0 (508), the guard pin | 0 | - | 15 | b1:15 | 0 |
| 507 w1/b0 (1000508) | 20 | b3:6 b6:14 | **0** | - | 0 |
| 507 w1/b1 (1001508) | 25 | b2:12 b4:6 b7:7 | 71 | b8:71 | 0 |
| 507 w2/b1 (2001508) | 38 | b5:6 b8:32 | 15 | b4:15 | 0 |
| 10507 w0/b0 (10508) | 16 | b4:6 b10:10 | **0** | - | 0 |
| 10507 w1/b0 (1010508) | 12 | b5:1 b9:11 | 18 | b1:18 | 0 |
| 10507 w2/b1 (2011508) | 6 | b7:6 | 134 | b4:134 | 0 |
| 10507 w3/b0 (3010508) | 14 | b5:14 | **0** | - | 0 |
| **pooled, seven residual bursts** | **131** | | **238** | | **0** |

Two readings, both load-bearing:

1. **As a standalone receiver the MFB-form prototype does not clear WN7's residual.** 238 against ship's 131 over the seven bursts, and 15 on the burst ship decodes clean. The registered kill rule (<= 65) fires by a factor of 3.7. It zeroes three of the seven bursts and more than halves a fourth; it loses on the other three and on the guard pin. Its failures are the MFB's own kind - a block that settles into a self-consistent wrong fixed point (w1/b1 b8 holds 71 from R10 to R60; the refit at R30 moves it to 39 for one rung and it returns), not the deep-fade decoder events B3.9 anatomised on ship.
2. **The two receivers' failure sets are disjoint at block level, on every burst.** Not one of the 88 blocks is wrong under both. A per-block oracle choosing between the two decodes scores **0 on all eight bursts**. That is an ensemble ceiling in the B4 sense - the best any selector could do - and it is the measured successor lever this leg hands on. The mechanisms differ (ship loses deep-fade blocks to decoder error events with honest low-|LLR| wire bits; the MFB loses blocks to basin traps with a confident wrong decode), which is why they do not coincide.

## Verdict

**G1 kills, per its rule: the B3.9 verdict is confirmed under the MFB class as a standalone receiver.** Banked as "WN7 needs added information, measured under both the DFE-chain class and the MFB class". No shipped change from this leg.

## Stop-and-reassess (the escalation rule's written form)

Options on the table, with cost:

- **(a) Ensemble by label-free selection.** Run both receivers per 8PSK block and keep the decode the evidence prefers. The ceiling is 0 on these specimens (above). What it needs is a selector that, without labels, picks the right block every time; the natural one already exists in the MFB frame - re-encode each candidate decode to wire symbols, reconstruct through the estimated trajectory, and compare reconstruction residuals (the W5b2 port uses exactly this to accept period-2 cycles). A wrong decode of ~70 info bits re-encodes to a few hundred wire-bit differences, which should price as a residual excess well above the noise floor. Cost: ~1 hour of instrument, corpse-minutes to measure; a shipped build, if the ceiling survives, is the 8PSK port of `Ms110dMfbBlockDecoder` plus selection plumbing, 2-3 days. **This is G1c, registered below.**
- **(b) Schedule variants for the prototype** (hard-only, soft-only, no refit). Corpse-minutes each. Not taken: the kill rule was committed on one schedule and the failure set is a basin phenomenon, which W5b1 measured to be "basin-determined early" and schedule-insensitive in its tail.
- **(c) Close.** Zero cost; leaves (a)'s measured 0 on the table.

## G1c - the selection ceiling (registered 2026-08-20 16:51 UTC, the instrument launched in the same minute; this rule was written before any selection line was read)

**Question.** Does the label-free MFB reconstruction residual select the zero-error decode on every block of the eight specimens when offered ship's block decode and the prototype's?

**Instrument.** `MS110D_MFBRX_CANDIDATE=<autopsy-decoded file>` on the prototype: at the final rung each block re-encodes its own decode and ship's, reconstructs both through the block's final trajectory, and reports `own errs@residual cand errs@residual -> pick`. Ship's decodes are the same corpses' autopsy files. No shipped change.

**Kill/proceed rule (pre-committed).** All 88 blocks pick a zero-error decode -> **G1d** registers the build (the 8PSK port of the shipped MFB decoder behind structural scope, ship + MFB per block, residual selection), with the battery gate decision by §5.3 on both families. Any block picks a wrong decode -> report the margin distribution (right-minus-wrong residual on every block) and stop; a selector that is right on most blocks but not all cannot clear a mask that needs <= 32 errors per 3.24M bits, and the honest endpoint is the ensemble ceiling banked as a lever with a measured selector accuracy. Selection margins inside the noise floor on the zero-error side -> same stop.

### G1c measurements (2026-08-20, summaries in [wn7-selection/](wn7-selection/), ship's decodes from [wn7-ship-autopsy/](wn7-ship-autopsy/))

Eight specimens, 88 blocks, 18 of them contested (one receiver wrong, never both). **The residual selector picks a zero-error decode on all 88.** On the 70 uncontested blocks both decodes are identical and the residuals equal to the last digit, as they must be.

| specimen | block | wrong receiver | its errors | residual excess of the wrong decode | pick | selected errors |
|---|---|---|---|---|---|---|
| 10507 w0/b0 | b4 | ship | 6 | +0.40 % | own | 0 |
| 10507 w0/b0 | b10 | ship | 10 | +0.35 % | own | 0 |
| 507 w0/b0 | b1 | MFB | 15 | +4.76 % | cand | 0 |
| 10507 w1/b0 | b1 | MFB | 18 | +7.87 % | cand | 0 |
| 10507 w1/b0 | b5 | ship | 1 | +0.88 % | own | 0 |
| 10507 w1/b0 | b9 | ship | 11 | +0.58 % | own | 0 |
| 507 w1/b0 | b3 | ship | 6 | +1.08 % | own | 0 |
| 507 w1/b0 | b6 | ship | 14 | +1.04 % | own | 0 |
| 507 w1/b1 | b2 | ship | 12 | +0.55 % | own | 0 |
| 507 w1/b1 | b4 | ship | 6 | +0.17 % | own | 0 |
| 507 w1/b1 | b7 | ship | 7 | +0.36 % | own | 0 |
| 507 w1/b1 | b8 | MFB | 71 | +11.03 % | cand | 0 |
| 10507 w2/b1 | b4 | MFB | 134 | +22.18 % | cand | 0 |
| 10507 w2/b1 | b7 | ship | 6 | +0.19 % | own | 0 |
| 507 w2/b1 | b4 | MFB | 15 | +8.65 % | cand | 0 |
| 507 w2/b1 | b5 | ship | 6 | +0.22 % | own | 0 |
| 507 w2/b1 | b8 | ship | 32 | +2.41 % | own | 0 |
| 10507 w3/b0 | b5 | ship | 14 | +0.58 % | own | 0 |

**Margins.** The two failure mechanisms price very differently, and in the helpful direction. An MFB basin trap is a confident wrong decode of 15-134 info bits: it re-encodes to hundreds of wrong wire symbols and its residual sits 5-22 % above the right decode's. A ship decoder event is 1-32 info bits: its excess is 0.17-2.4 %, small in relative terms but not small against the noise of the comparison. Both residuals are means over the same ~37k half-chip rows of the same ring through the same trajectory, so the comparison's only noise is the cross term between the channel noise and the wrong decode's reconstruction difference; at the thinnest contested block (507 w1/b1 b4, six info errors, +0.17 %) the wrong decode adds about 0.040 of reconstruction energy against a cross-term standard deviation of about 0.010, i.e. ~4 sigma, and every other contested block is wider (the one-error block at +0.88 % is ~9 sigma). That is a selector that is right by construction on big mistakes and right by margin on small ones. What this instrument does not measure is the selector over a full battery - 16 bursts per family, 176 blocks each, with whatever contested blocks a fresh family brings - which is exactly what G1d's battery is for.

### G1c verdict

**Proceed to G1d, per the rule.** The ensemble ceiling of 0 survives its selector on every contested block of the eight specimens. Banked for the build: the MFB-form receiver generalised to 8PSK is not a better WN7 receiver than the DFE-chain path (238 vs 131), it is a *differently wrong* one, and the two together with residual selection decode every WN7 residual burst the Phase B battery left.
