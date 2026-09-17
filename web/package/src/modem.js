// pdn-soundmodem in a browser tab: the C# modem core compiled to WebAssembly, a Web Audio
// graph either side of it, and a PTT line over Web Serial. This is the sound-card answer to
// the question a KISS TNC on a serial port answers, and it sits in the same place: raw AX.25
// frames in, raw AX.25 frames out, and a carrier-sense reading. No waterfall, no config API,
// no KISS framing - there is no serial link here to frame anything for.
//
// The audio contract with the wasm module is one rate, the AudioContext's: the module
// decimates into the mode's DSP rate and upsamples back out with the same anti-aliased
// filters the daemon uses on a real sound card, so nothing here resamples and the browser's
// own resampler never touches modem audio.

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms))

/** PTT on a serial control line - the RTS/DTR keying every packet interface has done for decades. */
export class SerialPtt {
  /**
   * Prompts for a port (must be called from a user gesture) and opens it.
   * @param {{ signal?: 'rts' | 'dtr', baudRate?: number }} [options]
   * @returns {Promise<SerialPtt>}
   */
  static async request({ signal = 'rts', baudRate = 9600 } = {}) {
    if (!navigator.serial) throw new Error('this browser has no Web Serial (Chrome or Edge required)')
    const port = await navigator.serial.requestPort()
    await port.open({ baudRate })
    const ptt = new SerialPtt(port, signal)
    await ptt.unkey()
    return ptt
  }

  /**
   * @param {SerialPort} port an open Web Serial port
   * @param {'rts' | 'dtr'} [signal] which control line keys the radio
   */
  constructor(port, signal = 'rts') {
    this.port = port
    this.signal = signal
  }

