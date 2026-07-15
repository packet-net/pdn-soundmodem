# NinoTNC ↔ CM108 bench loop (the cable-edition interop rig)

Wiring a CM108 Radio Widget (Tom Wardill 2E0JJI, OARC — pads: PTT, OUT, IN, SQL, GND)
directly to a NinoTNC N9600A's radio port gives a wired "RF" link between pdn-soundmodem
and the NinoTNC — the interop gate for every mode, no radios or spectrum needed.

## NinoTNC DE-9 radio port (female on the TNC)

Verified from TARPN's own cable documentation (TK-790 wiring page + operator's manual):

| DE-9 pin | Signal | Direction |
|---|---|---|
| 1 | TXA — transmit audio | out of TNC |
| 2 | TX inhibit (>1 V inhibits) | into TNC |
| 3 | PTT — "pulls to near ground" to key | out of TNC (open collector) |
| 5 | RXA — receive audio | into TNC |
| 6 | GND | — |

## The loop

| Widget pad | → | NinoTNC DE-9 | Purpose |
|---|---|---|---|
| GND | → | pin 6 | common ground — connect first |
| OUT (CM108 audio out) | → | pin 5 (RXA) | our transmissions into the NinoTNC |
| IN (CM108 audio in) | → | pin 1 (TXA) | NinoTNC transmissions into us |
| PTT | — | leave unconnected | no radio to key; both ends RX continuously |
| SQL | → | pin 3 (PTT), optional | the NinoTNC keying (active-low) closes the widget's SQL input → visible as the CM108 VOLDOWN event: a hardware "peer is transmitting" indicator, later usable for TXDELAY measurement |

## Levels

- **Turn the CM108's Auto Gain Control OFF** and set a fixed capture gain. AGC on was
  silently applying ~12 dB (rxPeak 0.198 with, 0.050 without, same decode rates — our
  demodulator power-normalises so it does not care). It matters anyway: with AGC on, any
  level or deviation reading is the codec's opinion rather than the signal's, so the
  `--level-check` verdict is meaningless. Bench-good fixed setting on this rig:
  `amixer -c 3 sset 'Auto Gain Control' off; amixer -c 3 sset Mic 32` (+8 dB) → rxPeak
  ≈ 0.2, deterministic.
- **NinoTNC → widget** (pin 1 → IN): the widget's IN is a mic-class input. Put the
  NinoTNC's TXA range on **MIC (0–200 mV)** and start with TX-DEV low; raise while
  watching our waterfall (`/tools/waterfall` or the daemon) — healthy is a bright but
  non-clipping band over a visible noise floor.
- **Widget → NinoTNC** (OUT → pin 5): the NinoTNC is "comfortable with 3 V P-P and can
  do pretty good work down to around 0.3 V P-P" (operator's manual) — a headphone-class
  CM108 output reaches that easily; ~50 % playback gain in alsamixer is a good start,
  then trim by the NinoTNC's receive indication.

## Procedure

1. Same mode both ends (start 1200 AFSK, then the IL2P BPSK/QPSK/9600 modes — the modes
   GB7RDG actually runs, and the ones with no corpus yet).
2. Our side: `pdn-soundmodem --device <widget> --modem 0:<mode>` (or a PDN
   `kind: soundmodem` port); NinoTNC on USB serial into PDN as usual.
3. Send frames both directions (hail / axcall / kissutil); check decode both ways, watch
   DCD (`channelBusy`) and the waterfall.
4. Record WAV corpora of the NinoTNC's transmissions per mode (`arecord` from the widget
   or a capture tap in the daemon) — these become committed/local regression material,
   answering the open wire questions: 9600 GFSK vs the direwolf-validated baseband, QPSK
   phase maps, and the legacy-max-FEC bit reading.

