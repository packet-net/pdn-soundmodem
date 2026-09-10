# MS110D adaptation-policy report: RLS vs NLMS, and the forgetting-factor A/B (2026-07-24)

The design §6 measured deliverable for Phase B (phase-b-plan §B2.4). Evidence: [evidence/2026-07-24-phase-b2/lambda-ab.log](evidence/2026-07-24-phase-b2/lambda-ab.log), all lines self-labelled `[RLS λ=…]`/`[GENIE]` by the harness; code `7cf28e0` on `ms110d-phase-b2`.

## 1. What the question became

Design §2.5 specified a DFE tracked by RLS with λ = 0.995 (≈200-symbol memory, set by the 1 Hz Poor coherence time), with NLMS as the fallback comparison. Two phases of measurement changed the architecture underneath the question:

- **NLMS is out of the signal path.** `Dfe.Nlms` has zero call sites in the demodulator: Phase A replaced per-probe NLMS refresh with weighted batch least-squares probe solves anchored to the current taps, and Phase B0 fixed weighted-RLS consistency so the RLS recursion could carry decision-directed adaptation between solves. The A/B that remains meaningful is the **λ policy inside RLS**.
- **Phase B2 moved cross-frame memory out of RLS entirely.** The probe-anchored tap trajectory (both bracketing solves known before any data symbol is equalized) plus the per-probe gain-1 phase re-anchor now own frame-to-frame tracking; the RLS recursion tracks only the intra-frame residual on top of the interpolated base, and its updates are gated by decision confidence.

