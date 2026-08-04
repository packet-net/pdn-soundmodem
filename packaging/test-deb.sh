#!/usr/bin/env bash
# Acceptance test for the pdn-soundmodem .deb, run in throwaway containers.
#
#   packaging/build-deb.sh 0.0.0-test amd64
#   packaging/test-deb.sh [version] [debdir]
#
# Exercises install / seed / enable / remove / purge on Debian and Ubuntu, plus a real
# systemd instance (a plain image cannot catch a unit that systemd refuses to load, and
# deb-systemd-helper behaves differently when systemd is actually running).
#
# Needs Docker. Nothing is installed on the host.
set -uo pipefail

VERSION="${1:-0.0.0-test}"
HERE="$(cd "$(dirname "$0")" && pwd)"
DEBDIR="$(cd "${2:-$(dirname "$HERE")/artifacts}" && pwd)"
DEB="pdn-soundmodem_${VERSION}_amd64.deb"

[ -f "$DEBDIR/$DEB" ] || { echo "no $DEBDIR/$DEB - run packaging/build-deb.sh $VERSION amd64 first" >&2; exit 2; }
command -v docker >/dev/null || { echo "docker is required" >&2; exit 2; }

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"; docker rm -f pdnsm-systemd-test >/dev/null 2>&1' EXIT

# ---------------------------------------------------------------- plain container test
cat > "$WORK/plain.sh" <<PLAIN
#!/bin/bash
set -uo pipefail
DEB="$DEB"
PLAIN
cat >> "$WORK/plain.sh" <<'PLAIN'
FAIL=0
ok(){ echo "  PASS  $1"; }
bad(){ echo "  FAIL  $1"; FAIL=1; }
check(){ if eval "$2"; then ok "$1"; else bad "$1"; fi; }

export DEBIAN_FRONTEND=noninteractive
apt-get update -qq >/dev/null 2>&1
apt-get install -y -qq "/debs/$DEB" >/dev/null 2>&1

echo "== install =="
check "package is installed"            "dpkg -l pdn-soundmodem | grep -q '^ii'"
check "/usr/bin/pdn-soundmodem symlink" "[ -L /usr/bin/pdn-soundmodem ]"
check "payload in /usr/lib"             "[ -x /usr/lib/pdn-soundmodem/pdn-soundmodem ]"
check "native shim shipped"             "[ -f /usr/lib/pdn-soundmodem/libSystem.IO.Ports.Native.so ]"
check "systemd unit installed"          "[ -f /usr/lib/systemd/system/pdn-soundmodem.service ]"
# Must not live under /usr/share/doc: postinst reads it, and Debian permits doc to be
# stripped (the official Ubuntu images ship a dpkg path-exclude for exactly that).
check "example config outside doc"      "[ -f /usr/share/pdn-soundmodem/soundmodem.example.json ]"
check "config seeded"                   "[ -f /etc/pdn-soundmodem/soundmodem.json ]"
check "system user created"             "getent passwd pdn-soundmodem >/dev/null"
check "unit enabled on install"         "[ -L /etc/systemd/system/multi-user.target.wants/pdn-soundmodem.service ]"

echo
echo "== the binary runs from the packaged layout =="
out=$(timeout 25 /usr/bin/pdn-soundmodem --device null --ptt serial:/dev/ttyNOPE0:rts --kiss 18201 2>&1)
echo "$out" | grep -q 'DllNotFoundException' \
  && bad "native lib resolves (got DllNotFoundException)" \
  || ok  "native lib resolves - no DllNotFoundException"
echo "$out" | grep -q 'kiss tcp' && ok "modem starts and binds KISS" || bad "modem did not bind KISS"

echo
echo "== remove =="
apt-get remove -y -qq pdn-soundmodem >/dev/null 2>&1
check "binary gone after remove"        "[ ! -e /usr/lib/pdn-soundmodem/pdn-soundmodem ]"
check "config survives remove"          "[ -f /etc/pdn-soundmodem/soundmodem.json ]"
check "user survives remove"            "getent passwd pdn-soundmodem >/dev/null"

