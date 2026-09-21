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

### Confirmed against the NinoTNC reference capture, and why it is not a one-line removal

Run over `ninorx.wav`, 45 s holding 15 real NinoTNC C4FSK 19k2 transmissions, the shipped modem's
gate **opened zero times**. Not "rarely", not "late": never. `sm-c4fskprobe` prints it in its
first section and then stops, because nothing downstream of it can mean anything.

`--lead-in` does not rescue that recording either, and the reason is worth knowing. It seeds the
floor from digital silence so the gate opens on the idle hiss, but the floor then adapts up and
the gate falls at 6.78 s, which is 3.4 s **before** the first transmission at 10.19 s. On this
path the gate is open exactly when the channel is empty and shut exactly when it is not.

**Removing the gate is not the fix, and the measurement says so both ways.** Bypassed entirely
over the same file:

- the idle hiss produces **63 near-sync matches in timing phase 0 alone** (Hamming distance 1,
  inverted), 59 in phase 1, and so on. That is the false-lock the gate exists to prevent, and it
  is real rather than historical.
- **79 % of all decisions read `+inner`** and 14 % of normalised values sit below -1.5, which is
  the slicer railing rather than slicing.
- no frame decodes, so the gate is not the only thing between this receiver and the air.

The railing has a structural cause that any replacement has to deal with: **the gate is what
re-arms the envelope acquisition.** `_symbolsSinceGate` is reset on the gate's rising edge and the
max-hold acquisition runs only for `AcquireSymbols` after it, with `TrackEnvelope`'s
decision-directed update carrying the envelope from there. With no gate, `_symbolsSinceGate` never
resets, the acquisition runs once at the very start of the stream and never again, and the tracker
spends the rest of the file on whatever it learned from the first few milliseconds. The deframer
reset on the falling edge hangs off the same signal.

So a replacement owes three things, not one: a signal-present test that is true during an FM burst,
a re-arm for the envelope acquisition, and a reset for the deframer. The test itself should be a
ratio rather than a level - see `docs/dev/carrier-sense.md`, where the same mistake was made twice
at station level before it was fixed.

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

| Hz | 50 | 70 | 100 | 140 | 200 | 280 | 400 | 560 | 800 | 1130 | 1600 | 2260 | 3200 | 4500 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| dB | -6.9 | -3.2 | -1.7 | -0.9 | -0.4 | -0.1 | +0.1 | +0.1 | +0.1 | 0.0 | -0.1 | -0.3 | -0.8 | -0.9 |

| Hz | 6400 | 7500 | 8500 | 9600 | 11000 | 12500 | 14000 |
|---|---|---|---|---|---|---|---|
| dB | -3.4 | -6.7 | -10.8 | -16.2 | -28.0 | -49.5 | -58.3 |

The top half agrees closely with an independent measurement of the same rig two days earlier
through the OFDM-FM carriers (-3 dB at 6.3 kHz, -6 at 7.4, -8 at 8, -18.7 at 10 kHz), so both
instruments are sound.

Flat from 280 Hz to 4.5 kHz, high-passing below that, 3 dB down at about 70 Hz, and 3 dB down
again at 6.4 kHz. That is the
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

**`c4fsk19200` is not the way out, though, and I was wrong to suggest it might be.** It tolerates
a 70 Hz corner and the rig is AT 70, so it has nothing in hand at the bottom, and unlike
`c4fsk9600` it is also squeezed at the top: at 9600 sym/s its band reaches to 9.6 kHz, where this
path is 16 dB down, and it is already 11 dB down at 8.5 kHz. Measured on air today, with the gate
forced open on a real `c4fsk19200` burst, the eye is 0.186, no better than `c4fsk9600`'s 0.193,
and nothing decodes. So the top-of-band rolloff the first pass reached for is real and does matter
- just for the 19200 mode, which was not the mode it was measuring.

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

## Where the damage happens: both halves, measured against a reference TNC

