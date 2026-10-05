#!/usr/bin/env python3
"""Digest the filter-study CSVs: SNR for 10 % frame loss per configuration, and the charts.

Usage: analyze.py data/*.csv   (needs matplotlib; writes thresholds.csv and charts/ beside this file)

Every printed and plotted string is plain ASCII.
"""
import csv
import os
import sys
from collections import defaultdict

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt  # noqa: E402

matplotlib.rcParams["svg.fonttype"] = "none"  # text stays text: small, searchable SVGs

HERE = os.path.dirname(os.path.abspath(__file__))
TARGET = 0.10
ROLLOFFS = [0.35, 0.25, 0.15, 0.10, 0.05]
COLOURS = {0.35: "#1b4f9c", 0.25: "#2a9d8f", 0.15: "#8a6d00", 0.10: "#c4512d", 0.05: "#7b3fa0"}
CHANNELS = ["awgn", "moderate", "poor"]
CHANNEL_LABEL = {"awgn": "AWGN", "moderate": "Moderate (1 ms, 0.5 Hz)", "poor": "Poor (2 ms, 1 Hz)"}
MODES = ["ms110d-wn4", "ms110d-wn3"]
MODE_LABEL = {"ms110d-wn4": "WN4 (1200 bps)", "ms110d-wn3": "WN3 (600 bps)"}


def load(paths):
    rows = []
    for p in paths:
        with open(p, newline="") as f:
            for r in csv.DictReader(f):
                r["txRollOff"] = float(r["txRollOff"])
                r["rxRollOff"] = float(r["rxRollOff"])
                r["snrDb"] = float(r["snrDb"])
                r["payloadBytes"] = int(r["payloadBytes"])
                r["ran"] = r["ran"] == "1"
                r["trials"] = int(r["trials"])
                r["lost"] = int(r["lost"])
                # A rung skipped after two clean rungs below it counts as no loss.
                r["loss"] = float(r["lossRate"]) if r["ran"] else 0.0
                rows.append(r)
    return rows


def curves(rows):
    """(mode, channel, payload, filter, tx, rxkind) -> sorted [(snr, loss, row)]."""
    out = defaultdict(list)
    for r in rows:
        tx, rx = r["txRollOff"], r["rxRollOff"]
        kinds = []
        if abs(rx - 0.35) < 1e-9:
            kinds.append("standard")
        if abs(rx - tx) < 1e-9:
            kinds.append("matched")
        for k in kinds:
            out[(r["mode"], r["channel"], r["payloadBytes"], r["filter"], tx, k)].append((r["snrDb"], r["loss"], r))
    for v in out.values():
        v.sort(key=lambda t: t[0])
    return out


def snr_at(curve, target=TARGET):
    """Lowest SNR above which the loss stays at or below target, linear between rungs."""
    pts = [(s, l) for s, l, _ in curve]
    if not pts or pts[-1][1] > target:
        return None
    # Walk down from the top until the loss first exceeds the target.
    for i in range(len(pts) - 1, 0, -1):
        s1, l1 = pts[i]
        s0, l0 = pts[i - 1]
        if l0 > target >= l1:
            return s0 + (l0 - target) / (l0 - l1) * (s1 - s0)
    return float("-inf") if pts[0][1] <= target else None


FLOOR_FROM = 16.0


def floor(curve):
    """Pooled loss over the fading rungs at or above FLOOR_FROM dB: (lost, trials) or None."""
    lost = trials = 0
    for s, _, r in curve:
        if s >= FLOOR_FROM - 1e-9 and r["ran"]:
            lost += r["lost"]
            trials += r["trials"]
    return (lost, trials) if trials else None


def wilson(k, n, z=1.96):
    if n == 0:
        return (0.0, 1.0)
    p = k / n
    d = 1 + z * z / n
    c = p + z * z / (2 * n)
    m = z * ((p * (1 - p) / n) + z * z / (4 * n * n)) ** 0.5
    return ((c - m) / d, (c + m) / d)


def filter_order(name):
    if name == "none":
        return (1e9, name)
    lo, hi = name.split(":")[1].split("-")
    return (float(hi) - float(lo), name)


def width_label(name):
    if name == "none":
        return "no filter"
    lo, hi = name.split(":")[1].split("-")
    where = "centred" if is_centred(name) else "as rigs set it"
    return f"{(float(hi) - float(lo)) / 1000:.1f} kHz\n{lo}-{hi}\n{where}"


