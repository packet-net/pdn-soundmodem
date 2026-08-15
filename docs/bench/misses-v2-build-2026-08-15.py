#!/usr/bin/env python3
"""Build the misses-v2 corpus from a full-window sm-ota replay of the 40 m capture.

Inputs (all produced by `sm-ota replay` over the whole raw archive, plus the live log):
  - bursts CSV   (--bursts): one row per DCD burst, the receiver's own verdict
  - frames CSV   (--csv): one row per replayed decode, full payload hex
  - frames.sqlite: what the live station logged (two daemon generations, see below)
  - raw/         : the 15-minute 12 kHz chunks, ground truth audio

Outputs, under --out:
  - misses-v2/<utc>-<class>.wav  : per-miss audio cuts (burst +- margin)
  - misses-v2/manifest.json      : one entry per cut - times, dcd, rs/crc failure deltas,
                                   expected payload hex where retry evidence attaches one,
                                   and how that attachment was made (weaker ground truth
                                   than the GB7RDG NinoTNC referee; stated per case)
  - a generation-controlled log-vs-replay diff printed to stdout

Generation control (the roadmap's warning): the deployed daemon that wrote the frame log
changed on 2026-08-07 ~16:45Z (frame id 1328): before that, 0.25.1 pre-erasure
(no trailer corroboration, no erasure decoding); after, the erasure receiver. Rows are
tagged gen1/gen2 by id, and every diff figure is reported per generation - a delta
against gen1 mixes receiver generations and must not be read as an instrument floor.
"""

import argparse
import csv
import json
import sqlite3
import wave
from bisect import bisect_left
from datetime import datetime, timedelta, timezone
from pathlib import Path

GEN2_FIRST_ID = 1328  # first row written by the erasure-generation daemon (2026-08-07T16:45Z)
BAUD = 300
MATCH_WINDOW_S = 45  # same as sm-ota replay's frame-log diff
SIBLING_WINDOW_S = 600  # retry-sibling search span, the first-day analysis's ten minutes
CUT_MARGIN_S = 2.0
MIN_FULL_BURST_S = 1.0  # shorter than this cannot hold a whole frame (43/62 opening-evening)


def parse_utc(s):
    return datetime.fromisoformat(s.replace("Z", "+00:00"))


def chunk_utc(name):
    return datetime.strptime(Path(name).stem[4:], "%Y%m%dT%H%M%SZ").replace(tzinfo=timezone.utc)


