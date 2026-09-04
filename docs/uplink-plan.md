# Private stations on monitor.ukpacketradio.network - a station-initiated uplink

**Status: accepted 2026-09-04, work starting.** Tom: "I'm happy with the plan. Get the in-flight work landed, then run the plan." It was revised twice on the day it was written, once to send audio rather than the waterfall and once to send that audio uncompressed; both revisions are Tom's and both are in section 8. Nothing is built yet. Verified against `main` at `eb4e42a` (v0.55.0, the tree that is live in CT 146). The successor project to [docs/monitor-plan.md](monitor-plan.md), which put fifty UberSDR web receivers behind one picker at https://monitor.ukpacketradio.network; this adds a second kind of thing to that picker, and it is somebody's actual station.

Tom, and this is the whole brief:

> Perhaps individual private pdn-soundmodems (which won't be UberSDR receivers, but full blown transceivers) could also opt in to be selectable on monitor.ukpacketradio.network? i.e. we can see and listen to what each others' stations are hearing?

Yes, and the first station to opt in is GB7RDG-2.

The constraint from the monitor plan carries straight over and is still the thing everything bends around: one code base, one binary, one `.deb`, one set of tests, and the flavours are configuration. This project adds a third configuration, not a third program. **Flavour A** is the ordinary station: a config with a `device`, running at somebody's house on ALSA or on a Flex, feeding a node. It gains one new optional section, `publish`, and with that section absent nothing about it changes at all. **Flavour B** is the monitor: one process, the picker, the receivers. It gains a way to accept uplinks. **A station never accepts uplinks and a monitor never publishes one**, and the daemon says so if a config asks for both.

This document is written to be executed by sub-agents working one phase at a time under a coordinator, as the monitor plan was, so each phase in section 6 is a self-contained brief. Section 7 is the checklist the coordinator keeps up to date. Section 8 is the decisions, all of them taken.

## 1. What it is

An operator with a real station - a transceiver on an antenna - puts one block in their config, gets a token from the site owner, and their station appears on https://monitor.ukpacketradio.network alongside the web receivers. A visitor picks it and gets the page they already know: the waterfall, the AX.25 links panel, the decoded frames, and a Listen button that plays what that station's receiver is hearing right now.

**The station dials out.** A home station sits behind NAT on a dynamic address and must not have to open a port, hold a certificate, or run a web server anybody outside can reach. It makes one outbound WebSocket to the monitor and keeps it. The monitor lists the station while that socket is up.

**It sends audio, and the monitor draws the picture.** Tom, 2026-09-04:

> why are we sending the waterfall and not just the audio? Much more bandwidth efficient, and rendering the waterfall isn't high CPU.

Right on both counts, and it turns out to be the smaller change as well. The station relays its receive audio, its decoded frames and, through those frames, its links. **The audio is 16-bit PCM, uncompressed, exactly the samples the station's own modems are reading**, which is a decision of its own and is in 4.5. The monitor runs the same FFT and the same painting it already runs for every UberSDR receiver it shows, over audio that arrives from a socket instead of from a receiver's IQ stream. **The decodes stay the station's own**: the monitor runs no modems for a relayed station, so what the page lists is what that operator's daemon actually decoded, with their modes, their diversity settings and their dial. See 4.1 for what this does to the seam, which is that it nearly removes it.

**Nothing flows until somebody is watching.** The socket sits idle. When a visitor opens the station's page the monitor says so and audio starts; when the last one leaves it stops, after the same 60-second linger the receivers get. Audio flows for anyone watching, whether or not they have pressed Listen, because the audio is what the waterfall is drawn from. Decoded frames are the exception and flow all the time, because they are well under a kilobit a second and they are what makes a quiet band look alive to somebody arriving an hour later.

**Strictly one way.** The uplink carries audio, frames and a status sentence up. Down it carries one message, which says how many people are watching. There is no transmit, no configuration, no KISS, no restart, nothing that could act on the station even if the site were taken over. This is structural rather than a matter of the page hiding buttons, and 4.6 says exactly how.

**Leaving is removing the block.** An operator who has had enough deletes `publish` from their config and restarts; the socket goes and the station leaves the picker within seconds. The site keeps a `deny` of its own for the other direction.

**Same page, same history.** A relayed station lives at `/r/<slug>/` with the identical receiver page, keeps its own frame log on the monitor as `frames-<slug>.db`, and so its history and its links panel survive the station going off the air. The credit line names the station and its operator, because it is their radio, their antenna and their electricity.

## 2. What it is not

- **Not a way in.** The uplink cannot carry a command. See 4.6; it is the section to read first if you are worried, and it is the one whose tests matter most.
- **Not monitor-side demodulation.** Audio goes up; the decoding does not follow it. The monitor builds no modems for a relayed station and every frame on the page came from the station's own decoder. A design that demodulated on the monitor was considered and rejected, and 4.1 keeps the reasons on the record, because it is the obvious thing to try once audio is already crossing the wire.
- **Not a compressed feed.** No codec, no companding. What crosses the wire and what a visitor hears is modem audio, sample for sample, so it is usable by somebody working on a decoder and not only by somebody looking at a picture. 4.5.
- **Not a node service.** Nothing about the uplink touches KISS, the node, transmit, or the station's own operation. A station whose uplink is down is a station with a slightly quieter journal.
- **Not federation.** Stations do not talk to each other, nothing is combined across them, and there is no dedupe, no "heard by three stations" view and no cross-station correlation. Each is its own independent monitor that happens to share a hostname. Same answer as the monitor plan gave for receivers, and for the same reason: it is a different project with a different data model.
- **Not open to anyone.** A station is on the site because the site owner issued it a token. There is no sign-up, no self-service and no discovery.
- **Not for a station on a web receiver.** A daemon whose `device` is `ubersdr:` may not publish; that is Tom's decision of 2026-09-04 and it is a start-up refusal, not a convention. See 8.
- **Not a second site.** One deployment, one hostname, one picker, one list.

## 3. What already exists (verified 2026-09-04 against `main` at `eb4e42a`)

### 3.1 The message stream a browser gets, in full

This is what the monitor has to be able to produce for a relayed station, so it is worth having written down once. Everything below is `src/Packet.SoundModem/Waterfall/WaterfallWebServer.cs` (2125 lines).

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

### 3.2 Where the seam is, now that audio is what crosses it

Sending audio rather than a picture moves the join from the middle of the waterfall server to the edge of the daemon, where there is already a socket-shaped hole in the wall.

**The monitor's side is an existing interface with an existing implementation to copy.** `IAudioInput` is `M0LTE.Radio.Audio.IAudioInput` from the `M0LTE.Radio.Audio` package: `int SampleRate { get; }` and `int Read(Span<float> destination)`, blocking, normalised floats, returning the count written. `Station`'s receive loop is nine lines around it (`src/Packet.SoundModem.Daemon/Station.cs`:180-273): `_input.Read(_inputBuffer)` at :183, then `_channel.ProcessReceive` at :266 or, through a `Decimator`, at :271. `OnDemandUberSdrInput` already demonstrates the idle case - `Read` waits and returns 0 when there is no session, and the loop treats that as "nothing to say" and goes round again. `SoundModemChannel.ProcessReceive` (`SoundModemChannel.cs`:197-218) runs the spectrum source, the burst-SNR tracker, **every modem in `_modems`** and then the receive taps; with no modems added, the loop at :208-211 iterates nothing and the cost is the FFT and the taps.

So **a relayed station on the monitor is an ordinary station**: a `SoundModemChannel` at the relayed audio rate, no `AddModem` calls at all, a `Station` over an `IAudioInput` that reads from the uplink socket, and a `WaterfallWebServer.Routed(channel, options)` exactly as a receiver gets. The waterfall, the audio to browsers, the band overlays, the config message, the page and the router registration all come out unchanged, from code that is already running in production. Nothing needs a channel-free waterfall server, a spectrum wire format, a line-rate option or a shape record; all of that was in the previous revision of this document and all of it is gone.

**Three things are still missing and they are small.**

1. **The modem overlays.** A relayed station has no `IModem` to probe, and `Start` (:630-646) probes `_channel.Modems`. But `WaterfallOptions.DeclaredBands` (:37-52) already exists precisely to draw a band that nothing enumerable carries - it is how ARDOP is drawn - and the loop at :656-668 draws every declared band whose sub-channel has no modem. So the bands come off the wire and into `DeclaredBands`, and that path is already exercised in production.

2. **The frames.** `WaterfallWebServer.OnFrame` (:1027) is wired to `_channel.FrameReceivedWithQuality` (:697), and a `SoundModemChannel` event cannot be raised from outside the class. There is no injection point anywhere on `SoundModemChannel` (its public surface is `AddModem`, `AddReceiveTap`, `ProcessReceive`, `EnqueueTransmit` and a set of events). So one is needed on the waterfall server instead: everything `OnFrame` does after the measurement - `BroadcastFrame` at :1053 and `ObserveLink` at :1073 - taking its fields off the wire. That single entry point also gives the monitor its own `Ax25LinkObserver` state, which is what makes the links panel survive the station going offline.

3. **Telling a transmitted line from a received one.** `OnLine` marks a line `0x03` when `_lineIsTransmit` is set (:1004-1006), and that flag is set only by `PaceTransmitLines` (:955-977), which never runs on the monitor. So the flag needs a second way to be set, from the relayed audio's own per-block kind.

**The station's side is two hook points that already exist as loops over browsers.** The receive tap registered in `Start` (:672-691) gets every block of received audio and already does two things with it, `source.Process` and `BroadcastAudio`. The paced transmit path (:955-977) gets the station's own transmitted audio, released at the rate real time passes so that the display trails the modulator by exactly as much as the sound card does - which is exactly the pacing a relayed picture wants too, and getting it free is why the hook goes there rather than on `TransmittedAudio` (:806), where the whole keyup arrives in one call. `ForDisplay` (:874-884) scales transmitted audio by `TransmitDisplayGainDb = -35` (:862) before it is painted, because a modulator's own output drawn literally saturates the display.

`BroadcastAudio` (:1448) is not the hook, and that is deliberate: it assembles 40 ms `s16` blocks for browsers and returns early when nobody is listening (:1451-1466). The relay wants the float samples before any of that, so `BroadcastAudio` is untouched and a relayed station's audio does not depend on anybody having pressed Listen.

**What the previous revision needed and this one does not:** a `Relayed` factory with no channel, a `SpectrumShape` record, `_channel` becoming nullable across nine sites, a binary spectrum wire format, a max-hold decimator, a `publish.linesPerSecond` knob, and `SetRelayViewers`. The last one has a second reason for going, in 3.4.

### 3.3 The monitor host, and what a second kind of station needs from it

`src/Packet.SoundModem.Daemon/MonitorHost.cs` is 991 lines and holds a `Dictionary<string, MonitorStation>` keyed by slug (:54). `MonitorStation` is a private nested class (:404) built in two stages.

- **Stage one, no network** (`MonitorStation` ctor :421, `Build` :474): a `SoundModemChannel`, the modems, a `FrameLog` opened as `Path.Combine(directory, $"frames-{Slug}.db")` (:452), a `WaterfallWebServer.Routed` with `Public = true` and `PickerUrl = "../"` (:476-489), `SetReceiver` and `SetRadioStatus` (:493-494), the links backfill from the log (:503-506), `ViewersChanged` subscribed (:508), `Server.Start()` (:509), and `_router.Add($"/r/{Slug}/", Server)` (:510).
- **Stage two, on the first browser** (`OnViewersChanged` :578 to `AttachAsync` :598 to `OpenAndRunAsync` :643): open the receiver, build a `Station` (:669-682), run its receive loop on a dedicated long-running thread (:702-714).
- **Faults** (`OnFault` :756, `Fault` :771): a sentence into the journal, into the page's status chip and into `/api/instances`, and a rebuild armed for sixty seconds later if anybody is still watching (`ArmRebuild` :810-856), cleared when that window passes with nobody there (:842-852).
- **Never torn down**: there is no `_stations.Remove` anywhere in the file. A station outlives its receiver leaving the directory, which is what keeps its page, its links and its log alive (:21-25).

A `RelayStation` is the same two stages with the second one much smaller: stage one identical except for no modems and different bands, and stage two is "the uplink is connected and somebody is watching, so run the receive loop over the socket". It needs no receiver to open and no pre-flight, so there is nothing that can take fifteen seconds and nothing to be refused.

Two figures from the monitor plan matter here and both come out well. The measured **31 MB per station** was almost entirely the twenty demodulators of the frequency-diversity banks (monitor-plan.md section 5.3): a relayed station runs none of them. And the CPU of a station is dominated by the same demodulators; what is left for a relayed one is thirty 2048-point FFTs a second and a byte-per-bin scaling, which is what the process already does for every watched receiver.

`/api/instances` is built at :291-321 with rows at :323-345, and its fields are `slug`, `host`, `callsign`, `name`, `location`, `publicUrl`, `snrDb`, `loadStatus`, `availableClients`, `maxClients`, `offered`, `why`, `state`, `status`, `viewers`, `description`. The picker (`monitor.html`) reads only `page`, `title`, `about`, `staleSince`, `problem`, and per row `slug`, `callsign`, `host`, `name`, `location`, `publicUrl`, `snrDb`, `availableClients`, `maxClients`, `offered`. It says nothing about who else is watching and it must not start (monitor-plan.md section 8, 2026-09-04).

The slug rule is `UberSdrDirectory.SlugFor` (:555-568): lower-case, strip a trailing `.instance.ubersdr.org` or `.tunnel.ubersdr.org`, then `Sanitise` (:592-617) reduces every run outside `[a-z0-9]` to a single hyphen and trims the ends. `WaterfallWebServer.ValidatePathBase` (:1727-1745) accepts exactly that character set and says at :1721-1726 that this is deliberate. There is already a reservation mechanism: `UberSdrDirectory.Bind(slug, host)` (:299) and `AssignSlugs` (:461-537), where a slug held for a host that is not currently listed pushes any newcomer onto its full sanitised host instead (`heldForAnAbsentee` :495, `keepsIt` :500). That is exactly the behaviour a reserved station slug wants, and it is already tested.

### 3.4 The station side, and what publishing does not need from it

`Program.cs` is 2606 lines of top-level statements. The device dispatch is one `if/else` chain: pipe :1865, wav-loop :1894, ubersdr :1908, flex :2029, ALSA :2295. The `Station` is built at :2472-2560 and run at :2578; `Station` (545 lines) owns the receive loop, the three watches and the fault model, raises `Faulted` and never ends the process itself (:18-20).

**What "on demand" means on a private station, plainly, because it is easy to misread.** A private station is a transceiver. Its radio is on, its receiver is receiving and its modems are decoding, all the time, whether or not anybody anywhere is watching. That is what the station is for, and the uplink does not change it by one sample. **The only thing that is on demand is the uplink stream itself**: the socket carries no audio until the first site viewer arrives and stops carrying it after the last one leaves. That is true for every device type, ALSA and Flex alike, because it is a property of the uplink and not of the radio. There is no receiver session for a site visitor to open, and nothing a site visitor does reaches the station's radio at all.

The one case where a station's own *input* was itself on demand is the UberSDR-fed one, which Tom's decision of 2026-09-04 rules out (4.3): `Program.cs`:1971 wires `waterfallServer.ViewersChanged += onDemand.SetViewers;` inside `if (uberSdrConfig?.OnDemand == true)`, so a browser attaching makes `OnDemandUberSdrInput.SetViewers` (:220-263) open a session on somebody else's web receiver and the linger drop it again, and if that decision is ever reversed then a `WaterfallWebServer.SetRelayViewers(int)` that adds the site's viewers to `_clients.Count` is the one method that would extend it to the site. ALSA and Flex inputs are opened at start-up and held, and `Station.cs`:490-500 explains why an ALSA `Read` could not be made on-demand even if somebody wanted it.

The reconnect precedent is `UberSdrReconnectPolicy` (`src/Packet.SoundModem/UberSdr/UberSdrReconnectPolicy.cs`, 81 lines): outcomes `Healthy`, `Transient`, `Refused`, `ShortSession` (:11-33), and ladders at :42-48 - a flat 1 s breath after a healthy session, 1 s doubling to 30 s for a transport failure, 60 s doubling to 15 minutes for a refusal, and the first failure of a run waits exactly the base rather than twice it (:68-69). The give-up clock is five minutes (`UberSdrAudioInput.cs`:45), measured on `GetTimestamp` rather than `UtcNow` because an NTP step used to trip it early (:341-343).

The only outbound long-lived socket in the tree is `UberSdrAudioInput`'s `ClientWebSocket` (`ConnectAsync` :603-647), and it carries one hazard worth inheriting from deliberately rather than by accident. It sets `CollectHttpResponseDetails = true` (:621) so a 429 on the upgrade is distinguishable from a transport failure, and it sets **no keepalive at all**. `Station.cs`:481-486 spells out the consequence:

> A hung established WebSocket (half-open TCP; .NET sends pings but by default never times out missing pongs) starves the ring while the pump sits in `ReceiveAsync` believing the session is live: starvation's case.

For the UberSDR input the answer is the starvation watch. A `publish` uplink has no ring and no starvation watch of its own, so it has to set `KeepAliveInterval` and `KeepAliveTimeout` itself. Its reassembly loop should follow `UberSdrAudioInput`:462-476, which does honour `EndOfMessage`, but **not** its accumulator: the `ArrayBufferWriter<byte>` at :453 grows without limit and is the one unbounded WebSocket buffer in the repo.

`new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }` (`UberSdrAudioInput.cs`:54-58) is the other pattern to copy: the default infinite lifetime pins a DNS answer for the life of the process, and the monitor is behind a tunnel that can move.

### 3.5 What the station has to say about itself, and does not have today

There is no station-identity block in the config. `grep` over `DaemonConfig.cs` for a callsign, an operator, a locator or a site name finds two things and neither is usable:

- `modems[].identify.callsign` (`DaemonConfig.cs`:168) is the callsign sent **in Morse on the air**, per modem, and `Program.cs`:679-687 rejects an `identify` block outright on a receive-only station.
- `flex.stationName` (`DaemonConfig.cs`:393) is the name this client registers with a FlexRadio and defaults to the string `pdn-soundmodem`. It never leaves the radio.

`waterfall.title` and `waterfall.about` (`DaemonConfig.cs`:737, :741) are the only operator-authored free text that reaches a public page today. So `publish` has nothing to inherit and must carry its own identity, and the nearest precedent for the shape is `identify.callsign`'s refusal to invent a default for something that is a licence matter (`Program.cs`:705-711).

### 3.6 How UberSDR itself registers, since Tom asked us to copy it

Tom, on how a station should be issued a token: "how does UberSDR itself work? Copy that pattern". So it was looked up. The UberSDR server is `madpsy/ka9q_ubersdr` on GitHub, and the relevant file is `instance_reporter.go` (1812 lines), read at `main` on 2026-09-04, alongside the live collector.

- **The instance mints its own identifier.** `ensureUUID` (:498-515) calls `uuid.New()` (:505) on first run and writes it back into the instance's own config file as `instance_reporting.instance_uuid` (:533). `config/config.yaml.example`:1740-1744 comments it "automatically generated on first run ... This uniquely identifies your SDR instance in the registry ... Do not modify this value manually".
- **There is no credential.** Every 120 s the instance POSTs its whole self-description to `https://instances.ubersdr.org/api/instance/<instance_uuid>` (:802-811). The only headers set are `Content-Type` and `User-Agent` (:825-826). No `Authorization`, no bearer token, no signature, no shared secret anywhere in the file. `GET https://instances.ubersdr.org/api/instances/register` answers `400 Invalid UUID format`, which is the same endpoint family and the same story.
- **The check is a callback.** The collector verifies an instance by fetching its own public URL: the directory carries a `successful_callbacks` counter (40881 on ROCKSDR in the fetch of 2026-09-04), and the report struct's `test` field is documented "collector will verify `/api/description` instead of full callback" (:101). Possession of a publicly reachable host that answers as an UberSDR is what stands in for a credential.
- **There is an operator account, keyed by callsign.** `GET https://instances.ubersdr.org/api/callsign/M0LTE` returns `{"callsign":"M0LTE","public_uuid":"b838fc45-8dd2-4fa8-bb0d-8670244ad5da"}`, the report response carries `email_verified` (:905-916), and `static/create_public.html` is the sign-up page for it. The collector will also create `<callsign>.instance.ubersdr.org` pointing at the instance's source IP on request (`create_domain`, :1690-1699).
- **Listing is opt-in and off by default.** `enabled: false` in the shipped example (:1671).

**So the pattern cannot be copied whole, and the reason is the premise of this project.** UberSDR can accept a self-minted UUID because it calls the instance back on a public URL; a station behind NAT has nothing to call back to. Strip the callback and a self-minted identifier is not a credential at all - anyone could POST any UUID and claim any callsign. What can be copied is everything else, and 4.4 does: listing is opt-in and off by default; the identifier is one opaque string in one config key with the same "generated once, do not edit this by hand" comment; and the site binds it to a callsign, exactly as the collector binds a `public_uuid` to a callsign. The single deliberate difference is that ours is issued by the site rather than minted by the station, and it is issued because there is no callback to stand in for it.

### 3.7 What is greenfield

Everything else. There is no outbound relay of any kind, no token check anywhere except the config API's `X-API-Key` (`ConfigApi.cs`:424-439, which is constant-time via `CryptographicOperations.FixedTimeEquals` and is **not wired at all in the monitor flavour**, `MonitorHost.cs`:121-122), no hashing except a page-version cache-buster (`EmbeddedPage.cs`:30), no `RandomNumberGenerator`, no HMAC, no signatures, no audio codec of any kind, and no per-client or per-address cap anywhere in the process - the monitor plan delegates that to one Cloudflare rate-limit rule and says so (monitor-plan.md section 5.1).

## 4. What to build

### 4.1 The seam: an audio input, and one entry point for frames

**Option 1: relay audio and demodulate on the monitor.** The cheapest thing that could work: `MonitorHostOptions.OpenInput` (`MonitorHost.cs`:986-987) is already a delegate returning an input, so a socket-fed `IAudioInput` makes a relayed station an ordinary `MonitorStation` with no new anything at all. **Rejected**, and it stays on the record because once audio is crossing the wire it is the obvious next step. The monitor would have to be told each station's modem set, dial and sideband and keep them in step by hand, when the point is that a station opts in with one block and the site needs nothing but a token. And, fatally, the decodes on the page would be the monitor's: "what each other's stations are hearing" would quietly become "what the monitor makes of the audio your station sent", the station's own frame log and the site's would disagree for reasons nobody could see, and an operator's diversity banks, offsets and mode choices - the things that make one station hear better than another, and the reason for looking at somebody else's station at all - would count for nothing. It also costs the monitor twenty demodulators and 31 MB per watched station.

**Option 2: relay the display stream** - a spectrum wire format, the station's own lines going up and out to browsers unchanged. This was the previous revision's recommendation and it is superseded. It needed a channel-free `WaterfallWebServer`, a shape record, `_channel` nullable across nine sites, a binary line format, a max-hold decimator and a line-rate knob, and it cost more bandwidth in the watched-and-listening case than sending audio does in every case. Its one real advantage - the monitor never has to render anything - was answered by Tom in one sentence: rendering a waterfall is not expensive, and this process already renders fifty of them.

**Recommended, and what Tom has chosen: option 3, audio up with station-side decodes.** A relayed station is an ordinary station over a socket-fed input, with no modems, and the frames it decoded injected at one entry point. Three additions, all of them additive, and nothing existing changes:

```csharp
namespace Packet.SoundModem.Waterfall;

/// <summary>Where a station's display stream goes when it is not going to a browser.</summary>
public interface IWaterfallRelay
{
    /// <summary>Whether anybody at the far end is watching. False costs the station nothing.</summary>
    bool Wanted { get; }
    void Audio(ReadOnlySpan<float> samples, bool transmitted);
    void Frame(RelayedFrame frame);
    void Radio(string status);
}
```

1. **On the station**, `WaterfallWebServer` gains `public IWaterfallRelay? Relay { get; set; }` and offers it what it already computes at three places: the receive tap in `Start` (:672-691), with `transmitted: false`; the paced transmit loop (:970-975), with `transmitted: true`, so the monitor's picture is paced exactly as the station's own is; and `BroadcastFrame` (:1349), which gains an optional `byte[]? raw` parameter that `OnFrame` and `OnFrameTransmitted` pass and the two public `Report*` entry points (:1295, :1335) do not. `SetRadioStatus` (:495) offers the sentence. Every one of those is `if (_relay is { Wanted: true } relay)`, which on a station with no `publish` block is a null check on a field that is already in cache.

2. **On the monitor**, `WaterfallWebServer` gains `public void PushFrame(RelayedFrame frame)`, which is `BroadcastFrame` plus `ObserveLink` (:1053, :1073) with the fields taken from its argument instead of from a channel event, and `public bool IncomingIsTransmit { get; set; }`, which `OnLine` reads alongside `_lineIsTransmit` (:1004). The transmit flag is set by the relayed station's audio input immediately before `Read` returns a block, and `Read` and `ProcessReceive` are the same thread (`Station.cs`:183, :266), so it is exact rather than nearly right. A block is never half transmitted and half received.

3. **In the daemon**, `UplinkAudioInput : IAudioInput` on the monitor and `UplinkClient : IWaterfallRelay` on the station. Both are new files and neither touches anything that exists.

That is the whole seam: about fifty lines added to `WaterfallWebServer`, none removed and none changed, and every existing test in `WaterfallWebServerTests`, `WaterfallPageTests`, `WaterfallRouterTests` and `MonitorHostTests` passing unedited. If one needs editing, the change is wrong.

**The transmit level is a display constant doing double duty, and it should be said out loud.** `ForDisplay` scales transmitted audio by -35 dB (:862-884) so a modulator's own output does not saturate the picture. The relay is fed from the paced path, which is downstream of that scaling, so the monitor paints it right and a listener hears the station's own transmissions 35 dB below full scale. That is a comfortable listening level by coincidence rather than by design, and Phase 4 should listen to a real keyup and say whether it is right. Roadmap #14 (`docs/roadmap.md`, Tom 2026-09-04) wants exactly this audio present - "our own transmitted audio is included when the station is a pdn-soundmodem transceiver ... so the take is what the station was working, not just what it heard" - and is silent on level.

**One timing wrinkle, and it is real.** A `frame` message crosses the wire as soon as the station decodes it, but the audio that carried the burst is still in the monitor's jitter buffer. `BroadcastFrame` tags the frame onto `_source.NextLineIndex` (:1359), so an uncorrected relayed frame would be tagged five to twelve lines above the burst it belongs to, which on a 30-lines-a-second display is visibly wrong. The fix is to hold a relayed frame until the audio has caught up: `UplinkAudioInput` exposes how many samples it is holding, and `RelayStation` releases a queued frame once that much audio has been read. Small, self-correcting, no drift across a reconnect, and a test.

### 4.2 The wire

Two protocols with independent lifetimes, and this is deliberate. The browser protocol may change whenever the page does, because the page and the server ship in the same binary. The uplink protocol spans two machines running whatever versions their operators have installed, so it is versioned, additive, and **the monitor never forwards a station's JSON to a browser**: it parses everything into typed fields and re-serialises its own messages. A station cannot inject a field into the page's stream, because there is no code path in which its bytes reach a browser unexamined. The one binary stream is checked for its type byte and its exact expected length and decoded into floats.

**The endpoint.** `wss://monitor.ukpacketradio.network/uplink`, a WebSocket upgrade on the same port and through the same tunnel as everything else, handled by `MonitorHost.FrontDoorAsync` (:130) before the `/r/` branch. The router already hands the front door any path no prefix claims, WebSocket upgrades included, so nothing in `WaterfallRouter` changes.

**Authentication** is one HTTP header on the upgrade:

```
Authorization: Bearer pdnsm_<43 url-safe base64 characters>
```

A header rather than a query parameter because a query parameter is written to every log between here and there, Cloudflare's included. `ClientWebSocketOptions.SetRequestHeader` already does this for the UberSDR password (`UberSdrAudioInput.cs`:617, :623).

**Up, station to monitor:**

- `hello`, text, once and first, before anything else: the protocol version, the daemon version, the station's identity from its `publish` block, the audio rate, the block length, the dial and sideband, and the bands as `{sub, mode, lowHz, highHz, centreHz}`. The monitor answers with `welcome` or closes.
- **audio**, binary, `[0x02][kind][2 bytes pad][s16 LE mono at the declared rate]`, only while somebody is watching. `kind` is 0 for received and 1 for the station's own transmission, which is the one field the browser format does not have and the one the monitor needs for `IncomingIsTransmit`. Everything else about it - the 4-byte header, the 40 ms block, the sample format - is the browser format's, so there is one definition of each and there is nothing in the payload to decode. The length is therefore always exactly `4 + 2 * blockSamples` bytes, which is what the monitor checks it against.
- `frame`, text: the display fields of a `frame` message, plus `at` (UTC, ISO 8601), plus `raw` (base64 of the AX.25 bytes, absent on an ident ghost or a reported frame that had none). Always, whether or not anybody is watching.
- `radio`, text: `{"type": "radio", "status": "reference: GPSDO locked"}`, the sentence in the station's own status chip, as `WaterfallWebServer.SetRadioStatus` was given it. Sent once straight after `welcome`, so a session opens with the sentence the station already has rather than with an empty chip until the radio next says something, and then on every change. A station with nothing to say sends none at all, and a station whose sentence is withdrawn sends the message with `status` absent, which is what "there is nothing to say" looks like on the wire.
- `bye`, text, optional: a reason, sent before a planned disconnect, so the journal says "GB7RDG-2: going off air for the night" rather than "GB7RDG-2: connection closed".

**An optional field that is null is left out.** The station writes a key only when it has a value, so `snrDb` is absent on a mode that measured none and `raw` is absent on an ident ghost, where the browser's own `frame` message writes `"snrDb": null` for the same frame. This is the one place the uplink's JSON deliberately differs from the browser's: it is a versioned, additive protocol between two machines on different releases, so a reader has to cope with a missing key anyway, and dropping the nulls takes a frame from about 400 bytes to about 300. The monitor reads every optional field with `TryGetProperty` and treats absent and null as the same thing.

**Links are not on the wire, and that is on purpose.** A link card is a fold over frames. The monitor runs `Ax25LinkObserver` over the relayed `raw` bytes and gets the same cards from the same code, so there is one implementation rather than two that can disagree, the fold survives the station going away and a browser arriving afterwards, and the wire carries no message that exists only to be a summary of another one.

**Down, monitor to station, and this is the entire list:**

- `demand`, text: `{"type": "demand", "viewers": 2}`. Sent on every change and at least every 20 s, which doubles as the heartbeat. There is no "audio wanted" flag any more: the audio is what the picture is drawn from, so one viewer means audio.
- `welcome`, text, once: the slug the monitor has put the station under and the URL of its page, so the station can journal `publish: live at https://monitor.ukpacketradio.network/r/gb7rdg-2/`. The URL is the monitor's own `monitor.publicUrl` with `/r/<slug>/` on the end where the site owner has written one down, and otherwise the name the upgrade arrived under, which a tunnel that rewrites `Host` leaves as nothing worth repeating - so a site behind one says its address outright and `url` is absent only on a site that has not (#402).
- `refused`, text, at most once and in place of a `welcome`: `{"type": "refused", "reason": "says it is GB7RDG-2 and this token was issued to GB7RDG"}`. Sent when the monitor has read the `hello` and will not have it - a callsign that does not match the token, a protocol version this site does not speak, an audio rate it cannot draw - and followed at once by a close carrying the same sentence as its reason text, truncated to the 120 ASCII characters a close frame holds. Either half is enough: a monitor that only closes with a reason says the same thing, and the station reads whichever arrives. A reason arriving before the `welcome` is a refusal, so the station journals the site's sentence on the attempt it happened on and waits on the refusal ladder (60 s doubling to a quarter of an hour, said once an hour) rather than the short-session one, because a mismatched callsign is somebody's mistake to fix rather than a site that is busy or falling over. The same words after a `welcome` are only how a session ended, and its length decides the rung as before.

**The heartbeat is a `demand` and not a ping, and this is a requirement on the monitor.** The station's silence watchdog counts application messages only: a complete text or binary message stamps it and a WebSocket ping or pong does not, because control frames never surface through `ClientWebSocket.ReceiveAsync`. Forty-five seconds without one ends the session and the station reconnects, so a monitor that holds the socket open with control frames alone and sends `demand` only when the count changes takes every station on a quiet band off the site every forty-six seconds, for ever. Send a `demand` at least every 20 s whether or not anything has changed.

**Upward, the station's own keepalive is a transport ping.** It sets `KeepAliveInterval` and `KeepAliveTimeout` to 20 s, so it sends a WebSocket ping every 20 s and abandons a socket that does not answer with a pong inside 20 s. It sends no application-level keepalive: with nobody watching and a quiet band, a healthy station can be silent above the transport indefinitely, and a monitor must not read that as a station that has gone.

**Framing.** The monitor's reader must reassemble on `EndOfMessage` with a hard cap, as `UberSdrAudioInput` does at :462-476 and as `WaterfallWebServer`'s browser reader conspicuously does not (:1837-1852 reads into a 1024-byte buffer and ignores `EndOfMessage` entirely - harmless there, wrong here).

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
  "audioRate": 12000,
  "frames": "always"
}
```

- `url` and `token` are required and there is no default for either. `token` carries the same comment `CONFIG.md` gives `api.key` and that UberSDR gives `instance_uuid`: issued once, pasted in once, not edited by hand.
- `callsign` is required, because a station on a public page that will not say who it is has no business being there, and because the monitor checks it against the token (4.4).
- `operator`, `location`, `radio` and `site` are optional and are what the credit line and the picker row are made of. `site` is refused unless it is an absolute http or https URL, by exactly the check the directory's `public_url` goes through (`UberSdrDirectory.HttpUrlOrNull` :583-587).
- `audioRate` is the relayed audio rate, defaulting to the channel's DSP rate capped at 12000, decimated by the existing `Decimator` (`Station.cs`:105-107 builds one for exactly this job). Integer divisors of the DSP rate only; anything else is a start-up refusal rather than a resampler. The relayed waterfall spans 0 to `audioRate/2`, so a 48 kHz station that leaves the default gets a 0 to 6 kHz picture, and start-up says so and names any modem whose band falls outside it. It is also the only lever an operator on a thin upload has, there being no codec (4.5): 48000 costs 770 kbit/s while somebody is watching and 6000 costs 98, the latter at the price of a 0 to 3 kHz picture and of any modem above 3 kHz.
- `frames` is `always` (the default) or `watched`.

**Validation, all exit 2**, in the style of `Program.cs`:503-518 and `DaemonConfig.ValidateMonitor` (:1073-1156):

- `publish` and `monitor` together: a monitor does not publish. Name both, say to remove one.
- **`publish` on a `device` that starts `ubersdr:`**: Tom's decision of 2026-09-04. The sentence should say why rather than just refuse: a public web receiver is already on the site in its own right, and relaying it a second time through a private daemon would show one operator's antenna twice under two names and spend that receiver's daily allowance on the site's behalf without the site knowing. Pointed at the monitor's own `monitor.allow`/`deny` as the way to have a say about a receiver.
- `url` absent, or not an absolute `ws` or `wss` URL. Plain `ws` to anything but a loopback host is a warning, not a refusal: it is the shape of a smoke test and the shape of a mistake, and the operator should be the one to decide which.
- `token` absent or shorter than 32 characters.
- `callsign` absent, or not a plausible callsign with an optional SSID. There is a parser to lean on in `Ax25AddressParser` (:44-79).
- `audioRate` not a divisor of the channel's DSP rate, or not in 6000..48000.
- No `waterfall` section: the uplink is a client of the waterfall server, and without one there is nothing to publish. Same sentence shape as the on-demand check at `Program.cs`:512-517.
- Field lengths: callsign 16, operator 40, location 60, radio 60. Refused at start-up with the limit named, so the operator finds out at once rather than seeing their sentence cut in half on somebody else's website.

**`publish.token` must be redacted by the config API**, alongside `api.key` (`ConfigApi.cs`:94, :107-109, which writes `"(set, not shown)"`). A station running the config API on its LAN must not hand its uplink token to anyone holding the API key. Four lines, and not optional.

**`UplinkClient`**, in the library at `src/Packet.SoundModem/Waterfall/UplinkClient.cs`, implementing `IWaterfallRelay`:

- Constructed with the `publish` settings, a `TimeProvider` and an `Action<string>? log`, in the library's usual style; the daemon passes `stationJournal.ErrorSink` (`Station.cs`:449) so the lines carry a tag if there ever is one. Its lines are prefixed `publish:`, matching `ubersdr:`, `flex:`, `ptt:`.
- **It holds no `SoundModemChannel`, no KISS server, no PTT, no config API and no `Station`.** Its constructor takes a `WaterfallWebServer` and a settings record and nothing else, which is checkable by reading nine lines, and 4.6 makes it checkable by a test.
- Connect, send `hello`, wait for `welcome`, then loop. `socket.Options.KeepAliveInterval = 20 s` and `KeepAliveTimeout = 20 s`, because .NET's default sends pings and never times out missing pongs (`Station.cs`:481-486) and there is no starvation watch on this path. Belt and braces: a `demand` at least every 20 s from the monitor, and a reconnect if two go missing.
- **Reconnects** on `UberSdrReconnectPolicy`, which needs no changes: `Healthy` for a session that carried traffic, `Transient` for a transport failure (1 s doubling to 30), `Refused` for HTTP 401, 403 or 429 on the upgrade (60 s doubling to 15 minutes), which needs `CollectHttpResponseDetails = true` as `UberSdrAudioInput.cs`:621 sets it. A refused token says so once an hour rather than every minute: it is a mistake somebody has to fix, not a condition that clears itself.
- **Never faults the station.** The uplink is a courtesy. It writes a line and retries for ever; it does not raise `Station.Faulted`, it does not set `radioLost`, and it does not touch the exit code. A node whose owner is asleep must not stop passing traffic because a website is down. Tom, 2026-09-04, asked for exactly this.
- **`Wanted`** is its own state, from the last `demand`, and false whenever the socket is down. It gates the audio and nothing else; frames go up regardless.
- **Blocking**: decimate to `audioRate` if needed, convert to `s16` with the same `Audio.Pcm16.FromFloat` `BroadcastAudio` uses (:1478), assemble 40 ms blocks, send. There is no encoding step, because there is no codec. Received and transmitted audio are never mixed in one block, so `kind` is a property of the block rather than of a sample.

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

- **Tokens are stored hashed.** The monitor only ever compares, so it never needs the plaintext, and a config file that leaks does not hand out working uplinks. Plain SHA-256, no salt and no KDF, and the reason is worth stating rather than leaving to look like an oversight: the token is 256 bits from `RandomNumberGenerator`, so there is no dictionary to defend against and a work factor would only cost the monitor time on every connection. Compared with `CryptographicOperations.FixedTimeEquals` over the 32 raw bytes, as `ConfigApi.cs`:436-438 does over the API key.
- **The callsign is bound to the token**, which is what stops one station claiming another's slug and is the same relationship UberSDR's collector has between a callsign and a `public_uuid` (3.6), with the direction of issuance reversed. The slug comes from the monitor's table, never from the wire; the `hello`'s callsign must match the entry's or the connection is closed with a sentence. A station has no way to ask for a slug at all.
- **One connection per token.** A second one that authenticates the same token closes the first, because a station whose socket has half-closed must not be locked out by its own ghost. The old socket gets a close reason that says so.
- `pdn-soundmodem --uplink-token CALLSIGN` prints a fresh token for that station and its hash, so the site owner does not have to invent either. Fifteen lines, and it is the difference between tokens that are 256 random bits and tokens that are somebody's cat.

**The slug.** `Sanitise(callsign.ToLowerInvariant())` gives `gb7rdg-2`, which is already the character set `ValidatePathBase` demands. It is written in the config next to the callsign rather than derived silently, so the URL a visitor bookmarks is a decision somebody took and not a function that might change. Collision with a receiver's slug is handled by the mechanism that already exists: the monitor calls `UberSdrDirectory.Bind(slug, "<uplink>")` for every configured uplink at start-up, and `AssignSlugs`'s `heldForAnAbsentee` path (:495-501) pushes any colliding receiver onto its full sanitised host. A station wins, because its slug is a callsign somebody was issued and a receiver's is derived from a hostname. Two uplinks with the same slug is an exit 2 at start-up.

**`RelayStation`**, a second kind of thing in `MonitorHost`'s table. `_stations` becomes `Dictionary<string, IMonitorStation>` over a small interface - `Slug`, `Server`, `State()`, `Row()`, `DisposeAsync` - implemented by the existing `MonitorStation` and by the new one. Built on `hello`, not lazily on a request, because the site's promise is that a station is listed while its uplink is up:

- a `SoundModemChannel(audioRate)` with **no modems added at all**;
- a `FrameLog` at `frames-<slug>.db`, from the same `MonitorHostOptions.FrameLogDirectory` (:952) the receivers use;
- a `WaterfallWebServer.Routed(channel, options)` with the same `Public`, `Title`, `About` and `PickerUrl = "../"` the receivers get, the station's own `DialFrequencyHz` and `Sideband`, and its bands in `DeclaredBands`;
- `SetReceiver(description, url)` where the description is built from the station's identity and the url is its `site` if it passed `HttpUrlOrNull`;
- the links backfill from the log (`StationFactory.BackfillLinks` :324), so a station that has been up before opens with its links already drawn;
- `ViewersChanged` subscribed, `Start()`, `_router.Add($"/r/{slug}/", Server)`;
- an `UplinkAudioInput` and a `Station` over it, on its own long-running thread as a receiver's is (`MonitorHost.cs`:702-714), with `StationOptions.SessionLive = () => uplink.Connected && Server.Viewers > 0` so the dead-feed and starvation watches stand down while nobody is watching - the same seam `OnDemandUberSdrInput` uses (`StationOptions.SessionLive`, `Station.cs`:510).

Kept for the life of the process once built, exactly as receivers are, so the page and the history survive. On disconnect the station stops being `offered` and its status chip says who it is and that they are not connected just now; on reconnect it comes back under the same slug with the same log.

**The linger applies to demand.** When the last viewer leaves, the monitor waits `monitor.lingerSeconds` (the same 60 s) before sending `demand{viewers:0}`, so a page refresh or a tab switch does not stop and restart a home station's stream. Same knob, same reason, one fewer thing to explain.

**`/api/instances`** gains `kind`, which is `receiver` or `station`. For a station: `slug`, `kind`, `callsign`, `operator`, `location`, `radio`, `modes` (the mode names off the `hello`'s bands), `publicUrl` (the operator's `site`), `offered` (true while connected), `why` (`"not connected just now"` when it is not), `state`, `status`, `viewers`. `snrDb`, `availableClients`, `maxClients`, `host` and `loadStatus` are null or absent: they are facts about a web receiver and inventing them for a station would be inventing them. The URL keeps its name even though "instances" is now a misnomer, because it is documented, it is polled by a page in the wild, and a rename buys nothing.

**The picker: one list, two categories.** Tom, 2026-09-04: "One list with two categories". So the existing five-cell row (`monitor.html`:158-175, every row a link) keeps its shape and its sort, and each row carries its category rather than the page carrying two tables: a small unobtrusive tag in the first cell, `station` or `receiver`, and the two heading sentences that a two-table layout would have carried move into the page's `about` text. A station's row shows callsign, operator, location, radio and the modes it runs; a receiver's shows what it shows now, and the two figures a station has no honest answer for are simply blank rather than borrowed. Sorted by callsign, and by nothing else, as now.

Nothing in a row says who else is watching, or how many, or what state anything is in. That is settled (monitor-plan.md section 8, 2026-09-04) and this project does not reopen it.

**The credit line** on the receiver page is currently a hard-coded sentence naming an UberSDR receiver (`waterfall.html`:658):

> Heard on X, an UberSDR web receiver; the session is opened only while somebody has this page open.

A relayed station needs its own, and the right change is a `cfg.receiverKind` field the page switches on, not a sentence sent from the server: the sentence contains an anchor built around an escaped name, so a server-supplied sentence would be either unescapable or a new injection path. Two sentences in the page, one per kind, and the station's says whose radio it is, that they are hearing it live, and that their own transmissions are in what you hear.

### 4.5 Bandwidth, and why the audio is not compressed

Tom, 2026-09-04, on the proposal to send mu-law:

> losing the ability to get pure modem audio, this seems a shame because others might want to use that audio as a means to tune their own modem designs.

So: **no codec.** The station sends 16-bit PCM, exactly the samples its own modems are reading, and what reaches the site is modem audio rather than a rendering of it. That makes the stream a research artefact as well as a picture. Somebody working on a decoder can point it at what a real station on a real antenna is actually hearing, at 3 a.m., on a band they do not live under; the Listen button hands out those samples; and the record button of roadmap #14 (`docs/roadmap.md`, 2026-09-04) hands out a WAV that is the samples rather than a companded copy of them. A companded or perceptually coded stream would have been fine for both of the things this project set out to do and useless for the third, and the third is the one nobody would have noticed losing until they wanted it.

All figures are the wire, including the 8-byte client-to-server WebSocket frame header (2 base, 2 extended length, 4 mask). TLS and TCP add about 3 per cent on top. The audio block is the browser format's 40 ms, so 480 samples at 12 kHz and 1920 at 48 kHz, and there are 25 blocks a second.

| Stream | `audioRate` 12000, the default | `audioRate` 48000, a station option |
|---|---|---|
| Audio, 16-bit PCM | 24.3 kB/s, **194 kbit/s** | 96.3 kB/s, **770 kbit/s** |
| Frames | about 400 bytes each with the base64 AX.25 bytes: **0.4 kbit/s** at 500 an hour, 3.2 kbit/s on a channel doing one a second |
| Down, and the heartbeat | one short JSON message every 20 s |

So the three cases that matter, on the defaults:

- **Nobody watching:** under 1 kbit/s averaged, almost all of it decoded frames. This is what "an idle opt-in costs a home connection nothing" has to survive, and it does.
- **Somebody watching:** **194 kbit/s** upstream, whether or not they pressed Listen, because the audio is what the picture is drawn from.
- **Ten people watching:** still 194 kbit/s. The station sends one stream and the monitor fans it out, which is the same promise the monitor makes to receiver operators about sessions, and it is a test rather than a hope.

**The ADSL caveat, said honestly rather than buried.** A UK FTTC line uploads 10 to 20 Mbit/s and FTTP a great deal more, so 194 kbit/s is one to two per cent of it and nobody will notice. **ADSL is different**: a typical ADSL upload is 800 kbit/s to 1 Mbit/s, so a watched station is using a fifth to a quarter of it, and that is enough to be felt by a video call or anything else sharing the line - not continuously, but for as long as somebody has the page open. There is no codec to fall back on, by decision, so an operator on ADSL has exactly two levers: set `audioRate` to 6000 and accept a 0 to 3 kHz picture at 98 kbit/s, or not opt in. `CONFIG.md` should say this in the `publish` section rather than leaving it to be discovered, and it should say it as a fact about their line rather than as a warning. **A 48 kHz station cannot sensibly publish from ADSL at all**, and start-up should say so when it sees `audioRate` 48000.

**What was costed and declined**, kept because the question will come back and because the arithmetic was done:

| | on the wire, 12 kHz | why not |
|---|---|---|
| **16-bit PCM** | **194 kbit/s** | chosen: the samples, unaltered |
| mu-law (G.711) | 98 | halves it for thirty lines of table-driven code and no dependency, and its quantisation noise sits about 38 dB below the signal, which after the FFT's 30 dB of processing gain is invisible on the picture. But it is lossy, and the audio stops being modem audio |
| IMA ADPCM, 4-bit | 49 | lossy, and worse than that: the adaptive step tracks the loudest thing in the block, so a strong burst in one modem slot lifts the whole displayed noise floor 15 to 20 dB and greys out the slots either side of it |
| Opus at 32 kbit/s | 34 | lossy, perceptual, and a dependency: `libopus0` by P/Invoke (Debian trixie 1.5.2-2 on amd64, arm64, armel, armhf, i386, ppc64el, riscv64 and s390x, BSD-3-Clause, so a `Depends:` line on the `.deb`) or the managed `Concentus` 2.2.2 from NuGet, also BSD-3-Clause. It preserves per-band energy exactly and synthesises fine structure inside a band at low rates, which is the wrong trade for a waterfall and for a decoder alike |
| FLAC, or any lossless coder | about 160, on noise-like audio | it would keep every sample, which meets the objection. Declined for now anyway: a new dependency and a decode step at both ends for perhaps a fifth, when the thing being protected is precisely that the wire is trivially inspectable. Worth revisiting only if somebody on a thin line actually asks |

`permessage-deflate` would have been the free version of that last row and is not available: .NET's `HttpListener` does not negotiate WebSocket compression and the monitor is the server.

**CPU.** There is nothing to encode, so the station's cost is a float-to-`s16` conversion per sample, which is what `BroadcastAudio` already does for every listening browser (:1478). On the monitor, a watched relayed station costs thirty 2048-point FFTs a second and no demodulators at all, which is less than any receiver it already shows.

### 4.6 Structurally one way

The page already hides transmit and configuration on a public deployment. That is not what makes this safe, and saying "the page hides it" would be the wrong answer to an operator asking whether a website can key their radio. The answer is that there is nothing to hide, in four layers:

1. **The station opens the connection and no port is opened.** There is no listener, no certificate, no port forward and no DNS record. The monitor cannot reach the station at all; it can only answer on a socket the station made.
2. **There are three inbound message types, and between them they carry a slug, a URL, one integer and a sentence.** `welcome`, once, which does nothing but let publishing begin and put a line in the journal; `demand`, which carries a viewer count; and `refused`, which carries the site's reason for not having this station, goes to the journal and ends the session on nothing else - and ending the session is not a power, since the monitor closes the socket a moment later anyway and could have done so without saying anything. The reader's `switch` has those three cases and a default that increments a counter and drops the message, and a second `welcome` is counted and dropped rather than restarting anything. A message whose `type` is not a string, or whose `viewers` is not a number, is dropped as well rather than throwing, so a monitor that grows a field or types one wrongly cannot end a station's session. A test asserts that a `config`-shaped message, a KISS-shaped message, a transmit request and every badly typed shape all do nothing, and that not one of them costs a reconnect.
3. **The client holds nothing that can act.** `UplinkClient`'s constructor takes a `WaterfallWebServer` and a settings record. It has no `SoundModemChannel` (so it cannot call `EnqueueTransmit`), no `IPttControl`, no `KissTcpServer`, no `ConfigApi` and no `Station`. Enforced by a reflection test over the type's fields in the style of `SourceTextTests`, so a later change that adds one fails the build rather than being noticed in review or not.
4. **The one thing `demand` can do is bounded.** It can make the station send audio it is already receiving, over a socket it already opened, at a rate its own config fixed. It cannot transmit, retune, reconfigure, restart, read a file, or make the station connect to anything other than the URL in its own config. With `ubersdr:` publishing refused (4.3), it cannot even cause a session to be opened on somebody else's receiver.

The remaining honest exposure runs the other way and should be said plainly: **a relayed station is a semi-trusted publisher.** Every string it sends reaches a public page and `/api/instances`, and the site vouches for nothing except that the token belongs to that operator. A station could claim to have heard a callsign it did not. The mitigations are the ones any publisher gets: the token is issued to a person, the credit line names them, `deny` removes them, and everything they send is treated exactly as the third-party directory's strings already are.

- **Length caps at the boundary**, which the directory's strings do not currently have: callsign 16, operator 40, location 60, radio 60, mode 24, a frame's `why`/`il2p`/`hex` 256, a radio status sentence 200, a `raw` frame 2048 bytes decoded. Over the cap is a refused `hello` or a dropped message, not a truncation, so nothing arrives half-said.
- **The same URL check.** `site` goes through `HttpUrlOrNull` (`UberSdrDirectory.cs`:583-587) at the boundary and is checked again by both pages at the place that writes the attribute (`monitor.html`:140, `waterfall.html`:656). That is the fix for the `javascript:` hole the PR #388 review found, and a station is exactly the same class of input.
- **The same journal flattening.** Anything from a station that reaches the journal goes through `UberSdrDirectory.Ascii` (:623-637), because `journalctl`'s pager under a C locale renders a byte above 0x7F as `<E2><80><94>` and `SourceTextTests` cannot catch runtime data.
- **A gap to close while we are here.** `waterfall.html`:1142-1145 writes a frame's `from` and `to` into `innerHTML` **without** `esc()`, unlike every neighbouring site (:1355, :1410, :1457). It is currently safe only because `Ax25AddressParser.TryReadAddress` (:58-61) restricts a callsign to `[A-Z0-9]` and an SSID. That is an implicit dependency on a parser the relayed path does not go through, since a relayed frame's `from` arrives as a string over a socket. Escape it in Phase 1 and stop depending on the coincidence.
- **Message and rate caps.** A `hello` over 8 KB, a text message over 16 KB, an audio message that is not exactly `4 + 2 * blockSamples` bytes for the rate the `hello` declared: close the connection, journal one line, apply the `Refused` backoff to that token. A sustained rate over twice that declared bitrate: same. The monitor's fan-out queue to browsers is already bounded and drops oldest (:1770-1777), so a flood cannot back up there; the uplink reader and the input's jitter buffer are what have to be bounded, and `UberSdrAudioInput`'s unbounded accumulator (:453) is the pattern to avoid. The jitter buffer drops the oldest audio when it overruns, which is what a late block deserves.
- **A cap on uplinks**, which is the size of the token table: an unauthenticated upgrade is refused before anything is allocated. Bad tokens get a fixed 1 s delay and a counted journal line, so a guessing run is slow, visible, and additionally sitting behind Cloudflare's 60-requests-per-10-seconds rule.

**TLS and the tunnel.** The uplink goes to the same hostname as the browsers, so it is `wss` terminated by Cloudflare and carried to the container over the existing `cloudflared` tunnel. WebSockets are on by default on the zone. Two things to check in Phase 4 rather than assume: that the one rate-limit rule (block, 60 requests per 10 s per address, mitigated for 10 s) does not count a long-lived socket as anything more than the single upgrade request that opened it, and that neither Cloudflare nor `cloudflared` drops an idle WebSocket. The 20-second `demand` heartbeat exists so that the second question has an answer we control rather than one we hope for.

### 4.7 Etiquette

Shorter than the monitor plan's, because the relationship is different: these are people who asked to be here.

- **Nothing runs on somebody's station without a viewer.** That is the whole design and it is a test: no viewer, no audio.
- **The credit names the operator**, on the page and in the picker. It is their radio and their electricity, and their own transmissions are in what a visitor hears.
- **Leaving is one line of config**, and it takes effect at the next restart. The site does not get a vote.
- **`deny` is the site's side of the same courtesy** and it is honoured within seconds, because it closes the socket.
- **The site says what it does with what it is sent**, in the footer: that a relayed station's frames are logged and shown, that its history outlives the station going off air, and that the site is a public record of callsigns heard, which is no more than any other monitor site publishes.
- **Somebody else's receiver is not relayed through the back door.** A daemon on a web receiver may not publish (4.3), so the site can never end up spending a receiver operator's allowance under a name that operator has never heard of.

## 5. Deployment

A sketch, as the monitor plan's was before it was built.

- **No new container, no new tunnel, no new DNS.** The monitor is CT 146 on proxmox1 (10.45.0.128), hostname `monitor`, 3072 MB, behind the `monitor` tunnel and the `monitor.ukpacketradio.network` CNAME, all of it already there. `/uplink` is one more path on port 8099.
- **The monitor's config** gains a `monitor.uplinks` array with one entry: `GB7RDG-2`, slug `gb7rdg-2`, and the SHA-256 of a token generated on the box. A restart applies it.
- **GB7RDG-2** is `root@pdn-soundmodem`, the live node, whose checkout is `/home/tf/pdn-soundmodem` and whose config is `/etc/pdn-soundmodem/soundmodem.json`. It gets the release `.deb` and a `publish` block. **This is a live node carrying traffic**, so the deployment step is: read its config first, add `publish` and nothing else, restart, and confirm from the journal that every line that was there before is still there and in the same order. If the uplink cannot be added without changing anything else, stop and say so rather than working around it.
- **Memory: measured in Phase 3, and it is about 1.4 MB per relayed station.** On x86-64 under .NET 10, the CT 146 configuration (monitor-plan.md 5.1) with `monitor.uplinks` added, stations connected and idle (nobody watching), reading `VmRSS` from `/proc` and quoting it as kB/1000: **89.4 MB with none, 92.1 MB with one, 96.2 MB with five**. That is 1.4 MB per station over the five, and it is live objects rather than GC slack - the same run under `DOTNET_GCConserveMemory=9` gives 90.8, 91.3 and 95.7 MB, which is 1.0 MB per station and the same answer to within the noise of a dev box.

  So a relayed station costs **about a twentieth of what a receiver's station costs**, and the prediction of "about 1.5 MB" was right. The reason is the one the arithmetic in 3.3 gave: the monitor plan measured 31 MB per receiver station and almost all of it was the twenty demodulators of the frequency-diversity banks, and a relayed station runs none of them - what is left is a channel with no modems, a waterfall server, a frame log, a link observer and a page. **It changes no sizing decision**: fifty relayed stations would be 70 MB against the 1.6 GB fifty receivers cost, so the container stays 3 GB for the receivers' sake and the uplinks are free by comparison. Measured on a Debug build on the dev box rather than the release `.deb` in CT 146, so treat the absolute figures as within a few MB and the per-station delta as the number that matters.
- **Rate limit**: unchanged. A station is one upgrade request per reconnect.
- **Upkeep**: unchanged.

**As deployed, 2026-09-04 (v0.56.0).** Monitor: CT 146 upgraded, token issued inside the container with `pdn-soundmodem --uplink-token` (plaintext kept only in a root-only file there and never printed), one `monitor.uplinks` entry added and nothing else changed, backup `soundmodem.json.pre-uplink`; all 49 receivers, the picker and the pages unchanged through both restarts. Node: GB7RDG-2 upgraded by Tom, then a `publish` block added and nothing else (backup `soundmodem.json.pre-publish`); the start-up journal after the restart is identical to the one before it line for line, plus `publish: publishing to monitor.ukpacketradio.network as GB7RDG-2` and `publish: live as gb7rdg-2`, and both KISS hosts reattached within 25 s. The `live as <slug>` form rather than a URL is because the tunnel rewrites Host to `127.0.0.1:8099`, so the site cannot name itself. Upstream rate measured on the node's own socket: 0.048 kbit/s with nobody watching, 198.9 kbit/s with one viewer (predicted 194 plus framing), 0.052 kbit/s again 90 s after the viewer left, with the station journalling `1 watching, sending audio` and, after the 60 s linger, `nobody watching, audio stopped`. A station has no row and no page until its first hello. The relayed-station memory delta could not be isolated on the box because receivers came and went during the measurement; the Phase 3 figure stands. Left for the soak: both journals overnight, the keyup level, the announcement.

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

### 6.1 Phase 1: the seam

**Scope.** `IWaterfallRelay` and `RelayedFrame` in the library. `WaterfallWebServer` gains the `Relay` property and offers it audio at the receive tap and at the paced transmit loop, frames from `BroadcastFrame`, and the status sentence from `SetRadioStatus`; `BroadcastFrame` gains an optional `raw`. It also gains `PushFrame` and `IncomingIsTransmit` for the monitor's side. The `from`/`to` escaping gap at `waterfall.html`:1142-1145 is closed. The wire format of 4.2 is the normative reference and this document is where it lives.

**Out of scope.** Any socket. Any config key. Anything in the daemon. `UplinkClient`, `UplinkAudioInput`, `RelayStation`.

**Files expected to change.** `src/Packet.SoundModem/Waterfall/WaterfallWebServer.cs`; new `src/Packet.SoundModem/Waterfall/IWaterfallRelay.cs`; `src/Packet.SoundModem/Waterfall/wwwroot/waterfall.html`; `tests/Packet.SoundModem.Tests/Waterfall/WaterfallWebServerTests.cs`, `WaterfallPageTests.cs`, `browser/page-probe.mjs`; new `tests/Packet.SoundModem.Tests/Waterfall/WaterfallRelayTests.cs`.

**Tests to add.**

- `A_Server_With_No_Relay_Sends_Exactly_What_It_Sent_Before`
- `A_Relay_Is_Offered_Every_Received_Block_The_Channel_Delivers`
- `A_Relay_Is_Offered_Transmitted_Audio_At_The_Rate_It_Is_Painted`
- `A_Relay_That_Is_Not_Wanted_Is_Offered_No_Audio_At_All`
- `A_Relay_Is_Offered_Frames_Whether_Or_Not_It_Wants_Audio`
- `A_Relay_Is_Offered_The_Raw_Bytes_Of_An_Ax25_Frame`
- `An_Id_Beacon_Reaches_A_Relay_Without_Raw_Bytes`
- `A_Relay_That_Throws_Costs_Its_Own_Message_And_Nothing_Else`
- `A_Pushed_Frame_Is_Listed_And_Read_Into_The_Links_Panel`
- `A_Pushed_Frame_Is_Tagged_Onto_The_Current_Line`
- `Incoming_Transmit_Marks_A_Line_As_Ours`
- `A_Channel_With_No_Modems_Still_Draws_Its_Declared_Bands`
- `A_Frame_From_A_Callsign_With_Angle_Brackets_Is_Escaped_On_The_Page` (page probe)

**Acceptance criteria.**

1. Full suite green, run in full, not filtered.
2. Every existing test in `WaterfallWebServerTests`, `WaterfallPageTests`, `WaterfallRouterTests` and `MonitorHostTests` passes **unedited**. If one needs editing, the change is wrong: stop and explain rather than editing the test.
3. A golden capture of a whole browser session against a server with `Relay` null is byte-identical to one taken from `main`, message for message and in the same order.
4. Nothing is removed from `WaterfallWebServer` and nothing existing is changed except `BroadcastFrame`'s signature, which gains an optional parameter.
5. No `Console` use has appeared anywhere in `src/Packet.SoundModem/`.

**What must not change, and how it is proven.** Flavour A and flavour B both, since this phase is in the shared library: criteria 2, 3 and 4, plus a smoke run of a station config on `main` and on the branch, with stdout and stderr captured and diffed after normalising the temp path and the ephemeral port - the method used in PR #387 and PR #388, and the one that found the two byte-identical.

### 6.2 Phase 2: the station side

**Depends on Phase 1. Can run in parallel with Phase 3 in a separate worktree**, since it touches the station path and Phase 3 touches `MonitorHost`, and both are written against 4.2.

**Scope.** `PublishConfig` and its validation, including the `ubersdr:` refusal. `UplinkClient` in the library: connect, `hello`, `demand`, the reconnect ladder, the keepalive, the decimation to `audioRate`, the 40 ms `s16` blocking, the transmitted-audio flag, the journal lines. The wiring in `Program.cs`. `publish.token` redacted by the config API.

**Out of scope.** Anything in `MonitorHost`. The picker. The token table. `--uplink-token`. `CONFIG.md` and the example config, which are Phase 3's so they land in one piece.

**Files expected to change.** `src/Packet.SoundModem.Daemon/DaemonConfig.cs` (the new section, its validation, its entry in the unknown-key list at :1212-1229); `src/Packet.SoundModem.Daemon/Program.cs` (a local, an assignment, the wiring); `src/Packet.SoundModem.Daemon/ConfigApi.cs` (the redaction); new `src/Packet.SoundModem/Waterfall/UplinkClient.cs`; new `tests/Packet.SoundModem.Tests/Waterfall/UplinkClientTests.cs`; `tests/Packet.SoundModem.Tests/Daemon/` for the config validation tests.

**Tests to add.**

- `A_Station_With_No_Publish_Block_Opens_No_Socket_At_All`
- `A_Publish_Block_Without_A_Token_Is_A_Configuration_Error`
- `A_Publish_Block_Without_A_Callsign_Is_A_Configuration_Error`
- `A_Publish_Block_Without_A_Waterfall_Is_A_Configuration_Error`
- `A_Publish_Block_On_A_Web_Receiver_Is_A_Configuration_Error`
- `Publish_And_Monitor_Together_Is_A_Configuration_Error`
- `A_Publish_Url_That_Is_Not_Ws_Or_Wss_Is_A_Configuration_Error`
- `An_Audio_Rate_That_Does_Not_Divide_The_Dsp_Rate_Is_A_Configuration_Error`
- `A_Publish_Token_Is_Not_Read_Back_By_The_Config_Api`
- `The_Uplink_Sends_Its_Hello_Before_Anything_Else`
- `The_Uplink_Sends_No_Audio_Until_A_Viewer_Arrives`
- `The_Uplink_Stops_Sending_Audio_When_The_Last_Viewer_Leaves`
- `The_Uplink_Sends_Frames_Whether_Or_Not_Anybody_Is_Watching`
- `The_Uplink_Sends_Its_Own_Transmitted_Audio_Flagged_As_Ours`
- `Forty_Eight_Kilohertz_Audio_Is_Decimated_To_The_Published_Rate`
- `A_Dropped_Uplink_Retries_On_The_Transport_Ladder`
- `A_Refused_Token_Backs_Off_To_Quarter_Hours_And_Says_So_Once`
- `A_Dropped_Uplink_Leaves_The_Station_Running_And_The_Exit_Code_Alone`
- `An_Uplink_Ignores_Every_Message_Type_But_Demand`
- `An_Uplink_Client_Holds_Nothing_That_Can_Transmit` (reflection over the type's fields)

**Acceptance criteria.**

1. Full suite green.
2. A station config with no `publish` block produces a journal byte-identical to `main`'s, by the diff method above.
3. `UplinkClientTests` drives a real in-process WebSocket server standing in for the monitor, not a mock, so the framing and the keepalive are actually exercised.
4. A station with a `publish` block and no local browser sends nothing at all until the stub monitor says a viewer arrived, proven by counting bytes on the stub's socket over ten seconds.
5. `CONFIG.md` is untouched; Phase 3 documents both ends at once.

**What must not change, and how it is proven.** Flavour A without a `publish` block, by criterion 2; flavour B, which this phase does not touch at all; and the live node, which does not get this code until Phase 4. Nothing in this phase is reachable without a `publish` block, and the one shared file, `ConfigApi.cs`, has its own tests.

### 6.3 Phase 3: the monitor side

**Depends on Phase 1.**

**Scope.** The `/uplink` endpoint on `MonitorHost.FrontDoorAsync`. `monitor.uplinks` and its validation. Token checking, hashing, the constant-time compare, the one-connection-per-token rule, the caps and the refusals of 4.6. `UplinkAudioInput` with its jitter buffer and its transmit flag. `RelayStation` and the `IMonitorStation` split, including the frame-hold that tags a frame onto the burst that carried it. Slug reservation through `UberSdrDirectory.Bind`. `/api/instances` gaining `kind` and the station fields. The picker's one list with two categories. The receiver page's second credit sentence. `pdn-soundmodem --uplink-token`. `CONFIG.md` for both `publish` and `monitor.uplinks`, `soundmodem.example.json` for both, the amendment-log entry in `docs/plan.md`, and the memory measurement written back into section 5 of this document.

**Out of scope.** The container, the tunnel, DNS, the release. Anything that changes what a receiver does.

**Files expected to change.** `src/Packet.SoundModem.Daemon/MonitorHost.cs`; new `src/Packet.SoundModem.Daemon/RelayStation.cs`; new `src/Packet.SoundModem.Daemon/UplinkServer.cs`; new `src/Packet.SoundModem.Daemon/UplinkAudioInput.cs`; `src/Packet.SoundModem.Daemon/MonitorStartup.cs`; `src/Packet.SoundModem.Daemon/DaemonConfig.cs`; `src/Packet.SoundModem.Daemon/UberSdrDirectory.cs` (the reservation only); `src/Packet.SoundModem/Waterfall/wwwroot/monitor.html`; `src/Packet.SoundModem/Waterfall/wwwroot/waterfall.html`; `tests/Packet.SoundModem.Tests/Monitor/*`; `CONFIG.md`; `soundmodem.example.json`; `docs/plan.md`; `docs/uplink-plan.md`.

**Tests to add.**

- `An_Uplink_With_No_Token_Is_Refused`
- `An_Uplink_With_A_Wrong_Token_Is_Refused_And_Delayed`
- `An_Uplink_Whose_Callsign_Does_Not_Match_Its_Token_Is_Refused`
- `A_Station_Cannot_Choose_Its_Own_Slug`
- `A_Second_Connection_On_One_Token_Closes_The_First`
- `A_Connected_Station_Is_Offered_And_A_Disconnected_One_Is_Not`
- `A_Disconnected_Station_Keeps_Its_Page_Its_History_And_Its_Links`
- `A_Reconnecting_Station_Comes_Back_Under_The_Same_Slug_And_The_Same_Log`
- `A_Relayed_Station_Builds_No_Modems`
- `Relayed_Audio_Becomes_A_Waterfall_Line`
- `Relayed_Transmit_Audio_Becomes_A_Line_Marked_As_Ours`
- `A_Relayed_Frame_Is_Written_To_The_Stations_Own_Frame_Log`
- `A_Relayed_Frame_Reaches_The_Monitors_Own_Links_Panel`
- `A_Relayed_Frame_Is_Tagged_Onto_The_Burst_That_Carried_It`
- `A_Viewer_Arriving_Sends_Demand_And_Leaving_Sends_It_Again_After_The_Linger`
- `Two_Viewers_On_One_Station_Ask_For_One_Stream`
- `A_Station_Nobody_Is_Watching_Stands_Its_Dead_Feed_Watch_Down`
- `A_Station_Slug_Pushes_A_Colliding_Receiver_Onto_Its_Full_Slug`
- `Two_Uplinks_With_One_Slug_Is_A_Configuration_Error`
- `An_Oversized_Hello_Closes_The_Connection`
- `An_Audio_Message_Of_The_Wrong_Length_Closes_The_Connection`
- `A_Flooding_Station_Is_Dropped_And_Named`
- `An_Overrunning_Jitter_Buffer_Drops_The_Oldest_Audio`
- `A_Station_String_With_A_Script_Tag_Reaches_The_Page_Escaped` (page probe)
- `A_Station_Site_Url_That_Is_Not_Http_Is_Refused`
- `A_Stations_Name_In_The_Journal_Is_Ascii`
- `The_Instances_Api_Says_Which_Rows_Are_Stations`
- `The_Picker_Lists_A_Station_And_A_Receiver_In_One_List` (page probe)

**Acceptance criteria.**

1. Full suite green.
2. A live smoke: a monitor and a station daemon in two processes, the station opted in over a real socket, the page opened, a waterfall drawn from relayed audio, Listen pressed, audio heard, a frame decoded on the station and listed on the site with its tag on the right burst, the tab closed, the stream confirmed stopped after the linger. Keep both journals as evidence.
3. Memory measured with 0, 1 and 5 relayed stations and the per-station figure written into section 5 of this document in the same PR.
4. `CONFIG.md` has a `## publish` section and a `monitor.uplinks` subsection that a stranger could configure from, each in the house shape: the key in backticks, one sentence, a minimal JSON block including the siblings it depends on, a four-column field table, then bold-lead-in paragraphs, then a `**Validation**` paragraph listing every exit-2 condition.
5. The token helper produces a token and a hash that the monitor accepts, demonstrated end to end.
6. The picker looked at in a real browser, with a station and a receiver in the list, before the PR is opened.

**What must not change, and how it is proven.** Flavour A, no path of which is touched in this phase. Flavour B with no `uplinks` configured: run the CT 146 monitor config once more and diff the journal against the Phase 1 capture, and confirm from `/api/instances` that every receiver row is what it was, field for field.

### 6.4 Phase 4: deployment

**Depends on Phases 2 and 3. Needs a token issued.**

**Scope.** Tag a release so there is a `.deb`. Generate a token for GB7RDG-2 and put its hash in the monitor's config. Upgrade the monitor, upgrade GB7RDG-2, add its `publish` block, restart both. Validate through the tunnel: the picker lists it in the one list with its category, the page loads, the waterfall runs off relayed audio, Listen works, the frame log fills, `/api/instances` says `kind: station`. **Listen to a real keyup** and say whether -35 dB is the right level for the station's own transmitted audio (4.1). **Measure the actual upstream rate** on the station's own line with a watched page and with none, and check it against 4.5's 194 kbit/s and under 1 kbit/s; a station on ADSL is the one that would feel a mistake here. Soak overnight with a tab open and a tab closed, and read both journals for reconnects, for audio flowing with nobody watching, and for anything a watch did that it should not have. Write the deployment facts into the project memory the way the monitor deployment is recorded. Then tell the people who would want a token.

**Out of scope.** Any code change that is not a deployment fix.

**Acceptance criteria.** GB7RDG-2 is on https://monitor.ukpacketradio.network and can be watched and listened to from outside the LAN. No audio flows with nobody watching. The uplink survives the night, or reconnects and says why. The memory figure from Phase 3 matches what the container actually uses. The keyup level and the measured upstream rate are written down.

**What must not change, and how it is proven.** **The live node keeps working**, and this is the one that matters most: GB7RDG-2 carries traffic, and its journal over the soak must show every line it showed before the `publish` block was added, in the same order, plus the `publish:` lines and nothing else. Its KISS hosts stay attached, its node keeps passing frames, and its transmit path is not touched. The public site keeps working for its existing receivers throughout: the picker still lists them, and a receiver page opened before the upgrade and after it behaves the same.

## 7. Order of work

The coordinator updates this list as phases land. `[ ]` not started, `[~]` in progress, `[x]` done.

- [x] **Phase 0** - this document. PR: #393
- [x] **Phase 1** - `IWaterfallRelay`, the audio and frame hooks, `PushFrame`, `IncomingIsTransmit`, the escaping fix. PR: #395
- [x] **Phase 2** - the `publish` block, `UplinkClient`, reconnects, the token redaction. Needs 1; can run beside 3. PR: #397
- [x] **Phase 3** - `/uplink`, tokens, `UplinkAudioInput`, `RelayStation`, `/api/instances`, the picker, the second credit sentence, CONFIG.md, the example config, memory measured. Needs 1; can run beside 2. PR: #398
- [~] **Phase 4** - release, monitor config, GB7RDG-2 opted in, validated live, the keyup level judged and the upstream rate measured, soaked, recorded, announced. Needs 2 and 3. Release v0.56.0 (2026-09-04); live, rate measured, memory recorded; soak, keyup level and announcement outstanding. PR: #399 (link fix in the same release)

## 8. Decisions

Every decision below was taken by Tom. Nothing is open, and the plan is accepted:

> I'm happy with the plan. Get the in-flight work landed, then run the plan - again with you as the coordinator and using sub-agents of models of your choice.

So: the in-flight work lands first, then Phase 1, then Phases 2 and 3 in parallel worktrees, then Phase 4. One PR per phase, a fresh reviewer per PR, and the coordinator coordinates rather than writing the code - the way the v0.55.0 monitor refactor was run.

### In the brief

- **Private stations can opt in.** In his words: "Perhaps individual private pdn-soundmodems (which won't be UberSDR receivers, but full blown transceivers) could also opt in to be selectable on monitor.ukpacketradio.network? i.e. we can see and listen to what each others' stations are hearing?"
- **The station dials out**, with a config block naming the monitor and a token the site owner issues. No inbound connection to a home station, ever.
- **On demand.** Nothing while nobody is watching; the monitor says when a viewer arrives and leaves.
- **Strictly one way, receive only.** The station's display stream up, and the viewer count down. Nothing else.
- **Leaving is removing the block**, and the site keeps a `deny`.
- **Same page, same history.** `/r/<slug>/`, the same receiver page, its own frame log on the monitor, and a credit line naming the station and its operator.
- **GB7RDG-2 is the first**, and it is a live node carrying traffic.

### 2026-09-04, on the first draft

- **Audio goes up, not the waterfall.** In his words: "why are we sending the waterfall and not just the audio? Much more bandwidth efficient, and rendering the waterfall isn't high CPU." Taken as: the station relays audio, frames and status; the monitor renders the picture with the code it already runs; the modems stay on the station so the decodes are still the station's own. This is what 4.1 calls option 3 and it supersedes the first draft's spectrum-up design, together with its wire format, its max-hold decimation and its `linesPerSecond` knob. Monitor-side demodulation stays rejected and stays on the record as 4.1's option 1, because it is what somebody will try next.
- **The station's own transmitted audio is part of what a viewer hears**, flagged so the monitor paints it as ours, matching roadmap #14 (`docs/roadmap.md`, 2026-09-04): "our own transmitted audio is included when the station is a pdn-soundmodem transceiver ... so the take is what the station was working, not just what it heard".
- **The picker is one list with two categories**, not two headed sections. 4.4.
- **A station row shows** callsign, operator, location, radio and the modes it runs.
- **Decoded frames flow all the time**, watched or not.
- **There is no relayed line rate**, this being superseded by the design change; the monitor renders at the page's own 30 lines a second.
- **The token follows UberSDR's pattern as far as it can.** Tom: "how does UberSDR itself work? Copy that pattern." What UberSDR does is in 3.6: the instance mints its own UUID, keeps it in one config key it is told not to edit, and posts it with no credential at all, because the collector calls the instance back on its public URL to check it is real. A station behind NAT has nothing to call back to, so the pattern is copied in every respect except that the identifier is issued by the site rather than minted by the station. 4.4.
- **Removing a station may need a restart.** Accepted.
- **A station on a web receiver may not publish.** A `publish` block on a `device` starting `ubersdr:` is exit 2 at start-up, with a sentence saying why, and it is documented in `CONFIG.md`. It also settles what "on demand" means everywhere else: see 3.4, which is the paragraph to read if anybody thinks a site visitor can reach a station's radio.
- **The uplink never faults the station.** Never. A node passing traffic at 3 a.m. does not restart because a website is unreachable, and the cost is that a permanently misconfigured `publish` block is a journal line every fifteen minutes and nothing louder.

### 2026-09-04, on the second draft

- **No codec.** In his words, refusing the recommended mu-law: "losing the ability to get pure modem audio, this seems a shame because others might want to use that audio as a means to tune their own modem designs." Taken as: 16-bit PCM, exactly as the station's modems hear it. `publish.audioRate` defaults to 12000, which is 194 kbit/s upstream while somebody is watching and under 1 kbit/s while nobody is; 48000 is available as a station option for the wide modes, at 770 kbit/s. No companding, no compression, no `codec` key. mu-law, IMA ADPCM, Opus and lossless coding were all costed and 4.5 keeps the costings as the record of what was declined and why, along with the ADSL caveat that having no codec creates.
- **The plan is accepted and the work starts**, as above.