The shipped policy is the frame-tied λ = 1 − ln10/U (memory ≈ 0.43·U symbols - a 10× down-weight per data span), a documented deviation from §2.5 (issue #64). The A/B asks: does restoring the coherence-set 0.995 buy anything on the B2 machinery?

## 2. Method

Matched-seed pairs (seed 500+wn, identical channel/noise realizations per arm) at the 400k-bit smoke budget on Poor at mask SNR, spanning the geometry range: U=48 (WN2, λ_tied = 0.952, the largest policy gap - 21 vs 200 symbols of memory), U=96 (WN4, 0.976), U=256 (WN5 BPSK and WN13 QPSK, 0.991 - the smallest gap). Genie pairs (perfect channel observation on the estimation side) for tracking-vs-detection attribution. Smoke-budget numbers: directional evidence for a policy choice, not gate evidence.

## 3. Result - the frame-tied λ stands

| Point | Mode | λ = 1 − ln10/U | λ = 0.995 | Δ |
|---|---|---|---|---|
| WN2 Poor +5 dB | BPSK r1/4, U=48 | 8.56E-5 (42 err) | 8.76E-5 (43 err) | none |
| WN4 Poor +10 dB | BPSK r2/3, U=96 | 7.04E-6 (6 err) | 1.06E-5 (9 err) | 0.995 worse |
| WN5 Poor +11 dB | BPSK r3/4, U=256 | 5.55E-6 (6 err) | 5.55E-6 (6 err) | none |
| WN13 Poor +11 dB | QPSK r9/16, U=256 | 5.55E-6 (9 err) | 9.25E-6 (15 err) | 0.995 worse |

λ = 0.995 is never better: two points identical, two moderately worse (error counts too small for the deltas to be individually significant, but the direction is consistent and nowhere positive). The physical reading matches the architecture: with the interpolated base and phase re-anchor carrying cross-frame memory, long RLS memory can only smear stale fading states into the current frame - the very effect the frame-tied window was chosen to avoid. **Decision: λ = 1 − ln10/U remains the default; `Ms110dDemodOptions.RlsForgettingFactor` stays as the measurement knob.** Re-open only if a future stage removes the interpolated base.

Where §2.5's physics argument went: the coherence-time reasoning was right for a *stand-alone* RLS tracker; the B2 receiver is not one. The anchored probe solves already integrate over exactly one probe-to-probe span, and the trajectory interpolation is the coherence-time-aware component.

## 4. Genie attribution

- **WN4** (and by extension the U≥96 BPSK family): genie ≈ measured within small-sample noise (8.22E-6 genie vs 7.04E-6 measured; deep fades hold 40-49 % of uncoded errors in ~10 % of bits in both). The residual WN4 error rate is **detection-limited at fade time**, not tracking-limited - perfect channel knowledge does not remove it. Further WN4 margin must come from fade-time LLR honesty/diversity, not better estimation.
- **WN2**: the pre-arming run (6da319b-era, same detector discriminators) measured genie WN2 at **zero errors in 491k bits** against 8.56E-5 measured - WN2's residual is **tracking-limited**, the natural B3.1 target. The current-code genie WN2 line (3.12E-2, identical error count across both λ arms) is an **instrument defect, not a channel measurement**: the genie's σ²·Σweight MMSE Gram term interacting with the K=48 heavy ridge (1.0 vs the 1e-3/0.15 of other modes) over-regularizes the clean-row solves. Logged as a B3.1 instrument-repair item; genie numbers for K=48 modes are unreliable until it lands. (The B0 genie calibration was validated on light-ridge modes only - the same lesson as B0's zero-forcing episode: every instrument needs re-validation per operating regime.) *[Corrected by B3.1, see the 2026-07-27 addendum: the σ²·Σweight hypothesis was refuted - the 3.12E-2 was one Class-C SignalLost-truncated burst, and the genie was never broken by the ridge.]*

## 5. Standing summary

Tracking architecture as of Phase B2: batch weighted LS probe solves (anchored ridge) → probe-anchored retrospective tap trajectory with common-rotation phase ramp → per-probe gain-1 phase re-anchor → decision-gated weighted RLS on the intra-frame residual (frame-tied λ) → collapse detection (bad-probe criterion + energy, one fresh solve per unhealthy episode). NLMS: retired from the signal path; retained in `Dfe` as API surface only.

## 6. Addendum at Phase B closeout (2026-07-27)

Corrections and follow-ups accumulated between this report's B2.4 delivery and the phase close ([phase-b-closeout.md](phase-b-closeout.md)):

- **§4's WN2 genie "instrument defect" is refuted (B3.1, PR #76).** The banked 3.12E-2 reproduced bit-exactly on the pre-B3.0 commit and the backported census localized ALL 15,328 errors to ONE SignalLost-truncated burst (19/20 bursts zero) - the genie's clean ring still fades, so the 1 s K=48 abandonment patience fired there exactly as in the normal path, and the wall-clock patience fix repaired both at once. The σ²·Σweight term itself is exactly the noise diagonal noisy rows carry implicitly - same expected Gram, trace, and ridge - so there was never an over-regularization to fix. On post-B3.0 code the same seeds read genie WN2 = 0 errors / 3.04M (bound 1.21E-6), confirming §4's tracking-limited attribution with a full-budget number. The meta-lesson stands unchanged: it was the *conclusion* ("defect in the genie math") that was wrong, caught by exactly the re-validation discipline the original text prescribed.
- **The λ verdict survived the rest of the phase.** λ = 0.995 was re-eliminated by measurement during the B3.2 WN2 autopsy (no change on the corpse; the recursion's deviations are discarded at frame end) - two independent measurements now back the frame-tied default. `RlsForgettingFactor` remains a report-only knob.
- **A second adaptation-policy knob joined it**: `TrackRidge` (B3.2) - the K=48 per-probe anchor ridge, shipped at 8.0 from a measured interior optimum (0.5…16 → 43/42/20/5/1/23 coded errors), mechanism error-confidence rather than error-count. Same report-only rule.
- **The §5 architecture description is superseded** for turbo-side tracking by the B3.3-B4.1 arc (TIR channel shortening with the floating-gain eigen refit, exact pre-cursor chains, per-segment h2, SPIKE-UP χ² floor pricing, the frozen-pass and late-lock salvage rungs); the closeout §2-§3 is the current record. The probe-solve/trajectory/RLS-residual pipeline this report describes is unchanged on the first-pass path.

Verdict unchanged: **RLS with the frame-tied λ stands; NLMS stays out of the signal path.**
