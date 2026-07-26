# B3.8 M2b: offline dry-fit of the gauge-stitched cubic anchor track (kill gate).
#
# The stitch reduces to gauge-free WITHIN-FRAME ratios: frame f's a1/a0 is the
# channel growth probe(f+1)/probe(f) with the frame's own gauge cancelled. The
# cubic's outer points in frame f's gauge are therefore
#   P0 = a0(f) * (a0(f-1)/a1(f-1)),   P3 = a1(f) * (a1(f+1)/a0(f+1))
# with P1 = a0(f), P2 = a1(f) native — no cross-frame gauge chain, no drift.
#
# Scores, per block and aggregate:
#  (1) label-free hold-out-one-probe: predict the centre from +/-1,+/-2 (uniform
#      spacing 288) — 4-pt cubic Lagrange (-1+9+9-1)/16 vs 2-pt average; both are
#      pure curvature statistics of the gauge-free relative track.
#  (2) with an oracle log: per-segment h1 error of the Catmull-Rom track vs the
#      linear interpolant, m1b alignment (per-frame complex scale), seg centres
#      t_s = (32+64s+16)/288 against anchor abscissae -16/272.
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
if len(sys.argv) > 2:
    with open(sys.argv[2]) as f:
        for line in f:
            m = rx_oracle.search(line)
            if m:
                b, fr = int(m.group(1)), int(m.group(2))
                oracle.setdefault((b, fr), [
                    complex(float(m.group(3 + 2 * s)), float(m.group(4 + 2 * s)))
                    for s in range(4)])

FLOOR = 0.1  # of block rms, the m1b skip rule
med = lambda xs: statistics.median(xs) if xs else float("nan")
p90 = lambda xs: sorted(xs)[int(0.9 * len(xs))] if xs else float("nan")

all_lin_h, all_cub_h, all_lin_o, all_cub_o = [], [], [], []
for b in sorted({bb for (bb, _) in anchors}):
    fs = sorted(fr for (bb, fr) in anchors if bb == b)
    rms = statistics.mean(abs(anchors[(b, fr)][0]) for fr in fs) or 1e-9
    ok = lambda *vs: all(abs(v) >= FLOOR * rms for v in vs)

    # (1) hold-out-one-probe. Probe n is frame n's a0 (= frame n-1's a1).
    lin_h, cub_h, skip = [], [], 0
    for n in fs:
        if not all((b, g) in anchors for g in (n - 2, n - 1, n, n + 1)):
            continue
        a0m2, a1m2 = anchors[(b, n - 2)]
        a0m1, a1m1 = anchors[(b, n - 1)]
        a0c, a1c = anchors[(b, n)]
        a0p1, a1p1 = anchors[(b, n + 1)]
        if not ok(a0m2, a1m2, a0m1, a1m1, a0c, a1c, a0p1, a1p1):
            skip += 1
            continue
        r_m1 = a0m1 / a1m1
        r_p1 = a1c / a0c
        r_m2 = r_m1 * (a0m2 / a1m2)
        r_p2 = r_p1 * (a1p1 / a0p1)
        lin_h.append(abs(1 - (r_m1 + r_p1) / 2))
        cub_h.append(abs(1 - (-r_m2 + 9 * r_m1 + 9 * r_p1 - r_p2) / 16))

    # (2) oracle per-segment error, linear vs Catmull-Rom track.
    lin_o, cub_o = [], []
    for fr in fs:
        if (b, fr) not in oracle:
            continue
        a0, a1 = anchors[(b, fr)]
        p0 = p3 = None
        if (b, fr - 1) in anchors:
            pa0, pa1 = anchors[(b, fr - 1)]
            if ok(pa0, pa1, a0):
                p0 = a0 * (pa0 / pa1)
        if (b, fr + 1) in anchors:
            na0, na1 = anchors[(b, fr + 1)]
            if ok(na0, na1, a1):
                p3 = a1 * (na1 / na0)
        m1 = (a1 - p0) / 2 if p0 is not None else (a1 - a0)
        m2 = (p3 - a0) / 2 if p3 is not None else (a1 - a0)
        o = oracle[(b, fr)]

        def score(pred):
            den = sum(abs(p) ** 2 for p in pred)
            orms = sum(abs(x) ** 2 for x in o)
            if den < 1e-12 or orms < 1e-12:
                return None
            g = sum(oo * pp.conjugate() for oo, pp in zip(o, pred)) / den
            return (sum(abs((g * pp) - oo) ** 2 for pp, oo in zip(pred, o)) / orms) ** 0.5

        ts = [(32 + 64 * s + 16) / 288.0 for s in range(4)]
        s_lin = score([a0 + (a1 - a0) * t for t in ts])
        s_cub = score([
            (2 * t ** 3 - 3 * t ** 2 + 1) * a0 + (t ** 3 - 2 * t ** 2 + t) * m1
            + (-2 * t ** 3 + 3 * t ** 2) * a1 + (t ** 3 - t ** 2) * m2
            for t in ts])
        if s_lin is not None and s_cub is not None:
            lin_o.append(s_lin)
            cub_o.append(s_cub)

    line = (f"b{b}: holdout n={len(lin_h)} skip={skip} "
            f"lin med={med(lin_h):.3f} p90={p90(lin_h):.3f} "
            f"cub med={med(cub_h):.3f} p90={p90(cub_h):.3f}")
    if lin_o:
        line += (f" | oracle-seg n={len(lin_o)} lin med={med(lin_o):.3f} p90={p90(lin_o):.3f}"
                 f" cub med={med(cub_o):.3f} p90={p90(cub_o):.3f}")
    print(line)
    all_lin_h += lin_h
    all_cub_h += cub_h
    all_lin_o += lin_o
    all_cub_o += cub_o

print(f"ALL: holdout n={len(all_lin_h)} lin med={med(all_lin_h):.3f} p90={p90(all_lin_h):.3f} "
      f"cub med={med(all_cub_h):.3f} p90={p90(all_cub_h):.3f}"
      + (f" | oracle-seg n={len(all_lin_o)} lin med={med(all_lin_o):.3f} p90={p90(all_lin_o):.3f}"
         f" cub med={med(all_cub_o):.3f} p90={p90(all_cub_o):.3f}" if all_lin_o else ""))
