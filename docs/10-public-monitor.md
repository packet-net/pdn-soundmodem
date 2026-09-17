# The public monitor

A monitor site is one web page fronting many receivers, so somebody with no antenna can watch a band. When you finish, your station will be on a monitor site, or you will be running a site of your own. The live one is monitor.ukpacketradio.network.

## Before you start

- A station that is decoding frames, as in [02-first-station.md](02-first-station.md).
- A [`waterfall`](reference/config.md#waterfall) section in the config file. The uplink publishes what the station page computes, so without one there is nothing to publish.
- A token from the site's operator. There is no sign-up page; ask the operator, whose contact details are usually in the site's about paragraph.
- Upstream bandwidth. About 194 kbit/s while somebody is watching your page, and under 1 kbit/s while nobody is.

A station on a public web receiver cannot publish; [09-web-receivers.md](09-web-receivers.md) has the rest of what that device refuses.

## Add the publish block

Put a [`publish`](reference/config.md#publish) block in `/etc/pdn-soundmodem/soundmodem.json`, beside the rest of your station.

```json
"publish": {
  "url": "wss://monitor.ukpacketradio.network/uplink",
  "token": "pdnsm_...",
  "callsign": "GB7RDG-2",
  "operator": "Tom M0LTE",
  "location": "Reading, England",
  "radio": "IC-7300 into a doublet at 10 m"
}
```

The `url` and the `token` come from the operator. Paste the token in as given. The site checks `callsign` against the token it issued that station. The `operator`, `location` and `radio` lines appear on your row in the picker and on your page, and `site` adds a link to a page of your own.

`audioRate` defaults to the modem's DSP rate capped at 12000 Hz, and the relayed waterfall spans 0 to half of it. On a slow upload set `"audioRate": 6000`, which costs 98 kbit/s and gives a 0 to 3 kHz picture; a modem above 3 kHz then will not appear on the site, and start-up says which.

`frames` defaults to `"always"`, which sends decoded frames whether or not anybody is watching. Frames are under a kilobit a second. `"watched"` holds them until somebody is there.

## Restart and read the journal

```
sudo systemctl restart pdn-soundmodem
journalctl -u pdn-soundmodem -f
```

Two lines say it worked.

```
publish: publishing to monitor.ukpacketradio.network as GB7RDG-2
publish: live at https://monitor.ukpacketradio.network/r/gb7rdg-2/
```

Open that address and you should see your own waterfall, links pane and decoded frames. The second line reads `publish: live as gb7rdg-2` instead when the site has not written its own address down. Your page is still there; the site did not say where.

## Watch the viewer count

The socket sits idle until somebody opens your page.

```
publish: 1 watching, sending audio
publish: nobody watching, audio stopped
```

Audio stops sixty seconds after the last viewer leaves by default, so a page refresh does not stop and restart the stream. Your receiver and your modems run all the time either way.

## What goes up, and what can come back

Your station sends the audio its own modems are reading, every frame they decode, and a one-line status sentence about the radio. The site sends back the number of people watching, and nothing else.

Nothing on this connection can transmit, retune or reconfigure your station. The wire format is written up in [dev/uplink-wire-format.md](dev/uplink-wire-format.md).

Every frame you decode is listed on your page on the site and written into a log the site keeps, and that log outlives your station going off air. Decide whether you want that before opting in. The station's own config API reads `publish.token` back as `(set, not shown)`, as it does the API key.

A site that is down, a token that is wrong or a network that has gone each writes a journal line and retries, and none of them stops your station.

## Leave

Delete the `publish` block and restart. The socket goes and your row leaves the picker within seconds, but `/r/<slug>/` still serves your page, marked `not connected just now`, until the site restarts. Ask the operator if you want the page itself gone.

## Run a monitor site

A [`monitor`](reference/config.md#monitor) section turns pdn-soundmodem into a site. It is exclusive with `device`: one process is a site or a station and never both. A site serves no KISS, no PTT, no API, no survey, no paging and no ARDOP, and `waterfall.public` is forced true.

```json
{
  "bind": "*",
  "waterfall": {
    "port": 8099,
    "title": "UK packet monitor",
    "about": "The 40 m packet window, as heard by public web receivers. Receive only."
  },
  "monitor": {
    "publicUrl": "https://monitor.example.org",
    "modems": [ { "subChannel": 0, "mode": "afsk300-il2pc", "rfFrequency": 7050300 } ]
  },
  "frameLog": { "path": "/var/lib/pdn-soundmodem" }
}
```

`waterfall.port` has to be written down, because the site serves all of itself on that one port. `monitor.modems` is required and every entry needs an `rfFrequency`; these are the modems every receiver on the site runs. [`frameLog.path`](reference/config.md#framelog) is a directory here rather than a file, holding one `frames-<slug>.db` per receiver. Four lines among the start-up ones say what this process is.

```
monitor: many receivers behind one page, receive only
directory: 3 of 51 receivers listed from https://instances.ubersdr.org/api/instances
monitor: http://127.0.0.1:8099/, published as https://monitor.example.org/
monitor: receive only - no KISS, no transmitter, no configuration API on this port
```

### Choose which receivers appear

Receivers come from the UberSDR public directory at `directory`, fetched again every `refreshMinutes`. One is offered when it is online, offers the IQ mode the [`ubersdr`](reference/config.md#ubersdr) section asks for, has an antenna connected, and covers the RF window your modems occupy.

`allow`, when it is non-empty, is the only list of hosts offered. `deny` is hosts never offered, and `deny` beats `allow`. When the operator of a receiver would rather not be listed, put their host in `deny` and they are gone from the picker within `refreshMinutes`. Two hosts in `allow` is also how you run a smoke test against a site fronting fifty.

Nothing exists for a receiver nobody has picked. The first request for `/r/<slug>/` builds that receiver's modems, frame log and page, and they are kept for the life of the process. Its session on the receiver runs only while somebody is watching and for sixty seconds after.

### Set publicUrl

`publicUrl` is the address the world reaches the site on: scheme, host and optional port, nothing after. Left empty, each request's `Host` header is used instead. Set it on any site behind a tunnel or proxy, so a publishing station is told the public address. Without it a publishing station's journal says `publish: live as gb7rdg-2` and never names a URL.

### Accept private stations

A private station is somebody's transceiver rather than a web receiver. It dials in over an uplink and appears in the picker tagged `station`. Mint it a token on the monitor machine with [`--uplink-token`](reference/command-line.md#one-shot-flags), naming the station it is for.

```
pdn-soundmodem --uplink-token GB7RDG-2
```

```
Give this to GB7RDG-2's operator, once, for their "publish" block:

  "token": "pdnsm_P3rkyAB2V_VHZ13Mk2D8dEZG4kC5H-idxTUNsqISXCE"

Keep this in the monitor's own config, under "monitor"."uplinks":

  {
    "callsign": "GB7RDG-2",
    "slug": "gb7rdg-2",
    "tokenSha256": "827a7af8ff660f65e13b0749be00ba09cc109198c9be132fc9872abe6ccc71d5"
  }
```

The output says a little more; those two blocks are the parts you need. Paste the entry into `monitor.uplinks`, restart, and give the token to that operator once. Nothing is written to a file, the site keeps only the hash, and the token is never shown again.

The callsign is bound to the token, so a station claiming to be somebody else is refused with the reason. The slug never comes off the wire, so the address a visitor bookmarks is yours to choose, and a configured slug beats a web receiver whose hostname would give the same one. An empty `uplinks` list makes `/uplink` a 404 like any other path the site does not serve. Adding or removing a station needs a restart.

```
monitor: accepting uplinks at /uplink from 1 station (GB7RDG-2 -> /r/gb7rdg-2/)
```

### What the site serves

| Path | What |
|---|---|
| `/` | The picker: one row per receiver and per relayed station, with a title, an about paragraph and a stale marker when the directory is unreachable |
| `/api/instances` | The JSON the picker polls |
| `/r/<slug>/` | One receiver's or station's page, with its waterfall, links pane and frames |
| `/uplink` | Where a private station connects, with the token this site issued it |
| `/robots.txt` | Asks crawlers to leave `/r/` and `/api/` alone |

Anything else is a 404. The full list is in [reference/ports-and-endpoints.md](reference/ports-and-endpoints.md#the-monitor-sites-routes).

## Check it worked

If you are publishing, the journal says `publish: live at ...` and that address shows your band.

If you are running a site, `/` lists receivers, picking one opens its page, and `monitor: accepting uplinks` names every station you invited.

## If it did not

- `publish: ... refused this station: says it is GB7RDG-2 and this token was issued to GB7RDG`. The `callsign` in your block is not the one the token was issued against. Fix it, or ask for a token for the callsign you use. Said once an hour until then, and the station carries on.
- `publish: still not publishing: ...`. The site is down or unreachable. It retries every few seconds and writes that line once a quarter of an hour.
- `publish: 240 blocks dropped: this station's upload cannot carry 12000 Hz audio. Lower "publish"."audioRate"`. Your upload is too slow for the rate you asked for. Set `"audioRate": 6000`.
- The service exits 2 with a sentence naming `publish` or `monitor`. A config refusal; the reasons are listed under each section in [reference/config.md](reference/config.md#publish), and reading start-up failures is in [12-troubleshooting.md](12-troubleshooting.md).
- A receiver is missing from the picker. It is offline, has no antenna, has no free listener slot (the picker counts it as not available), does not offer the IQ mode the site asks for, does not cover the window your modems occupy, or is in `deny`.

## Related

- [09-web-receivers.md](09-web-receivers.md) for the receivers a site fronts.
- [07-station-page.md](07-station-page.md) for the page a visitor sees.
- [11-logging-and-metrics.md](11-logging-and-metrics.md) for the frame log a site keeps.
- [reference/config.md](reference/config.md#publish) for every `publish` and `monitor` key.
- [reference/ports-and-endpoints.md](reference/ports-and-endpoints.md#uplink) for the uplink itself.
