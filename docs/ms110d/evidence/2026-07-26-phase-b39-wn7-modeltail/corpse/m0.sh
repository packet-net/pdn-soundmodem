#!/usr/bin/env bash
# B3.9 M0: one ORACLE corpse run per WN7 residual burst (7 specimens),
# separate OUT dir per specimen (cross-SEED filename-collision rule).
set -uo pipefail
cd /home/tf/pdn-soundmodem
EXE=tests/Packet.SoundModem.Tests/bin/Release/net10.0/Packet.SoundModem.Tests
BASE=/home/tf/.claude/jobs/4be765aa/tmp/b39/m0
pids=""

run() {
    local dir=$1; shift
    mkdir -p "$BASE/$dir"
    env "$@" MS110D_AUTOPSY=1 MS110D_AUTOPSY_ORACLE=1 MS110D_AUTOPSY_OUT="$BASE/$dir" \
        "$EXE" -method "*.Mask_Burst_Corpse_Dump" > "$BASE/$dir/run.log" 2>&1 &
    local pid=$!
    choom -n 500 -p "$pid" 2>/dev/null || true
    pids="$pids $pid"
}

run c-w1b0 MS110D_AUTOPSY_WN=7 MS110D_AUTOPSY_WORKER=1 MS110D_AUTOPSY_BURST=0
run c-w1b1 MS110D_AUTOPSY_WN=7 MS110D_AUTOPSY_WORKER=1 MS110D_AUTOPSY_BURST=1
run c-w2b1 MS110D_AUTOPSY_WN=7 MS110D_AUTOPSY_WORKER=2 MS110D_AUTOPSY_BURST=1
run d-w0b0 MS110D_AUTOPSY_WN=7 MS110D_AUTOPSY_SEED=10507 MS110D_AUTOPSY_WORKER=0 MS110D_AUTOPSY_BURST=0
run d-w1b0 MS110D_AUTOPSY_WN=7 MS110D_AUTOPSY_SEED=10507 MS110D_AUTOPSY_WORKER=1 MS110D_AUTOPSY_BURST=0
run d-w2b1 MS110D_AUTOPSY_WN=7 MS110D_AUTOPSY_SEED=10507 MS110D_AUTOPSY_WORKER=2 MS110D_AUTOPSY_BURST=1
run d-w3b0 MS110D_AUTOPSY_WN=7 MS110D_AUTOPSY_SEED=10507 MS110D_AUTOPSY_WORKER=3 MS110D_AUTOPSY_BURST=0

rcs=""
for pid in $pids; do
    rc=0; wait "$pid" || rc=$?
    rcs="$rcs $rc"
done
echo "rc=$rcs" > "$BASE/all-done.txt"
