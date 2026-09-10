# B3.7 M1b: (1) anchor-series midpoint test — how much does within-frame linear
# interpolation between two probe anchors err, measured label-free from the anchor
# series itself (local gauge stitching via the shared probe between consecutive
# frames); (2) for w0/b0, direct comparison of the frozen linear h1 against the
# oracle pass's per-segment h1 after per-frame complex-scale alignment (G2).
import cmath
import re
import statistics
import sys

rx_frozen = re.compile(
    r"frozen-frame b(\d+) f(\d+): lag=(\d+) .* a0=(-?[\d.]+),(-?[\d.]+) a1=(-?[\d.]+),(-?[\d.]+)")
rx_oracle = re.compile(
    r"turbo-frame b(\d+) f(\d+): .* h1=(-?[\d.]+),(-?[\d.]+)\|(-?[\d.]+),(-?[\d.]+)\|"
    r"(-?[\d.]+),(-?[\d.]+)\|(-?[\d.]+),(-?[\d.]+)")

anchors = {}  # (b, f) -> (a0, a1) complex, first sighting (= the pass that made the seed)
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

blocks = sorted({b for (b, _) in anchors})
for b in blocks:
    fs = sorted(fr for (bb, fr) in anchors if bb == b)
    rms = statistics.mean(abs(anchors[(b, fr)][0]) for fr in fs) or 1e-9
    mid = []
    skipped = 0
    for fr in fs:
        if (b, fr - 1) not in anchors:
            continue
        a0p, a1p = anchors[(b, fr - 1)]
        a0c, a1c = anchors[(b, fr)]
        if abs(a0c) < 0.1 * rms or abs(a1p) < 1e-9:
            skipped += 1
            continue
        gauge = a1p / a0c
        v0, v1, v2 = a0p, a1p, a1c * gauge
        if abs(v1) < 0.1 * rms:
            skipped += 1
            continue
        mid.append(abs(v1 - (v0 + v2) / 2) / abs(v1))
    seg = []
    for fr in fs:
        if (b, fr) not in oracle:
            continue
        a0, a1 = anchors[(b, fr)]
        # Segment centres 32,96,160,224 against anchor abscissae ~-16 / ~272.
        pred = [a0 + (a1 - a0) * ((32 + 64 * s + 16) / 288.0) for s in range(4)]
        o = oracle[(b, fr)]
        den = sum(abs(p) ** 2 for p in pred)
        orms = sum(abs(x) ** 2 for x in o)
        if den < 1e-12 or orms < 1e-12:
            continue
        g = sum(oo * pp.conjugate() for oo, pp in zip(o, pred)) / den
        resid = sum(abs((g * pp) - oo) ** 2 for pp, oo in zip(pred, o))
        seg.append((resid / orms) ** 0.5)
    med = lambda xs: statistics.median(xs) if xs else float("nan")
    p90 = lambda xs: sorted(xs)[int(0.9 * len(xs))] if xs else float("nan")
    print(f"b{b}: triples={len(mid)} skip={skipped} midpt-err med={med(mid):.3f} p90={p90(mid):.3f}"
          + (f" | seg-h1-err med={med(seg):.3f} p90={p90(seg):.3f} n={len(seg)}" if seg else ""))
