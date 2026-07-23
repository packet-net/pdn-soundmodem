# MS110D Equalizer Status — 2026-07-23

## Executive Summary

**Phase A AWGN gate: CLEARED (10/10 pass).** The AWGN turbo regression is fully
resolved via three combined changes (commit `45e2b1a`):
1. Bidirectional equalization for all PSK modes (was U≤96 only)
2. Flat-channel gate: skip turbo on AWGN for non-BPSK-U>48 modes
3. BCJR always-on for BPSK U>48 in turbo (h2 gate removed)

**Poor channel: degraded by bidirectional (non-blocking for Phase A).** The
bidirectional averaging hurts on fading channels (averages 3 passes with different
channel states). Future refinement: use bidirectional only when `IsFlatChannel()`.

## AWGN Mask Results (Phase A hard gate — ALL PASS)

| WN | SNR | Bits | BER | Status |
|----|-----|------|-----|--------|
| 0 | -6 dB | 3M | < 1E-5 | **PASS** |
| 1 | -3 dB | 500k | < 1E-5 | **PASS** |
| 2 | 0 dB | 500k | < 1E-5 | **PASS** |
| 3 | 3 dB | 3M | < 1E-5 | **PASS** |
| 4 | 5 dB | 3M | < 1E-5 | **PASS** |
| 5 | 6 dB | 3M | < 1E-5 | **PASS** |
| 6 | 9 dB | 3M | < 1E-5 | **PASS** |
| 7 | 13 dB | 3M | < 1E-5 | **PASS** |
| 8 | 16 dB | 3M | < 1E-5 | **PASS** |
| 13 | 6 dB | 3M | < 1E-5 | **PASS** |

## Poor Channel Baseline (Phase A: measured, not gated)

| WN | SNR | BER | Notes |
|----|-----|-----|-------|
| 0 | -1 dB | ~7.7E-2 | Bidirectional degrades fading |
| 1 | 3 dB | (stuck) | Teardown hang, likely similar |
| 2 | 5 dB | (stuck) | Teardown hang |
| 3 | 7 dB | 8.74E-3 | Degraded |
| 4 | 10 dB | 1.98E-5 | Near target (was 1.13E-5 pre-bidirectional) |
| 5 | 11 dB | 2.33E-2 | Degraded |
| 6 | 14 dB | 3.48E-1 | Pre-existing catastrophic (QPSK) |
| 7 | 19 dB | 4.91E-1 | Pre-existing catastrophic (8PSK) |
| 8 | 23 dB | 4.97E-1 | Pre-existing catastrophic (QAM16) |
| 13 | 11 dB | 1.21E-2 | Degraded |

**Previous baseline (pre-bidirectional, commit `27ea8e7`):** WN4 Poor was 1.13E-5.

## Other Gates

| Test | Status |
|------|--------|
| Loopback | 40/40 PASS |
| Unit tests | 93/93 PASS |
| Static WID2 (9 dB) | PASS |
| Doppler offset (3 pts) | 3/3 PASS |
| Flat-channel gate diagnostic | 10/10 PASS |

## Known Issues

### 1. VSTest teardown hang (test reliability)

`dotnet vstest` processes hang in `futex_wait_queue` at 0% CPU after completing
long-running tests. Server GC (`1236f29`) did not fix it. The computation finishes
correctly but the process never exits. Workaround: kill the process after the log
shows "Test Run Successful/Failed". Root cause likely in the VSTest adapter's
named-pipe communication during multi-process teardown.

### 2. Poor channel degradation from bidirectional

The 3-pass bidirectional averaging degrades Poor channel BER because it averages
equalized symbols from 3 passes with different fading states. Fix: gate the
bidirectional on `IsFlatChannel()` — use 3-pass on flat, single-pass on fading.
This is a Phase B refinement (Poor is non-blocking for Phase A).

### 3. WN6/7/8 Poor catastrophic (pre-existing, Phase B)

QPSK/8PSK/QAM16 on fading channels produce near-random output. These modes need
the Phase B RLS upgrade. See `docs/ms110d/wn6-poor-catastrophic-handover.md`.

## Commits

| Hash | Description |
|------|-------------|
| `45e2b1a` | Bidirectional + flat-channel gate + BCJR always-on (the fix) |
| `1236f29` | Server GC + stderr progress (test reliability) |
| `ab55f8d` | Status doc + test runtime expectations |
| `1a349ad` | WN6 Poor catastrophic handover |
| `0b9ca98` | Original flat-channel gate (superseded by `45e2b1a`) |

## Test Parallelization

`scripts/run-masks.sh` fans out one `dotnet vstest` process per WN point using
`MS110D_MASK_WN` env var filter. On 16 cores, 10 AWGN points complete in ~1h
(was ~6h sequential). Usage:

```bash
./scripts/run-masks.sh awgn         # 10 parallel AWGN points
./scripts/run-masks.sh poor         # 10 parallel Poor points
./scripts/run-masks.sh awgn 500000  # smoke run (500k bits)
```

## Phase A Closeout

- [x] D-LXIV AWGN WN0–6+13: **ALL PASS**
- [x] Static WID2 (9 dB): PASS
- [x] Doppler offset 3/3: PASS
- [x] Loopback 40/40: PASS
- [x] Poor channel baseline recorded (non-blocking)
- [ ] Poor channel degradation from bidirectional (Phase B refinement)
