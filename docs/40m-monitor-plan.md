# 40m-monitor - a public HF packet monitor over one UberSDR receiver

**Status: 4.1 and 4.2 built on `feat/ubersdr-on-demand`, 2026-09-03. 4.3: CT 146 `40m-monitor` is up on a pre-release build, connect-on-demand verified against M9PSY-1 from inside the container; the tunnel and DNS are the remaining steps, then the release .deb replaces the pre-release one.** Written at Tom's request for a public-facing deployment of pdn-soundmodem at https://40m-monitor.ukpacketradio.network, showing the 7050-7052 kHz packet window that GB7RDG-2 works, as heard by a public UberSDR receiver.

## 1. What it is

One pdn-soundmodem deployment bound to one UberSDR receiver, chosen in its config, serving the existing browser page to the public: the waterfall, the AX.25 links panel, the decoded-frame log, and the browser audio for a visitor who wants to hear the band. Receive only. Any number of visitors at once, all watching the same receiver.

The one behavioural change from today's daemon: the receiver session exists **only while at least one browser has the page open**. With nobody watching, the daemon sits idle and holds no session on anybody's receiver.

## 2. What it is not

- **Not a receiver picker.** One deployment is one receiver. Want a second receiver, run a second deployment. A wrapper that fronts several deployments is a possible later project; the directory at `https://instances.ubersdr.org/api/instances` is a clean JSON list (host, port, TLS, online flag, free slots, IQ modes, tuning range, callsign, location, noise floor) and is noted here for then.
- **Not a KISS endpoint.** KISS stays bound to loopback and is not tunnelled. Nothing but the waterfall port reaches the internet.
- **Not an operator console.** No transmit, no config API, no survey. All three are already off unless configured; the deployment simply does not configure them.

## 3. What already exists

Most of this is built. Confirmed against the tree at `d80d6e7` (v0.53):

- `"device": "ubersdr:<host>"` (`src/Packet.SoundModem/UberSdr/`): takes the receiver's IQ stream, demodulates SSB in-process, rides the receiver's hourly session rollover, backs off politely on 429 and daily-allowance refusals (`UberSdrReconnectPolicy`), and raises `Lost` after five minutes of failed reconnects, which the daemon answers with exit 1 and a systemd restart.
- The waterfall server holds one WebSocket per open page (`_clients` in `WaterfallWebServer`), sends a `radio` status message the page already displays, replays the last 50 logged frames and every known link on connect, and the page reconnects by itself after a daemon restart.
- The daemon's receive loop treats a 0-sample `Read` as "nothing to say" and just loops; every input that can return 0 waits inside `Read` first, so idling is already a supported state at the loop.
- The page's only uplink is "audio on/off, waterfall lines on/off". The config API is wired only when `api.key` is set; survey download links appear only with a survey configured.

## 4. What to build

### 4.1 Connect on demand (the one real daemon change)

Config: `"ubersdr": { "onDemand": true, "lingerSeconds": 60 }`. Off by default; the live stations are unaffected.

A new `IAudioInput` wrapper, `OnDemandUberSdrInput`, owning zero or one `UberSdrAudioInput`:

