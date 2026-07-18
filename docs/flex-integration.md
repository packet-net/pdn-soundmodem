# FlexRadio 6000-series integration (DAX audio + API PTT)

**Goal.** Let a pdn-soundmodem daemon use a FlexRadio 6000-series SDR as its "sound card + PTT"
over the LAN — `--device flex:<radio>[:slice]` — the same way it uses an ALSA card today, so
the HF waveforms (`freedv-datac*`, and ARDOP) and the packet modes can run through a Flex with no
external sound-card wiring. Tom has a **Flex 6500** to test against.

**Status:** research + design only. Nothing implemented. Written 2026-07-17 against pdn-soundmodem
`main` @ `4c6eab8`. Every wire-protocol constant below is transcribed from either the official
FlexRadio API wiki (git-cloned, cited per line) or the MIT-licensed Go reference clients
(`flexclient` / `flexlib-go`, read in full), not from search summaries. Radio-*behaviour* facts
(latency, self-monitor) are from the FlexRadio community forum and are marked `[unverified]` where
I could not ground them in a primary spec — treat those as "check on Tom's radio", not settled.

## Provenance / sources

Primary (authoritative — read in full or line-by-line):

- **Official protocol wiki**, cloned as a git repo (the rendered wiki pages fail to load in a
  fetcher; the backing repo is the reliable source):
  `git clone https://github.com/flexradio/smartsdr-api-docs.wiki.git` — pages cited below as
  *wiki:PageName* (e.g. *wiki:TCPIP-dax*, *wiki:Discovery-protocol*, *wiki:TCPIP-stream*,
  *wiki:TCPIP-xmit*, *wiki:SmartSDR-TCPIP-API*, *wiki:TCPIP-keepalive*).
- **`kc2g-flex-tools/flexclient`** (Go, MIT) — <https://github.com/kc2g-flex-tools/flexclient> —
  the TCP session + discovery + VITA plumbing. Files read: `client.go`, `discovery.go`,
  `discovery_unix.go`, `vita.go`, `vita_pcm.go`, `setters.go`.
- **`kc2g-flex-tools/nDAX`** (Go, MIT) — <https://github.com/kc2g-flex-tools/nDAX> — the DAX
  RX/TX audio path. File read: `main.go` (the DAX enable sequence + the exact TX VITA-49 packet
  bytes), `README.md`.
- **`kc2g-flex-tools/nCAT`** (Go, MIT) — <https://github.com/kc2g-flex-tools/nCAT> — CAT + PTT.
  File read: `ptt.go` (the `xmit`/`slice set tx=1` sequence).
- **`hb9fxq/flexlib-go`** (Go, MIT) — <https://github.com/hb9fxq/flexlib-go> — the VITA-49
  class-code table (`vita/vitatypes.go`) that flexclient depends on.

Secondary (community forum — behaviour facts, `[unverified]` unless corroborated):

- DAX audio channel counts / native rate: FlexRadio Community "audio stream specifications for
  DAX" <https://community.flexradio.com/discussion/6346756/> and K9XN's 6500 writeup
  <https://k9xn.org/2014/08/28/flex-radio-systems-flex-6500/>.
- Panadapter-during-transmit / self-monitor: FlexRadio Community "Panadapter Signal During
  Transmit" <https://community.flexradio.com/discussion/5901572/>.
- DAX latency ("+0.3 DT" for FT8): FlexRadio Community "Question on Audio Delay when using DAX"
  <https://community.flexradio.com/discussion/8025327/>.
- FlexLib licensing: <https://www.flexradio.com/software/smartsdr-v4-x-api-flexlib/>.

## TL;DR

- **The project Tom half-remembers is `kc2g-flex-tools` — `nDAX` (DAX audio ⇄ PulseAudio) + `nCAT`
  (CAT/PTT via a hamlib-rigctld shim), both Go, Linux, MIT-licensed, built on the `flexclient`
  library.** It is exactly a "expose the Flex like a local sound card" tool. It is a superb
  *reference*, and a viable *quick smoke-test bridge*, but not what we want to *depend on* at
  runtime (it drags in PulseAudio + snd-aloop + two external daemons).
- **Recommended: a pure-managed C# `FlexRadio/` client inside `Packet.SoundModem`** — discovery +
  the TCP command/status session + VITA-49 DAX RX/TX surfaced as our existing audio interfaces,
  plus `FlexPtt : IPttControl` sending `xmit 1/0`. Selected `--device flex:<radio>[:slice]`. The
  MIT Go refs give us every constant needed; a from-scratch client is legally clean and GPL-safe.
- **Licence verdicts:** FlexLib (Flex's own .NET lib) and its port FlexLib_Core are **proprietary
  → avoid** (GPL-incompatible). `flexclient`/`flexlib-go`/`nDAX`/`nCAT` are **MIT → free to read
  and port** with attribution. The **wire protocol itself is publicly documented** by FlexRadio.
- **Radio facts:** 6500 = 4 slices, **4 DAX audio channels**, native DAX audio **24 kSPS**;
  a full-rate **48 kHz float32** DAX mode also exists. Both bridge to our 12/48 kHz DSP with
  integer ratios. **The OBW self-capture idea does *not* hold** on a 6500 with the public API —
  see § Self-monitor.
- **What needs Tom's radio vs not:** the whole protocol layer (discovery, session, VITA parse/
  build, PTT) is unit-testable offline, and a small **mock radio** lets the *entire daemon* run
  `--device flex:` end-to-end with no hardware. Only discovery/stream/PTT smoke, the HF-loop, and
  the latency/`txdelay` floor genuinely need the 6500.

---

## 1. "The project out there" and the landscape

`kc2g-flex-tools` (author Andrew Rodland, KC2G) is a set of Linux-native Go tools that make a Flex
usable without SmartSDR-for-Windows or a Maestro:

| Project | What it does | Lang | Licence | State | Gives us |
|---|---|---|---|---|---|
| **flexclient** | The library: discovery, TCP command/status session, VITA-49 RX, DAX/slice setters. All the others sit on it. | Go | MIT | Active | The exact session + discovery + VITA-49 constants (we read every relevant file). |
| **nDAX** | Creates a PulseAudio source/sink for a slice; bridges DAX audio RX/TX. Supports reduced-bw (24 kHz s16) and high-bw (48 kHz float32). | Go | MIT | Active | The exact DAX enable sequence and byte-exact TX packet layout. |
| **nCAT** | CAT + PTT exposed as a hamlib/rigctld network server (rig model 2). | Go | MIT | Active | The exact PTT sequence (`slice set tx=1` → `xmit 1/0`) and interlock read. |
| **flexlib-go** (hb9fxq) | Standalone VITA-49 parser/type library; flexclient depends on it. | Go | MIT | Maintained | The canonical VITA-49 class-code table. |
| **FlexLib** (FlexRadio) | Flex's *own* .NET/C# API library — the reference implementation of the protocol. | C#/.NET | **Proprietary** (redistributable with apps; commercial licence required; source is "available to assist understanding") | Vendor-maintained | Reference only — cannot be a dependency of a GPL work. |
| **FlexLib_Core** (brianbruff) | Cross-platform .NET 9 port of FlexLib, Windows deps stripped. | C#/.NET | Inherits FlexLib's proprietary terms | Community | Tempting (it's C#!) but the licence is the blocker, not the port. Avoid. |
| **xLib6000 / xLib6001** (K3TZR) | Full Flex 6000 API implementation — but **Swift/macOS**, not .NET. | Swift | MPL/open | Maintained | Another clean-room reference for protocol details; wrong language to depend on. |
| **AetherSDR** | Newer open-source Linux GUI client for Flex radios. | (mixed) | Open | New/early | Landscape only. |
| **Hamlib** | Generic rig control. Its Flex 6000 support is thin; nCAT exists precisely because people front Hamlib apps with a rigctld shim rather than use Hamlib's native Flex backend. | C | LGPL | Maintained | Not a route for audio; marginal for CAT. |

