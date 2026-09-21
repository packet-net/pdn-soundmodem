# C4FSK on air: what is wrong, measured

**Status 2026-09-21, second pass.** `c4fsk9600` and `c4fsk19200` deliver nothing over the
radio1/radio2 bench. There are two separate faults, and the first pass of this document blamed a
third thing that turns out not to exist. Everything below is measured on the rig on 2026-09-21
unless it says otherwise.

The short version. The receive path's **energy gate never opens**, because an FM receiver with
its squelch open is LOUDER when idle than when a carrier arrives, and C4FSK is the only modem
that hard-gates its bit path on that detector. That alone is the whole "zero frames, every size,
both directions". Behind it there is a second, independent fault: the audio coupling network
rolls off at the bottom of the band, and the **baseline wander** that produces closes a 4-level
eye. `c4fsk9600` needs a path flat to about 30 Hz and this one is 3 dB down at 70.

The first pass blamed a rolloff at the TOP of the band. That was a misreading of its own
instrument and it is corrected below.

## The rig

Two Tait TM8110s at 146.900 MHz, wide (25 kHz) channel, 1 W into 20 dB attenuators into antennas
about a metre apart. Audio injected at the **T12** tap, which bypasses the limiter, the 3 kHz low
pass and pre-emphasis; receive from the discriminator. CM108 interfaces built to
[docs/hardware/tait-tm8100-cm108.md](../hardware/tait-tm8100-cm108.md), `pdn-soundmodem` on a Pi
at each end, KISS on 8105, config API on 8107. An SDRplay RSP1 sits on radio1.

`ofdm-fm-8k` delivers 100 % on this rig all day, and did again at the end of this session (10 of
10 at 1024 bytes, 15.1 kbit/s). It is the control. But note WHY it is immune to the fault below:
its lowest subcarrier is at 211 Hz, so it never uses the part of the band that is broken.

## Fault 1: the energy gate never opens on an FM receiver

`C4fskModem` is the only modem in the tree where `EnergyBusyDetector.Busy` is a hard gate on the
bit path rather than one vote in carrier sense; the constructor comment says so and gives the
reason (on silence the slicer saturates, and the Mode-2 sync word is 18 ones in 24 bits, so the
deframer false-locks continuously - about 12k near-sync hits in one recording). The detector
asserts at 6 dB above a tracked floor.

**An FM receiver with the squelch open goes QUIET when a carrier arrives.** Measured on radio1
today, through the modem's own 7.2 kHz receive filter:

| | level |
|---|---|
| idle, open-squelch hiss | -17.0 dBFS |
| a `c4fsk9600` transmission, transmit volume 0 dB | -18.1 dBFS |
| the same at -3 / -6 / -12 / -18 dB | -20.9 / -23.8 / -30.0 / -36.1 dBFS |

The signal is below the idle channel at every transmit level there is. The gate cannot assert.
Run over a 100 s recording containing 29 s of transmissions, the modem's gate opened exactly
once, at 48.92 s, which is the moment the transmission STOPPED and the hiss came back.

This is sufficient on its own for every symptom: zero frames at 8, 16, 32, 64 and 256 bytes, both
directions, at every transmit level. It is also why `fsk9600`, which has no hard gate, delivers
over the identical path.

It also explains why the loopback passes byte-identically: on a virtual-audio loop the inter-burst
audio is digital silence, so the gate is trivially right.

**Every conclusion the first pass drew from a transmit-level sweep is void**, because the gate was
shut throughout. That includes "correcting the level does not fix decoding".

## Fault 2: baseline wander closes the 4-level eye

With the gate forced open the modem still reads nothing, and the eye at the symbol instants is
shut. Measured as the rms distance of the symbol-instant values to the nearest ideal 4-PAM level,
normalised to the outer envelope (0.00 is a perfect eye, 0.33 is no eye at all):

| | eye | sync-word correlation |
|---|---|---|
| clean transmit, straight out of the modulator | 0.019 to 0.032 | 0.945 |
| received off air, transmit volume 0 dB | 0.193 | 0.82 |
| the same at -3, -6, -12, -18 dB | 0.193, 0.192, 0.191, 0.193 | 0.81 to 0.82 |

Completely level-independent, so it is not deviation, not the IF filter and not noise.

**What the path actually does.** End-to-end audio response, radio2 transmit through RF to radio1
receive, stepped tone through `POST /api/txtest`, read off radio1's `rawCapture`:

| Hz | 50 | 70 | 100 | 140 | 200 | 280 | 400 | 560 | 800 | 1130 | 1600 | 2260 | 3200 | 4500 | 6400 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| dB | -6.9 | -3.2 | -1.7 | -0.9 | -0.4 | -0.1 | +0.1 | +0.1 | +0.1 | 0.0 | -0.1 | -0.3 | -0.8 | -0.9 | -3.7 |

