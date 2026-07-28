#!/usr/bin/env bash
# Managed-channel arms of the Poor re-anchor: for each datac mode, the SAME WattersonChannel Poor
# (MPP geometry: 2 equal Rayleigh paths, 2 ms, 1 Hz Gaussian Doppler) measured two ways —
#   burst  : independent fresh-fade per single-packet burst (the #104 baseline method), via `sim`
#   stream : N single-packet bursts through ONE continuous fade realisation (codec2's MPP method),
#            via `sim-stream`
# at a grid bracketing each mode's published MPP operating point. Isolates the METHODOLOGY axis on a
# fixed channel. SNR is true active-burst-power SNR in a 3 kHz noise bandwidth (both arms), directly
# comparable to codec2's published N/100.
set -u
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../../../.." && pwd)"
SMOTA="$ROOT/tools/Packet.SoundModem.Ota/bin/Release/net10.0/sm-ota.dll"
DATA="$HERE/data"
BURSTS="${BURSTS:-100}"
mkdir -p "$DATA"
export DOTNET_gcServer=0

run_mode() {
  local mode="$1" grid="$2"
  rm -f "$DATA/${mode}-poor-stream.csv" "$DATA/${mode}-poor-burst.csv"
  echo "=== $(date -u +%H:%M:%S) $mode stream ==="
  dotnet "$SMOTA" sim-stream --mode "$mode" --channel poor --snr "$grid" --bursts "$BURSTS" \
    --csv "$DATA/${mode}-poor-stream.csv" --quiet 2>/dev/null || echo "!! $mode stream $?"
  echo "=== $(date -u +%H:%M:%S) $mode burst ==="
  dotnet "$SMOTA" sim --mode "$mode" --layer packet --channel poor --snr "$grid" --bursts "$BURSTS" \
    --workers 4 --csv "$DATA/${mode}-poor-burst.csv" --quiet 2>/dev/null || echo "!! $mode burst $?"
}

#        mode            grid (true SNR dB, brackets the published op point)
run_mode freedv-datac0  "-4,-2,0,2,4"
run_mode freedv-datac14 "-6,-4,-2,0,2"
run_mode freedv-datac13 "-8,-6,-4,-2,0"
run_mode freedv-datac3  "-4,-2,0,2,4"
run_mode freedv-datac4  "-8,-6,-4,-2,0"
run_mode freedv-datac1  "1,3,5,7,9"
echo "=== $(date -u +%H:%M:%S) DONE ==="
touch "$DATA/../MANAGED_DONE"
