# Letting go of a lock with no signal under it (issue #553)

Status: design and evidence note, 2026-10-05, revised 2026-10-06 after the review of PR #556 (a 16 s window instead of 4 s, and the median floor). Describes the signal-absent release in `Ms110dDemodulator` (the `SignalAbsent` burst end), why it is built the way it is, and what was measured before it shipped.

## The fault

pdn-mailcast found it (its PR #22): when the receiver acquires the preamble of a burst too weak to read, about -3 to -12 dB SNR in 3 kHz, it can stay locked for good. With no EOM it goes on demodulating the noise that follows, at about 8 times the idle receive CPU, and is deaf to every later burst until something resets it.

The receiver already had a signal-lost exit: four seconds of consecutive mini-probes whose equalized correlation sits below `max(0.10, 0.45 x reference)`. Two things defeat it on a weak burst. The reference is the burst's own healthy level, and on a weak burst that level is little above what noise gives. And the equalizer is trained on every probe, so on noise it fits the noise, and the next probe's equalized correlation keeps landing above 0.10 often enough to reset the four-second count. Reproduced on main at the native rate (WN4 at -6 dB, AWGN): still locked after 180 s of noise, on every seed tried, and the same for WN3, WN5 to WN8 and WN13 at -9 dB. WN1 and WN2 (whose K = 48 probe solve is heavily regularized) and WN0 (no probes; its own discriminator has an absolute, noise-calibrated line) let go by themselves.

## The decision

Each frame's mini-probe is matched-filtered against the raw received symbols, not the equalizer's output. For each lag d in a window of half a probe base period either side of the cursor (15 lags for K = 32, 25 for K = 48), the K received symbols from the probe's start plus d are correlated with the known probe, giving c_d. Each |c_d|^2 is divided by K times the mean received power over the span, so on noise each lag e_d is a unit-mean exponential whatever the level. The frame's statistic is

    presence = sum over d of e_d - lags x median(e_d) / E[median]

where E[median] is the expected middle value of that many unit exponentials (0.7254 for 15 lags, 0.7084 for 25). On noise it averages zero. A signal at symbol SNR s adds about K s / (1 + s), times the share of its channel energy inside the lag window, in the few lags its paths occupy, and leaves the median where the noise put it. The receiver averages the statistic over the frames of the last 16 s (134 frames at U = 256, 300 at U = 96, 400 at U = 48) and lets go when that average falls below

    line = 4 x sqrt(lags / frames)

four standard deviations of a noise-only window mean, taking each lag as an independent unit exponential (measured on noise: a per-frame standard deviation of 3.7 for 15 lags and 5.2 for 25, against 3.9 and 5.0). The release ends the burst with `Ms110dBurstEndReason.SignalAbsent`, counts it in `Ms110dDemodulator.SignalAbsentReleases`, drops `CarrierDetect`, and on `Ms110dModem` raises `LockReleased` once, with one plain-ASCII line, and counts it in `LocksReleased`. The daemon journals that line against its modem, and the mailcast receiver journals and counts it.

Why this and not the alternatives:

- **It is the equalizer that is fooled, so the detector must not go through it.** The statistic reads the ring with the receiver's carrier and timing and nothing else: no taps, no reference level carried from the burst's start, no AGC (the ratio is scale-free). Noise is scored as noise however the DFE has adapted to it.
- **A fixed line works because the scale is known.** Normalizing by the received power puts noise at zero with a variance set by the lag count alone, so the line has the same meaning at every receive level. The old discriminator could not have a fixed line because its scale is the equalizer output's.
- **The floor is for carriers.** A steady carrier on one of the probe's spectral lines (1800 Hz plus a multiple of 2400 / base length: 150 Hz steps for K = 32, 96 Hz for K = 48) correlates with the probe equally at every lag. With the plain sum it scored like a signal and held the lock for ever, even 6 dB under the noise: #553 again, from a CW ident or any carrier on the band. A carrier fills the median along with everything else, so the floored statistic puts it below zero (measured -0.6 to -11.5 against plain sums of 2.6 to 18.6, every mode's line being under 1.4).
- **The window is long because slow fades are the real case.** GB7RDG's path is 40 m NVIS, where a fade can take the probes away for many seconds inside a burst that is still worth reading, and the receiver before #553 rode those fades, if for the wrong reason. A lost burst costs more than a dead lock held a few seconds longer, so the window was chosen from evidence, below, as the shortest with a clear margin at which no frame the old receiver read is lost. A 4 s window, the old signal-lost patience, lost 855 frames of 78,949 in that evidence.
- **A cap on lock time is not sound for App D.** App D has no block CRC, so "no data block decoded" is not something the demodulator can observe: the Viterbi decoder always emits bits, and the IL2P CRC sits a layer above. A cap on lock time without decodes therefore becomes a cap on burst length, which either cuts real long bursts (packing allows bursts of any length) or, set long enough to be safe, leaves the lock in place for minutes. The probe test needs neither.
- **One frame late, on purpose.** Each frame scores the probe before its own data block, never its own trailing probe, because the last lags of that probe may not have arrived when the frame is processed. Every sample the statistic reads is then resident whatever block size the caller feeds, and the release frame does not depend on how the audio is cut up (pinned by a test at 37, 960, 4801 samples and the whole stream).

