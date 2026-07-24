# MS110D Phase B — program plan (2026-07-24)

**Charter.** Design §6 (the single gate owner) defines Phase B's hard gate: **D-LXIV at mask, no allowance, AWGN + Poor, WN0–8+13, with Phase A regressions green**; the measured deliverable is an RLS-vs-NLMS A/B report. The AWGN column already holds (closeout: 10/10 at ≥3M bits, zero errors). The modulations (8PSK/16QAM) and RLS tracking landed in Phase A. What remains is exactly one thing: **make the Poor column true, then flip `MS110D_POOR_GATED=1`**. Umbrella issue: #69; feeding issues #64/#65/#67. Non-goals: WN9–12 (Phase C), off-air/OTA (Rung 3), 48 kHz WBHF.

Accept rule per point (§5.3 as restated at the A closeout): ≥3×10⁶ payload bits, ≥600 s simulated, zero acquisition failures; ≥30 errors → direct BER ≤ 1E-5, else the 97.5 % Poisson upper bound clears 1E-5. Evidence only through the `MS110D_MASK_LOG` chain, disjoint-seed cross-checked.

## 1. Starting position — the baseline sorts into three regimes

First complete Poor baseline (2026-07-23 closeout, measured-not-gated, evidence in `evidence/2026-07-23-phase-a-closeout/`):

| WN | Mode | Poor SNR | Baseline BER | × mask | Regime |
|----|------|----------|--------------|--------|--------|
| 4 | BPSK r2/3, U=96/K=32 | +10 dB | 2.36E-5 | 2.4× | **near** — the machinery works; needs margin |
| 13 | QPSK r9/16, U=256/K=32 | +11 dB | 6.2E-4 | 62× | **near-ish** — closes on core improvements |
| 3 | BPSK r1/3, U=96/K=32 | +7 dB | 8.7E-3 | 870× | structural |
| 5 | BPSK r3/4, U=256/K=32 | +11 dB | 2.2E-2 | 2 200× | structural |
| 1 | BPSK r1/8, U=48/K=48 | +3 dB | 2.85E-2 | 2 850× | structural |
| 2 | BPSK r1/4, U=48/K=48 | +5 dB | 3.67E-2 | 3 670× | structural |
| 0 | Walsh 75 bps | −1 dB | 8.1E-2 | 8 100× | structural (own detector) |
| 6 | QPSK r3/4, U=256/K=32 | +14 dB | 1.29E-1 | 12 900× | **broken** |
| 7 | 8PSK r3/4, U=256/K=32 | +19 dB | 4.66E-1 | 46 600× | **broken** |
| 8 | 16QAM r3/4, U=256/K=32 | +23 dB | 4.96E-1 | 49 600× | **broken** |

The regime split drives the whole plan. BER 0.47–0.50 is not a weak equalizer — it is random output, i.e. a **defect** (rotation, mis-mapping, self-corrupting feedback), and no amount of RLS science fixes a sign error. The structural tier has known physics deficits (below). Only the near tier is a tuning problem.

**What the baseline already tells us (facts, current code):**

- **The physics.** Poor = 2 equal-power Rayleigh paths, 2 ms apart, 1 Hz two-sigma Doppler → coherence ≈ 300 ms. U=256 modes solve the channel every 120 ms (≈2–3 solves per coherence time) and then hold that snapshot **static across a 107 ms data span** — the channel moves materially mid-frame and every downstream consumer (DFE decisions, BCJR model, LLR scaling) is fed a stale estimate. Deep fades arrive ~1/s and last tens of ms; the Long interleaver spans 5–10 s ≈ 5–10 independent fades — enough diversity **if** LLRs are honest about fade-time confidence.
- **The BCJR turbo path is BPSK-and-U>48 only.** WN1/2 (U=48) and every QPSK+ mode are excluded from MAP re-equalization; QPSK/8PSK instead get the DFE-re-solve turbo the WN6 handover suspects of self-corruption. That WN1/2 — the strongest codes on the ladder, with the tightest probe cadence (40 ms) — sit at 3 000× mask while WN4 (r2/3) sits at 2.4× points at the exclusions, not the codes.
- **The BCJR echo model is capped at lag 8** (3.3 ms) because the trellis is 2^delay states — the #64/#69 "echo ceiling". §3 removes this ceiling outright.
- **WN0 has no multipath or fade processing at all.** `TrackWalsh` is coherent Walsh correlation plus a decision-directed carrier PLL averaged over 8 symbols (107 ms — at the coherence time, so the loop is either too slow or too noisy), and the 2 ms echo = 4.8 chips of intra-symbol self-interference the correlator never models. 8.1E-2 at −1 dB is the expected result of that description.
- **WN6 vs WN13 is a 200× discrepancy on identical geometry** (both QPSK, U=256/K=32, 3 dB and one code rate apart). Either the coded operating point sits across a cliff, or something in the WN6 path is broken that WN13's margin hides. The autopsy (§2, B1) decides — compare **uncoded** symbol-error rates: matching uncoded SER ⇒ code operating point; diverging ⇒ equalizer/LLR defect.

