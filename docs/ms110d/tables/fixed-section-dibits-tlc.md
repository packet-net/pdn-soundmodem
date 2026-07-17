# Preamble Fixed-section di-bits, M=1 rule, and TLC = conjugate rule

Source: MIL-STD-188-110D Appendix D, D.5.2.1 - D.5.2.1.3, document pages
165-167 (PDF pages 170-172). Doc pp.165-166 are image pages (no text layer),
transcribed by eye at 300 dpi; the two mapping subsections on doc p.167
(D.5.2.1.3.1/.3.2) have a real text layer and were extracted verbatim.

## D.5.2.1 Synchronization preamble. (doc p.165, image)

> The synchronization preamble is used for rapid initial synchronization and
> provides time and frequency alignment. The synchronization preamble shall
> consist of two main sections, a transmitter level control (TLC) settling time
> section, and a synchronization section containing a repeated preamble
> super-frame. The preamble super-frame consists of three distinct subsections,
> one with a fixed (known) modulation, one to convey a downcount, and one to
> convey waveform identification. The superframe shall be repeated M times. The
> Synchronization section shall be immediately followed by the modulated data
> (Figure D-8).

Figure D-8 ("Synchronization preamble structure."): a TLC section, then the
Synchronization section = M repeats of a super-frame, then Data Modulation.
Each super-frame = [ Fixed | 4 Downcount symbols c3, c2, c1, c0 | 5 WID
symbols w4, w3, w2, w1, w0 ].

## D.5.2.1.1 4-ary Orthogonal Walsh Modulation in the preamble. (doc pp.165-166, image)

> 4-ary orthogonal Walsh modulation shall be used in the synchronization
> section of the preamble. The length of each channel symbol, in chips or
> symbols, is dependent on the bandwidth of the modem waveform selected and
> shall be as given by Table D-XIII.
>
> The 4-ary orthogonal Walsh modulation shall use the Walsh sequences in Table
> D-XIV. The di-bit representing the 2 bits of information to convey is mapped
> into a 4 element Walsh sequence of 0 and 4 as defined in Table D-XIV. This
> 4-element sequence shall be repeated to satisfy the Walsh sequence length
> requirement in Table D-XIII for the bandwidth in use.
>
> Scrambling shall be performed by aligning the expanded Walsh sequence with
> the Fixed, Count, or the Waveform ID synchronization preambles defined in
> Tables D-XVIII, D-XIX, D-XX, respectively, and performing a modulo 8 addition
> between the specified 8-PSK symbol from the table and the corresponding Walsh
> element.

## D.5.2.1.2 TLC Section. (doc p.166, image) — TLC = conjugate of Fixed

> The first section of the preamble, denoted TLC, is provided exclusively for
> radio and modem TGC and AGC. It shall consist of N blocks of 8-PSK. The
> length of each block in PSK symbols, based on the bandwidth used in the
> transmission to follow, shall be as shown in Table D-XIII. The value of N
> shall be configurable to range from 0 to 255 (for N=0 this first section is
> not transmitted). These symbols shall be formed by taking the **complex
> conjugate of the symbols** of the sequence specified below for the Fixed
> section in Table D-XVIII.

## D.5.2.1.3 Synchronization Section. (doc p.166, image) — Fixed di-bits + M=1 rule

> The Fixed subsection of the super-frame shall consist of either 1 or 9
> orthogonal Walsh modulated channel symbols. The length of each channel
> symbol, dependent on bandwidth, is given in Table D-XIII. For the case of the
> single Walsh symbol **the di-bit shall be 3 (binary 11)**, and the super frame
> shall be transmitted **only once (M=1)**. For the case of 9 Walsh symbols the
> di-bits shall be **{0, 0, 2, 1, 2, 1, 0, 2, 3}**, 3 being the last di-bit
> transmitted. The Fixed subsection is intended exclusively for synchronization
> and Doppler offset removal purposes.
>
> The next subsection shall consists of four, orthogonal Walsh modulated
> di-bits, labeled as c3, c2, c1 and c0, each conveying two bits of
> information. This subsection represents a 5 bit downcount plus 3 parity bits.
> This count shall be initialized to a value of (M-1) and shall be decremented
> with each of the M preamble repetitions until it reaches zero in the final
> super-frame before data begins.
>
> The final subsection of the preamble super-frame shall consist of five,
> orthogonal Walsh modulated channel symbols, each conveying two bits of
> information. These di-bits are labeled as w4, w3, w2, w1, and w0. These 10
> bits represent a Waveform ID consisting of waveform number, interleaver
> option, convolutional code length and parity check.

("shall consists" in the downcount paragraph is as printed.)

## Notes

- Fixed-section di-bit sequence (9-symbol case): `0, 0, 2, 1, 2, 1, 0, 2, 3`
  — 3 transmitted last. Single-symbol case: di-bit `3`, and then M=1.
- TLC symbols = complex conjugate of the Fixed-section D-XVIII sequence
  (`d18-fixedpn.txt`), N blocks, N configurable 0-255, block length =
  Table D-XIII entry (`d13-preamble-chips-per-symbol.csv`).
- The downcount/WID di-bit bit-mappings (parity equations) are D.5.2.1.3.1 and
  D.5.2.1.3.2 on doc p.167 — that page has a text layer; the verbatim extract
  lives with the D-XV..D-XVII notes in `ledger-transcription-notes-b.md`.
