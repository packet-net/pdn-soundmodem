# G0 - close the books and re-baseline (MS110D Poor-gate successor program)

Registered 2026-08-20 before the run, per [poor-gate-successor-plan.md](../../poor-gate-successor-plan.md) §3/§4.

## Registration

**Question.** Does current `main` (`5f7bbb5`, the demodulator functionally frozen since the W6 merge `d2bb4a7` - every later touch under `src/Packet.SoundModem/Ms110d` is comment-only or the `Ms110dModem` host wrapper, which the mask harness does not traverse) reproduce the W6 byte-identity baseline on every comparable digit, with the measured-only points now pinned in code rather than in prose?

**Decision inputs.** The W0 battery ([../2026-07-31-wn8-w0/battery/](../2026-07-31-wn8-w0/battery/): the program's byte-identity baseline for the 108 non-WN8 censuses, WN7 83/3,243,776 canonical and 48/3,243,776 disjoint); the W6 battery ([../2026-07-31-wn8-w6/battery/](../2026-07-31-wn8-w6/battery/): WN8 1,254/4,325,120 canonical = 2.90E-4, 75,713/4,325,120 disjoint = 1.75E-2, AWGN WN8 0/4,325,120); the closeout §6 guard pins re-proved at W0 (WN7 w0/b0 0 coded / 11c/0r/4v / oracle b5:15; WN7 w1/b0 20 coded / 11c/0r/5v; WN6 w0/b0 0; WN13sp 0; WN0 w2/b97 0); the hermetic count 790/0 (W5b2).

**What changes in this leg.** One test-side change and no demodulator change: `Ms110dMaskTests.MeasuredOnlyBank` pins WN7 and WN8's closing counts (both seed families) as exact assertions in the battery configuration (in-code budget, 4 workers, no instrument knobs), discharging the W6 exit (ii) obligation "`MS110D_POOR_GATED` expected-red values re-bank to them", which had been met in documentation only. `MS110D_POOR_GATED=1` keeps its meaning (assert the 1E-5 mask everywhere; the two stay expected-red under it). Also the documentation reconciliation listed in the plan's G0 entry, in a separate docs-only PR.

**Instrument + method.** (a) Release build; the hermetic MS110D namespace and `SourceTextTests` on the exe. (b) The five §6 guard-pin corpses (`pins.sh`, the W0 env set on the Release exe). (c) The three-lane battery in the W0 form (`battery.sh`): lane A canonical Poor WN {5,6,2,13,3,4,1,0,8,7} serial, lane B the same at `MS110D_MASK_SEED_OFFSET=10000`, lane C AWGN WN0-8+13 serial then static then Doppler; 4 intra-point workers; in-code budgets (6M for WN2/5/6 Poor, §5.3 otherwise); per-point `MS110D_MASK_LOG` + `MS110D_MASK_BURST_LOG` censuses; lanes self-choom +500. Run on the Release exe directly rather than through `dotnet test` so a CLR abort under load surfaces as a non-zero rc and a missing mask line (the W0 lesson: the MTP wrapper exited 0 over a startup crash). No rebuild once the battery starts.

**Kill/proceed rule (pre-committed).** Proceed to G1 only if ALL of: hermetic namespace 0 failed; all five corpse pins at their §6 digits; the eight gated Poor points PASS with mask lines byte-consistent with W0 (identical seeds, identical digits); the WN7 and WN8 Poor points pass their new bank assertions (which is the same statement as "digits identical to W0/W6", now made by the suite itself); AWGN 10/10 + static + Doppler zero errors at W0's bit counts; all non-WN8 censuses byte-identical to the W0 baseline and the WN8 censuses byte-identical to W6. ANY drift -> stop, diagnose, and close the discrepancy in writing before any DSP work. This battery's censuses become the successor program's byte-identity baseline regardless of route.

## Measurements

*(appended when the run completes)*
