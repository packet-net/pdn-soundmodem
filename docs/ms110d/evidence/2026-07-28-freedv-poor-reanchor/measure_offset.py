#!/usr/bin/env python3
# Measure the inter-burst-silence SNR offset of a codec2 freedv_data_raw_tx output:
#   offset_dB = 10*log10(active/total), the amount ch's whole-stream SNR3k reads BELOW the true
#   active-burst SNR (ch averages signal power over the silent gaps between single-packet bursts).
# Prints the offset (dB, negative). Usage: measure_offset.py <mode> <codec2_src_dir>
import subprocess, sys, struct, math

mode, src = sys.argv[1], sys.argv[2]
raw = subprocess.run(
    [f"{src}/freedv_data_raw_tx", "--testframes", "100", "--bursts", "100",
     "--framesperburst", "1", mode, "/dev/zero", "-"],
    capture_output=True).stdout
n = len(raw) // 2
s = struct.unpack(f"<{n}h", raw[:n * 2])
rms = math.sqrt(sum(v * v for v in s) / n)
thr = rms * 0.02
active = sum(1 for v in s if abs(v) > thr)
print(f"{10 * math.log10(active / n):.3f}")
