# Below the FM threshold: a mode for the stations the network cannot reach

Status: design record as of 2026-09-30. Describes what an FM link can carry at the lowest
carrier-to-noise ratio, measured through the FM link model, and the mode that would be built on it.
The measurements are from `prototypes/lowsnr-fm/`, which runs against M0LTE.FmChannel at the
commit that added its IF tap (`ApplyToIf`, branch `claude/low-snr-fm-packet-modes-xnscal`), not
against the 0.7.0 package. No mode exists, nothing is in the catalogue, and nothing here has been
on air. The phases and gates at the end are what would be built if it were taken up.

**Charter.** Get frames through an FM radio at a carrier-to-noise ratio far below the one where
its discriminator stops working, so that a station whose path to the network is 20 dB short of what
every existing FM mode needs can still join it, slowly. The station assumed throughout is the one
this repository is built around: a Tait TM8100 tapped at R1 and T13 through a CM108 interface with
the passive coupling of [docs/hardware/tait-tm8100-cm108.md](../../hardware/tait-tm8100-cm108.md),
or the same radio with an SDR on its IF. Microphone and speaker paths are measured once, for the
record, and not designed for.

**The claim, in one paragraph.** Every FM data mode this repository carries, including the OFDM-FM
floor at +5.8 dB CNR, is stopped by the same wall: the limiter-discriminator's threshold. Below it
the discriminator hands up the signal shrunk by `(1 - exp(-cnr))` and buried in clicks, and no
waveform or code run on that audio can get more than about 12 dB below the wall. But the clicks are
whole turns of phase, and a receiver that integrates the discriminator's output back into a phase
and detects there sees them as nothing. Measured: one slow audio-tone MFSK waveform, injected at
T13, decodes LDPC-coded frames 8 of 8 at -20 dB CNR from an SDR on the IF at 15 bit/s, and 8 of 8 at
-22 dB at 7.5 bit/s; from the R1 tap through a CM108, once the interface's AC coupling has been
undone, 7 of 8 at -18 dB. That is 24 to 28 dB below the best FM floor in this repository, and it is
within about 3 dB of FT8's efficiency per bit, through an FM transmitter. The one thing between the
R1 tap and the SDR's number is the coupling, and it is a calibration, not a capacitor.

---

## 1. Where the state of the art sits

Everything below is carrier-to-noise in the receiver's IF bandwidth, which is where an FM threshold
is defined and the only convention the FM link model uses. It is not SNR3k, and nothing here is
convertible to it without running the link.

| FM mode | Net rate | 50 % knee, this repository | Where measured |
|---|---|---|---|
| `afsk1200-il2p` | 1200 bit/s | about +8 dB (mic), +7.5 dB (data port) | [mode-validation.md](../mode-validation.md), 2026-08 ladders |
| `fsk9600-il2p` | 9600 bit/s | about +10.5 dB (25 kHz data port) | same |
| `ofdm-fm` BPSK 1/2 | 1076 bit/s | **+5.8 dB**, the lowest anywhere here | [ofdm-fm/receiver-findings.md](../ofdm-fm/receiver-findings.md) rate ladder |
| `ofdm-fm` QPSK 1/2 | 1964 bit/s | +6.3 dB | same |

The three bottom OFDM-FM rungs sit within 0.5 dB of each other and the findings call them
fade-limited rather than noise-limited. They are threshold-limited: BPSK, QPSK and their code rates
all die where the discriminator does, and no rung has ever been measured below +4.3 dB because none
decodes there.

Outside this repository the nearest things are the SSB weak-signal modes, which do not meet a
discriminator at all: FT8 decodes at -21 dB SNR in 2.5 kHz for 77 bits in 12.6 s, and FreeDV
datac14 at about -12 dB SNR3k for 58 bit/s. Running one of those through an FM radio as audio does
not carry their sensitivity across, because the FM radio's threshold sits in front of them: the mode
never sees a signal below about +5 dB CNR that is not mostly clicks.

## 2. What an FM receiver does to a weak signal

