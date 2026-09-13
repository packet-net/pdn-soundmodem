# The modem in a browser tab

The pdn-soundmodem core, compiled to WebAssembly and published as
[`@packet-net/soundmodem`](https://www.npmjs.com/package/@packet-net/soundmodem): a sound card
instead of a TNC, driving a radio through a plain USB audio interface and a serial PTT line.

It sits where a KISS TNC on a serial port sits. Raw AX.25 frames in, raw AX.25 frames out,
plus a carrier-sense reading, and it depends on nothing. A station picks its modem the way it
always has - a TNC on a lead, or the sound card - and the link layer above never notices.

## What is here

```
modem/      the C# project that compiles the core to WebAssembly, and its JS-facing surface
package/    the npm package: src/ is what ships, _framework/ is the built bundle
demo/       a page that drives it, with @packet-net/ax25 as the link layer
parity/     the native twin of the JS-facing surface, so decodes can be diffed against native
test/       the Node harnesses: decode/loopback, and two whole stations over a simulated wire
build.sh    builds the bundle into package/ and emits the type declarations
verify.sh   re-measures everything claimed below
```

The JS-facing surface is the whole boundary between C# and JavaScript, and it is deliberately
small: open a mode, push received audio, pull decoded AX.25 frames, read carrier sense,
modulate a frame to audio. No waterfall, no config API, no KISS framing - there is no serial
link here to frame anything for.

`package/package.json` carries `0.0.0-dev`; the real version is stamped from the release tag,
so the npm package, the NuGet package and the .debs all ship the same number from the same
commit.

## Run the demo

The demo loads the published package from a CDN, so there is nothing to build:

```sh
python3 -m http.server 8080           # any static server, from THIS directory
```

Then open `http://localhost:8080/demo/`. It has to be https or localhost: the microphone and
Web Serial both need a secure context. Chrome or Edge, because Web Serial does not exist in
Firefox or Safari.

Add `?local` to load the modem from this working tree instead, for developing the package and
the page together - `./build.sh` first, so the WebAssembly bundle is there for it to load.

The modem comes from jsDelivr rather than esm.sh, and that is not a preference. This package is
already plain ESM with no dependencies, so it needs no transform and must not get one: esm.sh
bundles each module and hands back a re-export stub for the AudioWorklet, which a worklet
cannot follow because worklet scope has no module resolution. The modem would load and then be
deaf. jsDelivr serves the files byte for byte as published (checked), so every
`new URL(..., import.meta.url)` inside the package still resolves to the WebAssembly bundle and
to the worklet.

## Build the package

```sh
./build.sh --aot                      # what ships; drop --aot for a smaller, slower build
```

## Verifying it

```sh
./verify.sh                                          # all of the below
node test/decode.mjs decode ../samples/ninotnc/qpsk2400.wav qpsk2400
node test/decode.mjs loopback bpsk300                # modulate, then demodulate it back
cd test && npm install && node two-stations.mjs qpsk3600
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
identically. The eleventh (`c4fsk9600`, and `c4fsk19200` with it) fails the same way in the
native twin, so it is a property of a noiseless zero-channel loopback and this harness, not of
WebAssembly.

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
as "the same as native, within the noise". Even the interpreter leaves three quarters of a
core spare on the heaviest mode (`bpsk300` runs a nine-modem frequency-diversity bank).

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

Nothing here has been near a radio, and the one genuinely unknown thing is browser audio
capture: `getUserMedia` is asked for `echoCancellation`, `noiseSuppression` and
`autoGainControl` all off, but the OS mixer's own AGC sits outside the browser's reach. That
wants a bench session, not more code. So does the PTT-to-audio alignment: the browser owns the
output buffer, so `package/src/modem.js` pads the unkey with the reported `outputLatency`, and
that padding should be checked on a scope rather than trusted.
