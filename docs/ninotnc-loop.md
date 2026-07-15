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
