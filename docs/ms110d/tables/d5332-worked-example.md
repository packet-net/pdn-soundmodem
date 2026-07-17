# D.5.3.3.2 Interleaver load — formula and worked example

Source: MIL-STD-188-110D Appendix D, document pages 222-223 (PDF pages
227-228). Image pages (no text layer); transcribed by eye at 300 dpi.

## D.5.3.3.2 Interleaver load. (verbatim, doc pp.222-223)

> The punctured block code bits shall be loaded into the interleaver array
> beginning with location 0. The location for loading each successive bit shall
> be obtained from the previous location by incrementing by the "Interleaver
> Increment Value" specified in Tables D-LI through D-LXII, modulo the
> "Interleaver Size in Bits."
>
> Defining the first punctured block code bit to be B(0), then the load location
> for B(n) is given by:
>
> Load Location = ( n * Interleaver Increment Value) Modulo (Interleaver Size in Bits)
>
> Thus for Waveform 1 in Table D-XXXIII, using ultrashort interleaver (192 bit
> size with an increment of 25), the first 9 interleaver load locations are:
> 0, 25, 50, 75, 100, 125, 150, 175 and 8.

## Notes

- The worked-example sequence is `0, 25, 50, 75, 100, 125, 150, 175, 8`
  (9 values; 8 = (8 x 25) mod 192 = 200 mod 192). Machine-checked.
- Spec oddity, recorded not resolved: the prose cites "Table D-XXXIII" for
  Waveform 1's ultrashort parameters, but the 3 kHz interleaver parameters
  (192-bit ultrashort size for WN 1) actually live in Table D-XXXVII
  (`d37-interleaver-params-3khz.csv`); the 192/25 values themselves are
  consistent with D-XXXVII + D-LI (`d51-increments-3khz.csv`).
- Preceding context on doc p.222 (same image page, for completeness):
  D.5.3.3 "Block interleaver structure." ("The block interleaver used is
  designed to separate neighboring bits in the punctured block code as far as
  possible over the span of the interleaver with the largest separations
  resulting for the bits that were originally closest to each other.") and
  D.5.3.3.1 "Interleaver size in bits." ("The interleaver shall consist of a
  single dimension array, numbered from 0 to its size in bits -1. The array
  size shall depend on both the data rate and interleaver length selected as
  shown in the Tables D-XXXVII through D-XLVIII, one for each bandwidth.")
