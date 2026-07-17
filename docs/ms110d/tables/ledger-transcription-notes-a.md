# MIL-STD-188-110D App D ledger transcription — notes (independent transcriber A)

Source: `milstd110d.pdf` (MIL-STD-188-110D, 270 physical/PDF pages; document
page = PDF page - 5). All values below were read by eye from page images
rendered at 200 dpi, with 300 dpi crops used for the PN arrays, the encoder
figures, and doubtful cells. This is the "A" leg of the dual-transcription
discipline; diff against branch `ms110d-ledger-b` before closing ledger rows.

**Unclear-cell count: 0.** Every cell was legible at 200 dpi or in the 300 dpi
crop; no `[unclear:...]` markers were needed.

## Files <-> ledger rows <-> pages

| File | Table / item | Doc page | PDF page |
|------|--------------|----------|----------|
| d03-transcoding-bpsk.csv | Table D-III | 145 | 150 |
| d04-transcoding-qpsk.csv | Table D-IV | 146 | 151 |
| d05-transcoding-8psk.csv | Table D-V | 146 | 151 |
| d06-8psk-symbol-mapping.csv | Table D-VI | 146 | 151 |
| walsh-data-sequence-prose.md | WID 0 Walsh data prose (D.5.2) | 163 | 168 |
| d11-unknown-data-symbols.csv | Table D-XI (full, all 12 bandwidths) | 164 | 169 |
| d12-known-miniprobe-symbols.csv | Table D-XII (full, all 12 bandwidths) | 164 | 169 |
| d13-preamble-channel-symbol-length.csv | Table D-XIII | 165 | 170 |
| d14-walsh-sync-sequences.csv | Table D-XIV | 166 | 171 |
| preamble-fixed-tlc-prose.md | D.5.2.1–D.5.2.1.3.2 prose (Fixed di-bits, M=1, TLC=conjugate, count/WID mapping) | 165–167 | 170–172 |
| d15-waveform-number-mapping.csv | Table D-XV | 168 | 173 |
| d16-interleaver-selection-mapping.csv | Table D-XVI | 168 | 173 |
| d17-constraint-length-voice-mode-mapping.csv | Table D-XVII | 169 | 174 |
| d18-fixedpn.txt | Table D-XVIII fixedPN[256] | 169 | 174 |
| d19-cntpn.txt | Table D-XIX cntPN[256] | 169 | 174 |
| d22a-base13-miniprobe.csv | Table D-XXII (a) (Barker-13) | 172 | 177 |
| d23-base16-miniprobe.csv | Table D-XXIII (16-symbol base) | 175 | 180 |
| d24-base19-miniprobe.csv | Table D-XXIV (19-symbol base) | 176 | 181 |
| d25-base25-miniprobe.csv | Table D-XXV (25-symbol base) | 177 | 182 |
| d37-interleaver-3khz.csv | Table D-XXXVII (3 kHz, full 14x4x3) | 205 | 210 |
| fig-d09-k7-encoder.md | Figure D-9 + D.5.3.2.1 polynomials/wiring | 219 | 224 |
| fig-d10-k9-encoder.md | Figure D-10 + D.5.3.2.2 polynomials/wiring | 220 | 225 |
| d51-interleaver-increments-3khz.csv | Table D-LI (3 kHz increments) | 223 | 228 |
| d5332-interleaver-load-example.md | D.5.3.3.2 worked example | 223 | 228 |

Cell counts: d03 2x2=4; d04 4x2=8; d05 8x2=16; d06 8x4=32; d11 12x14=168;
d12 12x14=168; d13 8x2=16; d14 4x2=8; d15 16x3=48; d16 4x2=8; d17 2x2=4;
d18 256; d19 256; d22a 13x3=39; d23 16x3=48; d24 19x3=57; d25 25x3=75;
d37 14x12=168 (+3 dashes); d51 14x4=56 (+1 dash). Total ~1,180 data cells
plus the two figures and three prose files.

## Ledger discrepancies (what the document actually has)

- **Probe base sequences**: the ledger row says "D-XXIII base-16 / D-XXIV
  base-25". In the document, D-XXIII is the **16**-symbol base (doc 175),
  D-XXIV is the **19**-symbol base (doc 176), and the **25**-symbol base is
  **D-XXV** (doc 177). All three are transcribed (plus D-XXII(a) Barker-13,
  which the ledger row mentioned as single-read). For 3 kHz the relevant
  mini-probes are 24 symbols (base 13, shift 6) and 32 symbols (base 16,
  shift 8) per Table D-XXI.
- **Walsh data-sequence prose**: the ledger locates it at "D.5.1.2.1, doc
  145-146", but D.5.1.2.1 there is "PSK data symbols" (no Walsh content).
  The WID 0 Walsh data-path prose is the last paragraph of **D.5.2 Frame
  structure, doc 163** — transcribed in walsh-data-sequence-prose.md.
- **D-XIV 10/11 rows** (ledger had 10<->11 provisional): document reads
  10 -> 0044 and 11 -> 0440 (00 -> 0000, 01 -> 0404).

## Document oddities transcribed as printed

- Table D-VI, symbol 6 In-Phase is printed "0.0000000" (seven decimals,
  vs "0.000000" elsewhere). Transcribed verbatim.
- Table D-XIII only has rows for bandwidths 3–24 kHz (8 rows); no rows for
  30–48 kHz are printed.
- Table D-XI, 6 kHz row: WN11/WN12 read **540** (not 544 like WN5–10).
- w1-lsb oddity (ledger row D-XV/XVI/XVII), doc 168 verbatim: "The
  convolutional code constraint length shall be mapped into w1 as defined in
  Table D-XVII.  The lsb of w1 shall be 0." So w1 = (d3 d2) with d2 = 0 from
  the mapping, while d2 also participates in the 3-bit checksum d2 = d9^d8^d7
  (D.5.2.1.3.2, doc 167) — both statements transcribed as printed.
- Table D-XXV trailing-digit quirks, verified at 300 dpi and kept verbatim:
  rows 9/12/21 Quadrature = 0.951056 but row 18 = 0.951057; rows 6/13/17/24
  Quadrature = -0.951057; row 24 In-Phase = **0.309016** (all other 0.309017).
- D.5.3.3.2 worked example cites "Table D-XXXIII" in the document, which is a
  mini-probe base-sequence table; from context the intended reference is the
  3 kHz interleaver tables (D-XXXVII/D-LI). Transcribed as printed with a note.
- Figure D-9 polynomials print as T1 = x^6+x^4+x^3+x^1+1 (0o133),
  T2 = x^6+x^5+x^4+x^3+1 (0o171); Figure D-10 as T1 = x^8+x^6+x^5+x^4+1
  (0o561), T2 = x^8+x^7+x^6+x^5+x^3+x^1+1 (0o753). Tap wiring in both figures
  matches the printed polynomials arrow-for-arrow; b0 (T1) is taken first.
  (The ledger's "0o561/0o753" note applies to the K=9 figure only.)

## Rendering method

Whole pages at 200 dpi (`pdftoppm -r 200`); 300 dpi region crops
(`pdftoppm -r 300 -x/-y/-W/-H`) for D-XVIII/D-XIX, Figures D-9/D-10 wiring,
D-XXV value columns, and the D-XIII table tail. One careful read per table,
written to file immediately after reading.
