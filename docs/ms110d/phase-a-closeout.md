# MS110D Phase A — formal closeout (2026-07-23)

**Phase A is closed.** Every hard gate in design §6 passes, measured on the closeout commit after the defects below were found and fixed. The Poor-channel numbers are banked measured-not-gated per §6; the review findings were fixed on this branch where fixable (see below); the remainders in issues #64/#65/#67 define the Phase B entry work.

## What the closeout audit found (and why the old numbers were retired)

An adversarial review of the 2026-07-20→23 equalizer campaign (both the modem source and the test side) found that the previously-banked results were measured through three defects, so all mask evidence was re-measured from scratch on fixed instruments:

1. **RX ring wrap under turbo (fixed, `839be92`).** `TurboReequalize` re-reads the whole interleaver block from the sample ring at block close, but the ring (≈6.83 s) was shorter than the Long blocks it re-read: WN1/2 span 10.24 s on air (33 % of frames re-read from overwritten slots), WN5/6/7/8/13 span 7.68 s (~11 %), WN3/4 sit exactly at the boundary. The head frames of every Long block were silently re-equalized against samples from seconds later in the burst, degrading their LLRs to erasures the outer convolutional code then had to bridge — the mechanism behind WN5 AWGN's marginal 7.69E-5 era. RingBits 15→16 plus a `BlockSamplesResident` backstop.
2. **The mask harness's evidence chain (fixed, `cfd9fd0`).** The xUnit v3 migration's class-level filter made every per-WN process also run the full 3M-bit static gate (a large slice of why sweeps took forever) and allowed a per-point PASS with the point itself skipped; `Poor_Channel_Smoke` asserted a bound its default budget could never clear (97.5 % Poisson upper bound at zero errors in 100k bits = 3.7E-5 > 1E-5), so default Poor fan-outs always failed; the Poor gate had been silently re-hardened against design §6; and the §5.3 600 s fading floor was plumbed but never passed. All repaired: method-level filters, per-point evidence via `MS110D_MASK_LOG`, Poor measured-by-default (`MS110D_POOR_GATED=1` arms the Phase B gate), smoke non-gating, sub-budget runs labelled `[SMOKE]`.
3. **CI red since the migration (fixed, `dbeb73e`).** The MTP apphosts could not resolve the runner's distro .NET (every post-migration CI run executed zero tests), and both test steps still used VSTest filter syntax. DOTNET_ROOT is now derived from the runner's own dotnet; filters use `--filter-not-trait`/`--filter-trait`; the aspiration scoreboard step sets `NINOTNC_ASPIRATION=1` (it was permanently vacuous without it).

Also fixed from the same review: burst-state leak across bursts (`_blockFrameChips`/`_blockTap0Mag`/`PeakSearchMetric` on Reset), and comments that no longer described the code (turbo flat-channel exception covers WN3/4/5, not just WN5; the BPSK U>48 BCJR path is unconditional). Design §5.1 (Rung 1 coverage) and §5.3 (accept rule) were restated in place to match what the instruments actually do.

## Phase A hard gates (design §6) — all pass

Accept rule per point (§5.3 as restated): ≥3×10⁶ payload bits, zero acquisition failures; ≥30 errors → direct BER ≤ 1E-5, else 97.5 % Poisson upper bound ≤ 1E-5. Fading gate points additionally ≥600 s simulated.

**D-LXIV AWGN masks — 10/10 pass, every point ≥3×10⁶ bits with ZERO errors** (run 2026-07-23/24, commits `ae3998c⁻`; the per-point 97.5 % Poisson upper bounds sit at ~1.2E-6, an 8× margin under the 1E-5 mask):

