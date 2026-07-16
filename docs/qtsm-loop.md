# QtSoundModem ↔ pdn-soundmodem interop loop (the snd-aloop edition)

QtSoundModem (UZ7HO SoundModem, ported to Qt/Linux by John Wiseman G8BPQ) is the
**ancestor** of this modem's AFSK/BPSK/QPSK demodulator chain (see [PROVENANCE.md](../PROVENANCE.md)).
This rig cross-validates the two over a virtual audio cable — no sound card, no radios —
so a frame produced by one modem is demodulated by the other and vice versa. It complements
the Dire Wolf WAV cross-validation and the wired NinoTNC bench ([ninotnc-loop.md](ninotnc-loop.md))
by testing against the actual codebase ours descends from.

Everything here was **measured on this box** (Ubuntu 24.04, QtSoundModem built from source at
`github.com/g8bpq/QtSoundModem`, version reported **0.0.0.76**). Where a number is a decode
count it is exactly what the run printed; where a statement is about QtSM's behaviour it comes
from its source (file:line cited) or from a measurement, never from memory.

## The two modems

| | QtSoundModem | pdn-soundmodem |
|---|---|---|
| role | reference ancestor | this repo |
| build | `qmake && make` (Qt5) | `dotnet build -c Release src/Packet.SoundModem.Daemon` |
| headless | `./QtSoundModem nogui` (uses `QCoreApplication`, no X) — `main.cpp:49` | native |
| KISS TCP | `[KISS] Server=1 Port=…` | `--kiss <port>` |
| DSP rate | fixed **12000** Hz (`TXSampleRate`/`RXSampleRate`) | `--capture-rate` (12000 for audio-band modes) |

## Building QtSoundModem headless

```sh
sudo -n apt-get install -y qtbase5-dev qtmultimedia5-dev libqt5serialport5-dev \
    qtchooser qtbase5-dev-tools libfftw3-dev libasound2-dev libpulse-dev
git clone --depth 1 https://github.com/g8bpq/QtSoundModem.git
cd QtSoundModem
qmake QtSoundModem.pro && make -j2      # ~2 min; links -lasound -lfftw3f -ldl
./QtSoundModem nogui                     # reads ./QtSoundModem.ini from the CWD
```

`main.cpp` takes a genuine console switch: `argv[1] == "nogui"` sets `nonGUIMode` and
constructs a `QCoreApplication` instead of `QApplication`, so no display/`offscreen` platform
plugin is needed. It reads **`QtSoundModem.ini` from the current working directory** (relative
`QSettings`), so run it from a per-rig directory. Debug/trace lines (`qDebug`/`Debugprintf`)
go to stdout.

## snd-aloop audio cable

```sh
sudo -n modprobe snd-aloop pcm_substreams=2      # appears as a new card (here: card 4 "Loopback")
```

The Loopback card has two PCM **devices** (0 and 1), cross-linked: a stream played to
`pcm{X}p` appears on the capture of `pcm{1-X}c` at the **same subdevice**. So two full-duplex
apps each use ONE device and hear each other:

| app | device string | capture ← | playback → |
|---|---|---|---|
| QtSoundModem | `hw:4,0` (it auto-prepends `plug` → `plughw:4,0`) | `pcm0c` | `pcm0p` |
| pdn-soundmodem daemon | `plughw:4,1` | `pcm1c` | `pcm1p` |

Links: daemon `pcm1p` → QtSM `pcm0c`, and QtSM `pcm0p` → daemon `pcm1c`. Prove the cable
before wiring apps:

```sh
# tone on device 0 playback shows up on device 1 capture
arecord -D plughw:4,1,0 -f S16_LE -r 12000 -c 1 -d 2 /tmp/cap.wav &
speaker-test -D plughw:4,0,0 -c 2 -r 12000 -t sine -f 1000
# /tmp/cap.wav is non-silent  → cable OK
```

### Two ALSA gotchas that cost real time here

1. **`audio` group must be *active* in the process, not just in `/etc/group`.** On this box
   `tf` is in `audio` in `/etc/group` but the login shell's process group set does not include
   it, so `arecord -l` reports "no soundcards found" and every `snd_pcm_open` fails. Every
   audio process (QtSM, the daemon, `aplay`, `arecord`) must be launched under
   **`sg audio -c "…"`**.
2. **ALSA can't resolve the card by name here** (`plughw:Loopback,…` → "Cannot get card index
   for Loopback"). Use the **numeric card index** (`plughw:4,…`).

### Rates

