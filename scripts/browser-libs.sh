#!/usr/bin/env bash
# Makes the Playwright headless shell in $PLAYWRIGHT_BROWSERS_PATH runnable on a machine that is
# missing some of what it needs, without root and without changing the machine.
#
#   scripts/browser-libs.sh <work dir>
#
# Prints environment lines for CI to put in $GITHUB_ENV, and nothing when the machine already has
# everything:
#
#   PDN_BROWSER_LIBRARY_PATH=...    shared libraries it lacked, fetched under <work dir>/root
#   PDN_BROWSER_FONTCONFIG_FILE=... a font configuration naming fonts fetched there, when the
#                                   machine has no fonts at all (Chromium dies laying out text
#                                   with none)
#
# ConfigPageBrowserTests hands these to the browser alone, as LD_LIBRARY_PATH and FONTCONFIG_FILE,
# so nothing else in the job sees them. Packages come from the distribution's own repositories,
# through a package index this script keeps under <work dir>. Exits non-zero, saying what is
# still missing, when it cannot make the browser whole.
#
# Why: CI runs on self-hosted runners that are not ours to install packages on, and some of them
# are containers without a desktop stack. On 2026-11-06 proxmox0's runner had no libnss3, libgbm1,
# libasound2 and others, and no fonts.
set -euo pipefail

work="$1"
root="$work/root"
chrome="$(find "${PLAYWRIGHT_BROWSERS_PATH:?}" -type f -name chrome-headless-shell -perm -u+x | head -n 1)"
if [ -z "$chrome" ]; then
  echo "browser-libs: no chrome-headless-shell under $PLAYWRIGHT_BROWSERS_PATH; install it first" >&2
  exit 1
fi
mkdir -p "$root"

missing() { LD_LIBRARY_PATH="${1:-}" ldd "$chrome" | awk '/not found/ { print $1 }' | sort -u; }

libpath() {
  find "$root" -name '*.so*' -printf '%h\n' 2> /dev/null | sort -u | paste -sd: -
}

# ---------------------------------------------------------------- apt, without root

apt="$work/apt"
opts=(
  -o "Dir::State::Lists=$apt/lists"
  -o "Dir::Cache=$apt/cache"
  -o "Debug::NoLocking=1"
  -o "APT::Sandbox::User=$(id -un)"
)
apt_ready=""

# A package index of our own: apt-get update and download need no root when every directory they
# write to is ours and nothing is locked. Built only when something has to be fetched.
ensure_apt() {
  [ -n "$apt_ready" ] && return 0
  mkdir -p "$apt/lists/partial" "$apt/cache/archives/partial" "$apt/debs"
  # A source that fails to update (a third-party repository that is down, a mirror hiccup) is not
  # fatal: the packages wanted may still be listed, and the checks at the end decide.
  apt-get "${opts[@]}" -qq update || echo "browser-libs: apt-get update reported errors; carrying on" >&2
  apt_ready=1
}

# A real package that apt can download, by exactly that name: not a virtual name such as
# libasound2, which on a t64 distribution only points at libasound2t64, and not a name apt would
# read as an action ("libnss3-" means "remove libnss3" to apt).
real() {
  local out
  out="$(apt-cache "${opts[@]}" policy "$1" 2> /dev/null)"
  [ "$(echo "$out" | head -n 1)" = "$1:" ] && echo "$out" | grep -q 'Candidate: [^(]'
}

installed() { dpkg-query -W -f='${Status}' "$1" 2> /dev/null | grep -q 'install ok installed'; }

# The packages these need in turn, as far down as apt knows, less what is installed already.
with_depends() {
  apt-cache "${opts[@]}" depends --recurse --no-recommends --no-suggests --no-conflicts \
      --no-breaks --no-replaces --no-enhances "$@" 2> /dev/null \
    | awk '/^[a-z0-9]/ { print $1 }' | sort -u \
    | while read -r pkg; do
        # < /dev/null: apt-cache and dpkg-query would otherwise read this loop's input.
        if ! installed "$pkg" < /dev/null && real "$pkg" < /dev/null; then echo "$pkg"; fi
      done
}

