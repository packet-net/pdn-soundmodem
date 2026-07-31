# W0 — entry re-baseline on current main (WN8 redesign program)

Registered 2026-07-31 before the run, per [wn8-program-plan.md](../../wn8-program-plan.md) §3/§4.

## Registration

**Question.** Has `main` drifted from the Phase B byte-identity baseline (the b38 battery, [../2026-07-26-phase-b38-wn7-anchortrack/battery/](../2026-07-26-phase-b38-wn7-anchortrack/battery/), plus the b39 WN7/WN8 verdict numbers and the closeout §6 guard pins)? Main has moved since b38 — at least PR #103's input AGC and the OTA-era work — and the program needs its own banked baseline before any DSP change.

**Decision inputs.** The b38 battery-A/-B/-C mask lines and WN7 census CSVs; [../2026-07-26-phase-b39-wn8-verdict/](../2026-07-26-phase-b39-wn8-verdict/) (WN8 4.96E-1/4.97E-1, ceiling 9.2E-4/2.5E-4); [phase-b-closeout.md](../../phase-b-closeout.md) §1 table + §6 pin registry (hermetic 697 passed / 0 failed / 105 env-gated skips).

**Method.** (a) `dotnet build` then the full hermetic suite `dotnet test --no-build` — re-pin the count. (b) The five §6 guard-pin corpse runs (`Mask_Burst_Corpse_Dump`, exact env in `pins.sh`): WN7 w0/b0 (+oracle), WN7 w1/b0, WN6 w0/b0, WN13 SEED=10513 w3/b5, WN0 w2/b97. (c) The three-lane battery in the b38 form (`battery.sh`, committed beside this file): lane A canonical Poor WN {5,6,2,13,3,4,1,0,8,7} serial, lane B the same at `MS110D_MASK_SEED_OFFSET=10000`, lane C AWGN WN0–8+13 serial then static then doppler; 4 intra-point workers; in-code budgets (6M for WN2/5/6 Poor, §5.3 otherwise); per-point `MS110D_MASK_LOG` + `MS110D_MASK_BURST_LOG` censuses; lanes self-choom +500 (fleet expendable; the box also runs earlyoom avoiding the session). No rebuild once the battery starts.

**Kill/proceed rule (pre-committed).** Proceed to W1 only if ALL of: hermetic suite 0 failed; the eight gated Poor points PASS with mask lines statistically consistent with b38 (identical seeds → expect identical digits; any changed digit on a non-target point is drift); WN7 measured rows reproduce b38 exactly (83/6,486,528-class canonical, 48 disjoint — byte-identical census CSVs); WN8 reproduces coin-flip (4.96E-1/4.97E-1); AWGN 10/10 + static + doppler zero errors; all five corpse pins at their §6 digits. ANY drift → stop, diagnose, and close the discrepancy in writing before any DSP work. This battery's censuses become the program's byte-identity baseline regardless of route.

## Measurements (2026-07-31, HEAD 4994c3f code tree ≡ main 373beb4 + docs-only commits)

- **Hermetic suite: 790 passed / 0 failed / 108 env-gated skips** — the program's re-pinned count (Phase B's 697/0/105 plus the OTA-era additions; zero failures is the pin).
- **All five §6 guard-pin corpses at their exact digits** (`pins.sh`, summaries under `/tmp/wn8-w0-pins` reproduced in the run): WN7 w0/b0 **0 coded / 11c/0r/4v / oracle b5:15**; WN7 w1/b0 **20 coded / 11c/0r/5v**; WN6 w0/b0 **0 / 11c/0r/0v**; WN13sp **0 / 11c/0r/0v**; WN0 w2/b97 **0 coded**.
- **Battery** ([battery/](battery/): per-point mask lines, status.log, 120 census CSVs, census-compare.txt):
  - **Poor canonical — every error count identical to b38 battery-A**: WN0 0, WN1 0, WN2 30/6.09M, WN3 0, WN4 0, WN5 23/6.49M, WN6 35/6.49M, WN13 0, WN7 **83 → 2.56E-5**, WN8 **2,145,864 → 4.96E-1**.
  - **Poor disjoint — identical to b38 battery-B**: WN0 3, WN1 0, WN2 29, WN3 0, WN4 3, WN5 0, WN6 39, WN7 **48 → 1.48E-5**, WN8 **2,150,887 → 4.97E-1**. (WN13 disjoint ran the in-code 3M budget, 0/3,243,520 — b38's lane used an explicit 6M override, 0/6,487,040; zero errors both, a budget-shape difference, not drift.)
  - **WN7 census byte-identity: all 8 files identical to b38** (`census-compare.txt`) — canonical + disjoint × workers 0–3.
  - **AWGN 10/10 zero errors at b38's exact bit counts.** Two points (WN6, WN8) died at first launch with `Fatal error. Internal CLR error. (0x80131506)` at peak battery load — and the MTP wrapper still exited 0, so the lane's rc=0 concealed it; the missing-mask-line rule (the Phase A evidence-chain discipline) caught both. Re-run on the idle box: clean, digits identical to b38.
  - **Static WID2 0/3,043,456** (= b38). **Doppler 3/3 zero errors** (this driver ran the run-masks.sh doppler form — no worker fan-out — so budgets are smaller than b38's lane-C doppler leg; an instrument-shape note on an engineering check, not a gate).
- Wall-clock: ~3.5 h for the three lanes on this box (Debug config, `dotnet test --no-build` per run-masks.sh), vs b38's ~33 min — a speed difference only; every deterministic digit reproduced.

## Verdict

**PASS — no drift.** Current main reproduces the b38/b39 baseline exactly on every comparable digit: both WN8 walls stand as banked (coin-flip both families), WN7's measured rows and censuses are byte-identical, the gated eight hold, and the guard pins are green. This battery's censuses ([battery/census/](battery/census/), all 30 points × workers) are the program's byte-identity baseline from here on. **Proceed to W1.**
