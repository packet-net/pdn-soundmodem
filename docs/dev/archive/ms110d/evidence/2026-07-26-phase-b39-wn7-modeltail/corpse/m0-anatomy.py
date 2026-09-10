#!/usr/bin/env python3
# B3.9 M0: oracle-matched vs excess-over-oracle anatomy of the WN7 residual.
# Per specimen: ship per-block coded errors (decoded.txt diff, 36864-bit blocks)
# vs the oracle-ceiling per-block line; census cross-check; honesty-gate sums.
import re, sys, os

BASE = "/home/tf/.claude/jobs/4be765aa/tmp/b39/m0"
BLOCK = 36864
# specimen -> (family, tag, census_errors, census_collapses)
SPECS = {
    "c-w1b0": ("canonical", "wn7-w1-b0-oracle", 20, 15),
    "c-w1b1": ("canonical", "wn7-w1-b1-oracle", 25, 9),
    "c-w2b1": ("canonical", "wn7-w2-b1-oracle", 38, 9),
    "d-w0b0": ("disjoint", "wn7-w0-b0-oracle", 16, 15),
    "d-w1b0": ("disjoint", "wn7-w1-b0-oracle", 12, 9),
    "d-w2b1": ("disjoint", "wn7-w2-b1-oracle", 6, 16),
    "d-w3b0": ("disjoint", "wn7-w3-b0-oracle", 14, 9),
}

fam_tot = {"canonical": {"ship": 0, "orc": 0, "matched": 0, "excess": 0, "beats": 0},
           "disjoint": {"ship": 0, "orc": 0, "matched": 0, "excess": 0, "beats": 0}}

for spec, (fam, tag, cen_err, cen_col) in SPECS.items():
    d = os.path.join(BASE, spec)
    summ = open(os.path.join(d, f"autopsy-summary-{tag}.txt")).read()
    m = re.search(r"coded errors (\d+) ", summ)
    ship_total = int(m.group(1))
    col = int(re.search(r"collapses (\d+)", summ).group(1))
    turbo = re.search(r"turbo (\S+),", summ).group(1)
    om = re.search(r"oracle coded errors per block: (.+)", summ)
    orc = {}
    for tok in om.group(1).split():
        b, e = tok[1:].split(":")
        orc[int(b)] = int(e)

    dec, pay = open(os.path.join(d, f"autopsy-decoded-{tag}.txt")).read().splitlines()[:2]
    n = min(len(dec), len(pay))
    ship = {}
    for i in range(n):
        if dec[i] != pay[i]:
            ship[i // BLOCK] = ship.get(i // BLOCK, 0) + 1

    ok = "OK" if (ship_total == cen_err and col == cen_col and sum(ship.values()) == ship_total) else "MISMATCH"
    blocks = sorted(set(ship) | {b for b, e in orc.items() if e})
    matched = sum(min(ship.get(b, 0), orc.get(b, 0)) for b in blocks)
    excess = sum(max(0, ship.get(b, 0) - orc.get(b, 0)) for b in blocks)
    beats = sum(max(0, orc.get(b, 0) - ship.get(b, 0)) for b in blocks)
    t = fam_tot[fam]
    t["ship"] += ship_total; t["orc"] += sum(orc.values())
    t["matched"] += matched; t["excess"] += excess; t["beats"] += beats

    print(f"{spec} [{fam}] ship={ship_total} oracle={sum(orc.values())} "
          f"matched={matched} excess={excess} beats-oracle={beats} "
          f"collapses={col} turbo={turbo} census-check={ok}")
    for b in blocks:
        print(f"    b{b}: ship={ship.get(b,0):4d} oracle={orc.get(b,0):4d}")

print()
pooled = {k: fam_tot["canonical"][k] + fam_tot["disjoint"][k] for k in fam_tot["canonical"]}
for fam in ("canonical", "disjoint"):
    t = fam_tot[fam]
    print(f"{fam}: ship={t['ship']} oracle={t['orc']} matched={t['matched']} "
          f"excess={t['excess']} beats-oracle={t['beats']}  (family budget ~32/3.24M)")
print(f"pooled: ship={pooled['ship']} oracle={pooled['orc']} matched={pooled['matched']} "
      f"excess={pooled['excess']} beats-oracle={pooled['beats']}  (pooled budget ~64/6.49M)")
print()
print("honesty gate: matched mass must be <= budget for a model-front arm to be registrable;")
print(f"  pooled matched = {pooled['matched']} vs 64; "
      f"canonical {fam_tot['canonical']['matched']} vs 32; disjoint {fam_tot['disjoint']['matched']} vs 32")
