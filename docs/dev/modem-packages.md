# Modem packages

Status: current as of 2026-10-23. Describes how a modem moves out of this repository into its own NuGet package, what goes and what stays, and which modems have moved. MS110D was the first done to this recipe.

Each waveform can live in its own `M0LTE.<Name>` package and repository, so other programs can use it without taking all of pdn-soundmodem. pdn-soundmodem then keeps a small wrapper that makes the waveform one of its modems.

## What goes and what stays

The package gets the waveform: the modulator, the demodulator, their tables and FEC, and the tests that drive those directly. Its namespace is `M0LTE.<Name>`. It depends only on shared packages such as M0LTE.Dsp, M0LTE.Fec and M0LTE.Ofdm, never on pdn-soundmodem.

pdn-soundmodem keeps the `IModem` wrapper, `src/Packet.SoundModem/Modems/<Name>Modem.cs`. The wrapper owns everything that is this program's choice rather than the waveform's: IL2P framing, conversion to the 48 kHz DSP rate, channel-busy detection, the KISS hardware-control command, packing frames into a burst and the per-frame quality report. The catalogue row stays too, along with the tests that go through the wrapper or the catalogue.

There is deliberately no shared package for the `IModem` contract. Putting one there would drag `FrameQuality`, the IL2P receiver and the busy detector after it, and every change to the contract would become a release of several repositories in order. Revisit this if another program needs a whole modem rather than a waveform.

## The recipe

1. Create `M0LTE/M0LTE.<Name>` by copying the skeleton of [M0LTE.Ms110d](https://github.com/M0LTE/M0LTE.Ms110d): `Directory.Build.props`, `Directory.Packages.props`, `tests/Directory.Build.props`, `PublicApiTests.cs`, `docs/versioning.md`, the `ci` and `publish` workflows, `LICENSE`, `global.json` and `.gitignore`. Write a README and a PROVENANCE.md that names the pdn-soundmodem commit the code left from.
2. Move the waveform and its tests. Rename the namespace. Fix any doc `cref` that points back into pdn-soundmodem.
3. Measure through the shared channel models, never a copy: [M0LTE.HfChannel](https://github.com/M0LTE/M0LTE.HfChannel) for HF, [M0LTE.FmChannel](https://github.com/M0LTE/M0LTE.FmChannel) for FM.
4. Anything pdn-soundmodem, its tools or its tests reached through `InternalsVisibleTo` either becomes public in the package, and so part of its locked API, or the test using it moves to the package. A package never grants `InternalsVisibleTo` back to this repository.
5. Run the package's tests, approve the first `PublicApi.approved.txt`, and check nothing that used to run is now silently skipped.
6. Here: pin the version in [Directory.Packages.props](../../Directory.Packages.props), reference it from `Packet.SoundModem.csproj` with a `<Name>SourcePath` switch like the others, move the wrapper to `Modems/`, delete what moved, and update the PROVENANCE.md row and both licence lists.
7. Release in order. Tom sets up the nuget.org trusted-publishing policy for the new repository's `publish.yml`, the package is tagged and published, and only then can this repository's pull request pass CI.

Downstream programs that used the old `Packet.SoundModem.<Name>` namespace (pdn-mailcast uses MS110D) need `using M0LTE.<Name>;` when they next move their pdn-soundmodem pin, and `using Packet.SoundModem.Modems;` for the wrapper.

## Where each modem is

| Modem | Package | Wrapper here |
|---|---|---|
| MIL-STD-188-110D | M0LTE.Ms110d | `Modems/Ms110dModem.cs` |
| FreeDV datac OFDM | M0LTE.Ofdm | `Modems/FreeDvDatacModem.cs` |
| ARDOP | M0LTE.Ardop | the daemon's `ArdopChannelBridge.cs` |
| POCSAG | M0LTE.Pocsag | `Pocsag/` (the paging server) |
| AFSK 300 and 1200 | not yet | |
| FSK 4800 and 9600 | not yet | |
| C4FSK | not yet | |
| BPSK and QPSK | not yet | |
| OFDM-FM | not yet | |