QtSM runs the audio-band DSP at 12 kHz and opens the aloop at 12 kHz. The daemon must present
the aloop the **same** hw rate — snd-aloop copies frames 1:1, so a cable's two ends must agree.
Run the daemon **`--capture-rate 12000`** for the audio-band modes so capture *and* playback are
12 kHz native (no resampling anywhere). Fixing the factor-1 decimator crash (below) is what
makes this possible.

**The RUH (9600/4800) modes run at 48 kHz** — QtSM's `init_RUH96`/`init_RUH48` configure the
Dire Wolf demod for a 48 kHz sample rate, and our daemon runs the 9600-family at 48 kHz too, so
the aloop cable is opened at 48 kHz on both ends (`--capture-rate 48000`, the daemon default).
The aloop bridges 48 kHz fine.

**Headless QtSM does not switch to 48 kHz on its own** — a QtSM bug this rig had to patch. The
`using48000` flag (which makes `InitSound` open the card at 48 kHz when a RUH modem is active)
is set **only in the GUI init path** (`QtSoundModem.cpp`); the `nogui` worker
(`tcpCode.cpp`, before `InitSound`) never set it, so headless RUH opened the card at 12 kHz and
fed the 48 kHz-configured demod garbage → 0/10. The fix is three lines in the worker: set
`using48000` from the configured `speed[]`/`soundChannel[]` before `InitSound`. Filed against
QtSM's headless mode; applied to the local build for these measurements (a rig change, not a
change to either modem).

## QtSoundModem.ini (per mode)

