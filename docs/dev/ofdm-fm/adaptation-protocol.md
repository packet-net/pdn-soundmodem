# Adaptation over the air: the protocol

Status: a draft for Tom to read, 2026-11-24. A design record for #830 (part of #829). Nothing here is built. The choices still open are listed at the end and marked "to decide"; everything else is a proposal that stands unless he objects.

This says how two stations with the adaptive OFDM-FM modem find and keep the best rate for the link between them, with nothing passing between them but the radio. It is meant to be read in ten minutes and built from.

## What is already true

Measured on the bench with the prototype (two Tait TM8110s with internal sound card boards, through attenuators):

- **Every burst says what it is.** It opens with silence, a training symbol and a 42-bit header: burst type (2 bits), sender (6), format (8), length (6), burst number (4) and a 16-bit check. A receiver that has been told nothing finds the burst, reads the header and decodes the payload.
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

Two ways, the same content: who it is for (6 bits), the recommended format (8 bits), and a status of 4 bits (whether the last burst from that station decoded, and the low three bits of its burst number).

- **Riding on traffic.** Every burst addressed to one station begins its payload with a three-byte prelude: the destination's number, a "report wanted" flag, and the sender's report for the destination. So wherever traffic flows both ways, as it does under AX.25 with its acknowledgements, reports cost nothing extra.
- **By itself, as a header with no payload.** The burst type says "report", and the header's format, length and burst-number fields carry the recommended format, the destination and the status. It is the shortest burst the modem can make (silence, training, header: under 0.2 s on the full band) and the most robust, since a header reads below any payload. It is sent in the header layout the reverse path supports, narrow if need be.

**When a station sends a report by itself:**

- at once, when a payload addressed to it failed to decode;
- at once, when the sender asked ("report wanted");
- at once, when its recommendation for a station that is sending to it has dropped;
- otherwise not: a recommendation that has only risen waits for traffic to ride on, or for the sender to ask.

A report sent in answer to a burst goes out straight after that burst ends, ahead of anyone waiting for the channel, the way an acknowledgement does.

## How the sender chooses a format

For each neighbour the sender holds the last report received and when.

| What it has | What it sends at |
|---|---|
| A report less than 30 s old | the recommended format |
| An older report | the recommended format, one rung slower for each further 30 s, down to the estimate below |
| No report, but it has heard the neighbour | its own recommendation for the opposite direction, two rungs slower, with "report wanted" set |
| Nothing at all | the first-contact format (#835), with "report wanted" set |

**When no answer comes** to a burst that asked for one: send again two rungs slower; if that also goes unanswered, the first-contact format. A report, when it comes, puts things right in one step. Silence is weak evidence (the answer may have been lost, or the channel taken), which is why it only ever makes the sender more cautious and never feeds the margin.

**What does not count against the link:** a burst lost when the receiver heard another station at the same time, and anything the receiver could not attribute. Only the receiver's own measurements move M.

## What each end remembers, and for how long

- **As a receiver, per neighbour:** the quality estimate, M, the current recommendation. Forgotten after ten minutes without hearing them.
- **As a sender, per neighbour:** the last report and its age. Useless after about two minutes (see the table).
- **Nothing is negotiated**, so nothing has to survive a restart. A station that restarts knows nothing, uses the cold rules, and is told what it needs within one exchange. The other end notices nothing.
- The two directions of a link are entirely separate. Each has its own estimate, margin and format.

## What a burst carries, in full

| Burst type | Header | Payload |
|---|---|---|
| Data to one station | sender, format, length, burst number | prelude (destination, report wanted, report for the destination), then one or more whole datagrams |
| Data to everyone | the same | a prelude that says "everyone", then datagrams. No report. |
| Report | sender; destination, recommended format and status in place of length, format and burst number | none |
| Identification (optional) | sender | the station's callsign, in the first-contact format |

- The header stays at 42 bits. The destination is in the payload's prelude, not the header, because every bit added to the header costs reach, and a station that cannot decode a payload does not need to know whom it was for.
- A burst carries whole datagrams, several if they fit. No datagram is split across bursts in this version; the host is told how much one burst can carry to each neighbour now (#822) and sizes its frames to suit.

## A worked exchange

A has traffic for B and has never heard B.

1. A sends its first datagram in the first-contact format, "report wanted".
2. B decodes it, measures a strong signal, and answers at once with a report: "to me, use format 40" (say 55 kbit/s).
3. A sends the rest at format 40. B's acknowledgements, sent by the layer above, travel in bursts whose prelude repeats B's recommendation, and A's bursts carry A's recommendation for B's direction the same way.
4. The path worsens by 10 dB. B's next burst from A fails. B reports at once: "failed; use format 31". A's next burst is at 31.
5. Both fall silent for five minutes. A's knowledge of B has aged out; its next burst goes at its own estimate of the path, two rungs slow, "report wanted", and one exchange later it is back at the right rate.

## What is left to the layer above

- **Who stations are.** The modem deals in 6-bit numbers. Binding a number to a callsign after first contact, and identifying the station as the licence requires, belong above, with the optional identification burst for hosts that do not.
- **Getting a datagram there in the end.** The modem reports what became of each burst it can know about ("the far end says it decoded", "no answer"), which is more than a KISS TNC ever told its host. Repeating a lost datagram stays with the layer above in this version; #836 decides whether the modem should do more.
- **Routing, connections, flow.** Unchanged, but better informed: the host interface (#822) says what each neighbour can carry now, and #841 considers the AX.25 engine using it directly.

## To decide

1. **Receiver recommends a format** (proposed), or reports raw quality and lets the sender choose. The first is 8 bits and puts the decision where the knowledge is.
2. **A report is a header with no payload** (proposed), or a small ordinary burst. The first is shorter and more robust, at the price of giving three header fields a second meaning.
3. **Destination in the payload's prelude** (proposed), or in the header. The header option lets a station tell "for me" from "not for me" when the payload fails, and costs about a decibel of header reach.
4. **Reports only on failure, on request and on a drop** (proposed), or after every burst. Every burst gives the host a firm "delivered" for each one, and where traffic flows one way only it costs a fifth of a second a burst: 7% on a 2.3 s burst, a quarter on a 0.7 s one.
5. **Cold start from the sender's own listening, two rungs slow** (proposed), or always the first-contact format.
6. **No splitting of datagrams across bursts in this version** (proposed).
7. **A target of about one burst in fifty lost** for the margin (proposed). Lower is safer and slower.
8. **The numbers**: 30 s for a report to stay fresh, ten minutes to forget a neighbour, two rungs on silence, 3 dB starting margin. All are first guesses for #833 and #834 to settle on the bench.

## Not in this version

- Tables private to a link (#820): a refinement once the standard formats are proven.
- Repeats and combining inside the modem (#836): the burst number and a spare burst type are reserved for it.
- Anything that needs more than two stations to test, beyond what #835 covers.
