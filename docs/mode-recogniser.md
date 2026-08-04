# Unknown-mode recogniser - design and feasibility

**Status: design only, nothing implemented.** This is the answer to "there is something else
transmitting over us; have a guess as to what it is."

## Verdict

Feasible, and most of the value is cheap, because we do not have to demodulate to classify.
Amateur HF/VHF digital modes separate cleanly in a small feature space that we can measure from
the FFT we are already computing for the waterfall. The expensive part is not the recognition -
it is being honest about confidence.

The design below is deliberately tiered so each tier ships and earns its keep alone. Tier 0 is
useful on its own and is nearly free.

## What we already have to build on

- `WaterfallSource` - a running FFT over the passband at the configured line rate, 1024 bins.
  The recogniser should consume the same lines the display draws, for the same reason
  `BandActivityTracker` does: the numbers then always agree with what the operator can see.
- `BandActivityTracker` - per-band noise floor by min-tracking over ~15 s, and burst detection
  as a run of lines ≥ 6 dB over that floor, with mean SNR and run length. Burst segmentation is
  therefore already written; it is currently scoped to a declared modem band rather than to the
  whole passband.
- Working demodulators for AFSK1200, BPSK300, QPSK2400/3600, GFSK9600, FreeDV datac1/datac3,
  ARDOP and POCSAG - which matters for tier 3.
- `ModemBandProbe` - measures a modem's occupied bandwidth off its own modulator. The same
  measurement, applied to a heard burst instead of a synthesised one, is the primary feature.
- A `TimeProvider`, and with a Flex a disciplined one. This turns out to matter more than any
  spectral feature (see "UTC phase" below).

## The feature space

Ordered by how much they buy per unit of work.

**1. Occupied bandwidth (−26 dB) and centre.** The single most separating feature. 200 Hz-class
(PSK31, FT8, FT4), 500 Hz-class (ARDOP 500, Olivia 500, Pactor 1/2), 2000-2400 Hz-class (ARDOP
2000, VARA HF, Pactor 3/4), and the packet widths we already know. Measured directly off the
averaged burst spectrum.

**2. Burst duration and duty cycle.** FT8 transmits for 12.64 s; WSPR for 110.6 s; JS8 for 12.6
or 5.6 s depending on speed. AX.25 packet bursts are 0.1-3 s and irregular. ARQ protocols
ping-pong with characteristic turnaround gaps.

**3. UTC phase.** The whole WSJT-X family starts on a hard slot boundary - FT8 every 15 s, FT4
every 7.5 s, WSPR every 2 minutes on the even minute. A burst that starts within ±0.5 s of a
15 s boundary and lasts 12.6 s is FT8 and essentially nothing else. This is one cheap comparison
against the clock and it removes an entire family from the search space with near-certainty. It
is also the feature most likely to be *wrong* on a station with a bad clock, so it should
degrade to "unknown" rather than mislead when the clock is not disciplined - which the Flex
reference status already tells us.

**4. Carrier count and spacing.** The cepstrum (or autocorrelation) of the averaged burst
spectrum gives tone spacing directly, without identifying individual tones. FT8 is 8 tones at
6.25 Hz; Olivia 32/1000 is 32 at 31.25 Hz; ARDOP 4FSK.500 is 4 carriers; VARA is a dense OFDM
comb. A single carrier with steep raised-cosine skirts is PSK31 or similar.

**5. Symbol rate, without demodulating.** The autocorrelation of the burst envelope, or the
spectrum of |x|², shows a line at the symbol rate. This works on signals we have no decoder for.

**6. Modulation family, by cyclostationarity.** The classic discriminator: the spectrum of x²
has a line at twice the carrier for BPSK and not for QPSK; x⁴ has one at four times the carrier
for QPSK. FSK shows lines at the tone frequencies in |x|. Cheap, and it separates the families
that bandwidth alone cannot.

## Architecture