Nothing in the statistic writes receiver state, so until it fires the receiver is bit-identical to one without it.

**The bound.** A burst that ends is let go once the window holds mostly noise: at most one window plus one frame after its last probe, plus the frame it takes to read the next one. That is 16.25 s after the burst's last sample at U = 256 and a little less for the shorter frames. A weak burst whose presence is already below the line can be let go before it ends, which costs nothing, since it cannot be read.

## Measured

Native 9600 Hz unless stated, SNR in 3 kHz, every run seeded.

**Choosing the window and the line.** `Ms110dSignalAbsentTuning` ran 2,127 cases with the release switched off, so the receiver behaved exactly as it did before #553, and traced the statistic and every frame read; `evidence/2026-10-06-signal-absent-tuning/evaluate.py` then scores any window and line exactly, offline, since the statistic changes nothing until it fires. The fading cases are 60 s packed bursts at every waveform number: one Rayleigh path at 0.02, 0.05, 0.1 and 0.2 Hz at the AWGN mask SNR plus 0, 5 and 10 dB (five seeds; and five fresh validation seeds at 0.02 and 0.05 Hz, held back until the choice was made), scripted fades 15 and 30 dB deep held 4 to 8 s at the AWGN mask SNR minus 3, plus 0, 3 and 6 dB, and held 10 to 18 s at minus 3 to plus 3 dB, and the AWGN and Poor mask points; 1,803 cases, 78,949 frames read by the old receiver. The rule was: no frame the old receiver read may be lost.

| window, line | frames lost | cases losing frames | release after a weak burst (median / max) |
|---|---|---|---|
| 4 s, plain sum, 4 sigma (the first version of this PR) | 855 | 49 | 2.2 / 3.5 s |
| 8 s, floored, 4 sigma | 97 | 6 | 5.9 / 7.6 s |
| 12 s, floored, 4 sigma | 0 | 0 | 9.2 / 11.6 s |
| 16 s, floored, 4 sigma (shipped) | 0 | 0 | 13.1 / 15.5 s |

12 s already loses nothing, but only just: its lowest window that still had a frame to come was 4.4 noise sigmas, a hair over the line, on a 0.05 Hz Rayleigh WN4 burst at +5 dB. At 16 s the lowest such window is 8.4 sigmas, twice the line, on an 18 s fade 15 dB deep at WN4 +5 dB; the hardest 0.02 Hz validation seed is at 11.9. The reviewer's two cases (WN4, 15 dB held 4 to 6 s at +3 dB; 0.05 Hz Rayleigh at +6 dB, seed 2) read 29 and 12 frames, as the old receiver did, and are CI tests now.

**Noise only.** Per frame the floored statistic averaged -0.05 with a standard deviation of 3.7 for 15 lags (64,170 noise frames), and -0.24 and 5.2 for 25 lags; independent lags would give 3.9 and 5.0. The lines: 1.34 at U = 256 (WN5 to WN8, WN13), 0.89 at U = 96 (WN3, WN4), 1.00 at U = 48 (WN1, WN2).

