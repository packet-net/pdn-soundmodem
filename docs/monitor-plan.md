# monitor.ukpacketradio.network - one deployment, many UberSDR receivers, a picker

**Status: deployed, 2026-09-03.** Live at https://monitor.ukpacketradio.network, from CT 146 `monitor` on proxmox1 running pdn-soundmodem v0.55.0; the picker, `/api/instances`, a receiver page and a real WebSocket through the tunnel were all checked, and two receivers were watched at once with one session each. Left to do: an overnight soak, a look at the picker in a real browser, and a word with the busiest receivers' operators about the per-address allowance (4.6) before announcing. The successor to [docs/40m-monitor-plan.md](40m-monitor-plan.md), which shipped in v0.54.0/v0.54.1 and ran at https://m9psy-1-monitor.ukpacketradio.network from CT 146 on proxmox1, one receiver per deployment, until this replaced it. Tom has asked for the bigger version: one deployment at https://monitor.ukpacketradio.network with a front page that lists the UberSDR receivers from the public directory, a visitor picks one, and the daemon holds at most one session on each receiver however many people are watching it. The single-receiver flavour does not go away.

Tom, 2026-09-03, and this is the constraint everything else bends around:

> I don't want to lose the current deployment model - it's strictly the same code base, two flavours via configuration.

So: one binary, one package, one set of tests. **Flavour A** is what ships today, `"device": "ubersdr:<host>"` with `"ubersdr": { "onDemand": true }`, one receiver per process, and it must behave *identically* afterwards, including on the live node stations that use ALSA and Flex devices and have never heard of a web receiver. **Flavour B** is a new `"monitor"` config section that turns the same daemon into the many-receiver picker.

This document is written to be executed by sub-agents working one phase at a time under a coordinator, so each phase in section 6 is a self-contained brief: a fresh engineer with this file and the repo and nothing else should be able to do the work. Section 7 is the checklist the coordinator keeps up to date.

## 1. What it is

One pdn-soundmodem process serving one public site. The front page lists the UberSDR receivers that could show the 40 m packet window, fetched live from https://instances.ubersdr.org/api/instances and decorated with what this daemon knows about each of them. A visitor clicks one and gets today's page: the waterfall, the AX.25 links panel, the decoded-frame log and the browser audio, for that receiver.

Behind the page, per receiver: one `SoundModemChannel` with the configured modems, one frame log, one links observer, one on-demand UberSDR input, one receive loop. The receiver's session exists only while at least one browser is watching *that* receiver, and is dropped 60 s after the last one leaves, the same linger flavour A uses. Ten visitors on one receiver cost that receiver one session. A receiver nobody has picked costs it nothing at all, and costs this process a few MB of idle objects.

Receive only, as before. The daemon transmits nothing, and on a web receiver it could not.

## 2. What it is not

- **Not a replacement for flavour A.** Same binary, same `.deb`, same tests. `"device"` and `"monitor"` are mutually exclusive and the daemon says so at start-up; everything else about flavour A is untouched. The live stations on ALSA and Flex must not be able to tell that any of this happened.
- **Not a KISS endpoint, not an operator console.** Flavour B configures no KISS, no transmit, no config API, no survey, no paging, no ARDOP host. Those stay wired for flavour A and are simply not reachable in flavour B.
- **Not a receiver aggregator.** Nothing is combined across receivers: no diversity, no dedupe, no "heard by three sites" view. Each receiver is its own independent monitor that happens to share a process and a hostname. Cross-receiver correlation is a different project and would want a different data model.
- **Not a directory mirror.** The daemon shows what the directory says, filtered; it does not curate, rank or cache anything for anybody else, and it does not publish an instance list of its own.
- **Not one page per receiver on its own port.** One port, one front door, receivers under path prefixes. Fifty listeners on fifty ports behind one tunnel is not a deployment anybody wants to operate.

## 3. What already exists (verified 2026-09-03 against `main` at v0.54.1)

### 3.1 The library is already multi-instance clean

This was the open question and the answer is good. Nothing in `src/Packet.SoundModem/` assumes one of anything.

- `SoundModemChannel` (`src/Packet.SoundModem/Channel/SoundModemChannel.cs`, 1017 lines) is per-instance throughout: constructor `SoundModemChannel(int)` at :94, `SampleRate` as an instance property at :114, the modem dictionary `_modems` at :52 exposed as `Modems` at :152, `AddModem` at :155. Its only statics are immutable helpers (`ResolveTrim` :297, `InhibitPollInterval` :565, `MinimumSchedulerWait` :678).
- One wart: `AddReceiveTap` at :189 appends to `_receiveTaps` (:53) and there is no `RemoveReceiveTap`. `WaterfallWebServer.DisposeAsync` (:1901) unsubscribes the four channel events it subscribed to but cannot take its tap back. Harmless in both flavours as designed here, because a receiver's channel and its waterfall server are created together and live as long as each other; it becomes a leak only if somebody later restarts a receiver's waterfall server against a channel that outlives it. Phase 1 should not add that capability speculatively, but it should not build anything that depends on being able to.
- `WaterfallWebServer` (`src/Packet.SoundModem/Waterfall/WaterfallWebServer.cs`, 1945 lines) holds everything per instance: `_channel`, `_source`, `_clients`, `Links` (:274), the config message built per instance in `BuildConfigMessage` (:671), the frame backlog taken as a delegate (`WaterfallOptions.FrameHistory` :71), the receiver credit set by `SetReceiver(description, url)` (:474). Its statics are immutable: `Json` (:180), `LinkExpiryPeriod` (:288) and `Page` (:1884), a `Lazy` that loads the embedded HTML once and stamps a version hash. The tests already build twenty of these in one process on free ports (`tests/Packet.SoundModem.Tests/Waterfall/WaterfallWebServerTests.cs` and `WaterfallPageTests.cs`), so N-in-a-process is not a new claim, it is a tested one.
- The one thing `WaterfallWebServer` owns that it should not, for our purposes, is a port. The constructor at :298 builds its own `HttpListener` (:304) with the single prefix `http://{bind|+}:{port}/` (:309), started at :640, accepted in `AcceptLoopAsync` (:1452), closed at :1921. Six lines. There is no path-prefix parameter, and `ServeAsync` (:1474) routes on absolute paths: **any** WebSocket upgrade at **any** path is accepted first (:1478), then `/api/*` to the installed `ApiHandler`, then `/metrics` and `/metrics/frames`, then `/`, `/index.html` and `/links` serve the embedded page, then `/survey/<file>`, else 404.
- Every hot path in that class dereferences `_channel`, `_source` and `_clients` singly. **One server per receiver behind a router is the design; one server multiplexing many sources is not.** The second would mean threading a source identity through the spectrum callback, the frame callbacks, the link observer, the audio fan-out and the config message, and would leave a class that is currently understandable in one sitting unreadable.
- `waterfall.html` (`src/Packet.SoundModem/Waterfall/wwwroot/waterfall.html`, 1717 lines, self-contained, embedded resource) has exactly four absolute URLs: the WebSocket at :1627, two `href="/survey/..."` at :1146 and :1147, and `window.open("/links", ...)` at :1563. It reads its own path once, `location.pathname === "/links"` at :1160, to decide whether it is the torn-off links window. It also puts `location.host` into the share text at :1453, which is display only. No other assets, no `/api` or `/metrics` use, no framework, no build step. The page-version auto-reload keys `sessionStorage["pdnsm-reloaded"]` on the served hash at :499, which is per origin and therefore shared by every receiver under one hostname; see 4.2.
- The page has no notion of choosing a source. Its only receiver-identity fields are the public credit block, `cfg.publicMonitor`, `cfg.title`, `cfg.about`, `cfg.receiver` and `cfg.receiverUrl`, rendered at :610-623 and consulted again at :807.
- `UberSdrAudioInput` (`src/Packet.SoundModem/UberSdr/UberSdrAudioInput.cs`, 790 lines) keeps all session state per instance and owns its own `ClientWebSocket`. It has one process-global: `private static readonly HttpClient Http` at :47, used for the `POST /connection` pre-flight (:686) and `GET /api/description` (:732). Functionally fine for N hosts, but it is constructed with a timeout and nothing else, so it has the default infinite `PooledConnectionLifetime` and pins DNS results for the life of the process. That did not matter when the host came from a config file and the process was restarted to change it. It matters when hosts come from a directory that can move them.
- `OnDemandUberSdrInput` (`src/Packet.SoundModem/UberSdr/OnDemandUberSdrInput.cs`, 503 lines) has no mutable statics at all. Phases Idle, Connecting, Live, Lingering, Retrying; `SetViewers(int)` at :196; `PhaseChanged` at :99; `Status` at :128. It is wired in `Program.cs` at :2209-2210, `PhaseChanged` into `SetRadioStatus` and `ViewersChanged` into `SetViewers`. This class is exactly the shape flavour B needs and needs no changes.
- `UberSdrEndpoint` (`src/Packet.SoundModem/UberSdr/UberSdrDevice.cs`:10) is `record struct (string Host, int Port, bool Ssl)`, which is precisely the directory's `host`, `port` and `tls`. The directory client does not need to build device strings and re-parse them.
- The only mutable static in the whole library is `ModemPluginRegistry` (`src/Packet.SoundModem/Modems/ModemPluginRegistry.cs`:23-24), a process-wide plugin catalogue behind a lock with an immutable snapshot. Process-wide is the right scope for it; every receiver runs the same modem set anyway.
- The library uses `Console` nowhere. It logs through injected `Action<string>? log` delegates, which is what makes a per-receiver journal prefix a one-line change rather than a hunt.