The link model frequency-modulates a carrier, adds complex noise at the stated ratio in the IF
bandwidth, filters, and takes the angle between successive samples: a limiter and a discriminator.
Three things follow from that construction, and all three were checked against it rather than
assumed. The click model is Rice's (S. O. Rice, *Noise in FM receivers*, 1963), which every FM
textbook carries.

**Signal suppression.** With the carrier at a frequency offset `d` from the IF centre and a ratio
`cnr` (linear), the discriminator's mean output is `d * (1 - exp(-cnr))`. At 0 dB the signal comes
out at 63 % of its amplitude, at -10 dB at 9.5 %, and the power goes as `cnr^2` from there. This is
not a filter that can be equalised: it is the phase following the noise whenever the noise is the
bigger vector, and the fraction of the time that is true.

**Click noise.** Whenever the resultant of carrier and noise circles the origin the phase slips a
whole turn, and the discriminator emits an impulse of area 2 pi. Their rate is
`N = (B / sqrt(3)) * erfc(sqrt(cnr))`, so on a 9.75 kHz IF about 0.04 per second at +10 dB, 55 at
+5, 730 at 0, 2100 at -4 and 3000 at -10 dB. Each is white in the audio, with a density of exactly
`N` Hz^2 per Hz of audio bandwidth, and below about +6 dB they outweigh the Gaussian part by orders
of magnitude. So below the threshold the audio is the shrunken signal plus white noise whose power
is the click rate.

**What a detector on the audio can therefore do**, for a tone of deviation `D` over a symbol of
length `T`: output SNR is about `D^2 T (1 - exp(-cnr))^2 / (2 N)`. At -4 dB with 2.5 kHz deviation
and 100 ms symbols that is 12 dB, which is why the plain detector still copies there; at -10 dB it
is 10 dB, at -15 dB it is -1 dB, and each doubling of `T` buys only 3 dB against a term that is
falling 20 dB per 10 dB of ratio. The audio path bottoms out somewhere around -8 dB, and no
waveform or code run on that audio moves it far, because the signal has been multiplied by `cnr^2`
before the code sees it. OFDM-FM's floor is 14 dB above this, and that gap is a modem question;
the rest is not.

**What a detector on the phase can do.** The clicks are exact turns. A receiver that integrates the
discriminator output back into a phase and takes `exp(j theta)` has reconstructed the limiter's
output: a unit-amplitude copy of the IF signal in which each click is a full rotation that leaves
the phase where it was. The classic result for detecting a weak signal after a hard limiter is a loss
of pi/4, about 1 dB, against the unlimited signal, with no threshold at all. The suppression by
`(1 - exp(-cnr))` is gone because it was never in the phase: it was the clicks' asymmetry, and the
clicks have been folded away.

That is the whole idea. Everything else here is measuring how much of it survives a real audio path.

## 3. What was measured

### 3.1 One waveform, three receivers

The transmit side is the same in every case and could not be simpler: a burst of audio tones, one
tone per symbol, continuous phase, unit amplitude, into the microphone or data socket of any FM
radio. 8-ary FSK on tones at 1200 to 1270 Hz, 10 Hz apart, 100 ms per symbol, at 2500 Hz peak
deviation (100 % of a 12.5 kHz channel), so the modulation index is about 2 and the RF spectrum is
the carrier plus sidebands at plus and minus the tone, plus and minus twice it, and little beyond.
A single tone at full deviation is the friendliest waveform a limiter and a pre-emphasis network can
be given: constant envelope in the audio as well as at RF, nothing to clip, nothing to tilt.

Three receivers were run on every burst:

- **A, audio.** Energy at each tone in the discriminator audio, per symbol. This is what feeding
  FT8 or any other audio mode through an FM radio does.
- **P, phase.** The discriminator audio integrated to a phase, `exp(j theta)`, then the energy of
  the sideband pattern each tone would produce (the lines at plus and minus one and two times the
  tone), per symbol, with a search over a common frequency offset for the burst.
- **S, SDR.** The same sideband receiver run on the IF complex envelope from `ApplyToIf`: an SDR
  tapped ahead of the limiter, with the carrier at unit amplitude and the noise at the stated ratio.

