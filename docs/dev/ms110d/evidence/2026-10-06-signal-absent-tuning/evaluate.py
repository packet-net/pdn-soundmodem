#!/usr/bin/env python3
"""Scores signal-absent settings against the traces Ms110dSignalAbsentTuning writes (issue #553).

  evaluate.py TRACES floored|plain WINDOW_S SIGMAS     frames lost against the receiver before #553,
                                                      weak bursts left locked, release latency
  evaluate.py TRACES --margin WINDOW_S[,WINDOW_S...]  the lowest window, in noise sigmas, that still
                                                      had a frame to come in its burst

Each trace was run with the release switched off, so the receiver behaved as before #553; since the
statistic writes no receiver state, the point a setting would have let go is exact, and any frame
read after it in the same burst is a frame that setting loses.
"""
import collections, glob, math, os, sys

def lags(k): return 25 if k == 48 else (15 if k == 32 else 13)

def load(path):
    ev = [line.rstrip('\n').split(',') for line in open(path)]
    h = ev[0]
    return dict(id=h[1], wn=int(h[2]), fl=int(h[3]), k=int(h[4]), start=int(h[5]), end=int(h[6])), ev[1:]

def line_for(meta, window_s, sigmas):
    frames = math.ceil(window_s * 2400 / meta['fl'])
    return frames, sigmas * math.sqrt(lags(meta['k']) / frames)

def score(meta, ev, variant, window_s, sigmas):
    frames, line = line_for(meta, window_s, sigmas)
    win = collections.deque(); total = 0.0
    released = None; skip = False; read_before = read_with = 0; main_end = None
    for e in ev:
        if e[0] == 'P':
            if skip: continue
            v = float(e[2] if variant == 'plain' else e[3])
            win.append(v); total += v
            if len(win) > frames: total -= win.popleft()
            if len(win) == frames and total / frames < line:
                skip = True
                released = released if released is not None else int(e[1])
        elif e[0] == 'F':
            read_before += 1
            if not skip: read_with += 1
        elif e[0] == 'B':
            main_end = main_end or e[1]
            skip = False; win.clear(); total = 0.0
    return read_before, read_with, released, main_end

def main():
    d = sys.argv[1]
    files = sorted(glob.glob(os.path.join(d, '*.csv')))
    if sys.argv[2] == '--margin':
        for window_s in [float(x) for x in sys.argv[3].split(',')]:
            worst = []
            for f in files:
                meta, ev = load(f)
                if meta['id'][0] == 'C': continue
                frames, unit = line_for(meta, window_s, 1.0)
                seq = []
                for e in ev:
                    if e[0] == 'B': break
                    if e[0] == 'P': seq.append(float(e[3]))
                    elif e[0] == 'F': seq.append(None)
                later = [False] * (len(seq) + 1)
                for i in range(len(seq) - 1, -1, -1): later[i] = later[i + 1] or seq[i] is None
                win = collections.deque(); total = 0.0; low = math.inf
                for i, v in enumerate(seq):
                    if v is None: continue
                    win.append(v); total += v
                    if len(win) > frames: total -= win.popleft()
                    if len(win) == frames and later[i + 1]: low = min(low, total / frames / unit)
                if low < math.inf: worst.append((low, meta['id']))
            worst.sort()
            print(f"{window_s:g} s: " + ', '.join(f"{m:.2f} {i}" for m, i in worst[:6]))
        return
    variant, window_s, sigmas = sys.argv[2], float(sys.argv[3]), float(sys.argv[4])
    lossy = []; latency = []; stuck = []; cut = []; before = after = cases = 0
    for f in files:
        meta, ev = load(f)
        read_before, read_with, released, main_end = score(meta, ev, variant, window_s, sigmas)
        if meta['id'][0] != 'C':
            cases += 1; before += read_before; after += read_with
            if read_with < read_before: lossy.append((meta['id'], read_before, read_with))
            continue
        if not any(e[0] == 'P' for e in ev): continue
        if main_end == 'Eom':
            if released is not None:
                t = 0; t_end = None
                for e in ev:
                    if e[0] == 'P': t = int(e[1])
                    if e[0] == 'B': t_end = t; break
                if released <= t_end: cut.append(meta['id'])
            continue
        if released is None:
            if main_end is None: stuck.append(meta['id'])
            continue
        latency.append((released - meta['end']) / 9600)
    print(f"{variant} {window_s:g} s {sigmas:g} sigma: {after}/{before} frames over {cases} fading cases, "
          f"{len(lossy)} cases lose frames; {len(stuck)} weak bursts left locked, {len(cut)} readable ones cut")
    for x in lossy[:8]: print('  loses', x)
    if latency:
        latency.sort()
        print(f"  let go after a weak burst's end: n={len(latency)} median {latency[len(latency)//2]:.2f} "
              f"p95 {latency[int(0.95*(len(latency)-1))]:.2f} max {latency[-1]:.2f} s")

main()