### 3.2 The cost is concentrated in the daemon

`src/Packet.SoundModem.Daemon/Program.cs` is 2988 lines of top-level statements in one flat scope with no `Station` or `Receiver` type. Everything is created exactly once and named by a local: `channel` (:666), `frameLog` (:699), `waterfallServer` (:1219-1222), `SignalSurvey` (:1414), `StationTelemetry` (:1553), `RawCaptureWriter` (:1586), `ConfigApi` (:1766), the single `IAudioInput input` (declared :2099) chosen by a five-way if/else on the device string (pipe :2106, wav-loop :2135, ubersdr :2150, flex :2268, ALSA :2534), `XrunWatch` (:2702), `DeadFeedWatch` (:2747), `StarvationWatch` (:2783), one `CancellationTokenSource cancellation` (:1742) that every fault path cancels, and one receive loop at :2842 whose dead-feed, starvation, xrun and drop counters are loop-scope locals. The process exits at :2982, `return radioLost || restartRequested ? 1 : 0`.

Two things in there are process-shaped rather than receiver-shaped and have to change:

- The starvation watch's stalled-shutdown path calls `Environment.Exit(1)` at :2834. In flavour B one wedged receiver must not take the other forty-nine down.
- There are 46 bare `return 1;` / `return 2;` sites and 155 `Console.Write*` / `Console.Error.Write*` calls with no receiver prefix. Most of the returns are start-up validation and stay exactly where they are; the ones inside the per-receiver body have to become a fault on that receiver.
- `FrameLog` is **not** in the library. It is `src/Packet.SoundModem.Daemon/FrameLog.cs`:24, `internal sealed`, one SQLite connection and one writer thread per instance, opened at Program.cs:699 and wired to `channel.FrameReceivedWithQuality` at :711-716. Per-instance is fine, with one snag: `DisposeAsync` calls `SqliteConnection.ClearAllPools()` at :455, which is process-global and would clear every other receiver's pool too. Only reached at shutdown as designed here, but it is a footgun if receivers ever become disposable.

Rough size of the per-receiver body, once transmit, PTT, KISS, ARDOP, paging, ident, Flex and ALSA are set aside: **500 to 700 lines**, spread over the channel and modem construction (:527-1215), the waterfall server (:1216-1305), the UberSDR open (:2150-2267) and the receive loop with its watches (:2694-2957).

### 3.3 What is greenfield

Nothing anywhere in the tree fetches or parses the UberSDR directory. The directory client, its DTOs, its cache, the picker page and the front-door router are all new.

The directory itself, fetched 2026-09-03 (`GET https://instances.ubersdr.org/api/instances`), is `{"count": 51, "instances": [...]}` with, per instance and using the real field names:

| Field | Type | Use here |
|---|---|---|
| `id` | uuid string | Stable identity; not used in URLs (see the slug rule in 4.2) |
| `host` | string | The connection host, e.g. `m9psy-1.instance.ubersdr.org`. Unique across the list |
| `port` | int | 443 on 48 of 51; 80, 8080 and 9080 on the rest |
| `tls` | bool, **often absent** | Present and true on 48 of 51; **missing entirely** on `pjmarsh.co.uk`, `sdr.meucorp.net` and `na5b.com`. Absent means plain HTTP. `UberSdrEndpoint(host, port, tls)` takes it directly |
| `is_online` | bool | First filter |
| `available_clients` / `max_clients` | int | Free slots now and in total; a receiver with none is listed as full, not offered |
| `public_iq_modes` | string array | `iq48` on all 51; `iq96`, `iq192` and `iq384` also appear. Must contain the mode the daemon asks for |
| `tuning_range` | object | `min_frequency`, `max_frequency`, `spectrum_span_hz`, `spectrum_center_hz`, `input_samprate`, `samprate_source`, `reported`. **`reported` is false on 6 of 51**, where `samprate_source` is `"fallback"` and the range is a placeholder 10 kHz to 30 MHz. Filter on the range, but treat an unreported range as "probably fine" rather than as fact |
| `callsign` | string | The picker's primary label. Unique across all 51 today, but not guaranteed |
| `name` | string | The receiver's own description of itself, e.g. "SDR with Active Loop" |
| `location` | string | Free text, e.g. "Reading, England, UK". Also `country_code`, `country_name`, `maidenhead`, `latitude`, `longitude` |
| `public_url` | string | The receiver's own web page, for the credit link |
| `max_session_time` | int seconds | The hourly rollover the input already rides. **Not always 3600**: the 51 report 0, 1800, 3600, 7200, 10800, 14400, 21600 and 43200 |
| `snr_0_30_mhz`, `snr_1_8_30_mhz` | int dB | The receiver's own reported signal quality, ranging -1 to 50 across the list. **This is the number to show, not `noise_floor`** |
| `noise_floor` | bool | A capability flag, true on all 51. It is *not* a level. The 40m plan's shorthand "noise floor" meant this number, which is actually `snr_0_30_mhz` |
| `load_status` | string | `ok` on 40, `warning` on 10, `critical` on 1. Worth showing, not worth filtering on |
| `antenna_connected` | bool | False on 1 of 51. A useful filter |
| `last_seen`, `last_report_age_seconds` | timestamp, int | How stale the directory's own view is |
| `version`, `uptime`, `cpu_model`, `distance`, `bearing_degrees`, `addons`, `chat_enabled`, `cw_skimmer`, `digital_decodes`, `cors_enabled`, `pskreporter_rank`, `ssb_predictions`, `frontend` | mixed | Not used. `pskreporter_rank`, `ssb_predictions`, `frontend`, `dsp_enabled`, `gpsdo_enabled` and several others are absent on some instances, so every DTO field outside the table above should be optional |