**Real bursts never let go.** The full mask battery in the G0 form (Poor on both seed families, AWGN, the static WID 2 rig and the Doppler checks; 32 points, 4 workers each) is byte-identical to the 2026-08-21 G4 battery: 120 of 120 per-burst censuses, end reasons included, and every mask line's bits and errors. A release anywhere in it would have changed a census. How far clear of the line: over four 90 s bursts at every AWGN and Poor mask point (Long interleaver, 20 super-frames, the masks' own form), the lowest window of any burst was 16.7 times the line (WN1 AWGN -3 dB) and 18.5 to 27.7 times it everywhere else, Poor included. CI pins packed bursts of 113 to 120 s (WN4 Poor +10 dB, WN6 Poor +14 dB, WN1 AWGN -3 dB) to their EOM, and seven slow-fade cases to the old receiver's frame count.

**Weak bursts let go.** 2,520 bursts: WN1 to WN8 and WN13; AWGN at -3, -6, -9 and -12 dB and Poor at -3, -6 and -9 dB; 40 seeds each; 4 s of data behind the catalogue's 3 super-frame preamble, then 30 s of noise. None was still locked at the end. 294 were never acquired and 431 had their preamble heard but failed the WID check, so never locked. Of the 1,795 that locked, 217 were read to their EOM, 264 were let go by the old signal-lost exit (every WN1 and WN2 one, within 4 s, and some strong-reference K = 32 ones) and 1,314 by the new one. Time from the burst's last sample to the release: at most 15.48 s, against the 16.25 s bound. A burst whose window never held enough signal goes at the first full window, 11.7 s after its end here (16 s after its data began); one that was clearly present goes after 12 to 15.5 s.

| WN | AWGN -3 | AWGN -6 | AWGN -9 | Poor -3 | Poor -6 | Poor -9 |
|---|---|---|---|---|---|---|
| 1, 2 | all EOM | 3.86 / 3.96 | 3.56 / 3.96 | 3.56 / 3.96 | 3.46 / 3.96 | 3.56 / 3.96 |
| 3 | 14.78 / 15.48 | 14.08 / 15.08 | 11.68 / 12.98 | 14.08 / 15.08 | 12.68 / 15.08 | 11.68 / 11.68 |
| 4 | 14.68 / 15.48 | 13.58 / 15.28 | 11.68 / 14.28 | 14.28 / 15.38 | 12.78 / 14.28 | 11.68 / 12.08 |
| 5 | 14.03 / 15.33 | 12.53 / 14.73 | 11.73 / 13.83 | 13.23 / 14.83 | 11.73 / 13.63 | 11.73 / 11.73 |
| 6 | 14.03 / 15.03 | 12.53 / 15.33 | 11.73 / 14.13 | 13.13 / 14.93 | 11.73 / 14.03 | 11.73 / 11.73 |
| 7 | 14.23 / 15.33 | 12.63 / 14.43 | 11.73 / 11.93 | 12.63 / 15.33 | 11.73 / 14.63 | 11.73 / 11.73 |
| 8 | 14.33 / 15.03 | 13.23 / 14.33 | 11.73 / 13.23 | 13.73 / 14.83 | 11.83 / 14.13 | 11.73 / 11.93 |
| 13 | 14.23 / 15.33 | 13.03 / 14.33 | 11.73 / 12.43 | 13.13 / 14.93 | 11.73 / 14.13 | 11.73 / 12.53 |

Median / maximum seconds from the burst's end to the release, whichever exit made it (WN1 and WN2 are one row: the old exit, the same figures to 0.1 s). On main, the same seeds for WN4 and WN6 at -6 dB left 138 of 160 bursts still locked after the 30 s.

