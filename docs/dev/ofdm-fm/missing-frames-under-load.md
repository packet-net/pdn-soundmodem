# Frames that the transmitter logs and the receiver never hears

**Status: measured 2026-09-18, and the first diagnosis was wrong.**

## The symptom

Queue a lot of frames and a large fraction never arrive. Three runs on the 6 kHz preset:

| run | transmitter logged | receiver decoded |
|---|---|---|
| 100 x 1900 B | 100 | 48 |
| 100 x 1900 B | 74 in 70 s | 15 |
| 60 x 1024 B | 75 in 47 s | 9 |

Short runs at the same setting, minutes apart, deliver 40 of 40 and 39 of 39. So it is load, not margin.

## The first diagnosis, and why it was wrong

The obvious reading is that the receiver gives up: it decodes the start of a busy period and stops. That is what this document said, and it named the streaming receive path as the suspect.

**It is not the receiver.** An offline replay runs the same decoder over the station's own raw capture, with no real-time constraint and as many passes as wanted. Over 820 seconds of radio2's capture spanning the whole test sequence:

| | frames |
|---|---|
| decoded live, by the station, in that period | 307 |
| decoded offline from the station's own recorded audio | 307 |

The same number. Given unlimited time and the identical audio, the decoder recovers exactly what it recovered on air and not one frame more. **The missing frames are not in the receiver's audio in decodable form**, so nothing the receiver does could have found them.

## Where they went: not established, and my first answer was wrong

I argued here that the frames were never keyed, on the strength of two fractions matching: 68 % of the implied air time present as carrier, and 68 % of logged frames decoded. **The carrier figure was measured wrongly and the match was an artefact.**

Carrier presence was taken as audio more than 6 dB below the receiver's idle noise. On FM that is a carrier, and it is also the digital silence recorded while this station is itself transmitting with its receive path gated. The bench was working in both directions, so some of what I counted as a far end holding the channel was our own keyups. Separating the three states over the same 820 seconds:

| state | time |
|---|---|
| far-end carrier | 153.9 s |
| our own transmission, receive gated | 27.8 s |
| idle noise | 638.3 s |

That makes the carrier fraction 57 %, not 68 %, against 68 % of frames decoded. The fractions do not match and the inference goes with them.

I also misread the code. `Program.cs` says at the `RecordTransmitted` call site that it is raised after the audio has gone to the device, "so a logged row is a frame that actually went on air". That should have been read before the conclusion, not after it.

**So the destination of the missing frames is open.** 453 logged transmitted, 307 received, and no candidate I would defend for the difference.

## Two controlled experiments, which are the cleanest evidence here

Everything above came from archaeology on runs that were doing something else. These two were set up for the question: restart both stations, mark both frame logs, queue exactly 20 frames of 1024 bytes, and compare the two stations' own counts.

| | transmitted | received |
|---|---|---|
| sent immediately after the restart | 20 | 14 |
| sent after letting the receiver settle 45 s | 20 | 17 |

And the losses are not spread through the run. Aligning the timestamps of the first one, the transmitter sent at two second intervals from 43:28 to 44:06 and the receiver missed 43:28, 43:30, 43:32, 43:36, 43:38 and 43:40, then decoded **every single frame** from 43:42 to the end.

**The losses are at the beginning, not the end**, which is the opposite of what a receiver failing under load would do, and it is why the framing at the top of this document, and the one before it, were both wrong.

Some of it is the receiver settling after a restart: 45 seconds of quiet took it from 14 of 20 to 17 of 20. That matters for every measurement taken tonight, because switching a profile restarts the daemon and the harness starts sending as soon as the API answers. It does not explain all of it.

For contrast, frames sent one per keyup with a gap between them, which is what the campaign's per-cell harness does, delivered 100 % all evening at every rate and every length.

## What does stand

The receiver is exonerated, and that part rests on a direct comparison rather than on arithmetic: 307 decoded live, 307 decoded offline from the same recorded audio. Nothing the receiving modem does could have found more, because there is no more in its audio.

The discrepancy itself is real and load-dependent: short runs at identical settings deliver 40 of 40 and 39 of 39 minutes apart.

## Why the replay tool earned its place

It was written to find a receiver bug and its first useful act was to prove there was not one. The whole question turned on a number that could not be obtained from the air at all, because on air the decoder only ever gets one attempt at each burst in real time. The replay harness itself is a campaign tool and is not part of this repository; what is reproducible here is the principle, which is that a raw capture plus an offline decoder answers a question the air cannot.

## What is safe to say about throughput

Every figure in [bandwidth-and-frame-length.md](bandwidth-and-frame-length.md) comes from runs short enough to sit inside the region where delivery is complete, and they are honest for that region. What none of them establish is that a long transfer sustains those rates, and this is why.

## The later answer

[preset-design.md](preset-design.md), section 4.5, found the mechanism a day later: the transmitting sound card was being drained and re-armed between frames, so every frame after the first in a keyup went out on a clock that had not settled, and the receiver's own retry budget was being reset by the code path that committed, so one undecodable burst could be retried over a hundred times while the frames behind it went by unheard. Both are fixed. This page stands as the record of the two wrong diagnoses that came first.
