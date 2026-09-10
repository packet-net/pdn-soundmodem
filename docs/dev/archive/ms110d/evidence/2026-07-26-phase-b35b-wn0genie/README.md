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

---

## Amendment 1 (registered mid-leg, before any lever): M3 re-read + the mechanism the
## instrument actually found

**M0 and M-healthy: GREEN.** All 10 pass-(a) corpses reproduce their census lines
exactly (coded AND uncoded columns); H0–H3 stay 0-error under both oracle modes.
Pass (b) = pass (a) to noise level (2,401 vs 2,377 pooled — E3 419→443, others ±2
uncoded): the clean-carrier-refine share is nil; attribution is pure.

**M3 as registered: RED (0/6) — and the failure was the fidelity method's assumptions,
not the instrument.** Two defects, disclosed: (i) the scoring script's data-span
constant was wrong (42.24 s — a di-bit/coded-bit slip; the true span is 12,672 coded
bits / 2 per symbol = 6,336 symbols = 84.48 s), which pinned the lag search 42 s off;
(ii) after fixing it, the registered PAIRING (finger 0 ↔ path 0, fingers 4/5 ↔ path 1)
is itself the wrong assumption on every error corpse. The full 7-finger × 2-path
correlation matrix shows:

| population | finger-energy profile | measured pairing |
|------------|----------------------|------------------|
| all 9 zero-error bursts (H0–H3 + w0 probes b1,b2,b3,b6,b8) | two peaks: f0 ≈ 0.15, f5 ≈ 0.12–0.15, floor 0.036 | f0↔path0 ρ 0.96–0.97, f5↔path1 ρ 0.94–0.96 |
| all 9 error bursts (E1–E6 + w0 probes b4=4, b19=21, b23=19 errs) | ONE peak: f0 ≈ 0.15, floor everywhere else | **f0↔path1 ρ 0.95–0.97**; NO finger tracks path0 (best ρ ≤ 0.43 = leakage) |

**M3 amended bar** — the pairing is measured, not assumed: dominant finger↔path ρ ≥
0.85 for every path that lies inside the finger window. Result: 6/6 error corpses GREEN
(ρ 0.95–0.97), 4/4 healthy GREEN on both paths (ρ 0.94–0.97). The instrument is
faithful; findings are readable.

**The mechanism (18/18 concordance, no exceptions):** error bursts are exactly the
bursts where acquisition locked onto the LATER path — the 2 ms echo. Finger 0 then sits
on path 1 and the direct path arrives at −4.8 chips, OUTSIDE the causal finger window
[0..+6]: half the received energy is invisible to the detector and the burst runs 84 s
as single-path Rayleigh with zero diversity. Direct-locked bursts get the designed
dual-diversity RAKE and their measured population BER is exactly 0 over ~4.15M bits
(655/952 census bursts) WITH THE SHIPPED DD DETECTOR. The census tail is not "deep
fades"; it is a lock-geometry lottery (~31% of bursts).

**M1: R = 1.000.** O-inst decodes every corpus block clean: 2,401 pooled baseline coded
errors → 0, on all six corpses — achieved on the echo-locked one-path geometry, i.e.
even WITHOUT the missing path, perfect reference quality crosses every Viterbi cliff.
Uncoded 1,792–2,120 → 264–343 per burst (≈6.7×).

**M2: noise+decision share 96.1%, lag share 3.9%** (E_pole = 94: E1 23, E2 8, E3 29,
E4 0, E5 34, E6 0). The 80 ms one-pole lag is nearly free; the DD innovation quality
(additive noise + wrong-winner updates, poisoning the gains around every fade null) is
the deficit.

