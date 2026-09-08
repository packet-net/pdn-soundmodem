#!/usr/bin/env bash
# Builds the WebAssembly modem into the npm package, and emits its type declarations.
#
#   ./build.sh            trimmed, interpreted - ~1.4 MB gzipped, real-time on every mode
#   ./build.sh --aot      AOT compiled - ~2.8 MB gzipped, roughly native speed (what ships)
#
# Both produce the same decodes; the difference is download size against CPU. Needs the
# `wasm-tools` dotnet workload, and `wasm-experimental` too if you want the templates.
set -euo pipefail
cd "$(dirname "$0")"

args=(-c Release)
if [[ "${1:-}" == "--aot" ]]; then
  args+=(-p:RunAOTCompilation=true -p:WasmStripILAfterAOT=true)
fi

dotnet publish modem/Packet.SoundModem.Wasm.csproj "${args[@]}"

bundle=modem/bin/Release/net10.0/browser-wasm/AppBundle
rm -rf package/_framework
cp -r "$bundle/_framework" package/_framework

# Types come from the JSDoc on the shipped JavaScript, so there is no transpiled copy that
# could drift from the code that was tested. Optional: only a publish actually needs them.
if command -v npm > /dev/null; then
  npm --prefix package install --silent --no-audit --no-fund
  npm --prefix package run --silent types
else
  echo "npm not found: skipping type declarations (fine for a local run, not for publishing)"
fi

raw=$(find package/_framework -type f ! -name '*.map' ! -name '*.symbols' -printf '%s\n' | awk '{s+=$1} END {print s}')
echo
echo "bundle: $(numfmt --to=iec "$raw") in package/_framework"
echo "demo:   serve this directory and open demo/ (https, or localhost)"
