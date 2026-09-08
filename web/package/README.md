# @packet-net/soundmodem

A sound card instead of a TNC. The [pdn-soundmodem](https://github.com/packet-net/pdn-soundmodem)
DSP core compiled to WebAssembly, with a Web Audio graph and a serial PTT line either side of
it, so a browser tab with a USB audio interface is a packet modem.

It sits exactly where a KISS TNC on a serial port sits: raw AX.25 frames in, raw AX.25 frames
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

Chrome or Edge. Web Serial, which carries the PTT line, does not exist in Firefox or Safari;
the modem itself runs anywhere, so receive-only or VOX use does not need it. A secure context
(https, or `http://localhost`) for both the microphone and the serial port.

PTT is RTS or DTR on a serial port. A CM108-style dongle that keys over HID is not a serial
port and will not work through Web Serial; use VOX, or a separate USB-serial lead.

## Licence

AGPL-3.0-or-later. See `NOTICE` for what is in the bundle and under which licence: the modem
core and the IL2P codec are GPL-3.0-or-later inside an AGPL-3.0-or-later combination, which is
what GPLv3 section 13 provides for. Anything that imports this package forms a combined work
under those terms.
