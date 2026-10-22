#!/usr/bin/env python3
"""Fails unless every package bundles the latest .NET 10 runtime patch.

The daemon in each .deb is self-contained and the npm package carries a WebAssembly build of the
runtime, so each carries the runtime of whichever SDK (or SDK workload) built it, and an operating
system update never patches it. A self-hosted runner with a stale SDK once shipped another
packet-net project's armhf packages on runtime 10.0.7 while the rest had 10.0.13, months of
security fixes behind, and nothing noticed. This reads the runtime version out of each package and
compares it with the latest 10.0 runtime Microsoft lists. The NuGet packages are libraries with no
runtime in them, so they are not checked.

Usage: scripts/check-bundled-runtime.py <package.deb|package.tgz>...
"""
import json
import re
import subprocess
import sys
import tarfile
import tempfile
import urllib.request
from pathlib import Path

INDEX = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json"
CHANNEL = "10.0"
PROGRAM = "pdn-soundmodem"
# The single-file daemon names its runtime pack in the embedded deps.json.
NATIVE = re.compile(rb"runtimepack\.Microsoft\.NETCore\.App\.Runtime\.[a-z0-9-]+/(\d+\.\d+\.\d+)")
# The WebAssembly runtime's loader carries the path of the pack it was built from.
WASM = re.compile(rb"Microsoft\.NETCore\.App\.Runtime\.Mono\.browser-wasm/(\d+\.\d+\.\d+)/")


def latest_runtime():
    with urllib.request.urlopen(INDEX, timeout=30) as response:
        index = json.load(response)
    for channel in index["releases-index"]:
        if channel["channel-version"] == CHANNEL:
            return channel["latest-runtime"]
    sys.exit(f"no .NET {CHANNEL} channel in {INDEX}")


def program_bytes(package):
    """(name, bytes, pattern) for each file in the package that names the bundled runtime."""
    if package.suffix == ".tgz":
        with tarfile.open(package) as t:
            return [(m.name, t.extractfile(m).read(), WASM)
                    for m in t.getmembers() if m.isfile() and m.name.endswith("/_framework/dotnet.native.js")]
    with tempfile.TemporaryDirectory() as tmp:
        subprocess.run(["dpkg-deb", "-x", str(package), tmp], check=True)
        return [(str(p.relative_to(tmp)), p.read_bytes(), NATIVE)
                for p in Path(tmp).rglob(PROGRAM) if p.is_file() and not p.is_symlink()]


def main(packages):
    if not packages:
        sys.exit(__doc__)
    want = latest_runtime()
    print(f"latest .NET {CHANNEL} runtime: {want}")
    bad = False
    for package in map(Path, packages):
        programs = program_bytes(package)
        if not programs:
            print(f"::error::{package.name}: no program found to check")
            bad = True
        for name, data, pattern in programs:
            found = sorted({m.decode() for m in pattern.findall(data)})
            print(f"{package.name}: {name} bundles {', '.join(found) or 'nothing recognisable'}")
            if found != [want]:
                print(f"::error::{package.name} bundles .NET runtime {', '.join(found) or 'unknown'}, "
                      f"not {want}. Update the .NET 10 SDK (and wasm-tools workload) on the runner that built it.")
                bad = True
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
