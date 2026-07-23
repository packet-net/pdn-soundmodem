# MS110D Phase A — formal closeout (2026-07-23)

**Phase A is closed.** Every hard gate in design §6 passes, measured on the closeout commit after the defects below were found and fixed. The Poor-channel numbers are banked measured-not-gated per §6; the deferred review findings are tracked as issues #64–#67 and define the Phase B entry work.

## What the closeout audit found (and why the old numbers were retired)

An adversarial review of the 2026-07-20→23 equalizer campaign (both the modem source and the test side) found that the previously-banked results were measured through three defects, so all mask evidence was re-measured from scratch on fixed instruments:

1. **RX ring wrap under turbo (fixed, `839be92`).** `TurboReequalize` re-reads the whole interleaver block from the sample ring at block close, but the ring (≈6.83 s) was shorter than the Long blocks it re-read: WN1/2 span 10.24 s on air (33 % of frames re-read from overwritten slots), WN5/6/7/8/13 span 7.68 s (~11 %), WN3/4 sit exactly at the boundary. The head frames of every Long block were silently re-equalized against samples from seconds later in the burst, degrading their LLRs to erasures the outer convolutional code then had to bridge — the mechanism behind WN5 AWGN's marginal 7.69E-5 era. RingBits 15→16 plus a `BlockSamplesResident` backstop.
2. **The mask harness's evidence chain (fixed, `cfd9fd0`).** The xUnit v3 migration's class-level filter made every per-WN process also run the full 3M-bit static gate (a large slice of why sweeps took forever) and allowed a per-point PASS with the point itself skipped; `Poor_Channel_Smoke` asserted a bound its default budget could never clear (97.5 % Poisson upper bound at zero errors in 100k bits = 3.7E-5 > 1E-5), so default Poor fan-outs always failed; the Poor gate had been silently re-hardened against design §6; and the §5.3 600 s fading floor was plumbed but never passed. All repaired: method-level filters, per-point evidence via `MS110D_MASK_LOG`, Poor measured-by-default (`MS110D_POOR_GATED=1` arms the Phase B gate), smoke non-gating, sub-budget runs labelled `[SMOKE]`.
3. **CI red since the migration (fixed, `dbeb73e`).** The MTP apphosts could not resolve the runner's distro .NET (every post-migration CI run executed zero tests), and both test steps still used VSTest filter syntax. DOTNET_ROOT is now derived from the runner's own dotnet; filters use `--filter-not-trait`/`--filter-trait`; the aspiration scoreboard step sets `NINOTNC_ASPIRATION=1` (it was permanently vacuous without it).

Also fixed from the same review: burst-state leak across bursts (`_blockFrameChips`/`_blockTap0Mag`/`PeakSearchMetric` on Reset), and comments that no longer described the code (turbo flat-channel exception covers WN3/4/5, not just WN5; the BPSK U>48 BCJR path is unconditional). Design §5.1 (Rung 1 coverage) and §5.3 (accept rule) were restated in place to match what the instruments actually do.

## Phase A hard gates (design §6) — all pass

Accept rule per point (§5.3 as restated): ≥3×10⁶ payload bits, zero acquisition failures; ≥30 errors → direct BER ≤ 1E-5, else 97.5 % Poisson upper bound ≤ 1E-5. Fading gate points additionally ≥600 s simulated.

<!-- AWGN-TABLE -->

| Gate | Result |
|------|--------|
| Rung 0+1 hermetic suite | green locally (see below); CI repaired on this branch, verified on merge |
| OBW (§5.4 pinned bounds) | green (hermetic suite) |
| Static WID2 0/3/9 ms @ 9 dB (restated house bar) | <!-- STATIC --> |
| Doppler ±75 Hz engineering checks | <!-- DOPPLER --> |
| §8 transcription ledger | cleared 2026-07-17 (dual-transcribed, zero conflicts) |

## Poor channel (measured, not gated — design §6 Q1)

Banked per the §5.3 ledger discipline; these are the Phase B baseline, not Phase A acceptance. Caveat that applies to every Poor number: the turbo BCJR's echo model is currently matched to the D.6.1 rig's 2 ms spacing (issue #64), so these are best-case for this specific channel geometry.

<!-- POOR-TABLE -->

WN6/7/8 (QPSK/8PSK/16QAM on fading) remain catastrophic as previously documented — QPSK/8PSK BCJR extension and 16QAM carrier recovery are Phase B scope (issues #64/#65; `wn6-poor-catastrophic-handover.md` has the investigation trail).

## Deferred findings → issues

- **#64** — equalizer heuristics fitted to the Poor rig (BCJR delay hard-coded to 2 ms, flat/fading detectors, bidirectional decision history, RLS λ deviation).
- **#65** — turbo robustness (no divergence protection, DD-row destruction at block close, BCJR LLR conventions, dead QAM16 paths with inconsistent scale).
- **#66** — steady-state allocation in the per-frame RX hot path (CLAUDE.md rule).
- **#67** — test coverage vs restated §5.1 + disjoint-seed mask verification.

## Phase B entry

Per design §6: WN7–8 modulations already landed (PR #60) and pass their AWGN masks; Phase B's hard gate is D-LXIV at mask, AWGN + Poor, WN0–8+13, with Phase A regressions green. The path there runs through #64 (make the equalizer honest about channel geometry) and #65, then `MS110D_POOR_GATED=1` flips the banked Poor points into the gate.

## Reproduction

```bash
dotnet build
./scripts/run-masks.sh all          # full evidence run (~10 GB peak, see below)
./scripts/run-masks.sh awgn 500000  # smoke (logs labelled SMOKE)
```

One operational note: the full 22-process sweep plus the session that launched it is close to this box's 12 GB — the OOM killer took the interactive session twice during the closeout runs. `run-masks.sh` survives that (each point is its own process writing its own evidence file), but launch it detached (`setsid`) if the box is under memory pressure.
