# B3.8 M2c: echo-model budget — what echo structure does the oracle establish
# that the frozen single-tap chain model cannot, and how big is it against the
# noise floor? Parses oracle turbo-frame (lag, lag2, n, h2 segs, h2b segs) and
# frozen-frame (lag, |c|, n). All cross-solve magnitude comparisons are rough
# (different FF gauges) and labelled so; the |h2b|^2/(2n) column is oracle-
# internal and gauge-clean.
import re
import statistics
import sys

rx_frozen = re.compile(
    r"frozen-frame b(\d+) f(\d+): lag=(\d+) \|c\|=([\d.]+) n=([\d.E+-]+)")
rx_oracle = re.compile(
    r"turbo-frame b(\d+) f(\d+): lag=(\d+) lag2=(\d+) n=([\d.E+-]+) .*? "
    r"h2avg=(-?[\d.]+),(-?[\d.]+) h2=(-?[\d.]+),(-?[\d.]+)\|(-?[\d.]+),(-?[\d.]+)\|"
    r"(-?[\d.]+),(-?[\d.]+)\|(-?[\d.]+),(-?[\d.]+)"
    r"(?: h2b=(-?[\d.]+),(-?[\d.]+)\|(-?[\d.]+),(-?[\d.]+)\|(-?[\d.]+),(-?[\d.]+)\|(-?[\d.]+),(-?[\d.]+))?")

frozen = {}
with open(sys.argv[1]) as f:
    for line in f:
        m = rx_frozen.search(line)
        if m:
            frozen.setdefault((int(m.group(1)), int(m.group(2))),
                              (int(m.group(3)), float(m.group(4)), float(m.group(5))))

orc = {}
with open(sys.argv[2]) as f:
    for line in f:
        m = rx_oracle.search(line)
        if m:
            b, fr = int(m.group(1)), int(m.group(2))
            h2 = [complex(float(m.group(8 + 2 * s)), float(m.group(9 + 2 * s))) for s in range(4)]
            h2b = None
            if m.group(16) is not None:
                h2b = [complex(float(m.group(16 + 2 * s)), float(m.group(17 + 2 * s))) for s in range(4)]
            orc.setdefault((b, fr), (int(m.group(3)), int(m.group(4)), float(m.group(5)), h2, h2b))

med = lambda xs: statistics.median(xs) if xs else float("nan")
p90 = lambda xs: sorted(xs)[int(0.9 * len(xs))] if xs else float("nan")

for b in sorted({bb for (bb, _) in orc}):
    fs = sorted(fr for (bb, fr) in orc if bb == b)
    pair = [fr for fr in fs if orc[(b, fr)][1] > 0]
    h2p = [statistics.mean(abs(x) ** 2 for x in orc[(b, fr)][3]) for fr in fs]
    h2bp = [statistics.mean(abs(x) ** 2 for x in orc[(b, fr)][4]) for fr in pair]
    h2b_vs_n = [statistics.mean(abs(x) ** 2 for x in orc[(b, fr)][4]) / (2 * orc[(b, fr)][2])
                for fr in pair if orc[(b, fr)][2] > 1e-9]
    olags = statistics.multimode(orc[(b, fr)][0] for fr in fs)
    line = (f"b{b}: frames={len(fs)} pair-frames={len(pair)} ({100 * len(pair) / len(fs):.0f}%) "
            f"olag-mode={olags} |h2|2 med={med(h2p):.3f} "
            f"|h2b|2 med={med(h2bp):.3f} h2b/(2n) med={med(h2b_vs_n):.1f} p90={p90(h2b_vs_n):.1f}")
    fset = [fr for fr in fs if (b, fr) in frozen]
    if fset:
        agree = sum(1 for fr in fset if frozen[(b, fr)][0] == orc[(b, fr)][0])
        pre = sum(1 for fr in fset if frozen[(b, fr)][0] > 8)
        zero = sum(1 for fr in fset if frozen[(b, fr)][0] == 0)
        # rough (cross-gauge): frozen captured echo power vs oracle total echo power
        cap = [frozen[(b, fr)][1] ** 2 /
               max(1e-9, statistics.mean(abs(x) ** 2 for x in orc[(b, fr)][3])
                   + (statistics.mean(abs(x) ** 2 for x in orc[(b, fr)][4]) if orc[(b, fr)][4] else 0))
               for fr in fset]
        unmod = [max(0.0, statistics.mean(abs(x) ** 2 for x in orc[(b, fr)][3])
                 + (statistics.mean(abs(x) ** 2 for x in orc[(b, fr)][4]) if orc[(b, fr)][4] else 0)
                 - frozen[(b, fr)][1] ** 2) / (2 * frozen[(b, fr)][2])
                 for fr in fset if frozen[(b, fr)][2] > 1e-9]
        line += (f" | frozen: lag-agree={agree}/{len(fset)} precursor={pre} lag0={zero} "
                 f"cap med={med(cap):.2f} unmod/(2n) med={med(unmod):.1f} p90={p90(unmod):.1f}")
    print(line)
