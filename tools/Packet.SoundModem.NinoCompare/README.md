# nino-compare

A harness to benchmark our BPSK decode against a **NinoTNC** listening to the *same* received
audio, and to characterise how far off-frequency the stations on a channel actually sit — so the
frequency-diversity bank (`bpsk300-multi` / `bpsk1200-multi`) can be tuned to match or beat the
NinoTNC. Dev tooling; not shipped in the `pdn-soundmodem` package.

Uses packet.net's `Packet.Ax25` codec to parse frames and `MQTTnet` to consume the NinoTNC feed
(both MIT-licensed).

## The workflow

1. **Capture the NinoTNC's decodes** (the reference truth) from its MQTT topic of binary AX.25
   frames, while it hears the channel:

   ```sh
   nino-compare mqtt-capture --broker mqtt.example:1883 --topic ninotnc/rx --out nino.jsonl
   # --kiss if the payload is KISS-framed rather than a bare AX.25 frame
   # --user / --pass for an authenticated broker
   ```

2. **Decode the same audio** with our bank and log what we get, plus per-frame carrier offset:

   ```sh
   nino-compare decode --wav session.wav --detector coherent --pairs 4 --step 7.5 --out ours.jsonl
   ```

   The trailing summary prints a **fine carrier-offset estimate** (`BpskCarrierOffsetEstimator`)
   and a histogram of the winning-branch offsets — the spread across stations is what sizes the
   default `--step` and `--pairs`.

3. **Compare**:

   ```sh
   nino-compare compare --ours ours.jsonl --nino nino.jsonl
   ```

   Reports matched / **we-missed** (NinoTNC decoded, we didn't) / **we-extra**, and a copy
   percentage. Frames are matched on content (hex), so it is robust to clock skew between the two
   captures. Each missed frame prints its timestamp — capture the audio around it, feed the
   snippet back through `decode`, and deep-dive why we didn't copy it. Fix the modem, add a
   regression test (see `OffAirBpskTests` / `BpskMultiModemTests`), repeat until we match or beat
   the NinoTNC.

## Frame files (JSONL)

One JSON object per line: `{ "t": <seconds>, "hex": "<AX.25 bytes>", "from": ..., "to": ...,
"summary": ..., "offsetHz": ... }`. `t` is wall-clock unix seconds for `mqtt-capture` and seconds
from the start of the recording for `decode`. `hex` (the raw frame bytes) is the comparison key.

## Notes

- `decode` decimates the WAV to the 12 kHz channel DSP rate (matching the daemon); the WAV rate
  must be a multiple of 12 kHz (48 kHz DAX audio is fine).
- The winning-branch offset is meaningful for the **coherent** detector (only a branch near the
  carrier locks); for **differential** it is not (any branch within ±baud/4 decodes) — use the
  fine estimate there.
