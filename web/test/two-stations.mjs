// Two complete AX.25 stations connected by a piece of wire.
//
// Everything here is the shipping code: the demodulator and modulator are pdn-soundmodem's
// own, compiled to WebAssembly; the link layer is @packet-net/ax25 driving the SDL state
// machine generated from the AX.25 v2.2 spec; the glue is sm-transport.js exactly as the
// browser page uses it. The only simulated part is the audio device - instead of two sound
// cards and two radios, one station's transmitted samples become the other's received
// samples.
//
//   node two-stations.mjs [mode]
import { Ax25Listener, Callsign } from '@packet-net/ax25'
import { SoundModemTransport } from '../package/src/transport.js'
import { dotnet } from '../package/_framework/dotnet.js'

const MODE = process.argv[2] ?? 'afsk1200'
const AUDIO_RATE = 48000
const TICK_MS = 20
const TICK_SAMPLES = (AUDIO_RATE * TICK_MS) / 1000
/** Band noise. A receiver is never silent, and a modem fed digital zero has no noise floor. */
const NOISE = 0.002

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms))
const runtime = await dotnet.withDiagnosticTracing(false).create()
const Modem = (await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName)).Modem

/**
 * The channel, and the reason it runs on a clock.
 *
 * Audio has to arrive in real time or the station's own sense of time comes apart: a busy
 * detector decays over audio, not over wall clock, so a burst delivered instantly leaves the
 * receiver holding busy for as long as the burst was - and if the idle between bursts is
 * paced but the bursts are not, a station can end up permanently behind its own channel.
 * The first version of this harness did exactly that, and the slowest mode (300 baud, whose
 * busy holds for 3.5 s after a burst) was the one that showed it.
 */
class Channel {
  constructor() {
    this.stations = []
    this.queues = new Map()
  }

  add(station) {
    this.stations.push(station)
    this.queues.set(station, [])
  }

  /** Queues a transmission for everyone else - one keyup, heard by every other station. */
  deliver(from, audio) {
    for (const station of this.stations) {
      if (station === from) continue
      const queue = this.queues.get(station)
      for (let at = 0; at < audio.length; at += TICK_SAMPLES) {
        queue.push(audio.subarray(at, Math.min(at + TICK_SAMPLES, audio.length)))
      }
    }
  }

  #noise() {
    const block = new Float32Array(TICK_SAMPLES)
    for (let i = 0; i < TICK_SAMPLES; i++) block[i] = (Math.random() - 0.5) * NOISE
    return block
  }

  start() {
    this.timer = setInterval(() => {
      for (const station of this.stations) {
        const queued = this.queues.get(station).shift()
        station.hear(queued ?? this.#noise())
      }
    }, TICK_MS)
  }

  stop() { clearInterval(this.timer) }
}

/**
 * A SoundModem with a wire where its sound card should be. The same surface the browser
 * class exposes, so sm-transport.js cannot tell the difference.
 */
class WireModem {
  #frameListeners = new Set()

  constructor(name, channel) {
    this.name = name
    this.channel = channel
    this.handle = Modem.Open(MODE, AUDIO_RATE)
    this.dspRate = Modem.DspRateOf(this.handle)
    this.transmitting = false
    channel.add(this)
  }

  onFrame(callback) {
    this.#frameListeners.add(callback)
    return () => this.#frameListeners.delete(callback)
  }

  get carrierDetect() { return !this.transmitting && Modem.CarrierDetect(this.handle) }
  channelBusy() { return this.transmitting || Modem.ChannelBusy(this.handle) }

