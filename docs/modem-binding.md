# Runtime modem binding

How `pdn-soundmodem` loads a modem it does not contain, and why it is built this way.

Opened 2026-08-08. Status: **design, not built.** The trigger is OFDM-AB (see
[ofdm-ab/plan.md](ofdm-ab/plan.md)): an implementation of it has to live outside this repository
while its licence position is unresolved, and the station still has to be able to run it.

## The problem, stated exactly

This repository is GPL-3.0-or-later and intends to stay that way. A modem whose provenance is
unsettled cannot be a source file here, cannot be a `PackageReference` here, and cannot be
mentioned in this repository's build graph at all - because a GPL work that requires a
non-distributable component in order to build is a work nobody else can build.

At the same time the deployment reality is a single daemon on a station, and an operator wants
`mode = ofdm-ab-nb` in a config file to work exactly as `mode = fsk9600` does.

So: **the modem must be discovered and loaded at run time, from an assembly this repository has
never heard of, through a contract this repository defines.** No compile-time reference in either
direction, no build-time dependency, and nothing in the shipped package that stops working when
the external modem is absent.

## Shape

Four pieces, in dependency order.

### 1. The contract lives in this repository, and only the contract

`IModem` already exists and is already the seam every mode is driven through - `ModemCatalog`
builds one, the daemon streams audio into it, KISS carries frames out. A plugin therefore needs to
supply exactly two things: an `IModem` implementation, and a way to name and construct it.

```
public interface IModemPlugin            // in Packet.SoundModem, public API
{
    string Id { get; }                   // "ofdm-ab", the family this plugin provides
    IReadOnlyList<ModemDescriptor> Modes { get; }   // what it can build, for catalogue listing
    IModem Create(string mode, int sampleRate, Action<byte[]> frameReceived, ModemOptions options);
}
```

`ModemDescriptor` is the catalogue metadata a mode already carries (DSP rate, centre semantics,
whether it runs IL2P+CRC, and so on), which is what lets a plugin mode appear in `--list-modes`,
in the mode recogniser and in the waterfall exactly like a built-in one.

Nothing else crosses the boundary. In particular the plugin does not see the daemon, the KISS
server or the config; it sees audio in and frames out, which is the whole point of `IModem`.

### 2. Discovery is explicit, never ambient

A plugin is loaded because the operator asked for it, from a path the operator wrote down:

```yaml
modemPlugins:
  - path: /opt/pdn/plugins/M0LTE.OfdmAb.dll
```

No scanning of a plugins directory, no probing next to the executable, no environment variable.
Ambient discovery means a file appearing on disk changes what a station transmits, and that is not
a property this daemon should have. An explicitly listed path also makes the audit trivial: the
config says what non-repository code is loaded, and the startup log repeats it.

### 3. Isolation is an `AssemblyLoadContext`

Each plugin loads into its own collectible `AssemblyLoadContext` with an
`AssemblyDependencyResolver` rooted at the plugin's own directory, so the plugin brings its own
dependencies without imposing them on the host.

One rule makes this work rather than fight: **the contract assembly is shared, everything else is
private.** The load context resolves `Packet.SoundModem` (and its own framework references) from
the host so that the `IModem` the plugin returns is the same type the host expects, and resolves
everything else from the plugin's own folder. Get this wrong and you get the classic
"`IModem` cannot be converted to `IModem`" failure, which is worth naming here because it costs an
afternoon every time.

### 4. Registration goes through the catalogue, with a namespace

`ModemCatalog` grows a registration entry point that plugin modes arrive through, and plugin modes
are **prefixed by their plugin id** (`ofdm-ab:nb-qam64`). Three reasons: a plugin can never shadow
or redefine a built-in mode; a station log or a mode-validation entry always says plainly which
modes were not built here; and an unloaded plugin gives a clean "unknown mode `ofdm-ab:nb-qam64`,
no plugin registered for `ofdm-ab`" rather than a mysterious absence.

## What this deliberately does not do

- **No plugin transmit-path privileges.** A plugin gets the same `IModem` surface as a built-in
  mode and no more. It cannot key a radio, open a port, or reach the config.
- **No versioned plugin ABI, yet.** The contract is a normal .NET interface and a plugin built
  against a different `Packet.SoundModem` version may simply fail to load. The daemon should say
  so clearly and carry on without that mode, rather than pretend. A real ABI version handshake can
  come when there is more than one plugin in the world.
- **No dynamic reload.** Plugins load at startup. Collectible contexts leave the door open, but
  swapping a modem under a running station is a feature nobody has asked for and it would need a
  story for in-flight audio.
- **Nothing about licences.** The mechanism is licence-neutral: it is equally the answer for a
  proprietary vendor modem, an experimental mode someone does not want to publish yet, and our
  OFDM-AB work. What it guarantees is only that *this* repository stays buildable and
  distributable by anyone, on its own.

## Testing

The mechanism needs a plugin to test against, and that plugin should live here: a tiny in-repo
sample plugin (a passthrough or a deliberately trivial modem) built as a separate assembly that is
**not** referenced by the library, loaded from its build output by the tests. That gives a real
`AssemblyLoadContext` exercise in CI without any external dependency, and it pins the failure
modes that matter: contract type identity across the boundary, a missing file, a plugin that
throws in `Create`, a duplicate id, and an unknown mode for a registered plugin.

The sim harness gets it for free: once a plugin mode is in the catalogue, `SimModem` drives it,
which means a plugin modem can have a Watterson or FM ladder and mask rows measured by the same
instruments as everything else. That matters more than it sounds - it means an out-of-tree modem
is held to the same measured standard as an in-tree one.

## Order of work

1. `IModemPlugin` + `ModemDescriptor` in `Packet.SoundModem`, plus catalogue registration and the
   `plugin:mode` naming.
2. The loader: `AssemblyLoadContext` with shared-contract resolution, explicit paths from config,
   and honest startup logging of what loaded and what did not.
3. The in-repo sample plugin and its tests.
4. Daemon config plumbing (`modemPlugins`) and documentation in CONFIG.md.

None of it is blocked on OFDM-AB, and all of it is useful the moment any modem wants to live
outside this repository.
