# Ports and endpoints

Every TCP listener pdn-soundmodem opens, what each one speaks, and what the journal says about it. The listeners are started in `src/Packet.SoundModem.Daemon/Program.cs`; the config keys they read are in the [configuration reference](config.md), and the flags in the [command-line reference](command-line.md).

## Every listener

| Listener | What it carries | Default port | Set by | Protocol |
|---|---|---|---|---|
| Shared KISS port | every packet modem, addressed by sub-channel | `8105` | `kissPort`, `--kiss` | KISS over TCP |
| Per-modem KISS port | one modem, presented as sub-channel 0 | none | `modems[].port` on a packet modem | KISS over TCP |
| ARDOP command port | the ardopcf host interface | `8515` | `modems[].port` on an `ardop` entry, `ardop.port`, `--ardop` | CR-terminated ASCII |
| ARDOP data port | the ardopcf data socket | command port + 1 | always the next port up | length-prefixed blocks |
| Paging port | POCSAG pages in, pages heard out | `8106` | `paging.port`, `--paging PORT[:BAUD]` | one text line per command |
| Page port | the station page, its WebSocket, survey captures, `/metrics`, `/api` | `8107` | `waterfall.port`, `--waterfall` | HTTP and WebSocket |
| Monitor site | the picker, one page per receiver, `/uplink` | none; must be stated | `waterfall.port` with a `monitor` section | HTTP and WebSocket |

Every listener binds to the top-level `bind` (`--bind`), which is `127.0.0.1` by default. `"*"` or `"0.0.0.0"` binds every interface. A `bind` that is not an IP address stops the modem with exit 2. There is no per-listener bind; the `waterfall` section has no `bind` key.

