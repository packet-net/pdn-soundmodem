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
seen wires PTT to, and `filters` defaults to C-Media's vendor ID - pass `filters: []` to see
every HID device on the machine for a clone that reports somebody else's.

On Linux the browser needs permission on the hidraw node, and it runs as you rather than as a
service account, so `uaccess` is the rule to write rather than a group:

```
KERNEL=="hidraw*", ATTRS{idVendor}=="0d8c", TAG+="uaccess"
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
