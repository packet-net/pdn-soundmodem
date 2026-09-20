# pdn-soundmodem

A software TNC for Linux. It turns a sound card, a FlexRadio or a public web receiver into a packet modem, talks KISS over TCP to your node or APRS software, runs every NinoTNC mode, ARDOP for Winlink and POCSAG paging, and shows the band in a browser. One daemon, a web GUI, one configuration file.

## Who it is for

- Operators running a node, a BBS or an APRS station who would rather have a modem on the same machine than another box on the desk.
- Anyone on HF who wants several modes sharing one SSB passband, interoperating with a NinoTNC and with QtSoundModem.
- People with no antenna up: a public web receiver is a device here, so a station can listen to a band it cannot reach.

## Install

Debian, Ubuntu or Raspberry Pi OS, on amd64, arm64 or armhf. The package is self-contained, so there is no .NET runtime to install. amd64 and arm64 want glibc 2.27 or newer (Debian 10, Ubuntu 18.04); armhf wants glibc 2.34 or newer (Debian 12, Ubuntu 22.04), so 32-bit bullseye is below the line. See [docs/01-install.md](docs/01-install.md).

```sh
curl -fsSL https://packet-net.github.io/apt/pubkey.asc | sudo gpg --dearmor -o /usr/share/keyrings/packet-net.gpg
echo "deb [signed-by=/usr/share/keyrings/packet-net.gpg] https://packet-net.github.io/apt ./" | sudo tee /etc/apt/sources.list.d/packet-net.list
sudo apt update
sudo apt install pdn-soundmodem
```

Now follow the guide. It starts at [docs/README.md](docs/README.md), and [docs/01-install.md](docs/01-install.md) covers the install in full: what it put where, how to check it worked, and what to do if it did not.

![The station page: two modems drawn over the passband, each decoded frame tagged on the burst that carried it](docs/images/waterfall.png)

## What it does

| Area | Page |
|---|---|
| Every mode, what it talks to and how well proven it is | [docs/05-modes.md](docs/05-modes.md) |
| Sound cards, CM108 interfaces, serial PTT, FlexRadio | [docs/03-radios-and-interfaces.md](docs/03-radios-and-interfaces.md) |
| Receive and transmit levels | [docs/04-levels.md](docs/04-levels.md) |
| KISS over TCP: LinBPQ, the PDN node, APRS software | [docs/06-connect-your-software.md](docs/06-connect-your-software.md) |
| ARDOP for Pat and Winlink Express | [docs/06-connect-your-software.md](docs/06-connect-your-software.md#ardop-for-pat-and-winlink-express) |
| POCSAG paging | [docs/06-connect-your-software.md](docs/06-connect-your-software.md#pocsag-paging) |
| The station page in the browser | [docs/07-station-page.md](docs/07-station-page.md) |
| HF: several modes in one passband, placed by RF frequency | [docs/08-hf.md](docs/08-hf.md) |
| Listening on a public web receiver | [docs/09-web-receivers.md](docs/09-web-receivers.md) |
| Putting your station on a public monitor site | [docs/10-public-monitor.md](docs/10-public-monitor.md) |
| The frame log, survey captures, Prometheus and Grafana | [docs/11-logging-and-metrics.md](docs/11-logging-and-metrics.md) |
| When nothing decodes | [docs/12-troubleshooting.md](docs/12-troubleshooting.md) |

## Status

Every mode in the catalogue is built and usable, and each one says how far it has been proven. Some have decoded real signals off air: AFSK 1200, AFSK 300 IL2P+CRC, the 300 and 1200 baud PSK modes, the FreeDV data modes and most of the MIL-STD-188-110D waveforms, which are also held to the standard's own fading-channel masks in simulation. Most of the rest are bench-proven in both directions against a real NinoTNC or a live QtSoundModem over a wired loop. Which level each mode has reached, and what produced the verdict, is in [docs/05-modes.md](docs/05-modes.md). ARDOP is not a catalogue mode; it is validated against ardopcf, including a Pat to Pat message exchange, and its evidence is in [PROVENANCE.md](PROVENANCE.md).

## Developers

The core is on NuGet as [`pdn-soundmodem`](https://www.nuget.org/packages/pdn-soundmodem); the assembly and namespace are `Packet.SoundModem`.

The same DSP compiled to WebAssembly is on npm as [`@packet-net/soundmodem`](https://www.npmjs.com/package/@packet-net/soundmodem), so a browser tab with a USB audio interface is a packet modem. Its own README is [web/package/README.md](web/package/README.md).

From source you need the .NET 10 SDK, and `dpkg-dev` to build a package:

```sh
dotnet build
dotnet test
packaging/build-deb.sh 0.69.0 amd64    # also arm64, armhf; cross-builds from any host
```

[docs/dev/README.md](docs/dev/README.md) is the index of developer documents: the roadmap, the validation ledger, the modem plugin contract, the bench rigs and the archive.

## Licence and credits

GPL-3.0-or-later; the text is in [COPYING](COPYING). It stays GPL because it is built on GPL prior art: UZ7HO SoundModem (Andrei Kopanchuk) through QtSoundModem (John Wiseman, G8BPQ), Dire Wolf (John Langner, WB2OSZ), MMDVM-TNC (Jonathan Naylor, G4KLX), the IL2P specification (Nino Carrillo, KK4HEJ), and ka9q_ubersdr (madpsy).

[PROVENANCE.md](PROVENANCE.md) records, component by component, what this code is based on. The sibling [packet.net](https://github.com/packet-net/packet.net) node is AGPL-3.0 and the two combine under GPLv3 section 13; nothing MIT-licensed may depend on this package.