Sources: [TK-790 cable doc](https://tarpn.net/t/builder/builders_wiring_kenwood_tk790.html)
(pins 1/3/5/6), [operator's manual](https://tarpn.net/t/nino-tnc/n9600a/n9600a_operation.html)
(levels, PTT electrical, pin-2 inhibit), [CM108 Radio Widget](https://www.tindie.com/products/tomwardill/cm108-radio-widget/)
(pad semantics, SQL = VOLDOWN event).

## Results — 2026-07-15, NinoTNC firmware 3.44

Rig: the loop above on `plughw:3,0` (CM108B, full-speed USB behind an EHCI hub),
NinoTNC flashed to **v3.44** (the current flashtnc release) and in software-controlled
mode (DIPs 1111), MIC TXA range, 1x / AC / CD jumpers,
driven by `nino-bench` (`tools/Packet.SoundModem.NinoBench`). NinoTNC-side truth read
from the GETALL diagnostic registers (`AX25RxPkts` / `IL2PRxPkts`), not just KISS
delivery. RX level at our ADC ≈ 0.17–0.28 full-scale peak per mode ("GOOD" band —
no deviation/pot change needed).

**Every mode the NinoTNC has that is not C4FSK now has a pdn-soundmodem counterpart,
and all of them pass bidirectionally** (13 of the 15 DIP positions; see § Coverage):

| DIP | pair (ours : NinoTNC mode) | NinoTNC → us | us → NinoTNC | DCD assert lag | DCD release lag |
|---|---|---|---|---|---|
| 0 | fsk9600 : 0 | 12/12 | 12/12 | avg 0 ms | avg 67 ms, max 90 ms |
| 2 | fsk9600-il2p : 2 | 12/12 | 12/12 | avg 0 ms, max 2 ms | avg 65 ms, max 90 ms |
| 4 | fsk4800-il2p : 4 | 12/12 | 12/12 | avg 1 ms, max 8 ms | avg 64 ms, max 90 ms |
| 5 | qpsk3600 : 5 | 12/12 | 12/12 | avg 18 ms, max 31 ms | avg 86 ms, max 120 ms |
| 6 | afsk1200 : 6 | 12/12 | 12/12 | avg 13 ms, max 31 ms | avg 115 ms, max 209 ms |
| 7 | afsk1200-il2p : 7 | 12/12 | 12/12 | avg 8 ms, max 37 ms | avg 130 ms, max 362 ms |
| 8 | bpsk300 : 8 | 10/10 | 10/10 | avg 10 ms, max 31 ms | avg 161 ms, max 389 ms |
| 9 | qpsk600 : 9 | 10/10 | 10/10 | avg 7 ms, max 31 ms | avg 149 ms, max 631 ms |
| 10 | bpsk1200 : 10 | 10/10 | 10/10 | avg 45 ms, max 362 ms | avg 87 ms, max 149 ms |
| 11 | qpsk2400 : 11 | 12/12 | 11/12 | avg 8 ms, max 31 ms | avg 89 ms, max 121 ms |
| 12 | afsk300 : 12 | 8/8 | 7/8 | avg 11 ms, max 29 ms | avg 398 ms, max 1230 ms |
| 13 | afsk300-il2p : 13 | 8/8 | 8/8 | avg 12 ms, max 31 ms | avg 379 ms, max 750 ms |
| 14 | afsk300-il2pc : 14 | 8/8 | 8/8 | avg 23 ms, max 152 ms | avg 276 ms, max 645 ms |

The stray single misses (mode 12 and 11 at 7/8 and 11/12 us→NinoTNC) are the NinoTNC
declining one of ours; every other cell is clean, and no run recorded a capture xrun.


## How short can TXDELAY be?

Measured per direction, because the two ends have different minima and conflating them
hides which is the constraint (`--txdelay-ms` sets the NinoTNC's, `--our-txdelay-ms` ours).
Preambles are quantised to 16-bit words, so the applied value is the GETALL `PreamblCnt`
readback, not the request.

| direction | afsk1200 (6) | fsk9600 (0) | bpsk300 (8) | qpsk2400 (11) | qpsk3600 (5) |
|---|---|---|---|---|---|
| **NinoTNC → us** (our demod locking) | 5/6 @ 13 ms | 6/6 @ 20 ms | 6/6 @ 20 ms | 6/6 @ 20 ms | 6/6 @ 18 ms |
| **us → NinoTNC** (its demod locking) | 6/6 @ 20 ms | 6/6 @ 20 ms | 6/6 @ 20 ms | **needs ~100 ms** | — |

So **20 ms is enough for everything except the QPSK modes**, and our own demodulator locks
on ~20 ms in every mode tested. QPSK-2400 falls off a cliff between 75 ms (5/6) and 50 ms
(0-1/6) — repeated with the mode verified, 2 runs each.

**That cliff is our fault, not the NinoTNC's** (issue #1). Its receiver decodes its *own*
47 ms-preamble bursts when they are recorded and replayed into it (3/4; control: its
300 ms bursts 4/4), so it can acquire on 47 ms of a compliant signal. Ours is not
compliant: our QPSK-2400 measures **4506 Hz** of 99% occupied bandwidth against Nino's
**1992 Hz** and his published 2400 Hz spec, because `QpskModulator` shapes phase only and
never pulse-shapes I/Q. His ~2400 Hz receive filter therefore sees a distorted signal and
needs longer to lock. Expect QPSK to join the other modes at 20-50 ms once #1 lands — and
note the same defect would splatter on air.

**Retraction:** an earlier note here claimed "QPSK from cold wants ≥500 ms TXDELAY". That
was wrong on both halves. The failures that produced it were the QPSK *modulator* bug
(integer-boundary phase synthesis, fixed above) plus unreliable first frames after a mode
change — not preamble length. 100 ms is enough, and even that ceiling is the NinoTNC's,
not ours.

## Coverage

Against Nino's v44 mode table ([v44-op-modes.png](https://github.com/ninocarrillo/flashtnc/blob/master/v44-op-modes.png)
+ the "MODE SWITCH MAPPING v3/4.43" block in flashtnc's release-notes.txt, which is also
where the per-mode symbol rates, carriers and OBW figures below come from):

| DIP | NinoTNC mode | our modem | state |
|---|---|---|---|
| 0 | 9600 GFSK AX.25 | `fsk9600` | ✅ bench-proven |
| 1 | 19200 C4FSK IL2P+CRC | — | ❌ no C4FSK modem |
| 2 | 9600 GFSK IL2P+CRC | `fsk9600-il2p` | ✅ bench-proven |
| 3 | 9600 C4FSK IL2P+CRC | — | ❌ no C4FSK modem |
| 4 | 4800 GFSK IL2P+CRC | `fsk4800-il2p` | ✅ bench-proven |
| 5 | 3600 QPSK IL2P+CRC | `qpsk3600` | ✅ bench-proven |
| 6 | 1200 AFSK AX.25 | `afsk1200` | ✅ bench-proven |
| 7 | 1200 AFSK IL2P+CRC | `afsk1200-il2p` | ✅ bench-proven |
| 8 | 300 BPSK IL2P+CRC | `bpsk300` | ✅ bench-proven |
| 9 | 600 QPSK IL2P+CRC | `qpsk600` | ✅ bench-proven |
| 10 | 1200 BPSK IL2P+CRC | `bpsk1200` | ✅ bench-proven |
| 11 | 2400 QPSK IL2P+CRC | `qpsk2400` | ✅ bench-proven |
| 12 | 300 AFSK AX.25 | `afsk300` | ✅ bench-proven |
| 13 | 300 AFSK IL2P | `afsk300-il2p` | ✅ bench-proven |
| 14 | 300 AFSK IL2P+CRC | `afsk300-il2pc` | ✅ bench-proven |
| 15 | Set from KISS | n/a | — |

**The gap is C4FSK** (modes 1 and 3) — coherent 4-level FSK, added in firmware 3/4.42,
carrying 19200 bps in 20 kHz OBW and 9600 bps in 10 kHz respectively (2079 Hz / 1039 Hz
outer deviation). It is a genuinely different modem, not a reparameterisation of anything
we have, and it is the one remaining piece of full NinoTNC mode coverage.

DCD lags are `IModem.ChannelBusy` sampled every 2 ms against the capture envelope
(>0.04 assert reference, <0.02 release reference); release lag is dominated by the
deliberate PacketDcd/EnergyBusyDetector hysteresis + hold, i.e. DCD reliably outlives
the carrier by ~0.1–0.4 s and never releases early — the safe side for CSMA.

Fixed along the way (all found by this rig, none by loopback/WAV testing):

- `AlsaPcm`: CM108B capture needs an explicit `snd_pcm_start` (EIO from readi
  otherwise); playback needs `snd_pcm_prepare` after `snd_pcm_drain` (EBADFD on the
  second transmission otherwise).
- `QpskModulator`: phase trajectory is now evaluated in continuous time. The old
  integer-boundary synthesis collapsed the quarter-symbol ramp to a single hard-stepped
  sample and jittered symbol boundaries by ±½ sample at 1800 baud (12 kHz ÷ 1800 =
  6⅔ samples/symbol) — the NinoTNC decoded only ~56–88% of those bursts. Continuous-time
  quarter-symbol ramps decode 100%. (A half-symbol ramp decodes ~7% — the NinoTNC wants
  sharp-ish transitions; keep `TxRampFraction` at 0.25.)
- `Fsk9600Modem`: RX now interpolates ×2 before the DPLL (Dire Wolf demod_9600's
  trick) — at 5 samples/bit the quantised timing nudges cost ~12% of classic-HDLC
  frames (no FEC to hide the resulting bit errors; IL2P at the same baud was already
  100%). At 10 points/bit classic 9600 is 25/25 and DCD assert lag collapsed to ≤2 ms.
- `AfskDemodulator` **discriminator clamp — the big one, and it was costing the proven
  modes too.** Bringing up 300 AFSK (mode 12) it stuck at 3-6 of 8 frames while the FEC
  modes on the same audio scored better — the clue that the bits were marginal, not the
  signal. Recording the link and decoding it offline showed every burst was in fact
  *perfect* (1-4 bit errors, all in the closing flag my expectation had wrong) when a
  **fresh** demodulator saw it, and lossy when a **long-running** one did: accumulated
  state. Logging the envelope trackers found it. With no signal the power normalisation
  divides noise by ~zero and emits full-scale garbage; the trackers learn it, so a burst
  opens with its peaks pinned and its slice point up to ±0.037 off centre against a
  ±0.105 signal — a third of the eye. The clamp that was supposed to bound that garbage
  was a fixed ±1: only ~2x the legitimate ±0.5 at Bell 202's ±500 Hz shift, but **10x**
  the ±0.105 of the ±100 Hz HF modes. It now tracks the mode's own full deviation.
  **Measured on WA8LMF Track 2: @12 kHz single decoder 269 → 426, multi-bank 972 → 983
  (direwolf atest: 970); @44.1 kHz multi-bank 955 → 987 (atest: 983)** — ahead of the
  reference at both rates for the first time. A fixed clamp had been quietly costing real
  off-air frames for the whole project.
- Also tried and rejected, recorded here and in the code so it is not re-attempted: a
  **silence squelch** (zero the discriminator below an absolute power floor). Intuitive,
  and worthless once the clamp is right — Track 2 scored 269 unclamped, 426 clamped, 270
  squelched-only, 427 both. An earlier *relative* gate (power vs a tracked peak) was worse
  than useless: it collapsed Track 2 to 65, because one loud frame parks the tracker and
  squelches every quieter frame after it — exactly what that track exists to test.
- `AfskDemodulator` envelope rates are now per-bit rather than per-sample. The trackers'
  attack/decay were tuned at 1200 baud's 10 samples/bit; at 300 baud's 40 they decayed ~4x
  too fast between transitions. (No-op at 1200 baud/12 kHz, so the benchmark above is
  unaffected by it.)
- `PacketDcd`: added a quiet-symbol drop. Transition scoring can only drop DCD when it
  *sees* badly-timed transitions, so it depended on receiver noise to notice a signal had
  ended — on a truly quiet channel (a squelched radio, this wired loop, or our own newly
  squelching demodulator) DCD latched on for ever. A run of 24 transition-free symbols now
  drops it on its own. This is what tightened DCD release from a ragged 60-300 ms to a
  consistent 60-91 ms on the 300 baud modes.

Known behaviours (NinoTNC-side, not ours):

- **Our own transmissions come back on our own input, ~41 dB down** (measured: our TX
  reads 0.0017 in our capture where the NinoTNC's reads 0.193). Cause is the rig's
  four-wire twisted bundle carrying OUT and IN together — capacitive coupling, and Tom
  confirms the construction. It is **not** the codec monitoring playback into capture:
  muting `Mic Capture` silences our own TX too, so it arrives through the mic input.
  Harmless here (the bench scores each direction by the *other* end's report) but it means
  this rig cannot measure our RX in isolation while transmitting; a real installation
  relies on PTT muting.

- **TXDELAY changes take effect one frame late.** The GETALL readback updates
  immediately, but the air does not: with the NinoTNC moved 300 → 50 ms, burst #00 still
  measured 571 ms long and #01 onward 330 ms — a 241 ms excess, i.e. exactly the old
  setting. Change TXDELAY at least one frame before you depend on it, and never measure a
  TXDELAY change on the frame that follows it. (Thanks Tom — called it.)
- **The first frames after a *mode* change are unreliable**, independent of TXDELAY. This
  rig now settles 1500 ms after SETHW (`--settle-ms`) before it trusts anything.
- **SETHW silently fails.** It is fire-and-forget and the firmware never acknowledges it;
  observed on 3.44 refusing a mode-11 change and carrying on in mode 8. Both directions
  then score 0, which reads exactly like "this mode is broken" rather than "the mode never
  changed". `nino-bench` now verifies the running mode from GETALL's `BrdSwchMod` low byte
  and retries, and aborts rather than report a number from the wrong mode. Tracked for the
  PDN driver as packet.net#633.
- The spontaneous once-per-minute diagnostic frame (`TNC>USB`, `=00:` registers) shows
  up as a KISS data frame — hosts should not treat it as channel traffic.
