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

// ---- Levels --------------------------------------------------------------------------------
//
// A browser station has no sound card mixer to reach: getUserMedia hands over whatever the
// operating system's capture level made of the input, and the output goes wherever the sink
// sends it. So the two levels a station page trims on the card are trimmed here instead, by a
// gain node either side of the modem - the same two controls, in the same units, doing the same
// job to the same audio.
//
// Both are in dB and both are unity at 0, which is what makes them readable: on receive, 0 dB is
// the audio exactly as the device delivered it; on transmit, 0 dB is the audio exactly as the
// modulator produced it, which peaks at 0.8 (the modulators' own default amplitude, and the
// level a test tone is sent at). Turning transmit down from there is the usual direction of
// travel, because what it is driving is a microphone or data input expecting millivolts.
const MIN_GAIN_DB = -60
const MAX_GAIN_DB = 30

const dbToGain = (db) => 10 ** (db / 20)

/** dBFS from a linear magnitude, with a floor rather than the -Infinity a silent block gives. */
const dbfs = (magnitude) => (magnitude > 0 ? Math.max(-120, 20 * Math.log10(magnitude)) : -120)

const checkGainDb = (db, which) => {
  if (!Number.isFinite(db) || db < MIN_GAIN_DB || db > MAX_GAIN_DB) {
    // Refused rather than clamped, as the daemon refuses a mixer level outside the card's own
    // range: a level is a setting somebody chose, and quietly moving it is how a station ends up
    // transmitting at one figure while its operator reads another.
    throw new RangeError(`${which} gain must be ${MIN_GAIN_DB} to ${MAX_GAIN_DB} dB, not ${db}`)
  }
  return db
}

