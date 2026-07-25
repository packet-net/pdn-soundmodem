# Phase B3.3 model front — TIR channel-shortening turbo solve (design, pre-registered 2026-07-25)

This note is written BEFORE the implementation, per the Phase B discipline. The mechanism it responds to is banked in `../2026-07-25-phase-b33-wn7-oracle/README.md`; this file registers the fix design, its acceptance instruments, and the decision thresholds, so the measurements that follow cannot be tuned into the design after the fact.

## The mechanism being fixed (recap of the banked autopsy)

The §B2.3 turbo re-equalization batch-solves the FF filter per frame with target `expected[u]` — a **full-inversion** target (desired impulse response = 1). Against the Poor channel's second path (2 ms ≈ 4.8 symbols at comparable magnitude) this is the wrong ask, three ways:

1. **The inverse is IIR; the FF is a 12-symbol FIR.** Inverting `1 + g·z^{-5}` needs the geometric series `1 − g·z^{-5} + g²·z^{-10} − …`; the 24-tap T/2 FF truncates it, leaving the measured echo TRAIN at d5 (374 frames) + d10 (251 frames) plus T/2 fractional sidelobes — which the single-lag chain-BCJR model cannot represent.
2. **Noise enhancement at notches.** Where the two paths cancel, inversion gain explodes: measured mean BCJR noiseVar 0.045 at |h1| = 0.847 → effective detection SNR ≈ 9.5 dB at a +19 dB operating point (13× the additive noise).
3. **The leftover echo hides under the significance floor.** After partial inversion the residual per-lag energy sits below the 0.04·|h1|² floor on 65% of frames — the chain BCJR runs as a per-symbol matched filter on two-thirds of WN7, converting none of the echo to diversity.

The oracle-labels ceiling (perfect labels through this model): WN7 w0/b0 burst total ≈ 15,136 coded errors (3.7E-2) — the model, not the labels, is WN7's binding constraint. WN6 w0/b0 oracle 75 (small model tail; labels do the rest).

## The fix: unit-tap-constrained target impulse response (channel shortening)

Classic TIR shortening (Falconer/Magee; standard DMT/TEQ practice), adapted to the existing batch-LS: instead of asking the FF to invert the channel, ask it to shape the channel to **exactly the model the chain BCJR equalizes exactly** — `b = [1, c]` at one lag `d`:

minimize over (w, b_d):  Σ_u | w·window_u  −  x_u  −  b_d·x_{u−d} |²

With the unit-tap constraint (target tap at lag 0 fixed at 1) this is **jointly linear in (w, b_d)** — it is an ordinary LS with one extra regressor column `x_{u−d}`, target `x_u`. The post-FF response is then ≈ `x_u + c·x_{u−d}` with `c = −b_d`: the echo stays AT its lag at full strength, the FF does timing/pulse matching without inversion (no train, no notch gain), and the chain BCJR converts the deliberate echo into path diversity — the §B2.2 thesis applied to the solve itself.

### Implementation shape (minimal blast radius)

The `Dfe` batch-LS already accumulates a Gram over [FF taps; FB taps], and the FB columns have exactly the semantics needed (`past[j] = x_{u−1−j}`, output `ff·window + fb·past`). So:

