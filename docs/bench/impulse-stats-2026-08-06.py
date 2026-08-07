#!/usr/bin/env python3
"""Impulse-noise statistics v2 from the 40 m capture's raw chunks.

v1 counted packet transmissions as impulses (near-full-scale "events", durations
chopped by its own adaptive background). v2 fixes the methodology:

- Detection runs on OUT-OF-SLOT audio: the slice passband is 0-3 kHz (measured),
  the modem slots sit at 850 and 2150 Hz, so 2500-2950 Hz and 300-700 Hz are
  in-passband but signal-free. Static crashes are broadband and light both;
  packets light neither.
- Background is the 20th percentile of the band envelope per 10 s window -
  crashes are sparse, so the percentile never absorbs the event it measures.
- An impulse must exceed k x background in the PRIMARY band (2500-2950); its
  coincidence with the low band (300-700) is reported, separating broadband
  atmospherics from band-limited interference.

Caveat unchanged from v1: slice AGC compresses absolute amplitudes; rates and
durations are robust, the amplitude tail is a lower bound.
"""
import multiprocessing
import sys
import wave
from datetime import datetime, timezone

import numpy as np

K = 6.0
SMOOTH_MS = 0.5
MERGE_GAP_MS = 20.0
BG_WINDOW_S = 10
PRIMARY = (2500.0, 2950.0)
SECONDARY = (300.0, 700.0)


def band_envelope(x, rate, lo, hi):
    spectrum = np.fft.rfft(x)
    f = np.fft.rfftfreq(len(x), 1 / rate)
    spectrum[(f < lo) | (f > hi)] = 0
    banded = np.fft.irfft(spectrum, n=len(x))
    smooth = max(1, int(rate * SMOOTH_MS / 1000))
    return np.convolve(np.abs(banded), np.ones(smooth) / smooth, mode="same")


def background(env, rate):
    window = BG_WINDOW_S * rate
    blocks = len(env) // window
    bg = np.percentile(env[: blocks * window].reshape(blocks, window), 20, axis=1)
    return np.repeat(bg, window)


def runs_above(mask, rate):
    edges = np.flatnonzero(np.diff(mask.astype(np.int8)))
    if mask[0]:
        edges = np.insert(edges, 0, 0)
    if mask[-1]:
        edges = np.append(edges, len(mask) - 1)
    runs = edges.reshape(-1, 2)
    merged = []
    gap = int(rate * MERGE_GAP_MS / 1000)
    for start, end in runs:
        if merged and start - merged[-1][1] < gap:
            merged[-1][1] = end
        else:
            merged.append([start, end])
    return merged


def analyse(path):
    with wave.open(path, "rb") as w:
        rate = w.getframerate()
        raw = w.readframes(w.getnframes())
    x = np.frombuffer(raw, dtype=np.int16).astype(np.float32) / 32768.0
    if len(x) < 30 * rate:
        return None

    env_p = band_envelope(x, rate, *PRIMARY)
    env_s = band_envelope(x, rate, *SECONDARY)
    bg_p = background(env_p, rate)
    bg_s = background(env_s, rate)
    n = min(len(bg_p), len(env_p))
    mask = env_p[:n] > (K * np.maximum(bg_p[:n], 1e-7))

    stamp = path.split("raw-")[1].split(".")[0]
    t0 = datetime.strptime(stamp, "%Y%m%dT%H%M%SZ").replace(tzinfo=timezone.utc)
    impulses = []
    for start, end in runs_above(mask, rate):
        if end >= n:
            continue
        peak_db = 20 * np.log10(env_p[start:end + 1].max() / max(bg_p[start], 1e-7))
        sec_ratio = env_s[start:end + 1].max() / max(K * bg_s[start], 1e-7)
        impulses.append((t0.timestamp() + start / rate,
                         peak_db,
                         (end - start) * 1000.0 / rate,
                         sec_ratio > 1.0))
    return (t0, n / rate, impulses)


if __name__ != "__main__":
    sys.exit(0)

paths = sorted(sys.argv[1:])
multiprocessing.set_start_method("fork")
with multiprocessing.Pool() as pool:
    results = [r for r in pool.map(analyse, paths) if r]

all_imp = [i for _, _, imps in results for i in imps]
total_s = sum(s for _, s, _ in results)
broadband = [i for i in all_imp if i[3]]
print(f"{len(paths)} chunks, {total_s/3600:.1f} h analysed")
print(f"{len(all_imp)} events >{K:.0f}x background in {PRIMARY[0]:.0f}-{PRIMARY[1]:.0f} Hz "
      f"({len(all_imp)/(total_s/60):.2f}/min); {len(broadband)} "
      f"({100*len(broadband)/max(1,len(all_imp)):.0f} %) coincident in "
      f"{SECONDARY[0]:.0f}-{SECONDARY[1]:.0f} Hz (broadband atmospherics)")

print("\nbroadband-impulse rate by half hour (UTC):")
by_slot = {}
for t, db, ms, bb in broadband:
    slot = int(t // 1800) * 1800
    by_slot.setdefault(slot, []).append((db, ms))
for slot in sorted(by_slot):
    imps = by_slot[slot]
    label = datetime.fromtimestamp(slot, tz=timezone.utc).strftime("%H:%M")
    print(f"  {label}  {len(imps)/30:5.2f}/min   median {np.median([d for d, _ in imps]):4.1f} dB "
          f"over bg, median {np.median([m for _, m in imps]):5.1f} ms")

if broadband:
    db_all = np.array([d for _, d, _, _ in broadband])
    ms_all = np.array([m for _, _, m, _ in broadband])
    print("\nbroadband amplitude over background (dB): "
          f"p50 {np.percentile(db_all, 50):.1f}, p90 {np.percentile(db_all, 90):.1f}, "
          f"p99 {np.percentile(db_all, 99):.1f}, max {db_all.max():.1f}")
    print("broadband duration (ms): "
          f"p50 {np.percentile(ms_all, 50):.1f}, p90 {np.percentile(ms_all, 90):.1f}, "
          f"p99 {np.percentile(ms_all, 99):.1f}, max {ms_all.max():.0f}")
    sym = ms_all * 0.3
    print(f"span at 300 Bd (symbols): p50 {np.percentile(sym, 50):.2f}, "
          f"p90 {np.percentile(sym, 90):.2f}, p99 {np.percentile(sym, 99):.1f}")