declare -A fetched=()

# Downloads these packages and unpacks them under $root; false when there was nothing new to get.
fetch() {
  local wanted=() pkg deb
  for pkg in "$@"; do
    if [ -z "${fetched[$pkg]:-}" ]; then
      wanted+=("$pkg")
    fi
  done
  [ "${#wanted[@]}" -eq 0 ] && return 1
  echo "browser-libs: fetching ${wanted[*]}" >&2
  # Explicit, because callers use this in an || and bash then ignores set -e inside it.
  (cd "$apt/debs" && apt-get "${opts[@]}" -qq download "${wanted[@]}") || return 1
  for pkg in "${wanted[@]}"; do
    fetched[$pkg]=1
  done
  for deb in "$apt/debs"/*.deb; do
    [ -e "$deb" ] && dpkg-deb -x "$deb" "$root"
  done
}

# ---------------------------------------------------------------- shared libraries

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

if [ -n "$(missing)" ]; then
  echo "browser-libs: the browser is missing $(missing | tr '\n' ' ')" >&2
  # Libraries an earlier job on this runner fetched, restored from CI's cache, may be enough.
  if [ -n "$(missing "$(libpath)")" ]; then
    ensure_apt
    # Each pass fetches the packages for what is still missing; the libraries those bring can
    # need more, which the next pass finds. Four passes is far more than any real chain.
    for _ in 1 2 3 4; do
      still="$(missing "$(libpath)")"
      [ -z "$still" ] && break
      named=()
      for so in $still; do
        if pkg="$(package_for "$so")"; then
          named+=("$pkg")
        fi
      done
      [ "${#named[@]}" -eq 0 ] && break
      # shellcheck disable=SC2046
      fetch $(with_depends "${named[@]}") || break
    done
  fi

  still="$(missing "$(libpath)")"
  if [ -n "$still" ]; then
    echo "browser-libs: still missing after fetching: $(echo "$still" | tr '\n' ' ')" >&2
    exit 1
  fi

  echo "PDN_BROWSER_LIBRARY_PATH=$(libpath)"
fi

# ---------------------------------------------------------------- fonts

# Any font file where fontconfig's default configuration looks. None at all, and Chromium dies the
# moment a page lays out text, with nothing in its log.
has_fonts() {
  local dir
  for dir in /usr/share/fonts /usr/local/share/fonts "$HOME/.fonts" "$HOME/.local/share/fonts"; do
    # One directory at a time: find exits 1 for a directory that is not there, and under
    # pipefail that would hide a font found in the next one.
    [ -d "$dir" ] || continue
    if find "$dir" -type f \( -name '*.ttf' -o -name '*.otf' -o -name '*.pfb' -o -name '*.ttc' \) \
        -print -quit 2> /dev/null | grep -q .; then
      return 0
    fi
  done
  return 1
}

fetched_fonts() {
  find "$root/usr/share/fonts" -type f -name '*.ttf' -print -quit 2> /dev/null | grep -q .
}

if ! has_fonts; then
  echo "browser-libs: this machine has no fonts" >&2
  if ! fetched_fonts; then
    ensure_apt
    fetch fonts-dejavu-core || true
  fi
  if ! fetched_fonts; then
    echo "browser-libs: could not fetch fonts-dejavu-core" >&2
    exit 1
  fi
  cat > "$root/fonts.conf" << EOF
<?xml version="1.0"?>
<!DOCTYPE fontconfig SYSTEM "fonts.dtd">
<fontconfig>
  <dir>$root/usr/share/fonts</dir>
  <cachedir>$work/fontconfig-cache</cachedir>
</fontconfig>
EOF
  echo "PDN_BROWSER_FONTCONFIG_FILE=$root/fonts.conf"
fi
