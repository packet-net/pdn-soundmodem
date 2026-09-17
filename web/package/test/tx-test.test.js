// The two level controls and the transmitter test, against a fake Web Audio graph. Node's own
// test runner and no dependencies, because the package has none and is not about to acquire one.
//
// What is worth testing here is not the waveform - TestTone is C# and TestToneTests measures it -
// but everything this package decides on top of it: that a tone the modem will not send is
// refused rather than quietly moved, that the cap on a transmission is enforced here and not left
// to whatever a page put in its seconds box, that the radio is keyed before the audio and unkeyed
// after it whatever happens in between, that a stop fades instead of cutting, and that a test
// still waiting for a clear channel is withdrawn rather than keyed later when the channel frees.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { SoundModem, NoPtt } from '../src/modem.js'
import { SoundModemTransport } from '../src/transport.js'

const RATE = 48000

/** Everything the package asks of the wasm core, with the core's own constants in it. */
const fakeExports = () => ({
  Open: () => 7,
  DspRateOf: () => 1200,
  Close: () => {},
  Feed: () => {},
  TakeFrames: () => new Uint8Array(0),
  CarrierDetect: () => false,
  ChannelBusy: () => false,
  ResetCarrierState: () => {},
  Modulate: () => new Uint8Array(new Float32Array([0.8, -0.8, 0.8, -0.8]).buffer),
  TwoTonePairHz: () => Float64Array.of(700, 1900),
  BesselNullTonesHz: () => Float64Array.of(500, 999, 1248, 2079),
  BesselNullDeviationHz: (hz) => 2.405 * hz,
  // Flat rather than shaped: the raised-cosine envelope is TestTone's business and is measured in
  // C#. What matters here is the length, and that what goes in front of it is silence.
  RenderTestTone: (tones, peak, rate, seconds) =>
    new Uint8Array(new Float32Array(Math.round(rate * seconds)).fill(peak).buffer),
})

class FakeParam {
  constructor(value) { this.value = value; this.ramps = [] }
  setValueAtTime(value, at) { this.value = value; this.ramps.push(['hold', value, at]) }
  linearRampToValueAtTime(value, at) { this.value = value; this.ramps.push(['ramp', value, at]) }
}

class FakeNode {
  constructor() { this.outputs = []; this.inputs = [] }
  connect(to) { this.outputs.push(to); to.inputs.push(this); return to }
  disconnect() { this.outputs = [] }
}

class FakeGainNode extends FakeNode {
  constructor(_context, options) { super(); this.gain = new FakeParam(options?.gain ?? 1) }
}

class FakeAudioBuffer {
  constructor(channels, length, sampleRate) {
    this.numberOfChannels = channels
    this.length = length
    this.sampleRate = sampleRate
    this.duration = length / sampleRate
    this.data = new Float32Array(length)
  }

  copyToChannel(source, _channel, offset = 0) { this.data.set(source, offset) }
  getChannelData() { return this.data }
}

class FakeBufferSource extends FakeNode {
  constructor(context) { super(); this.context = context }

  start() {
    // An output device that has gone between the keyup and the first sample: rare, real, and the
    // one case where an unkey has to happen on a path nothing else walks.
    if (this.context.failNextStart) throw new Error('the output device has gone')
    this.context.log.push('audio on')
    this.context.playing = this
  }

  /** A real graph fires onended when it reaches the stop time; so does this, a tick later. */
  stop(when) {
    this.context.log.push('audio off')
    this.stoppedAt = when
    queueMicrotask(() => this.finish())
  }

  /** What the graph does when the buffer simply runs out. */
  finish() { this.onended?.() }
}

/** The context the modem most recently opened, so a test can drive its clock and its nodes. */
let context = null

class FakeAudioContext {
  constructor({ sampleRate }) {
    this.sampleRate = sampleRate
    this.currentTime = 0
    this.outputLatency = 0
    this.destination = new FakeNode()
    this.audioWorklet = { addModule: async () => {} }
    this.log = []
    this.playing = null
    context = this
  }

