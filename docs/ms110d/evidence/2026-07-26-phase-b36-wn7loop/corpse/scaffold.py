# §B3.6 scaffold analysis: per-frame first-pass label-error distribution per block,
# from the autopsy biterrs CSV (block,symbolInBlock,bitInSymbol; U=256 → frame =
# symbolInBlock // 256, 768 wire bits per frame at 8PSK). The converge/wander split
# separates perfectly at "frames under 20% label error": converged >= 35/64,
# reverting <= 33/64 (M0 corpse w0/b0).
import collections
import sys

errs = collections.Counter()
with open(sys.argv[1] if len(sys.argv) > 1 else 'autopsy-biterrs-wn7-w0-b0.csv') as f:
    next(f)
    for line in f:
        b, sym, bit = line.split(',')
        errs[(int(b), int(sym) // 256)] += 1

for b in range(11):
    fr = [100.0 * errs.get((b, f), 0) / 768 for f in range(64)]
    c20 = sum(1 for x in fr if x < 20)
    print(f"b{b}: frames<5%={sum(1 for x in fr if x < 5)} <10%={sum(1 for x in fr if x < 10)} "
          f"<20%={c20} worst-half-mean={sum(sorted(fr)[32:]) / 32:.1f}%")
