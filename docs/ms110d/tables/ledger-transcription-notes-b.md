# MIL-STD-188-110D App D — §8 ledger transcription, transcriber B

Source: `milstd110d.pdf` (MIL-STD-188-110D, 29 Dec 2017; permanent PDF ID
`DB10F99E7B75A24BD5A10223B8A98086`). Document page = PDF page − 5 throughout.
Pages rendered with poppler `pdftoppm` at 300 dpi; every numeric table
re-verified on a 2x-upscaled crop of the region before recording. Transcribed
independently from the document only (branch `ms110d-ledger-b`); the diff
against branch `ms110d-ledger-a` is the correctness gate.

**Unclear-cell count: 0.** Every cell was legible at 300 dpi or on the 2x
crop; no `[unclear:...]` markers were needed.

**Text layers found (contra expectations):** doc p.167 (PDF 172) — the
D.5.2.1.3.1/.3.2 downcount/WID mapping prose — has a real text layer and was
extracted verbatim with `pdftotext` (quoted in full below). Design checklist
item L8 calls the D.5.2.1.3.x prose "image-only (PDF pp.172-174)"; that is true
only of PDF 173-174 (doc 168-169). All other pages touched here are image-only
(pdftotext yields just the everyspec watermark).

## Ledger row → file map (doc page / PDF page)

| Ledger row (§8) | File(s) | Doc p. (PDF p.) |
|---|---|---|
| D-XI/D-XII U/K frame geometry | `d11-unknown-data-symbols.csv`, `d12-known-miniprobe-symbols.csv` | 164 (169) |
| D-XIII preamble chips/symbol | `d13-preamble-chips-per-symbol.csv` | 165 (170) |
| D-XIV Walsh di-bit map | `d14-walsh-dibit-map.csv` | 166 (171) |
| D-XV/XVI/XVII WID encodings + w1-lsb oddity | `d15-waveform-number-mapping.csv`, `d16-interleaver-selection-mapping.csv`, `d17-constraint-length-mapping.csv` + prose below | 167-169 (172-174) |
| Fixed di-bits; M=1 rule; TLC=conjugate | `fixed-section-dibits-tlc.md` | 165-167 (170-172) |
| D-XVIII fixedPN / D-XIX cntPN | `d18-fixedpn.txt`, `d19-cntpn.txt` | 169 (174) |
| D-XXIII / "D-XXIV" probe bases (see discrepancy) | `d22a-probe-base13.csv`, `d23-probe-base16.csv`, `d24-probe-base19.csv`, `d25-probe-base25.csv` | 172-177 (177-182) |
| Figures D-9/D-10 conv. polynomials | `fig-d09-k7-encoder.md`, `fig-d10-k9-encoder.md` | 219-220 (224-225) |
| D-XXXVII interleaver params (3 kHz) | `d37-interleaver-params-3khz.csv` | 205 (210) |
| D-LI increments (3 kHz) | `d51-increments-3khz.csv` | 223 (228) |
| D.5.3.3.2 worked example | `d5332-worked-example.md` | 222-223 (227-228) |
| D-III…D-VI PSK transcode/phase maps | `d03-transcoding-bpsk.csv`, `d04-transcoding-qpsk.csv`, `d05-transcoding-8psk.csv`, `d06-8psk-symbol-mapping.csv` | 145-146 (150-151) |
| Walsh data-sequence prose (D.5.1.2.1) | `d5121-walsh-data-prose.md` | 145-146 + context 163 (150-151, 168) |

## Discrepancies vs the ledger / design doc (recorded, not resolved)

1. **The ledger's "D-XXIV base-25" is actually Table D-XXV.** In the document,
   D-XXIII = 16-symbol base (32-probe), **D-XXIV = 19-symbol base (36-probe)**,
   and **D-XXV = 25-symbol base (48-probe)**. The design needs bases 13/16/25
   for the 3 kHz K = 24/32/48 probes, so all of D-XXII(a), D-XXIII, D-XXIV and
   D-XXV are transcribed; design.md §8's row (and the "Bases 16/25
   (D-XXIII/D-XXIV)" sentence in §2.4) should be corrected to D-XXIII/D-XXV
   when the ledger row is closed.
2. **Table D-XIII has only 8 rows (3-24 kHz).** No 30/36/42/48 kHz rows exist,
   although those bandwidths exist everywhere else (D-I/D-II/D-XI/D-XII). The
   preamble channel-symbol length is left unspecified by D-XIII for >24 kHz.
   Values follow length = 32·(BW/3) for the printed rows.
