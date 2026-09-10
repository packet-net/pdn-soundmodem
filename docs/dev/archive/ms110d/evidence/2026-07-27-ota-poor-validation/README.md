# MS110D on-air Poor-channel validation — WN2 (AGC) + WN6/WN13 (GPSDO)

**2026-07-27.** Both diagnosed real-RF Poor-channel failures resolved *and* confirmed over the air, each by its own fix and each with the diagnostic signature that proves the mechanism.

## Setup

| | |
|---|---|
| TX | FlexRadio 6500 @ `10.45.0.76`, **GPS-locked** to the external 10 MHz reference (`oscillator.setting=external`, `oscillator.state=external`, `oscillator.locked=1`, `freq_error_ppb=0`) |
| Path | ANT2 → 125 dB wired attenuator chain (15/50 W → 10/5 W → 50/2 W → 50/2 W) → SDRplay RSP1 on `studybox` |
| Capture | `sm-ota ladder … --capture rsp --rsp-host studybox`, `rx_sdr` CF32 over ssh, `AGC=false,IFGR=20,RFGR=0` |
| Route / power | IQ route, `--rf-power 4` → ~3.7 W measured (well under the 15 W radio cap and the 5 W RSP ceiling), `--dial-correction 0` |
| Modem | `a78bd0ecc58c` (main merged into `ms110d-ota-harness` to carry the WN2 AGC fix #103) |

## Reference calibration — the RSP1 against a GPS-truth Flex

Measured grid-free (clean tone from the GPS-accurate Flex → RSP1 → FFT peak):

- **RSP1 reference offset = +35.5 Hz at 18.1085 MHz (−1.96 ppm)** — sharp, unambiguous peak.
- **The total Flex↔RSP1 offset collapsed from ~−202 Hz (drifting ~+3–4 Hz/min) to a static +36 Hz the instant the Flex locked to the external reference.** That is the proof that the *Flex TCXO* was the dominant reference-wander source — and it is why disciplining it (below) recovers QPSK.
- At dial 0 the residual sits mid-grid (CFO +37…+42 across the runs), so acquisition is solid without a correction.

The tone→FFT method is the repeatable RSP freq-cal and should be wired into the harness as `sm-ota tone --capture rsp` (currently the `tone` capture path is UberSDR-only).

## Test 1 — WN2 (BPSK r1/4, K=48) Poor: the input-AGC fix (#101 → #103)

Pre-fix, WN2 Poor ended **all-SignalLost** over this rig (DFE dead-init at low receive level). With the merged AGC fix:

```
  #   start s   WN   asked     got   CFO Hz   coded BER  uncoded BER  end
  0     21.50    2    12.0    13.4     36.9           0     3.20E-01  Eom
  1     33.60    2       —    11.3     37.3 unscheduled            —  Eom
  2     45.20    2    12.0    11.9     38.2           0     1.46E-01  Eom
  3     57.30    2       —    -4.2     39.4 unscheduled            —  SignalLost
```

**Decodes, coded BER 0.** And the diagnostics show the AGC *engaging* — this is not WN2 riding the cleaner reference:

```
agc@1728: level=0.0063 gain=19.021   → DFE recovers, coded BER 0
agc@1152: level=0.0036 gain=32.000   → DFE recovers, coded BER 0
```

The receive level (0.0036–0.0063) is squarely in the dead-init regime — the very condition that gave `init gain≈0.005 SignalLost` pre-fix — and the input AGC catches it and boosts **19–32×**, after which the DFE inits healthy and decodes bit-exact. The level fix is what turns all-SignalLost into clean decodes, demonstrated on real RF.

## Test 2 — WN6 (QPSK r3/4) & WN13 (QPSK r9/16) Poor: the GPSDO reference (#102)

Pre-fix, both QPSK-Poor modes **collapsed into the first fade** over real RF (SignalLost, coded BER ~0.5) — diagnosed (#102) as receiver-reference phase-noise. With the Flex GPS-locked:

**WN6 Poor:**
```
  #   start s   WN   asked     got   CFO Hz   coded BER  uncoded BER  end
  0     21.10    6    12.0    14.2     37.8           0     2.88E-02  Eom
  2     44.50    6    12.0    10.9     38.8           0     4.59E-02  Eom
  3     56.20    6    12.0    -4.4     40.0    5.14E-01     2.50E-01  SignalLost
```

**WN13 Poor:**
```
  #   start s   WN   asked     got   CFO Hz   coded BER  uncoded BER  end
  0     21.60   13    14.0    15.4     39.9           0     6.84E-03  Eom
  2     45.00   13    14.0    12.1     40.4           0     5.03E-02  Eom
  3     56.70   13    14.0    -1.7     41.6    3.32E-01     2.24E-01  SignalLost
```

**Both decode, coded BER 0**, tracking cleanly through the Poor fades. Combined with the reference-offset collapse above (Flex TCXO removed by the GPS lock), this confirms the #102 causal chain end-to-end on the air: the wander that broke QPSK-Poor was Flex-TCXO-dominated, and disciplining the reference brings QPSK back. No modem code change — a rig/reference fix.

## Caveats

- **Every test lost exactly one burst — always the deep-fade delivery** (WN2 −4.2 dB, WN6 −4.4 dB, WN13 −1.7 dB). At those delivered SNRs the modes are legitimately below threshold; these are orthogonal to both fixes, not failures of them.
- The scorer's schedule-matching shows an "unscheduled" burst per pass (the found-by-acquisition times drift ~one slot from the scheduled times). The bursts decode correctly; the match is a harness nit, not a modem issue.
- Confound noted for completeness: WN2's run also benefits from the cleaner reference, but the AGC-firing diagnostic (level 0.004–0.006 → 19–32× boost) isolates the level fix as the operative one; WN2's #102 injection experiment already showed it is level-driven, not phase-driven.

## Provenance

- WN2 dead-init: issue #101 → PR #103 (input signal-level AGC, merged to `main`).
- WN6/WN13 reference phase-noise: issue #102 (resolved by GPSDO discipline; no code change).
- Earlier AWGN on-air (WN4/6/13): `../2026-07-27-ota-lab-campaign/`.
- Reference/oscillator status read via the Flex `radio`/`radio oscillator` status objects — M0LTE.Flex issue #11 (typed-property request).