def wire_seconds(payload_len):
    """Seconds of wire time for an IL2P+CRC frame with this AX.25 payload length,
    sync + header + blocks + trailer (no preamble - that is station-dependent)."""
    blocks = max(1, (payload_len + 238) // 239)
    wire_bytes = 15 + payload_len + 16 * blocks + 4
    return (24 + 8 * wire_bytes) / BAUD


def load_log(path, t_from, t_to, mode_prefix="bpsk300"):
    rows = []
    con = sqlite3.connect(path)
    for rid, heard_at, mode, payload in con.execute(
        "SELECT id, heard_at, mode, payload FROM frames "
        "WHERE direction != 'tx' AND heard_at >= ? AND heard_at <= ? ORDER BY id",
        (t_from, t_to),
    ):
        if not mode.startswith(mode_prefix):
            continue
        rows.append(
            {
                "id": rid,
                "utc": parse_utc(heard_at),
                "gen": 2 if rid >= GEN2_FIRST_ID else 1,
                "payload": bytes(payload).hex().upper(),
                "matched": False,
            }
        )
    con.close()
    return rows


def load_replay(path):
    frames = []
    with open(path) as f:
        for row in csv.DictReader(f):
            frames.append(
                {
                    "utc": parse_utc(row["utc"]),
                    "delivered": row["delivered"] == "1",
                    "payload": row["payload_hex"].upper(),
                    "matched": False,
                }
            )
    return frames


def diff(log_rows, replay_frames):
    """Nearest-first exact-payload matching inside the window - the replay command's own
    rules, reproduced here so the result carries generation tags."""
    by_payload = {}
    for r in log_rows:
        by_payload.setdefault(r["payload"], []).append(r)
    for f in sorted(replay_frames, key=lambda f: f["utc"]):
        best, best_skew = None, timedelta(seconds=MATCH_WINDOW_S)
        for r in by_payload.get(f["payload"], []):
            if r["matched"]:
                continue
            skew = abs(r["utc"] - f["utc"])
            if skew <= best_skew:
                best, best_skew = r, skew
        if best is not None:
            best["matched"] = True
            f["matched"] = True


def attach_expected(burst, decoded_frames_sorted, times):
    """Retry-sibling attachment: a decoded frame (replay or log) within ten minutes whose
    wire duration is consistent with the burst's DCD hold. AX.25 retries are
    byte-identical, so a nearby identical-duration decode is the best available guess at
    what the damaged burst carried - context evidence, not a referee."""
    lo = bisect_left(times, burst["utc_start"] - timedelta(seconds=SIBLING_WINDOW_S))
    hi = bisect_left(times, burst["utc_start"] + timedelta(seconds=SIBLING_WINDOW_S))
    candidates = {}
    for f in decoded_frames_sorted[lo:hi]:
        expect = wire_seconds(len(f["payload"]) // 2)
        # DCD opens during the preamble and holds ~24 symbol times past the end, so the
        # burst should run a little longer than the frame's wire time; allow generous slop
        # for TXDELAY differences between stations.
        if -0.3 <= burst["dcd_seconds"] - expect <= 1.5:
            candidates.setdefault(f["payload"], []).append(f)
    if len(candidates) == 1:
        payload, sightings = next(iter(candidates.items()))
        nearest = min(abs((s["utc"] - burst["utc_start"]).total_seconds()) for s in sightings)
        return payload, len(sightings), nearest
    return None, len(candidates), None


def cut_audio(raw_dir, chunks, start, end, out_path):
    """Extracts [start, end] from the chunk sequence into a mono 16-bit WAV."""
    frames = bytearray()
    rate = None
    for name in chunks:
        c_start = chunk_utc(name)
        with wave.open(str(Path(raw_dir) / name), "rb") as w:
            rate = w.getframerate()
            c_end = c_start + timedelta(seconds=w.getnframes() / rate)
            if c_end < start or c_start > end:
                continue
            a = max(0, int((start - c_start).total_seconds() * rate))
            b = min(w.getnframes(), int((end - c_start).total_seconds() * rate))
            if b <= a:
                continue
            w.setpos(a)
            frames.extend(w.readframes(b - a))
    if not frames:
        return False
    with wave.open(str(out_path), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(bytes(frames))
    return True


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--bursts", required=True)
    ap.add_argument("--frames", required=True)
    ap.add_argument("--framelog", required=True)
    ap.add_argument("--raw", required=True)
    ap.add_argument("--out", required=True)
    args = ap.parse_args()

    replay = load_replay(args.frames)
    t_from = (min(f["utc"] for f in replay) - timedelta(seconds=MATCH_WINDOW_S)).isoformat()
    t_to = (max(f["utc"] for f in replay) + timedelta(seconds=MATCH_WINDOW_S)).isoformat()
    log_rows = load_log(args.framelog, t_from, t_to)
    diff(log_rows, replay)

    for gen in (1, 2):
        rows = [r for r in log_rows if r["gen"] == gen]
        matched = sum(r["matched"] for r in rows)
        print(f"gen{gen}: {len(rows)} logged, {matched} matched, {len(rows) - matched} log-only")
    print(f"replay: {len(replay)} decoded, {sum(f['matched'] for f in replay)} matched, "
          f"{sum(not f['matched'] for f in replay)} replay-only")

    # Decoded pool for sibling attachment: replay decodes + log rows (either generation -
    # a payload is a payload wherever it was read).
    pool = [{"utc": f["utc"], "payload": f["payload"]} for f in replay]
    seen = {(f["utc"], f["payload"]) for f in pool}
    for r in log_rows:
        if (r["utc"], r["payload"]) not in seen:
            pool.append({"utc": r["utc"], "payload": r["payload"]})
    pool.sort(key=lambda f: f["utc"])
    times = [f["utc"] for f in pool]

    bursts = []
    with open(args.bursts) as f:
        for row in csv.DictReader(f):
            bursts.append(
                {
                    "utc_start": parse_utc(row["utc_start"]),
                    "utc_end": parse_utc(row["utc_end"]),
                    "dcd_seconds": float(row["dcd_seconds"]),
                    "decoded": row["decoded"] == "1",
                    "offset_hz": row["offset_hz"],
                    "rs_failures": int(row["rs_failures"]),
                    "crc_failures": int(row["crc_failures"]),
                }
            )

    misses = [b for b in bursts if not b["decoded"]]
    full = [b for b in misses if b["dcd_seconds"] >= MIN_FULL_BURST_S]
    print(f"bursts: {len(bursts)} dcd, {len(misses)} undecoded, "
          f"{len(full)} full-length (>= {MIN_FULL_BURST_S} s), "
          f"{len(misses) - len(full)} fragments (excluded)")

    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    chunks = sorted(p.name for p in Path(args.raw).glob("raw-*.wav"))
    manifest = []
    for b in full:
        expected, ambiguity, nearest = attach_expected(b, pool, times)
        cls = "expected-attached" if expected else (
            "ambiguous-siblings" if ambiguity > 1 else "no-sibling")
        stamp = b["utc_start"].strftime("%Y%m%dT%H%M%SZ")
        wav = f"miss-{stamp}.wav"
        if not cut_audio(
            args.raw, chunks,
            b["utc_start"] - timedelta(seconds=CUT_MARGIN_S),
            b["utc_end"] + timedelta(seconds=CUT_MARGIN_S),
            out / wav,
        ):
            continue
        manifest.append(
            {
                "wav": wav,
                "utc_start": b["utc_start"].isoformat(),
                "utc_end": b["utc_end"].isoformat(),
                "dcd_seconds": b["dcd_seconds"],
                "offset_hz": b["offset_hz"],
                "rs_failures": b["rs_failures"],
                "crc_failures": b["crc_failures"],
                "class": cls,
                "expected_hex": expected,
                "sibling_sightings": ambiguity if expected else None,
                "sibling_nearest_s": nearest,
            }
        )

    (out / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    by_class = {}
    for m in manifest:
        by_class[m["class"]] = by_class.get(m["class"], 0) + 1
    print(f"misses-v2: {len(manifest)} cuts -> {out}")
    for k, v in sorted(by_class.items()):
        print(f"  {k}: {v}")


if __name__ == "__main__":
    main()
