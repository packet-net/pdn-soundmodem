# Files and directories

What the Debian package installs, what pdn-soundmodem reads and writes on disk, and what an upgrade, a remove or a purge leaves behind. The package is built by [`packaging/build-deb.sh`](../../packaging/build-deb.sh) and the service runs under [`packaging/pdn-soundmodem.service`](../../packaging/pdn-soundmodem.service); every path on this page is as the shipped package lays it out.

## What the package installs

| Path | What it is |
|---|---|
| `/usr/lib/pdn-soundmodem/pdn-soundmodem` | The modem itself, a self-contained single-file build. No .NET runtime is installed or needed on the machine |
| `/usr/lib/pdn-soundmodem/*.so` | Native libraries published beside the binary (`libSystem.IO.Ports.Native.so` for serial PTT, `libe_sqlite3.so` for the frame log). The modem finds them relative to its own real path, so the binary lives here and `/usr/bin` holds a symlink |
| `/usr/bin/pdn-soundmodem` | Symlink to the binary above |
| `/usr/lib/systemd/system/pdn-soundmodem.service` | The systemd unit. Enabled and started on install, restarted on upgrade |
| `/etc/systemd/system/multi-user.target.wants/pdn-soundmodem.service` | The enable symlink, created by postinst on first install. Not shipped in the package |
| `/usr/share/pdn-soundmodem/soundmodem.example.json` | The annotated example config, a copy of [`soundmodem.example.json`](../../soundmodem.example.json) from the repository. The file postinst seeds from |
| `/etc/pdn-soundmodem/` | Shipped as an empty directory, so it outlives a remove and purge has to delete it by hand |
| `/etc/pdn-soundmodem/soundmodem.json` | The station's config. Seeded by postinst from the example if absent, owned by root, mode 0644. Not a dpkg-owned file |
| `/var/lib/pdn-soundmodem/` | The state directory. Created by systemd from `StateDirectory=` at each start, owned by the service user, mode 0750 |
| `/usr/share/doc/pdn-soundmodem/copyright`, `changelog.Debian.gz` | Licence and changelog |

The seeded config names `default` as the sound card and a CM108 interface on `/dev/hidraw0`, so the first start after a fresh install fails until the file is edited; `systemctl status pdn-soundmodem` says why. The package depends on `libc6`, `libgcc-s1`, `libstdc++6`, `libasound2` (or `libasound2t64`) and `adduser`, section `hamradio`.

## What the modem reads

