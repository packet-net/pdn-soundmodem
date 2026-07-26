#!/usr/bin/env python3
"""B3.5b M3 fidelity: O-inst finger-gain magnitudes (walsh diag lines, pass c)
vs the rig-recorded channel truth (autopsy-gains CSV, 96 Hz). Bars: rho >= 0.85
for finger0<->path0 and best(f4,f5)<->path1 on >=4/6 error corpses; lag fitted
within +/-208 ms of the derived data-start guess (same lag for both pairings)."""
import re
import sys

import numpy as np

CORPUS = {  # id -> (worker, burst)
    "E1": (2, 97), "E2": (2, 53), "E3": (3, 96),
    "E4": (1, 91), "E5": (1, 41), "E6": (2, 37),
}
D = "/home/tf/.claude/jobs/4be765aa/tmp/b35b/corpus"
SYM_S = 32.0 / 2400.0          # symbol period, s
DATA_S = 6336 * SYM_S          # 12672 coded wire bits / 2 per symbol = 576 sym x 11 blocks
GAIN_HZ = 96.0

def pearson(a, b):
    a = a - a.mean(); b = b - b.mean()
    d = np.sqrt((a * a).sum() * (b * b).sum())
    return float((a * b).sum() / d) if d > 0 else 0.0

ok = 0
for cid, (w, b) in CORPUS.items():
    frames = f"{D}/autopsy-frames-wn0-w{w}-b{b}-genie-wgoinst.log"
    gains = f"{D}/autopsy-gains-wn0-w{w}-b{b}-genie-wgoinst.csv"
    mags = []
    rx = re.compile(r"\|g\|=\[([0-9. eE+-]+)\]")
    for line in open(frames):
        m = rx.search(line)
        if m:
            mags.append([float(x) for x in m.group(1).split()])
    o = np.array(mags)                      # [symbols, 7]
    g = np.loadtxt(gains, delimiter=",", skiprows=1)
    g0 = np.abs(g[:, 1] + 1j * g[:, 2])     # |path0(t)| @96 Hz
    g1 = np.abs(g[:, 3] + 1j * g[:, 4])
    n = np.arange(len(o))
    t0_guess = len(g0) / GAIN_HZ - DATA_S   # data ends at the channel-span end
    best = (-2.0, 0.0)
    for dlag in range(-20, 21):             # +/-208 ms in 96 Hz steps
        t0 = t0_guess + dlag / GAIN_HZ
        idx = np.clip(((t0 + (n + 0.5) * SYM_S) * GAIN_HZ).astype(int), 0, len(g0) - 1)
        r = pearson(o[:, 0], g0[idx])
        if r > best[0]:
            best = (r, t0)
    rho0, t0 = best
    idx = np.clip(((t0 + (n + 0.5) * SYM_S) * GAIN_HZ).astype(int), 0, len(g1) - 1)
    rho1 = max(pearson(o[:, 4], g1[idx]), pearson(o[:, 5], g1[idx]))
    green = rho0 >= 0.85 and rho1 >= 0.85
    ok += green
    print(f"{cid} w{w} b{b}: symbols={len(o)} rho0={rho0:.3f} rho1={rho1:.3f} "
          f"lag={(t0 - t0_guess) * 1000:+.0f}ms {'GREEN' if green else 'RED'}")
print(f"M3: {ok}/6 green (bar: >=4/6)")
sys.exit(0 if ok >= 4 else 1)