The practical filter today keeps all 51: every one is online, has a free slot, offers `iq48` and claims to cover 7051 kHz. That is the happy case and it will not last; the filter earns its keep on the day six of them are down.

## 4. What to build

### 4.1 A `Station` object, carved out of Program.cs

**The name.** Call it `Station`, in `src/Packet.SoundModem.Daemon/Station.cs`. The codebase already uses "station" for exactly this bundle: `StationTelemetry` (Program.cs:1553), `FlexTuning.StationName` (:345), and the daemon's own message at :1707, "a receive-only station". "Receiver" is the one name it must not have, because in this vocabulary a receiver is the remote UberSDR box: `WaterfallWebServer.SetReceiver`, `UberSdrAudioInput.ReceiverDescription`, and the page's `cfg.receiver` and `cfg.receiverUrl`. Flavour A runs one `Station`; flavour B runs N.

One `Station` owns:

- one `SoundModemChannel` with its modems, built from the same band plan every station in the process shares (flavour B gives every receiver the same modem list, so the plan is computed once and the dial is the same everywhere);
- one `FrameLog`, its own SQLite file, `frames-<slug>.db` under `frameLog.path`, using the existing per-instance class unchanged;
- one `WaterfallWebServer`, and therefore one `Ax25LinkObserver` (`Links`, WaterfallWebServer.cs:274);
- one `OnDemandUberSdrInput`, wired to that server's `ViewersChanged` and `PhaseChanged` exactly as Program.cs:2209-2210 does today;
- one receive loop with its `DeadFeedWatch`, `StarvationWatch` and `XrunWatch`, and its own `CancellationTokenSource`;
- a tag on every journal line it writes, the slug, so fifty stations in one journal are readable: `m9psy-1: ubersdr: live, 2 viewers: ...`. Flavour A writes no tag, so its journal lines are byte-identical to today's;
- a fault model that is **"this station is down and says so"**: a `Faulted` event carrying a sentence, the sentence shown in the page's radio status and in the picker, and no call to `Environment.Exit` and no process exit from anywhere inside it.

Flavour A's process-level behaviour is preserved by the host, not by the station: the daemon subscribes to the single station's `Faulted`, journals it and exits 1, which is what `radioLost` does today at Program.cs:2982 and what `Restart=on-failure` reopens. Flavour B subscribes to the same event and updates one row of a table.

**One implementation, no drift.** Flavour A's on-demand UberSDR path and flavour B must run the *same* receive loop and the *same* watches. A forked copy is the failure mode this whole design exists to avoid, and it would be invisible until the two flavours disagreed about a dead feed at 3 a.m. Whether the extraction also moves the pipe, wav-loop, Flex and ALSA devices onto `Station` is the implementer's judgement. The rules are, in order: one implementation of the loop; flavour A behaviour identical. If the only way to avoid a second copy of the loop is to put all five device kinds behind it, then do that, and lean on the full suite as the safety net.

### 4.2 A front door on one port

Flavour B opens one `HttpListener` on `waterfall.port` and routes:

- `/` and `/index.html`: the picker page (4.4).
- `/api/instances`: a JSON snapshot of the filtered directory, each entry decorated with this daemon's own view of that receiver: `state` (one of `unpicked`, `idle`, `connecting`, `live`, `lingering`, `refused`, `faulted`), `viewers`, and the receiver description the input learned from `GET /api/description` once a session has ever been opened. Polled by the picker.
- `/r/<slug>/...`: everything under the prefix goes to that receiver's `WaterfallWebServer`, including the WebSocket upgrade at `/r/<slug>/ws` and the links window at `/r/<slug>/links`.
- Anything else: 404.

**`WaterfallWebServer` has to be able to serve under a path prefix.** Two ways to get there:

1. **A prefix parameter on the existing listener.** Add `string pathBase = "/"` to the constructor, prepend it to the `HttpListener` prefix at :309 and strip it before the routing comparisons in `ServeAsync` (:1474). Small diff. But every receiver still owns a port, so the front door has to reverse-proxy to fifty loopback ports, which is fifty listeners, fifty sockets to bind and a proxy hop for every waterfall frame and audio packet.
2. **A "serve this context" entry point.** Make the listener optional: a second constructor or a factory that takes no port, plus `Task<bool> TryServeAsync(HttpListenerContext context, string pathBase)` that does what `ServeAsync` does after stripping `pathBase`. The router owns the one listener, calls `Start()` on each server for the parts that are not the listener (the band probe, the channel subscriptions, the link expiry timer at :638), and dispatches contexts by prefix.

**Recommended: option 2.** The front door owns the port, which is the thing that actually has to be single, and the receivers stop owning something they should never have owned. There is no proxy hop, no second socket per viewer, and nothing has to be reconciled when a WebSocket upgrade arrives. The listener surface inside `WaterfallWebServer` is six lines (:184, :304, :309, :640, :1459, :1921-1922), so the change is contained, and flavour A keeps the existing constructor and the existing `AcceptLoopAsync` untouched. The cost is that `Start()` splits into "prepare" and "listen", which needs care because `Start()` currently both subscribes to the channel and starts the listener in one method (:569-641).

**The page's four absolute URLs become relative to a base.** The base is derived once at load from `location.pathname`: everything up to and including the last `/`, so `/r/m9psy-1/` under flavour B and `/` under flavour A. Then the WebSocket is `${base}ws`, the survey links are `${base}survey/...`, the links window is `${base}links`, and the "am I the links window" test at :1160 becomes "does `location.pathname` end with `/links`". Flavour A's base is `/` and every one of those resolves to exactly what it resolves to today, which is what makes the existing page tests the regression test.

**One consequence of sharing an origin:** the page-version `sessionStorage` key at :499 is per origin, so under flavour B all receivers share it. That is correct, not a bug: they are all the same page from the same binary and one reload settles all of them. Do not key it per receiver.

**The slug.** `<slug>` is derived from the directory's `host`, because `host` is the only field the directory guarantees unique (verified: no duplicates in the 51) and because a URL a visitor bookmarks must not change when an unrelated instance appears. The rule: lower-case the host; strip a trailing `.tunnel.ubersdr.org` or `.instance.ubersdr.org`; replace every run of characters outside `[a-z0-9-]` with a single `-`; trim leading and trailing `-`. That gives `m9psy-1`, `rocksdr`, `g4eyr`, `reading-ubersdr-m0lte-uk`, `websdr-heppen-be`. Applied to the current 51 it produces 51 distinct slugs. If two ever collide, both fall back to the full sanitised host and the daemon says so on a journal line.

