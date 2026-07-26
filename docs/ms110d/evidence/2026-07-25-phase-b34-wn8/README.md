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

---

## Amendment 1 (same day): the oracle gate PASSES at 9.3E-4; the shipped loop stalls
## at coin-flip start — probe rows join the QAM16 re-solve

Measured (`corpse/autopsy-summary-wn8-w0-b0-oracle.txt`): **oracle 505/540,640 = 9.3E-4**
(per-block 0–132; b5 fully clean) — inside the pre-registered ≤ ~1E-3 proceed bar. The
model class supports 16QAM detection at this floor with true labels. The WN7 corpse
reproduces **72,666 / 7c/4r / oracle 15 exactly** — PSK bit-identity holds.

The shipped loop measured a third outcome the design did not anticipate: **0c/11r,
coded errors identical to first-pass** (269,237). The design note's "26% wrong info
bits — inside the basin range" was an error: 26.4% is the CODED-STREAM bit error rate;
the first DECODES are 49.5% wrong info bits — coin-flip, outside any basin. The
turbo-iter trajectories show a strong initial pull (b0: 23,303 → 13,881 by i2; b5:
23,341 → 11,936 by i3) that stalls into a wander plateau around 12–18k decode-changes:
label-driven re-estimation from ~65%-wrong symbol labels dilutes ĥ toward zero, the
chains detect through a garbage equalizer, and the decode quality that would repair the
labels never arrives — a self-consistent stall. (The first pass has the same disease at
its root: the QAM16 DD gate (0.15)² passes ~nothing at MSE 0.6, so its per-probe solves
carry K=32 rows against 36 taps on a fading channel — excitation starvation is the
measured "gain-ramp" item from the B2 note.)

**The lever (one, no knobs)**: the bounding mini-probes are label-free truth sitting at
both ends of every frame, and the turbo re-solve currently ignores them. Join their
rows — those whose feedback history lies wholly inside the probe (K − fb = 20 rows per
probe, 40 per frame) — to every QAM16 re-solve at weight 1.0. The re-solved equalizer
then has a truth floor at every iteration (probe-grade at coin-flip labels, oracle-grade
as labels improve), which is exactly what the measured stall lacks. PSK paths untouched
(QAM16-conditional).

**Pre-registered acceptance**: corpse shipped < 269,237 with ≥ 1 block converging;
oracle holds ≤ 505; WN7/WN6/WN13sp guards unchanged. If the corpse does not move: the
stall localizes to the label-diluted h1/h2 CORRELATIONS (not the solve), and Amendment 2
targets those — the failure is informative either way.

## Amendment 2 (same day): probe rows alone do NOT move the shipped loop — iteration 0
## must be label-free end-to-end

Measured (`corpse/a1-summary-oracle.txt`): shipped **identical** (0c/11r, 269,237),
oracle 505 → 485 (statistically similar, bar held). The consequence clause executes:
the solve was not the binding constraint — the h1/h2 correlations are. The analysis
closes both label-driven escape routes at a coin-flip start: HARD labels bias every
correlation multiplicatively toward zero (ĥ ≈ L·h at label accuracy L ≈ 0.35), and
SOFT labels are a fixed point AT zero (E[x] ≈ 0 ⇒ correlations ≈ 0 ⇒ chain LLRs ≈ 0 ⇒
the SISO returns nothing — the reason the hybrid bootstrap exists). At bootstrap, NO
label-derived channel quantity is usable; the probes are the only truth.

