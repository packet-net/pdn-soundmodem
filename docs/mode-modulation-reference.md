# On-air modulation reference - FM vs SSB, and FM deviation targets

How each NinoTNC-lineage mode is carried on the air, for the OTA test harness. Source of truth: **NinoTNC firmware v3/4.44 modulator table** (operator-supplied, 2026-07-28). Companion to [`mode-validation.md`](mode-validation.md).

> **⚠️ Deviation vs channel spacing - do not confuse them.** "Wide" (25 kHz) and "narrow" (12.5 kHz) refer to **channel spacing**, *not* FM deviation. The **actual peak FM deviation** each mode must be transmitted at is the **Tgt Dev** column below (e.g. 3.0 kHz for AFSK 1200, not 12.5/25 kHz). When transmitting an FM mode on the rig, the audio drive **must be calibrated so the achieved peak deviation matches Tgt Dev** - and verified by measurement (FM-demodulate the RSP1 IQ and measure the peak frequency excursion, or a Bessel-null against a single tone). Getting this wrong (under- or over-deviating) degrades or breaks the decode and invalidates the SNR characterisation.

## FM modulators (transmit via the Flex FM mode, at the targeted deviation)

| NinoTNC mode | Tone/subcarrier Hz | **Tgt peak dev kHz** | **Channel** | NinoTNC switch | pdn `ModemCatalog` mode(s) |
|---|---|---|---|---|---|
| AFSK 1200 | 1248 (Bell 202: 1200/2200) | **3.0** | narrow (12.5) | 0110, 0111 | `afsk1200` (+`-fx25`, `-fx25rx`, `-il2p`, `-il2p-nocrc`) |
| GFSK 9600 | 999 | **2.4** | wide (25) | 0000, 0010 | `fsk9600` (+`-il2p`) |
| GFSK 4800 | 500 | **1.2** | narrow (12.5) | 0100 | `fsk4800-il2p` |
| C4FSK 9600 | 1039 | **2.5** | narrow (12.5) | 0011 | `c4fsk9600` |
| C4FSK 19200 | 2079 | **5.0** | wide (25) | **0001** | `c4fsk19200` |
| **QPSK 3600 (AQPSK)** | **1650** | 5.0 (**disputed - see below**) | **narrow (12.5), speaker/mic** | **0101** | `qpsk3600` - 1800 sym/sec on a 1650 Hz carrier |

### Corrections from the NinoTNC v3/4.43 release notes (2025-09-26), applied 2026-08-08

Nino's own switch map settles several things this table had wrong, and adds occupied-bandwidth
figures we never had. **The v43 map is ground truth; where it disagrees with us, we were wrong.**

| Switch | Mode | OBW | Nino's grouping |
|---|---|---|---|
| 0000 | 9600 GFSK AX.25 | 20 kHz | GFSK - needs a 9600 data port or discriminator/varactor |
| 0001 | 19200 C4FSK IL2Pc | 20 kHz | GFSK - data port |
| 0010 | 9600 GFSK IL2Pc | 20 kHz | GFSK - data port |
| 0011 | 9600 C4FSK IL2Pc | **10 kHz** | GFSK - data port |
| 0100 | 4800 GFSK IL2Pc | **10 kHz** | GFSK - data port |
| 0101 | **3600 AQPSK IL2Pc, 1800 sym/sec on 1650 Hz** | - | **FM AFSK - speaker/mic** |
| 0110 | 1200 AFSK AX.25 (legacy packet, APRS) | - | FM AFSK - speaker/mic |
| 0111 | 1200 AFSK IL2Pc | - | FM AFSK - speaker/mic |
| 1000 | 300 BPSK IL2Pc | 500 Hz | Shaped PSK - **SSB radios, or FM radios** |
| 1001 | 600 QPSK IL2Pc | 500 Hz | Shaped PSK (300 sym/sec) |
| 1010 | 1200 BPSK IL2Pc | 2400 Hz | Shaped PSK (1200 sym/sec) |
| 1011 | 2400 QPSK IL2Pc | 2400 Hz | Shaped PSK (1200 sym/sec) |
| 1100 | 300 AFSK AX.25 | 500 Hz | SSB AFSK, 1600/1800 Hz tones |
| 1101 | 300 AFSK IL2P | 500 Hz | SSB AFSK |
| 1110 | 300 AFSK IL2Pc | 500 Hz | SSB AFSK |
| 1111 | - | - | Enable the KISS SETHW command to select the mode |

What changed here as a result:

- **QPSK 3600 is switch 0101 alone, on a 1650 Hz carrier, and it is a speaker/mic mode.** This
  table previously gave it 2079 Hz and switches "0001, 0101", conflating it with C4FSK 19200 -
  which is switch 0001 alone. Our `ModemCatalog` was already correct at 1650 Hz; only the
  documentation was wrong. Nino groups it with the FM AFSK modes, i.e. it is meant to work through
  a microphone and speaker, and the sim now reproduces that (`qpsk3600` on `fm-mic` decodes 25/25
  at +18 dB CNR, mask-pinned).
