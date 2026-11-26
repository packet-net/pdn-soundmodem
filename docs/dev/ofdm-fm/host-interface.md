# The adaptive modem's host interface: a design draft

Status: a draft for review, dated 2026-11-26, for #822 (the generic host interface) and #841 (the AX.25 engine using it directly). Nothing is built. Every name marked "proposed" is a suggestion, not an existing API. The protocol it serves is [adaptation-protocol.md](adaptation-protocol.md).

## What exists today

The node does not use KISS in process. `SoundModemFrameTransport` (`/home/tf/src/packet.net/src/Packet.Node.Core/Transports/SoundModemFrameTransport.cs`) runs a `SoundModemChannel` (`src/Packet.SoundModem/Channel/SoundModemChannel.cs` here) over the audio device and presents it to the AX.25 engine as an `IAx25Transport` (`Packet.Ax25.Transport.Abstractions/IAx25Transport.cs`: `SendAsync`, `ReceiveAsync`) with three optional facets:

- `ITxCompletionTransport.SendAwaitingCompletionAsync` returns a `TxCompletion(Queued, Completed)` when the audio has left the device. It is the "de-KISS-named" form of ACKMODE.
- `ICarrierSense.ChannelBusy` is the modem's busy state.
- `ICsmaChannelParams` sets TXDELAY, persistence, slot time and tail, and the transport writes them into `SoundModemChannel.Csma`.

A frame goes from the engine to `SoundModemChannel.EnqueueTransmit(subChannel, byte[])`, which returns a task that completes when the frame has gone out. Inbound frames arrive on `FrameReceivedWithQuality` and become `Ax25InboundFrame` with optional `RadioMetadata`. The modem sees only opaque AX.25 bytes, one frame per burst, with no destination, tag or rate.

In the engine, `Ax25Listener` (`Packet.Ax25/Session/Ax25Listener.cs`) holds a `CarrierSenseGate` (`CarrierSenseGate.cs`) and waits for a clear channel before every send. With `Ax25ListenerOptions.RestartT1OnTxComplete` set it sends T1-arming frames (I frames, enquiries, SABM, DISC) through `SendAwaitingCompletionAsync`, then calls `ITimerScheduler.RearmIfRunning(Ax25TimerNames.T1, ctx.T1V)`. N1 and K are seeded when a session is built (`Ax25SessionParameters.N1` and `.K`, applied near `ctx.N1 = n1` and `ctx.K = ...`) and are not changed on a live session. `Ax25SessionContext` holds `Srt` and `T1V`.

The daemon speaks KISS over TCP: `KissTcpServer` (`Kiss/KissTcpServer.cs`) handles `KissCommand.AckModeData` (`Kiss/KissCodec.cs`) by enqueueing the frame and echoing the two-byte id when the enqueue task completes. `EmitQualityFrames` (off by default) adds a JSON `RxQuality` frame after each data frame for hosts that opt in. `IModem` (`Modems/IModem.cs`) is frame in, samples out. `IFramePackingModem` is the one precedent for several frames in a burst (MS110D): each packed frame keeps its own `FrameTransmitted` event and ACKMODE answer.

The prototype's station program (`/home/tf/src/ofdm-fm-lab/harness/station_run.py`, `proto/station.py`) has no host interface. A made-up host in the same process always has datagrams and sizes them to what one burst carries (`link.capacity`). No real host is told anything.

## What the interface must carry

Operations a host calls:

