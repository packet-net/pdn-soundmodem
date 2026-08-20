#!/usr/bin/env bash
# G0 comparison vs the byte-identity baselines (see README.md). Run after battery.sh.
#   WN8 points compare against the W6 decision battery (the closing state of the WN8 program);
#   every other point compares against the W0 re-baseline battery (byte-identical to b38).
# Usage: bash compare.sh [OUT]
set -uo pipefail
cd "$(dirname "$0")"
OUT="${1:-/tmp/poorgate-g0-battery}"
W0=../2026-07-31-wn8-w0/battery
W6=../2026-07-31-wn8-w6/battery
W5=../2026-07-31-wn8-w5b2/battery   # the only battery that kept AWGN WN8 censuses
W7=../2026-08-20-poorgate-g1d/battery   # WN7's baseline since G1d (the 8PSK ensemble, 0/0)

echo "== census byte-identity (every point x worker) =="
ident=0; differ=0; missing=0
for mine in "$OUT"/census-*.csv; do
    name=$(basename "$mine")
    if [[ "$name" == census-awgn-wn8-* ]]; then ref="$W5/$name"
    elif [[ "$name" == *-wn8-* ]]; then ref="$W6/$name"
    elif [[ "$name" == *-wn7-* ]]; then ref="$W7/census/$name"
    else ref="$W0/census/$name"; fi
    if [[ ! -f "$ref" ]]; then echo "  NO BASELINE $name"; missing=$((missing+1)); continue; fi
    if cmp -s "$mine" "$ref"; then ident=$((ident+1)); else echo "  DIFFER    $name  <-- DRIFT"; differ=$((differ+1)); fi
done
echo "  identical=$ident differ=$differ no-baseline=$missing"

echo ""
echo "== mask-line digits (bits, errors) vs baseline =="
for mine in "$OUT"/*.mask; do
    name=$(basename "$mine")
    if [[ "$name" == *wn8* ]]; then ref="$W6/$name"; elif [[ "$name" == *wn7* ]]; then ref="$W7/$name"; else ref="$W0/$name"; fi
    m=$(grep -ao "\[mask\].*" "$mine" | sed -E 's/.*: ([0-9,]+) bits, ([0-9]+) errors.*/\1 bits \2 errors/' | sort | paste -sd';')
    if [[ -f "$ref" ]]; then
        r=$(grep -ao "\[mask\].*" "$ref" | sed -E 's/.*: ([0-9,]+) bits, ([0-9]+) errors.*/\1 bits \2 errors/' | sort | paste -sd';')
        if [[ "$m" == "$r" ]]; then echo "  SAME   $name: $m"; else echo "  DIFFER $name: now [$m] baseline [$r]  <-- DRIFT"; fi
    else
        echo "  NOREF  $name: $m"
    fi
done | sort

echo ""
echo "== mask lines =="
for f in "$OUT"/*.mask; do grep -ao "\[mask\].*" "$f" | sed 's/ | uncoded.*//'; done | sort

echo ""
echo "== lane status =="
cat "$OUT/status.log"
