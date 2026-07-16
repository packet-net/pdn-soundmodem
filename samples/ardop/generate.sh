#!/bin/sh
# Regenerates the ardopcf oracle TX fixtures in this directory.
#
# Requires an ardopcf binary built from git a7c9228 (see PROVENANCE.md):
#   git clone --depth 1 https://github.com/pflarue/ardop && cd ardop && make
# Usage: ARDOPCF=/path/to/ardopcf sh generate.sh
#
# Each WAV is one TXFRAME transmission (leader 240 ms, trailer 20 ms, drive 100)
# written via ardopcf's null-device TX path (--writetxwav, NOSOUND audio) — no sound
# card involved. Payloads live in txframe-manifest.txt, regenerated alongside.
set -eu

ARDOPCF="${ARDOPCF:-ardopcf}"
DIR="$(cd "$(dirname "$0")" && pwd)"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

# name|txframe-args (payload hex comes from the manifest generator below)
python3 "$DIR/gen-payloads.py" > "$TMP/jobs.txt"

while IFS='|' read -r NAME ARGS; do
    ( cd "$TMP" && rm -f ARDOP_txfaudio_*.wav
      "$ARDOPCF" --nologfile --writetxwav \
        --hostcommands "MYCALL PDNSND;TXFRAME $ARGS;CLOSE" 8515 NOSOUND NOSOUND \
        >/dev/null 2>&1 || true
      mv ARDOP_txfaudio_*.wav "$DIR/txframe_$NAME.wav" )
    echo "generated txframe_$NAME.wav"
done < "$TMP/jobs.txt"
