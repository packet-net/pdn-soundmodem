# Configuration reference

Every key the modem reads from its configuration file, with type, default and one sentence each. The keys are defined in `src/Packet.SoundModem.Daemon/DaemonConfig.cs`, which also refuses a file it cannot run; where a value is applied is `src/Packet.SoundModem.Daemon/Program.cs`. How to choose values for a particular station is the guide's business; this page says what each key is and what the modem refuses.

## The file

| | |
|---|---|
| Path | `/etc/pdn-soundmodem/soundmodem.json`; the systemd unit passes it as `--config`. There is no search path and no environment variable; without `--config` the modem runs from flags and defaults alone (see the [command-line reference](command-line.md)). |
| Format | JSON. `//` and `/* */` comments and trailing commas are accepted. Keys match case-insensitively, so `kissPort`, `KissPort` and `kissport` are the same key. |
| Applying a change | Restart the service. The file is read once at start-up. |
| Written by the modem | Never, with one exception: `POST /api/config?persist=true` replaces it on an operator's explicit instruction. |
| Seeded from | `/usr/share/pdn-soundmodem/soundmodem.example.json`, copied in by the package on first install. |

An empty object `{}` is a valid file. It gives a station on ALSA device `default` at 48000 Hz capture, one `afsk1200` modem on sub-channel 0 at 1700 Hz, KISS on `127.0.0.1:8105`, no PTT line (the radio's VOX or nothing), the transmitter test enabled but unusable without a `ptt`, id-beacon listening and frequency matching on, and no browser page, frame log or other service.

An unknown key anywhere in the file is reported at start-up and ignored. The journal line is `config: WARNING - <section>: "<key>" is not a setting this version knows, and is being IGNORED. Check the spelling against <url>`, where `<section>` is the object it was found in (`waterfall`, `modem 1`, `alsa mixer`, `monitor uplink GB7RDG-2` and so on) and is omitted for a top-level key. Unknown keys inside `metrics` and `frequencyMatching` are the two exceptions: those objects keep them but nothing reports them.

An invalid file makes the modem exit with status 2 after printing what is wrong and what to do (the exit codes are listed in the [command-line reference](command-line.md#exit-codes)). The unit sets `RestartPreventExitStatus=2`, so systemd leaves the service stopped and the journal holds one explanation:

```
configuration error in /etc/pdn-soundmodem/soundmodem.json
  two modems share "subChannel": 0. Each modem needs its own KISS sub-channel (0-15) - renumber one of them.

  The service will not start until this is fixed. As root, to start
  from a known-good file:
    cp /usr/share/pdn-soundmodem/soundmodem.example.json /etc/pdn-soundmodem/soundmodem.json
  Then edit it for your sound device and PTT, and:
    systemctl restart pdn-soundmodem
  Every setting is documented at https://github.com/packet-net/pdn-soundmodem/blob/main/CONFIG.md
```

The same frame carries `no such file: <path>`, `no such directory: <dir>`, `permission denied reading the file`, `the file is empty`, ``the file contains only `null` - there is nothing to configure from`` and `not valid JSON - line L, position P: <detail>` (counted from 1, as an editor does). The frame is used for every refusal raised while the file is read, which is everything `DaemonConfig` checks: the file-level errors above, `bind`, the port claims, sub-channels and the `rfFrequency` rules, `txTest`, `modemPlugins`, `alsa`, `flex.transmitFilterHighHz`, `deadFeed`, the sideband kinds, `monitor` and `publish`, plus the `publish.audioRate` divisor check, which waits for the modems. Refusals raised later in start-up are one or two bare lines on stderr with exit 2 and no recovery text: an unknown mode and the mode rules under `modems`, every `identify` refusal, `ptt`, `captureRate`, `ubersdr`, `flex.txPowerWatts` and the sideband contradiction, ARDOP given twice via `--ardop`, the band plan, the page's port and settings, `api`, `frameLog`, `survey`, `rawCapture`, and a monitor's own start-up checks. Where a section below says a line is a warning, start-up continues.

Exit status 1 is different: hardware the file names but the machine does not have (a sound card that has not enumerated, a `/dev/hidraw0` that is not there, a radio still booting) exits 1 with a message naming the key and the file, and the service keeps retrying every five seconds.

## Top-level keys

In the order `DaemonConfig` declares them. `sideband`, `dialFrequency` and each modem's `rfFrequency` are covered together under [Band placement](#band-placement-sideband-dialfrequency-and-rffrequency) at the end of the page.

| Key | Type | Default | What it is |
|---|---|---|---|
| `device` | string | `"default"` | The audio input and output: an ALSA name, `null`, `pipe:`, `flex:` or `ubersdr:`. |
| `captureRate` | int | `48000` | ALSA capture and playback rate in Hz; the modem decimates to its DSP rate. |
| `kissPort` | int | `8105` | The shared KISS TCP port carrying every packet modem by sub-channel nibble. |
| `bind` | string | `"127.0.0.1"` | The address every listener binds to: KISS, per-modem ports, the station page, paging and ARDOP. `"*"` or `"0.0.0.0"` for all interfaces. |
| `sideband` | string | `"usb"` | What kind of radio this is, `"usb"`, `"lsb"` or `"fm"`, for turning `rfFrequency` into audio. See [Band placement](#band-placement-sideband-dialfrequency-and-rffrequency). |
| `dialFrequency` | number | chosen by the modem | Pins the dial in Hz instead of letting the band plan choose one. See [Band placement](#band-placement-sideband-dialfrequency-and-rffrequency). |
| `modems` | array | one `afsk1200` on sub-channel 0; none when a top-level `ardop` section is present | The modems sharing the audio channel. |
| `modemPlugins` | array | `[]` | Assemblies outside the package that provide extra modes. |
| `ptt` | object | absent: no keying line | How the radio is keyed: `serial` or `cm108`. |
| `txTest` | object | enabled, 5 s, cap 30 s | Bounds on the operator's two-tone and single-tone transmitter test. |
| `alsa` | object | absent: levels left alone | The sound card's mixer levels. |
| `paging` | object | absent: off | The POCSAG paging endpoint. |
| `ardop` | object | absent: off | Legacy way to start the ARDOP virtual TNC; a modem entry with `"mode": "ardop"` is the current form. |
| `flex` | object | absent: defaults | Slice parameters for a headless FlexRadio. |
| `ubersdr` | object | absent: defaults | Stream parameters for a public UberSDR web receiver. |
| `waterfall` | object | absent: no page | The station page: spectrum, waterfall, frames, links, and the HTTP listener that `api` and `metrics` share. |
| `monitor` | object | absent: not a monitor | Turns the process into a monitor site fronting many web receivers. Exclusive with `device`. |
| `publish` | object | absent: publishes nothing | Dials out to a monitor site and offers this station on it. Exclusive with `monitor`. |
| `api` | object | absent: no API | The runtime configuration API under `/api/` on the page port; `key` only. |
| `frameLog` | object | absent: not kept | A SQLite log of every frame heard and sent. |
| `survey` | object | absent: not surveying | Records bursts the station could not read. |
| `metrics` | object | absent: not served | `/metrics` and `/metrics/frames` on the page port. |
| `frequencyMatching` | object | absent: on with defaults | Answering an off-frequency station on its own frequency. |
| `rawCapture` | object | absent: off | Continuous chunked recording of the receive audio. |
| `deadFeed` | object | absent: per-device defaults | When a silent or stalled input restarts the service. |
| `idBeacons` | bool | `true` | Listen for the 300 baud AFSK idents a NinoTNC sends beside its PSK modes. |

## `device`

```json
{ "device": "plughw:CARD=Device,DEV=0" }
```

| Value | What it opens |
|---|---|
| `default`, `plughw:1,0`, `plughw:CARD=Device,DEV=0` | An ALSA capture and playback device. `aplay -L` lists the stable `CARD=` names. |
| `null` | ALSA's null device: no audio in or out. |
| `pipe:<in>,<out>[,<rate>]` | Two FIFOs standing in for a sound card, for two modems on the same air with no hardware between them. |
| `flex:<radio>[:slice][@station]` | A FlexRadio over the LAN. `<radio>` is `discover`, `host[:port]`, `serial=...`, `name=...` or `mock`; slice `A` to `H`; `@station` attaches to a running SmartSDR's slice instead of creating one. |
| `ubersdr:<instance>` | A public UberSDR web receiver's IQ stream, receive only. A host, a `host:port`, or the `https://` URL you would open in a browser. |

- `captureRate` applies to ALSA and pipe devices only. It must be a multiple of the channel's DSP rate (12000, or 48000 when any 48 kHz mode is configured), or start-up refuses with `--capture-rate must be a multiple of N`. A pipe's own rate has the same rule: `pipe rate N is not a multiple of the channel's N Hz`.
- A `flex:` or `ubersdr:` device provides its own clock; `captureRate` is ignored.
- A device that will not open exits 1 with a message naming the key and the file, and the service retries.

## `kissPort` and `bind`

```json
{ "kissPort": 8105, "bind": "127.0.0.1" }
```

- `bind` must parse as an IP address, or be `"*"`; anything else is refused with `"bind": "<value>" is not an IP address. Use "127.0.0.1" for loopback only, "*" for every interface, or the address of one interface.` A blank value stays on loopback.
- KISS has no authentication. Binding beyond loopback prints a `kiss: WARNING - listening beyond loopback` line at start-up; the text is under [Every listener](ports-and-endpoints.md#every-listener).
- Two services asking for one TCP port are refused before anything opens: `<this> and <that> both want TCP port N. Give them different ports.` The claims are `"kissPort"`, `the "port" of modem N`, `the ARDOP data port of modem N` (its `port` plus one), `the waterfall`, `the paging endpoint`, `the ARDOP command port` and `the ARDOP data port`. With a top-level `ardop` section `"kissPort"` is not claimed, so a clash between it and the ARDOP ports fails when the listener binds rather than at validation; see [`ardop`](#ardop).
- Channel access (TXDELAY, persistence, slot time, TXTAIL) has no key here; the host sets it over KISS at runtime.

## `modems`

```json
{
  "modems": [
    { "subChannel": 0, "mode": "afsk1200", "frequency": 1700 },
    { "subChannel": 1, "mode": "bpsk300", "rfFrequency": 7051600, "port": 8110 }
  ]
}
```

| Key | Type | Default | What it is |
|---|---|---|---|
| `subChannel` | int | `0` | The KISS sub-channel (port nibble) this modem answers to, 0 to 15. |
| `mode` | string | `"afsk1200"` | A catalogue mode name (see [modes](../modes.md)), `"ardop"` for the ARDOP virtual TNC, or a plugin mode written `pluginId:mode`. |
| `frequency` | number | the mode's own centre | Audio centre in Hz, transmit and receive. 1700 for `afsk*`, 1500 for `bpsk*` and `qpsk*` (1650 for `qpsk3600`), the spec centre for `freedv-*` and `ms110d-*`. |
| `rfFrequency` | number | none | Where this modem sits on the band in absolute Hz; the modem then works out the dial and this modem's audio centre. See [Band placement](#band-placement-sideband-dialfrequency-and-rffrequency). |
| `bandwidth` | number | measured from the modem; 2000 for `ardop` | How much room the band plan and the survey allow this modem, in Hz. On `ardop` it is also the TNC's ARQBW, so no session is accepted or asked for wider; only 200, 500, 1000 and 2000 are accepted there. |
| `busyDetect` | bool | `false` | On an `ardop` entry, watch that modem's own slot and tell the TNC when somebody else is using it (`BUSY TRUE`/`BUSY FALSE`, and `ConRejBusy` under `BUSYBLOCK`). Off by default: it is an energy meter, and out-of-band interference that lifts the noise floor faster than the floor estimator follows reads as in-band signal, which on 40 m at GB7RDG meant almost continuously busy. Meaningless on any other mode. |
| `port` | int | none | A TCP port for this modem alone. A packet mode gets KISS there with this modem presented as nibble 0; `ardop` gets the ardopcf host interface, command on this port and data on the next one up (default 8515 and 8516). |
| `offsetPairs` | int | 4, 5 or 0 by mode | Diversity-bank modes only: decoder branches either side of centre. 0 is a single modem. |
| `offsetStepHz` | number | by mode | Diversity-bank modes only: Hz between adjacent branches. |
| `acceptPlainIl2p` | bool | `false` | IL2P+CRC modes only: also pass frames that arrive as plain IL2P with no CRC to the host. |
| `identify` | object | none | Morse identification for this modem; see [`modems[].identify`](#modemsidentify). |

Diversity-bank defaults by mode:

| Modes | `offsetPairs` | `offsetStepHz` |
|---|---|---|
| `bpsk300`, `bpsk300-multi`, `bpsk300-nocrc`, `qpsk600` | 4 | 7.5 (baud / 40) |
| `bpsk1200`, `bpsk1200-multi`, `qpsk2400` | 4 | 30 (baud / 40) |
| `qpsk3600` | 0 | 45 (baud / 40) |
| `afsk300`, `afsk300-il2p`, `afsk300-il2pc` | 5 | 35 |
| `afsk1200-multi` | fixed at 3; the keys are ignored | |
| every other mode | ignored | ignored |

Rules and refusals:

- A `frequency` on a baseband mode (`fsk9600`, `fsk9600-il2p`, `fsk4800-il2p`, `c4fsk9600`, `c4fsk19200`) is refused: `modem N: mode 'X' occupies the audio band from DC upwards and has no centre frequency to move - drop the frequency override`. Every other mode, including `freedv-*` and `ms110d-*`, accepts one.
- `acceptPlainIl2p` on a mode that does not run IL2P+CRC is refused: `modem N: mode 'X' does not run IL2P+CRC, so it has no separate plain-IL2P reading to release - drop "acceptPlainIl2p"`. It applies to `afsk300-il2pc`, `afsk1200-il2p`, `bpsk300`, `bpsk300-multi`, `bpsk1200`, `bpsk1200-multi`, `qpsk600`, `qpsk2400`, `qpsk3600`, `fsk9600-il2p`, `fsk4800-il2p`, `c4fsk9600`, `c4fsk19200`, `freedv-*` and `ms110d-*`. With it on, the journal says once per modem that plain IL2P frames are checked by Reed-Solomon alone.
- Two entries with the same `subChannel`: `two modems share "subChannel": N. Each modem needs its own KISS sub-channel (0-15) - renumber one of them.`
- A `mode` that is not a catalogue mode, `ardop` or a loaded plugin's mode: `modem N: unknown mode 'X'`, followed by `did you mean: ...` for a near miss and a link to the mode list. For a `pluginId:mode` name the second line says whether the plugin is loaded and what it provides.
- `frequency` and `rfFrequency` on one entry, on USB or LSB: `modem N sets both "frequency" (F) and "rfFrequency" (R). Those say the same thing two ways ... Keep one.` On FM both are allowed; `rfFrequency` is the channel and `frequency` is where the tones sit in its audio.
- `rfFrequency` on some entries and not others: `some modems have "rfFrequency" and some do not (...). Give every modem an "rfFrequency" or none of them`.
- Two entries with `"mode": "ardop"`: `two modems have "mode": "ardop". One ARDOP TNC per channel`.
- An `ardop` entry beside a top-level `ardop` section: `ARDOP is configured twice - once as a modem entry and once in the top-level "ardop" section. Keep the modem entry ... and delete the "ardop" section.`
- A plugin mode whose declared rate is neither 12000 nor 48000 is refused with a sentence naming that mode and the two rates a channel runs at; one that differs from the rate the other modems settle the channel at is refused with a sentence naming it and the built-in mode that fixed the rate. Built-in modes share a channel at either rate.
- An `ardop` entry whose `bandwidth` is not one of the four widths ARDOP negotiates: `modem N is "mode": "ardop" with "bandwidth": B. ARDOP negotiates 200, 500, 1000 or 2000 Hz and nothing else - use one of those, or remove "bandwidth" to plan for the widest (2000) as before.` The value that is accepted becomes the TNC's ARQBW at start-up, reported on the `ardop host tcp:` line; an entry with no `bandwidth` keeps ARDOP's own default of `2000MAX`.
- An `ardop` entry whose `frequency` leaves less room inside a nominal 300-2700 Hz passband than its `bandwidth` asks for (2000 Hz when it states none) prints `ardop: WARNING - centre F Hz leaves room for an ARDOP bandwidth of W Hz ...`; a centre at or beyond the engine's 6000 Hz Nyquist prints the same prefix with `is outside the 0-6000 Hz band`.
- ARDOP shares the channel with the packet modems. An ARQ session holds packet transmissions until it ends.
- The ARDOP entry without a `port` listens on 8515, data on 8516.

### `modems[].identify`

```json
{ "identify": { "callsign": "M0LTE", "intervalMinutes": 10 } }
```

| Key | Type | Default | What it is |
|---|---|---|---|
| `callsign` | string | none; required | The callsign sent. |
| `intervalMinutes` | number | `10` | Minutes between idents. The clock runs only while this modem transmits; an idle modem never keys to identify. |
| `wpm` | number | `20` | Sending speed, PARIS words per minute. |
| `toneHz` | number | the modem's `frequency` when written, else the band plan's centre for it | The audio tone keyed. |
| `rfFrequency` | number | none | Where to identify in absolute Hz, as an alternative to `toneHz`; needs a band plan. |
| `includeMode` | bool | `false` | Send the mode name after the callsign, as in `M0LTE FREEDV-DATAC1`. |
| `amplitude` | number | `0.8` | Key-down peak, 0 to 1. |

- Refused: `identify` on a receive-only station (`"identify" needs a transmitter, and this station receives only`); on an `ardop` entry (`"identify" is not supported on ardop`); without a `callsign` (`"identify" needs a "callsign" - there is no default for a licence condition`); with both `toneHz` and `rfFrequency` (`they say the same thing two ways. Keep one.`); `rfFrequency` on FM (`has no meaning on FM`); `rfFrequency` with no band plan (`needs a band plan`); no `toneHz` on a modem that has neither a written `frequency` nor a band plan (`mode 'X' has no audio centre, so there is nothing to default the ident tone to`), which is every baseband mode and also any mode left on its default centre with no `rfFrequency` anywhere, so a single-modem file that identifies needs `toneHz` or `frequency` written down. An amplitude, speed or interval the identifier will not take is `"identify" is not usable` with the reason under it. Each line is prefixed `modem N:`.
- The RF form of a tone is the same arithmetic as Band placement: on USB the tone is `rfFrequency - dial`, on LSB `dial - rfFrequency`.
- A tone outside the band plan's passband is a warning, not a refusal: `modem N: WARNING - the ident tone F Hz is outside the L-H Hz passband this plan plays into, so it may be filtered away on transmit.`
- A transmitter test transmission counts as a transmission for the ident clock.

## `modemPlugins`

```json
{ "modemPlugins": [ { "path": "/opt/pdn/plugins/M0LTE.OfdmFm.dll" } ] }
```

| Key | Type | Default | What it is |
|---|---|---|---|
| `path` | string | none; required | The assembly to load. A relative path resolves against the working directory, so write an absolute one under systemd. |

- Nothing is discovered: only the paths listed load, and the journal repeats each one as `modem plugin: <id> from <path> [<modes>]`. The modes a plugin provides are written `pluginId:mode` in `modems[].mode` and cannot be spelt with `--modem`, whose separator is the same colon.
- An entry with no `path` is refused: `a "modemPlugins" entry has no "path". Each entry names one assembly to load`.
- A plugin that fails to load is reported as `modem plugin: FAILED <path> - <reason>` and start-up continues; a modem that asked for one of its modes then fails as an unknown mode. See [modem plugins](../dev/modem-plugins.md).

## `ptt`

```json
{ "ptt": { "type": "cm108", "device": "/dev/hidraw0", "gpio": 3 } }
```

| Key | Type | Default | What it is |
|---|---|---|---|
| `type` | string | `"serial"` | `"serial"` for an RTS or DTR line, `"cm108"` for the GPIO on a CM108-class interface. |
| `device` | string | `""` | The device path: `/dev/ttyUSB0` for serial, `/dev/hidraw0` for CM108. |
| `line` | string | `"rts"` | Serial only: `"rts"` or `"dtr"`. |
| `gpio` | int | `3` | CM108 only: the GPIO pin. |

- Omit the whole section for a radio keyed by VOX, or one that has no keying line. A FlexRadio keys itself and a web receiver has no transmitter.
- Refused: a `type` other than `serial` or `cm108` (`unknown ptt type 'X'`); any `ptt` with a `flex:` device (`--device flex: keys the radio itself; remove the conflicting --ptt (serial:/cm108:)`); any `ptt` with a `ubersdr:` device (`--device ubersdr: is a receive-only station ... Remove "ptt".`).
- A device that cannot be opened exits 1, with the file, the key, an `ls` to run and the udev note for `/dev/hidraw*`, and the service retries. The [`--ptt` flag](command-line.md#station-flags) replaces this section.
- Without a `ptt` the transmitter test is refused: `tx test: unavailable - no "ptt" is configured, so this daemon does not key the radio`.

## `txTest`

```json
{ "txTest": { "enabled": true, "seconds": 5, "maxSeconds": 30, "amplitude": 0.8 } }
```

| Key | Type | Default | What it is |
|---|---|---|---|
| `enabled` | bool | `true` | Whether the test exists at all: the button on the station page, `POST /api/txtest` and `--two-tone` / `--tone`. |
| `seconds` | number | `5` | How long a test runs when the request does not say. |
| `maxSeconds` | number | `30` | The longest test that will run whatever is asked for; clamped to between 1 and 60 however it is set. |
| `amplitude` | number | `0.8` | Peak level of the burst, above 0 and at most 1. |

- The section is present by default; there is nothing to switch on. The tones are not settings: two-tone is 700 and 1900 Hz, and a single tone takes its frequency per request.
- Refused at load: an `amplitude` that is not above 0 and at most 1 (`"txTest"."amplitude": A is not a level`) and a `seconds` of zero or below (`"txTest"."seconds": S is not a length`).
- The test is unavailable, with the reason in the journal, when `enabled` is false, when the station receives only, or when there is no `ptt`. The control is never on a public page. On a monitor `--two-tone` and `--tone` are refused with exit 2.

## `alsa`

```json
{ "alsa": { "mixer": { "captureGainDb": -12, "playbackDb": 0 } } }
```

`alsa` holds one object, `mixer`. The mixer is opened and read at every start-up whatever the file says. Absent, no level is set from the file; a capture or playback level remembered in the state file from an earlier page or `/api/mixer` change is still applied, and AGC and mic boost are still switched off.

| Key | Type | Default | What it is |
|---|---|---|---|
| `captureGainDb` | number | absent: left alone | Capture gain in dB, inside the card's own range. |
| `playbackDb` | number | absent: left alone | Transmit-side playback level in dB, inside the card's range. |
| `card` | string | derived from `device` | The mixer card when it is not the one the device string implies. |
| `stateFile` | string | `mixer-state.json` in the state directory | Where a change made on the station page or over `/api/mixer` is remembered between runs. |
| `captureControls` | string array | `Mic`, `Mic Capture`, `Capture` | Control names to look for the capture gain under, in order. |
| `agcControls` | string array | `Auto Gain Control`, `AGC`, `Mic AGC` | Control names to look for the AGC switch under; it is only ever switched off. |
| `micBoostControls` | string array | `Mic Boost`, `Mic Boost (+20dB)`, `Internal Mic Boost`, `Mic Capture Boost` | Control names to look for the mic boost under; it is only ever switched off. |
| `playbackControls` | string array | `Speaker`, `PCM`, `Master`, `Headphone` | Control names to look for the playback level under, in order. |

- Names are matched case-insensitively; a list of only blank strings falls back to the built-in one.
- The card's range is printed by [`--mixer-show DEVICE`](command-line.md#one-shot-flags) and in the start-up journal. A level outside it is one journal line naming the range, and that control is left alone; start-up continues.
- A level pinned here is applied at every start-up and wins over the state file; the state file fills in only for a control this section says nothing about. AGC and mic boost are switched off at every start-up on any card that has them.
- The default state file is `$STATE_DIRECTORY/mixer-state.json`, which is `/var/lib/pdn-soundmodem/mixer-state.json` under the shipped unit, else a file beside the config file.
- Removed keys are warned about by name. `captureGainPercent` and `playbackPercent`: `alsa mixer: <key> is no longer read; use <captureGainDb or playbackDb>, the card's range is shown by --mixer-show`. `agc` and `micBoost`: `alsa mixer: <key> is no longer a setting: AGC and mic boost are switched off at every start-up on any card that has them ...`, with a second sentence saying to remove the key.
- Refused: `alsa.mixer` beside `monitor` (`A monitor fronts web receivers and has no sound card of its own`); `alsa.mixer` with a `device` that is not a sound card (`"alsa"."mixer" is set but "device" is "X", which is not a sound card`); a `stateFile` that names the configuration file itself (`which is this configuration file. That file is never written by this daemon and a mixer change would overwrite it`).
- A card with no mixer is not a failure: `alsa: mixer: <card> has no mixer (<why>); the capture gain and the transmit level are left as the card has them, and there is no AGC or mic boost to switch off`.

## `paging`

```json
{ "paging": { "port": 8106, "baud": 1200, "invertPolarity": false } }
```

| Key | Type | Default | What it is |
|---|---|---|---|
| `port` | int | `8106` | The line-based paging TCP port. |
| `baud` | int | `1200` | POCSAG bit rate. The encoder supports 512, 1200 and 2400; the modem does not check the number, and what the POCSAG library does with another value is not documented. |
| `invertPolarity` | bool | `false` | Invert the transmit baseband, for a radio whose data path inverts. |

- Present means on. Paging shares the channel, its carrier sense and the PTT line with the packet modems.
- The `--paging PORT[:BAUD]` flag replaces this section. A port already claimed by another service is refused as under [`kissPort` and `bind`](#kissport-and-bind).

## `ardop`

```json
{ "ardop": { "port": 8515 } }
```

| Key | Type | Default | What it is |
|---|---|---|---|
| `port` | int | `8515` | Host-interface command port; data always listens on the next port up. |

- Legacy. A modem entry with `"mode": "ardop"` does the same and can also carry `frequency`, `rfFrequency`, `port` and `bandwidth`. Given only this section, the modem folds it into an `ardop` entry on the lowest free sub-channel at start-up. With no `modems` beside it the file gets no default `afsk1200` and no KISS port, so `{ "ardop": {} }` is an ARDOP-only station.
- Refused beside an `ardop` modem entry, see [`modems`](#modems). The `--ardop PORT` flag wins over this section when both are given, and is refused beside an `ardop` modem entry: `ARDOP is configured twice - as a modem and with --ardop/"ardop". Keep the modem entry.`
- With this section present the port check does not compare `kissPort` against the ARDOP ports, a hold-over from when ARDOP excluded the packet modems, so a `port` equal to `kissPort` beside packet modems passes validation and fails when the KISS listener binds.

## `flex`

```json
{ "device": "flex:10.45.0.76", "flex": { "antenna": "ANT1", "daxChannel": "3", "receiveOnly": true } }
```

`frequency`, `antenna`, `mode` and `stationName` are read for a headless `flex:` device (no `@station`); the M0LTE.Flex package documents the first three as ignored in attach mode. `daxChannel` and `arbitration` apply in attach mode too. `txPowerWatts`, `receiveOnly` and `transmitFilterHighHz` are handed to that package on both paths, and whether attach mode honours them is decided inside it. The two range refusals below fire whatever `device` names; otherwise the section is ignored for any other device.

| Key | Type | Default | What it is |
|---|---|---|---|
| `frequency` | string | `"14.100000"` | Slice frequency in MHz, six-decimal Flex form. A band plan supersedes it. |
| `antenna` | string | `"ANT1"` | Receive and transmit antenna. |
| `mode` | string | `"DIGU"` | Slice demodulation mode. It states the sideband. |
| `daxChannel` | string | `"2"` headless, `"1"` attach | The DAX channel this client claims. Two headless instances on one radio need different channels. |
| `receiveOnly` | bool | `false` | Never write the radio's global transmit state and never contend for a slice; for a second instance sharing a radio with a transmitting station. |
| `txPowerWatts` | number | absent: radio's own setting | Transmit power in watts, 0 to 100. |
| `transmitFilterHighHz` | int | absent: derived from the modems | Transmit-filter high cut in Hz, 500 to 10000; `0` leaves the radio's own filter alone. |
| `stationName` | string | `"pdn-soundmodem"` | The station name this client registers with the radio. |
| `arbitration` | bool | `false` | Key through the arbitrated PTT: wait for the radio to be quiet, re-assert the filter and slice, believe only a confirmed keyup. |

- The `--flex-freq`, `--flex-ant`, `--flex-mode` and `--flex-daxch` flags override `frequency`, `antenna`, `mode` and `daxChannel`.
- Refused: `transmitFilterHighHz` outside 500 to 10000 and not 0 (`"flex"."transmitFilterHighHz" is N. That is an audio cut-off in Hz ... use 500-10000, 0 to leave the radio's own filter alone, or remove it`); `txPowerWatts` below 0 or above 100 (`"flex"."txPowerWatts" is N W, outside the 0-100 W a 6000-series PA can produce`); a stated top-level `sideband` that contradicts what `mode` implies (`"sideband": "usb" contradicts the Flex slice mode DIGL, which is LSB ... Drop "sideband"`). A defaulted `sideband` is corrected to the slice mode's without a word.
- With a band plan, a stated `frequency` is superseded and warned about: `flex: WARNING - the slice frequency you set (F) is superseded by the band plan, which computed D`. The modem sets the slice, the transmit filter high cut and the slice receive filter from the plan and says so.
- A modem outside the radio's transmit or receive filter is a warning naming the modem, its edges and the filter; it is clipped or deaf, not refused.
- `ptt` and `alsa.mixer` are refused with a `flex:` device.

## `ubersdr`

```json
{ "device": "ubersdr:m9psy-1.instance.ubersdr.org", "ubersdr": { "mode": "iq48", "onDemand": true } }
```

Read for a `ubersdr:` device and for the receivers a `monitor` fronts. Ignored otherwise. The key is matched case-insensitively, so `ubersdr` and `uberSdr` name the same section.

| Key | Type | Default | What it is |
|---|---|---|---|
| `mode` | string | `"iq48"` | The receiver's IQ mode; `iq96` where an instance allows it. |
| `password` | string | absent | Password for a protected instance. |
| `ssbLowHz` | number | `150` | Lower edge of the SSB filter synthesised from the IQ, Hz above the dial. |
| `ssbHighHz` | number | `3450` | Upper edge of that filter. |
| `startupGuardMs` | int | `1000` | Audio discarded after each connect, in ms. |
| `gain` | number | `1.0` | Linear gain on the demodulated audio. |
| `onDemand` | bool | `false` | Hold a session on the receiver only while somebody has the station page open. |
| `lingerSeconds` | int | `60` | With `onDemand`, how long the session is kept after the last viewer leaves; 0 closes at once. |

- The receiver is tuned by the band plan: every modem needs an `rfFrequency`, or `dialFrequency` must be set. Otherwise: `the UberSDR instance at X has to be told where to listen. Give every modem an "rfFrequency" ... or set "dialFrequency" to pin it`.
- Refused: `"sideband": "fm"` (`cannot be served by X: a web receiver is an SSB receiver`); `onDemand` without a `waterfall` section (`"ubersdr"."onDemand" needs a "waterfall" section`); a negative `lingerSeconds` with `onDemand` (`"ubersdr"."lingerSeconds" cannot be negative`); any `ptt`, `alsa.mixer`, `publish` or `identify`, each named under its own section.
- The station receives only. Frames arriving over KISS are refused with `tx[N] DROPPED ... this station receives only`, and the transmitter test is unavailable.
- A receiver that cannot be reached at start-up exits 1 either way, and the service retries. With `onDemand`, a receiver that goes away later, or refuses a session, is retried while the page stays up and says so.

## `waterfall`

```json
{ "waterfall": { "port": 8107, "public": false, "title": "GB7RDG 40 m" } }
```

There is no `bind` key here. The page listens on the top-level `bind`.

| Key | Type | Default | What it is |
|---|---|---|---|
| `port` | int | `8107` | The HTTP and WebSocket port; `api` and `metrics` share it. |
| `dialFrequencyHz` | number | `0` | The page's opening dial in Hz. 0 takes the band plan's dial or `dialFrequency`, else audio frequencies only. |
| `sideband` | string | `"usb"` | What the page draws for: `"usb"`, `"lsb"` or `"fm"`. The band plan's answer wins when there is one; a top-level `"fm"` wins when this is not stated. |
| `linesPerSecond` | int | `30` | Waterfall line rate. Must divide the DSP rate. |
| `fftSize` | int | `0` | FFT length; 0 takes 2048 at 12 kHz or 8192 at 48 kHz. Must be a power of two no shorter than one line's hop. |
| `public` | bool | `false` | A page for visitors: shows `title` and `about`, credits the receiver, hides the KISS host badges, never carries the mixer or the transmitter test. |
| `enableAudioControls` | bool | `false` | Serve the mixer group and `/api/mixer` with no `api.key`, on an operator's page only. |
| `title` | string | the page's own | Public page title. |
| `about` | string | none | One paragraph for a visitor on a public page. |

- The `--waterfall PORT` flag stands in for or overrides `port`; `--dial HZ` sets `dialFrequencyHz` and is refused without a page.
- Refused: a `sideband` that is not `usb`, `lsb` or `fm` (`"waterfall"."sideband": "X" is not a kind of radio this knows`); a port already claimed (see [`kissPort` and `bind`](#kissport-and-bind)); a port that cannot be opened (`cannot serve the waterfall on ADDR:PORT ... Set by "waterfall"."port" and the top-level "bind"`); an `fftSize` or `linesPerSecond` the spectrum source will not take (`invalid waterfall settings ... Set by "waterfall"."fftSize" and "waterfall"."linesPerSecond"`).
- `enableAudioControls` with `public` is ignored and said so: `waterfall: "enableAudioControls" is IGNORED on a "public" page`.
- A `bind` beyond loopback prints a `waterfall: WARNING - listening beyond loopback` line naming the transmitter test on an operator's page and, when open, the mixer; the text is under [Every listener](ports-and-endpoints.md#every-listener).
- The mixer group and its level meter appear only on an operator's page of a sound-card station where `/api/mixer` answers, which needs `api.key` or `enableAudioControls`.

## `monitor`

```json
{
  "bind": "*",
  "waterfall": { "port": 8099, "title": "UK packet monitor" },
  "monitor": {
    "publicUrl": "https://monitor.example.org",
    "modems": [ { "subChannel": 0, "mode": "afsk300-il2pc", "rfFrequency": 7050300 } ],
    "uplinks": [ { "callsign": "GB7RDG-2", "slug": "gb7rdg-2", "tokenSha256": "<64 hex>" } ]
  }
}
```

A file with `monitor` describes a site, not a station. It reads `bind`, `sideband`, `dialFrequency`, `modemPlugins`, `waterfall`, `ubersdr`, `frameLog`, `idBeacons`, `deadFeed` and this section; it serves no KISS, PTT, API, survey, paging, ARDOP, metrics or transmitter test. The other station sections (`kissPort`, `captureRate`, `ptt`, `api`, `survey`, `metrics`, `rawCapture`, `frequencyMatching`, `paging`, `ardop`, `txTest`) are known keys, so a monitor file accepts them without a word and never reads them; only `device`, `publish` and `alsa.mixer` are refused.

| Key | Type | Default | What it is |
|---|---|---|---|
| `publicUrl` | string | `""` | This site's address as the world reaches it, scheme and host and optional port only; empty derives it from each request's `Host` header. |
| `directory` | string | `https://instances.ubersdr.org/api/instances` | Where the receiver list is fetched from; an absolute http or https URL. |
| `refreshMinutes` | int | `5` | How often the directory is fetched again; 0 fetches once at start-up. |
| `lingerSeconds` | int | `60` | How long a receiver's session is held after its last viewer leaves. |
| `allow` | string array | `[]` | When non-empty, the only hosts offered, matched on the directory's `host` case-insensitively. |
| `deny` | string array | `[]` | Hosts never offered; `deny` beats `allow`. |
| `modems` | array | `[]`; required | The modems every receiver runs, in the `modems` schema; every entry needs an `rfFrequency`. |
| `uplinks` | array | `[]` | Private stations this site accepts an uplink from. Empty refuses every connection to `/uplink`. |

Each `uplinks` entry:

| Key | Type | Default | What it is |
|---|---|---|---|
| `callsign` | string | required | The callsign the station must say it is; one to six letters and digits with an optional `-SSID`. |
| `slug` | string | required | The path segment its page is served under, `/r/<slug>/`: lower-case letters, digits and hyphens, no hyphen at either end. |
| `tokenSha256` | string | required | The SHA-256 of the token issued to the station, 64 hex characters, as printed by [`--uplink-token`](command-line.md#one-shot-flags). |

Rules and refusals:

- `waterfall.public` is forced true on a monitor.
- Refused: `device` written in the file (`this file sets both "device" ("X") and "monitor"`); `monitor.modems` empty (`"monitor"."modems" is empty`); no `waterfall` section (`"monitor" needs a "waterfall" section`); a `waterfall` with no `port` written down (`"waterfall" has no "port". A monitor serves its whole site on that one port`); no `monitor.modems` entry with an `rfFrequency` (`"monitor"."modems" has no "rfFrequency"`; a list where only some entries have one is not caught and fails with an unhandled exception); `"sideband": "fm"` (`cannot be served by a monitor`); a negative `refreshMinutes` or `lingerSeconds`; a `directory` that is not an absolute http or https URL; a `publicUrl` with credentials in it (the message does not repeat the value) or with anything after the host and port; an `allow` or `deny` entry that is not a bare hostname; `alsa.mixer` or `publish` beside `monitor`; a modem the station could not build, with the same message a station gets.
- `uplinks` refusals: a `callsign` that is not one, a `slug` that cannot be a path segment, a `tokenSha256` that is not 64 hex characters, and two entries sharing a slug, a callsign or a hash. Each names the entry and what to write instead.
- `frameLog.path` is a directory on a monitor, one `frames-<slug>.db` per receiver; a path that is a file or ends `.db` is refused with a sentence saying so.

## `publish`

```json
{ "publish": { "url": "wss://monitor.example.org/uplink", "token": "<issued>", "callsign": "GB7RDG-2", "operator": "M0LTE" } }
```

| Key | Type | Default | What it is |
|---|---|---|---|
| `url` | string | required | The site's uplink endpoint, an absolute `ws` or `wss` URL. |
| `token` | string | required | The token the site issued this station, at least 32 characters, pasted in as given. |
| `callsign` | string | required | This station's callsign, one to six letters and digits with an optional `-SSID` as under `monitor.uplinks`; the site checks it against the token. |
| `operator` | string | absent | Who runs it, up to 40 characters. |
| `location` | string | absent | Roughly where it is, up to 60 characters. |
| `radio` | string | absent | The radio and antenna, up to 60 characters. |
| `site` | string | absent | The operator's own page, an absolute http or https URL. |
| `audioRate` | int | the DSP rate capped at 12000 | The rate audio is published at, 6000 to 48000 and an integer divisor of the DSP rate; the relayed picture spans 0 to half of it. |
| `frames` | string | `"always"` | `"always"` publishes decoded frames whether or not anybody is watching; `"watched"` holds them until somebody is. |

- One way only: audio, frames and a status sentence go up, a viewer count comes down. Nothing on the wire can transmit, retune or reconfigure the station.
- Refused: `publish` beside `monitor` (`one process is not both`); on a `ubersdr:` device (`A receiver like that is already on the monitor site in its own right`); without a `waterfall` section (`"publish" needs a "waterfall" section`); a `url` that is not an absolute ws or wss URL; a `token` missing or under 32 characters; a `callsign` that is not one; a `site` that is not an absolute http or https URL; an `operator`, `location` or `radio` over its limit (`is N characters and the limit is L`); a `frames` other than `always` or `watched`; an `audioRate` outside 6000 to 48000, or one that does not divide the DSP rate once the modems are known (`which N does not divide. The audio is decimated rather than resampled, so it has to be an integer divisor: <list>`).
- Warnings: a plain `ws` URL off the machine (`publish: "url" is "...", which is unencrypted ws to <host> ... Use wss unless this is a test on your own wire.`); a modem above half the published rate (`publish: WARNING - the published audio spans 0 to N Hz, so modem ... will not appear on the site`); a 48000 Hz rate (`about 770 kbit/s upstream while somebody is watching`).
- Roughly 194 kbit/s upstream at 12000 Hz while somebody is watching, 98 at 6000, 770 at 48000. There is no codec.

## `api`

```json
{ "waterfall": { "port": 8107 }, "api": { "key": "<long random string>" } }
```

| Key | Type | Default | What it is |
|---|---|---|---|
| `key` | string | absent: no API | The shared secret every request must present, as `Authorization: Bearer KEY` or `X-API-Key: KEY`. |

- Served under `/api/` on the page port. There is no unauthenticated mode: no key, no API, except `/api/mixer` alone when `waterfall.enableAudioControls` is true.
- Refused: `api` without a `waterfall` section (`"api" is served on the waterfall's HTTP listener, and this station has no "waterfall" section - add one, or remove "api"`); `api` on a station run without `--config` (`"api" needs a --config file to read back and to write changes to`).
- `POST /api/config` takes a whole document, validates it with the same checks as start-up, and restarts the process onto it for one run; `?persist=true` writes it to the config file, the only time the modem writes that file. The mechanics are under [the API](ports-and-endpoints.md#the-api-under-api).
- The start-up journal names the endpoints: `api: configuration over http://.../api/config (key required)`, and `api: modem proposals over .../api/proposals` when `survey.propose` is on.

## `frameLog`

```json
{ "frameLog": { "path": "/var/lib/pdn-soundmodem/frames.db" } }
```

| Key | Type | Default | What it is |
|---|---|---|---|
| `path` | string | `/var/lib/pdn-soundmodem/frames.db` | The SQLite file. On a monitor, the directory the per-receiver files go in. |

- Present means every frame heard and every frame sent is written down, with its audio and RF placement and the decode quality.
- Refused when the file cannot be opened: `cannot open the frame log at <path> ... Set by "frameLog"."path". The service user must be able to write to its directory; remove the "frameLog" section to run without one.`
- With a log, the page's frames panel and links pane open on what is already recorded, and frequency matching replays recent offsets across a restart.

## `survey`

```json
{ "survey": { "path": "/var/lib/pdn-soundmodem/survey", "maxBytes": 536870912, "propose": true } }
```

| Key | Type | Default | What it is |
|---|---|---|---|
| `path` | string | `/var/lib/pdn-soundmodem/survey` | Where captures go, a WAV and a JSON sidecar per burst. |
| `maxBytes` | int64 | `536870912` (512 MiB) | Byte budget for the directory; the oldest captures are deleted to make room. |
| `maxPerHour` | int | `30` | Most captures in any rolling hour. |
| `cooldownSeconds` | number | `120` | How long the same part of the spectrum is left alone after a capture. |
| `marginSeconds` | number | `1.0` | Audio kept either side of the burst. |
| `maxSeconds` | number | `20` | Longest burst still treated as a packet. |
| `minPeakSnrDb` | number | `6` | Weakest burst kept, in dB over the noise floor. |
| `capture` | string array | the three verdicts | Which verdicts to write: `unclaimed`, `missed`, `unattributed`. |
| `propose` | bool | `false` | Read each capture back with every mode that could have carried it and propose modems. |
| `proposeMinCaptures` | int | `3` | Separate verified captures a proposal needs; values below 1 are treated as 1. |

- There is no `decodeClaimSeconds` key.
- An unknown `capture` entry is skipped with `survey: ignoring unknown capture kind "X" (unclaimed, missed, unattributed)`; if none is usable the default three apply.
- Refused when the directory cannot be opened: `cannot open the survey directory at <path> ... Set by "survey"."path".`
- Proposals reach the journal as `propose: ...` and, with an `api.key`, `GET /api/proposals`. The captures are served at `/survey/<file>` on the page port.

## `metrics`

```json
{ "waterfall": { "port": 8107 }, "metrics": { "enabled": true } }
```

| Key | Type | Default | What it is |
|---|---|---|---|
| `enabled` | bool | `true` | Whether to serve `/metrics` (Prometheus text) and `/metrics/frames` (InfluxDB line protocol). |
| `maxStations` | int | `256` | Most stations kept; the least recently heard is dropped on reaching it. |
| `frameWindowSeconds` | number | `300` | How long a frame stays in the per-frame feed; values below 1 are treated as 1. |
| `stationIdleHours` | number | `6` | How long a station keeps its series after its last frame; values below 0.1 are treated as 0.1. |

- Served on the page port with no authentication. Without a `waterfall` section the metrics are collected and a warning says there is nothing to serve them on: `metrics: WARNING - nothing to serve them on. They ride the waterfall's listener; add a "waterfall" section with a port, or remove "metrics".`
- Unknown keys in this section are not reported.
- A Grafana dashboard for these series is at [grafana/pdn-soundmodem.json](grafana/pdn-soundmodem.json).

## `frequencyMatching`

```json
{ "frequencyMatching": { "enabled": true, "maxTrimHz": 50 } }
```

| Key | Type | Default | What it is |
|---|---|---|---|
| `enabled` | bool | `true` | Shift the transmitter towards a station's measured offset. `false` measures and reports only. |
| `samples` | int | `8` | Frames kept per station for the estimate. |
| `maxAgeSeconds` | number | `600` | How old a frame may be and still count. |
| `minSamples` | int | `3` | Frames required before the estimate is acted on. |
| `maxSpreadHz` | number | `20` | Largest spread across those frames that still counts as settled. |
| `maxTrimHz` | number | `50` | Largest shift ever applied. |
| `damping` | number | `0.5` | Fraction of the offset applied to a station that has already moved under a correction once. |
| `chaseThresholdHz` | number | `10` | How far a station's frequency may move after correction starts before the correction stops. |
| `chaseCooldownSeconds` | number | `1800` | How long that station is left alone before its new offset is corrected for again. |
| `maxChases` | int | `3` | How many such moves before the station is given up on; 0 retries for ever. |

- On by default, including when the section is absent; the journal opens with `frequency matching: on - ...` and the thresholds in force. Offsets are measured either way.
- Broadcast destinations (`ID`, `BEACON`, `CQ`, `QST`, `ALL`, `NODES`, `MAIL`, `APRS`, `TEST`, with or without an SSID) are never aimed at one station.
- Unknown keys in this section are not reported. The design is in [frequency matching](../dev/frequency-matching.md).

## `rawCapture`

```json
{ "rawCapture": { "path": "/var/lib/pdn-soundmodem/raw", "chunkMinutes": 15 } }
```

| Key | Type | Default | What it is |
|---|---|---|---|
| `path` | string | `/var/lib/pdn-soundmodem/raw` | Where the chunks are written. |
| `maxBytes` | int64 | `4294967296` (4 GiB) | Byte budget for the directory; the oldest chunks are pruned to fit. |
| `chunkMinutes` | int | `15` | Audio minutes per WAV chunk. |

- Present means on: the unedited receive audio at the DSP rate, continuously. The default budget holds about two days at 12 kHz.
- Refused when the directory cannot be opened: `cannot open the raw-capture directory at <path> ... Set by "rawCapture"."path".`

## `deadFeed`

```json
{ "deadFeed": { "silenceSeconds": 30, "starvationSeconds": 30 } }
```

| Key | Type | Default | What it is |
|---|---|---|---|
| `silenceSeconds` | number | by device | Seconds of unbroken digital silence (every sample zero) that declare the feed dead; 0 turns the watch off. |
| `starvationSeconds` | number | by device | Seconds with no samples delivered at all that declare the feed starved; 0 turns the watch off. |

Per-device defaults, used for whichever key is absent:

| Input | `silenceSeconds` | `starvationSeconds` |
|---|---|---|
| `flex:` | 30 | 30 |
| `ubersdr:` | 30 | 30 |
| ALSA sound card, `pipe:` | 0 (off) | 30 |
| `--wav-loop`, `flex:mock` | 0 (off) | 0 (off) |
| an uplinked station on a monitor | 0 (off) | 30 |

- Either watch firing stops the process with exit 1, so the unit restarts it and rebuilds the input from scratch.
- Refused: a negative value (`"deadFeed"."silenceSeconds" is N. That is how many seconds the watch waits ... use a positive number of seconds, 0 to turn that watch off, or remove it for the device's default.`).
- A Flex that has stood down from a contested slice is silent on purpose and is not restarted by the silence watch.

## `idBeacons`

```json
{ "idBeacons": true }
```

| Key | Type | Default | What it is |
|---|---|---|---|
| `idBeacons` | bool | `true` | Run a 300 baud AFSK AX.25 listener 200 Hz above each PSK modem's carrier, where a NinoTNC sends its station identification. |

- Applies to `bpsk300`, `bpsk300-multi`, `bpsk300-nocrc`, `bpsk1200`, `bpsk1200-multi`, `qpsk600` and `qpsk2400`; `qpsk3600` and every other mode are unaffected. Idents heard are shown on the page, written to the frame log and counted by the survey as decoded; nothing a host sees changes.

## Band placement: `sideband`, `dialFrequency` and `rfFrequency`

```json
{
  "sideband": "usb",
  "dialFrequency": 7049450,
  "modems": [
    { "subChannel": 0, "mode": "afsk300-il2pc", "rfFrequency": 7050300 },
    { "subChannel": 1, "mode": "bpsk300", "rfFrequency": 7051600 }
  ]
}
```

| Key | Where | What it does |
|---|---|---|
| `sideband` | top level | `"usb"`, `"lsb"` or `"fm"`. Which arithmetic turns an RF frequency into an audio one. |
| `dialFrequency` | top level | Pins the dial in Hz. Absent, the modem chooses one, prints it, and on a headless Flex or a web receiver sets it. |
| `rfFrequency` | each `modems[]` entry | The modem's place on the band in absolute Hz. All entries or none. |

The arithmetic, per sideband:

| `sideband` | Audio centre of a modem | Dial when not pinned |
|---|---|---|
| `usb` | `rfFrequency - dial` | Chosen so the modems sit centred in a 300 to 2700 Hz passband, rounded to 50 Hz. |
| `lsb` | `dial - rfFrequency` | The same, mirrored. |
| `fm` | none; `frequency` is a tone on the channel and is left as written | `rfFrequency` is the channel and every modem must give the same one. |

Rules and refusals:

- On USB and LSB the plan writes each modem's audio centre back into `frequency`, so a `frequency` written beside an `rfFrequency` is refused (see [`modems`](#modems)). On FM the page and the plan do no arithmetic and `frequency` stays what it was.
- The passband is the nominal 300 to 2700 Hz of an ordinary SSB rig. A headless Flex, whose filters the modem sets itself, is widened as far as 10000 Hz when the modems need it, and the transmit filter high cut and the slice receive filter follow.
- A chosen dial that leaves a modem outside the passband is refused: `band plan: no dial frequency places every modem inside the 300-2700 Hz passband:` followed by one line per offending modem; when the modems span more than the passband the line reads `these modems span N Hz of RF (... to ...), which is more than the 2400 Hz a single SSB passband can carry`.
- A pinned `dialFrequency` that leaves a modem outside is a warning, not a refusal: `band plan: WARNING - with the dial pinned to D USB, these fall outside the nominal 300-2700 Hz passband. That is only a nominal figure - if your rig passes them, ignore this`.
- A baseband mode (`fsk*`, `c4fsk*`) cannot take an `rfFrequency` on USB or LSB: `band plan: modem N (X) is a baseband mode ... cannot be placed with "rfFrequency"`. On FM it can; `c4fsk9600` on a channel is the ordinary case.
- On FM, modems asking for different channels are refused: `on FM every modem is on the one channel the radio is set to, and these ask for A, B`. A `dialFrequency` that differs from the modems' `rfFrequency` is refused: `On FM those are the same thing said twice - the channel`.
- A `sideband` that is not `usb`, `lsb` or `fm`, at the top level or under `waterfall`, is refused: `"sideband": "X" is not a kind of radio this knows. Use "usb", "lsb" or "fm"`.
- On a headless Flex the slice `mode` states the sideband (`DIGU` and `USB` mean `usb`, `DIGL` and `LSB` mean `lsb`, `FM` and `NFM` mean `fm`); see [`flex`](#flex).
- With no `rfFrequency` anywhere there is no plan: modems sit at their `frequency`, the page's dial comes from `waterfall.dialFrequencyHz` or `dialFrequency`, and a web receiver needs `dialFrequency` to tune at all.
- The plan is printed at start-up as `dial: <MHz> USB` (or `channel: <MHz> FM`) followed by one `modem N <mode> at <MHz> = <Hz> Hz audio` line per modem. Refusals and warnings from the plan are prefixed `band plan:`.

Related: [command-line reference](command-line.md), [ports and endpoints](ports-and-endpoints.md), [files and directories](files.md).