Base it on the ini QtSM writes on first run and patch these keys (generator:
`docs`-referenced recipe; the committed bench is the C# tool). The load-bearing settings:

```ini
[Init]
SndRXDeviceName="hw:4,0"
SndTXDeviceName="hw:4,0"
SoundMode=0          ; ALSA
PTT=                 ; empty — no PTT device; audio plays regardless
TXSampleRate=12000
RXSampleRate=12000
[KISS]
Server=1
Port=8300
[AGWHost]
Server=0             ; keep the AGW listener out of the way
[Modem]
ModemType1=<N>       ; modulation+baud (table below)
soundChannel1=1      ; 1=left, 2=right; *** 0 = CHANNEL DISABLED *** (see gotcha)
RXFreq1=<Hz>         ; carrier (AFSK 1200 → 1700; BPSK/QPSK per our carriers)
NRRcvrPairs1=2       ; QtSM's normal multi-decoder bank (±30/60 Hz) + emphasis
PreEmphasisAll1=1
[AX25_A]
IL2P=<0|2>           ; 0=off, 2=IL2P TX+RX (UZ7HOStuff.h IL2P_MODE_TXRX)
IL2PCRC=<0|1>        ; 1 for the IL2P+CRC modes
FX25=0
```

### The gotcha that cost the most time: `soundChannel1=0` disables the channel

`soundChannel[chan]` is **0 = unused/disabled**, 1 = left, 2 = right (sm_main.c:815
`"Unused channed"`, ax25_agw.c:1402 `"Not in use"`). With it 0, QtSM never runs channel A's
demodulator, so its DCD never evaluates, its CSMA scheduler never drains the KISS TX buffer,
and **it neither transmits nor receives** — while looking alive. It must be **1** (or 2).

### QtSM ModemType table (index = `ModemType1` value)

From `sm_main.c` `modes_name[]`. The ones this rig uses:

| our mode | QtSM `ModemType1` | QtSM mode name | carrier | IL2P |
|---|---|---|---|---|
| `afsk1200` | 1 | AFSK AX.25 1200bd | 1700 | 0 |
| `afsk1200-il2p` | 1 | AFSK AX.25 1200bd | 1700 | 2 + CRC |
| `bpsk300` | 6 | BPSK AX.25 300bd | 1500 | 2 + CRC |
| `qpsk2400` | 10 | QPSK AX.25 2400bd | 1500 | 2 + CRC |
| `qpsk3600` | 9 | QPSK AX.25 3600bd | 1650 | 2 + CRC |

## Wiring KISS

QtSM serves KISS TCP on `[KISS] Port` (here 8300); the daemon on `--kiss 8310`. Both broadcast
received frames to every client and neither echoes a client's own transmit frame, so a frame
appearing on the *other* modem's KISS socket is a genuine over-the-air decode. The committed
driver **`tools/Packet.SoundModem.QtsmBench`** (`qtsm-bench`) connects to both and, per
direction, writes a Q0AAA UI frame (40-byte self-describing ASCII payload, per-run nonce) into
one modem's KISS and counts how many surface on the other's.

```sh
qtsm-bench --qtsm-port 8300 --our-port 8310 --label afsk1200 --frames 10
```

KISS port nibble → QtSM channel: `Chan = Msg[0] >> 4` (kiss_mode.c:198), so port 0 = channel A.

## Results

> **Re-measured 2026-07-16 under the coherent PSK detector (the default since issue #5).**
> The detector is **receiver-side**, so it changes only the **qtsm→ours** direction of the
> BPSK/QPSK modes (our RX runs a Costas loop now, not differential detection); our modulator
> is unchanged, so **ours→QtSM is unaffected** by the flip. The differential-era matrix (git
> history of this file) is superseded. Two findings moved on fresh evidence: **#11 qpsk600
> qtsm→ours 9/10 → 10/10** (coherent), and **#10 fsk4800-il2p ours→QtSM 0/10 → 10/10** (could
> not be reproduced under current code — see the finding). The whole matrix now interoperates
> **both ways** except one marginal leg (qpsk600 ours→QtSM).

Method:

- **qtsm→ours** — QtSM's per-mode transmission is captured off the aloop into a WAV
  (`samples/qtsm/`) and decoded by our modem with **`sm-decode`** (coherent default). This is
  deterministic and checked in as the `Category=Interop` corpus
  (`QtsmInteropTests`) — no live rig needed to re-run it.
- **ours→QtSM** — our pre-generated TX WAV (`sm-samples --native-rate`) is played
  *continuously* into QtSM and QtSM's KISS decodes are counted. This is the artifact-free
  figure (it avoids the snd-aloop capture-starvation artifact that depresses a live
  half-duplex figure). Coherent-independent; re-run this session for the changed cells
  (#10 fsk4800, #11 qpsk600) and the controls, carried forward from the differential era for
  the unchanged modes.

Rig: card 4 aloop, QtSM 0.0.0.76, 10 frames/direction. Measured 2026-07-16.

| our mode | compatibility | QtSM pairing (`ModemType` · name · framing) | rate | qtsm→ours | ours→QtSM | verdict |
|---|---|---|---|---|---|---|
| `afsk1200` | universal (Bell 202) | 1 · AFSK AX.25 1200 · HDLC | 12k | **10/10** | **10/10** | ✅ both ways |
| `afsk1200-il2p` | NinoTNC / QtSM (IL2P) | 1 · AFSK AX.25 1200 · IL2P+CRC | 12k | **10/10** | **9/10** | ✅ both ways |
| `bpsk300` | NinoTNC / QtSM V26A | 6 · BPSK AX.25 300 · IL2P+CRC | 12k | **10/10** | **10/10** | ✅ both ways |
| `bpsk1200` | NinoTNC / QtSM V26A | 4 · BPSK AX.25 1200 · IL2P+CRC | 12k | **10/10** | **10/10** | ✅ both ways |
| `qpsk600` | NinoTNC / QtSM V26A | 16 · QPSK V26A 600bps (`SPEED_Q300`) · IL2P+CRC | 12k | **10/10** | **8/10** | ⚠️ ours→QtSM marginal (#11) |
| `qpsk2400` | NinoTNC / QtSM **V26A** | **12 · QPSK V26A 2400bps (`SPEED_DW2400`) · IL2P+CRC** | 12k | **10/10** | **10/10** | ✅ both ways |
| `qpsk3600` | NinoTNC / QtSM legacy | 9 · QPSK AX.25 3600bd · IL2P+CRC | 12k | **10/10** | **10/10** | ✅ both ways |
| `fsk9600` | universal G3RUH / DW | 19 · RUH 9600(DW) · HDLC | 48k | **10/10** | **10/10** | ✅ both ways |
| `fsk9600-il2p` | NinoTNC / DW-RUH (IL2P) | 19 · RUH 9600(DW) · IL2P+CRC | 48k | **10/10** | **10/10** | ✅ both ways |
| `fsk4800-il2p` | NinoTNC **+ DW-RUH** | 18 · RUH 4800(DW) · IL2P+CRC | 48k | **10/10** | **10/10** | ✅ both ways (was one-way; #10) |

The **compatibility** column names which peers a mode is known to interoperate with, per
Tom's rule that every mode be explicit about its interop target (never trade NinoTNC interop
for QtSM interop): *universal* = decoded by NinoTNC, Dire Wolf and QtSM alike; *NinoTNC /
QtSM V26A* = our V.26A PSK map, which pairs with NinoTNC and with QtSM's V26A modes (not its
legacy UZ7HO maps — see #6); *NinoTNC + DW-RUH* = the 4800 GFSK, NinoTNC-derived and now
also cross-validated against QtSM's Dire-Wolf RUH-4800 (#10).

**Nine of ten mode/pairings interoperate cleanly both ways.** Only `qpsk600` ours→QtSM is
marginal (8/10 — QtSM's narrow V26A-600 receiver against our NinoTNC-proven TX; #11); its
qtsm→ours leg is now clean (10/10 under coherent). Both rate classes work: 12 kHz audio-band
and 48 kHz RUH, over the same aloop cable. `fsk4800-il2p`, one-way in the differential-era
matrix, now decodes **both ways** (#10).

### QPSK 2400 needs QtSM's V26A/DW2400 mode — not the legacy "QPSK AX.25 2400bd"

QtSM offers three 2400-bps QPSK modulations (`sm_main.c`/`UZ7HOStuff.h`): the legacy UZ7HO
map (type 10, `SPEED_Q2400`), **V26A** (type 12, `SPEED_DW2400` — the Dire Wolf V.26 map),
and V26B (type 14, `SPEED_2400V26B`). Our `qpsk2400` (the NinoTNC/IL2P symbol map) matches
**only V26A**:

| our `qpsk2400` vs QtSM QPSK-2400 type | qtsm→ours | verdict |
|---|---|---|
| 10 · QPSK AX.25 2400bd (legacy UZ7HO) | 0/5 | phase-map mismatch |
| **12 · QPSK V26A 2400bps (SPEED_DW2400)** | **5/5** | **matches — use this** |
| 14 · QPSK V26B 2400bps | 0/5 | phase-map mismatch |

Reproducible offline with our own decoder on QtSM's recorded transmissions
(`samples/qtsm/`):

```
sm-decode samples/qtsm/qtsm-qpsk2400-legacy.wav qpsk2400 --crc  → 0/8 frames
sm-decode samples/qtsm/qtsm-qpsk2400-v26a.wav   qpsk2400 --crc  → 8/8 frames
```

This is the "pairwise-negotiated QPSK phase map" caveat in `QpskModulator`'s doc comment made
concrete: ours is the V.26A convention (as NinoTNC and Dire Wolf use), so it pairs with QtSM's
V26A/DW2400, not its older bespoke 2400 map. Note the asymmetry with 3600: QtSM has no V26
variant at 3600, and our `qpsk3600` matches its **legacy** type-9 map cleanly (10/10 both
ways) — the two rates simply pick different QtSM modes.

### The snd-aloop capture-starvation artifact (why live ours→qtsm reads low)

Our daemon streams its playback **only during a TX burst**; between frames the aloop playback
side goes idle, so QtSM's linked capture starves and clips the *start* of the next burst.
QtSM's DCD/CSMA also needs its capture continuously clocked. The proof it is an artifact and
not an interop failure: the identical frames, recorded and played **continuously** into QtSM,
decode at the WAV rate shown (e.g. afsk1200 10/10) even though the live ours→qtsm reads 3–6/10.
The reverse direction (qtsm→ours) does not suffer because QtSM streams its output more
continuously, so our capture stays fed — hence its live figures are already clean.

## Findings / discrepancies

1. **QPSK phase-map pairing** (above). Our V.26A QPSK matches QtSM's V26A modes, not its legacy
   ones: `qpsk2400` = **V26A/DW2400 (type 12)** not legacy type 10; `qpsk600` = **V26A
   (type 16, `SPEED_Q300`)**; `qpsk3600` matches QtSM's legacy type 9 (QtSM has no V26 at 3600).
   Evidence in `samples/qtsm/`. The *pairing* is a characterisation, not a defect (our map is
   V.26A, matching NinoTNC and Dire Wolf).
   **`qpsk600` marginality (#11) — half-resolved.** Under the **coherent** detector its
   qtsm→ours leg is now **10/10** (differential-era 9/10 was live-path variance: on a clean
   deterministic WAV decode both detectors read 10/10). The remaining marginality is
   **ours→QtSM 8/10** — QtSM's narrow V26A-600 receiver (the P300 500 Hz filter set at
   300 sym/s) loses a frame or two of our transmission. This is **receiver-side in QtSM**: our
   `qpsk600` TX is NinoTNC-proven (mode 9, 10/10 both ways on the wired bench) and stays
   exactly as it is — we do not widen or re-shape it to suit QtSM (that would trade away
   NinoTNC compliance, and our OBW is deliberately inside the TNC's). Characterised, not a
   defect in ours.
2. **`fsk4800-il2p` now interoperates both ways (#10) — the differential-era 0/10 did not
   reproduce.** qtsm→ours **10/10** (QtSM's RUH 4800(DW) TX decodes on our receiver;
   `samples/qtsm/qtsm-ruh4800.wav`), and ours→QtSM **10/10** — QtSM's Dire-Wolf RUH-4800
   receiver decodes our 4800 GFSK transmission, reproduced twice this session (the committed
   `samples/pdn` mode-04 WAV and a freshly generated one) with QtSM's own RUH-4800 TX decoding
   in the same setup as a control. This contradicts the earlier finding of 0/10; the earlier
   measurement's 0/10 could not be reproduced under current code, and the timeline rules out a
   stale sample (the FskModem tail-flush acquisition fix — which had chopped the IL2P CRC
   trailer, exactly the kind of defect QtSM's CRC-checking receiver would reject — landed
   ~4 h *before* the original 0/10 measurement, so both used the fixed sample). No change was
   made to our 4800 modem: it is NinoTNC-derived and stays so; it now simply also
   cross-validates against QtSM's RUH-4800. Both directions require QtSM's headless
   `using48000` patch (finding 3) so the RUH demod runs at 48 kHz.
3. **Headless RUH needs the `using48000` patch** (§ Rates) — a QtSM-side bug: the flag that
   opens the card at 48 kHz for RUH is set only in the GUI path, so `nogui` RUH ran at 12 kHz.
   Without the patch all three RUH modes read 0/10 for the wrong reason.
4. **Daemon `--capture-rate == DSP-rate` crash** — fixed in this branch (see below).
5. **The soundChannel-0 disable** and **`sg audio` requirement** cost the most bring-up time;
   both are documented above so the next person does not rediscover them.

### Non-findings (checked, not real problems)

- `sm-decode` returning 0/partial on some of our *own* recorded-WAV sanity checks
  (Phase B in the runner) is **recording variance**, not a decoder or interop fault: on a
  clean capture the same decoder reads our `qpsk2400` recording 10/10, and QtSM decodes every
  one of these recordings. The gappy 22 s multi-burst captures just don't always feed the
  offline single decoder well. The authoritative measurements are the live cross-decode and
  the continuous-WAV playback, not the sanity check.

## Samples

`samples/qtsm/` holds QtSM's own transmissions, recorded off the aloop (QtSM's capture kept
clocked by a silence keepalive so it would key). Not reproducible without this rig + QtSM
0.0.0.76, so checked in. Each carries 10 UI frames with `QTSM-<mode>-NN` payloads (except the
two 2400 phase-map WAVs and `qtsm-ruh4800`, captured in an earlier session). These are the
`Category=Interop` corpus — `QtsmInteropTests` decodes them at test time with our modems.

| file | what |
|---|---|
| `qtsm-afsk1200.wav` | type-1 AFSK 1200 — our RX decodes **10/10** |
| `qtsm-bpsk300.wav` | type-6 BPSK 300 IL2P+CRC — our RX decodes **10/10** |
| `qtsm-bpsk1200.wav` | type-4 BPSK 1200 IL2P+CRC — our RX decodes **10/10** |
| `qtsm-qpsk600.wav` | type-16 QPSK V26A 600 IL2P+CRC — our RX decodes **10/10** (coherent) |
| `qtsm-qpsk3600.wav` | type-9 QPSK 3600 IL2P+CRC — our RX decodes **10/10** |
| `qtsm-qpsk2400-legacy.wav` | type-10 "QPSK AX.25 2400bd" — our `qpsk2400` decodes **0/8** (#6) |
| `qtsm-qpsk2400-v26a.wav` | type-12 "QPSK V26A 2400bps" — our `qpsk2400` decodes **8/8** (#6) |
| `qtsm-ruh4800.wav` | type-18 "RUH 4800(DW)" — our 4800 RX decodes it; QtSM's RUH-4800 RX now also decodes *our* 4800 TX **10/10** (#10, both ways) |

Regenerate the ours-side TX for the ours→QtSM leg with
`sm-samples <dir> --only <mode> --native-rate` (12 kHz for the audio-band PSK modes, 48 kHz
for RUH), then play it into a headless QtSM in the matching mode (docs above).

## A daemon fix this rig required

`--capture-rate 12000` (a 12 kHz DSP mode capturing at 12 kHz) crashed the daemon:
`Program.cs` always built `new Decimator(captureRate, captureRate / DspRate)`, i.e. a
factor-1 decimator, which throws. The RX loop now skips decimation when
`captureRate == DspRate` and feeds captured samples straight through — the playback side
already handled the equal-rate case. This is what lets the daemon run at the aloop's native
12 kHz with no resampling. (Not a modem/DSP change — daemon plumbing only.)
