#!/usr/bin/env bash
# Parallel mask test runner — fans out one process per WN point.
# Uses MTP (Microsoft Testing Platform) via dotnet test — no testhost, no teardown hang.
#
# Usage:
#   ./scripts/run-masks.sh awgn          # AWGN gate (10 parallel processes)
#   ./scripts/run-masks.sh poor          # Poor, measured-not-gated (10 parallel processes)
#   ./scripts/run-masks.sh "awgn poor"   # both sweeps in one invocation
#   ./scripts/run-masks.sh awgn 500000   # AWGN smoke (500k bits — logs labelled SMOKE)
#   ./scripts/run-masks.sh all           # AWGN + Poor + Static + Doppler
#
# Each process runs ONLY its own point (method-level filter + MS110D_MASK_WN), and writes
# its [mask] evidence line via the MS110D_MASK_LOG ledger hook (MTP does not relay test
# output to stdout). A PASS is only reported when the point's own [mask] line is present,
# so a run whose filter matched nothing (or whose theory case skipped) can never count as
# a pass — and MTP itself fails an all-skipped process ("Zero tests ran" policy).
#
# Requires: dotnet build first.
# Results: /tmp/mask-results/<suite>-wn<N>.log

set -uo pipefail
cd "$(dirname "$0")/.."

RESULTS_DIR="/tmp/mask-results"
mkdir -p "$RESULTS_DIR"

SUITES="${1:-awgn}"
BITS="${2:-}"
[[ "$SUITES" == "all" ]] && SUITES="awgn poor static doppler"

BITS_ENV=""
[[ -n "$BITS" ]] && BITS_ENV="MS110D_MASK_BITS=$BITS"

WNS="0 1 2 3 4 5 6 7 8 13"
PIDS=""

echo "Suites: $SUITES"
[[ -n "$BITS" ]] && echo "Bit budget: $BITS (SMOKE below 3M)"
echo "Results: $RESULTS_DIR/"
echo ""

for suite in $SUITES; do
    rm -f "$RESULTS_DIR/${suite}"*.log "$RESULTS_DIR/${suite}"*.mask
    case "$suite" in
        awgn)
            for wn in $WNS; do
                log="$RESULTS_DIR/awgn-wn${wn}.log"
                echo "[START] AWGN WN$wn → $log"
                env MS110D_MASKS=1 MS110D_MASK_WN=$wn $BITS_ENV \
                    MS110D_MASK_LOG="$RESULTS_DIR/awgn-wn${wn}.mask" \
                    dotnet test --no-build -- \
                    --filter-method "*.Awgn_Mask_Gate" \
                    > "$log" 2>&1 &
                PIDS="$PIDS $!"
            done
            ;;
        poor)
            for wn in $WNS; do
                log="$RESULTS_DIR/poor-wn${wn}.log"
                echo "[START] Poor WN$wn → $log"
                env MS110D_MASKS_POOR=1 MS110D_MASK_WN=$wn $BITS_ENV \
                    MS110D_MASK_LOG="$RESULTS_DIR/poor-wn${wn}.mask" \
                    dotnet test --no-build -- \
                    --filter-method "*.Poor_Channel_Mask_Gate" \
                    > "$log" 2>&1 &
                PIDS="$PIDS $!"
            done
            ;;
        static)
            log="$RESULTS_DIR/static.log"
            echo "[START] Static WID2 → $log"
            env MS110D_MASKS=1 $BITS_ENV \
                MS110D_MASK_LOG="$RESULTS_DIR/static.mask" \
                dotnet test --no-build -- \
                --filter-method "*.Static_Wid2_Gate" \
                > "$log" 2>&1 &
            PIDS="$PIDS $!"
            ;;
        doppler)
            log="$RESULTS_DIR/doppler.log"
            echo "[START] Doppler → $log"
            env MS110D_MASKS=1 $BITS_ENV \
                MS110D_MASK_LOG="$RESULTS_DIR/doppler.mask" \
                dotnet test --no-build -- \
                --filter-method "*.Doppler_Offset_Engineering_Check" \
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
echo "Process exits: $((total - failed))/$total zero, $failed nonzero"
echo "========================================"

for log in "$RESULTS_DIR"/*.log; do
    [[ -f "$log" ]] || continue
    name=$(basename "$log" .log)
    wn="${name##*-wn}"
    case "$name" in
        awgn-wn*) want="[mask] AWGN WN${wn} " ;;
        poor-wn*) want="[mask] POOR WN${wn} " ;;
        static)   want="[mask] Static WID2" ;;
        doppler)  want="[mask] Doppler offset" ;;
        *)        want="[mask]" ;;
    esac
    mask_line=$(grep -F "$want" "${log%.log}.mask" 2>/dev/null | tail -1)
    if ! grep -q "Passed!" "$log" 2>/dev/null; then
        echo "  FAIL: $name — ${mask_line:-no [mask] line for this point}"
    elif [[ -z "$mask_line" ]]; then
        echo "  VACUOUS: $name — process passed but this point's test never ran"
    else
        echo "  PASS: $name — ${mask_line#"[mask] "}"
    fi
done

exit $failed
