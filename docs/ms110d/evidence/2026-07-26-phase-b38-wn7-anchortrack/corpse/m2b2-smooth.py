# B3.8 M2b follow-up: the oracle seg test said operational G2 error is noise-
# dominated, not curvature-dominated. Score SMOOTHED anchor tracks against the
# oracle: probe values in frame f's gauge via the gauge-free ratio chain, then
# Savitzky-Golay-5 quadratic (-3,12,17,12,-3)/35 or triangular-3 (1,2,1)/4
# smoothing at each probe, then linear/Hermite between the smoothed anchors.
# LIN/CUB raw variants must reproduce m2b-track.py's oracle columns exactly.
import re
import statistics
import sys

rx_frozen = re.compile(
    r"frozen-frame b(\d+) f(\d+): lag=(\d+) .* a0=(-?[\d.]+),(-?[\d.]+) a1=(-?[\d.]+),(-?[\d.]+)")
rx_oracle = re.compile(
    r"turbo-frame b(\d+) f(\d+): .* h1=(-?[\d.]+),(-?[\d.]+)\|(-?[\d.]+),(-?[\d.]+)\|"
    r"(-?[\d.]+),(-?[\d.]+)\|(-?[\d.]+),(-?[\d.]+)")

anchors = {}
with open(sys.argv[1]) as f:
    for line in f:
        m = rx_frozen.search(line)
        if m:
            b, fr = int(m.group(1)), int(m.group(2))
            anchors.setdefault((b, fr), (
                complex(float(m.group(4)), float(m.group(5))),
                complex(float(m.group(6)), float(m.group(7)))))

oracle = {}
with open(sys.argv[2]) as f:
    for line in f:
        m = rx_oracle.search(line)
        if m:
            b, fr = int(m.group(1)), int(m.group(2))
            oracle.setdefault((b, fr), [
                complex(float(m.group(3 + 2 * s)), float(m.group(4 + 2 * s)))
                for s in range(4)])

FLOOR = 0.1
SG5 = [-3 / 35, 12 / 35, 17 / 35, 12 / 35, -3 / 35]
TRI3 = [0.25, 0.5, 0.25]
med = lambda xs: statistics.median(xs) if xs else float("nan")
p90 = lambda xs: sorted(xs)[int(0.9 * len(xs))] if xs else float("nan")
VARIANTS = ["lin", "cub", "tri-lin", "tri-cub", "sg5-lin", "sg5-cub"]
agg = {v: [] for v in VARIANTS}

for b in sorted({bb for (bb, _) in anchors}):
    fs = sorted(fr for (bb, fr) in anchors if bb == b)
    rms = statistics.mean(abs(anchors[(b, fr)][0]) for fr in fs) or 1e-9
    ok = lambda *vs: all(abs(v) >= FLOOR * rms for v in vs)
    per = {v: [] for v in VARIANTS}

    for fr in fs:
        if (b, fr) not in oracle:
            continue
        a0, a1 = anchors[(b, fr)]

        # Probe values q[k] (probe fr+k) in frame fr's gauge via the ratio chain;
        # None where a link is missing or below the magnitude floor.
        q = {0: a0, 1: a1}
        for k in (-1, -2, -3):
            src = q.get(k + 1)
            nb = anchors.get((b, fr + k))
            q[k] = src * (nb[0] / nb[1]) if (src is not None and nb and ok(*nb)) else None
        for k in (2, 3, 4):
            src = q.get(k - 1)
            nb = anchors.get((b, fr + k - 1))
            q[k] = src * (nb[1] / nb[0]) if (src is not None and nb and ok(*nb)) else None

        def smooth(center, weights):
            half = len(weights) // 2
            vals = [q.get(center + i - half) for i in range(len(weights))]
            if any(v is None for v in vals):
                return q[center]  # incomplete window: raw fallback
            return sum(w * v for w, v in zip(weights, vals))

        def interp(p1, p2, m1, m2, cubic):
            ts = [(32 + 64 * s + 16) / 288.0 for s in range(4)]
            if not cubic:
                return [p1 + (p2 - p1) * t for t in ts]
            return [(2 * t ** 3 - 3 * t ** 2 + 1) * p1 + (t ** 3 - 2 * t ** 2 + t) * m1
                    + (-2 * t ** 3 + 3 * t ** 2) * p2 + (t ** 3 - t ** 2) * m2
                    for t in ts]

        o = oracle[(b, fr)]

        def score(pred):
            den = sum(abs(p) ** 2 for p in pred)
            orms = sum(abs(x) ** 2 for x in o)
            if den < 1e-12 or orms < 1e-12:
                return None
            g = sum(oo * pp.conjugate() for oo, pp in zip(o, pred)) / den
            return (sum(abs((g * pp) - oo) ** 2 for pp, oo in zip(pred, o)) / orms) ** 0.5

        def tangents(qq):
            m1 = (qq(1) - qq(-1)) / 2 if qq(-1) is not None else qq(1) - qq(0)
            m2 = (qq(2) - qq(0)) / 2 if qq(2) is not None else qq(1) - qq(0)
            return m1, m2

        raw = lambda k: q.get(k)
        m1r, m2r = tangents(raw)
        cases = {"lin": interp(q[0], q[1], 0, 0, False),
                 "cub": interp(q[0], q[1], m1r, m2r, True)}
        for name, w in (("tri", TRI3), ("sg5", SG5)):
            sm = lambda k: smooth(k, w) if q.get(k) is not None else None
            s0, s1 = smooth(0, w), smooth(1, w)
            m1s, m2s = tangents(sm)
            cases[f"{name}-lin"] = interp(s0, s1, 0, 0, False)
            cases[f"{name}-cub"] = interp(s0, s1, m1s, m2s, True)

        scores = {v: score(p) for v, p in cases.items()}
        if any(s is None for s in scores.values()):
            continue
        for v in VARIANTS:
            per[v].append(scores[v])

    n = len(per["lin"])
    print(f"b{b}: n={n} " + " ".join(
        f"{v}={med(per[v]):.3f}/{p90(per[v]):.3f}" for v in VARIANTS))
    for v in VARIANTS:
        agg[v] += per[v]

print(f"ALL: n={len(agg['lin'])} " + " ".join(
    f"{v}={med(agg[v]):.3f}/{p90(agg[v]):.3f}" for v in VARIANTS))