| WN | SNR | Bits | Errors | Result |
|----|-----|------|--------|--------|
| 0 | -6 dB | 3,000,704 | 0 | BER 0.00E+000, 97.5 % upper bound 1.22E-006 |
| 1 | -3 dB | 3,002,720 | 0 | BER 0.00E+000, 97.5 % upper bound 1.22E-006 |
| 2 | 0 dB | 3,018,912 | 0 | BER 0.00E+000, 97.5 % upper bound 1.22E-006 |
| 3 | +3 dB | 3,033,312 | 0 | BER 0.00E+000, 97.5 % upper bound 1.21E-006 |
| 4 | +5 dB | 3,087,456 | 0 | BER 0.00E+000, 97.5 % upper bound 1.19E-006 |
| 5 | +6 dB | 3,108,128 | 0 | BER 0.00E+000, 97.5 % upper bound 1.18E-006 |
| 6 | +9 dB | 3,243,648 | 0 | BER 0.00E+000, 97.5 % upper bound 1.13E-006 |
| 7 | +13 dB | 3,243,776 | 0 | BER 0.00E+000, 97.5 % upper bound 1.13E-006 |
| 8 | +16 dB | 3,243,840 | 0 | BER 0.00E+000, 97.5 % upper bound 1.13E-006 |
| 13 | +6 dB | 3,040,800 | 0 | BER 0.00E+000, 97.5 % upper bound 1.21E-006 |