Timing is a genie's in all three (found once at infinite CNR per receiver and reused), so these are
detection floors, not acquisition floors. Section 5 says what acquisition will cost. The link is the
`TaitTm8100.Link(Narrow, 2500)` profile unless stated: R1 and T13, flat, 9.75 kHz IF at -6 dB,
audio to 4.9 kHz, no emphasis, no limiter. Four seeds of 64 symbols per point.

### 3.2 The floors

Symbol error rate, 8-FSK, DC-coupled flat tap:

| CNR dB | A audio | P phase | S sdr |
|---|---|---|---|
| 0 | 0.000 | 0.000 | 0.000 |
| -4 | 0.000 | 0.000 | 0.000 |
| -8 | 0.289 | 0.000 | 0.000 |
| -10 | 0.578 | 0.000 | 0.000 |
| -12 | 0.750 | 0.000 | 0.000 |
| -14 | 0.867 | 0.000 | 0.000 |
| -16 | 0.867 | 0.008 | 0.000 |
| -18 | 0.891 | 0.051 | 0.020 |
| -20 | 0.902 | 0.207 | 0.113 |
| -22 | 0.895 | 0.398 | 0.246 |
| -24 | 0.910 | 0.523 | 0.492 |

Three things to read off it. The audio receiver dies between -6 and -10 dB, exactly where the
`cnr^2` arithmetic of section 2 says it must, and already 14 dB below the OFDM-FM floor. The phase
receiver and the SDR are within a decibel of each other all the way down, which is the pi/4 result
holding through a real IF filter and a real audio low-pass. And the SDR's numbers are what theory
gives an orthogonal 8-FSK detector at this symbol rate: at -20 dB the energy per symbol over the
noise density is `cnr * B * T`, 9.9 dB, of which the four sideband lines carry 92 % and pay about
1.5 dB of non-coherent combining loss for being four lines rather than one.

A 250 Hz carrier error on the SDR path (`--cfo 250`) was found to 0.2 Hz by the burst-level search
and cost nothing measurable at -8, -16 or -20 dB.

### 3.3 The audio path is not the limit; its coupling is

The phase receiver needs the discriminator output DC-coupled, and this was found the hard way. The
first version removed the burst's mean before integrating, which any AC-coupled input does
implicitly, and it failed below 0 dB with a symbol error rate of 1.0: not chance, systematically
wrong. The reason is a number worth keeping.

Below threshold the phase is a random walk: `N` clicks a second, each a whole turn, in either
direction. Its frequency (the discriminator output) therefore has spectral density `N` all the way
down to DC, and a high-pass at corner `f_c` removes `N * f_c` Hz^2 of it. What is left after
`exp(j theta)` is a frequency error wandering with an rms of `sqrt(N * f_c)` hertz on a timescale of
`1 / f_c`. At -4 dB and a 70 Hz corner, which is where a CM108 dongle's microphone coupling puts it
on a flat R1 tap ([hardware/tm8100-cm108-interface-notes.md](../hardware/tm8100-cm108-interface-notes.md)),
that is 380 Hz of wander against tones 10 Hz apart. At a 1 Hz corner it is still 45 Hz. Measured:

| Coupling | P at 0 dB | -4 | -8 | -12 | -16 |
|---|---|---|---|---|---|
| DC | 0.000 | 0.000 | 0.000 | 0.000 | 0.008 |
| 70 Hz one-pole, as is | 0.758 | 0.816 | 0.871 | | |
| 5 Hz | 0.719 | 0.813 | 0.844 | 0.859 | 0.863 |
| 1 Hz | 0.766 | 0.781 | 0.797 | 0.871 | 0.840 |
| 1 Hz, offset tracked per symbol | 0.641 | 0.645 | 0.676 | 0.742 | 0.805 |
| 70 Hz, undone at 70 Hz, tracked | 0.000 | 0.000 | 0.000 | 0.000 | 0.012 |
| 70 Hz, undone at 69 or 71 Hz, tracked | | 0.000 to 0.004 | 0.004 | 0.012 to 0.016 | |
| 70 Hz, undone at 65 Hz, tracked | 0.305 | 0.383 | 0.500 | 0.551 | 0.777 |

