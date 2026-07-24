#!/usr/bin/env python3
"""B3 tail-autopsy analyzer: correlate a corpse dump's per-symbol equalizer output
(vs truth), the frame-diagnostics timeline, and the true channel gain trajectory.

Usage: analyze_autopsy.py <dir> <tag>     e.g. analyze_autopsy.py out wn5-w0-b3
"""
import math
import re
import sys
from pathlib import Path

GEOM = {  # wn: (U, K, bits/symbol)
    1: (48, 48, 1), 2: (48, 48, 1), 3: (96, 32, 1), 4: (96, 32, 1),
    5: (256, 32, 1), 6: (256, 32, 2), 7: (256, 32, 3), 13: (256, 32, 2),
}
PREAMBLE_CHIPS = 20 * 18 * 32  # PreambleSuperframes=20, M>1, TlcBlocks=0


def main() -> None:
    outdir, tag = Path(sys.argv[1]), sys.argv[2]
    wn = int(re.match(r"wn(\d+)", tag).group(1))
    u, k, bps = GEOM[wn]

    summary = (outdir / f"autopsy-summary-{tag}.txt").read_text()
    print(summary)
    sym_per_block = int(re.search(r"\((\d+) symbols/block\)", summary).group(1))
    frames_per_block = sym_per_block // u

    # Per-symbol equalizer output vs truth.
    rows = []  # (idx, y, ref_idx, ref)
    with open(outdir / f"autopsy-symbols-{tag}.csv") as f:
        next(f)
        for line in f:
            p = line.split(",")
            rows.append((int(p[0]), complex(float(p[1]), float(p[2])),
                         int(p[3]), complex(float(p[4]), float(p[5]))))

    # True composite channel power per symbol air-time (mask-harness index mapping).
    gains = []
    with open(outdir / f"autopsy-gains-{tag}.csv") as f:
        next(f)
        for line in f:
            p = line.split(",")
            g0 = complex(float(p[1]), float(p[2]))
            g1 = complex(float(p[3]), float(p[4]))
            gains.append((abs(g0) ** 2 + abs(g1) ** 2) / 2)

    def air_gain(idx: int) -> float:
        blk, s = divmod(idx, sym_per_block)
        chip = (blk * frames_per_block + s // u) * (u + k) + s % u
        gi = (PREAMBLE_CHIPS + chip) * 4 // 100
        return gains[min(gi, len(gains) - 1)]

    # Uncoded symbol errors (any wrong bit in the symbol).
    err_syms = set()
    with open(outdir / f"autopsy-biterrs-{tag}.csv") as f:
        next(f)
        for line in f:
            p = line.split(",")
            err_syms.add(int(p[0]) * sym_per_block + int(p[1]))

    # ---- 1. Death map: per-frame symbol-error rate along the burst ----
    n_frames = (len(rows) + u - 1) // u
    frame_err = [0] * n_frames
    frame_fade = [0.0] * n_frames
    for idx, y, ridx, ref in rows:
        if ridx < 0:
            continue
        fr = idx // u
        if idx in err_syms:
            frame_err[fr] += 1
        frame_fade[fr] += air_gain(idx) / u

    print(f"== death map: {n_frames} frames of {u} symbols "
          f"(frame SER | composite gain) ==")
    bad_frames = [(f, e) for f, e in enumerate(frame_err) if e > u * 0.05]
    for f in range(n_frames):
        bar = "#" * min(60, int(60 * frame_err[f] / u))
        if frame_err[f] > 0 or f % 20 == 0:
            print(f"  fr{f:4d}  ser={frame_err[f] / u:5.2f}  "
                  f"gain={frame_fade[f]:5.2f}  {bar}")
    print(f"  {len(bad_frames)} frames above 5% SER hold "
          f"{sum(e for _, e in bad_frames)}/{len(err_syms)} symbol errors")

    # ---- 2. Rotation autopsy inside the dead frames ----
    # For each errored symbol, the rotation of the equalized output relative to truth.
    # BPSK lock -> cluster at 180 deg; QPSK at +-90; noise/collapse -> uniform.
    hist = [0] * 12  # 30-degree bins of arg(y * conj(ref))
    dead = {f for f, _ in bad_frames}
    n_rot = 0
    for idx, y, ridx, ref in rows:
        if ridx < 0 or idx // u not in dead or idx not in err_syms:
            continue
        if abs(y) < 1e-6:
            continue
        ang = math.degrees(math.atan2((y * ref.conjugate()).imag,
                                      (y * ref.conjugate()).real))
        hist[int(((ang + 180) % 360) // 30)] += 1
        n_rot += 1
    print("\n== error-symbol rotation histogram (dead frames, deg bins) ==")
    for b in range(12):
        lo = -180 + b * 30
        frac = hist[b] / max(1, n_rot)
        print(f"  [{lo:+4d},{lo + 30:+4d})  {hist[b]:6d}  {'#' * int(60 * frac)}")

    # Magnitude of equalized output on errored vs clean symbols in dead frames:
    # collapse (taps died) reads as tiny |y|; a lock reads as healthy |y|.
    mags_err, mags_ok = [], []
    for idx, y, ridx, ref in rows:
        if ridx < 0 or idx // u not in dead:
            continue
        (mags_err if idx in err_syms else mags_ok).append(abs(y))
    if mags_err:
        mags_err.sort()
        mags_ok.sort()
        med = lambda a: a[len(a) // 2] if a else float("nan")
        print(f"\n  dead-frame |y| median: errors {med(mags_err):.3f}, "
              f"correct {med(mags_ok):.3f}  (truth |ref|=1)")

    # ---- 3. Frame-diagnostics timeline through the death region ----
    print("\n== frame diagnostics through dead region (context +-3 frames) ==")
    diag = (outdir / f"autopsy-frames-{tag}.log").read_text().splitlines()
    show = set()
    for f, _ in bad_frames:
        show.update(range(max(0, f - 3), min(len(diag), f + 4)))
    last = -10
    for f in sorted(show):
        if f >= len(diag):
            break
        if f != last + 1:
            print("  ...")
        marker = ">>" if f in dead else "  "
        print(f"{marker} [{f:4d}] {diag[f]}")
        last = f


if __name__ == "__main__":
    main()
