# B3.7 M1c: within-frame noise heteroscedasticity the frozen frame-constant floor
# ignores — from the oracle pass's turbo-frame nseg quads. Also reports oracle
# per-frame lag acceptance for the rank-starvation comparison (M1a follow-on).
import re
import statistics
import sys

rx = re.compile(
    r"turbo-frame b(\d+) f(\d+): lag=(\d+) lag2=(-?\d+) n=([\d.E+-]+) "
    r"nseg=([\d.E+-]+)\|([\d.E+-]+)\|([\d.E+-]+)\|([\d.E+-]+)")

rows = {}
with open(sys.argv[1]) as f:
    for line in f:
        m = rx.search(line)
        if m:
            b, fr = int(m.group(1)), int(m.group(2))
            rows.setdefault((b, fr), (
                int(m.group(3)), int(m.group(4)),
                [float(m.group(6 + s)) for s in range(4)]))

blocks = sorted({b for (b, _) in rows})
for b in blocks:
    ks = sorted(k for k in rows if k[0] == b)
    lagacc = sum(1 for k in ks if rows[k][0] > 0)
    pairacc = sum(1 for k in ks if rows[k][1] > 0)
    spread = [max(rows[k][2]) / max(1e-12, min(rows[k][2])) for k in ks]
    med = statistics.median(spread) if spread else float("nan")
    p90 = sorted(spread)[int(0.9 * len(spread))] if spread else float("nan")
    print(f"b{b}: frames={len(ks)} oracle-lag-acc={lagacc} oracle-pair-acc={pairacc} "
          f"nseg-spread med={med:.2f} p90={p90:.2f}")