So: a bigger capacitor does not fix it, and following the wander symbol by symbol does not fix it,
because at any corner the wander within one symbol is already too fast. What fixes it is undoing
the coupling exactly. A one-pole high-pass has an exact inverse bar its DC (add back the corner
times a running integral), and with that inverse at the true corner, and the integral leaking at
0.005 Hz rather than the 0.2 Hz first tried (0.2 Hz left a 9 Hz wander of its own at +4 dB, which
was enough), the phase receiver is back on top of the SDR. The corner has to be known to about
1 to 2 %: 69 and 71 Hz against a true 70 both hold to -12 dB, 65 does not. The interface notes say
the dongle's corner is not one component and that a single pole at 90 Hz and four poles at 35 Hz fit
the same fifteen points, so the inverse will have to be *found* rather than looked up: a search over
the corner (and the pole count) that maximises the sideband receiver's own metric on a burst above
threshold is a one-off calibration per station, in the same family as the offset search, and the
receiver already has the metric. Or the input is DC-coupled: R1 is a 2.3 V pedestal that moves two
thirds of a volt per 2 kHz of frequency error, which an ADC that is not a sound card takes in its
stride.

Through a microphone-and-speaker path the phase receiver is dead below 0 dB and stays dead: the
300 Hz high-pass and the de-emphasis both stand between the discriminator and the socket, and no
inverse is available for a network whose time constant no radio publishes. That path gets the audio
receiver and nothing better, and the audio receiver gives it -6 dB.

### 3.4 The microphone-and-speaker path, for the record only

Not a configuration this plan designs for; measured once so the number exists.
`FmLinkProfile.MicAndSpeaker(2500)`: 300 to 3000 Hz both ways, 750 us emphasis both ways, 8 kHz IF.

| CNR dB | A audio | P phase |
|---|---|---|
| +4 | 0.000 | 0.000 |
| 0 | 0.000 | 0.000 |
| -4 | 0.000 | 0.449 |
| -6 | 0.027 | 0.816 |
| -8 | 0.273 | 0.906 |
| -10 | 0.574 | 0.895 |
| -12 | 0.730 | 0.855 |

An unmodified handheld would copy this waveform to about -6 dB (coded frames 8 of 8 at -6, 0 of 8
at -8): 12 dB below the OFDM-FM floor, with a receiver that is a Goertzel filter. It is a free
by-product of the waveform, not a reason for it.

### 3.5 Coded frames

One LDPC codeword per burst, codec2's `H_256_512_4` from M0LTE.FecLdpc as OFDM-FM already uses it:
256 payload bits, 512 on air, 171 symbols, 17.1 s, 15 bit/s net. Bit LLRs are max-log over the
tone energies, scaled by the burst's median tone energy (a noise-only line at any ratio worth
decoding at) and clipped, which took one iteration to learn: unclipped LLRs of a few hundred make
the sum-product decoder fail on a codeword with no symbol errors at all. Eight frames per point.

Frames of 8, R1/T13 Tait narrow profile unless stated:

| Symbol | Net rate | CNR dB | A audio | P phase, DC coupled | P phase, 70 Hz coupling undone | S sdr |
|---|---|---|---|---|---|---|
| 50 ms | 30 bit/s | -12 | 0 | 8 | | 8 |
| 50 ms | 30 bit/s | -14 | 0 | 8 | | 8 |
| 50 ms | 30 bit/s | -16 | 0 | 8 | | 8 |
| 50 ms | 30 bit/s | -18 | 0 | 0 | | 3 |
| 100 ms | 15 bit/s | -16 | 0 | 8 | 8 | 8 |
| 100 ms | 15 bit/s | -18 | 0 | 8 | 7 | 8 |
| 100 ms | 15 bit/s | -20 | 0 | 4 | 0 | 8 |
| 100 ms | 15 bit/s | -22 | 0 | 0 | 0 | 0 |
| 200 ms | 7.5 bit/s | -20 | 0 | 8 | | 8 |
| 200 ms | 7.5 bit/s | -22 | 0 | 8 | | 8 |
| 200 ms | 7.5 bit/s | -24 | 0 | 1 | | 7 |
| 200 ms | 7.5 bit/s | -26 | 0 | 0 | | 0 |

