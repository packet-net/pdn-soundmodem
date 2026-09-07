# Receive levels: what each demodulator actually wants

**What this is.** The per-frame audio level badges shipped in v0.60.0 (`TOO LOUD`, `TOO QUIET`)
were set from folklore: a NinoTNC bench loop's "GOOD" band, a CM108 interface design target, and
one over-the-air capture somebody called comfortable. Tom, 2026-09-07: *"the two thresholds should
be determined by examining the modems and determining what the desired/optimal levels are."* This
document is that examination. It reads every demodulator's front end for anything that depends on
the absolute input level, then measures each mode by decoding real frames at every level from
24 dB past full scale down to the converter's own floor, and derives the thresholds from where the
decodes actually start costing something.

**The short answer.** The demodulators care far less than the shipped numbers assumed, and they do
not all care about the same thing. Seventeen of the twenty modes that carry a level lose nothing
measurable anywhere between -84 dBFS and full scale, and at most a decibel at 24 dB of overdrive.
Two do care at the loud end and one family at the quiet end, for reasons that are visible in the
source:

| group | modes | what makes it different | loud | quiet |
|---|---|---|---|---|
| sign or angle slicer | AFSK 300, BPSK 300/1200, QPSK 600/2400/3600, FSK 4800/9600, every framing of each | a bit is a sign or a quadrant; clipping does not move either | **0 dBFS** | **-78 dBFS** |
| four-level slicer | `c4fsk9600`, `c4fsk19200` | four amplitudes against fixed thresholds at 0 and +-2/3 of a tracked envelope | **-6 dBFS** | **-78 dBFS** |
| power-normalised discriminator | the 1200 baud AFSK family, all six | divides by its own in-band power with an absolute floor of 1e-5 under it | **0 dBFS** | **-39 dBFS** |

Plus the clip flag, unchanged and unconditional: a converter that ran out of codes is a fact
rather than a prediction, and it costs at least a decibel on every mode measured.

For comparison, v0.60.0 shipped one pair for all of them: loud at -3 dBFS, quiet at -24.

---

## 1. Which audio the badge is measuring

The number a frame is badged on is `FrameQuality.PeakDbFs`, and getting the thresholds into its
units matters more than anything else here. It is:

- the **peak magnitude**, not the RMS, of the audio the **modems** hear;
- at the **channel's DSP rate** (12 kHz for the AFSK/BPSK/QPSK families, 48 kHz for FSK/C4FSK),
  which on a 48 kHz card is **past the decimating FIR** - unity DC gain, so no level change beyond
  about 1.3 dB of passband ripple measured on the bench CM108;
- **below the half-duplex gate**, so nothing the card heard while the station was keyed is in it;
- over the **frame's own span**, from the sample its demodulator saw the sync at to the sample it
  took the last bit at, less that mode's `FrameSpanMarginSamples`, read in half-millisecond cells
  (`InputLevelHistory`);
- **clamped to 0 dBFS at the top** and -120 at the bottom by `InputLevelMeter.DbFs`, so a frame
  whose audio reached full scale reads exactly 0.0 and nothing reads above it.

The clip flag beside it is the one reading taken somewhere else: on the card's own samples, before
the decimator, because past the decimator "the converter ran out of codes" is an inference rather
than a fact.

Two consequences run through everything below. **The peak includes whatever else was on the
channel during the frame** - on a link at its own decode threshold the noise adds several dB to the
reading, so a frame whose signal peaks at -18 dBFS is badged on a composite of about -13. That is
correct: it is the composite that fills the converter. And **the peak saturates**: once the audio
clips, every overdrive from 0.1 dB to 24 dB reads the same 0.0 dBFS, and the only thing that
distinguishes them is the clip flag.

---

## 2. What the source says before anything is measured

Every absolute-amplitude dependency in the receive chain, found by reading it. "Absolute" means
the constant is compared against a quantity that scales with the input; everything else is a ratio
against something that scales the same way and therefore cancels.

