#!/usr/bin/env bash
# Parallel mask test runner — fans out one process per WN point.
# Uses MTP (Microsoft Testing Platform) via dotnet test — no testhost, no teardown hang.
#
# Usage:
#   ./scripts/run-masks.sh awgn         # AWGN (10 parallel processes)
#   ./scripts/run-masks.sh poor         # Poor (10 parallel processes)
#   ./scripts/run-masks.sh awgn 500000  # AWGN smoke (500k bits)
#   ./scripts/run-masks.sh all          # AWGN + Poor + Static + Doppler
#
# Requires: dotnet build first.
# Results: /tmp/mask-results/<suite>-wn<N>.log

set -uo pipefail
cd "$(dirname "$0")/.."

RESULTS_DIR="/tmp/mask-results"
mkdir -p "$RESULTS_DIR"
rm -f "$RESULTS_DIR"/*.log

SUITES="${1:-awgn}"
BITS="${2:-}"
[[ "$SUITES" == "all" ]] && SUITES="awgn poor static doppler"

BITS_ENV=""
[[ -n "$BITS" ]] && BITS_ENV="MS110D_MASK_BITS=$BITS"

WNS="0 1 2 3 4 5 6 7 8 13"
PIDS=""

echo "Suites: $SUITES"
[[ -n "$BITS" ]] && echo "Bit budget: $BITS"
echo "Results: $RESULTS_DIR/"
echo ""

for suite in $SUITES; do
    case "$suite" in
        awgn)
            for wn in $WNS; do
                log="$RESULTS_DIR/awgn-wn${wn}.log"
                echo "[START] AWGN WN$wn → $log"
                env MS110D_MASKS=1 MS110D_MASK_WN=$wn $BITS_ENV \
                    dotnet test --no-build -- \
                    --filter-class "Packet.SoundModem.Tests.Ms110d.Ms110dMaskTests" \
                    > "$log" 2>&1 &
                PIDS="$PIDS $!"
            done
            ;;
        poor)
            for wn in $WNS; do
                log="$RESULTS_DIR/poor-wn${wn}.log"
                echo "[START] Poor WN$wn → $log"
                env MS110D_MASKS_POOR=1 MS110D_MASK_WN=$wn $BITS_ENV \
                    dotnet test --no-build -- \
                    --filter-class "Packet.SoundModem.Tests.Ms110d.Ms110dMaskTests" \
                    > "$log" 2>&1 &
                PIDS="$PIDS $!"
            done
            ;;
        static)
            log="$RESULTS_DIR/static.log"
            echo "[START] Static WID2 → $log"
            env MS110D_MASKS=1 $BITS_ENV \
                dotnet test --no-build -- \
                --filter-class "Packet.SoundModem.Tests.Ms110d.Ms110dMaskTests" \
                > "$log" 2>&1 &
            PIDS="$PIDS $!"
            ;;
        doppler)
            log="$RESULTS_DIR/doppler.log"
            echo "[START] Doppler → $log"
            env MS110D_MASKS=1 $BITS_ENV \
                dotnet test --no-build -- \
                --filter-class "Packet.SoundModem.Tests.Ms110d.Ms110dMaskTests" \
                > "$log" 2>&1 &
            PIDS="$PIDS $!"
            ;;
    esac
done

echo ""
echo "Launched $(echo $PIDS | wc -w) processes. Waiting..."
failed=0
for pid in $PIDS; do
    wait "$pid" || ((failed++))
done

echo ""
echo "========================================"
total=$(echo $PIDS | wc -w)
echo "Results: $((total - failed))/$total passed, $failed failed"
echo "========================================"

for log in "$RESULTS_DIR"/*.log; do
    [[ -f "$log" ]] || continue
    name=$(basename "$log" .log)
    mask_line=$(grep "\[mask\]" "$log" 2>/dev/null | tail -1)
    if grep -q "Passed!" "$log" 2>/dev/null; then
        echo "  PASS: $name"
    elif [[ -n "$mask_line" ]]; then
        ber=$(echo "$mask_line" | grep -oP 'BER \S+' || echo '?')
        echo "  FAIL: $name ($ber)"
    else
        echo "  ???:  $name"
    fi
done

exit $failed
