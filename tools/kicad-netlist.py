#!/usr/bin/env python3
"""Netlist a KiCad 8 .kicad_sch, parsing the s-expressions properly.

Written after three successive regex attempts each produced confident nonsense rather
than an error, which is the failure mode that matters: a netlist that is wrong but
plausible gets believed.

  1. A wire regex assuming one line matched zero of 69 wires, so everything looked
     unconnected.
  2. Resolving pin geometry by prefix meant lib_id "Device:C" inherited the pin list of
     "Cmedia_USB_Audio:CM108AH", so capacitors were reported with pins named AVDD/LOL.
  3. Ignoring (mirror y) put Q1's pins where no wire was, and the whole PTT circuit came
     out as three isolated nets.
  4. Extracting a lib symbol's body by scanning to the next symbol swept in neighbouring
     symbols' pins, which put AVDD and DVDD on the GND net.

A real parser removes the whole class. It is about forty lines.
"""

import math
import sys
from collections import defaultdict


# --- s-expression parser ------------------------------------------------------

def parse(text):
    """Return nested lists; atoms are str. Quoted strings keep their contents."""
    tokens, i, n = [], 0, len(text)
    stack, cur = [], []
    while i < n:
        c = text[i]
        if c == "(":
            stack.append(cur)
            cur = []
            i += 1
        elif c == ")":
            done = cur
            cur = stack.pop()
            cur.append(done)
            i += 1
        elif c == '"':
            j = i + 1
            buf = []
            while j < n:
                if text[j] == "\\":
                    buf.append(text[j + 1]); j += 2; continue
                if text[j] == '"':
                    break
                buf.append(text[j]); j += 1
            cur.append("".join(buf))
            i = j + 1
        elif c.isspace():
            i += 1
        else:
            j = i
            while j < n and not text[j].isspace() and text[j] not in '()"':
                j += 1
            cur.append(text[i:j])
            i = j
    return cur[0] if len(cur) == 1 else cur


def head(node):
    return node[0] if node and isinstance(node[0], str) else None


def kids(node, name):
    return [c for c in node if isinstance(c, list) and head(c) == name]


def kid(node, name):
    got = kids(node, name)
    return got[0] if got else None


# --- load ---------------------------------------------------------------------

PATH = sys.argv[1] if len(sys.argv) > 1 else "single-sided-radio-interface.kicad_sch"
root = parse(open(PATH).read())

# --- library symbol pin geometry ----------------------------------------------

libpins = {}
libsec = kid(root, "lib_symbols") or []
for sym in kids(libsec, "symbol"):
    name = sym[1]
    pins = []

    def collect(node):
        for p in kids(node, "pin"):
            at = kid(p, "at")
            nm = kid(p, "name")
            num = kid(p, "number")
            if at and num:
                pins.append((float(at[1]), float(at[2]),
                             nm[1] if nm else "~", num[1]))
        for sub in kids(node, "symbol"):      # graphical sub-units of THIS symbol only
            collect(sub)

    collect(sym)
    # Dedupe by pin number: sub-units repeat the same pin in some libraries.
    seen, uniq = set(), []
    for px, py, nm, num in pins:
        if num in seen:
            continue
        seen.add(num)
        uniq.append((px, py, nm, num))
    libpins[name] = uniq

# --- placed instances ---------------------------------------------------------

instances = []
for sym in kids(root, "symbol"):
    lib_id = kid(sym, "lib_id")
    at = kid(sym, "at")
    if not lib_id or not at:
        continue
    lib = lib_id[1]
    x, y = float(at[1]), float(at[2])
    rot = float(at[3]) if len(at) > 3 else 0.0
    mir = kid(sym, "mirror")
    mx = -1.0 if (mir and mir[1] == "y") else 1.0
    my = -1.0 if (mir and mir[1] == "x") else 1.0

    ref = val = None
    for p in kids(sym, "property"):
        if p[1] == "Reference":
            ref = p[2]
        elif p[1] == "Value":
            val = p[2]
    if not ref or ref.startswith("#"):
        continue
    pins = libpins.get(lib)
    if pins is None:
        print(f"  warning: no library geometry for {ref} ({lib})", file=sys.stderr)
        continue

    th = math.radians(rot)
    for px, py, pname, pnum in pins:
        sx, sy = px * mx, py * my
        rx = sx * math.cos(th) - sy * math.sin(th)
        ry = sx * math.sin(th) + sy * math.cos(th)
        instances.append((ref, val, pnum, pname, (round(x + rx, 2), round(y - ry, 2))))

# --- wires, labels, power symbols, no-connects --------------------------------

wires = []
for w in kids(root, "wire"):
    pts = kid(w, "pts")
    xy = kids(pts, "xy")
    if len(xy) >= 2:
        wires.append(((float(xy[0][1]), float(xy[0][2])),
                      (float(xy[1][1]), float(xy[1][2]))))

labels = []
for kind in ("label", "global_label", "hierarchical_label"):
    for l in kids(root, kind):
        at = kid(l, "at")
        labels.append((l[1], (float(at[1]), float(at[2]))))

