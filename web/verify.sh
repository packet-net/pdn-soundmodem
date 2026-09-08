#!/usr/bin/env bash
# Everything this package claims, re-measured. Needs ./build.sh first, and `npm install` in
# test/ for the two-station harness.
set -uo pipefail
cd "$(dirname "$0")"

samples=../samples/ninotnc
native=parity/bin/Release/net10.0/sm-wasm-parity
scratch=$(mktemp -d)
trap 'rm -rf "$scratch"' EXIT
failures=0

echo "== decode parity, WebAssembly against native =="
for pair in "afsk1200.wav afsk1200" "afsk1200-il2p.wav afsk1200-il2p" "afsk300.wav afsk300" \
            "afsk300-il2pc.wav afsk300-il2pc" "bpsk300.wav bpsk300" "bpsk1200.wav bpsk1200" \
            "qpsk600.wav qpsk600" "qpsk2400.wav qpsk2400" "qpsk3600.wav qpsk3600"; do
  set -- $pair
  node test/decode.mjs decode "$samples/$1" "$2" 2>/dev/null | grep -E '^\[' > "$scratch/w"
  $native decode "$samples/$1" "$2" 2>/dev/null | grep -E '^\[' > "$scratch/n"
  if diff -q "$scratch/w" "$scratch/n" > /dev/null; then
    echo "  IDENTICAL  $2  $(grep -c '^\[' "$scratch/w") frames"
  else
    echo "  DIFFERS    $2"; failures=$((failures + 1))
  fi
done

echo "== transmit round trip, modulate into demodulate =="
for mode in afsk1200 afsk1200-il2p afsk300-il2pc bpsk300 bpsk1200 qpsk600 qpsk2400 qpsk3600 \
            fsk9600 fsk9600-il2p fsk4800-il2p; do
  line=$(node test/decode.mjs loopback "$mode" 2>/dev/null | grep 'round trip')
  echo "  $line"
  [[ "$line" == *IDENTICAL* ]] || failures=$((failures + 1))
done

echo "== two stations, a whole AX.25 session over the modem =="
for mode in afsk1200 bpsk300 qpsk2400 fsk9600; do
  out=$( (cd test && node two-stations.mjs "$mode" 2>/dev/null) )
  printf '  %-14s %s\n' "$mode" "$(echo "$out" | tail -1)"
  [[ "$out" == *"all checks passed"* ]] || failures=$((failures + 1))
done

echo "== the npm package, packed and loaded as a consumer would =="
# The bundle's boot config names every asset it will fetch, and the loader treats a missing
# one as fatal rather than optional. So the test is not "does the tarball look right", it is
# "does everything the manifest asks for survive the `files` list" - which is how excluding
# the symbol map was caught.
if command -v npm > /dev/null; then
  tarball=$(cd package && npm pack --silent --pack-destination "$scratch")
  tar xzf "$scratch/$tarball" -C "$scratch"
  packed="$scratch/package"
  for asset in $(grep -oE '"[A-Za-z0-9_.\-]+\.(wasm|js|symbols|dat)"' package/_framework/dotnet.boot.js | tr -d '"' | sort -u); do
    if [[ ! -f "$packed/_framework/$asset" ]]; then
      echo "  MISSING from the package: $asset"; failures=$((failures + 1))
    fi
  done
  modes=$(cd "$packed" && node --input-type=module -e "
    import { dotnet } from './_framework/dotnet.js'
    const rt = await dotnet.withDiagnosticTracing(false).create()
    const M = (await rt.getAssemblyExports(rt.getConfig().mainAssemblyName)).Modem
    console.log(M.Modes().length)
  " 2>/dev/null | tail -1)
  if [[ "${modes:-0}" -gt 0 ]]; then
    echo "  packed bundle loads, $modes modes"
  else
    echo "  packed bundle FAILED to load"; failures=$((failures + 1))
  fi
else
  echo "  npm not found: skipped"
fi

echo
[[ $failures -eq 0 ]] && echo "all good" || echo "$failures failure(s)"
exit $((failures == 0 ? 0 : 1))