  createMediaStreamSource() { return new FakeNode() }
  createBuffer(channels, length, rate) { return new FakeAudioBuffer(channels, length, rate) }
  createBufferSource() { return new FakeBufferSource(this) }
  async resume() {}
  async close() {}
}

class FakeWorkletNode extends FakeNode {
  constructor(ctx) { super(); ctx.worklet = this; this.port = { onmessage: null, close() {} } }
}

globalThis.GainNode = FakeGainNode
globalThis.AudioContext = FakeAudioContext
globalThis.AudioWorkletNode = FakeWorkletNode
globalThis.navigator ??= {}
globalThis.navigator.mediaDevices ??= { getUserMedia: async () => ({ getTracks: () => [] }) }

/** A PTT that writes what it was asked to do into the same log the audio writes to. */
const loggingPtt = () => ({
  key: async () => { context.log.push('key') },
  unkey: async () => { context.log.push('unkey') },
  close: async () => {},
})

/** An open modem with the radio's waits taken out, so a test is not held up by settling time. */
async function openModem({ ptt = NoPtt, exports = fakeExports(), busy } = {}) {
  if (busy) exports.ChannelBusy = busy
  const modem = new SoundModem(exports)
  await modem.open({ mode: 'afsk1200', ptt, sampleRate: RATE })
  modem.pttLeadMs = 0
  modem.txTailMs = 0
  modem.txDelayMs = 100
  return modem
}

/** Lets queued microtasks and timers run, so an await chain can reach its next stop. */
const settle = (ms = 5) => new Promise((resolve) => setTimeout(resolve, ms))

// ---------------------------------------------------------------- what is about to go out

test('the description of a two-tone test reads like the line the daemon journals', async () => {
  const modem = await openModem()

  assert.equal(
    modem.describeTestTone({ twoTone: true, seconds: 5 }).text,
    'two-tone 700+1900 Hz, 5.0 s, peak level 0.80')
})

test('a single tone is described with the deviation its Bessel null calibrates', async () => {
  const modem = await openModem()
  const plan = modem.describeTestTone({ twoTone: false, toneHz: 1248, seconds: 5 })

  assert.equal(plan.text,
    'single tone 1248 Hz (FM Bessel null at 3.0 kHz deviation), 5.0 s, peak level 0.80')
  assert.deepEqual(plan.tones, [1248])
})

test('a tone below the audio band, or above Nyquist, is refused rather than moved', async () => {
  const modem = await openModem()

  assert.throws(() => modem.describeTestTone({ twoTone: false, toneHz: 20 }), /between 50 Hz/)
  assert.throws(() => modem.describeTestTone({ twoTone: false, toneHz: 24000 }), /24000 Hz/)
  assert.throws(() => modem.describeTestTone({ twoTone: false, toneHz: NaN }), /50 Hz/)
})

test('a length past the cap is capped here, and the line says it was', async () => {
  const modem = await openModem()
  const plan = modem.describeTestTone({ twoTone: true, seconds: 120 })

  assert.equal(plan.seconds, 30)
  assert.match(plan.text, /30\.0 s \(capped from 120\.0 s\)/)
})

test('the presets are the core\'s own tones, each with its null deviation', async () => {
  const modem = await openModem()

  assert.deepEqual(modem.twoTonePairHz, [700, 1900])
  assert.deepEqual(modem.besselNullPresets.map((p) => p.toneHz), [500, 999, 1248, 2079])
  assert.equal(Math.round(modem.besselNullPresets[3].deviationHz), 5000)
})

// ---------------------------------------------------------------- the keyup

test('a test keys the radio, puts the tones out, and unkeys after them', async () => {
  const modem = await openModem({ ptt: loggingPtt() })

  const running = modem.testTone({ twoTone: true, seconds: 2 })
  await settle()
  assert.equal(modem.transmitting, true, 'the TX lamp is lit for the whole keyup')
  assert.equal(modem.testToneRunning, true)
  assert.deepEqual(context.log, ['key', 'audio on'])

  context.playing.finish()
  const done = await running

  assert.deepEqual(context.log, ['key', 'audio on', 'unkey'])
  assert.equal(modem.transmitting, false)
  assert.equal(modem.testToneRunning, false)
  assert.equal(done.stopped, false)
  assert.match(done.text, /done, 2\.0 s on air/)
})

