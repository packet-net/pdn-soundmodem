# MS110D Phase B — formal closeout (2026-07-27)

**Phase B is closed.** Eight of the ten Poor-channel points (WN0–6, WN13) hold design §6's at-mask bar as **hard gates, default-armed**, on canonical and disjoint seed families at full §5.3 budgets, with every Phase A regression green throughout. The remaining two points close with **written, current-binary, measured-only verdicts** under the plan §4 escalation rule ("a written stop-and-reassess, not more tuning"): WN7 at 2.56E-5/1.48E-5 against the 1E-5 mask — the waveform's own fade-lottery floor, every remaining lever class measured dead — and WN8 at coin-flip, doubly walled by a 25–92× true-label model ceiling and an uncrossable bootstrap basin. The §6 row-B hard gate as written ("WN0–8+13 at mask") is therefore met 8-of-10, not 10-of-10, and this document is the honest record of both the eight and the two.

The program ran 2026-07-24 → 2026-07-26 as PRs #70–#95 and #97 on `packet-net/pdn-soundmodem` (contiguous except #96, which was not part of this program), staged B0 (instruments) → B1 (autopsies) → B2 (science core) → B3 (family closure) → B4 (gate flip), exactly as [phase-b-plan.md](phase-b-plan.md) registered before any of it ran. The plan's status log is the leg-by-leg narrative; this closeout is the summary of record.

## 1. Where the phase started and where it ended

