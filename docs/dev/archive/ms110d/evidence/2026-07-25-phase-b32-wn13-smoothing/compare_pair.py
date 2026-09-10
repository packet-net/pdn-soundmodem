#!/usr/bin/env python3
"""B3.2 WN13 specimen: normal-vs-genie comparison over the dead block.

Uses the harness's authoritative biterrs CSVs (like analyze_autopsy.py), and
decomposes the tracking deficit: per-frame SER for both runs against the true
composite gain, fade-phase classification of the EXCESS (normal-only) errors,
and |y| confidence stats on errors.
"""
import math
import sys
from pathlib import Path

U, K = 256, 32
SYM_PER_BLOCK = 16384
FRAMES_PER_BLOCK = SYM_PER_BLOCK // U  # 64
PREAMBLE_CHIPS = 11520


def load(outdir: Path, tag: str):
    ys = {}
    with open(outdir / f"autopsy-symbols-{tag}.csv") as f:
        next(f)
        for line in f:
            p = line.split(",")
            ys[int(p[0])] = complex(float(p[1]), float(p[2]))
    errs = set()
    with open(outdir / f"autopsy-biterrs-{tag}.csv") as f:
        next(f)
        for line in f:
            p = line.split(",")
            errs.add(int(p[0]) * SYM_PER_BLOCK + int(p[1]))
    gains = []
    with open(outdir / f"autopsy-gains-{tag}.csv") as f:
        next(f)
        for line in f:
            p = line.split(",")
            g0 = complex(float(p[1]), float(p[2]))
            g1 = complex(float(p[3]), float(p[4]))
            gains.append((abs(g0) ** 2 + abs(g1) ** 2) / 2)
    return ys, errs, gains


def air_gain(gains, idx):
    blk, s = divmod(idx, SYM_PER_BLOCK)
    chip = (blk * FRAMES_PER_BLOCK + s // U) * (U + K) + s % U
    gi = (PREAMBLE_CHIPS + chip) * 4 // 100
    return gains[min(gi, len(gains) - 1)]


def main():
    d = Path(sys.argv[1])
    ny, nerr, gains = load(d / "corpse-normal", "wn13-w3-b5")
    gy, gerr, _ = load(d / "corpse-genie", "wn13-w3-b5-genie")
    nsym = max(ny) + 1

    print(f"uncoded errored symbols: normal {len(nerr)}, genie {len(gerr)}, "
          f"excess {len(nerr) - len(gerr)}, normal-only {len(nerr - gerr)}, "
          f"genie-only {len(gerr - nerr)}")

    for b in range(11):
        l2, h2 = b * SYM_PER_BLOCK, (b + 1) * SYM_PER_BLOCK
        x = sum(1 for i in nerr if l2 <= i < h2)
        g2 = sum(1 for i in gerr if l2 <= i < h2)
        tag = "  <-- DEAD" if b == 8 else ""
        print(f"  blk {b:2d}: normal {x / SYM_PER_BLOCK:6.4f} ({x:5d})  "
              f"genie {g2 / SYM_PER_BLOCK:6.4f} ({g2:5d})  excess {x - g2:5d}{tag}")

    print("\n== block 8 frames (n-SER / g-SER / gain; genie line only if differs) ==")
    lo = 8 * SYM_PER_BLOCK
    for f in range(FRAMES_PER_BLOCK):
        s0 = lo + f * U
        fn = sum(1 for i in range(s0, s0 + U) if i in nerr) / U
        fg = sum(1 for i in range(s0, s0 + U) if i in gerr) / U
        g = sum(air_gain(gains, i) for i in range(s0, s0 + U, 16)) / 16
        bar = "#" * int(40 * fn)
        print(f"  fr {512 + f:3d}  n={fn:5.2f} g={fg:5.2f} d={fn - fg:+5.2f} "
              f"gain={g:5.2f}  {bar}")

    # fade-phase classification of EXCESS errors across whole burst
    classes = {"deep": [0, 0], "edge": [0, 0], "healthy": [0, 0]}
    conf_n = {"deep": [], "edge": [], "healthy": []}
    conf_g = {"deep": [], "edge": [], "healthy": []}
    for i in range(nsym):
        g = air_gain(gains, i)
        cls = "deep" if g < 0.25 else ("edge" if g < 0.7 else "healthy")
        classes[cls][1] += 1
        if i in nerr and i not in gerr:
            classes[cls][0] += 1
        if i in nerr and i in ny:
            conf_n[cls].append(abs(ny[i]))
        if i in gerr and i in gy:
            conf_g[cls].append(abs(gy[i]))

    print("\n== excess (normal-only) uncoded errors by fade phase ==")
    tot = sum(v[0] for v in classes.values())
    for cls, (e, n) in classes.items():
        print(f"  {cls:8s}: {e:6d} excess / {n:7d} syms "
              f"({100 * e / max(tot, 1):5.1f}% of excess, excess rate {e / max(n, 1):.4f})")

    # overall SER by class for both runs (is the tax flat or fade-shaped?)
    print("\n== SER by fade phase (normal vs genie) ==")
    for cls in classes:
        n_in = sum(1 for i in range(nsym)
                   if (air_gain(gains, i) < 0.25) == (cls == "deep")
                   and ((0.25 <= air_gain(gains, i) < 0.7) == (cls == "edge")))
        ne = len(conf_n[cls])
        ge = len(conf_g[cls])
        n_tot = classes[cls][1]
        print(f"  {cls:8s}: normal {ne / max(n_tot, 1):.4f}  genie {ge / max(n_tot, 1):.4f}  "
              f"ratio {ne / max(ge, 1):.2f}")

    def med(v):
        return sorted(v)[len(v) // 2] if v else float("nan")

    print("\n== |y| median on errored symbols ==")
    for cls in classes:
        print(f"  {cls:8s}: normal {med(conf_n[cls]):.3f} ({len(conf_n[cls])}), "
              f"genie {med(conf_g[cls]):.3f} ({len(conf_g[cls])})")


main()
