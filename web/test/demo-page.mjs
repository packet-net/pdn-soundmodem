// Drives web/demo/index.html's real script - the shipping text, in real V8, against the real
// package - and reports what it managed to do as JSON on stdout. verify.sh reads that back.
//
// This exists for the reason the station page's probe exists: everything else here tests the
// modem, and that leaves a gap the size of the whole browser. A mistyped element id, a handler
// bound to the wrong event, a slider that moves and reaches nothing - none of it is visible to a
// decode test, and all of it is ordinary JavaScript that Node runs exactly as a browser does. The
// DOM shim below costs nothing in fidelity because nothing that matters here is a pixel.
//
// Usage: node demo-page.mjs [old] [--json]
//   `old`    strips the newest package methods off the prototype first, to prove the page degrades
//            rather than throwing when the CDN is still serving a release behind.
//   --json   prints everything it read off the page as well, for working out why an assert failed.
import assert from 'node:assert/strict'
import { readFileSync, writeFileSync, unlinkSync } from 'node:fs'
import { fileURLToPath, pathToFileURL } from 'node:url'
import path from 'node:path'

const here = path.dirname(fileURLToPath(import.meta.url))
const demoDir = path.join(here, '..', 'demo')
const html = readFileSync(path.join(demoDir, 'index.html'), 'utf8')
const pretendOld = process.argv.includes('old')
const asJson = process.argv.includes('--json')

// ---------------------------------------------------------------- the DOM the page asks for
const noop = () => {}

function classListFor(node) {
  const parse = () => String(node.className || '').split(/\s+/).filter(Boolean)
  const write = (list) => { node.className = list.join(' ') }
  return {
    add(...names) { const l = parse(); for (const n of names) if (!l.includes(n)) l.push(n); write(l) },
    remove(...names) { write(parse().filter((n) => !names.includes(n))) },
    contains: (name) => parse().includes(name),
    toggle(name, force) {
      const on = force === undefined ? !parse().includes(name) : !!force
      if (on) this.add(name); else this.remove(name)
      return on
    },
  }
}

// What the markup gives each element before any script runs: its value, and whether it ships
// hidden or disabled. Without this a range input reads "" rather than its value attribute, and
// the page's own parseFloat of it is NaN - which is a defect in the shim, not in the page, and
// exactly the kind of thing that makes a probe lie in the reassuring direction.
const fromMarkup = new Map()
for (const tag of html.matchAll(/<(input|select|button|textarea)\s([^>]*)>/g)) {
  const id = /\bid="([^"]+)"/.exec(tag[2])
  if (!id) continue
  const value = /\bvalue="([^"]*)"/.exec(tag[2])
  fromMarkup.set(id[1], {
    value: value ? value[1] : '',
    hidden: /\bhidden(\s|=|$)/.test(tag[2]),
    disabled: /\bdisabled(\s|=|$)/.test(tag[2]),
  })
}

const byId = new Map()

function makeElement(tag = 'div', id = '') {
  const node = {
    tagName: tag.toUpperCase(), id, className: '', textContent: '', innerHTML: '',
    value: '', disabled: false, hidden: false, title: '', style: {}, children: [],
    scrollTop: 0, scrollHeight: 0,
    append(...nodes) { for (const n of nodes) this.children.push(n) },
    appendChild(n) { this.children.push(n); return n },
    remove() {},
    setAttribute: noop, getAttribute: () => null,
    querySelector(selector) {
      const want = /option\[value="([^"]+)"\]/.exec(selector)
      return want ? this.children.find((c) => c.value === want[1]) ?? makeElement('option') : null
    },
    get firstElementChild() { return this.children[0] ?? null },
    get childElementCount() { return this.children.length },
    click() { this.onclick?.({ preventDefault: noop }) },
    /** What a browser does when the operator drags a slider: set, then fire. */
    drag(to) { this.value = String(to); this.oninput?.() },
    /** And when they pick from a select. */
    pick(to) { this.value = String(to); this.onchange?.() },
  }
  node.classList = classListFor(node)
  return node
}