| where | constant | compared against | bites at |
|---|---|---|---|
| `AfskDemodulator.cs:306` | `1e-5f` added to the normalising power | in-band I/Q power (proportional to A^2) | half discriminator gain at about **-44 dBFS** peak, 90% of gain at -34 |
| `C4fskModem.cs:383` | `EnergyBusyDetector.Busy` as a hard gate | +6 dB over a tracked floor | no gate, **no bits at all** - the only squelch in the tree |
| `C4fskModem.cs:432,617` | `1e-6f` envelope half-swing floor | tracked outer envelope (proportional to A) | about **-120 dBFS** |
| `C4fskModem.cs:523-529` | slice at `0`, `+-2/3` | the envelope-normalised value | scale-invariant **given a converged envelope**; see section 5 |
| `EnergyBusyDetector.cs:73,80` | `1e-12` noise-floor clamp | block power | in-band RMS under about **-114 dBFS** cannot assert busy |
| `BpskDemodulator.cs:764`, `QpskDemodulator.cs:1026`, `BpskCarrierOffsetEstimator.cs:120` | `1e-9` | differential product magnitude (proportional to A^2) | about **-90 dBFS**: the carrier-offset window stops accumulating, so `CarrierOffsetHz` goes null and MLSE never engages its outer taps. **Decoding continues** on the DF-DD tracker |
| `QpskDecisionDcd.cs:74` | `1e-24` | decision power (proportional to A^4) | about **-120 dBFS**, and it is a silence sentinel by design |
| `MlseEqualiser.cs:382`, `QpskDemodulator.cs:903` | `1e-12` | sample times prediction (proportional to A^2) | about **-120 dBFS**, divide guards |
| `WaterfallSource.cs:23,174` | `FloorDb = -100`, byte clamp at 255 | bin power, absolutely calibrated | the burst-SNR path only; see section 8 |
| `BandActivityTracker.cs:17` | `BurstThresholdRatio = 4` | band power over a rolling floor | 6 dB excursion; burst-SNR only |

And what is **not** there, which is the more important half:

- **No AGC anywhere in the BPSK/QPSK chain.** Not one gain stage, not one normaliser. The chain is
  linear from the card to the decision, and the decision is a sign test (`projection >= 0`) or a
  quadrant test (`Math.Round(Math.Atan2(im, re) / (pi/2))`). Both are scale-invariant by
  construction.
- **No fixed slicing level in AFSK or two-level FSK either.** Both slice against a min/max envelope
  midpoint and then take a sign. The AFSK chain's power normalisation stands in for QtSoundModem's
  AGC stage; the FSK chain does not even have that.
- **Every DCD in the tree is a ratio.** `PacketDcd` compares a symbol magnitude against a fraction
  of its own 32-symbol running mean; `QpskDecisionDcd` divides by the decision power before
  comparing; `EnergyBusyDetector` compares against a floor it tracks itself. None of them can
  refuse to open because a signal is quiet, only because it is quiet *relative to the channel*.
- **All decision arithmetic is `float` or `double`**, with the Viterbi metrics explicitly
  renormalised each symbol. There is no fixed point and no accumulator that a loud input can
  overflow short of 1e19.

So on the source alone the prediction is: nothing cares at the loud end except the converter, the
1200 baud AFSK family starts losing something in the mid -40s, C4FSK stops dead if its energy gate
cannot open, and everything else runs until the converter has no codes left. That is exactly what
the measurement found.

---

## 3. Method

`ReceiveLevelSweepProbe` and `ReceiveLevelRig` in `tests/Packet.SoundModem.Tests/Channel/`. One
run of one cell is:

1. A real modulator makes the burst: `ModemCatalog.Create(mode, rate, ...).Modulate(frame, 300)`,
   so it carries 300 ms of the transmit delay a real station sends.
2. The burst is scaled so its **peak** sits at the level under test. A level above 0 dBFS is an
   overdrive: the audio is scaled past full scale and the converter model clips it, which is what a
   sound card does when the capture gain is too high.
