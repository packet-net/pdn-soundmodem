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

const rows = sandbox.document.getElementById("frames").children.map(c => c.innerHTML);

console.log(JSON.stringify({
  ordinaryTag,
  identTag,
  frameRows: rows,
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
