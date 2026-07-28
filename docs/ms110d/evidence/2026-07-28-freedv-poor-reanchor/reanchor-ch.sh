#!/usr/bin/env bash
# codec2 `ch` cross-check arms of the Poor re-anchor — the CHANNEL-MODEL axis and the published anchor.
#
# For each datac mode, two arms through codec2's OWN MPP channel (src/ch.c, --mpp: 2 paths 0/2 ms,
# 1 Hz Gaussian Doppler, one continuous 700 s fading file):
#   ourmodem : OUR managed datac modem's audio (emit-raw) -> ch --mpp -> OUR rx (decode-raw)
#   anchor   : codec2's OWN freedv_data_raw_tx -> ch --mpp -> freedv_data_raw_rx  (self-consistency:
#              must reproduce the published N/100, validating ch + the fading file)
#
# SNR is codec2's own SNR3k, corrected to the true active-burst SNR by the inter-burst-silence offset
# (ch averages signal power over the silent gaps too, biasing SNR3k low): trueSNR = SNR3k - offset,
# offset = 10*log10(active/total) < 0. For `ourmodem` the offset is exact (our manifest); for `anchor`
# it is measured from codec2's tx output (active_frac.py).
#
# Prereqs (documented in README):
#   CODEC2_BUILD  = a built codec2 tree's build_linux dir (ch, freedv_data_raw_{tx,rx} under src/)
#   FADING_DIR    = dir holding fast_fading_samples.float from  gen_fading <f> 8000 1.0 700 1
set -u
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../../../.." && pwd)"
SMOTA="$ROOT/tools/Packet.SoundModem.Ota/bin/Release/net10.0/sm-ota.dll"
SRC="${CODEC2_BUILD:?set CODEC2_BUILD to codec2 build_linux}/src"
FAD="${FADING_DIR:?set FADING_DIR to the fading-file dir}"
WORK="${WORK:-/tmp/codec2-reanchor/work}"; mkdir -p "$WORK"
OUT="$HERE/data/ch-crosscheck.csv"
BURSTS="${BURSTS:-100}"
export DOTNET_gcServer=0
echo "mode,arm,No,snr3k,trueSNR,recv,N" > "$OUT"

# our modem through ch
ourmodem() {
  local mode="$1" nlist="$2"
  local raw="$WORK/${mode}_s.s16" man="$WORK/${mode}_m.txt"
  dotnet "$SMOTA" sim-stream --mode "$mode" --emit-raw "$raw" --manifest "$man" --bursts "$BURSTS" 2>/dev/null
  for No in $nlist; do
    "$SRC/ch" "$raw" "$WORK/o.s16" --No "$No" --mpp --fading_dir "$FAD" 2>"$WORK/ch.err"
    local snr res ok nn off tsnr
    snr=$(grep SNR3k "$WORK/ch.err" | tr -s ' ' | cut -d' ' -f3)
    res=$(dotnet "$SMOTA" sim-stream --mode "$mode" --decode-raw "$WORK/o.s16" --manifest "$man" --quiet 2>/dev/null)
    ok=$(echo "$res" | cut -d' ' -f1); nn=$(echo "$res" | cut -d' ' -f2); off=$(echo "$res" | cut -d' ' -f3)
    tsnr=$(python3 -c "print(f'{$snr-($off):.2f}')")
    echo "$mode,ourmodem,$No,$snr,$tsnr,$ok,$nn" >> "$OUT"
    printf "  %-14s ourmodem No=%-4s SNR3k=%-7s true=%-7s %s/%s\n" "$mode" "$No" "$snr" "$tsnr" "$ok" "$nn"
  done
}

# codec2 tx -> ch -> codec2 rx (the anchor). $mode here is the codec2 mode name (datacN, no freedv-)
anchor() {
  local mode="$1" nlist="$2"
  local off
  off=$(python3 "$HERE/measure_offset.py" "$mode" "$SRC")
  for No in $nlist; do
    "$SRC/freedv_data_raw_tx" --testframes "$BURSTS" --bursts "$BURSTS" --framesperburst 1 "$mode" /dev/zero - 2>/dev/null \
      | "$SRC/ch" - - --No "$No" --mpp --fading_dir "$FAD" 2>"$WORK/ch.err" \
      | "$SRC/freedv_data_raw_rx" --testframes "$mode" - /dev/null -v 2>"$WORK/rx.err"
    local snr line tfrms tfers recv tsnr
    snr=$(grep SNR3k "$WORK/ch.err" | tr -s ' ' | cut -d' ' -f3)
    line=$(grep 'Coded FER' "$WORK/rx.err" | tr -s ' ')
    tfrms=$(echo "$line" | sed 's/.*Tfrms: *//' | cut -d' ' -f1)
    tfers=$(echo "$line" | sed 's/.*Tfers: *//' | cut -d' ' -f1)
    recv=$(( ${tfrms:-0} - ${tfers:-0} ))
    tsnr=$(python3 -c "print(f'{$snr-($off):.2f}')")
    echo "$mode,anchor,$No,$snr,$tsnr,$recv,$BURSTS" >> "$OUT"
    printf "  %-14s anchor   No=%-4s SNR3k=%-7s true=%-7s %s/%s\n" "$mode" "$No" "$snr" "$tsnr" "$recv" "$BURSTS"
  done
}

# mode | No list — chosen so ch's SNR3k brackets the published op point in TRUE SNR. The narrow
# low-rate modes (datac4/13/14, <=250 Hz OBW) need far more noise to reach a negative 3 kHz-ref SNR.
ourmodem freedv-datac0  "-12 -13 -14 -15 -16";  anchor datac0  "-12 -13 -14 -15 -16"
ourmodem freedv-datac3  "-13 -14 -15 -16 -17";  anchor datac3  "-13 -14 -15 -16 -17"
ourmodem freedv-datac1  "-18 -19 -20 -21 -22";  anchor datac1  "-18 -19 -20 -21 -22"
ourmodem freedv-datac14 "-9 -10 -11 -12 -13";   anchor datac14 "-9 -10 -11 -12 -13"
ourmodem freedv-datac13 "-8 -9 -10 -11 -12";    anchor datac13 "-8 -9 -10 -11 -12"
ourmodem freedv-datac4  "-8 -9 -10 -11 -12";    anchor datac4  "-8 -9 -10 -11 -12"
echo "wrote $OUT"
