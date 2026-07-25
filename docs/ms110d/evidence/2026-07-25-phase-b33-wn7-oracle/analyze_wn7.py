#!/usr/bin/env python3
"""B3.3 WN7 mechanism autopsy: normal-vs-genie decomposition of the 8PSK failure.

Uses the per-symbol truth in autopsy-symbols (descrambled y + reference ring
index), the per-path complex gain trajectories, and the llrstats CSVs.
Discriminators:
  - SER vs in-frame position u (chord-curvature = mid-frame hump;
    DFE error propagation = rising from frame head; smear = flat)
  - donut statistic per frame (|sum y^8| / sum |y|^8 concentration, the B1 metric)
  - SER vs per-path ratio |g1|/|g0| (non-minimum-phase episodes) and total power
  - error run-length histogram (feedback propagation signature)
  - phase-error distribution on errors
"""
import cmath
import math
import sys
from collections import Counter
from pathlib import Path

U, K = 256, 32
SYM_PER_BLOCK = 16384
FRAMES_PER_BLOCK = SYM_PER_BLOCK // U  # 64
PREAMBLE_CHIPS = 11520
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
            i = int(p[0])
            ys[i] = complex(float(p[1]), float(p[2]))
            refs[i] = r
    gains = []
    with open(outdir / f"autopsy-gains-{tag}.csv") as f:
        next(f)
        for line in f:
            p = line.split(",")
            gains.append((complex(float(p[1]), float(p[2])),
                          complex(float(p[3]), float(p[4]))))
    return ys, refs, gains


def nearest8(y):
    a = cmath.phase(y)
    return int(round(a / (math.pi / 4))) % 8