  /** One tick of received audio. Keying makes a station deaf, so a keyed one hears nothing. */
  hear(audio) {
    if (this.transmitting) return
    Modem.Feed(this.handle, new Uint8Array(audio.buffer, audio.byteOffset, audio.byteLength))
    const packed = Modem.TakeFrames(this.handle)
    for (let p = 0; p < packed.length;) {
      const length = packed[p] | (packed[p + 1] << 8)
      const frame = packed.subarray(p + 2, p + 2 + length)
      for (const listener of this.#frameListeners) listener(frame)
      p += 2 + length
    }
  }

  async transmit(ax25Frame, txDelayMs) {
    const audio = new Float32Array(Modem.Modulate(this.handle, ax25Frame, txDelayMs).buffer)
    this.transmitting = true
    try {
      this.channel.deliver(this, audio)
      // The keyup lasts as long as the burst: this is a radio, not a packet queue.
      await sleep((audio.length / AUDIO_RATE) * 1000)
    } finally {
      this.transmitting = false
      Modem.ResetCarrierState(this.handle)
    }
  }
}

const channel = new Channel()
const stations = {}
for (const call of ['M0LTE-1', 'GB7RDG-1']) {
  const modem = new WireModem(call, channel)
  // p = 1: this test is about the DSP and the link layer, not about how long a p-persistence
  // roll takes. A real station leaves the daemon's 63.
  const transport = new SoundModemTransport(modem, { persistence: 255, txDelayMs: 300 })
  // T1 has to fit the mode. The AX.25 default of 6 s is a VHF number, and at 300 baud a
  // frame plus TXDELAY plus the answer does not fit inside it - the link then retries a frame
  // that was never lost. A station picks T1 from the mode's throughput; so does this.
  const listener = new Ax25Listener(transport, {
    myCall: call,
    carrierSense: transport,
    t1Ms: Number(process.env.SM_T1_MS ?? (MODE.includes('300') ? 30000 : 10000)),
  })
  await listener.start()
  stations[call] = { modem, transport, listener }
}
channel.start()

console.log(`mode ${MODE}, audio ${AUDIO_RATE} Hz, chain ${stations['M0LTE-1'].modem.dspRate} Hz`)

let failures = 0
const check = (what, ok) => { console.log(`${ok ? 'PASS' : 'FAIL'}  ${what}`); if (!ok) failures++ }
const within = (ms, promise) => Promise.race([promise, sleep(ms).then(() => null)])

// 1. A connectionless frame - the simplest thing a station can put on the air.
const heard = new Promise((resolve) => { stations['GB7RDG-1'].modem.onFrame(resolve) })
await stations['M0LTE-1'].listener.sendUi(
  Callsign.parse('TEST'), new TextEncoder().encode('CQ from the browser'))
const uiBytes = await within(30000, heard)
check('UI frame crosses the wire', uiBytes !== null)
if (uiBytes) {
  const text = new TextDecoder().decode(uiBytes.subarray(16))
  check(`  payload survives ("${text}")`, text === 'CQ from the browser')
}

// 2. A connected-mode session: SABM(E), UA, I frames, RR, DISC, UA - every frame of it
//    modulated to audio and demodulated back on the other side.
stations['GB7RDG-1'].listener.onSessionAccepted((session) => {
  session.onData(async (chunk) => {
    const text = new TextDecoder().decode(chunk)
    console.log(`      GB7RDG-1 received: ${JSON.stringify(text)}`)
    await session.write(new TextEncoder().encode(`you said: ${text}`))
  })
})

const outbound = await Promise.race([
  stations['M0LTE-1'].listener.connect('GB7RDG-1'),
  sleep(60000).then(() => new Error('connect timed out')),
])

check('connect completes over the air', !(outbound instanceof Error))
if (!(outbound instanceof Error)) {
  const reply = new Promise((resolve) => outbound.onData((c) => resolve(new TextDecoder().decode(c))))
  await outbound.write(new TextEncoder().encode('hello'))
  const answer = await within(120000, reply)
  check(`round trip through the link ("${answer}")`, answer === 'you said: hello')
  await outbound.disconnect()
  check('disconnect completes', true)
}

channel.stop()
for (const { listener, modem } of Object.values(stations)) {
  await listener.dispose()
  Modem.Close(modem.handle)
}

console.log(failures === 0 ? '\nall checks passed' : `\n${failures} check(s) failed`)
process.exit(failures === 0 ? 0 : 1)
