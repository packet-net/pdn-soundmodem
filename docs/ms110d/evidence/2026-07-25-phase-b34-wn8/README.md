# Phase B3.4 — WN8 (16QAM) turbo inclusion (design, pre-registered 2026-07-25)

Written BEFORE the implementation, per the Phase B discipline.

## The map (measured on main 7b82ede, this branch's rig commits only)

- **AWGN @16 dB: at mask already** — 0 errors / 3.2–4.3M bits across every recent
  battery, uncoded 3.97E-3. Not this leg's problem.
- **Poor @23 dB: 4.96E-1 coded / 26.4% uncoded** (`corpse/census-wn8-poor-remap.csv`) —
  statistically identical to the B2-era baseline. Nothing in #76–#84 touched it,
  because none of it was allowed to: the FinishBlock gate excludes QAM16 from turbo.
- Corpse w0/b0 (`corpse/autopsy-summary-wn8-w0-b0.txt`, first QAM16 corpse — the rig
  gained wire-domain QAM16 truth this branch): every block uniformly dead
  (~24.5k/49k info bits), nearest-point SER 0.67, and the profiles
  (`corpse/wn8-constellation-profiles.txt`) say STRUCTURAL, not tracking:
  SER/|g|/MSE flat in u (head 0.634 vs tail 0.676 — no mid-frame decay), |g| ≈ 0.75
  everywhere including right after each probe solve, error split ~evenly
  radial/tangential, deep fades hold only ~13% of uncoded errors.
- **Genie ≈ normal** (268,541 vs 269,237 coded; same per-u profile): detector-limited
  outright — the B3.1 verdict that preceded WN6/WN7's B3.3 program, reproduced at
  16QAM. Estimation noise contributes nothing; first-pass levers are dead on arrival.

## Mechanism

WN8 is WN7's disease priced against half the min distance. The B3.3 arc measured the
8PSK failure as a residual channel-model perturbation (~15° class) that the DFE slicer
cannot carry, and fixed it detector-side: chain BCJR + TIR + SISO soft-feedback turbo
took WN7's oracle floor from SER 0.39 to 3.7E-5-class. QAM16's inner-ring min distance
is 0.366 vs 8PSK's 0.765 — the same model residual costs **6.4 dB more** against the
decision geometry, and 4 dB of extra mask SNR does not pay for it. The first pass was
never going to work; the detection machinery that saved the other Class-B waveforms
has simply never run on WN8.

## Design: wire-domain turbo inclusion

One lever: admit QAM16 to the existing turbo loop, exactly as shipped for PSK, with
the QAM-specific wiring below. No new knobs, no loop changes, PSK paths bit-identical.

1. **Wire domain throughout.** The QAM16 scramble is an XOR label permutation, not a
   ring rotation, so descrambled-domain equalization (the PSK convention) does not
   exist geometrically. The chain BCJR runs on wire samples with the `Qam16`
   constellation and identity bit labels; the scramble nibble n_t enters as (a) a
   per-symbol PERMUTATION of the prior array (prior for wire symbol w = data prior at
   w XOR n_t), (b) a per-bit SIGN FLIP of the output LLRs (data bit i = wire bit i XOR
   bit i of n_t). Both exact — XOR commutes with per-bit marginalization. The echo
   term h2·x[t−d] is naturally wire·wire; the PSK path's scrambler-rotation echo
   bookkeeping is not needed.
2. **Hard-label path** (`TurboReequalize`, also the oracle instrument): wireIndex =
   `Qam16[scrambler.NextQam(nibble, 4)]` — the modulator's own mapping (4 fetched bits
   MSB-first are the symbol number, no transcode table).
3. **Soft path** (`TurboReequalizeSoft`): E[x] = Σ_t p_t·Qam16[t XOR n_t] and — new
   for QAM — the true second moment E[|x|²] = Σ_t p_t·|Qam16[t XOR n_t]|², because XOR
   moves symbols BETWEEN rings. Variance = E[|x|²] − |E[x]|².
4. **The E[|x|²]=1 audit.** Every TurboCore estimation normalization that used
   count-based division on the PSK ring (rows Gram, h1/h2 correlations, the EM
   variance bump |h|²·(1−|E[x]|²)) is audited and, where the identity was load-bearing,
   replaced with the accumulated second moment. This is the one real math delta of the
   leg; each site gets the audit comment.
5. **Scale trap stays resolved by construction**: the first-pass 10.0-scale LLRs reach
   the turbo only through the first Viterbi decode (scale-invariant); from iteration 1
   the SISO consumes chain-BCJR output (calibrated). The existing throw at the shared
   PSK LLR sink stays — QAM16 first-pass LLRs keep their own path.
6. **Cost**: M² = 256 branch pairs per chain step, ~5× the 8PSK cost, WN8 blocks only.

## Pre-registered acceptance — the oracle GATES the leg

The ceiling arithmetic is genuinely uncertain: at the fadecross-era BCJR noise floors
(healthy ~0.009, weak-quartile ~0.025), QAM16's inner-ring d² = 0.134 gives d²/2σ²
between ~2.7 and ~7.4 — anywhere from hopeless to comfortable. So:

1. **Oracle ceiling first** (`MS110D_AUTOPSY_ORACLE=1` on the WN8 w0/b0 corpse, which
   exercises the identical wiring): if the oracle coded errors land in a
   mask-reachable class (≤ ~1E-3 corpse BER — the WN6/WN13 precedent had oracle ≈ 0
   before their turbo shipped), proceed. If not, the model class does not support
   16QAM at this floor: record the negative with the measured floor, ship the rig +
   oracle wiring as instruments, and the leg pivots to a registered model-front design
   (no lever soup).
2. **Shipped turbo**: corpse w0/b0 coded errors fall materially (the first decode is
   26% wrong info bits — INSIDE the basin range WN7 recovered from); no PSK corpse
   moves a bit: WN7 w0/b0 reproduces 72,666 / 7c/4r / oracle 15 exactly, WN6/WN13sp
   guards 0/0.
3. **Battery gates the merge**: full standard set (all PSK legs bit-identical is the
   expectation, but the battery proves it), WN8 Poor smoke improves materially, AWGN
   WN8 holds 0. WN8 Poor at FULL §5.3 budget both families is the leg's headline
   number — reported either way; whether it reaches the mask decides B3.4's status,
   not whether this lever ships.
4. Amendments recorded if measurements force changes, per the segnoise template.

Files land here as the measurements run.
