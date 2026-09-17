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
  constructor(modem, { txDelayMs = 300, persistence = 63, slotTimeMs = 100, channelWaitMs = 30000 } = {}) {
    this.modem = modem
    /** @type {(() => void) | undefined} */
    this.unsubscribe = undefined
    this.txDelayMs = txDelayMs
    this.persistence = persistence
    this.slotTimeMs = slotTimeMs
    /** How long a test transmission waits for a clear channel before giving up on one. */
    this.channelWaitMs = channelWaitMs
    this.running = false
    /** Transmissions are serialised: one radio, one keyup at a time. */
    this.queue = Promise.resolve()
    /** The controller that withdraws a test still waiting for the channel. @type {AbortController | undefined} */
    this.testWait = undefined
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

  /**
   * Sends one test transmission, contending for the channel first. A test takes its own keyup
   * rather than being appended to a frame's - the operator wants to measure the tones, not the
   * packet in front of them - but it takes its turn in the same queue, because there is one
   * radio, and it waits for a clear channel like anything else that keys: a transmitter test is
   * not a reason to talk over somebody.
   */
  /**
   * @param {{ twoTone?: boolean, toneHz?: number, seconds?: number }} [options]
   * @returns {Promise<{ text: string, onAir: number, stopped: boolean }>}
   */
  async testTone(options = {}) {
    if (!this.running) throw new Error('transport not started')
    if (this.testWait) throw new Error('a test transmission is already running')

    const waiting = new AbortController()
    this.testWait = waiting
    // The other half of the bound on a test. The burst bounds its own airtime; this bounds the
    // wall clock, because the wait for a clear channel has no timeout of its own and a channel
    // busy for minutes would otherwise leave a page saying "running" for ever.
    let timedOut = false
    const gaveUp = setTimeout(() => { timedOut = true; waiting.abort() }, this.channelWaitMs)

    const run = this.queue.then(async () => {
      try {
        await contend({
          channelBusy: () => this.modem.channelBusy(),
          persistence: this.persistence,
          slotTimeMs: this.slotTimeMs,
          signal: waiting.signal,
        })
      } catch (aborted) {
        if (aborted.name !== 'AbortError') throw aborted
        // Withdrawn while it was still queued, so the radio never keyed - and it must not key
        // later when the channel finally clears. The wording is the daemon's for the same two
        // outcomes, because they are the same two outcomes.
        throw new Error(timedOut
          ? `the channel did not clear within ${(this.channelWaitMs / 1000).toFixed(0)} s, `
            + 'so the test was withdrawn and nothing was transmitted'
          : 'stopped before it reached the air, so nothing was transmitted')
      } finally {
        clearTimeout(gaveUp)
      }

      return this.modem.testTone({ txDelayMs: this.txDelayMs, ...options })
    }).finally(() => {
      if (this.testWait === waiting) this.testWait = undefined
    })

    // Keep the chain alive after a failed test, for the reason send() does: one error must not
    // wedge the radio.
    this.queue = run.catch(() => {})
    return run
  }

  /**
   * Ends the test transmission: withdraws it if it is still waiting for the channel, fades it
   * out if it is already on the air. Does nothing when there is neither.
   * @returns {void}
   */
  stopTestTone() {
    // Nothing in flight, nothing to do - and nothing asked of the modem either, which is what
    // lets stop() call this unconditionally without caring what is standing in for a sound card.
    if (!this.testWait) return
    this.testWait.abort()
    this.modem.stopTestTone()
  }

  async stop() {
    this.running = false
    this.stopTestTone()
    this.unsubscribe?.()
    this.unsubscribe = undefined
  }

  /**
   * Carrier sense: a null would mean "cannot tell", and this modem always can.
   * @returns {boolean}
   */
  channelBusy() { return this.modem.channelBusy() }
}
