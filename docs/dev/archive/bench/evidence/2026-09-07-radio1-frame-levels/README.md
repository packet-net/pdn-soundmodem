# radio1, 2026-09-07: per-frame levels at four capture gains

The only real-hardware evidence behind `docs/receive-levels.md`, copied here verbatim from the
bench run rather than cited from a temp path. A Pi with a CM108 and an FM radio, decoding real
GB7RDG `qpsk3600` traffic (15-byte supervisory frames, one 57-byte frame, one 79-byte ident) on
main at `3c455aa`, which is v0.60.0.

- `bench-1-notes.md` - the run's own notes, both blocks, as written on the day.
- `probe-2-23db.txt` - 23 dB capture gain: frames peak 0 dBFS, clipped, TOO LOUD.
- `probe-2-11db.txt` - 11 dB: frames -3.6 to -4.1 dBFS, no badge, the input railing between them.
- `probe-2-0db.txt` - 0 dB: frames -13.7 to -15.1 dBFS, no badge; meter peak -10.8, rms -19.8.
- `probe-2-m12db.txt` - -12 dB: frames -26 to -27.3 dBFS, badged TOO QUIET by v0.60.0's
  thresholds and decoding perfectly, which is the reading that sent the thresholds back to the
  demodulators.

`snrDb` is null on every frame at every gain. That is the existing burst-SNR path and not the
level path; `docs/receive-levels.md` section 8 says why.

These are a record of what the run emitted. Do not edit them to match a later analysis.