def main(paths):
    rows = load(paths)
    cs = curves(rows)
    os.makedirs(os.path.join(HERE, "charts"), exist_ok=True)

    # Threshold table.
    table = []
    for key, curve in sorted(cs.items(), key=lambda kv: (kv[0][0], kv[0][1], kv[0][2], filter_order(kv[0][3]), -kv[0][4], kv[0][5])):
        mode, ch, pb, flt, tx, rk = key
        s = snr_at(curve)
        fl = floor(curve) if ch != "awgn" else None
        lo, hi = wilson(*fl) if fl else (None, None)
        table.append({"mode": mode, "channel": ch, "payloadBytes": pb, "filter": flt, "txRollOff": tx,
                      "receiver": rk, "snr10": "" if s is None else ("below grid" if s == float("-inf") else f"{s:.1f}"),
                      "floorLost": "" if not fl else fl[0], "floorTrials": "" if not fl else fl[1],
                      "floorLoss": "" if not fl else f"{fl[0] / fl[1]:.3f}",
                      "floorCiLo": "" if not fl else f"{lo:.3f}", "floorCiHi": "" if not fl else f"{hi:.3f}"})
    with open(os.path.join(HERE, "thresholds.csv"), "w", newline="") as f:
        w = csv.DictWriter(f, fieldnames=list(table[0].keys()))
        w.writeheader()
        w.writerows(table)

    for pb, family, rk in CHART_SETS:
        flts = sorted({k[3] for k in cs if k[2] == pb and (k[3] == "none" or k[3].startswith(family + ":"))},
                      key=filter_order)
        if len(flts) < 2:
            continue
        plot_snr10(cs, pb, flts, rk, family)
        plot_loss(cs, pb, flts, rk, family)
        plot_floor(cs, pb, flts, rk, family)
    print_summary(cs)
    print_markdown(table)


# (frame size, filter family, receiver) for each chart set: the sweeps behind the others were
# run with the receiver left at 0.35 only.
CHART_SETS = [(960, "xtal", "standard"), (960, "xtal", "matched"), (960, "dsp", "standard"), (255, "xtal", "standard")]
WORK_FROM, WORK_TO = 10.0, 22.0


def cell(cs, mode, ch, pb, f, tx, rk):
    c = cs.get((mode, ch, pb, f, tx, rk))
    if not c:
        return "-"
    s = snr_at(c)
    ladder_top = max(t[0] for t in c)
    snr = f">{ladder_top:g}" if s is None else ("<grid" if s == float("-inf") else f"{s:.1f}")
    if ch == "awgn":
        return snr
    k, n = pooled(c, WORK_FROM, WORK_TO)
    return f"{snr} / {100 * k / n:.0f}%" if n else snr


def print_summary(cs):
    """The two tables the README quotes."""
    flts = ["none", "xtal:150-3150", "xtal:200-2900", "xtal:300-2700", "xtal:600-3000",
            "dsp:150-3150", "dsp:200-2900", "dsp:300-2700", "dsp:600-3000"]
    for pb in (960, 255):
        print(f"\n### Summary A, {pb}-byte frames: roll-off 0.35, receiver unchanged\n")
        present = [f for f in flts if any(k[2] == pb and k[3] == f for k in cs)]
        print("| mode, channel | " + " | ".join(present) + " |")
        print("|---|" + "---|" * len(present))
        for mode in MODES:
            for ch in CHANNELS:
                print(f"| {MODE_LABEL[mode]}, {ch} | " + " | ".join(cell(cs, mode, ch, pb, f, 0.35, "standard") for f in present) + " |")
    for f in ("xtal:300-2700", "xtal:600-3000", "none"):
        print(f"\n### Summary B, 960-byte frames through {f}: each roll-off, receiver unchanged / matched\n")
        print("| mode, channel | " + " | ".join(f"{tx:.2f}" for tx in ROLLOFFS) + " |")
        print("|---|" + "---|" * len(ROLLOFFS))
        for mode in MODES:
            for ch in CHANNELS:
                cells = []
                for tx in ROLLOFFS:
                    a = cell(cs, mode, ch, 960, f, tx, "standard")
                    b = cell(cs, mode, ch, 960, f, tx, "matched")
                    cells.append(a if tx == 0.35 else f"{a} ; {b}")
                print(f"| {MODE_LABEL[mode]}, {ch} | " + " | ".join(cells) + " |")


def is_centred(name):
    if name == "none":
        return False
    lo, hi = name.split(":")[1].split("-")
    return abs((float(lo) + float(hi)) / 2 - 1800) < 1


def family_label(family):
    return "crystal filter model" if family == "xtal" else "DSP filter model"


def rx_label(rk):
    return ("receiver matched filter left at 0.35" if rk == "standard"
            else "receiver matched filter set to the transmit roll-off")