**Verdict on "the project":** `nDAX`+`nCAT` is real, current, and exactly the "expose the Flex as a
sound card" idea. We should mine `flexclient`/`nDAX`/`nCAT` for constants (done — see §2) and keep
the *tools themselves* as an optional zero-code smoke-test bridge (§6, Route B), but build our own
managed client for the product path.

---

## 2. The Flex 6000 API surface (grounded, with exact constants)

Two transports, both discovered from the same broadcast:

### 2.1 Discovery — UDP :4992 broadcast

The radio broadcasts a VITA-49 "extension data with stream ID" packet once per second to UDP port
**4992** (*wiki:Discovery-protocol*; flexclient `discovery_unix.go` binds `:4992` with
`SO_REUSEPORT`). Identifying fields (*wiki:Discovery-protocol*, cross-checked with flexclient
`discovery.go`):

- Stream ID `0x800`, class ID high `0x00001C2D` (OUI `1C2D`), class ID low `0x534CFFFF`
  (`"SL"` = `0x534C`, packet class `0xFFFF`). flexclient matches on `OUI == 0x001c2d &&
  PacketClassCode == 0xffff`.
- Payload is an ASCII key/value string:
  `model=%s serial=%s version=%s name=%s callsign=%s ip=%u.%u.%u.%u port=%u`
  — spaces in `name` are sent as underscores (convert back for display).

So discovery = "listen on UDP 4992, parse the VITA-49 payload's `key=value` pairs, match on
`serial`/`model`/`ip`." Also support an explicit IP to skip discovery (broadcast won't cross
subnets).

### 2.2 Command / status — TCP :4992

ASCII line protocol (*wiki:SmartSDR-TCPIP-API*; flexclient `client.go`):

- **Prologue on connect:** `V<major.minor.a.b>` (version), then `H<32-bit-hex>` (this client's
  *handle*).
- **Command (client→radio):** `C[D]<seq>|<command>\n` — the optional `D` requests verbose debug;
  terminator may be CR, LF, or CRLF. flexclient sends `C<seq>|<cmd>\n`.
- **Command result (radio→client):** `R<seq>|<hex_error>|<message>` — `0` error = OK; non-zero is
  a documented failure code (*wiki:Known-API-Responses*).
- **Status (radio→client):** `S<handle>|<object> key=value key=value …` — asynchronous object
  updates; a client subscribes with `sub <topic> all` or auto-subscribes by controlling an object.
  flexclient's `parseGenericState` builds the `object → {key:value}` map we'd mirror.
- **Message (radio→client):** `M<hexnum>|<text>` (informational/warnings/faults).
- **Keepalive (optional):** `keepalive enable` then a `ping` once per second; **15 s** of silence
  disconnects (*wiki:TCPIP-keepalive*). flexclient/nCAT don't use it (they rely on the TCP socket);
  we'd enable it for a long-running headless node so a wedged radio is detected.

### 2.3 Streaming — VITA-49 over UDP

The radio streams VITA-49 to the client on a UDP port the client nominates: after connecting, the
client opens a UDP socket and sends `client udpport <port>` (flexclient `InitUDP`); the radio's own
VITA source port is **4991**. Stream type is identified by the VITA **packet class code**
(flexlib-go `vita/vitatypes.go`):

| Class | Code | Meaning |
|---|---|---|
| `SL_VITA_DISCOVERY_CLASS` | `0xFFFF` | discovery |
| `SL_VITA_METER_CLASS` | `0x8002` | meter readings |
| `SL_VITA_FFT_CLASS` | `0x8003` | panadapter FFT |
| `SL_VITA_WATERFALL_CLASS` | `0x8004` | waterfall |
| `SL_VITA_OPUS_CLASS` | `0x8005` | Opus-compressed remote audio |
| `SL_VITA_IF_NARROW_CLASS` | `0x03E3` | **DAX audio (full-rate float32) / remote_audio** |
| `SL_VITA_IF_WIDE_CLASS_24/48/96/192kHz` | `0x02E3/E4/E5/E6` | DAX **IQ** streams |

OUI is `0x001C2D`; `MAX_VITA_PACKET_SIZE = 16384`. We only need the **DAX audio** class for the
modem path (IQ is for a future panadapter/OBW experiment, §5).

### 2.4 DAX audio — the actual audio pipe

**Enable sequence** (nDAX `enableDax`, cross-checked with *wiki:TCPIP-dax* and *wiki:TCPIP-stream*):

1. Bind to the client: find our `client` object by station name (`sub client all`), then
   `client bind client_id=<uuid>`.
2. `client udpport <port>` — register where the radio sends VITA (§2.3).
3. *(reduced-bw only)* `client set send_reduced_bw_dax=true`.
4. `slice set <idx> dax=<ch>` — associate DAX channel with the slice.
5. `dax audio set <ch> slice=<idx> [tx=1]` — bind *this client* as the DAX RX (and, with `tx=1`,
   the TX source) for the channel. Multiple clients can share a DAX channel, so the radio uses the
   sender's client ID to decide whose TX samples to use (*wiki:TCPIP-dax*).
6. `stream create type=dax_rx dax_channel=<ch>` → returns the RX stream ID (hex).
7. `audio stream 0x<rxid> slice <idx> gain <0-100>` — set RX gain.
8. *(TX)* `stream create type=dax_tx` → returns the TX stream ID.

**Audio format** (nDAX `main`, the two branches — this is the load-bearing detail):

| Mode | Rate | Sample | Samples/pkt | 64-bit streamClass | LAN rate (mono) |
|---|---|---|---|---|---|
| Reduced-bw (native, default) | **24000 Hz** | **s16 big-endian** | 128 | `0x00001C2D534C0123` | ~48 KB/s |
| Full-bw ("high-bw") | **48000 Hz** | **float32 big-endian** | 256 | `0x00001C2D534C03E3` | ~192 KB/s |

DAX audio RX arrives as VITA payload = interleaved samples (flexclient treats `remote_audio_rx` as
dual-mono big-endian float32 and delivers the left channel; DAX audio is likewise mono-per-channel
into a slice). We convert payload → `float[]` at the DAX rate.

**DAX audio TX packet** — nDAX `streamFromPulse` builds it byte-for-byte; a 28-byte VITA-49 header
then the samples:

```
byte  0      : 0x18                      // pkt type = IFDataWithStream, C (class-id present)
byte  1      : 0xD0 | (seq & 0x0F)       // TSI=Other, TSF=SampleCount, 4-bit packet count
u16be        : (payloadBytes/4) + 7      // total length in 32-bit words (7 header words)
u32be        : <TX stream id>            // from step 8
u64be        : <streamClass>             // 0x…0123 (s16/24k) or 0x…03E3 (f32/48k)
u32be        : 0                          // timestamp integer  (unused)
u64be        : 0                          // timestamp fractional (unused)
<payload>    : <samplesPerPacket × bytesPerSample> big-endian samples
```

