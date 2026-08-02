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
| `kissPort` | int | `8105` | Shared KISS-over-TCP port, all modems by nibble — [below](#kissport-and-kissbind) |
| `bind` | string | `"127.0.0.1"` | Address **every** listener binds to; `"*"` or `"0.0.0.0"` for all |
| `sideband` | string | `"usb"` | Which sideband the radio is on — [below](#band-plans-in-rf-terms) |
| `dialFrequency` | number | *(computed)* | Pin the dial instead of letting the daemon choose — [below](#band-plans-in-rf-terms) |
| `modems` | array | one `afsk1200` on sub-channel 0 | The modems sharing the channel — [below](#modems) |
| `ptt` | object | *(none — VOX)* | How the radio is keyed — [below](#ptt) |
| `waterfall` | object | *(disabled)* | Browser spectrum/waterfall page — [below](#waterfall) |
| `paging` | object | *(disabled)* | POCSAG paging endpoint — [below](#paging) |
| `ardop` | object | *(disabled)* | ARDOP virtual TNC — [below](#ardop) |
| `frameLog` | object | *(not kept)* | Record every frame heard to SQLite — [below](#framelog) |
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

## `kissPort` and `bind`

```json
"kissPort": 8105,
"bind": "127.0.0.1"
```

`kissPort` is the **shared** port: every modem is reachable on it, selected by the KISS port
nibble — the QtSoundModem multiplex model, and what Direwolf does on 8001.

`bind` is the address **every** listener uses — the shared KISS port, the per-modem ports, the
waterfall, paging and ARDOP alike. One setting rather than one per service: they are all on the
same machine facing the same network. `"*"` or `"0.0.0.0"` opens them to all interfaces.

> **KISS has no authentication of any kind.** Anything that can reach the port can key your
> transmitter. It stays on loopback unless you deliberately change it, and the daemon prints a
> warning at start-up when you do. If a host on another machine needs access, an SSH tunnel is
> a better answer than `"*"`.

### A port per modem

The nibble only helps if your host software lets you set it, and a good deal of it does not —
it assumes KISS channel 0 and offers nowhere to say otherwise. On the shared port such a host
can only ever reach `subChannel: 0`, however many modems you have configured.

Give a modem its own port and that stops mattering:

```json
"kissPort": 8105,
"modems": [
  { "subChannel": 0, "mode": "afsk1200", "port": 8110 },
  { "subChannel": 1, "mode": "bpsk300",  "port": 8111 }
]
```

A dedicated port carries **only** that modem, and presents it as **nibble 0**:

- frames received by that modem arrive on it labelled channel 0;
- other modems' traffic never appears on it;
- anything transmitted into it goes out on that modem, whatever nibble the client used.

So a host that only speaks channel 0 talks to port 8111 and works `bpsk300` without ever
knowing a multiplex exists. The shared port keeps working alongside, still reporting true
nibbles — you can run both at once.

Two services asking for the same TCP port is rejected at start-up, naming both, rather than
left to whichever listener happens to bind second.

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
| `mode` | string | `"afsk1200"` | See [docs/modes.md](docs/modes.md) for all 38 modes, plus `ardop` — [below](#ardop) |
| `frequency` | number | mode default | Audio centre in Hz, TX **and** RX |
| `rfFrequency` | number | *(none)* | Where on the band it sits, in absolute Hz — [below](#band-plans-in-rf-terms) |
| `bandwidth` | number | measured | How much room to plan for; mainly for `ardop` — [below](#band-plans-in-rf-terms) |
| `offsetPairs` | int | `4` | Diversity-bank modes only |
| `offsetStepHz` | number | baud/40 | Diversity-bank modes only |
| `port` | int | *(none)* | A TCP port carrying this modem alone — KISS, or the ardopcf host interface for `ardop` — [below](#a-port-per-modem) |

Omit `modems` entirely and you get one `afsk1200` on sub-channel 0.

### `frequency`

Moves a modem's audio centre, QtSoundModem-style — for meeting a peer who sits off the usual
centre. Only the **variable-centre families** accept it:

| Family | Default centre | Accepts `frequency`? |
|---|---|---|
| `afsk*` | 1700 Hz | yes |
| `bpsk*`, `qpsk*` | 1500 Hz (1650 for `qpsk3600`) | yes |
| `ardop` | 1500 Hz | yes — shifted outside the TNC, [see below](#ardop) |
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

## Band plans in RF terms

Audio centres are an awkward way to describe a band plan: you think in "BPSK300 at 7051.6", not
"1600 Hz with the dial on 7050.0", and the moment the dial moves every number is wrong. Give the
daemon `rfFrequency` instead and it works the dial out for you:

```json
"sideband": "usb",
"modems": [
  { "subChannel": 0, "mode": "afsk300-il2pc", "rfFrequency": 7050300 },
  { "subChannel": 1, "mode": "ardop",         "rfFrequency": 7050950, "bandwidth": 500 },
  { "subChannel": 2, "mode": "bpsk300",       "rfFrequency": 7051600 }
]
```

At start-up it tells you what to set:

```
dial: 7.049450 MHz USB — set your radio to this
  modem 0 afsk300-il2pc at 7.050300 MHz = 850 Hz audio
  modem 1 ardop at 7.050950 MHz = 1500 Hz audio
  modem 2 bpsk300 at 7.051600 MHz = 2150 Hz audio
```

**It picks a better dial than you would by hand.** The obvious round number for that plan is
7050.000, giving a tidy-looking 300/950/1600 Hz — but `afsk300` then occupies 150–450 Hz, half of
it below where an SSB filter starts. The daemon centres the whole ensemble in the passband
instead, which is how 7.049450 falls out.

`sideband` is `"usb"` (RF = dial + audio, the data-mode norm) or `"lsb"` (RF = dial − audio).

**Bandwidths are measured, not assumed.** Each modem is asked to modulate a probe frame and the
occupied width is metered off it — the same measurement the waterfall draws its overlays from, so
the two can never disagree. `ardop` is the exception: its bandwidth is negotiated per session, so
the planner assumes the widest (2000 Hz) unless `bandwidth` says otherwise. Setting it also caps
what ARDOP negotiates (200/500/1000/2000), which is worth doing — it reclaims the room the
planner would otherwise reserve.

### Rules

- **All or nothing.** Either every modem has an `rfFrequency` or none does. The dial is shared,
  so a modem pinned to an audio offset would sit at whatever RF the dial chosen for the others
  happened to put it.
- **One or the other.** A modem cannot set both `frequency` and `rfFrequency`; they say the same
  thing two ways.
- **No dial, no start.** If the modems are spread wider than one SSB passband can carry, the
  daemon says so — naming the span and the modems — rather than starting something that cannot
  work. Nothing else can be done about it: that is a second-radio problem.

### Pinning the dial

Set `dialFrequency` for a net frequency, or to match another application, and it is used as-is:

```json
"dialFrequency": 7050000,
"sideband": "usb"
```

A pinned dial that puts a modem outside the nominal 300–2700 Hz passband **warns and starts**
rather than refusing — that figure is nominal, the daemon cannot see your rig's filter, and you
asked for this dial. Omit `dialFrequency` and it will be chosen to fit.

The waterfall inherits the computed dial and sideband, so its RF scale is right without being
told twice.

### On a FlexRadio, it just does it

For a headless `flex:` device the daemon owns the radio, so rather than telling you the dial it
**sets** it — the slice goes to the computed frequency, and the transmit filter's high cut is
opened to clear the highest modem:

```
dial: 7.049450 MHz USB
  modem 0 afsk300-il2pc at 7.050300 MHz = 850 Hz audio
  modem 1 ardop at 7.050950 MHz = 1500 Hz audio
  modem 2 bpsk300 at 7.051600 MHz = 2150 Hz audio
flex: setting the slice to 7.049450 MHz and the transmit filter high cut to 2550 Hz from the band plan
```

That matters because **the transmit filter is a global, persistent radio setting** — whatever
last touched the radio. A 300 Hz CW filter left over from another session would quietly truncate
the top of a band plan, and nothing would say so.

**Only the high cut can be set from here.** The transmit filter's *low* cut and the slice's
*receive* filter are not exposed by the station API, so they stay as the radio has them. The
daemon reads the transmit filter back at bring-up and warns, per modem, if the plan falls outside
what the radio will actually pass — so a modem sitting below the low cut is reported rather than
silently transmitting nothing. Widening that is a job for the radio.

In attach mode (`@station`) none of this happens: SmartSDR owns the slice, and the daemon would
only be fighting it.

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

## Channel access is the host's, not the config's

There is deliberately **no `csma` section**. TXDELAY, P (persistence), SLOTTIME and TXTAIL are
the host's to set, and it sets them at runtime with the standard KISS parameter commands —
`0x01`, `0x02`, `0x03` and `0x04`. The daemon honours all four the moment they arrive, which
QtSoundModem does not.

Until a host sends them, these are in force:

| Parameter | Default | Notes |
|---|---|---|
| TXDELAY | 300 ms | Key-up to first data. A *radio* allowance — the modems themselves acquire from 0–20 ms; 300 ms budgets a real transmitter's PTT-to-RF settling, which FM gear routinely needs |
| P (persistence) | 63 | ≈ 25 % chance of transmitting per slot |
| SLOTTIME | 100 ms | Gap between persistence rolls |
| TXTAIL | 20 ms | Carrier held after the last bit |

`--txdelay MS` overrides the TXDELAY default, for bench runs with no host attached.

**Scope is the radio, not the modem.** There is one PTT, so these settings apply to the whole
channel: a host on one modem's dedicated port that sends TXDELAY changes it for every modem,
and with several clients connected the last one to send a parameter frame wins.

> Earlier versions accepted a `"csma"` block in the config. It is now ignored, and the daemon
> says so loudly at start-up rather than quietly reverting a link you had tuned.

## `waterfall`

A live spectrum and waterfall page — 30 fps, every modem's measured band overlaid, each
decoded frame tagged on its burst with callsign, SNR and frequency offset. Genuinely useful
for confirming you are hearing the band at a sane level before trusting the decoder.

```json
"waterfall": { "port": 8107, "dialFrequencyHz": 14105000, "sideband": "usb" }
```

| Field | Type | Default | Notes |
|---|---|---|---|
| `port` | int | `8107` | HTTP listen port |
| `dialFrequencyHz` | number | `0` | Rig dial the RF scale derives from; 0 = audio frequencies only |
| `sideband` | string | `"usb"` | `"usb"` (RF = dial + audio) or `"lsb"` (RF = dial − audio) |
| `linesPerSecond` | int | `30` | Waterfall line / display frame rate |
| `fftSize` | int | `0` | 0 = rate default (2048 at 12 kHz, 8192 at 48 kHz) |

**The page can play the received audio.** Press *Listen* in the top bar and the station's
receive audio streams to the browser, so you can hear the channel you are watching — an SSB
signal, a burst you are about to decode, or the noise floor you are arguing with. It is
per-viewer and **off until asked for**, so opening the page to look at a waterfall does not
quietly start pulling ~24 KB/s, and several viewers cost nothing unless they each ask.

Nothing is received while the station transmits, so the audio stops for the length of a keyup.
That is silence, not a dropout.

Omit the section to disable it. `dialFrequencyHz` is only the page's opening default — each
browser can retune its own copy, and it is inherited from a band plan when there is one. The
waterfall binds to the top-level [`bind`](#kissport-and-bind) like everything else; there is no
authentication, so opening it beyond loopback means a reverse proxy or VPN.

## `frameLog`

Everything the station hears, written to a SQLite file:

```json
"frameLog": { "path": "/var/lib/pdn-soundmodem/frames.db" }
```

Omit the section and frames are heard and not written down. One row per decoded frame:

| Column | What it holds |
|---|---|
| `heard_at` | UTC, ISO 8601 |
| `sub_channel`, `mode`, `mode_name` | which modem heard it, and what it is — `bpsk300-il2pc` and `BPSK300 IL2Pc` |
| `source`, `destination` | AX.25 callsigns where the frame carries them; null where it does not |
| `length`, `corrected`, `crc_valid` | size, FEC corrections applied, whether the CRC checked |
| `offset_hz` | how far off centre the sender actually was |
| `audio_hz`, `rf_hz` | where that modem sits — `rf_hz` filled in when you have given it an `rfFrequency` |
| `payload` | the frame itself, as a blob |

So "who have I heard on 40m today" is a query:

```sql
SELECT source, COUNT(*), MAX(heard_at)
FROM frames WHERE rf_hz > 7000000 AND rf_hz < 7300000
GROUP BY source ORDER BY 2 DESC;
```

**It is written on a background thread** — the receive path queues and returns, so logging never
delays a decode. If the disk fills or goes away the modem keeps decoding and drops log rows
rather than stopping. **The file is WAL**, so you can read it with `sqlite3` or point a dashboard
at it while the modem is still running and writing.

The packaged service runs unprivileged, so the default path is under its own state directory. If
you move it, the service user has to be able to write to the directory — the daemon says so
plainly at start-up rather than running without a log you asked for.

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
ARIM/gARIM and hamChat connect unmodified. **It is a modem entry like any other** — it shares
the passband with the packet modes rather than excluding them:

```json
"captureRate": 12000,
"modems": [
  { "subChannel": 0, "mode": "afsk300-il2pc", "frequency": 300,  "port": 8100 },
  { "subChannel": 1, "mode": "ardop",         "frequency": 950,  "port": 8101 },
  { "subChannel": 2, "mode": "bpsk300",       "frequency": 1600, "port": 8103 }
]
```

Three modes in one 3 kHz passband on one radio, each on its own centre and its own host port.

**`port` here speaks ardopcf, not KISS.** ARDOP is a connected-mode ARQ protocol with session
semantics; there is no way to express a session over KISS, which carries AX.25 frames and knows
nothing of connections. So the entry gets the ardopcf host interface: **command on `port`, data
always on `port + 1`.** That reservation is real and the daemon enforces it — `8103` above is
not a typo, because `8102` belongs to ARDOP's data port.

**`frequency` moves it off 1500 Hz.** ARDOP's waveforms are pinned to a 1500 Hz centre and the
underlying library exposes no way to move them, so the daemon shifts the audio outside the TNC:
transmitted audio is mixed from 1500 Hz to your centre, and received audio mixed back before the
TNC sees it. The TNC never knows. Choose the centre with ARDOP's *negotiated* bandwidth in mind
— up to 2000 Hz — and the daemon warns at start-up when a centre cannot fit the widest session
inside a nominal 300–2700 Hz SSB passband.

**Sharing the radio with an ARQ session.** ARDOP owns the channel's timing while a session is
up: an AX.25 frame landing mid-turnaround breaks it. So while the ARQ engine is connected or
connecting, packet transmissions are **held** — queued, not discarded. A frame held longer than
30 seconds is then rejected rather than escaping minutes late as a duplicate, since an AX.25
host will have retried long before a Winlink session ends. Receive is unaffected throughout:
every modem and ARDOP hear the channel simultaneously.

**ARDOP is 12 kHz.** It can share a channel with the other 12 kHz modes (`afsk*`, `bpsk*`,
`qpsk*`) but not with the 48 kHz families (`fsk9600`, `c4fsk*`, `freedv-*`, `ms110d-*`);
configuring both is rejected at start-up naming the offending modes.

One ARDOP TNC per channel — it is a whole virtual TNC, not a demodulator you can run twice.

> The older top-level form still works and is folded into a modem entry at start-up:
> ```json
> "ardop": { "port": 8515 }
> ```
> It has no `frequency` and no `subChannel`, so prefer the modem entry. Configuring both at
> once is rejected.

See [docs/ardop-design.md](docs/ardop-design.md).

## `flex`

Slice parameters used **only** when `device` is a headless `flex:` string — that is, `flex:`
with no `@station`. Ignored for ALSA devices and for attach-mode Flex, where SmartSDR owns
the slice.

```json
"flex": { "frequency": "14.100000", "antenna": "ANT1", "mode": "DIGU", "daxChannel": "1" }
```

| Field | Type | Default | Notes |
|---|---|---|---|
| `frequency` | string | `"14.100000"` | MHz, six-decimal Flex form — a **string**, not a number. Superseded by a band plan |
| `antenna` | string | `"ANT1"` | RX/TX antenna |
| `mode` | string | `"DIGU"` | Slice demod mode |
| `daxChannel` | string | `"2"` headless, `"1"` attach | DAX channel to claim — [below](#coexisting-with-smartsdr) |

The headless path disables band persistence and explicitly tunes the slice, so it lands on the
requested frequency regardless of the radio's last-used band.

**The slice mode states the sideband**, so do not also set `sideband`: `DIGU` is USB, `DIGL` is
LSB, and the daemon takes it from the slice. Setting a `sideband` that contradicts the slice mode
is rejected — silently accepting it would mirror every modem about the dial.

**A band plan supersedes `frequency`.** With `rfFrequency` modems the dial is computed, so a slice
frequency here would be saying two different things; the daemon warns and uses the plan.

### Coexisting with SmartSDR

A running SmartSDR grabs **DAX channel 1**, and a headless client on the same channel contends
with it (live finding, 2026-07-17). So an unset `daxChannel` puts a *headless* client on **2** —
which means it does not matter whether SmartSDR was started before or after the modem. Attach
mode keeps 1, because there it is SmartSDR's slice by definition.

Set `daxChannel` explicitly if you have other DAX users to work around.

> **There is no IQ / raw-waveform option here, by design.** The daemon reaches a Flex over
> **DAX audio** only: it hands real audio to the radio's own DIGU SSB modulator, so the signal
> goes through the same TX chain a user's would. The software-IQ route — single-sideband IQ
> through a headless waveform, bypassing the radio's SSB modulator, ALC and TX DSP — exists
> only in the OTA bench harness (`sm-ota ladder --route iq`), and exists precisely *because*
> it bypasses that chain, which makes it a measurement instrument rather than a deployment
> path. See [docs/flex-integration.md](docs/flex-integration.md) § 2.3.

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
`captureRate`, `kissPort`, `bind`, `ptt`, `paging`, `flex` and `waterfall`, so a
`--device` passed alongside `--config` is silently discarded (`--txdelay` still applies —
it has no config equivalent). The exceptions:

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
  "ptt": { "type": "serial", "device": "/dev/ttyUSB0", "line": "rts" }
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

**Winlink over HF via ARDOP, sharing 40m with two packet modes** — one radio, one passband:

```json
{
  "device": "plughw:1,0",
  "captureRate": 12000,
  "modems": [
    { "subChannel": 0, "mode": "afsk300-il2pc", "frequency": 300,  "port": 8100 },
    { "subChannel": 1, "mode": "ardop",         "frequency": 950,  "port": 8101 },
    { "subChannel": 2, "mode": "bpsk300",       "frequency": 1600, "port": 8103 }
  ],
  "ptt": { "type": "serial", "device": "/dev/ttyUSB0", "line": "rts" }
}
```

Point Pat at 8101 (data 8102), and your packet host at 8100 and 8103.

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
