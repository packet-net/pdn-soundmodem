# Letting go of a lock with no signal under it (issue #553)

Status: design and evidence note, 2026-10-05. Describes the signal-absent release in `Ms110dDemodulator` (the `SignalAbsent` burst end), why it is built the way it is, and what was measured before it shipped.

## The fault

pdn-mailcast found it (its PR #22): when the receiver acquires the preamble of a burst too weak to read, about -3 to -12 dB SNR in 3 kHz, it can stay locked for good. With no EOM it goes on demodulating the noise that follows, at about 8 times the idle receive CPU, and is deaf to every later burst until something resets it.

The receiver already had a signal-lost exit: four seconds of consecutive mini-probes whose equalized correlation sits below `max(0.10, 0.45 x reference)`. Two things defeat it on a weak burst. The reference is the burst's own healthy level, and on a weak burst that level is little above what noise gives. And the equalizer is trained on every probe, so on noise it fits the noise, and the next probe's equalized correlation keeps landing above 0.10 often enough to reset the four-second count. Reproduced on main at the native rate (WN4 at -6 dB, AWGN): still locked after 180 s of noise, on every seed tried, and the same for WN3, WN5 to WN8 and WN13 at -9 dB. WN1 and WN2 (whose K = 48 probe solve is heavily regularized) and WN0 (no probes; its own discriminator has an absolute, noise-calibrated line) let go by themselves.

## The decision

Each frame's mini-probe is matched-filtered against the raw received symbols, not the equalizer's output. For each lag d in a window of half a probe base period either side of the cursor (13, 15 and 25 lags for K = 24, 32 and 48), the K received symbols from the probe's start plus d are correlated with the known probe, giving c_d. Each |c_d|^2 is divided by K times the mean received power over the span, so on noise each lag contributes a unit-mean exponential whatever the level, and the frame's statistic is

    presence = sum over d of |c_d|^2 / (K P) - lags

which is zero-mean on noise and grows by about K s / (1 + s) for a signal at symbol SNR s, times the share of the channel's energy inside the lag window. The receiver averages it over the frames of the last 4 s (34 frames at U = 256, 75 at U = 96, 100 at U = 48) and lets go when that average falls below

    line = 4 x sqrt(lags / frames)

four standard deviations of a noise-only window mean, taking each lag as an independent unit exponential. The release ends the burst with `Ms110dBurstEndReason.SignalAbsent`, counts it in `Ms110dDemodulator.SignalAbsentReleases`, drops `CarrierDetect`, and on `Ms110dModem` raises `LockReleased` once, with one plain-ASCII line for the host's journal, and counts it in `LocksReleased`.

Why this and not the alternatives:

- **It is the equalizer that is fooled, so the detector must not go through it.** The statistic reads the ring with the receiver's carrier and timing and nothing else: no taps, no reference level carried from the burst's start, no AGC (the ratio is scale-free). Noise is scored as noise however the DFE has adapted to it.
- **A fixed line works because the scale is known.** Normalizing by the received power puts noise at zero with a variance set by the lag count alone, so the line is derived, not tuned, and is the same at every receive level. The old discriminator could not have a fixed line because its scale is the equalizer output's.
- **A cap on lock time is not sound for App D.** App D has no block CRC, so "no data block decoded" is not something the demodulator can observe: the Viterbi decoder always emits bits, and the IL2P CRC sits a layer above. A cap on lock time without decodes therefore becomes a cap on burst length, which either cuts real long bursts (packing allows bursts of any length) or, set long enough to be safe, leaves the lock in place for minutes (mailcast's own workaround is 150 s). The probe test needs neither.
- **The window is the existing signal-lost patience.** 4 s was chosen for that exit against Poor-channel fades (the Phase B census its comment in `Ms110dDemodulator` cites), and a mean over 4 s asks less of a fading signal than four seconds of consecutive bad probes: a fade inside the window lowers the mean, it does not end it.
- **One frame late, on purpose.** Each frame scores the probe before its own data block, never its own trailing probe, because the last lags of that probe may not have arrived when the frame is processed. Every sample the statistic reads is then resident whatever block size the caller feeds, and the release frame does not depend on how the audio is cut up (pinned by a test at 37, 960, 4801 samples and the whole stream).

Nothing in the statistic writes receiver state, so until it fires the receiver is bit-identical to one without it.

**The bound.** A burst that ends is let go once the window holds mostly noise: at most one window plus one frame after its last probe, plus the frame it takes to read the next one. That is 4.25 s after the burst's last sample at U = 256 and less for the shorter frames. A weak burst whose presence is already below the line can be let go before it ends, which costs nothing, since it cannot be read.

## Measured

Native 9600 Hz unless stated, SNR in 3 kHz, every run seeded.

**Noise only.** Frames more than a second after a weak burst's end, on the K = 32 modes: the per-frame statistic's standard deviation was 3.1 to 3.4 (independent lags would give sqrt(15) = 3.9), the 4 s window mean sat at -0.16 to -0.07 with a standard deviation of 0.29 to 0.51, and the highest noise-only window seen was 1.45 against a line of 2.66 (WN6) and 0.69 against 1.79 (WN4). The lines: 2.66 at U = 256 (WN5 to WN8, WN13), 1.79 at U = 96 (WN3, WN4), 2.00 at U = 48 (WN1, WN2).

**Real bursts never let go.** The full mask battery in the G0 form (Poor on both seed families, AWGN, the static WID 2 rig and the Doppler checks; 32 points, 4 workers each) is byte-identical to the 2026-08-21 G4 battery: 120 of 120 per-burst censuses, end reasons included, and every mask line's bits and errors. A release anywhere in it would have changed a census. How far clear of the line: over four 90 s bursts at every AWGN and Poor mask point (Long interleaver, 20 super-frames, the masks' own form), the lowest 4 s window of any burst was 4.8 times the line (WN1 AWGN -3 dB, the lowest-SNR mask point of all) and 5.4 to 8.0 times it everywhere else, Poor included.

The CI test `A_Two_Minute_Packed_Burst_At_A_Decodable_Snr_Is_Never_Let_Go_Early` pins it for packed bursts of 113 to 120 s: WN4 Poor +10 dB, WN6 Poor +14 dB and WN1 AWGN -3 dB each hold the lock to their EOM, with no release.

**Weak bursts let go.** 2,520 bursts: WN1 to WN8 and WN13; AWGN at -3, -6, -9 and -12 dB and Poor at -3, -6 and -9 dB; 40 seeds each; 4 s of data behind the catalogue's 3 super-frame preamble, then 30 s of noise. None was still locked at the end. 294 were never acquired and 431 had their preamble heard but failed the WID check, so never locked. Of the 1,795 that locked, 217 were read to their EOM (mostly WN1 and WN2 at -3 and -6 dB), 7 were let go by the old signal-lost exit and 1,571 by the new one. Time from the burst's last sample to the release: at most 3.53 s, against the 4.25 s bound; per cell the medians run from 1.5 to 3.1 s at -3 and -6 dB. At -9 and -12 dB many are let go before the burst ends (the earliest at 0.54 s before), the window having filled with a signal already below the line. On main, the same seeds for WN4 and WN6 at -6 dB left 138 of 160 bursts still locked after the 30 s (every AWGN one, and 58 of 80 on Poor, where the rest never locked bar one the old exit let go 4.0 s after its end); with this change none.

| WN | AWGN -3 | AWGN -6 | AWGN -9 | Poor -3 | Poor -6 | Poor -9 |
|---|---|---|---|---|---|---|
| 1 | all EOM | 2.66 / 3.16 | 1.66 / 2.76 | 2.76 / 3.06 | 2.16 / 2.96 | 1.36 / 2.36 |
| 2 | all EOM | 2.86 / 3.26 | 1.96 / 2.86 | 3.06 / 3.26 | 2.56 / 2.96 | 1.76 / 2.46 |
| 3 | 3.08 / 3.48 | 2.38 / 3.08 | 1.08 / 2.08 | 2.88 / 3.28 | 2.18 / 3.28 | 0.78 / 2.58 |
| 4 | 3.08 / 3.48 | 2.48 / 3.08 | 1.18 / 2.28 | 2.78 / 3.38 | 2.28 / 3.08 | 0.38 / 2.28 |
| 5 | 2.73 / 3.43 | 1.73 / 3.23 | -0.27 / 2.73 | 2.43 / 3.53 | 1.53 / 2.83 | -0.27 / 0.83 |
| 6 | 2.73 / 3.53 | 1.73 / 3.03 | -0.27 / 1.53 | 2.63 / 3.33 | 1.73 / 2.73 | -0.27 / 1.23 |
| 7 | 2.73 / 3.33 | 1.73 / 2.73 | -0.27 / 1.83 | 2.43 / 3.33 | 1.23 / 3.03 | -0.27 / 0.03 |
| 8 | 2.93 / 3.53 | 2.13 / 3.03 | 0.63 / 1.63 | 2.83 / 3.43 | 1.83 / 3.03 | 0.33 / 2.73 |
| 13 | 2.73 / 3.33 | 1.83 / 2.63 | -0.27 / 1.73 | 2.73 / 3.53 | 1.73 / 3.33 | -0.07 / 2.13 |

Median / maximum seconds from the burst's end to the release; negative is before the end. At -12 dB AWGN most bursts are not acquired at all, and the few that are go before their end.

**CPU, the issue's scenario.** At the daemon's 48 kHz, `Ms110dModem` with the catalogue defaults, one WN4 burst at -6 dB and then three simulated hours of noise, process CPU time inside `Process` per simulated hour, the three runs side by side on this box (16 threads, shared, load average about 11):

| | CPU per simulated hour | share of a core | carrier held |
|---|---|---|---|
| main, after the weak burst | 563 s | 15.7% | the whole 3 h |
| this change, after the weak burst | 45.0 s | 1.25% | 4.1 s |
| this change, noise only (idle) | 44.9 s | 1.25% | 1.8 s |

The idle figure's 1.8 s is the receiver reading the preambles noise now and then appears to start, and dropping them when the WID check fails, as it always has. A run of the same three under heavier load (about 30) gave 23.2% before against 2.2% after, so the ratio, about 12 to 1, is the figure to carry, not the percentages.

## Not covered

- WN0 has no probes. Its own discriminator, an absolute line on the Walsh correlation, already lets go: across -9 to -18 dB (six seeds per step) the bursts either decoded to their EOM or were let go within 0.1 s of their end, and below about -11 dB they are not acquired at all.
- The daemon does not yet print `LockReleased`; a host that wants the line in its journal subscribes to it. `LocksReleased` is there for a host that counts, as pdn-mailcast's workaround does.

## Reproduce

From the repository root, after `dotnet build -c Release`:

```sh
t="dotnet run --no-build -c Release --project tests/Packet.SoundModem.Tests/Packet.SoundModem.Tests.csproj --"
$t -class Packet.SoundModem.Tests.Ms110d.Ms110dSignalAbsentTests      # the CI tests, about a minute
MS110D_SIGNAL_ABSENT_CENSUS=1 MS110D_CENSUS_WORKERS=10 $t -method "*.Release_Latency_Census"   # 20 min
MS110D_SIGNAL_ABSENT_MARGIN=1 MS110D_CENSUS_SEEDS=4 $t -method "*.Presence_Margin_At_The_Mask_Points"
MS110D_LOCK_CPU=1 $t -method "*.Cpu_Per_Simulated_Hour_After_A_Weak_Burst"   # MS110D_LOCK_CPU_RUN=weak or idle for one
```

The battery is `docs/dev/archive/ms110d/evidence/2026-08-20-poorgate-g0/battery.sh`, run with its `cd` line pointed at the repository root (it predates the move into `archive/` and now lands in `docs/dev`), and compared census by census and mask line by mask line against `docs/dev/archive/ms110d/evidence/2026-08-21-poorgate-g4/battery/` as its `compare.sh` does. The "main" CPU row used the same measurement on a checkout of main at 6532152.
