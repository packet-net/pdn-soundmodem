# Phase B3.10 — WN8 model-front M0: re-measure the true-label ceiling on the current binary

Branch `ms110d-phase-b39-wn7-modeltail` (shared with B3.9), from `main` @ `0b83fa5`.
Date: 2026-07-26.

## Starting position (B3.4, unchanged through B3.8)

WN8 (QAM16, 6400 bps) is doubly blocked: (1) the bootstrap basin — the first
decode is coin-flip (b38 census: ~269k/540k errors per burst, 49.8%) and the
turbo never engages (the block-turbo gate excludes QAM16 structurally;
`turboS=11` on every census row); (2) the model ceiling — B3.4 measured
**9.3E-4 with TRUE labels**, ≈93× above the 1E-5 mask. Every battery since has
carried WN8 byte-identical: no shipped-path change has touched it.

## M0 registration (before any run)

**Question.** Does the B3.4 ceiling still bind on the current binary? The ship
path is byte-identical since B3.4, but the oracle instrument rides
`TurboReequalize` — the chain-BCJR machinery rebuilt across B2.2–B3.8 (chain
decomposition, scrambler-exact echo, per-position h1, time-varying channel).
The 9.3E-4 figure is therefore stale as evidence: the current-model ceiling
must be re-measured, not cited.

**Method.** One oracle corpse run per family: canonical w0/b0 (channelSeed
509, SEED default 508) and disjoint w0/b0 (channelSeed 10509, SEED=10508 —
composition `SEED + worker·1e6 + burst·1000 + 1` with worker 0, burst 0).
`MS110D_AUTOPSY_ORACLE=1`; separate OUT dirs. Validity check: ship-side coded
errors, collapses, and turbo counters must match the b38 census rows exactly
(canonical 269,237 / 10 collapses / 0c-0r-0a-11s; disjoint 269,154 / 13 /
0c-0r-0a-11s). The oracle side (one chain-BCJR pass per block trained on true
info bits, after the shipped pipeline) is passive — B3.9 re-validated this on
all seven WN7 specimens.

**Statistics.** Per-block `oracle coded errors`; specimen ceiling BER =
Σ oracle errors / 540,640. Mask arithmetic for reference: 8 bursts ×
540,640 = 4,325,120 bits/family; 1E-5 ⇒ ~43 errors/family.

**Decision rule (pre-committed).**
- **Ceiling ≥ 10× mask on either specimen** (≥ ~54 oracle errors per burst):
  the model story closes design-only. Moving a ≥10× true-label ceiling — and
  then separately crossing a coin-flip bootstrap basin with no label-free
  crossing — needs waveform-processing redesign (FD equalization,
  pilots-in-data): outside Phase B scope. WN8 verdict = **measured-only**;
  Phase B closeout opens.
- **Ceiling < 10× mask**: the chain rework has silently moved the model front;
  a model-front arm becomes registrable — full discipline applies (fresh
  held-out banked before design, corpse before battery, full battery before
  merge), and the registration must state that the bootstrap basin remains a
  second independent wall the arm does not address.
- Either way, the basin block stands on its own: gating WN8 requires BOTH
  walls down; M0 measures only the ceiling.

## M0 measured (2/2 specimens, census cross-check exact)

Ship sides reproduce their b38 census rows exactly (canonical 269,237 coded /
10 collapses / 0c-0r-0a-11s; disjoint 269,154 / 13 / 11s) — specimens valid.
Oracle ceilings on the current chain (`corpse/summary-*.txt`):

- **canonical (509): 496 oracle errors / 540,640 = 9.2E-4** — 92× above the
  1E-5 mask; per block `89 42 55 15 36 0 116 21 46 62 14` — 10 of 11 blocks
  nonzero, a distributed model failure, not WN7's isolated fade-lottery events.
- **disjoint (10509): 136 / 540,640 = 2.5E-4** — 25× above mask; per block
  `0 0 0 22 33 0 55 0 12 14 0`.

The B3.4 figure (9.3E-4) is reproduced at 9.2E-4 on the chain that B2.2–B3.8
rebuilt end-to-end (chain decomposition, scrambler-exact echo, per-position h1,
time-varying channel): **none of the Phase B model improvements moved the
QAM16 ceiling**. The 16-point constellation at 23 dB on the Poor channel sits
beyond what this equalizer-plus-chain model class can track even with perfect
labels.

## Verdict (per the pre-committed rule): WN8 closes design-only, measured-only

Both specimens exceed the 10× line by an order of magnitude — the design-only
close fires decisively. Both walls stand on current measurements: the
true-label ceiling at 25–92× mask, and the bootstrap basin (coin-flip first
decode, turbo structurally gated out for QAM16 — engaging it on coin-flip
labels was measured harmful in B3.4, and B3.9 shows label quality is not the
binding constraint even where the model is good). The lever class required —
FD equalization, pilots-in-data, a per-symbol-tracking model — is a
waveform-processing redesign, outside Phase B scope.

**WN8 disposition: stays measured-only. No demod change, no held-out consumed,
no battery required.** With B3.9's WN7 verdict, every Phase B waveform story is
now resolved: 8 of 10 gated, WN7 and WN8 measured-only with written,
current-binary verdicts. Phase B closeout opens.
