#!/usr/bin/env bash
# Builds the pdn-soundmodem .deb for one architecture.
#
#   packaging/build-deb.sh <version> [amd64|arm64|armhf] [outdir]
#
# Produces <outdir>/pdn-soundmodem_<version>_<arch>.deb containing a self-contained
# single-file build (no .NET runtime dependency on the target), a systemd unit and an
# example config. Default outdir is <repo>/artifacts.
#
# Layout note: PublishSingleFile bundles the managed assemblies and the runtime, but
# leaves per-package native shims (libSystem.IO.Ports.Native.so) loose beside the
# executable, and .NET resolves those relative to the real path of the running binary.
# So the payload goes in /usr/lib/pdn-soundmodem/ and /usr/bin/pdn-soundmodem is a
# symlink into it. Installing only the bare executable to /usr/bin drops the shim and
# serial PTT dies with DllNotFoundException at the first key-down.
set -euo pipefail

VERSION="${1:?usage: build-deb.sh <version> [arch] [outdir]}"
ARCH="${2:-amd64}"

case "$ARCH" in
  amd64) RID=linux-x64 ;;
  arm64) RID=linux-arm64 ;;
  armhf) RID=linux-arm ;;
  *) echo "unsupported arch $ARCH" >&2; exit 2 ;;
esac

# dpkg-deb ships in the Essential `dpkg` package, so this only trips on a non-Debian host.
command -v dpkg-deb >/dev/null || { echo "dpkg-deb not found — this needs a Debian-family host" >&2; exit 3; }

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(dirname "$HERE")"
OUTDIR="${3:-$ROOT/artifacts}"
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

PKGDIR=/usr/lib/pdn-soundmodem
DOCDIR=/usr/share/doc/pdn-soundmodem
DATADIR=/usr/share/pdn-soundmodem
UNITDIR=/usr/lib/systemd/system

dotnet publish "$ROOT/src/Packet.SoundModem.Daemon/Packet.SoundModem.Daemon.csproj" \
  --configuration Release \
  --runtime "$RID" \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:Version="$VERSION" \
  -p:DebugType=none \
  -p:GenerateDocumentationFile=false \
  --output "$STAGE/publish"

mkdir -p "$STAGE/root$PKGDIR" \
         "$STAGE/root/usr/bin" \
         "$STAGE/root$UNITDIR" \
         "$STAGE/root/etc/pdn-soundmodem" \
         "$STAGE/root$DATADIR" \
         "$STAGE/root$DOCDIR" \
         "$STAGE/root/DEBIAN"

