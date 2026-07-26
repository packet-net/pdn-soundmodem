# B3.7 E1' post-mortem: per-frame frozen wire-error delta (cons - free), classed by
# the (free-solve lag, constrained-solve lag) transition. Uses the LAST frozen-frame
# sighting per (b, f) (= the diag pass, which is what the frozen-biterrs CSVs measure).
import collections
import re
import sys

rx = re.compile(r"frozen-frame b(\d+) f(\d+): lag=(\d+)")

def lags(path):
    d = {}
    with open(path) as f:
        for line in f:
            m = rx.search(line)
            if m:
                d[(int(m.group(1)), int(m.group(2)))] = int(m.group(3))  # last wins
    return d

def errs(path):
    e = collections.Counter()
    with open(path) as f:
        next(f)
        for line in f:
            b, sym, bit = line.split(',')
            e[(int(b), int(sym) // 256)] += 1
    return e

free_lag = lags(sys.argv[1])
cons_lag = lags(sys.argv[2])
free_err = errs(sys.argv[3])
cons_err = errs(sys.argv[4])

def cls(l):
    return "5" if l == 5 else ("alias" if l in (10, 11) else ("0" if l == 0 else "other"))

by_class = collections.defaultdict(list)
by_block = collections.defaultdict(int)
for k in sorted(free_lag):
    if k not in cons_lag:
        continue
    d = cons_err.get(k, 0) - free_err.get(k, 0)
    by_class[(cls(free_lag[k]), cls(cons_lag[k]))].append(d)
    by_block[k[0]] += d

for (a, b), ds in sorted(by_class.items(), key=lambda kv: -len(kv[1])):
    mean = sum(ds) / len(ds)
    worse = sum(1 for d in ds if d > 10)
    better = sum(1 for d in ds if d < -10)
    print(f"{a}->{b}: n={len(ds)} meanΔ={mean:+.1f} bits/frame (of 768) worse>10={worse} better<-10={better}")
print("per-block Δ:", " ".join(f"b{b}:{d:+d}" for b, d in sorted(by_block.items())))
