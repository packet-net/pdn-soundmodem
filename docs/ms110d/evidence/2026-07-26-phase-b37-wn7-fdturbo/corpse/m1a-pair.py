# B3.7 M1a: pair-solve information content per block, from a frames log carrying
# frozen-pair + frozen-frame lines (MS110D_AUTOPSY_FROZEN_PAIRDIAG=1 run).
# Reports, per block: pair acceptance rate, |c2|/|c1| on accepted frames,
# SSE budget (sse1 vs sseP vs sseN), and residual-above-noise after the pair
# (the FD-MMSE revival condition: ssePair/rows >> priced noise floor).
import collections
import re
import statistics
import sys

pair = {}    # (b, f) -> dict
frame = {}   # (b, f) -> dict
rx_pair = re.compile(
    r"frozen-pair b(\d+) f(\d+): rows=(\d+) lag=(\d+) \|c1\|=([\d.]+) lag2=(\d+) "
    r"\|c2\|=([\d.]+) sseN=([\d.E+-]+) sseP=([\d.E+-]+)")
rx_frame = re.compile(
    r"frozen-frame b(\d+) f(\d+): lag=(\d+) \|c\|=([\d.]+) n=([\d.E+-]+) rows=(\d+) "
    r"h1a=([\d.]+)\|([\d.]+) sseN=([\d.E+-]+) sse1=([\d.E+-]+)")

with open(sys.argv[1]) as f:
    for line in f:
        m = rx_pair.search(line)
        if m:
            b, fr = int(m.group(1)), int(m.group(2))
            pair[(b, fr)] = dict(rows=int(m.group(3)), lag=int(m.group(4)),
                                 c1=float(m.group(5)), lag2=int(m.group(6)),
                                 c2=float(m.group(7)), sseN=float(m.group(8)),
                                 sseP=float(m.group(9)))
            continue
        m = rx_frame.search(line)
        if m:
            b, fr = int(m.group(1)), int(m.group(2))
            # Keep the FIRST sighting per (b, f): the diagnostic frozen pass runs before
            # any salvage re-runs of the same block.
            frame.setdefault((b, fr), dict(
                lag=int(m.group(3)), c=float(m.group(4)), n=float(m.group(5)),
                rows=int(m.group(6)), sseN=float(m.group(9)), sse1=float(m.group(10))))

blocks = sorted({b for (b, _) in frame})
for b in blocks:
    keys = sorted(k for k in frame if k[0] == b and k in pair)
    if not keys:
        continue
    acc1 = [k for k in keys if frame[k]["lag"] > 0]
    accp = [k for k in keys if pair[k]["lag2"] > 0]
    ratio = [pair[k]["c2"] / max(1e-9, pair[k]["c1"]) for k in accp]
    # SSE per row vs the priced floor (n is per-dimension; SSE sums both dims):
    # resid-per-row/2 ~ per-dim residual, compare against n.
    over1 = [frame[k]["sse1"] / max(1, frame[k]["rows"]) / 2 / max(1e-12, frame[k]["n"]) for k in keys]
    overp = [pair[k]["sseP"] / max(1, pair[k]["rows"]) / 2 / max(1e-12, frame[k]["n"]) for k in accp]
    drop = [pair[k]["sseP"] / max(1e-12, frame[k]["sse1"]) for k in accp]
    med = lambda xs: statistics.median(xs) if xs else float("nan")
    print(f"b{b}: frames={len(keys)} lag-acc={len(acc1)} pair-acc={len(accp)} "
          f"med|c2/c1|={med(ratio):.3f} med sseP/sse1={med(drop):.3f} "
          f"med sse1/rows/2n={med(over1):.2f} med sseP/rows/2n={med(overp):.2f}")