def legend(ax):
    from matplotlib.lines import Line2D
    handles = [Line2D([], [], color=COLOURS[tx], marker="o", label=f"roll-off {tx:.2f} ({2400 * (1 + tx):.0f} Hz edge to edge)")
               for tx in ROLLOFFS]
    ax.legend(handles=handles, fontsize=7, loc="best")


def plot_snr10(cs, pb, flts, rk, family):
    fig, axes = plt.subplots(2, 3, figsize=(14, 8), sharex=True)
    xs = list(range(len(flts)))
    for i, mode in enumerate(MODES):
        for j, ch in enumerate(CHANNELS):
            ax = axes[i][j]
            top = 6.5 if ch == "awgn" else 23.0
            values = []
            for k, tx in enumerate(ROLLOFFS):
                ys, never = [], []
                for x, f in zip(xs, flts):
                    c = cs.get((mode, ch, pb, f, tx, rk))
                    s = snr_at(c) if c else None
                    ys.append(float("nan") if s is None or s == float("-inf") else s)
                    if c and s is None:
                        never.append(x + (k - 2) * 0.06)
                values += [y for y in ys if y == y]
                ax.plot(xs, ys, marker="o", color=COLOURS[tx])
                if never:
                    ax.plot(never, [top] * len(never), linestyle="none", marker="x", color=COLOURS[tx])
            if values:
                lo = min(values) - 0.5
                hi = max(max(values) + 0.5, lo + (3 if ch == "awgn" else 8))
                ax.set_ylim(lo, max(hi, top + 0.5) if ch != "awgn" else hi)
            ax.set_title(f"{MODE_LABEL[mode]}, {CHANNEL_LABEL[ch]}", fontsize=10)
            ax.grid(True, alpha=0.3)
            ax.set_xticks(xs)
            ax.set_xticklabels([width_label(f) for f in flts], fontsize=8)
            if j == 0:
                ax.set_ylabel("SNR for 10% frame loss (dB in 3 kHz)")
    legend(axes[0][0])
    fig.suptitle(f"SNR needed for 10% frame loss against receive filter, {pb}-byte frames\n"
                 f"{family_label(family)}, {rx_label(rk)}; signal centred at 1800 Hz audio. Lower is better;\n"
                 f"x at the top of a fading panel: still above 10% loss at 22 dB, the top of the ladder.",
                 fontsize=11)
    fig.tight_layout()
    base = os.path.join(HERE, "charts", f"snr10-{family}-{rk}-{pb}B")
    fig.savefig(base + ".png", dpi=100)
    fig.savefig(base + ".svg")
    plt.close(fig)


def representative_snr(cs, mode, ch, pb):
    """The lowest rung where the unfiltered standard 0.35 signal loses 10% or less."""
    c = cs.get((mode, ch, pb, "none", 0.35, "standard"))
    if not c:
        return None
    for s, l, _ in c:
        if l <= TARGET:
            return s
    return None


def pooled(curve, lo, hi):
    """Lost and trials over the rungs that ran between lo and hi dB inclusive."""
    lost = trials = 0
    for s, _, r in curve or []:
        if lo - 1e-9 <= s <= hi + 1e-9 and r["ran"]:
            lost += r["lost"]
            trials += r["trials"]
    return lost, trials


def loss_window(cs, mode, ch, pb):
    """The SNR window the fixed-SNR chart reads: the representative rung, and for the fading
    channels the rungs 2 dB either side as well (pooled, 120 frames instead of 40)."""
    snr = representative_snr(cs, mode, ch, pb)
    if snr is None:
        return None
    return (snr, snr) if ch == "awgn" else (snr - 2, snr + 2)


