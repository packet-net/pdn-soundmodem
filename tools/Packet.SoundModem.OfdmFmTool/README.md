# pdn-ofdm

A small command-line tool that drives the adaptive OFDM-FM modem directly: one sound device, one PTT, one channel. No KISS, no daemon, no config file, no AX.25. It is for bench and field trials: listen, send a datagram, and time a ping to another station running the same tool.

```
pdn-ofdm listen --device SPEC [--ptt SPEC] --call CALL [--detail] [--log FILE] [--record FILE.wav]
pdn-ofdm send   --device SPEC  --ptt SPEC  --call CALL --to ADDRESS [...] TEXT...
pdn-ofdm ping   --device SPEC  --ptt SPEC  --call CALL --to ADDRESS [--count N] [--size BYTES] [...]
```

`--device` and `--ptt` are the daemon's own: an ALSA name or `pipe:IN,OUT[,RATE]`, and `serial:DEVICE[:rts|:dtr]` or `cm108:HIDRAW[:GPIO]`. `--call` is this station's callsign and the link address it sends from and answers to; `--to` is the other station's link address. Both are typed as written and upper-cased.

## Commands

- `listen` prints the modem's trace and every datagram received. With `--ptt` it answers pings. Without it the station is receive-only and cannot key.
  `pdn-ofdm listen --device plughw:CARD=Device,DEV=0 --ptt cm108:/dev/hidraw0 --call M0XYZ-1`
- `send` sends the text as one datagram, says whether it was transmitted or dropped, and exits. A text too big for one datagram is refused.
  `pdn-ofdm send --device plughw:CARD=Device,DEV=0 --ptt cm108:/dev/hidraw0 --call G4ABC-1 --to M0XYZ-1 hello`
- `ping` sends `--count` datagrams (default 5) of `--size` bytes (default 32), one at a time, each asking for a report. For each it prints the round trip, from handing the datagram to the modem to the echo arriving, and the rate in each direction. The first ping from cold includes both stations identifying. The summary gives the rate the link supports both ways: the lower of the two. An echo not back in 60 s is counted as no answer.
  `pdn-ofdm ping --device plughw:CARD=Device,DEV=0 --ptt cm108:/dev/hidraw0 --call G4ABC-1 --to M0XYZ-1 --count 10`

`--detail` raises the trace to detail level. `--log FILE` appends every line to a file. `--record FILE.wav` writes the received audio, 48 kHz mono. Ctrl-C stops cleanly and releases the PTT.

## Output

Every line starts with UTC time, then the modem's own words. This is a simulated run from two cold stations, so the clock starts at zero:

```
00:00:05.80  Sent identification as G4ABC-1, 1.36 s on air
00:00:13.25  Identified in Morse as G4ABC-1
00:00:13.39  Sent 1 datagram to everyone for M0XYZ-1 at 0.25 kbit/s, 2.24 s on air
00:00:24.98  Heard M0XYZ-1: 1 datagram, signal 87 dB
00:00:24.98  ping 1 of 5: 32 bytes, 24.98 s round trip, out 70.4 kbit/s, back not known yet
00:00:27.90  ping 2 of 5: 32 bytes, 2.92 s round trip, out 70.4 kbit/s, back 70.4 kbit/s
00:00:35.86  5 sent, 5 answered, 70.4 kbit/s both ways
```

The first ping includes both stations identifying; the rest take what the radio takes.

The station identifies before it first transmits: its identification burst, then the callsign in Morse, then the data. The time of each identification is kept in `${XDG_STATE_HOME:-$HOME/.local/state}/pdn-ofdm/`, one file for each callsign and device, so that running the tool again within ten minutes does not identify again.

## The echo

The ping is this tool's own convention, not part of the modem: a datagram under protocol byte `0xC1` whose payload is a kind byte (1 request, 2 reply), a two-byte sequence number and filler to the size asked for. The listener answers a request by sending the same bytes back with kind 2.

## Building for a Raspberry Pi

Debian 13 arm64: `dotnet publish tools/Packet.SoundModem.OfdmFmTool -c Release -r linux-arm64 --self-contained` gives a `pdn-ofdm` binary to copy over. This was built but not run on a Pi.
