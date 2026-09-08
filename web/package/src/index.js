// A sound card instead of a TNC.
//
// The modem produces and consumes raw AX.25 frames, which is all a link layer needs from
// whatever is between it and the air. Nothing here knows about any particular AX.25
// implementation, and this package depends on nothing at all.
export { SoundModem, SerialPtt, NoPtt } from './modem.js'
export { SoundModemTransport } from './transport.js'
