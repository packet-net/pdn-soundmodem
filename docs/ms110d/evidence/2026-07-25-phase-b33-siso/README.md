# Phase B3.3 — SISO soft-feedback turbo (2026-07-25, afternoon)

The labels-front implementation the oracle split called for (`2026-07-25-phase-b33-wn7-oracle/`): the shipped turbo loop now drives the chain-BCJR re-estimation with **SISO soft decisions** — a tail-biting log-MAP BCJR over the K7/K9 rate-1/2 mother code (`TailBitingSisoDecoder`) whose per-coded-bit posteriors are re-punctured/re-interleaved into per-symbol expectations E[x] — instead of hard re-encoded decodes.

## Headline (corpse level; mask runs in the PR battery)

| corpse | first-pass/hard-turbo (banked) | genie | oracle | **soft-feedback (this PR)** |
|---|---|---|---|---|
| WN13 specimen w3/b5 (seed 10513) | 8,392 | 12 | 0 | **16** (11c/0r) |
| WN6 w0/b0 (seed 506) | 7,572 | — | 75 | **240** (11c/0r) |
| WN7 w0/b0 (seed 507) | 172,691 | ≈normal | 15,136 | **172,688** (1c/10r — unchanged) |

The WN13 fade-cluster specimen — the class B3.2 measured as probe-information-limited (hard turbo trains on 45%-garbage labels mid-frame) — decodes to within 4 errors of the genie. WN7 is untouched, exactly as the oracle predicted: its residual is the channel model (the pre-registered TIR/channel-shortening front), not labels.

## Design (what's in the code)

- **`TailBitingSisoDecoder`** (`src/.../Fec/`): log-MAP BCJR on the mother lattice, same trellis convention as the package Viterbi (branch register v: prev state v≫1, next v&(M−1), outputs parity(v&Poly)); tail-biting by circular wrap-extension W = min(6K, N); max* via a 128-entry log(1+e^−d) table. Returns posterior LLRs for all 2N coded positions — punctured positions (input 0) get the code's own opinion.
- **Soft expectations**: posteriors → re-puncture (float `Ms110dPuncture.Apply`) → re-interleave (float `Ms110dInterleaver.Interleave`) → per-symbol P(label) products → E[x] over the descrambled constellation, rotated by the scrambler (`Psk8[(s+r)&7] = Psk8[s]·Psk8[r]`). Per-symbol variance 1−|E[x]|².
- **`TurboCore`** (split out of `TurboReequalize`): the whole §B2.3 machinery — per-frame FF batch-LS, 4-segment h1, scrambler-exact echo, chain BCJR — now takes (expected[], variance[]?). Every estimation consumer keeps its exact form because E[|x|²]=1 on the PSK ring makes the /count normalizations the EM answer for soft labels too; the only new arithmetic is the EM noise term |h|²·(1−|E[x]|²) added to the residual, skipped entirely when variance is null. **The hard path is bit-identical**: the banked first-pass and oracle llrstats reproduce byte-for-byte, and the oracle per-block lines match digit-for-digit (WN7 b0:73…b10:3072; WN13 all-0; WN6 5/49/14/7).
- **Hybrid bootstrap, cap 8**: iteration 0 trains on hard re-encoded labels, iterations 1+ on SISO soft labels. Convergence rule unchanged (exact decode fixed point, else revert — the #65 discipline).

## The pure-soft negative result (`pure-soft/`) — why the hybrid

v1 ran soft labels from iteration 0 with the old cap 5. It was a regression everywhere: WN6 65,680 (0c/**11r** — every block reverted to first pass), WN13 15,781 (9c/2r), WN7 179,633. The per-iteration diagnostic (`wn6-cold-start-trajectory.log`) shows why, and it is not oscillation: block 0's decode-changes walk 4440 → 1111 → 1460 → 180 → **12** — contracting steadily toward a fixed point the 5-iteration cap never lets it reach, after which the revert throws away a nearly-converged decode for the (much worse) first pass. The cold start is the first-pass LLR stream's fixed max-log scale: mean |LLR| ≈ 1.6 at +14 dB with 97 % of symbols "weak" (var > 0.25), where the calibrated chain-BCJR output runs 12+. The loop spends three iterations rediscovering confidence through the chain BCJR before the code can bite.

The hybrid removes both problems at once: the hard iteration 0 is the bootstrap the old turbo already proved on every passing waveform, and it hands iteration 1 properly-scaled chain-BCJR LLRs. Once sharp, E[x] saturates, the labels effectively quantize, and exact fixed points return: in `hybrid/*-trajectory.log` **every** WN6/WN13 block ends at decode-changes=0 (healthy blocks in 2–4 iterations; the WN13 dead block contracts 7656 → 3931 → 3350 → 1132 → 61 → 6 → 0 in 7).

## Files

- `hybrid/` — the three corpses on the shipped configuration: summaries (headline numbers + unchanged oracle lines), llrstats (first/oracle passes — the bit-identity witnesses), per-iteration trajectories.
- `pure-soft/` — v1 summaries (the regression) + the WN6 cold-start trajectory that diagnosed it.

## Residue

- WN13's 16 vs genie 12 vs oracle 0, WN6's 240 vs oracle 75: the remaining gap is iteration-0's hard bootstrap quality and the first-pass LLR scale — candidate knobs if the mask runs want more, but only if they do.
- WN7 rides the B3.3 model front (TIR channel-shortening + time-varying h2), unchanged by this PR.