Sent via the same UDP socket (`SendUdp`). nDAX paces packets with a 1 ms sleep and skips
all-zero packets; we pace off the sample clock (the transmitter is already device-paced — §4).

### 2.5 PTT / TX control

There is **no serial/GPIO PTT** — keying is a command (nCAT `ptt.go`, *wiki:TCPIP-xmit*):

- Ensure the slice is the TX slice: `slice set <idx> tx=1` (once, when we take the channel).
- Key: `xmit 1`. Unkey: `xmit 0`.
- TX state is observable on the **`interlock`** status object: `interlock state=TRANSMITTING`
  means on air. The interlock walks `RECEIVE → READY → PTT_REQUESTED → TRANSMITTING →
  UNKEY_REQUESTED → …` (*wiki:TCPIP-xmit*, *wiki:Interlock-State-Transition-Diagram*). The
  `PTT_REQUESTED → TRANSMITTING` settle is the Flex analogue of a hardware transmitter's
  PTT-to-RF delay (§4).
- Slice control we may or may not want to drive: `slice t <idx> <freq.6f>` (tune),
  `slice set <idx> mode=DIGU|DIGL|USB|…`, `filt <idx> <lo> <hi>` (passband) — flexclient
  `setters.go`. See open question 6 (attach-only vs full slice control).

---

## 3. Radio-side facts for the HF loop

- **DAX audio channels on a 6500: 4** (the 6700 has 8); **4 slice receivers**; **4 DAX-IQ streams**
  up to 192 kSPS. Native DAX audio I/O rate **24 kSPS**. Source: FlexRadio Community DAX spec
  thread + K9XN 6500 writeup. `[partially unverified]` — cross-checked against nDAX's 24 kHz
  default, consistent.
- **Sample-rate/format for our modems:** both DAX modes bridge cleanly (§4). We never need the
  ALSA plug-layer resampler.
- **TX audio injection** is exactly the DAX TX stream (§2.4) — no mic path, fixed levels, the
  data-port-equivalent we want. RX gain via `audio stream … gain`, AGC-T via CAT if needed.
- **Latency** `[unverified for our path]`: FT8 users report about **+0.3 s DT** through DAX, well
  inside FT8's 2.5 s budget. That figure bundles WSJT-X's own buffering. Our path (§4) can run a
  much tighter jitter buffer. The number that matters for **ARDOP's ~1.5–2.1 s ACK windows** is
  round-trip: radio DSP one-way (tens of ms) + our RX jitter buffer + the `xmit`→`TRANSMITTING`
  settle. **Must be measured on the 6500** — it's the Flex equivalent of the PTT-to-RF settle that
  `--txdelay` already budgets for.
