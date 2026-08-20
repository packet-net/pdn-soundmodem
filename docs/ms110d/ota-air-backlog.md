# MS110D OTA - the on-the-air backlog

Everything that is **blocked on hardware**: the radio, the receiver, a physical change at a site, or another operator. Kept separate from [`ota-handover.md`](ota-handover.md), which is the roadmap of work to be *built*, because these do not compete for the same time - offline work continues while this list waits, and the list is what to reach for the moment a radio is free.

Each item says what blocks it, what it in turn blocks, how to do it, and **what to record**, so a session at the radio does not end with a number nobody can interpret later.

Status: `todo` · `blocked` (waiting on someone/something else) · `done` (moved to the evidence log with a date).

## The list

| # | What | Needs | Blocks | Status |
|---|---|---|---|---|
| A1 | Re-measure `--dial-correction` | radio + receiver | **everything** | todo, every session |
| A2 | §E2 ladder into the dummy load | radio, ~1 h | the whole point of the campaign | **done 2026-07-27**, over the pad-chain rig rather than the dummy load - `evidence/2026-07-27-110d-full-campaign/`. The dummy-load/UberSDR form is superseded by the deterministic rig |
| A3 | Prove the live `sm-ota ladder` path | radio, 10 min | A2 | **done 2026-07-27** - 18 live ladders, 0 run failures; the timebase lined up |
| A4 | Re-measure path SNR margin at the chosen power | radio, 10 min | interpreting A2's top rungs | **done for the pad-chain rig** (~13.5 dB delivered at 3.7 W; ~19-20 dB expected at 15 W); re-measure after any change to the chain. See H2 in `poor-gate-successor-plan.md` for lifting it |
| A5 | Receiver 100 Hz floor - choke or supply | **Tom's hands at the receiver** | the ladder's ceiling; WN7/WN8 Poor | blocked |
| A6 | Receiver front-end gain | Tom, and a shared-instrument decision | more TX power, more path margin | blocked |
| A7 | Settle the Flex's supply for campaigns | Tom | reproducibility of every A2 number | blocked |
| A8 | Two-tone IMD | real antenna + stable tap | §I3 completeness | blocked on A9 |
| A9 | §E4 - real antenna | Tom, licence conditions | A8, A10 | **done 2026-08-03** - see `evidence/2026-08-03-e4-first-on-air/` |
| A10 | m9psy as second site | M9PSY (operator green-light held) | §E4 two-site results | todo - A9 is done; note m9psy was quota-blocked (429) on 2026-08-03, and M0XDK-1/M0EYT were characterised instead |
| A11 | §I2 GPSDO cross-check via m9psy | m9psy, no transmit | confidence in A1's method | todo |
| A12 | §E3 - IQ vs SSB A/B through DIGU | radio, 20 min | measuring what the TX SSB filter costs | todo |
| A13 | §I1 receiver AGC/clipping audit, formally | receiver, no transmit | trusting levels at high power | todo |
| A14 | Kenwood TK-90 bench measurements | a TK-90 | extending §E3 to a real commercial radio | blocked, no radio |

**Everything offline that the campaign needed is built**: `sm-ota ladder`, `score`, `monitor`, the schedule/manifest and the streaming converter are done and have run live (A2/A3). Software gaps that remain are quality-of-life, not blockers, and are listed in [ota-handover.md](ota-handover.md) under "Still open in software". The next thing that moves the campaign forward is a radio - and, for the two Poor points above the rig ceiling, an hour at the pad chain (H2 in [poor-gate-successor-plan.md](poor-gate-successor-plan.md)).

**Added 2026-08-20.** The successor program's hardware legs, in its §4: **H1** a radio evening (§E3 A/B = A12, the Phase 1 hour-long ladder, m9psy = A10) and **H2** the pad-chain ceiling (the no-transmit noise-floor-vs-gain table, then a pad swap if the floor is gain-stage limited), which is what A5/A6 were reaching for on the UberSDR path and is now a better instrument for the same question.

---

## A14 - Kenwood TK-90 bench measurements *(blocked: we do not have one)*

[`radio-tk-90-evaluation.md`](radio-tk-90-evaluation.md) assesses it from the manufacturer's documents and measures the bandwidth penalty in simulation. **Both option boards (KIF-2 filter, KPE-2 ALE) are hard or impossible to obtain**, so the radio as buyable is a 2.2 kHz path with no ALE. What survives is still good - ±0.5 ppm, a proper data port, full CAT - but the measured bandwidth penalty of ~2-2.5 dB is now unavoidable rather than a few tenths, and there is no ALE to offset it. Usable if one is to hand; a poor buy specifically for this purpose.

