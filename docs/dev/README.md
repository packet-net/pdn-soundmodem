# Developer documents

Status: current as of 2026-10-10. Describes what is in docs/dev and what each file is for, how open work is tracked, and the rules all work follows.

This folder is for people changing the code. Nothing here is user documentation: the guide starts at [docs/README.md](../README.md), and the reference tables are under [docs/reference](../reference/config.md).

Every file here opens, under its H1, with a status line: whether it is current, a design record or a reference, the date it was last checked against the code, what it describes, and where the component lives now if it has left this repository. Several have: ARDOP, the OFDM engine, the DSP primitives, Reed-Solomon, LDPC, IL2P, POCSAG and the FlexRadio client each ship as an M0LTE package pinned in [Directory.Packages.props](../../Directory.Packages.props), and each of those packages keeps its own provenance. What every component in this repository is based on, and the licence that follows from it, is [PROVENANCE.md](../../PROVENANCE.md) at the root.

## What is here

| Path | What it is |
|---|---|
| [roadmap.md](roadmap.md) | A pointer: open work is tracked as GitHub issues (below). The roadmap as it stood until 2026-10-10 is frozen at [archive/roadmap-2026-10.md](archive/roadmap-2026-10.md), with a table of which issue each of its items became. |
| [plan.md](plan.md) | The plan record: the decisions of 2026-07-14, the four build phases and what each still owes. Its amendment log is closed at [archive/plan-amendment-log.md](archive/plan-amendment-log.md). |
| [mode-validation.md](mode-validation.md) | The validation ledger: how each mode string in the catalogue has been proven, with a dated append-only record. |
| [ardop-design.md](ardop-design.md) | ARDOP design and scoping, written before the implementation. The implementation is the M0LTE.Ardop package; the bridge onto the shared channel is still here. |
| [modem-plugins.md](modem-plugins.md) | How the daemon loads a modem it does not contain: the plugin contract, registry and loader. |
| [carrier-sense.md](carrier-sense.md) | Why audio cannot answer "is this channel busy" on an FM path, in two separate ways, what does, and what a station with no control cable to its radio is still left with. |
| [mode-modulation-reference.md](mode-modulation-reference.md) | How each NinoTNC-lineage mode is carried on air, FM or SSB, and the FM deviation targets. |
| [receive-levels.md](receive-levels.md) | The measurements behind the TOO LOUD and TOO QUIET thresholds, modem by modem. |
| [false-decodes.md](false-decodes.md) | What a row may claim about a frame: the measurement behind withholding a callsign, and a band SNR, from a reading nothing checked. |
| [frequency-matching.md](frequency-matching.md) | Measuring how far off frequency a heard station is, and transmitting to suit. |
| [uplink-wire-format.md](uplink-wire-format.md) | The station-to-monitor uplink wire format, the normative description UplinkWire.cs cites. |
| [archive/documentation-plan.md](archive/documentation-plan.md) | The plan for the 2026-09 documentation rewrite, finished and archived. |
| [bench/](bench/) | Bench rigs and benchmarks: the NinoTNC cable loop, the QtSoundModem virtual-cable loop, sm-tnctest against the WA8LMF TNC Test CD, and the ARDOP on-air acceptance procedure. |
| [hardware/](hardware/) | Hardware notes: the TM8100 to CM108 and FT-450D to CM108 interface reasoning, the CM108 widget netlist, and the TM8100 internal USB board. The wiring guides themselves are user documents at [docs/hardware/tait-tm8100-cm108.md](../hardware/tait-tm8100-cm108.md) and [docs/hardware/yaesu-ft450d-cm108.md](../hardware/yaesu-ft450d-cm108.md). |
| [ms110d/](ms110d/) | MIL-STD-188-110D Appendix D: the transcribed interop tables and their README, the waveform design, and the standard itself under spec/. |
| [ofdm-fm/](ofdm-fm/) | OFDM-FM design notes: the shipped preset set, how the 8 kHz preset was measured on air, the receive-path findings including what was reverted, and the geometry-signalling work. Carrier sense moved out to [carrier-sense.md](carrier-sense.md), because it turned out not to be about this mode family at all. |
| [refs/](refs/) | Verbatim transcriptions of other people's specifications. Never edited. |
| [plans/](plans/) | Plans for work not started: 2G ALE. |
| [archive/](archive/) | Frozen records of closed work: plans, campaign evidence, handovers and closeouts. Its [README](archive/README.md) states the rules. |

## How open work is tracked

Open work is [GitHub issues](https://github.com/packet-net/pdn-soundmodem/issues), and nothing in this repository keeps a second list of it. A `#N` in a current document, comment or commit means GitHub issue or pull request N; the frozen roadmap's own item numbers (#4 to #19) were different and survive only in the archive.

- A programme of work is an issue labelled `epic`, with its pieces as sub-issues.
- `needs-radio`: needs Tom, a radio and bench or air time. Each such issue is self-contained and none is blocking. Operate as M0LTE.
- `parked`: a request or a sized idea recorded with the decisions taken at the time, not scheduled.
- `ruled-out`: closed as not planned, with the reason, so the question does not get re-asked. Proprietary waveforms and modes already covered by an existing one are filed this way.

New work gets an issue before it gets a branch; a pull request says `Closes #N` for what it finishes. A design that needs more room than an issue body goes in a document under docs/dev (or [plans/](plans/) before it starts), and the issue links to it.

## Standing directives

These apply to every piece of work.

- Proven reliable rather than barely working: bit-exact against an oracle, then the channel models, then a real radio loop before anything is called done.
- Occupied bandwidth never exceeds the reference implementation's, and CI enforces it.
- NinoTNC compatibility is never traded away. New modes are additive, never a reshaping of an existing NinoTNC-compatible mode to suit a different peer.
- Every mode is labelled with the modem or TNC it interoperates with. Candidates rank by: an open spec we can implement from scratch, then guaranteed real-world interop, then packet-data transport over keyboard, beacon or voice modes. See [PROVENANCE.md](../../PROVENANCE.md).
- Anything that changes decode behaviour lands with its sim-ladder A/B and a corpus re-score, and gets a dated entry in [mode-validation.md](mode-validation.md). A mask moves only with a ledger entry that justifies it, and masks come from measured reality, never from aspiration.
- Nothing in the receive work changes a transmitted bit: the parity, QtSM and off-air suites stay the regression gate.
- The 37-frame corpus is exhausted as a discriminator: do not tune against its tail. New tuning decisions go against the capture campaign's `misses-v2`.

## Hardware available

A Flex 6500 (10.45.0.76, on the bench with an ANT1 dummy load, into which GB7RDG's transceiver couples), GB7RDG's HF port, radio1 (a Pi with a CM108 and a radio on a dummy load), and an FM radio loop.