3. AWGN is added at a fixed signal-to-noise ratio, calibrated against the scaled burst's own power
   in a **3 kHz reference bandwidth** - the convention every other AWGN ladder in this tree is
   quoted in, so the knees below can be read against `docs/mode-validation.md`. Half a second of
   noise-only lead-in and lead-out either side.
4. The whole thing goes through a **16-bit converter model**: multiply by 32767, clamp, round,
   divide by 32768. That is `Pcm16`, which is what an ALSA capture actually delivers, and it both
   saturates at the top and quantises at the bottom.
5. A real `SoundModemChannel` with a real demodulator on it reads the result in 100 ms blocks -
   the block size every station without ARDOP on it uses - with the card-rate tap wired as
   `Program.cs` wires it, so the clip flag is judged where the daemon judges it.
6. The cell counts how many of N frames came back byte-identical, and records the level and the
   clip flag the channel reported for them.

The order matters and is the physical one: the noise arrives at the antenna, and the converter
quantises and clips signal and noise together.

**What a cell reports is a knee, not a decode count.** A decode count at one SNR says a mode works
or does not; it says nothing about how close the level put it to not working. So each cell is the
**lowest SNR, in whole dB, at which the mode still copies three quarters of its frames at that
level**, and what is tabulated is how far that has risen above the same mode's knee at -18 dBFS.
That difference is **dB of link margin the capture gain has cost the operator**, which is the unit
a station is already run in. The ladder is 1 dB and the cells are 12 frames (8 on the slowest
diversity banks), so a single cell is worth about +-1 dB and only a trend across several cells is
worth reading.

The frame is a **15-byte AX.25 supervisory frame** - RR with the poll bit clear, which is what most
of the traffic on a working link is and what the radio1 bench heard from GB7RDG seven times in ten
minutes. Section 7 repeats the two binding cases with a 66-byte UI frame.

**Modes covered.** All twenty catalogue modes that can place their own frames.
`bpsk300-multi` and `bpsk1200-multi` are not listed separately because `ModemCatalog` builds them
from the identical factory arm as `bpsk300` and `bpsk1200`. `freedv-*`, `ms110d-*` and the ARDOP
bridge carry no level at all - they decode from native blocks and cannot say where in the audio a
frame was - so there is nothing to badge and nothing to measure.

---

## 4. Results: the loud arm

dB of link margin lost, against dB of overdrive past full scale. `>45` is "no SNR on the ladder
made this work". Reference knee is the mode's own knee at -18 dBFS, in a 3 kHz bandwidth.

| mode | ref knee | 0 | +2 | +4 | +6 | +9 | +12 | +18 | +24 |
|---|---|---|---|---|---|---|---|---|---|
| `afsk1200` | +5 | 0 | 0 | 0 | +1 | +1 | +2 | +2 | +3 |
| `afsk1200-fx25` | +3 | 0 | +1 | +1 | +1 | +1 | +1 | +2 | +2 |
| `afsk1200-fx25rx` | +5 | 0 | 0 | 0 | +1 | +1 | +2 | +2 | +3 |
| `afsk1200-multi` | +5 | 0 | 0 | 0 | 0 | +1 | +1 | +2 | +2 |
| `afsk1200-il2p` | +4 | +1 | +1 | +1 | +1 | +1 | +1 | +2 | +2 |
| `afsk1200-il2p-nocrc` | +4 | +1 | +1 | +1 | +1 | +1 | +1 | +2 | +2 |
| `afsk300` | 0 | +1 | +2 | +1 | +1 | +1 | +2 | +2 | +3 |
| `afsk300-il2p` | -1 | +1 | 0 | +1 | +1 | +2 | +2 | +2 | +2 |
| `afsk300-il2pc` | -1 | +1 | 0 | +1 | +1 | +2 | +2 | +2 | +2 |
| `bpsk300` | -5 | +1 | +1 | +1 | +1 | +1 | +1 | +1 | +1 |
| `bpsk300-nocrc` | -5 | +1 | +1 | +1 | +1 | +1 | +1 | +1 | +1 |
| `bpsk1200` | +1 | 0 | 0 | +1 | +1 | +1 | +1 | +1 | +2 |
| `qpsk600` | -1 | 0 | +1 | +1 | +1 | +1 | +2 | +2 | +2 |
| `qpsk2400` | +5 | 0 | 0 | +1 | 0 | +1 | +1 | +1 | +2 |
| `qpsk3600` | +8 | 0 | 0 | 0 | +1 | +2 | +2 | +4 | +5 |
| `fsk9600` | +13 | 0 | -1 | -1 | 0 | +1 | +2 | +3 | +3 |
| `fsk9600-il2p` | +9 | 0 | 0 | -1 | -1 | 0 | +1 | +1 | +2 |
| `fsk4800-il2p` | +6 | 0 | 0 | +1 | +1 | +1 | +1 | +1 | +2 |
| `c4fsk9600` | +19 | -1 | -1 | +1 | **+5** | **>45** | >45 | >45 | >45 |
| `c4fsk19200` | +20 | +1 | +1 | **+4** | **+10** | **>45** | >45 | >45 | >45 |