- **Send (proposed)**: a datagram, a neighbour or everyone, an opaque host tag, and an optional "report wanted" flag. Everyone is destination 0 and gets no report. The flag is "Decided" item 4, for hosts with no acknowledgements of their own. The call may be refused: the datagram is larger than one burst to that neighbour now carries (the modem never splits, "Decided" item 6), or the queue to that neighbour is full (see push-back).
- **Cancel / flush (proposed)**: by tag, or everything queued for a neighbour. After a link reset stale frames must not go out. A datagram already in a burst cannot be recalled.
- **Bind (proposed)**: a callsign to a neighbour. The modem uses 6-bit station numbers on the air; the layer above binds them to callsigns after first contact. Who assigns the numbers is open (#835) and this draft does not solve it.
- **Own callsign**, given at open: the modem makes its own transmissions and must identify them.
- **Query**: a snapshot of every neighbour's link state, for a host that joins late.

Events the modem raises:

- **Transmitted(tag, neighbour, burst, format, rate, airtime, time out)**, one per datagram, when the burst carrying it has left the audio device. This is what `SendAwaitingCompletionAsync` gives today, but per datagram and with the rate. It is the start point for T1.
- **Dropped(tag, reason)**: cancelled, expired in the queue, or the link was forgotten. A datagram never silently vanishes.
- **Received(datagram, from, to, quality)**: `to` is this station or everyone, because a station hears bursts for others too. Quality as `RadioMetadata` gives today.
- **PathReport(neighbour, recommended format, failed)**: a report came back from that neighbour. It is about the path. "Failed" means the neighbour heard a burst from this station and could not decode it. The report names no burst, so the modem can only say "the last burst sent to you", and can attach the tags in it as a best guess.
- **ReportSilence(neighbour, count)**: a burst that asked for a report was met with silence. The burst or the report may have been lost, or the station gone.
- **LinkState(neighbour)**, on change: format and rate now; datagram room in one burst (the number the host sizes frames to); where the knowledge came from (a report, the modem's own estimate, or first contact) and how old it is; and "gone", when the modem forgets the neighbour after ten minutes.
- **ChannelBusy**, for display. Channel access belongs wholly to the modem, so the host does not gate on it.
- **Congestion(neighbour, writable)**: push-back for a slow link. Proposed form: the queue per neighbour is bounded in seconds of airtime, Send is refused when full, and a Writable event follows when it drains.
- **ModemSent(kind, neighbour, format)**: reports and identification the host never asked for, for monitoring.

What the modem cannot claim. There is no "delivered", and no "failed" for a datagram. A decoded burst is known only to the receiver, and the protocol is strict that a report is not an acknowledgement. Transmitted means it went out, PathReport means what the neighbour measured, and neither says a given datagram arrived. Acknowledging, repeating and ordering stay with the host.

## How the AX.25 engine would use it

- **T1 from Transmitted.** The engine already has the seam: `RestartT1OnTxComplete` and `RearmIfRunning`. Today completion comes from a per-frame task; it would come from the Transmitted event. The new part is the rate: `Srt` sampling (`Ax25SessionQuirks.Ax25Spec41KarnSrtSampling`) learns slowly when the rate moves several hundredfold. The engine could seed `Srt`/`T1V` from LinkState on connect and on a rate change, then let sampling refine it.
- **Frame size and window from burst room.** N1 would follow the datagram room in LinkState. At the lowest rates a burst may carry less than a default AX.25 frame, so a large N1 is refused by Send; the segmenter (`Ax25Listener`'s over-N1 path) already exists. K follows from how many frames one burst holds, since several datagrams share a burst.
- **PathReport "failed"** could trigger the poll that T1 would send seconds later. It is a hint, not a sequence number; the engine recovers in its own way.
- **What the engine stops assuming.** That frames are sent one per burst and timed from enqueue. That N1 and K are fixed for a session's life (today build-time only). That it gates on a carrier-sense source (`CarrierSenseGate`) and sets TXDELAY, persistence and slot time (`ICsmaChannelParams`); for this port those are the modem's. That a frame's addressee comes from parsing its AX.25 header: Send takes the neighbour.

## In process and over a socket

In process, the form can follow `IAx25Transport`: an interface with async sends returning a result, and events, or an `IAsyncEnumerable` of events like `ReceiveAsync`. It should be a separate interface from `IAx25Transport` so that the modem does not know about AX.25 (#841).

Over a socket, the daemon needs the same operations as messages. Two things in this repository could be copied. `KissTcpServer` is the multi-client TCP precedent, with a client list, per-client queues and an opt-in extension frame. `WaterfallWebServer` pushes JSON events over a WebSocket. A versioned message stream over TCP or a Unix socket, one JSON object per message, would carry the same operations and events.

## The KISS and ACKMODE adapter (deferred)

It sits on the same interface as one more client. It reads the AX.25 destination (or the first unused digipeater) to choose the neighbour, sends one datagram per frame, and echoes the ACKMODE id when the Transmitted event for that tag arrives.

It cannot offer per-datagram "report wanted", cancel, LinkState-driven frame sizing (unless the host opts into metadata frames), push-back beyond TCP, or any verdict on delivery. A KISS host that sends frames larger than a burst carries gets them refused.

## Open decisions for Tom

1. **Neighbour identity at the interface.** Options: the host names neighbours by callsign and the modem keeps the number binding; or numbers cross the interface and the host binds them. Recommendation: callsigns and opaque handles at the interface, numbers hidden, so #835 can settle number assignment behind it.
2. **Sending to a station not yet bound.** First contact has no number to address. Options: the host sends to everyone until a binding exists; or Send to an unbound callsign is refused until the modem has heard it. This depends on #835 and cannot be settled here. Recommendation: let #835 decide, and leave the call shape open.
3. **Attributing a "failed" report.** The report names no burst. Options: expose it as "last burst failed" with a best-guess list of tags; or expose only the path event. Recommendation: the best guess, labelled as such, because the engine can act on it sooner than T1.
4. **Meaning of Transmitted.** Options: end of the burst's audio, matching today's completion; or the moment of keying. Recommendation: end of audio, with airtime in the event, as T1 should start there.
5. **Push-back form.** Options: a per-neighbour queue bounded in airtime with refuse and Writable; or credits the host spends. Recommendation: the airtime bound, because it states the cost of a slow link directly.
6. **How far the engine follows the rate.** Options: seed T1 and N1 from LinkState and keep the existing sampling; or compute T1 from the rate each time. Recommendation: seed and sample, as the engine's seam for it already exists. N1 becoming live-changeable on a session is an engine change in the packet.net repository.
7. **Socket encoding.** Options: JSON messages, easy to read in `journalctl` and by other hosts; or a binary frame like KISS. Recommendation: JSON, with a version in the first message. The datagram payload itself would be base64 or a binary part.
