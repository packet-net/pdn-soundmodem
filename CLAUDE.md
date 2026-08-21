# CLAUDE.md

Operating notes for Claude Code (and other agents) working in `packet-net/pdn-soundmodem`.

## What this repo is

A headless (no GUI) soundcard packet modem in C#/.NET 10, serving both the PDN node
(in-process transport with native DCD) and standalone use (KISS-TCP daemon). Read
[docs/plan.md](docs/plan.md) for the phase plan and current status, and the founding research at
`packet.net/docs/research/headless-soundmodem.md` for the full design rationale.

## Licence rules (hard)

- This repo is **GPL-3.0-or-later** and must stay that way: it contains/will contain work
  derived from QtSoundModem (GPLv3+) and Dire Wolf (GPL-2.0-or-later). Never relicense,
  never copy code from here into an MIT-licensed package, and never let an MIT-licensed
  package depend on `pdn-soundmodem`. The AGPL-3.0 packet.net node may depend on it (§13).
- **Provenance discipline**: any algorithm ported from QtSoundModem or Dire Wolf gets a
  comment naming the source file/function. FEC/protocol layers are implemented from the
  published specs (IL2P v0.6, FX.25) with the spec's test vectors; reference C sources are
  used to pin constants the spec leaves in figures - say so in comments when you do.
- New dependencies must be GPL-compatible (MIT/Apache-2.0/BSD/LGPL are fine). AGPL-3.0 is
  also permitted (GPLv3 §13 allows the combination), but pulls AGPL §13's network-source
  requirement onto the combined work - so weigh it before adding one. The `M0LTE.Flex`
  package (the extracted FlexRadio client) is one such AGPL-3.0 dependency, Tom-approved.

## Interop ground truth

The live network this modem must serve is NinoTNC IL2P+CRC (300 BPSK / 2400 QPSK /
3600 QPSK / 9600 GFSK) - **spec + NinoTNC behaviour is ground truth**, QtSoundModem is a
cross-check. Known wire nuance: the spec v0.6 example packets leave the RESERVED (ex-FEC)
header bit clear; Dire Wolf sets it. We encode it clear and ignore it on receive.

## Mode validation ledger (keep it current)

[docs/mode-validation.md](docs/mode-validation.md) is the living record of every mode/submode's
validation status (simulation + on-air) and its provenance. **Standing rule: whenever you prove a
modem/mode works - especially one that was *not* working before - add a dated entry to the ledger**
naming the mode, the broken→working transition, and the commit/PR/issue that did it, and update its
row in the matrix. A fix isn't finished until the ledger records it.

## Conventions (mirror packet.net)

- net10.0, C# latest, nullable + warnings-as-errors, Central Package Management
  (`Directory.Packages.props` - no `Version=` on `PackageReference`).
- Tests: xunit + AwesomeAssertions (never FluentAssertions), test names
  `Snake_Case_Like_Sentences`, one test project per library. Wall-clock via `TimeProvider`
  only - never `DateTime.Now`/`Stopwatch` in library code (inject `TimeProvider`).
- DSP hot paths: zero steady-state allocation (preallocated buffers, `Span<T>`,
  `ArrayPool`), no LINQ in per-sample/per-block code.
- **No em dashes or en dashes, anywhere.** Not in code, comments, docs, commit messages or
  PR bodies. Use a hyphen, a comma, a semicolon or a full stop. This is Tom's house style and
  it is not negotiable: he has never typed one. Every one that was in this repo got there from
  an agent, starting with the scaffold commit. Frozen evidence bundles
  (`docs/ms110d/evidence/`) and verbatim transcriptions (`docs/refs/`) are records, so they
  keep whatever they were written with.
- Printable strings are ASCII: `->` not an arrow, `,` not a middle dot. `journalctl`'s pager
  under a C locale renders anything above 0x7F as `<E2><80><94>`, and a station's console is
  not ours to configure. Maths notation (pi, sigma, plus-minus, section marks) is fine in
  comments, which are never printed. `SourceTextTests` enforces both rules.
- CI: every workflow job MUST target `[self-hosted, Linux, X64]` - no GitHub-hosted
  runners (no minutes budget). Same rule as packet.net.
- PRs merge on locally-run green tests (`dotnet test`); fix forward.
- **Cross-repo iteration**: the co-developed packages swap to local checkouts with
  `-p:FecSourcePath=... -p:Il2pSourcePath=... -p:FlexSourcePath=...` (see
  `Packet.SoundModem.csproj`) - no pack/publish round trip per change. Unset, CI and
  everyone else consume the published packages; version pins in
  `Directory.Packages.props` only move when a package actually ships.
- **Watterson masks** (`WattersonMaskTests`, rx-roadmap workstream 0): each audio mode's
  measured performance floor is pinned - the smoke tier blocks CI, `SM_MASK_GATE=1` runs the
  full ladder. A PR touching a modem's receive path runs the affected mode's full ladder A/B
  and quotes it; a mask moves only with a mode-validation.md entry, in either direction.

## What lives where

```
src/Packet.SoundModem/       the core library (NuGet: pdn-soundmodem)
  Fec/                       CRC-16/X-25, Hamming(7,4), Reed-Solomon GF(2^8)
  Il2p/                      IL2P frame codec (spec draft v0.6, incl. IL2P+CRC)
tests/Packet.SoundModem.Tests/
tools/Packet.SoundModem.Decode/       sm-decode: one file, one mode you already know
tools/Packet.SoundModem.MultiDecode/  pdn-decode: sweep every mode over a file nobody labelled
docs/plan.md                 phase plan + status - keep it current as you work
docs/pdn-decode.md           the sweep tool, and why its default set is the whole catalogue
```

The architecture/design rationale lives in the founding research doc in packet.net
(`docs/research/headless-soundmodem.md`) - this repo's plan.md §Decisions is the binding summary.

## Releases and release notes

A release is a `v*` tag on `main`; `release.yml` tests, builds the NuGet package and the `.deb`s, and writes the GitHub Release notes with `scripts/release-notes.py` - one bullet per merged PR (or direct commit) since the previous tag, grouped by the conventional prefix, nothing else. So **a PR title is a release-note bullet**: write it as the plain, one-line, user-facing statement of what changed (what a station operator or a library consumer would want to read), with a `feat:`/`fix:`/`docs:`/`test:`/`chore:` prefix so it lands in the right section. The detail belongs in the PR body. Never hand-write release notes or re-add install text to them; INSTALL.md is linked from every release.
