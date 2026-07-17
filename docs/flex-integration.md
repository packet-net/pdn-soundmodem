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
- Parse `--device`: `flex:<radio>[:slice]` (radio = `discover`, an IP, or a `serial=`/`name=`
  discovery spec; slice defaults to `A`) selects the Flex triplet
  (`FlexAudioInput`/`FlexAudioOutput`/`FlexPtt` all sharing one `FlexClient`); anything else stays
  ALSA. When `--device flex:` is set, `--ptt` is implicitly the Flex (reject a conflicting
  `--ptt serial:/cm108:`), matching how the Flex owns keying.

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

**Design finding — a headless setup path is needed (the one Phase-3 code change).**
`FlexStation.SetUpAsync` assumes SmartSDR's model: it searches for a **client by station name**
and a **pre-existing slice**. With no SmartSDR neither exists, so it times out. The proven
headless sequence is: `client gui` (become a GUI client, get our own client_id) → `slice create`
(own our slice) → the DAX enable. Two quirks, both handled:
- `client set station=<name>` is **rejected** (err `0x50000000`) — but unneeded; we bind our own
  slice, not a named station's.
- `client bind client_id=<uuid>` **errors** (`0x5000003E`) yet DAX works regardless — we are
  already the owning GUI client, so the explicit bind is redundant and should be skipped (or made
  non-fatal) in the headless path.
So: add `FlexStation` headless bring-up (GUI-register + create-slice, tolerate the redundant
bind, don't require a station match); `FlexDevice`'s `--device flex:` selects it when the radio
has no SmartSDR station. Then re-run on hardware to push a full modem (FreeDV datac / ARDOP)
through the radio into the dummy load.

**Open questions from §7 now answered:** firmware 4.1.5 (6000-series DAX ✓); full-bw 48 kHz
float32 DAX works ✓; same-segment discovery works ✓; exclusive use ✓; HF-first (48 kHz) ✓.
Remaining for the HF-loop phase: real DAX-RX UDP loss/reorder on a busy box, and the round-trip
latency for ARDOP (the 139 ms is the PTT half only).
