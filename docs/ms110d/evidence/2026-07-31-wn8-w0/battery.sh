#!/usr/bin/env bash
# W0 entry re-baseline battery — the b38 three-lane form (see README.md).
# Usage: setsid bash docs/ms110d/evidence/2026-07-31-wn8-w0/battery.sh
# Requires: dotnet build first. NEVER rebuild while this runs.
set -uo pipefail
cd "$(dirname "$0")/../../../.."

OUT=/tmp/wn8-w0-battery
mkdir -p "$OUT"
STATUS="$OUT/status.log"
echo "$(date +%H:%M:%S) battery start, HEAD=$(git rev-parse --short HEAD)" >> "$STATUS"

point() { # lane suite wn extra_env...
    local lane="$1" suite="$2" wn="$3"; shift 3
    local tag="${suite}-wn${wn}"
    local filter="*.Poor_Channel_Mask_Gate"
    local arm="MS110D_MASKS_POOR=1"
    if [[ "$suite" == awgn ]]; then filter="*.Awgn_Mask_Gate"; arm="MS110D_MASKS=1"; fi
    env $arm MS110D_MASK_WN="$wn" MS110D_MASK_WORKERS=4 "$@" \
        MS110D_MASK_LOG="$OUT/${tag}.mask" \
        MS110D_MASK_BURST_LOG="$OUT/census-${suite}" \
        dotnet test --no-build -- --filter-method "$filter" > "$OUT/${tag}.log" 2>&1
    echo "$(date +%H:%M:%S) ${lane} done ${tag} rc=$?" >> "$STATUS"
}

lane_a() {
    choom -n 500 -p "$BASHPID" 2>/dev/null || true
    for wn in 5 6 2 13 3 4 1 0 8 7; do point lane-A poor "$wn"; done
    echo "$(date +%H:%M:%S) lane-A LANE A COMPLETE" >> "$STATUS"
}
lane_b() {
    choom -n 500 -p "$BASHPID" 2>/dev/null || true
    for wn in 5 6 2 13 3 4 1 0 8 7; do point lane-B poord "$wn" MS110D_MASK_SEED_OFFSET=10000; done
    echo "$(date +%H:%M:%S) lane-B LANE B COMPLETE" >> "$STATUS"
}
lane_c() {
    choom -n 500 -p "$BASHPID" 2>/dev/null || true
    for wn in 0 1 2 3 4 5 6 7 8 13; do point lane-C awgn "$wn"; done
    env MS110D_MASKS=1 MS110D_MASK_WORKERS=4 MS110D_MASK_LOG="$OUT/static.mask" \
        dotnet test --no-build -- --filter-method "*.Static_Wid2_Gate" > "$OUT/static.log" 2>&1
    echo "$(date +%H:%M:%S) lane-C done static rc=$?" >> "$STATUS"
    env MS110D_MASKS=1 MS110D_MASK_LOG="$OUT/doppler.mask" \
        dotnet test --no-build -- --filter-method "*.Doppler_Offset_Engineering_Check" > "$OUT/doppler.log" 2>&1
    echo "$(date +%H:%M:%S) lane-C done doppler rc=$?" >> "$STATUS"
    echo "$(date +%H:%M:%S) lane-C LANE C COMPLETE" >> "$STATUS"
}

lane_a & A=$!
lane_b & B=$!
lane_c & C=$!
wait "$A" "$B" "$C"
echo "$(date +%H:%M:%S) BATTERY COMPLETE" >> "$STATUS"