Two services asking for the same TCP port is refused at start-up with both settings named, and the ARDOP data port counts as a claim on command port + 1. The one gap is a top-level `ardop` section, which takes `kissPort` out of the check (see [`ardop`](config.md#ardop)); a clash there fails when the KISS listener binds.

A `--two-tone` or `--tone` run opens no listener at all. A station with only an `ardop` modem opens no KISS port. A monitor opens only the page port.

Beyond loopback the journal warns at start-up, because none of these listeners has any authentication except the API's key and the uplink's token:

```
kiss: WARNING - listening beyond loopback. KISS has no authentication: anything that can reach these ports can transmit on your licence.
waterfall: WARNING - listening beyond loopback. The page has no authentication, and on an operator's page it carries a transmit test: anything that can reach this port can key your transmitter on your licence.
```

The page line shown is the operator page's. A `public` page ends `anything that can reach this port can watch this station.` instead, and with `enableAudioControls` a sentence saying the mixer is open with no key is appended.

Each listener says where it is when it starts:

```
kiss tcp: 127.0.0.1:8105 (all modems, by sub-channel nibble)
kiss tcp: 127.0.0.1:8110 (modem 1 bpsk300 only, as nibble 0)
ardop host tcp: 127.0.0.1:8515 (data 8516, ardopcf-compatible virtual TNC, modem 2, ARQBW 500MAX, centre 950 Hz, busy watch 661-1239 Hz)
paging tcp: 127.0.0.1:8106 (pocsag1200, DAPNET/POCSAG-compatible)
waterfall: http://127.0.0.1:8107/
metrics: http://127.0.0.1:8107/metrics (prometheus) and http://127.0.0.1:8107/metrics/frames (one point per frame, influx line protocol). No authentication.
api: configuration over http://127.0.0.1:8107/api/config (key required). POST replaces it for one run; add ?persist=true to write /etc/pdn-soundmodem/soundmodem.json.
```

## KISS over TCP

The framing is standard KISS: `FEND` (`0xC0`) delimited frames with `FESC` transparency, a command nibble and a port nibble in the first byte. The port nibble is the sub-channel.

### Addressing

| Port | Received frames | Frames from the host |
|---|---|---|
| Shared (`kissPort`) | every modem's frames, each under its own sub-channel | transmitted on the modem the sub-channel names |
| Per-modem (`modems[].port`) | that modem's frames only, relabelled sub-channel 0 | transmitted on that modem whatever sub-channel the host wrote |

Both kinds run at once on one channel, and any number of hosts may attach to any port; every host on a port receives every frame that port publishes. A frame for a sub-channel with no modem is refused and journalled as `tx[N] DROPPED ...: no modem on sub-channel N`, at most once a minute per reason, with the number held back appended to the next line as `(and N more like it in the last minute)`. On a station that receives only, every frame from a host is refused the same way, with the reason, and the journal says so once at start-up. An `ardop` modem entry's `port` is never a KISS port.

A host that stops reading is dropped once more than 1 MiB of frames is queued for it unread, with that reason on its disconnect line.

### Commands

| Command | Name | Direction | What the modem does |
|---|---|---|---|
| `0` | data | both | from a host: queued for transmission on the addressed modem; to hosts: every frame a modem decodes and passes on |
| `1` | TXDELAY | host to modem | one byte, times 10 ms; applied to the channel at once |
| `2` | P | host to modem | one byte, persistence 0 to 255; applied at once |
| `3` | SLOTTIME | host to modem | one byte, times 10 ms; applied at once |
| `4` | TXTAIL | host to modem | one byte, times 10 ms; applied at once |
| `5` | full duplex | host to modem | accepted, changes nothing; the channel is half duplex |
| `6` | SETHW | both | modem-specific; acts on `ms110d-*` modems, see [SETHW on ms110d](#sethw-on-ms110d) |
| `7` | RX quality | modem to host | one JSON frame after each data frame, only with `--quality-frames` |
| `12` | ACKMODE data | both | data with a two-byte id that comes back once the frame has been transmitted, see [ACKMODE](#ackmode) |
| any other | | | ignored |

A known command with too short a payload is ignored the same way: TXDELAY, P, SLOTTIME, TXTAIL or SETHW with no byte, ACKMODE with fewer than two.

TXDELAY, P, SLOTTIME and TXTAIL set the channel's CSMA parameters, which every modem in the process shares, so a value sent by any host on any port applies to all of them. The values live in memory: a restart returns to the configured `txDelay` and the channel's defaults.

### SETHW on ms110d

On a port whose modem is `ms110d-*`, a SETHW frame switches the transmit waveform from the next burst on. Receive is unchanged; the demodulator reads every Phase A waveform whatever the transmit setting.

| Payload byte | Meaning |
|---|---|
| `0` | waveform number: 0 to 8 or 13 |
| `1` (optional) | interleaver: `0` short, `1` long; absent keeps the current one |

An applied SETHW is echoed back to the sending host as a SETHW frame with the same payload, under the port's own sub-channel (0 on a per-modem port). A refused one is journalled and nothing is sent back, because KISS has no error channel. The setting is not written anywhere; a restart returns to the configured `mode`. On any other mode's port SETHW is journalled as ignored. An empty SETHW is ignored silently.

```
modem 3: SETHW -> ms110d-wn2, short interleaver
modem 3: SETHW ignored - interleaver byte 2 is not 0 (short) or 1 (long)
modem 0: SETHW ignored - the modem on port 0 has no hardware settings
```

### ACKMODE

An ACKMODE frame's payload is `id_lo id_hi data...`. The data is transmitted as a data frame would be, and when its audio has fully left the device the two id bytes come back to the host that sent them, alone, in a command `12` frame under the port's own sub-channel (0 on a per-modem port). An id with no data is acknowledged at once. A frame the channel refuses gets no acknowledgement; the journal carries the `DROPPED` line.

### Quality frames

With `--quality-frames` on the command line (there is no config key), every data frame delivered to a port is followed by a command `7` frame on the same sub-channel carrying UTF-8 JSON. Keys are present only when the modem measured them. A frame the station decoded and held back from hosts gets no quality frame.

```
{"mode":"qpsk2400-il2pc","len":56,"corrected":2,"crc":true,"offsetHz":-5,"snrDb":12.3,"emphasisDb":3}
```

| Key | Type | Meaning |
|---|---|---|
| `mode` | string | the catalogue mode that decoded the frame |
| `len` | integer | frame bytes |
| `corrected` | integer | bytes the FEC repaired |
| `crc` | boolean | whether the frame's own check sequence verified |
| `offsetHz` | integer | measured carrier offset from the modem's centre |
| `snrDb` | number, one decimal | signal to noise as the modem measures it |
| `emphasisDb` | number | pre-emphasis the modem detected, on diversity banks |

The start-up line when it is on is `rx-quality frames: on (KISS command 0x07, JSON payload)`.

### Journal

Every attach and every loss is one line naming the port, the host, the number of hosts left on that port afterwards, and which modems the port reaches. A loss that was not a clean close carries its reason.

```
kiss[8105] 192.168.1.50:54312 connected - 2 clients (all modems)
kiss[8110] 127.0.0.1:40000 disconnected - 0 clients (modem 1 only)
kiss[8105] 192.168.1.50:54312 disconnected: the host stopped reading (over 1 MiB of frames queued unread) - 1 client (all modems)
kiss[8105] accept failed: Too many open files - listening continues
```

## ARDOP host interface

An `ardop` modem entry, the top-level `ardop` section or `--ardop PORT` starts an ARDOP 1 virtual TNC from the M0LTE.Ardop package with ardopcf's TCP host interface on two ports: commands on `port` and data on `port + 1`, `8515` and `8516` by default. Configuring an `ardop` modem entry and the top-level section or flag together stops the modem with exit 2; `--ardop` wins over `ardop.port`.

| Socket | Carries |
|---|---|
| Command, `port` | CR-terminated ASCII commands from the host, replies, and asynchronous notifications from the TNC |
| Data, `port + 1` | blocks of a 2-byte big-endian length followed by the payload; TNC-to-host payloads start with a 3-character type tag (`ARQ`, `FEC`, `ERR`, `IDF`) inside the length |

One host per socket, as in ardopcf: a new connection replaces the previous one. Pat, Winlink Express, ARIM, gARIM and hamChat connect unmodified; point them at the command port. `PROTOCOLMODE` `ARQ`, `FEC` and `RXO` are all served. The command set is ardopcf's own, documented at <https://github.com/pflarue/ardop>; this page lists only where this TNC differs.

| Divergence from ardopcf | What happens here |
|---|---|
| busy detection | off unless the modem entry sets `"busyDetect": true`; unset, `BUSY TRUE`/`BUSY FALSE` is never sent and `BUSYBLOCK` has nothing to act on, exactly as before it existed |
| `BUSYDET` 1-10 | accepted and answered as ardopcf answers them, but not honoured: they parameterise the thresholds of ardopcf's rank-order spectral detector, and this station's detector is a band-limited energy meter over the ARDOP slot. `BUSYDET 0` disables detection exactly as ardopcf does, and `BUSY TRUE`/`BUSY FALSE` and `BUSYBLOCK` behave as ardopcf's |
| `CWID` | accepted and answered as ardopcf answers it; no CW identification is transmitted |
| `LOGLEVEL`, `CONSOLELOG`, `DEBUGLOG`, `CMDTRACE` | accepted and answered as ardopcf answers them; nothing changes, the modem's journal is its only log |
| `TXFRAME` | not implemented; answered `FAULT CMD TXFRAME not recoginized`, ardopcf's own spelling for an unknown command |
| `VERSION` | reports `pdn-soundmodem_` followed by the M0LTE.Ardop package version (`pdn-soundmodem_0.4.0` at this release), not the modem's own version |
| The channel | shared with the packet modems. The TNC's audio is moved from its native 1500 Hz to the entry's `frequency`, and to the channel rate when a 48 kHz mode has set it there. While an ARQ session is connected or connecting, packet frames are held in the queue and refused after 30 s |
| Receive-only station | the host ports are still served and every frame the demodulator recovers is listed, but no session can complete; the journal warns at start-up |

The host TNC lives in the M0LTE.Ardop package, at the version `Directory.Packages.props` pins (0.4.0 at this release); the divergences above are read from that package's source, not from this repository. The transcript conformance test in `tests/Packet.SoundModem.Tests/Ardop/ArdopHostLiveTests.cs` compares this TNC's replies with a live ardopcf command by command, excluding `VERSION`.

## POCSAG paging

The `paging` section or `--paging PORT[:BAUD]` opens a line-based service: UTF-8, one command per line, `\n` terminated with a trailing `\r` stripped, empty lines ignored. A line over 4096 characters gets `ERR line too long` and the connection is closed. Replies go to the client that sent the command; `HEARD` lines go to every client. A client that stops reading is dropped once 1 MiB of lines is queued for it.

| Line from the client | Reply |
|---|---|
| `PAGE <ric> <function> ALPHA <text>` | `OK <id>` or `ERR <reason>` |
| `PAGE <ric> <function> NUMERIC <text>` | `OK <id>` or `ERR <reason>` |
| `PAGE <ric> <function> TONE` | `OK <id>` or `ERR <reason>` |
| `PAGE <ric> <function> <other word> ...` | `ERR type must be ALPHA, NUMERIC or TONE` |
| a first word other than `PAGE` | `ERR unknown command (expected PAGE)` |

| Field | Rule |
|---|---|
| `PAGE`, `ALPHA`, `NUMERIC`, `TONE` | case-insensitive |
| `<ric>` | 0 to 2097151, or `ERR ric must be 0..2097151` |
| `<function>` | 0 to 3, or `ERR function must be 0..3` |
| `<text>` | everything after the type to the end of the line, at most 240 characters, or `ERR text too long (max 240 characters)`; ALPHA is 7-bit ASCII and NUMERIC the POCSAG numeric set, with the encoder's own message after `ERR` for anything outside them |
| fewer than four fields | `ERR usage: PAGE <ric> <function> ALPHA\|NUMERIC\|TONE [text]` |

`OK <id>` means the page is queued, not that it is on air; `<id>` counts up from 1 for the life of the process. A page the channel refuses at once, on a station with no transmitter, gets `ERR <reason>` instead. A page that was answered `OK` and then could not be sent has no client to tell and goes to the journal as `page[<id>] to <ric> DROPPED: <reason>`. Pages share the channel's CSMA and PTT with the packet modems; the spec preamble is stretched to cover a longer configured `txDelay`.

| Line to every client | When |
|---|---|
| `HEARD <ric> <function> ALPHA <text>` | a page decoded off air with function 1, 2 or 3 |
| `HEARD <ric> <function> NUMERIC <text>` | a page decoded off air with function 0 |
| `HEARD <ric> <function> TONE` | a page decoded off air with no content |

Control characters in decoded text are replaced with spaces so a page cannot fake a line break.

The encoder supports `512`, `1200` (the default, DAPNET's) and `2400` for `paging.baud`; nothing checks the number. It names the mode label `pocsag<baud>` in the start-up line. `paging.invertPolarity` inverts the transmitted baseband; the decoder detects polarity on its own.

## HTTP on the page port

The `waterfall` section (or `--waterfall PORT`) serves these routes on `waterfall.port`. Requests are matched on `GET`; there is no `HEAD` handling, and anything unmatched is a 404. No route on a station page carries CORS headers, and there is no `/robots.txt` on a station page.

### Routes

| Route | Serves |
|---|---|
| `/`, `/index.html` | the station page, with `Cache-Control: no-cache, must-revalidate` |
| `/links` | the same page, opening on the links pane alone |
| any path with a WebSocket upgrade | the live stream; the page itself opens `ws` |
| `/survey/<file>` | one survey capture from `survey.path`, `audio/wav` or `application/json`; only with a `survey` section |
| `/metrics`, `/metrics/frames` | see [Metrics](#metrics); only with a `metrics` section |
| `/api/config`, `/api/proposals`, `/api/txtest`, `/api/mixer` | see [The API](#the-api-under-api); 404 without an `api.key`, except the mixer exception |

A capture name is served only when it is 1 to 128 characters of lower-case letters, digits, hyphens and dots, contains no `..`, ends in `.wav` or `.json`, and names a file inside the survey directory. Anything else is a 404.

### The WebSocket

The socket carries what the page draws: the station's configuration on connect, decoded frames and their history, link cards, host-port attachment, input level, the radio's status sentence, survey counts and captures, transmissions, and binary spectrum and audio blocks. The station sends `{"type":"ping"}` and drops a page that has said nothing for 60 s; the page answers `{"type":"pong"}`. The page also sends `{"type":"audio","on":true}` to start its audio and `{"type":"spectrum","on":false}` to stop its waterfall lines, each with `on` true or false, and on an operator's page `txtest`, which starts or stops the transmitter test.

A `txtest` from a browser is acted on only when the request's `Origin` header names the host, or the host and port, the request arrived on; a request with no `Origin` header (a script) is allowed. A refused one is journalled at most once a minute. A public page and a page relayed through a monitor carry no transmit control at all.

### The API under /api

An `api` section with a `key` installs the API on the page port. It needs a `waterfall` section and a `--config` file; without either the modem stops with exit 2. Without a key every `/api/` path is a 404, with one exception: `waterfall.enableAudioControls` true on a page that is not `public` serves `/api/mixer` with no key, and nothing else.

The key is presented as `Authorization: Bearer KEY` or `X-API-Key: KEY`; `X-API-Key` is read first. The comparison is fixed-time. A wrong or missing key is a 401 with a plain-text reason and no `WWW-Authenticate` challenge.

| Endpoint | Method | Key | Request | Response |
|---|---|---|---|---|
| `/api/config` | `GET` | required | none | `{"source": "file" or "ephemeral", "configPath": "...", "running": {...}}`; `api.key` and `publish.token` read `(set, not shown)` |
| `/api/config` | `POST` | required | a complete configuration document, the shape of `soundmodem.json` | 400 with the same message the journal would carry, station untouched; or 200 `{"applied": true, "persisted": false, "restarting": true, "note": "..."}` and the process exits 1 for systemd to restart it |
| `/api/config?persist=true` | `POST` | required | as above | as above with `"persisted": true`, written to the config file; 500 if the file cannot be written |
| `/api/proposals` | `GET` | required | none | `{"proposing": false, "why": "...", "proposals": []}` without `survey.propose`; otherwise `{"proposing": true, "examined": N, "readable": N, "skippedForBacklog": N, "proposals": [...]}`, each proposal carrying a `config` to POST to `/api/config` |
| `/api/txtest` | `POST` | required | `{"twoTone": true, "seconds": 5}`, `{"twoTone": false, "toneHz": 999, "seconds": 5}` or `{"stop": true}` | `{"transmitted": bool, "sent": ..., "refused": ..., "failed": ...}` with the unused keys null, answered once the test is over: 200 when it went out, 409 when the station would not run it, 500 when it broke; `{"stopped": true, "note": "..."}` for a stop; 404 with no transmitter or `txTest.enabled` false |
| `/api/mixer` | `GET` | required, or none with `enableAudioControls` | none | `available` true, `card`, `controls` (every control name), `capture` and `playback` (each null when the card has no such control, else `control`, `decibels`, `dbRange` as `min`, `max` and `mutesBelowMin` or null on a card with no dB scale, `percent`, and `source` as `config`, `state` or `none`), `agc` and `micBoost` (each null or `control`, `on` and `forcedOff`, always true), `summary` and `journal` (the start-up lines); `{"available": false, "why": "..."}` on a station with no sound card |
| `/api/mixer` | `POST` | required, or none with `enableAudioControls` | `{"captureGainDb": 6, "playbackDb": -8}`, either or both | 200 with the read-back plus `applied`, `persisted`, `warn`, `stateFile` and `note`; 400 for a level outside the card's range or a removed key; 409 on a station with no mixer |
| `/api/mixer?persist=false` | `POST` | as above | as above | the card is set for this run and nothing is written |
| any other method | | | | 405 with a one-line hint |

A keyless `POST /api/mixer` from a browser is refused with 403 unless its `Origin` header names the host the request arrived on; a request with no `Origin`, or one presenting the key, is allowed.

What a change does to the running station:

| Endpoint | Effect |
|---|---|
| `POST /api/config` | validated first through the same code as start-up; then written to `pending-config.json` in the state directory (`$STATE_DIRECTORY`, else beside the config file), and the process exits 1. The next start-up reads that file once, deletes it, and runs on it; any later restart returns to the config file. The journal says `api: this station is running a ONE-RUN configuration applied over the API` on every start-up it applies to |
| `POST /api/config?persist=true` | written over the config file instead, then the same restart |
| `POST /api/mixer` | applied to the card at once with no restart, and written to the mixer state file so the next start-up sets it again. The config file is never written; a level it pins wins at the next start-up (see [`alsa`](config.md#alsa)), and the answer says so with `"warn": true` |
| `POST /api/txtest` | keys the transmitter for the test and answers when it is over |

Run outside systemd, an applied configuration stops the modem rather than restarting it; the journal warns at start-up when no `INVOCATION_ID` is present.

## Metrics

A `metrics` section serves what the station has heard on the page port, with no authentication. With no `waterfall` section there is nothing to serve them on and the journal warns. `metrics.enabled` false turns them off.

| Route | Format | Content type |
|---|---|---|
| `/metrics` | Prometheus text exposition: totals and sums per station and mode | `text/plain; version=0.0.4; charset=utf-8` |
| `/metrics/frames` | InfluxDB line protocol: one point per frame heard in the last `frameWindowSeconds` | `text/plain; charset=utf-8` |

A frame counts only when the frame's own check sequence verified (a CRC that passed, or an HDLC or FX.25 frame whose FCS passed), the station passed it to hosts rather than holding it back, and its AX.25 source address parsed. Every other decode adds one to `pdn_frames_uncounted_total`. Series are keyed by base callsign and mode; SSIDs are folded into the callsign and kept as a field on the per-frame feed.

| Series | Type | Labels | Meaning |
|---|---|---|---|
| `pdn_station_info` | gauge | `station`, `mode`, `sub_channel` | 1 per station and mode, with the sub-channel it was last heard on |
| `pdn_station_frames_total` | counter | `station`, `mode` | frames whose check sequence verified |
| `pdn_station_bytes_total` | counter | `station`, `mode` | bytes in those frames |
| `pdn_station_snr_db_sum` | counter | `station`, `mode` | sum of per-frame SNR |
| `pdn_station_frames_with_snr_total` | counter | `station`, `mode` | frames with an SNR; the divisor for the sum |
| `pdn_station_frequency_offset_above_hz_sum` | counter | `station`, `mode` | sum of offsets above centre, counting only those frames |
| `pdn_station_frequency_offset_below_hz_sum` | counter | `station`, `mode` | sum of offsets below centre, as a positive number |
| `pdn_station_frames_with_offset_total` | counter | `station`, `mode` | frames with a measured offset; the divisor for both sums |
| `pdn_station_corrected_bytes_total` | counter | `station`, `mode` | bytes the FEC repaired |
| `pdn_station_snr_db_last` | gauge | `station`, `mode` | SNR of the most recent frame; holds between transmissions |
| `pdn_station_frequency_offset_hz_last` | gauge | `station`, `mode` | offset of the most recent frame, positive above centre |
| `pdn_frames_uncounted_total` | counter | none | decodes attributed to no station |
| `pdn_stations` | gauge | none | stations currently held |

A station leaves `/metrics` after `stationIdleHours` (default 6) without a frame. When `maxStations` (default 256) is reached, the least recently heard station is dropped to make room. Frames leave `/metrics/frames` after `frameWindowSeconds` (default 300); a window is served, so reading it consumes nothing.

Each line-protocol point is measurement `pdn_frame` with tags `station`, `mode` and `sub_channel`, fields `bytes` (integer), `ssid` (string, when the frame had one), `snr_db`, `offset_hz` and `corrected_bytes` (integer), each present only when measured, and a nanosecond timestamp of when the frame was heard.

```
pdn_frame,station=GB7BPQ,mode=afsk300-il2pc,sub_channel=0 bytes=61i,ssid="1",snr_db=15.2,offset_hz=-4 1757500000000000000
```

A Grafana dashboard that reads both feeds is at [grafana/pdn-soundmodem.json](grafana/pdn-soundmodem.json).

## Uplink and monitor

### /uplink

A station with a `publish` section dials out to `publish.url`, a `ws` or `wss` URL ending in `/uplink` on a monitor site, and holds one WebSocket open. The monitor accepts it only from a station listed in its `monitor.uplinks`, matched on the token.

| Item | Rule |
|---|---|
| Authentication | `Authorization: Bearer <token>` on the upgrade. [`--uplink-token`](command-line.md#one-shot-flags) mints one; the site stores the hash as `monitor.uplinks[].tokenSha256` and the station keeps the token as `publish.token` |
| 404 | the site has no `monitor.uplinks`; the path does not exist |
| 400 | not a WebSocket upgrade |
| 401 | no bearer token; or a token not in the table, after a fixed delay and a counted journal line |
| 429 | this token is refused for a while after breaking the protocol, or already has a socket waiting to say hello |
| Up, station to site | `hello` once and first (protocol version, daemon version, callsign, operator, location, radio, site, audio rate, block length, dial, sideband, frames policy, modems); binary audio blocks while somebody is watching; `frame` for every decoded frame, or only while watched with `publish.frames` `"watched"`; `radio` when the status sentence changes; `bye` before a planned close |
| Down, site to station | `welcome` once (slug, path, page URL); `demand` with the viewer count, on every change and at least every 20 s; `refused` in place of a `welcome`, with the reason, followed by a close |
| Refused after the hello | a protocol version other than 1; a callsign that is not the one the token was issued to; an audio rate outside 6000 to 48000 Hz or one the site cannot draw a whole number of lines from; a block length outside its cap; an over-long string; a band the site cannot place |
| Keepalive | the station pings every 20 s and drops a socket with no application message for 45 s; the site's `demand` is the heartbeat, and the site closes a socket that has not said hello within 10 s |

Nothing sent down this socket can transmit, retune or reconfigure the station; a message of any other type is counted and dropped. The wire format is in [docs/dev/uplink-wire-format.md](../dev/uplink-wire-format.md). A second connection on the same token supersedes the first once its hello is accepted, and the first is told why.

The station journals `publish: live at https://<site>/r/<slug>/` when the `welcome` carries a URL, which it does when the site has set `monitor.publicUrl`, and `publish: live as <slug>` otherwise.

### The monitor site's routes

A `monitor` section turns the process into a site on `waterfall.port`, which must be stated. It opens no KISS, ARDOP or paging port and serves none of the station's `/api/` endpoints, and no `/metrics` or `/survey/` route; `waterfall.public` is forced true.

| Route | Method | Serves |
|---|---|---|
| `/`, `/index.html` | `GET`, `HEAD` | the picker |
| `/api/instances` | `GET`, `HEAD` | JSON: `page`, `title`, `about`, `listFrom`, `staleSince`, `problem`, and `receivers`, one row per listed receiver plus every relayed station |
| `/robots.txt` | `GET`, `HEAD` | `User-agent: *`, `Disallow: /r/`, `Disallow: /api/` |
| `/r/<slug>` | `GET`, `HEAD` | a redirect to `/r/<slug>/` with the query string kept |
| `/r/<slug>/`, `/r/<slug>/links`, `/r/<slug>/ws` | as the station page | that receiver's station page, links pane and WebSocket, built on the first request |
| `/uplink` | WebSocket | above |
| anything else | | 404 |

A slug is the `monitor.uplinks[].slug` of a relayed station, or the directory's slug for a listed web receiver. A slug the site does not offer is a 404.

Related: [configuration reference](config.md), [command-line reference](command-line.md), [files and directories](files.md).
