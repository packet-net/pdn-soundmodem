# The adaptive modem's host interface: a design draft

Status: a draft for review, dated 2026-11-26, for #822 (the generic host interface) and #841 (the AX.25 engine using it directly). Nothing is built. Every name marked "proposed" is a suggestion, not an existing API. The protocol it serves is [adaptation-protocol.md](adaptation-protocol.md).

## What exists today

The node does not use KISS in process. packet.net is the node's repository; in packet.net, `src/Packet.Node.Core/Transports/SoundModemFrameTransport.cs` runs a `SoundModemChannel` (`src/Packet.SoundModem/Channel/SoundModemChannel.cs` here) and presents it to the AX.25 engine as an `IAx25Transport` (`src/Packet.Ax25.Transport.Abstractions/`) with three optional facets:

- `ITxCompletionTransport.SendAwaitingCompletionAsync` returns a `TxCompletion` when the audio has left the device.
- `ICarrierSense.ChannelBusy` is the modem's busy state.
- `ICsmaChannelParams` sets TXDELAY, persistence, slot time and tail.

A frame goes to `SoundModemChannel.EnqueueTransmit(subChannel, byte[])`. The modem sees opaque AX.25 bytes, one frame per burst, with no destination, tag or rate. Inbound frames arrive on `FrameReceivedWithQuality` as `Ax25InboundFrame` with optional `RadioMetadata`.

In packet.net, `src/Packet.Ax25/Session/Ax25Listener.cs` waits on a `CarrierSenseGate` before every send. With `Ax25ListenerOptions.RestartT1OnTxComplete` set it sends T1-arming frames through `SendAwaitingCompletionAsync`, then calls `ITimerScheduler.RearmIfRunning(Ax25TimerNames.T1, ctx.T1V)`. N1 and K are seeded when a session is built (`Ax25SessionParameters.N1`, `.K`) and not changed on a live session.

The daemon speaks KISS over TCP: `Kiss/KissTcpServer.cs` answers `KissCommand.AckModeData` by echoing the id when the enqueue task completes, and with `EmitQualityFrames` adds a JSON `RxQuality` frame per data frame. `IModem` (`Modems/IModem.cs`) is frame in, samples out. `IFramePackingModem` is the one precedent for several frames in a burst: each packed frame keeps its own `FrameTransmitted` event and ACKMODE answer.

The prototype's station program has no host interface. A made-up host in the same process always has datagrams and sizes them to what one burst carries.

## What the interface must carry

Operations a host calls:

