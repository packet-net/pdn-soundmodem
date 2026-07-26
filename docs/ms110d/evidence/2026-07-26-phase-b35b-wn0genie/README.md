# B3.5b — WN0 genie-gain oracle (instrument-only leg; registered before any code)

WN0 after the §B3.5 Walsh RAKE sits at Poor 5.99E-3 canonical / 6.40E-3 disjoint — 13.3×
better than pre-RAKE, 600× above the 1E-5 mask. The residual is block-cliff shaped
(collapse at block-uncoded ≈17–20%, right:wrong LLR mass 13:1), and the banked lever
class — DD reference quality (α retune, margin-gated DD updates, two-sided gain
smoothing) — has never been priced. The §B3.4/§B4.1 lesson is binding: bound the ceiling
with an oracle BEFORE pulling any lever. This leg builds and reads that oracle. No
demodulator behaviour ships; the shipped WN0 path must be byte-identical after the merge.

## The question

If the RAKE's decision-directed finger gains were replaced by the TRUE per-finger channel
gains, how much of the block-cliff residual disappears?

- Most of it → reference quality is the binding constraint → register the lever ladder
  (family chosen by the decomposition below) with bars.
- Little of it → the energy simply isn't there at the correlator output during the
  killing fades → the reference lever class is DEAD for WN0-at-mask, and WN0 waits for a
  detector-family idea. A closed door, honestly measured, is the cheap outcome.

## Fresh anatomy (census arithmetic, #88 battery, both families pooled)

952 bursts / ~6.0M bits, 37,188 coded errors (6.2E-3 pooled — matches the table):

| burst class      | bursts | errors held | share |
|------------------|--------|-------------|-------|
| ≥100 coded errs  | 141    | 30,583      | 82.2% |
| 30–99            | 92     | 5,560       | 15.0% |
| 1–29             | 64     | 1,045       | 2.8%  |
| 0                | 655    | 0           | —     |

The RAKE turned WN0's pre-B3.5 uniform failure (476/476 bursts erroring, median burst BER
7.0E-2) into a tail-dominated one: 69% of bursts are now clean and 97.2% of the error
mass lives in ≥30-error fade-cluster bursts. A corpus drawn from that tail therefore
prices the mechanism that owns essentially the whole residual.

## Oracle construction (mechanism, exact)

