// Drives the waterfall page's real script — the shipping text, in one shared classic-script
// scope, in real V8 — against a real running WaterfallWebServer, and reports what it managed to
// do as JSON on stdout. WaterfallPageTests starts the server and reads that back.
//
// This exists because two defects shipped that no server-side test could see: a `ws` binding that
// wasn't in scope where the Listen handler needed it, and an Int16Array view onto a byte offset
// that wasn't a multiple of the element size. Both are JavaScript semantics rather than pixels,
// which is why a DOM shim costs nothing in fidelity — everything that mattered was in the engine.
//
// Usage: PAGE=<html> PORT=<n> node page-probe.mjs
import { readFileSync } from "node:fs";
import vm from "node:vm";

const html = readFileSync(process.env.PAGE, "utf8");
const script = html.slice(html.indexOf("<script>") + 8, html.lastIndexOf("</script>"));

// Which border-left the shipping stylesheet actually gives a row, resolved as a browser resolves
// it: every rule whose class chain the row matches, by specificity and then by source order. A
// transmitted frame read back out of the log carries both `tx` and `hist`, whose rules set the
// same property — so which one wins is a real question, and one the DOM shim above cannot answer
// because it holds no styles. Only bare class chains are matched; the rules that decide this one
// are all of that shape, and a fuller selector engine would be a second browser to maintain.
const styleText = html.slice(html.indexOf("<style>") + 7, html.indexOf("</style>"))
  .replace(/\/\*[\s\S]*?\*\//g, " ");   // comments first, or they arrive glued to the selector
function borderLeft(className) {
  const classes = className.split(" ").filter(Boolean);
  let won = null, wonRank = -1;
  for (const rule of styleText.matchAll(/([^{}]+)\{([^{}]*)\}/g)) {
    const declared = /border-left(?:-color)?\s*:\s*([^;]+)/.exec(rule[2]);
    if (!declared) continue;
    for (const selector of rule[1].split(",")) {
      const sel = selector.trim();
      if (!/^(\.[A-Za-z0-9_-]+)+$/.test(sel)) continue;
      const want = sel.slice(1).split(".");
      if (!want.every(c => classes.includes(c))) continue;
      if (want.length >= wonRank) { wonRank = want.length; won = declared[1].trim(); }
    }
  }
  return won;
}

const noop = () => {};

// Text drawn on a canvas is captured, because "what did the page write onto the waterfall" is a
// question about strings and needs no pixels to answer. Sampled either side of a synchronous
// call, so nothing else can have drawn in between. fillText only — strokeText paints the same
// string again as a halo, and counting it twice would make every assertion read oddly.
const drawnText = [];
const ctx2d = new Proxy({}, { get: (_, k) =>
  k === "fillText" ? ((text) => { drawnText.push(String(text)); }) :
  k === "strokeText" ? noop :
  ["measureText"].includes(k) ? (() => ({ width: 10 })) :
  ["createLinearGradient"].includes(k) ? (() => ({ addColorStop: noop })) :
  ["createImageData", "getImageData"].includes(k) ? (() => ({ data: new Uint8ClampedArray(4 * 4096) })) :
  typeof k === "string" ? noop : undefined,
  set: () => true });

const els = new Map();
function el(id) {
  if (els.has(id)) return els.get(id);
  const e = {
    id, textContent: "", innerHTML: "", value: "0.8", checked: false, disabled: false,
    width: 800, height: 300, style: {}, children: [], dataset: {},
    className: "", classList: { add: noop, remove: noop, toggle: noop, contains: () => false },
    getContext: () => ctx2d, appendChild: noop, removeChild: noop, insertBefore: noop,
    addEventListener: noop, removeEventListener: noop, getBoundingClientRect: () => ({ width: 800, height: 300, left: 0, top: 0 }),
    querySelector: () => el(id + "-q"), querySelectorAll: () => [], focus: noop, scrollTo: noop,
    append: noop, setAttribute: noop, getAttribute: () => null,
    closest: () => null, contains: () => false, add: noop, options: [], selectedIndex: 0,
    click() { this.onclick && this.onclick({ preventDefault: noop }); },
    // Real enough for the decoded-frames panel, which builds rows with createElement, sets their
    // innerHTML and prepends them — so the markup it produces can be read back and asserted on.
    prepend(node) { node._parent = this; this.children.unshift(node); },
    replaceChildren() { this.children.length = 0; },
    remove() { const kids = this._parent?.children; const at = kids?.indexOf(this) ?? -1; if (at >= 0) kids.splice(at, 1); },
    get firstChild() { return this.children[0] ?? null; },
    get lastChild() { return this.children[this.children.length - 1] ?? null; },
  };
  els.set(id, e);
  return e;
}
const document_ = {
  getElementById: el, createElement: tag => el("new-" + tag + "-" + Math.random()),
  querySelector: () => el("q"), querySelectorAll: () => [], addEventListener: noop,
  body: el("body"), documentElement: el("html"), title: "", visibilityState: "visible",
};

// A real-enough AudioContext: it must accept the decoded block and report its length, because
// the point of the check is that a block gets that far at all.
let played = 0, peak = 0;
class AudioBuffer_ {
  constructor(ch, len) { this.length = len; this.numberOfChannels = ch; this._d = new Float32Array(len); }
  getChannelData() { return this._d; }
}
class AudioContext_ {
  constructor() { this.currentTime = 0; this.state = "running"; this.sampleRate = 48000; this.destination = {}; }
  async resume() { this.state = "running"; }
  async close() { this.state = "closed"; }
  createGain() { return { gain: { value: 1 }, connect: noop, disconnect: noop }; }
  createBuffer(ch, len) { return new AudioBuffer_(ch, len); }
  createBufferSource() {
    const self = this;
    return { buffer: null, connect: noop, onended: null,
      start(when) { played++; const d = this.buffer._d;
        for (let i = 0; i < d.length; i++) { const a = Math.abs(d[i]); if (a > peak) peak = a; }
        self.currentTime = Math.max(self.currentTime, when); } };
  }
}

const sandbox = {
  document: document_, WebSocket, console, fetch, AudioContext: AudioContext_,
  setTimeout, clearTimeout, setInterval, clearInterval, requestAnimationFrame: cb => setTimeout(() => cb(performance.now()), 16),
  cancelAnimationFrame: clearTimeout, performance, location: { host: `127.0.0.1:${process.env.PORT}`, protocol: "http:" },
  devicePixelRatio: 1, Int16Array, Float32Array, Uint8Array, Uint8ClampedArray, ArrayBuffer, DataView,
  Math, JSON, Date, Object, Array, String, Number, Boolean, Error, Map, Set, Promise, parseFloat, parseInt, isNaN,
  matchMedia: () => ({ matches: false, addEventListener: noop }),
  ResizeObserver: class { observe() {} unobserve() {} disconnect() {} },
  Option: class { constructor(text, value) { this.text = text; this.value = value; this.selected = false; } },
  Image: class { },
  getComputedStyle: () => ({ getPropertyValue: () => "12px monospace", font: "12px monospace", color: "#fff" }),
  Intl, TextDecoder, TextEncoder, URL, URLSearchParams, Uint16Array, Int32Array, navigator: { userAgent: 'probe' },
  addEventListener: noop, localStorage: { getItem: () => null, setItem: noop },
  __stats: () => ({ played, peak }),
  __text: () => [...drawnText],
};
sandbox.window = sandbox; sandbox.globalThis = sandbox; sandbox.self = sandbox;

vm.createContext(sandbox);
vm.runInContext(script, sandbox, { filename: "waterfall.html" });

const wait = ms => new Promise(r => setTimeout(r, ms));
const run = (expr) => { try { return vm.runInContext(expr, sandbox); } catch (e) { return "THREW: " + e; } };

// Anything the page throws asynchronously — a bad typed-array view arrives this way — must fail
// the run rather than be lost to the event loop.
const thrown = [];
process.on("uncaughtException", e => thrown.push(String(e)));

await wait(1500);
const connected = run("!!(ws && ws.readyState === 1)") === true && run("!!cfg") === true;

let clickError = null;
try { vm.runInContext(`document.getElementById("listen").click()`, sandbox); }
catch (e) { clickError = String(e); }
await wait(2500);
const whilePlaying = sandbox.__stats();
const listening = run("audioOn()");
const label = run(`document.getElementById("listen").textContent`);

// Stopping must actually stop: the server is told, and nothing more is scheduled.
run(`document.getElementById("listen").click()`);
const at = sandbox.__stats().played;
await wait(1200);

// The decoded-frames panel and the waterfall tags, driven with the two kinds of frame event the
// server sends. Text drawn on a canvas is captured rather than counted, so the tag can be read
// back: an ident tags like anything else, and the only thing distinguishing it is what it says.
const beforeOrdinary = sandbox.__text().length;
run(`tagFrame({sub:0, mode:"afsk1200", from:"M0LTE", to:"GB7RDG", line:0, burstLines:12, snrDb:12.5})`);
const ordinaryTag = sandbox.__text().slice(beforeOrdinary);
run(`logFrame({sub:0, mode:"afsk1200", from:"M0LTE", to:"GB7RDG", lenBytes:24, snrDb:12.5})`);

const beforeIdent = sandbox.__text().length;
run(`tagFrame({sub:0, mode:"afsk300-multi11", from:"KK4HEJ", to:"IDENT", line:0, burstLines:12, offsetHz:-35, id:true})`);
const identTag = sandbox.__text().slice(beforeIdent);
run(`logFrame({sub:0, mode:"afsk300-multi11", from:"KK4HEJ", to:"IDENT", lenBytes:17, offsetHz:-35, id:true})`);

// Our own transmission, through the dispatch the socket handler uses — the point is what the
// page does with the event, not what the two drawing functions do when called by hand. Driven
// against a received frame through the same entry point, so "listed but not tagged" is measured
// rather than assumed.
const beforeHeard = sandbox.__text().length;
run(`onFrameEvent({sub:0, mode:"afsk1200", from:"GB7RDG", to:"M0LTE", line:0, burstLines:9, lenBytes:31, snrDb:9.5})`);
const heardTag = sandbox.__text().slice(beforeHeard);
const beforeTx = sandbox.__text().length;
run(`onFrameEvent({sub:0, mode:"afsk1200", from:"M0LTE-9", to:"GB7RDG", line:0, lenBytes:24, tx:true})`);
const txTag = sandbox.__text().slice(beforeTx);

// A frame that decoded and would not yield callsigns: the panel is where an operator sees the
// word "unattributed", so it is where the reason and the bytes have to appear. The reason quotes
// a character straight off the air, so it is also where markup could arrive if nothing escaped it.
run(`onFrameEvent({sub:2, mode:"bpsk300-il2pc-multi9", from:null, to:null, line:0, burstLines:9, lenBytes:118, snrDb:13.2, offsetHz:-13, crc:true, corrected:0, il2p:"Type1", why:"byte 0 of the destination callsign is 0x3C -> 0x1E ('<'), not a shifted callsign character", hex:"00010203DEADBEEF"})`);

// A capture: tagged onto the waterfall where it happened, and listed with a link to its audio.
const beforeCapture = sandbox.__text().length;
run(`onCapture({verdict:"unclaimed", centreHz:1144, lowHz:944, highHz:1344, durationSeconds:2.1, snrDb:11.4, secondsAgo:0.4, file:"20260804-151909-1144hz-unclaimed.wav"})`);
const captureTag = sandbox.__text().slice(beforeCapture);

run(`setSurveyStatus({captured:7, skipped:2, bytes:12582912})`);
const surveyStatus = sandbox.document.getElementById("survey").textContent;

const frames = sandbox.document.getElementById("frames").children;
const rows = frames.map(c => c.innerHTML);
const rowClasses = frames.map(c => c.className);

// The opening backlog out of the station's frame log. Driven last, and after the live rows are
// captured above, so what it does to a panel that already has rows in it can be seen — which is
// the reconnect case. Timestamps: one from today so the row shows a clock time, one from a past
// year so it has to show the date rather than a bare time that reads as this morning.
const today = new Date();
const todayAt = new Date(Date.UTC(
  today.getUTCFullYear(), today.getUTCMonth(), today.getUTCDate(), 9, 15, 0)).toISOString();
// The station logs what it sends as well as what it hears, so the backlog carries both: the
// transmitted one goes in oldest, where it lands at the bottom of a newest-first panel.
const beforeHistory = sandbox.__text().length;
run(`onHistory({frames: [
  {at: "2024-11-03T20:00:00.000Z", sub: 0, mode: "bpsk300-il2pc", from: "M0LTE", to: "GB7RDG-2", lenBytes: 18, tx: true, hist: true},
  {at: "2024-11-03T21:04:00.000Z", sub: 1, mode: "afsk300-il2pc", from: "GB7BEX-15", to: "GB7IOW-1", lenBytes: 22, corrected: 2, crc: false, hist: true},
  {at: "${todayAt}", sub: 0, mode: "bpsk300-il2pc", from: "GB7RDG-2", to: "EI0RSI-1", lenBytes: 31, offsetHz: 8.6, corrected: 0, crc: true, hist: true}
]})`);
const historyTag = sandbox.__text().slice(beforeHistory);
const afterHistory = sandbox.document.getElementById("frames").children;

console.log(JSON.stringify({
  ordinaryTag,
  identTag,
  heardTag,
  txTag,
  frameRows: rows,
  frameRowClasses: rowClasses,
  captureTag,
  surveyStatus,
  historyTag,
  historyRows: afterHistory.map(c => c.innerHTML),
  historyRowClasses: afterHistory.map(c => c.className),
  // What the stylesheet makes of a row that is both ours and from before the page opened,
  // against the two rows it is made of.
  txHistBorder: borderLeft("fr tx hist"),
  txBorder: borderLeft("fr tx"),
  histBorder: borderLeft("fr hist"),
  connected,
  clickError,
  listening,
  label,
  blocksPlayed: whilePlaying.played,
  peakAmplitude: Number(whilePlaying.peak.toFixed(3)),
  blocksAfterStop: sandbox.__stats().played - at,
  stoppedLabel: run(`document.getElementById("listen").textContent`),
  thrown,
}));
process.exit(0);