## 2. Program structure

Five stages. Each has an explicit exit gate; families close under the full §5.3 accept rule before the next one starts. Diagnosis is a stage, not a vibe: **no fix lands without a written mechanism** — the Phase A audit's central lesson is that plausible fixes tuned against the rig produce numbers that die under de-rigging.

### B0 — instruments first (S/M)

The Phase A closeout proved the instruments are worth auditing as hard as the modem. Phase B needs three new ones before any science:

1. **Channel-truth genie.** The Watterson rig is ours, so the true tap trajectory is knowable: export it (env-gated, test-side) and give the demodulator a test-only injection seam for channel truth. A genie-aided run per point = the **achievability bound of the current detector under perfect channel knowledge**. This cleanly splits every deficit into *tracking* (genie ≪ measured) vs *detection/LLR* (genie ≈ measured, both bad) vs *infeasible-as-architected* (genie can't reach 1E-5 — escalate, don't tune). Validate the genie itself on the static rig, where truth is trivial.
2. **Failure-localizing telemetry.** Per-point: uncoded SER alongside coded BER (§5.3 already calls for it), errors time-stamped against the genie's fade envelope (are we only dying in fades?), turbo outcome counters (converged / reverted / aborted). All behind the existing evidence-file discipline.
3. **Off-rig discipline harness.** Watterson variants {1 ms, 0.5 Hz} and {3 ms, 2 Hz} plus the WID2 static, run as direction-checks (not gates) for every tuned constant. Nothing may encode the D.6.1 rig's exact numbers again — the delay-5 lesson.

Also in B0, the small honesty remainders from #64/#65/#67 that affect evidence quality: weighted-RLS consistency (weight scales the tap step but P always fully updates, so low-weight DD rows progressively **freeze** adaptation — scale the row into both, or drop the half-measure), λ policy stated against physics (current per-mode λ = 1 − ln10/U ∈ 0.952–0.991 vs design §2.5's 0.995 — RLS memory should be set by coherence time, not frame length; A/B it, feeds the §6 report), surplus decoded bits counted as errors, `Poor_At_Mask_Snr_Still_Uses_Turbo` made to assert something real, BCJR component tests on Gaussian noise with an **LLR-calibration** assertion (turbo trusts LLR scale; miscalibration silently costs dB), max-log documented as max-log, debug I/O out of library code, `_utpScratch` removed. Stretch, non-blocking: data-phase late entry, EOT-assisted termination, every-offset cold start (§5.1 leftovers).

**Gate B0:** genie validated against static truth; telemetry in the evidence chain; hermetic suite green; Phase A evidence set re-run clean.

### B1 — autopsies of the broken tier (time-boxed, M)

WN6, WN7, WN8, WN0 (plus a cheaper look at WN1/2) each get a root-cause note **before** any fix. Method per point: ablate turbo; first-pass vs turbo uncoded SER; genie-aided run; then the specific hypothesis list — WN6: the handover doc's ladder (turbo DFE-re-solve training on wrong QPSK decisions; LLR axis rotation after re-solve; scrambler/geometry mismatch) plus the WN6-vs-WN13 uncoded comparison. WN7: phase tracking loss vs detection (0.466 ≈ random says lock, not margin). WN8: the ~43°/frame rotation vs 16QAM decision regions, and the gain reference through Rayleigh amplitude — both currently uncompensated mid-frame. WN0: genie split of intra-symbol echo vs carrier-through-fades.

