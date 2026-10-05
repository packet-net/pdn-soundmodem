#!/bin/sh
# The filter-study sweeps, exactly as run for this evidence. Each CSV is appended point by point
# and a rerun skips the points it already holds, so an interrupted sweep resumes where it stopped.
# Build first: dotnet build tools/Packet.SoundModem.Ota -c Release (from the repo root).
set -e
here=$(dirname "$0")
ota="dotnet $here/../../../../../tools/Packet.SoundModem.Ota/bin/Release/net10.0/sm-ota.dll filter-study"
awgn="-3,-2.5,-2,-1.5,-1,-0.5,0,0.5,1,1.5,2,2.5,3,3.5,4,5,6"
fading="0,2,4,6,8,10,12,14,16,18,20,22"
common="--modes 3,4 --channels awgn,moderate,poor --snr-awgn $awgn --snr-moderate $fading --snr-poor $fading --bursts 40 --abandon-after 5 --workers ${WORKERS:-8}"

# 1. The main sweep: 960-byte frames, every roll-off, both receivers, the crystal filters as
#    rigs place them (centred at 1500 Hz audio) and the 2.4 kHz one centred on the signal.
case "${1:-all}" in all|1)
$ota $common --payload-bytes 960 \
  --tx-rolloff 0.35,0.25,0.15,0.10,0.05 --rx standard,matched \
  --filters none,xtal:300-2700,xtal:200-2900,xtal:150-3150,xtal:600-3000 \
  --csv "$here/data/main-960B.csv"
esac

# 2. The DSP filter shape, same widths, the widest and narrowest roll-offs, the receiver as it is.
case "${1:-all}" in all|2)
$ota $common --payload-bytes 960 \
  --tx-rolloff 0.35,0.05 --rx standard \
  --filters dsp:300-2700,dsp:200-2900,dsp:150-3150,dsp:600-3000 \
  --csv "$here/data/dsp-960B.csv"
esac

# 3. Short frames: 255 bytes, the narrow filter as placed and centred.
case "${1:-all}" in all|3)
$ota $common --payload-bytes 255 \
  --tx-rolloff 0.35,0.05 --rx standard \
  --filters none,xtal:300-2700,xtal:600-3000 \
  --csv "$here/data/short-255B.csv"
esac
