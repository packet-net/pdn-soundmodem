# Troubleshooting

How to find out what a station is doing, and what to do about the things that most often go wrong.

## Read the journal first

The modem says what it is doing on its own output, and systemd keeps it. Follow a running station with:

```sh
journalctl -u pdn-soundmodem -f
```

Use `journalctl -u pdn-soundmodem -n 50 --no-pager` for the last fifty lines instead, and `systemctl status pdn-soundmodem` to see whether it is running at all, with the last few lines under it.

If you run more than one modem on the machine, each instance has its own unit name. The station configured in `/etc/pdn-soundmodem/NAME.json` is `pdn-soundmodem@NAME`, so its journal is `journalctl -u pdn-soundmodem@NAME -f`. See [files and directories](reference/files.md#more-than-one-modem).

The first line is always the version, written before the config file is even read:

```
pdn-soundmodem 0.69.0, commit 1cd85706f54a5bf84546dd2fbb04467992cdfcab
```

`journalctl -u pdn-soundmodem | head` therefore answers "which version is this" for a station that has been running for a month. A build that was not made from a release tag says `0.0.0-dev (dev build, not a numbered release)` in the same place.

## What start-up prints

A healthy start-up is a handful of lines, each prefixed by the thing that wrote it:

```
pdn-soundmodem 0.69.0, commit 1cd85706f54a5bf84546dd2fbb04467992cdfcab
config: /etc/pdn-soundmodem/soundmodem.json
modem 0: afsk1200
frequency matching: on - answering a station on its own frequency after 3 frames, if they agree within 20 Hz, ...
waterfall: http://127.0.0.1:8107/
kiss tcp: 127.0.0.1:8105 (all modems, by sub-channel nibble)
audio: plughw:CARD=Device,DEV=0 capture 48000 Hz -> 12000 Hz
audio: capture buffer 500 ms, period 30 ms
```

The prefixes you will see are `config:`, `modem N:`, `band plan:`, `frequency matching:`, `alsa:`, `audio:`, `ptt:`, `kiss:`, `waterfall:`, `tx test:`, `ardop:`, `flex:`, `publish:` and `metrics:`. A line with `WARNING` in it means start-up carried on regardless, so read it and decide.

`config: WARNING - "waterfal" is not a setting this version knows, and is being IGNORED` is the one to watch for after editing the file. A mistyped key is ignored, the station starts, and the setting you meant does nothing. Check the spelling in the [configuration reference](reference/config.md).

## Frame lines

Every frame the station hears or sends is one line:

```
rx[0] afsk1200 M0LTE>APRS 55 bytes  snr 72.9 dB
rx[0] afsk300-il2pc M0LTE>GB7IOW-1 15 bytes  crc ok  fec 0  -5 Hz
tx[0] afsk1200 M0LTE>GB7RDG-2 28 bytes
tx[0] DROPPED M0LTE>GB7RDG-2 28 bytes: this station receives only
```

| Field | What it says |
|---|---|
| `rx[N]` / `tx[N]` | Direction and the KISS sub-channel the modem is on. |
| mode | The mode it was heard on, as the catalogue names it. |
| `SOURCE>DEST` | The AX.25 addresses. `(no ax25 header)` where the payload is not AX.25, and `?` where the destination field was blank. |
| `crc ok` / `CRC BAD` | Only on modes that carry a CRC. A mode with none claims neither. |
| `plain il2p (rs only)` | The frame came in as IL2P with no CRC, so Reed-Solomon is all that stands behind it. It adds `not passed to host` where your node never saw it. |
| `fec N` | Bytes the forward error correction had to repair. A rising count means the link is being carried by the FEC. |
| `snr N.N dB` | The burst measured against the band's noise floor. |
| `-5 Hz` | The measured carrier offset, which is what to retune by. |
| `emph +2 dB` | Diversity banks only, and only when it is not zero: the other station's transmit audio is tilted. |
| `shifted +3.5 Hz to suit them` | On a `tx` line: frequency matching answered a station on the frequency it was heard on. |

A transmission is logged when it goes out, never when it is queued, and a frame can wait behind the channel for seconds. A frame that never went out appears once as `DROPPED` with the reason and never as `tx`.

ARDOP is not AX.25 and says what it can:

```
rx[2] ardop IDFrame GB7NOT-2>? 0 bytes  crc ok  q 78
tx[2] ardop ConReq500M M0LTE>GB7RDG 0 bytes
```

The frame type stands in for the mode name. The callsigns are the ones the frame states, with `?` for a half that frame type does not carry and `(no callsign)` where it names neither. `q N` is ARDOP's own 0 to 100 quality on every frame it decodes, and `sn +N.N dB` appears only on a Ping or a PingAck.

Your station's own Morse idents and pages are listed too:

```
id[3] M0LTE in CW
page[1] to 1234567 sent (pocsag1200)
page[1] to 1234567 DROPPED: ...
```

## Host lines

Every attach and every loss is logged:

```
kiss[8105] 192.168.1.50:54312 connected - 2 clients (all modems)
kiss[8110] 127.0.0.1:40000 disconnected - 0 clients (modem 1 only)
kiss[8105] 192.168.1.50:54312 disconnected: Connection reset by peer. - 1 client (all modems)
```

Each line names the port, the host, how many sessions are left on that port, and which modems that port reaches. A clean close carries no reason; a host that vanished carries the transport's. More in [ports and endpoints](reference/ports-and-endpoints.md#journal).

## The service will not start

Two different failures, told apart by the exit code. Exit 2 is a configuration the modem refused, and systemd leaves the service stopped. Exit 1 is hardware the file names but the machine does not have, and systemd keeps retrying every five seconds. The full list is in the [command-line reference](reference/command-line.md#exit-codes).

### Exit 2: the configuration was refused

A refusal in the file's contents gets this frame, which names the file, says what is wrong and says what to do. A few, such as an unknown mode, are one line on their own.

```
configuration error in /etc/pdn-soundmodem/soundmodem.json
  two modems share "subChannel": 0. Each modem needs its own KISS sub-channel (0-15) - renumber one of them.

  The service will not start until this is fixed. As root, to start
  from a known-good file:
    cp /usr/share/pdn-soundmodem/soundmodem.example.json /etc/pdn-soundmodem/soundmodem.json
```

The unit sets `RestartPreventExitStatus=2`, so the journal holds one readable explanation rather than a copy every five seconds. Fix the file and `systemctl restart pdn-soundmodem`.

Grouped by what causes them:

| Cause | What the message looks like | What to do |
|---|---|---|
| The file itself | `no such file`, `no such directory`, `permission denied reading the file`, `the file is empty`, ``the file contains only `null` `` | Copy the example over it and edit that. |
| A typo in the JSON | `not valid JSON - line 7, position 3: ',' is an invalid start of a value.` | The line and position are counted from 1, as your editor does. Trailing commas and `//` comments are allowed, so it is usually a missing brace or quote. |
| Two modems on one sub-channel | `two modems share "subChannel": 0` | Renumber one. See [`modems`](reference/config.md#modems). |
| A mode name that does not exist | `unknown mode 'fsk9600il2p'` with a `did you mean:` list | A hyphen is easy to lose among 38 names. See [Modes](05-modes.md). |
| A mode option that mode does not have | `mode 'X' ... has no centre frequency to move`, `mode 'X' does not run IL2P+CRC` | Drop the key. See [`modems`](reference/config.md#modems). |
| Band placement | `"sideband": "am" is not a kind of radio this knows`, `on FM every modem is on the one channel the radio is set to`, `band plan: no dial frequency places every modem inside the 300-2700 Hz passband` | See [band placement](reference/config.md#band-placement-sideband-dialfrequency-and-rffrequency) and [HF](08-hf.md). |
| Keying a radio that keys itself | `--device flex: keys the radio itself; remove the conflicting --ptt`, `--device ubersdr: is a receive-only station` | Delete the [`ptt`](reference/config.md#ptt) section. |
| A keying method that does not exist | `unknown ptt type 'X'` | `serial` or `cm108`, or no section at all for VOX. |
| A mixer on something that is not a sound card | `... which is not a sound card ... Remove the "alsa" section` | See [`alsa`](reference/config.md#alsa). |
| A capture rate the modems cannot use | `--capture-rate must be a multiple of 12000` | 48000 suits everything. |
| Two services on one TCP port | `the waterfall and "kissPort" both want TCP port 8105. Give them different ports.` | See [every listener](reference/ports-and-endpoints.md#every-listener). |
| A section that needs the station page | `"api" is served on the waterfall's HTTP listener, and this station has no "waterfall" section`; `"publish" needs a "waterfall" section`; `"monitor" needs a "waterfall" section` | Add a [`waterfall`](reference/config.md#waterfall) section, or remove the other one. |
| Both a station and a monitor site | `this file sets both "device" ... and "monitor"`, `this file sets both "publish" and "monitor"` | One process is one of them. See [the public monitor](10-public-monitor.md). |
| An uplink credential | `which is not an absolute ws or wss URL`, `it is the credential the site issued this station` | See [the public monitor](10-public-monitor.md). |

An unknown mode looks like this, and the suggestion is usually right:

```
modem 0: unknown mode 'fsk9600il2p'
  did you mean: fsk9600-il2p, fsk4800-il2p
  the 38 built-in mode names are listed at https://github.com/packet-net/pdn-soundmodem/blob/main/docs/05-modes.md
```

### A port something else already holds

If the page's port is taken, the message names it and the station exits 2:

```
cannot serve the waterfall on 127.0.0.1:8107
  Address already in use
  Set by "waterfall"."port" and the top-level "bind". Another process may
  already hold the port; "*" or "0.0.0.0" serves every interface.
```

A KISS port that is taken is less tidy: the station aborts with `Unhandled exception. System.Net.Sockets.SocketException (98): Address already in use` and a stack trace, and systemd retries it every five seconds. `ss -ltnp | grep 8105` finds the holder. The usual holder is another instance of the modem.

### Exit 1: hardware that is not there

A file that is structurally fine but names a sound card, a PTT line or a radio that will not open gets a message rather than a stack trace, and the service keeps trying:

```
cannot open the cm108 PTT device "/dev/hidraw0"
  Could not find file '/dev/hidraw0'.

  Set by "ptt" in /etc/pdn-soundmodem/soundmodem.json
  This selects how the radio is keyed; omit it entirely for VOX, or for a
  FlexRadio, which keys itself.
  List what this machine actually has:
    ls -l /dev/hidraw*
```

Retrying is what you want at boot, because a USB interface can still be enumerating when the service starts. It is also what fills the journal while you work on it, so `systemctl stop pdn-soundmodem` until the config is right. The sound-card version of the message tells you to run `aplay -l ; arecord -l ; aplay -L`, and the FlexRadio version tells you to check the radio has finished booting.

Card numbers move when devices are replugged, so prefer a stable name such as `plughw:CARD=Device,DEV=0` over `plughw:1,0`. See [radios and interfaces](03-radios-and-interfaces.md).

## Permission problems

The service runs as the unprivileged `pdn-soundmodem` user, which the unit puts in the `audio` group for `/dev/snd/*` and `dialout` for serial PTT on `/dev/ttyUSB*`. Serial keying needs nothing further.

CM108 keying needs one more step, because `/dev/hidraw*` is root-only by default. `Permission denied` on a hidraw node almost always means the udev rule is missing; `Could not find file` means the wrong node. The rule is in [02-first-station.md](02-first-station.md). Add it, replug the interface, then `systemctl restart pdn-soundmodem`.

Which `hidraw` number is yours today is found the way [03-radios-and-interfaces.md](03-radios-and-interfaces.md#the-gpio-pin-on-a-cm108-interface) shows. The rest of the permission picture is in [files and directories](reference/files.md#ownership-and-permissions).

Running the modem by hand from a terminal uses your own account, so your user needs the same group memberships.

## Nothing decodes

Work down this list. Most stations that hear nothing fail on one of the first three.

1. Is audio arriving at all? Open the [station page](07-station-page.md) and watch the waterfall. A flat, dead spectrum means the modem is not getting the card's audio, so check the `audio:` start-up line names the device you meant.
2. Is the level sane? A frame that arrives too hot is badged `TOO LOUD` on the frames panel, and one that arrives too quiet is badged `TOO QUIET` on the two C4FSK modes and the 1200 baud AFSK family, whose demodulators read the level. Clipping is the failure that costs decodes; being quiet costs far less than people expect. Set the gain from [Levels](04-levels.md); the measurements behind the thresholds are in [receive levels](dev/receive-levels.md).
3. Is the radio's squelch open? A closed squelch passes no audio between bursts and chops the start of each frame as it opens, which is the part the demodulator needs to find the signal. Open it, or take audio from a data output that is not squelched. The input level meter then reads hiss between frames, which is expected: each frame's own level is measured over the frame.
4. Is it the mode you configured? A station you cannot copy may not be running what you assumed. Record some audio and sweep every mode over it with `pdn-decode`, in [Decode a recording](13-decode-a-recording.md).
5. Is the sideband right? On HF, USB where the other station is on LSB decodes nothing at all. Check the `dial:` and `modem N ... at ...` lines from the band plan against what the rig is set to. See [HF](08-hf.md).
6. Are you off frequency? If frames decode but the line ends in something like `-180 Hz`, retune by that much. The same figure is on each burst's tag on the waterfall.
7. Are frames arriving but your software sees nothing? That is a sub-channel problem, not a decode problem. A host on the shared KISS port only sees frames whose nibble matches, and a host on a modem's own port sees that modem as nibble 0. See [Connect your software](06-connect-your-software.md) and [addressing](reference/ports-and-endpoints.md#addressing).
8. If nothing above helped, prove the chain off the air by replaying a demo recording through the whole daemon with `--wav-loop`, as [Your first station](02-first-station.md) describes. If that decodes, the modem is fine and the problem is between the antenna and the card.

## Lost audio and CPU starvation

ALSA recovers from an overrun by restarting the stream, silently, and every one is a hole where audio should have been. The modem counts them and says so:

```
audio: 3 capture overruns, 1 playback underrun (4 since start)
```

The first such line carries a sentence of advice after the counts; later ones are the counts alone, at most every ten seconds. Capture overruns drop frames that were on the air. Playback underruns put a discontinuity in what you transmitted.

How much slack the station has comes from the card, and start-up says so: `audio: capture buffer 500 ms, period 30 ms` on the bench interface. Another card rounds to whatever it can do, and a card that refused the request says which error it refused with on the next line.

Under LXC or Docker, a `Nice=` value inside the container does not rank the process against other containers. What ranks it is the container's CPU weight, which you set from the host (for LXC, `pct set ID --cpuunits N`; for Docker, `--cpu-shares`). Dedicated cores are the other answer.

## The station restarts itself

Two watches cover the two ways an input can die. Either one stops the process with exit 1, so systemd reopens the device:

```
receive feed dead: 30 s of unbroken digital silence from the radio - restarting to rebuild the session (recurring? check DAX/slice config - a deliberately muted DAX stream restart-loops this way)
receive feed starved: the sound device returned no samples for 30 s - a stalled or unplugged card - restarting to reopen it
```

One restart is the watch working. A restart loop means the input is silent or absent for a reason the watch cannot know about: a muted DAX channel, a virtual card whose other half is not running yet, a feed you mute on purpose. Set that watch to 0 in [`deadFeed`](reference/config.md#deadfeed) if the silence is intended.

A sound card gets the starvation watch only, because a wired input can be silent for real reasons. A FlexRadio and a web receiver get both.

## Where to ask

Open an issue at [github.com/packet-net/pdn-soundmodem/issues](https://github.com/packet-net/pdn-soundmodem/issues). Include the version line from the top of the journal, thirty lines of journal around the problem, your config file with any `publish.token` or `api.key` removed, and what radio and interface you are using. If it is a decode problem, a short WAV recording of the signal is worth more than any description of it.

## Related

- [Your first station](02-first-station.md) for the working baseline to compare against
- [Levels](04-levels.md) for gain, badges and the test tones
- [The station page](07-station-page.md) for the waterfall, the frames panel and the badges
- [Logging and metrics](11-logging-and-metrics.md) for the frame log, survey captures and metrics
- [Configuration reference](reference/config.md), [command-line reference](reference/command-line.md), [ports and endpoints](reference/ports-and-endpoints.md), [files and directories](reference/files.md)
