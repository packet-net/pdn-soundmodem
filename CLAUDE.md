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
  used to pin constants the spec leaves in figures — say so in comments when you do.
- New dependencies must be GPL-compatible (MIT/Apache-2.0/BSD/LGPL are fine).

## Interop ground truth

The live network this modem must serve is NinoTNC IL2P+CRC (300 BPSK / 2400 QPSK /
3600 QPSK / 9600 GFSK) — **spec + NinoTNC behaviour is ground truth**, QtSoundModem is a
cross-check. Known wire nuance: the spec v0.6 example packets leave the RESERVED (ex-FEC)
header bit clear; Dire Wolf sets it. We encode it clear and ignore it on receive.

## Conventions (mirror packet.net)

- net10.0, C# latest, nullable + warnings-as-errors, Central Package Management
  (`Directory.Packages.props` — no `Version=` on `PackageReference`).
- Tests: xunit + AwesomeAssertions (never FluentAssertions), test names
  `Snake_Case_Like_Sentences`, one test project per library. Wall-clock via `TimeProvider`
  only — never `DateTime.Now`/`Stopwatch` in library code (inject `TimeProvider`).
- DSP hot paths: zero steady-state allocation (preallocated buffers, `Span<T>`,
  `ArrayPool`), no LINQ in per-sample/per-block code.
- CI: every workflow job MUST target `[self-hosted, Linux, X64]` — no GitHub-hosted
  runners (no minutes budget). Same rule as packet.net.
- PRs merge on locally-run green tests (`dotnet test`); fix forward.

## What lives where

```
src/Packet.SoundModem/       the core library (NuGet: pdn-soundmodem)
  Fec/                       CRC-16/X-25, Hamming(7,4), Reed-Solomon GF(2^8)
  Il2p/                      IL2P frame codec (spec draft v0.6, incl. IL2P+CRC)
tests/Packet.SoundModem.Tests/
docs/plan.md                 phase plan + status — keep it current as you work
```

The architecture/design rationale lives in the founding research doc in packet.net
(`docs/research/headless-soundmodem.md`) — this repo's plan.md §Decisions is the binding summary.