**This section replaces two earlier attempts in this document's history, both of which were
wrong.** The measurement that settles it is a NinoTNC in C4FSK 19k2 (mode 1) put on radio2 in
place of its CM108 on 2026-09-21, transmitting through the same Tait, captured on the same SDR
and received by the same station. A reference implementation through the same radio is the
control that my own transmissions could never be.

One instrument throughout: the symbol-instant values fitted to four levels by k-means, scored as
the within-cluster rms as a fraction of the half-swing. 0.02 is a healthy eye; the inner and outer
levels are 1/3 of the half-swing apart, so the decision margin is about 0.11; 0.19 is shut.

| | eye |
|---|---|
| our modulator's own float samples, clipped at full scale as the card would | **0.021** |
| **our station as radiated** (`c4fsk9600`) | **0.191** |
| **NinoTNC as radiated** (`c4fsk19200`) | **0.080** |
| NinoTNC as received by radio1 | **0.151** |
| our station as received by radio1 | **0.19** |

Two separate defects, both ours, and the reference separates them:

1. **Our transmit chain wrecks the signal before it reaches the antenna.** A reference TNC through
   the same radio radiates 0.080 with textbook levels (-1.000, -0.305, +0.334, +1.000) and even
   occupancy (26/24/25/26 %). We radiate 0.191 from samples that measure 0.021 when they leave the
   modem, with skewed levels (-1.000, -0.371, +0.259, +1.000) and occupancy (17/26/35/22 %). The
   only things in that span are the CM108's DAC and its output coupling capacitor, the interface's
   transmit tail (Rt, Rb, C4) and the Tait's T12 input.
2. **Our receive chain then roughly doubles the spread again**, taking the NinoTNC's clean 0.080
   to 0.151. That is why radio1 decoded **0 of 15** of the NinoTNC's frames, and why it still read
   nothing when the energy gate was forced open in a probe. Fixing the gate alone would not have
   read a reference transmitter.

So the modes are not failing for one reason. The gate stops anything at all being attempted; the
transmit chain radiates an eye a reference TNC does not; and the receive chain cannot read a clean
one when it gets it.

**What the reference also tells us about acquisition.** The NinoTNC's bursts are 131 ms end to end
for a 272-byte frame, and the preamble alternation occupies about one 5 ms block at the front. So
a real peer acquires from roughly 5 ms of run-in, which is the figure our receiver has to live
with, and it is far below the 20 ms floor `Modulate` imposes on our own transmissions.

**Deviation as radiated**, with the discriminator low-passed before decimation: the NinoTNC at its
pot's midpoint measures **4.85 and 4.89 kHz** peak (99.9th percentile) across two bursts against
the 5.0 kHz `c4fsk19200` is specified at, so a correct station is within 3 % of the published
figure. Ours measures about 6.8 kHz against `c4fsk9600`'s published 2.5 kHz, which is fault 4 seen
from the air.

## Two instrument bugs of mine, because they nearly cost the diagnosis

Both were in my SDR demodulation, and both made the radiated eye look worse than it was:

- **No anti-alias filter before decimation.** A 1 Msps discriminator output subsampled to audio
  rate folds 20 kHz of noise into the band. Low-pass the discriminator output at the modem's own
  receive bandwidth first.
- **1 Msps decimated by 20 is 50 kHz, not 48.** I wrote 48000 into the WAV header, which stretched
  the time base by 4.2 %, put the preamble tone at 2303 Hz instead of 2400, and smeared the eye by
  a whole symbol every 25. The give-away is that the preamble is a tone at exactly half the symbol
  rate: if it does not read 2400 Hz (or 4800 for the 19200 mode), the time base is wrong and
  nothing downstream means anything.

Between them these produced a confident "the damage is all on the transmit side" reading, then a
retraction, then the reference TNC showing that the transmit side really is the worse half after
all. **Measure the preamble tone frequency first, every time, as the instrument's own check.**

