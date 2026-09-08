# sm-tnctest - the corpus benchmark, and the WA8LMF TNC Test CD

`sm-decode` answers "decode this file as afsk1200". `pdn-decode` answers "what mode is this?".
`sm-tnctest` answers the third question, the one that has decided arguments about TNCs since the
1990s: **how many packets does this decoder get off this recording, and how many does yours?**

```
sm-tnctest "01_40-Mins-Traffic -on-144.39.flac"
```

## Why it is a leaderboard and not a test

The corpus is APRS as it actually arrived: WA8LMF's TNC Test CD, a recording of 144.39 MHz in
Los Angeles during afternoon rush hour, made at his home in Pasadena in 2009. It is not a
synthetic ladder. It has co-channel collisions, mobiles into and out of picket fencing, badly
deviated transmitters, twisted audio, and a long tail of stations right down at the noise.

Nobody knows how many packets are on it. There is no key. A frame that no decoder in the world
has ever recovered is indistinguishable from no frame at all, so **there is no denominator** and
therefore no percentage, no pass mark and no such thing as a perfect score. The only statement
the corpus supports is a comparative one: this decoder got N off this audio and that one got M.
That is why the packet world has kept score on it for two decades rather than certifying against
it, and it is why everything in this tool is built to make one run comparable with another
rather than to produce a verdict.

Two things follow, and they are the design of the tool:

- **The audio has to arrive the same way every time.** The disc is 44100 Hz and the AFSK chain
  runs at 12000, so every score passes through a resampler; that resampler is `pdn-decode`'s,
  referenced rather than copied, so a benchmark score and a forensic decode of the same file
  cannot disagree about what the modem heard. Which channel was read, what the resampling was,
  and whether the file decoded intact are all printed in the report rather than assumed.
- **A run has to be diffable against another run.** `--json` writes every frame it decoded, not
  just the total, and `--baseline` diffs the frame sets. A change to a receive path that leaves
  the count alone and swaps *which* frames it got has done something real, and a benchmark that
  only remembers totals cannot see it.

## The corpus

The tracks are **not in this repository** - they are WA8LMF's work, and redistribution terms are
still TBC (see `docs/plan.md` Phase 0). Keep them wherever you keep them and pass a path.

**The tags on this rip do not all describe the audio, so go by the length and by what decodes.**
Measured with this tool:

| File | Its own title tag | Length | What is actually on it |
|---|---|---|---|
| 01 | 40 Mins Traffic on 144.39 MHz in Los Angeles | 25:49 | the traffic recording, **flat** |
| 02 | 100 Mic-E Bursts DE-Emphasized | 25:49 | the same traffic recording, **de-emphasised** |
| 03 | 100 Mic-E Bursts FLAT | 5:07 | 100 identical WA8LMF Mic-E bursts, flat. Tag correct |
| 04 | 25-Minutes Drive Test - San Gabriel Valley | 10:00 | not yet scored |
| 05, 06, 07 | KPC-3+ CAL tone, flat / de-emphasised / pre-emphasised | 1:09, 1:09, 3:39 | a tone, no packets |

Files 01 and 02 are the pair the disc exists for: **one recording, twice**, once as flat
discriminator audio and once through a de-emphasis network. That is the difference between
tapping a receiver's discriminator and taking its speaker output, it is where Bell 202 twist
comes from, and it is what the bank's three pre-emphasis branches are for.

The identification is not a guess. 02 is the same length as 01 to within half a second, decodes
1011 frames against 01's 1007, and yields the **same 845 distinct frame contents from the same
119 callsigns**. A hundred bursts of one Mic-E beacon, which is what its tag claims and what 03
really is, decodes 100 frames and one distinct content. 02 is also the file this repository has
been calling Track 2 since 2026-07-15, whose recorded figures (972, later 983, against atest's
970) sit exactly where a continuation of this series would.

Two more things worth writing down because they will otherwise be rediscovered:

