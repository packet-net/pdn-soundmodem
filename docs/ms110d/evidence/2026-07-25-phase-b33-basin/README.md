# Phase B3.3 labels basin — first-pass calibration and bootstrap quality (design, pre-registered 2026-07-25)

Written BEFORE the implementation, per the Phase B discipline. The model front closed in
`../2026-07-25-phase-b33-tir/` (PR #81); this leg attacks the other half of the WN7 split:
the **labels basin** — shipped 107,938 coded errors on the w0/b0 corpse against an oracle
ceiling of 237. The mechanism below is read off the banked #81 instruments; the levers and
their acceptance are fixed here before the first run.

## The mechanism (banked evidence, `../2026-07-25-phase-b33-tir/corpse/`)

The turbo loop's outcome is decided by the quality of its iteration-0 start:

1. **The first decode varies 6.8–22.4% coded errBits per block** (llrstats `first` pass,
   `wn7-tir-priors-llrstats.csv`), and the outcome cleaves on that number: every block
   starting ≤14.8% converged (b0/b2/b3/b8; b9 at 17.6% converged at the cap's edge, i19);
   every block starting ≥17% except b9 wandered to the cap and reverted (b1/b4/b5/b6/b7/b10).
   The basin boundary sits near **15–17% first-decode errBits**.
2. **The wander is self-reinforcing, not iteration-starved**: cap 96 converts nothing
   (`../2026-07-25-phase-b33-tir/cap96/`) — b4/b5/b10 plateau at 4–6k decode-changes,
   b1/b6/b7 fall to a ~300–700-change wander without fixed points. Iteration 0 trains the
   TIR solve and channel estimates on hard labels that are 17–22% wrong (worse still in the
   coded/wire domain after the encoder spreads each info error over a constraint length),
   so segH1/h2 come out corrupted exactly where repair is needed, the chain BCJR equalizes
   against the wrong channel with full confidence, and the SISO round-trips a near-miss
   self-consistent state.
3. **The first-pass LLRs are far under-confident and barely separate right from wrong**:
   mean |LLR| on correct bits 1.6–1.9 where the calibrated chain-BCJR oracle stream runs
   19–46 on the same blocks (5–25×), and mean |LLR| on wrong bits is 0.8–1.1 — barely a 2×
   separation. This is the banked #80 measurement that forced the hybrid hard bootstrap.

## Root cause of (3), and why it also causes (1)

The first-pass LLR scales are fixed constants: BPSK `4·Re`, QPSK `2·(Re±Im)`, 8PSK max-log
`scale 2.0` (`Ms110dDemodulator.PushLlrs`/`PushMaxLogLlrs`). A max-log LLR is
`(min1 − min0)/(2σ²)`; the constants correspond to an assumed post-equalizer noise
σ² ≈ 0.25–0.7 per complex dimension — a design-floor assumption, 5–25× pessimistic at a
+19 dB operating point, and **frame-invariant**. Two consequences:

- **Inter-frame misweighting** — the soft-decision Viterbi is invariant to a global LLR
  scale but NOT to per-frame relative weights. On the Poor channel the post-equalizer
  noise varies strongly across the block (fades); with a fixed scale, symbols from a
  fade-corrupted frame enter the trellis with the same weight as clean-frame symbols. The
  interleaver spreads both across the block, so the fade errors are what drag the worst
  blocks to 17–22% first-decode errBits.
- **The timid absolute scale** is what made a soft bootstrap unviable (#80: E[x] blobs at
  half-magnitude, three iterations spent rediscovering confidence) and forced the hard
  iteration-0.

The fix is not a new constant — it is removing the constant: the demod already **measures**
the post-equalizer residual per frame on known symbols (the probe MSE that the frame
diagnostics print, computed by re-equalizing each probe with the post-solve taps). That
measured σ̂² is the calibration, genie-free, no rig knob.

## Levers, in measurement order (one lever, one corpse, one commit)

### Lever 1 — per-frame first-pass LLR calibration from probe residuals

Replace the fixed scales for the PSK modes with `1/(2σ̂²_u)`, σ̂²_u interpolated across the
data span between the bracketing probes' post-solve residual MSE (both are already computed
by the time the span is equalized — the fading branch processes data after the closing
probe's solve). Unify BPSK/QPSK/8PSK onto the exact max-log metric (`PushMaxLogLlrs`) so
the constellation geometry is by construction rather than per-mode shortcuts; σ̂² floored
at machine epsilon only (a genuinely clean probe SHOULD mint high confidence). QAM16 keeps
its live fixed 10.0 scale — its calibration belongs to B3.4 where the 5×-scale trap is
already documented.

Flat-channel branch note: the 3-pass averaged symbol reuses the same received samples, so
its noise is correlated across passes — no averaging bonus is claimed; the single-pass
probe MSE is the honest (conservative) σ̂².

Blast radius: the first Viterbi decode (via inter-frame weighting — this is the lever's
point), the shipped bits of reverted/skipped blocks, and llrstats. The turbo's own LLR
streams are chain-BCJR-calibrated already and untouched. On AWGN/static channels σ̂² is
frame-constant → a global rescale → the hard decode is bit-identical by Viterbi scale
invariance; the battery verifies this claim.

**Pre-registered acceptance:** on the WN7 corpse (`MS110D_AUTOPSY_WN=7 _SNR=19 _SEED=507
_ORACLE=1`), first-decode per-block errBits must fall, pulling worst starts toward the
measured ≤15% convergence basin; shipped (107,938 / 5c/6r baseline) must improve or hold
with more blocks converging. **The oracle number (237) must not move** — the oracle path
trains on truth and never reads first-pass LLRs; movement means an unintended leak.
Guards: WN6 corpse (`_WN=6 _SNR=14 _SEED=506`) holds 0 shipped / 0 oracle; WN13 specimen
w3/b5 holds 0/0.

### Lever 2 — iteration-0 training-row quality weighting

Iteration 0 currently trains weight-1.0 on every re-encoded hard label. With calibrated
first-pass LLRs (lever 1), the raw LLR stream carries a per-bit agreement signal against
the re-encoded label: where the channel observation disagrees with the decoded label (or
agrees only weakly), the row is down-weighted. Exact weighting form to be fixed after
lever 1's measurement (it needs the calibrated LLR distributions to justify the squash);
candidates: per-symbol min-bit-|LLR| squashed to [w_min, 1], or SISO posterior magnitudes
from the iteration-0 decode. The EM variance diagonal (`AddTrainingRow` overload from #81)
is the soft-iteration analogue already in place; this lever brings the same honesty to the
hard bootstrap.

**Pre-registered acceptance:** WN7 corpse shipped falls further / more blocks converge;
oracle 237 may move only if the lever is also applied to the oracle path (it must NOT be —
the oracle trains on truth; weighting truth rows by first-pass confidence would corrupt
the instrument). Same WN6/WN13 guards.

### Lever 3 — damped extrinsic feedback (only if wander persists)

Standard turbo-equalization practice: scale the extrinsics fed as chain-BCJR priors by a
damping factor γ < 1 (literature range 0.7–0.75) to break limit-cycle wander — the b4/b5/b10
plateau signature. Applied to the prior path only (`_softWireExt`), not to the detector
extrinsics returned to the SISO. Taken up only if levers 1–2 leave wandering blocks, and
measured at a single literature-standard γ before any sweep; a γ sweep on the corpse would
be tuning-to-the-corpse and is out of bounds without a disjoint confirmation burst.

**Pre-registered acceptance:** converts at least one of the wandering blocks to
convergence on the WN7 corpse without regressing converged blocks; WN6/WN13 guards hold.

## Instrument addition (measurement-only, lands with this note)

llrstats currently records `first` and `oracle` passes; the basin walk needs the missing
middle: a `final` pass — the LLR stream the turbo settled on (on reverted blocks this is
the last iterate's stream, which is exactly the wander-state view the mechanism analysis
needs). One event on the demod (`TurboBlockLlrs`), wired into the autopsy rig's existing
`WriteLlrStats`. No shipped-path effect.

## Sequencing and the exit

Levers land one at a time; after the basin levers, the **model floor** front (two-adjacent-
lag chains for the d/d+1 fractional sidelobes, pre-registered in §B2.2 and in the #81
"where WN7 stands" note) gets its own design note — it is NOT part of this one. The
FD-turbo architecture decision remains the pre-registered escalation if both fronts stall
at mask+2 dB. Full §5.3 mask runs (canonical + disjoint) for WN7 and WN6, the standard
battery, and Phase A regressions gate the merge, as always.

Files land here as the measurements run.
