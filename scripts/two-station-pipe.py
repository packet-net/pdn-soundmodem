"""Two pdn-soundmodem daemons on a virtual audio pipe: does a frame cross the air?

Station A is handed an AX.25 frame over KISS; station B has to hear it out of the audio alone.
Nothing is shared between the two processes but a pair of FIFOs, so a frame arriving at B has been
modulated, written as audio, read back by another process and demodulated. That is a different and
stronger claim than a modem round-tripping its own buffer in one process: the transmit path and the
receive path have to agree about something for it to work at all.

    scripts/two-station-pipe.py                                    # afsk1200, the baseline
    scripts/two-station-pipe.py ofdm-fm:nb /opt/pdn/M0LTE.OfdmFm.dll 48000

Exits 0 if the frame arrived byte-identical, 1 with both stations' logs if it did not.
"""
import json
import os
import socket
import subprocess
import sys
import tempfile
import time

S = os.environ.get("TWO_STATION_DIR", tempfile.mkdtemp(prefix="two-station-"))
os.makedirs(S, exist_ok=True)
DAEMON = os.environ.get(
    "PDN_SOUNDMODEM",
    os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                 "src/Packet.SoundModem.Daemon/bin/Release/net10.0/pdn-soundmodem"))
MODE = sys.argv[1] if len(sys.argv) > 1 else "afsk1200"
PLUGIN = sys.argv[2] if len(sys.argv) > 2 else None
RATE = sys.argv[3] if len(sys.argv) > 3 else "48000"

AB = f"{S}/air-a-to-b"
BA = f"{S}/air-b-to-a"
for f in (AB, BA):
    if os.path.exists(f):
        os.unlink(f)

FEND, FESC, TFEND, TFESC = 0xC0, 0xDB, 0xDC, 0xDD


def kiss_wrap(payload):
    out = bytearray([FEND, 0x00])
    for b in payload:
        if b == FEND:
            out += bytes([FESC, TFEND])
        elif b == FESC:
            out += bytes([FESC, TFESC])
        else:
            out.append(b)
    out.append(FEND)
    return bytes(out)


def kiss_frames(buf):
    frames, cur, in_frame, esc = [], bytearray(), False, False
    for b in buf:
        if b == FEND:
            if in_frame and cur:
                frames.append(bytes(cur))
            cur, in_frame, esc = bytearray(), True, False
            continue
        if not in_frame:
            continue
        if esc:
            cur.append(FEND if b == TFEND else FESC if b == TFESC else b)
            esc = False
        elif b == FESC:
            esc = True
        else:
            cur.append(b)
    return frames


def config(path, port, in_pipe, out_pipe):
    cfg = {
        "device": f"pipe:{in_pipe},{out_pipe},{RATE}",
        "kissPort": port,
        "modems": [{"subChannel": 0, "mode": MODE}],
    }
    if PLUGIN:
        cfg["modemPlugins"] = [{"path": PLUGIN}]
    json.dump(cfg, open(path, "w"), indent=2)
    return path


a_cfg = config(f"{S}/station-a.json", 18201, AB, BA)   # A reads a->b, writes b->a
b_cfg = config(f"{S}/station-b.json", 18202, BA, AB)   # B reads b->a, writes a->b

a_log = open(f"{S}/station-a.log", "w")
b_log = open(f"{S}/station-b.log", "w")
a = subprocess.Popen([DAEMON, "--config", a_cfg], stdout=a_log, stderr=subprocess.STDOUT)
b = subprocess.Popen([DAEMON, "--config", b_cfg], stdout=b_log, stderr=subprocess.STDOUT)

try:
    time.sleep(4)
    for name, proc, log in (("A", a, f"{S}/station-a.log"), ("B", b, f"{S}/station-b.log")):
        if proc.poll() is not None:
            print(f"station {name} exited {proc.returncode}:")
            print(open(log).read())
            sys.exit(1)

    rx = socket.create_connection(("127.0.0.1", 18202), timeout=5)
    tx = socket.create_connection(("127.0.0.1", 18201), timeout=5)
    time.sleep(0.5)

    # A plain AX.25 UI frame: M0LTE-1 > TEST via nobody, no layer 3.
    frame = bytes([
        0xA8, 0x8A, 0xA6, 0xA8, 0x40, 0x40, 0x60,          # TEST
        0x9A, 0x60, 0x98, 0xA8, 0x8A, 0x40, 0x63,          # M0LTE-1
    ]) + bytes([0x03, 0xF0]) + b"two stations, one pipe"

    tx.sendall(kiss_wrap(frame))
    print("sent from station A, listening on station B ...")

    rx.settimeout(1.0)
    got = bytearray()
    deadline = time.time() + 25
    heard = []
    while time.time() < deadline and not heard:
        try:
            chunk = rx.recv(4096)
            if not chunk:
                break
            got += chunk
            heard = [f for f in kiss_frames(got) if f[1:] == frame]
        except socket.timeout:
            pass

    if heard:
        print(f"HEARD on station B: {len(heard[0]) - 1} bytes, identical to what A sent")
        print(f"   payload: {heard[0][17:].decode('ascii', 'replace')}")
        sys.exit(0)

    print(f"NOT HEARD. bytes seen on B's KISS port: {len(got)}")
    for name, log in (("A", f"{S}/station-a.log"), ("B", f"{S}/station-b.log")):
        print(f"--- station {name} ---")
        print(open(log).read()[-1500:])
    sys.exit(1)
finally:
    for p in (a, b):
        p.terminate()
    for p in (a, b):
        try:
            p.wait(timeout=5)
        except subprocess.TimeoutExpired:
            p.kill()
    a_log.close()
    b_log.close()
