#!/usr/bin/env python3
"""Send AX.25 UI frames to a pdn-soundmodem KISS TCP port.

Traffic for an on-air test, and a way to make a modem transmit without a node attached:

    scripts/kiss-send.py --host 10.45.0.37 --port 8103 --from M0LTE --to TEST \
        --text "freedv datac1 test" --count 5 --interval 20

The port is the modem's dedicated one (frames arrive as nibble 0), or the shared KISS port with
--nibble N to address a sub-channel. Nothing here is specific to a mode: the daemon takes an
AX.25 frame and the configured modem decides how it reaches the air.
"""

import argparse
import socket
import sys
import time

FEND, FESC, TFEND, TFESC = 0xC0, 0xDB, 0xDC, 0xDD


def encode_callsign(call, last):
    """AX.25 address field: callsign-SSID, shifted left one bit, LSB set on the last one."""
    if "-" in call:
        base, _, ssid = call.partition("-")
        ssid = int(ssid)
    else:
        base, ssid = call, 0
    if not 0 <= ssid <= 15:
        raise ValueError(f"SSID out of range in '{call}'")
    base = base.upper()
    if len(base) > 6 or not base.isalnum():
        raise ValueError(f"'{call}' is not a callsign AX.25 can carry")
    out = bytearray((ord(c) << 1) for c in base.ljust(6))
    out.append((0x60 | (ssid << 1)) | (1 if last else 0))
    return bytes(out)


def ui_frame(source, dest, text):
    """A UI frame, control 0x03, PID 0xF0 (no layer 3)."""
    return (
        encode_callsign(dest, last=False)
        + encode_callsign(source, last=True)
        + b"\x03\xf0"
        + text.encode("ascii")
    )


def kiss_wrap(payload, nibble):
    body = bytearray([nibble << 4])
    for b in payload:
        if b == FEND:
            body += bytes([FESC, TFEND])
        elif b == FESC:
            body += bytes([FESC, TFESC])
        else:
            body.append(b)
    return bytes([FEND]) + bytes(body) + bytes([FEND])


def main():
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--host", default="127.0.0.1")
    p.add_argument("--port", type=int, required=True)
    p.add_argument("--from", dest="source", required=True, help="source callsign, e.g. M0LTE")
    p.add_argument("--to", dest="dest", default="TEST", help="destination callsign")
    p.add_argument("--text", default="pdn-soundmodem test", help="UI payload")
    p.add_argument("--count", type=int, default=1, help="how many frames to send")
    p.add_argument("--interval", type=float, default=10.0, help="seconds between frames")
    p.add_argument("--nibble", type=int, default=0, help="KISS sub-channel (shared port only)")
    a = p.parse_args()

    frame = kiss_wrap(ui_frame(a.source, a.dest, a.text), a.nibble)
    with socket.create_connection((a.host, a.port), timeout=10) as s:
        for n in range(a.count):
            # Numbered so a receiving log can show which of them arrived, not just how many.
            body = kiss_wrap(ui_frame(a.source, a.dest, f"{a.text} {n + 1}/{a.count}"), a.nibble)
            s.sendall(body)
            print(f"sent {n + 1}/{a.count}: {a.source}>{a.dest} "
                  f"{len(body)} bytes to {a.host}:{a.port}", flush=True)
            if n + 1 < a.count:
                time.sleep(a.interval)
    return 0


if __name__ == "__main__":
    sys.exit(main())
