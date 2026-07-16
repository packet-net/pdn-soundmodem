# MIL-STD-188-110D Appendix D table transcription — notes (transcriber B)

Source: `milstd110d.pdf` (MIL-STD-188-110D, 29 Dec 2017, 270 physical pages,
Distribution A). Appendix D body runs physical (PDF) pages 145-250; the printed
document page number is always PDF page minus 5 (e.g. document page 143 = PDF
page 148). All Appendix D tables except Table D-XX are embedded page images with
no text layer; they were transcribed by eye from the page images (native
resolution 1584x1224, upscaled crops used to verify doubtful cells). Table D-XX
and the D.5.1.3/D.5.1.4 scrambler prose have a real text layer and were
extracted programmatically.

**Unclear-cell count: 0.** Every cell in every transcribed table was legible at
native or 2x-upscaled resolution; no `[unclear:...]` markers were needed.

## Deviations from the brief's scouting notes

- The brief's filename hint called D-VII "constellation-bpsk-qpsk". In the
  actual document, the constellation coordinate tables are: D-VII = **16QAM**,
  D-VIII = **32QAM**, D-IX = **64QAM**, D-X = **256QAM**. BPSK/QPSK/8PSK have no
  coordinate tables — they are plain unit-circle PSK, covered by transcoding
  tables D-III/D-IV/D-V and the 8PSK mapping table D-VI (not transcribed; not
  in the role list). Files are named for the modulation each actually covers:
  `d07-constellation-16qam.csv` ... `d10-constellation-256qam.csv`.
- "Table D-XLIX code-rate/puncturing map": D-XLIX ("Code Rate and Modulation")
  is the per-waveform/bandwidth code-rate map and is what `d49-code-rates.csv`
  contains. The actual puncture masks live in the separate **Table D-L,
  "Puncture patterns"** (document page 222, PDF page 227); to keep the agreed
  1:1 file layout it is transcribed in full at the end of these notes rather
  than in its own CSV.

## Per-table notes

### Table D-I. Symbol Rates and Sub Carrier — `d01-symbol-rates.csv`
- Document page 143, PDF page 148. Image quality: crisp, no doubtful cells.
- 12 rows (3-48 kHz), 3 columns.
- The printed table uses thousands separators in the last four rows
  ("24,000", "15,300", "28,800", "18,300", "33,600", "21,300", "38,400",
  "24,300"); separators dropped in the CSV, digits transcribed as printed.
- Structural: symbol rate = 800 x bandwidth_kHz throughout; sub-carrier
  = 300 + BW/2 Hz, matching the prose formula "(300+(BW/2))" on the same page.

### Table D-II. Modulation used to obtain each data rate — `d02-rate-modulation.csv`
- Document page 144, PDF page 149 (landscape). Crisp; bottom rows re-verified
  on a 2x-upscaled crop of the embedded page image.
- 12 bandwidth rows x 14 waveform-number columns (WN 0-13). Values are data
  rates in bps. Empty cells transcribed as empty; the two dashes (9 kHz WN4,
  18 kHz WN4) transcribed as `-`.
- WN13 (QPSK) exists only for 3 kHz (2400 bps); all other WN13 cells are blank.
- Cells I looked at twice (all confirmed as printed):
  - 3 kHz WN5 = **1600** (not 2400).
  - 21 kHz WN0 = **300**, lower than 18 kHz WN0 = 600, and the whole 21 kHz row
    tracks the 15 kHz row rather than continuing 18 kHz's progression (the
    21 kHz family uses lower code rates instead — see D-XLIX row 21).
  - 42 kHz WN11 = **160000** (verified at 2x zoom; breaks the "same as 36 kHz"
    pattern of neighbouring cells) and 42 kHz WN12 = 192000 (same value as
    36 kHz WN12).
- Structural surprise: there is **no 40 kHz row** in D-I, D-II or D-XLIX, yet
  the list of tables has "TABLE D-LXI. INTERLEAVER INCREMENT VALUE FOR 40 KHZ
  WAVEFORM" (and no 42 kHz increment table is listed: the increment-table list
  runs 3,6,9,12,15,18,21,24,30,36,**40**,48). Looks like a 40-vs-42 kHz
  inconsistency in the standard itself; not resolved here.

