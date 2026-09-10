# Documentation plan

Written 2026-09-10. Reviewed and approved by Tom the same day, with the decisions in section 13 taken as recorded there. When the work is finished, this file goes to docs/dev/archive/ with the other closed plans.

## 1. What we have

| | |
|---|---|
| Markdown files tracked | about 140 |
| Lines of markdown | 27,500 |
| docs/ms110d (a closed research programme) | 84 markdown files, 9,300 lines, 30 MB including a 12.9 MB copy of the standard and 17 MB of campaign CSV and logs |
| Top-level docs/*.md | 25 files, 9,700 lines; 4 of them are something an operator would read |
| Root | README 226 lines, INSTALL 204, CONFIG 3,307, PROVENANCE 79, CLAUDE 112 |
| Docs site, index page, link checker | none |
| Shipped in the .deb | only soundmodem.example.json. Everything else is reached through GitHub links from release notes, journal lines and error messages |

What the review found:

1. **The user documentation is three root files, and the problem is shape, not accuracy.** README, INSTALL and CONFIG are right in most places and CONFIG is nearly complete. But README is a 226-line feature essay, CONFIG wraps 350 rows of reference in 1,700 lines of narrative, and the two repeat each other. There is no reading order and no page that says where to start.
2. **Nearly everything else is a plan or a campaign record.** Of the 25 top-level docs, 13 are plans, 7 are evidence, 1 is a spec transcription. Only modes.md, pdn-decode.md, tnc-test-cd.md and frequency-matching.md are user reading. Below that, docs/ms110d holds 56 evidence folders for a programme that closed on 2026-08-21.
3. **Docs that operators are sent to open with a false status line.** INSTALL sends FlexRadio users to flex-integration.md, whose header says "Nothing implemented". CONFIG sends ARDOP users to ardop-design.md, which says "design, pre-implementation". uplink-plan.md says "Nothing is built yet". qso-tool-plan.md says "No code exists". All four features shipped. Findings were appended and the headers never revised.
4. **Doc and code disagree in places a user will hit.** `--help` prints "see source header for usage". Running with no arguments starts a station on the default sound card, although INSTALL says it prints the option list. CONFIG's worked example puts `bind` inside `waterfall`, a key that does not exist. CONFIG documents `survey.decodeClaimSeconds`, which the config cannot set. CONFIG has `--ardop` precedence backwards. README still says ARDOP is exclusive with the packet modems. The `frequencyMatching` section, ten keys that can retune a transmitter, appears in no user document. `flex.receiveOnly` is undocumented. The npm package is not mentioned outside web/. Section 9 has the full list.
5. **Three overlapping roadmaps** (roadmap.md, plan.md section 17, waveform-roadmap.md), whose own header says they must be updated together, plus a fourth (rx-roadmap.md) that stopped in August. plan.md is 1,651 lines, 85 percent of it an amendment log with thousand-character lines.
6. **Dead and stray files.** samples/freedv (2.2 MB) and tools/oracle describe tests deleted on 2026-07-18 when the OFDM code moved to its own package. Two 7 MB ladder WAVs, their manifests and oarc.png sit in the repo root, committed by accident on 2026-08-03 inside a KISS logging commit. docs/bench holds a csproj that is not in the solution.
7. **Source code cites docs by path.** About fifteen source files point at plan.md, ms110d/tables, qpsk/plan.md, ardop/plan.md, the hardware notes and bench evidence. Every move has to update those citations in the same change.

What is good and must survive: modes.md matches the catalogue mode for mode. receive-levels.md matches the code's thresholds. mode-validation.md is a real system of record with a maintenance rule. The TM8100 wiring guide is a proper user document. web/package/README.md is published to npm on every release and reads well. INSTALL's permissions and package-contents sections are correct.

## 2. What we are aiming for

1. **One front door.** README says what this is, who it is for, how to install it in a minute, and where the guide starts. Under 100 lines.
2. **One guide with a reading order.** A newcomer reads it top to bottom and ends with a working station. An experienced operator jumps in by task from the index.
3. **Reference is tables.** Every key, flag, port and file, with type, default and one sentence. No stories.
4. **Everything else lives under docs/dev** and says at the top whether it is current or a record.
5. **Nothing is documented twice.** A fact has one home. Everything else links to it.

The people the guide serves, in the order they tend to arrive:

- A packet operator with a radio and a USB sound-card interface who wants a TNC for LinBPQ, the PDN node or APRS software. VHF FM first. HF SSB is the same person a week later.
- A FlexRadio owner, who has no sound card and no PTT lead.
- Somebody with no antenna, listening on a public web receiver, and perhaps running a public page or feeding the monitor site.
- A Winlink user who wants ARDOP for Pat or Winlink Express.
- Somebody transmitting pages to DAPNET pagers.
- The operator of a monitor site.
- A builder wiring a Tait TM8100.
- A developer: the NuGet library, the npm WebAssembly package, a modem plugin, or a contributor.
- Somebody with a recording to decode.

## 3. The shape

```
README.md                          front door
PROVENANCE.md                      stays: the licence record, cited from packaging/copyright
CLAUDE.md                          stays: agent notes, not user documentation
docs/
  README.md                        the guide's contents page, reading order, and an "I want to" index
  01-install.md
  02-first-station.md
  03-radios-and-interfaces.md
  04-levels.md
  05-modes.md
  06-connect-your-software.md
  07-station-page.md
  08-hf.md
  09-web-receivers.md
  10-public-monitor.md
  11-logging-and-metrics.md
  12-troubleshooting.md
  13-decode-a-recording.md
  hardware/tait-tm8100-cm108.md
  reference/
    config.md                      every key
    command-line.md                every flag, and precedence against --config
    ports-and-endpoints.md         KISS, ARDOP, paging, HTTP, API, metrics, uplink
    files.md                       what the package installs and what the daemon writes
    grafana/pdn-soundmodem.json
  images/waterfall.png
  dev/
    README.md                      index, and the rule that nothing here is user documentation
    roadmap.md                     the one living roadmap
    plan.md                        decisions and phases, trimmed (see decision 3)
    mode-validation.md             the ledger, unchanged
    ardop-design.md
    modem-plugins.md
    mode-modulation-reference.md
    receive-levels.md
    frequency-matching.md
    uplink-wire-format.md
    bench/                         ninotnc-loop.md, qtsm-loop.md, tnc-test-cd.md
    hardware/                      the TM8100 notes, the netlist, the internal USB board
    ms110d/                        README.md, design.md, tables/, spec/
    refs/ardop-spec-rev2.md        verbatim, never edited
    plans/ale.md
    archive/                       frozen records, with a README saying so
```

Folder READMEs in samples/, tools/ and web/ stay where they are. They describe the folder they sit in and would be worse anywhere else. This is the one exception to "everything else under docs/dev" and it is listed as decision 6.

Plain markdown, relative links, rendered by GitHub. No docs site now. The layout works unchanged under a static site generator if one is wanted later.

## 4. The guide, page by page

Every page has the same skeleton: one sentence saying what you will have at the end, "Before you start", the steps, "Check it worked", "If it did not", and "Related". Pages say what to do and what you should see. Reasons go in a short "How it works" section at the end of the page, capped at a few paragraphs, or in docs/dev.

**docs/README.md, the contents page.** The reading order (01 to 13) with one line each, then an "I want to" list that maps tasks to pages and sections: connect LinBPQ, run Winlink, set my TX level, listen without a radio, publish my station, read the journal, and so on. Under 60 lines.

**01 Install.** Who: anyone. Start: a Debian, Ubuntu or Raspberry Pi OS machine. End: the package installed, the service present, the config file seeded, and the knowledge that it will not run until configured. Covers picking the .deb, the one-line installer, checksums, no sudo, what went where, the service user, upgrading, removing. Leaves out all configuration.

**02 Your first station.** Who: a packet operator with a radio and a USB sound-card interface. Start: package installed. End: one AFSK 1200 modem decoding frames off air, showing them on the station page, with node or APRS software attached over KISS. Steps: find the sound card, choose the PTT line, write a six-line config, start the service, read the journal, open the station page, see a frame, connect the software. One path only, VHF FM on an APRS frequency, because it produces a decode within minutes without a peer. Each step says what you should see and where to go if you do not. Somebody with no radio to hand can follow it with a demo recording replayed through the daemon, and the page says how.

**03 Radios and interfaces.** Who: somebody choosing or wiring hardware. Covers the audio path (USB sound cards, CM108-class interfaces such as the DRA and RB-USB RIM, the Tait internal board), each PTT method with its config block (serial RTS or DTR, CM108 GPIO with the udev rule, VOX and why it is a poor choice), FlexRadio (headless and attach modes, DAX channels, the radio keying itself, receive-only), and the web receiver as a receive-only device. Ends with the user knowing which `device` string and `ptt` block to write. The FlexRadio material comes from the setup half of flex-integration.md.

**04 Levels.** Who: everyone, right after the first decode. Covers why level matters in two paragraphs, the input level meter and its target zone, setting receive gain from the station page or the config, the TOO LOUD and TOO QUIET badges and what to do about each, the transmit test tones (two-tone for SSB linearity, one tone for FM deviation by Bessel null, with the table of tone frequencies), setting transmit gain, AGC and mic boost being forced off, and where levels are remembered. Conclusions come from receive-levels.md; the measurements stay in docs/dev.

**05 Modes.** Who: somebody deciding what to run. Covers the families in plain words (which radio path, what they talk to), the full mode table (mode string, bit rate, framing, radio path, interoperates with, verification level), running several modems at once on sub-channels, audio centre versus RF placement, diversity banks in one paragraph, accepting plain IL2P, NinoTNC ident beacons, plugins in one paragraph, and ARDOP and POCSAG as things that are configured differently. The table is today's modes.md, plus rows that make clear ARDOP and POCSAG are not catalogue modes.

**06 Connect your software.** Who: node, APRS and Winlink users. Covers KISS over TCP (the shared port and sub-channels, a port per modem for software that only speaks channel 0, the KISS parameters and the fact they are honoured live, ACKMODE, no authentication and what `bind` means for that), recipes for LinBPQ, the PDN node and Dire Wolf-style APRS clients, ARDOP for Pat and Winlink Express (the `ardop` mode, the two ports, the client settings, what differs from ardopcf), and POCSAG paging (the endpoint, the PAGE command, what comes back).

**07 The station page.** Who: everyone. Covers each control and panel in the order it appears: dial, sideband and span; Listen; the links pane and transcripts; the decoded frames panel and every badge; the mixer group; the TX test; Last TX power and SWR; the modem chips and host badges; public mode; the API key and what it unlocks; opening the page beyond loopback and the warning that produces.

**08 HF.** Who: HF operators, the 40 m IL2P network, Winlink over HF. Covers SSB and the passband, band plans in RF terms (`rfFrequency`, `dialFrequency`, `sideband`, and the dial the daemon tells you to set), several modes sharing one passband with the 40 m example, frequency matching (what it does, when it retunes, how to turn it off), ident beacons, and FM radios in one paragraph via the `fm` sideband. This is where frequency-matching.md's orphaned content lands.

**09 Web receivers.** Who: somebody with no antenna. Covers the `ubersdr` device, what it can and cannot do, on-demand sessions, the public page options, and the frame log as the natural companion.

**10 The public monitor.** Who: a station owner who wants to appear on the monitor site, and the site operator. Covers publishing (the `publish` block, getting a token, what is sent and what never is, leaving), then running a site (the `monitor` flavour, the receiver directory, allow and deny, private uplinks, minting tokens).

**11 Logging and metrics.** Covers the SQLite frame log and a few useful queries, survey captures, raw capture, the two metrics endpoints, and importing the Grafana dashboard.

**12 Troubleshooting.** Covers reading the journal (the rx, tx, kiss and audio lines, field by field), the catalogue of start-up refusals and what each means, permission problems, a no-decodes checklist, CPU starvation in containers, and dead-feed restarts. Most of this exists in CONFIG's "Watching a station work" and "What is rejected at start-up" and needs cutting to the point.

**13 Decode a recording.** Covers pdn-decode, sm-decode and sm-pocsag. States up front that these need a source build because the .deb does not ship them.

**hardware/tait-tm8100-cm108.md.** Today's wiring guide, kept as a user document with a light edit. The reasoning, the netlist and the internal board design go to docs/dev/hardware.

## 5. The reference

Four pages, all tables, written from the code and checked key by key against DaemonConfig.cs and Program.cs, not from CONFIG.md.

- **config.md.** One section per config block in the order the top-level table lists them. Each section: a minimal example, then a table of key, type, default, one sentence. Validation rules as one-line notes under the table. Cross-links into the guide for anything that needs more than a sentence. Target under 700 lines.
- **command-line.md.** Every flag with its argument, default, and one sentence. A short table of what wins when a flag and the config file both set a thing, taken from the code rather than from CONFIG's current, partly wrong, table. Which flags have no config equivalent, and which config sections have no flag.
- **ports-and-endpoints.md.** Every listener and what it speaks: the KISS command set, the ARDOP host interface (a pointer to ardopcf's documentation plus the documented divergences), the paging line protocol, the HTTP routes, the API endpoints and their auth, the metrics formats, the uplink.
- **files.md.** What the package installs, what the daemon reads, what it writes and when, and what survives an upgrade or a purge.

## 6. The README

Under 100 lines, in this order:

1. One paragraph in plain words. A software TNC for Linux. Turns a sound card, a FlexRadio or a public web receiver into a packet modem. Talks KISS over TCP to your node or APRS software. Runs every NinoTNC mode, ARDOP for Winlink, and POCSAG paging. Shows the band in a browser.
2. Who it is for, three bullets.
3. Install in a minute: the one-liner, then "now follow the guide" with a link to docs/README.md.
4. The screenshot.
5. What it does, as a short table of areas each linking into the guide: modes and what they talk to, radios and interfaces, the station page, HF, web receivers and the monitor, ARDOP, paging, logging.
6. Status in one paragraph: what is proven on air, on the bench, or in simulation, with a link to the mode table.
7. Developers: the NuGet package, the npm package, building from source, and the docs/dev index.
8. Licence and credits in a few lines, pointing at PROVENANCE.md.

## 7. Developer docs and the archive

**docs/dev** holds what a contributor needs and nothing that a user needs. Every file in it opens with a status line: what it describes, when it was last checked against the code, and, where a component has moved to another repository, where it went. Each kept document gets a correctness pass during the move: claims checked against the code, stale sections cut or sent to the archive.

**docs/dev/archive** holds closed plans and evidence. Nothing in it is edited, per the standing rule that a record is not prose. Its README says: these are records of work as it happened, they may describe code that has since changed or left this repository, and the current state is in docs/dev. Files keep their names. Source comments that cite them are updated to the new path in the same PR.

The living-document set shrinks to two: roadmap.md for what is open and parked, and mode-validation.md for what is proven. plan.md's decisions and phases stay as a short record; its amendment log is closed and archived (decision 3).

## 8. Where every current file goes

Root:

| File | Disposition | Note |
|---|---|---|
| README.md | rewrite | section 6 |
| INSTALL.md | content becomes 01-install.md; the file is deleted | past releases' notes link to INSTALL.md on main and will 404; accepted. scripts/release-notes.py points at docs/01-install.md from now on |
| CONFIG.md | split into reference/config.md and the guide pages; then deleted | the strings in DeviceDiagnostics.cs, UplinkToken.cs, Program.cs and StationFactory.cs, and the check in packaging/test-deb.sh, move to the new paths |
| PROVENANCE.md | keep at root; correctness pass | ARDOP, OFDM, LDPC, IL2P and Flex are described as in-tree and now live in M0LTE.* packages |
| CLAUDE.md | keep; update "what lives where" and the plan.md rule | |
| soundmodem.example.json | replace with a minimal working config: the four or five keys a first station needs, one short comment pointing at the reference, nothing else | 247 lines of commentary that duplicate CONFIG.md, and it is the first thing a user edits. Tom: no masses of JSON comments |
| fm-ladder-dryrun.wav, .manifest.json, ssb-ladder-dryrun.wav, .manifest.json, oarc.png | delete | committed by accident on 2026-08-03; nothing references them |

docs/ top level:

| File | Disposition | Note |
|---|---|---|
| 40m-monitor-plan.md | archive | retired 2026-09-03 |
| ardop-design.md | dev; fix the status line; say the implementation is M0LTE.Ardop | CONFIG and code cite it |
| flex-integration.md | setup half into 03; protocol notes and the slice-outage record to archive | INSTALL points here and its header says nothing is implemented |
| freedv-hf-loop.md | archive | a bench procedure that was never run |
| freedv-ota-plan.md | archive | campaign complete 2026-08-15 |
| frequency-matching.md | content into 08 and reference/config.md; the file to dev | orphan today, and the only place the section is documented |
| mode-modulation-reference.md | dev, verbatim | cited from eight source files |
| mode-recogniser.md | archive | design for something never built |
| mode-validation.md | dev, unchanged | the ledger |
| modem-binding.md | dev as modem-plugins.md; one paragraph in 05 | |
| modes.md | becomes 05-modes.md | exact against the catalogue today |
| monitor-plan.md | archive | its config half is already reference material |
| ninotnc-24h-continuous-losses.md | archive | provenance for a checked-in test corpus |
| ninotnc-loop.md | dev/bench | |
| ofdm-design.md | archive | the OFDM code left the repo on 2026-07-18 |
| pdn-decode.md | becomes 13-decode-a-recording.md | |
| plan.md | decisions and phases to dev/plan.md; amendment log to archive | decision 3 |
| qso-tool-plan.md | delete | pdn-qso has its own repository and this says no code exists |
| qtsm-loop.md | dev/bench | |
| receive-levels.md | dev; conclusions into 04 | thresholds match FrameLevelLimits.cs today |
| roadmap.md | dev; absorbs waveform-roadmap.md and the open items of rx-roadmap.md | the one living roadmap |
| rx-roadmap.md | archive, after its open items move | |
| tnc-test-cd.md | dev/bench | a benchmark and its scoreboard |
| uplink-plan.md | section 4.2 to dev/uplink-wire-format.md; the rest to archive | UplinkWire.cs calls 4.2 the normative wire format |
| waveform-roadmap.md | merged into roadmap.md; deleted | |
| waterfall.png | docs/images | |
| grafana/ | docs/reference/grafana | |

docs/ subfolders:

| Path | Disposition | Note |
|---|---|---|
| ms110d/README.md, design.md, tables/, spec/ | dev/ms110d | cited from shipping source |
| ms110d, the other 20 markdown files (plans, closeouts, handovers, evaluations) | archive | closed programme |
| ms110d/evidence, 56 folders | archive, untouched | frozen records already, per CLAUDE.md |
| hardware/tm8100-cm108-interface.md | docs/hardware/tait-tm8100-cm108.md | user document |
| hardware, the notes, the netlist, the internal USB board | dev/hardware | |
| bench/, all of it including the loose scripts and the missscore csproj | archive | |
| refs/ardop-spec-rev2.md | dev/refs, never edited | |
| ale/plan.md | dev/plans/ale.md; correct the paragraph that argues against an MS110D state that closed in July | |
| ardop/plan.md | archive; update the three tool citations | parked 2026-08-08 |
| qpsk/plan.md | archive; update the code citations | closed 2026-08-07 |
| cfo/ | archive | |

samples, tools, web:

| Path | Disposition | Note |
|---|---|---|
| samples/README.md | keep in place; make it index all eight folders | it lists four |
| samples/demo/README.md | keep; linked from README and 05 | the "hear it" page |
| samples/ardop, offair/*, pocsag READMEs | keep in place | fixture notes beside fixtures |
| samples/freedv/README.md, PROVENANCE.md and the vectors | delete | the tests that read them left on 2026-07-18 |
| tools/Packet.SoundModem.NinoCompare, tools/Packet.SoundModem.UberSdr READMEs | keep in place | still built |
| tools/oracle/README.md and the harness | delete | |
| web/README.md | keep in place | contributor notes for that folder |
| web/package/README.md | keep; review; link from README | published to npm on every release |

After this: about 19 user pages, all rewritten; about 25 developer documents, all reviewed; and an archive of roughly 100 files, mostly evidence folder READMEs, none of them reachable from the front door.

## 9. Drift to fix in the rewrite

Found by checking the docs against the code. The rewrite fixes the documentation side; the first three need code changes and are the first PR.

| # | Today | Fix |
|---|---|---|
| 1 | `--help` prints "see source header for usage" | print real usage |
| 2 | `pdn-soundmodem` with no arguments starts a station on device `default` | print usage and exit (decision 1) |
| 3 | The usage comment in Program.cs still says ARDOP is exclusive and the KISS server is not started, and gives the wrong headless DAX default | correct it, since the help text will come from it |
| 4 | README says `--ardop` is exclusive with `--modem` and `--paging` and CSMA persistence is forced to 255 | ARDOP shares the channel now |
| 5 | CONFIG's worked example has `bind` inside `waterfall` | the waterfall uses the top-level `bind` |
| 6 | CONFIG documents `survey.decodeClaimSeconds` | the config cannot set it |
| 7 | CONFIG says `--ardop PORT` is used only if the file has no ardop section | the flag wins |
| 8 | `frequencyMatching` is in no user document; `api` is missing from CONFIG's top-level table; `flex.receiveOnly` is undocumented | reference/config.md is generated from the code and checked by a test |
| 9 | CONFIG and INSTALL say everything has a command-line equivalent | about two thirds of the sections have none |
| 10 | README lists `pocsag1200` as though it were a mode | paging is the `paging` section, with a baud |
| 11 | README's release paragraph omits the npm package | |
| 12 | ModemConfig.Mode's doc comment lists 8 of 38 modes | |
| 13 | README and INSTALL use 0.7.0 in the build example at version 0.63.0 | use a placeholder |

## 10. Writing rules

These apply to every user page and to the README.

- Lead with what the reader will have when they finish. Then tell them what to do, then what they should see.
- One idea per sentence. Short paragraphs. A sentence beats a label with a colon.
- Say what the software does. Do not praise it, do not explain why a design is clever, and do not tell the story of how a decision was reached. That material belongs in docs/dev if it belongs anywhere.
- Words to leave out: genuinely, honest, honestly, deliberately, exactly, simply, just, robust, seamless, note that, worth knowing, worth remembering, the point is, the thing that matters, which is the whole idea. No "not X, Y" reversals as a stylistic habit. No rhetorical questions. No bold sentence openers doing the job of a heading.
- One name per thing, used everywhere: pdn-soundmodem or "the modem" for the program; "the station page" for the browser page; "your node or APRS software" in the guide and "host" in the reference for what attaches over KISS; "sub-channel" for the KISS port nibble; "sound card" for any USB audio device and "interface" for a CM108-class radio interface.
- Keys, flags, ports and paths in code font. Commands and expected output in fenced blocks. Show the journal line the reader should see.
- Plain ASCII. Hyphens, never dashes. No hard wrapping; one paragraph is one line.
- Do not document what the code does not do, and do not document internals in the guide.
- Every page links onward. Nothing is a dead end.

## 11. Keeping it true

- A test in the existing suite, modelled on the pin that holds KnownModes at 38, that reflects the JSON property names of DaemonConfig and its nested types and fails if any is absent from reference/config.md; the same for every catalogue mode against 05-modes.md and every flag in Program.cs against reference/command-line.md. Drift then fails CI instead of waiting for a reader.
- A relative-link check over every markdown file, run in ci.yml. A short script is enough.
- SourceTextTests extended to docs/ for the ASCII and dash rules, if it does not already cover markdown.
- Every docs/dev file carries its status line. The archive README carries the freeze notice.
- The rule already in CLAUDE.md stands: a PR title is a release-note bullet, so documentation PRs carry the `docs:` prefix and say what a reader can now find.

## 12. Order of work

Each PR is written by one agent and reviewed by a fresh one that follows the pages literally against the code, and for 01 and 02 against radio1. PRs merge on green tests.

1. **Code fixes.** Real `--help`, usage on no arguments, the corrected usage header, the mode doc comment, the link checker in CI. Small, and it unblocks the command-line reference.
2. **Triage.** Create docs/dev and docs/dev/archive, move every file per section 8 with `git mv`, delete the dead and stray files, update every source citation, write dev/README.md and archive/README.md, update CLAUDE.md. Mechanical and reviewable from the file list. No rewriting.
3. **Reference.** The four reference pages from the code, and the coverage tests that pin them.
4. **Guide.** docs/README.md, pages 01 to 13 and the TM8100 page, in one PR. Each page is written by one agent and reviewed by a fresh one against the code and the reference; the reviewer of 01 and 02 follows them on radio1; an editor then reads the whole guide for consistency of voice and terms. One PR rather than three because the pages link to each other and the link check has to pass on every merge.
5. **Front door.** README, the INSTALL pointer, the slimmed example config, the release-notes.py link, the code strings that name CONFIG.md and INSTALL.md, and the deletion of CONFIG.md.
6. **Developer docs pass.** Status lines and correctness on everything kept in docs/dev, the roadmap merge, the PROVENANCE review.

The reference comes before the guide because the guide links into it. The README comes last because it links to everything.

## 13. Decisions, as taken by Tom on 2026-09-10

1. **No-argument behaviour.** `pdn-soundmodem` alone prints usage and exits, the same as `--help`. Approved.
2. **INSTALL.md and CONFIG.md.** Both are deleted once their content has moved into the guide and the reference. No pointer file is left behind; the release-notes link moves to docs/01-install.md. Approved with that change.
3. **plan.md.** The amendment log is closed and archived; the decisions and phases stay as a short record in docs/dev; roadmap.md is the one place open work is tracked; the CLAUDE.md rule changes to match. Approved.
4. **Archive** stays in this repository under docs/dev/archive. Approved.
5. **Deletions** as listed in section 8. Approved.
6. **Folder READMEs** in samples/, tools/ and web/ stay beside what they describe. Approved.
7. **The example config** becomes a minimal working file with almost no comments. Tom does not want masses of JSON comments in a sample config. Approved.