echo
echo "== purge =="
apt-get purge -y -qq pdn-soundmodem >/dev/null 2>&1
check "config removed on purge"         "[ ! -e /etc/pdn-soundmodem/soundmodem.json ]"
check "config dir removed on purge"     "[ ! -d /etc/pdn-soundmodem ]"
check "user removed on purge"           "! getent passwd pdn-soundmodem >/dev/null"
check "no enable symlink left"          "[ ! -e /etc/systemd/system/multi-user.target.wants/pdn-soundmodem.service ]"

echo
[ "$FAIL" = 0 ] && echo "ALL CHECKS PASSED" || echo "SOME CHECKS FAILED"
exit $FAIL
PLAIN

# -------------------------------------------------------------- systemd container test
cat > "$WORK/systemd.sh" <<SYSD
#!/bin/bash
set -uo pipefail
DEB="$DEB"
SYSD
cat >> "$WORK/systemd.sh" <<'SYSD'
FAIL=0
ok(){ echo "  PASS  $1"; }
bad(){ echo "  FAIL  $1"; FAIL=1; }
check(){ if eval "$2"; then ok "$1"; else bad "$1"; fi; }

export DEBIAN_FRONTEND=noninteractive
apt-get update -qq >/dev/null 2>&1
# Docker images ship a policy-rc.d that makes every maintainer-script service start a
# no-op. Leave it in place and postinst's `start` is silently skipped, which would make
# any assertion about auto-start vacuous. Remove it so the real thing runs.
rm -f /usr/sbin/policy-rc.d
apt-get install -y -qq "/debs/$DEB" >/dev/null 2>&1

echo "== install under real systemd =="
check "package installed"     "dpkg -l pdn-soundmodem | grep -q '^ii'"
check "systemd sees the unit" "systemctl cat pdn-soundmodem.service >/dev/null 2>&1"
verify=$(systemd-analyze verify /usr/lib/systemd/system/pdn-soundmodem.service 2>&1)
[ -z "$verify" ] && ok "systemd-analyze verify is clean" || bad "systemd-analyze verify: $verify"
state=$(systemctl is-enabled pdn-soundmodem.service 2>&1)
[ "$state" = "enabled" ] && ok "enabled on install" || bad "expected enabled on install, got '$state'"
# postinst starts it too. There is no sound card in here so it will not stay up, but
# systemd must have actually executed it - assert on a timestamp systemd only sets once
# the main process has run. (`journalctl | grep -q .` is NOT a valid check here: it
# matches the literal "-- No entries --" line and so passes when nothing ever started.)
started=$(systemctl show pdn-soundmodem.service -p ExecMainStartTimestamp --value 2>/dev/null)
[ -n "$started" ] && ok "postinst started it ($started)" \
                  || bad "postinst did not start the unit - ExecMainStartTimestamp empty"

echo
echo "== start =="
systemctl start pdn-soundmodem.service >/dev/null 2>&1
sleep 3
if journalctl -u pdn-soundmodem.service --no-pager 2>/dev/null | grep -q 'DllNotFoundException\|203/EXEC'; then
  bad "unit failed for a packaging reason"
  journalctl -u pdn-soundmodem.service --no-pager -n 20 2>/dev/null
else
  ok "unit ran the binary (no exec/lib failure)"
fi
systemctl stop pdn-soundmodem.service >/dev/null 2>&1

echo
echo "== a bad config stops, and says why, instead of crash-looping =="
cp /etc/pdn-soundmodem/soundmodem.json /tmp/good.json
printf '{ "device": "null", "modems": [ { "subChannel": 0, "mode": } ] }' > /etc/pdn-soundmodem/soundmodem.json
systemctl reset-failed pdn-soundmodem >/dev/null 2>&1 || true
journalctl --rotate >/dev/null 2>&1; journalctl --vacuum-time=1s >/dev/null 2>&1
systemctl restart pdn-soundmodem >/dev/null 2>&1 || true
# Longer than RestartSec=5, so a unit that was going to crash-loop has had its chance.
sleep 9
restarts=$(systemctl show pdn-soundmodem.service -p NRestarts --value 2>/dev/null)
[ "$restarts" = "0" ] && ok "no restarts after a bad config (RestartPreventExitStatus=2)" \
                      || bad "expected 0 restarts, got '$restarts' - a bad config is crash-looping"
