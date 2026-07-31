# W0 — entry re-baseline on current main (WN8 redesign program)

Registered 2026-07-31 before the run, per [wn8-program-plan.md](../../wn8-program-plan.md) §3/§4.

## Registration

**Question.** Has `main` drifted from the Phase B byte-identity baseline (the b38 battery, [../2026-07-26-phase-b38-wn7-anchortrack/battery/](../2026-07-26-phase-b38-wn7-anchortrack/battery/), plus the b39 WN7/WN8 verdict numbers and the closeout §6 guard pins)? Main has moved since b38 — at least PR #103's input AGC and the OTA-era work — and the program needs its own banked baseline before any DSP change.

**Decision inputs.** The b38 battery-A/-B/-C mask lines and WN7 census CSVs; [../2026-07-26-phase-b39-wn8-verdict/](../2026-07-26-phase-b39-wn8-verdict/) (WN8 4.96E-1/4.97E-1, ceiling 9.2E-4/2.5E-4); [phase-b-closeout.md](../../phase-b-closeout.md) §1 table + §6 pin registry (hermetic 697 passed / 0 failed / 105 env-gated skips).

**Method.** (a) `dotnet build` then the full hermetic suite `dotnet test --no-build` — re-pin the count. (b) The five §6 guard-pin corpse runs (`Mask_Burst_Corpse_Dump`, exact env in `pins.sh`): WN7 w0/b0 (+oracle), WN7 w1/b0, WN6 w0/b0, WN13 SEED=10513 w3/b5, WN0 w2/b97. (c) The three-lane battery in the b38 form (`battery.sh`, committed beside this file): lane A canonical Poor WN {5,6,2,13,3,4,1,0,8,7} serial, lane B the same at `MS110D_MASK_SEED_OFFSET=10000`, lane C AWGN WN0–8+13 serial then static then doppler; 4 intra-point workers; in-code budgets (6M for WN2/5/6 Poor, §5.3 otherwise); per-point `MS110D_MASK_LOG` + `MS110D_MASK_BURST_LOG` censuses; lanes self-choom +500 (fleet expendable; the box also runs earlyoom avoiding the session). No rebuild once the battery starts.

**Kill/proceed rule (pre-committed).** Proceed to W1 only if ALL of: hermetic suite 0 failed; the eight gated Poor points PASS with mask lines statistically consistent with b38 (identical seeds → expect identical digits; any changed digit on a non-target point is drift); WN7 measured rows reproduce b38 exactly (83/6,486,528-class canonical, 48 disjoint — byte-identical census CSVs); WN8 reproduces coin-flip (4.96E-1/4.97E-1); AWGN 10/10 + static + doppler zero errors; all five corpse pins at their §6 digits. ANY drift → stop, diagnose, and close the discrepancy in writing before any DSP work. This battery's censuses become the program's byte-identity baseline regardless of route.

## Measurements

(appended after the run)
