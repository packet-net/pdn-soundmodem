#!/usr/bin/env bash
# Builds the pdn-soundmodem .deb for one architecture.
#
#   packaging/build-deb.sh <version> [amd64|arm64|armhf]
#
# Produces artifacts/pdn-soundmodem_<version>_<arch>.deb containing a self-contained
# single-file build (no .NET runtime dependency on the target), a systemd unit and an
# example config. Depends: libasound2 only.
set -euo pipefail

VERSION="${1:?usage: build-deb.sh <version> [arch]}"
ARCH="${2:-amd64}"

case "$ARCH" in
  amd64) RID=linux-x64 ;;
  arm64) RID=linux-arm64 ;;
  armhf) RID=linux-arm ;;
  *) echo "unsupported arch $ARCH" >&2; exit 2 ;;
esac

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(dirname "$HERE")"
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

dotnet publish "$ROOT/src/Packet.SoundModem.Daemon/Packet.SoundModem.Daemon.csproj" \
  --configuration Release \
  --runtime "$RID" \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:Version="$VERSION" \
  -p:DebugType=none \
  --output "$STAGE/publish"

mkdir -p "$STAGE/root/usr/bin" \
         "$STAGE/root/lib/systemd/system" \
         "$STAGE/root/etc/pdn-soundmodem" \
         "$STAGE/root/DEBIAN"

install -m 0755 "$STAGE/publish/pdn-soundmodem" "$STAGE/root/usr/bin/pdn-soundmodem"
install -m 0644 "$HERE/pdn-soundmodem.service" "$STAGE/root/lib/systemd/system/pdn-soundmodem.service"
install -m 0644 "$ROOT/soundmodem.example.json" "$STAGE/root/etc/pdn-soundmodem/soundmodem.example.json"

cat > "$STAGE/root/DEBIAN/control" <<EOF
Package: pdn-soundmodem
Version: $VERSION
Architecture: $ARCH
Maintainer: Tom Fanning M0LTE and Packet.NET contributors
Depends: libasound2 | libasound2t64
Section: hamradio
Priority: optional
Homepage: https://github.com/packet-net/pdn-soundmodem
Description: Headless soundcard packet-radio modem (KISS TCP)
 AFSK 1200, BPSK 300 / QPSK 2400 / QPSK 3600 (IL2P+CRC), 9600 baseband
 (classic G3RUH and IL2P) and FX.25, with native DCD, p-persistent CSMA,
 serial/CM108 PTT and a multi-client KISS-over-TCP server.
 GPL-3.0-or-later.
EOF

cat > "$STAGE/root/DEBIAN/postinst" <<'EOF'
#!/bin/sh
set -e
if ! getent passwd pdn-soundmodem >/dev/null; then
    adduser --system --no-create-home --group pdn-soundmodem
fi
if [ ! -e /etc/pdn-soundmodem/soundmodem.json ]; then
    echo "pdn-soundmodem: copy /etc/pdn-soundmodem/soundmodem.example.json to soundmodem.json and edit before starting."
fi
exit 0
EOF
chmod 0755 "$STAGE/root/DEBIAN/postinst"

mkdir -p "$ROOT/artifacts"
dpkg-deb --build --root-owner-group "$STAGE/root" \
  "$ROOT/artifacts/pdn-soundmodem_${VERSION}_${ARCH}.deb"
echo "built: artifacts/pdn-soundmodem_${VERSION}_${ARCH}.deb"