- **The 5.0 kHz deviation figure for QPSK 3600 is doubtful on provenance, and the evidence I first
  offered for it was wrong.** The provenance argument stands: the figure appears to have been
  inherited from the C4FSK 19200 row this mode was wrongly sharing, and 5.0 kHz is a wide-channel
  deviation for a mode Nino groups with the speaker/mic modes. But the *measurement* I attributed
  to it does not stand. I reported that qpsk3600 plateaus at 83-87 % even at +40 dB CNR at 5.0 kHz
  against 100 % at 3.0 kHz, and called it an IF-truncation floor. Tom's challenge - "could just be
  your implementation?" - was right, and the controls say it is not truncation: widening the IF
  does not fix it (it is worse), and `qpsk3600` degrades at high signal on a *linear* channel too.
  See the qpsk3600 high-signal anomaly below. **The deviation figure still needs confirming with
  Nino before anyone transmits qpsk3600**, but on provenance, not on that measurement.
- **The shaped PSK modes work on FM radios as well as SSB.** Nino says so explicitly. This document
  previously listed them as SSB-only, and the FM channel model still refuses them
  (`FmModes.PeakDeviationHz` returns null), which is now a known gap rather than correct behaviour:
  we have no deviation figure for a PSK-over-FM transmission.
- **Occupied bandwidths confirm the narrow/wide split independently.** 10 kHz for 9600 C4FSK and
  4800 GFSK (both fit a 12.5 kHz channel), 20 kHz for 9600 GFSK and 19200 C4FSK (both need 25 kHz).
  That corroborates Tom's ground truth on the C4FSK pair and settles the two entries here that were
  inference.
- **An arithmetic discrepancy worth resolving.** Carson bandwidth from our recorded deviations
  reproduces Nino's OBW for the C4FSK modes almost exactly (2.5 kHz dev, 4800 Hz baseband ->
  9.8 kHz against 10; 5.0 kHz dev -> 19.6 against 20) but falls well short for the GFSK ones
  (2.4 kHz dev -> 14.4 against 20; 1.2 kHz -> 7.2 against 10). Either the GFSK deviation figures
  here are low, or Nino's OBW numbers for that group are rounded up to the channel they fit. Worth
  asking; the classification is unaffected either way.

### The qpsk3600 high-signal anomaly: found, explained, and the fix identified (2026-08-08)

Chasing the deviation question turned up something more important, and QtSoundModem's source
explained it.

**The symptom.** `qpsk3600` decodes *worse* as the signal gets stronger. On the FM channel (data
port, flat, wide IF - so neither truncation nor the microphone path is involved) it scores 30/30 at
+18 dB CNR and then 22/30, 23/30, 21/30 at +24, +30 and +40. On linear AWGN the same shape appears
in miniature: 99/97/95/94/97 % across +15/+20/+25/+30/+40 at N=100.

**Ruled out by measurement**: the channel model (`afsk1200-il2p` and `c4fsk9600` are both 25/25 at
+40 dB CNR on the same path); the shared QPSK code (`qpsk600` and `qpsk2400` are both 25/25 at +20,
+30 and +40 dB); acquisition (TXDELAY 150, 300 and 600 ms all give exactly 21/30, and the same
frames fail each time); deviation and IF truncation (a wider IF is worse).

**The cause: a non-integer samples-per-symbol ratio.** At the deployed 12 kHz DSP rate, 1800
symbols per second is **6.667 samples per symbol**. Every other PSK mode in the catalogue is an
integer - `qpsk600` at 40, `qpsk2400` at 10, `bpsk300` at 40 - and those are exactly the modes that
do not show the effect. Our symbol instant comes from a phase accumulator that wraps onto the
sample grid, so a coarse fractional ratio can park the sampler consistently off-centre; noise
dithers it and, perversely, helps.

Proven by re-running the same mode at rates that make the ratio integer (AWGN, +40 dB, N=30):

| DSP rate | samples/symbol | result |
|---|---|---|
| 12000 | 6.667 | 28/30 |
| 10800 | 6.000 | 29/30 |
| 36000 | 20.000 | **30/30** |

and on the FM path, where the effect is severe, 20 samples/symbol removes it completely: **30/30 at
both +24 and +40 dB CNR**, against 26/30 and 21/30 at 6.667.

