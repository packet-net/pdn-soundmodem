#!/usr/bin/env bash
# W0 comparison vs the b38 byte-identity baseline (see README.md). Run after battery.sh.
set -uo pipefail
cd "$(dirname "$0")"
OUT=/tmp/wn8-w0-battery
B38=../2026-07-26-phase-b38-wn7-anchortrack/battery

echo "== WN7 census byte-identity vs b38 =="
for w in 0 1 2 3; do
    for fam in "poor:3m" "poord:3md"; do
        mine="$OUT/census-${fam%%:*}-wn7-w${w}.csv"
        ref="$B38/census-wn7-${fam##*:}-wn7-w${w}.csv"
        if cmp -s "$mine" "$ref"; then echo "  IDENTICAL ${fam%%:*} w${w}"; else echo "  DIFFER    ${fam%%:*} w${w}  <-- DRIFT"; fi
    done
done

echo ""
echo "== W0 mask lines =="
for f in "$OUT"/*.mask; do
    grep -ao "\[mask\].*" "$f" | sed 's/ | uncoded.*//'
done | sort

echo ""
echo "== lane status =="
cat "$OUT/status.log"
