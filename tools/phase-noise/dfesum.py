#!/usr/bin/env python3
"""dfesum.py - compress a verbose `sm-ota score --diagnostics` run into a per-burst DFE
summary. Reads score stdout on stdin (or a file arg) and, for each burst, prints the coded
BER + end reason from the table row alongside the DFE fade-tracking signature parsed from
the `frame@` diagnostic lines: first/min/median gain, the reference gain, peak `bad`-probe
count, and the fraction of frames pinned below the 0.10 collapse line.

This is the read-out for the phase-noise / level experiment: a burst that decodes shows
gain snapping back (min recovers, bad resets); a burst in the real-RF collapse shows gain
pinned low and bad climbing to the SignalLost limit (34 for QPSK U256+K32, 100 for WN2).
"""
import re
import statistics
import sys

ROW = re.compile(r"^\s{0,3}(\d+)\s+(\d+\.\d+)\s+(\S+)\s+(\S+)\s+(\S+)\s+"
                 r"(\S+)\s+(\S+)\s+(\S+)\s+(\w+)")
FR = re.compile(r"frame@(\d+):\s+gain=([\-\d.]+)\s+ref=([\-\d.]+).*?bad=(\d+)")


def flush(b):
    if b is None:
        return
    gains = b["gains"]
    if gains:
        g0 = gains[0]
        gmin = min(gains)
        gmed = statistics.median(gains)
        pinned = sum(1 for g in gains if g < 0.10) / len(gains)
        ref0 = b["refs"][0] if b["refs"] else float("nan")
        badmax = max(b["bads"]) if b["bads"] else 0
        dfe = (f"frames={len(gains):3d} g0={g0:.3f} ref0={ref0:.3f} "
               f"gmin={gmin:.3f} gmed={gmed:.3f} badmax={badmax:3d} "
               f"pinned<0.10={pinned*100:4.0f}%")
    else:
        dfe = "frames=  0 (no DFE trace - never reached tracking)"
    print(f"  #{b['idx']:<2} t={b['start']:>6}s asked={b['asked']:>5} "
          f"coded={b['coded']:>9} end={b['reason']:<13} | {dfe}")


def main():
    src = open(sys.argv[1]) if len(sys.argv) > 1 else sys.stdin
    cur = None
    header = []
    for line in src:
        if "frame@" in line:
            m = FR.search(line)
            if m and cur is not None:
                cur["gains"].append(float(m.group(2)))
                cur["refs"].append(float(m.group(3)))
                cur["bads"].append(int(m.group(4)))
            continue
        if any(k in line for k in ("accept@", "wid@", "refine@", "lock@",
                                   "reject@", "turbo", "anchor")):
            continue
        m = ROW.match(line)
        if m and m.group(7) not in ("unscheduled",):
            flush(cur)
            cur = {"idx": int(m.group(1)), "start": m.group(2), "asked": m.group(4),
                   "coded": m.group(7), "reason": m.group(9),
                   "gains": [], "refs": [], "bads": []}
            continue
        if cur is None and ("schedule" in line or "===" in line or "MISSED" in line):
            header.append(line.rstrip())
    flush(cur)
    if header:
        print("  " + " | ".join(h.strip() for h in header if h.strip())[:200])


if __name__ == "__main__":
    main()