- **Send (proposed)**: a datagram, a neighbour or everyone, an opaque host tag, an optional "report wanted" flag and a priority, ordinary or answer. Everyone is destination 0 and gets no report. The flag is "Decided" item 4, for hosts with no acknowledgements of their own. Answer marks a datagram as the reply for the first slot after a burst, the slot AX.25 reserves for acknowledgements (6.4.3). A requested report rides on whatever the host sends there, and the modem sends it alone only once the slot has passed unused; with channel access in the modem it cannot tell an answer from ordinary traffic unless told. The call may be refused: the datagram is larger than one burst to that neighbour now carries (the modem never splits, "Decided" item 6), or the queue to that neighbour is full.
- **Cancel / flush (proposed)**: by tag, or everything queued for a neighbour, so stale frames do not go out after a link reset. A datagram already in a burst cannot be recalled.
- **Bind (proposed)**: a callsign to a neighbour. The modem uses 6-bit station numbers; the layer above binds them to callsigns after first contact. Who assigns the numbers, and so how a host first sends to a station it has not heard, is open (#835) and not solved here.
- **Own callsign**, given at open, is needed only for the optional identification burst. Identification otherwise stays with the layer above.
- **Query**: a snapshot of every neighbour's link state, for a host that joins late.

Events the modem raises:

- **Transmitted(tag, neighbour, burst, format, rate, airtime, time out)**, one per datagram, when the burst carrying it has left the audio device. It is the start point for T1.
- **Dropped(tag, reason)**: cancelled, expired in the queue, or the link was forgotten. A datagram never silently vanishes.
- **Received(datagram, from, to, quality)**: `to` is this station or everyone, because a station hears bursts for others too.
- **PathReport(neighbour, recommended format, failed)**: a report came back. It is about the path. "Failed" means the neighbour heard a burst from this station and could not decode it.
- **LinkState(neighbour)**, on change: format and rate now; datagram room in one burst; where the knowledge came from (a report, the modem's own estimate, first contact) and how old it is, which also shows reports going unanswered; and "gone" when the modem forgets the neighbour after ten minutes.
- **ChannelBusy**, for display and for the host's own pacing decisions.
- **Congestion(neighbour, writable)**: push-back for a slow link. The draft's choice: the queue per neighbour is bounded in seconds of airtime, Send is refused when full, and a Writable event follows when it drains.
- **ModemSent(kind, neighbour, format)**: reports and identification the host never asked for, so that a monitor shows everything the station put on air.

Transmitted means the end of the burst's audio, as `SendAwaitingCompletionAsync` does today, plus the airtime and rate.

What the modem cannot claim. There is no "delivered", and no "failed" for a datagram. A decoded burst is known only to the receiver, and the protocol is strict that a report is not an acknowledgement. Transmitted means it went out, PathReport means what the neighbour measured, and neither says a given datagram arrived. Acknowledging, repeating and ordering stay with the host.

How much a burst carries, measured in the prototype. At the most robust format (number 192, 0.25 kbit/s) the longest burst, 252 symbols or about 11 s, carries 348 bytes of prelude and datagrams, so a datagram of up to 342 bytes always fits when the link is at or above that format. At the fastest measured format one burst carries tens of kilobytes.

## How the AX.25 engine would use it

- **T1 from Transmitted.** The engine already has the seam: `RestartT1OnTxComplete` and `RearmIfRunning`. Completion would come from the Transmitted event instead of a per-frame task. The new part is the rate: `Srt` sampling (`Ax25SessionQuirks.Ax25Spec41KarnSrtSampling`) learns slowly when the rate moves several hundredfold. The draft's choice: seed `Srt` and `T1V` from LinkState on connect and on a rate change, and let sampling refine them.
- **Frame size and window.** A default AX.25 frame fits one burst even at the bottom, so N1 need not shrink. What changes with rate is how long that burst is on the air, which T1 must follow, and how many frames one burst holds, which K can follow.
- **Answers.** The engine would mark its acknowledgements and responses (RR, RNR, REJ, SREJ, UA, DM) as answers. Today the engine has no such distinction on the wire path. In packet.net, `src/Packet.Ax25/Session/UFrameSpec.cs` has `IsExpedited`, set by `ActionDispatcher.cs` for Expedited UA and DM, but nothing reads it after construction. S-frame responses carry no priority, and there is no T101 or first-slot logic; T2 only delays an acknowledgement. The marking would be new, made where the dispatcher builds those frames.
- **Failed-report hint.** See decision 3.
- **What the engine stops assuming.** That frames are sent one per burst and timed from enqueue. That a frame's addressee comes from parsing its AX.25 header: Send takes the neighbour. That a transport without a completion signal is a fallback case. Decision 2 covers its carrier-sense gate and CSMA settings.

## In process and over a socket

In process, the form can follow `IAx25Transport`: async sends returning a result, and events or an `IAsyncEnumerable` of events like `ReceiveAsync`. It should be a separate interface, so the modem does not know about AX.25 (#841).

Over a socket, the daemon needs the same operations as messages. In this repository `KissTcpServer` is the multi-client TCP precedent, with per-client queues and an opt-in extension frame, and `WaterfallWebServer` pushes JSON events over a WebSocket. The encoding is left until the socket is built.

## The KISS and ACKMODE adapter (deferred)

It sits on the same interface as one more client. It reads the AX.25 destination (or the first unused digipeater) to choose the neighbour, sends one datagram per frame, and echoes the ACKMODE id when the Transmitted event for that tag arrives. It cannot offer per-datagram "report wanted" or priority, cancel, rate-driven sizing (unless the host opts into metadata frames), push-back beyond TCP, or any verdict on delivery.

## Open decisions for Tom

1. **Neighbour identity at the interface.** Options: the host names neighbours by callsign and the modem keeps the number binding; or numbers cross the interface and the host binds them. Recommendation: callsigns and opaque handles at the interface, numbers hidden, so #835 can settle number assignment behind it.
2. **Who owns channel access.** Today the engine waits on `CarrierSenseGate` and sets TXDELAY, persistence and slot time through `ICsmaChannelParams`. Options: the modem owns carrier sense, persistence and the slot for this mode, and the engine marks its answers; or the engine keeps its gate and the modem only refuses to key while busy. Recommendation: the modem owns it, because it makes transmissions of its own and two gates would fight.
3. **The failed-report hint.** When a lone report marked failed arrives in the slot straight after this station's own burst to that neighbour, the attribution is not a guess: it is that burst. Options: (a) pass "the burst just sent to X was heard and not decoded" to the host with that burst's tags, so the engine may poll at once instead of waiting for T1; (b) pass only the path change and let T1 run. Recommendation: (a). It is a hint with no sequence number, the modem itself resends nothing, and it is the "one fast signal" the protocol document describes under "What the modem adds that AX.25 cannot do".
4. **Order of building.** Options: the in-process interface for the node first, the socket form and the KISS and ACKMODE adapter later; or all together. The first means the standalone daemon cannot use the adaptive mode until the socket and adapter exist. Recommendation: the first.