code=$(systemctl show pdn-soundmodem.service -p ExecMainStatus --value 2>/dev/null)
[ "$code" = "2" ] && ok "exited 2 (the code the unit refuses to retry)" \
                  || bad "expected exit 2 for a bad config, got '$code'"
log=$(journalctl -u pdn-soundmodem.service --no-pager 2>/dev/null)
echo "$log" | grep -q 'configuration error in /etc/pdn-soundmodem/soundmodem.json' \
  && ok "journal names the file" || bad "journal does not name the offending file"
echo "$log" | grep -q 'not valid JSON' && ok "journal says what is wrong" || bad "journal does not say what is wrong"
echo "$log" | grep -q 'CONFIG.md' && ok "journal points at the reference" || bad "journal has no pointer to docs"
echo "$log" | grep -q 'Unhandled exception' && bad "journal contains a stack trace" \
                                            || ok "no stack trace in the journal"
cp /tmp/good.json /etc/pdn-soundmodem/soundmodem.json
systemctl reset-failed pdn-soundmodem >/dev/null 2>&1 || true

echo
echo "== reinstall keeps enablement =="
apt-get install -y -qq --reinstall "/debs/$DEB" >/dev/null 2>&1
check "still enabled after reinstall" "[ \"\$(systemctl is-enabled pdn-soundmodem.service)\" = enabled ]"

echo
echo "== purge =="
apt-get purge -y -qq pdn-soundmodem >/dev/null 2>&1
check "unit gone"             "! systemctl cat pdn-soundmodem.service >/dev/null 2>&1"
check "no wants symlink left" "[ ! -e /etc/systemd/system/multi-user.target.wants/pdn-soundmodem.service ]"
check "config removed"        "[ ! -e /etc/pdn-soundmodem/soundmodem.json ]"
check "user removed"          "! getent passwd pdn-soundmodem >/dev/null"

echo
[ "$FAIL" = 0 ] && echo "ALL CHECKS PASSED" || echo "SOME CHECKS FAILED"
exit $FAIL
SYSD

chmod +x "$WORK"/*.sh
RC=0

for img in debian:bookworm ubuntu:24.04; do
  echo "################ $img ################"
  docker run --rm -v "$DEBDIR:/debs:ro" -v "$WORK:/w:ro" "$img" bash /w/plain.sh || RC=1
  echo
done

echo "################ debian:bookworm + systemd ################"
cat > "$WORK/Dockerfile" <<'EOF'
FROM debian:bookworm
RUN apt-get update && apt-get install -y --no-install-recommends systemd systemd-sysv dbus && apt-get clean
CMD ["/sbin/init"]
EOF
if docker build -q -t pdnsm-systemd "$WORK" >/dev/null 2>&1; then
  docker rm -f pdnsm-systemd-test >/dev/null 2>&1
  docker run -d --name pdnsm-systemd-test --privileged --cgroupns=host \
    -v /sys/fs/cgroup:/sys/fs/cgroup:rw --tmpfs /run --tmpfs /run/lock \
    -v "$DEBDIR:/debs:ro" -v "$WORK:/w:ro" pdnsm-systemd >/dev/null
  for _ in $(seq 1 20); do
    docker exec pdnsm-systemd-test systemctl is-system-running 2>/dev/null | grep -qE 'running|degraded' && break
    sleep 1
  done
  docker exec pdnsm-systemd-test bash /w/systemd.sh || RC=1
else
  echo "  SKIP  could not build the systemd image"
fi

echo
[ "$RC" = 0 ] && echo "=== deb acceptance: PASS ===" || echo "=== deb acceptance: FAIL ==="
exit $RC
