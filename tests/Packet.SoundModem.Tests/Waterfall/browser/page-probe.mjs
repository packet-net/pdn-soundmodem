// Drives the waterfall page's real script - the shipping text, in one shared classic-script
// scope, in real V8 - against a real running WaterfallWebServer, and reports what it managed to
// do as JSON on stdout. WaterfallPageTests starts the server and reads that back.
//
// This exists because two defects shipped that no server-side test could see: a `ws` binding that
// wasn't in scope where the Listen handler needed it, and an Int16Array view onto a byte offset
// that wasn't a multiple of the element size. Both are JavaScript semantics rather than pixels,
// which is why a DOM shim costs nothing in fidelity - everything that mattered was in the engine.
//
// Usage: PAGE=<html> PORT=<n> [AUDIO=1] node page-probe.mjs
import { readFileSync } from "node:fs";
import vm from "node:vm";

const html = readFileSync(process.env.PAGE, "utf8");
const script = html.slice(html.indexOf("<script>") + 8, html.lastIndexOf("</script>"));

// Which border-left the shipping stylesheet actually gives a row, resolved as a browser resolves
// it: every rule whose class chain the row matches, by specificity and then by source order. A
// transmitted frame read back out of the log carries both `tx` and `hist`, whose rules set the
// same property - so which one wins is a real question, and one the DOM shim above cannot answer
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

// The same question one level down, for a badge inside a row: which background the shipping
// stylesheet gives `<span class="X">` sitting inside `<div class="Y">`. The badge rules are
// descendant selectors (.fr .id, .fr.tx .id, .fr .id.rs) rather than the bare chains above, and
// which of them wins is a real question - a plain-IL2P badge and a transmit badge both match .id,
// and the answer decides whether the RS-only warning survives on a row that is also something
// else. Specificity is the total class count, ties broken by source order, as a browser does.
// var(--name) is looked up in the same stylesheet so the answer is a colour rather than a token.
function badgeBackground(rowClassName, badgeClassName) {
  const rowClasses = rowClassName.split(" ").filter(Boolean);
  const badgeClasses = badgeClassName.split(" ").filter(Boolean);
  const chain = /^(\.[A-Za-z0-9_-]+)+$/;
  let won = null, wonRank = -1;
  for (const rule of styleText.matchAll(/([^{}]+)\{([^{}]*)\}/g)) {
    const declared = /(?:^|;)\s*background\s*:\s*([^;]+)/.exec(rule[2]);
    if (!declared) continue;
    for (const selector of rule[1].split(",")) {
      const parts = selector.trim().split(/\s+/);
      if (parts.length !== 2 || !parts.every(p => chain.test(p))) continue;
      const [ancestor, element] = parts.map(p => p.slice(1).split("."));
      if (!ancestor.every(c => rowClasses.includes(c))) continue;
      if (!element.every(c => badgeClasses.includes(c))) continue;
      const rank = ancestor.length + element.length;
      if (rank >= wonRank) { wonRank = rank; won = declared[1].trim(); }
    }
  }

  const variable = won && /^var\(\s*(--[A-Za-z0-9_-]+)\s*\)$/.exec(won);
  if (!variable) return won;
  const value = new RegExp(`${variable[1]}\\s*:\\s*([^;]+)`).exec(styleText);
  return value ? value[1].trim() : won;
}

const noop = () => {};

// Text drawn on a canvas is captured, because "what did the page write onto the waterfall" is a
// question about strings and needs no pixels to answer. Sampled either side of a synchronous
// call, so nothing else can have drawn in between. fillText only - strokeText paints the same
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

// A className that actually tracks classList, because state the page carries in a class - which
// of two colours the transmit readout is wearing - is otherwise invisible to every assertion. The
// list reads and writes className, so a row that sets className directly (the frames panel does)
// and one that toggles classes (the header does) both read back the same way.
function classListFor(node) {
  const parse = () => String(node.className || "").split(/\s+/).filter(Boolean);
  const write = list => { node.className = list.join(" "); };
  return {
    add(...names) { const list = parse(); for (const n of names) if (!list.includes(n)) list.push(n); write(list); },
    remove(...names) { write(parse().filter(n => !names.includes(n))); },
    contains: name => parse().includes(name),
    toggle(name, force) {
      const on = force === undefined ? !parse().includes(name) : !!force;
      on ? this.add(name) : this.remove(name);
      return on;
    },
  };
}