Flat from 280 Hz to 4.5 kHz, and high-passing below that, 3 dB down at about 70 Hz. That is the
coupling network: the interface has a 1 uF in the transmit tail (the hardware page puts its corner
at 43 to 64 Hz), a 1 uF at each end on the CM108 board itself, and 4u7 in the receive tail.

**What that does to each mode**, from `C4fskBaselineWanderProbe`, a first-order high pass in front
of the real receiver, 40 seeds, 60 dB AWGN so the filter is the only impairment, frames delivered
out of 40:

```
mode           bytes |  none    10    20    30    50    70   100   150   200   300
c4fsk9600         60 |    40    40    40    40     0     0     0     0     0     0
c4fsk19200        60 |    40    40    40    40    40    40     0     0     0     0
fsk9600           60 |    40    40    40    40    40    40    40    40    39     0
```

`c4fsk9600` dies at a 50 Hz corner. The rig is at 70. It never had a chance. `c4fsk19200` runs at
twice the symbol rate, so it has half the low-frequency content and tolerates twice the corner,
which is a prediction of the mechanism and it came out right. The binary control tolerates four to
six times as much, because a 2-level eye has three times the margin.

Through a filter fitted to the fifteen measured points above, `c4fsk9600` and `c4fsk19200` both
deliver 0 of 40. Split the fit in two: the LOW end on its own delivers 0 of 40 and the TOP end on
its own delivers 40 of 40 for every mode. The damage is entirely at the bottom of the band.

**Confirmed on the real audio.** Applying the matching inverse (two first-order sections at 50 Hz)
to a real off-air `c4fsk9600` burst lifts the eye from 0.193 to 0.160 and the sync correlation
from 0.736 to 0.848. Real, and only partial, which is consistent with the probe's finding that a
one-tap decision-directed DC loop recovers about one rung of corner frequency and is roughly a
factor of two short of what this rig needs.

**The preamble is the tell.** It is a pure outer-level alternation at exactly half the symbol
rate, so it is the one pattern in the waveform with no low-frequency content at all, and it
arrives perfect: a clean 2400 Hz sine, outer-symbol spread 1.6 % of the half-swing (36 dB),
2 % third harmonic, phase jitter 0.75 % of a symbol. Everything after it, which does have
low-frequency content, is mangled. That rules out noise, jitter, level and clipping in one
measurement, and it is why the received signal can look healthy on a spectrum display and carry
nothing.

## Fault 3, minor: the envelope tracker can run away

`TrackEnvelope` has no guard stopping `_peakHigh` crossing below `_peakLow`. Once it does,
`half` clamps to 1e-6, the normalised slicer input rails and every decision goes to an outer
level for the rest of the burst. Running the real modem over a real off-air burst with the gate
forced open, `peakHigh` went +0.23 -> +0.08 -> -3.1 -> -43 -> -33173 and the half-swing went
negative, about 4 s into an 8 s burst. It is a consequence of a bad eye rather than a cause of
one, but it turns a degraded burst into an unrecoverable one.

## The binary control: fsk9600 now works on air

`fsk9600` had never been proven on air. It is now, on this rig, 20 frames per rung, scored from
both stations' own journals:

| | 32 B | 64 B | 128 B | 256 B | 512 B |
|---|---|---|---|---|---|
| radio2 -> radio1 | 100 % | 95 % | 80 % | 35 % | 10 % |
| radio1 -> radio2 | 100 % | 85 % | 80 % | 35 % | 5 % |

`c4fsk9600` on the same rig in the same session is 0 of 20 at 8, 16, 32, 64 and 256 bytes.

The falling curve is the same wander: a longer frame has more chances to hit a run of same-sign
symbols long enough to drag the baseline across the slicer's threshold. So the coupling network is
costing the binary mode most of its long frames too. **Nothing that feeds a radio's 9600 baud
socket is going to work properly through this interface until the coupling corners come down**,
and that is a hardware finding as much as a software one.

## What the first pass got wrong, and why

It reported the receive path as 23 dB down at 4 kHz and 29 dB down at 4.8 kHz, and built the
whole hypothesis on that. Those figures are the **transmitted waveform's own spectrum**, measured
through a receiver, not the path's response. A shaped 4800 sym/s 4-PAM signal is 17.6 dB down at
4 kHz by construction and has a spectral NULL at 4.8 kHz, which is the symbol rate. Normalise the
received burst against a clean transmission of the same mode instead of against its own peak and
the path comes out flat to within 2 dB from 100 Hz to 4 kHz, which the tone sweep above then
confirmed directly.