- **The title tag on 01 says "40 Mins Traffic" and the audio is 25 minutes 49 seconds.** That is
  not a truncated read: the file's STREAMINFO declares 68,325,012 sample instants, the decode
  produces exactly that many, and the whole decode hashes to the MD5 the encoder wrote. The audio
  in this copy is 25:49, so a score off it is not comparable with a published figure taken from a
  40 minute copy. Compare against runs over the same file.
- **The stations sit off a 1700 Hz centre, and which way depends on the emphasis.** On 01 the
  measured offset distribution is centred near -30 Hz and runs past -150; on 02, the same
  stations, it is centred on 0 and runs past +150. Both are the discriminator's DC level moving
  with the twist rather than the transmitters moving, which is worth knowing before reading an
  offset histogram as a statement about anybody's crystal.

## Scoring convention

**The score is the number of frames the modem passed to its host.** That is what a TNC would
have printed and what every published figure for this corpus counts. Every frame in it passed an
HDLC FCS, so nothing here is a maybe. Frames the bank read on more than one branch are merged by
the modem's own deduplication, exactly as they would be on the air, so parallel decoders do not
inflate the number.

The report puts several other counts beside it, because a bare total hides the interesting moves:

| Line | What it means |
|---|---|
| SCORE | frames delivered: the leaderboard number |
| distinct frame contents | how many were byte-unique. Lower than the score by design, because a beacon every two minutes really is the same bytes twice |
| AX.25-shaped | frames whose address field parses. Everything passed an FCS, so a shortfall is a real transmission of something that is not AX.25 |
| source callsigns heard | how much of the channel's population was reached, which moves differently from the frame count when the misses are concentrated on a few weak stations |
| station offset, centred | where each station sat relative to the channel centre, as the winning branch measured it (its own offset plus its residual), which says whether the recording is on frequency |
| winning emphasis branch | which pre-emphasis branch decoded each frame, which says how twisted the transmitters were |

## Standing scores

Measured 2026-09-07, Release build, on the corpus described above.

### The traffic recording, 25:49 of LA rush hour, at 12000 Hz

| File | Receiver | Score | Distinct | Callsigns | Wall clock | atest, recorded |
|---|---|---|---|---|---|---|
| 01 flat | `afsk1200-multi` (21 branches: 7 offsets x 3 emphasis) | **1007** | 845 | 119 | 106 s, 14.7x | 999 |
| 01 flat | `afsk1200` (single decoder) | 968 | 811 | 118 | 5 s, 306x | |
| 02 de-emphasised | `afsk1200-multi` | **1011** | 845 | 119 | 105 s, 14.7x | 970 |
| 02 de-emphasised | `afsk1200` | 534 | 466 | 100 | 5 s, 314x | |

The same bank over 01 at the file's own 44100 Hz, no resampler in the path, as the rate control:

| File | Receiver | Score | Distinct | Callsigns | Wall clock |
|---|---|---|---|---|---|
| 01 flat, at 44100 Hz | `afsk1200-multi` | 1006 | 845 | 119 | 1236 s, 1.3x |

The reference figures are Dire Wolf `atest` at **999** on 01 and **970** on 02, recorded in this
repository on 2026-07-15 against our 959 and 972 at the time (`docs/plan.md` Phase 1). The
receive-path work since - the per-mode discriminator clamp, sub-sample DPLL crossing
interpolation, the seven timing phases and the clock hold - has taken 01 from 959 to 1007 and 02
from 972 to 1011, which puts the AFSK bank ahead of the reference on both. **That comparison
rests on the recorded `atest` numbers rather than on a re-run**: Dire Wolf is not built on the
machine this was measured on, and a fresh A/B is what would settle it.

**The resampling costs nothing, which is worth knowing because everything above depends on it.**
Run at the file's own 44100 Hz, with no resampler in the path at all, 01 scores **1006** against
the resampled 12000 Hz run's 1007 - one frame in a thousand, from the same 845 distinct contents
and the same 119 callsigns. So the polyphase conversion is not quietly buying or costing frames,
and the 12 kHz figures are the ones to quote: 12000 is the rate the daemon runs this mode at, and
at 44100 the same bank manages 1.3x real time against 14.7x, which is a receiver with no margin
rather than a faster one.