- **Self-monitor / OBW — the important correction.** The premise "the radio can capture our own
  transmission (an OBW-measurement win)" **does not hold with the public API on a 6500.** Two
  findings:
  - The panadapter *does* show a transmit trace via **receiver leakage**, but FlexRadio state this
    is **not an accurate on-air representation** — internal coupling produces spurs/anomalies "not
    actually on-air." The clean **−80 dBc pre-distortion tap exists but is not exposed** through the
    API. So a DAX-IQ self-capture during TX is **not a trustworthy OBW measurement**.
    (Community thread 5901572.)
  - `MON` (transmit monitor) feeds the *transmitted audio* back — for a data signal that's just the
    audio we sent, post-TX-DSP. Useful as a **plumbing sanity check** ("did the radio get our DAX
    TX audio?"), **not** an off-air recapture.
  - **Conclusion:** keep the existing OBW discipline (bench RF loop, or a *second* receiver off-air
    — `docs/freedv-hf-loop.md`). Treat any Flex self-view as indicative only. If we ever want a
    real Flex-side OBW check it needs a **second slice/DAX-IQ on a separate SCU** looking at the
    antenna during TX, and even then the accuracy caveat applies — a research spike, not a Phase-1
    feature.

---

## 4. Recommended architecture

### The three routes

**Route A — pure-managed C# `FlexRadio/` client (RECOMMENDED).**
A new folder `src/Packet.SoundModem/FlexRadio/` implementing the §2 protocol subset, surfaced
through the interfaces the daemon already speaks:

- `FlexClient` — TCP session (connect, prologue, `SendCommand`/await result, status subscription),
  discovery, and the UDP VITA send/receive loop. ~a direct C# transcription of `flexclient`.
- `FlexAudioInput : IAudioInput` *(new interface — see § the one gap)* — depacketizes DAX-RX VITA
  into `float[]` at the DAX rate, with a small reorder/jitter buffer (mirror nDAX's ring of ~3
  packets; deepen on a loaded box, exactly as the daemon already deepens the ALSA capture buffer
  for ARDOP).
- `FlexAudioOutput : IAudioOutput` — takes DSP-rate `float` samples, converts to the DAX format,
  builds the §2.4 TX packet, sends over UDP. `Drain()` = flush the last partial packet and wait
  out its airtime.
- `FlexPtt : IPttControl` — `Key()` → `slice set <idx> tx=1` (once) + `xmit 1`; `Unkey()` →
  `xmit 0`; optionally confirm via the `interlock` status.

*Why A:* no external processes, no PulseAudio, no snd-aloop — fits the headless-Pi/.deb deployment
and this box's known ALSA-loopback fragility (`docs/qtsm-loop.md`). It plugs into the **existing**
`IAudioOutput`/`IPttControl` transmitter (`SoundModemChannel.RunTransmitterAsync(IAudioOutput,
IPttControl, …)`), so **all modes — KISS packet, POCSAG paging, and ARDOP — get Flex support for
free** (the ARDOP host reports device names but does not open audio; audio flows through the shared
channel path). We control latency and the sample-rate bridge end-to-end. The MIT Go refs give us
every constant, so it's a clean, GPL-safe port.

**Route B — nDAX + nCAT → PulseAudio → ALSA loopback (good smoke test, not the product).**
Zero new .NET code: run `nDAX` (creates Pulse source/sink) + `nCAT` (rigctld PTT), bridge Pulse to
an ALSA device the daemon opens, PTT via a rigctld client. *Against it:* three extra daemons
(nDAX, nCAT, PulseAudio) + snd-aloop plumbing this box is known to fight (`docs/qtsm-loop.md`);
two extra resample stages and Pulse buffering add latency (worst for ARDOP); PTT via a second
network shim. **Use B once, early, as an independent cross-check** that the daemon can modem through
a Flex at all, and as a latency yardstick — then build A.

**Route C — FlexLib / FlexLib_Core dependency (rejected).**
Proprietary licence, **GPL-incompatible**; historically Windows-lean. FlexLib_Core is a nice port
but inherits Flex's terms. Reject.

### The one gap in our codebase

The transmit side is **already abstracted** (`RunTransmitterAsync` takes `IAudioOutput` +
`IPttControl`; `AlsaAudioOutput`, `UpsamplingAudioOutput`, `SerialPtt`, `Cm108Ptt`, `NullPtt` are
the existing implementations). The **receive side is not**: `Packet.SoundModem.Daemon/Program.cs`
opens capture directly as `AlsaPcm.Open(device, Capture, …)` and calls `capture.Read(short[])` in
the main loop. So the single required refactor is:

- Introduce **`IAudioInput`** — `int SampleRate { get; }` + `int Read(Span<float>)` (or keep
  `short` to match ALSA and convert in the loop; `float` is cleaner for Flex's float32 mode).
- Wrap the current ALSA capture as `AlsaAudioInput : IAudioInput` (thin — it already exists as
  `AlsaPcm`), and add `FlexAudioInput : IAudioInput`.
- Parse `--device`: `flex:<radio>[:slice][@station]` (radio = `discover`, an IP, or a
  `serial=`/`name=` discovery spec, or `mock`; slice defaults to `A`) selects the Flex triplet
  (`FlexAudioInput`/`FlexAudioOutput`/`FlexPtt` all sharing one `FlexClient`); anything else stays
  ALSA. When `--device flex:` is set, `--ptt` is implicitly the Flex (reject a conflicting
  `--ptt serial:/cm108:`), matching how the Flex owns keying.
- **Selection policy (headless vs attach) — implemented, §8.** With no `@station` the daemon
  **owns the radio** and brings it up **headless** (register as a GUI client, create its own
  slice) — the "pdn at the radio, no SmartSDR" deployment, and the **default**. The created
  slice's frequency/antenna/mode come from `--flex-freq`/`--flex-ant`/`--flex-mode` (or a config
  `Flex` section), defaulting to `14.100000` MHz / `ANT1` / `DIGU`. A trailing `@station`
  (`flex:<radio>[:slice]@<station>`) selects **attach** mode: coexist with a running SmartSDR by
  binding that station's existing slice (the slice params are then ignored). `FlexStation` exposes
  the two paths as `SetUpHeadlessAsync` (default) and `SetUpAsync` (attach); `FlexDevice.OpenAsync`
  picks between them from the parsed `@station`.

This is a small, self-contained change that also tidies the capture path (symmetry with the output
interface) independent of Flex.

### Protocol subset to implement (and what to skip)

Implement: discovery (broadcast + explicit IP), TCP session (prologue, command/result, status
subscribe for `client`/`slice`/`interlock`), optional keepalive+ping, `client udpport`, the DAX
enable sequence (§2.4 steps 1–8), DAX-RX depacketize, DAX-TX packetize, `xmit`/`slice set tx=1`.
Skip for now: FFT/waterfall/meter streams, Opus remote audio, DAX-IQ, panadapter creation, ATU/amp
/xvtr, SmartLink/WAN (TLS) — all present in the refs, none needed for the modem path.

### Sample-rate bridging

Pick the DAX rate to be an integer multiple of the modem's DSP rate, mirroring the existing
`--capture-rate must be a multiple of DspRate` rule and reusing `Decimator`/`Upsampler`:

| Modem family | DSP rate | Best DAX mode | RX | TX | LAN |
|---|---|---|---|---|---|
| audio-band (afsk/psk/…) | 12 kHz | reduced-bw **24 kHz s16** | decimate ÷2 | upsample ×2 | light (~48 KB/s) |
| 9600-family + `freedv-*` | 48 kHz | full-bw **48 kHz float32** | none (1:1) | none (1:1) | heavier (~192 KB/s) |

(48 kHz full-bw also serves the 12 kHz modes with ÷4 decimation if we ever want one code path; the
per-rate choice above is lighter on a busy box and reuses the existing decimator factors 1/2/4.)
Endianness: DAX is **big-endian**; convert on the packet boundary (our DSP is host-endian float).

### Latency budget (to validate, §5)

RX: DAX packet = 128 samp/24 kHz or 256 samp/48 kHz ≈ **5.3 ms/packet**; a 3-packet reorder buffer
≈ **16 ms**; wired-LAN RTT < 1 ms. TX: the transmitter is device-paced today by ALSA `Drain()`; for
Flex, pacing is the DAX packet cadence, and the airtime-complete signal (our `Drain()`) is "last
packet sent + its duration." PTT: `xmit 1` is one TCP round-trip (~1–3 ms LAN) **plus** the
`PTT_REQUESTED → TRANSMITTING` interlock settle (radio-dependent) — budget it inside `--txdelay`,
same as PTT-to-RF on a real rig. Net expectation: comfortably inside FreeDV/packet timing; **ARDOP
is the one to measure** before declaring it good.

---

## 5. Validation

### Without the radio (offline / CI)

- **Protocol unit tests** (all constants are known, so these are exact):
  - Discovery: parse a synthesized VITA-49 discovery packet → assert `model/serial/ip/port`;
    round-trip the `name` underscore↔space rule.
  - Session lines: parse `V…`, `H…`, `R<seq>|<err>|…`, `S<handle>|<obj> k=v …`, `M…`;
    command serialization `C<seq>|<cmd>\n`.
  - DAX-RX depacketize: s16be and float32be payload → `float[]` (levels, endianness, mono).
  - DAX-TX packetize: assert the 28-byte header **byte-for-byte** against the §2.4 layout for both
    stream classes (this is a fixed vector — a regression guard).
- **Mock radio** (recommended, `[Trait("Category","Interop")]`-style but no hardware): a small
  in-process/loopback fake that (a) accepts a TCP connection, sends the prologue, answers `R…` OK to
  the DAX enable commands and emits `slice`/`interlock` status, and (b) on UDP replays a WAV as
  DAX-RX VITA packets and captures our DAX-TX packets back into a WAV. This lets the **whole daemon
  run `--device flex:mock`** and lets us **loop a modem through it** (our modulator → mock → our
  demodulator, byte-exact frame check) with zero hardware — the strongest offline guarantee, and
  it exercises the `IAudioInput` refactor end-to-end.
- **Route B cross-check** (optional, needs a Flex on the LAN — so really § with-radio): stand up
  nDAX+nCAT and confirm the daemon modems through the Pulse bridge, as an independent latency
  yardstick for Route A.

### With Tom's 6500

1. **Discovery smoke:** listen, print `model/serial/version/ip` for the 6500. (Confirms broadcast
   reaches the node; if not, fall back to explicit IP.)
2. **Stream smoke:** create `dax_rx` on a chosen channel/slice, capture band noise to a WAV
   (`arecord`-equivalent through our path); create `dax_tx`, send a 1 kHz tone, watch the Flex DAX
   TX meter move.
3. **PTT smoke:** `xmit 1`/`0`, observe `interlock state=TRANSMITTING` and TX power; measure the
   `PTT_REQUESTED→TRANSMITTING` settle (the `--txdelay` input).
4. **HF loop — Flex rig variant of `docs/freedv-hf-loop.md`:** the same mode matrix, but one (or
   both) ends use `--device flex:<radio>:A` with Flex PTT instead of an ALSA card + serial/CM108.
   Add a short "Flex variant" section there: device string, no `--ptt` flag, RX gain via
   `audio stream … gain`, level-setting via the DAX TX gain rather than a mixer. Compare decode
   counts and the `txdelay` floor against the ALSA+real-transmitter baseline.
5. **Latency measurement:** round-trip through DAX (loopback a burst, or use `--quality-frames`
   `FoffEstHz`/timing) and record the number ARDOP cares about.
6. **OBW self-capture investigation (research, low priority):** attempt a DAX-IQ self-view during
   TX and **document the discrepancy** vs a second receiver off-air (expected per §3: not
   spec-accurate). Records the finding so we don't chase it again.

---

## 6. Phasing, effort, risks

### Phasing

| Phase | Deliverable | Needs radio? | Rough effort |
|---|---|---|---|
| 0 | `FlexClient` session + discovery + VITA parse/build; the byte-exact unit tests. | No | ~1–1.5 days |
| 1 | `IAudioInput` refactor + `AlsaAudioInput`; `FlexAudioInput` (DAX RX) + `FlexPtt`; `--device flex:` parsing; **mock radio**; daemon RX runs against the mock. | No | ~2–3 days |
| 2 | `FlexAudioOutput` (DAX TX); full TX+RX modem loop through the mock (byte-exact frames). | No | ~1–2 days |
| 3 | Hardware bring-up: discovery/stream/PTT smoke on the 6500. | **Yes** | ~0.5 day + Tom |
| 4 | HF-loop Flex variant + latency/`txdelay` floor; `freedv-hf-loop.md` update; OBW spike. | **Yes** | Tom-driven |

Phases 0–2 (the bulk) are fully offline. (No ax25-ts parity leg applies — pdn-soundmodem is its own
repo, not `packet.net`.)

### Risks

- **SmartSDR protocol versioning.** The core commands (discovery, `stream`, `dax`, `xmit`, `slice`)
  are stable across the v1.x–v4.x SmartSDR that the 6000-series runs, and nDAX/nCAT track them. A
  newer **"DAXv2"** exists for the 8000/Aurora line — **out of scope**; target the 6000-series DAX.
  *Open question 1:* which firmware is on the 6500. `[partly unverified]`
- **UDP timing on a busy box.** DAX RX is ~180–190 packets/s; the capture consumer must drain
  promptly or drop packets. Mirror nDAX's reorder ring and consider realtime priority; on the
  mask-sweep box, use the deeper-jitter-buffer pattern the daemon already has for ARDOP. Losses look
  like modem regressions — don't misread them (house rule: reds are real until proven otherwise).
- **Licence hygiene.** Depend on **nothing** proprietary (no FlexLib/FlexLib_Core). The port draws
  on MIT Go code — record provenance in the source headers.
- **DAX channel/client contention.** Only one client is the TX source per DAX channel; the radio
  disambiguates by client ID. Pick a station name + DAX channel + slice and document them; decide
  coexistence with a Windows SmartSDR session (*open question 4*).
- **Discovery across subnets.** Broadcast won't traverse VLANs; always support explicit IP.
- **Endianness / format bugs.** DAX is big-endian; the byte-exact TX vector test (Phase 0) is the
  guard.

---

## 7. Open questions for Tom

1. **Firmware version** on the 6500? (Fixes the DAX/command variant we target; confirms it's
   6000-series DAX, not 8000/Aurora DAXv2.)
2. **Default DAX mode:** full-bw **48 kHz float32** (direct for `freedv`/9600, heavier LAN) vs
   reduced-bw **24 kHz s16** (native, lighter), or auto-pick by the modem's DSP rate (the §4
   recommendation)?