globalThis.document = {
  getElementById(id) {
    if (!byId.has(id)) byId.set(id, Object.assign(makeElement('div', id), fromMarkup.get(id) ?? {}))
    return byId.get(id)
  },
  createElement: (tag) => makeElement(tag),
}
const $ = (id) => document.getElementById(id)

globalThis.location = { search: '?local' }
globalThis.performance ??= { now: () => Date.now() }

// ---------------------------------------------------------------- the audio graph
// The same shape the package's own tests use: nothing renders, but every connection, every gain
// and every keying call is recorded, so what the page asked the radio to do can be read back.
const log = []
let context = null

class FakeParam {
  constructor(value) { this.value = value }
  setValueAtTime(value) { this.value = value }
  linearRampToValueAtTime(value) { this.value = value }
}

class FakeNode {
  constructor() { this.outputs = []; this.inputs = [] }
  connect(to) { this.outputs.push(to); to.inputs.push(this); return to }
  disconnect() { this.outputs = [] }
}

globalThis.GainNode = class extends FakeNode {
  constructor(_ctx, options) { super(); this.gain = new FakeParam(options?.gain ?? 1) }
}

globalThis.AudioContext = class {
  constructor({ sampleRate }) {
    this.sampleRate = sampleRate
    this.currentTime = 0
    this.outputLatency = 0
    this.destination = new FakeNode()
    this.audioWorklet = { addModule: async () => {} }
    this.playing = null
    context = this
  }

  createMediaStreamSource() { return new FakeNode() }

  createBuffer(channels, length, rate) {
    return {
      numberOfChannels: channels, length, sampleRate: rate, duration: length / rate,
      data: new Float32Array(length),
      copyToChannel(source, _c, offset = 0) { this.data.set(source, offset) },
    }
  }

  createBufferSource() {
    const context_ = this
    const node = new FakeNode()
    node.start = () => { log.push('audio on'); context_.playing = node }
    node.stop = () => { log.push('audio off'); queueMicrotask(() => node.onended?.()) }
    node.finish = () => node.onended?.()
    return node
  }

  async resume() {}
  async close() {}
}

globalThis.AudioWorkletNode = class extends FakeNode {
  constructor(ctx) { super(); ctx.worklet = this; this.port = { onmessage: null, close: noop } }
}

// Node ships a read-only navigator of its own, so the shim is defined over it rather than
// assigned to it.
Object.defineProperty(globalThis, 'navigator', { configurable: true, writable: true, value: {
  mediaDevices: {
    getUserMedia: async () => ({ getTracks: () => [] }),
    enumerateDevices: async () => [
      { kind: 'audioinput', deviceId: 'mic-1', label: 'USB PnP Sound Device' },
      { kind: 'audiooutput', deviceId: 'spk-1', label: 'USB PnP Sound Device' },
    ],
  },
  // No Web Serial and no WebHID, which is the honest state of this process and is also what the
  // page has to cope with in Firefox. PTT stays on "none" throughout.
} })

// ---------------------------------------------------------------- run the page's own script
// ?local makes the page import the package from this working tree; the link layer is rewritten to
// the copy installed here, because esm.sh is not reachable from a test run and is not the subject.
const ax25 = pathToFileURL(
  path.join(here, 'node_modules', '@packet-net', 'ax25', 'dist', 'index.js')).href
const script = html
  .slice(html.indexOf('<script type="module">') + 22, html.lastIndexOf('</script>'))
  .replace('esm(PKG.ax25)', JSON.stringify(ax25))

if (pretendOld) {
  // Stand in for a CDN still serving the release before this one: the page must disable what it
  // cannot drive and say which version is wanted, not throw on the first click.
  const { SoundModem } = await import('../package/src/index.js')
  for (const name of ['testTone', 'stopTestTone', 'describeTestTone', 'besselNullDeviationHz']) {
    delete SoundModem.prototype[name]
  }
  for (const name of ['rxGainDb', 'txGainDb', 'inputLevel', 'twoTonePairHz', 'besselNullPresets']) {
    delete SoundModem.prototype[name]
  }
}

const scratch = path.join(demoDir, `.demo-probe-${process.pid}.mjs`)
writeFileSync(scratch, script)
try {
  await import(pathToFileURL(scratch).href)
} finally {
  unlinkSync(scratch)
}

