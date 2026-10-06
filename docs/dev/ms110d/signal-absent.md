# Letting go of a lock with no signal under it (issue #553)

Status: design and evidence note, 2026-10-05, revised three times on 2026-10-06 after the reviews of PR #556: a 16 s window instead of 4 s, then carrier cancellation and a background measured in the lags, then up to four carriers. Describes the signal-absent release in `Ms110dDemodulator` (the `SignalAbsent` burst end), why it is built the way it is, and what was measured before it shipped.

## The fault

pdn-mailcast found it (its PR #22): when the receiver acquires the preamble of a burst too weak to read, about -3 to -12 dB SNR in 3 kHz, it can stay locked for good. With no EOM it goes on demodulating the noise that follows, at about 8 times the idle receive CPU, and is deaf to every later burst until something resets it.

The receiver already had a signal-lost exit: four seconds of consecutive mini-probes whose equalized correlation sits below `max(0.10, 0.45 x reference)`. Two things defeat it on a weak burst. The reference is the burst's own healthy level, and on a weak burst that level is little above what noise gives. And the equalizer is trained on every probe, so on noise it fits the noise, and the next probe's equalized correlation keeps landing above 0.10 often enough to reset the four-second count. Reproduced on main at the native rate (WN4 at -6 dB, AWGN): still locked after 180 s of noise, on every seed tried, and the same for WN3, WN5 to WN8 and WN13 at -9 dB. WN1 and WN2 (whose K = 48 probe solve is heavily regularized) and WN0 (no probes; its own discriminator has an absolute, noise-calibrated line) let go by themselves.

## The decision

Each frame's mini-probe is matched-filtered against the raw received symbols, not the equalizer's output. For each lag d in a window of half a probe base period either side of the cursor (15 lags for K = 32, 25 for K = 48), the K received symbols from the probe's start plus d are correlated with the known probe, giving a complex c_d. A signal puts its channel energy into the few lags its paths occupy; noise puts the same expected energy into every lag. Two steps come before the energies are added up:

1. **Steady carriers are cancelled, up to four of them.** A carrier's c_d is an exact complex sinusoid in d, A e^{jwd}, whatever its frequency, so across the lags a carrier is a line in the spectrum of c. A signal is broad there: its lag spectrum is its channel's frequency response. The spectrum of c (4 bins per lag) is averaged over the last 4 s of earlier frames, never the current one. The strongest bin in it is taken for a carrier when it stands 1.5 times above what noise puts in a bin and its excess over the noise is 4 times the spectrum's median excess. The median excess is what a signal's own channel puts in every bin, and two equal paths ripple it by a factor of two at most, so a readable burst's ripple is never taken for a carrier. The carrier's frequency is refined with a parabola through the bin and its neighbours, its leakage (the lag window's Dirichlet kernel, scaled to its excess) is taken off the spectrum, and the search repeats. Then the frame's c is projected off each carrier's e^{jwd}, and off d e^{jwd} with d counted from the middle lag (the first-order term in w, which catches an estimate slightly off the carrier as the receiver's own carrier loop wanders). On noise each carrier found takes out exactly two degrees of freedom, whatever the earlier frames pointed to.
2. **The background is measured in the lags themselves.** Each residual lag's energy is divided by its own noise variance after the projection (1 less the projection's weight at that lag). The median of those, over the middle order statistic of that many unit exponentials (0.7254 for 15 lags, 0.7127 for 25), is the background, averaged over the last 2 s of frames.

The frame's statistic is

    presence = (sum over d of |residual_d|^2) / background - (lags - rank)

where rank is the dimension of the projection, twice the carriers found. This averages zero on noise, and on up to four carriers of any level. A signal at symbol SNR s adds about K s / (1 + s), times the share of its channel energy inside the lag window, in the few lags its paths occupy, and leaves the median where the noise put it. The receiver averages the statistic over the frames of the last 16 s (134 frames at U = 256, 300 at U = 96, 400 at U = 48) and lets go when that average falls below

    line = 4 x sqrt(lags / frames)

which is four standard deviations of a noise-only window mean, taking each lag as an independent unit exponential. Measured on noise, the per-frame standard deviation is 3.2 for 15 lags and 3.8 for 25, against 3.9 and 5.0 for independent lags, and the per-frame mean is -0.03 and +0.02. The release ends the burst with `Ms110dBurstEndReason.SignalAbsent`, counts it in `Ms110dDemodulator.SignalAbsentReleases`, and drops `CarrierDetect`. On `Ms110dModem` it raises `LockReleased` once, with one plain-ASCII line, and counts it in `LocksReleased`. The daemon journals that line against its modem, and the mailcast receiver journals and counts it.

