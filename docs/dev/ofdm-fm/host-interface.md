# The adaptive modem's host interface: a design draft

Status: a design, decided 2026-11-26 (the choices under Decided) and amended 2026-11-27 (identity, link addresses and station numbers, at the end); built in process as the `Station` class of M0LTE.OfdmFm, with nothing yet over a socket. For #822 (the generic host interface) and #841 (the AX.25 engine using it directly). Every name marked "proposed" is a suggestion, not an existing API. The protocol it serves is [adaptation-protocol.md](https://github.com/M0LTE/M0LTE.OfdmFm/blob/main/docs/adaptation-protocol.md), in the M0LTE.OfdmFm repository.

## What exists today

The node does not use KISS in process. packet.net is the node's repository; in packet.net, `src/Packet.Node.Core/Transports/SoundModemFrameTransport.cs` runs a `SoundModemChannel` (`src/Packet.SoundModem/Channel/SoundModemChannel.cs` here) and presents it to the AX.25 engine as an `IAx25Transport` (`src/Packet.Ax25.Transport.Abstractions/`) with three optional facets:

- `ITxCompletionTransport.SendAwaitingCompletionAsync` returns a `TxCompletion` when the audio has left the device.
- `ICarrierSense.ChannelBusy` is the modem's busy state.
- `ICsmaChannelParams` sets TXDELAY, persistence, slot time and tail.

A frame goes to `SoundModemChannel.EnqueueTransmit(subChannel, byte[])`. The modem sees opaque AX.25 bytes, one frame per burst, with no destination, tag or rate.

In packet.net, `src/Packet.Ax25/Session/Ax25Listener.cs` waits on a `CarrierSenseGate` before every send. With `Ax25ListenerOptions.RestartT1OnTxComplete` set it sends T1-arming frames through `SendAwaitingCompletionAsync`, then calls `ITimerScheduler.RearmIfRunning(Ax25TimerNames.T1, ctx.T1V)`. N1 and K are seeded when a session is built (`Ax25SessionParameters.N1`, `.K`) and not changed on a live session.

The daemon speaks KISS over TCP: `Kiss/KissTcpServer.cs` answers `KissCommand.AckModeData` by echoing the id when the enqueue task completes, and with `EmitQualityFrames` adds a JSON `RxQuality` frame per data frame. `IFramePackingModem` is the one precedent for several frames in a burst: each packed frame keeps its own `FrameTransmitted` event and ACKMODE answer.

The prototype's station program has no host interface; its made-up host sizes datagrams to what one burst carries.

## What the interface must carry

Operations a host calls:

