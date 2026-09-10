# B3.8 M2f: precision/recall of the re-lock trigger (causal accept with a blown
# probe floor) against the bad frames (frozen wire error >= 20%), plus the share
# of each block's frozen error mass sitting on triggered frames. Trigger:
# 1 <= lag <= 8 and n > THRESH (thermal-referenced; sweep a few values).
import csv
import re
import statistics
import sys

rx = re.compile(r"frozen-frame b(\d+) f(\d+): lag=(\d+) \|c\|=([\d.]+) n=([\d.E+-]+)")
frozen = {}
with open(sys.argv[2]) as f:
    for line in f:
        m = rx.search(line)
        if m:
            frozen.setdefault((int(m.group(1)), int(m.group(2))),
                              (int(m.group(3)), float(m.group(4)), float(m.group(5))))

errs = {}
with open(sys.argv[1]) as f:
    for row in csv.DictReader(f):
        key = (int(row["block"]), int(row["symbolInBlock"]) // 256)
        errs[key] = errs.get(key, 0) + 1

for thresh in (0.10, 0.15, 0.25):
    print(f"--- trigger: 1<=lag<=8 and n>{thresh}")
    for b in sorted({b for (b, _) in frozen}):
        fs = sorted(fr for (bb, fr) in frozen if bb == b)
        trig = [fr for fr in fs if 1 <= frozen[(b, fr)][0] <= 8 and frozen[(b, fr)][2] > thresh]
        bad = [fr for fr in fs if errs.get((b, fr), 0) / 768.0 >= 0.20]
        caught = len(set(trig) & set(bad))
        mass = sum(errs.get((b, fr), 0) for fr in fs)
        tmass = sum(errs.get((b, fr), 0) for fr in trig)
        print(f"  b{b}: bad={len(bad)} trig={len(trig)} recall={caught}/{len(bad)} "
              f"false={len(trig) - caught} errmass on trig={tmass}/{mass} "
              f"({100 * tmass / max(1, mass):.0f}%)")