**Truth channel** — the existing §B0 genie seam, unchanged: the rig feeds the SAME
channel realization noise-free (`WriteGenie`, same seed at SNR = ∞, proven identical by
`Ms110dGenieTests`) and the demod reads it at identical positions/timing/carrier via
`ReadChipEst`. **Truth labels** — the rig already reconstructs `fetchedBlocks` (the
interleaved wire bits per block); the WN0 truth di-bit at (block, sym) is
`fetched[2·sym]<<1 | fetched[2·sym+1]` (Modulate's MSB-first order). Both seams exist;
the oracle only wires them to the RAKE.

Per symbol, the oracle variant (`Wid0WalshModem.DemodulateRakeOracle`, a SEPARATE method
— the shipped `DemodulateRake` is not touched) computes the noisy per-finger candidate
correlations exactly as shipped, plus one clean-stream correlation per finger against the
TRUE Walsh row: `o[k] = cleanCorr_k(true)/32` — the true finger-k channel gain as seen
through the receiver's own front-end, carrier model, and pulse shape, with zero additive
noise and zero decision errors. Two gain modes:

- **O-inst** (the ceiling): gains = `o[k]` instantaneous, warm from symbol 0 (no
  noncoherent warm-up). Removes noise, decision errors, AND the 80 ms one-pole lag.
- **O-pole** (the decomposition point): gains = the shipped one-pole (same α = 1/6, same
  use-then-update order, same 8-symbol warm-up) but with `o[k]` as the innovation instead
  of the noisy winner correlation. Removes noise + decision errors, KEEPS the lag.

MRC statistic, LLR construction, signal-lost discriminator: byte-identical construction
to shipped, only the gain source differs. Post-EOM blocks (no truth) fall back to the
shipped DD call (scrambler state advances identically either way).

**Registered mechanism notes** (so nobody debugs these as surprises):

1. *The carrier PLL loses its observable under the oracle and is deliberately skipped.*
   Oracle gains are read through the same θ(t) correction as the noisy chips, so the
   combined statistic Σ o*·corr self-cancels any common phase error — the PLL error term
   is ≈0 by construction. The oracle instead absorbs carrier drift into the per-symbol
   gains themselves (O-inst exactly; O-pole with 80 ms lag ⇒ ≤ ~2° bias at the ≲0.05 Hz
   post-refine drift rates — negligible against fade dynamics). TrackWalsh skips
   RetuneCarrier in oracle mode.
2. *O-inst self-noise bound.* T-spaced finger windows leak the other path's
   scramble-misaligned power into `o[k]` at ~|g_other|/√32 (≈ −15 dB amplitude). MRC
   detection degrades only quadratically in reference error: SNR cost ≈
   10·log10(1 + 0.032) ≈ 0.14 dB. The ceiling read is therefore honest to ~0.15 dB; the
   damage DD suffers at fade nulls (sign flips arriving 80 ms late) is orders larger.
3. *Genie feeding alone already changes one thing for WN0*: acquisition/tail carrier
   refinement uses estimation-side reads (`ReadChipEst`), which the genie redirects to
   the clean stream. Attribution therefore needs a genie-fed DD baseline (pass b below);
   the (b)−(a) delta is itself a readout (the clean-carrier-refine share).

## Passes and corpus

Four passes per corpse, one lever apart each:

| pass | feeding   | gains                         | reads |
|------|-----------|-------------------------------|-------|
| a    | plain     | shipped DD                    | M0 anchor: must equal the census line exactly |
| b    | genie-fed | shipped DD                    | attribution baseline (isolates clean carrier refine) |
| c    | genie-fed | **O-inst**                    | M1 ceiling |
| d    | genie-fed | **O-pole**                    | M2 decomposition |

Corpus — 4 healthy controls + 6 tail bursts spanning 98–697 errors, drawn from the #88
census (`../2026-07-26-phase-b4-gate/battery/census/`), reproduced via the autopsy rig's
seed arithmetic (channelSeed = baseSeed + worker·10⁶ + 1000·burst + 1):

| id | family | worker | burst | channelSeed | census coded errs | census uncoded |
|----|--------|--------|-------|-------------|-------------------|----------------|
| H0 | 3m  (seed 500)   | 0 | 0  | 501     | 0   | 279  |
| H1 | 3m               | 1 | 0  | 1000501 | 0   | 238  |
| H2 | 3m               | 2 | 0  | 2000501 | 0   | 178  |
| H3 | 3m               | 3 | 0  | 3000501 | 0   | 241  |
| E1 | 3m               | 2 | 97 | 2097501 | 697 | 1854 |
| E2 | 3md (seed 10500) | 2 | 53 | 2063501 | 489 | 2120 |
| E3 | 3md              | 3 | 96 | 3106501 | 419 | 1991 |
| E4 | 3md              | 1 | 91 | 1101501 | 381 | 1928 |
| E5 | 3m               | 1 | 41 | 1041501 | 293 | 1895 |
| E6 | 3md              | 2 | 37 | 2047501 | 98  | 1792 |

Error-corpus baseline mass: 2,377 coded errors. Budgets: corpus only — an instrument leg
with no shipped-path change needs no battery; the guard set + full suite stand in (below).

## Measures and bars

- **M0 (corpse validity)**: pass (a) coded errors == the census column exactly, all 10
  corpses. Any mismatch → STOP, fix the rig before reading anything.
- **M-healthy (inertness of the construction)**: passes (c) and (d) on H0–H3 must stay at
  0 coded errors. Any injected error → the oracle construction is damaging (seam bug or
  self-noise beyond the registered bound) → instrument invalid, fix first.
- **M3 (fidelity — audit the instrument before believing it)**: per error corpse,
  Pearson ρ between |o_0(t)| (finger 0, from the oracle diag line) and |g_path0(t)|
  (rig's `LastPathGains`), and between best-of |o_4|,|o_5| and |g_path1(t)|, at the best
  integer lag within ±208 ms (alignment is fitted, not derived). Bar: ρ ≥ 0.85 on both
  pairings for ≥4 of 6 error corpses. Red → instrument invalid → STOP, fix.
- **M1 (the ceiling)**: pooled repair on the error corpus,
  R = 1 − E_inst(c) / E_base(b), plus per-corpse first-decode errors per block (does the
  block cross the Viterbi cliff to 0).
- **M2 (the decomposition)**: with E_pole(d) between the two ends: lag share =
  (E_pole − E_inst)/(E_base − E_inst); noise+decision share = (E_base − E_pole)/(E_base −
  E_inst). Readout, no bar — it picks the lever family if the ladder opens.

## Decision rule (pre-registered)

On pooled R over the six error corpses, with M0/M-healthy/M3 green:

1. **R ≥ 0.95** → the reference-quality ceiling is high enough to own the tail → register
   the lever ladder as an amendment (family by M2: lag-dominant → faster/two-sided gain
   smoothing; noise/decision-dominant → margin-gated DD updates; if (b)−(a) dominates
   both → carrier-refine family), with χ²-honest bars in the B4.1 template. Rung 1 may
   be implemented in this window only after the amendment is committed.
2. **R ≤ 0.30** → the energy is not there at the correlator output even with true gains →
   the reference lever class is DEAD for WN0-at-mask. Written verdict, levers stay
   banked-closed, WN0 waits for a detector-family idea. No lever is attempted.
3. **0.30 < R < 0.95** → measured-ceiling report only; no lever opens this window. The
   projected best case (even at corpus-R extrapolated across the ≥30-err mass, the 2.8%
   small-burst mass alone leaves ≥ 1.7E-4 ≈ 17× mask) means a partial-repair lever needs
   its own cost/benefit registration before it is worth anything.

Consequence clauses: any M0/M-healthy/M3 red stops the leg at the instrument — findings
are not read from a broken instrument (the agent-campaign audit lesson). Guard corpses
(WN7 72,666/7c/4r + oracle b5:15, WN6 0/11c0r, WN13sp 0/11c0r) and the full suite
(697/0) must hold EXACT after the instrument lands — the shipped path is add-only, so
even a one-bit drift falsifies the "instrument-only" claim and stops the merge.

## Ship form

Nothing ships to the demod's default behaviour. The PR carries: the oracle instrument
(demod-internal seams + rig env `MS110D_AUTOPSY_WALSH_ORACLE=inst|pole`, which implies
genie feeding), this registration, the corpus artifacts, and the results section
executing the decision rule above.