**Steady carriers.** After a weak burst, a carrier at 1650, 1800, 1950 or 2100 Hz (WN4, WN6) or at 1800, 1896 or 1992 Hz (WN2, the K = 48 lines) at -6, 0 or +6 dB in 3 kHz: the plain sum averaged 2.6 to 18.6, enough to hold the lock for ever, and the floored statistic -0.6 to -11.5. Every one was let go: WN4 and WN6 by the new exit after 13.8 to 15.1 s, WN2 by either exit after 3.4 to 8.9 s. These are CI tests.

**CPU, the issue's scenario.** At the daemon's 48 kHz, `Ms110dModem` with the catalogue defaults, one WN4 burst at -6 dB and then three simulated hours of noise, process CPU time inside `Process` per simulated hour, the three runs side by side on this box (16 threads, shared, load average about 10):

| | CPU per simulated hour | share of a core | carrier held |
|---|---|---|---|
| main, after the weak burst | 610 s | 17.0% | the whole 3 h |
| this change, after the weak burst | 52.2 s | 1.45% | 15.5 s |
| this change, noise only (idle) | 51.7 s | 1.43% | 1.8 s |

The idle figure's 1.8 s is the receiver reading the preambles noise now and then appears to start, and dropping them when the WID check fails, as it always has. The first version of this PR, measured the same way under similar load, gave 563 s against 45 s; the ratio, about 12 to 1, is the figure to carry, not the percentages.

## Not covered

- WN0 has no probes. Its own discriminator, an absolute line on the Walsh correlation, already lets go: across -9 to -18 dB (six seeds per step) the bursts either decoded to their EOM or were let go within 0.1 s of their end, and below about -11 dB they are not acquired at all.
- A fade longer than the window with nothing of the signal left in it still ends the burst, as it would have to: the evidence's worst case, an 18 s fade 15 dB deep at WN4, stayed 8.4 noise sigmas above zero because the faded signal was still faintly there, but a fade into nothing for longer than 16 s is let go. Where the old receiver held on through such a fade it was by luck, on an equalizer fitted to noise.
- The mailcast receiver's own 150 s limit on a lock (`MailcastOnAir.LongestBurst`, from pdn-mailcast's PR #22) stays as a backstop. With this change the modem lets go long before it, and the receiver journals the modem's line and counts it in the same `locksReleased` figure.

## Reproduce

From the repository root, after `dotnet build -c Release`:

```sh
t="dotnet run --no-build -c Release --project tests/Packet.SoundModem.Tests/Packet.SoundModem.Tests.csproj --"
$t -class Packet.SoundModem.Tests.Ms110d.Ms110dSignalAbsentTests      # the CI tests, about two and a half minutes
MS110D_SIGNAL_ABSENT_CENSUS=1 MS110D_CENSUS_WORKERS=10 $t -method "*.Release_Latency_Census"   # about 10 core-hours
MS110D_SIGNAL_ABSENT_MARGIN=1 MS110D_CENSUS_SEEDS=4 $t -method "*.Presence_Margin_At_The_Mask_Points"
MS110D_LOCK_CPU=1 $t -method "*.Cpu_Per_Simulated_Hour_After_A_Weak_Burst"   # MS110D_LOCK_CPU_RUN=weak or idle for one
for set in A B C D V E; do
  MS110D_SIGNAL_ABSENT_TUNE=1 MS110D_TUNE_SET=$set MS110D_TUNE_OUT=/tmp/tune MS110D_TUNE_WORKERS=14 $t -method "*.Trace_A_Tuning_Set"
done
python3 docs/dev/ms110d/evidence/2026-10-06-signal-absent-tuning/evaluate.py /tmp/tune floored 16 4
python3 docs/dev/ms110d/evidence/2026-10-06-signal-absent-tuning/evaluate.py /tmp/tune --margin 4,8,12,16
```

The tuning sets took about an hour on 14 cores. The battery is `docs/dev/archive/ms110d/evidence/2026-08-20-poorgate-g0/battery.sh`, run with its `cd` line pointed at the repository root (it predates the move into `archive/` and now lands in `docs/dev`), and compared census by census and mask line by mask line against `docs/dev/archive/ms110d/evidence/2026-08-21-poorgate-g4/battery/` as its `compare.sh` does. The "main" CPU row used the same measurement on a checkout of main at f7a1234.
