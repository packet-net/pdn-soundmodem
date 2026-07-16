# FreeDV datac ↔ HF radio loop (the proven-reliable gate)

The `freedv-datac*` modes are code-complete and validated against stock codec2 tooling in both
directions **at the audio/WAV level** (plan §17 entries of 2026-07-16; PRs #17–#21). This
document is the procedure for the step none of that covers: **the real radio path** — task #4's
"proven reliable, not only just working" gate. Written 2026-07-16 while the implementation
context was fresh; the numbers and file/flag names below are verified against main @ `b5f56ae`.

Everything here assumes the working agreements: run the real end-to-end flow on hardware, don't
stop at unit tests; record real counts, not impressions; one heavy job at a time on this box.

## What is already proven (don't re-test at the bench)

- **Waveform interop, both directions, all six modes**: our TX decodes in codec2's own
  `freedv_data_raw_rx` byte-exactly, and our RX decodes codec2's `freedv_data_raw_tx` (clean and
  at +22 Hz / ~5 dB SNR3k). CI-guarded via checked-in vectors (`samples/freedv/`).
- **Carrier offset** to ±45 Hz, **sample-clock error** to ±600 ppm, AWGN knee equal to codec2's
  (19/20 = 19/20 on identical audio).
- **OBW ≤ FreeDV's own** for all six modes (CI: `Never_Wider_Than_FreeDVs_Own_Transmission`).
- KISS integration: AX.25 frames as IL2P+CRC inside the datac payloads, spanning packets
  (datac0 carries full frames across ~5 packets; datac1 tested to 1000-byte frames).

## What ONLY the radio loop can prove (the target list)

| # | Item | Why the bench is needed | How to measure |
|---|---|---|---|
| 1 | **SSB path distortion** — TX ALC compression of the OFDM envelope, RX AGC pumping, filter ripple/group delay at the 1500 Hz-centred passband edges | none of it is in the simulations | decode counts per mode at strong signal; if a wide mode (datac1, 1.7 kHz) underperforms the narrow ones, suspect passband edges first |
| 2 | **Audio levels / clipping** through real sound devices | the modem emits codec2's clipped waveform; a wrong drive level re-clips or under-drives | see § Levels — set once, then leave alone |
| 3 | **PTT → RF timing** with `--txdelay` | the burst preamble is fixed; the radio's PTT-to-RF settle is not | sweep `--txdelay` 100→300 ms; find the floor where 10/10 holds |
| 4 | **EnergyBusyDetector as the CSMA source** | burst DCD asserts ~1 frame late (known); energy detect is the practical carrier sense — never noise-tested on a real RX noise floor | with one end receiving band noise only, confirm `ChannelBusy` is quiet; key the far end: confirm busy asserts during the burst and releases after |
| 5 | **datac1 phantom-DCD tail** — the 16-bit UW gives ~10 %/burst odds the end-of-burst check lingers one phantom packet (~4 s stuck DCD; CRC backstop then ends it) | measured probability is from bench audio, not radio | run ≥30 datac1 bursts; count stuck-DCD events and their duration; confirm a back-to-back burst inside the window is the only loss mode |
| 6 | **pdn↔pdn CSMA on a shared channel** | untested anywhere | both ends offered traffic simultaneously (see § Drive); count collisions/lost frames vs the p-persistence settings |
| 7 | **Low-SNR floor per mode** (datac4/13/14 are negative-SNR designs; only ~5.5 dB was tested) | attenuator gives calibrated steps | drop TX power / add attenuation in 3 dB steps; record the knee per mode; compare against codec2's published MPP/AWGN figures |
| 8 | **Long soak** | lockups, leaks, drift | ≥100 bursts unattended per direction; zero stuck states |

