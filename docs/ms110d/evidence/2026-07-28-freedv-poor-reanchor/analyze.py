#!/usr/bin/env python3
"""Aggregate the Poor re-anchor arms into the effect decomposition. Pure stdlib.

Reads data/<mode>-poor-{burst,stream}.csv (managed WattersonChannel, the METHODOLOGY axis) and
data/ch-crosscheck.csv (codec2 `ch` --mpp, the CHANNEL-MODEL axis + the published anchor), interpolates
each arm's packets-per-100 to the mode's published MPP operating-point SNR, and prints:
  1. the per-mode methodology table (burst vs stream, both on our WattersonChannel),
  2. the per-mode channel-model table (our modem through ch vs codec2's own tx/rx anchor), and
  3. the decomposition of the #104 gap into channel-model dB and methodology points.
All SNR is true active-burst SNR in a 3 kHz noise bandwidth — codec2's published convention.
"""
import csv, glob, math, os

DATA = os.path.join(os.path.dirname(__file__), "data")

# codec2 README_data.md: (operating-point SNR dB, published packets/100 on MPP).
PUB = {"datac0": (0.0, 70), "datac1": (5.0, 92), "datac3": (0.0, 74),
       "datac4": (-4.0, 90), "datac13": (-4.0, 90), "datac14": (-2.0, 90)}
ORDER = ["datac0", "datac1", "datac3", "datac4", "datac13", "datac14"]


def load(path):
    if not os.path.exists(path):
        return []
    with open(path) as f:
        return list(csv.DictReader(f))


def curve_managed(rows):
    """(snrDb, per100) sorted, from a managed burst/stream CSV."""
    pts = [(float(r["snrDb"]), 100.0 * float(r["successRate"])) for r in rows]
    return sorted(pts)


def curve_ch(mode, arm):
    rows = [r for r in load(os.path.join(DATA, "ch-crosscheck.csv"))
            if r["mode"].replace("freedv-", "") == mode and r["arm"] == arm]
    pts = [(float(r["trueSNR"]), 100.0 * int(r["recv"]) / int(r["N"])) for r in rows]
    return sorted(pts)


def interp(pts, x):
    if not pts:
        return math.nan
    if x <= pts[0][0]:
        return pts[0][1]
    if x >= pts[-1][0]:
        return pts[-1][1]
    for i in range(1, len(pts)):
        if pts[i - 1][0] <= x <= pts[i][0]:
            (x0, y0), (x1, y1) = pts[i - 1], pts[i]
            return y0 + (x - x0) / (x1 - x0) * (y1 - y0)
    return math.nan


def snr_at(pts, target):
    """SNR (dB) where per100 crosses target, ascending — the N/100 knee."""
    for i in range(1, len(pts)):
        (x0, y0), (x1, y1) = pts[i - 1], pts[i]
        if y0 < target <= y1:
            return x0 + (target - y0) / (y1 - y0) * (x1 - x0)
    return math.nan


def f(x, s="%.0f"):
    return "—" if x is None or (isinstance(x, float) and math.isnan(x)) else s % x


print("## Poor re-anchor: methodology axis (WattersonChannel Poor, our modem)\n")
print("Independent-burst (the #104 method) vs continuous-stream (codec2's MPP method), same channel,\n"
      "each interpolated to the mode's published operating-point SNR.\n")
print("| mode | op SNR | burst N/100 | stream N/100 | published | stream−burst | stream−pub |")
print("|---|---|---|---|---|---|---|")
meth = {}
for m in ORDER:
    op, pub = PUB[m]
    b = interp(curve_managed(load(os.path.join(DATA, f"freedv-{m}-poor-burst.csv"))), op)
    s = interp(curve_managed(load(os.path.join(DATA, f"freedv-{m}-poor-stream.csv"))), op)
    meth[m] = (b, s)
    print(f"| {m} | {op:+.0f} | {f(b)} | {f(s)} | {pub} | "
          f"{f(s - b, '%+.0f') if not math.isnan(b*s) else '—'} | "
          f"{f(s - pub, '%+.0f') if not math.isnan(s) else '—'} |")

print("\n## Poor re-anchor: channel-model axis (codec2 `ch` --mpp)\n")
print("Our modem through codec2's own ch channel, and codec2's own tx/rx anchor through the same ch\n"
      "(the self-consistency check — must reproduce the published N/100). Interpolated to op SNR.\n")
print("| mode | op SNR | our modem /ch | codec2 anchor /ch | published | ch−Watterson(stream) |")
print("|---|---|---|---|---|---|")
for m in ORDER:
    op, pub = PUB[m]
    o = interp(curve_ch(m, "ourmodem"), op)
    an = interp(curve_ch(m, "anchor"), op)
    s = meth[m][1]
    delta = (o - s) if not (math.isnan(o) or math.isnan(s)) else math.nan
    print(f"| {m} | {op:+.0f} | {f(o)} | {f(an)} | {pub} | "
          f"{f(delta, '%+.0f') if not math.isnan(delta) else '—'} |")

print("\n## Decomposition of the #104 Poor gap\n")
print("The #104 baseline read burst N/100 below published; here that gap is split into the part the\n"
      "continuous-stream methodology recovers (stream−burst) and the residual (stream−published),\n"
      "which the channel-model axis attributes to ch-vs-Watterson + realisation/tail noise.\n")
print("| mode | #104 gap (pub−burst) | recovered by stream | residual (pub−stream) |")
print("|---|---|---|---|")
for m in ORDER:
    op, pub = PUB[m]
    b, s = meth[m]
    if math.isnan(b) or math.isnan(s):
        print(f"| {m} | — | — | — |")
        continue
    print(f"| {m} | {pub - b:+.0f} | {s - b:+.0f} | {pub - s:+.0f} |")
