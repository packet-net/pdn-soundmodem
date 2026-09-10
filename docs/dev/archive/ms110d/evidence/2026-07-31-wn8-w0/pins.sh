#!/usr/bin/env bash
# W0 guard-pin corpse runs — closeout §6 registry (see README.md).
# Usage: bash docs/ms110d/evidence/2026-07-31-wn8-w0/pins.sh   (after dotnet build)
set -uo pipefail
cd "$(dirname "$0")/../../../.."

OUT=/tmp/wn8-w0-pins
mkdir -p "$OUT"

pin() { # name extra_env...
    local name="$1"; shift
    mkdir -p "$OUT/$name"
    env MS110D_AUTOPSY=1 MS110D_AUTOPSY_OUT="$OUT/$name" "$@" \
        dotnet test --no-build -- --filter-method "*.Mask_Burst_Corpse_Dump" \
        > "$OUT/$name.log" 2>&1
    echo "== $name rc=$? =="
    grep -aE "coded|turbo|oracle" "$OUT/$name.log" | tail -5
}

pin wn7-w0b0-oracle MS110D_AUTOPSY_WN=7 MS110D_AUTOPSY_WORKER=0 MS110D_AUTOPSY_BURST=0 MS110D_AUTOPSY_ORACLE=1
pin wn7-w1b0        MS110D_AUTOPSY_WN=7 MS110D_AUTOPSY_WORKER=1 MS110D_AUTOPSY_BURST=0
pin wn6-w0b0        MS110D_AUTOPSY_WN=6 MS110D_AUTOPSY_WORKER=0 MS110D_AUTOPSY_BURST=0
pin wn13sp-w3b5     MS110D_AUTOPSY_WN=13 MS110D_AUTOPSY_SEED=10513 MS110D_AUTOPSY_WORKER=3 MS110D_AUTOPSY_BURST=5
pin wn0-w2b97       MS110D_AUTOPSY_WN=0 MS110D_AUTOPSY_WORKER=2 MS110D_AUTOPSY_BURST=97
