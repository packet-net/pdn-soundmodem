# §E4 — MS110D over a real antenna and a real propagation path

**2026-08-03, 07:20–08:30 UTC. First on-air MS110D transmissions from a real antenna, decoded at a remote receiver over an ionospheric/ground-wave path.** **Every waveform of the Phase A/B ladder acquired; WN0-WN7 and WN13 decoded bit-exact; WN8 (16QAM) acquired with a correct WID and then failed to carry data.** Modem revision `708dc87`.

This closes [`ota-air-backlog.md`](../../ota-air-backlog.md) **A9** (§E4, real antenna) — the item that had been blocked on hardware since the campaign began — and supersedes the whole dummy-load framing of [`ota-execution-plan.md`](../../ota-execution-plan.md), which assumed ANT1 was a dummy load and the only listener was a receive loop metres away.

## Station and path

| | |
|---|---|
| Transmitter | FLEX-6500 at `10.45.0.76`, **ANT1 — a real antenna** (no longer the dummy load the docs described) |
| Route | Waveform API IQ (`--route`-equivalent: `sm-ota burst`, `underlying_mode=RAW`), not DAX/DIGU |
| Measured SWR | **1.53–1.64** across 7.126–7.147 MHz — a real antenna's match, not a dummy load's flat 1.0 |
| Power | `--rf-power 48`, peak forward **43.0–47.7 W** measured *during the burst* |
| Frequency | **7.126500 MHz** waveform centre, modem at +2000 Hz → **7.1285 MHz**, occupying 7.12711–7.12989 |
| Receiver | **`wessex.zapto.org`** (southern England), iq48, session dial error **+1.98 Hz** |
| Identification | CW "M0LTE MS110D" at 30 wpm, at the start of each transmission group |

Two other receivers were characterised and rejected for scoring: **M0XDK-1** (Northampton, IO92mh, active loop) could not hear us at all at 50 W while hearing other UK stations at 20–29 dB; **M0EYT** (Wimborne, IO80xs) heard us but at only +2.6…+5.6 dB SNR in 3 kHz, below threshold for everything above WN1, and with a **−64.8 Hz** dial error of its own.

## Results — every waveform bit-exact

| WN | Modulation / rate | Acquired | WID | CFO | Payload | BER |
|---|---|---|---|---|---|---|
| 0 | Walsh 75 bps | ✅ | match | −0.3 Hz | 8/8 | 0 errors |
| 1 | BPSK r1/8 150 bps | ✅ | match | −0.2 Hz | 64/64 | 0 errors |
| 2 | BPSK r1/4 300 bps | ✅ | match | −0.2 Hz | 160/160 | **0.00E+000 bit-exact** |
| 3 | BPSK r1/3 600 bps | ✅ (2nd try) | match | −0.1 Hz | 224/224 | **0.00E+000 bit-exact** |
| 4 | BPSK r2/3 1200 bps | ✅ | match | −0.1 Hz | 480/480 | **0.00E+000 bit-exact** |
| 5 | BPSK r3/4 1600 bps | ✅ | match | −0.1 Hz | 736/736 | **0.00E+000 bit-exact** |
| 6 | QPSK r3/4 3200 bps | ✅ | match | −0.2 Hz | 1504/1504 | **0.00E+000 bit-exact** |
| 13 | QPSK r9/16 2400 bps | ✅ | match | −0.2 Hz | 1120/1120 | **0.00E+000 bit-exact** |
| 7 | 8PSK r3/4 4800 bps | ✅ | match | −0.3 Hz | 2272/2272 | **0.00E+000 bit-exact** |
| 8 | 16QAM r3/4 6400 bps | ✅ | match | −0.3 Hz | 3040/3040 | **4.91E−001 — acquires, cannot carry** |

**WN8 is the ceiling of this path.** It acquired cleanly (WID correct, CFO −0.3 Hz — the receiver found and identified the burst exactly as for every other rung) and then produced BER 0.491 with `burst end: SignalLost`: 16QAM needs more SNR than a 48 W NVIS path delivered at 08:33 UTC. That is the honest shape of a link budget running out, not a demodulator fault, and it is consistent with the ledger's standing note that WN8 is the hardest point and measured-only against the mask.