// ---------------------------------------------------------------- drive it
const settle = (ms = 20) => new Promise((resolve) => setTimeout(resolve, ms))

/**
 * Waits for something the page is doing in its own time. A test transmission contends for the
 * channel first - p-persistent CSMA, a slot at a time - and then waits out the PTT lead, so how
 * long it takes to reach the air is a roll of the dice rather than a fixed number of milliseconds.
 */
async function waitFor(what, why, ms = 4000) {
  for (const deadline = Date.now() + ms; Date.now() < deadline;) {
    if (what()) return
    await settle(10)
  }
  throw new Error(`gave up waiting for ${why}`)
}
const report = { old: pretendOld }

await settle()
report.modes = $('mode').children.length
report.mode = $('mode').value
report.banner = $('banner').textContent
report.inputs = $('input').children.map((o) => o.textContent)

// Levels, before the modem is even open: the package keeps the dB and applies it when the graph
// is built, so a station can be set up and then started.
$('rxgain').drag(6)
$('txgain').drag(-12)
report.rxRead = $('rxgainRead').textContent
report.txRead = $('txgainRead').textContent
report.levelNote = $('levelNote').textContent
report.levelsDisabled = $('rxgain').disabled && $('txgain').disabled

// The TX test menu, built from the core's own tones.
report.txTestKinds = $('ttKind').children.map((o) => `${o.value}=${o.textContent}`)
$('ttKind').pick('tone:1248')
report.presetSays = $('ttWhat').textContent
report.hzHiddenForPreset = $('ttHz').hidden
$('ttKind').pick('tone')
report.hzShownForFreeTone = !$('ttHz').hidden
$('ttHz').value = '1500'
$('ttHz').oninput?.()
report.freeToneSays = $('ttWhat').textContent

$('start').click()
await settle(60)
report.startLog = $('log').children.map((c) => c.textContent).filter((t) => /running|PTT|core loaded/.test(t))
report.txTestEnabled = !$('ttGo').disabled

if (!pretendOld) {
  // The gains reached the graph, either side of the core.
  report.rxGainLinear = +context.worklet.inputs[0].gain.value.toFixed(3)
  report.txGainLinear = +context.destination.inputs[0].gain.value.toFixed(3)

  // A block of audio arrives; the meter reads it.
  context.worklet.port.onmessage({ data: { block: new Float32Array(8), peak: 0.125, rms: 0.05 } })
  await settle(150)
  report.meterRead = $('meterRead').textContent
  report.meterWidth = $('meterBar').style.width
  report.meterQuiet = $('meterBar').className

  // And a clipped one.
  context.worklet.port.onmessage({ data: { block: new Float32Array(8), peak: 1, rms: 0.8 } })
  await settle(150)
  report.clipLit = $('clip').className

  // A two-tone test, run to its end.
  $('ttKind').pick('two')
  $('ttSecs').value = '3'
  $('ttGo').click()
  await settle(20)
  report.whileRunning = { text: $('ttGo').textContent, amber: $('ttGo').className, says: $('ttWhat').textContent }
  await waitFor(() => context.playing, 'the test to reach the air')
  report.keyed = [...log]
  report.burstSeconds = +(context.playing.buffer.duration).toFixed(2)
  context.playing.finish()
  await waitFor(() => $('ttGo').textContent === 'Send', 'the button to come back')
  report.afterRunning = { text: $('ttGo').textContent, amber: $('ttGo').className, says: $('ttWhat').textContent }

  // And one stopped part way through.
  log.length = 0
  context.playing = null
  $('ttSecs').value = '30'
  $('ttGo').click()
  await waitFor(() => context.playing, 'the second test to reach the air')
  context.currentTime = 2.4
  $('ttGo').click()
  await waitFor(() => $('ttGo').textContent === 'Send', 'the stop to take effect')
  report.stopped = $('ttWhat').textContent

  // A tone the modem will not send is refused before anything is keyed.
  log.length = 0
  context.playing = null
  $('ttKind').pick('tone')
  $('ttHz').value = '20'
  $('ttGo').click()
  await settle(200)
  report.refused = $('ttWhat').textContent
  report.refusedKeyed = [...log]
} else {
  report.txTestSays = $('ttWhat').textContent
}