3. **Network:** is the Flex on the **same L2 segment** as the node (so discovery broadcast works),
   or should we lead with explicit-IP config?
4. **Channel/slice allocation policy:** which DAX channel + slice letter should the daemon claim by
   default, and must it **coexist with SmartSDR-for-Windows** running simultaneously (shared DAX
   channel) or assume exclusive use?
5. **Primary target:** is this mainly HF (`freedv`/ARDOP) — argues for 48 kHz full-bw first — or
   also VHF packet through the Flex?
6. **Slice control depth:** should the client also drive **tune/mode/filter** (`slice t`,
   `mode=DIGU`, `filt`), or **attach-only** — operator sets the slice up in SmartSDR and we just
   bind DAX + PTT? (Minimal-first is my inclination.)
7. **Test harness appetite:** build the **mock radio** for CI (recommended — makes Phases 0–2
   self-testing), or accept hardware-only validation?
8. **OBW:** given §3 (self-capture isn't spec-accurate on the 6500), is the panadapter/DAX-IQ
   self-view worth even a research spike, or do we leave OBW entirely to the bench/second-receiver
   path?

---

## 8. Phase 3 — hardware bring-up results (2026-07-17, M0LTE's FLEX-6500)

First contact with a real radio. Radio: **FLEX-6500**, serial 1916-5312-6500-6692, firmware
**4.1.5.39794** (SmartSDR v4.x — confirms 6000-series DAX, *not* 8000/Aurora DAXv2), callsign
M0LTE, 10.45.0.76, 4 slices / 4 panadapters. Dummy load on ANT1, no SmartSDR running. Driven by
a staged smoke harness against the shipped `FlexRadio/` client library.

**Everything works end-to-end:**

| Step | Result |
|---|---|
| Discovery (UDP :4992 broadcast) | radio found; all fields parsed (model/serial/version/callsign/ip) |
| TCP session + status subscription | prologue, `sub`, `S…` status objects (`radio`/`interlock`/`slice`) all parsed correctly |
| Headless GUI-client register (`client gui`) | **OK** — returns our client UUID |
| `slice create freq=14.1 ant=ANT1 mode=DIGU` | **OK** — slice A created, owned by our client_handle |
| DAX-RX audio | **571 packets in 3 s, 0 lost**, 48 kHz float32, peak 0.10 (dummy-load noise floor) |
| DAX-TX audio | streamed 0.5 s while `TRANSMITTING` |
| PTT (`xmit 1`/`0`) | RECEIVE → **TRANSMITTING (settle 139 ms)** → READY → RECEIVE; clean |

**The 139 ms PTT→TRANSMITTING settle** is the Flex analogue of PTT-to-RF delay — comfortably
inside ARDOP's ~1.5–2.1 s ACK window; a good `--txdelay` starting point.

**Design finding — a headless setup path is needed (the one Phase-3 code change). — DONE.**
`FlexStation.SetUpAsync` assumes SmartSDR's model: it searches for a **client by station name**
and a **pre-existing slice**. With no SmartSDR neither exists, so it times out. The proven
headless sequence is: `client gui` (become a GUI client, get our own client_id) → `slice create`
(own our slice) → the DAX enable. Two quirks, both handled:
- `client set station=<name>` is **rejected** (err `0x50000000`) — but unneeded; we bind our own
  slice, not a named station's.
- `client bind client_id=<uuid>` **errors** (`0x5000003E`) yet DAX works regardless — we are
  already the owning GUI client, so the explicit bind is redundant and should be skipped (or made
  non-fatal) in the headless path.

### Headless setup (implemented)

`FlexStation.SetUpHeadlessAsync` implements the proven sequence and is the **default** for
`--device flex:` (attach — `SetUpAsync` — is preserved for the coexistence case, selected by a
`@station` suffix). The bring-up, in order:

1. `InitUdpAsync()` (register the client's UDP port).
2. `client gui` → err=0; parse our **client_id (uuid)** from the result message.
3. `slice create freq=<f> ant=<ant> mode=<m> rxant=<ant>` (from `FlexStationOptions.Frequency`/
   `Antenna`/`SliceMode`; defaults `14.100000`/`ANT1`/`DIGU`) → err=0, then **find OUR slice by
   `client_handle == FlexClient.Handle`** (handles compared with any `0x` prefix normalised away —
   the prologue `H` line carries none, slice status carries the `0x` form; matching on the handle,
   not a station name or a hardcoded `index_letter`, is the robust rule).
4. **Force the slice on-frequency** (`FlexStation.EnsureTunedAsync`) — the band-persistence fix
   below. `radio set band_persistence_enabled=0` (best-effort) → `slice set <idx> active=1`
   (best-effort) → `slice t <idx> <freq>` (the flexclient `SliceTune` form, `%.6f`; err=0) →
   re-read the slice's `RF_frequency` (bounded poll, never hangs) and, if it still doesn't match
   the request within ~2 Hz, surface `FlexStation.TuneWarning` (setup does **not** throw — the
   `slice t` succeeded).
5. **Best-effort** `client bind client_id=<uuid>` — swallow the `0x5000003E` rejection (surfaced on
   `FlexStation.HeadlessBindResult` for observability; a `Debug` line notes it), never fail setup.
   We never send `client set station` (it's rejected and unneeded).
6. `EnableDaxAsync` **unchanged** — the eight-step DAX enable shared with the attach path
   (§2.4): `slice set <idx> dax=<ch>` → `dax audio set <ch> slice=<idx> tx=1` →
   `stream create type=dax_rx` → `audio stream … gain` → `stream create type=dax_tx`. `<ch>` is
   `FlexStationOptions.DaxChannel` (default `1`, `--flex-daxch`) — see the coexistence note below.

Provenance: the DAX enable and PTT are the nDAX/nCAT port (MIT, KC2G). The **headless sequence
itself is pdn's own** — nDAX is attach-only (it binds a station SmartSDR already created).

### Band persistence — the headless tune fix (2026-07-17, live on the 6500)

**The bug.** On a real 6500 with `band_persistence_enabled=1` (the firmware default), `slice
create freq=<f> …` returns `err=0` **but the radio ignores the create `freq` and snaps the new
slice back to the last-used band.** The slice comes up on the wrong QRG — its `RF_frequency`
status reports the *persisted* band, not the requested one — and DAX then streams audio from the
wrong frequency (silent / wrong band). The `slice create` alone is not enough to place a headless
slice on-frequency.

**The fix (proven live, in order):**

1. `radio set band_persistence_enabled=0` — best-effort; this is the cause.
2. `slice set <idx> active=1` — best-effort.
3. `slice t <idx> <freq>` — the explicit tune (flexclient `SliceTune`, `%.6f`); returns `err=0`.
4. Verify: re-read the slice's `RF_frequency`; it now matches `<freq>`.

After this the slice is correctly on-frequency and DAX-RX carries real audio — verified by
decoding a live off-air BPSK300 signal from GB7RDG through it. `EnsureTunedAsync` runs this between
the slice create and the DAX enable in the headless path (only — the attach path leaves tuning to
SmartSDR). Steps 1–2 are best-effort (some firmwares may not expose the setting; DAX still works
if it's already off); step 3 is load-bearing; step 4 is observability — a residual mismatch sets
`FlexStation.TuneWarning` (the daemon prints it to stderr) rather than failing setup or hanging.

The `MockFlexRadio` headless mode models this faithfully so it's actually tested, not bypassed:
`slice create` reports the slice on the **persisted band** (`14.100000`, ignoring the create
`freq`), and `slice t <idx> <freq>` updates and re-emits `RF_frequency`. A headless setup that
requests e.g. `7.050100` therefore only ends on `7.050100` **because `EnsureTunedAsync` ran** — the
regression test (`FlexHeadlessSetupTests`, `FlexDeviceOpenTests`) asserts exactly that, plus the
presence and ordering of `band_persistence_enabled=0` and `slice t` in the mock command log.

### DAX channel — coexisting with a running SmartSDR (2026-07-17 live finding)

A running SmartSDR **grabs DAX channel 1**, so a headless pdn client sharing the same box must
claim a *different* DAX channel or the two contend. `FlexStationOptions.DaxChannel` (default `1`)
is now configurable end-to-end: `--flex-daxch <n>` on the CLI, `"daxChannel"` in the config's
`flex` section, threaded through `FlexTuning`/`FlexDevice` into the DAX enable (`slice set … dax=<n>`,
`dax audio set <n> …`, `stream create type=dax_rx dax_channel=<n>`). It applies to **both** the
headless and attach paths (it's the DAX channel the client claims, not a slice param). Guidance:
**a headless client alongside SmartSDR should pick a free DAX channel** (SmartSDR is on 1), **or
use attach mode** (`@station`) to share the slice SmartSDR already owns.

**Offline validation:** `MockFlexRadio` gained a `MockSetupMode.Headless` that models the real
6500's behaviour — answers `client gui` (returns a uuid), `slice create` (emits a `slice` status
with `client_handle` = the caller's handle, `index_letter` = A), returns the **same `0x5000003E`**
for the redundant `client bind`, and **rejects** `client set station` with `0x50000000`. The full
headless bring-up + DAX RX/TX + PTT run against `flex:mock`, and the byte-exact modem loop
(`FlexModemLoopTests`: AFSK1200 through reduced-bw 24 kHz s16; FreeDV datac3 through full-bw
48 kHz float32) now runs **through the headless path** — recovered frames byte-identical, no
hardware.

**Remaining:** a ~2-minute hardware confirmation on the 6500 — push a real FreeDV-datac / ARDOP
frame into the dummy load through the shipped `--device flex:` daemon (see the checklist below).

**Open questions from §7 now answered:** firmware 4.1.5 (6000-series DAX ✓); full-bw 48 kHz
float32 DAX works ✓; same-segment discovery works ✓; exclusive use ✓; HF-first (48 kHz) ✓.
Remaining for the HF-loop phase: real DAX-RX UDP loss/reorder on a busy box, and the round-trip
latency for ARDOP (the 139 ms is the PTT half only).

### Final hardware-confirmation checklist (~2 min, radio + dummy load on ANT1)

The headless path ships in the product; confirm it drives the real 6500 end-to-end. Run the
**shipped daemon** (not the smoke harness) against M0LTE's 6500 (10.45.0.76), dummy load on ANT1:

1. **FreeDV datac3 into the dummy load (headless default):**

   ```sh
   pdn-soundmodem --device flex:10.45.0.76 \
                  --flex-freq 14.100000 --flex-ant ANT1 --flex-mode DIGU \
                  --modem 0:freedv-datac3 --txdelay 200 --kiss 8105
   ```

   Expect on stdout: `audio: flex:10.45.0.76 DAX 48000 Hz → 48000 Hz (slice A, headless 14.100000 MHz ANT1 DIGU)`
   and `kiss tcp: 127.0.0.1:8105`. Then push a frame — e.g. `nc 127.0.0.1 8105` and send a KISS
   data frame, or point axcall/BPQ at 8105. Watch the Flex: `interlock` → `TRANSMITTING`, DAX-TX
   meter moves, RF into the dummy load. (Discovery works too: `--device flex:discover`.)

2. **ARDOP frame into the dummy load (headless):**

   ```sh
   pdn-soundmodem --device flex:10.45.0.76 --flex-freq 14.100000 --ardop 8515 --txdelay 200
   ```

   Connect Pat/ardopcf to `127.0.0.1:8515` (data 8516) and start an ARQ call or send an FEC
   frame; confirm keying + RF as above. This is the round-trip-latency measurement point ARDOP
   cares about (the 139 ms PTT settle is the TX half only).

3. **Coexistence sanity (optional, if SmartSDR is running):** `--device flex:10.45.0.76:A@Flex`
   should attach to the SmartSDR "Flex" station's slice A instead of creating one.

Success = keys the radio, `interlock=TRANSMITTING`, RF on the dummy load, no setup errors
(the `client bind` rejection is expected and silently tolerated).

---

## 9. IQ interfaces — RX via DAX-IQ, TX via the Waveform API (2026-07-17)

The DAX path above (§2–§8) gives the modem an *audio* pipe through a ~3 kHz slice — proven, shipped.
This section is the separate **IQ** story: wideband complex baseband, for (a) multi-channel RX and
(b) our own >3 kHz waveforms. Two very different mechanisms, one per direction.

### 9.1 RX — DAX-IQ (receive only)

`stream create type=dax_iq daxiq_channel=<n>` (wiki:TCPIP-stream) + `dax iq set <ch> pan=<p>
rate=<24|48|96|192>` (wiki:TCPIP-dax) streams **wideband complex baseband** (VITA wide classes
`0x02E3/E4/E5/E6`, up to 192 kSPS, 4 streams on a 6500 — §2.3/§3) *from* the radio, before the SSB
filter and AGC. This is a clean fit for **multi-channel monitoring** (one slice → a digital-
downconversion front-end → N of our real-baseband demodulators) and for feeding a wide own-mode RX.
It rides the same VITA/UDP transport the DAX client already speaks; the only new DSP is an NCO-mix +
decimate DDC to land signals at a modem's real baseband.

**DAX-IQ receive verified on the 6500** (2026-07-18, spike harness). Bring-up: `client gui` →
`display pan c freq=<f> ant=<A>` (GUI-client only; returns `<panId>,<waterfallId>`) → `display pan
s <panId> daxiq_channel=<ch>` → `dax iq set <ch> pan=<panId> rate=<24|48|96|192>` → `stream create
type=dax_iq daxiq_channel=<ch>` (returns the stream id) → `stream set <id> daxiq_rate=<r>`. The
radio then streams wide VITA to our `client udpport`. **Payload format — the load-bearing gotcha:
DAX-IQ is little-endian float32 interleaved I/Q (host order), NOT big-endian like DAX audio (§2.4).**
At 96 kSPS each packet is class `0x534C02E5`, 4100 payload bytes = **512 complex samples + a 4-byte
trailer word** (strip it). Confirmed by decoding a clean noise-floor spectrum into the dummy load.

**Implemented + hardware-validated (2026-07-18).** `FlexRadio/FlexDaxIqSource.cs` implements the
`Iq/IIqSource` seam over the `M0LTE.Flex` `FlexClient` — it runs the bring-up above, subscribes to
`FlexClient.VitaPacketReceived` (an `Action<VitaPreamble, byte[]>`), routes packets matching its
stream id into `FlexRadio/DaxIqStreamBuffer.cs` (LE-float32 depacketize → bounded reorder ring →
blocking `Read`, with VITA-4-bit packet-loss counting). Smoke on M0LTE's 6500: DAX-IQ open, **238k
complex samples / 2 s, 0 lost**, sane levels — feeds straight into `MultiChannelReceiver`. One
package quirk handled: for these packets `VitaPreamble.PayloadLength` can exceed
`bytes.Length − PayloadOffset`, so the source **clamps to the bytes present** rather than trusting
the reported length (the buffer consumes only whole 8-byte pairs, so the trailing tail is harmless).
Unit-tested offline in `tests/…/FlexRadio/DaxIqStreamBufferTests.cs` (byte-format, loss, blocking
Read, and a tone round-trip through the depacketize + DDC). **Remaining:** daemon/CLI wiring to
select multi-channel RX and place the per-channel offsets (a config-shape decision).

**DAX-IQ is receive-only.** There is *no* path to push IQ *into* the radio for transmit through DAX.
Ground truth: Doug **K3TZR** (author of xLib6000/xLib6001, the fullest clean-room Flex API libs),
FlexRadio Community thread 7789005: *"I'm not aware of any ability to send IQ samples to the radio
for transmission."* DAX-IQ streams IQ from the radio; `TXAudio` carries audio only. The wiki's
`stream set <id> tx=1 daxiq_rate=…` line is **not** a wideband-IQ injection path.

### 9.2 TX — the Waveform API (the only IQ-TX door — PROVEN on the 6500)

Arbitrary TX-IQ on a Flex goes through the **Waveform API** (wiki:TCPIP-waveform). Contrary to the
earlier assumption that this is a Windows/proprietary/on-radio-only path, three facts hold:

- **It is GPL-3.0, not proprietary.** FlexRadio's own SDK — `flexradio/smartsdr-codec2` and the
  maintained fork `n5ac/smartsdr-dsp` (the FreeDV/Codec2 waveform) — is GPL-3.0: *"open under GPL3
  … third party access to an otherwise 'closed' software defined radio."* Licence-compatible with
  this repo; we **port the protocol** (as we did DAX from the MIT Go refs), we don't depend on it.
- **The modem can run OFF the radio, on a network host.** SDK README: the MODEM *"can be located
  inside a separate process inside the radio, on a separate processor attached to the network, or
  in a separate PC."* FlexRadio's developer page confirms "run both outside and inside the radio"
  and names **FLEX-6000 or FLEX-8000**. So it fits the headless-Linux daemon model.
- **Registration is the same ASCII TCP session we already speak** (`waveform create` / `waveform
  set` / `sub slice all`), not a hidden loader. The one coupling: it registers a **mode** on the
  radio (the flavour of §7's "Flex-native mode" — but there is no other way to put arbitrary IQ on
  air; audio TX is clamped to the mode's ~3 kHz SSB filter).

**Proven end-to-end on M0LTE's FLEX-6500** (API `V1.4.0.0`, 10.45.0.76, headless, dummy load on
ANT1, `rfpower=10`), by a from-scratch raw-socket C# harness that speaks *none* of the SDK code —
just the reverse-engineered wire protocol:

| Step | Result |
|---|---|
| `waveform create name=… mode=PDN underlying_mode=USB version=…` (ad-hoc, over TCP) | **err=0** — a custom waveform registers headlessly, no SmartSDR, no installed `.ssdr_waveform` |
| `slice set <n> mode=PDN` on our own headless slice | **accepted** — status confirms `mode=PDN` |
| Radio pushes IF-data buffers to our UDP port | RX: 1303 buffers / 3 s; **stream direction = stream_id LSB** (even=RX, odd=TX) |
| `xmit 1` | `interlock` RECEIVE → **PTT_REQUESTED → TRANSMITTING** — RF into the dummy load |
| Radio pulls TX-IQ from our waveform | **224 TX buffers in 1.2 s; we supplied a complex sample for every one (0 drops)** — cadence 187.5 pkt/s = **24 kHz, 128 complex samples/packet** |
| `xmit 0` | UNKEY_REQUESTED → READY → RECEIVE — clean |

So: **IQ TX via the Waveform API works, headless, driven entirely from our own code.** The radio
asked our process for transmit IQ and keyed the PA.

**Implemented in the `M0LTE.Flex` package (0.3.0), 2026-07-18** — it belongs there next to
`FlexStation`/`FlexPtt`, not in this consumer. `FlexWaveform.SetUpHeadlessAsync` registers the
waveform + a slice in that mode + the band-persistence tune; `FlexWaveformIqOutput` is the
reflection-driven IQ sink (`Write` a burst of interleaved I/Q, `Drain`, unkey) — on each radio
TX-buffer request (full-bw IF-data class `0x03E3`, odd stream id) it reflects the next buffered IQ
via `DaxStreamFormat.FullBandwidth.BuildPacket`. `FlexWaveformOptions.UnderlyingMode` defaults to
`RAW` (§9.5). The mock models the waveform TX loop for offline tests; hardware-proven on M0LTE's
6500 (RAW 3 kHz tone → 189 TX buffers reflected into the dummy load). pdn-soundmodem consumes it
once 0.3.0 is published.

### 9.3 Waveform TX-IQ packet (byte-exact, from smartsdr-dsp `vita_output.c`)

Same 28-byte VITA header as the DAX-TX packet (§2.4) — the only deltas are the **class** and that the
payload is **stereo float32 = interleaved I/Q** (2 words/sample):

```
byte 0    : 0x18                         // IFDataWithStream, class-id present
byte 1    : 0xD0 | (packet_count & 0x0F) // TSI=Other, TSF=SampleCount, 4-bit count
u16be     : 7 + samples*2                // length in 32-bit words (7 hdr + 2/complex-sample)
u32be     : <stream id>                  // the id the radio pushed us (reflect the SAME id)
u32be     : 0x001C2D                     // class_id_h = FlexRadio OUI
u32be     : 0x534C03E3                   // class_id_l = SL stereo-float32 (I/Q) — full-bw class
u32be     : 0                            // timestamp int
u64be     : 0                            // timestamp frac
<payload> : samples × { float32be I, float32be Q }
```

Sent over UDP to **radio:4991**. Direction/keying is status-driven: on `interlock
state=PTT_REQUESTED` the radio streams TX buffers (odd stream_id) at our `udpport`; we reflect IQ;
`UNKEY_REQUESTED` ends it. Provenance: `smartsdr-dsp` GPL-3.0 (N5AC/KE9H) — protocol pinned from
`vita_output.c`, `sched_waveform.c`, `status_processor.c`, `FreeDV.cfg`.

### 9.4 Registration + bring-up sequence (headless, no SmartSDR)

`sub slice all` → `client udpport <p>` → `client gui` → `waveform create name=<n> mode=<M>
underlying_mode=USB version=…` → `waveform set <n> tx=1` → `waveform set <n> {rx,tx}_filter
low_cut=/high_cut=` (one param per command) → `waveform set <n> udpport=<p>` → own a headless slice
(§8: `slice create` + `slice t` band-persistence tune) → `slice set <idx> mode=<M>` (activates the
waveform) → `slice set <idx> tx=1` → `transmit set rfpower=<low>` → key with `xmit 1/0`.

Firmware syntax notes (V1.4.0.0): filter params are **one per command**; `{rx,tx}_filter depth=`
is **rejected** (`0x50000016`) though `low_cut`/`high_cut` work; `sub interlock all` is invalid
(`0x500000A3`) but interlock status arrives to a GUI client anyway.

### 9.5 Open question — achievable TX bandwidth (the crux for our own >3 kHz modes)

The waveform runs at **24 kHz complex**, but `underlying_mode=USB` routes it through the SSB
modulator + `tx_filter`. FreeDV uses USB with a 600–2400 Hz TX filter and even duplicates its real
audio into I=Q — i.e. it is *not* demonstrating wideband arbitrary IQ→RF. A no-TX command probe on
the 6500 shows the surface is **not** SSB-capped: `underlying_mode` accepts `USB/LSB/DIGU/DIGL/AM/
FM/NFM/DFM/CW/RTTY/DATA/RAW/IQ` (note **RAW** and **IQ**), and `tx_filter high_cut` accepts up to
**24000 Hz** with no clamp. That strongly *hints* true wideband/complex TX is reachable — but
command acceptance ≠ on-air behaviour.

**Self-capture via a second-slice DAX-IQ — attempted 2026-07-18, CONFIRMED NON-VIABLE on the 6500.**
The obvious cheap check (a panadapter + DAX-IQ at the TX frequency, capturing while we transmit a
comb of tones at ±2/5/8/11 kHz) does **not** work: during TX the receiver is blanked, so the DAX-IQ
stream keeps flowing but carries only the muted noise floor — the transmitted comb is nowhere in the
captured spectrum (comb bins sit within ~6 dB of the noise median, i.e. absent; mean magnitude is
actually *lower* during TX than during RX). This upgrades §3's "not spec-accurate" to "sees nothing":
DAX-IQ self-capture cannot measure our own TX. The TX path itself was fine (interlock TRANSMITTING,
radio pulled the TX-IQ buffers) — we simply can't observe it from the same radio.

**MEASURED with an external receiver — 2026-07-18, via M0LTE's UberSDR (`ka9q_ubersdr`, RX888 on an
active loop) hearing the dummy-load leakage.** We TX a comb of complex tone-pairs (±3/±7 kHz) under
each `underlying_mode` and capture the RF on UberSDR's `iq96` stream (a spectrogram makes it
unambiguous — automated peak-picking is defeated by stronger on-air background carriers):

- **`underlying_mode=RAW` → TRUE WIDEBAND COMPLEX IQ→RF.** All four comb tones reproduce, symmetric
  about the carrier (±3 and ±7 kHz), i.e. **both sidebands** — a clean ~14 kHz-wide complex signal,
  far beyond SSB. **This is the answer: the Waveform API DOES unlock wideband complex TX.**
- **`underlying_mode=USB` and `=IQ` → SSB-limited.** Only the +3 kHz tone survives (~single
  sideband, ~3 kHz) — as expected for USB; notably plain `IQ` behaves like USB here, **`RAW` is the
  wideband one**.
- **Ceiling = the waveform's 24 kHz complex rate (±12 kHz).** Pushing the comb to ±10 kHz brings in
  imaging/aliasing near the Nyquist edge, so the clean usable width is ~±7–10 kHz (~14–20 kHz). Fine
  for HF (2.7 kHz channels) and modest VHF; whether a higher waveform sample rate exists on the 6000
  is unconfirmed. Evidence: `docs/img` spectrogram in the session, harness `scratchpad/wfspike`
  (`rf <mode> comb …`) + `scratchpad/uberiq` (UberSDR iq96 capture).

**UberSDR API notes (for reuse):** `POST /connection` (must send a **User-Agent** header — the
server maps `user_session_id`→UA and rejects the WS without it) then
`ws://…/ws?frequency=&mode=iq96&user_session_id=&version=2`; a bypassed (LAN/whitelisted) IP is
required for the wide IQ modes. Binary frames are **zstd-compressed**; inside, per
`clients/iq-recorder/pcm_decoder.go`: magic `0x5043`"PC" full header → samples at [29:] (sampleRate
at [20:24]) or `0x504D`"PM" minimal → samples at [13:]; PCM is **big-endian int16, interleaved I/Q**.

**Not viable for this**, confirmed earlier the same day: a second-slice DAX-IQ self-capture (RX
blanked during TX). The external-RX route above is the one that works.

### 9.6 What this means for the roadmap

- **Multi-channel RX (own #2 interest):** unblocked, low-risk — DAX-IQ RX + a DDC front-end, no TX
  story, no Waveform API, no licence question. The near-term IQ win.
- **Wideband own-modes (#8/#9):** RX via DAX-IQ; **TX via the Waveform API is proven feasible,
  licence-clean, AND wideband** (GPL-3.0 port, headless, 6000-supported; §9.5 confirms
  `underlying_mode=RAW` carries true complex both-sideband IQ, ~14–20 kHz usable, capped by the
  24 kHz waveform rate). The §9.5 gate is **cleared**: the Waveform API is a real wideband-TX path,
  not an SSB dead-end. Building it effectively makes an own-mode a Flex *waveform* for the transmit
  half — accepted, since there is no other IQ-TX door on a Flex. The ~24 kHz ceiling suits HF and
  modest-VHF own-modes; a VARA-FM-class ~25 kbps signal would sit near the edge (confirm the
  waveform max rate before committing to that).
