#!/usr/bin/env bash
# Everything this proof of concept claims, re-measured. Needs ./build.sh first, and
# `npm install` in browser/test for the two-station harness.
set -uo pipefail
cd "$(dirname "$0")"

samples=../samples/ninotnc
native=parity/bin/Release/net10.0/sm-wasm-parity
scratch=$(mktemp -d)
trap 'rm -rf "$scratch"' EXIT
failures=0

echo "== decode parity, wasm against native =="
for pair in "afsk1200.wav afsk1200" "afsk1200-il2p.wav afsk1200-il2p" "afsk300.wav afsk300" \
            "afsk300-il2pc.wav afsk300-il2pc" "bpsk300.wav bpsk300" "bpsk1200.wav bpsk1200" \
            "qpsk600.wav qpsk600" "qpsk2400.wav qpsk2400" "qpsk3600.wav qpsk3600"; do
  set -- $pair
  (cd browser && node main.mjs decode "../$samples/$1" "$2" 2>/dev/null) | grep -E '^\[' > "$scratch/w"
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
  line=$( (cd browser && node main.mjs loopback "$mode" 2>/dev/null) | grep 'round trip' )
  echo "  $line"
  [[ "$line" == *IDENTICAL* ]] || failures=$((failures + 1))
done

echo "== two stations, a whole AX.25 session over the modem =="
for mode in afsk1200 bpsk300 qpsk2400 fsk9600; do
  out=$( (cd browser/test && node two-stations.mjs "$mode" 2>/dev/null) )
  printf '  %-14s %s\n' "$mode" "$(echo "$out" | tail -1)"
  [[ "$out" == *"all checks passed"* ]] || failures=$((failures + 1))
done

echo
[[ $failures -eq 0 ]] && echo "all good" || echo "$failures failure(s)"
exit $((failures == 0 ? 0 : 1))