Every other decode ended on **EOM**, i.e. the burst framing was recovered end to end, not merely acquired. WN3's first attempt missed with a search metric of 0.290 against a 0.32 threshold and decoded bit-exact on a retry 70 s later — a fade, not a wall, and the only failure of the session once the receiver was correct.

WN6 deserves note: the ledger records it as needing "a disciplined RX reference (receiver phase-noise, not a modem defect; #102)". Wessex is disciplined, and WN6 decoded bit-exact first time. That is consistent with #102's diagnosis and is the first on-air confirmation of it against a receiver that is not ours.

## What actually blocked this for an hour, and the lesson

**The per-receiver dial correction is the whole ballgame.** The demodulator's acquisition grid is ±75 Hz. Against M0EYT the combined reference error was −64.8 Hz — inside the grid but at its edge — and *nothing acquired* through eight bursts at 45–50 W across WN0/1/2/4. Against Wessex the error was +1.98 Hz and *everything* acquired first time. The handover's standing instruction ("re-measure `--dial-correction` every session, it blocks everything") understates it: it must be re-measured **per receiver**, and a receiver whose error sits near the grid edge will present as a broken demodulator.

Method that works, and the one that does not:
- **Works:** `sm-ota tone` with the capture tuned at the waveform centre, and read its own analyser's "tone 1: expected/found" line.
- **Does not:** a hand-rolled differential of transmit-vs-control spectra over a wide window. Tried here, it locked onto a neighbouring carrier and produced a confident, precise, wrong answer of −517.8 Hz.

## Harness defects found (not yet fixed)

1. **`sm-ota burst` and `tone` key the transmitter even when the capture failed to start.** Three transmissions went out unmeasured when the receiver answered 503. Nothing should key until the receiver is confirmed recording.
2. **Peak forward power was computed and then discarded on every data burst** — `Report` printed it only alongside a valid SWR, and SWR is deliberately not evaluated on a modulated envelope. Fixed in this session: forward power and SWR now print independently, with their different preconditions stated. This is what finally showed the burst reaching 43–48 W.
3. **The transmit report's keyed duration is wrong for the burst path** — it reported 0.03 s for a 1.4 s burst, while 30 FWDPWR samples at 20 fps prove ~1.5 s of key-down. Cosmetic, but it looks exactly like a fault and cost time.
4. **`ladder` ignores `--rf-power-ceiling` and `--max-swr`**, so it aborts at the dummy-load-era 1.50 SWR limit and refuses power above 30 even when told otherwise.
5. Rapid sequential bursts exhaust an UberSDR instance's session allowance (503). A campaign wanting many bursts should hold **one** capture open and transmit into it, scoring by timestamp.

## Operating notes

- **The SWR abort threshold of 1.50 is a dummy-load rule** and must be raised (2.0 used here) for a real antenna. The standing note already says it exists "to catch a wrong or disconnected load, not to protect a PA in no danger".
- **A clear-frequency check must cover the neighbours, not just your own footprint.** An initial check of ±1.5 kHz called 7.1465 MHz clear while an LSB QSO on 7150 overlapped the top 600 Hz of the slot — caught by the operator, not the instrument. The check now requires the slot **and 3 kHz either side** to sit near the noise floor, which is what selected 7.1285 MHz.
- Band survey: 7.059–7.197 MHz swept via Wessex in three iq48 spans; candidate slots ranked by max-hold (an SSB station between overs is still somebody's frequency). The three quietest windows were all inside 7.175–7.200, which IARU R1 reserves for intercontinental priority, and were rejected on that ground.

## Files

`captures.sha256` lists every scored capture; the `.json` sidecars carry each capture's receiver description, sample-0 UTC and SHA-256. The WAVs themselves are held outside git (≈5 MB each).