**How QtSoundModem avoids it, and the fix that follows.** QtSoundModem (GPLv3; `ax25_demod.c`,
`decode_stream_QPSK` and `make_core_INTR`) never lets its symbol sampler see a fractional ratio. It
interpolates each PSK mode by `n_INTR = baud/300` - **6 for Q3600** - and the sampler then hardcodes
`baudrate = 300`, so every PSK mode arrives at the decision point with exactly 40 samples per
symbol. Its sampler is also a different design from ours: it keeps an exponentially averaged
envelope per position-within-symbol (`bit_buf[sample_cnt]`) and decides at the position where that
averaged eye peaks, rather than at a transition-nudged phase wrap - an estimator that gets *better*
as noise falls, which is precisely the opposite of what we measured.

The fix must be **internal interpolation in the receive path**, not a catalogue rate change: no
divisor of the 48 kHz soundcard rate gives an integer samples-per-symbol at 1800 baud (48000/1800 =
26.667), which is presumably why the mode sits at 12 kHz in the first place. Interpolating by 3
(20 samples/symbol) or by 6 (40, matching QtSoundModem exactly) inside the demodulator is the
change. Not yet built.

**Why it matters operationally**: a strong local signal is the normal case for FM packet, so this
is the regime the mode is most used in.

Two operational notes from the same release, which bear on our capture campaign:

- **Before v43, the first frame after an ID beacon was sometimes sent with no preamble** (fixed in
  v43). Our acquisition needs a preamble, so such a frame is structurally unacquirable - a
  candidate explanation for isolated unexplained misses in the corpus when the sender runs older
  firmware, and one that costs nothing to check against ID-beacon timing.
- **v43 adds a Morse ID for the HF modes**, alongside the existing 300 baud AFSK identification the
  PSK modes use and the 1200 AFSK identification the FM modes use (see `IdBeaconGhost` and
  `NinoPskIdBeacon`). A CW burst in an HF slot is now an expected artefact, not an anomaly.
- v43 also widened the 300 AFSK transmit waveform slightly (still under 500 Hz OBW) and adjusted
  the correlator gain on AFSK demodulator 1 - both are wire/receive changes that could move an
  interop A/B between firmware versions, so a NinoTNC comparison should record the firmware
  revision.

**AFSK 1200 is narrow, and here is why rather than an assumption**: Bell 202's 1200/2200 Hz tones
at 3.0 kHz peak deviation give a Carson bandwidth of 2*(3000 + 2200) = **10.4 kHz**, which lands in
Nino's own 10 kHz OBW bucket alongside 9600 C4FSK and 4800 GFSK - both of which fit a 12.5 kHz
channel. That is consistent with him grouping it with the speaker/mic modes, and with the fact that
legacy packet and APRS run on 12.5 kHz-spaced channels across IARU Region 1.

**Channel spacing is part of the mode, not an operator preference** (Tom, 2026-08-08, from
MMDVM-TNC and NinoTNC practice: C4FSK 9600 is the narrow-channel mode, C4FSK 19200 the wide one).
It sets the receiver's IF filter - roughly 8 kHz on 12.5 kHz spacing, 16 kHz on 25 kHz - and a
mode run on the wrong one has its own sidebands truncated. Measured through the FM channel model
(`sm-ota sim --channel fm-data`, 2026-08-08): `fsk9600` on its proper wide channel knees at about
+10.5 dB CNR, and on a narrow one at about +15 dB. **4.5 dB is what the wrong channel costs**, and
it is invisible unless the spacing is stated with the number. `c4fsk9600` pays about 1.8 dB for
being a narrow-channel mode (knee ~19.5 dB narrow against ~17.7 dB wide), which is the price of
fitting - it does work there, which is the point of it.

## SSB modulators (transmit via SSB/DIGU - the DAX route the harness already drives)

| NinoTNC mode | Tone/centre Hz | NinoTNC switch | pdn `ModemCatalog` mode(s) |
|---|---|---|---|
| AFSK 300 | 1700 | 1100, 1101, 1110 | `afsk300` (+`-il2p`, `-il2pc`) |
| PSK 300 | 1500 | 1000, 1001 | `bpsk300` (+`-nocrc`) |
| PSK 1200 | 1500 | 1010, 1011 | `bpsk1200` (+`-multi`) |

Not in the NinoTNC table but SSB by nature (V.26A / DW audio PSK carriers): `qpsk600`, `qpsk2400`.

## Consequences for the harness

- **SSB modes** ride the existing DAX/DIGU SSB path (audio carrier → SSB) - no deviation concern; the modem's own audio spectrum defines the signal.
- **FM modes** need a **Flex FM-TX path with per-mode deviation calibration** (drive set so peak dev = Tgt Dev, measured back off the RSP1 FM discriminator) plus an **IQ→FM-discriminator RX** feeding the modem. `qpsk3600` transmits at 5.0 kHz dev like C4FSK 19200 - do **not** route it through the SSB path.
- The correct FM/SSB assignment above supersedes any earlier categorisation in session notes (which had `qpsk3600` and `afsk300` on the wrong sides).
