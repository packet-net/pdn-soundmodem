# OFDM-FM design notes

Status: current as of 2026-09-20. Describes the design notes and measurement records behind the OFDM-FM modes: what was measured, on what rig, what was tried and reverted, and what still limits the waveform. The modem itself is in this repository at `src/Packet.SoundModem/Modems/OfdmFm/` and ships as the `ofdm-fm-*` modes; the operator-facing description is [docs/05-modes.md](../../05-modes.md).

OFDM across the audio path of an ordinary FM transceiver. These pages are the working notes behind the modes.

Almost everything here was measured on one bench, two Tait TM8110s on CM108 interfaces at 25 kHz channel spacing, in September 2026. Where a figure comes from simulation the page says so.

## Start here

| | |
|---|---|
| [profile-set.md](profile-set.md) | The shipped presets, what each one is, and what each one measured. The short answer to "which mode should I run". |
| [preset-design.md](preset-design.md) | How the 8 kHz preset was arrived at, on air, and what still limits it. The single densest page here. |
| [receiver-findings.md](receiver-findings.md) | Everything tried on the receive path, including the four things that looked obviously right, were built, measured no better, and were reverted. The rate ladder, the margin measure, the LDPC comparison and the decode-cost bound live here. |

## The campaign, in order

| | |
|---|---|
| [on-air-plan.md](on-air-plan.md) | The pre-flight, written before anyone had a radio on a bench: what counts as proof, what a null looks like, and what to measure first. |
| [on-air-campaign.md](on-air-campaign.md) | What the bench turned out to be, the stages as they ran, and four rig faults that each taught something. |
| [bandwidth-and-frame-length.md](bandwidth-and-frame-length.md) | Sizing a preset to the measured audio path: the bandwidth sweep, bit loading, and why frame length is the biggest lever. |
| [preset-design.md](preset-design.md) | The day after, and the preset that came out of it. |

## Specific problems

| | |
|---|---|
| [carrier-sense.md](carrier-sense.md) | Why audio cannot answer "is the channel busy" on an FM path, in two separate ways, and what does. Also the best defect of the campaign: an unused modem on the same channel holding the transmitter shut. |
| [geometry-signalling.md](geometry-signalling.md) | Signalling the carrier layout in the burst header, proved on air. The machinery is in the code; no shipped preset uses it, and this page says why. |
| [missing-frames-under-load.md](missing-frames-under-load.md) | Frames a station logs as transmitted that the far end never hears, and two wrong diagnoses before the right one. |

## Two conventions used throughout

**Profiles are written at the rate the channel runs.** An OFDM-FM channel runs at 48 kHz, so a profile carries a 2048-point transform and a 64-sample cyclic prefix, which is a 44.0 ms symbol and 22.727 symbols a second. Sweeps taken before 2026-09-19 evening used a 128-sample prefix and are about 3 % slower for the same arithmetic; pages say so where it matters.

**Carrier layouts live in a station's own file, not in the source.** The built-in presets are in the catalogue; anything else is a JSON profile beside the daemon. [profile-set.md](profile-set.md) has the format.
