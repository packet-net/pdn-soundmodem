# Install

At the end of this page pdn-soundmodem is installed on your machine, the service exists, and `/etc/pdn-soundmodem/soundmodem.json` is there waiting to be edited. The modem will not run until you edit it, and that is what [02-first-station.md](02-first-station.md) is for.

## Before you start

A machine running Debian, Ubuntu or Raspberry Pi OS, on amd64, arm64 or armhf.

It needs glibc 2.34 or newer, on every architecture. That means **Debian 12 (bookworm), Ubuntu 22.04, Raspberry Pi OS bookworm, or newer**. Debian 11 (bullseye) ships glibc 2.31 and is below the line.

Two separate things set that floor, which is why it applies everywhere rather than only to 32-bit machines. The SQLite library the frame log writes through is built against glibc 2.34 on all three architectures, and on armhf .NET 10's own 32-bit ARM runtime needs 2.34 as well.

The package declares this, so apt on an older machine refuses the install and says which dependency it cannot satisfy. That is deliberate. An earlier version of the package did not declare it and installed anyway, which is worse: on bullseye the modem would start, print its version, look healthy, and then fail at the first frame it tried to write to the log.

If you are on a bullseye machine, the ways forward are to upgrade it to bookworm, or on a Pi to reinstall from a bookworm image; the 64-bit image is the better choice on a Pi 3, Zero 2 W, 4 or 5.

Root, or an account with `sudo`.

Nothing else. The package is self-contained, so there is no .NET runtime to install.

## Add the repository

The package comes from an apt repository, so your machine can install it and keep it up to date the same way it does everything else. Fetch the signing key, then write the source line:

```sh
curl -fsSL https://packet-net.github.io/apt/pubkey.asc | sudo gpg --dearmor -o /usr/share/keyrings/packet-net.gpg
echo "deb [signed-by=/usr/share/keyrings/packet-net.gpg] https://packet-net.github.io/apt ./" | sudo tee /etc/apt/sources.list.d/packet-net.list
```

The second command echoes the line it wrote. apt now checks every package it fetches from that repository against the key, and refuses anything the key did not sign.

If your machine has no `sudo`, become root with `su -` and run every command on this page without the `sudo` prefix.

## Install it

```sh
sudo apt update
sudo apt install pdn-soundmodem
```

apt picks the build for your architecture, and pulls in the handful of system libraries the package depends on (`libasound2`, `libstdc++6` and friends). There is nothing to choose and nothing to download by hand.

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

The service runs as a system user called `pdn-soundmodem`, which the package creates. The unit gives that user the `audio` group so it can open `/dev/snd/*`, and `dialout` so it can key a radio over serial PTT, so `id pdn-soundmodem` does not list either. CM108 keying needs one more step, a udev rule, which [02-first-station.md](02-first-station.md) covers when you get to it.

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
  Access to the path '/dev/hidraw0' is denied.
  Set by "ptt" in /etc/pdn-soundmodem/soundmodem.json
```

With no interface plugged in yet the middle line reads `Could not find file '/dev/hidraw0'.` instead.

Below that come a few lines of advice about permissions, and a line saying the service will keep retrying in case the device is merely late appearing.

Seeing that means the install is complete and the modem is waiting for a config it can use. Go to [02-first-station.md](02-first-station.md).

## If it did not

`Unable to locate package pdn-soundmodem` means apt has not read the repository yet, or the source line did not land. Run `sudo apt update` again and check that `cat /etc/apt/sources.list.d/packet-net.list` prints the `deb [signed-by=...]` line.

`The following signatures couldn't be verified` on `apt update` means the key is missing or was written somewhere else. Fetch it again with the `curl` command above, and check that `/usr/share/keyrings/packet-net.gpg` exists.

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

```sh
sudo apt update && sudo apt upgrade
```

That takes a new pdn-soundmodem along with everything else the machine has updates for. `sudo apt install pdn-soundmodem` does the same for this package alone. Your config file is left alone, the service stays enabled if it was, and everything under `/var/lib/pdn-soundmodem` survives, including the mixer levels the station page last set. Template instances that were running are restarted on the new binary.

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