The obvious alternatives were checked against the real data and rejected. **Callsign** is unique across the 51 today and would read better, but the directory does not promise it and a collision would have to be broken by something order-dependent, which breaks a bookmark. **The host's first label** looks tempting and is wrong: `websdr` appears twice (`websdr.heppen.be`, `websdr.lumpkinschools.com`) and `ubersdr` four times (`ubersdr.k3fef.com`, `ubersdr.k3gmq.com`, `ubersdr.k1ra.us`, `ubersdr.pt2fhc.org`). The slug is ugly for the fifteen instances not on an ubersdr.org tunnel; the picker shows the callsign and the location, so the slug is only ever seen in the address bar.

**The receiver page in flavour B gets one addition:** a "back to the receivers" link next to the credit block, pointing at `../`. Under flavour A there is nothing to go back to, so it is not rendered; the existing `cfg.publicMonitor` block at :610-623 is the place for it and a new config field (say `cfg.pickerUrl`, null under flavour A) is the switch.

### 4.3 The directory client

A small class, `UberSdrDirectory`, in the daemon next to `Station`. It fetches `monitor.directory` on a timer (`refreshMinutes`, default 5) and holds the last good result.

- **Tolerant of the directory being down.** A failed fetch keeps the previous list, journals one line, and sets a flag the picker renders as "the receiver directory is unreachable; this list is from HH:MM". A cold start with no list yet shows an empty picker and says why. The directory going away must never take a live session down.
- **Filters,** in this order: `is_online`; `available_clients > 0`; `public_iq_modes` contains the mode the daemon uses (`ubersdr.mode`, default `iq48`); `antenna_connected` is not false; and, when `tuning_range.reported` is true, `min_frequency <= window <= max_frequency` for the RF window the configured modems occupy. An unreported tuning range passes the last check, because the placeholder value 10 kHz to 30 MHz is a fallback and not a claim.
- **`allow` and `deny`,** matched on `host`, case-insensitive. `deny` always wins. A non-empty `allow` means only those hosts, which is how you run the picker against two receivers for a smoke test. This is the mechanism by which an operator who asks not to be listed is not listed, so it is not optional and it is tested.
- **Stations are created lazily,** on the first pick of a receiver, and then kept for the life of the process. An idle station is a channel, a few modems, a frame log and a waterfall server with no clients, measured in single-digit MB (to be confirmed in Phase 3). The thing that costs the remote operator anything is the session, and the session only exists while somebody is watching. Keeping the station means the links panel and the frame log survive a visitor leaving and coming back, which is the whole reason a quiet band looks alive.
- **A station whose receiver leaves the directory** keeps working if somebody is watching it and stops being offered on the picker. It is not torn down mid-view because a refresh went one way.
- **`PooledConnectionLifetime`.** The static `HttpClient` at `UberSdrAudioInput.cs`:47 gets `new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }`. Directory-supplied hostnames are exactly the case the default infinite lifetime gets wrong: a tunnel host moves, and a process that has been up for a week keeps dialling an address nobody is listening on. The directory client uses its own `HttpClient` with the same setting.

### 4.4 The picker page

A new embedded resource, `src/Packet.SoundModem/Waterfall/wwwroot/monitor.html`, self-contained in exactly the way `waterfall.html` is: one file, no framework, no build step, plain ASCII, served from the assembly and stamped with a version hash the same way `Page` does at WaterfallWebServer.cs:1884.

- Title and about paragraph from `waterfall.title` and `waterfall.about`, so the two flavours are configured the same way.
- One row per receiver: callsign, name, location, state, viewer count, `snr_0_30_mhz` as a signal figure where the directory gives one, free slots as `available_clients` of `max_clients`, and a link to `public_url` for the receiver's own page. The row itself links to `/r/<slug>/`.
- Sorted live first, then by callsign. A visitor should land on the page and immediately see where the other visitors already are.
- Polls `/api/instances` every 10 s. No WebSocket: the picker is a table that changes once a minute, and a socket per idle browser tab is a cost with no return.
- Says the awkward things plainly: "the receiver directory is unreachable, this list is from 19:42"; "full" for `available_clients == 0`; "this receiver's daily allowance for this monitor is used up, back tomorrow" when a station's on-demand input is in the refused phase, which is the same sentence `OnDemandUberSdrInput` already produces for the waterfall page.
- No map, no flags, no images. It is a list.

### 4.5 Config, flavour B

```json
{
  "monitor": {
    "directory": "https://instances.ubersdr.org/api/instances",
    "refreshMinutes": 5,
    "lingerSeconds": 60,
    "allow": [],
    "deny": [],
    "modems": [
      { "subChannel": 0, "mode": "afsk300-il2pc",           "rfFrequency": 7050300 },
      { "subChannel": 1, "mode": "ardop", "bandwidth": 500, "rfFrequency": 7050950 },
      { "subChannel": 2, "mode": "bpsk300",                 "rfFrequency": 7051600 }
    ]
  },
  "frameLog": { "path": "/var/lib/pdn-soundmodem" },
  "waterfall": {
    "port": 8099,
    "public": true,
    "title": "UK packet monitor",
    "about": "The 7050-7052 kHz packet window on 40 m, as heard by public web receivers. Pick a receiver to watch. Receive only: this site decodes what it hears and shows the AX.25 links and frames; nothing is transmitted."
  },
  "bind": "127.0.0.1"
}
```

- `monitor` and `device` are **mutually exclusive**. Both set is exit 2 with a message naming both and saying to remove one; neither set is the existing behaviour, unchanged.
- `monitor.modems` is the same modem schema as the top-level `modems`, applied to every receiver. It lives inside `monitor` rather than at the top level so that a config file cannot half-describe a station in one flavour and a monitor in the other.
- `frameLog.path` is a **directory** in flavour B and a **file** in flavour A. The daemon creates the directory if it can and writes `frames-<slug>.db` per station. Getting this wrong should be an exit 2 with the reason, not a silent SQLite error.
- `ubersdr` is still honoured in flavour B for the stream parameters it owns (`mode`, `ssbLowHz`, `ssbHighHz`, `startupGuardMs`, `gain`); `onDemand` is implied true and `lingerSeconds` comes from `monitor.lingerSeconds`.
- Start-up validation, all exit 2: `monitor.modems` non-empty; `waterfall` present with a `port`; `waterfall.public` implied true and forced, because a picker on a LAN dressed as an operator console is nobody's requirement; `refreshMinutes` and `lingerSeconds` non-negative; `directory` a valid absolute http or https URL; every entry in `allow`/`deny` a plausible hostname.
- Documented in `CONFIG.md` with its own `## monitor` section and a cross-reference from `## waterfall` and `## ubersdr`, plus a commented flavour-B block in `soundmodem.example.json`. Phase 3.

### 4.6 Etiquette and limits

This is section 4.4 of the 40m plan, carried forward and made harder by there being fifty operators rather than one.

- **One session per receiver, however many viewers.** The fan-out is in this daemon; each receiver sees one client. This is the single most important promise the design makes to the people whose antennas these are, and it is a test, not a hope: two WebSockets on one receiver, one `OpenAsync`.
- **The per-address daily allowance now applies per receiver**, and this deployment has one egress address. Fifty receivers means fifty independent allowances, each of which this site can exhaust on its own. The picker says so per receiver, in the receiver's own row, and the receiver page says so in its status chip. Nobody should have to guess why a receiver went quiet.
- **`allow` and `deny` are how an operator's wishes are honoured.** If an operator asks to be left out, they go in `deny` and are gone from the picker within `refreshMinutes`. That is the answer to give when asked, and it should be given before anyone has to ask.
- **The linger avoids churn.** 60 s, as flavour A. A refresh, a tab switch or a flaky mobile connection must not cost a receiver a tear-down and rebuild.
- **The hourly rollover is the receiver's**, already handled, and `max_session_time` varies from 30 minutes to 12 hours across the directory, so nothing may assume 3600.

