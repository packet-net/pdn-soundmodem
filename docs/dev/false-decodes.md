# False decodes: what a row may claim about a frame

What the station is allowed to say about a frame it decoded, and the measurement that decided it.

## The report

GB7RDG, 2026-09-12, slot 3 (`bpsk300-il2pc`, 7051.60 kHz, sub-channel 2). Tom: *"I'm seeing what appear to be false decodes of GB7BPQ, mixed with real ones, i.e. a 'frame' tagged with a label on the waterfall but absolutely no trace of a signal visible in the waterfall. ~same strength in SNR, clearly visible vs not visible at all."*

About sixteen such rows a day on that slot. Not a regression: the mechanism landed in v0.26.0 and v0.37.0 and the station's own frame log shows a flat rate since 2026-08-16. It was noticed on a day with good propagation, when the absolute count was at its weekly high.

## What has to line up

Three things, and on that slot all three do.

1. A frame can be accepted on Reed-Solomon alone, with no CRC behind it, when an IL2P+CRC link falls back to reading plain IL2P. That second reading exists for a real reason and is not the fault: a BPQ32 neighbour on this very slot transmits plain IL2P with no trailing CRC and was structurally invisible without it (`Il2pReceiver`).
2. Chase decoding flips the receiver's least-confident bits and re-runs Reed-Solomon until the parity closes. Behind a CRC that is a rescue. On the plain path there is no independent check left at all.
3. The callsign is then read off a header the IL2P wire format protects with two parity symbols, so mis-correction is cheap, and what comes back is a real regular one or two characters out.

The names in that population are the giveaway: `16WBPQ` and `EVWBPQ` for GB7BPQ, `16WLOX-2` for GB7LOX-2, `EI0RSM-13`, `EI0RSX-8` and `EOSRSM-1` for EI0RSI. Sometimes the mis-correction walks all the way back and lands on the correct spelling, and those are the rows an operator cannot tell from a real decode.

## The measurement

Slot 3, receives, since 2026-09-05 (when the `plain_il2p` column was added), out of the station's own `frames.db`. The discriminator is payload corroboration: that band repeats the same frames all day, so a payload seen exactly once in 120,870 rows is very likely a payload nobody sent.

| integrity class | chased | rows | payload seen only once | % |
|---|---|---|---|---|
| CRC ok | no | 5661 | 200 | 3.5% (baseline) |
| CRC ok | yes | 131 | 2 | 1.5% |
| RS only, trailer corroborated | no | 174 | 14 | 8.0% |
| RS only, trailer corroborated | yes | 25 | 4 | 16.0% |
| RS only, no corroboration | no | 452 | 47 | 10.4% |
| **RS only, no corroboration** | **yes** | **131** | **99** | **75.6%** |

So chase decoding is not the fault. Behind a CRC it is the best-behaved class there is, better than the no-chase baseline, and capping `corrected` or turning the chase off would cost that for nothing. The class that fabricates stations is chase with nothing checking it.

The trailing-CRC corroboration path is sound too: a trailer within four wire bits of the one the payload implies is a false accept of about 1.1e-5, the same order as the trailing CRC's own (`Il2pReceiver.CorroborationMaxBits`), and the table agrees with the arithmetic.

## The three rules

### 1. A corrupt destination does not attribute a frame

`Ax25AddressParser.TryParse` read the destination field and discarded the result, so any frame whose source six characters were upper-case alphanumerics got a label however obviously wrong the rest of the address field was. Taking the real GB7BPQ to GB7RDG-2 frame `8E846EA4888E64 8E846E84A0A2E1 31` as the reference, row id=125786 (six bits chased) came in as `8E844C527CAE64 8E846E84A0A2E1 31`: the destination decodes to `GB&)>W`, four bytes wrong, and the source half still spells GB7BPQ.

The rule is not "require both". PD4R-12's beacons carry an all-spaces destination, and requiring both filed a perfectly readable sender as unattributed. The rule is that the destination must **read as a callsign or be genuinely blank**, where blank is all six callsign bytes being shifted spaces, 0x40 on the wire. Anything else is seven bytes of header that did not survive, and the other seven are not evidence of who transmitted.

Measured over a week of the station's own log, counting only rows that carry a label today:

| class | labelled rows | lose the label |
|---|---|---|
| CRC ok | 5660 | 0 |
| CRC ok, chased | 131 | 0 |
| RS only, trailer corroborated | 200 | 0 |
| RS only | 436 | 5 (1.1%) |
| RS only, chased | 85 | 17 (20.0%) |

Zero on everything a CRC or a trailer stood behind, one in five of the class that was fabricating stations. The 35 genuinely blank-destination rows in the same week keep their callsign. On the ARDOP slot it also withdraws nine rows a week where the parser was reading housekeeping text out of an ARDOP frame as a callsign (`522847-3`, off `..House keeping Results`).

### 2. A reading nothing checked names nobody

`plain_il2p AND trailer_near_bits IS NULL AND chased_bits > 0`: the 75.6% class. Such a frame is still listed, still badged **RS ONLY**, still logged and still relayed with its bytes. It simply does not claim a station, and the row says why rather than leaving "unattributed" over an address field that plainly is one.

Nothing downstream loses by it. The class is already withheld from the host, and the links pane already refuses it.

### 3. A band SNR is not drawn beside a reading nothing checked

`snr_db` is not a measurement of the frame. It is in-band power over a rolling minimum noise floor, taken off the display's own FFT lines (`BandActivityTracker`), and three properties make it useless as evidence that a frame is real:

- **It is floored near 6 dB.** Every line summed into it had to clear the burst gate at that ratio, so anything weaker reports null rather than a small number.
- **Its denominator is a minimum**, the quietest of thirty half-second blocks. The time-domain twin of that rule was measured in v0.65.0: the min-tracking floor settles 2.55 dB below the mean of the noise it tracks, and the worst block in 600 s of pure noise read 7.18 dB over the floor.
- **It repeats the previous burst's figure for two seconds** after a run ends. A fabricated frame emitted shortly after an unrelated real transmission on the same band inherits that transmission's SNR exactly, which is precisely the "~same strength in SNR" in the report.

Beside a frame something checked it is a fair strength cue and it stays. Beside one nothing checked it is what makes the row look trustworthy, so it is not drawn. `peak_dbfs` cannot stand in for it: across slot 3 it averages -20.5 dBFS on CRC-valid frames and -20.2 dBFS on the RS-only ones, because it is measuring the sound card and not the frame.

## Where the verdicts are taken, and what is never touched

Both verdicts are properties of the decode, so they are taken once, where the facts behind them live (`DecodeStanding`, read off `FrameQuality`), and carried: to the page's `frame` message, to the uplink, and to the backlog a browser opens on, which reads them back off the frame log's own `plain_il2p`, `trailer_near_bits` and `chased_bits` columns. A rule reapplied at each edge is how a badge came to be dead for a release (docs/dev/receive-levels.md section 6b).

They are display decisions and nothing else. **The frame log and the journal keep what was read**, callsign included, because they are records of what the station heard and because the measurement above was made from exactly those columns. `snr_db` is written and relayed whatever the reading was worth, for the same reason. What changes is what a row claims.

Neither verdict looks at `monitor_only`. That is the operator's routing choice - whether an RS-only frame is handed to the host - and it is identical across every branch of one bank, so the same bits would get different verdicts on a `-nocrc` port and an `-il2pc` one. What a reading established is a fact about the decode.