def plot_loss(cs, pb, flts, rk, family):
    fig, axes = plt.subplots(2, 3, figsize=(14, 8), sharex=True)
    xs = list(range(len(flts)))
    for i, mode in enumerate(MODES):
        for j, ch in enumerate(CHANNELS):
            ax = axes[i][j]
            window = loss_window(cs, mode, ch, pb)
            if window is None:
                ax.set_visible(False)
                continue
            for tx in ROLLOFFS:
                ys = []
                for f in flts:
                    k, n = pooled(cs.get((mode, ch, pb, f, tx, rk)), *window)
                    ys.append(100 * k / n if n else float("nan"))
                ax.plot(xs, ys, marker="o", color=COLOURS[tx])
            where = f"SNR {window[0]:g} dB" if window[0] == window[1] else f"SNR {window[0]:g} to {window[1]:g} dB"
            ax.set_title(f"{MODE_LABEL[mode]}, {CHANNEL_LABEL[ch]}, {where}", fontsize=10)
            ax.set_ylim(bottom=0)
            ax.grid(True, alpha=0.3)
            ax.set_xticks(xs)
            ax.set_xticklabels([width_label(f) for f in flts], fontsize=8)
            if j == 0:
                ax.set_ylabel("frame loss (%)")
    legend(axes[0][0])
    fig.suptitle(f"Frame loss against receive filter at a fixed SNR, {pb}-byte frames\n"
                 f"{family_label(family)}, {rx_label(rk)}. The SNR is where the unfiltered 0.35 signal first loses 10% or less;\n"
                 f"fading panels pool that rung and the rungs 2 dB either side. Lower is better.",
                 fontsize=11)
    fig.tight_layout()
    base = os.path.join(HERE, "charts", f"loss-{family}-{rk}-{pb}B")
    fig.savefig(base + ".png", dpi=100)
    fig.savefig(base + ".svg")
    plt.close(fig)


def plot_floor(cs, pb, flts, rk, family):
    fig, axes = plt.subplots(2, 2, figsize=(11, 8), sharex=True)
    xs = list(range(len(flts)))
    for i, mode in enumerate(MODES):
        for j, ch in enumerate(["moderate", "poor"]):
            ax = axes[i][j]
            for k, tx in enumerate(ROLLOFFS):
                ys, err_lo, err_hi = [], [], []
                for f in flts:
                    c = cs.get((mode, ch, pb, f, tx, rk))
                    fl = floor(c) if c else None
                    if not fl:
                        ys.append(float("nan"))
                        err_lo.append(0)
                        err_hi.append(0)
                        continue
                    p = fl[0] / fl[1]
                    lo, hi = wilson(*fl)
                    ys.append(100 * p)
                    err_lo.append(100 * (p - lo))
                    err_hi.append(100 * (hi - p))
                off = (k - 2) * 0.06
                ax.errorbar([x + off for x in xs], ys, yerr=[err_lo, err_hi], marker="o", capsize=2,
                            color=COLOURS[tx], linewidth=1.2, elinewidth=0.7)
            ax.set_title(f"{MODE_LABEL[mode]}, {CHANNEL_LABEL[ch]}", fontsize=10)
            ax.set_ylim(bottom=0)
            ax.grid(True, alpha=0.3)
            ax.set_xticks(xs)
            ax.set_xticklabels([width_label(f) for f in flts], fontsize=8)
            if j == 0:
                ax.set_ylabel(f"frame loss at {FLOOR_FROM:g} dB and above (%)")
    legend(axes[0][0])
    fig.suptitle(f"Frame loss that more signal does not cure: fading channels at {FLOOR_FROM:g}-22 dB pooled, {pb}-byte frames\n"
                 f"{family_label(family)}, {rx_label(rk)}; bars are 95% intervals", fontsize=11)
    fig.tight_layout()
    base = os.path.join(HERE, "charts", f"floor-{family}-{rk}-{pb}B")
    fig.savefig(base + ".png", dpi=100)
    fig.savefig(base + ".svg")
    plt.close(fig)


def print_markdown(table):
    """One markdown table per (payload, receiver): rows filter x roll-off, columns mode/channel."""
    idx = {(t["mode"], t["channel"], t["payloadBytes"], t["filter"], t["txRollOff"], t["receiver"]): t["snr10"] for t in table}
    for pb in sorted({t["payloadBytes"] for t in table}):
        for rk in ("standard", "matched"):
            flts = sorted({t["filter"] for t in table if t["payloadBytes"] == pb and t["receiver"] == rk}, key=filter_order)
            if not flts:
                continue
            print(f"\n### {pb}-byte frames, {rx_label(rk)}\n")
            cols = [(m, c) for m in MODES for c in CHANNELS]
            print("| filter | roll-off | " + " | ".join(f"{m[7:].upper()} {c}" for m, c in cols) + " |")
            print("|---|---|" + "---|" * len(cols))
            for f in flts:
                for tx in ROLLOFFS:
                    vals = [idx.get((m, c, pb, f, tx, rk)) for m, c in cols]
                    if all(v is None for v in vals):
                        continue
                    cells = ["-" if v in (None, "") else v for v in vals]
                    print(f"| {f} | {tx:.2f} | " + " | ".join(cells) + " |")


if __name__ == "__main__":
    main(sys.argv[1:] or [os.path.join(HERE, "data", f) for f in sorted(os.listdir(os.path.join(HERE, "data"))) if f.endswith(".csv")])