### Table D-VII. 16QAM I/Q components — `d07-constellation-16qam.csv`
- Document page 149, PDF page 154. Crisp. 16 rows.
- Two PSK rings per the prose (4PSK inner ring at radius ~0.366 = 0.258819*sqrt(2),
  12PSK outer ring at radius 1.0). Values are 6-decimal sin/cos multiples
  (0.866025, 0.500000, 0.258819).

### Table D-VIII. 32QAM I/Q components — `d08-constellation-32qam.csv`
- Document page 150, PDF page 155. Crisp. 32 rows (two 16-row columns printed
  side by side).
- Self-check passed: symbols 16-31 are exactly symbols 0-15 with Quadrature
  negated.

### Table D-IX. 64QAM I/Q components — `d09-constellation-64qam.csv`
- Document page 152, PDF page 157. Crisp. 64 rows (two 32-row columns).
- Self-check passed: symbols 32-63 are exactly symbols 0-31 with In-Phase
  negated. Value alphabet: 0, 1.000000, 0.822878, 0.821137, 0.932897,
  0.568218, 0.588429, 0.152996, 0.117686, 0.360142, 0.353057.

### Table D-X. 256QAM I/Q components — `d10-constellation-256qam.csv`
- Document pages 154-157, PDF pages 159-162 (4 pages, two 32-row columns per
  page). Crisp. 256 rows.
- Rows 0-127 transcribed cell-by-cell; rows 128-255 were also read from the
  page images and confirmed to be exactly rows 0-127 with In-Phase negated,
  so the CSV was generated from the read rows plus that verified mirror to
  avoid re-typing slips.
