// The WebAssembly SDK wants an entry module for the app bundle. Nothing uses it: a page
// imports _framework/dotnet.js directly, and the Node harness is web/test/decode.mjs.
import { dotnet } from './_framework/dotnet.js'

const runtime = await dotnet.withDiagnosticTracing(false).create()
const Modem = (await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName)).Modem
console.log(`pdn-soundmodem, ${Modem.Modes().length} modes:`)
console.log(Modem.Modes().join(' '))
console.log('\nto decode or loopback, use web/test/decode.mjs')