**Deliverable:** `docs/ms110d/phase-b-autopsies.md`, one section per point ending in *mechanism → fix family → genie ceiling*. **Gate B1:** every broken-tier point has a confirmed mechanism. Fixes for outright defects (sign/rotation/mapping bugs) may land here with regression evidence; anything that smells like tuning waits for B2.

### B2 — the science core (L)

The one architectural insight this plan commits to, plus the time-variation work, shared by every family:

1. **Time-varying channel representation.** The block-buffered architecture (everything is re-read for turbo anyway) makes retrospective interpolation free: both neighbouring probe solves are known before re-equalization runs. Interpolate the channel snapshot across each frame — per-position h1[]/h2[] for the BCJR (the API already takes spans; it is fed block constants today, #65), and probe-anchored **phase and gain ramps** for symbol decisions and LLR scaling (the 16QAM requirement, but it helps every mode: it converts the 107 ms staleness of U=256 frames into ≤ half-frame interpolation error).
2. **Chain-decomposed exact BCJR — removes the 2^delay ceiling and the BPSK restriction in one move.** For the sparse two-tap model h1 + h2·z⁻ᵈ, the symbol dependency graph splits into **d independent memory-1 chains** ({r, r+d, r+2d, …}: y[t] couples x[t] only to x[t−d], and per-symbol priors don't cross chains) — so per-chain BCJR has **M states** (constellation size), not M^d. Exact MAP under the model, no reduced-state approximation. Consequences: any echo lag becomes affordable (the 9 ms static's 21.6 symbols included — #64's ceiling gone), and QPSK (M=4) / 8PSK (M=8) BCJR are trivially cheap — the #69 worry about 4^L trellises dissolves. If the fractional 4.8-symbol echo demands a two-adjacent-lag model (taps at d and d+1), each chain becomes M² states — still cheap; measurement decides whether it's needed.
3. **Turbo/BCJR coverage extension.** U=48 (WN1/2) and QPSK/8PSK join the BCJR turbo path once (2) lands; the DFE-re-solve fallback is retired or repaired per the B1 verdict. 16QAM stays excluded until B3 decides on evidence (the 5×-LLR-scale trap is already a throw).
4. **RLS science** (the §6 measured deliverable): λ-vs-Doppler A/B, weighted-RLS fix from B0, probe-solve vs continuous-RLS tracking comparison → the RLS-vs-NLMS report, written against genie truth so it measures tracking error, not just BER deltas.

**Gate B2:** Phase A evidence set unharmed (AWGN 10/10, static, Doppler, hermetic); **WN4 Poor at mask under the full accept rule** — the nearest point is the proof the machinery works before the grind starts.

### B3 — family closure (XL, the bulk of Phase B)

Order by information gain and shared machinery, not by mask order:

1. **BPSK ladder: WN4 → WN3 → WN5 → WN1/2.** WN4 closes in B2. WN3/5 are the same machinery at worse operating points (WN5 adds the 107 ms cadence problem the time-varying model targets). WN1/2 add the U=48 turbo extension and the K=48 ridge/54-tap geometry at the ladder's lowest SNRs.
2. **QPSK: WN13 → WN6.** WN13 (62×) should close on B2 machinery alone; WN6 lands whatever its autopsy demanded.
3. **8PSK: WN7.** Chain BCJR + phase interpolation. Pre-registered fallback if it stalls at mask+2 dB: frequency-domain turbo equalization — an architecture decision taken explicitly, not a default.
4. **16QAM: WN8.** Probe-anchored phase+gain interpolation; amplitude reference through fades; turbo inclusion by measurement.
5. **Walsh: WN0 — its own mini-program**, run in parallel from B2 (it shares no DFE/BCJR machinery). Candidate ladder, genie-arbitrated before building: (a) two-finger probe-less RAKE with decision-directed per-path tracking; (b) noncoherent / differentially-anchored Walsh combining (the classic robust-mode answer to fades — likely the spec's intent, given no probes exist to track with); (c) chip-rate equalization ahead of the correlator. Longest-pole candidate of the whole phase; starting it early is the schedule hedge.

Every family closes per §5.3 full budget + a disjoint-seed (+10000) cross-check before the next family's tuning starts (background sweeps of later families are fine; conclusions aren't drawn from smoke).

### B4 — gate flip and closeout (S)

Full 10-point Poor sweep at full budget with `MS110D_POOR_GATED=1` armed, AWGN 10/10 + static + Doppler + OBW + hermetic regressions in the same evidence batch, disjoint-seed verification of every Poor point, evidence committed to `evidence/<date>-phase-b-closeout/`. `MS110D_POOR_GATED=1` becomes the standard sweep default; the nightly rotation (§5.3) gains the Poor points. `phase-b-closeout.md` mirrors the A record; design §6 row B marked closed same-day; plan.md amendment; #64/#65/#67/#69 closed with evidence trails; RLS-vs-NLMS report published.

## 3. Discipline (hard rules, carried from the Phase A audit)

- **No fix without a mechanism.** Structural/broken-tier changes cite their autopsy section.
- **No constant may encode the rig.** Every tuned value gets a physics justification and holds direction on the off-rig harness + disjoint seeds. (The BCJR delay-5 constant was silently load-bearing for an OOM constraint *and* rig-fitted — both failure modes of unexplained constants.)
- **Genie numbers are always labelled genie** and never cited as performance; SMOKE never cited as evidence; the `MS110D_MASK_LOG` chain is the only evidence path.
- **Phase A regression evidence re-runs before any merge touching the demod path** (cheap at workers=4).
- plan.md and the issues stay current as work lands; §6 remains the only gate table.

## 4. Risks and escalation

- **WN0 may be a redesign, not a fix** — no probes means nothing to anchor tracking on; if the genie says even perfect channel knowledge can't carry coherent correlation at −1 dB, the detector changes family (noncoherent), which is new science. Mitigated by starting its genie work in B1 and running it in parallel.
- **8PSK@19 dB / 16QAM@23 dB on 1 Hz Rayleigh are thin-margin detection problems.** If genie-aided runs can't reach 1E-5, escalate to the FD-turbo architecture decision immediately rather than tuning toward an unreachable bound.
- **Escalation rule** (mirrors Phase A's mask+4 dB rule): any family still > mask+2 dB when its stage budget closes gets a written stop-and-reassess — options on the table, cost of each — not more tuning.
- **Runtime is manageable:** the full Poor sweep fits an evening at `MS110D_MASK_WORKERS=4` (closeout measurement); WN0's 11 h serial point divides across workers. OOM discipline stands: fleets marked expendable (`choom +500`, baked into run-masks.sh), sweeps launched detached, 16 GB box.
- **Steep coded cliffs cut both ways:** several structural points may collapse to at-mask once LLRs become fade-honest (the WN13/WN6 cliff suggests operating points sit near a knee) — reason for the WN4-first proof and for re-measuring the whole table after B2 before assuming the B3 grind is long.

## 5. Issue mapping

| Item | Where |
|------|-------|
| #64 BCJR delay hard-coded → searched | done (Phase A closeout); ceiling itself removed in B2.2 |
| #64 bidirectional pass history, IsFlatChannel, CFO-immune fading detector | done (Phase A closeout) |
| #64 RLS λ deviation; RlsUpdate weight/P asymmetry | B0 → B2.4 (report) |
| #64 "~4.8 dB diversity" mechanism claim | B0 (re-justify from measurement or delete the claim) |
| #64 2-tap trellis ≤3.3 ms echo ceiling | B2.2 (chain decomposition) |
| #65 divergence protection (fixed-point revert), DD-row survival, per-dim noiseVar, dead-QAM16 throws, abort-path BeginTraining | done (Phase A closeout); acceptance-metric upgrade only if B1 implicates turbo |
| #65 per-position h1[]/h2[] block constants | B2.1 |
| #65 max-log doc, Console.Error/env-var in library, `_utpScratch` | B0 |
| #67 clock skew, ±75 Hz hermetic, WN×IL×K matrix, WN7/8 permutation, seed-offset knob | done (Phase A closeout) |
| #67 surplus-bit error accounting, weak turbo assert, BCJR test noise/calibration | B0 |
| #67 late entry / EOT termination / cold-start offsets | B0 stretch (non-blocking) |
| #69 QPSK BCJR, 8PSK path, 16QAM carrier recovery, BPSK deep-fade tracking, gate flip | B2/B3/B4 as structured above |

## Status log

### 2026-07-24 — B0 closed

All B0 exit-gate conditions met on `ms110d-phase-b0` (evidence: [evidence/2026-07-24-phase-b0/](evidence/2026-07-24-phase-b0/)). Instruments: the channel-truth genie landed with a bit-identical seam proof, ring-wrap/stream-lag guards, and one instrument-calibration lesson the plan §B0 anticipated — the first fading genie run was 26× worse than its baseline because noise-free rows produce the zero-forcing solve; the demodulator now measures σ² as mean |noisy−clean|² between its rings and restores the σ²·Σweight Gram term, making the genie the true-channel MMSE bound (validated: static rig 0 errors, WN4 Poor 3 vs 7 on matched seeds). Telemetry (uncoded SER, deep-fade error concentration from the rig's recorded tap trajectory, turbo outcome counters) is in every evidence line. The off-rig harness runs {1 ms, 0.5 Hz} and {3 ms, 2 Hz} — first light: WN4 clean on the former, BER 3.0E-1 on the latter (flagged for B1). Honesty remainders closed: weighted-RLS weight/P consistency (regression-pinned by DfeTests that fail on the old rule), surplus-bit accounting, the turbo assert asserts turbo, BCJR tests carry an analytic LLR-calibration identity, max-log documented, λ deviation documented, debug I/O host-side.

Results that moved during B0: the weighted-RLS fix alone took the Poor BPSK family up — **WN3 Poor to ZERO errors in 3.19M bits, confirmed at seed+10000 (first Poor point at mask)**; WN4 to 1.91E-5 canonical / 3.23E-6 disjoint (straddling); WN1/2/13 ~1.5–2× better; the broken tier (WN6/7/8, weight-1 fading paths — untouched by the fix) unmoved, as expected. Phase A hard gates all re-pass at full budget on the B0 code. The telemetry separates the regimes for B1: near points concentrate 38–48 % of uncoded errors in deep fades; WN7/8's errors are fade-uncorrelated (structural detection failure), WN7's turbo never converges (0c/88r) with coded BER WORSE than uncoded — an LLR-chain defect signature; WN0 barely codes (1.42E-1 → 1.12E-1). B1 (autopsies) is next: WN7 first (strongest defect signature), then WN8, WN6, WN0.

### 2026-07-24 (later) — B1 closed

All four broken-tier mechanisms confirmed in [phase-b-autopsies.md](phase-b-autopsies.md), each by ≥3 independent instruments, in a single day of instrument-driven diagnosis (the B0 investment repaying immediately). WN7: intra-frame rotation beyond ±22.5° collapses DD tracking into whole-frame phase smear (89/130 frames are donuts by the 8th-power concentration statistic), and the collapse persists because the anchored probe solve cannot re-lock — while the same chain decodes static 2-path with zero errors. WN8: same mechanism, phase AND amplitude, static 2-path clean at full budget (2.77E-6). WN6: not a defect — a rate-3/4 code cliff (its uncoded SER is BETTER than passing WN13's; the decoder falls off the threshold r=9/16 tolerates). WN0: echo self-interference refuted (static 2-path ZERO errors at −1 dB); flat Rayleigh alone costs 1.39E-2; adding the second path makes the coherent receiver 8× WORSE where a combining detector would gain diversity — the fix is the §B3.5 detector family, with flat-Rayleigh 1.39E-2 as the intermediate bar. New instrument: Ms110dScatterDiagnostics (per-symbol scatter + frame diagnostics + rig trajectory dumps); Static_2Path/Flat_Rayleigh isolation diagnostics now cover the autopsy WNs. B2 (time-varying channel representation + chain-decomposed BCJR) opens with one addition from the autopsies: collapse detection with a fresh non-anchored re-solve.

### 2026-07-24 (evening) — B2 closed

**The exit gate holds: WN4 Poor at mask under the full §5.3 rule on the merged code — 7.83E-6 canonical / 8.14E-6 disjoint (6.39M bits, ≥30 errors → direct rule, 0 acquisition failures, 5615 s simulated each)** — and WN13 Poor passed the full rule on its canonical seed too (5.70E-6), banked early for B3.2. Evidence: [evidence/2026-07-24-phase-b2/](evidence/2026-07-24-phase-b2/). What landed, in commit order: §B2.1 time-varying representation (per-symbol tap trajectory between the bracketing probe solves with rotation as a phase ramp, RLS tracking only the gated residual, per-probe gain-1 phase re-anchor, collapse detection with one fresh non-anchored re-solve per unhealthy episode, per-burst fading latch); §B2.2 chain-decomposed exact BCJR (M states per chain, exactness pinned by brute-force marginalization, the 2^delay ceiling and the BPSK restriction both gone); §B2.3 turbo on the chain BCJR for every PSK mode with a scrambler-exact echo model (the legacy lag correlation was phase-scrambled toward zero — a latent model defect) and per-position h1 from re-encoded mid-frame references; the flat-channel turbo skip retired with the DFE-re-solve fallback that motivated it. §B2.4 delivered: [rls-vs-nlms-report.md](rls-vs-nlms-report.md) — the frame-tied λ stands (0.995 never better across U=48/96/256), NLMS confirmed out of the signal path.

Poor table movement (smoke, vs the B0 baseline): WN2 3.67E-2 → 8.56E-5 (430×; the U=48 turbo exclusion was the bottleneck, as §1 predicted), WN13 6.2E-4 → 5.5E-6, WN4 1.91E-5 → 7.0E-6, WN5 2.2E-2 → 5.6E-6 (!), WN6 1.07E-1 → 3.5E-2 (cliff descending), WN7/WN8 broadly unmoved pending B3 (WN7's first-ever turbo-converged block; scatter BER 0.477 → 0.248 on the fixed realization). The collapse detector took three measured iterations (absolute MSE → MSE-relative → the Phase-A bad-probe criterion armed once per unhealthy episode) — each intermediate broke a different extreme (WN1 −3 dB AWGN false-fire; WN13 early-collapse blindness; WN2 K=48 fresh-solve death spiral), all caught by the instrument suite before merge. Known remainders carried to B3: WN13/WN6 disjoint-seed catastrophic-burst tail (rotation-invisible to correlation magnitude — the B3.2 entry autopsy), WN2 genie instrument defect on K=48 heavy-ridge modes (B3.1), WN5/WN2 full-budget closure runs, the whole-table re-measure before the B3 grind.

### 2026-07-24 (night) — B3 opened: the tail autopsy — two of four classes killed same-day

The B3-entry re-baseline's central finding (below 8PSK the mean is solved; the catastrophic-burst tail is everything) got its autopsy, and the tail turned out to be four distinct mechanisms, not one. Instruments first (evidence: [evidence/2026-07-24-phase-b3-tail-autopsy/](evidence/2026-07-24-phase-b3-tail-autopsy/)): a per-burst census in the mask harness (`MS110D_MASK_BURST_LOG`) that made the tail countable — WN5's 67,991 errors are ONE burst (99.97%), WN1's are 12 of 248 bursts with 236 at zero — and a corpse-reproduction rig (`Ms110dTailAutopsy`) that rebuilds any mask burst bit-exactly and dumps first-pass symbols against transmitted truth.

**Class A — carrier false lock (WN5 fatal, WN13-disjoint transient), FIXED.** The tail-superframe RefineCarrier refit's sequential phase unwrap random-walked ~3.5 turns through a deep fade (noise phases downweighted in the regression but still chaining the unwrap) and fitted −4.86 Hz on a burst whose initial acquisition read −0.53 Hz; beyond the ±4.17 Hz alias basin of the 120 ms frame, the tap-rotation trim locks the stable false fixed point at −1 cycle/frame — every probe statistic healthy, half of every data frame inverted (SER 0.55, uncoded worse than chance, 11/11 turbo reverts). Fix: `EstimateCarrierFit` — lag-product slope (faded groups self-suppress), no unwrap chain exists; unit tests pin the corpse scenario. The corpse decodes 0 errors.

**Class C — mid-fade burst abandonment (WN2 98.8% of errors, WN1 83%), FIXED.** The 25-consecutive-bad-probes SignalLost exit had frame-rate-scaled patience: 3 s at U=256 (never false-fired), 1 s at K=48 — and Poor fades outlive 1 s, discarding every abandoned burst's remaining bits. Patience is now wall-clock ~4 s uniformly.

**Post-fix, full §5.3 budgets: WN5 AT MASK (5.55E-6, bound 8.77E-6; was 2.10E-2; disjoint 1.02E-5 with no catastrophic bursts), WN2 1.59E-4 (was 1.32E-2; zero SignalLost), WN1 4.03E-3 with the only surviving errors in exactly the 2 Class-D bursts (was 2.31E-2), WN4 improved to 2.94E-6 bound 5.40E-6, WN3 0 errors unchanged.** WN13 hovers at the line post-fix (1.02E-5 canonical 6M, 1.26E-5 disjoint 3M) — Class A gone, benign tail owns it now — except one disjoint-6M specimen (w3/b5, 8,392 errors in ONE block): a fade cluster with the equalizer degraded between the dips (SER 0.5 at healthy and even up-faded gain), sitting exactly on the B3.2/B3.3 seam.

**Class B — genuinely marginal mean (WN6 spread over every burst)** — unchanged, stays the B3.3 program with WN7. **Class D — LLR overconfidence (2 WN1 bursts), mechanism found, fix next**: first-pass LLR scales are fixed per mode (BPSK 4·Re(y)) with no per-frame noise normalization, so fade-crushed frames emit confident garbage that dominates the Viterbi metric through the deinterleaver — a 15% sign-error stream that d_free=40 should decode clean comes out 49.6% wrong. Exonerations were experimental, not argued: turbo (new `DisableTurbo` option — identical errors), timing (accept@ diagnostics — identical lock noisy/noiseless), stream offset (decoded/payload cross-correlation — none). The per-frame σ² calibration fix is the B3.2 opener, and the benign near-miss tails on every sub-8PSK point (WN2's remaining 484, WN5's 18, WN13's ~24-66) are plausibly the same poison in small doses.

### 2026-07-24 (late) — Class D closed: the WID misread; WN1 Poor at ZERO errors

Class D was not LLR calibration — it was the WID. The autopsy rig's lock-info dump showed both corpses running wrong decode parameters for their whole bursts (K9 against a K7 transmission; Medium/K9 against Long/K7): the WID, read from ONE preamble super-frame, was corrupted by a −15 dB preamble fade and passed its weak checksum. Every alternative died by experiment first (turbo via the new `DisableTurbo` option, timing via the `accept@` diagnostic, stream misalignment by cross-correlation, decoder-order clustering against a random baseline, and per-frame LLR σ² derating — implemented, measured ineffective at ≤2×, reverted rather than landed unvalidated; the post-solve-residual-is-in-sample lesson is written into the evidence README for the benign-tail program).

Fix in two measured iterations (evidence: [evidence/2026-07-24-phase-b3-tail-autopsy/](evidence/2026-07-24-phase-b3-tail-autopsy/) `census-wid/`): soft-vote the WID across up to 5 pre-data super-frames (zero latency), then — after the census caught the unguarded vote breaking a wrong-CFO-bin burst that the old fail-and-relock path had saved — a vote-confidence gate (mean dibit margin ≥ 0.20, measured 4.6× separation) that restores the safety valve. **Final battery: WN1 Poor 0 errors / 3.04M bits / 248 bursts (bound 1.21E-6) — the second zero-error Poor point. WN2/WN5/WN13 bit-identical (the vote is neutral on healthy acquisitions), WN4 3.82E-6 at mask, WN3 0, AWGN 10/10 zero, static/Doppler zero.**

**B3 state after day one:** at mask — WN1 (0 errors), WN3 (0 errors), WN4, WN5; hovering at the line — WN13 (canonical 1.02E-5 at 6M, disjoint benign tail + one fade-cluster specimen); above — WN2 1.59E-4 (484 benign near-miss errors), WN6/WN7/WN8 (Class B / B3.3-B3.4), WN0 (B3.5). Next work, in order: **B3.1** repair the K=48 genie (σ²·Σweight over-regularization) so the WN2 margin work has a trustworthy bound; **B3.2** the benign near-miss tail (WN2's 484, WN5's 18, WN13's 24–66 + the fade-cluster block w3/b5 banked in `census-wid/`) — candidate mechanisms: fade-recovery tracking quality (the specimen shows SER 0.3–0.5 at healthy gain between dips) and the shelved LLR σ² calibration, which needs an out-of-sample noise statistic; **B3.3** 8PSK (WN7 relock latency; WN6 rides the same work); **B3.4** 16QAM; **B3.5** WN0 detector family; then **B4** — full-table gate runs and the MS110D_POOR_GATED=1 flip.