- Machine self-checks passed: (a) within every 16-symbol block, entries n+8
  equal entry n with Quadrature negated; (b) rows 128-255 = rows 0-127 with
  In-Phase negated; (c) max symbol magnitude is exactly 1.0 (the outlier pair
  0.056433/0.998304 at symbols 17, 25, 145, 153 lies on the unit circle —
  these are the "slight displacement of the 2 center-top and center-bottom
  constellation points" the D.5.1.2.2.4 prose describes).
- Value alphabet: 0.056433, 0.169300, 0.282166, 0.395033, 0.507899, 0.620766,
  0.733632, 0.846499, 0.959366, 0.998304.

### Table D-XX. Waveform Number Synchronization preamble — `d20-widpn.txt`
- Document page 170, PDF page 175. **Text layer present**; extracted verbatim
  with pypdf (only line-end whitespace trimmed). 256 values, all in 0-7
  (machine-checked).

### Text-layer extracts (`text-layer-extracts.md`)
- D.5.1.3 scrambler prose (document pages 160-161, PDF 165-166) and D.5.1.4
  WID 0 Walsh scrambler (document pages 161-162, PDF 166-167) extracted
  verbatim from the text layer.
- Machine cross-check passed: implementing the printed `tri()` trinomial
  (159,31) generator from the printed 159-bit `bitshift` init state reproduces
  the printed "first 32 symbols of the scramble sequence" exactly, and the
  printed worked example row equals (Walsh 0,4 repeated + sequence) mod 8 in
  every position. The three printed artefacts are mutually consistent.

### Table D-XXI. Mini-probe lengths and base sequences — `d21-miniprobes.csv`
- Document page 171, PDF page 176. Crisp; whole table re-verified on a
  2x-upscaled crop. 21 rows, 3 columns.
- Structural notes:
  - A **length-68** mini-probe exists (base 36, shift 18) between 64 and 72 —
    unexpected given no 68 appears in the D-XXII..D-XXXVI base-sequence table
    titles ("64 AND 72 SYMBOL MINI-PROBES" from the 36-symbol base).
  - The prose above the table says "14 different mini-probe sequences", but
    the table has 21 rows (several lengths share a base sequence; there are
    14 distinct base sequences listed across D-XXII..D-XXXVI).
  - Cyclic shift is base/2 rounded, but rounding direction is inconsistent in
    the printed table: base 13 -> 6 (down), base 169 -> **85** (up), base 289
    -> **145** (up). 85 and 145 verified at 2x zoom; transcribed as printed.

### Table D-XLIX. Code Rate and Modulation — `d49-code-rates.csv`
- Document page 218, PDF page 223 (landscape). Crisp; whole table re-verified
  on an upscaled crop. Same 12x14 shape as D-II; values are code rates.
- Dash cells (9 kHz WN4, 18 kHz WN4) and the WN13-blank pattern mirror D-II
  exactly. 3 kHz WN13 = 9/16.
- 21 kHz and 42 kHz rows use systematically lower code rates than their
  neighbours (2/7 / 1/16 / ... / 4/5 / 9/10 and 4/7 / ... / 5/6 / 3/4),
  consistent with those bandwidths' lower data-rate ladders in D-II.

### Tables D-LXIV and D-LXV (D.6 performance) — `d6x-ber-masks.csv`
- D-LXIV "Data performance requirements, All Bandwidths": document page 241,
  PDF page 246. Crisp. 14 rows (WN 0-13). SNR thresholds for coded BER
  <= 1.0E-5 on AWGN and "Poor" (ITU-R F.1487 mid-latitude-disturbed, 2-path,
  2 ms, 1 Hz) channels; WN11/WN12 have `-` (no requirement) for the Poor
  channel. Exceptions column transcribed verbatim ("<=" substituted for the
  printed <= glyph).
- D-LXV "Static Channel Tests": document page 242, PDF page 247 (this page
  also has a text layer for the prose). 18 rows: bandwidths 3, 6, 9, 12, 24,
  48 kHz x 3 WIDs each. Bandwidth spelled "KHz" in this table as printed.
  Note 9 kHz tests WID 3 (not WID 2) and 24/48 kHz test WID 5 — because WID 2
  resp. WID 4 don't exist in 9/18 kHz (see D-II) and the chosen WIDs vary.
- Structural: D.6.3 "Acquisition performance." reads, in full, "Not yet
  standardized." (document page 242).
- Note the D.6 prose (document page 240) references "Table D-LII" for embedded
  -modem SNR adjustment; D-LII is actually an interleaver-increment table, so
  that cross-reference in the standard appears stale (probably means D-LXIV).

## Table D-L. Puncture patterns (document page 222, PDF page 227) — transcribed here to preserve the 1:1 file layout

Columns: Code Rate | K=7 Puncture Pattern (T1 row / T2 row) | K=9 Puncture
Pattern (T1 row / T2 row) | Number of Repeats.

| Code Rate | K=7 pattern (T1/T2) | K=9 pattern (T1/T2) | Number of Repeats |
|-----------|---------------------|---------------------|-------------------|
| 9/10 | 111101110 / 100010001 | 111000101 / 100111010 | n/a |
| 8/9  | 11110100 / 10001011   | 11100000 / 10011111   | n/a |
| 5/6  | 11010 / 10101         | 10110 / 11001         | n/a |
| 4/5  | 1111 / 1000           | 1101 / 1010           | n/a |
| 3/4  | 110 / 101             | 111 / 100             | n/a |
| 2/3  | 11 / 10               | 11 / 10               | n/a |
| 4/7  | 1111 / 0111           | 1111 / 0111           | n/a |
| 9/16 | 111101111 / 111111011 | 111101111 / 111111011 | n/a |
| 1/2  | n/a                   | n/a                   | n/a |
| 2/5  | 1110 / 1010           | 1110 / 1010           | 1/2 Repeated 2x |
| 1/3  | n/a                   | n/a                   | 2/3 Repeated 2x |
| 2/7  | 1111 / 0111           | 1111 / 0111           | 1/2 Repeated 2x |
| 1/4  | n/a                   | n/a                   | 1/2 repeated 2x |
| 1/6  | n/a                   | n/a                   | 1/2 repeated 3x |
| 1/8  | n/a                   | n/a                   | 1/2 repeated 4x |
| 1/12 | n/a                   | n/a                   | 1/2 repeated 6x |
| 1/16 | n/a                   | n/a                   | 1/2 repeated 8x |

- Machine sanity check passed for every masked rate: (ones in T1 mask + ones
  in T2 mask) / (2 x mask length) equals the code rate, taking the repeat
  factor into account for 2/5 and 2/7.
- The mixed capitalisation of "Repeated"/"repeated" is as printed.
- Per D.5.3.2 (document page 217, text layer), the mother codes are rate-1/2
  K=7 (same as main-body 5.3.2) and rate-1/2 K=9, full-tail-biting; puncturing
  is applied before interleaving.
