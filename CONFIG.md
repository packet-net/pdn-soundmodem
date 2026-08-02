# Configuring pdn-soundmodem

The complete reference for `soundmodem.json`. For getting the package installed and running,
start with [INSTALL.md](INSTALL.md).

## The file

| | |
|---|---|
| Read from | `--config <path>` — the packaged systemd unit passes `/etc/pdn-soundmodem/soundmodem.json` |
| Format | JSON, with `//` and `/* */` **comments allowed** and trailing commas tolerated |
| Keys | case-insensitive (`kissPort`, `KissPort` and `kissport` are the same key) |
| Seeded from | `/usr/share/pdn-soundmodem/soundmodem.example.json` on first install |

Every key is optional. An empty `{}` is a valid config and gives you one `afsk1200` modem on
KISS sub-channel 0, ALSA device `default`, 48 kHz capture, KISS on port 8105.

Applying a change means restarting the service — the file is read once at start-up:

```sh
sudo systemctl restart pdn-soundmodem
```

(Commands here are written with `sudo`, as on Ubuntu and Raspberry Pi OS. Debian only
installs `sudo` when the root password is left blank at setup — without it, become root
with `su -` and drop the prefix.)

## Top-level keys

| Key | Type | Default | What it does |
|---|---|---|---|
| `device` | string | `"default"` | Sound device (or FlexRadio) — [below](#device) |
| `captureRate` | int | `48000` | ALSA capture/playback rate in Hz — [below](#capturerate) |
| `kissPort` | int | `8105` | KISS-over-TCP listen port |
| `modems` | array | one `afsk1200` on sub-channel 0 | The modems sharing the channel — [below](#modems) |
| `ptt` | object | *(none — VOX)* | How the radio is keyed — [below](#ptt) |
| `csma` | object | see below | Channel-access timing — [below](#csma) |
| `waterfall` | object | *(disabled)* | Browser spectrum/waterfall page — [below](#waterfall) |
| `paging` | object | *(disabled)* | POCSAG paging endpoint — [below](#paging) |
| `ardop` | object | *(disabled)* | ARDOP virtual TNC — [below](#ardop) |
| `flex` | object | see below | FlexRadio slice params — [below](#flex) |

---

## `device`

The ALSA device used for both capture and playback. `arecord -l` and `aplay -l` list what
your machine has.

```json
"device": "default"
```

- **`"default"`** — the system default. Fine when there is exactly one USB sound card.
- **`"plughw:1,0"`** — a specific card and device. Prefer `plughw:` over `hw:` — it lets ALSA
  convert sample formats, which most USB interfaces need. Card numbers move around when
  devices are re-plugged; `plughw:CARD=Device,DEV=0` (see `aplay -L`) is stable.
- **`"null"`** — ALSA's null device: the modem runs and serves KISS but hears and transmits
  nothing. Useful for checking a config parses and the daemon starts.
- **`"flex:<radio>[:slice][@station]"`** — a FlexRadio 6000-series over the LAN as both sound
  card *and* PTT.

For the Flex form, `<radio>` is `discover` (broadcast), an IP (`host[:port]`), a discovery
spec (`serial=…` / `name=…`), or `mock` (an in-process fake for offline testing). `<slice>` is
a letter `A`–`H`, default `A`.

With **no** `@station` the daemon owns the radio and brings it up headless, creating its own
slice from the [`flex`](#flex) section. A trailing `@station` attaches to a running SmartSDR's
existing slice instead, and the `flex` slice params are ignored because SmartSDR configures
it. Either way the radio keys itself, so **`ptt` must be omitted** — configuring both is
rejected at start-up. See [docs/flex-integration.md](docs/flex-integration.md).

## `captureRate`

```json
"captureRate": 48000
```

The rate the sound card runs at. The daemon decimates internally to the DSP rate the modes
need, so the card's native rate is the right answer — 48000 for essentially all USB audio.

**It must be an integer multiple of the DSP rate**, or the daemon refuses to start with
`--capture-rate must be a multiple of N`. The DSP rate is decided by your modes:

| Modes in use | DSP rate | Valid `captureRate` |
|---|---|---|
| Any mode named `*9600*`, or starting `fsk`, `c4fsk`, `freedv-`, `ms110d-` | 48000 Hz | 48000, 96000, … |
| Everything else (`afsk*`, `bpsk*`, `qpsk*`) | 12000 Hz | 12000, 24000, 36000, 48000, … |

Mixing families is fine: if *any* configured mode needs 48 kHz, the whole channel runs at
48 kHz. Ignored entirely for `flex:` devices, which supply their own DAX sample clock.

## `modems`

The logical modems sharing the one audio channel, each addressed by its KISS port nibble.
This is the QtSoundModem multiplex model — your host software picks a modem by KISS port.

```json
"modems": [
  { "subChannel": 0, "mode": "afsk1200-multi", "frequency": 1700 },
  { "subChannel": 1, "mode": "bpsk300", "frequency": 1500, "offsetPairs": 4, "offsetStepHz": 7.5 }
]
```

| Field | Type | Default | Notes |
|---|---|---|---|
| `subChannel` | int | `0` | KISS port nibble, 0–15. Must be unique — duplicates are rejected at start-up |
| `mode` | string | `"afsk1200"` | See [docs/modes.md](docs/modes.md) for all 38 modes |
| `frequency` | number | mode default | Audio centre in Hz, TX **and** RX |
| `offsetPairs` | int | `4` | Diversity-bank modes only |
| `offsetStepHz` | number | baud/40 | Diversity-bank modes only |

Omit `modems` entirely and you get one `afsk1200` on sub-channel 0.

### `frequency`

Moves a modem's audio centre, QtSoundModem-style — for meeting a peer who sits off the usual
centre. Only the **variable-centre families** accept it:

| Family | Default centre | Accepts `frequency`? |
|---|---|---|
| `afsk*` | 1700 Hz | yes |
| `bpsk*`, `qpsk*` | 1500 Hz (1650 for `qpsk3600`) | yes |
| `fsk*`, `c4fsk*` | — occupies DC-to-Nyquist | **no** |
| `freedv-*`, `ms110d-*` | — pinned by their specs | **no** |

Setting one on a fixed-centre mode is an error at start-up, not silently ignored.

### `offsetPairs` / `offsetStepHz`

The BPSK modes (`bpsk300`, `bpsk1200`, and their `-multi` aliases) run a frequency-diversity
bank by default: parallel decoder branches at stepped centres, which is how they tolerate an
off-frequency peer. `offsetPairs` is the number of branches *either side* of centre and
`offsetStepHz` the gap between them, so coverage spans ±`offsetPairs`×`offsetStepHz`.

More branches widen coverage at a linear CPU cost. `"offsetPairs": 0` gives a plain single
centred modem. Both are ignored by non-bank modes.

## `ptt`

How the radio is keyed. **Omit the whole section for VOX**, or when using a FlexRadio, which
keys itself.

```json
"ptt": { "type": "serial", "device": "/dev/ttyUSB0", "line": "rts" }
```
```json
"ptt": { "type": "cm108", "device": "/dev/hidraw0", "gpio": 3 }
```

| Field | Type | Default | Notes |
|---|---|---|---|
| `type` | string | `"serial"` | `"serial"` or `"cm108"` |
| `device` | string | `""` | `/dev/ttyUSB0` for serial, `/dev/hidraw0` for CM108 |
| `line` | string | `"rts"` | Serial only: `"rts"` or `"dtr"` |
| `gpio` | int | `3` | CM108 only: the GPIO pin driven |

**CM108 needs a udev rule.** `/dev/hidraw*` is root-only by default, so the service's
unprivileged user cannot open it — see [INSTALL.md § Permissions](INSTALL.md#permissions).
Serial PTT works out of the box because the unit already joins the `dialout` group.

Which `/dev/hidraw*` node is yours is worth confirming rather than assuming — the number
moves with what else is plugged in. `ls -l /sys/class/hidraw/*/device/` maps them to USB IDs.

## `csma`

Channel-access timing, in the usual TNC terms. KISS clients can override these at runtime, so
these are the values the daemon starts with.

```json
"csma": {
  "txDelayMilliseconds": 300,
  "persistence": 63,
  "slotTimeMilliseconds": 100,
  "txTailMilliseconds": 20
}
```

| Field | Default | Notes |
|---|---|---|
| `txDelayMilliseconds` | `300` | Key-up to first data. Long enough for the radio's T/R relay and the far end's AGC to settle |
| `persistence` | `63` | p-persistence 0–255. 63 ≈ 25 % chance of transmitting per slot |
| `slotTimeMilliseconds` | `100` | Gap between persistence rolls |
| `txTailMilliseconds` | `20` | Carrier held after the last bit |

`txDelayMilliseconds` is the one most worth tuning: too short and the first bytes are cut off
at the far end, too long and you waste channel time on every frame.

## `waterfall`

A live spectrum and waterfall page — 30 fps, every modem's measured band overlaid, each
decoded frame tagged on its burst with callsign, SNR and frequency offset. Genuinely useful
for confirming you are hearing the band at a sane level before trusting the decoder.

```json
"waterfall": { "port": 8107, "bind": "127.0.0.1", "dialFrequencyHz": 14105000, "sideband": "usb" }
```

| Field | Type | Default | Notes |
|---|---|---|---|
| `port` | int | `8107` | HTTP listen port |
| `bind` | string | `"127.0.0.1"` | `"*"` listens on all interfaces |
| `dialFrequencyHz` | number | `0` | Rig dial the RF scale derives from; 0 = audio frequencies only |
| `sideband` | string | `"usb"` | `"usb"` (RF = dial + audio) or `"lsb"` (RF = dial − audio) |
| `linesPerSecond` | int | `30` | Waterfall line / display frame rate |
| `fftSize` | int | `0` | 0 = rate default (2048 at 12 kHz, 8192 at 48 kHz) |

Omit the section to disable it. `dialFrequencyHz` is only the page's opening default — each
browser can retune its own copy. **`"bind": "*"` serves the page to anyone who can reach the
box**; there is no authentication, so keep it on loopback or behind a reverse proxy or VPN.

## `paging`

A POCSAG paging endpoint (DAPNET-compatible waveform). Pages are not AX.25 frames, so they
get a line-based TCP service of their own rather than a KISS port.

```json
"paging": { "port": 8106, "baud": 1200, "invertPolarity": false }
```

| Field | Type | Default | Notes |
|---|---|---|---|
| `port` | int | `8106` | Paging TCP listen port |
| `baud` | int | `1200` | `512`, `1200` (DAPNET) or `2400` |
| `invertPolarity` | bool | `false` | For radios whose data path inverts |

The grammar is one UTF-8 command per line — `PAGE <ric> <function> ALPHA|NUMERIC|TONE [text]`,
replying `OK <id>` or `ERR <reason>`. Every page heard on channel is broadcast to all
connected clients as a `HEARD …` line. Transmissions share the CSMA/PTT path with the packet
modems.

## `ardop`

An ARDOP virtual TNC with an ardopcf-compatible host interface, so Pat, Winlink Express,
ARIM/gARIM and hamChat connect unmodified.

```json
"ardop": { "port": 8515 }
```

| Field | Type | Default | Notes |
|---|---|---|---|
| `port` | int | `8515` | Command port; the **data port is always `port + 1`** |

**The ARDOP channel is dedicated**: `ardop` is exclusive with `modems` and `paging`, and
configuring them together is rejected at start-up. ARDOP runs its own channel discipline, so
the daemon's CSMA is bypassed. `"captureRate": 12000` is the recommended setting for this
mode. See [docs/ardop-design.md](docs/ardop-design.md).

## `flex`

Slice parameters used **only** when `device` is a headless `flex:` string — that is, `flex:`
with no `@station`. Ignored for ALSA devices and for attach-mode Flex, where SmartSDR owns
the slice.

```json
"flex": { "frequency": "14.100000", "antenna": "ANT1", "mode": "DIGU", "daxChannel": "1" }
```

| Field | Type | Default | Notes |
|---|---|---|---|
| `frequency` | string | `"14.100000"` | MHz, six-decimal Flex form — a **string**, not a number |
| `antenna` | string | `"ANT1"` | RX/TX antenna |
| `mode` | string | `"DIGU"` | Slice demod mode |
| `daxChannel` | string | `"1"` | DAX channel to claim; applies to headless **and** attach |

Sharing a box with a running SmartSDR means picking a `daxChannel` it is not using — SmartSDR
grabs DAX 1. The headless path disables band persistence and explicitly tunes the slice, so it
lands on `frequency` regardless of the radio's last-used band.

---

## What is rejected at start-up

The daemon validates before it opens anything, and refuses to start rather than run in a state
you did not ask for. Every configuration problem is reported the same way — the file, what is
wrong in plain words, and what to do — and exits with status **2**:

```
configuration error in /etc/pdn-soundmodem/soundmodem.json
  not valid JSON — line 7, position 3: ',' is an invalid start of a value.

  The service will not start until this is fixed. As root, to start
  from a known-good file:
    cp /usr/share/pdn-soundmodem/soundmodem.example.json /etc/pdn-soundmodem/soundmodem.json
  Then edit it for your sound device and PTT, and:
    systemctl restart pdn-soundmodem
  Every setting is documented at https://github.com/packet-net/pdn-soundmodem/blob/main/CONFIG.md
```

Read it with:

```sh
systemctl status pdn-soundmodem          # the last few lines, usually enough
journalctl -u pdn-soundmodem -n 30       # the whole message
```

**A rejected config does not crash-loop.** The unit sets `RestartPreventExitStatus=2`, so
systemd leaves the service stopped after a configuration error and the journal holds one
readable explanation rather than a copy every five seconds. Fix the file and
`systemctl restart pdn-soundmodem`. Any *other* failure — a USB sound card that has not
enumerated yet at boot, for instance — still restarts on its own as usual.

| Condition | What you get |
|---|---|
| File missing, unreadable, or in a missing directory | `no such file` / `no such directory` / `permission denied reading the file` |
| File empty | `the file is empty` |
| File contains the literal `null` | `the file contains only \`null\`` — with a minimal working config to copy |
| Malformed JSON | `not valid JSON — line L, position P: …` (counted from 1, as your editor does) |
| Two modems on the same `subChannel` | `two modems share "subChannel": N … renumber one of them` |
| `ardop` alongside `modems` or `paging` | `"ardop" cannot be combined with … keep "ardop" and delete the others, or delete "ardop"` |
| `mode` not a known mode | `unknown mode 'X'` — with a **did you mean** for near misses, and a link to the mode table |
| `frequency` on a fixed-centre mode | `mode 'X' has a fixed centre frequency — drop the frequency override …` |
| `captureRate` not a multiple of the DSP rate | `--capture-rate must be a multiple of N` |
| `ptt` alongside a `flex:` device | `--device flex: keys the radio itself; remove the conflicting --ptt …` |
| `ptt.type` not `serial` or `cm108` | `unknown ptt type 'X'` |

The mode suggestion is worth knowing about, because a hyphen is easy to lose among 38 names:

```
modem 0: unknown mode 'fsk9600il2p'
  did you mean: fsk9600-il2p, fsk4800-il2p
  the 38 valid mode names are listed at …/docs/modes.md
```

### Hardware the config names but the machine does not have

Different case, deliberately handled differently. A config that is *structurally* fine but
points at a sound card or PTT line that will not open — the usual first-install experience,
since the seeded config names a CM108 on `/dev/hidraw0` — exits **1**, not 2, so the service
**keeps restarting**. That is on purpose: a USB interface may simply not have enumerated yet
at boot, and the service should come up by itself when it does rather than stay down waiting
for a human.

You still get a message rather than a stack trace:

```
cannot open the cm108 PTT device "/dev/hidraw0"
  Could not find file '/dev/hidraw0'.

  Set by "ptt" in /etc/pdn-soundmodem/soundmodem.json
  This selects how the radio is keyed; omit it entirely for VOX, or for a
  FlexRadio, which keys itself.
  List what this machine actually has:
    ls -l /dev/hidraw*
  /dev/hidraw* is root-only by default, so the unprivileged service user cannot
  open it without a udev rule granting the audio group access — see the
  Permissions section of INSTALL.md. …
```

So: **"will not start until this is fixed" means the service is stopped and waiting for you;
"will keep retrying" means it is still trying and may fix itself.**

## Config file vs command line

Everything here has a command-line equivalent, and the daemon can run entirely without a
config file:

```sh
pdn-soundmodem --device plughw:1,0 --modem 0:afsk1200 --kiss 8105 --ptt serial:/dev/ttyUSB0:rts
```

**When `--config` is given, the file wins for most settings** — it overwrites `device`,
`captureRate`, `kissPort`, `ptt`, `csma`, `paging`, `flex` and `waterfall`, so a
`--txdelay` or `--device` passed alongside `--config` is silently discarded. The exceptions:

| Flag | Behaviour with `--config` |
|---|---|
| `--modem N:MODE[:FREQ]` | **Appends** to the file's `modems` list rather than replacing it |
| `--waterfall PORT`, `--dial HZ` | Override the file's `waterfall` section (and enable it if absent) |
| `--flex-freq`, `--flex-ant`, `--flex-mode`, `--flex-daxch` | Override the file's `flex` section |
| `--paging PORT[:BAUD]` | Replaces the file's `paging` section |
| `--ardop PORT` | Used only if the file has no `ardop` section |

Some options are command-line only and have no config equivalent — `--wav FILE` and
`--wav-loop FILE` (decode a recording instead of live audio), `--quality-frames`, and
`--psk-detector coherent|differential`.

## Worked examples

**VHF packet, one AFSK modem, serial PTT** — the common node case:

```json
{
  "device": "plughw:CARD=Device,DEV=0",
  "captureRate": 48000,
  "kissPort": 8105,
  "modems": [ { "subChannel": 0, "mode": "afsk1200-multi" } ],
  "ptt": { "type": "serial", "device": "/dev/ttyUSB0", "line": "rts" },
  "csma": { "txDelayMilliseconds": 300, "persistence": 63, "slotTimeMilliseconds": 100, "txTailMilliseconds": 20 }
}
```

**HF, two modems on one channel, CM108 PTT, waterfall on the LAN:**

```json
{
  "device": "plughw:1,0",
  "modems": [
    { "subChannel": 0, "mode": "bpsk300", "frequency": 1500 },
    { "subChannel": 1, "mode": "qpsk2400", "frequency": 1500 }
  ],
  "ptt": { "type": "cm108", "device": "/dev/hidraw0", "gpio": 3 },
  "waterfall": { "port": 8107, "bind": "*", "dialFrequencyHz": 14105000, "sideband": "usb" }
}
```

**9600 baud** — note `captureRate` must be a multiple of 48000 here:

```json
{
  "device": "plughw:1,0",
  "captureRate": 48000,
  "modems": [ { "subChannel": 0, "mode": "fsk9600" } ],
  "ptt": { "type": "serial", "device": "/dev/ttyUSB0", "line": "rts" }
}
```

**Winlink over HF via ARDOP** — no `modems`, no `paging`:

```json
{
  "device": "plughw:1,0",
  "captureRate": 12000,
  "ardop": { "port": 8515 },
  "ptt": { "type": "serial", "device": "/dev/ttyUSB0", "line": "rts" }
}
```

**A FlexRadio over the LAN, no sound card at all** — no `ptt`, the radio keys itself:

```json
{
  "device": "flex:10.45.0.76",
  "flex": { "frequency": "14.105000", "antenna": "ANT1", "mode": "DIGU", "daxChannel": "1" },
  "modems": [ { "subChannel": 0, "mode": "bpsk300" } ]
}
```

## See also

- [INSTALL.md](INSTALL.md) — installing the package and first-run setup
- [docs/modes.md](docs/modes.md) — every mode, its capabilities and verification level
- [docs/flex-integration.md](docs/flex-integration.md) — FlexRadio headless and attach modes
- [docs/ardop-design.md](docs/ardop-design.md) — the ARDOP implementation