- **Row accumulation** (TurboCore): fill `past` with the wire-domain re-encoded/soft-expected history (preceding-probe tail for `u−1−j < 0` — known chips), instead of zeros. Row cost is unchanged (the Gram loop already runs over all n columns).
- **`Dfe.SolveTrainingTir(regularization, ffNoisePower, maxLag)`** (new): from one accumulation, solve
  - the **null candidate** — FF-only subset (today's solve, same math), SSE₀;
  - **each single-lag candidate** d ∈ 1…maxLag (maxLag = FbTaps: 12 for K=32, 22 for K=48, 6 for K=24) — FF + one FB column subset, SSE_d;
  and install the best, subject to the acceptance margin below. Subset solves are non-destructive submatrix Choleskys in ctor-allocated scratch (no stackalloc in loops — CA2014); ridge and anchor-to-current-taps semantics mirror `SolveTraining`, with λ scaled by subset trace/size. SSE is computed exactly against the unridged subset Gram/RHS (targetEnergy − 2Re(solᴴr) + solᴴG₀sol).
- **Acceptance margin (the no-rig-constant justification).** Adding one free complex parameter reduces SSE even on echo-free frames; the noise-only reduction, maximized over L ≈ maxLag candidate lags, is ≈ ln(L)·SSE₀/U (each candidate's noise-only ΔSSE is ~exponential with mean SSE₀/U). TIR activates only when ΔSSE > **4·ln(L)·SSE₀/U** — a 4× safety factor over the noise-only expectation, the same construction as the existing 0.04·|h1|² echo floor (~1.5× over 2·ln24·σ²/U). A real echo at comparable path magnitude gives ΔSSE/SSE₀ of tens of percent; noise gives ~1–4%. Scale-free, physics-stated, no rig constant.
- **Downstream, when TIR is active at lag d:** the echo estimation is **pinned to d** (the echo is there by construction — the solve established its significance with U rows of evidence) and the 0.04 floor is **bypassed** for it: h2 comes from the direct correlation at d, exactly the existing estimator at a fixed lag. The alternative — leaving the free search + floor — risks the worst case: an FF that deliberately left an echo in, and a BCJR told there is none.
- **When TIR is not accepted:** the path is today's, unchanged — FF-only solve, free echo search, 0.04 floor. AWGN/flat frames take this branch by construction of the margin.
- **Soft iterations (EM consistency):** soft labels put E[x] in the FB columns; the EM-correct Gram needs E[x·x̄] = |E[x]|² + Var on the FB **diagonal** (cross terms are fine under independence, FF columns are data). New `AddTrainingRow` overload takes the past-variance span and adds w·Var[u−1−j] to Gram[F+j, F+j]. Target-side variance only offsets SSE by a constant per candidate — ignored. Iteration 0 is hard-bootstrap (the #80 hybrid), so the first TIR solve of every block is exact regardless.
- **First pass untouched.** The first-pass DFE already IS a decision-feedback shortener (FB taps cancel post-cursors). Only the turbo solve was asking for inversion.

### What is deliberately NOT in this change

One change per measurement: the banked seg-sweep levers (16-segment h1, −10%; per-segment h2, −25% cumulative — both measured under full inversion) compose AFTER the TIR baseline is measured; under TIR the per-segment-h2 lever plausibly matters more (h2 becomes a real fading path coefficient, not an inversion residue). The two-adjacent-lag chain (d, d+1 — M² states, pre-registered in §B2.2 for the 4.8-symbol fractional echo) is the follow-on if the single-lag TIR leaves the fractional sidelobes as the new binding residual. The WN13 chain-BCJR-priors lever is a separate leg.

## Pre-registered acceptance

Instruments and pass criteria fixed before the first run:

1. **WN7 corpse w0/b0** (`MS110D_AUTOPSY_WN=7 _SNR=19 _SEED=507 _WORKER=0 _BURST=0 _ORACLE=1`). Banked baselines: first-pass 172,691 coded errors, shipped turbo ≈ unchanged, **oracle burst total ≈ 15,136**. TIR must move the **oracle** number decisively (it isolates model fidelity from label quality); watch mean BCJR noiseVar (banked 0.045 → should fall toward the additive floor) and the echo-state distribution (65% floor-zeroed → TIR-active fraction). If oracle errBits falls below the rate-3/4 cliff, shipped decodes should follow through the hybrid soft iterations.
2. **WN6 corpse w0/b0** (`_WN=6 _SNR=14 _SEED=506 _ORACLE=1`). Banked: normal 7,572, oracle 75. Oracle should → ~0; shipped should cut toward the labels-limited residual.
3. **Guard instruments:** WN13 canonical (at-mask, must not regress), the BPSK ladder (TIR will activate on the Poor channel for at-mask modes — the battery decides whether the diversity conversion helps or hurts there; a regression gates the change, and mode-gating TIR to QPSK/8PSK is the documented fallback of last resort).
4. **Then:** full WN7 + WN6 Poor mask runs (§5.3 budgets, disjoint seeds), the standard battery (WN13 canonical + disjoint, WN2×2, WN1×2, WN5/WN4/WN7 smokes, AWGN×10, static, doppler), Phase A regressions.
5. **Escalation (pre-registered):** if WN7 stalls above mask+2 dB after TIR + the seg levers + (if the residual demands it) the two-adjacent-lag chain, the FD-turbo architecture decision is taken explicitly — not more tuning.

Files land here as the measurements run: corpse before/after summaries + llrstats, echo/TIR state diagnostics, mask logs, battery log.