3. **w1-lsb conflict (the ledger's "w1-lsb oddity"), verbatim on both sides:**
   D.5.2.1.3.2 (doc 167, text layer) makes d2 a checksum bit
   (`d2 = d9 ^ d8 ^ d7`), while the D-XVII intro sentence (doc 168, image)
   says "The lsb of w1 shall be 0" — and w1 = (d3, d2), so its lsb *is* d2.
   Both statements transcribed; contradiction left open.
4. **Stale cross-reference:** the D.5.3.3.2 worked example cites "Table
   D-XXXIII" for the 3 kHz WN-1 ultrashort parameters; they live in D-XXXVII.
   (Same family as the stale D-LII reference already recorded.)
5. **Fig D-10 page prose:** D.5.3.2.3 (full-tail-biting) says the register
   "should be filled with the last **seven** input data bits" — a K=7 constant
   in a section that otherwise says "(k-1)" and covers K=9 too.
6. **Printed rounding oddities transcribed as printed:** D-VI symbol 6
   In-Phase is "0.0000000" (7 decimals; all other cells 6). D-XXV prints
   0.951056 at symbols 9/12/21 but 0.951057 at 6/13/17/18/24, and 0.309016 (not
   ...17) at symbol 24 — float32-looking artifacts, kept verbatim.
7. D.5.2.1.3's downcount paragraph reads "shall consists" — as printed.

## Verbatim text-layer extract, doc p.167 (PDF 172) — D.5.2.1.3.1/.3.2

> D.5.2.1.3.1 Mapping of the downcount di-bits c3, c2, c1, and c0.
> The 5 bit super-frame down count is initialized to M-1 where M is the number
> of repeats of the super-frame and can be viewed as a binary number b4b3b2b1b0
> where b4 is the MSB and b0 is the LSB. Bits b7, b6, and b5 shall contain a
> parity check computed over b4b3b2b1b0 as follows, where the ^ symbol
> indicates exclusive-or:
>
>     b7 = b1 ^ b2 ^ b3
>     b6 = b2 ^ b3 ^ b4
>     b5 = b0 ^ b1 ^ b2
>
> C3 shall contain the two MSBs b7 and b6, b7 being the MSB. C2 shall contain
> the next two bits of the count b5 and b4, b5 being the MSB. C1 shall contain
> the next two bits of the count b3 and b2, b3 being the MSB. C0 shall contain
> the last two bits of the count b1 and b0, b1 being the MSB.
>
> D.5.2.1.3.2 Mapping of the Waveform ID di-bits w4, w3, w2, w1, and w0.
> The 10 bit waveform ID field consists of 5 di-bits w4, w3, w2, w1, and w0.
> The 10 bits are labeled d9 down to d0. W4 contains d9 and d8, where d9 is the
> MSB, w3 contains d7 and d6, and so on down to w0 which contains d1 and d0.
>
> The 3 LSBs, d2, d1, and d0, shall contain a 3 bit checksum calculated over
> d9d8d7d6d5d4d3 as follows, where the ^ symbol indicates exclusive-or:
>
>     d2 = d9 ^ d8 ^ d7
>     d1 = d7 ^ d6 ^ d5
>     d0 = d5 ^ d4 ^ d3

## Image-page prose around D-XV..D-XVII (doc 168-169, transcribed by eye)

Doc 168, above D-XV:

> The four bit waveform number, defined in Table D-II for all possible waveform
> options for a given bandwidth, shall be mapped into the w4 and w3 di-bits as
> defined in Table D-XV. The reserved encodings shall not be sent.

Between D-XV and D-XVI:

> The Interleaver selection shall be mapped to w2 as defined in Table D-XVI.

Below D-XVI (the w1-lsb sentence):

> The convolutional code constraint length shall be mapped into w1 as defined
> in Table D-XVII. The lsb of w1 shall be 0.

Doc 169, D-XVII's printed title is "Constraint length **and voice mode**
mapping." though the table has only the constraint-length column pair.
D.5.2.1.4 (doc 169, image):

> Expressed as a sequence of 8PSK symbols, using the symbol numbers given in
> Table D-VI the sections of the preamble shall be scrambled as described in
> D.5.2.1.1 using the respective sequences shown in Table D-XVIII through D-XX:

## Per-file notes

- **d11/d12** (doc 164, landscape, crisp): same 12x14 grid and blank/dash
  pattern as D-II/D-XLIX (dashes at 9 kHz WN4 and 18 kHz WN4; WN13 only at
  3 kHz). Printed titles: "TABLE D- XI. Number of Unknown (Data) Symbols in
  Frame" / "TABLE D- XII. Number of Known Symbols (Mini-Probe) in Frame"
  (space after "D-" as printed). Cells I looked at twice: 6 kHz WN11/12 U=540
  (vs 544 for WN5-10); 24 kHz WN3/4 U=816; 36 kHz WN4 U=3072 while WN3=1152;
  42 kHz WN4 U=3456; D-XII 36 kHz WN3=576 vs WN4=384.
- **d13** (doc 165): 8 rows only — see discrepancy 2.
- **d14** (doc 166): 00→0000, 01→0404, **10→0044, 11→0440** — both previously
  provisional rows read clean at high zoom (L5 candidate closed pending A/B
  diff).
- **d15/d16/d17** (doc 168-169): w4w3 is plain 4-bit binary of WN (machine
  check); printed di-bit values have internal spaces ("0 0"), dropped in the
  CSVs. D-XVI: "Ultra Short" as printed (D-XXXVII spells it "UltraShort").
- **d18/d19** (doc 169): monospace C arrays, 8 lines x 32 values each,
  formatted to mirror `d20-widpn.txt`. Re-verified line-by-line on 2x crops.
  **No structural oracle exists for these 512 digits** — the A/B diff is the
  only gate.
- **d22a/d23/d24/d25** (doc 172-177): each table's I/Q decimal formatting
  preserved as printed (D-XXII(a): "1.00000"/"0.0"; D-XXIII..D-XXV: 6 dp).
  D-XXII(b)/(c) (doc 173-174) were read as well and machine-verified against
  the base sequence + shift rule (see self-checks) but, being derived worked
  examples, are not given their own CSVs.
- **fig-d09/fig-d10** (doc 219-220): tap arrows traced on 2x crops; wiring
  matches the printed polynomials arrow-for-arrow (5 taps to b0 / 5 to b1 for
  K=7; 5 to b0 / 7 to b1 for K=9). Delay-convention octal: K=7 = (133, 171),
  K=9 = (561, 753); b0/T1 emitted first (printed prose under both figures).
- **d37** (doc 205, landscape, crisp): "UltraShort" column header as printed.
- **d51** (doc 223): WID0 ultrashort is "-" (no ultrashort interleaver for
  WN0, matching D-XXXVII). WID13 row equals WID6 row.

## Machine self-checks (all PASS)

1. D-XI/D-XII × D-XLIX × bits/symbol reproduce **every** D-II data rate:
   800·BW·U/(U+K)·bps·rate == data rate for all 132 populated (BW, WN≥1)
   cells; blank/dash patterns agree across all four tables.
2. D-XIII: every printed length == 32·BW/3.
3. D-XIV: the four sequences, mapped 0→+1/4→−1, are mutually orthogonal
   (the order-4 Walsh functions).
4. D-XV: w4w3 == 4-bit binary of WN for 0..13; reserved rows are 1110/1111.
5. d18/d19: exactly 256 values each, all in 0..7.
6. D-XXII(a) == Barker-13; D-XXII(b) == base + first 11 symbols; D-XXII(c) ==
   the 24-probe starting 6 into the base (validates the shift rule end-to-end).
7. D-XXIII == Frank-16 (s[4q+r] = exp(−j2π·q·r/4)) exactly at 6 dp.
8. D-XXIV: 19 ±1 values whose periodic autocorrelation is two-valued
   (19 / −1 off-peak) — a perfect binary sequence, as expected for a probe.
9. D-XXV == Frank-25 (s[5q+r] = exp(−j2π·q·r/5)) within printed rounding
   (max deviation 1.11e-06).
10. D-XXXVII: input bits == size × code rate (3 kHz D-XLIX row) for all 55
    populated cells; size == frames × U × bits/symbol (WN1-13, U from D-XI);
    WN0 carries 2 bits/frame; frame counts scale 1:4:16:64 across spans.
11. D-LI: every increment is coprime to its D-XXXVII size (interleaver load is
    a permutation); the WID0-ultrashort dash aligns with D-XXXVII's.
12. Worked example: (n·25) mod 192, n=0..8 → 0,25,50,75,100,125,150,175,8.
13. D-III antipodal (0→0, 1→4); D-IV Gray over even symbols; D-V is a
    bijection onto 0..7 and fully Gray (all adjacent-symbol tribit Hamming
    distances == 1); D-VI symbol n sits at phase n·π/4 on the unit circle.