## 5. Deployment

**Deployed 2026-09-03.** Live at https://monitor.ukpacketradio.network. What follows is what it actually is; the sketch it was planned from is kept below it, because two of its bullets were answered differently.

### 5.1 As deployed

- **Container**: CT 146 on proxmox1 (`root@10.45.0.10`), hostname `monitor`, IP 10.45.0.128. **Repurposed rather than built new**: it is the container that served `m9psy-1-monitor.ukpacketradio.network`, which is retired (see [docs/40m-monitor-plan.md](40m-monitor-plan.md)). Debian 13, unprivileged, 2 cores, **3072 MB** RAM, 512 MB swap, 8 GB rootfs on `local-zfs`, `keyctl=1,nesting=1`, `net0` on `vmbr0` by DHCP, `onboot: 1`, `unattended-upgrades` already covering Debian security and the cloudflared repo. 3 GB rather than 1 GB because a station costs about 31 MB and stations are never torn down, so the container has to hold every receiver anybody might pick, not the ones you expect them to.
- **Daemon**: pdn-soundmodem **0.55.0** from the release `.deb`, installed with `apt-get install`, running under the shipped `pdn-soundmodem.service` as user `pdn-soundmodem` with `StateDirectory=pdn-soundmodem`. Config at `/etc/pdn-soundmodem/soundmodem.json`: the flavour-B block of 4.5 verbatim, three modems, `frameLog.path` the directory `/var/lib/pdn-soundmodem`, `waterfall.port` 8099, `bind` 127.0.0.1, no `allow` and no `deny`. At start-up it listed **48 of the 50** receivers the directory offered, and 49 of 50 at the next refresh.
- **Tunnel**: the retired site's tunnel, **renamed** `m9psy-1-monitor` -> `monitor`, id `eb2f3d02-eca0-455d-9b0a-910ae8be473f`, remotely managed, `cloudflared` in the container under `/etc/systemd/system/cloudflared.service` as user `cloudflared`. A rename does not change the tunnel's token, so `/etc/cloudflared/tunnel.env` was left alone. Ingress `monitor.ukpacketradio.network -> http://127.0.0.1:8099` with `originRequest.httpHostHeader` set to `127.0.0.1:8099` and an `http_status:404` catch-all. **The Host rewrite is not optional**: the daemon's `HttpListener` matches on Host and answers 404 to anything else, which is what the tunnel sends by default. `cloudflared` was restarted once after the ingress change and logged `Updated to new configuration ... version=4` with the new hostname before registering four connections.
- **DNS**: a proxied CNAME `monitor` -> `eb2f3d02-eca0-455d-9b0a-910ae8be473f.cfargotunnel.com` in the `ukpacketradio.network` zone. The `m9psy-1-monitor` CNAME is deleted, and that name no longer resolves.
- **Rate limit**: the zone's one `http_ratelimit` rule, expression moved to `http.host eq "monitor.ukpacketradio.network"` with its action and limits unchanged: block, 60 requests per 10 s per `ip.src` and `cf.colo.id`, mitigated for 10 s. A tab on the picker polls `/api/instances` every 10 s, which is 6 requests a minute; a tab watching a receiver holds a WebSocket and polls nothing.
- **Frame history carried over**: the retired site's `/var/lib/pdn-soundmodem/frames.db` (23 frames) is now `frames-m9psy-1.db`, which is the name this flavour gives that receiver's log, so `/r/m9psy-1/` keeps what the old site collected rather than starting empty. A copy is at `frames.db.pre-monitor` and can be deleted after 2026-09-04.
- **Helper**: `/usr/local/sbin/monitor-tunnel-token <TOKEN>` writes `/etc/cloudflared/tunnel.env` and starts `cloudflared`, renamed from `m9psy-1-tunnel-token`. The old hostname is gone from `/etc` and `/usr/local` in the container.
- **Measured RSS**: **172 MB** (`ps -o rss=`, 171596 kB) after validation, with two stations built and both back to idle - `m9psy-1` and `reading-ubersdr-m0lte-uk`. Phase 3's arithmetic predicts 86 + 2 x 31 = 148 MB for two, so the container is running about 24 MB above it; the Phase 3 stations were built and idle with no socket, while these two have each carried a live 12 kHz stream through twenty demodulators, which is where the difference belongs. It does not change the sizing: all 49 listed receivers picked in one process is still of the order of 1.6 GB, which is why the container is 3 GB. Container memory in use at that point was 178 MB of 3072.

### 5.2 Still open

- **An overnight soak.** Not done. What to read afterwards is the journal's `directory:` lines - one per outage, so any at all means the directory went away - and the `<slug>: ubersdr: ...` phase lines for session churn: a receiver going `live -> lingering -> idle -> connecting` repeatedly with a tab open would mean something is dropping the WebSocket. Also worth checking that no station is holding a session with nobody watching, and that RSS has not grown beyond one station per receiver anybody picked.
- **A word with the busiest receivers' operators about the per-address allowance, before announcing.** Forty-nine receivers is forty-nine independent daily allowances against this site's one egress address. The picker says so per receiver when one runs out, but they should hear it from a person first, and `deny` is the answer for anyone who would rather not be listed - it takes effect within `refreshMinutes` with no restart.

### 5.3 The sketch this was planned from

Decisions here were Tom's and were not taken in this document.

- **Container**: a new unprivileged LXC `monitor` on proxmox1 (`root@10.45.0.10`), following CT 146's shape exactly: Debian 13, `keyctl=1,nesting=1`, `net0` on `vmbr0` by DHCP, rootfs on `local-zfs`, `swap: 512`, `onboot: 1`, `unattended-upgrades` over Debian security and the cloudflared repo. *Answered differently: CT 146 was repurposed, because the site it served is retired.*
- **Tunnel**: `cloudflared` in the container on its own remotely-managed tunnel named `monitor`, ingress `monitor.ukpacketradio.network -> http://127.0.0.1:8099`, with `originRequest.httpHostHeader` set to `127.0.0.1:8099`. That last part is not optional: the daemon's `HttpListener` matches on Host and answers 404 to anything else, which is what the tunnel sends by default.
- **DNS**: a proxied CNAME to `<tunnel id>.cfargotunnel.com` in the existing `ukpacketradio.network` zone. `monitor.ukpacketradio.network` is one level under the zone because the free universal certificate covers `*.ukpacketradio.network` and nothing deeper.
- **Cloudflare**: WebSockets on by default; one rate-limit rule on the hostname (the free plan allows one), 60 requests per 10 s per address, as CT 146 has. The picker polls `/api/instances` every 10 s per tab, which is well inside it.
- **Memory**: **measured in Phase 3, and it is the modems rather than the plumbing.** On x86-64 under .NET 10, with the three-modem 40 m band plan of 4.5 and stations created but idle (page loaded, no socket), reading `VmRSS` from `/proc` and quoting it as kB/1000: 86 MB with none, 139 MB at two, 242 MB at five, 415 MB at ten, 766 MB at twenty and 1096 MB at thirty. That is **31 MB per station**, linear, and it is live objects rather than GC slack: `DOTNET_GCConserveMemory=9` gives none of it back, and sixty further page loads against stations that already existed added nothing. Fifty receivers all picked is therefore about **1.6 GB**, and nothing is freed by a visitor leaving because the station is kept. The expectation of "a few MB" was wrong by an order of magnitude and the container has to be sized for the measurement.

  Ablation says where it goes, at five stations each: the full plan 31 MB per station; the same modems with `"offsetPairs": 0` on both 3.5 MB; one plain `afsk1200` 1.6 MB. So it is almost entirely the frequency-diversity banks - `afsk300-il2pc` runs 11 decoder branches by default and `bpsk300` runs 9, which is twenty demodulators per receiver. The id-beacon ghosts cost 0.2 MB per station and the frame log 1.3 MB; neither is worth an argument. **The decision this hands Tom** is whether fifty receivers is worth 1.6 GB of container, or whether the picker's stations should run the banks narrower than the single-receiver deployment does. The banks are what took the live 40 m slot from 3 of 13 frames to 13 of 13 (docs/plan.md, 2026-08-02), so they are not free to give up; on the other hand a monitor is a display rather than a link, and a station that misses an off-frequency burst costs a viewer one row on a page rather than a node its traffic. Nothing in the code forces either: `monitor.modems` takes `offsetPairs` like any other modem entry. *Deployed with the banks on and a 3 GB container.*