Why this and not the alternatives:

- **It is the equalizer that is fooled, so the detector must not go through it.** The statistic reads the ring with the receiver's carrier and timing and nothing else: no taps, no reference level carried from the burst's start, no AGC (the ratio is scale-free). Noise is scored as noise however the DFE has adapted to it.
- **A fixed line works because the scale is known.** Measuring the background in the lags puts noise at zero with a variance set by the lag count alone, so the line has the same meaning at every receive level. The old discriminator could not have a fixed line because its scale is the equalizer output's.
- **Carriers, both ways round.** A steady carrier on one of the probe's spectral lines (1800 Hz plus a multiple of 2400 / base length: 150 Hz steps for K = 32, 96 Hz for K = 48) correlates with the probe equally at every lag. Uncancelled, it scored like a signal and held the lock for ever after a weak burst, even 6 dB under the noise: #553 again, from a CW ident or any carrier on the band. The review's first answer, a floor taken from the median lag, over-corrected: it scaled the carrier's share of every lag by 1/0.73 and put a readable burst under a strong carrier below the line, so main read 34 frames and that version 9. The second trap is normalizing by the received power, as the first version did. A strong carrier off the lines barely correlates with the probe, but it fills the power, so it pushed the score under the noise and ended readable bursts. Cancelling the sinusoid and measuring the background in the residual lags avoids both. The third review found that cancelling one carrier is not enough: two carriers, 1650 and 2100 Hz at 6 or 9 dB under a WN4 burst at +6 dB, still ended it 9 frames in of main's 34. Hence the search for up to four lines, rather than a single frequency estimate.
- **The window is long because slow fades are the real case.** GB7RDG's path is 40 m NVIS, where a fade can take the probes away for many seconds inside a burst that is still worth reading, and the receiver before #553 rode those fades, if for the wrong reason. A lost burst costs more than a dead lock held a few seconds longer, so the window was chosen from evidence, below, as the shortest with a clear margin at which no frame the old receiver read is lost. A 4 s window, the old signal-lost patience, lost 4,009 frames of 68,672 in that evidence.
- **A cap on lock time is not sound for App D.** App D has no block CRC, so "no data block decoded" is not something the demodulator can observe: the Viterbi decoder always emits bits, and the IL2P CRC sits a layer above. A cap on lock time without decodes therefore becomes a cap on burst length, which either cuts real long bursts (packing allows bursts of any length) or, set long enough to be safe, leaves the lock in place for minutes. The probe test needs neither.
- **One frame late, on purpose.** Each frame scores the probe before its own data block, never its own trailing probe, because the last lags of that probe may not have arrived when the frame is processed. Every sample the statistic reads is then resident whatever block size the caller feeds, and the release frame does not depend on how the audio is cut up (pinned by a test at 37, 960, 4801 samples and the whole stream).

Nothing in the statistic writes receiver state, so until it fires the receiver is bit-identical to one without it.

**The bound.** A burst that ends is let go once the window holds mostly noise: at most one window plus one frame after its last probe, plus the frame it takes to read the next one. That is 16.25 s after the burst's last sample at U = 256 and a little less for the shorter frames. A weak burst whose presence is already below the line can be let go before it ends, which costs nothing, since it cannot be read.

## Measured

Native 9600 Hz unless stated, SNR in 3 kHz, every run seeded.

**Choosing the window and the line.** `Ms110dSignalAbsentTuning` ran each case with the release switched off, so the receiver behaved exactly as it did before #553, and traced the statistic and every frame read. `evidence/2026-10-06-signal-absent-tuning/evaluate.py` then scores any window and line exactly, offline, because the statistic changes nothing until it fires. The fading cases are 60 s packed bursts at every waveform number:

