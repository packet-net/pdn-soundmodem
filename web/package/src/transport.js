// The modem as a transport: the slot a KISS TNC on a serial port occupies, filled by a sound
// card instead.
//
// This file imports nothing. It satisfies a transport contract by shape - `start`, `send`,
// `stop`, and a `channelBusy()` for carrier sense - so any link layer that wants raw AX.25
// frames from somewhere can take them from here without either side knowing about the other.
// A station picks its modem the way it always has: a TNC on a lead, or the sound card.

/**
 * Classic p-persistent CSMA, AX.25 §6.4.2 - the same loop the C# SoundModemChannel runs:
 * while the channel is busy, wait a slot; when it is clear, roll p, and on a failed roll wait
 * a slot and try again. It belongs to the modem rather than to a link layer because it is a
 * property of a shared half-duplex radio channel: whoever owns the PTT owns the contention.
 */
async function contend({ channelBusy, persistence, slotTimeMs, signal }) {
  const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms))
  for (;;) {
    if (signal?.aborted) throw new DOMException('aborted', 'AbortError')
    if (channelBusy()) { await sleep(slotTimeMs); continue }
    if (Math.floor(Math.random() * 256) <= persistence) return
    await sleep(slotTimeMs)
  }
}

/** A transport, and a carrier-sense source, backed by a {@link SoundModem}. */
export class SoundModemTransport {
  /**
   * @param {import('./modem.js').SoundModem} modem an open SoundModem
   * @param options KISS channel-access parameters, in KISS units: persistence 0-255 where
   *   p = (value + 1) / 256, slot time and TXDELAY in milliseconds. The defaults are the
   *   daemon's.
   */
  constructor(modem, { txDelayMs = 300, persistence = 63, slotTimeMs = 100 } = {}) {
    this.modem = modem
    /** @type {(() => void) | undefined} */
    this.unsubscribe = undefined
    this.txDelayMs = txDelayMs
    this.persistence = persistence
    this.slotTimeMs = slotTimeMs
    this.running = false
    /** Transmissions are serialised: one radio, one keyup at a time. */
    this.queue = Promise.resolve()
  }

  /**
   * @param {(frame: Uint8Array) => void} onFrame
   * @returns {Promise<void>}
   */
  async start(onFrame) {
    this.unsubscribe?.()
    this.unsubscribe = this.modem.onFrame(onFrame)
    this.running = true
  }

  /**
   * @param {Uint8Array} axBytes one AX.25 frame, no flags and no FCS
   * @returns {Promise<void>}
   */
  async send(axBytes) {
    if (!this.running) throw new Error('transport not started')
    const send = this.queue.then(async () => {
      await contend({
        channelBusy: () => this.modem.channelBusy(),
        persistence: this.persistence,
        slotTimeMs: this.slotTimeMs,
      })
      await this.modem.transmit(axBytes, this.txDelayMs)
    })
    // Keep the chain alive after a failed send so one error does not wedge the radio.
    this.queue = send.catch(() => {})
    return send
  }

  async stop() {
    this.running = false
    this.unsubscribe?.()
    this.unsubscribe = undefined
  }

  /**
   * Carrier sense: a null would mean "cannot tell", and this modem always can.
   * @returns {boolean}
   */
  channelBusy() { return this.modem.channelBusy() }
}