## What is still not explained, and I would rather you knew

The reference TNC above says WHERE the damage happens. It does not say what mechanism does it, and
the low-frequency coupling loss is only part of the answer. It is a real part: the measured
response on its own takes both C4FSK modes from 40 of 40 to 0 of 40 in the probe, and undoing it
on real off-air audio is a real improvement. But it does not account for all of the measured
damage, and the part it does not account for is not linear. Three measurements say so, and they
are the loose end:

- **Undoing the measured low-frequency loss on real audio recovers about a third of it.** A cascade
  inverse (two first-order sections at 50 Hz was the best of the shapes tried) lifts the eye from
  0.193 to 0.160 and the sync correlation from 0.736 to 0.848. A clean transmission measures 0.032
  on the same instrument, so most of the gap is still there.
- **A linear model of the path, fitted from the sync word, does not explain the received sync
  word.** Averaging 14 copies of the known preamble-tail-plus-sync out of one keyup gives a
  noise-free waveform (0.83 % residual across copies), and a least-squares impulse response fitted
  to it leaves **7.3 %** unexplained. The same fit on a clean transmission leaves 0.28 %.
- **The post-cursor that fit does find is not what closes the eye.** It reads +1: -0.167,
  +2: -0.080 symbol-spaced, against +1: -0.026 on a clean transmission, which looks like exactly
  the pattern-dependent ISI the 5-tap equalizer exists to remove. Applying its exact inverse to the
  real symbol samples moves the eye from 0.193 to 0.191, which is nothing. Neither does a
  fractionally spaced MMSE equaliser built from the same estimate, at any span from 3 to 21
  symbols, nor a decision-directed least-squares fit.