- one Rayleigh path at 0.02, 0.05, 0.1 and 0.2 Hz, at the AWGN mask SNR plus 0, 5 and 10 dB, five seeds;
- scripted fades 15 and 30 dB deep, held 4 to 18 s, at the AWGN mask SNR minus 3, plus 0, 3 and 6 dB;
- the AWGN and Poor mask points;
- steady carriers under a readable burst: one on the probe lines (1650, 1950, 2100 Hz for K = 32, 1896 and 1992 Hz for K = 48) or off them (1875, 2025, 1850, 1944 Hz), from 6 dB below the burst to 8 dB above it; and two, three and four together, on the lines, off them and mixed, at 6 to 12 dB in 3 kHz each, under WN2, WN4 and WN6 bursts at the AWGN mask SNR plus 1 and plus 5 dB.

The final round traced 1,598 such cases with the multi-carrier statistic (the 4 to 8 s scripted fades of the earlier rounds were left out of it; the CI tests keep their cases): 68,672 frames read by the old receiver. The rule was: no frame the old receiver read may be lost.

| window, 4 sigma line | frames lost | cases losing frames | release after a weak burst (median / max) |
|---|---|---|---|
| 4 s | 4,009 | 82 | |
| 8 s | 850 | 17 | |
| 12 s | 0 | 0 | 7.7 / 12.1 s |
| 16 s (shipped) | 0 | 0 | 11.8 / 16.0 s |

12 s already loses nothing, with less in hand: its lowest window that still had a frame to come was 6.6 noise sigmas, against 10.7 at 16 s (both on an 18 s fade 15 dB deep at WN4 +5 dB). The lowest carrier case at 16 s is 18 sigmas (four carriers at 12 dB under WN6 +10 dB). The reviews' cases read what the old receiver read and are CI tests now: WN4 under one carrier 5 or 8 dB above it 34 of 34, under 1650 + 2100 Hz at 6, 9 and (at +10 dB) 10 dB 34 of 34, the 4 to 6 s fade at +3 dB 29, the 0.05 Hz Rayleigh seed 12.

The earlier versions of this PR, measured the same way: the 4 s window on the plain sum lost 855 frames in 49 cases; a 16 s window with a median floor lost 2,374 of 5,882 frames in 62 of 140 single-carrier cases; cancelling one carrier lost 25 of 34 frames under two.

**Noise only.** Per frame the statistic averaged -0.03 with a standard deviation of 3.2 for 15 lags (64,170 noise frames), and +0.02 and 3.8 for 25 lags (1,867 frames: the K = 48 modes are almost always let go by their own signal-lost exit first). Independent lags would give 3.9 and 5.0. The lines are 1.34 at U = 256 (WN5 to WN8, WN13), 0.89 at U = 96 (WN3, WN4) and 1.00 at U = 48 (WN1, WN2).

**Real bursts never let go.** The full mask battery in the G0 form is byte-identical to the 2026-08-21 G4 battery: 120 of 120 per-burst censuses, end reasons included, and every mask line's bits and errors. That covers Poor on both seed families, AWGN, the static WID 2 rig and the Doppler checks: 32 points, 4 workers each. A release anywhere in it would have changed a census. CI holds packed bursts of 113 to 120 s (WN4 Poor +10 dB, WN6 Poor +14 dB, WN1 AWGN -3 dB) to their EOM, and seven slow-fade and six carrier cases to the old receiver's frame count.

**Weak bursts let go.** The tuning traces include 324 weak bursts (WN1 to WN8 and WN13, AWGN and Poor, at -3, -6 and -9 dB, six seeds each, with 4 s of data behind the catalogue's 3 super-frame preamble, then 30 s of noise), and 108 weak bursts followed by one to four steady carriers, on or off the lines, at -6, 0 and +6 dB each. None stayed locked. The K = 48 modes mostly go by their old exit within 4 s. Every release came at most 15.95 s after the burst's end, against the 16.25 s bound, with a median of 11.8 s. A burst whose window never held enough signal goes at the first full window, about 11.7 s after its end here (16 s after its data began). On main, the same kind of burst stays locked: 138 of 160 WN4 and WN6 bursts at -6 dB were still locked 30 s on.

**CPU, the issue's scenario** (measured on the second version, with the same 16 s window; the carrier search adds a few thousand multiplies per frame; the review measured 1.35% of a core against main's 15.8% on the single-carrier revision). At the daemon's 48 kHz, `Ms110dModem` with the catalogue defaults, one WN4 burst at -6 dB and then three simulated hours of noise, process CPU time inside `Process` per simulated hour, the three runs side by side on this box (16 threads, shared, load average about 10):

