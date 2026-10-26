#!/usr/bin/env bash
# Quick local test of ci-require-tools.sh: bash scripts/test-ci-require-tools.sh
set -u
s="$(dirname "$0")/ci-require-tools.sh"
fail=0
check() { # <expected exit> <description> <args...>
  local want="$1" what="$2"; shift 2
  "$s" "$@" > /dev/null 2>&1; local got=$?
  if [ "$got" != "$want" ]; then echo "FAIL: $what (exit $got, wanted $want)"; fail=1; else echo "ok: $what"; fi
}
check 0 "present command" bash
check 1 "missing command" no-such-tool-xyz:no-such-pkg
check 1 "missing among present" bash no-such-tool-xyz
check 0 "present library" lib:libc.so.6:libc6
check 1 "missing library" lib:libno-such-xyz.so.9:libno-such
# ldconfig off PATH must not make a present library look absent.
PATH_SAVE="$PATH"; PATH="$(dirname "$(command -v bash)")"
check 0 "present library, ldconfig not on PATH" lib:libc.so.6:libc6
PATH="$PATH_SAVE"
"$s" no-such-tool-xyz:no-such-pkg 2>&1 | grep -q "install the 'no-such-pkg' package" || { echo "FAIL: message names package"; fail=1; }
exit "$fail"