test('TXDELAY in front of a test is silence, so the radio is keyed and quiet while it settles', async () => {
  const modem = await openModem()

  const running = modem.testTone({ twoTone: true, seconds: 1 })
  await settle()
  const audio = context.playing.buffer.data
  const lead = Math.round((modem.txDelayMs / 1000) * RATE)

  assert.equal(audio.length, lead + RATE, 'the lead is added to the burst, not taken out of it')
  assert.equal(audio.slice(0, lead).every((s) => s === 0), true, 'the lead is silent')
  assert.ok(Math.abs(audio[lead] - 0.8) < 1e-6, 'the tones start at the transmit peak the daemon uses')

  context.playing.finish()
  await running
})

test('the tones go out through the transmit gain, so a test measures what a frame gets', async () => {
  const modem = await openModem()
  modem.txGainDb = -12

  const running = modem.testTone({ twoTone: true, seconds: 1 })
  await settle()
  // burst -> its own fade -> the transmit gain -> the output. Every hop matters: a test that
  // skipped the gain would measure a level no frame is ever sent at.
  const fade = context.playing.outputs[0]
  const txGain = fade.outputs[0]
  assert.equal(Math.round(txGain.gain.value * 1000), 251, '-12 dB is a quarter of the amplitude')
  assert.equal(txGain.outputs[0], context.destination)

  context.playing.finish()
  await running
})

test('a radio left keyed is the failure that matters, so a throw still unkeys', async () => {
  const modem = await openModem({ ptt: loggingPtt() })
  context.failNextStart = true

  await assert.rejects(() => modem.testTone({ twoTone: true, seconds: 1 }), /output device/)

  assert.deepEqual(context.log, ['key', 'unkey'])
  assert.equal(modem.transmitting, false)
  assert.equal(modem.testToneRunning, false, 'and the next test is not refused as already running')
})

test('a second test is refused while one is running rather than keying over it', async () => {
  const modem = await openModem()
  const running = modem.testTone({ twoTone: true, seconds: 5 })
  await settle()

  await assert.rejects(
    () => modem.testTone({ twoTone: true, seconds: 5 }), /already running/)

  context.playing.finish()
  await running
})

// ---------------------------------------------------------------- stopping one

test('a stop fades the burst over its own edge time instead of cutting it', async () => {
  const modem = await openModem()
  const running = modem.testTone({ twoTone: true, seconds: 30 })
  await settle()

  const fade = context.playing.outputs[0]
  context.currentTime = 4.6
  modem.stopTestTone()

  assert.equal(fade.gain.ramps.length, 2)
  assert.deepEqual(fade.gain.ramps[0], ['hold', 1, 4.6], 'held at where it was, then taken down')
  assert.equal(fade.gain.ramps[1][1], 0)
  assert.ok(Math.abs(fade.gain.ramps[1][2] - 4.605) < 1e-9, 'over the 5 ms TestTone shapes with')
  // Stopped at the END of the fade: a stop time inside the ramp truncates it and puts back the
  // hard edge the fade is there to avoid.
  assert.equal(context.playing.stoppedAt, fade.gain.ramps[1][2])

  const done = await running
  assert.equal(done.stopped, true)
  // Keyed at 0, tones from 0.1 after the TXDELAY lead, stopped at 4.6.
  assert.equal(done.onAir.toFixed(1), '4.5')
  assert.match(done.text, /stopped after 4\.5 s/)
})

test('stopping when nothing is running does nothing at all', async () => {
  const modem = await openModem()

  assert.doesNotThrow(() => modem.stopTestTone())
  assert.equal(context.log.length, 0)
})

// ---------------------------------------------------------------- contention, in the transport

test('a test waits for a clear channel like anything else that keys', async () => {
  let busy = true
  const modem = await openModem({ ptt: loggingPtt(), busy: () => busy })
  const transport = new SoundModemTransport(modem, { slotTimeMs: 5, persistence: 255 })
  await transport.start(() => {})

  const running = transport.testTone({ twoTone: true, seconds: 1 })
  await settle(30)
  assert.deepEqual(context.log, [], 'nothing is keyed while the channel is busy')

  busy = false
  await settle(30)
  assert.deepEqual(context.log, ['key', 'audio on'])

  context.playing.finish()
  await running
})

