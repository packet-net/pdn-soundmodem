# CFO-1 — the QPSK detector default reversal (issue #116 program / corpus-decode goal)

Registered-and-measured 2026-07-31 under the goal "decode all corpus frames or name the recording defect", prioritising qpsk600.

## The diagnosis chain (each step banked in this session)

1. **Recordings exonerated**: levels in the GOOD band (peak 0.18–0.25, DC ≈ 0), spectra correct (~1350–1650 Hz), all bursts physically present (energy segmentation), burst durations arithmetically consistent with sane IL2P overheads at the wire rate.
2. **Frame-length handling exonerated**: self-loopback 10/10 at 60/130/255-byte frames for qpsk600/qpsk3600/bpsk1200/afsk300 at healthy SNR.
3. **Constant clock offset excluded**: resampling a failing qpsk600 capture at ±50…±400 ppm never recovers the medium/long frames and shows the short frame on a knife edge (±100 ppm toggles its decode).
4. **Today's modem decodes July's real NinoTNC qpsk600 capture** (short 1360 ms bursts) and the QtSM fixtures — the failure is specific to *coherent tracking across multi-second real bursts*, on top of the banked CFO walls (±5 Hz qpsk600 / ±8 Hz qpsk2400, CFO-0).
5. **The differential detector — already fully implemented, V.26A being differentially encoded by construction — copies all nine QPSK corpus files 3/3** ([data/qc-diff-all.txt](data/)), corpus-wide 40/45 with zero regressions.

## The reversal (mirrors the 2026-07-18 BPSK reversal, #40/#42)

`ModemCatalog.DefaultDetectorFor` → `Differential` for every PSK family; coherent stays selectable via `ModemOptions.Detector`.

| Mode | Corpus (coherent → differential) | CFO half-width (coh → diff) | AWGN 90% knee cost |
|---|---|---|---|
| qpsk600 | 0–1/3 → **3/3 ×3 files** | ±5 → ±10–40 Hz | +1.5 dB (+2.0 → +3.5) |
| qpsk2400 | 3/3 → 3/3 | ±8 → ~±60 Hz | +0.4 dB (+9.4 → +9.8) |
| qpsk3600 | 1–2/3 → **3/3 ×3 files** | ~±60 Hz | +3.5 dB (+14.0 → +17.5) |

Corpus on pure defaults: **40/45** ([data/qc-default-final.txt](data/)); hermetic suite 790/0 with the QtSM interop fixtures green under the new default. The five remaining files are non-QPSK stragglers with named symptoms (c4fsk9600 below its ~250 ms acquisition floor ×2; single-frame misses afsk1200-il2p@120 (short), bpsk1200@500 (short+medium), afsk300@240 (medium)) — the goal's open remainder.

## What this leaves for the #116 design leg

qpsk600's differential wall (±10–40 Hz) still needs the bank/offset-search stage for the ±100-class target; qpsk2400/qpsk3600 are near it already. The `--detector` axis is now a permanent `sm-ota sim` instrument alongside `--cfo`.