for sym in kids(root, "symbol"):
    lib_id = kid(sym, "lib_id")
    at = kid(sym, "at")
    if not lib_id or not lib_id[1].startswith("power:") or not at:
        continue
    val = None
    for p in kids(sym, "property"):
        if p[1] == "Value":
            val = p[2]
    # PWR_FLAG is an ERC marker, not a net name. Treating its Value as a label gives
    # every flagged net the same name, so VCC and GND weld together and the chip's
    # AVDD/DVDD appear grounded. Same for NC/DNP style markers.
    if val and val not in ("PWR_FLAG",):
        labels.append((val, (float(at[1]), float(at[2]))))

noconn = set()
for nc in kids(root, "no_connect"):
    at = kid(nc, "at")
    noconn.add((round(float(at[1]), 2), round(float(at[2]), 2)))

print(f"parsed: {len(libpins)} lib symbols, {len(instances)} placed pins, "
      f"{len(wires)} wires, {len(labels)} labels, {len(noconn)} no-connects")

# --- connectivity --------------------------------------------------------------

parent = {}


def find(a):
    parent.setdefault(a, a)
    while parent[a] != a:
        parent[a] = parent[parent[a]]
        a = parent[a]
    return a


def union(a, b):
    ra, rb = find(a), find(b)
    if ra != rb:
        parent[ra] = rb


def K(pt):
    return ("pt", round(pt[0], 2), round(pt[1], 2))


for a, b in wires:
    union(K(a), K(b))


def on_seg(p, a, b, tol=0.02):
    (px, py), (ax, ay), (bx, by) = p, a, b
    cross = (bx - ax) * (py - ay) - (by - ay) * (px - ax)
    if abs(cross) > tol * max(1.0, math.hypot(bx - ax, by - ay)):
        return False
    return (min(ax, bx) - tol <= px <= max(ax, bx) + tol
            and min(ay, by) - tol <= py <= max(ay, by) + tol)


# KiCad's real connection rule: wires join other wires at shared ENDPOINTS, or where a
# junction dot sits on them. A wire merely passing over a pin does NOT connect to it.
# Unioning on "lies anywhere along a segment" over-connects, and a long ground wire
# routed across the supply pins then drags AVDD and DVDD onto GND - a short that would
# be obvious on the bench but silent in a netlist listing.
junctions = []
for j in kids(root, "junction"):
    at = kid(j, "at")
    junctions.append((float(at[1]), float(at[2])))

endpoints = set()
for a, b in wires:
    endpoints.add(K(a))
    endpoints.add(K(b))

# A junction dot bonds every wire that passes through it.
for jp in junctions:
    for a, b in wires:
        if on_seg(jp, a, b):
            union(K(jp), K(a))

nodes = [(f"{ref}.{pnum}", (ref, val, pnum, pname), pt)
         for ref, val, pnum, pname, pt in instances]
label_nodes = [(f"@{nm}", None, pt) for nm, pt in labels]

# Pins bond only where they coincide with a wire endpoint or a junction.
for tag, _, pt in nodes:
    union(tag, K(pt))

# Labels are anchors placed on a wire, so they may sit mid-segment.
for tag, _, pt in label_nodes:
    union(tag, K(pt))
    for a, b in wires:
        if on_seg(pt, a, b):
            union(K(pt), K(a))

nodes += label_nodes

nets = defaultdict(list)
for tag, meta, pt in nodes:
    nets[find(tag)].append((tag, meta))

# --- report ---------------------------------------------------------------------

print("\n=== NETS ===")
rows, unnamed = [], 0
for root_id, members in nets.items():
    names = sorted({t[1:] for t, m in members if t.startswith("@")})
    pins = sorted((m for t, m in members if m), key=lambda m: (m[0], m[2]))
    if not pins:
        continue
    rows.append((names[0] if names else None, pins))

rows.sort(key=lambda r: (r[0] is None, r[0] or ""))
for name, pins in rows:
    if name is None:
        unnamed += 1
        name = f"N${unnamed:02d}"
    txt = ", ".join(f"{ref}.{pnum}" + (f"({pname})" if pname not in ("~", "") else "")
                    for ref, val, pnum, pname in pins)
    print(f"{name:12} {txt}")

print("\n=== COMPONENTS ===")
seen = {}
for ref, val, pnum, pname, pt in instances:
    seen[ref] = val
for ref in sorted(seen, key=lambda r: (r.rstrip("0123456789"), int(r.lstrip("ABCDEFGHIJKLMNOPQRSTUVWXYZ") or 0))):
    print(f"  {ref:5} {seen[ref]}")

print("\n=== NO-CONNECT PINS ===")
for ref, val, pnum, pname, pt in sorted(instances, key=lambda i: (i[0], i[2])):
    if (round(pt[0], 2), round(pt[1], 2)) in noconn:
        print(f"  {ref}.{pnum:>3} {pname}")

print("\n=== SANITY CHECKS ===")
bypin = defaultdict(set)
for ref, val, pnum, pname, pt in instances:
    bypin[ref].add(pnum)
u1 = bypin.get("U1", set())
print(f"  U1 distinct pins: {len(u1)}")
gnd = next((pins for name, pins in rows if name == "GND"), [])
bad = [f"{r}.{n}({pn})" for r, v, n, pn in gnd if pn in ("AVDD", "DVDD", "REGV")]
print(f"  supply pins wrongly on GND: {bad if bad else 'none'}")
