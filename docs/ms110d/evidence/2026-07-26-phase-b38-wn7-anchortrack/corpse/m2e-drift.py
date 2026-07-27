# B3.8 M2e: within-frame ECHO drift as the bad-frame mechanism. For each frame:
# oracle endpoint drift of h2 (|h2[3]-h2[0]|) and of h1, their power against the
# oracle noise floor (a frame-constant model mismatches each frame end by half
# the drift: err power ~ |d|^2/4), split by the frozen pass's per-frame wire
# error (bad >= 20%). h1's drift is tracked by the frozen lerp, h2's is not —
# if bad agree-frames show |d2|^2/(8n) >> 1 and >> good frames, the mechanism is
# the frame-constant echo coefficient.
import csv
import re
import statistics
import sys

rx_frozen = re.compile(r"frozen-frame b(\d+) f(\d+): lag=(\d+)")
rx_oracle = re.compile(
    r"turbo-frame b(\d+) f(\d+): lag=(\d+) lag2=(\d+) n=([\d.E+-]+) .*? h1=(-?[\d.]+),(-?[\d.]+)\|"
    r"(-?[\d.]+),(-?[\d.]+)\|(-?[\d.]+),(-?[\d.]+)\|(-?[\d.]+),(-?[\d.]+) "
    r"h2avg=(-?[\d.]+),(-?[\d.]+) h2=(-?[\d.]+),(-?[\d.]+)\|(-?[\d.]+),(-?[\d.]+)\|"
    r"(-?[\d.]+),(-?[\d.]+)\|(-?[\d.]+),(-?[\d.]+)")

frozen = {}
with open(sys.argv[2]) as f:
    for line in f:
        m = rx_frozen.search(line)
        if m:
            frozen.setdefault((int(m.group(1)), int(m.group(2))), int(m.group(3)))

orc = {}
with open(sys.argv[3]) as f:
    for line in f:
        m = rx_oracle.search(line)
        if m:
            b, fr = int(m.group(1)), int(m.group(2))
            h1 = [complex(float(m.group(6 + 2 * s)), float(m.group(7 + 2 * s))) for s in range(4)]
            h2 = [complex(float(m.group(16 + 2 * s)), float(m.group(17 + 2 * s))) for s in range(4)]
            orc.setdefault((b, fr), (float(m.group(5)), h1, h2))

errs = {}
with open(sys.argv[1]) as f:
    for row in csv.DictReader(f):
        key = (int(row["block"]), int(row["symbolInBlock"]) // 256)
        errs[key] = errs.get(key, 0) + 1

med = lambda xs: statistics.median(xs) if xs else float("nan")
blocks = [int(x) for x in sys.argv[4].split(",")] if len(sys.argv) > 4 else sorted({b for (b, _) in frozen})
for b in blocks:
    fs = sorted(fr for (bb, fr) in frozen if bb == b and (b, fr) in orc)
    grp = {("bad", True): [], ("bad", False): [], ("good", True): [], ("good", False): []}
    for fr in fs:
        rate = errs.get((b, fr), 0) / 768.0
        n, h1, h2 = orc[(b, fr)]
        d1 = abs(h1[3] - h1[0]) ** 2 / (8 * n)   # tracked by the frozen lerp
        d2 = abs(h2[3] - h2[0]) ** 2 / (8 * n)   # NOT tracked: frame-constant c
        grp[("bad" if rate >= 0.20 else "good", frozen[(b, fr)] not in (0,))].append((d1, d2))
    out = []
    for (name, echoed), vals in grp.items():
        if vals:
            out.append(f"{name}/{'echo' if echoed else 'lag0'}: n={len(vals)} "
                       f"h1drift/n med={med([v[0] for v in vals]):.2f} "
                       f"h2drift/n med={med([v[1] for v in vals]):.2f}")
    print(f"b{b}: " + " | ".join(out))