**Eighteen of the twenty do not have a loud cliff at all.** Driving them 24 dB past full scale -
which is not an overdrive so much as a square wave - costs between 1 and 5 dB, and 6 dB of
overdrive costs at most 1. That is what a sign test buys you: clipping a signal whose bits are
decided by which side of zero a sample fell on leaves the decision exactly where it was, and what
little it costs comes from the harmonics the clipping puts back into the passband.

**Both C4FSK modes fall off a cliff.** They are unharmed to +2 dB, lose 1 to 4 dB at +4, 5 to 10 dB
at +6, and decode nothing at all at +9 whatever the SNR. They are the only modes here whose slicer
reads an amplitude: four PAM levels against fixed thresholds at 0 and +-2/3 of a tracked envelope
(`C4fskModem.cs:523-529`). Clipping compresses the outer levels toward the inner ones while the
thresholds stay where they are, and no envelope tracker can undo that - the outer symbols are
simply not out there any more. `docs/mode-validation.md` records the same class of failure from the
other direction in the issue #336 entry, where noise *inflating* the envelope demoted outer symbols
under the same fixed 2/3 slice.

At every level above 0 dBFS the reported peak is exactly 0.0 and the clip flag is set on every
frame, which is the point made in section 1: the badge cannot tell +2 dB of overdrive from +24.

## 5. Results: the quiet arm

Same units, against the frame's peak in dBFS.

| mode | ref knee | -30 | -42 | -48 | -54 | -60 | -72 | -84 | -90 |
|---|---|---|---|---|---|---|---|---|---|
| `afsk1200` | +5 | 0 | 0 | **+2** | +3 | +4 | +4 | +4 | +6 |
| `afsk1200-fx25` | +3 | 0 | +1 | +1 | +2 | +3 | +3 | +3 | +5 |
| `afsk1200-fx25rx` | +5 | 0 | 0 | **+2** | +3 | +4 | +4 | +4 | +6 |
| `afsk1200-multi` | +5 | 0 | 0 | +1 | +2 | +2 | +2 | +2 | +5 |
| `afsk1200-il2p` | +4 | 0 | 0 | 0 | +1 | +1 | +1 | +1 | +2 |
| `afsk1200-il2p-nocrc` | +4 | 0 | 0 | 0 | +1 | +1 | +1 | +1 | +2 |
| `afsk300` | 0 | 0 | 0 | 0 | 0 | 0 | 0 | +1 | +2 |
| `afsk300-il2p` | -1 | 0 | -1 | 0 | 0 | 0 | 0 | 0 | +1 |
| `afsk300-il2pc` | -1 | 0 | -1 | 0 | 0 | 0 | 0 | 0 | +1 |
| `bpsk300` | -5 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| `bpsk300-nocrc` | -5 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| `bpsk1200` | +1 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | +1 |
| `qpsk600` | -1 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| `qpsk2400` | +5 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | +2 |
| `qpsk3600` | +8 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | +3 |
| `fsk9600` | +13 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | +2 |
| `fsk9600-il2p` | +9 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | +1 |
| `fsk4800-il2p` | +6 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | +2 |
| `c4fsk9600` | +19 | 0 | 0 | 0 | -1 | 0 | -1 | +1 | **>45** |
| `c4fsk19200` | +20 | 0 | 0 | 0 | 0 | 0 | 0 | **+4** | **>45** |

