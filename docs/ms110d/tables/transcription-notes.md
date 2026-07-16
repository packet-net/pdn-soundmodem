# MIL-STD-188-110D Appendix D — transcription notes (transcriber A)

Source: MIL-STD-188-110D, 29 Dec 2017 (everyspec.com copy, 270 PDF pages).
Method: image-only pages rendered with `pdftoppm -r 200/-r 400` and transcribed by eye;
text-layer pages extracted with `pdftotext -layout`. Worked only from the PDF.

**Page mapping: document page = PDF page − 5** for all of Appendix D (verified via the
Table D-XX caption: doc 170 = PDF 175).

Image quality throughout: excellent — these are not photocopied scans but Word-rendered
pages rasterized into the PDF (crisp type at 200 dpi). No cell anywhere had to be flagged
`[unclear:…]`; **0 unclear cells**. Ambiguity risk is therefore misreading, not illegibility —
mitigated with the cross-checks noted per table below.

## Inventory

| File | Table | Doc page | PDF page | Rows (data) |
|---|---|---|---|---|
| d01-symbol-rates.csv | D-I. Symbol Rates and Sub Carrier | 143 | 148 | 12 |
| d02-rate-modulation.csv | D-II. Modulation used to obtain each data rate | 144 | 149 | 12 × 14 WID cols |
| d07-constellation-16qam.csv | D-VII. I/Q components of each 16QAM symbol | 149 | 154 | 16 |
| d08-constellation-32qam.csv | D-VIII. I/Q components of each 32QAM symbol | 150 | 155 | 32 |
| d09-constellation-64qam.csv | D-IX. I/Q components of each 64QAM symbol | 152 | 157 | 64 |
| d10-constellation-256qam.csv | D-X. I/Q components of each 256QAM symbol | 154–157 | 159–162 | 256 |
| d20-widpn.txt | D-XX. Waveform Number Synchronization preamble | 170 | 175 | 256 values (text layer, verbatim) |
| d21-miniprobes.csv | D-XXI. Mini-probe lengths and base sequences | 171 | 176 | 21 |
| d49-code-rates.csv | D-XLIX. Code Rate and Modulation | 218 | 223 | 12 × 14 WID cols |
| d6x-ber-masks.csv | D-LXIV (doc 241 / PDF 246, by eye) + D-LXV (doc 242 / PDF 247, text layer) | 241–242 | 246–247 | 14 + 18 |
| text-layer-extracts.md | D.5.1.3 / D.5.1.4 scrambler + Walsh example | 160–162 | 165–167 | — |

## Deviations from the scouting brief (role list satisfied)

1. **d07 is 16QAM, not "bpsk-qpsk".** In the actual document the constellation *coordinate*
   tables D-VII…D-X cover 16QAM / 32QAM / 64QAM / 256QAM. BPSK/QPSK/8PSK have no
   coordinate tables — they are defined by transcoding/symbol-mapping tables D-III…D-VI
   (doc 145–146) onto the standard unit-circle 8PSK positions. Files are named for the
   modulation actually covered: `d07-…16qam`, `d08-…32qam`, `d09-…64qam`, `d10-…256qam`.
2. **The code-rate/puncturing role spans TWO tables.** D-XLIX (Code Rate and Modulation)
   is the per-bandwidth × per-WID code-rate map (→ `d49-code-rates.csv`). The puncture
   patterns themselves are a separate Table D-L (doc 222 / PDF 227). Since the deliverable
   filename list is fixed, D-L is transcribed in full below in these notes.
3. **The D.6 performance section has only two tables**: D-LXIV "Data performance
   requirements, All Bandwidths" (SNR masks for BER ≤ 1.0E-5, AWGN + "Poor" channel) and
   D-LXV "Static Channel Tests" (multipath delay profiles). Both are in `d6x-ber-masks.csv`
   with a `table` column; their schemas differ, so the CSV is a union of columns (D-LXIV rows
   leave Bandwidth/delay columns empty; D-LXV rows leave the SNR columns empty).
   D.6.3 "Acquisition performance" is "Not yet standardized." — no table.

## Per-table notes

### D-I (doc 143 / PDF 148)
- Rows for 30/36/42/48 kHz print values with thousands separators ("24,000", "15,300", …);
  transcribed without the comma. All values fit sub-carrier = 300 + BW/2 Hz and
  symbol rate = 0.8 × BW exactly — clean internal consistency.

