# Receive-performance roadmap

The durable plan for squeezing the remaining performance out of the IL2P receive path,
written 2026-08-06 after the PR #236 campaign rebuilt the differential BPSK chain. This is a
living document in the plan.md style: workstreams get dated status notes as they move, and a
workstream that lands gets its ledger entry in [mode-validation.md](mode-validation.md) - the
ledger stays the record of what was proven, this stays the record of what is worth doing next
and why.

## Where we are (the PR #236 baseline)

Measured on the sim ladder (`sm-ota sim`, default 9-branch bank, 60-byte frames, 300
bursts/point, 3 kHz SNR convention) and the real-audio corpora:

| Instrument | Reading |
|---|---|
| AWGN ladder | 49 % @ −5 dB, 82 % @ −4, 94 % @ −3 (was 4/46/81 before #236) |
| Carrier-offset sweep 0-33 Hz @ −3 dB | 92-97 % flat |
| CCIR Good (0.5 ms / 0.1 Hz) | 33 % @ −4 dB, outage-bound above |
| CCIR Poor (2 ms / 1 Hz) | ~29 % ceiling - equaliser-bound |
| GB7RDG 24 h miss corpus, demodulated byte-exact | 32 of 37 (`NINOTNC_ASPIRATION=1`) |
| GB7RDG 24 h miss corpus, **host-delivered** | 22 of 37 (3 CRC-verified + 19 trailer-corroborated) |
| bpsk1200 AWGN | 86 % @ +2 dB (was 20 %) |

The demodulated/delivered split matters and was initially conflated: the aspiration test counts
`FrameDecoded`, which includes withheld RS-only readings. Before trailer corroboration landed
(2026-08-06, same day as this doc), only 3 of the 32 byte-exact frames actually verified their
CRC and reached a host. The other 29 failed on 2-10 grazed trailer bits, clustered in the last
positions - the end-of-burst pulse-truncation cliff landing on the wire format's only
unprotected bytes. Corroboration (`Il2pReceiver`: recompute the trailer the payload implies,
deliver within 4 wire bits, ~1.1e-5 false-accept - CRC-order evidence) recovered 19 of them.

The demodulator now sits within ~0.5-1 dB of the matched-filter bound for this waveform on
AWGN. The remaining structural losses are **above** the demodulator: everything from the bit
decision upward is hard-decision, single-look, and causal. The wire format is NinoTNC IL2P+CRC
and is not negotiable; every workstream below is receive-side only unless marked otherwise.

## The 40 m capture campaign (live)

Tom's concern, 2026-08-06: the 37-frame miss corpus is small, and improvements are starting to
overfit its tail. The fix is a bigger, fresher corpus, and it is being collected now:

- **`pdn-capture-40m.service`** on this box (systemd, enabled at boot, `Restart=always`, runs
  as `tf`) drives the FlexRadio 6500 at `10.45.0.76` - **receive only**: headless slice A /
  DAX channel 2, ANT1, dial 7.049450 USB, station name `pdn-rx-capture`, KISS bound to
  localhost with no hosts attached, `transmitFilterHighHz: 0` so nothing TX-side on the radio
  is written. Started 2026-08-06 13:54 UTC.
- Modems live on the standard 40 m slots: `afsk300-il2pc` at 7.050300, `bpsk300` at 7.051600,
  with the ID-beacon ghost listening at +200 Hz.
- Three records accumulate under `/home/tf/capture-40m/`:
  - `raw/` - **continuous 12 kHz chunked WAVs** via the daemon's new `rawCapture` feature
    (15-minute chunks, 120 GB budget ≈ 60 days, oldest pruned). This is the re-scorable
    ground truth: every future receiver change can be replayed against every hour of it.
  - `framelog/frames.sqlite` - every frame the live receiver decoded, with quality fields.
  - `survey/` - curated bursts the station could not read (2 GB budget).
- The container's disk was grown 100 → 200 GB (`pct resize` on `root@10.45.0.10`) to fund it.
- **Methodology when harvesting**: re-run the raw chunks through the current receiver and diff
  against the frame log to find in-stream losses; extract bursts that fail isolated decode
  into a `misses-v2` corpus with expected bytes from context (retries, digipeats) or operator
  logs where available. The GB7RDG benchmark had a NinoTNC as referee; this capture has none,
  so "what should have decoded" comes from burst-level evidence (a clean burst that RS-fails)
  rather than a second receiver - weaker per-frame ground truth, far more frames.

There is no automatic end date: 24 h was the ask, the budget carries two months, and the value
grows with band variety (a weekend contest, a geomagnetic upset, summer QRN). Check on it with
`systemctl status pdn-capture-40m` and `ls -lh /home/tf/capture-40m/raw | tail`.

## Workstreams, ranked

Ranked by expected real-world return per unit of effort, with the reasoning pinned so a future
session can re-rank honestly as facts change.

### 0. Watterson masks and the accept discipline (cross-cutting; Tom, 2026-08-06)

Extend the MS110D programme's two instruments to the audio modems: **per-mode Watterson
performance masks** frozen as tests, and the **accept discipline** that goes with them. Most
of the machinery exists - `WattersonChannel` is itself validated, `SimChannel` pins the
Good/Poor geometries so packet and MS110D masks stay one rig, and `SimModem` makes the
generate-channel-score loop mode-generic. What is missing is thin and is this workstream:

- **Two tiers.** A small always-blocking smoke per mode - one anchor rung, 30-50 bursts,
  threshold far enough under the measured value that binomial wobble cannot flake it, sized
  to catch a regression of a dB or more. Plus an env-gated full ladder (the A/B instrument)
  over the whole SNR x channel x CFO grid. Deterministic seeds throughout: a mask failure
  reproduces exactly, or it is not a mask failure.
- **Masks from measured reality, never aspiration.** bpsk300/bpsk1200 masks come from the
  2026-08-06 campaign numbers; the QPSK family's come from their current, known-poor levels,
  which documents the truth and catches further slippage - when those modes get their own
  receive campaign the masks move up with the ledger entry that justifies it. Aspirations
  stay in the aspiration suites, where the house discipline already keeps them non-blocking.
- **The discipline.** A PR touching a modem's receive path runs that mode's full ladder A/B
  and quotes it; a mask moves only with a mode-validation.md entry. This is what the #236 and
  erasure campaigns did by hand; the workstream makes it the floor, not the habit.
- **The honest limit, stated up front.** Masks pin the receiver against the model. The
  Watterson sim carries no static crashes, no SSB filter tilt, no AGC and our own TX shaping
  rather than a NinoTNC's - so a green mask means "no regression against the model", and the
  capture campaign stays the truth about the band. Two instruments, two jobs.

Why it ranks where it does: three near-misses in two days were caught only by ad-hoc sweeps
or one-off pins (the mid-branch CFO hole, the coherent-margin inversion, the erasure
interpolation hazard), the ~1.4 dB banked this week exists only as ledger prose until a mask
holds it, and workstreams 5-7 below each need exactly this instrument to develop against.

### 1. Soft-decision and erasure Reed-Solomon decoding (the big lever)

Everything above the demodulator is hard-decision; that is the textbook ~2 dB give-away, and
it is the largest remaining item at fixed wire format. Receive-side legal, three steps of
increasing depth:

- **Erasure marking.** RS corrects twice as many erasures as errors. The DF-DD detector's
  projection magnitude is a free per-symbol confidence; a fade is visible in the envelope.
  Byte-level erasure flags roughly double the correctable span through a fade - worth little
  on AWGN, a lot on the fading channels 40 m actually serves.
- **CRC-arbitrated chase decoding.** Flip the m least-confident bits in combinations, re-run
  RS + CRC per candidate. The CRC arbitrates, so it cannot deliver garbage. Of the order of
  1 dB at the knee, where the real frames die.
- **Full soft RS (GMD/KV)** if the first two pay: diminishing returns beyond chase for these
  block sizes.

**Dependency**: the RS decoder lives in **M0LTE.Il2p** (NuGet, separate repo). The deframer
needs a soft-bit or erasure-hint input surface. This is the one workstream that cannot land
from this repo alone - it needs the Il2p repo opened alongside. Estimated 1-2 dB AWGN
equivalent, more under fading.

**Status 2026-08-06 (evening): the erasure leg is built and measured.** M0LTE.Fec 0.3.0
gained errors-and-erasures decoding with a caller-set cap on located errors (an attempt that
spends the whole parity budget is pure interpolation and always "succeeds" - found the hard
way, pinned by test); M0LTE.Il2p 0.2.0 gained `PushBit(bit, confidence)` and a failed-block
retry ladder of (erasures, cap) rungs that each keep two parity symbols in reserve; the BPSK
demodulator emits per-symbol confidence from the DF-DD decision magnitude. Measured: **AWGN
−5 dB 49 % → 55 %, −4 dB 82 % → 88 %, −3 dB 94 % → 96 %** (~0.3-0.4 dB); Good/Poor unchanged
within noise; the miss corpus unmoved at 22/37 delivered - its residue is not
payload-RS-limited (5 cases never demodulate, 10 fail only on obliterated trailers). The
header deliberately gets no erasure rescue (its 2-parity code cannot afford speculative
erasures without hallucinating collections). What remains of this workstream, in value order:
**CRC-arbitrated chase** (bit-flip retries on the least-confident bits, which handles the
scattered-error pattern erasures cannot and is the header's only rescue), and a look at
whether the **DCD-falling deframer reset kills fade-straddling frames** before erasures even
see them - a clean-dip probe showed the chain rides amplitude alone easily, so the Good
channel's losses are phase/timing collapse plus that reset, not byte damage.

### 2. Trailer corroboration - LANDED 2026-08-06

Measurement redirected this slot on the day the roadmap was written: probing why 29 corpus
frames demodulated byte-exact without delivering exposed the trailer-cliff mechanism above, and
the fix (corroboration in `Il2pReceiver`) took the corpus from 3 to 22 host-delivered - worth
more than any planned demodulator change, for two hundred lines. Left behind it, two follow-ups:

- **Tail handling in the demodulator.** The cliff itself is partly recoverable: the matched
  filter loses the final symbols because both ends truncate the last pulse. Our own modulator
  does too - extending TX rendering by the pulse span (wire-compatible, it is just carrier
  tail) would make our transmissions decode better at every receiver, and an RX-side
  asymmetric-tail treatment could shave the remaining grazed bits. Also worth probing: whether
  bpsk300's DF-DD reference decays into the trailer when the tail fades.
- **Ensemble decode-any**, demoted from this slot: the measured union gain was one corpus case
  (coherent copied `GB7OXF-180252` where DF-DD did not) plus general detector diversity under
  fading. Still cheap and worth having, but corroboration ate most of its lunch. Re-evaluate
  against the capture campaign's `misses-v2`.

### 3. Retransmission soft combining (the sleeper)

AX.25 retries are byte-identical frames seconds apart - the #236 guard bug proved the stream
is full of them. Hold soft symbols of bursts that failed to decode; when a later burst
correlates as the same transmission, combine (MRC on the aligned soft symbols) and decode the
sum. +3 dB on paper for two copies, more than that against fading because the two fades are
independent. Novel for this ecosystem, receive-only, wire-legal. Needs: burst soft-buffer
(bounded), alignment via the sync word + correlation, and honest accounting so a combined
decode is badged as such in the frame log.

### 4. Two-pass, non-causal burst processing

The receiver is strictly causal; a burst is short and cheap to buffer. First pass: decode as
today while estimating the burst's timing/phase/amplitude trajectory end-to-end. Second pass
(only when the first fails): re-decode with the smoothed, non-causal estimates. Claims the
remaining ~0.5 dB of DPLL timing loss, and is the natural place to compute fade-envelope
erasure flags for workstream 1. Fits inside `BpskDemodulator`/`BpskMultiModem` without touching
the streaming surface, since branches already hold per-burst state.

### 5. Poor-channel MLSE (the known mode limit, half-liftable)

CCIR Poor's 2 ms echo at 300 Bd spans ~0.6 symbol: the composite channel fits a **2-4 state
MLSE/BCJR** per branch - nearly free at this rate, and optimal for the ISI half of Poor's
losses. Expected: the ~29 % ceiling moves to 50-60 %. The other half is flat outage (both
Rayleigh paths fading together for 60-150 symbol times) which **no receiver fixes at fixed
wire format** - there is no interleaving to bridge it. Beyond MLSE, Poor is workstream 8's
problem.

### 6. Impulse-noise instrumentation, then mitigation

Every evidence instrument this project owns is Gaussian - the Watterson sim, every ladder.
Real 40 m evenings are static crashes and QRN, and the receiver has no blanker and no
heavy-tail-aware metric; every serious HF modem has one. Possibly the most real-world dB per
line of code on this list, and currently unmeasurable. Order of work is therefore fixed:
**first the instrument** (an impulsive-noise channel profile in the sim, plus scoring against
the raw 40 m capture above, which will contain real summer-evening QRN), **then** the blanker
or clipped-metric fix, sized by what the instrument shows.

### 7. Per-station acquisition priors

The daemon hears the same handful of stations all day. Cache each station's measured carrier
offset, timing skew and level (keyed by heard callsign) and warm-start acquisition from the
cache. Targets short supervisory frames, which die in acquisition, and the corpus says the
real misses skew exactly there. Modest scope; measurable directly against the capture
campaign's frame log (same station, decode rate before/after).

### 8. Waveform escalation between consenting stations (strategic)

The honest answer to Poor, and to every ceiling above: the repo already carries ARDOP, MS110D
and the FreeDV datac OFDM family, which have the equalisers and interleaving this waveform
lacks. Blue sky: per-neighbour capability discovery - speak IL2P BPSK as the lingua franca,
recognise a pdn-soundmodem peer, escalate that link to a Poor-capable waveform automatically.
Protocol and node work, not receiver work; parked here so the receive plan says out loud where
its own ceiling is.

## Discipline

- Every workstream that changes decode behaviour lands with its sim-ladder A/B and a corpus
  re-score, and gets its dated entry in [mode-validation.md](mode-validation.md).
- The 37-frame corpus is close to exhausted as a discriminator (32 demodulate, 22 deliver); do
  not tune against its tail. New tuning decisions wait for the capture campaign's `misses-v2`.
- Nothing here changes a single transmitted bit: NinoTNC interop is ground truth, and the
  parity/QtSM/off-air suites stay the regression gate.