**Fourteen of the twenty are flat to -84 dBFS**, which is not a demodulator objecting to anything -
it is a 16-bit converter running out of codes to describe the signal with. This is the finding the
shipped -24 dBFS threshold contradicts by 60 dB. Turning the capture gain down scales the radio's
own noise down with the signal, so the ratio the demodulator sees is unchanged, and every decision
in these chains is a ratio. There is nothing for the level to break until the codes run out.

**The two C4FSK modes stop 6 dB earlier than everything else**, at -90 rather than at -84, and for
a reason that falls straight out of the arithmetic: -84 dBFS peak is 2.1 converter codes, and four
PAM levels at +-1 and +-1/3 of that need the inner pair to land on 0.7 of a code. -90 dBFS is one
code, and one code can carry a sign but not four levels. A binary mode still has its sign; C4FSK
has nothing.

**The 1200 baud AFSK family is the only group with a quiet cliff above the converter's floor.**
`afsk1200` is flat to -42, 2 dB down by -48 and 4 dB down by -60, so it loses its first decibel at
about **-45 dBFS**. That is the `1e-5f` floor in `AfskDemodulator.cs:306`, and the constant's own
comment predicts it: against the roughly 0.15 nominal in-band power this modulator produces, half
the discriminator's gain has gone by a peak of about -44 dBFS. The floor is not a numerical
nicety - it is what stops the leading edge of a burst dividing near-zero by near-zero - and the
comment beside it explicitly declines to make it smaller. What it costs is not the gain itself,
which a sign test does not care about, but the non-linearity: with the signal power comparable to
the floor, amplitude noise modulates the discriminator's gain and lands in the eye.

The 300 baud AFSK family shares the same constant and does not show it above -84, which is
consistent with its knee sitting 5 to 6 dB lower and its +-250 Hz branch filters admitting far less
of the noise that does the modulating. That is an explanation offered, not a mechanism measured.

## 6. Where the thresholds come from

Both ends need the same missing ingredient: **how much louder or quieter than the frame just heard
the next one may reasonably be**. A frame that decoded is by definition fine; a badge on it is a
statement about headroom, and headroom is only meaningful against a spread.

**The spread is 6 dB**, and this repository already has the numbers.
`docs/hardware/tm8100-cm108-interface-notes.md` sets the interface at -12 dBFS for 60% of class
deviation and works out that 100% of class then lands at -7.6 dBFS: stations running anywhere
between 60% and 100% of class deviation therefore span **4.4 dB**. The same radio's published
receive-tap level is a +-10% band (0.62 / 0.69 / 0.76 Vp-p), another **1.8 dB** across it. That is
6.2 dB before anything else, and the radio1 bench measured a further 1.4 dB of frame-to-frame
spread from one station at a fixed gain. Six is also the headroom figure this tree already works to
elsewhere. It is carried as `FrameLevelLimits.StationSpreadDb`.

Then each threshold is `0 dBFS - (spread - overdrive the mode tolerates)` at the loud end and
`(the level where the mode loses its first dB) + spread` at the quiet end.

### Loud

| group | tolerates | headroom needed | threshold |
|---|---|---|---|
| sign or angle slicer | >= 24 dB of overdrive for <= 1 dB lost | 6 - 24 < 0, so none | **0 dBFS** |
| four-level slicer | 2 dB | 6 - 2 = 4, rounded out to 6 | **-6 dBFS** |

