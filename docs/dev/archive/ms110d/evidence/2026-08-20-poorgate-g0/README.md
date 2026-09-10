# G0 - close the books and re-baseline (MS110D Poor-gate successor program)

Registered 2026-08-20 before the run, per [poor-gate-successor-plan.md](../../poor-gate-successor-plan.md) §3/§4.

## Registration

**Question.** Does current `main` (`5f7bbb5`, the demodulator functionally frozen since the W6 merge `d2bb4a7` - every later touch under `src/Packet.SoundModem/Ms110d` is comment-only or the `Ms110dModem` host wrapper, which the mask harness does not traverse) reproduce the W6 byte-identity baseline on every comparable digit, with the measured-only points now pinned in code rather than in prose?

**Decision inputs.** The W0 battery ([../2026-07-31-wn8-w0/battery/](../2026-07-31-wn8-w0/battery/): the program's byte-identity baseline for the 108 non-WN8 censuses, WN7 83/3,243,776 canonical and 48/3,243,776 disjoint); the W6 battery ([../2026-07-31-wn8-w6/battery/](../2026-07-31-wn8-w6/battery/): WN8 1,254/4,325,120 canonical = 2.90E-4, 75,713/4,325,120 disjoint = 1.75E-2, AWGN WN8 0/4,325,120); the closeout §6 guard pins re-proved at W0 (WN7 w0/b0 0 coded / 11c/0r/4v / oracle b5:15; WN7 w1/b0 20 coded / 11c/0r/5v; WN6 w0/b0 0; WN13sp 0; WN0 w2/b97 0); the hermetic count 790/0 (W5b2).

**What changes in this leg.** One test-side change and no demodulator change: `Ms110dMaskTests.MeasuredOnlyBank` pins WN7 and WN8's closing counts (both seed families) as exact assertions in the battery configuration (in-code budget, 4 workers, no instrument knobs), discharging the W6 exit (ii) obligation "`MS110D_POOR_GATED` expected-red values re-bank to them", which had been met in documentation only. `MS110D_POOR_GATED=1` keeps its meaning (assert the 1E-5 mask everywhere; the two stay expected-red under it). Also the documentation reconciliation listed in the plan's G0 entry, in a separate docs-only PR.

**Instrument + method.** (a) Release build; the hermetic MS110D namespace and `SourceTextTests` on the exe. (b) The five §6 guard-pin corpses (`pins.sh`, the W0 env set on the Release exe). (c) The three-lane battery in the W0 form (`battery.sh`): lane A canonical Poor WN {5,6,2,13,3,4,1,0,8,7} serial, lane B the same at `MS110D_MASK_SEED_OFFSET=10000`, lane C AWGN WN0-8+13 serial then static then Doppler; 4 intra-point workers; in-code budgets (6M for WN2/5/6 Poor, §5.3 otherwise); per-point `MS110D_MASK_LOG` + `MS110D_MASK_BURST_LOG` censuses; lanes self-choom +500. Run on the Release exe directly rather than through `dotnet test` so a CLR abort under load surfaces as a non-zero rc and a missing mask line (the W0 lesson: the MTP wrapper exited 0 over a startup crash). No rebuild once the battery starts.

**Kill/proceed rule (pre-committed).** Proceed to G1 only if ALL of: hermetic namespace 0 failed; all five corpse pins at their §6 digits; the eight gated Poor points PASS with mask lines byte-consistent with W0 (identical seeds, identical digits); the WN7 and WN8 Poor points pass their new bank assertions (which is the same statement as "digits identical to W0/W6", now made by the suite itself); AWGN 10/10 + static + Doppler zero errors at W0's bit counts; all non-WN8 censuses byte-identical to the W0 baseline and the WN8 censuses byte-identical to W6. ANY drift -> stop, diagnose, and close the discrepancy in writing before any DSP work. This battery's censuses become the successor program's byte-identity baseline regardless of route.

## Measurements (2026-08-20, Release exe built 15:52:48 UTC from the bank commit on main `5f7bbb5`; PR #313's docs merged underneath while the battery ran, no source change)

- **Hermetic: MS110D namespace 297 passed / 0 failed / 60 env-gated skips; `SourceTextTests` 2/0** on the same exe.
- **All five §6 guard-pin corpses at their exact digits** (`pins-summary.txt`; autopsy summaries reproduced in the run): WN7 w0/b0 **0 coded / 11c/0r/4v / oracle b5:15**; WN7 w1/b0 **20 coded / 11c/0r/5v**; WN6 w0/b0 **0 / 11c/0r/0v**; WN13sp **0 / 11c/0r/0v**; WN0 w2/b97 **0 coded**. Total wall ~2.5 min on the exe.
- **Battery** ([battery/](battery/): 33 mask lines, status.log, all 120 census CSVs under [battery/census/](battery/census/)), 33 legs in **40 min wall** (15:57:13 to 16:36:51), every leg rc=0 with its mask line present:
  - **Poor canonical, every error count identical to W0/W6**: WN0 0, WN1 0, WN2 30/6,086,912, WN3 0, WN4 0, WN5 23/6,486,528, WN6 35/6,487,296, WN13 0, WN7 **83/3,243,776 = 2.56E-5**, WN8 **1,254/4,325,120 = 2.90E-4**.
  - **Poor disjoint, identical to W0/W6**: WN0 3, WN1 0, WN2 29, WN3 0, WN4 3, WN5 0, WN6 39, WN13 0, WN7 **48 = 1.48E-5**, WN8 **75,713 = 1.75E-2**.
  - **AWGN 10/10 zero errors at the baseline bit counts** (WN8 0/4,325,120); **static WID2 0/3,043,456**; **Doppler 3/3 zero errors** (220,896 / 220,896 / 270,304 bits, the W0 battery.sh form).
  - **Census byte-identity: 120 of 120 files identical** to their baselines (`compare.sh`: 104 non-WN8 files vs the W0 battery, the eight Poor WN8 files vs W6, the four AWGN WN8 files vs W5b2, the only battery that kept them).
  - **The bank held on all four measured-only legs**: `poor-wn7`, `poord-wn7`, `poor-wn8`, `poord-wn8` each ran under the new `MeasuredOnlyBank` assertions and passed (Total 10 / Failed 0 / 9 skipped per log), so the bank and the re-baseline validate each other as registered.
- Instrument note: running the Release test exe directly instead of `dotnet test` on the Debug build is what turned the W0/W6 3.5 h battery into 40 min with identical digits on every point, and it makes a CLR abort visible as a non-zero rc plus a missing mask line (the `mask=line|missing` column in status.log). This is the battery form from here on.

## Verdict

**PASS - no drift.** Current main reproduces the W0/W6 baseline on every comparable digit: the gated eight hold, WN7 and WN8 reproduce their measured-only closing counts and now assert them, the guard pins are exact, and all 120 censuses are byte-identical. This battery's censuses ([battery/census/](battery/census/)) are the successor program's byte-identity baseline from here on. **Proceed to G1.**
