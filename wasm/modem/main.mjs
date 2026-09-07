// The wasm modem bundle under Node - the harness that says whether WebAssembly changed a
// decode. Two commands:
//
//   node main.mjs decode <file.wav> <mode>   decode a WAV, print each frame as hex
//   node main.mjs loopback <mode>            modulate a frame, feed it back, decode it
//
// `decode` output is diffed against wasm/parity (the same calls against the native build).
import { readFileSync } from 'node:fs'
import { dotnet } from './_framework/dotnet.js'

const { getAssemblyExports, getConfig } = await dotnet.withDiagnosticTracing(false).create()
const Modem = (await getAssemblyExports(getConfig().mainAssemblyName)).Modem

const QUANTUM = 128 // one AudioWorklet render quantum, so the feed matches a real page's

let handle = 0

function pull(into) {
  const packed = Modem.TakeFrames(handle)
  for (let p = 0; p < packed.length;) {
    const len = packed[p] | (packed[p + 1] << 8)
    into.push(packed.subarray(p + 2, p + 2 + len))
    p += 2 + len
  }
}

function feed(samples, into) {
  for (let at = 0; at < samples.length; at += QUANTUM) {
    const block = samples.subarray(at, Math.min(at + QUANTUM, samples.length))
    Modem.Feed(handle, new Uint8Array(block.buffer, block.byteOffset, block.byteLength))
    pull(into)
  }
}

/** Flushes the FIR pipelines with silence: a live stream never ends, a file does. */
function flush(rate, into) {
  const quiet = new Uint8Array(QUANTUM * 4)
  for (let at = 0; at < rate / 2; at += QUANTUM) {
    Modem.Feed(handle, quiet)
    pull(into)
  }
}

/** Minimal 16-bit PCM WAV reader: enough for the repo's sample corpus. */
function readWav(path) {
  const buf = readFileSync(path)
  let at = 12
  let fmt = null
  let data = null
  while (at + 8 <= buf.length) {
    const id = buf.toString('ascii', at, at + 4)
    const size = buf.readUInt32LE(at + 4)
    const body = at + 8
    if (id === 'fmt ') fmt = { channels: buf.readUInt16LE(body + 2), rate: buf.readUInt32LE(body + 4), bits: buf.readUInt16LE(body + 14) }
    if (id === 'data') data = buf.subarray(body, body + size)
    at = body + size + (size & 1)
  }
  if (!fmt || !data) throw new Error('not a PCM wav')
  if (fmt.bits !== 16) throw new Error(`expected 16-bit, got ${fmt.bits}`)
  const frames = data.length / 2 / fmt.channels
  const out = new Float32Array(frames)
  for (let i = 0; i < frames; i++) out[i] = data.readInt16LE(i * 2 * fmt.channels) / 32768
  return { samples: out, rate: fmt.rate }
}

/** One AX.25 UI frame, addresses and all - no flags, no FCS, which is what the modem takes. */
function uiFrame(dest, source, text) {
  const address = (call, ssid, last) => {
    const out = new Uint8Array(7)
    for (let i = 0; i < 6; i++) out[i] = ((call[i] ?? ' ').charCodeAt(0)) << 1
    out[6] = 0x60 | (ssid << 1) | (last ? 1 : 0)
    return out
  }
  const payload = new TextEncoder().encode(text)
  const frame = new Uint8Array(7 + 7 + 2 + payload.length)
  frame.set(address(dest, 0, false), 0)
  frame.set(address(source, 1, true), 7)
  frame[14] = 0x03 // UI
  frame[15] = 0xf0 // no layer 3
  frame.set(payload, 16)
  return frame
}

const hex = (b) => Buffer.from(b).toString('hex')

const [, , ...argv] = process.argv
const command = argv[0]?.endsWith('.wav') ? 'decode' : argv[0]

if (command === 'decode') {
  const [wavPath, mode = 'afsk1200'] = argv[0]?.endsWith('.wav') ? argv : argv.slice(1)
  const { samples, rate } = readWav(wavPath)
  handle = Modem.Open(mode, rate)
  const chainRate = Modem.DspRateOf(handle)
  const decoded = []
  const started = process.hrtime.bigint()
  feed(samples, decoded)
  flush(rate, decoded)
  const elapsedMs = Number(process.hrtime.bigint() - started) / 1e6
  for (const [i, f] of decoded.entries()) console.log(`[${i + 1}] ${f.length} bytes  ${hex(f)}`)
  console.log(`${decoded.length} frames from ${wavPath} (${mode}, ${rate} Hz in, chain at ${chainRate} Hz)`)
  const seconds = samples.length / rate
  console.log(`${elapsedMs.toFixed(0)} ms of CPU for ${seconds.toFixed(2)} s of audio (real-time factor ${(elapsedMs / 1000 / seconds).toFixed(3)})`)
} else if (command === 'loopback') {
  const mode = argv[1] ?? 'afsk1200'
  const audioRate = Number(argv[2] ?? 48000)
  handle = Modem.Open(mode, audioRate)
  const chainRate = Modem.DspRateOf(handle)
  const sent = uiFrame('TEST', 'M0LTE', `hello from wasm over ${mode}`)
  const audio = new Float32Array(Modem.Modulate(handle, sent, 300).buffer)
  const decoded = []
  feed(audio, decoded)
  flush(audioRate, decoded)
  const ok = decoded.some((f) => hex(f) === hex(sent))
  console.log(`${mode}: ${(audio.length / audioRate).toFixed(2)} s of TX audio at ${audioRate} Hz (chain ${chainRate} Hz), ` +
    `${decoded.length} frame(s) back, round trip ${ok ? 'IDENTICAL' : 'MISMATCH'}`)
  if (!ok) {
    console.log(`  sent ${hex(sent)}`)
    for (const f of decoded) console.log(`  got  ${hex(f)}`)
    process.exitCode = 1
  }
} else {
  console.log('modes:', Modem.Modes().join(' '))
  console.log('usage: node main.mjs decode <file.wav> <mode> | loopback <mode> [audioRate]')
}
