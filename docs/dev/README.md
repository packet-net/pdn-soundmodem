# Developer documents

This folder is for people changing the code. Nothing here is user documentation: the guide starts at docs/README.md, which a later PR of the documentation rewrite writes, and the reference tables go under docs/reference.

Every file in this folder opens with a status line saying what it describes and when it was last checked against the code. Those status lines are added in a later PR of the rewrite; until then each file's own header stands.

## What is here

| Path | What it is |
|---|---|
| [roadmap.md](roadmap.md) | The one living roadmap: what is open and what is parked. [waveform-roadmap.md](waveform-roadmap.md) and [rx-roadmap.md](rx-roadmap.md) are waiting on a later PR: waveform-roadmap.md is merged into it and deleted, and rx-roadmap.md goes to the archive once its open items are in roadmap.md. |
| [plan.md](plan.md) | The plan record: the decisions of 2026-07-14, the phases and the blocked list. Its amendment log is closed and lives in [archive/plan-amendment-log.md](archive/plan-amendment-log.md). |
| [mode-validation.md](mode-validation.md) | The validation ledger: how each mode string in the catalogue has been proven, with a dated append-only record. |
| [ardop-design.md](ardop-design.md) | ARDOP design and scoping notes. The implementation is the M0LTE.Ardop package. |
| [modem-plugins.md](modem-plugins.md) | How the daemon loads a modem it does not contain: the plugin contract, registry and loader. |
| [mode-modulation-reference.md](mode-modulation-reference.md) | How each NinoTNC-lineage mode is carried on air, FM or SSB, and the FM deviation targets. |
| [receive-levels.md](receive-levels.md) | The measurements behind the TOO LOUD and TOO QUIET thresholds, modem by modem. |
| [false-decodes.md](false-decodes.md) | What a row may claim about a frame: the measurement behind withholding a callsign, and a band SNR, from a reading nothing checked. |
| [frequency-matching.md](frequency-matching.md) | Measuring how far off frequency a heard station is, and transmitting to suit. |
| [uplink-wire-format.md](uplink-wire-format.md) | The station-to-monitor uplink wire format, the normative description UplinkWire.cs cites. |
| [documentation-plan.md](documentation-plan.md) | The plan for this documentation rewrite. It moves to the archive when the work is finished. |
| [bench/](bench/) | Bench rigs and benchmarks: the NinoTNC cable loop, the QtSoundModem virtual-cable loop, and sm-tnctest against the WA8LMF TNC Test CD. |
| [hardware/](hardware/) | Hardware notes: the TM8100 to CM108 interface reasoning, the CM108 widget netlist, and the TM8100 internal USB board. The wiring guide itself is a user document at [docs/hardware/tait-tm8100-cm108.md](../hardware/tait-tm8100-cm108.md). |
| [ms110d/](ms110d/) | MIL-STD-188-110D Appendix D: the transcribed interop tables and their README, the waveform design, and the standard itself under spec/. |
| [refs/](refs/) | Verbatim transcriptions of other people's specifications. Never edited. |
| [plans/](plans/) | Plans for work not started: 2G ALE. |
| [archive/](archive/) | Frozen records of closed work: plans, campaign evidence, handovers and closeouts. Its [README](archive/README.md) states the rules. |
