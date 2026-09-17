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
//   (no args) loads the page twice against one browser store: once with nothing remembered, where
//            it drives every control and a whole connected-mode session, and once again to prove
//            the station it set up comes back.
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
  const placeholder = /\bplaceholder="([^"]*)"/.exec(tag[2])
  fromMarkup.set(id[1], {
    // The real tag, because the page asks: a remembered value that no longer names an option
    // reads back as "" on a select and on nothing else, and that check has to be exercised.
    tagName: tag[1].toUpperCase(),
    value: value ? value[1] : '',
    placeholder: placeholder ? placeholder[1] : '',
    hidden: /\bhidden(\s|=|$)/.test(tag[2]),
    disabled: /\bdisabled(\s|=|$)/.test(tag[2]),
  })
}

let byId = new Map()

// One browser, one store, across both loads of the page - which is the whole point: what the
// first load remembers is what the second one has to come back with.
const stored = new Map()
Object.defineProperty(globalThis, 'localStorage', { configurable: true, writable: true, value: {
  getItem: (k) => (stored.has(k) ? stored.get(k) : null),
  setItem: (k, v) => stored.set(k, String(v)),
  removeItem: (k) => stored.delete(k),
  clear: () => stored.clear(),
} })

function makeElement(tag = 'div', id = '') {
  const node = {
    tagName: tag.toUpperCase(), id, className: '', textContent: '', innerHTML: '',
    value: '', disabled: false, hidden: false, title: '', placeholder: '', style: {}, children: [],
    scrollTop: 0, scrollHeight: 0, listeners: {},
    append(...nodes) { for (const n of nodes) this.children.push(n) },
    appendChild(n) { this.children.push(n); return n },
    remove() {},
    focus: noop,
    setAttribute: noop, getAttribute: () => null,
    // Real, because the page saves what you changed through these while the on* properties are
    // claimed by what the control actually does. A shim that dropped them would lose every write
    // to the browser store and the restore test would pass against nothing.
    addEventListener(type, fn) { (this.listeners[type] ||= []).push(fn) },
    removeEventListener(type, fn) {
      const at = (this.listeners[type] || []).indexOf(fn)
      if (at >= 0) this.listeners[type].splice(at, 1)
    },
    fire(type, event = {}) {
      this[`on${type}`]?.(event)
      for (const fn of this.listeners[type] || []) fn(event)
    },
    querySelector(selector) {
      const want = /option\[value="([^"]+)"\]/.exec(selector)
      return want ? this.children.find((c) => c.value === want[1]) ?? makeElement('option') : null
    },
    get firstElementChild() { return this.children[0] ?? null },
    get childElementCount() { return this.children.length },
    click() { this.onclick?.({ preventDefault: noop }) },
    /** What a browser does when the operator drags a slider: set, then fire. */
    drag(to) { this.value = String(to); this.fire('input') },
    /** And when they pick from a select, or commit a text box. */
    pick(to) { this.value = String(to); this.fire('change') },
    /** And when they type a line and press a key. */
    type(text, key = 'Enter') { this.value = text; this.fire('input'); return this.onkeydown?.({ key }) },
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

// A serial interface the browser has already been granted. requestPort is the picker and it
// prompts; getPorts lists what has been granted already and does not - and that difference is
// the whole of the remembered-interface feature.
const fakePort = {
  opens: 0,
  signals: [],
  getInfo: () => ({ usbVendorId: 0x0403, usbProductId: 0x6001 }),
  async open() { this.opens++ },
  async setSignals(s) { this.signals.push(s) },
  async close() {},
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
  serial: {
    requestPort: async () => fakePort,
    getPorts: async () => [fakePort],
  },
  // No WebHID, which is the honest state of this process and is what the page has to cope with
  // in Firefox: the CM108 option is there and simply cannot be picked.
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

// A connected-mode session, without one. The AX.25 stack is somebody else's and two-stations.mjs
// already drives it over this modem for real; what is untested until here is the PAGE's half -
// that Enter writes the line, that D tears the link down, and that the three controls follow the
// session whichever end started it and whenever it goes away. So the listener's connect is
// replaced with one that hands back something that records what it was asked to do.
let established = null
class FakeSession {
  constructor(to) {
    this.to = to
    this.written = []
    this.disconnected = false
    established = this
  }

  onData(callback) { this.deliver = callback }
  onDisconnected(callback) { this.ended = callback }
  async write(bytes) { this.written.push(new TextDecoder().decode(bytes)) }
  async disconnect() { this.disconnected = true; this.ended?.() }
  /** The peer hanging up, or the retry limit running out: the page must react the same way. */
  dropped() { this.ended?.() }
}

const { Ax25Listener } = await import(ax25)
const realConnect = Ax25Listener.prototype.connect
Ax25Listener.prototype.connect = async function connect(to) {
  if (String(to).startsWith('NOPE')) throw new Error('retry limit reached')
  return new FakeSession(String(to))
}
let acceptInbound = null
const realOnSessionAccepted = Ax25Listener.prototype.onSessionAccepted
Ax25Listener.prototype.onSessionAccepted = function onSessionAccepted(callback) {
  acceptInbound = callback
  return realOnSessionAccepted.call(this, callback)
}

/** Loads the page into a fresh DOM. The browser store is deliberately NOT cleared between loads. */
let loads = 0
async function loadPage() {
  byId = new Map()
  const scratch = path.join(demoDir, `.demo-probe-${process.pid}-${loads++}.mjs`)
  writeFileSync(scratch, script)
  try {
    await import(pathToFileURL(scratch).href)
  } finally {
    unlinkSync(scratch)
  }
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

const lines = () => $('log').children.map((c) => c.textContent)
const report = { old: pretendOld }

// ============================== first load: a browser that has never seen this page ==========
await loadPage()
await settle()
report.modes = $('mode').children.length
report.mode = $('mode').value
report.banner = $('banner').textContent
report.inputs = $('input').children.map((o) => o.textContent)
report.peerDefault = $('peer').value
report.peerPlaceholder = $('peer').placeholder
report.txdelayDefault = $('txdelayRead').textContent
report.sessionControls = {
  connect: $('connect').disabled, disconnect: $('disconnect').disabled, line: $('line').disabled,
}

// Levels and TXDELAY, before the modem is even open: the package keeps them and applies them
// when the graph is built.
$('rxgain').drag(6)
$('txgain').drag(-12)
$('txdelay').drag(500)
report.rxRead = $('rxgainRead').textContent
report.txRead = $('txgainRead').textContent
report.txdelayRead = $('txdelayRead').textContent
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
$('ttHz').fire('input')
report.freeToneSays = $('ttWhat').textContent

// Set the station up, as an operator would, so the second load has something to restore.
$('mycall').pick('M0LTE-7')
$('peer').pick('GB7XYZ-1')
$('mode').pick('qpsk2400')
$('ptt').pick('rts')
await $('pick-ptt').onclick()
await settle()
report.picked = { opens: fakePort.opens, unkeyed: fakePort.signals.length > 0 }
report.remembered = JSON.parse(stored.get('pdn-soundmodem-demo') || '{}')

$('start').click()
await settle(60)
report.startLog = lines().filter((t) => /running|PTT|core loaded/.test(t))
report.txTestEnabled = !$('ttGo').disabled
report.connectEnabledAfterStart = !$('connect').disabled

if (!pretendOld) {
  // The gains reached the graph, either side of the core.
  report.rxGainLinear = +context.worklet.inputs[0].gain.value.toFixed(3)
  report.txGainLinear = +context.destination.inputs[0].gain.value.toFixed(3)

  // A block of audio arrives; the meter reads it. Then a clipped one.
  context.worklet.port.onmessage({ data: { block: new Float32Array(8), peak: 0.125, rms: 0.05 } })
  await settle(150)
  report.meterRead = $('meterRead').textContent
  report.meterQuiet = $('meterBar').className
  context.worklet.port.onmessage({ data: { block: new Float32Array(8), peak: 1, rms: 0.8 } })
  await settle(150)
  report.clipLit = $('clip').className

  // ---- the TX test -------------------------------------------------------------------------
  // The one control here that puts a signal on the air on purpose, so it is the one that most
  // needs watching: that it keys, that Stop takes it off, and that a tone the modem will not
  // send is refused before the radio is keyed to find out.
  $('ttKind').pick('two')
  $('ttSecs').value = '3'
  $('ttGo').click()
  await settle(20)
  report.whileRunning = { text: $('ttGo').textContent, amber: $('ttGo').className, says: $('ttWhat').textContent }
  await waitFor(() => context.playing, 'the test to reach the air')
  report.keyed = [...log]
  // The burst is the length asked for, behind the TXDELAY the slider is set to.
  report.burstSeconds = +(context.playing.buffer.duration).toFixed(2)
  context.playing.finish()
  await waitFor(() => $('ttGo').textContent === 'Send', 'the button to come back')
  report.afterRunning = { text: $('ttGo').textContent, amber: $('ttGo').className, says: $('ttWhat').textContent }

  // And one stopped part way through, which says how much of it went out rather than how much
  // was asked for.
  log.length = 0
  context.playing = null
  $('ttSecs').value = '30'
  $('ttGo').click()
  await waitFor(() => context.playing, 'the second test to reach the air')
  context.currentTime = 2.4
  $('ttGo').click()
  await waitFor(() => $('ttGo').textContent === 'Send', 'the stop to take effect')
  report.stopped = $('ttWhat').textContent

  // A tone the modem will not send is refused, and nothing is keyed to find that out.
  log.length = 0
  context.playing = null
  $('ttKind').pick('tone')
  $('ttHz').value = '20'
  $('ttGo').click()
  await settle(200)
  report.refused = $('ttWhat').textContent
  report.refusedKeyed = [...log]

  // ---- the session -------------------------------------------------------------------------
  // Connecting with nothing in the box asks the operator rather than dialling a blank callsign.
  $('peer').value = ''
  $('connect').click()
  await settle()
  report.emptyPeer = lines().at(-1)

  // A connect that is refused leaves the button usable rather than stranding the page.
  $('peer').pick('NOPE-1')
  $('connect').click()
  await settle(40)
  report.refusedConnect = { says: lines().at(-1), canRetry: !$('connect').disabled }

  $('peer').pick('GB7XYZ-1')
  $('connect').click()
  await waitFor(() => established, 'the session')
  await settle(20)
  report.connected = {
    connect: $('connect').disabled, disconnect: $('disconnect').disabled,
    line: $('line').disabled, placeholder: $('line').placeholder, says: lines().at(-1),
  }

  // A line typed into the session goes out CR-terminated and is echoed once it has been taken.
  await $('line').type('hello from a browser')
  await settle(20)
  report.sent = established.written
  report.echoed = lines().at(-1)
  report.boxCleared = $('line').value

  // What comes back is turned from CR into newlines for the pane.
  established.deliver(new TextEncoder().encode('de GB7XYZ-1\rgo ahead\r'))
  report.received = lines().at(-1)

  // A key that is not Enter does not transmit.
  await $('line').type('half typed', 'a')
  report.notSentOnEveryKey = established.written.length

  // D tears it down and puts the three controls back.
  $('line').value = ''
  $('disconnect').click()
  await waitFor(() => established.disconnected, 'the disconnect')
  await settle(20)
  report.afterDisconnect = {
    connect: $('connect').disabled, disconnect: $('disconnect').disabled,
    line: $('line').disabled, torn: established.disconnected,
  }

  // A station connecting to US gets the same three controls, with nothing clicked.
  const inbound = new FakeSession('M0ABC-2')
  acceptInbound(inbound)
  await settle(20)
  report.inbound = {
    says: lines().find((t) => /connected by M0ABC-2/.test(t)),
    line: $('line').disabled, disconnect: $('disconnect').disabled,
  }

  // A second caller does not move the box out from under the line being typed.
  acceptInbound(new FakeSession('M0DEF-3'))
  await settle(20)
  report.secondCaller = { says: lines().at(-1), stillWith: $('line').placeholder }

  // And a link that goes away on its own puts the page back exactly as D would.
  inbound.dropped()
  await settle(20)
  report.afterPeerHungUp = {
    line: $('line').disabled, disconnect: $('disconnect').disabled, connect: $('connect').disabled,
  }
}

if (pretendOld) {
  // The only two lines the degraded page has to get right: what it cannot drive, and why.
  report.txTestSays = $('ttWhat').textContent
  report.levelNote = $('levelNote').textContent
}

// ============================== second load: the station comes back ==========================
if (!pretendOld) {
  await loadPage()
  await settle(60)
  report.restored = {
    mode: $('mode').value, mycall: $('mycall').value, peer: $('peer').value,
    ptt: $('ptt').value, rxgain: $('rxgain').value, txgain: $('txgain').value,
    txdelay: $('txdelay').value,
  }
  report.restoredReads = {
    rx: $('rxgainRead').textContent, tx: $('txgainRead').textContent,
    txdelay: $('txdelayRead').textContent,
  }
  // Reopened without a prompt: requestPort was never called on this load, getPorts was.
  report.reopened = { opens: fakePort.opens, says: lines().find((t) => /reopened/.test(t)) }
}

if (asJson) console.log(JSON.stringify(report, null, 2))
Ax25Listener.prototype.connect = realConnect

// ---------------------------------------------------------------- what all that has to add up to
assert.ok(report.modes > 30, 'the mode list is populated from the core')
assert.match(report.banner, /working tree, \d+ modes, ready/)
assert.deepEqual(report.inputs, ['USB PnP Sound Device'], 'devices appear once access is granted')
assert.ok(report.startLog.some((line) => /running/.test(line)), 'the modem opened')

// No default peer. The station that used to be here is a real node, and a page that arrives
// pointed at somebody's BBS eventually connects to it by accident.
assert.equal(report.peerDefault, '', 'no callsign is dialled in by default')
assert.match(report.peerPlaceholder, /callsign/)

// The three session controls are dead until there is a session.
assert.deepEqual(report.sessionControls, { connect: true, disconnect: true, line: true })

if (pretendOld) {
  assert.equal(report.levelsDisabled, true, 'the sliders are disabled, not merely inert')
  assert.match(report.levelNote, /Levels needs a newer soundmodem than/)
  assert.match(report.txTestSays, /The TX test needs a newer soundmodem than/)
  assert.equal(report.txTestEnabled, false, 'and Send cannot be clicked')
  console.log('demo page, against a package one release behind: '
    + 'both controls disabled and saying so, nothing thrown')
} else {
  // Levels and TXDELAY, set before the modem was opened and applied when the graph was built.
  assert.equal(report.rxRead, '+6.0 dB')
  assert.equal(report.txRead, '-12.0 dB')
  assert.equal(report.txdelayDefault, '300 ms')
  assert.equal(report.txdelayRead, '500 ms')
  assert.equal(report.rxGainLinear, 1.995, '+6 dB reached the node before the core')
  assert.equal(report.txGainLinear, 0.251, '-12 dB reached the node before the output')
  assert.match(report.levelNote, /peak in the green, -18 to -9 dBFS/)

  // The meter, on a block in the target zone and then on one that clipped.
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
  assert.equal(report.freeToneSays, 'FM null at 3.61 kHz deviation')

  // The TX test: keyed, stopped, refused.
  assert.equal(report.whileRunning.text, 'Stop')
  assert.equal(report.whileRunning.amber, 'on')
  assert.equal(report.whileRunning.says, 'two-tone 700+1900 Hz, 3.0 s, peak level 0.80')
  assert.deepEqual(report.keyed, ['audio on'])
  // 3 s of tone behind the 500 ms TXDELAY the slider was dragged to, which is the slider
  // reaching the transmit path rather than only the readout beside it.
  assert.equal(report.burstSeconds, 3.5)
  assert.equal(report.afterRunning.text, 'Send')
  assert.match(report.afterRunning.says, /done, 3\.0 s on air$/)
  assert.match(report.stopped, /stopped after 1\.\d s$/)
  assert.match(report.refused, /a test tone must be between 50 Hz/)
  assert.deepEqual(report.refusedKeyed, [], 'and the radio was not keyed to find that out')

  // The session.
  assert.match(report.emptyPeer, /put a callsign in the box first/)
  assert.match(report.refusedConnect.says, /connect failed: retry limit reached/)
  assert.equal(report.refusedConnect.canRetry, true, 'a refusal leaves C clickable')
  assert.deepEqual(
    { connect: report.connected.connect, disconnect: report.connected.disconnect, line: report.connected.line },
    { connect: true, disconnect: false, line: false },
    'connected: C is out, D and the line box are in')
  assert.match(report.connected.placeholder, /type a line to GB7XYZ-1/)
  assert.match(report.connected.says, /connected to GB7XYZ-1/)

  // CR, not LF: that is what a keyboard-to-keyboard line has ended with since packet began.
  assert.deepEqual(report.sent, ['hello from a browser\r'])
  assert.equal(report.echoed, 'hello from a browser', 'echoed once the session had taken it')
  assert.equal(report.boxCleared, '', 'and the box is cleared for the next line')
  assert.equal(report.received, 'de GB7XYZ-1\ngo ahead\n', 'CR comes back as newlines')
  assert.equal(report.notSentOnEveryKey, 1, 'only Enter transmits')

  assert.deepEqual(report.afterDisconnect,
    { connect: false, disconnect: true, line: true, torn: true })

  // Connected TO, with nothing clicked.
  assert.ok(report.inbound.says, 'an inbound session is announced')
  assert.deepEqual({ line: report.inbound.line, disconnect: report.inbound.disconnect },
    { line: false, disconnect: false })
  assert.match(report.secondCaller.says, /also connected; the line box stays with M0ABC-2/)
  assert.match(report.secondCaller.stillWith, /M0ABC-2/, 'the box did not move mid-line')
  assert.deepEqual(report.afterPeerHungUp, { line: true, disconnect: true, connect: false },
    'a peer hanging up puts the page back exactly as D would')

  // And the station comes back on a reload.
  assert.deepEqual(report.restored, {
    mode: 'qpsk2400', mycall: 'M0LTE-7', peer: 'GB7XYZ-1', ptt: 'rts',
    rxgain: '6', txgain: '-12', txdelay: '500',
  })
  assert.deepEqual(report.restoredReads, { rx: '+6.0 dB', tx: '-12.0 dB', txdelay: '500 ms' })
  assert.equal(report.reopened.opens, 2, 'the granted port was reopened without the picker')
  assert.match(report.reopened.says, /PTT port reopened, keying RTS/)

  console.log(`demo page: ${report.modes} modes, levels and TXDELAY reach the graph, the meter `
    + 'latches a clip, the TX test keys and refuses as it should, a session connects, types, '
    + 'disconnects and comes back inbound, and the whole station is remembered across a reload')
}

// setInterval(paint) keeps the page's clock running, as it does in a tab, so say when to stop.
process.exit(0)