So the channel behaves differently under the preamble and sync (all outer symbols, and in the
preamble's case a single tone) than it does under data, which is what a non-linearity looks like.
The tone sweep's own harmonic content points the same way: distortion products sit 45 dB down at
50 to 140 Hz and rise to **26 dB down between 2.2 and 6.4 kHz**, and the received preamble carries
2 % third harmonic where the transmitted one carries 0.4 %. Five per cent of distortion against a
4-PAM margin of 33 % is a sixth of the eye, which matters here and would be invisible anywhere else
in the tree.

What it is not: it is not level (identical from 0 to -18 dB transmit, and the clipping that does
occur is upstream of the mixer so the sweep could not have moved it), not noise or jitter (the
all-outer preamble arrives with a 1.6 % spread and 0.75 % of a symbol of jitter), not the symbol
rate (swept plus or minus 600 ppm, flat), and not the top of the band for `c4fsk9600`.

Whoever picks this up: the reference TNC has done the hard half of the separation already, so the
remaining instrument is a scope. Compare the CM108's analogue output against the float samples the
daemon wrote, then the same at the far end of the interface tail, then at the Tait's T12 pin. That
splits the transmit half's three suspects and needs no radio time. For the receive half, repeat the
NinoTNC transmission and take radio1's audio at the discriminator directly rather than through its
interface.

A two-tone intermodulation measurement at a few points across the band would also help, and is now
easy: the daemon's own `POST /api/txtest` does a two-tone burst, and the SDR reads it.

## Fault 3, minor: the envelope tracker can run away

`TrackEnvelope` has no guard stopping `_peakHigh` crossing below `_peakLow`. Once it does,
`half` clamps to 1e-6, the normalised slicer input rails and every decision goes to an outer
level for the rest of the burst. Running the real modem over a real off-air burst with the gate
forced open, `peakHigh` went +0.23 -> +0.08 -> -3.1 -> -43 -> -33173 and the half-swing went
negative, about 4 s into an 8 s burst. It is a consequence of a bad eye rather than a cause of
one, but it turns a degraded burst into an unrecoverable one.

## Fault 4: the modulator emits above full scale, and #516 did not change the two modes' ratio

Both C4FSK modes clip in the modulator, on `main` today, before any station setting is applied.
Measured straight out of `C4fskModem.Modulate` at 48 kHz with a 200-byte frame and 250 ms of
TXDELAY:

| mode | peak | samples over full scale |
|---|---|---|
| `c4fsk9600` | 1.0562 | 1265 of 21608 (5.9 %) |
| `c4fsk19200` | 1.0261 | 26 of 16828 (0.2 %) |

`HeadroomFraction` is 0.8 and the pulse shaper overshoots the symbol amplitude by up to 32 %, so
0.8 is not enough headroom; 0.75 would be. As an eye this costs little here (clipping the real
modulator output at full scale moved the measurement from 0.032 to 0.033), but this is the one
amplitude-coded mode in the tree, `FrameLevels` is `ClipSensitive`, and the code comment beside
the scaler says in terms that clipping compresses the outer levels into the inner ones and no
envelope tracker downstream can undo it.

**Separately, #516's per-mode deviation scaling is a no-op for these two modes.** It computes
`HeadroomFraction * PeakDeviationHz / FullDeviationHz(ChannelSpacingHz)` from the mode's own
profile, and both C4FSK modes sit at exactly 100 % of the channel class they declare:
`c4fsk9600` is 2500 Hz in `Narrow` (full deviation 2500) and `c4fsk19200` is 5000 Hz in `Wide`
(full deviation 5000). Both therefore come out at 0.800, which is where they were before, and the
1:2 ratio the change set out to create is still 1:1. The handover's own measurements are the
symptom and they still hold: on one 25 kHz channel, `c4fsk9600` measured 6.92 kHz peak deviation
and `c4fsk19200` 6.73 kHz, which is to say the same. Normalising each mode against its own nominal
channel cancels out; the divisor has to be a single reference the station actually transmits on.

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
modulator that emits above 1.0 clips identically at every mixer setting, and no amount of turning
the transmit volume down will show you it is happening. See fault 4 below, which is where that
observation led.

**The RSP1's gain recipe in the first pass is wrong for this path.** At IFGR 40 to 45 with RFGR 0
it heard nothing at all: peak |x| 0.011 across a 25 s capture with a transmitter a few metres away.
At **IFGR 20, RFGR 0** the same carrier is 54 dB over the noise. Check it hears the carrier before
planning anything that depends on it.

**Decimating a 1 Msps discriminator output straight to 48 kHz folds 20 kHz of noise into the audio
band and closes the eye by itself.** Low-pass the discriminator output at the modem's own receive
bandwidth BEFORE subsampling. My first reading of the radiated eye was made without that and was
not trustworthy; it happened to give nearly the same answer, which is luck, not method.

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
3. **Fix the transmit chain first, because the reference TNC says it is the worse half.** A scope
   on three points splits it, and it needs no radio time. Then re-measure the receive half against
   a NinoTNC transmission, which is now a repeatable reference.
4. **Accept that neither C4FSK mode can work through this interface as built**, and decide which
   way out to take. `c4fsk9600` needs a corner below 30 Hz against the 70 it gets; `c4fsk19200`
   has nothing in hand at either end and measured no better on air. Either the coupling corners
   come down (which would also give `fsk9600` back its long frames, so it pays for itself), or the
   receiver grows DC restoration with more reach than a one-tap loop, or these modes are
   documented as needing a properly DC-coupled 9600 baud socket. Fixing the gate is still worth
   doing first, because until it is fixed nothing downstream of it can be tested at all.
5. **Give the modulator real headroom and make #516's ratio actually apply** (fault 4). Both are
   in `C4fskModem.Modulate` and neither needs a radio to verify.

## Leave the rig as you found it

Both stations on `ofdm-fm-8k`, transmit volume 0 dB. `setmode.py` is non-persisting so a restart
returns a station to `/etc/pdn-soundmodem/soundmodem.json` by itself, but the mixer is not: set it
back explicitly and check it with a bulk run before you walk away.