With the SDR's carrier 250 Hz off (`--cfo 250`) the 100 ms rows are identical: 8, 8, 0 at -18,
-20, -22. The code's cliff sits where the symbol error rate passes about 12 % for the SDR and
about 6 % for the phase receiver, whose errors bunch: the audio-path receiver's residual is not
white in time the way the SDR's is, and that is worth a look when the LLRs are done properly. Each
doubling of the symbol moves the SDR's cliff by the 3 dB it should, so the rate is a dial: 30 bit/s
at -16, 15 at -20, 7.5 at -22, and nothing stops 3.75 at -25 except the oscillators.

The decoder is fussy about its input. M0LTE.FecLdpc's sum-product decoder, fed synthetic LLRs of
uniform magnitude with 2 % of signs flipped, decodes 50 of 50 at magnitude 2.5, 42 of 50 at 6, and
0 of 50 at 1 or 0.2, where it runs to its iteration limit with two hundred checks unsatisfied. It
also keeps state, so one instance shared across parallel bursts corrupts them, which cost an hour and
looked like a channel result. The probe normalises the max-log LLRs to a mean magnitude of 2.5, which
is what OFDM-FM's coding layer settled on for the same decoder, and serialises the codec.

## 4. What the numbers mean at the antenna

On the model's Tait narrow profile the noise in the 9.75 kHz IF at a 9 dB noise figure is
-125 dBm, and the radio's published 12 dB SINAD point of -121 dBm sits at about +4 to +5 dB CNR on
that scale, which is where this repository's FM knees also cluster. So, on the same scale:

| | CNR | At the antenna socket |
|---|---|---|
| `ofdm-fm` BPSK 1/2, today's floor | +5.8 dB | about -119 dBm |
| This waveform, R1 through a CM108 with the coupling undone, 15 bit/s | -18 dB | about -143 dBm |
| This waveform, SDR on the IF, 15 bit/s | -20 dB | about -145 dBm |
| This waveform, SDR on the IF, 7.5 bit/s | -22 dB | about -147 dBm |

Site noise moves every row together, not their spacing: at 145 MHz a residential site raises the
floor by about 12 dB over thermal and a business site by 17, so the dBm figures are for a quiet
receiver and the decibels between rows are what carry. 26 dB is 20 times the distance in free
space and about five times over a path losing 35 dB per decade, and it is more than the difference
between 1 W and a 25 W Tait at full chat.

Per bit, the SDR floor of about -20 dB in 9.75 kHz is -14 dB in 2.5 kHz; at 15 bit/s that is an
Eb/N0 of about 8 dB, against FT8's 5 dB at 6.1 bit/s. Most of the 3 dB is the four-line combining
loss and the non-coherent 8-FSK; a coherent phase-modulated version of the same idea would close it,
and section 5 says why not to start there.

## 5. The design

**Waveform.** Slow non-coherent MFSK on audio tones, the tone at the modulation index that puts its
energy in the first two pairs of sidebands (about 2: 1250 Hz at 2500 Hz deviation, 2500 Hz at 5 kHz
on a 25 kHz channel). 8 tones, 10 Hz spacing, 100 ms symbols as the baseline; 50 ms symbols at 20 Hz
spacing where oscillator drift or flutter says so (3 dB less sensitive, twice as fast, and twice as
tolerant of a wandering offset). Non-coherent by choice: it is what FT8, WSPR and datac14 chose for
the same reason, that the phase of a commercial VHF synthesiser is not something a 100 ms symbol can
be asked to trust, and it is what makes the same waveform decodable by a Goertzel filter on a
speaker. Coherent BPSK on the same subcarrier, with pilots, is the 3 dB that can be gone back for.

**Why tones and not a DC shift.** A data port that reaches DC would allow true FSK, one RF line per
tone instead of four, and about 1.5 dB back. T13 on a Tait has a 3.7 Hz high-pass behind it and a
microphone socket has 300 Hz, so a DC shift is not transmittable from the sockets this mode exists
for. The tones cost 1.5 dB and buy the speaker receiver. Keep the tones.

