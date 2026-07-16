#!/usr/bin/env python3
"""Deterministic TXFRAME job list + manifest for the ardopcf oracle fixtures.

Emits `name|txframe-args` lines on stdout (consumed by generate.sh) and writes
txframe-manifest.txt beside this script: one line per fixture of the form
`<name> <frametype-hex> <sessionid-hex> <payload-hex|->` that the C# oracle
tests parse for their expected values. Payloads are a fixed xorshift32 stream
(seed 0xA5A5A5A5) so regeneration is byte-stable.
"""
import os

state = 0xA5A5A5A5


def prng() -> int:
    global state
    state ^= (state << 13) & 0xFFFFFFFF
    state ^= state >> 17
    state ^= (state << 5) & 0xFFFFFFFF
    return state & 0xFF


def payload(n: int) -> str:
    return "".join(f"{prng():02X}" for _ in range(n))


jobs: list[tuple[str, str, str, str, str]] = []  # name, args, type, session, payload

# Short control frames (session 0xFF).
jobs.append(("DataNAK-q60", "DataNAK 60 0xFF", "0B", "FF", "-"))
jobs.append(("BREAK", "BREAK 0xFF", "23", "FF", "-"))
jobs.append(("IDLE", "IDLE 0xFF", "24", "FF", "-"))
jobs.append(("DISC", "DISC 0xFF", "29", "FF", "-"))
jobs.append(("END", "END 0xFF", "2C", "FF", "-"))
jobs.append(("ConRejBusy", "ConRejBusy 0xFF", "2D", "FF", "-"))
jobs.append(("ConRejBW", "ConRejBW 0xFF", "2E", "FF", "-"))
jobs.append(("DataACK-q80", "DataACK 80 0xFF", "F5", "FF", "-"))

# ConAck with leader timing; PingAck with S:N + quality.
jobs.append(("ConAck200-t320", "ConAck200 320 0xFF", "39", "FF", "timing=320"))
jobs.append(("ConAck500-t240", "ConAck500 240 0xFF", "3A", "FF", "timing=240"))
jobs.append(("ConAck1000-t500", "ConAck1000 500 0xFF", "3B", "FF", "timing=500"))
jobs.append(("ConAck2000-t2000", "ConAck2000 2000 0xFF", "3C", "FF", "timing=2000"))
jobs.append(("PingAck-sn12-q80", "PingAck 12 80", "3D", "FF", "sn=12,quality=80"))

# Station frames.
jobs.append(("IDFrame", "IDFrame M7TFF-3 IO81VK", "30", "FF", "caller=M7TFF-3,grid=IO81VK"))
for bw, code in [("200M", "31"), ("500M", "32"), ("1000M", "33"), ("2000M", "34"),
                 ("200F", "35"), ("500F", "36"), ("1000F", "37"), ("2000F", "38")]:
    jobs.append((f"ConReq{bw}", f"ConReq{bw} GB7RDG-15 M7TFF", code, "FF",
                 "caller=M7TFF,target=GB7RDG-15"))
jobs.append(("Ping", "Ping GB7RDG M7TFF", "3E", "FF", "caller=M7TFF,target=GB7RDG"))

# 4FSK data frames: full-capacity even types, partial-fill odd types.
for name, code, full, partial in [
    ("4FSK.200.50S", 0x48, 16, 9),
    ("4FSK.500.100", 0x4A, 64, 40),
    ("4FSK.500.100S", 0x4C, 32, 20),
    ("4FSK.2000.600", 0x7A, 600, 321),
    ("4FSK.2000.600S", 0x7C, 200, 77),
]:
    data = payload(full)
    jobs.append((f"{name}.E", f"{name}.E {data} 0xFF", f"{code:02X}", "FF", data))
    data = payload(partial)
    jobs.append((f"{name}.O", f"{name}.O {data} 0xFF", f"{code + 1:02X}", "FF", data))

# PSK/16QAM data frames (Phase C): full-capacity even types, partial-fill odd types.
# Appended after the 4FSK block so the PRNG stream (and the Phase A fixtures it
# feeds) stays byte-stable.
for name, code, full, partial in [
    ("4PSK.200.100", 0x40, 64, 40),
    ("4PSK.200.100S", 0x42, 16, 9),
    ("8PSK.200.100", 0x44, 108, 60),
    ("16QAM.200.100", 0x46, 128, 77),
    ("4PSK.500.100", 0x50, 128, 80),
    ("8PSK.500.100", 0x52, 216, 130),
    ("16QAM.500.100", 0x54, 256, 150),
    ("4PSK.1000.100", 0x60, 256, 129),
    ("8PSK.1000.100", 0x62, 432, 216),
    ("16QAM.1000.100", 0x64, 512, 300),
    ("4PSK.2000.100", 0x70, 512, 257),
    ("8PSK.2000.100", 0x72, 864, 500),
    ("16QAM.2000.100", 0x74, 1024, 600),
]:
    data = payload(full)
    jobs.append((f"{name}.E", f"{name}.E {data} 0xFF", f"{code:02X}", "FF", data))
    data = payload(partial)
    jobs.append((f"{name}.O", f"{name}.O {data} 0xFF", f"{code + 1:02X}", "FF", data))

here = os.path.dirname(os.path.abspath(__file__))
with open(os.path.join(here, "txframe-manifest.txt"), "w") as manifest:
    manifest.write("# name frametype sessionid payload-hex-or-dash extra\n")
    for name, args, ftype, session, extra_or_payload in jobs:
        print(f"{name}|{args}")
        manifest.write(f"txframe_{name}.wav {ftype} {session} {extra_or_payload}\n")