For the eighteen sign-and-angle modes the spread is swallowed whole: a station 6 dB louder than one
reading -0.5 dBFS drives 5.5 dB of overdrive and costs about a decibel. A threshold under 0 dBFS
would badge frames that measurably cost their operator nothing, which is the failure this whole
exercise exists to remove. So the threshold is the top of the scale, and since the reading is
clamped there, the test fires exactly when the frame's own audio reached full scale.

For the two C4FSK modes the spread is not swallowed at all: a station 6 dB louder than one reading
-0.5 dBFS is at 5.5 dB of overdrive and has lost 5 to 10 dB, and 3 dB louder again decodes nothing.
The arithmetic gives -4 dBFS; it is taken out to **-6** because 6 dB is the minimum spread that can
be justified rather than a typical one, and because the failure past the cliff is not graceful -
the mode goes from working to silent inside 3 dB.

### Quiet

| group | first dB lost at | threshold |
|---|---|---|
| sign or angle slicer, and both C4FSK modes | -84 dBFS | **-78 dBFS** |
| 1200 baud AFSK family | about -45 dBFS | **-39 dBFS** |

-78 is a **quantisation-only** figure and is stated as such: it is where a 16-bit converter stops
being able to describe the signal, and a real card's analogue noise floor sits above that by an
amount nothing in this repository has measured. On real hardware the true cliff is somewhere
higher and this threshold is optimistic by that much. See section 9.

-39 is `afsk1200`'s cliff plus the spread. The whole 1200 baud family takes it, including the IL2P
framings whose own cliff is 6 dB lower: they share the demodulator and the constant, and the
difference between them is Reed-Solomon covering the first errors rather than the front end
behaving differently.

### The meter's own bands

`InputLevelMeter`'s bar is about the **card**, which may be carrying any mode at all, so it takes
the strictest mode's line in each direction:

- `HotPeakDbFs` moves from **-3 to -6**, matching `FrameLevelLimits.ClipSensitive`. The old value's
  own justification named 6 dB as this tree's headroom figure and then subtracted three of it; the
  sweep says six was right.
- `TargetPeakLowDbFs` / `TargetPeakHighDbFs` stay at **-18 / -9**. They survive the audit: -18 is
  21 dB above the earliest cliff any mode has and -9 is 3 dB below the strictest headroom line, so
  a signal landing in the zone is comfortable for every mode in the catalogue at once.
- `QuietPeakDbFs` stays at **-30**. It is the bar's grey advisory edge for a channel with nothing
  on it, a different question from a frame's badge, and it sits 9 dB above the strictest mode's
  quiet threshold, so the bar's advice still arrives before any badge does.

---

## 7. Two checks

**Frame length.** The sweep uses a 15-byte supervisory frame because that is what a working link is
mostly made of. Repeating the two binding cases with a 66-byte UI frame:

| | 15-byte frame | 66-byte frame |
|---|---|---|
| `c4fsk19200` at +4 / +6 / +9 dB overdrive | +4 / +10 / dead | +3 / +9 / dead |
| `afsk1200` first dB lost | about -45 dBFS | below -54 dBFS |

The loud cliff does not move with frame length. The AFSK quiet cliff moves *outward* on the longer
frame, so the short frame - the common one - is the conservative case and is what the threshold is
set from.

**The radio1 bench, 2026-09-07.** Real GB7RDG `qpsk3600` traffic through a CM108 and an FM radio,
at four capture gains (`/tmp/bench-060/bench-1-notes.md`). `qpsk3600` is in the sign-and-angle
group, so its thresholds are 0 and -78:

| capture gain | frames read | v0.60.0 badge | this document's badge | did they decode? |
|---|---|---|---|---|
| 23 dB | 0 dBFS, clipped | TOO LOUD | TOO LOUD | yes |
| 11 dB | -3.6 to -4.1 dBFS | none | none | yes |
| 0 dB | -13.7 to -15.1 dBFS | none | none | yes |
| -12 dB | -26 to -27.3 dBFS | **TOO QUIET** | none | **yes** |

