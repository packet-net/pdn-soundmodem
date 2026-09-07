// The adapter that makes a sound-card modem look like a TNC to @packet-net/ax25.
//
// The library's seam is already the right shape for this: Ax25Transport carries RAW AX.25
// frames ("no KISS framing - that's the transport's job"), and CarrierSense is a plain
// "is the channel busy right now?" that the listener consults before it keys up. So there is
// nothing to change in @packet-net/ax25 or in a page built on it: construct
// `new Ax25Listener(transport, { myCall, carrierSense: transport })` and a browser tab is a
// complete station.

/**
 * Classic p-persistent CSMA, AX.25 §6.4.2 - the same loop the C# SoundModemChannel runs:
 * while the channel is busy, wait a slot; when it is clear, roll p, and on a failed roll wait
 * a slot and try again. It lives here rather than in the library because it is a property of
 * a shared half-duplex radio channel, and the library's own gate deliberately only knows how
 * to wait for clear.
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

/** An Ax25Transport (and a CarrierSense) backed by a {@link SoundModem}. */
export class SoundModemTransport {
  /**
   * @param modem an open SoundModem
   * @param options KISS channel-access parameters, in KISS units: persistence 0-255 where
   *   p = (value + 1) / 256, slot time and TXDELAY in milliseconds. The defaults are the
   *   daemon's.
   */
  constructor(modem, { txDelayMs = 300, persistence = 63, slotTimeMs = 100 } = {}) {
    this.modem = modem
    this.txDelayMs = txDelayMs
    this.persistence = persistence
    this.slotTimeMs = slotTimeMs
    this.running = false
    /** Transmissions are serialised: one radio, one keyup at a time. */
    this.queue = Promise.resolve()
  }

  async start(onFrame) {
    this.unsubscribe?.()
    this.unsubscribe = this.modem.onFrame(onFrame)
    this.running = true
  }

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

  /** CarrierSense: null would mean "cannot tell", and this modem always can. */
  channelBusy() { return this.modem.channelBusy() }
}