### D-II (doc 144 / PDF 149, landscape)
- Column headers are waveform numbers 0–13 with modulation names:
  0 Walsh, 1–5 BPSK, 6 QPSK, 7 8PSK, 8 16QAM, 9 32QAM, 10 64QAM, 11 64QAM, 12 256QAM, 13 QPSK.
- WID 13 (QPSK, 2400 bps) exists **only** in the 3 kHz row; cells blank elsewhere
  (transcribed as empty, distinct from the printed "-").
- WID 4 is "-" (not available) at 9 kHz and 18 kHz.
- **Surprising cell, verified at 400 dpi**: 21 kHz WID 0 (Walsh) = **300** bps, breaking the
  otherwise non-decreasing progression (18 kHz and 24 kHz are both 600). Cross-checked
  against D-XLIX: 21 kHz Walsh code rate is 2/7, and 16800 sym/s ÷ 32 chips × 2 bits × 2/7
  = 300 bps exactly — the two tables agree, so 300 is correct as printed.
- Similar cross-check holds for every Walsh cell (rate = symrate/32 × 2 × coderate),
  including 42 kHz = 1200 with the odd 4/7 rate.

### D-VII 16QAM (doc 149 / PDF 154)
- 16 rows. Two PSK rings: outer 12-PSK at radius 1 (±0.866025/±0.5/±0.258819 mixes) and
  inner 4-PSK (±0.258819, ±0.258819). Values match cos/sin(15°,30°,…) to the printed 6 dp.

### D-VIII 32QAM (doc 150 / PDF 155)
- 32 rows, printed as two side-by-side 16-row column groups. Symbols 16–31 are exactly
  symbols 0–15 with Quadrature negated (verified cell-by-cell, not assumed).
- Value alphabet: 0.173415, 0.499386, 0.520246, 0.866380, 0.984849 (outer ring 16 + inner
  square 16, per the accompanying prose).

### D-IX 64QAM (doc 152 / PDF 157)
- 64 rows, two column groups. Symbols 32–63 mirror 0–31 with In-Phase negated **except**
  the axis points: 4 = (0, −1) but 36 = (−1, 0); 0 = (1, 0), 32 = (0, 1). I.e. the four
  unit-circle axis points occupy slots 0/4/32/36 asymmetrically — transcribed as printed.
- Value alphabet: 0.117686, 0.152996, 0.353057, 0.360142, 0.568218, 0.588429, 0.821137,
  0.822878, 0.932897, 1.000000.

### D-X 256QAM (doc 154–157 / PDF 159–162, 4 pages, "TABLE D-X continued")
- 256 rows; each page holds 64 symbols in two column groups
  (PDF 159: 0–63, 160: 64–127, 161: 128–191, 162: 192–255).
- Coordinates lie on the odd lattice (2k+1) × 0.056433 (k = 0…8: 0.056433, 0.169300,
  0.282166, 0.395033, 0.507899, 0.620766, 0.733632, 0.846499, 0.959366), **plus** four
  displaced points at (±0.056433, ±0.998304) — symbols 17, 25, 145, 153 — matching the
  prose note about displaced centre-top/centre-bottom points (D.5.1.2.2.4).
- Symbols 128–255 are exactly symbols 0–127 with In-Phase negated. I verified this by eye
  on the printed pages (including all irregular rows 128–159, 176, 184) rather than assuming.
- Post-transcription integrity checks run on the CSV: 256 distinct points; closed under
  I-negation and Q-negation (4-quadrant symmetry); |I| histogram
  36/36/36/32/32/28/24/20/12 (rounded-corner constellation), |Q| identical except
  8 × 0.959366 + 4 × 0.998304. All consistent with the printed figure D-5.

### D-XX (doc 170 / PDF 175)
- Has a text layer; `d20-widpn.txt` is the verbatim `pdftotext` extraction of the
  `int widPN[256]` block (values 0–7, 8 rows × 32).

### D-XXI (doc 171 / PDF 176)
- 21 rows, every cell read individually; no pattern assumed. For orientation only: the base
  sequence lengths are mostly perfect squares (16, 25, 36, 49, 64, 81, 100, 121, 144, 169,
  196, 256, 289) plus 13 and 19; the cyclic-shift column is approximately base/2 (floored
  for most odd bases: 13→6, 49→24, 121→60 — but 169→85 and 289→145 round up).
  The printed values are authoritative; the CSV is what is printed.
- Lengths 64/68/72 share base 36; 144/160 share 81; 180/192 share 100; 216/224/240 share
  121 — as printed.