| | CPU per simulated hour | share of a core | carrier held |
|---|---|---|---|
| main, after the weak burst | 610 s | 17.0% | the whole 3 h |
| this change, after the weak burst | 52.2 s | 1.45% | 15.5 s |
| this change, noise only (idle) | 51.7 s | 1.43% | 1.8 s |

The idle figure's 1.8 s is the receiver reading the preambles noise now and then appears to start, and dropping them when the WID check fails, as it always has. The first version of this PR, measured the same way under similar load, gave 563 s against 45 s; the ratio, about 12 to 1, is the figure to carry, not the percentages.

## Not covered

- WN0 has no probes. Its own discriminator, an absolute line on the Walsh correlation, already lets go: across -9 to -18 dB (six seeds per step) the bursts either decoded to their EOM or were let go within 0.1 s of their end, and below about -11 dB they are not acquired at all.
- A fade longer than the window with nothing of the signal left in it still ends the burst, as it would have to: the evidence's worst case, an 18 s fade 15 dB deep at WN4, stayed 8.4 noise sigmas above zero because the faded signal was still faintly there, but a fade into nothing for longer than 16 s is let go. Where the old receiver held on through such a fade it was by luck, on an equalizer fitted to noise.
- **The burst after a weak one.** While the receiver holds a lock it is not looking for preambles, so a burst whose preamble arrives before a weak lock is let go is missed. With pdn-mailcast's bursts of up to 18 s and 1 s gaps, that is the next burst, and the one after it too when bursts are shorter than the 12 to 15 s it takes to let go. Before this change everything after the weak burst was lost, until a reset or the mailcast receiver's 150 s limit. The fix would be to go on searching while locked and give a weak lock up to a fresh preamble. It is not done here, because the demodulator cannot tell a weak lock from a readable burst in a fade: App D has no block CRC, and only the IL2P layer above knows whether frames are coming out. A false preamble detection would then cost a readable burst its frames, the one thing this change is built not to do, and a preamble search during every lock costs about the idle receive CPU again. pdn-mailcast repairs a lost burst with its fountain-coded frames, as it repairs any other loss. If a lost burst after weak ones proves costly on air, the place to start is a preamble watch gated on the frame layer: no frame read since the lock, and a preamble well above the acquisition threshold.
- The mailcast receiver's own 150 s limit on a lock (`MailcastOnAir.LongestBurst`, from pdn-mailcast's PR #22) stays as a backstop. With this change the modem lets go long before it, and the receiver journals the modem's line and counts it in the same `locksReleased` figure.

## Reproduce

From the repository root, after `dotnet build -c Release`:

```sh
t="dotnet run --no-build -c Release --project tests/Packet.SoundModem.Tests/Packet.SoundModem.Tests.csproj --"
$t -class Packet.SoundModem.Tests.Ms110d.Ms110dSignalAbsentTests      # the CI tests, about two and a half minutes
MS110D_SIGNAL_ABSENT_CENSUS=1 MS110D_CENSUS_WORKERS=10 $t -method "*.Release_Latency_Census"   # the wider weak-burst census, about 10 core-hours
MS110D_LOCK_CPU=1 $t -method "*.Cpu_Per_Simulated_Hour_After_A_Weak_Burst"   # MS110D_LOCK_CPU_RUN=weak or idle for one
for set in A C D E K M N T; do
  MS110D_SIGNAL_ABSENT_TUNE=1 MS110D_TUNE_SET=$set MS110D_TUNE_OUT=/tmp/tune MS110D_TUNE_WORKERS=14 $t -method "*.Trace_A_Tuning_Set"
done
python3 docs/dev/ms110d/evidence/2026-10-06-signal-absent-tuning/evaluate.py /tmp/tune release 16 4
python3 docs/dev/ms110d/evidence/2026-10-06-signal-absent-tuning/evaluate.py /tmp/tune --margin 4,8,12,16
```

The tuning sets took about an hour and a half on 14 cores. The battery is `docs/dev/archive/ms110d/evidence/2026-08-20-poorgate-g0/battery.sh`, run with its `cd` line pointed at the repository root (it predates the move into `archive/` and now lands in `docs/dev`), and compared census by census and mask line by mask line against `docs/dev/archive/ms110d/evidence/2026-08-21-poorgate-g4/battery/` as its `compare.sh` does. The "main" CPU row used the same measurement on a checkout of main at f7a1234.