if (asJson) console.log(JSON.stringify(report, null, 2))

// ---------------------------------------------------------------- what all that has to add up to
assert.ok(report.modes > 30, 'the mode list is populated from the core')
assert.equal(report.mode, 'afsk1200')
assert.match(report.banner, /working tree, \d+ modes, ready/)
assert.deepEqual(report.inputs, ['USB PnP Sound Device'], 'devices appear once access is granted')
assert.ok(report.startLog.some((line) => /afsk1200 running/.test(line)), 'the modem opened')

if (pretendOld) {
  // A CDN one release behind: the controls are dead and say which version is wanted, and nothing
  // on the page threw on the way to that state.
  assert.equal(report.levelsDisabled, true, 'the sliders are disabled, not merely inert')
  assert.match(report.levelNote, /Levels needs a newer soundmodem than/)
  assert.match(report.txTestSays, /The TX test needs a newer soundmodem than/)
  assert.equal(report.txTestEnabled, false, 'and Send cannot be clicked')
  console.log('demo page, against a package one release behind: '
    + 'both controls disabled and saying so, nothing thrown')
} else {
  // Levels, set before the modem was opened and applied when the graph was built.
  assert.equal(report.rxRead, '+6.0 dB')
  assert.equal(report.txRead, '-12.0 dB')
  assert.equal(report.rxGainLinear, 1.995, '+6 dB reached the node before the core')
  assert.equal(report.txGainLinear, 0.251, '-12 dB reached the node before the output')
  assert.match(report.levelNote, /peak in the green, -18 to -9 dBFS/)

  // The meter, on a block that is in the target zone and then on one that clipped.
  assert.equal(report.meterRead, '-18.1 dBFS')
  assert.equal(report.meterQuiet, '', 'a signal in the zone is neither quiet nor hot')
  assert.equal(report.clipLit, 'lit', 'and a clip latches')

  // The TX test menu, built from the core's own tones rather than from numbers typed in here.
  assert.deepEqual(report.txTestKinds, [
    'two=Two tone 700+1900',
    'tone=One tone',
    'tone:500=500 Hz -> FM 1.2 kHz dev',
    'tone:999=999 Hz -> FM 2.4 kHz dev',
    'tone:1248=1248 Hz -> FM 3.0 kHz dev',
    'tone:2079=2079 Hz -> FM 5.0 kHz dev',
  ])
  assert.equal(report.presetSays, 'FM null at 3.00 kHz deviation')
  assert.equal(report.hzHiddenForPreset, true, 'a preset carries its own frequency')
  assert.equal(report.hzShownForFreeTone, true)
  assert.equal(report.freeToneSays, 'FM null at 3.61 kHz deviation')

  // A test run to its end.
  assert.equal(report.whileRunning.text, 'Stop')
  assert.equal(report.whileRunning.amber, 'on')
  assert.equal(report.whileRunning.says, 'two-tone 700+1900 Hz, 3.0 s, peak level 0.80')
  assert.deepEqual(report.keyed, ['audio on'])
  assert.equal(report.burstSeconds, 3.3, '3 s of tone behind 300 ms of TXDELAY silence')
  assert.equal(report.afterRunning.text, 'Send')
  assert.equal(report.afterRunning.amber, '')
  assert.match(report.afterRunning.says, /done, 3\.0 s on air$/)

  // And one stopped part way through, which says how much of it went out rather than how much
  // was asked for.
  assert.match(report.stopped, /stopped after 2\.\d s$/)

  // A tone the modem will not send is refused before anything is keyed.
  assert.match(report.refused, /a test tone must be between 50 Hz/)
  assert.deepEqual(report.refusedKeyed, [], 'and the radio was not keyed to find that out')

  console.log(`demo page: ${report.modes} modes, levels reach the graph either side of the core, `
    + 'meter reads and latches a clip, and the TX test keys, stops and refuses as it should')
}

// setInterval(paint) keeps the page's clock running, as it does in a tab, so say when to stop.
process.exit(0)