**Out of scope**: coexistence with regular FreeDV *voice* — per Tom (2026-07-16), data and
voice never share a channel; do not test or engineer for it. A bench RF loop also produces **no
multipath** — ionospheric behaviour (the datac modes' design case) needs a real sky-wave path
and is a separate, later exercise (an NVIS contact with a second station, or FreeDATA
interop over air).

## The rig

Two HF SSB transceivers into dummy loads / a shared attenuated path (or one TX into an SDR RX
for the one-way legs). Data/accessory ports strongly preferred over mic/speaker (fixed levels,
no mic processing). One sound device + PTT line per radio, one daemon instance per radio, on
this box or split across two machines.

Radio settings, both ends:
- **USB** (upper sideband), same dial frequency. The acquisition search covers **±50 Hz**
  (coarse grid) — any synthesized rig is fine; if one radio is analogue/drifty, keep it within
  ±40 Hz to leave margin.
- **All speech processing/compression OFF**; TX ALC barely moving (see § Levels); RX AGC fast
  (or off with manual RF gain if pumping is suspected).
- Filters wide enough for the mode: datac1 needs ~1.7 kHz centred on 1500 Hz — a normal 2.4 kHz
  SSB filter is fine for every mode.

## Levels (do this first, once)

- **TX drive**: play a datac3 burst on repeat (`--wav` loopback or key real traffic) and raise
  the playback level until ALC *just* registers, then back off slightly. OFDM through ALC
  compression distorts subtly — the clipped waveform is already at its designed PAPR, so ALC
  should be idle. Record the mixer settings (`amixer -c <card> ...`) in the results section.
- **RX capture**: peaks around −6 dBFS on the burst; never clipping. The demod's amplitude
  estimation is level-tolerant, so approximate is fine.
- Remember this box's ALSA gotchas (see `qtsm-loop.md`): launch every audio process under
  **`sg audio -c "…"`**, and use **numeric card indices** (`plughw:N,0`), not names.

## Drive

One daemon per radio (adjust devices/PTT to the rig at hand — CM108 PTT on hidraw, or serial
RTS; `--modem 0:freedv-datac3` etc.; the freedv modes force the 48 kHz DSP path):

```sh
# end A
sg audio -c "pdn-soundmodem --device plughw:3,0 --capture-rate 48000 --kiss 8310 \
    --modem 0:freedv-datac3 --ptt cm108:/dev/hidraw0 --txdelay 200 --quality-frames"
# end B (second device / second box)
sg audio -c "pdn-soundmodem --device plughw:4,0 --capture-rate 48000 --kiss 8311 \
    --modem 0:freedv-datac3 --ptt serial:/dev/ttyUSB1:rts --txdelay 200 --quality-frames"
```

The KISS cross-driver is the existing **`qtsm-bench`** (generic KISS↔KISS, despite the name):

```sh
qtsm-bench --qtsm-port 8310 --our-port 8311 --label freedv-datac3 --frames 10 \
    --direction both --frame-timeout-ms 30000
```

`--frame-timeout-ms` matters: a datac1 multi-packet burst is ~13 s of audio — size timeouts per
mode (datac0/3/4/13/14 bursts of one 60-byte frame run ~4–45 s; datac14 spans ~28 packets, so
give it minutes, or test it with short frames only). For the CSMA test (#6), run two
`qtsm-bench` instances pushing opposite directions simultaneously.

`--quality-frames` surfaces per-frame `FrameQuality` (RS corrections, CRC, frequency offset) on
the KISS side channel — record `FoffEstHz` per direction once; it measures the real
radio-to-radio frequency error and should sit well inside ±50 Hz.

## Matrix (record real counts; 10 frames/direction unless stated)

Order: **datac3 first** (robust, 500 Hz, medium payload — shakes down the rig), then datac1
(the wide workhorse — passband stress), datac0, then the narrow trio datac4/13/14.

| mode | A→B strong | B→A strong | knee (dB atten where <10/10) | txdelay floor | notes |
|---|---|---|---|---|---|
| freedv-datac3 | /10 | /10 | | | |
| freedv-datac1 | /10 | /10 | | | 508 B frames too; phantom-DCD count over ≥30 bursts |
| freedv-datac0 | /10 | /10 | | | frame spans ~5 packets |
| freedv-datac4 | /10 | /10 | | | negative-SNR design — expect a deep knee |
| freedv-datac13 | /10 | /10 | | | |
| freedv-datac14 | /10 | /10 | | | short frames only (28 packets/60 B frame) |

Then: busy-detect checks (#4), CSMA contention (#6), and the ≥100-burst soak (#8) on datac3.

**Pass criteria for the task-#4 gate**: 10/10 both directions at strong signal for every mode;
knee within ~3 dB of the bench AWGN knee; `--txdelay` floor documented per radio; zero stuck
states in the soak (the datac1 phantom-DCD tail may occur — it must always self-clear via the
CRC backstop, and its measured rate goes in the notes).

## When it's done

Record results in this file (replace the blank matrix), add the plan §17 entry, and close the
HF-loop half of task #4. Failures: investigate before re-running (the reds keep turning out to
be real bugs — house rule); anything that looks like a modem defect gets a GitHub issue with
the capture attached (`arecord` the RX audio of a failing run — WAV evidence turns a shrug into
a fix).

If a stock-FreeDV/FreeDATA station is available later, the over-air stretch goal is: our daemon
↔ FreeDATA's modem on a real path — the waveform layer is already proven compatible, so this
tests only propagation + levels.
