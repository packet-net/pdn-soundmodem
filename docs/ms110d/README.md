# MIL-STD-188-110D Appendix D — transcribed interop tables

The interop-critical tables of MIL-STD-188-110D Appendix D (WBHF — the public counterpart of
the RESTRICTED STANAG 5069), transcribed from the standard because they are embedded as
**images** in the PDF (no text layer). These values are load-bearing for any App D
implementation: a single misread cell breaks interop silently.

## Source

- MIL-STD-188-110D, 29 December 2017, Distribution Statement A (approved for public release).
- Downloaded 2026-07-16 from everyspec.com (MIL-STD-188-110D_55856).
- SHA-256 of the transcription-time download: `c12ec2f6a4b9daf79e4bdcea1b618ba0c745f07ec24633c048702d0fe9847bc0`.
  **Caveat (2026-07-16, design assembly):** this raw hash is *per-download* — everyspec's
  `download.php` stamps a fresh random second half into the PDF trailer `/ID` on every
  download (verified by byte-diffing two downloads: 12,884,037 bytes each, exactly 30
  differing bytes, all inside the second `/ID` hex string). Canonical identity instead:
  permanent PDF ID `DB10F99E7B75A24BD5A10223B8A98086` + stamp-invariant SHA-256 (second
  `/ID` zeroed): `6e177fa6c2a6985189160f00c8ad0e809e27872f3ba3d10d9426c66292eddf3d`.
- Page mapping: document page = PDF page − 5 throughout Appendix D.

## Verification: two independent transcriptions, diffed

Transcribed **twice, independently** (branches `ms110d-tables-a` and `ms110d-tables-b`,
2026-07-16), by agents forbidden from consulting each other or external sources for values.
Diff verdict across all ten table files: **six byte-identical** (including all four
constellation tables and the mini-probes), **four differing only in header/comment formatting —
zero value conflicts** (confirmed by normalized field-level comparison for the one
structurally-different file, d6x). The `ms110d-tables-a` branch is retained as the
independent record; this directory carries the B transcription as canonical.

Both transcribers additionally self-checked beyond eyeballing: constellation mirror/lattice
symmetries machine-verified (256QAM: 256 distinct points, 4-quadrant symmetry, max magnitude
exactly 1.0); every Table D-L puncture mask's ones-count reproduces its D-XLIX code rate; the
printed WID-0 scrambler init + generator regenerate the printed first-32 sequence exactly; the
Walsh worked example checks mod 8; the odd D-II cell (21 kHz Walsh = 300 bps, breaking the
doubling pattern) cross-validates against D-XLIX's 2/7 rate.

## Structural notes (vs the scouting expectations)

- The coordinate tables D-VII…D-X cover **16/32/64/256-QAM**; BPSK/QPSK/8PSK have no
  coordinate tables — they are unit-circle PSK with transcoding tables (D-III…D-VI).
- The puncture patterns are a separate **Table D-L** (transcribed in full inside
  `tables/transcription-notes.md`).
- Spec oddities recorded in the notes: a length-68 mini-probe; a "40 kHz"
  interleaver-increment table with no 40 kHz bandwidth anywhere in D-I/D-II/D-XLIX;
  D.6.3 acquisition performance literally "Not yet standardized."; a stale D-LII
  cross-reference in the D.6 prose.

These tables feed the App D design doc (task #7); the build phases per the verified scoping:
Phase A = 3 kHz framing + Walsh-75/BPSK/QPSK + basic DFE; Phase B = 16-QAM+ (gated on a
validation oracle — no open App D implementation exists).
