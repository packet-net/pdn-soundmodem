#!/usr/bin/env python3
# B3.9 M0 pass 2: LLR character + position anatomy of the residual blocks.
# - llrstats final/oracle rows: wire sign-errors + mean|LLR| right/wrong
# - ship coded error positions (decoded diff): clusters per block
# - oracle wire errors (oracle-biterrs): 16-bin symbol histogram per block
import os, re
from collections import defaultdict

BASE = "/home/tf/.claude/jobs/4be765aa/tmp/b39/m0"
BLOCK = 36864          # info bits per block
SYMS = 16384           # symbols per block
SPECS = {
    "c-w1b0": "wn7-w1-b0-oracle", "c-w1b1": "wn7-w1-b1-oracle",
    "c-w2b1": "wn7-w2-b1-oracle", "d-w0b0": "wn7-w0-b0-oracle",
    "d-w1b0": "wn7-w1-b0-oracle", "d-w2b1": "wn7-w2-b1-oracle",
    "d-w3b0": "wn7-w3-b0-oracle",
}

for spec, tag in SPECS.items():
    d = os.path.join(BASE, spec)
    summ = open(os.path.join(d, f"autopsy-summary-{tag}.txt")).read()
    orc = {}
    for tok in re.search(r"oracle coded errors per block: (.+)", summ).group(1).split():
        b, e = tok[1:].split(":")
        orc[int(b)] = int(e)

    dec, pay = open(os.path.join(d, f"autopsy-decoded-{tag}.txt")).read().splitlines()[:2]
    ship_pos = defaultdict(list)
    for i in range(min(len(dec), len(pay))):
        if dec[i] != pay[i]:
            ship_pos[i // BLOCK].append(i % BLOCK)

    llr = {}
    for line in open(os.path.join(d, f"autopsy-llrstats-{tag}.csv")):
        p = line.strip().split(",")
        if p[0] in ("final", "oracle"):
            llr[(p[0], int(p[1]))] = (int(p[3]), float(p[4]), float(p[5]))

    owire = defaultdict(list)
    with open(os.path.join(d, f"autopsy-oracle-biterrs-{tag}.csv")) as f:
        next(f)
        for line in f:
            b, s, _ = line.strip().split(",")
            owire[int(b)].append(int(s))

    blocks = sorted(set(ship_pos) | {b for b, e in orc.items() if e})
    print(f"== {spec}")
    for b in blocks:
        pos = sorted(ship_pos.get(b, []))
        clusters = []
        for p in pos:
            if clusters and p - clusters[-1][1] <= 1000:
                clusters[-1][1] = p; clusters[-1][2] += 1
            else:
                clusters.append([p, p, 1])
        cl = " ".join(f"[{a}-{z}]x{n}" for a, z, n in clusters) or "-"
        out = [f"  b{b}: ship={len(pos)} oracle={orc.get(b,0)} shipClusters={cl}"]
        for pas in ("final", "oracle"):
            if (pas, b) in llr:
                e, sr, sw = llr[(pas, b)]
                right = 49152 - e
                mr = sr / right if right else 0.0
                mw = sw / e if e else 0.0
                out.append(f"    {pas}: wireErr={e} mean|LLR|right={mr:.1f} wrong={mw:.1f}")
        ow = owire.get(b, [])
        if ow:
            hist = [0] * 16
            for s in ow:
                hist[min(15, s * 16 // SYMS)] += 1
            out.append(f"    oracleWire hist16={hist} n={len(ow)}")
        print("\n".join(out))