**Disjoint-seed cross-checks (seed+10000, issue #67):** AWGN WN4/WN5 both 0 errors at full budget on fresh realizations; Poor WN4 1.33E-5 (41 errors) vs the canonical seed's 2.36E-5 — statistically consistent. Neither the gate nor the baseline is a seed artifact. Raw evidence: [evidence/2026-07-23-phase-a-closeout/](evidence/2026-07-23-phase-a-closeout/).

| Gate | Result |
|------|--------|
| Rung 0+1 hermetic suite | **green** — 541 passed / 0 failed / 42 env-gated skips (2026-07-24, closeout tree); CI repaired on this branch, first green run on merge |
| OBW (§5.4 pinned bounds) | green (hermetic suite) |
| Static WID2 0/3/9 ms @ 9 dB (restated house bar) | **PASS** — 3,018,912 bits, 0 errors, 123 bursts, 0 acquisition failures |
| Doppler ±75 Hz engineering checks | **3/3 clean** — WN2 ±75 Hz, WN6 +75 Hz, 0 errors, 0 acquisition failures |
| §8 transcription ledger | cleared 2026-07-17 (dual-transcribed, zero conflicts) |

## Poor channel (measured, not gated — design §6 Q1)

Banked per the §5.3 ledger discipline; these are the Phase B baseline, not Phase A acceptance. Standing caveat: the turbo BCJR models one echo at a searched lag ≤ 3.3 ms (the 2^delay-state trellis ceiling — issue #64), so longer-spread real channels lean entirely on the DFE feedback span.

| WN | SNR | Bits | Errors | Result |
|----|-----|------|--------|--------|
| 0 | -1 dB | 3,000,704 | 244102 | BER 8.13E-002 (direct, ≥30 errors) |
| 1 | +3 dB | 3,002,720 | 85712 | BER 2.85E-002 (direct, ≥30 errors) |
| 2 | +5 dB | 3,018,912 | 110785 | BER 3.67E-002 (direct, ≥30 errors) |
| 3 | +7 dB | 3,033,312 | 26340 | BER 8.68E-003 (direct, ≥30 errors) |
| 4 | +10 dB | 3,087,456 | 73 | BER 2.36E-005 (direct, ≥30 errors) |
| 5 | +11 dB | 3,108,128 | 67718 | BER 2.18E-002 (direct, ≥30 errors) |
| 6 | +14 dB | 3,243,648 | 419899 | BER 1.29E-001 (direct, ≥30 errors) |
| 7 | +19 dB | 3,243,776 | 1512547 | BER 4.66E-001 (direct, ≥30 errors) |
| 8 | +23 dB | 3,784,480 | 1876604 | BER 4.96E-001 (direct, ≥30 errors) |
| 13 | +11 dB | 3,040,800 | 1874 | BER 6.16E-004 (direct, ≥30 errors) |

Notes on this table: it is the **first complete Poor baseline ever banked** — WN0/1/2 had never finished a run (WN0 crashed the receiver every time via the corrupt-WID bug below; WN1/2 died to the teardown hang, then to OOM). WN1 and WN2 each logged one acquisition failure in ~250/125 bursts, honestly counted as full-burst errors. WN4's 2.36E-5 vs the campaign's claimed 7.04E-6 (and 1.88E-5 measured pre-de-rig on the same seed) is the price of removing the rig-fitted heuristics — principally the BCJR echo delay no longer being hard-wired to this rig's exact 2 ms spacing. That claimed 7.04E-6 was also measured through the ring-wrap bug and the old evidence chain, so it was never a trustworthy number.

**Found by this evidence run — a receiver-killing acquisition bug (fixed, `ae3998c`):** at −1 dB on fading, a noise-corrupted WID can pass its 3-dibit checksum yet decode to (WN 0, UltraShort), a combination Table D-XXXVII does not define; `TryReadPreamble` let `Get3k`'s ArgumentException escape the receive path — on air, a daemon crash from unlucky noise, and the actual cause of every historically "stuck" Poor WN0 run. Acquisition now pre-validates with `Has3k` and rejects the candidate. Proven against the deterministic reproducer: the exact seed-500 stream that crashed at 2.84M bits now completes (BER 7.73E-2, landing on the historic figure).

WN6/7/8 (QPSK/8PSK/16QAM on fading) remain catastrophic as previously documented — QPSK/8PSK BCJR extension and 16QAM carrier recovery are Phase B scope (issues #64/#65; `wn6-poor-catastrophic-handover.md` has the investigation trail).

## Review findings: fixed here vs deferred

Fixed on this branch (see the issue comment trails for detail):
- **#64 (mostly fixed):** fading detection replaced with the CFO-immune tap-change statistic classified by recurring excursions over a min-tracking floor (validated: 0/1664 false-positive frames on AWGN, 0/4096 on static including the convergence transient, 152/256 detecting on Poor); IsFlatChannel is the same detector (UltraShort interleavers now classify from frame 1); bidirectional passes re-seed the frame-start decision history; the BCJR echo delay is searched (lags 1–8 + significance floor) instead of hard-coded. Remaining: RLS λ deviation, RlsUpdate weight/P asymmetry, and the structural 2-tap-trellis ceiling (2^delay states → echoes beyond ~3.3 ms stay the DFE feedback span's job).
- **#65 (mostly fixed):** turbo reverts to the first-pass decode on non-convergence; DD training rows survive the turbo pass (Dfe.Snapshot/RestoreTraining); noiseVar per complex dimension; dead QAM16 paths are explicit throws. Remaining: per-position h1[] time-invariance.
- **#66 (closed):** DFE solver scratch preallocated, the O(n⁴) RLS seed made O(n³), BCJR trellis pooled, probes cached — bit-identical numerics proven by three before/after evidence pairs including a 13-error static run.
- **#67 (mostly fixed):** clock-skew rig added — **±50 ppm passes bit-exact with ~14× margin** (breaking points ±700 ppm at ~4 s bursts, ±300–400 ppm at ~11 s), so the design figure is met; hermetic ±75 Hz CFO green for all four modulation families; the WN×interleaver×K matrix filled (23 pinned rows, all 31 missing combos verified); WN7/8 in the permutation check; disjoint-seed verification via `MS110D_MASK_SEED_OFFSET`. Remaining: data-phase late entry, positive EOT-assisted termination, every-offset cold start, two weak asserts.

Also from the closeout: intra-point mask parallelism (`MS110D_MASK_WORKERS` — disjoint-seed workers per point, counts summed, validated serial-vs-parallel; the low-rate poles drop ~N×) and the corrupt-WID acquisition crash fix above.

## Phase B entry

Per design §6: WN7–8 modulations already landed (PR #60) and pass their AWGN masks; Phase B's hard gate is D-LXIV at mask, AWGN + Poor, WN0–8+13, with Phase A regressions green. The path there runs through #64 (make the equalizer honest about channel geometry) and #65, then `MS110D_POOR_GATED=1` flips the banked Poor points into the gate.

## Reproduction

```bash
dotnet build
./scripts/run-masks.sh all          # full evidence run (~10 GB peak, see below)
./scripts/run-masks.sh awgn 500000  # smoke (logs labelled SMOKE)
```

One operational note: the full 22-process sweep plus the session that launched it is close to this box's 12 GB — the OOM killer took the interactive session twice during the closeout runs. `run-masks.sh` survives that (each point is its own process writing its own evidence file), but launch it detached (`setsid`) if the box is under memory pressure.