install -m 0755 "$STAGE/publish/pdn-soundmodem" "$STAGE/root$PKGDIR/pdn-soundmodem"
# Native shims published alongside the bundle (System.IO.Ports and friends).
for so in "$STAGE"/publish/*.so; do
  [ -e "$so" ] || continue
  install -m 0644 "$so" "$STAGE/root$PKGDIR/$(basename "$so")"
done
ln -s "..${PKGDIR#/usr}/pdn-soundmodem" "$STAGE/root/usr/bin/pdn-soundmodem"

install -m 0644 "$HERE/pdn-soundmodem.service" "$STAGE/root$UNITDIR/pdn-soundmodem.service"
install -m 0644 "$HERE/copyright" "$STAGE/root$DOCDIR/copyright"
# The seed config lives under /usr/share/pdn-soundmodem, NOT /usr/share/doc: Debian
# permits /usr/share/doc to be stripped (the official Ubuntu images ship a dpkg
# path-exclude for it), and postinst reads this file — a maintainer script that
# depends on a doc path fails to configure on those systems.
install -m 0644 "$ROOT/soundmodem.example.json" "$STAGE/root$DATADIR/soundmodem.example.json"

# Debian changelog. A numeric SOURCE_DATE_EPOCH keeps rebuilds of a tag byte-identical;
# anything else (an ISO string from a CI event payload, say) falls back to now rather
# than failing the build on `date -R`.
case "${SOURCE_DATE_EPOCH:-}" in
  ''|*[!0-9]*) CHANGELOG_DATE="$(date -R)" ;;
  *)           CHANGELOG_DATE="$(date -R --date="@$SOURCE_DATE_EPOCH")" ;;
esac
cat > "$STAGE/changelog.Debian" <<EOF
pdn-soundmodem ($VERSION) unstable; urgency=medium

  * Release $VERSION. See https://github.com/packet-net/pdn-soundmodem/releases/tag/v$VERSION

 -- Tom Fanning M0LTE <tom@m0lte.uk>  $CHANGELOG_DATE
EOF
gzip -9n -c "$STAGE/changelog.Debian" > "$STAGE/root$DOCDIR/changelog.Debian.gz"
chmod 0644 "$STAGE/root$DOCDIR/changelog.Debian.gz"

INSTALLED_SIZE="$(du -k -s --exclude=DEBIAN "$STAGE/root" | cut -f1)"

cat > "$STAGE/root/DEBIAN/control" <<EOF
Package: pdn-soundmodem
Version: $VERSION
Architecture: $ARCH
Maintainer: Tom Fanning M0LTE <tom@m0lte.uk>
Installed-Size: $INSTALLED_SIZE
Depends: libc6, libgcc-s1, libstdc++6, libasound2 | libasound2t64, adduser
Section: hamradio
Priority: optional
Homepage: https://github.com/packet-net/pdn-soundmodem
Description: Headless soundcard packet-radio modem (KISS TCP)
 AFSK 1200, BPSK 300 / QPSK 2400 / QPSK 3600 (IL2P+CRC), 9600 baseband
 (classic G3RUH and IL2P) and FX.25, with native DCD, p-persistent CSMA,
 serial/CM108 PTT and a multi-client KISS-over-TCP server.
 .
 Ships a systemd unit, enabled and started on install. The modem has no useful
 defaults, so edit /etc/pdn-soundmodem/soundmodem.json for your sound device and
 PTT and then "systemctl restart pdn-soundmodem"; until you do, the service will
 fail to start and "systemctl status pdn-soundmodem" will say why.
 .
 GPL-3.0-or-later.
EOF

# --- maintainer scripts -------------------------------------------------------
# The systemd stanzas follow dh_installsystemd's default output: enable the unit and
# start it on install, restart it on upgrade. Note the consequence — the seeded config
# names a sound device and PTT line that will not exist on most machines, so the first
# start after a fresh install is expected to fail until an admin edits it. That is the
# Debian-conventional posture and a deliberate choice; `systemctl status` after install
# is the intended way to find out what needs configuring.

cat > "$STAGE/root/DEBIAN/postinst" <<'EOF'
#!/bin/sh
set -e

EXAMPLE=/usr/share/pdn-soundmodem/soundmodem.example.json
CONFIG=/etc/pdn-soundmodem/soundmodem.json

case "$1" in
  configure)
    if ! getent passwd pdn-soundmodem >/dev/null; then
        adduser --system --no-create-home --group pdn-soundmodem
    fi
    if [ ! -e "$CONFIG" ] && [ -f "$EXAMPLE" ]; then
        cp "$EXAMPLE" "$CONFIG"
        chmod 0644 "$CONFIG"
        echo "pdn-soundmodem: seeded $CONFIG from the example."
    fi
    echo "pdn-soundmodem: edit $CONFIG for your sound device and PTT, then"
    echo "                systemctl restart pdn-soundmodem. Until then the service will"
    echo "                fail to start — systemctl status pdn-soundmodem says why."
    ;;
esac

if [ "$1" = "configure" ] || [ "$1" = "abort-upgrade" ] || [ "$1" = "abort-deconfigure" ] || [ "$1" = "abort-remove" ]; then
    if command -v deb-systemd-helper >/dev/null; then
        # Undo the mask that postrm sets on remove.
        deb-systemd-helper unmask 'pdn-soundmodem.service' >/dev/null || true
        # was-enabled reports true for a unit the helper has never seen, so this enables on
        # first install and re-creates symlinks on upgrade if [Install] changed. The else
        # branch records current symlinks so purge cleans up after an admin who disabled it.
        if deb-systemd-helper --quiet was-enabled 'pdn-soundmodem.service'; then
            deb-systemd-helper enable 'pdn-soundmodem.service' >/dev/null || true
        else
            deb-systemd-helper update-state 'pdn-soundmodem.service' >/dev/null || true
        fi
    fi
    if [ -d /run/systemd/system ] && command -v systemctl >/dev/null; then
        systemctl --system daemon-reload >/dev/null || true
        # $2 is the previously-configured version: set on upgrade, empty on first install.
        if [ -n "${2:-}" ]; then _action=restart; else _action=start; fi
        if command -v deb-systemd-invoke >/dev/null; then
            deb-systemd-invoke "$_action" 'pdn-soundmodem.service' >/dev/null || true
        fi
    fi
fi

exit 0
EOF

cat > "$STAGE/root/DEBIAN/prerm" <<'EOF'
#!/bin/sh
set -e

if [ -d /run/systemd/system ] && [ "$1" = "remove" ] && command -v deb-systemd-invoke >/dev/null; then
    deb-systemd-invoke stop 'pdn-soundmodem.service' >/dev/null || true
fi

exit 0
EOF

cat > "$STAGE/root/DEBIAN/postrm" <<'EOF'
#!/bin/sh
set -e

if [ -d /run/systemd/system ] && command -v systemctl >/dev/null; then
    systemctl --system daemon-reload >/dev/null || true
fi

if [ "$1" = "remove" ] && command -v deb-systemd-helper >/dev/null; then
    deb-systemd-helper mask 'pdn-soundmodem.service' >/dev/null || true
fi

if [ "$1" = "purge" ]; then
    if command -v deb-systemd-helper >/dev/null; then
        deb-systemd-helper purge 'pdn-soundmodem.service' >/dev/null || true
        deb-systemd-helper unmask 'pdn-soundmodem.service' >/dev/null || true
    fi
    # soundmodem.json is seeded by postinst, not shipped by dpkg, so dpkg will not
    # remove it on purge — do it here.
    rm -f /etc/pdn-soundmodem/soundmodem.json
    rmdir --ignore-fail-on-non-empty /etc/pdn-soundmodem 2>/dev/null || true
    if getent passwd pdn-soundmodem >/dev/null; then
        deluser --system --quiet pdn-soundmodem >/dev/null 2>&1 || true
    fi
fi

exit 0
EOF

chmod 0755 "$STAGE/root/DEBIAN/postinst" "$STAGE/root/DEBIAN/prerm" "$STAGE/root/DEBIAN/postrm"
for s in postinst prerm postrm; do sh -n "$STAGE/root/DEBIAN/$s"; done

mkdir -p "$OUTDIR"
DEB="$OUTDIR/pdn-soundmodem_${VERSION}_${ARCH}.deb"
dpkg-deb --build --root-owner-group "$STAGE/root" "$DEB"
echo "built: $DEB"
