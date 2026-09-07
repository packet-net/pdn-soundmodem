# The modem in a browser tab

The pdn-soundmodem core, compiled to WebAssembly, driving a radio through a plain USB audio
interface and a serial PTT line, with [`@packet-net/ax25`](https://github.com/m0lte/ax25-ts)
as the link layer. No TNC, no daemon, no install: a page, a sound card and a radio.

This is a proof of concept, not a product. What it establishes is that the port question was
the wrong question - the C# core runs in a browser as it is, and decodes bit for bit the same.

## What is here

```
modem/      the wasm module: the modem core plus a small JS-facing surface (Program.cs)
parity/     the native twin of that surface, so wasm decodes can be diffed against native
browser/    the page: audio graph, PTT, and the @packet-net/ax25 adapter
build.sh    builds the module and drops it in browser/_framework
```

The JS-facing surface is the whole boundary, and it is deliberately small: open a mode, push
received audio, pull decoded AX.25 frames, read carrier sense, modulate a frame to audio.
No waterfall, no config API, no KISS. Frames cross as raw AX.25, which is what
`@packet-net/ax25` takes.

## Build and run

```sh
./build.sh                # trimmed, interpreted
./build.sh --aot          # AOT compiled - bigger download, roughly native speed
cd browser && python3 -m http.server 8080     # any static server; https or localhost
```

Then open `http://localhost:8080/`. Chrome or Edge: Web Serial (the PTT line) does not exist
in Firefox or Safari, the same limitation `packet-term-web` already lives with.

## Verifying it

Two harnesses, both runnable without a browser or a radio:

```sh
node browser/main.mjs decode ../samples/ninotnc/qpsk2400.wav qpsk2400   # wasm
parity/bin/Release/net10.0/sm-wasm-parity decode ../samples/ninotnc/qpsk2400.wav qpsk2400
node browser/main.mjs loopback bpsk300                                  # TX into RX
cd browser/test && npm install && node two-stations.mjs qpsk3600        # two AX.25 stations
```

`two-stations.mjs` runs two complete stations - real DSP, real AX.25 - over a simulated wire,
and takes a connected-mode session all the way through: SABM(E), UA, I frames, the answer,
DISC. The only simulated part is the audio device.

## What it measured

**The decode is identical.** Every mode in the NinoTNC set, plus the Dire Wolf fixtures,
decoded byte for byte the same in WebAssembly as natively - same frames, same count, same
bytes. That is the finding that matters: the demodulators carry years of measured tuning
constants, and WebAssembly does not disturb one of them, so nothing needs re-validating.

**Transmit round-trips.** Ten of eleven modes modulate a frame and demodulate it back
identically through the wasm module. The eleventh (`c4fsk9600`, and `c4fsk19200` with it)
fails the same way in the native twin, so it is a property of a noiseless zero-channel
loopback and this harness, not of WebAssembly.

**It is fast enough, either way.** One core of a Ryzen 7 8745H, 48 kHz audio in, chain at the
mode's catalogue rate. Real-time factor, so 0.1 means a tenth of a core:

| mode | native | wasm AOT | wasm interpreted |
| --- | --- | --- | --- |
| afsk1200 | 0.036 | 0.011 | 0.037 |
| afsk300-il2pc | 0.069 | 0.073 | 0.267 |
| bpsk300 | 0.110 | 0.132 | 0.460 |
| qpsk2400 | 0.086 | 0.096 | 0.314 |
| qpsk3600 | 0.117 | 0.059 | 0.203 |

The native column includes process start-up, which is most of the afsk1200 figure; read AOT
as "the same as native, within the noise". Even the interpreter, which needs no AOT toolchain
and downloads half as much, leaves three quarters of a core spare on the heaviest mode
(`bpsk300` runs a nine-modem frequency-diversity bank).

**The download is small.** Trimmed and gzipped: 1.37 MB interpreted, 2.75 MB AOT. Brotli,
which any static host will serve, takes another 15 to 20 % off.

## What the harness taught us about the channel

Two failures in `two-stations.mjs` were the simulation being unphysical, and both are worth
knowing before anyone wires this to a real radio:

- **A receiver is never fed silence.** The first version delivered audio only during bursts.
  A busy detector decays over audio, not over wall clock, so with nothing arriving between
  bursts every station latched busy for ever and the CSMA gate waited on a channel that could
  never clear. A sound card always delivers; the harness has to as well.
- **Digital zero is not a noise floor.** The pump then delivered exact zeros, and the
  narrowband detectors have no floor to measure a signal against. Band noise at -54 dBFS
  fixed it, which is the same reason the real thing runs open-squelch.

A third is a real property of the modes rather than the harness: the busy hold after a burst
is long on the slow modes - 3.5 s of audio for `afsk300-il2pc` against 0.1 s for `qpsk2400` -
so bursts have to be delivered in real time or a station falls permanently behind its own
channel. And AX.25's default T1 of 6 s is a VHF number: at 300 baud a frame plus TXDELAY plus
the answer does not fit inside it, and the link retries a frame that was never lost. A
station picks T1 from the mode's throughput.

## What a real station still needs

Nothing in this proof of concept has been near a radio, and the one genuinely unknown thing
is browser audio capture: `getUserMedia` is asked here for `echoCancellation`,
`noiseSuppression` and `autoGainControl` all off, but the OS mixer's own AGC sits outside the
browser's reach. That wants a bench session, not more code. So does the PTT-to-audio
alignment: the browser owns the output buffer, so `sm-modem.js` pads the unkey with the
reported `outputLatency` and that padding should be checked on a scope rather than trusted.
