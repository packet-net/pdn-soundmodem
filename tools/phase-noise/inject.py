#!/usr/bin/env python3
"""inject.py - post-process an sm-ota IQ capture WAV to test what breaks the MS110D DFE.

The capture is a 16-bit interleaved-stereo WAV that the sm-ota scorer reads as complex
I/Q (left = I, right = Q). This tool reads it, perturbs the complex samples in one of the
candidate ways below, and writes a new WAV the scorer reads identically. No modem code is
touched - this is pure post-processing of a rendered sim capture, so the DFE is exercised
against a controlled perturbation of a signal it is known to decode clean.

Candidates (2026-07-27 phase-B DFE fade-tracking experiment):

  phase       Reference phase-noise: multiply each complex sample by e^{jφ(t)}, φ a random
              walk (Wiener) φ[n] = φ[n-1] + w[n], w ~ N(0, σ²). This is the phase process a
              free-running oscillator / SDR reference impresses on the recovered signal. The
              knob is the Lorentzian FWHM linewidth Δf (Hz), which fixes σ = sqrt(2π Δf / fs)
              and is sample-rate-independent. The physically interpretable output is the RMS
              phase deviation accumulated over one modem frame, sqrt(2π Δf T_frame) rad.

  bandlimited Stationary band-limited phase-noise: φ(t) = white noise low-pass filtered to a
              corner --corner-hz, scaled to a target RMS --phase-rms (rad). Models a bounded
              reference jitter with a chosen bandwidth rather than an unbounded random walk.

  level       Pure amplitude scale by --factor (no phase perturbation) - the level/threshold
              candidate. --factor 0.02 is a 50x (−34 dB) drop.

The Wiener/band-limited phase is applied to the WHOLE capture uniformly (the reference
multiplies signal and quiet alike); rotating the quiet lead-in is harmless because the SNR
estimate is magnitude-only. The rotation cannot add clipping (|e^{jφ}| = 1); level only
scales down. Deterministic given --seed.
"""

import argparse
import sys
import wave

import numpy as np

SYMBOL_RATE = 2400.0
# Modem frame durations (U+K symbols / symbol rate), for reporting per-frame RMS phase.
FRAMES = {
    "wn2 (U48+K48=96)": 96 / SYMBOL_RATE,     # 40 ms
    "wn6/wn13 (U256+K32=288)": 288 / SYMBOL_RATE,  # 120 ms
}


def read_iq(path):
    with wave.open(path, "rb") as w:
        if w.getnchannels() != 2 or w.getsampwidth() != 2:
            raise SystemExit(f"{path}: expected 16-bit stereo, got "
                             f"{w.getnchannels()}ch/{w.getsampwidth()*8}bit")
        fs = w.getframerate()
        raw = w.readframes(w.getnframes())
    s = np.frombuffer(raw, dtype="<i2").astype(np.float64).reshape(-1, 2)
    iq = (s[:, 0] + 1j * s[:, 1]) / 32767.0
    return iq, fs


def write_iq(path, iq, fs):
    re = np.clip(np.round(iq.real * 32767.0), -32768, 32767).astype("<i2")
    im = np.clip(np.round(iq.imag * 32767.0), -32768, 32767).astype("<i2")
    inter = np.empty(iq.size * 2, dtype="<i2")
    inter[0::2] = re
    inter[1::2] = im
    with wave.open(path, "wb") as w:
        w.setnchannels(2)
        w.setsampwidth(2)
        w.setframerate(fs)
        w.writeframes(inter.tobytes())


def wiener_phase(n, linewidth_hz, fs, rng):
    # Lorentzian FWHM Δf ⇒ per-sample increment variance σ² = 2π Δf / fs (field autocorr
    # exp(−π Δf |τ|) matches E[e^{jΔφ}] = exp(−k σ²/2) at τ = k/fs).
    sigma = np.sqrt(2.0 * np.pi * linewidth_hz / fs)
    incr = rng.normal(0.0, sigma, size=n)
    return np.cumsum(incr), sigma


def bandlimited_phase(n, phase_rms, corner_hz, fs, rng):
    white = rng.normal(0.0, 1.0, size=n)
    # One-pole low-pass at corner_hz.
    a = np.exp(-2.0 * np.pi * corner_hz / fs)
    y = np.empty(n)
    acc = 0.0
    for i in range(n):
        acc = a * acc + (1.0 - a) * white[i]
        y[i] = acc
    rms = np.sqrt(np.mean(y * y))
    if rms > 0:
        y = y * (phase_rms / rms)
    return y


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--in", dest="inp", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--mode", choices=["phase", "bandlimited", "level"], required=True)
    ap.add_argument("--linewidth-hz", type=float, default=0.0,
                    help="phase mode: Lorentzian FWHM linewidth in Hz")
    ap.add_argument("--phase-rms", type=float, default=0.0,
                    help="bandlimited mode: target RMS phase in radians")
    ap.add_argument("--corner-hz", type=float, default=20.0,
                    help="bandlimited mode: low-pass corner in Hz")
    ap.add_argument("--factor", type=float, default=1.0,
                    help="level mode: amplitude scale (0.02 = 50x/-34 dB down)")
    ap.add_argument("--seed", type=int, default=1)
    args = ap.parse_args()

    iq, fs = read_iq(args.inp)
    rng = np.random.default_rng(args.seed)
    n = iq.size

    if args.mode == "phase":
        phi, sigma = wiener_phase(n, args.linewidth_hz, fs, rng)
        iq = iq * np.exp(1j * phi)
        print(f"phase(Wiener): Δf={args.linewidth_hz:g} Hz  σ/sample={sigma:.3e} rad  "
              f"fs={fs}", file=sys.stderr)
        for name, tf in FRAMES.items():
            per_frame = np.sqrt(2.0 * np.pi * args.linewidth_hz * tf)
            print(f"    per-frame RMS phase, {name}: {per_frame:.4f} rad "
                  f"({np.degrees(per_frame):.2f} deg)", file=sys.stderr)
    elif args.mode == "bandlimited":
        phi = bandlimited_phase(n, args.phase_rms, args.corner_hz, fs, rng)
        iq = iq * np.exp(1j * phi)
        print(f"phase(bandlimited): RMS={args.phase_rms:g} rad corner={args.corner_hz:g} Hz",
              file=sys.stderr)
    else:
        iq = iq * args.factor
        db = 20.0 * np.log10(args.factor) if args.factor > 0 else float("-inf")
        print(f"level: factor={args.factor:g} ({db:.1f} dB)", file=sys.stderr)

    write_iq(args.out, iq, fs)
    print(f"wrote {args.out}: {n} frames @ {fs} Hz", file=sys.stderr)


if __name__ == "__main__":
    main()
