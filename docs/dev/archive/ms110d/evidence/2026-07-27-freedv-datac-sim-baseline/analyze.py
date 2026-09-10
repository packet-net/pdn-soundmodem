#!/usr/bin/env python3
"""Aggregate the sim-baseline CSVs into the waterfall thresholds, the published-FreeDV
cross-checks, and the level-invariance verdicts. Pure stdlib; reads data/*.csv, prints markdown."""
import csv, glob, math, os, sys

DATA = os.path.join(os.path.dirname(__file__), "data")

MODES = ["freedv-datac0", "freedv-datac1", "freedv-datac3",
         "freedv-datac4", "freedv-datac13", "freedv-datac14"]

# Published FreeDV anchors.
# AWGN 10% PER (rowetel.com/?p=7167, 2020): only datac1/datac3 published.
PUB_AWGN = {"freedv-datac1": 3.0, "freedv-datac3": -3.0}
# MPP (= our Poor, 2 ms/1 Hz) operating point: (SNR dB, packets-per-100) from
# codec2 README_data.md. Metric is packets received/100 at the operating SNR.
PUB_MPP = {"freedv-datac0": (0.0, 70), "freedv-datac1": (5.0, 92),
           "freedv-datac3": (0.0, 74), "freedv-datac4": (-4.0, 90),
           "freedv-datac13": (-4.0, 90), "freedv-datac14": (-2.0, 90)}


def load(suffix):
    path = os.path.join(DATA, suffix + ".csv")
    if not os.path.exists(path):
        return []
    with open(path) as f:
        return [row for row in csv.DictReader(f)]


def curve(rows, level0_only=True):
    pts = [(float(r["snrDb"]), float(r["successRate"]), int(r["successes"]), int(r["trials"]))
           for r in rows if (not level0_only or float(r.get("levelDb", 0)) == 0)]
    return sorted(pts)


def threshold(pts, target):
    for i in range(1, len(pts)):
        s0, p0, *_ = pts[i - 1]
        s1, p1, *_ = pts[i]
        if p0 < target <= p1:
            return s0 + (target - p0) / (p1 - p0) * (s1 - s0)
    return math.nan


def wilson(k, n, z=1.96):
    if n == 0:
        return (0.0, 1.0)
    p = k / n
    d = 1 + z * z / n
    c = p + z * z / (2 * n)
    m = z * math.sqrt(p * (1 - p) / n + z * z / (4 * n * n))
    return ((c - m) / d, (c + m) / d)


def fmt(x, s="%+.1f"):
    return "—" if x is None or (isinstance(x, float) and math.isnan(x)) else s % x


def short(m):
    return m.replace("freedv-", "")


print("## Per-mode thresholds (50% / 90% frame-or-packet success), dB in 3 kHz\n")
print("| mode | AWGN pkt 50% | AWGN pkt 90% | AWGN frame 50% | Poor pkt 50% | "
      "Poor frame 50% | Good frame 50% |")
print("|---|---|---|---|---|---|---|")
summary = {}
for m in MODES:
    ap = curve(load(f"{m}-awgn-packet"))
    af = curve(load(f"{m}-awgn-frame"))
    pp = curve(load(f"{m}-poor-packet"))
    pf = curve(load(f"{m}-poor-frame"))
    gf = curve(load(f"{m}-good-frame"))
    row = dict(ap50=threshold(ap, .5), ap90=threshold(ap, .9), af50=threshold(af, .5),
               pp50=threshold(pp, .5), pf50=threshold(pf, .5), gf50=threshold(gf, .5))
    summary[m] = row
    print(f"| {short(m)} | {fmt(row['ap50'])} | {fmt(row['ap90'])} | {fmt(row['af50'])} | "
          f"{fmt(row['pp50'])} | {fmt(row['pf50'])} | {fmt(row['gf50'])} |")

print("\n## AWGN cross-check vs published FreeDV (10% PER = 90% packet success)\n")
print("| mode | our AWGN pkt 90% (dB) | FreeDV AWGN 10%PER (dB) | delta |")
print("|---|---|---|---|")
for m, pub in PUB_AWGN.items():
    ours = summary[m]["ap90"]
    d = ours - pub if not math.isnan(ours) else math.nan
    print(f"| {short(m)} | {fmt(ours)} | {fmt(pub)} | {fmt(d)} |")

print("\n## Poor (MPP) cross-check vs published FreeDV (packets/100 at operating SNR)\n")
print("| mode | op SNR (dB) | our pkt succ (95% CI) | FreeDV N/100 | reading |")
print("|---|---|---|---|---|")
for m in MODES:
    op, pubN = PUB_MPP[m]
    rows = load(f"{m}-poor-packet")
    match = [r for r in rows if abs(float(r["snrDb"]) - op) < 1e-6 and float(r.get("levelDb", 0)) == 0]
    if not match:
        print(f"| {short(m)} | {fmt(op)} | (op SNR not in grid) | {pubN}/100 | — |")
        continue
    r = match[0]
    k, n = int(r["successes"]), int(r["trials"])
    lo, hi = wilson(k, n)
    ours = 100 * k / n
    within = lo * 100 <= pubN <= hi * 100
    reading = "consistent" if within else ("above pub" if ours > pubN else "below pub")
    print(f"| {short(m)} | {fmt(op)} | {ours:.0f}/100 [{lo*100:.0f}..{hi*100:.0f}] | "
          f"{pubN}/100 | {reading} |")

print("\n## Level-invariance probe (fixed SNR, absolute level −60…+20 dB)\n")
print("The WN2-relevant direction is DOWNWARD (a receiver delivering a low level): a level-"
      "sensitive un-normalised front end sags there even at fixed SNR. Upward level is a separate,"
      " expected effect — the modem's short-domain hard clip — reported as the clipping onset.\n")
print("| mode | SNR (dB) | down succ (−60…0) | down verdict | clip onset | +20 dB succ |")
print("|---|---|---|---|---|---|")
for m in MODES:
    rows = load(f"{m}-levelprobe")
    by_snr = {}
    for r in rows:
        by_snr.setdefault(float(r["snrDb"]), []).append(r)
    for snr in sorted(by_snr):
        rs = sorted(by_snr[snr], key=lambda r: float(r["levelDb"]))
        n = int(rs[0]["trials"])
        slack = 3.0 / max(1, n)
        down = [float(r["successRate"]) for r in rs if float(r["levelDb"]) <= 0]
        dmn, dmx = min(down), max(down)
        verdict = "INVARIANT" if dmx - dmn <= slack else "LEVEL-DEP"
        # clip onset: lowest positive level whose success drops a slack below the nominal
        nominal = next(float(r["successRate"]) for r in rs if float(r["levelDb"]) == 0)
        onset = "—"
        for r in rs:
            if float(r["levelDb"]) > 0 and float(r["successRate"]) < nominal - slack:
                onset = f"+{float(r['levelDb']):.0f} dB"
                break
        top = next(float(r["successRate"]) for r in rs if float(r["levelDb"]) == 20)
        print(f"| {short(m)} | {fmt(snr)} | {dmn*100:.0f}%..{dmx*100:.0f}% | {verdict} | "
              f"{onset} | {top*100:.0f}% |")
