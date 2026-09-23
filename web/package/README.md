# @packet-net/soundmodem

A sound card instead of a TNC. The [pdn-soundmodem](https://github.com/packet-net/pdn-soundmodem)
DSP core compiled to WebAssembly, with a Web Audio graph and a PTT line either side of it, so
a browser tab with a USB audio interface is a packet modem.

It sits where a KISS TNC on a serial port sits: raw AX.25 frames in, raw AX.25 frames
out, plus a carrier-sense reading. It depends on nothing, and it knows nothing about any
particular AX.25 implementation.

```js
import { SoundModem, SerialPtt, SoundModemTransport } from '@packet-net/soundmodem'

const modem = await SoundModem.load()
await modem.open({ mode: 'afsk1200', ptt: await SerialPtt.request() })

modem.onFrame((ax25) => console.log(ax25.length, 'bytes'))

const transport = new SoundModemTransport(modem)   // start / send / stop + channelBusy()
```

`SoundModemTransport` satisfies a transport contract by shape, so a link layer can take its
frames without either side importing the other. With
[`@packet-net/ax25`](https://www.npmjs.com/package/@packet-net/ax25) that reads:

```js
const listener = new Ax25Listener(transport, { myCall: 'M0LTE-1', carrierSense: transport })
```

which is the same call a serial KISS TNC gets, with a different transport passed in.

## Modes

Every mode the modem catalogue builds, including the NinoTNC set: `afsk1200`, `afsk300`,
`bpsk300`, `bpsk1200`, `qpsk600`, `qpsk2400`, `qpsk3600`, `fsk4800`, `fsk9600`, `c4fsk9600`,
`c4fsk19200`, their IL2P and IL2P+CRC variants, and the MIL-STD-188-110D and FreeDV families.
`modem.modes` lists them.

The decodes are bit for bit what the native build produces - the same demodulators, carrying
the same measured tuning constants, checked frame by frame against the native build on every
release.

## Levels

A browser station has no sound card mixer to reach, so the two levels a station trims on the
card are trimmed in the graph instead: one gain either side of the modem, both in dB, both unity
at 0.

```js
modem.rxGainDb = 6      // 0 dB is the device's audio untouched
modem.txGainDb = -12    // 0 dB is the modulator's own output, which peaks at 0.8
modem.inputLevel        // { peak, rms, clip } in dBFS, from the most recent block
```

Receive gain is applied before the demodulator, and `inputLevel` is measured on the same samples
the demodulator is given - so the reading moves when the gain does, which is the one thing a
meter beside a gain control exists to show. Transmit gain is applied to everything the modem
sends, frames and test tones alike. Either can be set before `open()`; the modem keeps the dB and
applies it when the graph is built. A level outside -60 to +30 dB is refused rather than clamped.

## The transmitter test

The station page's TX test, on a browser station: the classic two-tone pair for a linearity
check, or one tone for a carrier level or an FM deviation check by Bessel null. It goes out
through the ordinary transmit path - the same PTT line, the same transmit gain - which is the
point of it. A test that took a different route to the air would measure that route.

```js
modem.twoTonePairHz          // [700, 1900]
modem.besselNullPresets      // [{ toneHz: 500, deviationHz: 1202.5 }, ...]

// Contends for the channel first, like anything else that keys, then keys and sends.
const done = await transport.testTone({ twoTone: true, seconds: 5 })
// { text: 'two-tone 700+1900 Hz, 5.0 s, peak level 0.80 - done, 5.0 s on air', onAir, stopped }

transport.stopTestTone()     // withdraws it if queued; fades it out if it is on the air
```

`modem.describeTestTone(options)` reads a request without sending it, which is how a page shows
what is about to go out - and how a tone it will not send gets refused before anything is keyed.
The tones and the presets come from the core's own `TestTone`, so nothing here is a second copy
of a measurement setting. A single tone must be at least 50 Hz and below Nyquist, and is refused
rather than moved: the deviation read off a null is wrong by exactly as much as a tone that was
quietly nudged. A test is capped at 30 seconds whatever is asked for, because a test
transmission is a transmission.

Raise the transmit gain until the carrier disappears on a spectrum display and the level at that
point is the deviation the preset names.

## What it needs

Chrome, Edge or Opera, on desktop. Web Serial and WebHID, which carry the PTT line between
them, exist in neither Firefox nor Safari and are switched off in mobile Chromium builds; the
modem itself runs anywhere, so receive-only or VOX use does not need either. A secure context
(https, or `http://localhost`) for the microphone and for the keying device.

PTT is RTS or DTR on a serial port, or the GPIO pin of a CM108-family dongle over WebHID:

```js
import { Cm108Ptt } from '@packet-net/soundmodem'

await modem.open({ mode: 'afsk1200', ptt: await Cm108Ptt.request({ debug: true }) })
```

which is worth having because it makes the whole station one USB lead: the dongle carries
receive audio, transmit audio and the keying, so `inputDeviceId`, `outputDeviceId` and the PTT
are all the same piece of hardware. `gpio` defaults to 3, which is what every interface we have
seen wires PTT to, and `filters` defaults to C-Media's vendor ID plus the AIOC (`1209:7388`),
which emulates the same report - pass `filters: []` to see every HID device on the machine for
a clone that reports somebody else's.

On Linux the browser needs permission on the hidraw node, and it runs as you rather than as a
service account, so `uaccess` is the rule to write rather than a group:

```
KERNEL=="hidraw*", ATTRS{idVendor}=="0d8c", TAG+="uaccess"
KERNEL=="hidraw*", ATTRS{idVendor}=="1209", ATTRS{idProduct}=="7388", TAG+="uaccess"
```

One thing to know before you key a radio from a tab: the chip latches the pin, so PTT survives
the page going away. `Cm108Ptt` releases on `pagehide` and on `close()`, which covers a
navigation or a closed tab, but nothing in the browser can cover a crash. An interface with a
hardware transmit timeout is the belt to that braces.

## Licence

AGPL-3.0-or-later. See `NOTICE` for what is in the bundle and under which licence: the modem
core and the IL2P codec are GPL-3.0-or-later inside an AGPL-3.0-or-later combination, which is
what GPLv3 section 13 provides for. Anything that imports this package forms a combined work
under those terms.