- **Open question for Tom**: whether CT 146 and `m9psy-1-monitor.ukpacketradio.network` stay up alongside the new deployment. Keeping it costs one container and proves the two-flavour claim in production; retiring it means one site to explain. Nothing in this design forces either. *Answered by Tom, 2026-09-03: "Take down the M9PSY-1 instance and infrastructure, it's not required any more." CT 146 is the new deployment and the old name is gone.*

## 6. Phases

Each phase below is a brief. Work on a branch off `main` in `/home/tf/pdn-soundmodem`; the checkout in `/home/tf/src` is stale. One PR per phase, not merged by the implementer.

**How to run the tests in this repo, because it is not what you expect.** The suite is xunit v3 in-process, and `dotnet test --filter` is **silently ignored**: it runs everything and reports success, which looks like a fast green run and is not one. To run a single class:

```
cd tests/Packet.SoundModem.Tests && dotnet build --nologo -v q && dotnet run --no-build -- -class Packet.SoundModem.Tests.SourceTextTests
```

The whole suite is about 4 minutes and currently 2166+ tests, 0 failed, with about 179 skips that are gated on hardware or on tools that are not installed. A phase is not done until the whole suite has been run, not just the new class.

`SourceTextTests` fails the build on an em dash or an en dash anywhere in the repository and on any byte above ASCII in a string the daemon can print. Write plain ASCII: `->` not an arrow, `,` not a middle dot, `-` not a dash.

### 6.0 Phase 0: this document

**Done.** PR #385.

### 6.1 Phase 1: the `Station` extraction

**Scope.** Carve the per-station body out of `Program.cs` into a `Station` class as described in 4.1. No behaviour change of any kind. No `monitor` config, no front door, no directory.

**Out of scope.** The path prefix (Phase 2). Anything that reads the directory. Any new config key. Any change to what the daemon prints, except the receiver tag, which is not emitted when there is one station.

**Files expected to change.** `src/Packet.SoundModem.Daemon/Program.cs` (large deletion, the start-up and validation half stays); new `src/Packet.SoundModem.Daemon/Station.cs`; possibly new `src/Packet.SoundModem.Daemon/StationOptions.cs`; `tests/Packet.SoundModem.Tests/` gains a `Station` folder.

**Tests to add.**

- `A_Station_Runs_Its_Receive_Loop_Until_Its_Token_Is_Cancelled`
- `A_Starved_Station_Faults_Instead_Of_Exiting_The_Process`
- `A_Dead_Feed_Faults_The_Station_And_Says_Which_One`
- `A_Stations_Journal_Lines_Carry_Its_Tag_When_It_Has_One`
- `A_Single_Station_Writes_The_Same_Lines_As_Before` (golden lines, against the CT 146 config shape)
- `Two_Stations_In_One_Process_Keep_Separate_Frame_Logs`

**Acceptance criteria.**

1. Full suite green, run in full, not filtered.
2. A smoke run of the CT 146 config against M9PSY-1 produces the same journal lines as today, in the same order. Capture both and diff them.
3. `Program.cs` is shorter by roughly the lines moved, and there is exactly one receive loop in the tree.
4. No `Console` use has appeared anywhere in `src/Packet.SoundModem/`.
5. `Environment.Exit` is gone from the starvation path (currently Program.cs:2834). The station raises its fault; the host journals it and returns 1, so systemd sees exactly what it sees today.
6. `SqliteConnection.ClearAllPools()` at `FrameLog.cs`:455 is either left alone with a comment saying why it is safe, or narrowed. Do not leave it undocumented.

**How flavour A is proven unchanged.** The existing suite is the proof and it is large; treat any test that needs editing to pass as a signal that behaviour changed, and stop and explain rather than editing the test. The journal diff in criterion 2 is the second proof. A `.deb` built from the branch and dropped into CT 146 for an hour is the third, and is worth doing.

### 6.2 Phase 2: the path prefix and the front door plumbing

**Scope.** Give `WaterfallWebServer` the ability to serve under a path base without owning a port, as recommended in 4.2 (option 2). Make the page's four absolute URLs relative to a base derived from `location.pathname`. Add the router type that can put two servers behind one listener. No config, no directory, no picker page.

**This phase can run in parallel with Phase 1, in a separate worktree.** It touches `WaterfallWebServer.cs`, `waterfall.html` and their tests. It does not touch `Program.cs`. Coordinate only on the merge order.

**Out of scope.** `monitor.html`. The `monitor` config section. Anything in the daemon beyond keeping the existing call site compiling.

**Files expected to change.** `src/Packet.SoundModem/Waterfall/WaterfallWebServer.cs`; `src/Packet.SoundModem/Waterfall/wwwroot/waterfall.html`; new `src/Packet.SoundModem/Waterfall/WaterfallRouter.cs` (or in the daemon, if it needs to know about stations); `tests/Packet.SoundModem.Tests/Waterfall/WaterfallWebServerTests.cs`, `WaterfallPageTests.cs`, `tests/Packet.SoundModem.Tests/Waterfall/browser/page-probe.mjs`.

**Tests to add.**

- `A_Page_Served_Under_A_Prefix_Opens_Its_Socket_Under_That_Prefix` (page probe: base `/r/x/` gives `ws://host/r/x/ws`)
- `A_Page_Served_Under_A_Prefix_Opens_Its_Links_Window_Under_That_Prefix`
- `Survey_Links_Are_Relative_To_The_Pages_Base`
- `The_Links_Window_Recognises_Itself_Under_A_Prefix`
- `Two_Servers_Behind_One_Listener_Answer_On_Their_Own_Prefixes`
- `A_Router_Answers_404_Outside_Every_Prefix`
- `A_Socket_Upgrade_Reaches_The_Server_Its_Prefix_Names`

**Acceptance criteria.**

