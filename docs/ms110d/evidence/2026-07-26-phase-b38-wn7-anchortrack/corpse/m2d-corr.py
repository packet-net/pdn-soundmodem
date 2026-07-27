# B3.8 M2d: per-frame correlation — is the frozen pass's wire-error mass on the
# frames where the solve rejected the echo (lag=0), on alias/pre-cursor frames,
# or simply on deep-fade frames? Joins autopsy-frozen-biterrs CSV (per-bit
# errors; frame = symbolInBlock // 256, 768 bits/frame) with frozen-frame diag
# (lag, n, a0/a1) and oracle turbo-frame (lag2, h1 segs, h2 segs).
import csv
import math
import re
import statistics
import sys

rx_frozen = re.compile(
    r"frozen-frame b(\d+) f(\d+): lag=(\d+) \|c\|=([\d.]+) n=([\d.E+-]+) rows=.* "
    r"a0=(-?[\d.]+),(-?[\d.]+) a1=(-?[\d.]+),(-?[\d.]+)")
rx_oracle = re.compile(
    r"turbo-frame b(\d+) f(\d+): lag=(\d+) lag2=(\d+) .*? h1=(-?[\d.]+),(-?[\d.]+)\|"
    r"(-?[\d.]+),(-?[\d.]+)\|(-?[\d.]+),(-?[\d.]+)\|(-?[\d.]+),(-?[\d.]+) "
    r"h2avg=(-?[\d.]+),(-?[\d.]+)")

frozen = {}
with open(sys.argv[2]) as f:
    for line in f:
        m = rx_frozen.search(line)
        if m:
            b, fr = int(m.group(1)), int(m.group(2))
            if (b, fr) not in frozen:
                a0 = complex(float(m.group(6)), float(m.group(7)))
                a1 = complex(float(m.group(8)), float(m.group(9)))
                frozen[(b, fr)] = (int(m.group(3)), float(m.group(4)), float(m.group(5)),
                                   (abs(a0) + abs(a1)) / 2)

orc = {}
with open(sys.argv[3]) as f:
    for line in f:
        m = rx_oracle.search(line)
        if m:
            b, fr = int(m.group(1)), int(m.group(2))
            h1 = [abs(complex(float(m.group(5 + 2 * s)), float(m.group(6 + 2 * s)))) for s in range(4)]
            h2 = abs(complex(float(m.group(13)), float(m.group(14))))
            orc.setdefault((b, fr), (int(m.group(3)), int(m.group(4)), h1, h2))

errs = {}
with open(sys.argv[1]) as f:
    for row in csv.DictReader(f):
        key = (int(row["block"]), int(row["symbolInBlock"]) // 256)
        errs[key] = errs.get(key, 0) + 1

blocks = sorted({b for (b, _) in frozen}) if len(sys.argv) < 5 else [int(x) for x in sys.argv[4].split(",")]
for b in blocks:
    fs = sorted(fr for (bb, fr) in frozen if bb == b)
    if not fs:
        continue
    rows = []
    for fr in fs:
        lag, c, n, h1f = frozen[(b, fr)]
        rate = errs.get((b, fr), 0) / 768.0
        o = orc.get((b, fr))
        rows.append((fr, rate, lag, n, h1f, o))
    cat = {"lag0": [], "agree": [], "precur": [], "other": []}
    for fr, rate, lag, n, h1f, o in rows:
        if lag == 0:
            cat["lag0"].append(rate)
        elif o and lag == o[0]:
            cat["agree"].append(rate)
        elif lag > 8:
            cat["precur"].append(rate)
        else:
            cat["other"].append(rate)
    parts = " ".join(f"{k}:n={len(v)},mean={statistics.mean(v):.3f}" for k, v in cat.items() if v)
    bad = sum(1 for _, rate, *_ in rows if rate >= 0.20)
    print(f"b{b}: scaffold={64 - bad}/64 {parts}")
    # fade-depth split: bad frames vs good frames, oracle min-seg |h1| and |h2|
    badf = [(fr, rate, lag, h1f, o) for fr, rate, lag, n, h1f, o in rows if rate >= 0.20]
    goodf = [(fr, rate, lag, h1f, o) for fr, rate, lag, n, h1f, o in rows if rate < 0.20]
    for name, grp in (("bad", badf), ("good", goodf)):
        if not grp:
            continue
        h1o = [min(g[4][2]) for g in grp if g[4]]
        h2o = [g[4][3] for g in grp if g[4]]
        lag0frac = sum(1 for g in grp if g[2] == 0) / len(grp)
        print(f"   {name}: n={len(grp)} lag0-frac={lag0frac:.2f} "
              f"h1f med={statistics.median(g[3] for g in grp):.3f} "
              + (f"oracle minseg|h1| med={statistics.median(h1o):.3f} |h2avg| med={statistics.median(h2o):.3f}" if h1o else ""))
    top = sorted(rows, key=lambda r: -r[1])[:10]
    print("   worst: " + " ".join(
        f"f{fr}(r={rate:.2f},L={lag}" + (f",oL2={o[1]},m|h1|={min(o[2]):.2f}" if o else "") + ")"
        for fr, rate, lag, n, h1f, o in top))
