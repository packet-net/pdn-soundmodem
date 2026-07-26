# Phase B4 — full-table gate runs and the `MS110D_POOR_GATED` flip

**Branch:** `ms110d-b4-gate` off main 9f6bc85 (#87 merged). **Scope:** the phase-b-plan §B4 gate runs and the at-mask flip. The full closeout artifacts (phase-b-closeout.md, design §6 row B, issue closures, the RLS-vs-NLMS report) are deliberately NOT in this leg: three points (WN0/WN7/WN8) are open, so Phase B does not close here — this leg makes the solved majority of the table a hard gate and leaves the open trio measured.

This section is the registration, committed before any run. Results follow below it, appended after the battery.

## 1. What is being claimed and what the flip means

A flipped point makes `Poor_Channel_Mask_Gate` assert the §5.3 accept rule **by default** — every future full-budget run of the Poor table (nightly rotation included) becomes a hard red on failure, with no environment flag to arm. That is a promise about *every future draw*, not about this batch's draws. The registration therefore separates two questions:

1. **Do this batch's draws pass §5.3?** — the gate-run question, judged per point per family by the rule as implemented (≥3×10⁶ bits and ≥600 s sim; ≥30 errors → direct BER ≤ 1E-5, else 97.5 % Poisson upper bound ≤ 1E-5; zero acquisition failures).
2. **Is the point's *rate* far enough under mask that a default gate is trustworthy?** — the flip question. A point whose true rate sits at 0.95× mask passes §5.3 draws routinely and fails them routinely; arming that as a default gate institutionalizes a coin-flip nightly.

**Pre-run arithmetic that forces the second question** (this is why the flip criterion below is not simply "both families green"): WN6's banked evidence is 57 errors / 6M in each family — pooled 114/12M = 9.5E-6, 95 % CI ≈ [7.8, 11.4]E-6. Under the §5.3 direct rule a 6M family passes iff k ≤ 60; at Poisson mean 57 that fails with P ≈ 0.32 **per family per run**. Even this batch's both-family pass probability is only ≈ 0.46. No feasible budget fixes this — the false-red rate is intrinsic to gating a 0.95×-mask rate against a ≤1.0×-mask rule (a 2σ-decisive sample at 5 % margin needs ~1.5×10⁸ bits). The prompt's enumeration ("the flip covers the AT-MASK set, WN1–6 + WN13") was written from a scoreboard that did not carry this arithmetic; the criterion below is the pre-registered amendment, decided before any B4 data, and WN6's own fresh data still decides its outcome under it.

## 2. Flip criterion (uniform, rate-driven, pre-committed)

A point enters the default-gated set iff:

- **(a)** both families individually pass §5.3 as implemented (the gate-run question), AND
- **(b)** the pooled both-family sample's 97.5 % Poisson upper bound clears the mask: `PoissonUpper975(k_pooled) / bits_pooled ≤ 1E-5` — the rate is *statistically established* under mask, §5.3's own small-count philosophy applied to the pooled sample, AND
- **(c)** at the pooled rate estimate, a single future family run at the point's default budget has §5.3 false-red probability ≤ 5 % — the operational nightly-trust condition. (b) alone does not imply (c) at 3M budgets: the <30-error Poisson path passes only k ≤ 19, so a rate at (b)'s edge would still red most nights.

Exact thresholds from the implemented Wilson–Hilferty bound and the §5.3 pass sets (3M family passes iff k ≤ 19 or k = 30; 6M family passes iff k ≤ 60): the binding pooled thresholds are **k_pooled ≤ 26** for the 3M×2 points (c binds; b alone would allow 44), **k_pooled ≤ 96** for the 6M×2 points (c binds; b allows 98), **k_pooled ≤ 43** for WN13's 3.24M+6.49M precedent shape judged at its 3M default (c binds; b allows 77).

Predictions under banked rates, stated now: WN1/WN3/WN4/WN13 flip comfortably (banked 0-to-few errors). WN2 flips (banked 15/12 at 3M → expected ~27 pooled/12M, threshold 96). WN6 is expected NOT to flip — it needs fresh pooled k ≤ 96 against a banked mean of ~114, P ≈ 0.05 — and its consequence clause below is expected to execute. WN5 is genuinely open: its at-mask status dates from the B3-entry re-baseline, only 480k smokes since (persistently 6 errors — 1.25E-5 on the smoke denominator, the known #80-era residue), so its fresh 12M decides in either direction: pooled rate ≲ 8E-6 flips, above doesn't.

## 3. Pre-committed budgets (final for this batch — no extensions, no re-rolls)

| Point | SNR (3 kHz) | Canonical | Disjoint (+10000) | Gate armed | Rationale |
|---|---|---|---|---|---|
| Poor WN1 | 3 dB | 3M | 3M | yes | banked 0/0 |
| Poor WN2 | 5 dB | **6M** | **6M** | yes | upgrade from 3M precedent: rate ≈4.5E-6 trips the <30-error Poisson bound at k ≥ 20 (P ≈ 6 %/family at 3M); at 6M pass iff k ≤ 60 — decidable |
| Poor WN3 | 7 dB | 3M | 3M | yes | first full budget since the B3-entry re-baseline |
| Poor WN4 | 10 dB | 3M | 3M | yes | first full budget since the B3-entry re-baseline |
| Poor WN5 | 11 dB | **6M** | **6M** | yes | stale full-budget evidence + the 6-error smoke residue; 6M is decisive either way |
| Poor WN6 | 14 dB | 6M | 6M | yes | precedent budget; §5.3 draws stand as evidence whatever the flip outcome |
| Poor WN13 | 11 dB | 3M | 6M | yes | matches the #82/#83/#87 precedent shapes (6M disjoint preserves comparability with the banked #82 residue point) |
| Poor WN0 | −1 dB | 3M | 3M | measured | open (block-cliff residual, B3.5) |
| Poor WN7 | 19 dB | 3M | 3M | measured | open (attractor-bound, loop-structure levers) |
| Poor WN8 | 23 dB | 3M | 3M | measured | open (doubly blocked, B3.4) |
| AWGN ×10 | table D-LXIV | 3M each | — | yes (existing default) | the standing AWGN gate, per-WN chunks |
| Static WID2 | 9 dB (house) | full | — | yes (existing default) | standing gate |
| Doppler ×3 | ±75 Hz @24 dB | 200k each | — | engineering check | standing check |
| Hermetic suite | — | — | — | plain `dotnet test` | includes the OBW tests (ungated) |

Seeds exactly as the harness defines them: Poor 500+wn (+offset, +1e6/worker), AWGN 100+wn, static 900, Doppler 700+wn; `MS110D_MASK_WORKERS=4` throughout; census (`MS110D_MASK_BURST_LOG`) on every Poor leg. Gated legs run with `MS110D_POOR_GATED=1` so the §5.3 assert executes in-process — a red leg is a nonzero exit with the assertion in the log, not a post-hoc comparison.

## 4. Consequence clauses (registered before any run)

1. **Any gated point red on either family** → that point is excluded from the flip, the flip proceeds for the survivors, the scoreboard is amended same-day, and the red opens as its own investigation leg. No budget extensions, no re-rolls: a marginal red is a red for this batch.
2. **WN6 fails the flip criterion** (expected, P ≈ 0.95) → scoreboard amends WN6 from "AT MASK" to "AT THE LINE": §5.3 draws pass at the banked rate but pooled 12M bound ≈ 1.14E-5 — the point needs *margin*, not re-measurement. The pre-registered path to flipping it is the banked B3.3-segnoise lever class (−32 % on WN6 in both families, reverted on WN2 collateral) behind a shippable noise-floor estimator — a future leg, not this one.
3. **WN5 fails either §5.3 draws or the pooled bound** → its at-mask scoreboard status is retracted same-day (it was granted on stale evidence), and WN5 joins the open set with its census as the opening instrument.
4. **Any acquisition failure anywhere in the Poor table** → stop; corpse before anything flips (the acquisition clause is part of §5.3).
5. **Any AWGN/static/Doppler red** → nothing flips until it is explained; the demod is untouched on this branch, so a red here means environment or harness, and the leg re-runs after diagnosis.

## 5. Registered flip semantics (applied only after the battery, only for the passing set)

In `Ms110dMaskTests.Poor_Channel_Mask_Gate`:

- A static gated-WN set (membership = the points that passed the flip criterion) asserts `AssertMask` by default; `MS110D_POOR_GATED=1` remains as a force-all override so chasing legs on open points can still arm the gate.
- Per-WN default budgets: the 6M-evidence points (WN2/WN5/WN6) default to 6M when `MS110D_MASK_BITS` is unset, so the default-armed gate runs at the budget its evidence used and its statistics were pre-registered at. All other behavior (skip gating on `MS110D_MASKS_POOR`, reporting, census, workers) unchanged; plain `dotnet test` is unaffected.

**Post-flip verification (bit-identity bridge):** re-run the flipped assembly on WN2, WN13, and each flipped 6M point, canonical family, default budgets, with NO `MS110D_POOR_GATED` — error counts must be bit-identical to the corresponding gate-run legs (proving the flip changed *arming*, not measurement), and the assert must have executed by default. Plus the full plain suite green.

## 6. Battery structure

Three concurrent detached lanes (16-core box, 4 workers per leg, `choom`-marked expendable), each lane serial within itself: Lane A = Poor canonical (10 legs), Lane B = Poor disjoint (10 legs), Lane C = AWGN per-WN ×10 + static + Doppler. Per-WN chunking with in-process 4-worker splitting is the union-merge unit — each §5.3 point is accepted only on its own whole-point totals (per-chunk log lines never stand in for the gate number). Scripts and raw outputs under the job tmp dir; `battery.log` and censuses committed here.
