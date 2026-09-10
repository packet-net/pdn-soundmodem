# B3.8 M2b decomposition: WHERE in the frame does the frozen linear h1 model
# err, and how large is the model-error POWER against each frame's own
# probe-priced noise floor (the 'n=' field)? Per segment position s=0..3
# (centres 48,112,176,240 incl. the +16 probe offset): relative error med/p90,
# mean oracle |h1|, and the overconfidence ratio |err|^2 / (2*noiseVar)
# (noiseVar is per-dimension; model error is complex power).
import re
import statistics
import sys

rx_frozen = re.compile(
    r"frozen-frame b(\d+) f(\d+): lag=(\d+) .* n=([\d.E+-]+) rows=.* "
    r"a0=(-?[\d.]+),(-?[\d.]+) a1=(-?[\d.]+),(-?[\d.]+)")
rx_oracle = re.compile(
    r"turbo-frame b(\d+) f(\d+): .* h1=(-?[\d.]+),(-?[\d.]+)\|(-?[\d.]+),(-?[\d.]+)\|"
    r"(-?[\d.]+),(-?[\d.]+)\|(-?[\d.]+),(-?[\d.]+)")

anchors = {}
noise = {}
with open(sys.argv[1]) as f:
    for line in f:
        m = rx_frozen.search(line)
        if m:
            b, fr = int(m.group(1)), int(m.group(2))
            if (b, fr) not in anchors:
                anchors[(b, fr)] = (
                    complex(float(m.group(5)), float(m.group(6))),
                    complex(float(m.group(7)), float(m.group(8))))
                noise[(b, fr)] = float(m.group(4))

oracle = {}
with open(sys.argv[2]) as f:
    for line in f:
        m = rx_oracle.search(line)
        if m:
            b, fr = int(m.group(1)), int(m.group(2))
            oracle.setdefault((b, fr), [
                complex(float(m.group(3 + 2 * s)), float(m.group(4 + 2 * s)))
                for s in range(4)])

med = lambda xs: statistics.median(xs) if xs else float("nan")
p90 = lambda xs: sorted(xs)[int(0.9 * len(xs))] if xs else float("nan")

rel = [[] for _ in range(4)]     # relative error per position
oh = [[] for _ in range(4)]      # oracle |h1| per position
conf = [[] for _ in range(4)]    # model-error power / (2*noiseVar)
for (b, fr), (a0, a1) in sorted(anchors.items()):
    if (b, fr) not in oracle:
        continue
    o = oracle[(b, fr)]
    ts = [(32 + 64 * s + 16) / 288.0 for s in range(4)]
    pred = [a0 + (a1 - a0) * t for t in ts]
    den = sum(abs(p) ** 2 for p in pred)
    if den < 1e-12:
        continue
    g = sum(oo * pp.conjugate() for oo, pp in zip(o, pred)) / den
    nv = noise[(b, fr)]
    for s in range(4):
        err = (g * pred[s]) - o[s]
        if abs(o[s]) > 1e-6:
            rel[s].append(abs(err) / abs(o[s]))
        oh[s].append(abs(o[s]))
        if nv > 1e-9:
            conf[s].append(abs(err) ** 2 / (2 * nv))

for s in range(4):
    print(f"s{s}: n={len(rel[s])} rel med={med(rel[s]):.3f} p90={p90(rel[s]):.3f} "
          f"|h1| mean={statistics.mean(oh[s]):.3f} "
          f"errpow/floor med={med(conf[s]):.3f} p90={p90(conf[s]):.3f}")
frames = sorted(k for k in anchors if k in oracle)
tot = [sum(abs((sum(oracle[k][i] * (anchors[k][0] + (anchors[k][1] - anchors[k][0]) * ((32 + 64 * i + 16) / 288.0)).conjugate() for i in range(4)) /
        max(1e-12, sum(abs(anchors[k][0] + (anchors[k][1] - anchors[k][0]) * ((32 + 64 * i + 16) / 288.0)) ** 2 for i in range(4))) *
        (anchors[k][0] + (anchors[k][1] - anchors[k][0]) * ((32 + 64 * s + 16) / 288.0))) - oracle[k][s]) ** 2 for k in frames) / len(frames)
       for s in range(4)]
print("mean err power by s:", " ".join(f"{t:.5f}" for t in tot))
