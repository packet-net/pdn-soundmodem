# Web receivers

You will finish with a station that decodes packet off somebody else's antenna, on a machine with no radio, no sound card and no PTT lead.

pdn-soundmodem can take its audio from a public UberSDR web receiver instead of a sound card. UberSDR is receiver software that streams a live radio to a browser, with many public instances. The modem asks one of them for raw IQ, the untouched signal before any demodulation, and demodulates it here. Everything after that is the same as a radio station: the same modes, the same station page, the same frame log, the same KISS ports for your node or APRS software.

The one difference is that it receives and never transmits.

## Before you start

- pdn-soundmodem installed, with the config file at `/etc/pdn-soundmodem/soundmodem.json`. See [01-install.md](01-install.md).
- The address of a public UberSDR instance, such as `m9psy-1.instance.ubersdr.org`.
- The frequencies you want to listen on, in Hz. HF packet lives around 7.050 MHz in the UK; [08-hf.md](08-hf.md) covers band plans.
- Outbound HTTPS from the machine. No sound card, no interface and no PTT line.

## Point the modem at a receiver

Set [`device`](reference/config.md#device) to `ubersdr:` followed by the instance.

```json
"device": "ubersdr:m9psy-1.instance.ubersdr.org"
```

Write the instance however you have it. A bare host, a `host:port`, or the whole URL out of the browser's address bar (`ubersdr:https://m9psy-1.instance.ubersdr.org/`) all name the same receiver. HTTPS on port 443 is assumed unless the string says otherwise.

A radio has a dial you can read off. A web receiver has none, so the modem tunes it from your band plan. Give every modem an [`rfFrequency`](reference/config.md#band-placement-sideband-dialfrequency-and-rffrequency) in Hz, or set a top-level `dialFrequency` to pin the dial yourself. With neither, start-up stops with this:

```
the UberSDR instance at m9psy-1.instance.ubersdr.org has to be told where to listen. Give every modem an "rfFrequency" and the dial is worked out from them, or set "dialFrequency" to pin it - unlike a radio there is no dial already set to read off.
```

A web receiver is an SSB receiver, so `"sideband": "fm"` is refused. Point `device` at a sound card fed by an FM radio instead.

## Start it and read the journal

```
sudo systemctl restart pdn-soundmodem
journalctl -u pdn-soundmodem -f
```

Among the start-up lines you should see the dial the modem chose, the audio line, and the receiver naming itself:

```
dial: 7.048800 MHz USB
  modem 0 afsk300-il2pc at 7.050300 MHz = 1500 Hz audio
kiss: this station receives only - frames arriving on these ports are refused, not transmitted. Everything the modems hear is still delivered.
audio: m9psy-1.instance.ubersdr.org iq48 IQ at 7.048800 MHz -> USB 150-3450 Hz audio at 12000 Hz (RECEIVE ONLY)
ubersdr: M9PSY-1, RX888 with 40m Full Wave Loop (GPSDO), Dalgety Bay, Scotland, UK, reference offset 0 Hz
ubersdr: session limit 10800 s - the stream is picked up again each time the receiver ends one
```

The KISS ports are still served and everything the modems hear still reaches your node or APRS software. Frames sent the other way come back as `tx[N] DROPPED` with the reason. The transmitter test on the station page is unavailable and says so.

Four things are refused on this device. [`ptt`](reference/config.md#ptt) and a modem's [`identify`](reference/config.md#modemsidentify) need a transmitter. [`alsa.mixer`](reference/config.md#alsa) needs a sound card. [`publish`](reference/config.md#publish) is refused because a public receiver is on the monitor site in its own right. An `ardop` modem loads and hears the channel, with a start-up warning that no ARQ session can complete.

## Shape what you hear

The [`ubersdr`](reference/config.md#ubersdr) section sets the stream's parameters. All of it is optional.

```json
"ubersdr": { "mode": "iq48", "gain": 1.0 }
```

`mode` is the receiver's IQ mode. `iq48` is 48 kHz of complex baseband and is what every public instance offers; use `iq96` only where an instance allows it. `password` is for a protected instance.

`ssbLowHz` and `ssbHighHz` are the receive filter, in Hz above the dial. The defaults of 150 and 3450 clear the whole 300 to 2700 Hz band a plan can place modems in, with room either side. Narrow them to emulate a tighter rig.

`gain` scales the demodulated audio. The decoders hardly care about level, so it is there for the waterfall: raise it above 1.0 if a quiet instance makes the picture hard to read. `startupGuardMs` discards the first second after each connect, which is the level ramp at the head of an instance's stream.

## Connect only while somebody is watching

A station feeding a node wants the receiver all day. A page for visitors has nothing to show while nobody is looking, and the receiver's listener slot is somebody else's to lend. Set `onDemand`:

```json
"ubersdr": { "onDemand": true, "lingerSeconds": 60 }
```

The first browser to open the station page opens the session, every further browser shares it, and the last one to leave starts the `lingerSeconds` clock. A page refresh or a tab switch then costs the receiver nothing. `0` closes the session at once.

`onDemand` needs a [`waterfall`](reference/config.md#waterfall) section, since the page's viewers are what asks for the receiver. The audio line says what it is doing:

```
audio: m9psy-1.instance.ubersdr.org iq48 IQ at 7.048800 MHz -> USB 150-3450 Hz audio at 12000 Hz (RECEIVE ONLY, on demand: connected while the waterfall has a viewer, held 60 s after the last leaves)
```

Every later `ubersdr:` line carries the viewer count, so `ubersdr: live, 2 viewers: ...` and `ubersdr: lingering, 0 viewers: ...` tell you who a reconnect is for.

## Put the page in front of visitors

Set `public` on the [`waterfall`](reference/config.md#waterfall) section to dress the station page for people who did not build it.

```json
"waterfall": {
  "port": 8099,
  "public": true,
  "title": "40 m packet monitor",
  "about": "The 7050-7052 kHz packet window, receive only."
}
```

`title` goes in the tab and the top bar. `about` is one paragraph under it. An on-demand station also credits the web receiver it listens through and links to it.

A public page hides the KISS host badges, the dial and display controls, the stats and the links pane's *Mine* filter, and it never carries the mixer or the transmitter test. The waterfall, the links pane, the decoded frames and *Listen* all stay. [07-station-page.md](07-station-page.md) goes through every control.

Serving the page beyond the machine needs a top-level [`bind`](reference/config.md#kissport-and-bind) of `"*"`. There is no `bind` inside `waterfall`. Anything that can reach the port can watch the station, and watching is all it can do.

## Keep the frames

A receive-only station is a listening post, so turn on the [`frameLog`](reference/config.md#framelog) and let it accumulate.

```json
"frameLog": { "path": "/var/lib/pdn-soundmodem/frames.db" }
```

Every frame heard is written to SQLite with its placement and decode quality, and the page's frames and links panels open on what is already there. [11-logging-and-metrics.md](11-logging-and-metrics.md) has the queries and the metrics endpoints.

## A complete config

A 40 m listening post on three modes, on demand, with a public page:

```json
{
  "device": "ubersdr:m9psy-1.instance.ubersdr.org",
  "bind": "*",
  "kissPort": 8105,
  "modems": [
    { "subChannel": 0, "mode": "afsk300-il2pc", "rfFrequency": 7050300, "port": 8101 },
    { "subChannel": 1, "mode": "bpsk300",       "rfFrequency": 7051600, "port": 8102 },
    { "subChannel": 2, "mode": "qpsk600",       "rfFrequency": 7052200, "port": 8103 }
  ],
  "ubersdr": { "onDemand": true, "lingerSeconds": 60 },
  "waterfall": {
    "port": 8099,
    "public": true,
    "title": "40 m packet monitor",
    "about": "The 7050-7052 kHz packet window, receive only."
  },
  "frameLog": { "path": "/var/lib/pdn-soundmodem/frames.db" }
}
```

## Check it worked

Open `http://<machine>:8099/` and look at the top of the page. It should name the receiver and link to it, and the waterfall should be moving within a few seconds of the page opening.

```
journalctl -u pdn-soundmodem -o cat | grep '^ubersdr:'
```

A working station shows the receiver description and the session limit. A decode looks the same as it would off a radio:

```
rx[0] afsk300-il2pc M0LTE>GB7IOW-1 15 bytes  crc ok  fec 0  -5 Hz
```

## If it did not

`cannot stream IQ from "ubersdr:..."` with `cannot reach the UberSDR instance` means the address is wrong or the instance is down. The message includes a `curl` line to check it with. The service keeps retrying.

`refusing this address for now` at start-up, or `is refusing us for now` on a later try, means the daily listening allowance for your address is spent, or the instance is rate-limiting you. Nothing in the config fixes that. The station stays up and starts hearing audio when the receiver lets it back in.

`has been unreachable for 5 minutes` stops an always-on station with exit 1, and systemd restarts it. With `onDemand` a receiver that goes away keeps the station running. The page stays up, says what is wrong, and keeps trying while anybody is waiting.

The waterfall moves but nothing decodes. Check the modem is on the right `rfFrequency` and the right mode for what is on air; see [05-modes.md](05-modes.md) and the checklist in [12-troubleshooting.md](12-troubleshooting.md).

## How it works

The modem takes IQ rather than the instance's demodulated audio, so the receive filter is set here. `captureRate` does not apply; the stream brings its own clock.

Public instances cap a session, three hours on the ones measured, and report the cap at start-up. A closed stream is ordinary, and the modem picks it up again, losing about a second each time to the start-of-stream ramp.

These are somebody else's receivers. One long session is kinder to the receiver than repeated reconnects. If you will leave a station running for weeks, tell the instance's operator.

## Related

- [10-public-monitor.md](10-public-monitor.md) puts many web receivers behind one site with a picker, which is the [`monitor`](reference/config.md#monitor) section rather than this one.
- [08-hf.md](08-hf.md) for band plans, sidebands and several modes sharing one passband.
- [reference/config.md](reference/config.md#ubersdr) for every `ubersdr` and `waterfall` key with its default.
- [reference/ports-and-endpoints.md](reference/ports-and-endpoints.md#http-on-the-page-port) for what the page port serves.
- [13-decode-a-recording.md](13-decode-a-recording.md) if you would rather work from a recording than a live receiver.
