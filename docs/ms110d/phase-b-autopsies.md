# MS110D Phase B1 — broken-tier autopsies (2026-07-24)

Root-cause notes per phase-b-plan §B1: no fix lands without a mechanism written here. Every claim below cites an instrument reading from [evidence/2026-07-24-phase-b0/](evidence/2026-07-24-phase-b0/) or a dated diagnostic run on `ms110d-phase-b1` (scatter dumps via `Ms110dScatterDiagnostics`, genie via `MS110D_MASK_GENIE=1`).

## WN7 — 8PSK Poor @ +19 dB (baseline 4.62E-1) — MECHANISM CONFIRMED

Four independent instruments, one story:

1. **Genie ≈ measured** (uncoded 1.57E-1 genie vs 1.63E-1 baseline): perfect channel observation at the probes changes nothing — the deficit is not estimation.
2. **Static 2-path @ +19 dB: ZERO errors, uncoded 1.94E-4** — the same multipath geometry with no time-variation decodes perfectly: the 8PSK chain (mapping, scrambler, LLRs, DFE span) is correct; only dynamics break it.
3. **Scatter (seed 507, 130 frames): 89/130 frames fully phase-smeared** — per-frame 8th-power concentration |E[e^{j8θ}]| median 0.088 (locked frames read 0.68–0.85). The folded mod-45° error distribution is uniform (mean 9.9°, matching 11.25° uniform prediction) — a donut, not a noisy constellation. Amplitude holds ~0.85: gain tracks, phase is gone.
4. **Turbo 0c/88r**: every block oscillates and reverts — the re-encode of a garbage decode trains garbage, the fixed-point revert (issue #65 fix) correctly refuses it.

**Mechanism.** Between probe solves the channel/carrier model is piecewise-static (endTaps + fixed ω across the 107 ms U=256 span). The 1 Hz Rayleigh composite rotates tens of degrees inside a frame — during fade swings far more. BPSK/QPSK decision regions (±90°/±45°) absorb most of it; 8PSK's ±22.5° do not. Once intra-frame rotation crosses the half-angle, decision-directed updates (RLS rows and DD training rows) train toward wrong symbols and the loop self-destructs — phase coherence is lost for the WHOLE frame. Worse, the collapse PERSISTS: the next probe solve is rank-deficient and **anchored to the current (wrong) taps**, so re-lock is slow — 68 % of frames are smeared from only ~9 % deep-fade exposure, and the first ~20 frames of the dump are all smeared from one early event.

**Fix family** (B2, in order of expected leverage): (a) probe-anchored **retrospective phase interpolation** across each frame — both bracketing probe solves are known before LLRs are pushed in the block-buffered architecture, so this is free non-causality (phase-b-plan §B2.1); (b) per-position taps/h for the same reason; (c) **collapse detection + fresh non-anchored re-solve** when probe MSE explodes (the anchor is the persistence mechanism); (d) 8PSK joins the chain-decomposed BCJR turbo (§B2.2) once the first-pass symbols are sane. **Genie ceiling**: not meaningful pre-fix (the bound is broken by the same mechanism); re-measure after (a).

## WN8 — 16QAM Poor @ +23 dB (baseline 4.97E-1) — MECHANISM CONFIRMED (shared + amplitude)

Same instrument pattern: genie ≈ measured (uncoded 2.56E-1 vs 2.60E-1); **static 2-path @ +23 dB: ZERO errors, uncoded 8.32E-6** — the QAM chain is correct when nothing moves; errors fade-uncorrelated (12 % vs 9 % uniform). The WN7 mechanism applies with tighter tolerances in BOTH dimensions: 16QAM decision regions are ~±16° at the outer ring AND amplitude-partitioned, while the Rayleigh composite swings amplitude ±several dB within a frame (the ~43°/frame rotation figure in #69 was computed for exactly this point). Piecewise-static phase AND gain across 107 ms cannot hold 16QAM. Fix family: the same §B2.1 interpolation with the **gain ramp mandatory** (phase-b-plan §B3.4); turbo stays excluded until first-pass sanity exists.

## WN6 — QPSK Poor @ +14 dB (baseline 1.07E-1) — MECHANISM CONFIRMED: rate-3/4 cliff on shared physics

The B0 telemetry resolves the WN6-vs-WN13 200× discrepancy (identical QPSK U=256/K=32 geometry): **WN6's uncoded channel-bit SER (8.91E-2) is BETTER than WN13's (1.06E-1)** — the equalizer treats them the same, as it should — yet coded outcomes are 1.07E-1 vs 3.14E-4. The difference is the operating point vs the code: 8.9 % channel BER sits at/beyond the rate-3/4 K=7 threshold (decoder breakdown), while 10.6 % is comfortably inside rate-9/16's. WN6 is not a distinct defect: it is the same intra-frame-rotation physics (QPSK's ±45° breaks only on fade swings — hence ~9 % uncoded, vs 8PSK's ±22.5° breaking everywhere) pushed over a code cliff. Turbo agrees: WN6 98c/33r (recoverable), WN13 175c/0r. **Fix family**: §B2.1 interpolation to pull uncoded below the r=3/4 cliff, then QPSK chain-BCJR turbo (§B2.2) for margin. Re-measure after B2.1 before any WN6-specific work — the cliff prediction is that WN6 follows WN13 down as soon as uncoded drops a few points.

## WN0 — Walsh Poor @ −1 dB (baseline 1.12E-1) — MECHANISM CONFIRMED: coherent detection wastes the channel's diversity

The isolation pair settles it (2026-07-24, `b1-wn0-iso` runs):

- **Static 2-path @ −1 dB: ZERO errors in 100,864 bits, uncoded 1.14E-3** — the 4.8-chip intra-symbol echo is handled fine when the channel holds still. Echo self-interference REFUTED as the dominant mechanism.
- **Flat Rayleigh (one path, no echo) @ −1 dB: coded 1.39E-2, uncoded 1.02E-1**, with 65 % of uncoded errors in the 22 % of bits under deep fade — single-path fading already produces most of the failure: a −10 dB fade at −1 dB SNR erases 32-chip Walsh symbols outright, and the 8-symbol DD-PLL (107 ms average ≈ the coherence time) loses phase through every fade.
- **Poor (echo + fading) = 1.12E-1 — EIGHT TIMES worse than flat fading.** The second path makes the current receiver WORSE (the two paths beat; the time-varying composite degrades the coherent 32-chip correlation), when two independently-fading equal paths should be a diversity GAIN — they rarely fade together, which is precisely why the spec dares put the mask at −1 dB. The receiver is throwing away the channel's gift.

**Mechanism.** Coherent single-reference Walsh correlation with a slow DD-PLL: no per-path processing, no diversity combining, phase reference lost in fades. **Fix family** (B3.5, physics-indicated by the 8× inversion): per-path RAKE-style combining or noncoherent/differentially-anchored combining that turns the 2-path fading into diversity, with channel references from the Walsh decisions themselves. The flat-Rayleigh 1.39E-2 is the intermediate bar: any detector still worse than flat-fading on Poor is not combining. Secondary finding: 48 turbo-converged blocks on a WN0 sweep = corrupt-WID misacquisitions decoding as other WNs at −1 dB — real bursts land on the wrong demodulator path; quantify during B3.5 whether acquisition needs a WN0-favouring prior at low SNR.

---

**B1 gate: met.** All four broken-tier points have confirmed mechanisms, each pinned by at least three independent instrument readings, and every fix family maps into the plan's existing stages (§B2.1 time-varying representation + collapse recovery; §B2.2 chain BCJR; §B3.4 gain ramp; §B3.5 diversity detector). B2 opens next.
