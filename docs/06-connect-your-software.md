# Connect your software

When you finish this page your node, APRS client, Winlink client or paging software is attached to the modem over TCP and passing traffic.

## Before you start

- A station that decodes frames, from [02-first-station.md](02-first-station.md).
- Your node or APRS software installed, on this machine or on one that can reach it.
- A terminal for editing the config file, `/etc/pdn-soundmodem/soundmodem.json`.

Every change to the config file needs `sudo systemctl restart pdn-soundmodem` before it takes effect. KISS clients are disconnected by the restart and reconnect on their own.

## KISS over TCP

The modem speaks KISS over TCP. KISS is the TNC protocol your node or APRS software already knows, and over TCP it is the same framing on a socket instead of on a serial line. There is no serial port and no `kissattach`.

One port carries every packet modem at once. It is `kissPort`, 8105 unless you change it. The first byte of a KISS frame holds a port nibble, and the modem reads that nibble as the sub-channel: a frame marked 1 goes to the modem whose `subChannel` is 1. Your software may call the nibble the KISS channel, the port or the radio.

The journal names the port at start-up.

```
kiss tcp: 127.0.0.1:8105 (all modems, by sub-channel nibble)
```

Any number of programs may attach to one port, and each of them receives every frame that port publishes. The journal gives a line when one attaches and a line when one goes away, and the second carries a reason only when the close was not clean.

```
kiss[8105] 127.0.0.1:42756 connected - 1 client (all modems)
kiss[8105] 127.0.0.1:42756 disconnected - 0 clients (all modems)
```