// ---- The transmitter test ------------------------------------------------------------------
//
// The figures are the daemon's, named so they can be checked against it: TxTestRunner's
// DefaultSeconds, DefaultMaxSeconds and MinToneHz, and the modulators' own 0.8 peak. The cap is
// a licensing limit rather than a nicety - a test transmission is a transmission - so it is
// enforced here and not left to whatever the page put in its seconds box.
const DEFAULT_TEST_SECONDS = 5
const MAX_TEST_SECONDS = 30
const MIN_TONE_HZ = 50
const TEST_TONE_PEAK = 0.8
/** The raised-cosine rise and fall a burst is shaped with. TestTone.EdgeSeconds. */
const TEST_TONE_EDGE_SECONDS = 0.005

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
  #rxGain
  #txGain
  #frameListeners = new Set()
  #transmitting = false
  #rxGainDb = 0
  #txGainDb = 0
  #level = { peak: -120, rms: -120, clip: false }
  #test = null

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

    // The two level controls, one either side of the modem. Receive gain goes between the
    // device and the worklet, so the samples the modem is given and the samples the meter
    // measures are the same samples. Transmit gain goes between whatever is being played and
    // the output, so a frame and a test tone are scaled by the same node and the test really
    // does measure what a frame gets. Both carry whatever dB they were set to before the modem
    // was opened, which is how a page can restore levels and then Start.
    this.#rxGain = new GainNode(this.#context, { gain: dbToGain(this.#rxGainDb) })
    this.#txGain = new GainNode(this.#context, { gain: dbToGain(this.#txGainDb) })
    source.connect(this.#rxGain).connect(this.#worklet)
    this.#txGain.connect(this.#context.destination)

    await this.#context.resume()
    return this.dspRate
  }

  /**
   * Receive gain in dB: what the captured audio is multiplied by before the modem and the
   * meter see it. 0 dB is the device's audio untouched. Settable before the modem is opened.
   * @returns {number}
   */
  get rxGainDb() { return this.#rxGainDb }

  set rxGainDb(db) {
    this.#rxGainDb = checkGainDb(db, 'receive')
    // No ramp. This is a level control on a modem input, not a fader: what it is set to is
    // what the demodulator should be given, from the next render quantum onwards.
    if (this.#rxGain) this.#rxGain.gain.value = dbToGain(db)
  }

  /**
   * Transmit gain in dB: what modem audio is multiplied by on its way to the output device,
   * frames and test tones alike. 0 dB is the modulator's own output, which peaks at 0.8 - the
   * level a test tone is sent at too. Settable before the modem is opened.
   * @returns {number}
   */
  get txGainDb() { return this.#txGainDb }

  set txGainDb(db) {
    this.#txGainDb = checkGainDb(db, 'transmit')
    if (this.#txGain) this.#txGain.gain.value = dbToGain(db)
  }

  /**
   * The level at the modem's input, from the most recent block of audio: peak and RMS in dBFS,
   * and whether anything in that block reached full scale. Measured after the receive gain, so
   * it reads what the demodulator is actually being given.
   * @returns {{ peak: number, rms: number, clip: boolean }}
   */
  get inputLevel() { return this.#level }

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

  /** @param {{ block: Float32Array, peak: number, rms: number }} message */
  #receive({ block: samples, peak, rms }) {
    // The meter is updated whether or not the samples are decoded, and that is deliberate: a
    // keyed station still has an input, and what it is hearing then - its own audio coming back
    // round through the interface - is worth seeing rather than hiding behind a frozen bar.
    // 0.999 rather than 1.0: a converter that has run out of codes usually reports the last one
    // it has, and waiting for an exact full scale misses most of the clipping that matters.
    this.#level = { peak: dbfs(peak), rms: dbfs(rms), clip: peak >= 0.999 }

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
    this.#requireOpen()
    const pcm = new Float32Array(this.#exports.Modulate(this.#handle, ax25Frame, txDelayMs).buffer)
    await this.#keyed(async () => {
      const { ended } = this.#play(this.#toBuffer(pcm), this.#txGain)
      await ended
      await this.#drain()
    })
  }

  // ---- The transmitter test ----------------------------------------------------------------
  //
  // The station page's TX test, on a browser station: a two-tone pair for a linearity check, or
  // one tone for a carrier level or an FM deviation check by Bessel null. It goes out through
  // the ordinary transmit path - the same PTT line, the same transmit gain - which is the whole
  // point of it. A test that took a different route to the air would measure that route.
  //
  // Nothing is decided twice. The tones and the presets come from the core's own TestTone, the
  // burst is rendered by it, and the numbers the daemon holds as policy (the default length, the
  // cap, the lowest tone, the peak) are the constants at the top of this file, named after the
  // daemon's own. What a page may ask for here is what --two-tone could ask for there.

  /** The standard two-tone pair, low then high, in Hz. @returns {number[]} */
  get twoTonePairHz() { return [...this.#exports.TwoTonePairHz()] }

  /**
   * The single-tone presets, each with the FM deviation its Bessel null calibrates: raise the
   * transmit gain until the carrier disappears on a spectrum display and the level at that
   * point is that deviation.
   * @returns {{ toneHz: number, deviationHz: number }[]}
   */
  get besselNullPresets() {
    return [...this.#exports.BesselNullTonesHz()]
      .map((toneHz) => ({ toneHz, deviationHz: this.besselNullDeviationHz(toneHz) }))
  }

  /**
   * The FM deviation a tone nulls the carrier at, in Hz: 2.405 x the tone.
   * @param {number} toneHz
   * @returns {number}
   */
  besselNullDeviationHz(toneHz) { return this.#exports.BesselNullDeviationHz(toneHz) }

  /** True while a test transmission is queued here or on the air. @returns {boolean} */
  get testToneRunning() { return this.#test !== null }

  /**
   * Reads a request into the burst it asks for, or throws the reason it cannot be sent. Called
   * by {@link testTone}; called directly by a page that wants to show what is about to go out
   * before anything is keyed.
   */
  /**
   * @param {{ twoTone?: boolean, toneHz?: number, seconds?: number }} [options]
   * @returns {{ tones: number[], seconds: number, peakAmplitude: number, text: string }}
   */
  describeTestTone({ twoTone = true, toneHz = 0, seconds = DEFAULT_TEST_SECONDS } = {}) {
    this.#requireOpen()
    const asked = Number.isFinite(seconds) && seconds > 0 ? seconds : DEFAULT_TEST_SECONDS
    const capped = asked > MAX_TEST_SECONDS
    const length = Math.min(asked, MAX_TEST_SECONDS)

    let tones, what
    if (twoTone) {
      const [low, high] = this.twoTonePairHz
      tones = [low, high]
      what = `two-tone ${low.toFixed(0)}+${high.toFixed(0)} Hz`
    } else {
      const nyquist = this.sampleRate / 2
      if (!Number.isFinite(toneHz) || toneHz < MIN_TONE_HZ || toneHz >= nyquist) {
        // Refused rather than clamped, for the reason the daemon refuses it: a tone frequency is
        // a measurement setting, and silently moving it puts the deviation read off the null out
        // by exactly as much.
        throw new RangeError(
          `a test tone must be between ${MIN_TONE_HZ} Hz and the ${nyquist.toFixed(0)} Hz `
          + 'Nyquist of this channel')
      }
      tones = [toneHz]
      what = `single tone ${toneHz.toFixed(0)} Hz (FM Bessel null at `
        + `${(this.besselNullDeviationHz(toneHz) / 1000).toFixed(1)} kHz deviation)`
    }

    return {
      tones,
      seconds: length,
      peakAmplitude: TEST_TONE_PEAK,
      text: `${what}, ${length.toFixed(1)} s`
        + (capped ? ` (capped from ${asked.toFixed(1)} s)` : '')
        + `, peak level ${TEST_TONE_PEAK.toFixed(2)}`,
    }
  }

  /**
   * Keys the radio and sends one test transmission, resolving when it is off the air.
   *
   * TXDELAY is spent on silence in front of the tones, as the daemon's test spends it and as the
   * CW ident does: the transmitter wants to be keyed and quiet while it settles, and an SSB rig
   * radiates nothing without audio. So the burst is the length that was asked for, and the
   * keyup is that plus TXDELAY.
   */
  /**
   * @param {{ twoTone?: boolean, toneHz?: number, seconds?: number, txDelayMs?: number }} [options]
   * @returns {Promise<{ text: string, onAir: number, stopped: boolean }>} what went out, and how
   *   long of it - which is not what was asked for when {@link stopTestTone} cut it short.
   */
  async testTone(options = {}) {
    const plan = this.describeTestTone(options)
    if (this.#test) throw new Error('a test transmission is already running')
    if (this.#transmitting) throw new Error('the radio is already keyed, so nothing was sent')

    const rate = this.#context.sampleRate
    const txDelayMs = options.txDelayMs ?? this.txDelayMs
    const tone = new Float32Array(
      this.#exports.RenderTestTone(plan.tones, plan.peakAmplitude, rate, plan.seconds).buffer)
    const lead = Math.round((txDelayMs / 1000) * rate)
    const buffer = this.#context.createBuffer(1, lead + tone.length, rate)
    buffer.copyToChannel(tone, 0, lead)

    // A fade of its own in front of the transmit gain, so that a stop can take the burst down
    // over the same 5 ms TestTone shapes its own edges with. Stopping a buffer source outright
    // ends the tone on a rectangular edge, and a test transmission whose own edges splatter is a
    // poor instrument for measuring a transmitter's cleanliness.
    const fade = new GainNode(this.#context, { gain: 1 })
    fade.connect(this.#txGain)

    try {
      return await this.#keyed(async () => {
        const { node, ended } = this.#play(buffer, fade)
        const toneStartsAt = this.#context.currentTime + lead / rate
        this.#test = { node, fade, toneStartsAt, seconds: plan.seconds }
        await ended
        const stopped = this.#test.stoppedAt !== undefined
        const onAir = stopped
          ? Math.max(0, this.#test.stoppedAt - toneStartsAt)
          : plan.seconds
        await this.#drain()
        return {
          text: `${plan.text} - ${stopped
            ? `stopped after ${onAir.toFixed(1)} s`
            : `done, ${onAir.toFixed(1)} s on air`}`,
          onAir,
          stopped,
        }
      })
    } finally {
      this.#test = null
      fade.disconnect()
    }
  }

  /**
   * Ends the test transmission that is running, faded rather than cut. Does nothing when there
   * is none, which is what a doubled click looks like.
   * @returns {void}
   */
  stopTestTone() {
    const test = this.#test
    if (!test || test.stoppedAt !== undefined) return
    const now = this.#context.currentTime
    test.stoppedAt = now
    test.fade.gain.setValueAtTime(test.fade.gain.value, now)
    test.fade.gain.linearRampToValueAtTime(0, now + TEST_TONE_EDGE_SECONDS)
    // Stopped at the end of the fade, not before it: a stop time inside the ramp truncates the
    // fade and puts back the edge it was there to avoid.
    test.node.stop(now + TEST_TONE_EDGE_SECONDS)
  }

  async close() {
    this.stopTestTone()
    if (this.#handle) this.#exports.Close(this.#handle)
    this.#handle = 0
    this.#worklet?.port.close()
    this.#stream?.getTracks().forEach((t) => t.stop())
    await this.#context?.close()
    this.#context = undefined
    this.#rxGain = this.#txGain = undefined
    this.#test = null
    await this.ptt?.close?.()
  }

  #requireOpen() {
    if (!this.#context) {
      throw new Error('the modem is not open: open a mode first, so there is an audio device '
        + 'and a PTT line to key')
    }
  }

  /** Float PCM at the graph's rate, as a buffer a source node can play. */
  #toBuffer(pcm) {
    const buffer = this.#context.createBuffer(1, pcm.length, this.#context.sampleRate)
    buffer.copyToChannel(pcm, 0)
    return buffer
  }

  /** Starts a buffer playing into `into`, and says when it has finished. */
  #play(buffer, into) {
    const node = this.#context.createBufferSource()
    node.buffer = buffer
    node.connect(into)
    const ended = new Promise((resolve) => { node.onended = resolve })
    node.start()
    return { node, ended }
  }

  /**
   * The wait between the last sample leaving the graph and the radio being safe to unkey: the
   * configured tail, plus the output latency, because the graph is well ahead of the card.
   */
  #drain() {
    return sleep(this.txTailMs + Math.round((this.#context.outputLatency ?? 0) * 1000))
  }

  /**
   * Keys the radio, runs `play`, and unkeys - whatever `play` does or throws. One place, because
   * a frame and a test transmission are the same act as far as the PTT line is concerned, and
   * the failure that matters is a radio left keyed.
   */
  async #keyed(play) {
    this.#transmitting = true
    try {
      await this.ptt.key()
      await sleep(this.pttLeadMs)
      return await play()
    } finally {
      await this.ptt.unkey()
      this.#transmitting = false
      if (this.#handle) this.#exports.ResetCarrierState(this.#handle)
    }
  }
}