Entry baseline (Phase A closeout, 2026-07-23, first complete Poor bank ever measured) against the final battery (the B3.8 "b38" battery, PR #95, which is the binding byte-identity baseline — every later leg reproduced its censuses exactly):

| WN | Mode | Poor SNR | Phase-A baseline | Final canonical | Final disjoint | Disposition |
|----|------|----------|------------------|-----------------|----------------|-------------|
| 0 | Walsh 75 bps | −1 dB | 8.13E-2 | **0 errs** (bound 1.22E-6) | **3** (bound 2.92E-6) | **GATED** |
| 1 | BPSK r1/8, U=48/K=48 | +3 dB | 2.85E-2 | **0** | **0** | **GATED** |
| 2 | BPSK r1/4, U=48/K=48 | +5 dB | 3.67E-2 | **30 / 6.08M = 4.93E-6** | **29** (bound 6.84E-6) | **GATED** (6M default budget) |
| 3 | BPSK r1/3, U=96/K=32 | +7 dB | 8.68E-3 | **0** | **0** | **GATED** |
| 4 | BPSK r2/3, U=96/K=32 | +10 dB | 2.36E-5 | **0** | **3** | **GATED** |
| 5 | BPSK r3/4, U=256/K=32 | +11 dB | 2.18E-2 | **23** (bound 5.32E-6) | **0** | **GATED** (6M) |
| 6 | QPSK r3/4, U=256/K=32 | +14 dB | 1.29E-1 | **35 / 6.49M = 5.39E-6** | **39 = 6.01E-6** | **GATED** (6M; pooled bound 7.15E-6) |
| 13 | QPSK r9/16, U=256/K=32 | +11 dB | 6.16E-4 | **0 / 3.24M** | **0 / 6.49M** | **GATED** (9× margin) |
| 7 | 8PSK r3/4, U=256/K=32 | +19 dB | 4.66E-1 | 83 = **2.56E-5** | 48 = **1.48E-5** | **VERDICT: measured-only** (§4) |
| 8 | 16QAM r3/4, U=256/K=32 | +23 dB | 4.96E-1 | 4.96E-1 | 4.97E-1 | **VERDICT: measured-only** (§4) |

Alongside, in the same batteries every time: **AWGN 10/10 points zero errors at full §5.3 budgets, static WID2 (0/3/9 ms) zero, Doppler ±75 Hz 3/3 zero, hermetic suite 697/0** (105 env-gated skips). The gate flip is rate-driven, not draws-driven (the B4 registration's criterion): both families §5.3-green AND the pooled both-family 97.5% bound under mask AND ≤5% per-family false-red at the default budget — which is why WN2/WN5/WN6 carry 6M-bit default budgets and why WN6 only flipped after B4.1 bought it margin (false-red ~32% → ~0.02%).

Accept rule per point (§5.3): ≥3×10⁶ payload bits, ≥600 s simulated, zero acquisition failures; ≥30 errors → direct BER ≤ 1E-5, else the 97.5% Poisson upper bound clears 1E-5. All evidence through the `MS110D_MASK_LOG` chain, canonical + disjoint (seed+10000) families always.

## 2. What it took — the mechanism ledger

Phase B landed no fix without a written mechanism, and the mechanisms are the phase's real inventory. Per point:

- **WN0** (8.1E-2 → gated): coherent single-reference correlation wasted the two-path channel twice (B1: static 2-path is FREE; the echo became self-noise). The DD-MRC Walsh RAKE (B3.5) converted the echo to signal (13.3×); the joint count vote closed the last single-read acquisition field (all-same-symbol mush decodes to valid EncodeCount(31)); and the genie-gain oracle's fidelity check — a registered consequence clause doing its job — exposed the real tail: **acquisition locks the 2 ms echo on ~31% of bursts and the direct path sat outside the causal finger window**. The symmetric −6..+6 window (B3.5b) ended it: every echo-locked corpse to zero.
- **WN1** (2.85E-2 → 0/0): mid-fade burst abandonment (frame-scaled SignalLost patience = 1 s at K=48; Poor fades outlive it) → wall-clock ~4 s; and the Class-D paradox — whole bursts decoding 50% garbage through healthy telemetry — was a **WID misread under a preamble fade** beating the weak checksum; fixed by the 5-super-frame soft vote with a confidence gate.
- **WN2** (3.67E-2 → gated at 6M): the U=48 turbo exclusion (B2.3), the abandonment fix, and the K=48 anchor-ridge 1.0→8.0 (B3.2) — the solve's cross-frame memory, whose measured mechanism is error-*confidence*: the anchored equalizer coasts through fades and errors self-report as soft erasures.
- **WN3** (8.7E-3 → 0/0): the B0 weighted-RLS consistency fix alone — the weight/P asymmetry was progressively freezing adaptation (issue #64's remainder, closed by measurement day one).
- **WN4** (2.36E-5 → 0/3): the B2 exit gate — probe-anchored per-symbol tap trajectory + chain BCJR; the point that proved the machinery before the grind.
- **WN5** (2.2E-2 → gated at 6M): **carrier false lock** — the RefineCarrier refit's phase unwrap random-walked through a deep fade and manufactured −4.86 Hz, locking the stable 2π-alias at −1 cycle/frame where every probe statistic reads healthy; replaced by `EstimateCarrierFit` (lag-product slope — faded groups self-suppress; no unwrap chain exists to poison).
- **WN6** (1.29E-1 → gated at 6M): a rate-3/4 code cliff, not a defect (B1) — descended by the SISO soft-feedback turbo (B3.3), TIR channel shortening + chain-BCJR priors (corpse 7,572 → 0), the floating-gain eigen refit, and finally the B4.1 SPIKE-UP χ² floor pricing (57/57 → 35/39), the third form of a lever class that failed twice at battery scale before shipping with zero collateral.
- **WN13** (6.2E-4 → 0/0, 9× margin): B2 machinery + the B3.3 priors killed its converged-residual class outright; its fade-cluster specimen (8,392 errors in one block) fell from "probe-information-limited" to zero when soft feedback landed — the existence proof that broke B3.2's verdict.
- **WN7** (4.66E-1 → 2.56E-5, 18,000×, zero reverts): the longest arc in the program — the B1 whole-frame phase smear; the oracle-labels split into a labels front and a model front; TIR channel shortening (the FF was inverting the second path — effective detection SNR 9.5 dB at a +19 dB operating point); exact pre-cursor chains behind the mini-probe's base-16 alias (B3.7 — the one channel shape the causal chain couldn't express, hidden behind a periodic-probe artifact); the scaffold discovery (B3.6 — convergence rides healthy-frame count, not label percentage) with the frozen label-free salvage rung; and the late-lock second salvage rung (B3.8 — when the delayed path dominates the cursor, shift the equalizer window by the accepted lag). B3.6→B3.8 alone: 6,760×/13,180×.
- **WN8** (4.96E-1 → unchanged): the complete wire-domain QAM16 turbo was built and proved as an oracle instrument (B3.4) — and the measurements that followed are the verdict in §4.

## 3. The instruments (built first, trusted only after calibration)

The B0 rule — audit the instruments as hard as the modem — held for the whole phase; four separate instrument defects were caught by their own validation lanes before they could mislead (the genie's zero-forcing episode, the K=48 "genie defect" that was actually Class C, the eigen-TIR selection gauge, the WN0 oracle fidelity RED). The standing kit, all env-gated, all behavior-neutral when unset:

- **Channel-truth genie** (`MS110D_MASK_GENIE=1`) — the achievability bound of the current detector under perfect channel knowledge; the tracking-vs-detection arbiter.
- **Per-burst census** (`MS110D_MASK_BURST_LOG`) — made every tail countable; the byte-identity discipline for non-target waveforms rests on it.
- **Corpse rig** (`Ms110dTailAutopsy`, `MS110D_AUTOPSY_*`) — rebuilds any mask burst bit-exactly; the unit of diagnosis for the whole B3 arc.
- **Oracle-labels turbo** (`MS110D_AUTOPSY_ORACLE=1`) — one extra re-equalization pass trained on true info bits after the shipped pipeline; passive (the ship side of an oracle run reproduces the plain corpse bit-for-bit). Its authority ended the phase *revoked as a bound* (§4) but it remains the per-block reference that priced every model-front decision from B3.3 on.
- **LLR statistics** (`autopsy-llrstats`: first/final/oracle passes), **scatter diagnostics**, **clean-channel rig** (`MS110D_AUTOPSY_CLEAN=1`), **turbo-frame instrument** (per-frame anchors/noise floor/FF energy), **first-decode and final-decode dumps** — the anatomy set behind B3.9.
- **Report-only A/B knobs**: `RlsForgettingFactor` (λ), `TrackRidge` — never shipped as tuning, always as measurement.
- **Harness**: `MS110D_MASK_WORKERS` intra-point parallelism, `MS110D_MASK_SEED_OFFSET` disjoint families, three-lane detached battery form (~33 legs, ~33 min wall; lane logs in [evidence/2026-07-26-phase-b38-wn7-anchortrack/battery/](evidence/2026-07-26-phase-b38-wn7-anchortrack/battery/)).

## 4. The two verdicts

**WN7 — measured-only at 2.56E-5 canonical / 1.48E-5 disjoint (mask 1E-5), zero reverts both families.** The detection/salvage story closed in B3.8 (every block of all 16 bursts converges). The model story closed in B3.9 ([evidence/2026-07-26-phase-b39-wn7-modeltail/](evidence/2026-07-26-phase-b39-wn7-modeltail/)): the M0 anatomy of all 131 residual battery errors found the pre-registered honesty gate firing twice over — pooled oracle-matched mass 80 exceeds the 64-error pooled mask budget, **and the oracle ceiling itself measures worse than ship (142 vs 131)** — the shipped iterated decoder has surpassed its own bounding instrument. Every residual error is a single contiguous 14–24-info-bit decoder error event; wrong wire bits carry mean |LLR| ≈ 1 against 12–68 for right bits (honest erasures — nothing mispriced); coded outcome is decoupled from wire quality (ship's wire is *cleaner* than the oracle's in all four blocks where only ship has the event). The block interleaver scatters each deep fade into isolated weak bits and an error event is a co-location lottery over that population. All three banked directions measured dead: h1-model (~2% of the priced floor), per-segment echo (the oracle carries it and loses), tail pricing (no signature exists). **The residual is the waveform's own floor at this channel/SNR; reducing it requires added information — diversity, retransmission, outer coding — outside this demodulator.**

**WN8 — measured-only at coin-flip, both walls standing.** ([evidence/2026-07-26-phase-b39-wn8-verdict/](evidence/2026-07-26-phase-b39-wn8-verdict/), re-measuring B3.4 on the fully rebuilt chain.) Wall one: the true-label model ceiling is **9.2E-4 canonical (92× mask, 10 of 11 blocks nonzero) / 2.5E-4 disjoint (25×)** — none of the Phase B model work moved it; the 16-point constellation at 23 dB on Poor is beyond this equalizer-plus-chain model class even with perfect labels. Wall two: the bootstrap basin — the first decode is coin-flip (~49.8%) and B3.4 measured every label-free crossing attempt asymptoting back to 50.1% at cap 96 (a self-consistent wrong attractor the revert discipline correctly refuses). The lever class required — frequency-domain equalization, pilots-in-data, per-symbol tracking — is waveform-processing redesign, out of Phase B scope by the pre-committed rule.

## 5. Banked negatives — measured, do not re-try

Each of these cost a registered leg and is closed by measurement, not opinion. Future work re-opens one only with new mechanism evidence:

- **The oracle-labels pass is a reference, not a bound** — ship beats it where the iterated fixed point wins (B3.9); never use it as a ceiling in a registration.
- **Label-side WN7 arms are arithmetically excluded** (matched 80 > 64 pooled even at 100% excess conversion) and **no mispricing exists at the WN7 tail** (the SPIKE-UP family has nothing to reprice there).
- **Per-segment echo in the converged loop** — the oracle carries per-segment h2+h2b and loses to ship (B3.9).
- **Anchor-value processing** (gauge-stitched/cubic anchor tracks): a wash at operational spacing; frozen h1 model error ~2% of floor (B3.8 E2).
- **Margins cannot stabilize a ceiling block's fixed point** — only structural scoping (the salvage rungs) retains exactly (B3.8).
- **Start-side first-pass LLR calibration is dead under this loop structure** — improves starts, perturbs marginal soft trajectories net-negative; measured twice with different victims (B3.3 basin, basin2).
- **Restart ensembles cannot manufacture scaffold** (B3.6): a 2% info flip re-encodes to ~7–8% uniform wire damage.
- **Per-bin FD MMSE is dominated by the exact chain BCJR at equal channel knowledge** (B3.7 architecture verdict) — the FD-turbo escalation resolved into a channel-knowledge question, not an architecture change.
- **Burst-consensus lag constraints are anti-mechanism** (B3.7 E1′: the free solve's per-frame lag choices are frame-local channel truth).
- **16-segment h1 under TIR** regresses (inversion-regime-specific lever); **±1 non-causal anchor smoothing** closes LLR-mass gaps but no detection gap and taxes the healthy tail (B3.2).
- **RLS λ = 0.995 is never better** than the frame-tied λ on this architecture; **NLMS is out of the signal path** ([rls-vs-nlms-report.md](rls-vs-nlms-report.md)).
- **Raw per-segment noise pricing without the χ² gate** damages marginal waveforms (B3.3 segnoise → shippable only as B4.1's SPIKE-UP form).
- **The QAM16 ceiling is immovable by this equalizer+chain class** (B3.10), and its **bootstrap basin has no label-free crossing** (B3.4).

## 6. Guard-pin registry — binding on any future demod change

Any change touching `Ms110dDemodulator`/`Dfe`/BCJR/turbo paths re-proves these before merge (they are cheap; the battery is not):

| Guard | Pin |
|-------|-----|
| WN7 corpse w0/b0 (channelSeed 508) | **0 coded / turbo 11c/0r/4v / oracle b5:15** |
| WN7 corpse w1/b0 (channelSeed 1000508) | **20 coded / 11c/0r/5v** |
| WN6 corpse w0/b0 | **0 coded / 11c/0r/0v** |
| WN13sp corpse (SEED=10513, w3/b5) | **0 coded / 11c/0r/0v** |
| WN0 corpse w2/b97 (SEED=500) | **0 coded** |
| Hermetic suite | **697 passed / 0 failed** (105 env-gated skips) |

Corpse seed composition: `channelSeed = SEED (default 500+wn; disjoint family +10000) + worker·10⁶ + burst·10³ + 1`. Never share an OUT dir between same-(wn,worker,burst) runs from different SEEDs — the autopsy filenames collide. Full-battery baseline for byte-identity of non-target waveforms: the b38 battery ([evidence/2026-07-26-phase-b38-wn7-anchortrack/battery/](evidence/2026-07-26-phase-b38-wn7-anchortrack/battery/) + the #95 evidence README). A demod change merges only behind: registration first, corpse before battery, full three-lane battery before merge, non-target censuses byte-identical, both seed families, Phase A regressions green.

## 7. Evidence index

Every leg's registration, measurements, and verdict live in its dated evidence dir; the plan status log links them in narrative order. The chain: [2026-07-24-phase-b0](evidence/2026-07-24-phase-b0/) (instruments) → [phase-b-autopsies.md](phase-b-autopsies.md) (B1) → [2026-07-24-phase-b2](evidence/2026-07-24-phase-b2/) (science core) → [phase-b3-entry](evidence/2026-07-24-phase-b3-entry/), [phase-b3-tail-autopsy](evidence/2026-07-24-phase-b3-tail-autopsy/), [phase-b31-genie](evidence/2026-07-24-phase-b31-genie/), [phase-b32-wn2-anchor](evidence/2026-07-25-phase-b32-wn2-anchor/), [phase-b32-wn13-smoothing](evidence/2026-07-25-phase-b32-wn13-smoothing/), [phase-b33-wn7-oracle](evidence/2026-07-25-phase-b33-wn7-oracle/), [phase-b33-siso](evidence/2026-07-25-phase-b33-siso/), [phase-b33-tir](evidence/2026-07-25-phase-b33-tir/), [phase-b33-basin](evidence/2026-07-25-phase-b33-basin/), [phase-b33-twolag](evidence/2026-07-25-phase-b33-twolag/), [phase-b33-fadecross](evidence/2026-07-25-phase-b33-fadecross/), [phase-b33-basin2](evidence/2026-07-25-phase-b33-basin2/), [phase-b33-segnoise](evidence/2026-07-25-phase-b33-segnoise/), [wn2-cleantriplet](evidence/2026-07-25-wn2-cleantriplet/), [phase-b34-wn8](evidence/2026-07-25-phase-b34-wn8/), [phase-b35-wn0](evidence/2026-07-26-phase-b35-wn0/), [phase-b4-gate](evidence/2026-07-26-phase-b4-gate/), [phase-b41-wn6floor](evidence/2026-07-26-phase-b41-wn6floor/), [phase-b35b-wn0genie](evidence/2026-07-26-phase-b35b-wn0genie/), [phase-b36-wn7loop](evidence/2026-07-26-phase-b36-wn7loop/), [phase-b37-wn7-fdturbo](evidence/2026-07-26-phase-b37-wn7-fdturbo/), [phase-b38-wn7-anchortrack](evidence/2026-07-26-phase-b38-wn7-anchortrack/), [phase-b39-wn7-modeltail](evidence/2026-07-26-phase-b39-wn7-modeltail/), [phase-b39-wn8-verdict](evidence/2026-07-26-phase-b39-wn8-verdict/).

## 8. Reproduction

```bash
dotnet build -c Release
dotnet test                          # hermetic suite; Poor gates WN0–6+13 hard-armed by default
./scripts/run-masks.sh all           # full evidence sweep (detached recommended; see plan §4 OOM notes)
MS110D_MASK_SEED_OFFSET=10000 ...    # disjoint-family cross-check
```

The gated Poor points assert §5.3 in-process by default (`Poor_Channel_Mask_Gate`, 6M default budgets for WN2/WN5/WN6); `MS110D_POOR_GATED=1` force-arms the measured pair too (WN7/WN8 — expected red, they are measured-only). Batteries run as three concurrent detached lanes; on this class of box, launch detached (`setsid`), mark fleets expendable, and never rebuild mid-battery.

## 9. What Phase B leaves behind

For Phase C (or any successor): the two measured-only verdicts define exactly what a next program would have to bring — **WN7 needs added information** (diversity combining, retransmission/ARQ integration, outer coding — the demodulator is at its floor), **WN8 needs waveform-processing redesign** (FD equalization, pilots-in-data, per-symbol tracking — plus a bootstrap story the basin measurements say no label-free iteration provides). Neither is a tuning question; both are architecture questions, and both now have the instruments, corpses, and honest ceilings a registration would need on day one. The 75 bps fallback mode the whole ladder degrades to is certified on the channel it exists for, and the modem the network actually uses (BPSK/QPSK service modes) is hard-gated at mask on the hardest standard channel in the spec.