A frame addressed to a sub-channel with no modem on it is refused and journalled as `tx[N] DROPPED ...: no modem on sub-channel N`. Which port carries which frames is set out under [Addressing](reference/ports-and-endpoints.md#addressing).

## Give a modem its own port

A good deal of node and APRS software assumes KISS channel 0 and offers nowhere to say otherwise. On the shared port such a program can only ever reach the modem on sub-channel 0, however many you run. Give that modem a `port` of its own and the limit stops mattering.

```json
{
  "kissPort": 8105,
  "modems": [
    { "subChannel": 0, "mode": "afsk1200", "port": 8110 },
    { "subChannel": 1, "mode": "bpsk300",  "port": 8111 }
  ]
}
```

A dedicated port carries that modem's frames and nothing else, and labels them sub-channel 0. Anything your software sends into it is transmitted on that modem whatever nibble it wrote. The shared port keeps running alongside, still reporting true nibbles, so you can use both at once.

```
kiss tcp: 127.0.0.1:8105 (all modems, by sub-channel nibble)
kiss tcp: 127.0.0.1:8110 (modem 0 afsk1200 only, as nibble 0)
```

If two services ask for one TCP port the modem stops at start-up, naming both settings. The keys are under [`modems`](reference/config.md#modems).

## One port for several modes

Some peers only have 1200 baud, others can do IL2P or 9600, and you only have one frequency. Run a modem for each mode on the same channel. Every modem hears every burst, so whichever one decodes a station tells you what that station uses. Then group them behind one polyglot port:

```json
{
  "modems": [
    { "subChannel": 0, "mode": "afsk1200" },
    { "subChannel": 1, "mode": "afsk1200-il2p" }
  ],
  "polyglot": [
    { "port": 8120, "subChannels": [0, 1], "default": 0 }
  ]
}
```

Point your node at port 8120 as KISS channel 0. It sees one port, and it gets frames from both modems. When it sends a frame, the modem picks the mode the next station along was last heard in. A station it hasn't heard in the last hour goes out in the `default` mode, and so does a beacon or broadcast sent direct, so make that the mode everyone has.

```
kiss tcp: 127.0.0.1:8120 (polyglot: modems 0 afsk1200, 1 afsk1200-il2p, as nibble 0; unheard stations get modem 0, forgotten after 60 min)
polyglot[8120]: G4ABC-1 heard on modem 1 afsk1200-il2p, frames for it go there now (was modem 0 afsk1200, the default)
```

Your node still has one set of link timers for the whole port, so choose them for the slowest mode. If you'd rather have separate timers for each speed, give each modem its own `port` and set up a node port for each. There's no need to pair `afsk1200` with `afsk1200-fx25`: stations without FX.25 can already read FX.25 frames. The keys are under [`polyglot`](reference/config.md#polyglot).

## Channel access is your software's to set

The modem does not carry TXDELAY, persistence, slot time or TXTAIL in its config file. Your node or APRS software sends them over KISS, and the modem applies each to the channel as it arrives, with no restart.

| Parameter | KISS command | Until your software sets it |
|---|---|---|
| TXDELAY | 1 | 300 ms |
| P (persistence) | 2 | 63 |
| SLOTTIME | 3 | 100 ms |
| TXTAIL | 4 | 20 ms |

Every modem in the process shares one channel, so a value sent on any port applies to all of them. The values live in memory: a restart returns to the table above, or to whatever `--txdelay` said.

Software that wants to know when a frame has left the radio can send it as ACKMODE, KISS command 12, with a two-byte id of its own on the front, the LinBPQ convention. The id comes back once that frame's audio has been handed to the sound card, rather than when it was queued; that is at most one card buffer (120 ms) before the frame has finished playing, and the keyup it belongs to is still holding the channel behind it. The frames of one keyup are written to the card back to back, with no gap between them on the air. See [ACKMODE](reference/ports-and-endpoints.md#ackmode), and [Commands](reference/ports-and-endpoints.md#commands) for the rest of what your software may send.

## Who can reach these ports

KISS has no authentication of any kind. Anything that can open the port can transmit on your licence.

Every listener binds to the top-level `bind`, which is `127.0.0.1`, so out of the box only software on this machine can attach. Setting `"bind": "*"` opens every listener on every interface, including the station page and ARDOP, and the journal says so at start-up.

```
kiss: WARNING - listening beyond loopback. KISS has no authentication: anything that can reach these ports can transmit on your licence.
```

For software on another machine, an SSH tunnel to loopback is a better answer than opening the port: run `ssh -L 8105:127.0.0.1:8105 you@modem-host` there and point the software at `127.0.0.1:8105` on that machine. [`kissPort` and `bind`](reference/config.md#kissport-and-bind) has the rules.

## LinBPQ

LinBPQ reaches the modem with a KISS-over-TCP port definition in `bpq32.cfg`. This one is written from LinBPQ's documented syntax and has not been tested against the modem here, so treat it as a starting point.

```
PORT
 PORTNUM=1
 ID=VHF
 TYPE=ASYNC
 PROTOCOL=KISS
 IPADDR=127.0.0.1
 TCPPORT=8105
 CHANNEL=A
 PACLEN=236
ENDPORT
```

`CHANNEL=A` is sub-channel 0, `B` is 1, and so on. For a second modem, add a second `PORT` block with the next letter and the same `TCPPORT`. If that does not work on your build of LinBPQ, give the second modem its own `port` as above and point a second `PORT` block at that `TCPPORT` with `CHANNEL=A`.

LinBPQ sends the channel-access values over KISS when it attaches, so set them in the `PORT` block as `TXDELAY=`, `PERSIST=`, `SLOTTIME=` and `TXTAIL=`.

## The PDN node

The [PDN node](https://github.com/packet-net/packet.net) takes a different route. It loads this modem as a library and runs it inside its own process, as a port of kind `soundmodem`, so there is no KISS socket between them and nothing here to configure. Set it up in the node.

## APRS software

An APRS client that can use a network KISS TNC connects with no more than an address and a port: `127.0.0.1` and `8105` for a station on the same machine. Beaconing, digipeating and any APRS-IS gating stay the client's job. A client that offers a channel setting wants the sub-channel of the modem you mean; one that offers none needs its own modem port, as above.

## ARDOP for Pat and Winlink Express

ARDOP is a connected-mode HF protocol, so it does not travel over KISS. Add a modem entry with `"mode": "ardop"` and the modem builds an ARDOP virtual TNC with ardopcf's host interface instead of a demodulator.

```json
{
  "modems": [
    { "subChannel": 0, "mode": "afsk300-il2pc", "port": 8100 },
    { "subChannel": 1, "mode": "ardop", "frequency": 950, "bandwidth": 500, "port": 8101 }
  ]
}
```

`port` here is the command port, and the data port is always the next one up, so 8101 reserves 8102 as well. An entry with no `port` uses 8515 and 8516. `bandwidth` is the widest session the TNC will accept or ask for, and ARDOP negotiates 200, 500, 1000 or 2000 Hz and nothing else. `frequency` moves ARDOP off its native 1500 Hz centre, which is how it shares a passband with the packet modems; see [08-hf.md](08-hf.md) for placing several modes in one passband.

```
ardop host tcp: 127.0.0.1:8101 (data 8102, ardopcf-compatible virtual TNC, modem 1, ARQBW 500MAX, centre 950 Hz)
```

Point your client at the command port. Pat, Winlink Express, ARIM, gARIM and hamChat connect unmodified. Pat's `~/.config/pat/config.json` needs the address and the bandwidth you configured.

```json
"ardop": {
  "addr": "localhost:8101",
  "arq_bandwidth": { "Forced": false, "Max": 500 },
  "ptt_ctrl": false,
  "cwid_enabled": false
}
```

Leave PTT to the modem: it keys the radio for ARDOP the same way it does for packet, so the client does not need a rig control path.

ARDOP shares the channel with the packet modems rather than taking it over. While an ARQ session is up, packet frames are held in the queue and refused if they wait more than 30 seconds. Receive carries on throughout, on every modem.

Busy detection is off unless the entry sets `"busyDetect": true`. With it on, the modem watches that modem's own slot and tells the TNC whether somebody else is using it. It measures energy, so an off-channel signal that lifts the noise floor can read as a busy channel.

The command set is ardopcf's own, documented at <https://github.com/pflarue/ardop>. Where this TNC answers differently, [ARDOP host interface](reference/ports-and-endpoints.md#ardop-host-interface) lists each case.

## POCSAG paging

Paging software sends pages over a line-based TCP service, one command per line. Add the section to turn it on.

```json
{ "paging": { "port": 8106, "baud": 1200 } }
```

```
paging tcp: 127.0.0.1:8106 (pocsag1200, DAPNET/POCSAG-compatible)
```

Send `PAGE <ric> <function> ALPHA|NUMERIC|TONE [text]` and the reply is `OK <id>` or `ERR <reason>`. The RIC is the pager's address, 0 to 2097151, and the function is 0 to 3.

```
PAGE 1234567 3 ALPHA M0LTE de pdn-soundmodem
OK 1
```

`OK` means the page is queued. When it goes out the journal says so, and the page appears in the [frames panel](07-station-page.md) as a transmission.

```
page[1] to 1234567 sent (pocsag1200)
```

Every page the modem hears on the channel goes to all connected clients as a `HEARD` line in the same shape. Pages share carrier sense and the PTT line with the packet modems.

`baud` is 512, 1200 or 2400. DAPNET, the European amateur paging network, runs 1200 baud, which is the default. Set the radio to your network's paging frequency yourself; the modem sends audio and does not tune the radio. The commands, the replies and the error text are under [POCSAG paging](reference/ports-and-endpoints.md#pocsag-paging).

## Check it worked

Attach your software, then read the journal.

```
journalctl -u pdn-soundmodem -f
```

Software that has attached gets a `connected` line naming its address and the modems the port reaches. Open the station page and the modem's chip badge reads `KISS 8105: 1 host` instead of `KISS 8105, no host`. Send something from your software and a `tx[N]` line follows, with the sub-channel in the brackets.

## If it did not

Your software reports connection refused. Either the modem is not running, or it is bound to loopback and your software is on another machine. Check `systemctl status pdn-soundmodem`, then `bind`.

Your software attaches but hears nothing. It may be reading a sub-channel no modem answers to. The `kiss tcp:` lines say which modems each port carries; match one to the channel number your software is set to, or give that modem its own port.

Frames arrive but nothing transmits. Look for `tx[N] DROPPED` in the journal, which names the reason. If `tx[N]` lines appear and the radio never keys, look for `tx test: unavailable - no "ptt" is configured, so this daemon does not key the radio` at start-up. [12-troubleshooting.md](12-troubleshooting.md) works through both, along with a config file the daemon refused.

## Related

- [05-modes.md](05-modes.md) for which mode to put on each sub-channel.
- [08-hf.md](08-hf.md) for sharing one HF passband between ARDOP and packet.
- [11-logging-and-metrics.md](11-logging-and-metrics.md) for keeping a record of what went in and out.
- [reference/ports-and-endpoints.md](reference/ports-and-endpoints.md) for every listener, the KISS command set, and the ARDOP and paging protocols.
- [reference/config.md](reference/config.md) for `kissPort`, `bind`, `modems`, `paging` and `ardop`.
- [reference/command-line.md](reference/command-line.md) for `--kiss`, `--bind`, `--ardop`, `--paging` and the rest.