- **Idle**: `Read` waits 100 ms and returns 0, exactly as the real input does with an empty ring. No socket, no session.
- **First viewer attaches**: open the session in the background (`UberSdrAudioInput.OpenAsync`, as the start-up path does today) and go **Live**; `Read` then forwards to it. While connecting, `Read` keeps returning 0.
- **Last viewer leaves**: start a linger timer (`lingerSeconds`); if a viewer returns before it fires, nothing happens. When it fires, dispose the session and return to Idle. The linger exists so a page refresh, a tab switch or a flaky connection does not cost the receiver a session tear-down and rebuild each time; one session per visit, not per WebSocket.
- **Demand signal**: `WaterfallWebServer` gains a `ViewersChanged` event raised from the add/remove in `ServeWebSocketAsync`. The wrapper subscribes; nothing else in the daemon changes.
- **Start-up validation stays**: the daemon still POSTs the receiver's `/connection` pre-flight once at start-up, so a wrong host, a refused IQ mode or a bad password is still an exit-2 configuration error and not a silence later. The pre-flight is a REST call, not a stream, so it holds no slot. Then the daemon goes Idle.
- **Dead-feed watches stand down while idle.** The ubersdr defaults are 30 s starvation and 30 s silence; either would restart an idle daemon every half minute. The wrapper exposes `Idle`, and the watches skip their check while it is true. Live behaviour is unchanged.
- **`Lost` while live** does *not* exit 1 as the always-on device does (revised while building: the wrapper already owns a retry cycle for a session that fails to open, so a session that gives up joins it). The wrapper drops the dead session and, if anyone is still watching, reopens on the usual ladder; if nobody is, it goes idle. The page stays up throughout and the status chip says what is happening. Exit 1 would have restarted a daemon whose only fault was somebody else's receiver being down, and blanked the page while it did.
- **A receiver that cannot be opened** likewise: the failed open is retried on the reconnect ladder (1 s rungs for a transport failure, 60 s upward for a refusal) for as long as a viewer waits, and abandoned when the last one leaves.
- **Status to the browser** rides the existing `radio` message, one line per state: idle ("connects when someone is watching"), connecting, live (receiver callsign and location, from the receiver's own description), refused ("the receiver's daily allowance for this address is used; back tomorrow"). Each transition also goes to the journal with the viewer count, in ASCII.
- **Memory across sessions**: the links panel and frame log keep what they had. A visitor arriving to a quiet band still sees the last 50 frames and every link the station has heard, which is what makes a quiet band look alive rather than broken.

State machine (Idle, Connecting, Live, Lingering) is a plain class over `TimeProvider`, unit-tested with a fake clock: attach opens, detach starts the linger, re-attach inside the linger cancels it, linger expiry closes, attach while connecting does not open twice, detach while connecting still closes.

### 4.2 A public flavour of the page

`"waterfall": { "public": true, "title": "40 m packet monitor" }`:

- A title and one-paragraph blurb at the top: what the window is, that it is receive only, and **whose receiver this is** (callsign, location, and a link to the receiver's public URL). It is somebody else's receiver; the credit is not optional.
- The KISS host strip and anything else that only means something to an operator is hidden. Waterfall, links, frames and the listen control stay.
- The connection state from 4.1 shown where the radio status is shown today.

Nothing is removed from the operator page; `public` only hides.

### 4.3 Deployment

- **Container**: new unprivileged LXC on proxmox1 (`root@10.45.0.10`), Debian 13, `keyctl=1,nesting=1`, `net0` on `vmbr0` by DHCP, rootfs on `local-zfs`, `swap: 512`, `onboot: 1`. Name `40m-monitor`.
- **Daemon**: the `.deb` from the first release that carries 4.1 and 4.2 (so a release is a step, below). `bind` is loopback; only the waterfall port is reached by the tunnel.
- **Tunnel**: `cloudflared` inside the container on its own remotely-managed tunnel `40m-monitor`, ingress `40m-monitor.ukpacketradio.network -> http://localhost:8099`, same shape as the move-wiki container: a localhost origin cannot break when the DHCP lease moves.
- **DNS**: `ukpacketradio.network` is already on Cloudflare (nameservers `braden`/`sonia`, the same pair as `fann.ing`), so no migration; the tunnel route creates the CNAME. Confirm the zone is in Tom's account before assuming.
- **Cloudflare**: WebSockets are on by default; add a rate-limit rule on the hostname (the free plan allows one) so a scraper cannot hold hundreds of sockets open.
- **Upkeep**: `unattended-upgrades` covering Debian security and the cloudflared repo, as CT 145 has.

Config as deployed:

```json
{
  "device": "ubersdr:m9psy-1.instance.ubersdr.org",
  "ubersdr": { "onDemand": true, "lingerSeconds": 60 },
  "modems": [
    { "subChannel": 0, "mode": "afsk300-il2pc",           "rfFrequency": 7050300 },
    { "subChannel": 1, "mode": "ardop", "bandwidth": 500, "rfFrequency": 7050950 },
    { "subChannel": 2, "mode": "bpsk300",                 "rfFrequency": 7051600 }
  ],
  "frameLog": { "path": "/var/lib/pdn-soundmodem/frames.db" },
  "waterfall": {
    "port": 8099,
    "public": true,
    "title": "40 m packet monitor",
    "about": "The 7050-7052 kHz packet window on 40 m, the one GB7RDG-2 works, as heard by a public web receiver in Scotland. Receive only: this page decodes what it hears and shows the AX.25 links and frames; nothing is transmitted."
  },
  "bind": "127.0.0.1"
}
```

The three modems are GB7RDG-2's standard slots (same modes, same RF centres, so the overlays line up with what the node actually works): AFSK300 IL2P+CRC, ARDOP 500 and BPSK300 IL2P+CRC. Tom, 2026-09-03: "those are the standard slots". The daemon plans the dial from the RF centres as it does for the node today.

### 4.4 Etiquette and limits

- **Per-address daily allowance.** The receivers meter listening time per client address, and a public monitor on one fixed egress address could use a receiver's allowance for that address by mid-afternoon on a busy day. The page says so honestly when it happens (4.1), and the connection reply carries a bypass flag, so the fix is to ask the receiver's operator to allow the monitor's address. Do that before announcing the site, not after.
- **One session, however many viewers.** Fan-out happens in the daemon; the receiver sees one client. The linger stops a busy page from churning sessions.
- **Hourly rollover** is the receiver's and is already handled with a one-second breath.

## 5. Order of work

1. `OnDemandUberSdrInput`, the demand event, the watch stand-down, status messages; state-machine tests. Branch off `main` in `/home/tf/pdn-soundmodem` (the `/home/tf/src` checkout is stale).
2. The `public` page flavour.
3. Release (tag), so there is a `.deb`.
4. Container, tunnel, DNS, rate limit. Soak it overnight with a tab open and a tab closed; read the journal for session churn, allowance refusals and any watchdog restart while idle.
5. Ask the receiver's operator about the address allowance; then announce.

## 6. Decisions (Tom, 2026-09-03)

- **Receiver: M9PSY-1** (`m9psy-1.instance.ubersdr.org`), the instance the example config and the OTA tooling already use.
- **Linger: 60 s.** Longer holds a slot for nobody; shorter churns sessions on a refresh-happy visitor.
- **Modems: GB7RDG-2's three standard slots**, ARDOP included, as in the config above.
- **Frame history on a public box: yes.** It is the whole reason a quiet band still shows something; a public record of callsigns heard is no more than any other monitor site publishes.