| Path | When |
|---|---|
| `/etc/pdn-soundmodem/soundmodem.json` | At every start, from `--config` in the unit's `ExecStart`; that flag is the only way the file is found (see [the file](config.md#the-file)) |
| `/var/lib/pdn-soundmodem/pending-config.json` | At start, if present: a one-run config left by `POST /api/config` without `?persist=true` (see [the API](ports-and-endpoints.md#the-api-under-api)). It is loaded in place of the config file and deleted as soon as it has been read, before the station is built, whether or not it parsed. The journal then says the station is running a one-run configuration |
| `/var/lib/pdn-soundmodem/mixer-state.json` | At start, on a sound card that has a mixer: the capture gain and playback level set from the station page or `/api/mixer` in an earlier run. A key set in `alsa.mixer` wins over it (see [`alsa`](config.md#alsa)). Ignored with a journal line if it was written for a different `device` or does not parse |
| `modemPlugins[].path` | At start, before the modem list is planned: each named assembly and the `.deps.json` beside it. A plugin that fails to load is reported and skipped; a modem entry naming one of its modes then fails start-up as an unknown mode. See [modem plugins](../dev/modem-plugins.md) |
| `--wav FILE`, `--wav-loop FILE` | A recording in place of live audio |
| `/usr/share/pdn-soundmodem/soundmodem.example.json` | Only to decide whether a configuration-error message can offer a `cp` of the example |

The state directory is wherever systemd's `$STATE_DIRECTORY` points, which is `/var/lib/pdn-soundmodem` under the shipped unit. Run from a terminal without it, `pending-config.json` and `mixer-state.json` sit beside the config file. `alsa.mixer.stateFile` moves the mixer state file anywhere.

Device nodes the modem opens: `/dev/snd/*` for the sound card, `/dev/ttyUSB*` or `/dev/ttyS*` for serial PTT, `/dev/hidraw*` for CM108 PTT (opened for writing). A `pipe:IN,OUT` device creates the two FIFOs if they do not exist.

## What the modem writes and when

| Path | Written when | Size cap |
|---|---|---|
| `/var/lib/pdn-soundmodem/mixer-state.json` | A mixer change from the station page or `POST /api/mixer`. Persisted unless the request says `?persist=false`; the page never sends that. Written to `.mixer-state.json.<pid>.tmp` in the same directory, then renamed over the file. Holds `device`, `writtenAt`, `captureGainDb` and `playbackDb`, and only the controls that have been set | One small JSON file |
| `/var/lib/pdn-soundmodem/pending-config.json` | `POST /api/config` without `?persist=true`. The body is written verbatim, the process exits 1 and systemd restarts it on the new document. Deleted at that start | One config document |
| `/etc/pdn-soundmodem/soundmodem.json` | `POST /api/config?persist=true` only. The body is written verbatim. Nothing else in the modem writes the config file | One config document |
| `/var/lib/pdn-soundmodem/frames.db` (`frameLog.path`) | Every frame received or sent, and one row per transmitter test, when `frameLog` is set. SQLite in WAL mode, so `frames.db-wal` (and SQLite's `frames.db-shm`) sit beside it while the modem runs; the WAL is folded into the file at a clean shutdown. The directory is created if missing. Writes that fail are dropped and counted | None; grows until you prune it |
| `<frameLog.path>/frames-<slug>.db` | A monitor site: one log per receiver inside the directory `frameLog.path` names. A monitor refuses a `frameLog.path` that names a file or ends in `.db` | None |
| `/var/lib/pdn-soundmodem/survey/` (`survey.path`) | One `<yyyyMMdd-HHmmss>-<centre>hz-<verdict>.wav` and a `.json` sidecar per kept burst, when `survey` is set. Oldest deleted first, by name, when the directory is over budget. Served back at `/survey/<file>` on the page port (see [Routes](ports-and-endpoints.md#routes)) | `survey.maxBytes`, 512 MiB by default; at most `survey.maxPerHour` (30) captures an hour |
| `/var/lib/pdn-soundmodem/raw/` (`rawCapture.path`) | `raw-<yyyyMMddTHHmmssZ>.wav` chunks of `rawCapture.chunkMinutes` (15) of receive audio each, when `rawCapture` is set. Oldest deleted first when over budget; the chunk being written is never deleted | `rawCapture.maxBytes`, 4 GiB by default |
| `$TMPDIR/pdn-soundmodem-proposed-<id>.json`, `/tmp` under the unit | Every `POST /api/config`, to validate the proposed document through the start-up parser. Deleted when the request is answered | Transient |
| The FIFOs named in `pipe:IN,OUT` | At start, if absent. Mode 0666 before the umask | |

The mixer state file is the only file written without a config section asking for it. `--mixer-show` and `--uplink-token` write nothing. `--two-tone` and `--tone` build the station as configured, so a config with `frameLog` gets one row per test, and one with `survey` or `rawCapture` has the directory created and, for `rawCapture`, a chunk written while the test runs. The journal is stdout and stderr under systemd; the modem keeps no log file of its own.

## Ownership and permissions

| Item | Value |
|---|---|
| Service user | `pdn-soundmodem`, a system user with no home directory, created by postinst with `adduser --system --no-create-home --group` |
| Supplementary groups | `audio` for `/dev/snd/*`; `dialout` for serial PTT on `/dev/ttyUSB*` and `/dev/ttyS*` |
| `/var/lib/pdn-soundmodem` | `StateDirectory=pdn-soundmodem`, `StateDirectoryMode=0750`, owned by the service user; created by systemd at every start |
| `/etc/pdn-soundmodem/soundmodem.json` | root, 0644, readable by the service. `ReadWritePaths=/etc/pdn-soundmodem` lifts `ProtectSystem=full` for that one directory so a `?persist=true` write is not blocked by the unit |
| Hardening | `NoNewPrivileges=true`, `ProtectHome=true`, `ProtectSystem=full` |
| Realtime priority | `LimitRTPRIO=10` permits realtime scheduling up to priority 10; nothing in the modem asks for it, so the line has no effect |
| Start condition | `ConditionPathExists=/etc/pdn-soundmodem/soundmodem.json`: the unit does not start at all without the config file |
| Exit codes | `Restart=on-failure` with `RestartPreventExitStatus=2`: exit 2 (the configuration is wrong) is not retried, so the journal carries one explanation; any other non-zero exit, or a crash, restarts after `RestartSec=5`; exit 0 stays stopped. The config API's restart exits 1. The codes are listed in the [command-line reference](command-line.md#exit-codes) |

The seeded config file is owned by root, and `ReadWritePaths` only removes the read-only mount. A `?persist=true` write from the service therefore fails with a permission error until the file is made writable by the service user, for example `chown pdn-soundmodem /etc/pdn-soundmodem/soundmodem.json`. Neither the package nor the unit does this. The 500 the modem answers with blames `ProtectSystem=full` and suggests adding `ReadWritePaths=/etc/pdn-soundmodem`, which the shipped unit already has; the file's ownership is the cause.

`/dev/hidraw*` is root-only by default and CM108 PTT opens it for writing, so the service user needs a udev rule granting the `audio` group access to the interface's node, in `/etc/udev/rules.d/99-pdn-soundmodem-cm108.rules`, with the interface's vendor and product IDs (`0d8c:013c` is a common C-Media interface):

```
KERNEL=="hidraw*", ATTRS{idVendor}=="0d8c", ATTRS{idProduct}=="013c", MODE="0660", GROUP="audio"
```

The rule takes effect once udev has reloaded its rules and the interface has been replugged. `cat /sys/class/hidraw/*/device/uevent` maps each `hidraw` node to its USB IDs (`HID_ID=0003:00000D8C:00000012` for the C-Media interface on the bench); the number moves with what else is plugged in. Serial PTT needs no rule because the unit already joins `dialout`. The [Tait TM8100 page](../hardware/tait-tm8100-cm108.md) covers one CM108 interface in detail.

## Upgrade, remove and purge

| Action | Removed | Kept |
|---|---|---|
| Upgrade (install a newer `.deb`) | The old binary, shims, unit, example config and doc files, each replaced by the new one | `/etc/pdn-soundmodem/soundmodem.json` (postinst seeds only when the file is absent), `/var/lib/pdn-soundmodem` and everything in it, the service user, the unit's enablement. The unit is restarted if it is enabled or running |
| Remove (`apt remove`) | Binary, shims, symlink, unit, example config, doc directory. The unit is stopped by prerm and masked by postrm | `/etc/pdn-soundmodem/soundmodem.json` and its directory, `/var/lib/pdn-soundmodem` and everything in it, the service user |
| Purge (`apt purge`) | Everything remove removes, plus `/etc/pdn-soundmodem/soundmodem.json`, `/etc/pdn-soundmodem` if then empty, the unit's enable symlinks and mask, and the `pdn-soundmodem` user and its group (the group stays if another user has been added to it) | `/var/lib/pdn-soundmodem` and everything in it: the frame log, survey captures, raw captures and the mixer state file |

No maintainer script touches `/var/lib/pdn-soundmodem`; delete it by hand after a purge if you want it gone. A reinstall of the same version keeps the unit enabled. Postinst does not re-enable a unit an operator has disabled; it records the symlinks so purge can clean them up.

## Copying and backing up

| What | How |
|---|---|
| The station's configuration | `/etc/pdn-soundmodem/soundmodem.json`, plus `/var/lib/pdn-soundmodem/mixer-state.json` if a level was set from the page rather than the file |
| The frame log while the modem runs | Copy `frames.db`, `frames.db-wal` and `frames.db-shm` together, or read it in place with `sqlite3`. A copy of `frames.db` alone taken mid-run is missing everything still in the WAL |
| The frame log after a clean stop | `frames.db` alone; the WAL has been folded in |
| Survey and raw captures | Plain WAV and JSON files; copy the directory |
| The journal | `journalctl -u pdn-soundmodem`; the modem writes no log file |

## The station page assets

The station page and the monitor picker are two HTML files embedded in the binary as resources (`waterfall.html` and `monitor.html`) and served by the modem's own HTTP listener on the page port. Nothing is installed for them: there is no web root, no directory to serve and no page state on the machine. Display settings (dial, sideband, span, levels, volume) are kept per browser. The `web/` folder in the repository is the separate WebAssembly package and is not part of the `.deb`.

Related: [configuration reference](config.md), [command-line reference](command-line.md), [ports and endpoints](ports-and-endpoints.md).