  #signals(on) {
    return this.signal === 'dtr' ? { dataTerminalReady: on } : { requestToSend: on }
  }

  key() { return this.port.setSignals(this.#signals(true)) }
  unkey() { return this.port.setSignals(this.#signals(false)) }
  async close() { await this.unkey(); await this.port.close() }
}

/**
 * PTT on the GPIO pin of a CM108/CM119-family USB audio dongle, over WebHID: a Digirig, a DRA
 * board, an RB-USB RIM, the CM108 Radio Widget. The point of it is that one USB lead then
 * carries receive audio, transmit audio and the keying, so a browser station is a single piece
 * of hardware. Pick the same dongle in the input and output selectors and there is nothing
 * else plugged in.
 *
 * The report is the one Hamlib's `cm108.c` quotes from the C-Media documentation and Dire
 * Wolf's `cm108_write` sends: a write-GPIO command byte, the GPIO output values, the
 * data-direction register (1 = output) and an SPDIF byte. A hidraw caller prepends a
 * report-number byte; in WebHID that number is `sendReport`'s first argument instead, so the
 * payload here is the remaining four. Data before mask - the orders are indistinguishable on
 * a keyup and differ only on the release, which is how the native class carried them swapped
 * for months (see Cm108Ptt.cs).
 *
 * Chrome should hand this device over, and the reasoning is worth writing down because the
 * expectation is that it refuses. Its protected-usage check (`IsAlwaysProtected` in Chromium's
 * services/device/public/cpp/hid/hid_report_utils.cc) covers the keyboard usage page and the
 * Generic Desktop pointer, keypad and system ranges, and nothing else - not the consumer page
 * a CM108's volume keys sit on, and not a vendor-defined one. C-Media is not on the HID
 * blocklist either, which is a FIDO measure. What has NOT been confirmed against a real dongle
 * is the report descriptor itself: if a device turned out to declare its collection on one of
 * those protected pages, Chrome would hide it from the picker and there is no way round that.
 * Nobody has had one in front of this yet; see docs/dev/roadmap.md.
 *
 * On Linux the browser still needs permission on the hidraw node, and the daemon's rule is the
 * wrong shape for it - that one grants a service account through a group, where a browser runs
 * as the logged-in user. Use uaccess instead:
 *
 *     KERNEL=="hidraw*", ATTRS{idVendor}=="0d8c", TAG+="uaccess"
 */
export class Cm108Ptt {
  /** C-Media, which covers the great majority of these interfaces. */
  static VENDOR_CMEDIA = 0x0d8c

  /**
   * Prompts for a device (must be called from a user gesture) and opens it.
   *
   * @param {{ gpio?: number, filters?: HIDDeviceFilter[], debug?: boolean | ((message: string) => void) }} [options]
   *   `gpio` is the pin, 1 to 8, and 3 on every interface we have seen. `filters` narrows the
   *   browser's picker; the default is C-Media's vendor ID, and `[]` shows every HID device on
   *   the machine, which is what a clone that reports somebody else's ID needs. `debug` traces
   *   every report to the console, or to a function of your own.
   * @returns {Promise<Cm108Ptt>}
   */
  static async request({ gpio = 3, filters = [{ vendorId: Cm108Ptt.VENDOR_CMEDIA }], debug = false } = {}) {
    if (!navigator.hid) throw new Error('this browser has no WebHID (Chrome, Edge or Opera on desktop required)')
    const [device] = await navigator.hid.requestDevice({ filters })
    // An empty list is the user closing the picker, not a failure of the device.
    if (!device) throw new Error('no CM108 device chosen')
    if (!device.opened) await device.open()
    const ptt = new Cm108Ptt(device, gpio, debug)
    await ptt.unkey()
    return ptt
  }

  /**
   * @param {HIDDevice} device an open WebHID device
   * @param {number} [gpio] the GPIO pin, 1 to 8
   * @param {boolean | ((message: string) => void)} [debug] trace every report
   */
  constructor(device, gpio = 3, debug = false) {
    if (!Number.isInteger(gpio) || gpio < 1 || gpio > 8) throw new Error(`gpio must be 1 to 8, not ${gpio}`)
    this.device = device
    this.gpio = gpio
    this.mask = 1 << (gpio - 1)
    this.debug = debug === true ? (message) => console.debug(`cm108: ${message}`) : debug || null
    // Best effort against the failure this hardware makes easy: the chip latches the pin, so a
    // tab that goes away mid-transmission leaves the radio keyed until something writes to it
    // again. A clean close, a navigation or the tab being hidden all reach this; a crash or a
    // pulled lead cannot, which is the argument for a hardware timeout in the interface.
    this.releaseOnHide = () => { this.#send(false).catch(() => {}) }
    // Guarded because a station is not the only thing that imports this: a bundler or a test
    // runner evaluating the module outside a document has no window to listen on.
    if (typeof addEventListener === 'function') addEventListener('pagehide', this.releaseOnHide)
    this.debug?.(`opened ${device.productName || 'device'} `
      + `(${hex4(device.vendorId)}:${hex4(device.productId)}), keying GPIO${gpio} (mask 0x${hex2(this.mask)})`)
  }

  async #send(asserted) {
    // { write GPIO, output values, data-direction register, SPDIF }, with the report number
    // passed separately. The direction byte stays set either way: we drive the pin low to
    // release, rather than turning it back into an input and letting it float.
    const report = Uint8Array.of(0x00, asserted ? this.mask : 0x00, this.mask, 0x00)
    this.debug?.(`${asserted ? 'key  ' : 'unkey'} -> report 0x00 [${[...report].map(hex2).join(' ')}]`)
    try {
      await this.device.sendReport(0x00, report)
    } catch (e) {
      // Worth naming the device: "Failed to write the report" alone does not say which of the
      // two things plugged in stopped answering.
      throw new Error(`cm108 ${this.device.productName || 'device'}: ${e.message}`)
    }
  }

  key() { return this.#send(true) }
  unkey() { return this.#send(false) }

  async close() {
    if (typeof removeEventListener === 'function') removeEventListener('pagehide', this.releaseOnHide)
    await this.unkey()
    await this.device.close()
    this.debug?.('closed')
  }
}

const hex2 = (n) => n.toString(16).padStart(2, '0')
const hex4 = (n) => n.toString(16).padStart(4, '0')

/** PTT that keys nothing - for a VOX interface, or for listening only. */
export const NoPtt = { key: async () => {}, unkey: async () => {}, close: async () => {} }

export class SoundModem {
  #exports
  #handle = 0
  #context
  #stream
  #worklet
  #frameListeners = new Set()
  #transmitting = false

  /**
   * Loads the WebAssembly runtime and the modem core. Do this once per page; opening and
   * closing modes afterwards is cheap.
   */
  /**
   * @param {string} [frameworkUrl] where the WebAssembly bundle lives; the default is the
   *   copy shipped alongside this module, which is right for a CDN and for self-hosting.
   * @returns {Promise<SoundModem>}
   */
  static async load(frameworkUrl = new URL('../_framework/dotnet.js', import.meta.url).href) {
    const { dotnet } = await import(frameworkUrl)
    const runtime = await dotnet.withDiagnosticTracing(false).create()
    const exports = await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName)
    return new SoundModem(exports.Modem)
  }

  constructor(exports) {
    this.#exports = exports
    this.txDelayMs = 300
    this.txTailMs = 20
    this.pttLeadMs = 50
  }

  /**
   * Every mode the catalogue can build, e.g. afsk1200, bpsk300, qpsk2400, fsk9600-il2p.
   * @returns {string[]}
   */
  get modes() { return this.#exports.Modes() }

  /** The audio rate the graph is running at, once open. @returns {number} */
  get sampleRate() { return this.#context?.sampleRate ?? 0 }

  /** True while this station is keyed. @returns {boolean} */
  get transmitting() { return this.#transmitting }

  /**
   * Opens the radio: microphone (or USB codec input) in, speaker (or USB codec output) out,
   * a mode running between them.
   *
   * The three audio-processing constraints are not optional. A browser will happily hand a
   * modem echo-cancelled, noise-suppressed, automatically-gained audio, and every one of
   * those is a defect generator a demodulator cannot see around.
   */
  /**
   * @param {{ mode?: string, inputDeviceId?: string, outputDeviceId?: string,
   *           ptt?: { key(): Promise<void>, unkey(): Promise<void>, close?(): Promise<void> },
   *           sampleRate?: number }} [options]
   * @returns {Promise<number>} the rate the DSP chain ended up running at
   */
  async open({ mode = 'afsk1200', inputDeviceId, outputDeviceId, ptt = NoPtt, sampleRate = 48000 } = {}) {
    this.ptt = ptt
    this.#stream = await navigator.mediaDevices.getUserMedia({
      audio: {
        deviceId: inputDeviceId ? { exact: inputDeviceId } : undefined,
        channelCount: 1,
        echoCancellation: false,
        noiseSuppression: false,
        autoGainControl: false,
      },
    })

    this.#context = new AudioContext({ sampleRate, latencyHint: 'playback' })
    if (outputDeviceId && this.#context.setSinkId) await this.#context.setSinkId(outputDeviceId)
    await this.#context.audioWorklet.addModule(new URL('./rx-worklet.js', import.meta.url))

    this.mode = mode
    this.#handle = this.#exports.Open(mode, this.#context.sampleRate)
    this.dspRate = this.#exports.DspRateOf(this.#handle)

    const source = this.#context.createMediaStreamSource(this.#stream)
    this.#worklet = new AudioWorkletNode(this.#context, 'sound-modem-rx', {
      numberOfInputs: 1,
      numberOfOutputs: 0,
      processorOptions: { blockSize: 1024 },
    })
    this.#worklet.port.onmessage = (event) => this.#receive(event.data)
    source.connect(this.#worklet)
    await this.#context.resume()
    return this.dspRate
  }

  /**
   * Subscribes to decoded frames - raw AX.25 bytes, no flags and no FCS. Returns the
   * unsubscribe. There can be several: a soundcard modem hears the whole channel, and the
   * session layer wanting a frame must not stop a monitor pane seeing it.
   */
  /**
   * @param {(frame: Uint8Array) => void} callback
   * @returns {() => void} the unsubscribe
   */
  onFrame(callback) {
    this.#frameListeners.add(callback)
    return () => { this.#frameListeners.delete(callback) }
  }

  /** True while the demodulator sees a coherent packet signal (the DCD lamp). @returns {boolean} */
  get carrierDetect() { return !this.#transmitting && this.#exports.CarrierDetect(this.#handle) }

  /**
   * Carrier sense: busy while anything is on channel, ourselves included.
   * @returns {boolean}
   */
  channelBusy() {
    if (this.#transmitting) return true
    return this.#exports.ChannelBusy(this.#handle)
  }

  /** @param {Float32Array} samples */
  #receive(samples) {
    // Our own transmission is not traffic to decode, and hearing it would only teach the
    // busy detector that the channel is occupied by the station that is talking.
    if (this.#transmitting) return
    this.#exports.Feed(this.#handle, new Uint8Array(samples.buffer, samples.byteOffset, samples.byteLength))
    const packed = this.#exports.TakeFrames(this.#handle)
    for (let at = 0; at < packed.length;) {
      const length = packed[at] | (packed[at + 1] << 8)
      const frame = packed.subarray(at + 2, at + 2 + length)
      for (const listener of this.#frameListeners) listener(frame)
      at += 2 + length
    }
  }

  /**
   * Keys the radio and sends one AX.25 frame. TXDELAY is inside the modulated audio, so the
   * only waits here are the radio's own: a lead before the audio starts, and a tail after it
   * ends - and the tail has to include the output latency, because the last sample leaves the
   * graph well before it leaves the sound card.
   */
  /**
   * @param {Uint8Array} ax25Frame one AX.25 frame, no flags and no FCS
   * @param {number} [txDelayMs]
   * @returns {Promise<void>}
   */
  async transmit(ax25Frame, txDelayMs = this.txDelayMs) {
    const pcm = new Float32Array(this.#exports.Modulate(this.#handle, ax25Frame, txDelayMs).buffer)
    const buffer = this.#context.createBuffer(1, pcm.length, this.#context.sampleRate)
    buffer.copyToChannel(pcm, 0)

    this.#transmitting = true
    try {
      await this.ptt.key()
      await sleep(this.pttLeadMs)
      const node = this.#context.createBufferSource()
      node.buffer = buffer
      node.connect(this.#context.destination)
      const ended = new Promise((resolve) => { node.onended = resolve })
      node.start()
      await ended
      await sleep(this.txTailMs + Math.round((this.#context.outputLatency ?? 0) * 1000))
    } finally {
      await this.ptt.unkey()
      this.#transmitting = false
      this.#exports.ResetCarrierState(this.#handle)
    }
  }

  async close() {
    if (this.#handle) this.#exports.Close(this.#handle)
    this.#handle = 0
    this.#worklet?.port.close()
    this.#stream?.getTracks().forEach((t) => t.stop())
    await this.#context?.close()
    await this.ptt?.close?.()
  }
}