The pair of files is what makes the bank's value legible, and either one alone would mislead
about it. On the flat recording the bank is worth **+4 %** over one centred decoder, which is not
much for twenty times the CPU and a fifth of the throughput: a Bell 202 discriminator is not
narrow, so a station 60 Hz off is not a station a centred decoder misses, and the bank is buying
the tail rather than the bulk. On the de-emphasised recording - the same stations, through the
audio path most people actually have - it is worth **+89 %**, 1011 against 534, and nearly 400 of
those frames are decoded on a pre-emphasis branch rather than the flat one. Twist, not frequency,
is what the bank is really for, and the flat file alone would have hidden that.

### 03, 100 flat Mic-E bursts

| Receiver | DSP rate | Score |
|---|---|---|
| `afsk1200-multi` | 12000 | **100 of 100** |

The one track in the set whose true count is known, which is why it is worth running: it is the
harness's own check that it is not losing frames somewhere between the file and the modem.

## Reading the corpus

The tracks are FLAC, and this tool decodes FLAC itself (`FlacReader`) rather than asking for a
pre-converted WAV. That is a deliberate choice and not a small one, so here is the reasoning: a
benchmark that needs a manual conversion step is a benchmark that gets run with a different
conversion each time, and "which resampler did you use" is exactly the question that makes two
scores incomparable. Decoding the container in the tool removes the step and the question.

It is safe to trust because it does not ask to be trusted. FLAC carries a CRC-16 on every frame
and an MD5 of the whole decoded stream in its header, so the file itself says whether the decode
was right, and the report prints the verdict (`MD5 verified`). The decoder is separately pinned
by `FlacReaderTests`, which round-trips every subframe shape the format allows - constant,
verbatim, all five fixed orders, LPC, both Rice partition methods, the escape to raw residuals,
wasted bits, and all four stereo decorrelations - through a small encoder written for the purpose,
and refuses a stream with a flipped bit rather than decoding it.

WAV files work too, and take the same path from the resampler on.

The harness above the reader is pinned by `ScorerTests`, over modulated audio whose frame count
is known. That is the only place it can be pinned: a corpus score has no denominator, so no run
over the real thing can tell you the counter is right. Those tests are what catch a harness that
loses a frame at a chunk boundary, double counts one, or drops the last one for want of a flush.

## Options

```
sm-tnctest <track.flac|track.wav> [more tracks...] [options]

  --mode NAME[,NAME]  modes to score (default afsk1200-multi, the daemon's AFSK bank)
  --rate HZ           DSP rate override (default: each mode's catalogue rate)
  --centre HZ         audio centre override (default: each mode's own)
  --pairs N           diversity-bank width override, where the mode runs a bank
  --channel N         which channel of a multi-channel recording (default: the loudest)
  --skip SECONDS      start this far into each track
  --seconds SECONDS   score only this much of each track
  --frames            list every decoded frame
  --json PATH         write every track's run to PATH, frames included
  --baseline PATH     diff this run's frame sets against a run written by --json
  --quiet             no progress line
```

Several modes in one invocation share the file read and the resample, which is most of the wall
clock for a single-decoder run, so `--mode afsk1200-multi,afsk1200` is much cheaper than two runs.

`--skip` and `--seconds` are for iterating: a receive-path change can be pointed at two minutes
of the track in a few seconds, and only the full run needs to be scored.

The default channel is the loudest one. The WA8LMF discs are dual mono so it does not matter for
them, and the per-channel peak levels are printed either way, which is how anyone would notice if
a future recording was not.

## Working with a baseline

```
# before a change
sm-tnctest track1.flac --json before.json

# after
sm-tnctest track1.flac --baseline before.json
```

```
  against baseline
    afsk1200-multi           1007 -> 1011 (+4), 9 newly decoded, 5 no longer decoded
```

The last two numbers are the point. A change that gains nine and loses five is not the same
change as one that gains four and loses nothing, and their totals are identical.

The diff is over frame *contents*, so decoding one more copy of something already in the baseline
moves the score and neither of the other two numbers. That is the right way round: it says the
change found another reading of a transmission you already had, not a station you did not.