The lesson is narrow and worth keeping: **a received signal's spectrum is not a measurement of the
path.** To measure a path, put a known signal through it.

`c4fsk9600` does not need usable energy at 4.8 kHz. It needs it at 30 Hz.

## Traps

The first pass's four traps all still stand and are still worth reading:

**An envelope detector cannot find a narrowband signal in a wideband capture.** A 25 kHz signal in
a 2 MHz capture moves total power by a fraction of a dB. Use a spectrogram and track power in the
signal's own band; `~/ofdm-fm-campaign/narrowband.py` does this.

**Do not tune the dial onto the signal.** A direct-conversion receiver puts its LO leakage at
exactly 0 Hz. Tune a few hundred kHz off and mask DC.

**An FM receiver goes QUIET when a carrier arrives.** `IChannelBusySource` documents this, the
first pass walked into it as an instrument problem, and fault 1 above is the same fact walked into
by the modem itself.

**`bulk.py --bytes 1900` is silently refused by C4FSK.** IL2P's byte count is 10 bits, so 1023
payload bytes maximum. Use 1007 or below.

And these are new:

**`journalctl --since` on the Pi reads LOCAL time.** Handing it a UTC timestamp from the dev box
silently widens the window by an hour, so a per-rung frame count comes out cumulative over the
whole session. Take the boundary from the Pi itself.

**A filter under test must be allowed to run past the end of the burst.** A probe whose filter
output was the same length as its input pushed the last few milliseconds off the end, and the end
of the burst is the IL2P trailer, so a filter that did nothing but delay the audio scored 0 of 40.
Keep a "delay only" control column permanently: if it is ever not full marks, the rig is lying.

**Wind the transmit level down and the eye does not change, because the clipping is upstream of
the volume control.** `Pcm16.FromFloat` clamps at full scale before ALSA's mixer attenuates, so a
modulator that emits above 1.0 clips identically at every mixer setting. The deployed build
(0.73.0, `ofdmfm.51932aa`) emits `c4fsk9600` with its outer level at 1.035 of full scale and peaks
at 1.056, so 2.3 % of its samples are clipped; #516 takes that to 0.4 and fixes it. Measured as an
eye, the clipping costs little here, but it is exactly the thing `FrameLevelLimits.ClipSensitive`
exists to prevent and the stations need updating.

**The RSP1 heard nothing at IFGR 40 to 45 with RFGR 0** in this session, peak |x| 0.011 across a
25 s capture with a transmitter a few metres away. Whatever it was doing for the first pass it was
not doing today; do not plan an experiment that depends on it without checking it hears the
carrier first.

## How to measure this rig, quickly

The end-to-end audio response, which is the measurement the first pass needed and did not have:

```sh
# step a tone through the transmitter; 50 Hz is the daemon's floor
curl -s -XPOST -H "X-API-Key: $KEY" -H 'Content-Type: application/json' \
     -d '{"twoTone": false, "toneHz": 200, "seconds": 4}' http://radio2:8107/api/txtest
# then read the tone's level out of the receiving station's own capture
ssh pi@radio1 'sudo cat /var/lib/pdn-soundmodem/raw/raw-<UTC>.wav' > rx.wav
```

The scripts that did all of the above live in this session's scratch and are not in the repo; the
useful ones are a stepped-tone sweep with a coherent per-tone level read, a keyed-stretch finder
that works off the collapse of 9 to 15 kHz hiss rather than off total level, a sync-word
correlator, and a least-squares fit of the received waveform to a 4-PAM symbol sequence. The last
one is the instrument that settles "is the eye really shut": the fitted amplitudes came out as a
flat continuum off air against four spikes on a clean transmission.

## What to do

1. **Fix the gate** (fault 1). It is the blocker, it is a real defect on every FM station, and
   nothing else can be tested until it is done.
2. **Guard the envelope tracker** (fault 3). Small and clearly right.
3. **Decide what `c4fsk9600` is for.** Through this interface it cannot work: it needs a corner
   below 30 Hz and the interface gives 70. Either the coupling capacitors go up (which would also
   give `fsk9600` back its long frames), or the receiver grows DC restoration with more reach than
   a one-tap loop, or the mode is documented as needing a DC-coupled 9600 socket. `c4fsk19200`
   tolerates 100 Hz and is the one to try first on the rig as it stands.
4. **Update the stations.** They are on a build that predates #516 and transmits above full scale.

## Leave the rig as you found it

Both stations on `ofdm-fm-8k`, transmit volume 0 dB. `setmode.py` is non-persisting so a restart
returns a station to `/etc/pdn-soundmodem/soundmodem.json` by itself, but the mixer is not: set it
back explicitly and check it with a bulk run before you walk away.