### D-XLIX (doc 218 / PDF 223, landscape)
- Same layout/column headers as D-II. WID 13 only at 3 kHz (9/16). WID 4 is "-" at 9 and
  18 kHz (mirrors D-II availability).
- 21 kHz row is the odd one out (2/7, 1/16, 1/8, 1/4, 1/2, then 2/3s, 4/5, 9/10), and 42 kHz
  uses 4/7 Walsh + 5/6 / 3/4 at the top end — verified against the Walsh rate identity above.

### D-LXIV (doc 241 / PDF 246)
- 14 rows (WID 0–13). SNR in dB for BER ≤ 1.0E-5 on AWGN and "Poor" (ITU-R F.1487
  Mid-Latitude-Disturbed, 2-path/2 ms/1 Hz) channels. WID 11 and 12 have "-" for the Poor
  channel (not required). Exceptions column transcribed verbatim ("≤" written as "<=").
- There is a change bar in the left margin at the table title (revision mark) — cosmetic.

### D-LXV (doc 242 / PDF 247)
- Text layer available; 18 rows (6 bandwidths × 3 WIDs). Bandwidth printed as "3 KHz" etc.
  (capital K) — transcribed literally. All paths equal power; delays in ms relative to first path.

## Table D-L. Puncture patterns (doc 222 / PDF 227) — transcribed here because the
## deliverable filename list is fixed (see Deviations #2)

| Code Rate | K=7 Puncture Pattern | K=9 Puncture Pattern | Number of Repeats |
|---|---|---|---|
| 9/10 | 111101110 / 100010001 | 111000101 / 100111010 | n/a |
| 8/9 | 11110100 / 10001011 | 11100000 / 10011111 | n/a |
| 5/6 | 11010 / 10101 | 10110 / 11001 | n/a |
| 4/5 | 1111 / 1000 | 1101 / 1010 | n/a |
| 3/4 | 110 / 101 | 111 / 100 | n/a |
| 2/3 | 11 / 10 | 11 / 10 | n/a |
| 4/7 | 1111 / 0111 | 1111 / 0111 | n/a |
| 9/16 | 111101111 / 111111011 | 111101111 / 111111011 | n/a |
| 1/2 | n/a | n/a | n/a |
| 2/5 | 1110 / 1010 | 1110 / 1010 | 1/2 Repeated 2x |
| 1/3 | n/a | n/a | 2/3 Repeated 2x |
| 2/7 | 1111 / 0111 | 1111 / 0111 | 1/2 Repeated 2x |
| 1/4 | n/a | n/a | 1/2 repeated 2x |
| 1/6 | n/a | n/a | 1/2 repeated 3x |
| 1/8 | n/a | n/a | 1/2 repeated 4x |
| 1/12 | n/a | n/a | 1/2 repeated 6x |
| 1/16 | n/a | n/a | 1/2 repeated 8x |

- Each pattern cell is printed as two stacked rows (first row = G1/upper arm, second = G2);
  rendered here as `upper / lower`.
- The 2/5 and 2/7 "Number of Repeats" cells print "½ Repeated 2x" with a Unicode ½ glyph;
  the 1/4…1/16 rows print "1/2 repeated Nx" in ASCII with lowercase r — transcribed as seen.

## Structurally surprising / worth flagging

- **Appendix D's own table numbering** matches the brief's role list well (D-I, D-II,
  D-XXI, D-XLIX, D-XX, D.6 tables) *except* the constellation-table detail in Deviations #1
  and the D-XLIX/D-L split in Deviations #2.
- D-II and D-XLIX both treat waveform "number" as the *column* axis and bandwidth as the
  *row* axis — i.e. the ladder is (bandwidth × WID) → rate; there is no single-column
  "data-rate ladder" table.
- WID 11 is a second 64QAM column (higher code rate 8/9 vs 3/4) rather than a distinct
  modulation; WID 13 is the special 3 kHz-only QPSK 2400 bps mode with the unique 9/16 rate.
- 15/30 kHz and 21/42 kHz form "half-rate twin" families (same code-rate rows scaled), while
  9/18/36 kHz share the 2/3-Walsh family — visible in both D-II and D-XLIX.
- The image-only span is doc 143–236 (PDF 148–241) with small text-layer islands
  (doc 148, 151, 153, 160–162, 167, 170, 217, 221, 235–237, 239–240, 242–244); everything
  interop-critical that has a text layer was extracted rather than re-typed.
