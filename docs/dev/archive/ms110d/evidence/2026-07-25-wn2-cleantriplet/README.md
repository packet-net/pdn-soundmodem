# WN2 clean-channel first-pass triplet — mechanism pinned, no fix warranted (2026-07-25)

Closes the e2e-rig relay: on a noiseless channel, WN2's first-pass output showed 3 wrong
hard decisions in 768 (their block geometry) at positions 24/40/46 — the three
least-confident decisions in the block (|LLR| 0.032/0.061/0.414 vs median 1.507) — while
WN0, WN6 and WN13 were exactly 0, and preamble length changed nothing.

## Instrument added

`MS110D_AUTOPSY_CLEAN=1` on the corpse rig: no channel at all — no Watterson fading, no
AWGN, just the lead-in/out padding. Rig-side only; the demodulator is untouched.

## Reproduction and localization

WN2 clean corpse (this rig's geometry: 8 blocks × 12288 BPSK symbols):
**uncoded 5/98304, coded 0** (`corpse/autopsy-summary-wn2-w0-b0-clean.txt`). All five
errors sit in block 0 at symbols 6, 15, 30, 37 (frame 0) and 94 (frame 1, position 46) —
entirely inside the first two data frames of the burst (`corpse/autopsy-biterrs-…`).
Their total wrong-|LLR| mass is 0.5 across five bits (mean 0.1) against a right-mass
mean of 3.56 (`corpse/llrstats-wn2-clean-head.csv`) — near-erasures, exactly as the
e2e rig saw. The turbo final pass and the Viterbi both absorb them: coded errors 0.

The equalized constellation tells the real story (`corpse/warmup-profiles.txt`): on a
NOISELESS channel WN2's per-frame mean |y| starts at **0.23** and ramps to ~1.0 over
~15–20 frames (~600–800 ms), with ISI scatter comparable to the signal in frames 0–1.
Waveform split: WN1 (also U/K = 48/48) shows the same ramp and 3 uncoded errors of its
own; WN3 (96/32) and WN6 (256/32) open at |y| = 0.998, frame 0, zero errors. The
artifact belongs to the K=48 geometry, not to BPSK and not to acquisition.

## Mechanism

`Ms110dDemodulator.cs` K-switch (the `48 => (32, 22, 16, 1.0f, 8.0f)` row): K=48
waveforms run the anchored-coasting equalizer policy — initRidge 1.0 and trackRidge 8.0,
versus 1e-3/0.15 for every other waveform. That ridge is the MEASURED Poor-channel
optimum (the WN2 +5 dB sweep 0.5/1/2/4/8/16 → 43/42/20/5/1/23 coded errors, documented
at the switch): the over-regularized solve deliberately shrinks the FF taps so the
equalizer coasts on its anchor instead of chasing fades, and errors self-report low
confidence. At burst start the same shrinkage means under-gain PLUS uncancelled
pulse-shaping ISI until the per-probe anchored batch solve accumulates — the observed
0.23 → 1.0 ramp, and a handful of near-zero-LLR wrong hard decisions while the scatter
still reaches the BPSK boundary.

Confirmation with the existing knob (`corpse/autopsy-summary-wn2-clean-trackridge015.txt`):
`MS110D_AUTOPSY_TRACK_RIDGE=0.15` (the non-K48 value) collapses the ramp to ~4 frames
(0.51/0.79/0.89/0.97…) and **uncoded errors go to 0**. The residual frame-0 deficit
(0.51) is the initRidge 1.0 contribution decaying. Preamble length is irrelevant because
the init solve trains only on the last half super-frame + probe regardless of count —
consistent with the e2e rig's 20-superframe null result.

## Verdict: no fix

The triplet is the designed, bounded cost of the ridge that bought WN2 its mask. The
clean-channel cost is ~5 near-erasure uncoded bits per burst, coded cost ZERO; the
Poor-channel benefit of ridge 8 over 0.15 is the difference between at-mask and ~43
coded errors per smoke. Softening the ridge (globally, or SNR-adaptively at burst
start) would perturb WN2 first-pass trajectories — the lever class measured
net-negative twice at battery scale (basin #82/#84 legs) — to polish a number the coded
output already absorbs. The observation is answered, the instrument ships for future
clean-channel work, and the demodulator is untouched.
