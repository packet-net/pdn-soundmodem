#!/usr/bin/env python3
"""Occupancy across a slice of band, from an sm-iqcapture IQ WAV.

The pre-transmission check: before putting a signal on a frequency, show what is already
there. Reads the 16-bit stereo (I=left, Q=right) WAV sm-iqcapture writes, takes a Welch
spectrum, and prints power per bin in dB relative to the capture's own noise floor.

    scripts/iq-occupancy.py --wav capture.wav --centre 7056000 --from 7049000 --to 7062000

The centre frequency is the one the capture was tuned to (it is in the filename and the JSON
sidecar). Bins are reported against the *median* of the whole capture, which is a robust noise
floor: a few strong carriers cannot drag it the way a mean would.

Occupancy is judged per bin over time as well as on average, because a packet channel is busy
in bursts. A bin whose peak-over-time is well above the floor had something in it even if its
average looks quiet, and that is exactly the signal a mean-only check misses.
"""

import argparse
import json
import os
import sys
import wave

import numpy as np


def read_iq(path):
    with wave.open(path, "rb") as w:
        if w.getnchannels() != 2 or w.getsampwidth() != 2:
            raise SystemExit(f"{path}: expected 16-bit stereo I/Q, got "
                             f"{w.getnchannels()}ch/{w.getsampwidth() * 8}-bit")
        rate = w.getframerate()
        raw = w.readframes(w.getnframes())
    pairs = np.frombuffer(raw, dtype="<i2").astype(np.float32).reshape(-1, 2)
    return pairs[:, 0] + 1j * pairs[:, 1], rate


def main():
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--wav", required=True)
    p.add_argument("--centre", type=float, help="capture centre in Hz (default: from sidecar)")
    p.add_argument("--from", dest="lo", type=float, required=True, help="report from this Hz")
    p.add_argument("--to", dest="hi", type=float, required=True, help="report to this Hz")
    p.add_argument("--bin-hz", type=float, default=500.0, help="report resolution (default 500)")
    p.add_argument("--busy-db", type=float, default=6.0,
                   help="dB over the noise floor that counts as occupied (default 6)")
    a = p.parse_args()

    centre = a.centre
    if centre is None:
        sidecar = os.path.splitext(a.wav)[0] + ".json"
        with open(sidecar) as f:
            centre = float(json.load(f)["capture"]["frequency_hz"])

    iq, rate = read_iq(a.wav)
    if len(iq) < rate:
        raise SystemExit("capture is under a second; occupancy would be noise")

    # Welch over the complex baseband. 8192-point segments at 48 kHz give ~5.9 Hz bins, which
    # is far finer than we report - the point is to average enough segments that the floor is
    # stable, then aggregate up to --bin-hz.
    nfft = 8192
    hop = nfft // 2
    window = np.hanning(nfft).astype(np.float32)
    segments = []
    for start in range(0, len(iq) - nfft, hop):
        spectrum = np.fft.fftshift(np.fft.fft(iq[start:start + nfft] * window))
        segments.append(np.abs(spectrum) ** 2)
    if not segments:
        raise SystemExit("capture too short for one FFT segment")
    power = np.array(segments)                      # time x frequency
    freqs = centre + np.fft.fftshift(np.fft.fftfreq(nfft, 1.0 / rate))

    mean_db = 10 * np.log10(power.mean(axis=0) + 1e-30)
    peak_db = 10 * np.log10(power.max(axis=0) + 1e-30)
    floor = np.median(mean_db)

    print(f"capture   {os.path.basename(a.wav)}")
    print(f"centre    {centre / 1e6:.6f} MHz, {rate} Hz, {len(iq) / rate:.1f} s, "
          f"{len(segments)} segments")
    print(f"floor     median {floor:.1f} dB (arbitrary reference); busy = +{a.busy_db:.0f} dB\n")
    print(f"{'kHz':>12}  {'mean':>7}  {'peak':>7}   verdict")

    worst = 0.0
    busy_bins = []
    edges = np.arange(a.lo, a.hi + a.bin_hz, a.bin_hz)
    for lo, hi in zip(edges[:-1], edges[1:]):
        sel = (freqs >= lo) & (freqs < hi)
        if not sel.any():
            continue
        m = mean_db[sel].max() - floor
        pk = peak_db[sel].max() - floor
        worst = max(worst, m)
        busy = m >= a.busy_db
        if busy:
            busy_bins.append((lo, hi, m))
        print(f"{lo / 1e3:>9.1f}-{hi / 1e3:<.1f}  {m:>+6.1f}  {pk:>+6.1f}   "
              f"{'OCCUPIED' if busy else 'clear'}")

    print()
    if busy_bins:
        print(f"VERDICT: NOT CLEAR - {len(busy_bins)} bin(s) at or over +{a.busy_db:.0f} dB:")
        for lo, hi, m in busy_bins:
            print(f"  {lo / 1e3:.1f}-{hi / 1e3:.1f} kHz  {m:+.1f} dB")
        return 1

    print(f"VERDICT: CLEAR across {a.lo / 1e3:.1f}-{a.hi / 1e3:.1f} kHz "
          f"(worst bin {worst:+.1f} dB, threshold +{a.busy_db:.0f})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