1. Every existing test in `WaterfallWebServerTests` and `WaterfallPageTests` passes unedited. If one needs editing, the change is wrong.
2. A server constructed the old way, with a port and no base, behaves exactly as before, including the `/`, `/index.html`, `/links`, `/survey/` and `/metrics` routes and the any-path WebSocket accept at :1478.
3. The page's base under flavour A resolves to `/` and every URL it builds is byte-identical to today's.
4. Two servers on one listener, each with its own channel, each serving its own config message and its own frames.

**How flavour A is proven unchanged.** Criteria 1 to 3. Additionally, build the page and diff the rendered URLs at base `/` against the current file's; there should be no difference at all.

### 6.3 Phase 3: directory, monitor host, picker, config

**Depends on Phases 1 and 2.**

**Scope.** Everything in 4.3, 4.4 and 4.5: the directory client with its DTOs, filtering, allow/deny and slug rule; the `monitor` config section with its validation; the monitor host that owns the listener, creates stations lazily and serves `/api/instances`; `monitor.html`; `CONFIG.md`; `soundmodem.example.json`; the amendment-log entry in `docs/plan.md`; and the memory measurement written back into section 5 of this document.

**Out of scope.** The container, the tunnel, DNS, the release. Any cross-receiver feature.

**Files expected to change.** New: `src/Packet.SoundModem.Daemon/UberSdrDirectory.cs`, `src/Packet.SoundModem.Daemon/MonitorHost.cs`, `src/Packet.SoundModem/Waterfall/wwwroot/monitor.html`, `tests/Packet.SoundModem.Tests/Monitor/*`, a checked-in fixture `tests/Packet.SoundModem.Tests/Monitor/instances.json` captured from the real directory. Changed: `Program.cs` (dispatch on `monitor` versus `device`), the daemon's config record types, `src/Packet.SoundModem/UberSdr/UberSdrAudioInput.cs` (the `PooledConnectionLifetime`), `CONFIG.md`, `soundmodem.example.json`, `docs/plan.md`, `docs/monitor-plan.md`.

**Tests to add.**

- `The_Directory_Is_Parsed_From_A_Real_Capture` (against the fixture, including the three instances with no `tls` field and the six with `reported: false`)
- `An_Instance_With_No_Tls_Field_Is_Read_As_Plain_Http`
- `An_Offline_Instance_Is_Not_Offered`
- `A_Full_Instance_Is_Listed_But_Not_Offered`
- `An_Instance_Without_Our_Iq_Mode_Is_Not_Offered`
- `An_Instance_Whose_Range_Excludes_The_Window_Is_Not_Offered`
- `An_Unreported_Tuning_Range_Does_Not_Exclude_An_Instance`
- `A_Denied_Host_Is_Never_Offered_Even_If_It_Is_Also_Allowed`
- `An_Allow_List_Excludes_Everything_It_Does_Not_Name`
- `Every_Host_In_The_Capture_Gets_Its_Own_Slug`
- `A_Slug_Survives_Another_Instance_Appearing`
- `A_Station_Is_Not_Created_Until_Its_Receiver_Is_Picked`
- `Two_Viewers_On_One_Receiver_Open_One_Session`
- `The_Last_Viewer_Leaving_Drops_The_Session_After_The_Linger`
- `A_Viewer_Returning_Inside_The_Linger_Keeps_The_Session`
- `Viewers_On_One_Receiver_Do_Not_Open_A_Session_On_Another`
- `The_Instances_Api_Reports_Each_Receivers_State_And_Viewers`
- `A_Directory_Outage_Keeps_The_Last_Good_List_And_Says_So`
- `A_Cold_Start_With_No_Directory_Says_So_Rather_Than_Showing_Nothing`
- `Both_Monitor_And_Device_Is_A_Configuration_Error`
- `A_Monitor_With_No_Modems_Is_A_Configuration_Error`
- `A_Monitor_Frame_Log_Path_Is_A_Directory`
- `The_Picker_Page_Lists_Every_Offered_Receiver` (Node probe, in the style of `WaterfallPageTests`, skipped when `node` is absent)
- `The_Picker_Page_Says_When_The_Directory_Is_Unreachable`

**Acceptance criteria.**

1. Full suite green.
2. A live smoke against two real receivers: run the monitor config with `"allow": ["m9psy-1.instance.ubersdr.org", "reading-ubersdr.m0lte.uk"]`, open both pages, confirm two sessions, close one, confirm one session drops after 60 s and the other does not. Keep the journal as evidence.
3. Memory measured with 0, 2, 5 and 10 stations created, and the per-station figure written into section 5 of this document in the same PR.
4. `CONFIG.md` has a `## monitor` section that a stranger could configure from.
5. The `deny` list is demonstrated end to end: put a host in it, refresh, watch it leave the picker.

**How flavour A is proven unchanged.** No flavour-A code path is touched except the `HttpClient` construction in `UberSdrAudioInput`, which the existing UberSDR tests cover. Run the CT 146 config once more and diff the journal against the Phase 1 capture.

### 6.4 Phase 4: release and deployment

**Depends on Phase 3. Needs Tom's decisions from section 5.**

**Scope.** Tag a release so there is a `.deb`. Build the `monitor` container, the tunnel, DNS and the rate-limit rule per section 5. Soak it overnight with tabs open on two receivers and tabs closed, and read the journal for session churn, allowance refusals and any watchdog restart while idle. Write the deployment facts into the project memory the way the 40m deployment is recorded. Then talk to the operators of the receivers that get the most use about the per-address allowance, before announcing.

**Out of scope.** Any code change that is not a deployment fix.

**Acceptance criteria.** The site answers on https://monitor.ukpacketradio.network; the picker lists receivers; two receivers can be watched at once from two browsers; an overnight soak shows no restart and no session held with nobody watching; the memory figure from Phase 3 matches what the container actually uses.

## 7. Order of work

The coordinator updates this list as phases land. `[ ]` not started, `[~]` in progress, `[x]` done.

- [x] **Phase 0** - this document. PR #385.
- [x] **Phase 1** - the `Station` extraction out of `Program.cs`, no behaviour change. PR #387.
- [x] **Phase 2** - `WaterfallWebServer` under a path base, page URLs relative, the two-server router. Can run beside Phase 1 in its own worktree. PR #386.
- [x] **Phase 3** - directory client, monitor host, picker page, `monitor` config, CONFIG.md, example config, memory measured. Needs 1 and 2. PR #388.
- [x] **Phase 4** - release, container, tunnel, DNS, rate limit, soak, operator etiquette. PR #390.

## 8. Decisions

Tom's, taken before any of it was built:

- **Hostname: `monitor.ukpacketradio.network`.** One level under the zone, for the certificate reason in section 5.
- **The list is dynamic**, from https://instances.ubersdr.org/api/instances. Not a hand-maintained list in the config.
- **Two flavours, one code base, configuration is the switch.** In Tom's words: "I don't want to lose the current deployment model - it's strictly the same code base, two flavours via configuration."
- **The per-receiver flavour stays supported** and stays deployed as it is until Tom says otherwise. Whether CT 146 keeps running alongside the new site is open.

### Decisions taken while building Phase 3 (2026-09-03)

Tom's are above; these are the implementer's, recorded here because each of them is a place where the plan was silent, or was wrong against the real code or the real directory, and somebody reading this later should not have to reconstruct why.

