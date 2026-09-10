# Phase B3 — the catastrophic-burst tail autopsy (2026-07-24, evening)

The B3-entry re-baseline (../2026-07-24-phase-b3-entry/) showed the sub-8PSK Poor points tail-dominated: a minority of bursts holding essentially all the errors. This directory banks the autopsy that localized, mechanized, and (for two of the three classes) killed that tail. All runs on `ms110d-phase-b3`; pre-fix census on the B3-entry code (73bc96b), fixed census on the carrier-fit + signal-lost patience fixes.

## Instruments (committed with this evidence)

- `MS110D_MASK_BURST_LOG=prefix` — per-burst census line from every mask worker: errors, first/last error bit, collapse re-solves, turbo counters, uncoded/deep-fade deltas, end reason, and the burst's channel seed for exact reproduction.
- `Ms110dTailAutopsy` (`MS110D_AUTOPSY=1`, `_WN/_SEED/_WORKER/_BURST/_SNR/_OUT/_GENIE`) — rebuilds one mask burst bit-exactly and dumps every first-pass equalized data symbol next to its true transmitted constellation point, the per-frame diagnostics, the uncoded bit-error positions, and the true channel gain trajectory. `analyze_autopsy.py` correlates them (death map, error-rotation histogram, fade alignment, diagnostics timeline).
- `FrameDiagnostics` gained the probe correlation PHASE, the re-anchor residual, and the slerp's per-frame common rotation φ — the fields the rotation-invariant probe-gain statistics were blind to.
- `RefineCarrier` emits its per-group fit phases/weights (`refine@…` lines) when diagnostics are subscribed.

## The census (pre-fix, full §5.3 budgets, canonical seeds)

| WN | total errors | the tail | class |
|---|---|---|---|
| 5 | 67,991 / 24 bursts | ONE burst (w0/b3) holds 67,973 = 99.97% | A |
| 13 disjoint | 8,596 / 16 bursts | ONE burst (w1/b2) holds 8,559; first 18k bits only, then recovers | A |
| 2 | 40,292 / 124 bursts | 4 SignalLost truncations hold 39,808 = 98.8%; 48 benign bursts hold 430 | C + benign |
| 1 | 70,251 / 248 bursts | 10 SignalLost truncations hold 58,048 = 83%; 2 whole-burst deaths hold 12,203; **236 bursts have zero errors** | C + D |
| 6 | 143,109 / 12 bursts | spread over every burst in patches | B (marginal mean) |

## Class A — carrier false lock from a fade-poisoned acquisition refit

The WN5 corpse (w0/b3, channel seed 3506, ~90 s burst): dead from bit 3 to bit 135,135 at SER ≈ 0.55 in EVERY frame regardless of fade state; uncoded error rate 0.556 — *worse than chance*, i.e. anti-correlated; all 11 turbo attempts reverted; collapse detector quieter than on healthy bursts. The corpse dump's rotation histogram put **100% of errored symbols beyond ±90° at full magnitude** (|y| median 0.865 on errors vs 0.868 on correct): not noise, not collapse — pure rotation past the BPSK decision boundary.

Mechanism, inward: `omega` entered data at −4.97 Hz (rig injects zero frequency offset) and walked to −8.5e-3 rad/sample. The new `phi` diagnostic showed probe-to-probe rotation ≈ +2.5 rad while omega × 576 samples/frame = −3.75 rad — **the trim was reading the 2π-alias of the true rotation** and driving omega toward the stable false fixed point at −1 cycle/frame (−8.33 Hz). Between probes the data span sweeps a full turn, inverting half of every frame; at the probes themselves the per-probe phase re-anchor absorbs the rotation, so every probe statistic reads healthy. The aliasing basin edge is |Δf| > 4.17 Hz for the 120 ms U=256 frames (12.5 Hz for U=48). The WN13-disjoint specimen entered near the basin edge and escaped once |rotation per frame| < π — Class A is fatal deep in the basin, transient near its edge.

Outward: the initial acquisition read −0.53 Hz (fine). The **tail-superframe `RefineCarrier` refit** poisoned it: its sequential phase unwrap ran through a deep fade whose groups carry pure noise phase — downweighted in the regression but still forming the unwrap chain — and the chain random-walked ~3.5 turns down (`refine@9216`: +1 rad → −20 rad through 15 faded groups, then stable at −20…−22 on strong weights, i.e. back near the pre-fade phase mod 2π). The weighted regression fitted **−4.86 Hz through the manufactured ramp** and `_omegaAcquired` centred the ±3 Hz tracking clamp on the poisoned value — the clamp then *defended* the false lock. At noiseless SNR the same channel realization acquires at −0.9 Hz and decodes 0 errors: the outlier is noise×fade, not the channel.

**Fix**: `EstimateCarrierFit` — slope from weighted lag-1 phasor products (a faded group self-suppresses instead of poisoning its neighbours), lag-8 refinement with the branch resolved by the lag-1 value, intercept from the coherent de-rotated sum. No unwrap chain exists. Unit tests pin the corpse scenario (the fade window must read ≈0 Hz where the regression read −4.86) plus ramp recovery, branch resolution, and the no-signal refusal. The corpse decodes **0 errors** on the fixed code, turbo 11/11 converged; the tail refit reads −0.34 Hz through the same fade.

## Class C — mid-fade burst abandonment (SignalLost patience in probes, not seconds)

WN1/WN2 truncation deaths: 25 consecutive bad probes abort the burst — 3 s of patience at U=256 (never false-fired in any evidence) but **only 1 s at the K=48 modes' 40 ms frames**, and Poor-channel fades outlive 1 s. Every abandoned burst discards its remaining bits, interleaver contents included. **Fix**: the patience is now wall-clock (~4 s of frames, `⌈4·2400/(U+K)⌉`), uniform across modes. (The WN0 Walsh path has an analogous 1.2 s constant — left for B3.5, where WN0's detector program lives.)