def gain_at(gains, idx):
    blk, s = divmod(idx, SYM_PER_BLOCK)
    chip = (blk * FRAMES_PER_BLOCK + s // U) * (U + K) + s % U
    gi = (PREAMBLE_CHIPS + chip) * 4 // 100
    return gains[min(gi, len(gains) - 1)]


def med(v):
    return sorted(v)[len(v) // 2] if v else float("nan")


def main():
    d = Path(sys.argv[1])
    ny, nref, gains = load(d, "wn7-w0-b0")
    gy, gref, _ = load(d, "wn7-w0-b0-genie")
    idxs = sorted(set(ny) & set(gy))
    nsym = len(idxs)
    comp = [abs(g0) ** 2 + abs(g1) ** 2 for (g0, g1) in gains]
    print(f"symbols with truth: {nsym}; mean composite path power "
          f"{sum(comp) / len(comp) / 2:.3f} (sanity ~= 1)")

    nerr = {i for i in idxs if nearest8(ny[i]) != nref[i]}
    gerr = {i for i in idxs if nearest8(gy[i]) != gref[i]}
    print(f"\nuncoded SER: normal {len(nerr) / nsym:.4f} ({len(nerr)}), "
          f"genie {len(gerr) / nsym:.4f} ({len(gerr)}), "
          f"normal-only {len(nerr - gerr)}, genie-only {len(gerr - nerr)}")

    print("\n== per-block SER (normal / genie) ==")
    for b in range(BLOCKS):
        lo, hi = b * SYM_PER_BLOCK, (b + 1) * SYM_PER_BLOCK
        n = sum(1 for i in nerr if lo <= i < hi)
        g = sum(1 for i in gerr if lo <= i < hi)
        print(f"  blk {b:2d}: n {n / SYM_PER_BLOCK:.4f} ({n:5d})   "
              f"g {g / SYM_PER_BLOCK:.4f} ({g:5d})")

    print("\n== SER vs in-frame position u (bins of 16 symbols) ==")
    print("  bin  u-range   n-SER    g-SER    n-|dtheta|med")
    for bin_ in range(16):
        lo_u, hi_u = bin_ * 16, (bin_ + 1) * 16
        tot = ne = ge = 0
        dth = []
        for i in idxs:
            u = i % U
            if not (lo_u <= u < hi_u):
                continue
            tot += 1
            if i in nerr:
                ne += 1
                dth.append(abs(cmath.phase(ny[i] * PSK8[nref[i]].conjugate())))
            if i in gerr:
                ge += 1
        print(f"  {bin_:3d}  {lo_u:3d}-{hi_u - 1:3d}  {ne / tot:.4f}   "
              f"{ge / tot:.4f}   {math.degrees(med(dth)):6.1f} deg")

    # donut statistic per frame: 8th-power phase concentration of y/|y|.
    print("\n== donut frames (|sum (y/|y|)^8| / count < threshold) ==")
    for name, ys_, refs_, errset in (("normal", ny, nref, nerr),
                                     ("genie", gy, gref, gerr)):
        donuts = frames = 0
        donut_err = ok_err = donut_syms = ok_syms = 0
        for f0 in range(0, BLOCKS * SYM_PER_BLOCK, U):
            fi = [i for i in range(f0, f0 + U) if i in ys_]
            if len(fi) < U // 2:
                continue
            frames += 1
            acc = sum((ys_[i] / abs(ys_[i])) ** 8 for i in fi if abs(ys_[i]) > 1e-6)
            conc = abs(acc) / len(fi)
            errs_in = sum(1 for i in fi if i in errset)
            if conc < 0.3:
                donuts += 1
                donut_err += errs_in
                donut_syms += len(fi)
            else:
                ok_err += errs_in
                ok_syms += len(fi)
        print(f"  {name:6s}: {donuts}/{frames} donut frames; "
              f"SER inside donuts {donut_err / max(donut_syms, 1):.3f}, "
              f"outside {ok_err / max(ok_syms, 1):.3f} "
              f"(donut share of errors {donut_err / max(donut_err + ok_err, 1):.2f})")

    print("\n== SER vs path-ratio rho=|g1|/|g0| (non-min-phase test) ==")
    print("  rho bin        syms    n-SER    g-SER   share-of-n-errs")
    bins = [(0, 0.5), (0.5, 1.0), (1.0, 2.0), (2.0, 1e9)]
    for lo_r, hi_r in bins:
        tot = ne = ge = 0
        for i in idxs:
            g0, g1 = gain_at(gains, i)
            rho = abs(g1) / max(abs(g0), 1e-9)
            if not (lo_r <= rho < hi_r):
                continue
            tot += 1
            ne += i in nerr
            ge += i in gerr
        lbl = f"{lo_r:.1f}-{'inf' if hi_r > 100 else f'{hi_r:.1f}'}"
        print(f"  {lbl:12s} {tot:7d}  {ne / max(tot, 1):.4f}   "
              f"{ge / max(tot, 1):.4f}   {ne / max(len(nerr), 1):.2f}")

    print("\n== SER vs composite power (fade depth) ==")
    print("  power bin      syms    n-SER    g-SER")
    for lo_p, hi_p in [(0, 0.25), (0.25, 0.7), (0.7, 1.5), (1.5, 1e9)]:
        tot = ne = ge = 0
        for i in idxs:
            g0, g1 = gain_at(gains, i)
            p = abs(g0) ** 2 + abs(g1) ** 2
            if not (lo_p <= p < hi_p):
                continue
            tot += 1
            ne += i in nerr
            ge += i in gerr
        lbl = f"{lo_p:.2f}-{'inf' if hi_p > 100 else f'{hi_p:.2f}'}"
        print(f"  {lbl:12s} {tot:7d}  {ne / max(tot, 1):.4f}   {ge / max(tot, 1):.4f}")

    print("\n== error run lengths (consecutive errored symbols) ==")
    for name, errset in (("normal", nerr), ("genie", gerr)):
        runs = Counter()
        run = 0
        for i in idxs:
            if i in errset:
                run += 1
            elif run:
                runs[min(run, 10)] += 1
                run = 0
        if run:
            runs[min(run, 10)] += 1
        total_runs = sum(runs.values())
        mass = {k: k * v for k, v in runs.items()}
        long_mass = sum(v for k, v in mass.items() if k >= 5)
        print(f"  {name:6s}: runs {total_runs}, len>=5 share of errored syms "
              f"{long_mass / max(sum(mass.values()), 1):.2f}, "
              f"hist {dict(sorted(runs.items()))}")

    print("\n== |y| median: errors vs correct (confidence honesty) ==")
    for name, ys_, errset in (("normal", ny, nerr), ("genie", gy, gerr)):
        ev = [abs(ys_[i]) for i in idxs if i in errset]
        cv = [abs(ys_[i]) for i in idxs if i not in errset]
        print(f"  {name:6s}: err {med(ev):.3f} ({len(ev)}), ok {med(cv):.3f}")

    print("\n== llrstats (first pass, per block) ==")
    for name, tag in (("normal", "wn7-w0-b0"), ("genie", "wn7-w0-b0-genie")):
        with open(d / f"autopsy-llrstats-{tag}.csv") as f:
            next(f)
            rows = [line.strip().split(",") for line in f if line.startswith("first")]
        parts = []
        for p in rows:
            ratio = float(p[5]) / max(float(p[4]), 1e-9)
            parts.append(f"b{p[1]}:{int(p[3])}/{ratio:.3f}")
        print(f"  {name:6s}: errBits/wrongRatio  {'  '.join(parts)}")


main()
