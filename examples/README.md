# Worked configurations

Complete, working `pdn-soundmodem` configuration files, in rough order of how much they do. Copy
one and edit it, rather than starting from a blank file. `../CONFIG.md` is the reference for every
key; these are the arrangements the keys add up to.

Every file here is loaded by the daemon's own loader in `ExampleConfigTests` and, unlike a snippet
in prose, is known to parse and to contain no setting this version does not have. That check exists
because the flagship `../soundmodem.example.json` was telling operators to give a modem its own TCP
port with `"kissPort"`, where the setting is `"port"` - silently ignored, which is exactly what an
untested example does to you.

| File | What it is |
|---|---|
| [01-ofdm-fm-minimal.json](01-ofdm-fm-minimal.json) | The smallest thing that runs OFDM-FM |
| [02-ofdm-fm-tm8100-station.json](02-ofdm-fm-tm8100-station.json) | A real station: CM108 keying, frame log, waterfall, dead-feed watch |
| [03-ofdm-fm-two-profiles.json](03-ofdm-fm-two-profiles.json) | Two OFDM-FM profiles on one radio, one with a TCP port of its own |
| [04-ofdm-fm-alongside-ninotnc.json](04-ofdm-fm-alongside-ninotnc.json) | OFDM-FM sharing a channel with the NinoTNC modes the neighbours run |
| [05-ofdm-fm-bench-no-geometry.json](05-ofdm-fm-bench-no-geometry.json) | Bench or loopback, needing nothing private installed |
| [06-ofdm-fm-monitor.json](06-ofdm-fm-monitor.json) | Receive-only monitor: no PTT, survey and raw capture on |

## What OFDM-FM needs that other modes do not

**The waveform is not in this package.** It is loaded from a path you write down in
`modemPlugins`; nothing is scanned for. See `../CONFIG.md` § `modemPlugins`.

**The channel must run at 48000 Hz.** The plugin offers every profile at one host rate and that
rate is 48 kHz, so a 12000 channel simply will not have these modes on it.

**Do not give an `ofdm-fm` modem a `frequency`.** It occupies the band its profile puts it in and
has no frequency translation of its own, so a centre is refused at start-up rather than accepted
and ignored. The same goes for `rfFrequency`, which is the same setting stated the other way round.

**The profile names come from a geometry file, which is not in this repository.** The plugin looks
for `ofdm-fm.local.json` beside its own assembly and beside the daemon, walking up the directory
tree from each, so putting it next to the `.dll` you named in `modemPlugins` is the simple answer.
With no such file the plugin still declares one mode, `ofdm-fm:synthetic`, whose geometry is
invented - enough to prove the audio path, the KISS port, keying and the plugin seam, and not the
waveform a station runs on air. That is what [05](05-ofdm-fm-bench-no-geometry.json) uses.

Start-up always tells you which modes were actually found:

```
modem plugin: ofdm-fm from /opt/pdn/plugins/M0LTE.OfdmFm.dll [ofdm-fm:synthetic, ofdm-fm:nb, ofdm-fm:enb, ofdm-fm:wb, ofdm-fm:ewb]
```

A `modems` entry naming a mode that is not in that list stops start-up, as an unknown mode. A
plugin that will not load at all is reported by name and start-up continues, because a station
should not refuse to come up over an experimental modem that is not installed.

## What was verified, and what was not

Each of these was started with the real daemon binary, on the `null` audio device, with the plugin
staged outside both repositories and the geometry file beside it. All six load the plugin, register
their modes, open their listeners and reach a running audio path; 03's per-modem port was checked
by connecting to it, and both its KISS listeners accept connections.

Two things that could not be exercised on the machine they were tested on, and are therefore taken
from the reference rather than demonstrated:

- **PTT.** There is no `/dev/hidraw0` there, so the `cm108` blocks in 02 and 04 are structurally
  valid and untried. Check yours with `--txdelay` and a receiver before trusting it.
- **Sound-card device names.** `plughw:1,0` is a plausible placeholder, not your card. `aplay -l`
  and `arecord -l` will tell you what to put there.