**Decision rule: clause 1 executes (R ≥ 0.95) — the ladder opens. But the registered
family mapping (M2 → margin-gated DD updates) is OVERRIDDEN by the mechanism evidence,
and that override is this amendment's point:** the reference-quality family would teach
DD to survive a geometry in which the detector is starved of half its energy; the
geometry family removes the starvation and returns every burst to the regime the
shipped detector already decodes at BER 0 with margin. Mechanism-directed beats
symptom-directed (the §B3.3→§B4.1 arc's lesson).

### Rung G (geometry): symmetric finger window

**Lever (one lever, WN0-only code):** extend `Wid0WalshModem`'s RAKE window from chip
delays 0..+6 to **−6..+6** (13 fingers, RakeChips 32+12 = 44; buffer base moves 6 chips
early; forward sample requirement unchanged at +38). DD update rule, α, warm-up,
quadratic-CSI MRC, LLR construction, carrier PLL: all UNCHANGED. Whichever path
acquisition anchors, both paths now land inside the window (echo-locked: path0 at
−4.8 → fingers −5/−4; direct-locked: unchanged). The signal-lost discriminator keeps
its physical window (the delay-0 chips) and threshold; max-over-13-vs-7 noise fingers
shifts its noise-side statistic ≈ +15% (0.23 → ~0.27 vs threshold 0.35) — watched via
census end-reasons, bar below. No config knob: this is the §B3.5 detector covering the
channel geometry it was designed for; the off-ramp is git.

**Bars (all pre-registered; one measurement each):**

- G1 (corpse, mechanism): E1–E6 + b4/b19/b23 show two-peak profiles under the shipped
  DD (direct path recovered), and pooled corpus coded errors ≤ 5% of the 2,401 baseline
  (≥95% repair by the REAL detector, no oracle). Healthy corpses H0–H3 + b1–b8 probes
  stay exactly 0.
- G2 (battery, full both-family §5.3 budgets): WN0 Poor BER ≤ 6.0E-4 both families
  (≥10× on 5.99E-3/6.40E-3) — the ship bar, B3.5-precedent. Gate consideration only on
  the measured k against the §5.3 arithmetic, never assumed.
- G3 (collateral): every other WN's battery censuses byte-identical to the #88/#89
  baseline (WN0-only code cannot touch them; any drift falsifies that claim and kills
  the lever). WN0 acquisition-failure count stays 0 and non-Eom end-reasons do not
  increase (the discriminator margin).
- Guards + suite exact as ever (WN7 72,666/7c/4r + oracle b5:15, WN6 0/11c0r, WN13sp
  0/11c0r, 697/0).

**Consequence clauses:** G1 fail → rung G reverts to banked; the reference family
(margin-gated DD, rung R1) becomes the next registered candidate in its own leg. G3
fail (any collateral) → rung G dies regardless of G1/G2. G2 partial (repair real but
<10×) → report honestly; ship/bank decided by the B3.5 ship-worthiness precedent
(collateral-free measured improvement) with the arithmetic in the results.

Reference-family rungs (margin-gated DD updates, two-sided smoothing with decision
delay) stay BANKED behind rung G — opened only if G leaves a residue that M2's
decomposition still attributes to innovation quality.

---

## Results

### Instrument phase (corpse/, all bars green)

Four passes per corpse; coded errors:

| corpse | census | a: plain | b: genie-DD | c: O-inst | d: O-pole |
|--------|--------|----------|-------------|-----------|-----------|
| H0–H3  | 0 ×4   | 0 ×4 (uncoded exact: 279/238/178/241) | 0 ×4 | 0 ×4 | 0 ×4 |
| E1 w2b97 | 697  | 697 | 697 | **0** | 23 |
| E2 w2b53 | 489  | 489 | 489 | **0** | 8  |
| E3 w3b96 | 419  | 419 | 443 | **0** | 29 |
| E4 w1b91 | 381  | 381 | 381 | **0** | 0  |
| E5 w1b41 | 293  | 293 | 293 | **0** | 34 |
| E6 w2b37 | 98   | 98  | 98  | **0** | 0  |
| pooled E | 2,377 | 2,377 | 2,401 | **0** | 94 |

M0 exact ×10, M-healthy green, M1 R = 1.000, M2 = 96.1% noise+decision / 3.9% lag,
M3 (amended pairing) 6/6 + 4/4 at ρ 0.94–0.97. O-inst uncoded on the error corpus:
1,792–2,120 → 264–343 per burst. The lock-geometry census (corpse/lock-census.txt)
carries the 18/18 concordance table.

### Rung G (battery/, all bars green)

**G1**: all 9 echo-locked corpses (E1–E6 + b4/b19/b23; 2,445 pooled baseline errors)
decode **0 coded errors** with the shipped DD detector — bar was ≤5% residual, measured
0%. Two-peak profiles restored (direct path at −5/−4; both paths DD-tracked at
ρ 0.91–0.94 — the DD reference was never the bottleneck; the geometry was). All 9
healthy corpses stay 0 (uncoded shift noise-level, e.g. H0 279→289). The shipped
detector's uncoded on former killers (238–303/burst) lands at the one-path oracle's
level: dual diversity ≥ perfect-reference-single-path, exactly the §B3.5 design thesis.

**G2 (full §5.3, both families)**:

- canonical: **3,000,704 bits, 0 errors** — BER 0, 97.5% bound **1.22E-6** (was 5.99E-3)
- disjoint: **3,000,704 bits, 3 errors** — BER 1.00E-6, bound **2.92E-6** (was 6.40E-3)
- 476 bursts/family, 0 acquisition failures, all Eom; uncoded 1.85E-2/1.86E-2

**G3**: all 72 non-WN0 battery census files **byte-identical** to the #88/#89 baseline
(`cmp` clean); gated seven at their exact digits (WN5 23/0, WN6 35/39, WN2 30/29,
WN13 0/0, WN3 0/0, WN4 0/3, WN1 0/0), open pair unchanged (WN7 1.73E-1/1.95E-1, WN8
4.96E-1/4.97E-1), AWGN 10/10 + static + Doppler zero. 32/32 legs rc=0, no retries.
WN0 end-reason profile improved: 9 non-Eom baseline endings → 0 (the 13-finger
discriminator concern is measured moot). Guards byte-identical through both commits;
suite 697/0 (105 skips).

### WN0 joins the default-gated set (B4 flip criterion, all three conditions)

(a) families individually §5.3-green: bounds 1.22E-6 / 2.92E-6 ≤ 1E-5. (b) pooled
3/6,001,408 → bound 1.46E-6 ≤ 1E-5. (c) per-family false-red at the pooled rate:
P(k ≥ 20 | λ ≈ 1.5) ≈ 1E-15. Flipped in `Ms110dMaskTests` (`wn is 0 or 1 or ...`);
armed-negative verified: the flipped gate at a 100k smoke budget fails on the Poisson
clause (bound 3.64E-5 > 1E-5) — the gate is live, not decorative.

### Disposition

- The reference-quality lever class (α, margin-gated DD, two-sided smoothing) closes
  UNPULLED: registered, priced (R = 1.000 says it *could* have worked), and obsoleted
  by the mechanism fix — banked-closed unless a future residue reopens it.
- The "energy ceiling" suspicion is resolved: the energy was there all along, 4.8 chips
  to the left of the window.
- Instrument lesson (the leg's method payoff): the fidelity audit that came back RED
  under the registered pairing was the discovery — following its consequence clause
  (stop, investigate the instrument) instead of waving it through is what surfaced the
  echo-lock mechanism. The oracle answered a question it was not asked.
