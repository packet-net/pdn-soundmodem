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
- `send` sends the text as one datagram, says whether it was transmitted or dropped, and exits. A text too big for one datagram is refused. It waits for the station's first identification to finish before it exits.
  `pdn-ofdm send --device plughw:CARD=Device,DEV=0 --ptt cm108:/dev/hidraw0 --call G4ABC-1 --to M0XYZ-1 hello`
- `ping` sends `--count` datagrams (default 5) of `--size` bytes (default 32), one at a time, each asking for a report. For each it prints the round trip, from handing the datagram to the modem to the echo arriving, and the rate in each direction. The summary gives the rate the link supports both ways: the lower of the two. An echo not back in 30 s is counted as no answer.
  `pdn-ofdm ping --device plughw:CARD=Device,DEV=0 --ptt cm108:/dev/hidraw0 --call G4ABC-1 --to M0XYZ-1 --count 10`

`--detail` raises the trace to detail level. `--log FILE` appends every line to a file. `--record FILE.wav` writes the received audio, 48 kHz mono. Ctrl-C stops cleanly and releases the PTT.

## Output

Every line starts with UTC time, then the modem's own words. This is a simulated run, so the clock starts at zero:

```
00:00:05.80  Sent 1 datagram to everyone for M0XYZ-1 at 0.25 kbit/s, 2.24 s on air
00:00:14.00  Identified in Morse as G4ABC-1
00:00:30.00  ping 1 of 5: no answer in 30 s
00:00:33.34  Heard M0XYZ-1: 1 datagram, signal 127 dB
00:00:33.34  ping 2 of 5: 32 bytes, 3.26 s round trip, out 70.4 kbit/s, back not known yet
00:00:36.28  ping 3 of 5: 32 bytes, 2.94 s round trip, out 70.4 kbit/s, back 70.4 kbit/s
00:00:41.29  5 sent, 4 answered, 70.4 kbit/s both ways
```

The first ping of a cold start is often lost: the station identifies in Morse straight after its first transmission and is keyed when the answer arrives. Run ping again, or send once first.

## The echo

The ping is this tool's own convention, not part of the modem: a datagram under protocol byte `0xC1` whose payload is a kind byte (1 request, 2 reply), a two-byte sequence number and filler to the size asked for. The listener answers a request by sending the same bytes back with kind 2.

## Building for a Raspberry Pi

Debian 13 arm64: `dotnet publish tools/Packet.SoundModem.OfdmFmTool -c Release -r linux-arm64 --self-contained` gives a `pdn-ofdm` binary to copy over. This was built but not run on a Pi.
