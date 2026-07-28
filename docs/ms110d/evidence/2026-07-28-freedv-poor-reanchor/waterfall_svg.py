#!/usr/bin/env python3
"""Render the re-anchored Poor waterfalls as a self-contained SVG (pure stdlib, no matplotlib).

Small multiples, one panel per datac mode, each overlaying:
  grey  = independent-burst (the #104 method), WattersonChannel Poor
  blue  = continuous-stream (codec2's method),  WattersonChannel Poor
  green = our modem through codec2's real ch --mpp
  red o = codec2's published MPP operating point (N/100 at op SNR)
X = true active-burst SNR (3 kHz), Y = packets received / 100.
"""
import csv, os

HERE = os.path.dirname(__file__)
DATA = os.path.join(HERE, "data")
PUB = {"datac0": (0.0, 70), "datac1": (5.0, 92), "datac3": (0.0, 74),
       "datac4": (-4.0, 90), "datac13": (-4.0, 90), "datac14": (-2.0, 90)}
ORDER = ["datac0", "datac1", "datac3", "datac4", "datac13", "datac14"]


def rows(path):
    return list(csv.DictReader(open(path))) if os.path.exists(path) else []


def managed(mode, which):
    return sorted((float(r["snrDb"]), 100 * float(r["successRate"]))
                  for r in rows(os.path.join(DATA, f"freedv-{mode}-poor-{which}.csv")))


def ch(mode):
    return sorted((float(r["trueSNR"]), 100 * int(r["recv"]) / int(r["N"]))
                  for r in rows(os.path.join(DATA, "ch-crosscheck.csv"))
                  if r["mode"].replace("freedv-", "") == mode and r["arm"] == "ourmodem")


# layout
PW, PH, MX, MY = 300, 190, 46, 34     # panel w/h, inner margins
COLS, GX, GY = 3, 24, 40
W = COLS * PW + (COLS + 1) * GX
ROWSN = (len(ORDER) + COLS - 1) // COLS
H = ROWSN * PH + (ROWSN + 1) * GY + 30
svg = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{H}" font-family="sans-serif" font-size="11">']
svg.append(f'<rect width="{W}" height="{H}" fill="white"/>')
svg.append(f'<text x="{W/2}" y="22" text-anchor="middle" font-size="15" font-weight="bold">'
           'FreeDV datac Poor/MPP re-anchor — burst vs stream vs codec2 ch, per 100</text>')


def panel(px, py, mode):
    b, s, c = managed(mode, "burst"), managed(mode, "stream"), ch(mode)
    op, pub = PUB[mode]
    xs = [x for x, _ in b + s + c] + [op]
    xlo, xhi = min(xs) - 0.5, max(xs) + 0.5
    ix, iy, iw, ih = px + MX, py + 8, PW - MX - 8, PH - MY - 8

    def X(v):
        return ix + (v - xlo) / (xhi - xlo) * iw

    def Y(v):
        return iy + (100 - v) / 100 * ih
    out = [f'<g>']
    # frame + grid
    out.append(f'<rect x="{ix}" y="{iy}" width="{iw}" height="{ih}" fill="none" stroke="#ccc"/>')
    for g in (0, 25, 50, 70, 90, 100):
        yy = Y(g)
        out.append(f'<line x1="{ix}" y1="{yy:.1f}" x2="{ix+iw}" y2="{yy:.1f}" stroke="#eee"/>')
        out.append(f'<text x="{ix-4}" y="{yy+3:.1f}" text-anchor="end" fill="#888">{g}</text>')
    for xv in range(int(xlo) + 1, int(xhi) + 1):
        xx = X(xv)
        out.append(f'<line x1="{xx:.1f}" y1="{iy}" x2="{xx:.1f}" y2="{iy+ih}" stroke="#f3f3f3"/>')
        out.append(f'<text x="{xx:.1f}" y="{iy+ih+13}" text-anchor="middle" fill="#888">{xv}</text>')

    def curve(pts, colour):
        if not pts:
            return
        d = " ".join(("M" if i == 0 else "L") + f"{X(x):.1f} {Y(y):.1f}" for i, (x, y) in enumerate(pts))
        out.append(f'<path d="{d}" fill="none" stroke="{colour}" stroke-width="2"/>')
        for x, y in pts:
            out.append(f'<circle cx="{X(x):.1f}" cy="{Y(y):.1f}" r="2" fill="{colour}"/>')
    curve(b, "#999")
    curve(s, "#1f6fd6")
    curve(c, "#2ca25f")
    out.append(f'<circle cx="{X(op):.1f}" cy="{Y(pub):.1f}" r="4.5" fill="none" stroke="#d62728" stroke-width="2.5"/>')
    out.append(f'<text x="{ix}" y="{py-2}" font-weight="bold">{mode} (pub {pub}/100 @ {op:+.0f} dB)</text>')
    out.append('</g>')
    return "\n".join(out)


for i, mode in enumerate(ORDER):
    r, c = divmod(i, COLS)
    px = GX + c * (PW + GX)
    py = 30 + GY + r * (PH + GY)
    svg.append(panel(px, py, mode))

# legend
ly = H - 14
items = [("#999", "burst (#104)"), ("#1f6fd6", "stream (codec2 method)"),
         ("#2ca25f", "our modem / ch --mpp"), ("#d62728", "published MPP point")]
lx = GX
for col, lab in items:
    svg.append(f'<line x1="{lx}" y1="{ly-4}" x2="{lx+22}" y2="{ly-4}" stroke="{col}" stroke-width="3"/>')
    svg.append(f'<text x="{lx+27}" y="{ly}">{lab}</text>')
    lx += 27 + len(lab) * 6.4 + 24
svg.append("</svg>")
open(os.path.join(HERE, "waterfalls.svg"), "w").write("\n".join(svg))
print("wrote waterfalls.svg")
