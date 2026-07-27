#!/usr/bin/env bash
# FreeDV datac OFDM simulation baseline — the full sweep behind this evidence dir.
#
# For every datac mode: AWGN + Watterson Poor + Good waterfalls at both the raw-packet layer
# (the FreeDV-comparable per-packet CRC/coded-BER currency) and the frame layer (the deployment
# IL2P+CRC metric), plus an absolute-level invariance probe (the WN2-analogue axis). All pure
# software — no radio. Native 8 kHz (codec2's own rate, the rate FreeDV's figures are measured at).
#
# Launch detached:
#   setsid bash <this> >/abs/path/run.log 2>&1 &
# Bounded to WORKERS parallelism; the box is a shared 16 GB LXC.
set -u

ROOT="/home/tf/pdn-freedv-sim"
SMOTA="$ROOT/tools/Packet.SoundModem.Ota/bin/Release/net10.0/sm-ota.dll"
DATA="$ROOT/docs/ms110d/evidence/2026-07-27-freedv-datac-sim-baseline/data"
WORKERS="${WORKERS:-4}"
PKT_BURSTS="${PKT_BURSTS:-200}"   # per-packet layer: tight CIs + directly compares to FreeDV's N/100
FRM_BURSTS="${FRM_BURSTS:-100}"   # frame layer: the deployment metric
LVL_BURSTS="${LVL_BURSTS:-60}"

export DOTNET_gcServer=0

run() {  # run <csv-suffix> <args...>
  local suffix="$1"; shift
  echo "=== $(date -u +%H:%M:%S) $suffix ==="
  dotnet "$SMOTA" sim "$@" --workers "$WORKERS" --csv "$DATA/$suffix.csv" --quiet \
    || echo "!! $suffix exited $?"
}

# mode | awgn grid | poor grid | good grid | level-probe SNRs (healthy,marginal)
# grids bracket each mode's knee; poor grids include the published MPP operating-point SNR.
sweep() {
  local mode="$1" awgn="$2" poor="$3" good="$4" lvlsnr="$5"
  run "$mode-awgn-packet"  --mode "$mode" --layer packet --channel awgn --snr "$awgn"  --bursts "$PKT_BURSTS"
  run "$mode-poor-packet"  --mode "$mode" --layer packet --channel poor --snr "$poor"  --bursts "$PKT_BURSTS"
  run "$mode-awgn-frame"   --mode "$mode" --layer frame  --channel awgn --snr "$awgn"  --bursts "$FRM_BURSTS"
  run "$mode-poor-frame"   --mode "$mode" --layer frame  --channel poor --snr "$poor"  --bursts "$FRM_BURSTS"
  run "$mode-good-frame"   --mode "$mode" --layer frame  --channel good --snr "$good"  --bursts "$FRM_BURSTS"
  run "$mode-levelprobe"   --mode "$mode" --layer packet --channel awgn --snr "$lvlsnr" \
      --levels -60,-40,-20,-10,-6,0,6,12,20 --bursts "$LVL_BURSTS"
}

#      mode            awgn                       poor              good                    lvlsnr
sweep freedv-datac0  "-6,-4,-3,-2,-1,0,2,4"     "-2,0,2,4,6,8"    "-6,-4,-2,0,2,4"        "6,0"
sweep freedv-datac1  "-1,0,1,2,3,4,5,6,8"       "1,3,5,7,9,11"    "-1,0,1,2,3,4,6"        "10,4"
sweep freedv-datac3  "-8,-6,-5,-4,-3,-2,-1,0,2" "-2,0,2,4,6,8"    "-8,-6,-4,-3,-2,0"      "6,-1"
sweep freedv-datac4  "-10,-8,-7,-6,-5,-4,-2,0"  "-6,-4,-2,0,2,4"  "-10,-8,-6,-5,-4,-2"    "2,-3"
sweep freedv-datac13 "-10,-8,-7,-6,-5,-4,-2,0"  "-6,-4,-2,0,2,4"  "-10,-8,-6,-5,-4,-2"    "2,-3"
sweep freedv-datac14 "-9,-7,-6,-5,-4,-3,-2,0"   "-6,-4,-2,0,2,4"  "-9,-7,-5,-4,-3,-1"     "3,-2"

echo "=== $(date -u +%H:%M:%S) DONE ==="
touch "$DATA/../SWEEP_DONE"
