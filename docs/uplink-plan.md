# Private stations on monitor.ukpacketradio.network - a station-initiated uplink

**Status: planned, 2026-09-04.** Nothing is built. Verified against `main` at `7e4529d` (v0.55.0, the tree that is live in CT 146). The successor project to [docs/monitor-plan.md](monitor-plan.md), which put fifty UberSDR web receivers behind one picker at https://monitor.ukpacketradio.network; this adds a second kind of thing to that picker, and it is somebody's actual station.

Tom, and this is the whole brief:

> Perhaps individual private pdn-soundmodems (which won't be UberSDR receivers, but full blown transceivers) could also opt in to be selectable on monitor.ukpacketradio.network? i.e. we can see and listen to what each others' stations are hearing?

Yes, and the first station to opt in is GB7RDG-2.

The constraint from the monitor plan carries straight over and is still the thing everything bends around: one code base, one binary, one `.deb`, one set of tests, and the flavours are configuration. This project adds a third configuration, not a third program. **Flavour A** is the ordinary station: a config with a `device`, running at somebody's house on ALSA, on a Flex, or on an UberSDR receiver, feeding a node. It gains one new optional section, `publish`, and with that section absent nothing about it changes at all. **Flavour B** is the monitor: one process, the picker, the receivers. It gains a way to accept uplinks. **A station never accepts uplinks and a monitor never publishes one**, and the daemon says so if a config asks for both.

This document is written to be executed by sub-agents working one phase at a time under a coordinator, as the monitor plan was, so each phase in section 6 is a self-contained brief. Section 7 is the checklist the coordinator keeps up to date. Section 8 is the decisions, split into the ones Tom has already taken and the ones he is being asked to take.

## 1. What it is

An operator with a real station - a transceiver on an antenna, or a private daemon of their own on a web receiver - puts one block in their config, gets a token from the site owner, and their station appears on https://monitor.ukpacketradio.network alongside the web receivers. A visitor picks it and gets the page they already know: the waterfall, the AX.25 links panel, the decoded frames, and a Listen button that plays what that station's receiver is hearing right now.

**The station dials out.** A home station sits behind NAT on a dynamic address and must not have to open a port, hold a certificate, or run a web server anybody outside can reach. It makes one outbound WebSocket to the monitor, exactly as it already makes one outbound WebSocket to an UberSDR receiver, and keeps it. The monitor lists the station while that socket is up.

**Nothing flows until somebody is watching.** The socket sits idle. When a visitor opens the station's page the monitor says so, and the station starts sending spectrum; when the visitor presses Listen the monitor says so, and audio starts; when they leave, both stop. Decoded frames are the exception and flow all the time, because they are perhaps twenty bytes a minute and they are what makes a quiet band look alive to somebody arriving an hour later. See 4.5 for the arithmetic and 8 for the decision.

**Strictly one way.** The uplink carries the display stream up. Down it carries one message, which says how many people are watching and whether any of them has pressed Listen. There is no transmit, no configuration, no KISS, no restart, nothing that could act on the station even if the site were taken over. This is structural rather than a matter of the page hiding buttons, and 4.6 says exactly how.

**Leaving is removing the block.** An operator who has had enough deletes `publish` from their config and restarts; the socket goes and the station leaves the picker within seconds. The site keeps a `deny` of its own for the other direction.

**Same page, same history.** A relayed station lives at `/r/<slug>/` with the identical receiver page, keeps its own frame log on the monitor as `frames-<slug>.db`, and so its history and its links panel survive the station going off the air. The credit line names the station and its operator, because it is their radio, their antenna and their electricity.

## 2. What it is not

- **Not a way in.** The uplink cannot carry a command. See 4.6; it is the section to read first if you are worried, and it is the one whose tests matter most.
- **Not audio relay.** The station sends what it decoded, not just what it heard, and the decoding happens on the station with that operator's modems, their diversity settings and their dial. A design that shipped audio and demodulated it on the monitor was considered and rejected in 4.1; it is cheaper and it answers a different question.
- **Not a node service.** Nothing about the uplink touches KISS, the node, transmit, or the station's own operation. A station whose uplink is down is a station with a slightly quieter journal.
- **Not federation.** Stations do not talk to each other, nothing is combined across them, and there is no dedupe, no "heard by three stations" view and no cross-station correlation. Each is its own independent monitor that happens to share a hostname. Same answer as the monitor plan gave for receivers, and for the same reason: it is a different project with a different data model.
- **Not open to anyone.** A station is on the site because the site owner issued it a token. There is no sign-up, no self-service and no discovery.
- **Not a second site.** One deployment, one hostname, one picker. Stations and receivers are rows on the same page, whether or not they are under the same heading (8).

## 3. What already exists (verified 2026-09-04 against `main` at `7e4529d`)

### 3.1 The message stream a browser gets, in full

This is the thing being relayed, so it is worth having written down once. Everything below is `src/Packet.SoundModem/Waterfall/WaterfallWebServer.cs` (2125 lines).

| Message | Kind | Built at | When | What it carries |
|---|---|---|---|---|
| `config` | text | `BuildConfigMessage` :735, sent :1804 | once, first, per browser | `sampleRate`, `binWidthHz`, `lineLength`, `linesPerSecond`, `dialHz`, `radioStatus`, `sideband`, `page` (the served page's hash), `publicMonitor`, `title`, `about`, `receiver`, `receiverUrl`, `pickerUrl`, and `modems[]` of `{sub, mode, modeName, lowHz, highHz, centreHz}` |
| spectrum line | **binary** | `OnLine` :979 | per line, at `linesPerSecond` | `[0x01 heard or 0x03 transmitted][u32 LE line index][one dB-scaled byte per bin]` |
| audio block | **binary** | `BroadcastAudio` :1448 | per 40 ms, only to clients that asked | `[0x02][3 bytes pad][s16 LE mono at the channel rate]`. The pad is not decoration: a browser's `Int16Array` view needs its byte offset aligned (:1409-1412) |
| `frame` | text | `BroadcastFrame` :1349 | per decode and per transmission | `line`, `sub`, `mode`, `from`, `to`, `lenBytes`, `snrDb`, `burstLines`, `offsetHz`, `corrected`, `crc`, `id`, `tx`, `txTrimHz`, `why`, `il2p`, `hex`, `plain`, `monitorOnly` |
| `history` | text | `BuildHistoryMessage` :1916 | once per browser, from the frame log | the last 50 logged frames, oldest first, each marked `hist` |
| `link` | text | `ObserveLink` :1127, `ExpireLinks` :1153 | per AX.25 link event | the link card as it now stands and the event that changed it |
| `links` | text | `BuildLinksMessage` :1178 | once per browser | every link the observer holds, each with its 100-frame backlog |
| `radio` | text | `SetRadioStatus` :495, broadcast :508 | on change | one sentence for the status chip |
| `hosts` | text | :459 | on change | which KISS hosts are attached. Operator-only; a public page hides the strip |
| `tx` | text | :433 | during a transmission | the power and SWR readout |
| `survey` | text | :608 | on change | capture counts, only where a survey is configured |
| `capture` | text | :2050 | on change | raw-capture state |

And what the browser sends back, in its entirety, `TryApplyClientRequest` :1414-1437:

```json
{"type": "audio",    "on": true}
{"type": "spectrum", "on": false}
```

That is the whole uplink from a browser. Anything else is parsed, matched against those two names, and dropped (the `switch` has no default arm); a message that is not JSON is swallowed by `catch (JsonException)` at :1433. The page sends `audio` from the Listen button (`waterfall.html`:684) and `spectrum: false` once, when it has decided it is the torn-off links window (`waterfall.html`:1695). Nothing else in 1717 lines of page sends anything.

Per-client state is two booleans on `WaterfallClient` (:228-250): `AudioEnabled`, off by default, and `SpectrumEnabled`, on. `OnLine` and `BroadcastAudio` both check whether **any** client wants the stream before doing the work (:997-1004, :1451-1466), which is exactly the shape a relay needs.

### 3.2 Where the seam is, and it is narrower than it looks

`WaterfallWebServer` is 2125 lines and reads as though it is welded to `SoundModemChannel`. It is not. There are exactly nine lines in the file that touch `_channel`, and they are five distinct dependencies:

| Need | Lines | What a relayed station has instead |
|---|---|---|
| `SampleRate`, for the config message, the audio block size and the transmit pacer | :633, :741, :913, :1474 | a number off the wire |
| `Modems`, to probe each modem's occupied band at `Start` and to name the mode of a transmission | :636, :638, :658, :1111 | `WaterfallOptions.DeclaredBands` (:37-52), which **already** exists to draw a band no `IModem` carries. ARDOP is drawn that way today |
| `AddReceiveTap`, the float samples that become spectrum lines and audio blocks | :672 | nothing: the lines and the blocks arrive already made |
| `TransmittedAudio`, `TransmittingChanged`, for painting our own keyup | :695, :696 | nothing: one way, and a relayed station never paints a transmission of the monitor's |
| `FrameReceivedWithQuality`, `FrameTransmittedWithTrim` | :697, :698 | frames off the wire |

Everything else in the class - the clients, the bounded per-client queue and its drop-oldest policy (:1770-1777), the retained-state handshake and the lock ordering that makes it exact (:1780-1800), the links observer and its expiry timer, the frame history delegate, the page, `TryServeAsync` and the path base, `ServedAt`, `Viewers` and `ViewersChanged` - is about browsers and knows nothing about channels. `_source` is the one other complication: it is the `WaterfallSource` that turns samples into lines, and it supplies three numbers to the config message (`BinWidthHz`, `LineLength`, `LinesPerSecond`, `WaterfallSource.cs`:125-134) and the current line index to every `frame` message (:1359). A relayed station has all four off the wire and no source.

`WaterfallSource` itself (`src/Packet.SoundModem/Dsp/WaterfallSource.cs`) is where the sizes come from: one byte per bin, `LineLength = fftSize / 2`, and `fftSize` defaults to 2048 below 24 kHz and 8192 at and above (:82-85). So a 12 kHz station's line is **1024 bytes** and a 48 kHz station's is **4096**, at 5.86 Hz per bin either way. The daemon runs at 12 kHz unless a modem demands 48 (`StationFactory.cs`:63-77).

The routing half was already solved for the monitor and needs nothing: `WaterfallWebServer.Routed(channel, options)` (:340) builds a server with no listener and no port, `TryServeAsync(context, pathBase)` (:1573) serves it under a prefix, and `WaterfallRouter` (`src/Packet.SoundModem/Waterfall/WaterfallRouter.cs`, 252 lines) owns the one port and dispatches. The only thing standing between that and a relayed station is the `SoundModemChannel` in the signature.

### 3.3 The monitor host, and what a second kind of station needs from it

`src/Packet.SoundModem.Daemon/MonitorHost.cs` is 991 lines and holds a `Dictionary<string, MonitorStation>` keyed by slug (:54). `MonitorStation` is a private nested class (:404) built in two stages, and the two stages are the shape a relayed station wants too.

- **Stage one, no network** (`MonitorStation` ctor :421, `Build` :474): a `SoundModemChannel`, the modems, a `FrameLog` opened as `Path.Combine(directory, $"frames-{Slug}.db")` (:452), a `WaterfallWebServer.Routed` with `Public = true` and `PickerUrl = "../"` (:476-489), `SetReceiver` and `SetRadioStatus` (:493-494), the links backfill from the log (:503-506), `ViewersChanged` subscribed (:508), `Server.Start()` (:509), and `_router.Add($"/r/{Slug}/", Server)` (:510).
- **Stage two, on the first browser** (`OnViewersChanged` :578 to `AttachAsync` :598 to `OpenAndRunAsync` :643): open the receiver, build a `Station` (:669-682), run its receive loop on a dedicated long-running thread (:702-714).
- **Faults** (`OnFault` :756, `Fault` :771): a sentence into the journal, into the page's status chip and into `/api/instances`, and a rebuild armed for sixty seconds later if anybody is still watching (`ArmRebuild` :810-856). A fault is cleared when that window passes with nobody there (:842-852).
- **Never torn down**: there is no `_stations.Remove` anywhere in the file. A station outlives its receiver leaving the directory, which is what keeps its page, its links and its log alive (:21-25).

A `RelayStation` needs stage one and not stage two: the router registration, the frame log, the waterfall server, the viewer counting, the linger (which becomes "how long to keep asking the station for spectrum after the last viewer left"), and a row in `/api/instances`. It needs no `SoundModemChannel`, no modems, no `Station`, no receive loop and no thread. That last point is worth holding on to for the sizing in section 5: the monitor plan measured **31 MB per station** and found it was almost entirely the twenty demodulators of the frequency-diversity banks (monitor-plan.md section 5.3). A relayed station has none of them.

`/api/instances` is built at :291-321 with rows at :323-345, and its fields are `slug`, `host`, `callsign`, `name`, `location`, `publicUrl`, `snrDb`, `loadStatus`, `availableClients`, `maxClients`, `offered`, `why`, `state`, `status`, `viewers`, `description`. The picker (`monitor.html`) reads only `page`, `title`, `about`, `staleSince`, `problem`, and per row `slug`, `callsign`, `host`, `name`, `location`, `publicUrl`, `snrDb`, `availableClients`, `maxClients`, `offered`. It says nothing about who else is watching and it must not start (monitor-plan.md section 8, 2026-09-04).

The slug rule is `UberSdrDirectory.SlugFor` (:555-568): lower-case, strip a trailing `.instance.ubersdr.org` or `.tunnel.ubersdr.org`, then `Sanitise` (:592-617) reduces every run outside `[a-z0-9]` to a single hyphen and trims the ends. `WaterfallWebServer.ValidatePathBase` (:1727-1745) accepts exactly that character set, and says at :1721-1726 that this is deliberate. There is already a reservation mechanism: `UberSdrDirectory.Bind(slug, host)` (:299) and `AssignSlugs` (:461-537), where a slug held for a host that is not currently listed pushes any newcomer onto its full sanitised host instead (`heldForAnAbsentee` :495, `keepsIt` :500). That is exactly the behaviour a reserved station slug wants, and it is already tested.

### 3.4 The station side, and what it already does with viewers

`Program.cs` is 2606 lines of top-level statements. The device dispatch is one `if/else` chain: pipe :1865, wav-loop :1894, ubersdr :1908, flex :2029, ALSA :2295. The `Station` is built at :2472-2560 and run at :2578; `Station` (`src/Packet.SoundModem.Daemon/Station.cs`, 545 lines) owns the receive loop, the three watches and the fault model, raises `Faulted` and never ends the process itself (:18-20).

The line that matters most for this project is `Program.cs`:1971:

```csharp
waterfallServer.ViewersChanged += onDemand.SetViewers;
```

It is inside `if (uberSdrConfig?.OnDemand == true)` and it is the entire on-demand mechanism: browsers attach, the count goes up, `OnDemandUberSdrInput.SetViewers` (:220-263) opens the session; the count goes to zero, the linger arms, and sixty seconds later the session is dropped. On-demand is an UberSDR concept only - ALSA and Flex are opened at start-up and held, and `Station.cs`:490-500 explains why an ALSA `Read` could not be made on-demand even if somebody wanted it.

**So a relayed station on an on-demand UberSDR receiver needs no new logic on the station side at all**, provided the monitor's viewer count reaches that same `ViewersChanged`. A monitor visitor arriving becomes a viewer, the ladder opens the session, the linger holds it across a page refresh, and a station nobody is watching holds no session on anybody's receiver. That is the right answer and it falls out for free.

The reconnect precedent is `UberSdrReconnectPolicy` (`src/Packet.SoundModem/UberSdr/UberSdrReconnectPolicy.cs`, 81 lines): outcomes `Healthy`, `Transient`, `Refused`, `ShortSession` (:11-33), and ladders at :42-48 - a flat 1 s breath after a healthy session, 1 s doubling to 30 s for a transport failure, 60 s doubling to 15 minutes for a refusal, and the first failure of a run waits exactly the base rather than twice it (:68-69). The give-up clock is five minutes (`UberSdrAudioInput.cs`:45), measured on `GetTimestamp` rather than `UtcNow` because an NTP step used to trip it early (:341-343).

The only outbound long-lived socket in the tree is `UberSdrAudioInput`'s `ClientWebSocket` (`ConnectAsync` :603-647), and it carries one hazard worth inheriting from deliberately rather than by accident. It sets `CollectHttpResponseDetails = true` (:621) so a 429 on the upgrade is distinguishable from a transport failure, and it sets **no keepalive at all**. `Station.cs`:481-486 spells out the consequence:

> A hung established WebSocket (half-open TCP; .NET sends pings but by default never times out missing pongs) starves the ring while the pump sits in `ReceiveAsync` believing the session is live: starvation's case.

For the UberSDR input the answer is the starvation watch. A `publish` uplink has no ring and no starvation watch, so it has to set `KeepAliveInterval` and `KeepAliveTimeout` itself.

Two other patterns to copy, both cited by the monitor plan's Phase 3: `new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }` (`UberSdrAudioInput.cs`:54-58), because the default infinite lifetime pins a DNS answer for the life of the process and the monitor is behind a tunnel that can move; and the generation counter in `OnDemandUberSdrInput` (:72, checked at :349, :377, :396, :438, :469), which is what stops a callback from an abandoned attempt acting on a live one.

### 3.5 What the station has to say about itself, and does not have today

There is no station-identity block in the config. `grep` over `DaemonConfig.cs` for a callsign, an operator, a locator or a site name finds two things and neither is usable:

- `modems[].identify.callsign` (`DaemonConfig.cs`:168) is the callsign sent **in Morse on the air**, per modem, and `Program.cs`:679-687 rejects an `identify` block outright on a receive-only station. So the flavour that most wants to name itself publicly is the one guaranteed not to have it.
- `flex.stationName` (`DaemonConfig.cs`:393) is the name this client registers with a FlexRadio and defaults to the string `pdn-soundmodem`. It never leaves the radio.

`waterfall.title` and `waterfall.about` (`DaemonConfig.cs`:737, :741) are the only operator-authored free text that reaches a public page today. So `publish` has nothing to inherit and must carry its own identity, and the nearest precedent for the shape is `identify.callsign`'s refusal to invent a default for something that is a licence matter (`Program.cs`:705-711).

### 3.6 What is greenfield

Everything else. There is no outbound relay of any kind, no token check anywhere except the config API's `X-API-Key` (`ConfigApi.cs`:424-439, which is constant-time via `CryptographicOperations.FixedTimeEquals` and is **not wired at all in the monitor flavour**, `MonitorHost.cs`:121-122), no hashing except a page-version cache-buster (`EmbeddedPage.cs`:30), no `RandomNumberGenerator`, no HMAC, no signatures, and no per-client or per-address cap anywhere in the process - the monitor plan delegates that to one Cloudflare rate-limit rule and says so (monitor-plan.md section 5.1).

## 4. What to build

### 4.1 The seam: one interface, pointing one way

This is the central design question and it deserves the four options costed rather than one asserted.

**Option 1: a `IMessageSource` abstraction inside `WaterfallWebServer`.** Abstract the input side, so one implementation is fed by a channel and another by an uplink. It is the obvious reading of the problem and it is the wrong cut. The class's channel dependencies are not one thing; they are a band probe that genuinely needs `IModem` instances, a receive tap on the hot path, four events and a sample rate. An interface wide enough to cover all of them is a second copy of `SoundModemChannel`'s public surface, and the class ends up with a nullable source, a nullable channel and two ways of doing everything. Cost: a large diff through the spine of the file the whole site depends on, and a class that is currently understandable in one sitting stops being so.

**Option 2: extract the browser half into a `WaterfallBroadcaster`.** Move the clients, the bounded queues, the send loop, the per-client flags, the retained-state handshake, the viewers event and the page serving into a new class, leave `WaterfallWebServer` as channel-to-broadcaster glue, and let a relay drive the same broadcaster. Cleaner on paper. Cost: about 450 lines moved, including the lock ordering at :260-266 and the "either in the opening snapshot or in a broadcast, never both and never neither" invariant, which is the subtlest thing in the file. And it does not finish the job: the links observer, the link expiry timer and the frame-history delegate would stay behind in `WaterfallWebServer`, so the relay would need a second copy of those too, which is the duplication the extraction was meant to avoid.

**Option 3: a separate small relay server that serves the same page.** About 300 lines, no changes to `WaterfallWebServer` at all. Rejected: it is a second implementation of the browser protocol, it will drift, and the drift will be invisible until a page change lands on one flavour and not the other.

**Option 4: relay audio and demodulate on the monitor.** The cheapest of all - `MonitorHostOptions.OpenInput` (`MonitorHost.cs`:986-987) is already a delegate returning an input, so an `IAudioInput` fed by a WebSocket would make a relayed station an ordinary `MonitorStation` with no new anything. It is worth writing down why not, because somebody will suggest it. First, the monitor would have to be told each station's modem set, dial and sideband, and keep them in step by hand, when the entire point is that a station opts in with one block and the site needs nothing but a token. Second, and fatally, the decodes on the page would be the monitor's rather than the station's: "what each other's stations are hearing" would quietly become "what the monitor makes of the audio your station sent", and the station's own frame log and the site's would disagree for reasons nobody could see. Third, it costs the monitor twenty demodulators and 31 MB per watched station, and it costs the station 194 kbit/s of audio whenever anyone is watching whether or not they pressed Listen. Fourth, audio cannot carry the things only the station knows: its callsign, its radio, its per-frame SNR and offset measurements, its diversity branch that won.

**Recommended: option 5, which is option 1 done narrowly.** Add one interface, pointing one way, and have `WaterfallWebServer` sit on both ends of it.

```csharp
namespace Packet.SoundModem.Waterfall;

/// <summary>Where a station's display stream goes when it is not going to a browser.</summary>
public interface IWaterfallRelay
{
    bool WantsSpectrum { get; }
    bool WantsAudio { get; }
    void Line(long index, ReadOnlySpan<byte> bins, bool transmitted);
    void Audio(ReadOnlySpan<short> samples);
    void Frame(RelayedFrame frame);
    void Radio(string status);
}
```

On the **station**, `WaterfallWebServer` gains `public IWaterfallRelay? Relay { get; set; }`, and four call sites offer it what they already offer browsers: `OnLine` (:979) after the trackers, `BroadcastAudio` (:1448) instead of building a message, `BroadcastFrame` (:1349) which starts returning its bytes so the callers can hand over the raw frame with them, and `SetRadioStatus` (:495). The `anyClients` test at :997-1004 and the `wanted` test at :1451-1466 gain `|| relay.WantsSpectrum` and `|| relay.WantsAudio`, which is what makes a station with no browser open still produce lines for a viewer on the site. With `Relay` null, every one of those is a null check on a reference that is already in cache, and nothing else changes.

On the **monitor**, `WaterfallWebServer` gains a channel-free construction and **implements the same interface**:

```csharp
public static WaterfallWebServer Relayed(RelayedStationShape shape, WaterfallOptions? options = null);
```

where `RelayedStationShape` is the sample rate, the line length, the bin width, the lines per second, the dial, the sideband and the bands - the seven things the config message needs and the channel used to supply. `Line`, `Audio`, `Frame` and `Radio` then do exactly what `OnLine`, `BroadcastAudio`, `BroadcastFrame` and `SetRadioStatus` do; `WantsSpectrum` and `WantsAudio` report what its browsers want. So the monitor's uplink reader has no idea it is talking to a waterfall server, and the station's waterfall server has no idea it is talking to a socket.

The mechanical cost, counted: `_channel` becomes `SoundModemChannel?` and its nine sites get a null guard or read a new `_sampleRate` field; `_source` becomes optional and its four numbers move into the shape record; `BroadcastFrame` gains a `line` argument that defaults to the source's `NextLineIndex` and returns its bytes; `OnFrameTransmitted`'s `_channel.Modems` lookup is inside a path a relay never takes. Roughly 120 lines changed in 2125, all of them mechanical, and every existing test in `WaterfallWebServerTests` and `WaterfallPageTests` passes unedited. If one needs editing, the change is wrong.

**One addition that is not mechanical**, and it is what makes the on-demand behaviour of 3.4 work: `public void SetRelayViewers(int)`, whose count is added to `_clients.Count` for the purposes of `Viewers` and `ViewersChanged`. Then a viewer on the site is a viewer as far as the station's own on-demand ladder is concerned, and `Program.cs`:1971 does the rest with no change to it at all. It is also the honest number: somebody genuinely is watching, just not from the LAN.

### 4.2 The wire

Two protocols with independent lifetimes, and this is deliberate. The browser protocol may change whenever the page does, because the page and the server ship in the same binary. The uplink protocol spans two machines running whatever versions their operators have installed, so it is versioned, additive, and the monitor never forwards a station's JSON to a browser. **The monitor parses everything a station sends into typed fields and re-serialises its own messages.** A station cannot inject a field into the page's stream, because there is no code path in which its bytes reach a browser unexamined. The two binary streams are checked for their type byte and their exact expected length and reframed.

**The endpoint.** `wss://monitor.ukpacketradio.network/uplink`, a WebSocket upgrade on the same port and through the same tunnel as everything else, handled by `MonitorHost.FrontDoorAsync` (:130) before the `/r/` branch. The router already hands the front door any path no prefix claims, WebSocket upgrades included, so nothing in `WaterfallRouter` changes.

**Authentication** is one HTTP header on the upgrade:

```
Authorization: Bearer pdnsm_<43 url-safe base64 characters>
```

A header rather than a query parameter because a query parameter is written to every log between here and there, Cloudflare's included. `ClientWebSocketOptions.SetRequestHeader` already does this for the UberSDR password (`UberSdrAudioInput.cs`:617, :623).

**Up, station to monitor:**

- `hello`, text, once and first, before anything else. The protocol version, the daemon version, the station's identity from its `publish` block, the sample rate, the spectrum shape, the dial and sideband, and the bands. The monitor answers with `welcome` or closes.
- **spectrum**, binary, `[0x01 or 0x03][u32 LE index][bins]`, only while `spectrum` is wanted. Same shape as the browser's, so the byte layout has exactly one definition.
- **audio**, binary, `[0x02][3 pad][s16 LE]`, only while `audio` is wanted.
- `frame`, text: the display fields of a `frame` message, plus `at` (UTC, ISO 8601), plus `raw` (base64 of the AX.25 bytes). `raw` is what lets the monitor keep its own link observer and its own frame log, which is what makes the links panel and the history survive the station going offline. A typical frame is a few hundred bytes.
- `bye`, text, optional: a reason, sent before a planned disconnect, so the journal says "GB7RDG-2: going off air for the night" rather than "GB7RDG-2: connection closed".

**Down, monitor to station, and this is the entire list:**

- `demand`, text: `{"type": "demand", "viewers": 2, "spectrum": true, "audio": false}`. Sent on every change and at least every 20 s, which doubles as the heartbeat.
- `welcome`, text, once: the slug the monitor has put the station under and the URL of its page, so the station can journal `publish: live at https://monitor.ukpacketradio.network/r/gb7rdg-2/`.

That is all. There is no third message and there never will be one; 4.6 is about keeping it that way.

**Framing.** The monitor's reader must reassemble on `EndOfMessage` with a hard cap, which is what `UberSdrAudioInput` does at :462-476 and what `WaterfallWebServer`'s browser reader conspicuously does not (:1837-1852 reads into a 1024-byte buffer and ignores `EndOfMessage` entirely, so a long browser message arrives as fragments that each fail to parse - harmless there, wrong here, since a spectrum line is 1029 bytes). Note also that `UberSdrAudioInput`'s accumulator is an `ArrayBufferWriter<byte>` that grows without limit (:453): the one genuinely unbounded WebSocket buffer in the repo, and not a pattern to copy.

### 4.3 The station side: `publish`

```json
"publish": {
  "url": "wss://monitor.ukpacketradio.network/uplink",
  "token": "pdnsm_...",
  "callsign": "GB7RDG-2",
  "operator": "Tom M0LTE",
  "location": "Reading, England",
  "radio": "IC-7300 into a doublet at 10 m",
  "site": "https://gb7rdg.example/",
  "linesPerSecond": 10,
  "audioRate": 12000,
  "frames": "always"
}
```

- `url` and `token` are required and there is no default for either. `callsign` is required, because a station on a public page that will not say who it is has no business being there, and because the monitor checks it against the token (4.4).
- `operator`, `location`, `radio` and `site` are optional and are what the credit line and the picker row are made of. `site` is a link to the operator's own page, refused unless it is an absolute http or https URL, by exactly the check the directory's `public_url` goes through (`UberSdrDirectory.HttpUrlOrNull` :583-587).
- `linesPerSecond` is the relayed line rate, defaulting to **10** where the page's own is 30. See 4.5.
- `audioRate` is the relayed audio rate, defaulting to **12000**, decimated from the channel rate by the existing `Decimator` (`Station.cs`:105-107 builds one for exactly this job). Only integer divisors; anything else is a start-up refusal rather than a resampler.
- `frames` is `always` or `watched`, defaulting to `always`. See 8.

**Validation, all exit 2**, in the style of `Program.cs`:503-518 and `DaemonConfig.ValidateMonitor` (:1073-1156):

- `publish` and `monitor` together: a monitor does not publish. Name both, say to remove one.
- `url` absent, or not an absolute `ws`/`wss` URL. Warn, do not refuse, on plain `ws` to anything but a loopback host: it is the shape of a smoke test and the shape of a mistake, and the operator should be the one to decide which.
- `token` absent or shorter than 32 characters.
- `callsign` absent, or not a plausible callsign-with-optional-SSID. There is a parser to lean on in `Ax25AddressParser` (:44-79).
- `linesPerSecond` not in 1..30, or not a divisor of the waterfall's own rate. A non-divisor is a needless remapping problem for a knob nobody will turn.
- `audioRate` not a divisor of the channel's DSP rate.
- No `waterfall` section: the uplink is a client of the waterfall server, and without one there is nothing to publish. Same sentence shape as the on-demand check at `Program.cs`:512-517.
- Field lengths: callsign 16, operator 40, location 60, radio 60. Refused at start-up with the limit named, so the operator finds out at once rather than seeing their sentence cut in half on somebody else's website.

**`publish.token` must be redacted by the config API**, alongside `api.key` (`ConfigApi.cs`:94, :107-109, which writes `"(set, not shown)"`). A station running the config API on its LAN must not hand its uplink token to anyone holding the API key. This is a four-line change and it is not optional.

**`UplinkClient`**, in the library at `src/Packet.SoundModem/Waterfall/UplinkClient.cs`, implementing `IWaterfallRelay`:

- Constructed with the `publish` settings, a `TimeProvider` and an `Action<string>? log`, in the library's usual style; the daemon passes `stationJournal.ErrorSink` (`Station.cs`:449) so the lines carry a tag if there ever is one. Its lines are prefixed `publish:`, matching `ubersdr:`, `flex:`, `ptt:`.
- **It holds no `SoundModemChannel`, no KISS server, no PTT, no config API and no `Station`.** Its constructor takes a `WaterfallWebServer` and a settings record and nothing else. This is checkable by reading nine lines, and 4.6 makes it checkable by a test.
- Connect, send `hello`, wait for `welcome`, then loop. On the way, `socket.Options.KeepAliveInterval = 20 s` and `KeepAliveTimeout = 20 s`, because .NET's default sends pings and never times out missing pongs (`Station.cs`:481-486), and there is no starvation watch on this path to catch it. Belt and braces: a `demand` at least every 20 s from the monitor, and a reconnect if two go missing.
- **Reconnects** on `UberSdrReconnectPolicy`, which needs no changes: `Healthy` for a session that carried traffic, `Transient` for a transport failure (1 s doubling to 30), `Refused` for HTTP 401, 403 or 429 on the upgrade (60 s doubling to 15 minutes). 429 is distinguishable only if `CollectHttpResponseDetails = true` is set, as `UberSdrAudioInput.cs`:621 does. A refused token backs off to a quarter-hour and says so once per hour in the journal rather than every minute: it is a mistake somebody has to fix, not a condition that clears itself.
- **Never faults the station.** The uplink is a courtesy. It writes a line and retries for ever; it does not raise `Station.Faulted`, it does not set `radioLost`, and it does not touch the exit code. A node whose owner is asleep must not stop passing traffic because a website is down.
- **Demand to the station:** on each `demand`, `server.SetRelayViewers(viewers)`, and store the two booleans for `WantsSpectrum` and `WantsAudio`. On disconnect, `SetRelayViewers(0)` and both false, so a station whose uplink dropped stops producing for nobody and, if it is on-demand UberSDR, drops its session after the linger.
- **Decimation** of the spectrum: hold a max across the `sourceRate / publishRate` lines that make up one relayed line, emit the max, renumber the index as `index / N`, and scale a frame's `line` and `burstLines` the same way. Max-hold rather than dropping, because at 30 lines a line is 33 ms and a short burst can fall entirely inside a line that was dropped; a max is what a waterfall does when you zoom out, and it makes the relayed picture at 10 lines a second arguably better than a decimated one.

### 4.4 The monitor side: `RelayStation`, tokens, rows

**Config, in the monitor's own file:**

```json
"monitor": {
  "uplinks": [
    {
      "callsign": "GB7RDG-2",
      "slug": "gb7rdg-2",
      "tokenSha256": "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08"
    }
  ]
}
```

- **Tokens are stored hashed.** The monitor only ever compares, so it never needs the plaintext, and a config file that leaks does not hand out working uplinks. Plain SHA-256 with no salt and no KDF, and the reason is worth stating rather than leaving to look like an oversight: the token is 256 bits from `RandomNumberGenerator`, so there is no dictionary to defend against and a work factor would only cost the monitor time on every connection. Compared with `CryptographicOperations.FixedTimeEquals` over the 32 raw bytes, as `ConfigApi.cs`:436-438 does over the API key.
- **The callsign is bound to the token**, and this is what stops one station claiming another's slug. The slug comes from the monitor's table, never from the wire; the `hello`'s callsign must match the entry's or the connection is closed with a sentence. A station has no way to ask for a slug at all.
- **One connection per token.** A second one that authenticates the same token closes the first, because a station whose socket has half-closed must not be locked out by its own ghost. The old socket gets a close reason that says so.
- `pdn-soundmodem --uplink-token` prints a fresh token and its hash, so the site owner does not have to invent either. Fifteen lines, and it is the difference between tokens that are 256 random bits and tokens that are somebody's cat.

**The slug.** `Sanitise(callsign.ToLowerInvariant())` gives `gb7rdg-2`, which is already the character set `ValidatePathBase` demands. It is written in the config next to the callsign rather than derived silently, so the URL a visitor bookmarks is a decision somebody took and not a function that might change. Collision with a receiver's slug is handled by the mechanism that already exists: the monitor calls `UberSdrDirectory.Bind(slug, "<uplink>")` for every configured uplink at start-up, and `AssignSlugs`'s `heldForAnAbsentee` path (:495-501) then pushes any colliding receiver onto its full sanitised host. A station wins, because its slug is a callsign somebody was issued and a receiver's is derived from a hostname. Two uplinks with the same slug is an exit 2 at start-up.

**`RelayStation`**, a second kind of thing in `MonitorHost`'s table. `_stations` becomes `Dictionary<string, IMonitorStation>` over a small interface - `Slug`, `Server`, `State()`, `Row()`, `DisposeAsync` - implemented by the existing `MonitorStation` and by the new one. Built on `hello`, not lazily on a request, because the site's promise is that a station is listed while its uplink is up:

- a `FrameLog` at `frames-<slug>.db`, from the same `MonitorHostOptions.FrameLogDirectory` (:952) the receivers use;
- a `WaterfallWebServer.Relayed(shape, options)` with the same `Public`, `Title`, `About` and `PickerUrl = "../"` the receivers get;
- `SetReceiver(description, url)` where the description is built from the station's identity and the url is its `site` if it passed `HttpUrlOrNull`;
- the links backfill from the log (`StationFactory.BackfillLinks` :324), so a station that has been up before opens with its links already drawn;
- `ViewersChanged` subscribed, `Start()`, `_router.Add($"/r/{slug}/", Server)`;
- and, unlike a `MonitorStation`, no channel, no modems, no `Station`, no thread.

Kept for the life of the process once built, exactly as receivers are, so the page and the history survive. On disconnect the station stops being `offered` and its status chip says who it is and that they are not connected just now; on reconnect it comes back under the same slug with the same log.

**The linger applies to demand, not to a session.** When the last viewer leaves, the monitor waits `monitor.lingerSeconds` (the same 60 s) before sending `demand{spectrum:false}`, so a page refresh or a tab switch does not stop and restart a home station's stream. Same knob, same reason, one fewer thing to explain.

**`/api/instances`** gains `kind`, which is `receiver` or `station`. For a station: `slug`, `kind`, `callsign`, `operator`, `location`, `radio`, `modes` (the mode names off the `hello`'s bands), `publicUrl` (the operator's `site`), `offered` (true while connected), `why` (`"not connected just now"` when it is not), `state`, `status`, `viewers`. `snrDb`, `availableClients`, `maxClients`, `host` and `loadStatus` are null or absent: they are facts about a web receiver and inventing them for a station would be inventing them. The URL keeps its name even though "instances" is now a misnomer, because it is documented, it is polled by a page in the wild, and a rename buys nothing.

**The picker.** Whether stations and receivers are one list or two headed sections is Tom's (section 8). Both work with what `rowHtml` already does (`monitor.html`:158-175, five cells, every row a link):

- **One list**, sorted by callsign as now, with the station's radio in the cell where a receiver's `name` goes and its location in the same cell as a receiver's, leaving the signal and slots cells empty on station rows. Cheapest, and it makes the point that a visitor is choosing an ear rather than a kind of hardware.
- **Two sections**, "Stations on the air" and "Web receivers", each with its own header row and its own columns: a station shows callsign, operator, location, radio and the modes it runs; a receiver shows what it shows now. Slightly more page, no empty cells, and it lets each heading carry the sentence that is true of it - that a station is somebody's own radio and may go off when they do, and that a receiver is a public SDR.

Either way, nothing in a row says who else is watching, or how many, or what state anything is in. That is settled (monitor-plan.md section 8, 2026-09-04) and this project does not reopen it.

**The credit line** on the receiver page is currently a hard-coded sentence naming an UberSDR receiver (`waterfall.html`:658):

> Heard on X, an UberSDR web receiver; the session is opened only while somebody has this page open.

A relayed station needs its own, and the right change is a `cfg.receiverKind` field the page switches on, not a sentence sent from the server: the sentence contains an anchor built around an escaped name, so a server-supplied sentence would be either unescapable or a new injection path. Two sentences in the page, one per kind, and the station's says whose radio it is and that they are hearing it live.

### 4.5 Bandwidth, and the numbers a home connection actually sees

All figures are the wire, including the 8-byte client-to-server WebSocket frame header (2 base, 2 extended length, 4 mask). TLS and TCP add about 3 per cent on top.

At **12 kHz**, which is what GB7RDG-2 and every packet station in this repo runs unless a modem demands 48, the FFT is 2048 points and a line is 1024 bins, one byte each, plus a 5-byte header: 1029 bytes on the wire as 1037.

| Stream | 12 kHz station | 48 kHz station |
|---|---|---|
| Spectrum at 30 lines/s (the page's own rate) | 31.1 kB/s, **249 kbit/s** | 123.3 kB/s, **986 kbit/s** |
| Spectrum at 10 lines/s (the proposed default) | 10.4 kB/s, **83 kbit/s** | 41.1 kB/s, **329 kbit/s** |
| Audio at the channel rate | 24.3 kB/s, **194 kbit/s** | 96.4 kB/s, **771 kbit/s** |
| Audio decimated to 12 kHz | 24.3 kB/s, **194 kbit/s** | 24.3 kB/s, **194 kbit/s** |
| Frames | a few hundred bytes each, tens per hour |
| Idle, nobody watching | one ping and one `demand` every 20 s, and whatever frames the band produces |

So the three cases that matter, on the defaults:

- **Nobody watching:** under 1 kbit/s averaged, and most of it is decoded frames. Five hundred frames an hour at 400 bytes is 0.44 kbit/s. This is the number the promise "an idle opt-in costs a home connection nothing" has to survive, and it does.
- **Somebody watching, not listening:** **86 kbit/s** upstream. That is one decent-quality video call's worth on the quiet side, and it is inside the upload of even a poor ADSL line.
- **Somebody watching and listening:** **290 kbit/s** upstream.

At the page's own 30 lines a second those become 256 and 460 kbit/s, which is still fine on FTTC and unpleasant on ADSL with a VoIP call going. Hence 10 as the default and 30 as an option for an operator who knows their line. A 48 kHz station at 30 lines a second is nearly 1 Mbit/s for the spectrum alone, which is why the default is a rate rather than "same as the page".

**What is not being done, and why.** `permessage-deflate` would take perhaps a third off the spectrum stream, and it is not available: .NET's `HttpListener` does not negotiate WebSocket compression, and the monitor is the server. Opus would take the audio from 194 kbit/s to about 24, and it is a new native dependency, a browser decode path and a licence question, for a stream that only flows while somebody has actually pressed a button. Neither earns its place in this project; both are recorded here so the next person does not have to work out that they were considered.

**One copy, however many viewers.** The station sends one stream to the monitor and the monitor fans it out. Ten people watching GB7RDG-2 cost GB7RDG-2 the same 86 kbit/s as one. That is the same promise the monitor makes to receiver operators about sessions, and it is a test rather than a hope.

### 4.6 Structurally one way

The page already hides transmit and configuration on a public deployment. That is not what makes this safe, and saying "the page hides it" would be the wrong answer to an operator asking whether a website can key their radio. The answer is that there is nothing to hide, in four layers:

1. **The station opens the connection and no port is opened.** There is no listener, no certificate, no port forward and no DNS record. The monitor cannot reach the station at all; it can only answer on a socket the station made.
2. **There is one inbound message type.** `demand`, carrying an integer and two booleans. The reader's `switch` has one case and a default that increments a counter and drops the message. A test asserts that a `config`-shaped message, a KISS-shaped message and a transmit request all do nothing.
3. **The client holds nothing that can act.** `UplinkClient`'s constructor takes a `WaterfallWebServer` and a settings record. It has no `SoundModemChannel` (so it cannot call `EnqueueTransmit`), no `IPttControl`, no `KissTcpServer`, no `ConfigApi` and no `Station`. This is enforced by a reflection test over the type's fields in the style of `SourceTextTests`, so a later change that adds one fails the build rather than being noticed in review or not.
4. **The three things `demand` can do are all bounded.** It can make the station produce spectrum it is already computing; it can make it produce audio it is already receiving; and, on an on-demand UberSDR station only, it can cause a session to be opened on a receiver, which is exactly what a browser on the station's own LAN can already do. It cannot transmit, retune, reconfigure, restart, read a file, or make the station connect to anything other than the URL in its own config.

The remaining honest exposure runs the other way and should be said plainly: **a relayed station is a semi-trusted publisher.** Every string it sends reaches a public page and `/api/instances`, and the site vouches for nothing except that the token belongs to that operator. A station could claim to have heard a callsign it did not. The mitigations are the ones any publisher gets: the token is issued to a person, the credit line names them, `deny` removes them, and everything they send is treated exactly as the third-party directory's strings already are.

- **Length caps at the boundary**, which the directory's strings do not currently have: callsign 16, operator 40, location 60, radio 60, mode 24, a frame's `why`/`il2p`/`hex` 256, a radio status sentence 200. Over the cap is a refused `hello` or a dropped message, not a truncation, so nothing arrives half-said.
- **The same URL check.** `site` goes through `HttpUrlOrNull` (`UberSdrDirectory.cs`:583-587) at the boundary and is checked again by both pages at the place that writes the attribute (`monitor.html`:140, `waterfall.html`:656). That is the fix for the `javascript:` hole the PR #388 review found, and a station is exactly the same class of input.
- **The same journal flattening.** Anything from a station that reaches the journal goes through `UberSdrDirectory.Ascii` (:623-637), because `journalctl`'s pager under a C locale renders a byte above 0x7F as `<E2><80><94>` and `SourceTextTests` cannot catch runtime data.
- **A gap to close while we are here.** `waterfall.html`:1142-1145 writes a frame's `from` and `to` into `innerHTML` **without** `esc()`, unlike every neighbouring site (:1355, :1410, :1457). It is currently safe only because `Ax25AddressParser.TryReadAddress` (:58-61) restricts a callsign to `[A-Z0-9]` and an SSID. That is an implicit dependency on a parser the relayed path does not go through, since a relayed frame's `from` arrives as a string over a socket. Escape it in Phase 1 and stop depending on the coincidence.
- **Message and rate caps.** A `hello` over 8 KB, a text message over 16 KB, a spectrum message that is not exactly `5 + lineLength` bytes, an audio message that is not `4 + 2 x blockSamples`: close the connection, journal one line, apply the `Refused` backoff to that token. A sustained rate over twice the declared `linesPerSecond`, or over 200 kB/s in total: same. The monitor's own fan-out queue is already bounded and drops oldest (:1770-1777), so a flood cannot back up into memory once it is past the reader; the reader itself is the thing that has to be bounded, and `UberSdrAudioInput`'s unbounded accumulator (:453) is the pattern to avoid.
- **A cap on uplinks**, which is the size of the token table: an unauthenticated upgrade is refused before anything is allocated. Bad tokens get a fixed 1 s delay and a counted journal line, so a guessing run is slow, visible, and additionally sitting behind Cloudflare's 60-requests-per-10-seconds rule.

**TLS and the tunnel.** The uplink goes to the same hostname as the browsers, so it is `wss` terminated by Cloudflare and carried to the container over the existing `cloudflared` tunnel. WebSockets are on by default on the zone. Two things to check in Phase 4 rather than assume: that the one rate-limit rule (block, 60 requests per 10 s per address, mitigated for 10 s) does not count a long-lived socket as anything more than the single upgrade request that opened it, and that neither Cloudflare nor `cloudflared` drops an idle WebSocket. The 20-second `demand` heartbeat exists so that the second question has an answer we control rather than one we hope for.

### 4.7 Etiquette

Shorter than the monitor plan's, because the relationship is different: these are people who asked to be here.

- **Nothing runs on somebody's station without a viewer.** That is the whole design and it is a test: no viewer, no spectrum, no audio.
- **The credit names the operator**, on the page and in the picker. It is their radio and their electricity.
- **Leaving is one line of config**, and it takes effect at the next restart. The site does not get a vote.
- **`deny` is the site's side of the same courtesy** and it is honoured within seconds, because it closes the socket.
- **The site says what it does with what it is sent**, in the footer: that a relayed station's frames are logged and shown, that its history outlives the station going off air, and that the site is a public record of callsigns heard, which is no more than any other monitor site publishes.

## 5. Deployment

A sketch, as the monitor plan's was before it was built.

- **No new container, no new tunnel, no new DNS.** The monitor is CT 146 on proxmox1 (10.45.0.128), hostname `monitor`, 3072 MB, behind the `monitor` tunnel and the `monitor.ukpacketradio.network` CNAME, all of it already there. `/uplink` is one more path on port 8099.
- **The monitor's config** gains a `monitor.uplinks` array with one entry: `GB7RDG-2`, slug `gb7rdg-2`, and the SHA-256 of a token generated on the box. A restart applies it.
- **GB7RDG-2** is `root@pdn-soundmodem`, the live node, whose checkout is `/home/tf/pdn-soundmodem` and whose config is `/etc/pdn-soundmodem/soundmodem.json`. It gets the release `.deb` and a `publish` block. **This is a live node carrying traffic**, so the deployment step is: read its config first, add `publish` and nothing else, restart, and confirm from the journal that every line that was there before is still there and in the same order. If the uplink cannot be added without changing anything else, stop and say so rather than working around it.
- **Memory** should be small and needs measuring rather than asserting. A relayed station has no `SoundModemChannel` and no demodulators, so none of the 31 MB per station the monitor plan measured applies; what is left is a waterfall server, a frame log, a link observer and a page, which the same ablation put at about 1.5 MB. Phase 3 measures it with 0, 1 and 5 relayed stations and writes the number back into this section, the way the monitor plan's Phase 3 did.
- **Rate limit**: unchanged. A station is one upgrade request per reconnect.
- **Upkeep**: unchanged.

Announcing is a separate step and comes after a soak. The people to tell are the ones who would want a token.

## 6. Phases

Each phase is a brief. Work on a branch off `main` in `/home/tf/pdn-soundmodem`; the checkout in `/home/tf/src` is stale. One PR per phase, not merged by the implementer.

**How to run the tests, because it is not what you expect.** The suite is xunit v3 in-process and `dotnet test --filter` is **silently ignored**: it runs everything and reports success, which looks like a fast green run and is not one. To run one class:

```
cd tests/Packet.SoundModem.Tests && dotnet build --nologo -v q && dotnet run --no-build -- -class Packet.SoundModem.Tests.SourceTextTests
```

The whole suite is about 5 minutes and currently 2200-odd tests with about 179 skips gated on hardware or on tools that are not installed. A phase is not done until the whole suite has been run, unfiltered.

`SourceTextTests` fails the build on an em dash or an en dash **anywhere in the repository**, including this document, and on any byte above ASCII in a string the daemon can print. Write plain ASCII: `->` not an arrow, `,` not a middle dot, `-` not a dash.

### 6.0 Phase 0: this document

**Done.** PR #393.

### 6.1 Phase 1: the seam, and the protocol written down

**Scope.** `IWaterfallRelay` and `RelayedFrame` in the library. `WaterfallWebServer` gains the `Relay` property and offers it what it offers browsers; gains `Relayed(shape, options)`, implements `IWaterfallRelay` itself, and gains `SetRelayViewers(int)`. `BroadcastFrame` returns its bytes and takes an optional line index. `_channel` becomes optional and `_source`'s four numbers move into a shape record. The `from`/`to` escaping gap at `waterfall.html`:1142-1145 is closed. The wire format of 4.2 is written into `docs/uplink-plan.md` as the normative reference, with the byte layouts.

**Out of scope.** Any socket. Any config key. Anything in the daemon beyond keeping the existing call sites compiling. `UplinkClient`. `RelayStation`.

**Files expected to change.** `src/Packet.SoundModem/Waterfall/WaterfallWebServer.cs`; new `src/Packet.SoundModem/Waterfall/IWaterfallRelay.cs`; `src/Packet.SoundModem/Waterfall/wwwroot/waterfall.html`; `tests/Packet.SoundModem.Tests/Waterfall/WaterfallWebServerTests.cs`, `WaterfallPageTests.cs`, `browser/page-probe.mjs`; new `tests/Packet.SoundModem.Tests/Waterfall/RelayedWaterfallTests.cs`.

**Tests to add.**

- `A_Relayed_Server_Serves_Its_Page_With_No_Channel_At_All`
- `A_Relayed_Server_Builds_Its_Config_From_The_Shape_It_Was_Given`
- `A_Pushed_Line_Reaches_A_Browser_Byte_For_Byte`
- `A_Pushed_Audio_Block_Reaches_Only_The_Browsers_That_Asked_For_It`
- `A_Pushed_Frame_Is_Listed_And_Read_Into_The_Links_Panel`
- `A_Relayed_Server_Opens_A_Browser_With_Its_History_And_Its_Links`
- `A_Channel_Fed_Server_With_No_Relay_Sends_Exactly_What_It_Sent_Before`
- `A_Relay_Is_Offered_Every_Line_A_Browser_Would_Have_Had`
- `A_Relay_Is_Offered_Lines_When_No_Browser_Is_Watching_At_All`
- `A_Relay_That_Wants_No_Spectrum_Is_Sent_None`
- `A_Relay_That_Wants_No_Audio_Is_Sent_None`
- `A_Relay_That_Throws_Costs_Its_Own_Message_And_Nothing_Else`
- `Relay_Viewers_Are_Added_To_The_Browsers_Watching`
- `Relay_Viewers_Reach_The_On_Demand_Ladder_Like_Any_Other_Viewer`
- `A_Frame_From_A_Callsign_With_Angle_Brackets_Is_Escaped_On_The_Page` (page probe)

**Acceptance criteria.**

1. Full suite green, run in full, not filtered.
2. Every existing test in `WaterfallWebServerTests`, `WaterfallPageTests`, `WaterfallRouterTests` and `MonitorHostTests` passes **unedited**. If one needs editing, the change is wrong: stop and explain rather than editing the test.
3. A golden capture of a whole browser session against a channel-fed server with `Relay` null is byte-identical to one taken from `main`, message for message and in the same order.
4. A relayed server and a channel-fed server serve the same page bytes and the same page version.
5. No `Console` use has appeared anywhere in `src/Packet.SoundModem/`.

**How flavour A is proven unchanged.** Criteria 2, 3 and 4. Additionally, a smoke run of a station config on `main` and on the branch, with stdout and stderr captured and diffed after normalising the temp path and the ephemeral port - the method used in PR #387 and PR #388, and the one that found that the two were byte-identical.

### 6.2 Phase 2: the station side

**Depends on Phase 1. Can run in parallel with Phase 3 in a separate worktree**, since it touches the station path and Phase 3 touches `MonitorHost`, and both are written against 4.2.

**Scope.** `PublishConfig` and its validation. `UplinkClient` in the library: connect, `hello`, `demand`, the reconnect ladder, the keepalive, the decimation and the max-hold, the journal lines. The four lines of wiring in `Program.cs`. `publish.token` redacted by the config API. A `--uplink-token` switch is Phase 3's, with the monitor that consumes it.

**Out of scope.** Anything in `MonitorHost`. The picker. The token table. `CONFIG.md` and the example config, which are Phase 3's so they land in one piece.

**Files expected to change.** `src/Packet.SoundModem.Daemon/DaemonConfig.cs` (the new section, its validation, its entry in the unknown-key list at :1212-1229); `src/Packet.SoundModem.Daemon/Program.cs` (a local, an assignment, the wiring); `src/Packet.SoundModem.Daemon/ConfigApi.cs` (the redaction); new `src/Packet.SoundModem/Waterfall/UplinkClient.cs`; new `tests/Packet.SoundModem.Tests/Waterfall/UplinkClientTests.cs`; `tests/Packet.SoundModem.Tests/Daemon/` for the config validation tests.

**Tests to add.**

- `A_Station_With_No_Publish_Block_Opens_No_Socket_At_All`
- `A_Publish_Block_Without_A_Token_Is_A_Configuration_Error`
- `A_Publish_Block_Without_A_Callsign_Is_A_Configuration_Error`
- `A_Publish_Block_Without_A_Waterfall_Is_A_Configuration_Error`
- `Publish_And_Monitor_Together_Is_A_Configuration_Error`
- `A_Publish_Url_That_Is_Not_Ws_Or_Wss_Is_A_Configuration_Error`
- `A_Publish_Token_Is_Not_Read_Back_By_The_Config_Api`
- `The_Uplink_Sends_Its_Hello_Before_Anything_Else`
- `The_Uplink_Sends_Nothing_Until_A_Viewer_Arrives`
- `The_Uplink_Sends_Spectrum_When_A_Viewer_Arrives_And_Stops_When_They_Leave`
- `The_Uplink_Sends_Audio_Only_While_Audio_Is_Wanted`
- `The_Uplink_Sends_Frames_Whether_Or_Not_Anybody_Is_Watching`
- `Three_Source_Lines_Become_One_Relayed_Line_At_Their_Maximum`
- `A_Relayed_Frames_Line_Index_Is_Scaled_With_The_Lines`
- `A_Dropped_Uplink_Retries_On_The_Transport_Ladder`
- `A_Refused_Token_Backs_Off_To_Quarter_Hours_And_Says_So_Once`
- `A_Dropped_Uplink_Leaves_The_Station_Running_And_The_Exit_Code_Alone`
- `A_Dropped_Uplink_Takes_The_Relay_Viewers_Back_To_Zero`
- `An_Uplink_Ignores_Every_Message_Type_But_Demand`
- `An_Uplink_Client_Holds_Nothing_That_Can_Transmit` (reflection over the type's fields)

**Acceptance criteria.**

1. Full suite green.
2. A station config with no `publish` block produces a journal byte-identical to `main`'s, by the diff method above.
3. `UplinkClientTests` drives a real in-process WebSocket server standing in for the monitor, not a mock, so the framing and the keepalive are actually exercised.
4. An on-demand UberSDR station with a `publish` block and no local browser opens no session until the stub monitor says a viewer arrived, and drops it after the linger when it says they left. This is the test that proves the on-demand ladder is being reused rather than reimplemented.
5. `CONFIG.md` is untouched; Phase 3 documents both ends at once.

**How flavour A is proven unchanged.** Criterion 2, plus the whole suite: nothing in this phase is reachable without a `publish` block, and the one shared file, `ConfigApi.cs`, has its own tests.

### 6.3 Phase 3: the monitor side

**Depends on Phase 1.**

**Scope.** The `/uplink` endpoint on `MonitorHost.FrontDoorAsync`. `monitor.uplinks` and its validation. Token checking, hashing, the constant-time compare, the one-connection-per-token rule, the caps and the refusals of 4.6. `RelayStation` and the `IMonitorStation` split. Slug reservation through `UberSdrDirectory.Bind`. `/api/instances` gaining `kind` and the station fields. The picker rows, in whichever of the two shapes Tom picks. The receiver page's second credit sentence. `pdn-soundmodem --uplink-token`. `CONFIG.md` for both `publish` and `monitor.uplinks`, `soundmodem.example.json` for both, the amendment-log entry in `docs/plan.md`, and the memory measurement written back into section 5 of this document.

**Out of scope.** The container, the tunnel, DNS, the release. Anything that changes what a receiver does.

**Files expected to change.** `src/Packet.SoundModem.Daemon/MonitorHost.cs`; new `src/Packet.SoundModem.Daemon/RelayStation.cs`; new `src/Packet.SoundModem.Daemon/UplinkServer.cs`; `src/Packet.SoundModem.Daemon/MonitorStartup.cs`; `src/Packet.SoundModem.Daemon/DaemonConfig.cs`; `src/Packet.SoundModem.Daemon/UberSdrDirectory.cs` (the reservation only); `src/Packet.SoundModem/Waterfall/wwwroot/monitor.html`; `src/Packet.SoundModem/Waterfall/wwwroot/waterfall.html`; `tests/Packet.SoundModem.Tests/Monitor/*`; `CONFIG.md`; `soundmodem.example.json`; `docs/plan.md`; `docs/uplink-plan.md`.

**Tests to add.**

- `An_Uplink_With_No_Token_Is_Refused`
- `An_Uplink_With_A_Wrong_Token_Is_Refused_And_Delayed`
- `An_Uplink_Whose_Callsign_Does_Not_Match_Its_Token_Is_Refused`
- `A_Station_Cannot_Choose_Its_Own_Slug`
- `A_Second_Connection_On_One_Token_Closes_The_First`
- `A_Connected_Station_Is_Offered_And_A_Disconnected_One_Is_Not`
- `A_Disconnected_Station_Keeps_Its_Page_Its_History_And_Its_Links`
- `A_Reconnecting_Station_Comes_Back_Under_The_Same_Slug_And_The_Same_Log`
- `A_Relayed_Frame_Is_Written_To_The_Stations_Own_Frame_Log`
- `A_Relayed_Frame_Reaches_The_Monitors_Own_Links_Panel`
- `A_Viewer_Arriving_Sends_Demand_And_Leaving_Sends_It_Again_After_The_Linger`
- `Pressing_Listen_Is_The_Only_Thing_That_Asks_For_Audio`
- `Two_Viewers_On_One_Station_Ask_For_One_Stream`
- `A_Station_Slug_Pushes_A_Colliding_Receiver_Onto_Its_Full_Slug`
- `Two_Uplinks_With_One_Slug_Is_A_Configuration_Error`
- `An_Oversized_Hello_Closes_The_Connection`
- `A_Spectrum_Message_Of_The_Wrong_Length_Closes_The_Connection`
- `A_Flooding_Station_Is_Dropped_And_Named`
- `A_Station_String_With_A_Script_Tag_Reaches_The_Page_Escaped` (page probe)
- `A_Station_Site_Url_That_Is_Not_Http_Is_Refused`
- `A_Stations_Name_In_The_Journal_Is_Ascii`
- `The_Instances_Api_Says_Which_Rows_Are_Stations`
- `The_Picker_Lists_A_Station_And_A_Receiver` (page probe)

**Acceptance criteria.**

1. Full suite green.
2. A live smoke: a monitor and a station daemon in two containers or two processes, the station opted in, the page opened, spectrum seen, Listen pressed, audio heard, the tab closed, the stream confirmed stopped after the linger. Keep both journals as evidence.
3. Memory measured with 0, 1 and 5 relayed stations and the per-station figure written into section 5 of this document in the same PR.
4. `CONFIG.md` has a `## publish` section and a `monitor.uplinks` subsection that a stranger could configure from, each in the house shape: the key in backticks, one sentence, a minimal JSON block including the siblings it depends on, a four-column field table, then bold-lead-in paragraphs, then a `**Validation**` paragraph listing every exit-2 condition.
5. The token helper produces a token and a hash that the monitor accepts, demonstrated end to end.
6. The picker looked at in a real browser, both shapes of row, before the PR is opened.

**How flavour A is proven unchanged.** No flavour-A code path is touched in this phase. Run the CT 146 monitor config once more, with no `uplinks` configured, and diff the journal against the Phase 1 capture.

### 6.4 Phase 4: deployment

**Depends on Phase 3. Needs Tom's decisions from section 8.**

**Scope.** Tag a release so there is a `.deb`. Generate a token for GB7RDG-2 and put its hash in the monitor's config. Upgrade the monitor, upgrade GB7RDG-2, add its `publish` block, restart both. Validate through the tunnel: the picker lists it, the page loads, the waterfall runs, Listen works, the frame log fills, `/api/instances` says `kind: station`. Soak overnight with a tab open and a tab closed, and read both journals for reconnects, for a stream held with nobody watching, and for anything the on-demand ladder did that it should not have. Write the deployment facts into the project memory the way the monitor deployment is recorded. Then tell the people who would want a token.

**Out of scope.** Any code change that is not a deployment fix.

**Acceptance criteria.** GB7RDG-2 is on https://monitor.ukpacketradio.network and can be watched and listened to from outside the LAN. Its own journal, over the soak, shows every line it showed before the `publish` block was added, plus the `publish:` lines and nothing else. No stream flows with nobody watching. The uplink survives the night, or reconnects and says why. The memory figure from Phase 3 matches what the container actually uses.

## 7. Order of work

The coordinator updates this list as phases land. `[ ]` not started, `[~]` in progress, `[x]` done.

- [x] **Phase 0** - this document. PR: #393
- [ ] **Phase 1** - `IWaterfallRelay`, the channel-free waterfall server, `SetRelayViewers`, the escaping fix, the wire format written down. PR:
- [ ] **Phase 2** - the `publish` block, `UplinkClient`, on-demand, reconnects, the token redaction. Needs 1; can run beside 3. PR:
- [ ] **Phase 3** - `/uplink`, tokens, `RelayStation`, `/api/instances`, the picker rows, the second credit sentence, CONFIG.md, the example config, memory measured. Needs 1; can run beside 2. PR:
- [ ] **Phase 4** - release, monitor config, GB7RDG-2 opted in, validated live, soaked, recorded, announced. Needs 2 and 3. PR:

## 8. Decisions

### Taken by Tom, before any of it was designed

- **Private stations can opt in.** In his words: "Perhaps individual private pdn-soundmodems (which won't be UberSDR receivers, but full blown transceivers) could also opt in to be selectable on monitor.ukpacketradio.network? i.e. we can see and listen to what each others' stations are hearing?"
- **The station dials out**, with a config block naming the monitor and a token the site owner issues. No inbound connection to a home station, ever.
- **On demand**, as the site already is for web receivers. Nothing while nobody is watching; the monitor says when a viewer arrives and leaves; audio only while somebody has pressed Listen.
- **Strictly one way, receive only.** The uplink carries the display stream up and "viewers present" and "audio wanted" down. Nothing else.
- **Leaving is removing the block**, and the site keeps a `deny`.
- **Same page, same history.** `/r/<slug>/`, the same receiver page, its own frame log on the monitor, and a credit line naming the station and its operator.
- **GB7RDG-2 is the first**, and it is a live node carrying traffic.

### Being asked of Tom

1. **One list or two sections on the picker.** Both are laid out in 4.4. **Recommendation: two headed sections**, "Stations on the air" above "Web receivers". A station and a web receiver are different things to a visitor - one is somebody's own radio and antenna and may go off when they go to bed, the other is a public SDR with a slot count - and their useful columns are different, so one list means empty cells on every station row. Two sections also gives each heading one sentence to say what the thing under it is, which the picker currently has nowhere to put. Against it: it is more page, and the one-list version makes the nicer point that a visitor is choosing an ear rather than a kind of hardware.

2. **What a station row shows.** **Recommendation: callsign, operator, location, radio, and the modes it runs.** The modes are the fact that actually distinguishes one station from another for somebody choosing where to listen, and they come free off the `hello`. The radio is the fact people will most enjoy reading and costs one config line. Against including the radio: it is unverifiable free text and it will end up being where people put jokes.

3. **Do decoded frames flow all the time, or only while somebody is watching?** **Recommendation: all the time**, with `publish.frames: "watched"` for an operator who would rather not. It costs well under 1 kbit/s (4.5), and it is what makes the site's frame log complete rather than a record of when people happened to be looking - a visitor arriving on a quiet Tuesday sees the last fifty frames from the last day instead of an empty panel. This is a small extension of the shape already agreed, which said the station sends nothing while nobody is watching, so it needs saying out loud rather than assuming.

4. **The default relayed line rate.** **Recommendation: 10 lines a second**, against the page's own 30, with max-hold so nothing is lost. It takes the watched-and-not-listening case from 249 to 83 kbit/s, which is the difference between "fine on FTTC" and "fine on anything". Against: the waterfall scrolls visibly less smoothly, and an operator on fibre has no reason to accept that. It is one config line either way; the question is only which way the default points.

5. **Where the token lives on the station.** **Recommendation: `publish.token` in the config file**, redacted by the config API exactly as `api.key` is, because that is the pattern the repo already has and one file is one thing to back up and one thing to get the permissions right on. The alternative is `publish.tokenFile` pointing at a 0640 file, which keeps the secret out of a config an operator might paste into a mailing list. Worth having both eventually; the question is which one Phase 2 ships.

6. **How a station is removed in a hurry.** Deleting its entry from `monitor.uplinks` refuses its next connection, but the monitor's config is only read at start-up, so it needs a restart, and a restart drops every viewer on the site. **Recommendation: accept that for now** - a station is removed because its operator asked or because something is wrong, and neither is a five-second problem - and note that a `deny` that could be reloaded without a restart is a separate small project that the receiver `deny` list would want too.

7. **Whether a private station fed by an UberSDR receiver may opt in.** It may, technically, and the brief allows it. It means the site could show the same physical receiver twice: once directly, and once as decoded by somebody's own daemon with their own modems. **Recommendation: allow it and say nothing special about it.** The two rows are honestly different things - the second is that operator's decoding, which is the point of the whole project - and if a case turns up where it confuses people, `deny` is the answer. Against: it also means a private station can quietly consume a receiver operator's daily allowance on the site's behalf without the site knowing.

8. **Whether the uplink should ever be able to fault the station.** **Recommendation: no**, and Phase 2 tests it. A node passing traffic at 3 a.m. must not restart because a website is unreachable. The cost is that a permanently misconfigured `publish` block is a journal line every fifteen minutes and nothing louder.
