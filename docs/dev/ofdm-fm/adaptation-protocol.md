# Adaptation over the air: the protocol

Status: a design record, agreed 2026-11-24 and brought into line with the prototype on 2026-11-24, later the same day. For #830 (part of #829): Tom read it, commented (a version number and feature flags, the FM capture effect, a check against the flows of AX.25 2.3) and agreed the nine choices listed under "Decided". A prototype was then built to it, run in simulation and run on two real radios (#833), and the rules changed in a number of places: "Changed since it was agreed" lists every one. Built and run on the bench at one signal level; not yet tried on a weak or a changing path (#834). None of it is in this repository's code yet: the prototype is kept outside this repository, and the port follows (#837 to #840).

This says how two stations with the adaptive OFDM-FM modem find and keep the best rate for the link between them, with nothing passing between them but the radio. It is meant to be read in a quarter of an hour and built from.

## What is already true

Measured on the bench with the prototype (two Tait TM8110s with internal sound card boards, through attenuators):

- **Every burst says what it is.** It opens with silence, a training symbol and a short header that names its sender, its format and its length, behind a 16-bit check (the header's fields are set out below). A receiver that has been told nothing finds the burst, reads the header and decodes the payload.
- **There is a ladder of standard formats** from about 65 kbit/s at a strong signal to 0.25 kbit/s at about -125 dBm (#831 fixes the list). The header reads further down than any payload.
- **A receiver measures the channel from every data burst it hears**, carrier by carrier, and from that can say which standard format the link would carry with a given margin.
- **Two stations have found their rate by these rules** with nothing between them but the radios, from cold, in three or four bursts ("What the bench has shown", near the end).

Two things follow that shape the whole protocol.

1. **There is nothing to keep in step.** A sender may change format on any burst and the receiver decodes it, because the header names it. No handshake, no session, no agreed state that can go wrong.
2. **Every transmission sounds the channel for everyone who hears it.** A station learns the quality of the path from each neighbour by listening to whatever that neighbour sends, to anyone, whether or not the payload decoded.

## The idea in one paragraph

The receiver decides. Each station keeps, for every neighbour it hears, the fastest standard format it could currently receive from them. It tells the neighbour that format whenever it transmits to them, and in a very short burst of its own when it has nothing else to send and the neighbour needs to know. A sender uses the format it was last told, grows cautious as that goes stale, and falls back to an estimate from its own listening when it has been told nothing.

## Who measures, who decides

- **The receiver measures and recommends.** It has the picture carrier by carrier; sending that picture back would cost far more than sending its conclusion. The recommendation is one format number (8 bits).
- **The sender obeys, and may go slower.** It never goes faster than it was told. It may go slower for its own reasons: a small payload that fits a short robust burst, traffic for several stations at once, or a recommendation it no longer trusts.

## How the receiver arrives at a recommendation

For each neighbour, from every data burst of theirs whose header it reads, whoever the burst was for. Three things are kept: an estimate of the path, a margin, and the recommendation. They are brought up to date in that order after each burst.

### The estimate of the path

One figure of quality for each carrier, in decibels, held as a full-band burst at full drive would show it.

1. **What is measured.** A burst whose payload decoded is measured on the carriers its payload used, with every symbol now known: an accurate figure. A burst whose payload failed is measured on the carriers of its opening, from the training and the header: a coarse one. A burst that something else came on top of (see Loss, below) is not measured at all. The prototype measures data bursts only; a lone report is read and not measured.
2. **Through the peak limit.** The robust formats are driven into the sender's peak limit: the drive is part of the format (see the format number, below). The limit's own distortion is no part of the path, and it is known exactly, because the limit did the same arithmetic to the same samples at the sender. So the receiver of a decoded burst rebuilds every symbol as it left the limit and measures the burst against that. Measured against the burst as built, the same bench path read 14 dB from a burst 6 dB over full drive and 31 dB from one at full drive, so a recommendation made from a robust burst reached only as far as the next drive class and a link took 8 to 14 bursts to climb from cold. Measured through the limit the first figure is 27 dB and the climb takes 3 to 4. This makes the limit part of the burst format: the whole burst after its opening silence is scaled so that its root mean square level is 12.8 dB under full scale at full drive, and higher by the format's drive, and is then clipped at 0.95 of full scale (#837 is where the exact arithmetic belongs). The prototype does this for decoded payloads only; the opening of a failed burst is still measured as built.
3. **A common footing.** A format on fewer carriers puts more power on each, and one driven harder has that much more. From every figure take off 10 log10(all carriers / carriers the measured part used) plus the format's drive, in decibels. The measured part is the payload for a decoded burst and the opening for a failed one.
4. **Folding it in.** Compare the new figures with the estimate, each averaged over the carriers that have both.
   - **More than 2 dB under the estimate:** the path has got worse, and that is believed at once. The new figures replace the estimate on the carriers measured, and the estimate on every other carrier comes down by the same average amount, since what was known of those is from before.
   - **Otherwise, from a decoded burst:** on each carrier measured, the estimate becomes half the old figure and half the new, in decibels. A neighbour's first three measurements count in full, since there is no history worth averaging with. A carrier with no figure yet takes the new one.
   - **Otherwise, from a failed burst:** nothing. A failed burst's own measurement is believed only where it says the path is worse; one that measures well is the margin's business. The one exception is a failed burst on carriers with no estimate at all, whose figures are taken as they are.
5. **Carriers never measured.** A narrow burst says nothing about the carriers above its band. For the recommendation they are filled in from the top of what was measured: the median of the highest seven carriers measured, falling 20 dB for each decade of frequency above the highest (as FM noise does), less 3 dB of caution.

### The margin

M, per neighbour, steered by results. It starts at 3 dB.

- A payload that decodes lowers M by 0.01 dB, to no less than 0.
- A payload that fails at a format no faster than the current recommendation (or before there is one) raises M by 0.5 dB, to no more than 12, unless the failure is put down to something else on the channel (see Loss).

That settles where about one burst in fifty fails.

### The recommendation

1. For each standard format, work out the margin the estimate leaves it: how far the estimate could fall, on every carrier alike, before the format's carriers between them no longer carry what the format asks of them. The format is credited with its own power on each carrier (the footing above, the other way round). How many bits a carrier carries at a given quality belongs with the format list (#831).
2. Take the fastest format whose margin is at least M, and which is no faster than the cap (below) if there is one. Formats are compared by their rate, across bands; of two with the same rate, the one with more margin. If no format has M, take the most robust.
3. **Step down at once.** If that format is no faster than the current recommendation, or there is none yet, it becomes the recommendation, and anything waiting (next) is dropped.
4. **Step up only on two bursts running.** A faster format waits. If the next burst supports one at least as fast as the one waiting, the one waiting becomes the recommendation: it is the one both bursts supported. (If the two have the same rate, the newer one is taken.) If the next burst supports something faster than the recommendation but slower than the one waiting, that waits in its turn and the count starts again.
5. **A new neighbour's first three measurements may raise the recommendation at once.** Without this, and with the estimate moving by halves, a link from cold climbs a staircase.

### After a failure: the cap

When a payload fails, and the failure is not put down to something else on the channel, the receiver recommends nothing faster than the format one rung below the one that failed, in whatever band and whatever it measures, until two bursts running from that neighbour have decoded. A further failure can lower the cap and never raises it. This is separate from the margin: the margin moves half a decibel at a time, and a measurement that still looks good after a failure would otherwise send the sender straight back to the format that had just failed.

## How the recommendation gets back: the report

Two ways, the same content: the recommended format (8 bits), and one bit saying "your last burst reached me and I could not decode it".

- **Riding on traffic.** Every burst addressed to one station begins its payload with a two-byte prelude: flags ("report wanted", "a capability word follows", "a recommendation is given", "failed") and the sender's recommendation for the destination. A station with nothing to recommend yet, as at first contact, leaves "a recommendation is given" clear. So wherever traffic flows both ways, as it does under AX.25 with its acknowledgements, reports cost nothing extra. The prototype never sets "failed" in a prelude, because a failure is always reported alone and at once.
- **By itself, as a header with no payload.** The burst type says "report", and the header's format and burst-number fields carry the recommended format and the failed bit. It is the shortest burst the modem can make (silence, training, header: under 0.2 s on the full band) and the most robust, since a header reads below any payload. A burst opens in one of three ways: on the full band, or, for the formats that keep to the low carriers, on the carriers up to 3 kHz or up to 1.5 kHz only, with a longer opening. A receiver looks for all three. A lone report opens the way the reporting station's own data to that neighbour would, and at the same drive (by the sender's rules below), so it reaches as far as that data does: about 0.3 s on the carriers up to 3 kHz and half a second on those up to 1.5 kHz.

**When a station sends a report by itself:**

- at once, when a payload addressed to it failed to decode, whatever the cause;
- when the sender asked ("report wanted") and the burst decoded: the report rides on whatever the host sends back in the first slot, and if the host sends nothing in that slot the modem sends it alone;
- at once, when a burst addressed to it decodes and its recommendation for the sender drops, on that burst, to below what the sender was last told (in a prelude or a lone report);
- otherwise not: a recommendation that has only risen waits for traffic to ride on, or for the sender to ask;
- never for a burst addressed to everyone, or to another station. Those are measured all the same.

A lone report on a drop is redundant when the station has data of its own about to go back, because that burst's prelude carries the same recommendation. The prototype sends it anyway; this was seen only in simulation, on a poor made-up path, and the rule has not been changed.

"The first slot" is the one AX.25 already reserves for acknowledgements after a frame (its priority acknowledge, section 6.4.3). The modem's lone reports use it and nothing else of the modem's does. When a burst fails there is no acknowledgement to come, so the slot is free for the report. When it decodes, the slot belongs to the layer above, and whatever that sends, then or later, carries the report in its prelude.

**After a lone report**, the station that sent it goes straight on with its own data, if it has any and is not still waiting for an answer to a burst of its own (it heard the other station last, so the turn is its own), and the station that heard it leaves the channel clear for about a second first. In the prototype the second half is a general rule: no data burst goes out until the channel has been clear for between 0.8 and 1.2 s, a figure drawn afresh each time the channel has been in use, so that the two stations do not wait the same time. Without it both stations transmitted together after every lone report. This is a rule for two stations; what a shared channel needs after the first slot is not designed yet.

## How the sender chooses a format

For each neighbour the sender holds the last report received and when, how many times a report it asked for has not come (the silences), and how many data bursts it has sent them since it last heard anything at all from them.

| What it has | What it sends at | "Report wanted" |
|---|---|---|
| A report less than 30 s old | the recommended format | clear |
| An older report | the recommended format, one rung slower for each whole 30 s of the report's age (one at 30 s, two at 60 s), but no slower than the estimate in the next row, or than the first-contact format if it has none, and never faster than the report said | set |
| No report, but it has heard the neighbour | its own recommendation for the opposite direction, two rungs slower | set |
| Nothing at all | the first-contact format (#835) | set |

If the sender's own estimate, two rungs slow, is faster than what the report said, the report does not age at all: the sender stays at what it was told.

**"Report wanted" is also set:**

- after any silence, until a report arrives (see Loss, below);
- when two data bursts have gone to the neighbour since anything at all was heard from them: a data burst of theirs to anyone, decoded or not, or a report to this station. A path that dies outright is then noticed in two bursts, without waiting 30 s for the report to go stale. And in traffic that goes one way it makes the sender ask on every third burst, which is the only way the rate rises there, since a recommendation that has only risen waits to be asked for.

Asking costs the far end a burst of a fifth of a second on the full band.

The modem never sends anything again by its own decision. These rules choose the format for the next thing the host hands it, which after a loss is whatever the layer above sends to recover.

## Loss, collisions and the capture effect

An FM receiver takes the stronger of two signals and suppresses the weaker. So two stations transmitting at once do not look like a collision at the receiver:

- **The other station is stronger.** The receiver is captured by it. If that happens part-way through, the wanted burst's header was read and then its payload turns into something else. If it happens from the start, the wanted burst is never seen at all.
- **The other station is weaker.** The wanted burst is still the one received, with the weaker signal as added noise. A dense format needs 30 dB or more and can be spoiled by an interferer too weak to notice; a robust format rides over it.

What the receiver can tell, and what it does:

| What it sees | What it concludes | What it does |
|---|---|---|
| A header, a payload that fails, and no sudden change part-way | the path cannot carry this format | reports "failed", with its new recommendation; takes the burst's measurement if it is more than 2 dB under the estimate; raises M if the format was no faster than its recommendation; sets the cap |
| A header, a payload that fails, and quality good at the start and falling suddenly part-way | something else came on the channel | reports "failed" with the recommendation unchanged; takes no measurement; M, the cap and the count of bursts decoded running do not move |
| Nothing | nothing | nothing: it cannot report what it did not hear |

"Suddenly" has a test. Each symbol has a mean squared miss from its constellation points, in units of the noise expected. The failure is sudden when some point splits the payload's symbols into an earlier part whose median miss is under 1.5 times the opening's plus 0.5, and a later part whose median is over three times the earlier part's (or over 1.5, if that is more). A payload of fewer than six symbols is never judged sudden, and neither end part may be shorter than two symbols.

The agreed version had a third kind of failure, "a header and nothing decodable after it, with no measurement worth having", where the receiver could not say and M did not move. The prototype has no such case: the opening of a burst whose header read always gives a measurement, so a failure is either sudden or it is not. It also does not look for another burst's training inside the one it is receiving; the sudden fall is the only sign it uses.

And the sender:

- **"Failed", with a slower format:** use it.
- **"Failed", format unchanged:** the same format again for whatever comes next. Going slower would make the burst longer and the next overlap more likely.
- Both come to one rule, which is all the prototype's sender has: use the format the report gives. It does not look at the failed bit.
- **No report at all** when one was due, which is when the burst had "report wanted" set: the burst may never have been heard (captured from the start, or the station gone), or the report was lost. Each such burst counts one silence, whether nothing came back or a burst whose prelude gave no recommendation. After one silence the next burst goes at the format the table gives, unchanged. After two, two rungs slower than the table gives. After three or more, the first-contact format. Silence never moves M, which is the receiver's alone.
- **Only a report that gives a recommendation clears the silences**, alone or in a prelude. A prelude that gives none is word from the neighbour but not a report, and the count stands. Otherwise a station that hears a neighbour it cannot reach has its silences cleared by every burst it decodes from them, and goes on for ever at a rate the neighbour never decodes; with the rule, it is back at the first-contact format on the third silence.

A report is a header, the most robust thing the modem sends, so it gets through over a weaker overlapping signal that would spoil data. How much weaker an interferer must be for each format to survive has not been measured on these radios; #835 needs a third station and should measure it.

## What each end remembers, and for how long

- **As a receiver, per neighbour:** the estimate and how many measurements have gone into it, M, the current recommendation and any faster one waiting, the cap and the count of bursts decoded running, and what the neighbour was last told.
- **As a sender, per neighbour:** the last report and its age, which counts for less every 30 s until the sender is back on its own estimate (see the table); the count of silences; the count of bursts sent since anything was heard; the neighbour's capability word.
- **All of it is dropped, both roles together, ten minutes after the neighbour was last heard**: a data burst of theirs to anyone, or a report to this station.
- **Nothing is negotiated**, so nothing has to survive a restart. A station that restarts knows nothing, uses the cold rules, and is told what it needs within one exchange. The other end notices nothing.
- The two directions of a link are entirely separate. Each has its own estimate, margin and format.

## What a burst carries, in full

### Version, burst types and feature flags

Three separate ways for the protocol to grow, so that the expensive one is rarely needed.

- **Version: 2 bits at the front of every header, in the same place for good.** It changes only when something every station must parse changes: the header's layout, the training, the meaning of the base formats. This document is version 0. Value 3 is kept as an escape meaning "a longer version field follows", so the field cannot run out. A station that reads a version it does not know decodes nothing more of that burst and treats the channel as busy for as long as it hears signal. (802.11 has carried a 2-bit version since 1997 and has moved it once, because nearly everything since has been added the two ways below.)
- **Burst type: 3 bits, widened from 2.** New kinds of burst are the likeliest additions (parity for a repeat, a private table, a sounding burst). A station that meets a type it does not know can still read who sent it and whom it is for, and stays off the channel while it lasts. Types so far: data, report, identification.
- **Feature flags: a 16-bit capability word that each station advertises, outside the header.** Four bits give the revision of the standard format list the station knows; twelve are one flag per optional feature (none defined yet; tables private to a link and repeats inside the modem would be the first). It travels in the payload prelude of a data burst when a prelude flag says so, which is whenever "report wanted" is set (so at first contact, on the first burst to a neighbour that had been forgotten, and wherever else the sender's rules above set it), and in every identification burst. The rules:
  - an optional feature is used towards a neighbour only after that neighbour has shown the flag for it;
  - a flag a station does not know is ignored;
  - a neighbour whose capability word has not been heard is assumed to have none, which is always safe;
  - traffic for everyone uses nothing optional;
  - a receiver recommends only formats from a list revision the sender has shown it knows, and from revision 0 until it has shown anything. Revision 0 gives all 256 numbers a meaning, so in this version there is no such thing as a number a station does not know.

### The header, field by field

In the order sent:

| Field | Bits | What it holds |
|---|---|---|
| Version | 2 | 0. Value 3 is the escape. |
| Burst type | 3 | 0 data, 1 report, 2 identification. The rest are spare. |
| Sender | 6 | the sending station's number, 1 to 63 |
| Destination | 6 | the station it is for, or 0 for everyone |
| Format | 8 | the standard format of the payload (#831). In a report: the format recommended to the destination. |
| Length | 6 | payload symbols, in fours (so 0 to 252). In a report: 0. |
| Burst number | 4 | on data, a count that goes up by one with each data burst a station sends, round and round. The receiver needs it: it is part of the seed of the payload's scrambler (below). In a report: the top bit is "failed" and the other three are 0. |
| Check | 16 | over the 35 bits before it: a CRC with polynomial 0x1021, started at all ones, most significant bit first, nothing reflected and nothing inverted at the end |

That is 51 bits, from 42 in the prototype before this. Measured in simulation, the nine added bits (version, a wider burst type, destination) cost half to three quarters of a decibel of header reach (on the full band, 172 headers read of 200 against 192 with each carrier 4 dB under the noise).

Station numbers are set at both ends for the bench. How a station comes by its number on a shared channel is #835.

| Burst type | Header | Payload |
|---|---|---|
| Data to one station | every field | the prelude, then one or more whole datagrams |
| Data to everyone | the same, with destination 0 | datagrams. No prelude, no report. |
| Report | every field, with the recommended format and the failed bit in the format and burst-number fields, and length 0 | none |
| Identification (optional) | as data, with destination 0 | the station's callsign and capability word, in the first-contact format |

- **The destination is in the header** (6 bits, with 0 meaning everyone). The first draft kept it in the payload to save header bits. That does not survive more than two stations: when a payload fails, nobody could tell whom it was for, so either every station that heard it reports at once in the same slot, or nobody does. In the header, exactly one station knows the failure is its to report.
- A burst carries whole datagrams, several if they fit. The modem never splits a datagram across bursts. AX.25 sizes its own frames (N1) and has its own segmenter (section 2.4), and the host is told how much one burst can carry to each neighbour now (#822) so that it can size its frames to suit.

### The format number

The list of formats is #831's and is provisional. The protocol needs only this much of it.

- **A format number is a band (the top 3 bits) and a rung (the low 5 bits):** number = 32 x band + rung. The band says how much of the audio the payload uses, band 0 the most; in the first list the eight bands reach up to 8, 6.5, 5, 4, 3, 2, 1.5 and 1 kHz. The rung says how many information bits each carrier carries in each symbol, 32 steps of about 13% each, rung 0 the fewest.
- **Everything else follows from those two:** the constellation, the code rate, which of the three openings the burst has, and its drive. So every one of the 256 numbers means something and any station can decode any of them cold.
- **The drive is part of the format.** A format's bursts are driven a fixed number of decibels over full drive, into the peak limit: more for the low rungs, where noise and not distortion is what limits them (6 dB over at most in the first list), none for the high ones. A sender does not choose it. The receiver relies on it twice: to put a measurement on the common footing, and to rebuild a burst as it left the limit.
- **"One rung slower"** is the same band, one rung down. From the bottom rung of any band it is the most robust format.
- **"Faster" and "slower" between formats in different bands** go by rate.
- **The most robust format** is the bottom rung of the 1.5 kHz band (number 192 in the first list, 0.25 kbit/s): the bottom of the ladder that has been measured. The first-contact format is the same one until #835 says otherwise. The 1 kHz band has slower formats still; they are below anything measured so far.

### The payload, the scrambler and the check

A format and a length give a payload of a fixed number of bits. From the front:

1. **The prelude**, in a burst to one station: two bytes. First byte, from the top bit: "report wanted", "a capability word follows", "a recommendation is given", "failed", and four spare bits sent as 0. Second byte: the recommended format (0 when none is given).
2. **The capability word**, when flagged: two bytes, the more significant first.
3. **Datagrams**, each behind its length in two bytes, the more significant first. A length of 0, or too little room left for another length, ends them.
4. **Padding** of zero bits, up to the check.
5. **The check**: the payload's last 32 bits.

**Everything before the check is scrambled.** Without that, padding, or data with long runs in it, puts the same point on every carrier, and that makes a symbol that is nearly all peak. The scrambler is additive: each bit is added (exclusive or) to the next bit of a sequence that both ends make the same way.

- **The seed** is 31 bits, taken from the burst's own header. Most significant bit first: a 1, the sender (6 bits), the destination (6 bits), the burst number (4 bits), then the fourteen bits 10 1010 0101 1011. As a number: 2^30 + 2^24 x sender + 2^18 x destination + 2^14 x burst number + 0x2A5B. It is never zero.
- **The generator** is a 31-stage shift register, x^31 + x^28 + 1. Call the seed's bits `b[0]` to `b[30]`, most significant first. Every later bit is `b[i] = b[i-28] xor b[i-31]`. The sequence repeats only after 2^31 - 1 bits, far longer than any burst.
- **The seed's own bits are not used.** The payload's first bit is added to `b[31]`, its second to `b[32]`, and so on up to the check.
- **The check is over the scrambled bits** and is sent as it is: a 32-bit CRC with polynomial 0x04C11DB7, started at all ones, most significant bit first, nothing reflected and nothing inverted at the end. A receiver checks first and unscrambles after.
- **To test an implementation:** sender 1, destination 2 and burst number 1 give the seed 0x41086A5B, and the sequence then begins, in hexadecimal with the first bit at the top of the first byte, 92 96 71 0E 0C 4B F2 FC.

Why it is this scrambler:

- **Additive, not self-synchronising.** The self-synchronising kind exists for links with no frame timing, and it multiplies errors. A burst has frame timing.
- **Seeded afresh for every burst.** A payload that happens to render badly, with high peaks into the limit, would with a fixed sequence render badly every time it was sent: before this, one fixed waveform lost 39 of 240 on the bench where its neighbours lost 1%. With the seed taken from the header, a repeat of the same data in a later burst is a different waveform. The fields used are ones the receiver has read before it needs the sequence, so the seed costs no bits. This is why the burst number now matters to the receiver.
- **One sequence for each burst, not the best of several.** Sending each burst with whichever of several sequences gives the lowest peaks was measured (40 bursts a format, the best of 16 sequences against one) and not adopted. It takes 0.4 to 0.7 dB off what the peak limit removes at 6 dB over full drive, where that is about 20 dB under the signal; 2.4 dB at 3 dB over, where it is already 33 dB under; and 0.4 dB at full drive, where it is 47 dB under. That is not worth the header bits it would take to say which sequence was used. The worst of 16 is 0.5 to 3 dB worse than the typical one, and that is the case a changing seed protects a repeat from.

## Under a link layer such as AX.25: what the modem must not do

**The modem is not built around AX.25, and nothing in it depends on AX.25.** AX.25 should work well over it, as one client among others. So the rule is general: the modem moves datagrams and chooses the rate; acknowledging, repeating, ordering, pacing and splitting belong to whatever sits above, and a host with none of those of its own gets none from the modem either.

AX.25 2.3 was read against this design as the client that matters most today, so that the modem does not grow a second copy of what a link layer already does. AX.25 connected mode already has:

| AX.25 has | So the modem does not |
|---|---|
| Acknowledgement of every I frame, by N(R) on any I or S frame coming back (6.4.6) | acknowledge bursts. A report is about the path, not about a frame. |
| Recovery: REJ and SREJ for gaps (4.4.3, 4.4.4, 6.4.4), and on a timeout a poll and a checkpoint (4.4.5, 6.4.11) | send anything again by its own decision, or keep sequence numbers of its own |
| The poll and final bits, for "where are you up to?" | ask for delivery status. "Report wanted" asks only about the path, and an AX.25 poll already brings a burst back that carries the report. |
| The idle poll, T3 | keep links alive |
| RNR and the window k, for flow | hold traffic back for the far end's sake. It tells the host what a link carries and leaves the pacing to it. |
| Its own frame size (N1) and a segmenter (2.4) | split datagrams |
| A slot reserved for acknowledgements (6.4.3, T101) | invent a second priority scheme. Its lone reports use that slot. |

Three places where the first draft of this document had strayed, now corrected: a status field that named the burst being reported on (an acknowledgement by another name); an option to report after every burst so the host could be told "delivered"; and "send again two rungs slower", which read as the modem retransmitting.

**What the modem adds that AX.25 cannot do:**

- **The rate.** T1 "cannot be expressed in absolute time; the time required to send frames varies greatly with the signaling rate" (6.7.1.1). With a rate that varies several hundredfold the link layer needs to be told what the link carries now, and when a frame has actually gone out, to set T1, the frame size and the window sensibly (#822, #841).
- **One fast signal.** A frame that fails its check is discarded by AX.25 "with no further action taken" (4.4.6). If it was the last frame outstanding, which a lost burst carrying several frames often makes it, the sender finds out only when T1 runs out (4.4.5.1), seconds later, and then does not know why. The receiving modem does know: it heard the burst and can say at once "that did not decode; use this format". That is the report on failure, and it is the only thing here that overlaps AX.25's recovery in time. It carries no sequence number and triggers no resend; AX.25 still recovers in its own way, just sooner and at a rate that works.

## A worked exchange under AX.25

A connects to B, having never heard it. The format numbers and rates are the ones the bench path gave from cold (about -78 dBm).

1. A's SABM goes in the first-contact format (192) with "report wanted" and A's capability word. B decodes it and recommends format 24 (30 kbit/s), which is as much as one narrow burst lets it say about the whole band. B's UA comes back in the priority slot. B has no report from A yet but has now heard it, so the UA goes at B's own recommendation two rungs slower (format 22, 23 kbit/s) with "report wanted" set, and its prelude carries B's report: "to me, use format 24". The connection's own handshake has trained one direction, and A's reading of the UA lets it tell B the other.
2. A's I frames go at format 24, several to a burst, with A's recommendation for B in the prelude. This is the first full-band burst B has heard from A. It is B's second measurement of A, so it counts in full and may raise the recommendation at once: B's RR, delayed a little by its T2 so as to acknowledge the frames together, rides in a short burst whose prelude says "use format 29" (55 kbit/s). A burst or two more and both directions are at format 30 (62 kbit/s).
3. The path worsens by 10 dB. A's next burst reaches B and does not decode. AX.25 at B sees nothing. B's modem measures the burst's opening, finds it more than 2 dB under its estimate and takes the new figures in place of the old; raises M by half a decibel; and will recommend nothing faster than format 29 until two bursts running decode. It sends a lone report in the priority slot: "failed; use format 26" (say; 38 kbit/s). No acknowledgement arrives, so A's T1 runs out and AX.25 polls (RR, P set); the poll and everything after it go at format 26; B answers with its N(R) and A resends what was lost, as AX.25 always would.
4. A station C that B cannot hear transmits over one of A's bursts. B's modem sees good quality that collapses part-way and reports "failed", format unchanged. Its estimate, M and cap are as they were. A's recovery goes at the same format.
5. Both fall silent for five minutes. A's report from B is ten rungs stale, which takes it down to A's own estimate of the path, two rungs slow, and no further. Its next frame, perhaps the T3 idle poll, goes at that with "report wanted" set, and the answer puts it right. After ten minutes of silence A would have forgotten B altogether and started from the first-contact format.

## What is left to the layer above

- **Who stations are.** The modem deals in 6-bit numbers. Binding a number to a callsign after first contact, and identifying the station as the licence requires, belong above, with the optional identification burst for hosts that do not.
- **Getting a datagram there in the end.** Acknowledging, repeating and ordering belong to the layer above, as set out under AX.25 above. The modem tells the host when each frame has gone out, and what it knows about the path. #836 decides whether the modem should ever do more.
- **Routing, connections, flow.** Unchanged, but better informed: the host interface (#822) says what each neighbour can carry now, and #841 considers the AX.25 engine using it directly.

## Decided (Tom, 24 November 2026)

1. **The receiver recommends a format.** It does not send back raw quality. The recommendation is 8 bits and puts the decision where the knowledge is.
2. **A report is a header with no payload.** Shorter and more robust than a small ordinary burst, at the price of giving two header fields a second meaning and of resting on the header's check alone.
3. **The destination is in the header.** It is most of the half to three quarters of a decibel the longer header costs, and it means one station knows a failed burst was its to report.
4. **Reports go only on failure, on request and on a drop**, never after every burst. The modem sets "report wanted" by the sender's rules above; a host with no acknowledgements of its own can also ask for it on a datagram, through the host interface (#822).
5. **Cold start from the sender's own listening, two rungs slow**, not always the first-contact format. The bench is to show whether two rungs is the right caution, especially between unlike radios (#825).
6. **The modem never splits a datagram.**
7. **A target of about one burst in fifty lost** to start from. The bench tries 1%, 2%, 5% and 10% and keeps what gives the best real throughput. A fixed value, not a setting.
8. **Version of 2 bits with an escape, burst type of 3 bits, feature flags in a 16-bit capability word outside the header.**
9. **The numbers** (30 s for a report to stay fresh, ten minutes to forget a neighbour, two rungs on repeated silence, 3 dB starting margin) are first guesses for #833 and #834 to settle on the bench, and end up as fixed values, not settings.

## Changed since it was agreed

Nothing here reverses any of the nine choices above. Each line is a rule that the agreed version did not have, or had differently, with why and where it came from: simulation (the real modem in a loop with a made-up channel), the bench (#833), or simply building it.

| What changed | Why | From |
|---|---|---|
| The payload is scrambled, with a sequence seeded from the burst's own header. The burst number now matters to the receiver; the agreed version said nothing depended on it. | Padding made symbols that were nearly all peak; a fixed sequence makes a bad waveform bad every time it is repeated. | Simulation; the per-burst seed from a bench soak |
| After a failure the receiver recommends nothing faster than one rung below what failed, until two bursts running decode (the cap). | A measurement that still looked good sent the sender straight back to the format that had just failed. | Simulation |
| A failed burst's own measurement is believed only where it says the path is worse. | The opening of a failed burst is a coarse measurement; when it reads well, the failure is the margin's business. | Simulation |
| A measurement more than 2 dB under the estimate replaces it outright, and the carriers it did not measure come down by as much. | A path that has got worse must be believed at once, not by halves, and what was known of the other carriers is from before. | Simulation |
| Every measurement is put on a common footing (a full-band burst at full drive), and the carriers above a narrow burst's band are filled in at 20 dB a decade down, less 3 dB. | Formats differ in carriers and in drive; a narrow first-contact burst has to be enough to recommend a full-band format. | Simulation |
| A new neighbour's first three measurements count in full and may raise the recommendation at once. | Averaging by halves and "two bursts running" at every step made the climb from cold a staircase. | Bench |
| Quality is measured through the peak limit. | The limit's own distortion was being counted as the path's: 8 to 14 bursts to climb from cold, 3 to 4 with this. | Bench |
| "Report wanted" is also set when two bursts have gone to a neighbour with nothing at all heard back. | A path that dies outright is noticed without waiting 30 s; and it is the only way the rate rises in one-way traffic. | Simulation; the one-way effect seen on the bench |
| "Report wanted" is set whenever the report in hand is 30 s old or more, and after any silence until a report arrives. | The agreed version set it only where the sender had no report; a sender going slower on a stale report should ask for a fresh one. | Building it |
| A prelude that gives no recommendation does not clear the count of silences, and a burst that asked and got only such a prelude back counts as a silence. | A station that heard a neighbour it could not reach stayed at a rate the neighbour never decoded. | Simulation |
| The steps after silence (same format, two rungs slower, first contact) apply to what the table gives; they do not replace it. | So a report that is also going stale keeps ageing. | Building it |
| A stale report ages no further than the sender's own estimate two rungs slow, and not at all if that is faster than the report. | "Never faster than it was told" has to hold while a report ages. | Building it |
| A lone report on a drop goes only when the recommendation falls below what the sender was last told, on a burst addressed to this station. | A drop the sender already knows of needs no report. | Building it |
| After a lone report the station that sent it goes on with its own data, and the other leaves the channel clear for about a second. | Both stations transmitted together after every lone report. | Simulation; used on the bench |
| Noted, not changed: a lone report on a drop is redundant when that station has data about to go back. | The data burst's prelude carries the same recommendation. | Simulation |
| Two kinds of failure, not three: "cannot say" is gone, and another burst's training inside a burst is not looked for. | The opening of any burst whose header read gives a measurement. | Building it |
| The sender does not look at the failed bit, and a prelude never carries it set. | Obeying the recommendation covers both cases; a failure is always reported alone and at once. | Building it |
| M is held between 0 and 12 dB. | Limits it did not have. | Building it |
| Stepping up: a slower candidate on the second burst starts the count again. | The agreed version did not say. | Building it |
| A neighbour's state in both roles is dropped together after ten minutes. | The agreed version forgot only the receiver's half. | Building it |
| A format number is a band and a rung, and the drive is part of the format. | The first format list (#831). | Building it |
| Three openings, not two. | The formats on the lowest carriers need a longer opening than those up to 3 kHz. | Building it |

## What the bench has shown

Two stations of the prototype, one process each, with nothing between them but the two Tait radios through attenuators (about -78 dBm), each started cold (#833).

| Run | Direction | Bursts sent | Delivered | Settled at | From cold to within one rung of that |
|---|---|---|---|---|---|
| 4 min, traffic both ways | 1 to 2 | 21 | 21 | 62 kbit/s | burst 8 |
| | 2 to 1 | 21 | 21 | 62 kbit/s | burst 12 |
| 25 min, traffic both ways | 1 to 2 | 130 | 130 | 62 kbit/s | burst 12 |
| | 2 to 1 | 130 | 129 | 62 kbit/s | burst 10 |
| 20 min, traffic one way | 1 to 2 | 96 | 96 | 62 kbit/s | burst 14 |
| 6 min, traffic both ways, measuring through the peak limit | 1 to 2 | 31 | 31 | 62 kbit/s | burst 4 |
| | 2 to 1 | 31 | 31 | 62 kbit/s | burst 3 |

- **459 of 460 bursts delivered.** The stations never keyed at once, and nothing was decoded that was not sent.
- **The one failure** (at format 30, the top rung but one) was reported at once and the report was read. The next four bursts went at formats 28, 28, 29 and 29, and the fifth was back at 30.
- **The slow climb was the peak limit**, as set out under the estimate above. The last run is the same path with the receiver measuring through the limit.
- **In traffic that goes one way the rate rose only when the sender asked**, on every third burst: 32 requests in 96 bursts, 32 lone reports, all read.
- **The margin** came down from 3 dB to 1.7 and 2.2 dB over the 130 bursts of the long run. The top rung was never recommended.
- **One failure in 460 says nothing yet about the target of one burst in fifty**, and the trial of 1%, 2%, 5% and 10% has not been run. The numbers (30 s, ten minutes, two rungs, 3 dB) are still the first guesses.

What in those runs was the prototype and not the protocol:

- Its receiver takes two to three seconds to decode a burst, and eight when one fails. So after a data burst a station waited 2 s plus 0.16 s for each payload symbol for the far end, unless it heard from them sooner: the first slot, stretched. That is also how long it waited for a report it had asked for before counting a silence. Throughput by the clock was 10 to 12 kbit/s for the same reason, against 43 to 56 kbit/s over the time the bursts were on the air.
- There was no AX.25. A made-up host always had datagrams to send, in bursts of up to 2.5 s. A report that had been asked for waited up to 1.5 s for a data burst to ride on, and went alone at once from a station with no traffic.
- After a wait that ended in silence, a station held off up to 4 s more, at random, so that the two would not move together.
- Carrier sense came from the open receiver's noise above the modem's band, which a carrier quiets. That is simple and has 28 to 45 dB of contrast at this signal strength. It will not work at weak signal, where a carrier does not quiet the receiver.

**Not yet tried on air:**

- a weak path, where the narrow formats, the long openings and the bottom of the ladder come into play;
- a path that changes, or breaks (#834, which needs a hand on the attenuator). Drops and dead paths have only been run in simulation: there a 14 dB drop cost three bursts, and a path too poor for a full-band header to be read was found again at the first-contact format on the sixth burst;
- more than two stations, broadcasts, and stations listening in (#835);
- unlike radios (#825);
- a real link layer above it.

## Not in this version

- Tables private to a link (#820): a refinement once the standard formats are proven.
- Repeats and combining inside the modem (#836): the burst number and a spare burst type are reserved for it.
- Anything that needs more than two stations to test, beyond what #835 covers.