If one is obtained, six measurements turn every estimate in that document into a number; the first three matter most.

1. The actual passband in **both** directions (sweep DI→RF and RF→DEO). The spec gives a width, not edges, and the emulation had to guess them.
2. Whether KIF-2 is in the **transmit** path. The manual implies the IF filter is, but never says so for KIF-2; if it is receive-only, transmit is stuck at 2.2 kHz and the penalty is worse.
3. Group delay across the passband - the simulation models amplitude truncation only, and group delay at the edges is what hurts a serial-tone waveform.
4. ALC linearity on DATA (`sm-ota tone --tone2-hz` does this directly).
5. Duty cycle, which no Kenwood manual states.
6. Frequency error against RWM, the same reference used for the Flex and the UberSDR.

## A1 - Re-measure `--dial-correction`

**Every session, before anything else.** At 18 MHz the combined TX+RX reference error exceeds the demodulator's ±75 Hz acquisition grid, so getting it wrong means nothing acquires and it presents as a demodulator fault. It has already moved once: 115 Hz, then 129 Hz after a supply change - 0.9 ppm, from changing what the *radio* was powered by.

Do: `sm-ota tone` with a short capture, measure the received tone's offset from nominal, and use it as `--dial-correction` for the session.

Record: the value, the supply in use, and the ambient temperature if it is unusual.

## A2 - §E2 ladder into the dummy load

The campaign's reason for existing: the same bursts the mask suite simulates, put through the same channel rig, sent through real hardware, and scored at a *measured* SNR.

Built and rehearsed offline - measured SNR tracked the request to 0.3 dB from 18 dB down to 3 dB with no radio in the loop. Only the hardware is untested.

Do:
```
sm-ota ladder --wn 6 --snr 15,12,9,6,3,0 --repeats 4 --rf-power 15 \
              --capture-host ubersdr --dial-correction <from A1> --out-dir <dir>
```
then score the capture. Reachable coverage with the current path (see the ceiling note below): the whole AWGN mask except WN8's 16 dB point, and the Poor mask through WN6/WN13.

**Ceiling.** Path margin (~24-26 dB at 15 W) and the −27 dBc close-in artefact both bind at the *top* of the ladder, not the bottom - at 5 dB the artefact sits 22 dB below the injected noise and contributes nothing; at 23 dB it is 4 dB below and costs ~1.5 dB. Useful ceiling ≈ 15-16 dB until A5 and A6 move it.

Record: the capture, its SHA-256, the CSV, the pass gain, the modem's git commit, and A1's value. The manifest does this - do not hand-roll it.

## A3 - Prove the live `sm-ota ladder` path

The live transmit path has never touched a radio. The dry-run path is well tested, and everything downstream of the capture is tested, but the loop of key → transmit → record the key time → line it up against the capture's `Sample0Utc` is not.

Do: one rung, one repeat, 5 W, with a capture. Check the burst lands where the manifest says.

Record: the offset between predicted and actual burst position. If it is not within a second or so, the timebase alignment is wrong and A2's matching would silently mis-attribute rungs.

## A4 - Path SNR margin at the chosen power

~24-26 dB at 15 W, measured once during bring-up. It caps how high the ladder can go, and it will have changed if anything about the bench has.

Do: transmit a carrier at the ladder's power, capture, and read the SNR from a signal-free stretch against the burst.

Record: the figure and the power it was taken at. If it is below ~25 dB, A2's top rungs need reading with the correction noted above.

## A5 - The receiver's 100 Hz floor *(blocked on Tom being at the receiver)*

**The one experiment nobody has run, and the one that would change the most.**

| source of the transmission | ±100 Hz |
|---|---|
| original mains PSU | −8.0 dBc |
| switched-mode mains PSU | −20 dBc |
| large battery | −26.9 dBc |
| the radio's own tune carrier | −27.5 dBc |
| RWM 9.996 MHz - a distant caesium carrier, same receiver | ≈ −27 dBc |

Three independent sources converge on ≈ −27 dBc, one of them a frequency standard that cannot have 100 Hz sidebands of its own. That is far more likely to be the measuring instrument than a coincidence, so the working conclusion - **not proven** - is that ≈ −27 dBc is the UberSDR's own floor.

Do: a common-mode choke on the receiver's antenna feed, *or* change/filter its supply, then re-run the RWM measurement (`sm-ota measure --purity`, masking only ±25 Hz).