- **The capture is 50 instances, not 51, and three have no antenna rather than one.** Section 3.3 was written against a fetch earlier the same day. Everything else in that table held exactly: three instances omit `tls` (`pjmarsh.co.uk`, `sdr.meucorp.net`, `na5b.com`), six report `tuning_range.reported: false` with the placeholder 10 kHz to 30 MHz, `snr_0_30_mhz` is the figure to show and `noise_floor` is a capability flag. The slug rule gives 50 distinct slugs. The fixture in `tests/Packet.SoundModem.Tests/Monitor/instances.json` is that fetch, verbatim.

- **Filtered means not listed; full means listed and not offered.** The plan gives one ordered list of filters and then says, of free slots alone, that a receiver with none "is listed as full, not offered". Read literally that leaves it open what happens to the other four. Taken as: a receiver this monitor could not use at all - offline, wrong IQ mode, no antenna, a reported range that cannot reach the window - is not listed, because a list of receivers that show the 40 m packet window has no business holding one that cannot; a receiver with no free slot is listed and shown as full, because that is one a visitor may well come back to.

- **A slug already serving a station is not taken away from it.** The plan says two colliding hosts both fall back to the full sanitised host. They do - unless one of them already has a station, in which case that one keeps the short slug and the newcomer takes the fallback. Following the plan literally would move a live receiver's URL because an unrelated instance appeared, which is the exact failure deriving the slug from the host was chosen to prevent. Both behaviours are tested.

- **There is a `retrying` state, and the plan's list of states is one short.** 4.2 lists `unpicked`, `idle`, `connecting`, `live`, `lingering`, `refused`, `faulted`. `OnDemandUberSdrInput` also has `Retrying`, which is an open that failed for a transport reason and is being retried on a ladder while somebody waits: neither connecting nor faulted, and with its own sentence. `/api/instances` reports it as `retrying` and the picker says "unreachable, trying again".

- **`refused` had to be made readable rather than sniffed from a sentence.** 4.4 says the refused phase produces "the same sentence `OnDemandUberSdrInput` already produces for the waterfall page". It does not: there is no refused phase, and the only sentence is the generic retry one with the exception's message inside it. `OnDemandUberSdrInput` gained a `Refused` property, set from the reconnect ladder's own `UberSdrReconnectOutcome.Refused` and cleared by a session that opens. Four lines, no behaviour change, and the alternative was matching on the text of an error message.

- **A station is built in two stages.** The plan has a station created on the first request under `/r/<slug>/`. Doing all of it there would mean awaiting the receiver's pre-flight inside that request: up to fifteen seconds of a browser waiting, and nothing sensible to answer with when it fails. So the first request builds everything that needs no network - channel, modems, frame log, page - and answers from it at once, and the receiver is contacted only when a browser actually attaches. Politer as well as faster: a crawler that fetches every page on the site costs no receiver anything.

- **A faulted station is rebuilt after a minute if somebody is still watching.** The brief offered a choice between recreating the input on the on-demand ladder and marking the station faulted in the instances API. Both, in the end, and it is the simpler of the two readings: the sentence goes in the journal, on the page's status chip and in the picker's row, and the station is built again sixty seconds later provided somebody is still there. That is what flavour A answers a dead feed with - restart and reconnect afresh - applied to one station rather than to the process. A `Stalled` fault still ends the process, because a loop wedged inside a blocked `Read` can never be rebuilt and the process can never shut down tidily.

- **`SetRadioStatus` before `Start` threw.** Found by walking into it. `SetReceiver` has always guarded the case with a comment saying why; the one beside it did not, and rebuilt the config message unconditionally, which dereferences the spectrum source. Fixed in the library rather than worked around in the caller. Flavour A calls it after `Start` and is unaffected either way.

- **Anything from the directory that reaches the journal is flattened to ASCII.** Receiver names and locations are somebody else's UTF-8 - the capture holds Greek and accented Latin - and `journalctl`'s pager under a C locale renders those as `<CE><95>`. `SourceTextTests` cannot catch it because it is runtime data rather than a source string. Journal lines name receivers by host or slug, which are ASCII by construction, and anything else that could reach one goes through `UberSdrDirectory.Ascii`. The pages get the real UTF-8, which is where it belongs.

- **An `ardop` entry in `monitor.modems` declares a band and decodes nothing.** Section 2 says flavour B configures no ARDOP host, and 4.5's example config nonetheless carries an `ardop` modem entry. Both stand, and the consequence is worth writing down: the entry takes part in the band plan, which is what keeps the dial and the other modems' audio centres identical to the single-receiver deployment's, and it draws its band on every receiver's waterfall - but ARDOP decoding is `ArdopChannelBridge`, which is the virtual TNC and its host ports, so nothing decodes there. Documented in CONFIG.md rather than quietly left for a deployer to notice a shaded region that never lights up. Wiring a decode-only ARDOP tap is a real option and is not this phase's.

- **Everything the directory says is somebody else's writing, and is treated as such (review of PR #388).** Three holes, all of them found by a reviewer driving the real thing rather than reading it. `public_url` went into an `href` on the picker and on every receiver's page having been escaped for four HTML characters, which does nothing about a scheme: a `javascript:` URL in the directory would have run in every visitor's session on this site's origin. It is now refused where it enters - absolute http or https only, falling back to the receiver's own endpoint - and refused again at both places that write the attribute, because that is the line that actually does the damage. A `host` that is not a hostname is ignored with one journal line, rather than travelling into a station whose pre-flight then threw `UriFormatException` from a code path that caught a list of other exception types and left the station marked as attaching for ever, idle on the picker with a viewer waiting and nothing anywhere saying why; that catch is now every exception and the flag is cleared in a `finally`. And `public_iq_modes` absent or empty passed the filter where 4.3 says the list must contain the mode - silence is not consent, and a receiver listed on that basis is one every visitor picking it would find unreachable.

- **`monitor.modems` is built once at start-up, against a channel nothing reads.** The mode-name and rate checks do not build anything, so a modem this configuration cannot actually make - `acceptPlainIl2p` on a mode with no separate plain reading - got through start-up and then failed once per station, once per request, as a 404 with a stack trace behind it. One exit 2 instead.

- **`waterfall.port` is required in this flavour.** 4.5 says so and the first cut did not enforce it: omitting it took the single-station default of 8107 silently. A site meant to be reached from outside, through a tunnel pointed at a port somebody chose, should not come up on a number nobody chose.

- **`robots.txt` asks crawlers off `/r/` and `/api/`, and the picker stays indexable.** A courtesy rather than a control: following a receiver's link is what builds its station, so a crawler that ignores the file still takes the process to its full memory in one pass. What bounds that is the rate limit and having sized the container for every listed receiver, which is said in the same breath wherever this is documented.

- **A slug bound to a station is bound for the life of the process, including after its receiver leaves the directory.** The first cut protected it only while the owner was still listed, so a host that left and a newcomer that sanitised to the same slug would have had the newcomer offered under a prefix the router was still serving the old station on: one operator's receiver under another operator's name.

- **A fault is cleared when the rebuild window passes with nobody watching.** It described a session that no longer exists, and left standing it had the picker telling every visitor that a receiver was "not working just now" a quarter of an hour after anything had been asked of it.

- **The memory figure is 31 MB per station, not "a few MB".** See section 5. The finding is that it is almost entirely the frequency-diversity banks, and the decision it hands Tom is recorded there rather than taken here.
