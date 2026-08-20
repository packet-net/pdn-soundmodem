# G2 - WN8's first-block bootstrap (MS110D Poor-gate successor program)

Registered 2026-08-20 after G1d's exit (i) for WN7, per [poor-gate-successor-plan.md](../../poor-gate-successor-plan.md) §3/§4, before any code.

## Registration

**Question.** What defeats block 0 on the three disjoint WN8 bursts that carry the whole 1.75E-2 (w1/b0 channelSeed 1010509, w2/b0 2010509, w3/b0 3010509: ~24.5k errors each, both shipped schedules reverting to coin-flip), and which banked lever prices to the mask class on them?

**Decision inputs (banked).** W6: the three bursts and the W6a-recovered w0/b1; canonical's one leaking burst (w1, channelSeed 1001509, 1,029 errors). W1b: the matched-filter bound on the exact channel decodes both w0/b0 specimens to zero (never run on these three bursts). W3: label-free probe-anchor trajectories at -29/-30 dB NMSE on the w0/b0 specimens, zero coded errors through the bound (never run on these three). W5a: the B3.5b phenomenon at WN8 - acquisition centring the chip clock on the echo, fixed by the per-burst delay-profile window. G1d: the block-0 probe-geometry fault (the preamble's closing probe is unshifted) - it did not touch Long-interleaver blocks, so it is not this mechanism. The shipped decoder's per-rung diagnostics (`mfb-rung`, `mfb-block`) and the prototype's block anatomy (window, per-anchor energy and peak, the demod's lock, anchor-fit and truth-reconstruction residual per block, per-rung coded errors per block).

**Mechanism candidates (pre-registered, to be discriminated by the anatomy, not assumed).**
1. *Window placement* - the per-burst delay-profile window, built from block 0's first eight probes, misplaced for these bursts (the B3.5b class): the b0 anatomy's window and per-anchor peaks against the truth-recon residual of every block would all be bad, not only block 0.
2. *Trajectory at the burst start* - block 0's left boundary has the preamble's closing probe as its only anchor and the demodulator's coarsest CFO/timing state: block 0's truth-recon and anchor-fit residuals would sit above the other blocks', and the bound with probe anchors would fail at b0 where the exact-channel bound passes.
3. *Detection bootstrap* - model and window fine (residuals in family, the probe-anchor bound clean at b0) but the schedule never descends from the coin-flip R0 on that block: a basin question, answered by schedule diversity or a better-seeded first rung.
4. *Acquisition state* - a lock that is wrong in a way later blocks recover from (CFO refinement); visible in the lock line and the residual-per-block profile.

**Instrument + method.** On the four bursts (w0/b0 as the healthy control, then w1/b0, w2/b0, w3/b0 of seed 10508, +23 dB), in parallel: (a) the prototype under the W5b2 schedule (`MS110D_MFBRX_SOFT=1 SOFTUNTIL=30 REFIT=1 ITERS=72`), (b) the shipped decoder's autopsy corpse with its `mfb-*` diagnostics, (c) the matched-filter bound with `MS110D_MFB_TRAJ=truth` and `=probes`. Reads per block: exact-channel bound, probe-anchor bound, prototype fixed point, shipped outcome, truth-recon and anchor-fit residuals, window and lock. Then price the surviving lever against the truth seam before any build (plan §4: build only what prices <= ~2x mask-class on the three).

**Kill/proceed rule (pre-committed).**
- Exact-channel bound clean at b0 on all three, probe-anchor bound clean at b0 on all three -> mechanism 3 (or 1 with a visible window signature): the lever is on the decoder's schedule side (a third schedule, a seeded first rung from the probe-anchor MFB's own R0), registered as G2b with a corpse-first build.
- Exact-channel bound clean, probe-anchor bound failing at b0 -> mechanism 2: the lever is trajectory information at the burst start (the preamble as a leftward anchor, the W3 moment track for block 0); price by re-running the bound with a preamble-extended anchor series before building; G2b builds only if the priced bound is clean at b0.
- Exact-channel bound itself failing at b0 on any of the three -> that block is a waveform floor event at this operating point (an honest erasure the r3/4 code cannot absorb); banked as such, the lever list shrinks to the other two bursts, and if all three are floor events the program's WN8 endpoint is exit (ii) with the measured ceiling re-stated.
- Whatever the route: the first battery that ships anything must keep WN7 and the 112 non-WN8 censuses byte-identical and AWGN WN8 at 0.

## Measurements

*(appended as the leg runs)*
