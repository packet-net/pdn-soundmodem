# WID 0 Walsh data-sequence prose (doc page 163, PDF page 168 — section D.5.2)

Transcribed by eye from the page image (independent transcriber A).

Ledger note: the ledger row calls this "Walsh data-sequence prose (D.5.1.2.1),
doc 145-146". In the document, D.5.1.2.1 (doc 145) is "PSK data symbols"
(BPSK/QPSK/8PSK transcoding — see d03/d04/d05 CSVs) and contains no Walsh
material. The prose that actually defines the WID 0 Walsh data path is the
final paragraph of **D.5.2 Frame structure** on doc page 163, quoted below,
together with Table D-XIV (di-bit -> Walsh sequence, doc 166) and the
D.5.1.4 WID 0 scrambling section (text-layer-anchored, already extracted).

## D.5.2 Frame structure — Waveform 0 paragraph (doc 163, verbatim)

> Waveform 0 uses a different structure after the Synchronization Preamble, in
> which data "frames" are 32-symbol Walsh sequences (channel symbols), each
> corresponding to a single unknown (data) bit (a di-bit after coding).
> Mini-probes are not sent in waveform 0, so Walsh-coded data symbols are sent
> continuously after the initial Synchronization Preamble.

Related sentences from the same section (doc 163):

> The frame structure that shall be used for the waveforms specified in this
> appendix is shown in Figure D-7. An initial synchronization preamble is
> followed by frames of alternating data (Unknown) and probe (Known) symbols.
> Each data frame shall consist of a data block consisting of U data symbols,
> followed by a mini-probe consisting of K known symbols.
>
> Tables D-XI and D-XII provide the Unknown and Known frame structure for
> waveforms 1 through 12.

## How the WID 0 data path assembles (cross-references, for the reader)

- Coded data di-bit -> 4-element Walsh sequence of 0s and 4s per Table D-XIV
  (00 -> 0000, 01 -> 0404, 10 -> 0044, 11 -> 0440), repeated 8 times to fill a
  32-symbol channel symbol (see D.5.1.4 example: di-bit 01 -> "0, 4, 0, 4"
  repeated across 32 chips).
- Each 32-chip channel symbol is scrambled modulo 8 with 32 chips of the
  Trinomial (159, 31) scramble sequence defined in D.5.1.4 (text-anchored
  extract already in tables/).