const els = new Map();
function el(id) {
  if (els.has(id)) return els.get(id);
  const e = {
    id, textContent: "", innerHTML: "", value: "0.8", checked: false, disabled: false,
    width: 800, height: 300, style: {}, children: [], dataset: {},
    className: "",
    getContext: () => ctx2d, removeChild: noop,
    addEventListener: noop, removeEventListener: noop, getBoundingClientRect: () => ({ width: 800, height: 300, left: 0, top: 0 }),
    querySelector: () => el(id + "-q"), querySelectorAll: () => [], focus: noop, scrollTo: noop,
    setAttribute: noop, getAttribute: () => null,
    closest: () => null, contains: () => false, add: noop, options: [], selectedIndex: 0,
    // Real as well, because the links pane builds each card from parts held in hand.
    append(...nodes) { for (const node of nodes) { node._parent = this; this.children.push(node); } },
    appendChild(node) { node._parent = this; this.children.push(node); return node; },
    click() { this.onclick && this.onclick({ preventDefault: noop }); },
    // Real enough for the decoded-frames panel, which builds rows with createElement, sets their
    // innerHTML and prepends them - so the markup it produces can be read back and asserted on.
    prepend(node) { node._parent = this; this.children.unshift(node); },
    // With its arguments: the modem chips are built as a list and handed over in one call, and
    // dropping them made the whole strip invisible to the probe.
    replaceChildren(...nodes) {
      this.children.length = 0;
      for (const node of nodes) { node._parent = this; this.children.push(node); }
    },
    remove() { const kids = this._parent?.children; const at = kids?.indexOf(this) ?? -1; if (at >= 0) kids.splice(at, 1); },
    // Real, with a null reference meaning the end, as in a browser: the links pane keeps its
    // cards in order by taking one out and putting it back where it now belongs.
    insertBefore(node, ref) {
      node._parent = this;
      const at = ref ? this.children.indexOf(ref) : -1;
      if (at < 0) this.children.push(node); else this.children.splice(at, 0, node);
      return node;
    },
    get firstChild() { return this.children[0] ?? null; },
    get lastChild() { return this.children[this.children.length - 1] ?? null; },
  };
  e.classList = classListFor(e);
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

// Anything the page throws asynchronously - a bad typed-array view arrives this way - must fail
// the run rather than be lost to the event loop.
const thrown = [];
process.on("uncaughtException", e => thrown.push(String(e)));

// Waited for rather than slept through. A fixed pause is a guess about how quickly a machine
// completes a handshake, and on a box running the rest of the suite alongside this it is
// sometimes wrong - which showed up as a page that had simply not connected yet, and as an audio
// window that had already half elapsed before the socket was open.
const untilTrue = async (predicate, ms) => {
  const deadline = Date.now() + ms;
  while (Date.now() < deadline) {
    if (predicate() === true) return true;
    await wait(50);
  }
  return false;
};
const until = (expr, ms) => untilTrue(() => run(expr) === true, ms);

const connected = await until("!!(ws && ws.readyState === 1 && cfg)", 20000);

// The modem chips as the handshake left them - nothing driven by hand. What the server had to say
// about its KISS ports when this browser arrived has to reach the labels, or a page opened at any
// time other than the moment a host connected would say nothing about attachment.
const chips = () => sandbox.document.getElementById("chips").children.map(c => c.innerHTML);
const chipsOnArrival = chips();

let clickError = null;
try { vm.runInContext(`document.getElementById("listen").click()`, sandbox); }
catch (e) { clickError = String(e); }
// A second of audio is 25 blocks at the 40 ms the server sends; wait for enough of them to prove
// a stream rather than a first packet, and give a busy machine longer to produce them than a
// fixed window would - it returns as soon as they are there. Only one of this suite's runs feeds
// the channel any audio at all, and it says so (AUDIO=1) and is waited for in full: a loaded
// runner has delivered the first block later than any short silence would allow, and taking
// that silence as the answer failed a release. Every other run stops waiting after a couple of
// seconds of nothing rather than spending the whole budget proving silence.
const expectAudio = process.env.AUDIO === "1";
const quietBy = Date.now() + 2500;
await untilTrue(
  () => { const p = sandbox.__stats().played; return p > 25 || (!expectAudio && p === 0 && Date.now() > quietBy); },
  expectAudio ? 20000 : 10000);
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

// Our own transmission, through the dispatch the socket handler uses - the point is what the
// page does with the event, not what the two drawing functions do when called by hand. Driven
// against a received frame through the same entry point, so "listed but not tagged" is measured
// rather than assumed.
const beforeHeard = sandbox.__text().length;
run(`onFrameEvent({sub:0, mode:"afsk1200", from:"GB7RDG", to:"M0LTE", line:0, burstLines:9, lenBytes:31, snrDb:9.5})`);
const heardTag = sandbox.__text().slice(beforeHeard);
const beforeTx = sandbox.__text().length;
run(`onFrameEvent({sub:0, mode:"afsk1200", from:"M0LTE-9", to:"GB7RDG", line:0, lenBytes:24, tx:true})`);
const txTag = sandbox.__text().slice(beforeTx);

// A frame read as plain IL2P on a link that runs IL2P+CRC: nothing but Reed-Solomon behind it,
// and by default not passed to the host. Driven twice - withheld and, on a modem told to accept
// them, delivered - because the badge must be there either way and only the explanation changes.
// Ahead of the unattributed frame below, which is addressed by an "il2p Type1" these also carry:
// the panel is newest first, so whatever is driven last is the one found first.
run(`onFrameEvent({sub:0, mode:"bpsk300-il2pc-multi9", from:"GB7BPQ", to:"BEACON", line:0, burstLines:9, lenBytes:46, corrected:0, crc:null, il2p:"Type1", plain:true, monitorOnly:true})`);
run(`onFrameEvent({sub:0, mode:"bpsk300-il2pc-multi9", from:"PD4R-11", to:"BEACON", line:0, burstLines:9, lenBytes:46, corrected:0, crc:null, il2p:"Type1", plain:true})`);

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

// The transmit readout, through its four states: keyed, keyed into a bad load, the average left
// behind at key-up, and a radio that reports no SWR at all. Each state is read back where it is
// set, because every one of them overwrites the last.
const readout = () => ({
  className: sandbox.document.getElementById("txReadout").className,
  hidden: sandbox.document.getElementById("txReadout").hidden === true,
  when: sandbox.document.getElementById("txWhen").textContent,
  power: sandbox.document.getElementById("txPower").textContent,
  swr: sandbox.document.getElementById("txSwr").textContent,
  swrHidden: sandbox.document.getElementById("txSwrFig").hidden === true,
  swrClass: sandbox.document.getElementById("txSwrFig").className,
});
run(`setTransmitReading({type:"tx", keyed:true, watts:29.4, swr:1.2, at:null})`);
const txKeyed = readout();
run(`setTransmitReading({type:"tx", keyed:true, watts:29.4, swr:3.1, at:null})`);
const txKeyedBadSwr = readout();
run(`setTransmitReading({type:"tx", keyed:false, watts:27.5, swr:1.4, at:"2026-08-14T09:15:23.000Z"})`);
const txHeld = readout();
run(`setTransmitReading({type:"tx", keyed:false, watts:27.5, swr:null, at:"2026-08-14T09:15:23.000Z"})`);
const txHeldNoSwr = readout();

// Which KISS ports have a host on them, onto the modem chips. The server has one modem (0), and
// both a dedicated port and the multiplexed one reach it - so the badge is about the modem, not
// about any one port. Driven attached and then detached, because the second state is the one an
// operator is looking for.
run(`setHostPorts({type:"hosts", ports:[{port:8105, sub:null, clients:1}, {port:8101, sub:0, clients:1}]})`);
const chipsAttached = chips();
run(`setHostPorts({type:"hosts", ports:[{port:8105, sub:null, clients:0}, {port:8101, sub:0, clients:0}]})`);
const chipsDetached = chips();

const frames = sandbox.document.getElementById("frames").children;
const rows = frames.map(c => c.innerHTML);
const rowClasses = frames.map(c => c.className);

// The AX.25 links pane, driven with the two messages the server sends it: every link it knows
// on connect, then one frame live. On connect: a link that is up and waiting on an answer, a
// beacon heard twenty minutes ago through a digipeater, a beacon this station repeated itself
// (sent under the originator's call, which must not make that call ours), and a link that
// finished two hours ago, which is too old to show. Then the frame this pane exists for, a data
// frame being sent for the second time. What is read back is the card as built - header,
// figures, concern, feed - because "a resend is obvious" is a claim about the markup and nothing
// on the server can see it; and then which cards each filter leaves showing, in which order.
const minutesAgo = m => new Date(Date.now() - m * 60 * 1000).toISOString();
const noSide = `{frames:0, data:0, bytes:0, resends:0, polls:0, pollsOpen:0, rejects:0, callsOpen:0, busy:null, awaiting:null}`;
run(`showLinks(false)`);
const linksHiddenBefore = sandbox.document.getElementById("links").hidden === true;
run(`onLinks({type:"links", links:[
  {id:"1|GB7BEX<>ID", sub:1, a:"GB7BEX", b:"ID", state:"unconnected", inferred:null, modulo:8,
   first:"${minutesAgo(80)}", last:"${minutesAgo(20)}",
   ab:{frames:1, data:0, bytes:0, resends:0, polls:0, pollsOpen:0, rejects:0, callsOpen:0, busy:null, awaiting:null},
   ba:${noSide},
   concern:null,
   recent:[
     {at:"${minutesAgo(20)}", from:"GB7BEX", to:"ID", via:["MB7UXX*"], kind:"UI", cmd:true, pf:null, ns:null, nr:null, len:18, text:"GB7BEX BBS Exeter", say:"beacon, 18 bytes", flags:["digipeated"], count:null, state:"unconnected", tx:null}
   ]},
  {id:"0|GB7RDG-2<>M0LTE-9", sub:0, a:"M0LTE-9", b:"GB7RDG-2", state:"connected", inferred:null, modulo:8,
   first:"${minutesAgo(3)}", last:"${minutesAgo(1)}",
   ab:{frames:3, data:1, bytes:12, resends:0, polls:0, pollsOpen:0, rejects:0, callsOpen:0, busy:null, awaiting:0},
   ba:{frames:2, data:1, bytes:49, resends:0, polls:1, pollsOpen:1, rejects:0, callsOpen:0, busy:null, awaiting:1},
   concern:"GB7RDG-2 timed out waiting and is polling",
   recent:[
     {at:"${minutesAgo(3)}", from:"M0LTE-9", to:"GB7RDG-2", via:null, kind:"SABM", cmd:true, pf:true, ns:null, nr:null, len:0, text:null, say:"calls GB7RDG-2", flags:null, count:null, state:"calling", tx:null},
     {at:"${minutesAgo(3)}", from:"GB7RDG-2", to:"M0LTE-9", via:null, kind:"UA", cmd:false, pf:true, ns:null, nr:null, len:0, text:null, say:"accepts the call; link up", flags:["final","linkUp"], count:null, state:"connected", tx:true},
     {at:"${minutesAgo(2)}", from:"M0LTE-9", to:"GB7RDG-2", via:null, kind:"I", cmd:true, pf:null, ns:0, nr:0, pid:240, len:12, text:"hello <node>", say:"sends #0, 12 bytes", flags:null, count:null, state:"connected", tx:null}
   ]},
  {id:"0|ID<>M0XYZ-3", sub:0, a:"M0XYZ-3", b:"ID", state:"unconnected", inferred:null, modulo:8,
   first:"${minutesAgo(2)}", last:"${minutesAgo(2)}",
   ab:{frames:1, data:0, bytes:0, resends:0, polls:0, pollsOpen:0, rejects:0, callsOpen:0, busy:null, awaiting:null},
   ba:${noSide},
   concern:null,
   recent:[
     {at:"${minutesAgo(2)}", from:"M0XYZ-3", to:"ID", via:["GB7RDG-2*"], kind:"UI", cmd:true, pf:null, ns:null, nr:null, len:9, text:"M0XYZ-3 QRV", say:"beacon, 9 bytes", flags:["digipeated"], count:null, state:"unconnected", tx:true}
   ]},
  {id:"0|G4ABC-1<>GB7IOW-1", sub:0, a:"G4ABC-1", b:"GB7IOW-1", state:"disconnected", inferred:null, modulo:8,
   first:"${minutesAgo(150)}", last:"${minutesAgo(120)}",
   ab:{frames:4, data:1, bytes:30, resends:0, polls:0, pollsOpen:0, rejects:0, callsOpen:0, busy:null, awaiting:0},
   ba:{frames:4, data:1, bytes:30, resends:0, polls:0, pollsOpen:0, rejects:0, callsOpen:0, busy:null, awaiting:0},
   concern:null,
   recent:[
     {at:"${minutesAgo(120)}", from:"G4ABC-1", to:"GB7IOW-1", via:null, kind:"DISC", cmd:true, pf:true, ns:null, nr:null, len:0, text:null, say:"disconnects", flags:null, count:null, state:"disconnecting", tx:null}
   ]}
]})`);
const linkBar = () => ({
  n: sandbox.document.getElementById("linksN").textContent,
  summary: sandbox.document.getElementById("linksSummary").textContent,
  buttonClass: sandbox.document.getElementById("linksBtn").className,
  uiClass: sandbox.document.getElementById("linksUi").className,
  uiCount: sandbox.document.getElementById("linksUi").querySelector(".n").textContent,
  mineClass: sandbox.document.getElementById("linksMine").className,
  mineCount: sandbox.document.getElementById("linksMine").querySelector(".n").textContent,
  mineTitle: sandbox.document.getElementById("linksMine").title ?? "",
  emptyHidden: sandbox.document.getElementById("linksEmpty").hidden === true,
  empty: sandbox.document.getElementById("linksEmpty").textContent,
});
const shownCards = () => sandbox.document.getElementById("linkCards").children
  .map(card => ({ id: card.dataset.link, className: card.className, hidden: card.hidden === true }));
const linksOnArrival = linkBar();
const cardsOnArrival = shownCards();
run(`onLinkEvent({type:"link",
  link:{id:"0|GB7RDG-2<>M0LTE-9", sub:0, a:"M0LTE-9", b:"GB7RDG-2", state:"connected", inferred:null, modulo:8,
   first:"${minutesAgo(3)}", last:"${new Date().toISOString()}",
   ab:{frames:3, data:1, bytes:12, resends:0, polls:0, pollsOpen:0, rejects:0, callsOpen:0, busy:null, awaiting:0},
   ba:{frames:3, data:1, bytes:49, resends:1, polls:1, pollsOpen:0, rejects:0, callsOpen:0, busy:null, awaiting:1},
   concern:null, recent:null},
  event:{at:"${new Date().toISOString()}", from:"GB7RDG-2", to:"M0LTE-9", via:null, kind:"I", cmd:true, pf:null, ns:0, nr:1, pid:240, len:49, text:"Welcome to GB7RDG-2", say:"resends #0", flags:["resend"], count:null, state:"connected", tx:true}})`);
run(`showLinks(true)`);
const linksHiddenAfter = sandbox.document.getElementById("links").hidden === true;
const linkCards = sandbox.document.getElementById("linkCards").children.map(card => ({
  id: card.dataset.link,
  className: card.className,
  hidden: card.hidden === true,
  head: card.children[0]?.innerHTML ?? "",
  stats: card.children[1]?.innerHTML ?? "",
  concern: card.children[2]?.textContent ?? "",
  concernHidden: card.children[2]?.hidden === true,
  feed: (card.children[3]?.children ?? []).map(row => ({ className: row.className, html: row.innerHTML })),
}));
const linksAfterEvent = linkBar();
// A second link comes up between two other stations, newer than ours, and lands above it. Then
// each filter in turn: UI frames on shows the beacons; Mine on leaves only the link this
// station is one end of, which it knows from having answered on it. Half a minute ago rather
// than now: the call scripted below is timed at now and has to sort above this, and a fast
// runner gets both into the same millisecond (the v0.52.0 release run did), where "newer"
// is a tie and the page keeps the card it already had on top.
run(`onLinkEvent({type:"link",
  link:{id:"0|G4ABC-1<>GB7IOW-1", sub:0, a:"G4ABC-1", b:"GB7IOW-1", state:"connected", inferred:null, modulo:8,
   first:"${minutesAgo(0.5)}", last:"${minutesAgo(0.5)}",
   ab:{frames:1, data:0, bytes:0, resends:0, polls:0, pollsOpen:0, rejects:0, callsOpen:0, busy:null, awaiting:0},
   ba:{frames:1, data:0, bytes:0, resends:0, polls:0, pollsOpen:0, rejects:0, callsOpen:0, busy:null, awaiting:0},
   concern:null, recent:null},
  event:{at:"${minutesAgo(0.5)}", from:"GB7IOW-1", to:"G4ABC-1", via:null, kind:"UA", cmd:false, pf:true, ns:null, nr:null, len:0, text:null, say:"accepts the call; link up", flags:["final","linkUp"], count:null, state:"connected", tx:null}})`);
const cardsAfterSecondLink = shownCards();
sandbox.document.getElementById("linksUi").click();
const cardsWithUi = shownCards();
const linksWithUi = linkBar();
sandbox.document.getElementById("linksUi").click();
sandbox.document.getElementById("linksMine").click();
const cardsMine = shownCards();
const linksMine = linkBar();
sandbox.document.getElementById("linksMine").click();
// A call comes in that nothing answers. It goes to the top while it is a call; when the station
// gives up on it (the server's observer does, after three minutes, with a line that is not a
// frame: no kind) the card drops below the links that are up and the line says why.
const callCard = () => {
  const card = sandbox.document.getElementById("linkCards").children.find(c => c.dataset.link === "0|EI0RSI-1<>GB7RDG-2");
  return card ? {
    className: card.className,
    head: card.children[0]?.innerHTML ?? "",
    concernHidden: card.children[2]?.hidden === true,
    feed: (card.children[3]?.children ?? []).map(row => ({ className: row.className, html: row.innerHTML })),
  } : null;
};
run(`onLinkEvent({type:"link",
  link:{id:"0|EI0RSI-1<>GB7RDG-2", sub:0, a:"EI0RSI-1", b:"GB7RDG-2", state:"calling", inferred:null, modulo:8,
   first:"${new Date().toISOString()}", last:"${new Date().toISOString()}",
   ab:{frames:1, data:0, bytes:0, resends:0, polls:0, pollsOpen:0, rejects:0, callsOpen:1, busy:null, awaiting:null},
   ba:${noSide},
   concern:null, recent:null},
  event:{at:"${new Date().toISOString()}", from:"EI0RSI-1", to:"GB7RDG-2", via:null, kind:"SABM", cmd:true, pf:true, ns:null, nr:null, len:0, text:null, say:"calls GB7RDG-2", flags:null, count:null, state:"calling", tx:null}})`);
const cardsAfterCall = shownCards();
run(`onLinkEvent({type:"link",
  link:{id:"0|EI0RSI-1<>GB7RDG-2", sub:0, a:"EI0RSI-1", b:"GB7RDG-2", state:"disconnected", inferred:null, modulo:8,
   first:"${new Date().toISOString()}", last:"${new Date().toISOString()}",
   ab:{frames:1, data:0, bytes:0, resends:0, polls:0, pollsOpen:0, rejects:0, callsOpen:0, busy:null, awaiting:null},
   ba:${noSide},
   concern:null, recent:null},
  event:{at:"${new Date(Date.now() + 180000).toISOString()}", from:"EI0RSI-1", to:"GB7RDG-2", via:null, kind:null, cmd:false, pf:null, ns:null, nr:null, len:0, text:null, say:"got no answer in 3 minutes; the call has failed", flags:["timeout"], count:null, state:"disconnected", tx:null}})`);
const cardsAfterTimeout = shownCards();
const failedCall = callCard();
// The transcript reads the other way up from the card, oldest line first, with the classic
// monitor decode beside the words. Built for the failed call and for the station's own link,
// which has data frames with text and a resend in it.
const transcriptFailed = run(`transcriptFor(cards.get("0|EI0RSI-1<>GB7RDG-2"))`);
const transcriptOurs = run(`transcriptFor(cards.get("0|GB7RDG-2<>M0LTE-9"))`);

// The opening backlog out of the station's frame log. Driven last, and after the live rows are
// captured above, so what it does to a panel that already has rows in it can be seen - which is
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

// What a public deployment dresses the page with, as the handshake left it: the title, the
// about strip with the receiver credit, and the body class the stylesheet keys off.
const publicPage = {
  title: sandbox.document.title,
  bodyClass: sandbox.document.body.className,
  about: sandbox.document.getElementById("about").innerHTML,
  aboutHidden: sandbox.document.getElementById("about").hidden === true,
};

console.log(JSON.stringify({
  publicPage,
  txKeyed,
  txKeyedBadSwr,
  txHeld,
  txHeldNoSwr,
  chipsOnArrival,
  chipsAttached,
  chipsDetached,
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
  linksHiddenBefore,
  linksHiddenAfter,
  linksOnArrival,
  cardsOnArrival,
  linksAfterEvent,
  linkCards,
  cardsAfterSecondLink,
  cardsAfterCall,
  transcriptFailed,
  transcriptOurs,
  cardsAfterTimeout,
  failedCall,
  cardsWithUi,
  linksWithUi,
  cardsMine,
  linksMine,
  // What the stylesheet makes of a row that is both ours and from before the page opened,
  // against the two rows it is made of.
  txHistBorder: borderLeft("fr tx hist"),
  txBorder: borderLeft("fr tx"),
  histBorder: borderLeft("fr hist"),
  // And what it makes of the RS-only badge, against the ident badge it shares a shape with and
  // against the transmit rule that also claims .id.
  rsBadgeBackground: badgeBackground("fr", "id rs"),
  identBadgeBackground: badgeBackground("fr", "id"),
  rsBadgeOnTxRowBackground: badgeBackground("fr tx", "id rs"),
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
