# Install

At the end of this page pdn-soundmodem is installed on your machine, the service exists, and `/etc/pdn-soundmodem/soundmodem.json` is there waiting to be edited. The modem will not run until you edit it, and that is what [02-first-station.md](02-first-station.md) is for.

## Before you start

A machine running Debian, Ubuntu or Raspberry Pi OS, on amd64, arm64 or armhf.

Root, or an account with `sudo`.

Nothing else. The package is self-contained, so there is no .NET runtime to install.

## Pick the package for your architecture

Ask the machine which one it wants:

```sh
dpkg --print-architecture
```

| Answer | Package | Typical machine |
|---|---|---|
| `amd64` | `pdn-soundmodem_<version>_amd64.deb` | x86-64 PC or server |
| `arm64` | `pdn-soundmodem_<version>_arm64.deb` | Raspberry Pi 3, 4 or 5 on 64-bit Raspberry Pi OS |
| `armhf` | `pdn-soundmodem_<version>_armhf.deb` | 32-bit Raspberry Pi OS, older Pi |

## Install it

One line fetches the right `.deb` from the latest release and installs it:

```sh
wget -qO- https://api.github.com/repos/packet-net/pdn-soundmodem/releases/latest | grep -o "https://github.com/[^\"]*_$(dpkg --print-architecture)\.deb" | xargs -I{} sh -c 'wget -qO /tmp/pdn-soundmodem.deb "{}" && { command -v sudo >/dev/null 2>&1 && sudo apt install -y /tmp/pdn-soundmodem.deb || su -c "apt install -y /tmp/pdn-soundmodem.deb"; }'
```

To do it by hand instead, download the `.deb` and the `SHA256SUMS` file for your architecture from the [latest release](https://github.com/packet-net/pdn-soundmodem/releases/latest), check the download, then install it:

```sh
sha256sum -c SHA256SUMS --ignore-missing
sudo apt install ./pdn-soundmodem_<version>_<arch>.deb
```

The checksum step should print one line per file you downloaded:

```
pdn-soundmodem_0.69.0_arm64.deb: OK
```

`apt install ./file.deb` rather than `dpkg -i` because apt pulls in the handful of system libraries the package depends on (`libasound2`, `libstdc++6` and friends); `dpkg` would leave them unmet.

If your machine has no `sudo`, become root with `su -` and run every command here without the `sudo` prefix.

With the `.deb` sitting in `/root`, apt prints this while installing:

```
N: Download is performed unsandboxed as root as file '/root/pdn-soundmodem_0.69.0_arm64.deb'
   couldn't be accessed by user '_apt'. - pkgAcquire::Run (13: Permission denied)
```

That is a notice and the package installs anyway. Keep the file in `/tmp` instead and it does not appear.

## What the install did

| Path | What it is |
|---|---|
| `/usr/bin/pdn-soundmodem` | The program, a symlink into `/usr/lib/pdn-soundmodem/` |
| `/etc/pdn-soundmodem/soundmodem.json` | Your config file, seeded from the shipped example if it was not already there |
| `/usr/share/pdn-soundmodem/soundmodem.example.json` | The example the seed came from |
| `/usr/lib/systemd/system/pdn-soundmodem.service` | The service, enabled and started on install |
| `/usr/lib/systemd/system/pdn-soundmodem@.service` | The template service for a second modem on the same machine |
| `/var/lib/pdn-soundmodem/` | Where the modem keeps the frame log, survey captures and mixer levels |

The full list, including what the modem writes and when, is in the [files reference](reference/files.md#what-the-package-installs).

The service runs as a system user called `pdn-soundmodem`, which the package creates. It is in the `audio` group so it can open `/dev/snd/*`, and in `dialout` so it can key a radio over serial PTT. CM108 keying needs one more step, a udev rule, which [02-first-station.md](02-first-station.md) covers when you get to it.

## Check it worked

Ask the program its version:

```sh
pdn-soundmodem --version
```

```
pdn-soundmodem 0.69.0, commit 1cd85706f54a5bf84546dd2fbb04467992cdfcab
```

Then look at what the service said. `systemctl status` shows only the last ten journal lines and this message is longer than that, so ask journald for more:

```sh
journalctl -u pdn-soundmodem -n 30
```

The service is enabled and started on install, but it will not be running. The seeded config names a sound card called `default` and a CM108 interface on `/dev/hidraw0`, and one or both of those is wrong on nearly every machine. The message says which device it could not open and which setting names it:

```
cannot open the cm108 PTT device "/dev/hidraw0"
  Could not find file '/dev/hidraw0'.

  Set by "ptt" in /etc/pdn-soundmodem/soundmodem.json
```

Below that come a few lines of advice about permissions, and a line saying the service will keep retrying in case the device is merely late appearing.

Seeing that means the install is complete and the modem is waiting for a config it can use. Go to [02-first-station.md](02-first-station.md).

## If it did not

`package architecture (arm64) does not match system (armhf)` means you downloaded the wrong `.deb`. Run `dpkg --print-architecture` again and fetch the one it names.

`systemctl status` says `Unit pdn-soundmodem.service could not be found`: the package did not finish installing. Run `sudo apt install -f` and read what apt says about unmet dependencies.

A status output saying a start condition was not met means `/etc/pdn-soundmodem/soundmodem.json` is missing, and the service does not start at all without it. Copy the example back with `sudo cp /usr/share/pdn-soundmodem/soundmodem.example.json /etc/pdn-soundmodem/soundmodem.json`.

Anything else, or a message you cannot place, is in [12-troubleshooting.md](12-troubleshooting.md).

## More than one modem on one machine

A second sound card and radio is a second config file and a template instance. `pdn-soundmodem@NAME` reads `/etc/pdn-soundmodem/NAME.json` and keeps its state in `/var/lib/pdn-soundmodem/NAME/`, so the two instances never share a frame log. Nothing else differs from the plain service.

```sh
sudo cp /usr/share/pdn-soundmodem/soundmodem.example.json /etc/pdn-soundmodem/vhf.json
sudo nano /etc/pdn-soundmodem/vhf.json
sudo systemctl enable --now pdn-soundmodem@vhf
journalctl -u pdn-soundmodem@vhf -f
```

Each file has to name its own sound card and its own PTT line, and claim its own ports. The [files reference](reference/files.md#more-than-one-modem) has the rules for both.

## Upgrading

Install the new `.deb` the same way, one-liner or by hand. Your config file is left alone, the service stays enabled if it was, and everything under `/var/lib/pdn-soundmodem` survives, including the mixer levels the station page last set. Template instances that were running are restarted on the new binary.

`pdn-soundmodem --version` tells you which version you now have.

## Removing

```sh
sudo apt remove pdn-soundmodem     # takes the program, keeps your config
sudo apt purge pdn-soundmodem      # also takes the config file and the service user
```

Purge removes the seeded `soundmodem.json` and un-enables any template instances, but keeps each instance's own `NAME.json` and everything under `/var/lib/pdn-soundmodem`. Delete that directory by hand if you want the frame logs and captures gone. The [files reference](reference/files.md#upgrade-remove-and-purge) lists it line by line.

## Related

- [02-first-station.md](02-first-station.md), which is where you go next.
- [03-radios-and-interfaces.md](03-radios-and-interfaces.md) for choosing a sound card, an interface and a way of keying the radio.
- [reference/files.md](reference/files.md) for every path the package installs, reads and writes.
- [reference/command-line.md](reference/command-line.md) for every flag, including `--version`.