**The lever (Amendment 1's probe rows stay — they are load-bearing for iterations ≥ 1):
QAM16 iteration 0 becomes a label-free bootstrap detection pass.** The re-solve uses
probe rows + the ridge anchor only (no data rows — garbage-label rows act as a
gain-shrinking pseudo-ridge, measured useless in A1); h1 comes from two probe-edge
anchors (correlations over the bounding probe spans through the re-solved equalizer,
linear across the frame — the first pass's model class, honestly priced); the chains
run echo-free (delay 1, h2 = 0) with noiseVar from the probe-span residual. The chain
BCJR is then an exact-MAP detector on a probe-anchored channel — strictly better
calibrated than the rank-starved first-pass slicer whose decode currently seeds the
loop. Its decode becomes the label set for iteration 1, where the standard label-driven
machinery (now probe-row-completed) takes over; the oracle proves that machinery
converges from good-enough labels.

**Pre-registered acceptance**: unchanged from Amendment 1 (corpse < 269,237, ≥ 1
convergence, oracle ≤ 505-class, PSK guards bit-identical). If the bootstrap decode is
still coin-flip: the probe-anchored channel view itself cannot carry 16QAM detection at
this fade rate, the leg records the negative with both amendments banked, and the
model-front design (within-frame channel observability without labels) is the registered
follow-on.

## Amendment 3 (2026-07-26, small hours): the bootstrap breaks the stall — the loop now
## DESCENDS monotonically and hits the cap mid-climb; QAM16 iteration cap 96

Measured (`corpse/a2-summary-oracle.txt`, `corpse/a2-trajectories.txt`): shipped totals
unchanged (0c/11r — every block cap-reverted) but the loop DYNAMICS transformed. The
label-free bootstrap replaced the stall-at-15k plateau with a monotone descent: b0's
decode-changes run 24,712 → 16,431 → … → 2,620 across i0–i23 (halving roughly every 7
iterations), b5 to ~3,000, all 11 blocks alike. The cap-state llrstats show the climb's
signature: the chain stream at i23 carries MORE wrong-sign bits than first (36% vs 25%)
but their mass collapsed 7× (b0: 54,147 → 7,504) while right-mass grew 23% — wrong
decisions became near-erasures, which is exactly the mid-basin state the WN7 campaign
measured on converging blocks. The oracle (485-class, 9.3E-4) proves the attractor the
descent is heading for. This is cap starvation, not a wander plateau.

**The lever: iteration cap 24 → 96 for QAM16 only.** The 24-cap's own rationale ("the
costs are asymmetric: a cap-limited revert throws away ~10k repaired errors per block,
extra iterations only cost wall-clock on the rare non-converging blocks") argues the
extension: at the measured halving rate, single-digit churn needs ~i70–90. PSK keeps 24
bit-identically (its blocks converge by i15 or wander — measured across the whole B3.3
arc).

**Pre-registered acceptance**: ≥ 1 block reaches an exact fixed point within 96 and the
corpse shipped total falls materially; oracle unchanged (the oracle path has no cap);
PSK guards bit-identical. If blocks asymptote into a churn floor without converging: the
final-state stream at cap is the evidence (llrstats), the revert stands, and the leg
records how far the basin reaches with the label-free start.

## Amendment 4 (same day, FINAL): the churn floor is a self-consistent WRONG attractor —
## the bootstrap basin is uncrossable; the oracle instrument ships, the shipped gate closes

Cap 96 measured (`corpse/a3-summary-cap96-oracle.txt`, `corpse/a3-b0-trajectory-cap96.txt`):
the descent asymptotes at ~800–1,000 decode-changes from ~i48 and never reaches a fixed
point — 0c/11r again. The decisive number came from a new rig instrument (the final
stream's info decode, `corpse/a3-final-decode-floor.txt`): **the floor decode is
24,645/49,152 = 50.1% wrong — still coin-flip**, statistically identical to the first
decode. The falling churn was consistency, not correctness: the loop self-organizes into
a confident wrong fixed ring (right-mass 335k+ over a 36%-wrong-sign stream — the SISO
and the chains agreeing with each other, wrongly). The §B3.3 revert principle ("a
self-trained iterate with no fixed point is not evidence") is vindicated on its harshest
test to date — the revert was protecting us from shipping the echo chamber.

Verdict on the mechanism: at WN8's operating point the loop cannot bootstrap. The
bootstrap detection itself (exact-MAP chains on the probe-anchored channel view) is
coin-flip — 16QAM detection at 23 dB Poor REQUIRES mid-frame channel accuracy that only
labels provide (the oracle's segmented estimates), and labels only come from detection.
Every rung measured: hard labels (biased correlations — stall), soft labels (zero fixed
point), probe-row solves (necessary, insufficient), label-free bootstrap chains
(coin-flip), patience (wrong attractor). The full working form is banked as
`qam16-turbo-full.patch`.

**What ships** (per the design's registered fallback): the corpse rig's QAM16 truth +
final-decode instrument, and the complete §B3.4 turbo wiring — wire-domain chains,
permuted priors, per-bit sign flips, true second moments, probe training rows — behind
the restored FinishBlock gate, exercised by the ORACLE path (`MS110D_AUTOPSY_ORACLE=1`),
which now measures a **9.3E-4-class ceiling (485–505 on the corpse)** for any future
model-front leg to move. Shipped behavior is bit-identical for every waveform: WN7
corpse 72,666 / 7c/4r / oracle 15 exact, WN6 guard 0 / 11c/0r, WN13sp guard 0 / 11c/0r,
WN8 shipped 269,237 / turbo 0c/0r/0a/11s (the pre-leg skip state), suite 696/0.

WN8's honest scoreboard state: OPEN, doubly blocked — the bootstrap basin (this leg's
measured negative) AND the ceiling itself (9.3E-4 ≈ 93× the mask even with true
labels). A future leg must move the model floor (within-frame observability without
labels — pilots-in-data, FD processing, or a fade-trajectory prior) before turbo
inclusion is worth re-opening; both fronts now have exact instruments waiting.