- **Send (proposed)**: a datagram, a station name, an opaque host tag, an optional "report wanted" flag and a priority, ordinary or expedited. The name is opaque bytes (see Bind and names). Everyone is a reserved name and gets no report. The flag is "Decided" item 4 of the protocol document, for hosts with no acknowledgements of their own. Expedited is the specification's PH-EXPEDITED-DATA (section 5.5); ordinary sending is PH-SEIZE and PH-DATA. An expedited datagram is the reply for the first slot after a burst, the slot AX.25 reserves for acknowledgements (T101, 6.7.1.5). A requested report rides on whatever the host sends there, and the modem sends it alone only once the slot has passed unused. The call is refused if the datagram is larger than one burst to that station now carries (the modem never splits) or the queue to that station is full.
- **Cancel / flush (proposed)**: by tag, or everything queued for a station, so stale frames do not go out after a link reset. A datagram already in a burst cannot be recalled.
- **Names**: the host gives a name when it sends, and the modem keeps the list of which 6-bit on-air station number is which name. A name is a string of bytes that the modem compares and never interprets; the AX.25 engine passes callsign and SSID. The host never sees a station number (amended 2026-11-27: the first form had the host bind a name to a number; see the end). How a modem takes a number and learns which number answers to a name is still #835.
- **Identification is the modem's own job, and the host has no call for it.** The host gives the modem the callsign to identify with, once, and the modem does not transmit until it has (amended 2026-11-27: it was a required setting of the mode; see the end). Whenever the modem has been transmitting it identifies on its own clock, whatever the host has or has not sent: once in the protocol's identification burst, which other stations running this modem read, and once in Morse, which anyone can read (the daemon's existing `identify`, see [the configuration reference](../../reference/config.md)). The clock runs only while the modem transmits, as the Morse ident's does today, so an idle station never keys to identify. A host that identifies in its own traffic changes nothing: the modem does not look for it.
- **Query**: a snapshot of every station's link state, for a host that joins late.

Events the modem raises:

- **Transmitted(tag, station, burst, format, rate, airtime, time out)**, one per datagram, when the burst carrying it has left the audio device. It is the start point for T1.
- **Dropped(tag, reason)**: cancelled, expired in the queue, or the link was forgotten. A datagram never silently vanishes.
- **Received(datagram, from, to, quality)**: `to` is this station or everyone, because a station hears bursts for others too.
- **PathReport(station, recommended format, failed)**: a report came back. It is about the path. "Failed" means the station heard a burst from this station and could not decode it.
- **BurstNotDecoded(station, tags)** (proposed): a lone report marked failed arrived in the slot straight after this station's own burst, so the burst carrying those datagrams was heard and not decoded. The attribution is exact. It is a hint with no sequence number, and the modem resends nothing. The engine logs it first and acts on it only after a bench run has shown it right (Decided, 4). It is the "one fast signal" of the protocol document's "What the modem adds that AX.25 cannot do".
- **LinkState(station)**, on change: format and rate now; datagram room in one burst; where the knowledge came from (a report, the modem's own estimate, first contact) and how old it is, which also shows reports going unanswered; and "gone" when the modem forgets the station after ten minutes.
- **ChannelBusy**: the modem owns channel access (see Decided), but the specification's PH-BUSY and PH-QUIET also let the engine suspend its timers while the channel is busy, so the host is told.
- **Congestion(station, writable)**: push-back for a slow link. The draft's choice: the queue per station is bounded in seconds of airtime, Send is refused when full, and a Writable event follows when it drains.
- **ModemSent(kind, station, format)** (proposed): reports and identification the host never asked for, so that a monitor shows everything the station put on air.

Transmitted means the end of the burst's audio, as `SendAwaitingCompletionAsync` does today, plus the airtime and rate.

What the modem cannot claim: there is no "delivered" and no "failed" for a datagram. A decoded burst is known only to the receiver, and a report is about the path, not an acknowledgement. Acknowledging, repeating and ordering stay with the host.

Against the AX.25 2.3 draft, the proposed operations and events map to the physical state machine's primitives (5.5, C2a.1):

| Proposed | Specification |
|---|---|
| Send, ordinary | PH-SEIZE.Request and PH-DATA.Request (the modem seizes the channel itself) |
| Send, expedited | PH-EXPEDITED-DATA.Request |
| Received | PH-DATA.Indication |
| ChannelBusy | PH-BUSY.Indication and PH-QUIET.Indication |
| Transmitted | nearest is PH-SEIZE.Confirm, which confirms the channel was seized, not that the frame has gone |

The interface has no counterpart in the specification for the station name, link state, push-back, BurstNotDecoded and the modem's own bursts.

How much a burst carries, measured in the prototype. At the most robust format (number 192, 0.25 kbit/s) the longest burst, 252 symbols or about 11 s, carries 348 bytes of prelude and datagrams, so a datagram of up to 342 bytes always fits when the link is at or above that format. At the fastest measured format one burst carries tens of kilobytes.

## How the AX.25 engine would use it

- **T1 from Transmitted.** The engine already has the seam: `RestartT1OnTxComplete` and `RearmIfRunning`. Completion would come from Transmitted. `Srt` sampling (`Ax25SessionQuirks.Ax25Spec41KarnSrtSampling`) learns slowly when the rate moves several hundredfold, so the draft seeds `Srt` and `T1V` from LinkState on connect and on a rate change, and lets sampling refine them.
- **Frame size and window.** A default AX.25 frame fits one burst even at the bottom, so N1 need not shrink. What changes with rate is how long that burst is on the air, which T1 must follow, and how many frames one burst holds, which K can follow.
- **Expedited.** The engine would mark its acknowledgements and responses (RR, RNR, REJ, SREJ, UA, DM) as expedited. Today the engine has no such distinction on the wire path. In packet.net, `src/Packet.Ax25/Session/UFrameSpec.cs` has `IsExpedited`, set by `ActionDispatcher.cs` for Expedited UA and DM, but nothing reads it after construction. S-frame responses carry no priority, and there is no T101 or first-slot logic; T2 only delays an acknowledgement. The marking would be new, made where the dispatcher builds those frames. The engine's own gate is removed, and TXDELAY, persistence and slot time are the modem's.
- **BurstNotDecoded** is logged first, then used, as above.
- **What the engine stops assuming.** That frames are sent one per burst and timed from enqueue. That a frame's addressee comes from parsing its AX.25 header: Send takes the station. That a transport without a completion signal is a fallback case. That it gates on carrier sense (`CarrierSenseGate`) or sets CSMA values through `ICsmaChannelParams`.

## In process and over a socket

In process, the form can follow `IAx25Transport`: async sends returning a result, and events or an `IAsyncEnumerable` of events like `ReceiveAsync`. It should be a separate interface, so the modem does not know about AX.25 (#841).

Over a socket, the daemon needs the same operations as messages. `KissTcpServer` is the multi-client TCP precedent here, and `WaterfallWebServer` pushes JSON events over a WebSocket. The encoding is left until the socket is built.

## The KISS and ACKMODE adapter (deferred)

It sits on the same interface as one more client. It reads each frame's AX.25 destination (or the first unused digipeater) to choose the station and its control field to send acknowledgements and responses as expedited without the host saying so. It sends one datagram per frame, and echoes the ACKMODE id when the Transmitted event for that tag arrives. It cannot offer per-datagram "report wanted", cancel, rate-driven sizing (unless the host opts into metadata frames), push-back beyond TCP, or any verdict on delivery. What it does with frames a slow link cannot carry soon is decided when it is built.

## Decided (Tom, 26 November 2026)

1. The list of which on-air station number is which station lives in the modem. The host names a station by an opaque string of bytes that the modem compares and never interprets; the AX.25 engine passes callsign and SSID.
2. The modem owns channel access for this mode. It is the physical state machine of the AX.25 specification (C2a): carrier sense, the priority window T101, the slot delay T102 and TXDELAY T103 (6.7.1.5 to 6.7.1.7). The host asks it to send ordinary or expedited datagrams. This is how a KISS TNC has always behaved, so existing software is not shut out. The node's own engine is the one thing that changes: it stops gating and marks its acknowledgements and responses as expedited.
3. The modem's own report bursts use the priority window, which the specification does not provide for. This is a candidate proposal for the AX.25 2.3 working group, to be raised once the mode has run under a real AX.25 link.
4. The modem passes "the burst carrying these datagrams was heard and not decoded" to the host as a hint, with the datagrams' tags. The engine may treat it as T1 expiring early, and the modem resends nothing. The engine logs the hint first and acts on it only after a bench run has shown the hints to be right.
5. Built in this order: the in-process interface for the node first; the socket form and the KISS and ACKMODE adapter later. What the adapter does with frames a slow link cannot carry soon is decided when it is built. Until then the standalone daemon cannot use the adaptive mode.
6. Identification is a compliance fallback that does not depend on the host. The callsign is required in the modem's configuration; the modem always identifies by itself while it is transmitting, without trying to tell whether the host already has; and it does so both in its own identification burst and in Morse, on the same clock.

## Amended (Tom, 27 November 2026)

7. **The transmitter identifier callsign comes from the host, through the interface.** It is a request the host makes once, not a setting of the modem. Until the modem has one it does not transmit at all: it listens, measures and delivers what it hears. The same callsign given again is accepted and a different one is refused. This replaces "the callsign is required in the modem's configuration" in 6; the rest of 6 stands, and the Morse ident uses the same callsign.
8. **That callsign is not an address.** It is distinct from the names a host sends to and from: a host may transmit as many callsigns and SSIDs, and several stations may use the same transmitter identifier callsign. Nor can it be read out of traffic: on a multi-hop AX.25 connection one station transmits frames whose source address is another's, and the two are indistinguishable on the air.
9. **Station numbers are the modem's, and the host never sees one.** A modem takes its number at random, the modems settle clashes among themselves, and a number may change from time to time. 63 stations in hearing of each other is taken as enough. The host's request to bind a name to a number therefore goes. How a modem chooses, notices a clash and moves is in M0LTE.OfdmFm's `docs/station-numbers.md`; how it learns where to send is 12.
10. **KISS hosts, for the adapter when it is built.** The adapter never takes the callsign from a frame meant for the air (8 is why). The host gives it in something that is never transmitted: a frame made for the purpose, or KISS SETHW; which, is decided when the adapter is built. A host that has not given one is told so loudly, in a UI frame the adapter makes up and passes back to it.
11. **Several clients on one modem** (the socket form, when built): the same callsign from a second client is accepted, a different one is refused.
12. **The modem is a link layer with addresses of its own** ("so effectively MAC addresses and ARP"). Each datagram the host sends has a link address it is going to and one it comes from: opaque bytes the host chooses, which is what "the name" in 1 now is, with a "from" beside it. The modem learns where an address lives from the "from" on each burst it hears, as an Ethernet switch does, and sends to everyone while it does not know. So Send takes two addresses, and the host's events are by link address. The design must stand up to IP running over it, which is why the modem does not read the next hop out of a frame (a routed IP packet does not name it) and why it has addresses of its own at all; an IP host runs its ordinary neighbour discovery over it. For AX.25 the link addresses are the frame's own (the next hop, and the address the station is transmitting as), which the node's engine passes and the KISS adapter reads out of each frame. The full design is M0LTE.OfdmFm's [`docs/station-numbers.md`](https://github.com/M0LTE/M0LTE.OfdmFm/blob/main/docs/station-numbers.md).
13. **No catch-up identification, in this mode or any other.** An identification follows a transmission, the first one after ten minutes have passed since the last; an idle station never keys to identify. The Morse ident follows the same rule on every mode (#905). The closing ident of a transmit lease is a separate thing and is unchanged.
14. **Each received datagram comes with the address it was sent from, and each datagram has a protocol byte.** The first is the layer 2 source, as a source MAC address is: a host whose own header does not say who sent a packet cannot work without it. The second says what a datagram is, so that hosts of different protocols can share a channel, as the PID byte does in AX.25 and the EtherType on a LAN; its values are AX.25's PID numbers, with 0x00 added for a whole AX.25 frame. Both came out of reading a draft network stack by another amateur that is meant to run over any layer 2, which is the kind of host the design must stand up to.
15. **A burst for everyone goes at the best rate that every known neighbour can take**, not always at the slowest. A later step (#907).
16. **The "from" becomes occasional**: sent in full now and then, and as a short number between. A later step (#908).
17. **LinkState gives the rate in both directions**: what this station sends at, and what it has told the other the path from them supports.
18. **Larger datagrams are not provided for and not ruled out.** One burst at the slowest format carries a little over 330 bytes and the modem does not split. Splitting can be added later inside the modem or as a layer above it.
19. **The modem reports channel occupancy.** The share of the last minute and the last ten minutes the channel was busy, split into this station's own transmissions, other stations' bursts it could read, and the rest. A routing layer above can then prefer a quiet channel, and tell traffic from interference. With the rate each way, the room per burst, the backlog and the bursts not decoded, that is what the interface gives a router. A later step (#910).
