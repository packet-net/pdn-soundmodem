# Installing pdn-soundmodem

The release pipeline publishes a `.deb` for each tagged version. The package is
**self-contained** — it bundles the .NET runtime, so there is nothing else to install on
the target machine.

| Architecture | Package | Typical host |
|---|---|---|
| `amd64` | `pdn-soundmodem_<version>_amd64.deb` | x86-64 PC or server |
| `arm64` | `pdn-soundmodem_<version>_arm64.deb` | Raspberry Pi 3/4/5 on 64-bit Raspberry Pi OS |
| `armhf` | `pdn-soundmodem_<version>_armhf.deb` | 32-bit Raspberry Pi OS, older Pi |

Not sure which? `dpkg --print-architecture` tells you.

## Install

Download the `.deb` for your architecture from the
[latest release](https://github.com/packet-net/pdn-soundmodem/releases/latest), then:

```sh
sudo apt install ./pdn-soundmodem_<version>_<arch>.deb
```

Using `apt` rather than `dpkg -i` lets it pull the handful of system libraries the package
depends on (`libasound2`, `libstdc++6` and friends).

Each release also ships a `SHA256SUMS` file. To verify before installing:

```sh
sha256sum -c SHA256SUMS --ignore-missing
```

## Configure

**The service is enabled and started on install, and on a fresh install it will fail —
that is expected.** A packet modem has no useful defaults: the seeded config names a sound
device and PTT line that almost certainly don't match your machine.

```sh
systemctl status pdn-soundmodem
```

tells you exactly what it could not open, which setting selects it, and the command that
lists what your machine actually has. Work through what it says, then restart.

Installing seeds `/etc/pdn-soundmodem/soundmodem.json` from the shipped example
(`/usr/share/pdn-soundmodem/soundmodem.example.json`). Edit it:

```sh
sudoedit /etc/pdn-soundmodem/soundmodem.json
```

At minimum, set:

- **`device`** — the ALSA capture/playback device. `arecord -l` lists what is available;
  `"default"` works for a single USB sound card. A FlexRadio over the LAN is also a device
  here (`"flex:<radio>"`) — see [docs/flex-integration.md](docs/flex-integration.md).
- **`modems`** — which mode runs on which KISS sub-channel, e.g.
  `{ "subChannel": 0, "mode": "afsk1200-multi" }`. The example config lists the full mode
  set in its comments.
- **`ptt`** — how the radio is keyed. `serial` (RTS/DTR on `/dev/ttyUSB0`) or `cm108`
  (a GPIO pin on a USB sound-card interface such as a DRA/RB-USB RIM). Omit it entirely
  for VOX or for a FlexRadio, which keys itself.

Optionally, uncomment **`waterfall`** to serve a live spectrum and waterfall page on port
8107 — useful for checking you are hearing the band at a sane level before you trust the
decoder. It binds to loopback unless you set `"bind": "*"`.

The file is JSON but comments are allowed, so the example's annotations can stay.

> **Every setting, with defaults and validation rules, is documented in
> [CONFIG.md](CONFIG.md)** — the four fields above are just the ones you need to get on the
> air. CONFIG.md also covers POCSAG paging, the ARDOP virtual TNC, FlexRadio, CSMA timing,
> and how command-line flags interact with the config file.

Then restart it to pick up your changes:

```sh
sudo systemctl restart pdn-soundmodem
systemctl status pdn-soundmodem
journalctl -u pdn-soundmodem -f
```

It is already enabled at boot. If you would rather it didn't run, `sudo systemctl disable
--now pdn-soundmodem` — the setting survives package upgrades.

KISS-over-TCP listens on port **8105** by default. Point LinBPQ, Direwolf-style APRS
software, or the PDN node at it.

## Permissions

The service runs as the unprivileged `pdn-soundmodem` system user, which the package
creates. The unit puts it in the `audio` group (for `/dev/snd/*`) and `dialout` (for serial
PTT on `/dev/ttyUSB*`).

**CM108 PTT needs one extra step.** `/dev/hidraw*` nodes are root-only by default, so grant
the `audio` group access to your interface with a udev rule — replace the IDs with your
device's (`lsusb` will show them; `0d8c:013c` is a common C-Media interface):

```sh
sudo tee /etc/udev/rules.d/99-pdn-soundmodem-cm108.rules >/dev/null <<'EOF'
KERNEL=="hidraw*", ATTRS{idVendor}=="0d8c", ATTRS{idProduct}=="013c", MODE="0660", GROUP="audio"
EOF
sudo udevadm control --reload-rules && sudo udevadm trigger
```

Unplug and replug the interface, then restart the service.

## Upgrading

Install the new `.deb` the same way. Your `/etc/pdn-soundmodem/soundmodem.json` is left
alone, and if you had enabled the service it stays enabled.

## Uninstalling

```sh
sudo apt remove pdn-soundmodem     # removes the program, keeps your config
sudo apt purge  pdn-soundmodem     # also removes the config and the system user
```

## What the package installs

| Path | Contents |
|---|---|
| `/usr/bin/pdn-soundmodem` | symlink to the executable |
| `/usr/lib/pdn-soundmodem/` | the self-contained binary and its native shims |
| `/usr/lib/systemd/system/pdn-soundmodem.service` | the systemd unit |
| `/usr/share/pdn-soundmodem/soundmodem.example.json` | the annotated example config |
| `/etc/pdn-soundmodem/soundmodem.json` | your config, seeded on first install |
| `/usr/share/doc/pdn-soundmodem/` | copyright and changelog |

## Running without installing

The daemon takes its whole configuration on the command line too, which is handy for a
quick test before committing to a config file:

```sh
pdn-soundmodem --device default --modem 0:afsk1200 --kiss 8105
```

Run `pdn-soundmodem` with no arguments for the option list.

## Building the package yourself

```sh
packaging/build-deb.sh 0.7.0 amd64      # → artifacts/pdn-soundmodem_0.7.0_amd64.deb
```

Needs `dpkg-deb` (`apt install dpkg-dev`) and the .NET 10 SDK. `arm64` and `armhf` build
the same way — cross-compilation is handled by the .NET SDK, so any of the three can be
built from any host.

`packaging/test-deb.sh` runs the package through install / enable / upgrade / purge in
throwaway Debian and Ubuntu containers, including one running real systemd. It needs
Docker:

```sh
packaging/build-deb.sh 0.0.0-test amd64
packaging/test-deb.sh
```
