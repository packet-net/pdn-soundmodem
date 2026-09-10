#!/usr/bin/env python3
"""B3.3 WN7: per-symbol phase-error trajectory analysis.

Delta-theta_i = wrapped phase(y_i * conj(ref_i)) for EVERY symbol. Profiles:
head/mid/tail per frame, continuity across probes, and raw trajectories of the
worst frames — discriminates anchor-phase error (frame starts wrong) from
chord/sagitta error (mid-frame hump) from rate aliasing (ramp shape).
"""
import cmath
import math
import sys
from pathlib import Path

U, K = 256, 32
SYM_PER_BLOCK = 16384
BLOCKS = 11
PSK8 = [cmath.exp(1j * math.pi / 4 * s) for s in range(8)]


def load(outdir: Path, tag: str):
    ys, refs = {}, {}
    with open(outdir / f"autopsy-symbols-{tag}.csv") as f:
        next(f)
        for line in f:
            p = line.split(",")
            r = int(p[3])
            if r < 0:
                continue
            ys[int(p[0])] = complex(float(p[1]), float(p[2]))
            refs[int(p[0])] = r
    return ys, refs


def med(v):
    return sorted(v)[len(v) // 2] if v else float("nan")


def main():
    d = Path(sys.argv[1])
    for name, tag in (("normal", "wn7-w0-b0"), ("genie", "wn7-w0-b0-genie")):
        ys, refs = load(d, tag)
        idxs = sorted(ys)
        dth = {i: cmath.phase(ys[i] * PSK8[refs[i]].conjugate()) for i in idxs}

        print(f"===== {name} =====")
        print("== median |dtheta| (deg) by in-frame u bin, ALL symbols ==")
        rows = []
        for b in range(8):
            lo_u, hi_u = b * 32, (b + 1) * 32
            v = [abs(dth[i]) for i in idxs if lo_u <= i % U < hi_u]
            rows.append(f"{math.degrees(med(v)):5.1f}")
        print("  u 0-31..224-255: " + " ".join(rows))

        # per-frame head/mid/tail medians of |dtheta| and signed dtheta
        heads, mids, tails = [], [], []
        sheads, smids, stails = [], [], []
        nframes = BLOCKS * SYM_PER_BLOCK // U
        for f in range(nframes):
            f0 = f * U
            h = [dth[i] for i in range(f0, f0 + 32) if i in dth]
            m = [dth[i] for i in range(f0 + 112, f0 + 144) if i in dth]
            t = [dth[i] for i in range(f0 + 224, f0 + 256) if i in dth]
            if not h or not m or not t:
                heads.append(float("nan")); mids.append(float("nan")); tails.append(float("nan"))
                sheads.append(0.0); smids.append(0.0); stails.append(0.0)
                continue
            heads.append(med([abs(x) for x in h]))
            mids.append(med([abs(x) for x in m]))
            tails.append(med([abs(x) for x in t]))
            sheads.append(med(sorted(h)))
            smids.append(med(sorted(m)))
            stails.append(med(sorted(t)))

        thr = math.radians(22.5)
        hbad = sum(1 for x in heads if x == x and x > thr)
        mbad = sum(1 for x in mids if x == x and x > thr)
        tbad = sum(1 for x in tails if x == x and x > thr)
        print(f"  frames with med|dtheta| > 22.5 deg: head {hbad}/{nframes}, "
              f"mid {mbad}/{nframes}, tail {tbad}/{nframes}")

        # continuity across the probe: signed tail(f) vs signed head(f+1)
        pairs = [(stails[f], sheads[f + 1]) for f in range(nframes - 1)
                 if tails[f] == tails[f] and heads[f + 1] == heads[f + 1]]
        n = len(pairs)
        mx = sum(a for a, _ in pairs) / n
        my = sum(b for _, b in pairs) / n
        cov = sum((a - mx) * (b - my) for a, b in pairs) / n
        vx = sum((a - mx) ** 2 for a, _ in pairs) / n
        vy = sum((b - my) ** 2 for _, b in pairs) / n
        r = cov / math.sqrt(vx * vy) if vx > 0 and vy > 0 else float("nan")
        # and continuity within a frame: head vs its own tail
        pairs2 = [(sheads[f], stails[f]) for f in range(nframes)
                  if heads[f] == heads[f] and tails[f] == tails[f]]
        mx2 = sum(a for a, _ in pairs2) / len(pairs2)
        my2 = sum(b for _, b in pairs2) / len(pairs2)
        cov2 = sum((a - mx2) * (b - my2) for a, b in pairs2) / len(pairs2)
        vx2 = sum((a - mx2) ** 2 for a, _ in pairs2) / len(pairs2)
        vy2 = sum((b - my2) ** 2 for _, b in pairs2) / len(pairs2)
        r2 = cov2 / math.sqrt(vx2 * vy2) if vx2 > 0 and vy2 > 0 else float("nan")
        print(f"  signed-dtheta correlation: tail(f) vs head(f+1) r={r:.3f} "
              f"(probe reset test); head(f) vs tail(f) r={r2:.3f}")

        # worst frames: print coarse signed trajectory (8 chunks of 32)
        order = sorted(range(nframes), key=lambda f: -(mids[f] if mids[f] == mids[f] else -1))
        print("  worst-mid frames, signed median dtheta per 32-sym chunk (deg):")
        for f in order[:8]:
            f0 = f * U
            prof = []
            for c in range(8):
                v = sorted(dth[i] for i in range(f0 + c * 32, f0 + (c + 1) * 32) if i in dth)
                prof.append(f"{math.degrees(med(v)):+6.0f}" if v else "     ?")
            print(f"    fr {f:3d} (blk {f // 64}): " + " ".join(prof))
        print()


main()
