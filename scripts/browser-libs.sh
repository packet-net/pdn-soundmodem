#!/usr/bin/env bash
# Makes the Playwright headless shell in $PLAYWRIGHT_BROWSERS_PATH runnable on a machine that is
# missing some of the shared libraries it needs, without root and without changing the machine.
#
#   scripts/browser-libs.sh <work dir>
#
# Prints nothing and exits 0 when the browser already has every library. Otherwise it downloads the
# missing distribution packages with apt, using a package index of its own under <work dir>,
# unpacks them under <work dir>/root, checks that the browser now finds everything, and prints the
# library path to hand the browser (ConfigPageBrowserTests passes $PDN_BROWSER_LIBRARY_PATH to it
# as LD_LIBRARY_PATH, so nothing else in the job sees these libraries). Exits non-zero, naming what
# is still missing, when it cannot.
#
# Why: CI runs on self-hosted runners that are not ours to install packages on, and some of them
# are containers without a desktop stack (2026-11-06: proxmox0's runner had no libnss3, libgbm1,
# libasound2 and others).
set -euo pipefail

work="$1"
chrome="$(find "${PLAYWRIGHT_BROWSERS_PATH:?}" -type f -name chrome-headless-shell -perm -u+x | head -n 1)"
if [ -z "$chrome" ]; then
  echo "browser-libs: no chrome-headless-shell under $PLAYWRIGHT_BROWSERS_PATH; install it first" >&2
  exit 1
fi

missing() { LD_LIBRARY_PATH="${1:-}" ldd "$chrome" | awk '/not found/ { print $1 }' | sort -u; }

libpath() {
  find "$work/root" -name '*.so*' -printf '%h\n' 2> /dev/null | sort -u | paste -sd: -
}

if [ -z "$(missing)" ]; then
  exit 0
fi
echo "browser-libs: the browser is missing $(missing | tr '\n' ' ')" >&2

# Libraries fetched by an earlier job on this runner and restored from CI's cache: nothing to do.
if [ -d "$work/root" ] && [ -z "$(missing "$(libpath)")" ]; then
  echo "browser-libs: all found in $work/root, from an earlier fetch" >&2
  libpath
  exit 0
fi

# A package index of our own: apt-get update and download need no root when every directory they
# write to is ours and nothing is locked.
apt="$work/apt"
mkdir -p "$apt/lists/partial" "$apt/cache/archives/partial" "$apt/debs" "$work/root"
opts=(
  -o "Dir::State::Lists=$apt/lists"
  -o "Dir::Cache=$apt/cache"
  -o "Debug::NoLocking=1"
  -o "APT::Sandbox::User=$(id -un)"
)
apt-get "${opts[@]}" -qq update

# A real package that apt can download, by exactly that name: not a virtual name such as
# libasound2, which on a t64 distribution only points at libasound2t64, and not a name apt would
# read as an action ("libnss3-" means "remove libnss3" to apt).
real() {
  local out
  out="$(apt-cache "${opts[@]}" policy "$1" 2> /dev/null)"
  [ "$(echo "$out" | head -n 1)" = "$1:" ] && echo "$out" | grep -q 'Candidate: [^(]'
}

installed() { dpkg-query -W -f='${Status}' "$1" 2> /dev/null | grep -q 'install ok installed'; }

# Which package holds a missing library: the one named after it, as Debian and Ubuntu name them
# (libnss3.so -> libnss3, libgbm.so.1 -> libgbm1, libatk-1.0.so.0 -> libatk1.0-0), the t64 name
# that Debian 13 and Ubuntu 24.04 onwards use tried first. A library this cannot name (libatspi,
# libnssutil3) is found anyway: it comes in with another package, or as one's dependency.
package_for() {
  local so="$1" base ver stem candidate
  base="${so%%.so*}"
  ver="${so#*.so}"; ver="${ver#.}"; ver="${ver%%.*}"
  stem="$(echo "${base//_/-}" | tr 'A-Z' 'a-z' | sed 's/-\([0-9]\)/\1/g')"
  local candidates=("${stem}t64" "$stem")
  if [ -n "$ver" ]; then
    candidates=("$stem-${ver}t64" "$stem${ver}t64" "$stem-$ver" "$stem$ver" "${candidates[@]}")
  fi
  for candidate in "${candidates[@]}"; do
    if real "$candidate"; then
      echo "$candidate"
      return 0
    fi
  done
  return 1
}

# The packages these need in turn, as far down as apt knows, less what is installed already.
with_depends() {
  apt-cache "${opts[@]}" depends --recurse --no-recommends --no-suggests --no-conflicts \
      --no-breaks --no-replaces --no-enhances "$@" 2> /dev/null \
    | awk '/^[a-z0-9]/ { print $1 }' | sort -u \
    | while read -r pkg; do
        if ! installed "$pkg" && real "$pkg"; then echo "$pkg"; fi
      done
}

# Each pass fetches the packages for what is still missing and unpacks them; the libraries those
# bring can need more, which the next pass finds. Four passes is far more than any real chain.
declare -A fetched=()
for pass in 1 2 3 4; do
  still="$(missing "$(libpath)")"
  [ -z "$still" ] && break
  named=()
  for so in $still; do
    if pkg="$(package_for "$so")"; then
      named+=("$pkg")
    fi
  done
  [ "${#named[@]}" -eq 0 ] && break
  wanted=()
  for pkg in $(with_depends "${named[@]}"); do
    if [ -z "${fetched[$pkg]:-}" ]; then
      wanted+=("$pkg")
    fi
  done
  [ "${#wanted[@]}" -eq 0 ] && break
  echo "browser-libs: pass $pass fetching ${wanted[*]}" >&2
  (cd "$apt/debs" && apt-get "${opts[@]}" -qq download "${wanted[@]}")
  for pkg in "${wanted[@]}"; do
    fetched[$pkg]=1
  done
  for deb in "$apt/debs"/*.deb; do
    dpkg-deb -x "$deb" "$work/root"
  done
done

still="$(missing "$(libpath)")"
if [ -n "$still" ]; then
  echo "browser-libs: still missing after fetching: $(echo "$still" | tr '\n' ' ')" >&2
  exit 1
fi

libpath