## Post-fix census (same seeds, full budgets)

| WN | pre-fix | post-fix | state |
|---|---|---|---|
| 5 | 2.10E-2 | **18 errors, 5.55E-6, bound 8.77E-6 — AT MASK** | Class A dead |
| 5 disjoint | — | 33 errors, 1.02E-5 (largest burst 13) | benign tail straddles the line |
| 2 | 1.32E-2 | **484 errors, 1.59E-4, zero SignalLost** | Class C dead; benign tail remains |
| 1 | 2.31E-2 | **12,264 errors — ALL in the 2 Class-D bursts; 246/248 bursts zero** | Class C dead |
| 13 canonical (6M) | 5.70E-6 | 66 errors, 1.02E-5 | hovering at the line, benign-tail-owned |
| 13 disjoint (3M) | 2.65E-3 | 41 errors, 1.26E-5 | Class A dead |
| 13 disjoint (6M) | — | 8,507 errors — one fade-cluster burst (w3/b5), see below | B3.2/B3.3 specimen |
| 4 (gate point) | 4.99E-6 | **10 errors, 2.94E-6, bound 5.40E-6** | improved |
| 3 | 0 errors | **0 errors** | unchanged |

## Class D — the WID misread (SOLVED and FIXED: soft vote + confidence gate)

The 2 surviving WN1 bursts decoded 49.6% garbage from a 15% sign-error first-pass stream that rate-1/8 d_free=40 should decode clean. The elimination chain, each step an experiment: turbo (new `DisableTurbo` option — identical errors with it off), acquisition timing (new `accept@` diagnostic — identical lock noisy vs noiseless, and noiseless decodes 0), stream misalignment (decoded/payload cross-correlation — none at any offset), decoder-order error clustering (matches the random baseline), per-frame LLR σ² derating (implemented from the closing probe's pre-solve residual — the post-solve residual is an in-sample fit statistic that read 1.0 on a corpse running SER 0.3–0.5 — but ≤2× derating moved 6,081→6,044, so it was reverted; the idea stays shelved for the benign-tail program). What was left standing, confirmed by the rig's new lock-info dump: **the WID, read from ONE preamble super-frame's five Walsh dibits, was corrupted by a −15 dB preamble fade AND passed its weak checksum** — b26 locked K9 against a K7 transmission, b27 locked Medium/K9 against Long/K7 — so the Viterbi/deinterleaver ran wrong parameters for the entire burst while the wire-order telemetry (independent of decode parameters) stayed healthy.

**Fix, two measured iterations.** (1) Soft-vote the WID across up to 5 preamble super-frames (the section repeats identically in every one, all pre-data — zero added latency; Walsh correlation magnitudes summed per dibit — magnitudes, not phasors: super-frames a Rayleigh fade apart are not coherent). Both corpses decode 0 errors — but the census caught the unguarded vote breaking a previously-healthy burst (w1/b28, banked as `census-wid/wid-unguarded-wn1.log`): a wrong-CFO-bin acquisition (−18 Hz) turns every correlation to mush, and an argmax over summed noise beat the checksum, where the OLD single-read path failed it and fell back to re-search. (2) The vote-confidence gate restores that safety valve: accept only when the mean winner-vs-runner-up margin clears 0.20 — measured 0.532/0.681 on the fade corpses (pass, 0 errors), 0.116 on the wrong-CFO burst (reject → re-search → correct re-lock at 0.560 → 0 errors), 0.614 healthy. The threshold sits in a 4.6× gap.

## Final post-fix battery (margin-gated build, `census-wid/`)

**WN1 Poor: 0 errors / 3,039,488 bits, 248 bursts, 0 acquisition failures — bound 1.21E-6.** The second zero-error Poor point (with WN3). WN2 484 (bit-identical benign tail), WN5 18 (at mask), WN13 canonical 24, WN4 13 (at mask, bound 6.53E-6), WN3 0, WN13-disjoint-6M 8,490 (the fade-cluster specimen persists, as expected — it is not a WID case). Phase A: AWGN 10/10 zero errors, Static WID2 zero, Doppler ±75 Hz zero.

## The remaining catastrophic specimen — WN13 disjoint-6M w3/b5

One QPSK block (#8, frames 512–575) dead: a **fade cluster** (five deep dips inside 60 frames) with the equalizer degraded BETWEEN the dips — SER 0.3–0.5 at healthy and even up-faded gain (frames 506–510: gain 2–3, SER ≈ 0.5). Fade-recovery tracking degradation (the WN6/WN7 Class B family at QPSK) concentrated enough that one r=9/16 block's LLR stream is ~30% sign-error with unwarranted confidence. Both remaining B3 programs own a piece: B3.3 (re-lock latency through fade swings) and B3.2 (σ²-calibrated LLRs so the FEC survives what tracking cannot avoid).

## Files

- `census-prefix/` — pre-fix per-burst censuses (WN1/2/5/6/13-disjoint) + mask logs
- `census-fixed/` — post-fix censuses + mask logs (fix verification and gate regressions)
- `analyze_autopsy.py` — the corpse-dump analyzer (death map / rotation histogram / diagnostics correlation)
- Corpse reproduction: `MS110D_AUTOPSY=1 MS110D_AUTOPSY_WN=5 MS110D_AUTOPSY_WORKER=0 MS110D_AUTOPSY_BURST=3` on 73bc96b (dead) vs this commit (clean)
