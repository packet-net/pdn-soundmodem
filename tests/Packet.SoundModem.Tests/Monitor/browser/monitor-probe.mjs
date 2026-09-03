// Drives the picker page's real script - the shipping text, in real V8 - against a real running
// MonitorHost, and reports what it made of the receivers as JSON on stdout. MonitorPageTests
// starts the host and reads that back.
//
// The same argument as the waterfall page's probe next door: what a page does with a snapshot is
// JavaScript rather than pixels, and everything worth asserting about this page - which rows it
// drew, what each one says, what it says when the directory is down - is a question about strings.
// A DOM shim costs nothing in fidelity for that, and Chrome cannot open a socket in this sandbox.
//
// Usage: PAGE=<html> PORT=<n> [PATHNAME=/] node monitor-probe.mjs
import { readFileSync } from "node:fs";
import vm from "node:vm";

const html = readFileSync(process.env.PAGE, "utf8");
const script = html.slice(html.indexOf("<script>") + 8, html.lastIndexOf("</script>"));

const noop = () => {};

// Elements the page addresses by id, with the three properties it actually sets on them.
const els = new Map();
function el(id) {
  if (els.has(id)) return els.get(id);
  const e = { id, textContent: "", innerHTML: "", hidden: false, className: "" };
  els.set(id, e);
  return e;
}

const document_ = {
  getElementById: el,
  title: "",
  body: el("body"),
  addEventListener: noop,
};

const timers = [];

// A browser resolves a relative URL against the document it is running in; node's fetch demands
// an absolute one. The page asks for "api/instances" relative to its own path, which is the whole
// point of it working behind a tunnel, so the shim resolves it the way the browser would and
// records what was asked for.
const origin = `http://127.0.0.1:${process.env.PORT}`;
const fetched = [];
const fetch_ = (url, init) => {
  const resolved = new URL(String(url), origin + (process.env.PATHNAME || "/")).toString();
  fetched.push(resolved);
  return fetch(resolved, init);
};

const sandbox = {
  document: document_,
  console,
  fetch: fetch_,
  setTimeout, clearTimeout,
  // Recorded rather than run: the page polls every ten seconds and this probe is not waiting for
  // that. The first render is the one under test, and a second identical one proves nothing.
  setInterval: (fn, ms) => { timers.push(ms); return 0; },
  clearInterval: noop,
  location: {
    host: `127.0.0.1:${process.env.PORT}`,
    protocol: "http:",
    pathname: process.env.PATHNAME || "/",
    reload: () => { sandbox.__reloaded = true; },
  },
  sessionStorage: { getItem: () => null, setItem: noop },
  Date, JSON, Math, Object, Array, String, Number, Boolean, Error, Map, Set, Promise,
  parseFloat, parseInt, isNaN, Intl, URL, URLSearchParams,
  __reloaded: false,
};
sandbox.window = sandbox; sandbox.globalThis = sandbox; sandbox.self = sandbox;

vm.createContext(sandbox);

const thrown = [];
process.on("uncaughtException", e => thrown.push(String(e)));
process.on("unhandledRejection", e => thrown.push(String(e)));

vm.runInContext(script, sandbox, { filename: "monitor.html" });

// Waited for rather than slept through: the page's first render happens when its own fetch comes
// back, and on a loaded runner that is not at a fixed moment. The summary line is written on
// every render and on no other path, so it is what says a render has happened - the rows can
// legitimately be empty and every hidden flag starts in whichever state this shim gave it.
const deadline = Date.now() + 20000;
while (Date.now() < deadline && el("summary").textContent === "") {
  await new Promise(r => setTimeout(r, 25));
}

// Each row as its own string, so an assertion can say which receiver it is about.
const rows = el("rows").innerHTML.split('<div class="row').slice(1).map(r => '<div class="row' + r);

console.log(JSON.stringify({
  title: document_.title,
  heading: el("title").textContent,
  about: el("about").innerHTML,
  aboutHidden: el("about").hidden === true,
  summary: el("summary").textContent,
  stale: el("stale").textContent,
  staleHidden: el("stale").hidden === true,
  empty: el("empty").textContent,
  emptyHidden: el("empty").hidden === true,
  footer: el("footer").innerHTML,
  rows,
  pollMs: timers,
  fetched,
  reloaded: sandbox.__reloaded,
  thrown,
}));
process.exit(0);