```
UnknownSignalWatcher            consumes waterfall lines for the whole passband, not one band
  ├─ PassbandSegmenter          BandActivityTracker's logic, generalised: find bursts anywhere,
  │                             including ones that straddle or sit outside our declared modems
  ├─ BurstCapture               a few seconds of raw audio in a ring buffer, so a burst that
  │                             turns out to be interesting can still be analysed after it ends
  ├─ FeatureExtractor           the six features above, per completed burst
  └─ SignatureMatcher           a declarative table of known modes → score + explanation
```

`SignatureMatcher` should be a **table, not a model**. A signature is a named mode with a range
or tolerance per feature and a weight. Scoring is the product (or weighted sum) of per-feature
memberships, and the output carries the matched features as text.

The reasons for a table over a trained classifier are worth stating, because the pull towards
ML here is strong and wrong:

- **It explains itself.** "500 Hz wide, 4 carriers 125 Hz apart, 1.2 s burst → ARDOP
  4FSK.500.100S (0.86)" is a claim an operator can check and disagree with. A model's output is
  not.
- **There is no training set** and building one honestly is a much larger project than the
  recogniser.
- **The features are already the domain knowledge.** Every mode's parameters are published. We
  would be training a model to rediscover numbers we can simply write down.
- **Licensing.** A weights file of unclear provenance in a GPL-3.0-or-later repo is a problem we
  do not need.

## Tiers

**Tier 0 - measure, do not guess.** Detect bursts across the whole passband and report
bandwidth, centre, duration and SNR. Display "unidentified, 480 Hz @ 7051.2, 1.2 s". This is
already useful - it answers "is something sitting on us?" which is most of the operational
question - and it is close to what `BandActivityTracker` does today. Report it on the waterfall
as an untinted band with a measurement label.

**Tier 1 - the signature table over features 1-4.** Confidently identifies the WSJT-X family,
separates packet from ARQ, and places signals in the right bandwidth class. This is where the
bulk of the value sits for the effort.

**Tier 2 - cyclostationary features 5-6.** Needs the raw-audio ring buffer. Separates BPSK300
from FSK300 and narrows the ARQ protocols.

**Tier 3 - speculative demodulation.** On burst end, run our existing demodulators over the
captured audio. If BPSK300 IL2Pc decodes it, it is not a guess any more - and the result should
be presented differently from a signature match, because it is a different kind of claim. This
is the step that turns the recogniser from a guesser into a witness.

## Costs and limits - the honest part

- **Overlapping signals defeat feature extraction.** Two bursts overlapping in time and
  frequency give a bandwidth and a carrier spacing that belong to neither. The segmenter must
  detect the overlap and decline, not average two signals into a confident wrong answer.
- **Low SNR degrades every feature**, and it degrades the discriminating ones (carrier spacing,
  cyclostationary lines) fastest. Confidence must fall with SNR, and there must be a floor below
  which the answer is "unknown" with a measurement attached.
- **The modes most worth identifying are the ones we can never confirm.** VARA and Pactor 2/3/4
  are proprietary. We can match their signatures; we can never decode them to check. Their
  entries should carry that caveat in the UI.
- **A wrong ID is worse than no ID** if anything automatic ever consumes this. If a future
  version backs off transmitting because it thinks it heard a Winlink session, a
  misclassification becomes an operational bug on a shared channel. Recommendation: the
  recogniser stays advisory - it informs the operator and the log, and does not gate the PTT -
  unless and until a specific mode's identification is validated on air against known traffic.
- **CPU.** Tiers 0-1 reuse the existing FFT and cost essentially nothing. Tier 2 adds a few
  seconds of ring buffer (12 kHz × 4 bytes × 5 s ≈ 240 kB) and per-burst analysis off the audio
  thread. Tier 3 is a demod pass per candidate, on burst end only - the expensive tier, and the
  one to gate behind a config switch.

## Validation

Whatever gets built is validated the same way everything else here is: against recordings of
known traffic, with the answer known in advance. `docs/mode-validation.md` is the place the
result goes. A recogniser that has not been run against a recording of a real band is a
hypothesis, not a feature.
