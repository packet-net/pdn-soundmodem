# D.5.1.2.1 PSK data symbols — transcoding prose (bit order rules for the data path)

Source: MIL-STD-188-110D Appendix D, document pages 145-146 (PDF pages
150-151). Image pages (no text layer); transcribed by eye at 300 dpi.

This is the prose the WID-0 Walsh data path leans on: the di-bit order rule for
data (leftmost = oldest = fetched from the interleaver first) is stated here
for QPSK/8PSK transcoding; the D-XIV Walsh di-bit map itself carries no order
statement for data.

## D.5.1.2 intro (end of paragraph carried onto doc p.145, image)

> The waveforms utilizing binary phase-shift keying BPSK, quadrature phase-shift
> keying (QPSK), and eight-ary phase-shift keying (8PSK) constellations are
> scrambled to appear, on-air, as an 8PSK constellation. The scrambling serves a
> secondary purpose of randomizing the on air waveform in the presence of a
> fixed user data stream. The 16QAM and 32QAM constellations use multiple PSK
> rings to maintain good peak-to-average ratios, and the 64QAM constellation is
> a variation of the standard square QAM constellation, which has been modified
> to improve the peak-to-average ratio. The 256 QAM constellation is better than
> the standard 16 x 16 square constellation and achieves superior
> peak-to-average without sacrificing the very good pseudo-Gray code properties
> of the square constellation. An interesting feature of the constellation is
> the slight displacement of the 2 center-top and center-bottom constellation
> points to protect constellation points with larger Hamming distances by
> increasing the signal-space distance.

## D.5.1.2.1 PSK data symbols. (doc p.145)

> For the PSK constellations, a distinction is made between the data bits and
> the symbol number for the purposes of scrambling the BPSK and QPSK
> modulations to appear as 8PSK, on-air. Scrambling is applied as a modulo 8
> addition of a scrambling sequence to the 8PSK symbol number. Transcoding is
> an operation that links a symbol to be transmitted to a group of data bits.

## D.5.1.2.1.1 BPSK symbol mapping. (doc p.145)

> For the waveforms utilizing binary phase-shift keying BPSK, transcoding shall
> be achieved by linking one of the symbols specified in Table D-VI to a single
> data bits (bit) as shown in Table D-III.

("a single data bits (bit)" is as printed.) Table D-III -> `d03-transcoding-bpsk.csv`.

## D.5.1.2.1.2 QPSK symbol mapping. (doc p.145) — the di-bit order rule

> For the waveforms utilizing quadrature phase-shift keying QPSK, transcoding
> shall be achieved by linking one of the symbols specified in Table D-VI to a
> set of two consecutive data bits (dibit) as shown in Table D-IV. In this
> Table, **the leftmost bit of the dibit shall be the older bit; i.e., fetched
> from the interleaver before the rightmost bit.**

Table D-IV -> `d04-transcoding-qpsk.csv`.

## D.5.1.2.1.3 8PSK symbol mapping. (doc p.146)

> For the waveforms utilizing quadrature 8-ary phase-shift keying 8PSK,
> transcoding shall be achieved by linking one symbol to a set of three
> consecutive data bits (tribit) as shown in Table D-V. In this Table, the
> leftmost bit of the tribit shall be the oldest bit; i.e., fetched from the
> interleaver before the other two, and the rightmost bit is the most recent
> bit.

("quadrature 8-ary" is as printed.) Table D-V -> `d05-transcoding-8psk.csv`.

## D.5.1.2.1.4 The 8PSK constellation. (doc p.146)

> The constellation points which shall be used for 8PSK are shown in figure D-1
> and specified in terms of their In-phase and Quadrature components in Table
> D-VI.

Table D-VI -> `d06-8psk-symbol-mapping.csv`.

## Related context: the WID-0 data structure (D.5.2 Frame structure, doc p.163, image)

> Waveform 0 uses a different structure after the Synchronization Preamble, in
> which data "frames" are 32-symbol Walsh sequences (channel symbols), each
> corresponding to a single unknown (data) bit (a di-bit after coding).
> Mini-probes are not sent in waveform 0, so Walsh-coded data symbols are sent
> continuously after the initial Synchronization Preamble.

## Notes

- Nowhere in D.5.1.2.1.x or the doc p.163 paragraph is a di-bit order stated
  for the D-XIV Walsh map when used for **data**; the QPSK "leftmost = older =
  fetched first" rule is the analogue the design adopts (design.md L5/interp.
  (a)) — that remains an interpretation, not spec text.
- 3-kHz data Walsh channel symbols are 32 chips (matches Table D-XIII's 3 kHz
  preamble channel-symbol length and the "32-symbol Walsh sequences" sentence
  above).