test('a test given up on while the channel is busy is withdrawn, not keyed later', async () => {
  const modem = await openModem({ ptt: loggingPtt(), busy: () => true })
  const transport = new SoundModemTransport(modem, { slotTimeMs: 5 })
  await transport.start(() => {})

  const running = transport.testTone({ twoTone: true, seconds: 1 })
  await settle(20)
  transport.stopTestTone()

  await assert.rejects(() => running, /stopped before it reached the air/)
  await settle(30)
  assert.deepEqual(context.log, [], 'the radio was never keyed')
})

test('a channel that never clears withdraws the test rather than saying running for ever', async () => {
  const modem = await openModem({ ptt: loggingPtt(), busy: () => true })
  const transport = new SoundModemTransport(modem, { slotTimeMs: 5, channelWaitMs: 20 })
  await transport.start(() => {})

  await assert.rejects(
    () => transport.testTone({ twoTone: true, seconds: 1 }),
    /the channel did not clear within 0 s/)
  await settle(30)
  assert.deepEqual(context.log, [])
})

// ---------------------------------------------------------------- the two level controls

test('the gains sit either side of the core: receive before it, transmit after it', async () => {
  const modem = await openModem()
  modem.rxGainDb = 6
  modem.txGainDb = -6

  // The receive gain is the last thing the samples pass through before the worklet that feeds
  // the core, which is also where the meter measures them - so the bar moves when the slider
  // does and not only when the signal does.
  const rxGain = context.worklet.inputs[0]
  assert.equal(Math.round(rxGain.gain.value * 1000), 1995, '+6 dB is twice the amplitude')

  // The transmit gain is the last thing before the output device, so everything the modem sends
  // goes through it.
  const txGain = context.destination.inputs[0]
  assert.equal(Math.round(txGain.gain.value * 1000), 501, '-6 dB is half the amplitude')
})

test('a gain outside the range a level can be is refused, not clamped', async () => {
  const modem = await openModem()

  assert.throws(() => { modem.rxGainDb = 40 }, /receive gain must be -60 to 30 dB/)
  assert.throws(() => { modem.txGainDb = -80 }, /transmit gain must be -60 to 30 dB/)
  assert.throws(() => { modem.rxGainDb = NaN }, /receive gain/)
  assert.equal(modem.rxGainDb, 0, 'a refused level leaves the old one in place')
})

test('a gain set before the modem is opened is applied when the graph is built', async () => {
  const modem = new SoundModem(fakeExports())
  modem.rxGainDb = 12
  modem.txGainDb = -20
  await modem.open({ mode: 'afsk1200', sampleRate: RATE })

  // 12 dB is a factor of about 3.98; -20 dB is exactly a tenth. Levels can therefore be dialled
  // in before the radio is opened, which is the order an operator restoring settings works in.
  assert.equal(context.worklet.inputs[0].gain.value.toFixed(3), '3.981')
  assert.equal(context.destination.inputs[0].gain.value.toFixed(3), '0.100')
})

test('the meter reads what the modem is given, in dBFS, and flags a block that clipped', async () => {
  const modem = await openModem()

  context.worklet.port.onmessage({ data: { block: new Float32Array(4), peak: 0.5, rms: 0.25 } })
  assert.equal(modem.inputLevel.peak.toFixed(1), '-6.0')
  assert.equal(modem.inputLevel.rms.toFixed(1), '-12.0')
  assert.equal(modem.inputLevel.clip, false)

  context.worklet.port.onmessage({ data: { block: new Float32Array(4), peak: 1, rms: 0.7 } })
  assert.equal(modem.inputLevel.clip, true)

  // Silence reads as a floor rather than as -Infinity, which no meter can draw.
  context.worklet.port.onmessage({ data: { block: new Float32Array(4), peak: 0, rms: 0 } })
  assert.equal(modem.inputLevel.peak, -120)
})