The only row that changes is the one where the old threshold was demonstrably wrong: those four
frames decoded perfectly, and the sweep says `qpsk3600` at -27 dBFS has not lost a decibel and will
not lose one for another fifty.

---

## 8. Two things found on the way, neither fixed here

**The burst SNR is null on exactly the traffic this was measured on**, which the radio1 bench saw
at every gain and PR #431 noted without diagnosing. There are two independent causes, both in
`BandActivityTracker` and `WaterfallSource` rather than in anything this branch touches:

- **A railed input produces no SNR at all.** The waterfall's byte scale is absolutely calibrated
  and clamps at 0 dBFS (`WaterfallSource.cs:174`). When the whole band rails, every bin reads 255,
  the floor ring banks the same 1.0, and `power >= floor * 4` becomes `1.0 >= 4.0`, which is never
  true. That is the 23 dB gain case exactly.
- **A short frame is diluted below the 6 dB gate.** Lines are 33 ms apart but each is a 2048-point
  transform, 171 ms of audio at 12 kHz. A 15-byte `qpsk3600` frame is 42 ms of air, a quarter of
  the window, so its peak line reads roughly 6 dB under its true in-band power before the Hann
  weighting - and the gate wants 6 dB over the floor. That is the other three gains.

The per-frame level path does not have this problem because `FrameLevelMonitor` reads the
demodulator's own span in half-millisecond cells. Worth an issue of its own.

**`EnergyBusyDetector` can hold busy indefinitely.** Its hold counter only decrements while the
ratio is under the release threshold, so a signal parked in the 3 to 6 dB hysteresis band never
releases until the floor's 10-second upward adaptation drags it out. Not a level-threshold matter,
but it is in the same files and somebody should know.

---

## 9. What this could not measure

- **No hardware.** Every number here is simulation. The only real-hardware data quoted is the
  radio1 bench of 2026-09-07, which is four capture gains of one station on one mode and cannot be
  extended without the Pi.
- **The card's own analogue noise floor.** The converter model is quantisation and saturation and
  nothing else, so the quiet cliffs at -84 dBFS are the best case. A real CM108 has a noise floor
  above that and the cliffs on real hardware are higher by however much. **`-78 dBFS` is the
  threshold most likely to want moving once somebody can measure a real card.**
- **Station-to-station level spread on a real network.** The 6 dB comes from the tm8100 interface
  note's design arithmetic plus one bench station's frame-to-frame spread, not from watching a
  network. It is the number every threshold here is built on and it is the one most worth
  challenging.
- **AWGN only.** No fading, no interference, no impulse noise, no carrier offset. The question
  asked was what the level does, and holding everything else fixed is how that question gets a
  clean answer - but a mode's behaviour at a level under fading is not established here.
- **The two C4FSK cliffs are 2 dB resolved at best.** The sweep steps 3 dB or coarser through the
  overdrive range and the SNR ladder is 1 dB with 12 frames a cell.

## 10. Re-running it

```
cd tests/Packet.SoundModem.Tests
dotnet build --nologo -v q
RX_LEVELS=1 RX_LEVELS_OUT=/tmp/rx-levels.md \
  flock /tmp/pdnsm-suite.lock dotnet run --no-build -- \
  -class Packet.SoundModem.Tests.Channel.ReceiveLevelSweepProbe
```

`RX_LEVELS_MODES` picks modes (comma separated), `RX_LEVELS_TRIALS` sets the frames per cell,
`RX_LEVELS_LONG` swaps the 15-byte frame for the 66-byte one. It writes a table per mode as it
finishes each, and the whole catalogue is upwards of an hour; the diversity-bank modes are most of
it. `ReceiveLevelCliffTests` is the committed version: it re-runs the handful of cells each
threshold rests on and fails if a cliff has moved into the band this document declares safe.