**Two receivers for the station this is built for, one transmitter.** The transmitter is T13 in
both cases, and the same tones.

1. *R1 into the CM108 interface.* The phase receiver, with the interface's coupling undone. Frames
   to -18 dB at 15 bit/s, provided the coupling corner is calibrated to 1 to 2 %. That calibration is
   the piece of this design with no precedent here, and it is gate B2 below. The interface notes
   already say the corner is not one component (C1 and C2 on the tail, the dongle's own input
   capacitor, one pole at 90 Hz or four at 35 both fitting the measurements), which is exactly why it
   has to be found by the receiver rather than read off the build page.
2. *An SDR on the IF.* The same sideband detector on complex baseband, with the carrier search
   widened to the radios' combined frequency error (about plus or minus 400 Hz at 145 MHz for
   1.5 ppm each). Frames to -20 dB at 15 bit/s and -22 at 7.5, nothing to calibrate, and the radio's
   front end and IF filter still in front of the SDR's ADC, which at a shared site matters more than
   the two decibels. Where the mod exists, this is the receiver to build first.

A microphone-and-speaker station gets the plain audio receiver and about -6 dB from the same
transmission, for free; it is not designed for here.

**Framing.** At 15 bit/s a plain AX.25 UI frame spends 144 bits, nearly ten seconds, on two
addresses, a control byte and a PID. The mode should carry the IL2P header (104 bits, and already in
tree), or better a station-table index once both ends have heard each other's full callsign, and
one LDPC codeword per burst sized to the payload: `HRA_56_56` (40 bits net, datac14's), `H_128_256_5`,
`H_256_512_4`, up to `H_1024_2048_4f`, the burst's length signalled by which of a small set of sync
patterns opens it, the way datac tells its modes apart. CRC inside the codeword, as datac does, so a
converged decode is a frame.

**Sync.** A Costas array of the tones at the start of the burst and again at the end, searched over
time and offset by the same zero-padded FFT the receiver already runs, exactly as FT8 does. FT8's
experience is that this costs about a decibel at threshold against a genie, which is the number gate
A2 has to match. Slot-timed operation (bursts start on a UTC boundary, as FT8's do) removes most of
the search and makes channel access deterministic, and a station with the network to reach has the
time to keep; free-running is the fallback and costs only computation at these rates.

**Carrier sense.** None of the station's detectors can see a signal at -20 dB, and the radio's
squelch is closed. The only carrier sense this mode has is its own sync detector, and a 17 s burst
is expensive to lose to a collision. So: a frequency of its own, slot-timed, with the node
scheduling replies into slots. That is the network-side change, and it is the same shape as the
per-sub-channel gating in [carrier-sense.md](../carrier-sense.md): a modem whose busy answer comes
from its own decoder rather than from energy.

**What the node does with 15 bit/s.** A beacon, a short message, a position, a mailbox poll and its
answer. Connected mode with its acknowledgements is a poor fit and should not be the first thing
tried; UI frames with the LDPC's own check are the first thing.

## 6. Phases and gates

Every gate is a ladder through M0LTE.FmChannel at the profiles above, eight or more seeds a point,
quoted as frames of N at a CNR, and recorded in [mode-validation.md](../mode-validation.md) when a
mode first decodes where it did not before. The channel-model floors are model numbers until gate C1
ties them to a radio, exactly as the FM ladders in the roadmap are.

### A - the modem, genie-free
- **A1 Waveform and receivers.** The three receivers of section 3 as an `IModem`, tones and symbol
  length as parameters, the sideband receiver on both discriminator audio and complex baseband.
  Exit: reproduces the section 3.2 table with genie timing.
- **A2 Acquisition.** Costas sync, time and offset search, burst-level offset with per-symbol
  tracking. Exit: within 1 dB of the genie at the 50 % point for both the audio and the phase
  receiver; false decode rate measured on noise-only input over an hour of simulated channel and
  stated.
- **A3 Framing.** IL2P header or station table, codeword ladder, CRC, sync-pattern length
  signalling. Exit: a frame ladder like section 3.5 for each codeword size.

### B - the station
- **B1 Levels at T13.** The tone at full legal deviation from T13's 0.29 Vp-p per kHz, the
  receiver's deviation scale calibrated from a burst above threshold. Exit: the modulation index
  the receiver measures matches the one sent within 5 %.
- **B2 R1 into the CM108.** The coupling inverse, and its calibration from a burst above
  threshold: a search over corner and pole count maximising the sideband metric, stored per
  station. Exit: through a modelled 70 Hz one-pole and through a modelled four-pole at 35 Hz, both
  calibrated blind, frames to -18 dB. This gate decides whether configuration 2 exists.
- **B3 SDR on the IF.** `ApplyToIf` plus a carrier offset and an SDR sample-clock offset, the same
  receiver. Exit: frames to -20 dB at 15 bit/s and -22 at 7.5, at plus or minus 400 Hz.

### C - the radio
- **C1 A real ladder.** The CNR axis on a real Tait, which is roadmap "Needs Tom" #5 and has been
  the missing calibration of every FM number here: an attenuator between two radios, or the Flex
  and RSP1 rig, RSSI read over CCDI as the independent axis. Exit: the model's -6 and -20 dB rows
  reproduced within 3 dB on hardware (the -18 and -20 dB rows), and the model's knees re-pinned
  if not.
- **C2 The tap and the SDR.** Tom's demonstrated IF mod with an SDR, and R1 into a sound card whose
  coupling B2 has calibrated. Exit: a frame off air at a level no other mode here decodes at,
  entered in the ledger.

### D - the network
- **D1** A slot-timed port on a node, UI frames, a beacon and a message. **D2** Header compression
  by station table. Both unscheduled until C2 has a frame.

## 7. Risks and open questions

- **Oscillator drift.** 10 Hz tones over a 17 s burst ask the two radios' references to hold to a
  few hertz relative to each other for that long. A TM8100 is specified to 1.5 ppm over
  temperature, which is 220 Hz at 145 MHz, but the drift within a burst is thermal and slow; nothing
  measures it yet. The per-symbol offset tracking already in the probe handles a slow slide; the
  50 ms symbol is the fallback; C1 measures it.
- **Flutter and multipath.** Nothing here has been run with `FlutterDopplerHz` set. A mobile
  station's fade is a CNR excursion, not a shift, and a 100 ms symbol under a 10 Hz Doppler is
  partly averaged over it; the fixed stations this mode is for do not flutter much. It needs a row
  in every ladder anyway.
- **False decodes.** At -20 dB the receiver is deciding among eight hypotheses on energies that are
  mostly noise, and the burst-level offset search is a maximum over hundreds of bins. The LDPC's
  convergence and the CRC are the backstop, as in datac, and A2 has to state the rate on pure noise.
- **The coupling calibration.** B2 is the only gate whose method is untested. If a real dongle's
  coupling is not a small number of poles, configuration 2 falls back to a DC-coupled ADC, which is
  not a sound card and is a hardware project.
- **The IF filter is in the loop.** Both the phase and SDR floors were measured with the radio's
  9.75 kHz IF in front of the receiver. An SDR at the antenna without it sees the same signal in
  its own bins and more of everything else; that is a site question, not a modem one.
- **Level.** The phase receiver needs the discriminator output in known units (full deviation to
  2 pi times the deviation of phase per second). A wrong scale is a wrong modulation index, and the
  sideband pattern is only mildly sensitive to it (a 10 % error is a fraction of a decibel), but it
  is another number to calibrate from a burst above threshold, and the same search can carry it.
- **Air time and courtesy.** 17 s bursts on a shared frequency are not courteous. The frequency of
  its own in section 5 is a requirement, not a preference.

## 8. What it unlocks

A station 20 dB short of the network is, today, not on the network. With a handheld and a cable it
can be, at a message a minute; with a tap or an SDR it can be at 26 dB short. The same waveform
serves both, so a station can start with the cable and improve its receiver without anyone else
changing anything, and the transmitter it needs is any FM radio with a microphone socket. Nothing
about it is specific to a Tait, a 12.5 kHz channel or 2 m.
