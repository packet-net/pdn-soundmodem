# Adaptation over the air: the protocol

Status: agreed, 2026-11-24. A design record for #830 (part of #829): Tom read it, commented (a version number and feature flags, the FM capture effect, a check against the flows of AX.25 2.3) and agreed the nine choices listed at the end. Nothing here is built yet; the prototype comes first (#831 to #835), then the port.

This says how two stations with the adaptive OFDM-FM modem find and keep the best rate for the link between them, with nothing passing between them but the radio. It is meant to be read in a quarter of an hour and built from.

## What is already true

Measured on the bench with the prototype (two Tait TM8110s with internal sound card boards, through attenuators):

- **Every burst says what it is.** It opens with silence, a training symbol and a short header (42 bits in the prototype today): burst type (2 bits), sender (6), format (8), length (6), burst number (4) and a 16-bit check. A receiver that has been told nothing finds the burst, reads the header and decodes the payload.
- **There is a ladder of standard formats** from about 65 kbit/s at a strong signal to 0.25 kbit/s at about -125 dBm (#831 fixes the list). The header reads further down than any payload.
- **A receiver measures the channel from every burst it hears**, carrier by carrier, and from that can say which standard format the link would carry with a given margin.

Two things follow that shape the whole protocol.

1. **There is nothing to keep in step.** A sender may change format on any burst and the receiver decodes it, because the header names it. No handshake, no session, no agreed state that can go wrong.
2. **Every transmission sounds the channel for everyone who hears it.** A station learns the quality of the path from each neighbour by listening to whatever that neighbour sends, to anyone, whether or not the payload decoded.

## The idea in one paragraph

The receiver decides. Each station keeps, for every neighbour it hears, the fastest standard format it could currently receive from them. It tells the neighbour that format whenever it transmits to them, and in a very short burst of its own when it has nothing else to send and the neighbour needs to know. A sender uses the format it was last told, grows cautious as that goes stale, and falls back to an estimate from its own listening when it has been told nothing.

## Who measures, who decides

- **The receiver measures and recommends.** It has the picture carrier by carrier; sending that picture back would cost far more than sending its conclusion. The recommendation is one format number (8 bits).
- **The sender obeys, and may go slower.** It never goes faster than it was told. It may go slower for its own reasons: a small payload that fits a short robust burst, traffic for several stations at once, or a recommendation it no longer trusts.

## How the receiver arrives at a recommendation

For each neighbour, from the bursts heard from them:

1. Keep a running estimate of quality on each carrier, weighted towards recent bursts. A decoded payload gives an accurate one; a burst whose payload failed still gives a coarse one from its training symbol and header.
2. For each standard format, work out the margin that estimate leaves it.
3. Recommend the fastest format whose margin is at least M.
4. **M is steered by results**, per neighbour: a payload that fails at or below the recommended format raises M by half a decibel; each one that succeeds lowers it by a hundredth. That settles where about one burst in fifty fails. M starts at 3 dB.
5. Step down at once. Step up only when the faster format has had its margin for two bursts running.

## How the recommendation gets back: the report

Two ways, the same content: the recommended format (8 bits), and one bit saying "your last burst reached me and I could not decode it". Three more bits are spare.

- **Riding on traffic.** Every burst addressed to one station begins its payload with a two-byte prelude: a "report wanted" flag and the sender's report for the destination. So wherever traffic flows both ways, as it does under AX.25 with its acknowledgements, reports cost nothing extra.
- **By itself, as a header with no payload.** The burst type says "report", and the header's format and burst-number fields carry the recommended format and the failed bit. It is the shortest burst the modem can make (silence, training, header: under 0.2 s on the full band) and the most robust, since a header reads below any payload. It is sent in the header layout the reverse path supports, narrow if need be.

**When a station sends a report by itself:**

- at once, when a payload addressed to it failed to decode;
- at once, when the sender asked ("report wanted");
- at once, when its recommendation for a station that is sending to it has dropped;
- otherwise not: a recommendation that has only risen waits for traffic to ride on, or for the sender to ask.

A report sent in answer to a burst goes out in the first slot after that burst ends, the slot AX.25 already reserves for acknowledgements (its priority acknowledge, section 6.4.3). The two do not compete: when the burst decoded, the layer above's acknowledgement takes that slot and carries the report; when it did not, there is no acknowledgement and the slot is free for the report.

## How the sender chooses a format

For each neighbour the sender holds the last report received and when.

| What it has | What it sends at |
|---|---|
| A report less than 30 s old | the recommended format |
| An older report | the recommended format, one rung slower for each further 30 s, down to the estimate below |
| No report, but it has heard the neighbour | its own recommendation for the opposite direction, two rungs slower, with "report wanted" set |
| Nothing at all | the first-contact format (#835), with "report wanted" set |

The modem never sends anything again by its own decision. These rules choose the format for the next thing the host hands it, which after a loss is whatever the layer above sends to recover.

## Loss, collisions and the capture effect

An FM receiver takes the stronger of two signals and suppresses the weaker. So two stations transmitting at once do not look like a collision at the receiver:

- **The other station is stronger.** The receiver is captured by it. If that happens part-way through, the wanted burst's header was read and then its payload turns into something else. If it happens from the start, the wanted burst is never seen at all.
- **The other station is weaker.** The wanted burst is still the one received, with the weaker signal as added noise. A dense format needs 30 dB or more and can be spoiled by an interferer too weak to notice; a robust format rides over it.

What the receiver can tell, and what it does:

| What it sees | What it concludes | What it does |
|---|---|---|
| Quality low and even through the burst | the path cannot carry this format | reports "failed", with a slower recommendation, and raises M |
| Quality good at the start and falling suddenly part-way, or another burst's training appearing inside this one | something else came on the channel | reports "failed" with the recommendation unchanged; M does not move |
| A header and nothing decodable after it, with no measurement worth having | cannot say | reports "failed" with the recommendation unchanged |
| Nothing | nothing | nothing: it cannot report what it did not hear |

And the sender:

- **"Failed", with a slower format:** use it.
- **"Failed", format unchanged:** the same format again for whatever comes next. Going slower would make the burst longer and the next overlap more likely.
- **No report at all** when one was due: the burst may never have been heard (captured from the start, or the station gone), or the report was lost. The next burst goes at the same format once; if that too is met with silence, two rungs slower; after that, the first-contact format. Silence never moves M, which is the receiver's alone.

A report is a header, the most robust thing the modem sends, so it gets through over a weaker overlapping signal that would spoil data. How much weaker an interferer must be for each format to survive has not been measured on these radios; #835 needs a third station and should measure it.

## What each end remembers, and for how long

- **As a receiver, per neighbour:** the quality estimate, M, the current recommendation. Forgotten after ten minutes without hearing them.
- **As a sender, per neighbour:** the last report and its age. Useless after about two minutes (see the table).
- **Nothing is negotiated**, so nothing has to survive a restart. A station that restarts knows nothing, uses the cold rules, and is told what it needs within one exchange. The other end notices nothing.
- The two directions of a link are entirely separate. Each has its own estimate, margin and format.

## What a burst carries, in full

### Version, burst types and feature flags

Three separate ways for the protocol to grow, so that the expensive one is rarely needed.

- **Version: 2 bits at the front of every header, in the same place for good.** It changes only when something every station must parse changes: the header's layout, the training, the meaning of the base formats. This document is version 0. Value 3 is kept as an escape meaning "a longer version field follows", so the field cannot run out. A station that reads a version it does not know decodes nothing more of that burst and treats the channel as busy for as long as it hears signal. (802.11 has carried a 2-bit version since 1997 and has moved it once, because nearly everything since has been added the two ways below.)
- **Burst type: 3 bits, widened from 2.** New kinds of burst are the likeliest additions (parity for a repeat, a private table, a sounding burst). A station that meets a type it does not know can still read who sent it and whom it is for, and stays off the channel while it lasts. Types so far: data, report, identification.
- **Feature flags: a 16-bit capability word that each station advertises, outside the header.** Four bits give the revision of the standard format list the station knows; twelve are one flag per optional feature (none defined yet; tables private to a link and repeats inside the modem would be the first). It travels in the payload prelude of a data burst when a prelude bit says so (at first contact, with "report wanted", and after a neighbour has been forgotten) and in every identification burst. The rules:
  - an optional feature is used towards a neighbour only after that neighbour has shown the flag for it;
  - a flag a station does not know is ignored;
  - a neighbour whose capability word has not been heard is assumed to have none, which is always safe;
  - traffic for everyone uses nothing optional;
  - a sender told to use a format it does not know uses the fastest one it does know that is no faster. Format numbers are given out so that this is always well defined.

With the destination (below) the header is 51 bits, from 42 in the prototype before this. Measured in simulation, that costs half to three quarters of a decibel of header reach (on the full band, 172 headers read of 200 against 192 with each carrier 4 dB under the noise).

| Burst type | Header | Payload |
|---|---|---|
| Data to one station | version, type, sender, destination, format, length, burst number, check | prelude (report wanted, report for the destination, and the capability word when flagged), then one or more whole datagrams |
| Data to everyone | the same, with destination 0 | datagrams. No prelude, no report. |
| Report | version, type, sender, destination; the recommended format and the failed bit in place of format and burst number | none |
| Identification (optional) | sender, destination 0 | the station's callsign and capability word, in the first-contact format |

- **The destination is in the header** (6 bits, with 0 meaning everyone). The first draft kept it in the payload to save header bits. That does not survive more than two stations: when a payload fails, nobody could tell whom it was for, so either every station that heard it reports at once in the same slot, or nobody does. In the header, exactly one station knows the failure is its to report.
- A burst carries whole datagrams, several if they fit. The modem never splits a datagram across bursts. AX.25 has its own segmenter (section 2.4), and the host is told how much one burst can carry to each neighbour now (#822) so that it can size its frames to suit.

## Under a link layer such as AX.25: what the modem must not do

**The modem is not built around AX.25, and nothing in it depends on AX.25.** AX.25 should work well over it, as one client among others. So the rule is general: the modem moves datagrams and chooses the rate; acknowledging, repeating, ordering, pacing and splitting belong to whatever sits above, and a host with none of those of its own gets none from the modem either.

AX.25 2.3 was read against this design as the client that matters most today, so that the modem does not grow a second copy of what a link layer already does. AX.25 connected mode already has:

| AX.25 has | So the modem does not |
|---|---|
| Acknowledgement of every I frame, by N(R) on any frame coming back (6.4.6) | acknowledge bursts. A report is about the path, not about a frame. |
| Recovery: REJ and SREJ for gaps, and on a timeout a poll and a checkpoint (4.4.5, 6.4.11) | send anything again by its own decision, or keep sequence numbers of its own |
| The poll and final bits, for "where are you up to?" | ask for delivery status. "Report wanted" asks only about the path, and an AX.25 poll already brings a burst back that carries the report. |
| The idle poll, T3 | keep links alive |
| RNR and the window k, for flow | hold traffic back for the far end's sake. It tells the host what a link carries and leaves the pacing to it. |
| A segmenter (2.4) | split datagrams |
| A slot reserved for acknowledgements (6.4.3, T101) | invent a second priority scheme. Its lone reports use that slot. |

Three places where the first draft of this document had strayed, now corrected: a status field that named the burst being reported on (an acknowledgement by another name); an option to report after every burst so the host could be told "delivered"; and "send again two rungs slower", which read as the modem retransmitting.

**What the modem adds that AX.25 cannot do:**

- **The rate.** T1 "cannot be expressed in absolute time; the time required to send frames varies greatly with the signaling rate" (6.7.1.1). With a rate that varies several hundredfold the link layer needs to be told what the link carries now, and when a frame has actually gone out, to set T1, the frame size and the window sensibly (#822, #841).
- **One fast signal.** A frame that fails its check is discarded by AX.25 "with no further action" (4.4.6): the sender finds out only when T1 runs out, seconds later, and then does not know why. The receiving modem does know: it heard the burst and can say at once "that did not decode; use this format". That is the report on failure, and it is the only thing here that overlaps AX.25's recovery in time. It carries no sequence number and triggers no resend; AX.25 still recovers in its own way, just sooner and at a rate that works.

## A worked exchange under AX.25

A connects to B, having never heard it.

1. A's SABM goes in the first-contact format with "report wanted". B's UA comes back in the priority slot, and its prelude carries B's report: "to me, use format 40" (say 55 kbit/s). The connection's own handshake has trained one direction, and A's reading of the UA lets it tell B the other.
2. A's I frames go at format 40, several to a burst. B's RR, delayed a little by its T2 so as to acknowledge them together, rides in a short burst that repeats B's recommendation.
3. The path worsens by 10 dB. A's next burst reaches B and does not decode. AX.25 at B sees nothing. B's modem sends a lone report in the priority slot: "failed; use format 31". No acknowledgement arrives, so A's T1 runs out and AX.25 polls (RR, P set); the poll and everything after it go at format 31; B answers with its N(R) and A resends what was lost, as AX.25 always would.
4. A station C that B cannot hear transmits over one of A's bursts. B's modem sees good quality that collapses part-way and reports "failed", format unchanged. A's recovery goes at the same format.
5. Both fall silent for five minutes. A's knowledge of B has aged out. Its next frame, perhaps the T3 idle poll, goes at A's own estimate of the path, two rungs slow, and the answer puts it right.

## What is left to the layer above

- **Who stations are.** The modem deals in 6-bit numbers. Binding a number to a callsign after first contact, and identifying the station as the licence requires, belong above, with the optional identification burst for hosts that do not.
- **Getting a datagram there in the end.** Acknowledging, repeating and ordering belong to the layer above, as set out under AX.25 above. The modem tells the host when each frame has gone out, and what it knows about the path. #836 decides whether the modem should ever do more.
- **Routing, connections, flow.** Unchanged, but better informed: the host interface (#822) says what each neighbour can carry now, and #841 considers the AX.25 engine using it directly.

## Decided (Tom, 24 November 2026)

1. **The receiver recommends a format.** It does not send back raw quality. The recommendation is 8 bits and puts the decision where the knowledge is.
2. **A report is a header with no payload.** Shorter and more robust than a small ordinary burst, at the price of giving two header fields a second meaning and of resting on the header's check alone.
3. **The destination is in the header.** About a third of a decibel of header reach, and one station knows a failed burst was its to report.
4. **Reports go only on failure, on request and on a drop**, never after every burst. A host with no acknowledgements of its own sets "report wanted".
5. **Cold start from the sender's own listening, two rungs slow**, not always the first-contact format. The bench is to show whether two rungs is the right caution, unlike radios especially (#825).
6. **The modem never splits a datagram.**
7. **A target of about one burst in fifty lost** to start from. The bench tries 1%, 2%, 5% and 10% and keeps what gives the best real throughput. A fixed value, not a setting.
8. **Version of 2 bits with an escape, burst type of 3 bits, feature flags in a 16-bit capability word outside the header.**
9. **The numbers** (30 s for a report to stay fresh, ten minutes to forget a neighbour, two rungs on repeated silence, 3 dB starting margin) are first guesses for #833 and #834 to settle on the bench, and end up as fixed values, not settings.

## Not in this version

- Tables private to a link (#820): a refinement once the standard formats are proven.
- Repeats and combining inside the modem (#836): the burst number and a spare burst type are reserved for it.
- Anything that needs more than two stations to test, beyond what #835 covers.
