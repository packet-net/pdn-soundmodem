# W1b — matched-filter bound on the exact channel (WN8 redesign program)

Registered 2026-07-31, fired by W1's pre-committed rule (canonical truth-injected 1.85E-4 ≥ 1E-4 — [../2026-07-31-wn8-w1/](../2026-07-31-wn8-w1/)), before any instrument code or run.

## Registration

**Question.** With the channel known exactly (the recorded per-sample Watterson realization) and every other symbol's interference cancelled exactly (genie), what coded BER does matched-filter detection + the outer code achieve on the WN8 Poor specimens? This is the **matched-filter bound (MFB)**: an upper bound on the performance of any receiver-only architecture at this operating point. It decides W1's open fork — whether the W1 truth-pass residual (100 canonical / 36 disjoint, concentrated in 4 of 22 blocks) is the sparse {cursor, echo, straddle} model's error or the waveform's own floor.

**Decision inputs.** W1's specimens and truth-frame diagnostics ([../2026-07-31-wn8-w1/](../2026-07-31-wn8-w1/)); the b39 verdict numbers; the b0 genie precedent (same-seed `WattersonChannel.Apply` at SNR=∞ reproduces the identical gain draw — gains are drawn before noise — giving the exact noiseless faded signal).

**Mechanism / instrument.** Test-side only — no demodulator involvement at all (no acquisition, no DFE, no chains):

- Reconstruct the corpse exactly (the autopsy rig's construction, same seeds/settings). `R = Apply(audio, ∞)` on a same-seed channel = the exact noiseless faded signal, so `r = rx − R` is exactly the additive noise; its measured variance cross-checks the channel's own σ.
- Per data symbol u, build the exact-channel unit-symbol response from first principles: complex envelope `c_u(t) = pathScale·Σ_k g_k(t)·PulseD_k(t − 4u)` on the 3/16-cycles-per-sample carrier, where Pulse is the modulator's own SRRC table (symbol n's pulse starts at sample 4n — `Ms110dModulator.Shape`), `PulseD_k` its fractional delay by τ_k (the channel's own windowed-sinc kernel, half=16), and g_k(t) the recorded 96 Hz truth linearly interpolated (the channel's own form).
- The statistic is bias-free by construction: `ŷ_u = x_u + P_u⁻¹·⟨r, e_u⟩` — the true symbol plus the noise projected onto the two real passband basis templates (responses to x=1 and x=j), whitened by the per-symbol 2×2 template Gram. Per-symbol noise covariance σ²·M_u⁻¹ gives **exact elliptical per-symbol pricing** (fades → honest erasures). Template imperfection (the channel's 2.2 kHz envelope LPF is not modelled) only de-optimizes the projection — a slightly **pessimistic** bound, the safe direction for both reads.
- Max-log LLRs over the 16 wire points (Mahalanobis metric, wire index = symbol number XOR scramble nibble, positive⇒0 convention) → `Ms110dFraming.DecodeBlock` (K7 tail-biting Viterbi + depuncture + deinterleave — the rig's own decode instrument) → per-block coded errors.
- **Calibration lanes (B0 rule)**: (i) reconstruct `R̂` from `BuildSymbols` + templates and report RMS(R̂−R)/RMS(R) over the data span — validates layout, pulse model, and alignment in one label-exact shot (a one-probe layout error is instantly catastrophic, not subtle); (ii) measured noise variance vs the σ implied by the SNR calibration; (iii) predicted vs measured `E|ŷ−x|²` on the noisy run.

**Budget.** One test-side file (~350 lines), corpse runs only (seconds–minutes each), no battery (nothing ships; the demod is untouched — hermetic suite + unchanged-demod note replace the §6 ladder).

**Kill/proceed rule (pre-committed).**

- MFB coded errors ≥ **1E-4-class (10× mask) on both specimens** → the floor is the waveform's own at this operating point: **registered infeasibility verdict for any receiver-only program — exit (iii)**, closed like WN7's B3.9 verdict, banked negatives extended.
- MFB ≤ **2E-5-class (2×) on both** → the W1 residual is the sparse model's; Wall 1 is fully attributable to channel-representation, and the program proceeds to W2 (residual decomposition) / W3 (bootstrap physics) / W4 (candidate pricing) with the MFB as the honest target. Carried caveat: the MFB is a **bound, not a construction** — it licenses the search, it does not promise the mask.
- Between: written stop-and-reassess (the escalation rule).

## Measurements (2026-07-31, `Ms110dMfbBound` in this PR — test-side only, demod untouched)

**Calibration lanes, all green** (in the summaries beside this file):
- Gain-draw identity: the same-seed ∞-SNR channel reproduces the noisy run's gain realization exactly (asserted bitwise).
- Layout: every data symbol's predicted wire point matches the modulator's `BuildSymbols` stream exactly (asserted; the lane earned its keep immediately — the first run caught a 32-chip EOT extension missing from the index formula as a clean 30°-adjacent-point failure, fixed before any number was read).
- Template model residual: RMS(rebuilt − exact) / RMS(exact) = **6.1% / 6.2%** — the unmodelled envelope LPF; enters only as projection pessimism, never bias.
- Statistic-noise closure: predicted vs measured `E|ŷ−x|²` — **5.94E-3 vs 5.93E-3** canonical, **7.08E-3 vs 7.00E-3** disjoint (~1%).

**The bound** (both specimens, 540,672 info bits each):

| Specimen | MF slicer SER (uncoded) | MFB coded errors |
|---|---|---|
| canonical (channelSeed 509) | 2.44E-3 (440/180,224) | **0 — every block clean** |
| disjoint (channelSeed 10509) | 3.39E-3 (611/180,224) | **0 — every block clean** |

## Verdict

**The waveform is not the floor — the pre-committed proceed branch fires.** With the channel known exactly and interference cancelled exactly, matched-filter detection leaves an uncoded SER of only 2.4–3.4E-3 with exact elliptical per-symbol pricing, and the r3/4 code + 7.68 s interleaver decode **every block of both specimens to zero** (0/1,081,344 pooled; 97.5% bound 3.4E-6 — under mask even as a rate statement, though the specimens are bounds, not §5.3 evidence). The full attribution ladder for WN8 Poor at +23 dB now reads: shipped coin-flip → oracle (label-trained segment time model) 496/136 → truth time-variation in the sparse-tap gauge (W1) 100/36 → **exact channel, exact cancellation: 0/0**. Wall 1 decomposes entirely into channel-representation error — none of it is the waveform's own floor at this operating point. Exit (iii) is off the table on current evidence; the program proceeds to W2 (decompose the W1 sparse-model residual), W3 (label-free bootstrap physics), W4 (candidate ceiling pricing). Carried caveat, per the registration: the MFB is a **bound, not a construction** — it licenses the search; the gap between "achievable with perfect knowledge" and "achievable" is exactly what W3/W4 must price.
