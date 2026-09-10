#!/usr/bin/env bash
# B3.10 M0: WN8 oracle corpse runs, one per family, separate OUT dirs.
set -uo pipefail
cd /home/tf/pdn-soundmodem
EXE=tests/Packet.SoundModem.Tests/bin/Release/net10.0/Packet.SoundModem.Tests
BASE=/home/tf/.claude/jobs/4be765aa/tmp/b39/m1-wn8
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

run c-w0b0 MS110D_AUTOPSY_WN=8 MS110D_AUTOPSY_WORKER=0 MS110D_AUTOPSY_BURST=0
run d-w0b0 MS110D_AUTOPSY_WN=8 MS110D_AUTOPSY_SEED=10508 MS110D_AUTOPSY_WORKER=0 MS110D_AUTOPSY_BURST=0

rcs=""
for pid in $pids; do
    rc=0; wait "$pid" || rc=$?
    rcs="$rcs $rc"
done
echo "rc=$rcs" > "$BASE/all-done.txt"
