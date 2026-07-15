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

## Results — 2026-07-15, NinoTNC firmware 3.41

Rig: the loop above on `plughw:3,0` (CM108B, full-speed USB behind an EHCI hub),
NinoTNC in software-controlled mode (DIPs 1111), MIC TXA range, 1x / AC / CD jumpers,
driven by `nino-bench` (`tools/Packet.SoundModem.NinoBench`). NinoTNC-side truth read
from the GETALL diagnostic registers (`AX25RxPkts` / `IL2PRxPkts`), not just KISS
delivery. RX level at our ADC ≈ 0.17–0.28 full-scale peak per mode ("GOOD" band —
no deviation/pot change needed).

**All six supported pairs pass bidirectionally, sustained:**

| pair (ours : NinoTNC mode) | NinoTNC → us | us → NinoTNC | DCD assert lag | DCD release lag |
|---|---|---|---|---|
| afsk1200 : 6 | 25/25 | 25/25 | avg 13 ms | avg 100 ms, max 269 ms |
| bpsk300 : 8 | 15/15 | 15/15 | avg 48 ms | avg 160 ms, max 389 ms |
| qpsk2400 : 11 | 15/15 | 15/15 | avg 33 ms | avg 86 ms, max 120 ms |
| qpsk3600 : 5 | 15/15 | 15/15 | avg 53 ms | avg 85 ms, max 120 ms |
| fsk9600 : 0 | 25/25 | 25/25 | avg 0 ms, max 2 ms | avg 171 ms, max 242 ms |
| fsk9600-il2p : 2 | 25/25 | 25/25 | avg 0 ms, max 2 ms | avg 167 ms, max 240 ms |

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

Known behaviours (NinoTNC-side, not ours):

- **QPSK from cold wants ≥500 ms TXDELAY**: the very first QPSK burst after a mode
  change at 300 ms TXDELAY sometimes misses (its demod locking from cold); once warm,
  300 ms is fine. Recommend `txdelay-ms: 500` on QPSK ports facing NinoTNCs — matches
  TARPN's own advice.
- The spontaneous once-per-minute diagnostic frame (`TNC>USB`, `=00:` registers) shows
  up as a KISS data frame — hosts should not treat it as channel traffic.