Record: the RWM figure before and after. **If it moves, every row in the table above needs re-reading**, and the ladder's ceiling rises.

Ruled out already: the network cable. Pulling the Ethernet mid-transmission made the sidebands ~1.7 dB *worse* and raised the carrier 2.2 dB - a cable acting as part of the RF environment, not one injecting hum.

## A6 - Receiver front-end gain *(blocked: shared instrument)*

Reducing it would allow more transmit power and more path margin, at the cost of changing a configuration other people use. The tool's 30 W ceiling exists to protect the receiver's ADC, and the note in the code says the two should be changed together rather than one silently invalidating the other.

## A7 - Settle the Flex's supply *(blocked on Tom)*

The supply moved the 100 Hz figure by 12-19 dB and shifted the frequency reference by 0.9 ppm. Campaigns need one arrangement, chosen and stuck to, or results are not comparable between sessions.

Options: filter the mains supply, or run campaigns on the large battery. Record which, in every manifest.

## A8 - Two-tone IMD *(blocked on A9)*

Never obtained. The ≈ −28 dBc figure from bring-up is a floor set by path SNR, proved by dropping drive 6.1 dB and watching IMD3 move 1.1 dB where a genuine third-order product must move 12.2 dB. So the transmitter is *at least* that linear and we cannot yet say more.

Carry the caveat: **a fading path corrupts a two-tone measurement much as envelope modulation corrupts SWR.** A real antenna fixes the coupling and reference problems but not that one; a stable attenuated tap is what would.

## A9 - §E4, real antenna

Gated on B3.2 per the original plan; not gated on B3.3/B3.4. Morse ID is built and on by default. Vary power across repeats.

Note when planning: on air, transmitting the ladder's noise lead-in is antisocial in a way it is not into a dummy load. Decide whether to shorten it and lose the self-calibration, or keep it and pick a genuinely clear frequency.

## A10 - m9psy as second site *(blocked on A9)*

M9PSY has given an operator green-light and their instance has a GPSDO. **m9psy cannot hear a dummy load** - it only becomes useful once A9 puts a real antenna up.

## A11 - §I2 GPSDO cross-check

Does not need us to transmit at all: compare a third-party carrier (RWM) heard at both sites, and use m9psy's GPSDO-disciplined instance to confirm the correction method rather than only the local receiver's own drift.

## A12 - §E3, IQ vs SSB A/B

Same seeded bursts through `RAW` IQ and through DAX audio (`FlexStation`, `mode=DIGU`). The difference **is** the TX SSB filter and ALC contribution, measured rather than inferred. Claim a DAX channel other than 1 if SmartSDR is running.

## A13 - §I1 receiver audit, formally

Levels and PSD were done during bring-up; the AGC and clipping audit was never formally repeated after the operating point settled. Worth one clean pass before quoting anything at the top of the power range.

---

## Standing rules for a session at the radio

- **Measure the path with one tone BEFORE running a ladder.** `sm-ota tone --seconds 8` against the
  receiver you will score with, and read its "SNR in 3 kHz". It costs 30 s and it is the difference
  between a ladder and ten transmissions into a closed band. Measured on 2026-08-03: the same tone,
  same power, same receiver read **−1.5 dB at 08:22 UTC and −7.7 dB at 09:36** - the 40 m path lost
  ~14 dB of level in 75 minutes to rising D-layer absorption, which took a ladder that had been
  bit-exact on every waveform to nothing at all. A tone survives that (all its power in one bin);
  a 2.8 kHz burst does not, which is also why an operator watching a waterfall sees the ident and
  not the data.
- **Mid-morning is the wrong time for 40 m NVIS.** Prefer late afternoon/evening, or expect to be
  measuring absorption rather than the modem.

- **SWR abort threshold 1.5:1.** 1 kW dummy load, 100 W radio - it is there to catch a wrong or disconnected load, not to protect a PA in no danger.
- **Power ceiling 30 W**, and it protects the *receiver's* ADC, not the transmitter. A clipped front end makes its own intermodulation, indistinguishable from ours.
- **`--rf-power` has no default, by design.** A power level should be a decision, never an accident.
- **Ask before transmitting.**
- **SWR only means anything on a constant envelope.** The transmitter refuses to evaluate it on a modulated burst; leave that in.
- Every result goes in `docs/ms110d/evidence/<date>-<topic>/` with the capture SHA-256 and the modem's git commit. A score without a revision is uninterpretable - the demodulator changes daily.
