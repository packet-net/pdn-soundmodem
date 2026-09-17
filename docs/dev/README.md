# Developer documents

Status: current as of 2026-09-17. Describes what is in docs/dev and what each file is for.

This folder is for people changing the code. Nothing here is user documentation: the guide starts at [docs/README.md](../README.md), and the reference tables are under [docs/reference](../reference/config.md).

Every file here opens, under its H1, with a status line: whether it is current, a design record or a reference, the date it was last checked against the code, what it describes, and where the component lives now if it has left this repository. Several have: ARDOP, the OFDM engine, the DSP primitives, Reed-Solomon, LDPC, IL2P, POCSAG and the FlexRadio client each ship as an M0LTE package pinned in [Directory.Packages.props](../../Directory.Packages.props), and each of those packages keeps its own provenance. What every component in this repository is based on, and the licence that follows from it, is [PROVENANCE.md](../../PROVENANCE.md) at the root.

## What is here

| Path | What it is |
|---|---|
| [roadmap.md](roadmap.md) | The one living roadmap: what is open, parked and ruled out. It absorbed waveform-roadmap.md, and the open receive workstreams of rx-roadmap.md, whose record is [archive/rx-roadmap.md](archive/rx-roadmap.md). |
| [plan.md](plan.md) | The plan record: the decisions of 2026-07-14, the four build phases and what each still owes. Its amendment log is closed at [archive/plan-amendment-log.md](archive/plan-amendment-log.md). |
| [mode-validation.md](mode-validation.md) | The validation ledger: how each mode string in the catalogue has been proven, with a dated append-only record. |
| [ardop-design.md](ardop-design.md) | ARDOP design and scoping, written before the implementation. The implementation is the M0LTE.Ardop package; the bridge onto the shared channel is still here. |
| [modem-plugins.md](modem-plugins.md) | How the daemon loads a modem it does not contain: the plugin contract, registry and loader. |
| [mode-modulation-reference.md](mode-modulation-reference.md) | How each NinoTNC-lineage mode is carried on air, FM or SSB, and the FM deviation targets. |
| [receive-levels.md](receive-levels.md) | The measurements behind the TOO LOUD and TOO QUIET thresholds, modem by modem. |
| [false-decodes.md](false-decodes.md) | What a row may claim about a frame: the measurement behind withholding a callsign, and a band SNR, from a reading nothing checked. |
| [frequency-matching.md](frequency-matching.md) | Measuring how far off frequency a heard station is, and transmitting to suit. |
| [uplink-wire-format.md](uplink-wire-format.md) | The station-to-monitor uplink wire format, the normative description UplinkWire.cs cites. |
| [archive/documentation-plan.md](archive/documentation-plan.md) | The plan for the 2026-09 documentation rewrite, finished and archived. |
| [bench/](bench/) | Bench rigs and benchmarks: the NinoTNC cable loop, the QtSoundModem virtual-cable loop, sm-tnctest against the WA8LMF TNC Test CD, and the ARDOP on-air acceptance procedure. |
| [hardware/](hardware/) | Hardware notes: the TM8100 to CM108 interface reasoning, the CM108 widget netlist, and the TM8100 internal USB board. The wiring guide itself is a user document at [docs/hardware/tait-tm8100-cm108.md](../hardware/tait-tm8100-cm108.md). |
| [ms110d/](ms110d/) | MIL-STD-188-110D Appendix D: the transcribed interop tables and their README, the waveform design, and the standard itself under spec/. |
| [refs/](refs/) | Verbatim transcriptions of other people's specifications. Never edited. |
| [plans/](plans/) | Plans for work not started: 2G ALE. |
| [archive/](archive/) | Frozen records of closed work: plans, campaign evidence, handovers and closeouts. Its [README](archive/README.md) states the rules. |
