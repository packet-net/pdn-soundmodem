#!/usr/bin/env bash
# G0 guard-pin corpse runs - the Phase B closeout §6 registry, on the Release test exe.
# Usage: bash docs/ms110d/evidence/2026-08-20-poorgate-g0/pins.sh [OUT]   (after dotnet build -c Release)
set -uo pipefail
cd "$(dirname "$0")/../../../.."

EXE="$PWD/tests/Packet.SoundModem.Tests/bin/Release/net10.0/Packet.SoundModem.Tests"
OUT="${1:-/tmp/poorgate-g0-pins}"
mkdir -p "$OUT"
echo "HEAD=$(git rev-parse --short HEAD) exe=$(stat -c %y "$EXE")" > "$OUT/provenance.txt"

pin() { # name extra_env...
    local name="$1"; shift
    mkdir -p "$OUT/$name"
    env MS110D_AUTOPSY=1 MS110D_AUTOPSY_OUT="$OUT/$name" "$@" \
        "$EXE" -method "*.Mask_Burst_Corpse_Dump" > "$OUT/$name.log" 2>&1
    echo "== $name rc=$? ==" | tee -a "$OUT/summary.txt"
    grep -ahE "coded|turbo|oracle" "$OUT/$name.log" | tail -5 | tee -a "$OUT/summary.txt"
}

pin wn7-w0b0-oracle MS110D_AUTOPSY_WN=7 MS110D_AUTOPSY_WORKER=0 MS110D_AUTOPSY_BURST=0 MS110D_AUTOPSY_ORACLE=1
pin wn7-w1b0        MS110D_AUTOPSY_WN=7 MS110D_AUTOPSY_WORKER=1 MS110D_AUTOPSY_BURST=0
pin wn6-w0b0        MS110D_AUTOPSY_WN=6 MS110D_AUTOPSY_WORKER=0 MS110D_AUTOPSY_BURST=0
pin wn13sp-w3b5     MS110D_AUTOPSY_WN=13 MS110D_AUTOPSY_SEED=10513 MS110D_AUTOPSY_WORKER=3 MS110D_AUTOPSY_BURST=5
pin wn0-w2b97       MS110D_AUTOPSY_WN=0 MS110D_AUTOPSY_WORKER=2 MS110D_AUTOPSY_BURST=97
echo "PINS COMPLETE" >> "$OUT/summary.txt"
