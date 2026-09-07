#!/usr/bin/env bash
# Builds the WebAssembly modem and drops it next to the browser PoC.
#
#   ./build.sh            trimmed, interpreted - ~1.4 MB gzipped, real-time on every mode
#   ./build.sh --aot      AOT compiled - ~2.8 MB gzipped, roughly native speed
#
# Both produce the same decodes; the difference is download size against CPU.
set -euo pipefail
cd "$(dirname "$0")"

args=(-c Release)
if [[ "${1:-}" == "--aot" ]]; then
  args+=(-p:RunAOTCompilation=true -p:WasmStripILAfterAOT=true)
fi

dotnet publish modem/Packet.SoundModem.Wasm.csproj "${args[@]}"

bundle=modem/bin/Release/net10.0/browser-wasm/AppBundle
rm -rf browser/_framework
cp -r "$bundle/_framework" browser/_framework
# The Node harness sits beside the bundle so `node main.mjs decode <wav> <mode>` works from
# the same directory the page is served out of.
cp "$bundle/main.mjs" browser/main.mjs

raw=$(find browser/_framework -type f ! -name '*.map' ! -name '*.symbols' -printf '%s\n' | awk '{s+=$1} END {print s}')
echo "modem bundle: $(numfmt --to=iec "$raw") in browser/_framework"
echo "serve the browser/ directory over https (or localhost) and open index.html"
