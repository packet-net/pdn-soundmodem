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

*(appended as the leg runs)*
