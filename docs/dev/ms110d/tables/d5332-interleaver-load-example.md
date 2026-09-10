# D.5.3.3.2 interleaver load worked example (doc page 223, PDF page 228)

Transcribed by eye from the page image (independent transcriber A).

Context prose (top of doc page 223): the load location advances by
incrementing by the "Interleaver Increment Value" specified in Tables D-LI
through D-LXII, modulo the "Interleaver Size in Bits."

Defining the first punctured block code bit to be B(0), then the load location
for B(n) is given by:

    Load Location = ( n * Interleaver Increment Value) Modulo (Interleaver Size in Bits)

Worked example, as printed:

> Thus for Waveform 1 in Table D-XXXIII, using ultrashort interleaver (192 bit
> size with an increment of 25), the first 9 interleaver load locations are:
> 0, 25, 50, 75, 100, 125, 150, 175 and 8.

Note: the document really prints "Table D-XXXIII" here, which is a mini-probe
base-sequence table; from context (WID 1, 3 kHz, ultrashort, 192 bits,
increment 25) the intended reference is the 3 kHz interleaver parameter /
increment tables (D-XXXVII / D-LI). Transcribed as printed.

Self-check: (8 * 25) mod 192 = 200 mod 192 = 8, consistent with the printed
ninth location.
